using System.Diagnostics.Metrics;
using System.Globalization;
using Kupro.Api.Data;
using Kupro.Api.Features.Email;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Options;

namespace Kupro.Api.Features.Meetings;

/// <summary>One workspace's AI usage in one month (ADR-013 "Meeting agent usage"). Last months' rows stay for reporting.</summary>
public sealed class MeetingUsage : ITenantOwned
{
    public Guid WorkspaceId { get; set; }

    /// <summary>First day of the month, Copenhagen time.</summary>
    public DateOnly Period { get; set; }

    public double AudioSeconds { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }

    /// <summary>What speech and tokens cost at the routes' rates.</summary>
    public decimal CostCents { get; set; }

    public DateTimeOffset? Warned80At { get; set; }
    public DateTimeOffset? Warned100At { get; set; }
}

/// <summary>Server-wide spend per day, for the daily ceiling. Not tenant-owned: it sums every workspace.</summary>
public sealed class MeetingAgentSpend
{
    /// <summary>Copenhagen date.</summary>
    public DateOnly Day { get; set; }

    public decimal CostCents { get; set; }
}

internal sealed class MeetingUsageConfiguration : IEntityTypeConfiguration<MeetingUsage>, IEntityTypeConfiguration<MeetingAgentSpend>
{
    public void Configure(EntityTypeBuilder<MeetingUsage> builder)
    {
        builder.ToTable("meeting_usage");
        builder.HasKey(u => new { u.WorkspaceId, u.Period });
        builder.HasOne<Workspace>().WithMany().HasForeignKey(u => u.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(u => u.CostCents).HasPrecision(14, 4);
    }

    public void Configure(EntityTypeBuilder<MeetingAgentSpend> builder)
    {
        builder.ToTable("meeting_agent_spend");
        builder.HasKey(s => s.Day);
        builder.Property(s => s.CostCents).HasPrecision(14, 4);
    }
}

public enum BudgetState
{
    Available,

    /// <summary>The workspace used its hours this month.</summary>
    Spent,

    /// <summary>The server-wide daily ceiling is reached: every workspace falls back until tomorrow.</summary>
    Ceiling,
}

/// <param name="HoursUsed">Meeting hours used this month: what was spent, in hours at the configured hour value.</param>
/// <param name="HoursIncluded">Hours per seat times seats, pooled.</param>
/// <param name="ResetsOn">The first of next month, when the hours reset.</param>
/// <param name="Spent">True when the hours are used up or the server-wide daily ceiling is reached: the microphone is
/// off and typed text gets keyword proposals.</param>
public sealed record MeetingUsageResponse(double HoursUsed, double HoursIncluded, DateOnly ResetsOn, bool Spent);

/// <summary>
/// The meeting agent's AI budget (task 017, ADR-013): meters speech seconds and tokens per workspace per month, says
/// when a call may spend, and warns billing at 80 % and 100 %. Without <see cref="MeetingAgentOptions.Budget"/> it still
/// meters but never limits (self-hosted).
/// </summary>
public sealed partial class MeetingBudget(
    IDbContextFactory<KuproDbContext> dbFactory,
    WorkspaceContext workspace,
    IOptions<MeetingAgentOptions> options,
    IEmailSender email,
    MeetingAgentMetrics metrics,
    TimeProvider clock,
    ILogger<MeetingBudget> logger)
{
    private const decimal WarnAt = 0.8m;

    private static readonly TimeZoneInfo Copenhagen = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");

    private MeetingBudgetOptions? Budget => options.Value.Budget is { EuroPerHour: > 0 } budget ? budget : null;

    /// <summary>Before a model call: whether it may spend. A refusal is counted for the ceiling alert.</summary>
    public async Task<BudgetState> CheckAsync(CancellationToken ct)
    {
        if (Budget is null)
        {
            return BudgetState.Available;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var state = (await ReadAsync(db, ct)).State;
        if (state != BudgetState.Available)
        {
            metrics.Refused(state);
        }

        return state;
    }

    /// <summary>This month's usage, for settings and the meeting panel; null when there is no budget.</summary>
    public async Task<MeetingUsageResponse?> UsageAsync(CancellationToken ct)
    {
        if (Budget is not { } budget)
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (state, spentCents, budgetCents) = await ReadAsync(db, ct);
        var centsPerHour = budget.EuroPerHour * 100;
        return new MeetingUsageResponse(
            (double)Math.Round(spentCents / centsPerHour, 2), (double)Math.Round(budgetCents / centsPerHour, 2),
            Period(clock.GetUtcNow()).AddMonths(1), state != BudgetState.Available);
    }

    /// <summary>One utterance's audio, at the transcription route's rate for the language.</summary>
    public Task RecordAudioAsync(double seconds, string language, CancellationToken ct)
    {
        var route = options.Value.Transcription.For(language);
        var cents = (decimal)seconds / 60 * (route.EuroPerAudioMinute ?? 0) * 100;
        metrics.Audio(seconds, route.Model);
        return RecordAsync(seconds, 0, 0, cents, "transcription", route.Model, ct);
    }

    /// <summary>One extraction call's tokens, at the extraction route's rates for the language.</summary>
    public Task RecordTokensAsync(ModelUsage usage, string language, CancellationToken ct)
    {
        var route = options.Value.Extraction.For(language);
        var input = route.EuroPerMillionInputTokens ?? 0;
        var cents = ((usage.InputTokens - usage.CachedInputTokens) * input
            + usage.CachedInputTokens * (route.EuroPerMillionCachedInputTokens ?? input)
            + usage.OutputTokens * (route.EuroPerMillionOutputTokens ?? 0)) / 1_000_000m * 100;
        metrics.Tokens(usage, route.Model);
        return RecordAsync(0, usage.InputTokens, usage.OutputTokens, cents, "extraction", route.Model, ct);
    }

    private async Task RecordAsync(double seconds, long input, long output, decimal cents, string seam, string? model, CancellationToken ct)
    {
        var workspaceId = workspace.RequireWorkspaceId();
        var now = clock.GetUtcNow();
        var period = Period(now);
        metrics.Spend(cents, seam, model, workspace.Slug);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // One atomic increment per row, so utterances from several microphones at once are all counted. Raw SQL skips
        // the tenant filter, so the workspace id is passed explicitly.
        var total = (await db.Database.SqlQuery<decimal>($"""
            INSERT INTO meeting_usage AS u (workspace_id, period, audio_seconds, input_tokens, output_tokens, cost_cents)
            VALUES ({workspaceId}, {period}, {seconds}, {input}, {output}, {cents})
            ON CONFLICT (workspace_id, period) DO UPDATE SET
                audio_seconds = u.audio_seconds + excluded.audio_seconds,
                input_tokens = u.input_tokens + excluded.input_tokens,
                output_tokens = u.output_tokens + excluded.output_tokens,
                cost_cents = u.cost_cents + excluded.cost_cents
            RETURNING u.cost_cents AS "Value"
            """).ToListAsync(ct))[0];
        if (cents > 0)
        {
            var today = Today(now);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO meeting_agent_spend AS s (day, cost_cents) VALUES ({today}, {cents})
                ON CONFLICT (day) DO UPDATE SET cost_cents = s.cost_cents + excluded.cost_cents
                """, ct);
        }

        if (Budget is not null)
        {
            await WarnAsync(db, workspaceId, period, total, now, ct);
        }
    }

    /// <summary>
    /// Emails everyone with billing access once at 80 % and once at 100 % a month. Each threshold is claimed with a
    /// conditional update, so concurrent calls crossing it send one email. Crossing both at once sends only the 100 %.
    /// </summary>
    private async Task WarnAsync(KuproDbContext db, Guid workspaceId, DateOnly period, decimal totalCents, DateTimeOffset now,
        CancellationToken ct)
    {
        var budgetCents = await BudgetCentsAsync(db, ct);
        if (budgetCents <= 0 || totalCents < budgetCents * WarnAt)
        {
            return;
        }

        var full = totalCents >= budgetCents && await db.Database.ExecuteSqlAsync($"""
            UPDATE meeting_usage SET warned100at = {now}, warned80at = COALESCE(warned80at, {now})
            WHERE workspace_id = {workspaceId} AND period = {period} AND warned100at IS NULL
            """, ct) == 1;
        var almost = !full && await db.Database.ExecuteSqlAsync($"""
            UPDATE meeting_usage SET warned80at = {now}
            WHERE workspace_id = {workspaceId} AND period = {period} AND warned80at IS NULL
            """, ct) == 1;
        if (!full && !almost)
        {
            return;
        }

        var name = await db.Workspaces.Where(w => w.Id == workspaceId).Select(w => w.Name).SingleAsync(ct);
        var recipients = await db.Members.AsNoTracking()
            .Where(m => m.Kind == MemberKind.Human && m.Status == MemberStatus.Active
                && (m.Role == WorkspaceRole.Owner || m.CanManageBilling))
            .Select(m => db.Users.Where(u => u.Id == m.UserId).Select(u => new { u.Email, u.Locale }).FirstOrDefault())
            .ToListAsync(ct);
        var hours = (double)(budgetCents / (Budget!.EuroPerHour * 100));
        var reset = period.AddMonths(1);
        foreach (var to in recipients.Where(r => r is not null))
        {
            try
            {
                await email.SendAsync(Message(to!.Email, to.Locale, name, full, hours, reset), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogWarnFailed(logger, workspaceId, ex);
            }
        }
    }

    private static EmailMessage Message(string to, string locale, string workspaceName, bool full, double hours, DateOnly reset)
    {
        var danish = locale.StartsWith("da", StringComparison.OrdinalIgnoreCase);
        var culture = CultureInfo.GetCultureInfo(danish ? "da-DK" : "en-GB");
        var included = hours.ToString("0.#", culture);
        var on = reset.ToString("d MMMM", culture);
        return (danish, full) switch
        {
            (true, true) => new EmailMessage(to, $"Mødetimerne i {workspaceName} er brugt op",
                $"Alle {included} mødetimer er brugt denne måned. Mikrofonen er slået fra til {on}. Tekst sendt via API, CLI eller MCP bliver stadig til forslag med en enklere søgning på nøgleord."),
            (true, false) => new EmailMessage(to, $"80 % af mødetimerne i {workspaceName} er brugt",
                $"Der er brugt 80 % af de {included} mødetimer denne måned. Når de er brugt op, stopper mikrofonen til {on}."),
            (false, true) => new EmailMessage(to, $"The meeting hours in {workspaceName} are used up",
                $"All {included} meeting hours are used this month. The microphone is off until {on}. Text sent through the API, CLI or MCP still turns into proposals with a simpler keyword matcher."),
            (false, false) => new EmailMessage(to, $"80% of the meeting hours in {workspaceName} are used",
                $"80% of this month's {included} meeting hours are used. When they run out, the microphone stops until {on}."),
        };
    }

    private async Task<(BudgetState State, decimal SpentCents, decimal BudgetCents)> ReadAsync(KuproDbContext db, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var period = Period(now);
        var spent = await db.MeetingUsage.Where(u => u.Period == period).Select(u => u.CostCents).SingleOrDefaultAsync(ct);
        var budgetCents = await BudgetCentsAsync(db, ct);
        if (Budget!.DailyCeilingEuro is { } ceiling)
        {
            var today = Today(now);
            var day = await db.MeetingAgentSpend.Where(s => s.Day == today).Select(s => s.CostCents).SingleOrDefaultAsync(ct);
            if (day >= ceiling * 100)
            {
                return (BudgetState.Ceiling, spent, budgetCents);
            }
        }

        return (spent >= budgetCents ? BudgetState.Spent : BudgetState.Available, spent, budgetCents);
    }

    /// <summary>
    /// Hours per seat times seats, in cents. Until billing exists every workspace counts as paid with its current seats
    /// (task 017); the trial's own allowance comes with billing.
    /// </summary>
    private async Task<decimal> BudgetCentsAsync(KuproDbContext db, CancellationToken ct)
    {
        var seats = await db.Members.CountAsync(WorkspaceService.IsSeat, ct);
        return seats * Budget!.HoursPerSeat * Budget.EuroPerHour * 100;
    }

    private static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Copenhagen).DateTime);

    private static DateOnly Period(DateTimeOffset now)
    {
        var today = Today(now);
        return new DateOnly(today.Year, today.Month, 1);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Meeting budget warning email for workspace {WorkspaceId} failed")]
    private static partial void LogWarnFailed(ILogger logger, Guid workspaceId, Exception exception);
}

/// <summary>
/// Meeting agent spend for Grafana (ADR-014): audio, tokens and euro by model, euro by workspace for the top-spenders
/// panel, and refusals, which the daily-ceiling alert watches.
/// </summary>
public sealed class MeetingAgentMetrics
{
    private readonly Counter<double> _audio;
    private readonly Counter<long> _tokens;
    private readonly Counter<double> _spend;
    private readonly Counter<long> _refused;

    public MeetingAgentMetrics(IMeterFactory meters, IHostEnvironment environment)
    {
        // The API's own meter, which ServiceDefaults exports.
        var meter = meters.Create(environment.ApplicationName);
        _audio = meter.CreateCounter<double>("kupro.meeting_agent.audio", "s", "Audio sent to speech-to-text");
        _tokens = meter.CreateCounter<long>("kupro.meeting_agent.tokens", "{token}", "Tokens used by proposal extraction");
        _spend = meter.CreateCounter<double>("kupro.meeting_agent.spend", "{euro}", "What speech and tokens cost at the configured rates");
        _refused = meter.CreateCounter<long>("kupro.meeting_agent.refused", "{call}", "Model calls refused: budget used up or daily ceiling reached");
    }

    public void Audio(double seconds, string? model) => _audio.Add(seconds, new KeyValuePair<string, object?>("model", model));

    public void Tokens(ModelUsage usage, string? model)
    {
        _tokens.Add(usage.InputTokens, new("model", model), new("direction", "input"));
        _tokens.Add(usage.OutputTokens, new("model", model), new("direction", "output"));
    }

    public void Spend(decimal cents, string seam, string? model, string workspace) =>
        _spend.Add((double)(cents / 100), new("seam", seam), new("model", model), new("workspace", workspace));

    public void Refused(BudgetState state) =>
        _refused.Add(1, new KeyValuePair<string, object?>("reason", state == BudgetState.Ceiling ? "ceiling" : "budget"));
}
