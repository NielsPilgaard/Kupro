using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using Kupro.Api.Features.Email;
using Kupro.Api.Features.Imports;
using Kupro.Import;
using Kupro.Import.Snapshot;
using Kupro.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kupro.Tests.Features;

/// <summary>Imports Kupro reads itself: deltas, a rejected token, auto-sync (task 023, ADR-019 Source access and Mode).</summary>
public sealed partial class ImportCommitTests
{
    private const string FakeKind = "fake";

    private void Serve(ISourceConnector connector, string token = "good") =>
        Factory.Services.GetRequiredService<ImportSources>().Register(FakeKind, (_, given) =>
            given == token ? connector : new Refusing());

    private async Task<Guid> ConnectedAsync(FakeConnector source)
    {
        var created = await WithImportsAsync(s => s.CreateAsync(new CreateImportRequest(FakeKind, Token: "good"), Ct));
        created.Error.ShouldBeNull();
        var mapping = await MappingAsync(source);
        await WithDbAsync(async db =>
            await db.WorkspaceImports.Where(i => i.Id == created.Value!.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.Mapping, mapping.ToJson()), Ct));
        source.Cursors.Clear();
        return created.Value!.Id;
    }

    [Fact]
    public async Task A_connected_import_reads_the_source_then_only_what_changed_since()
    {
        var source = Source(Item("a", key: "WEB-1"));
        source.NextCursor = "2026-01-01T00:00:00Z";
        Serve(source);
        var import = await ConnectedAsync(source);

        (await RunAsync(import, ImportRunKind.DryRun)).State.ShouldBe(ImportRunState.Succeeded);
        (await RunAsync(import, ImportRunKind.Commit)).State.ShouldBe(ImportRunState.Succeeded);
        source.Cursors.ShouldBe([null], "the commit writes what the plan read");
        (await IssueAsync("WEB-1")).Title.ShouldBe("Item a");

        source.Records.Add(Item("b", key: "WEB-2"));
        var second = await RunAsync(import, ImportRunKind.Commit);

        second.State.ShouldBe(ImportRunState.Succeeded, second.Error);
        source.Cursors.ShouldBe([null, "2025-12-31T23:50:00.0000000Z"]);
        (await IssueAsync("WEB-2")).Title.ShouldBe("Item b");
        await WithDbAsync(async db =>
        {
            var row = await db.WorkspaceImports.SingleAsync(i => i.Id == import, Ct);
            row.Cursor.ShouldBe("2026-01-01T00:00:00Z");
            row.EncryptedToken.ShouldNotBe("good");
        });
    }

    [Fact]
    public async Task A_rejected_token_fails_the_run_and_pauses_auto_sync_until_it_is_replaced()
    {
        var source = Source(Item("a", key: "WEB-1"));
        Serve(source, token: "new");
        var import = await ConnectedAsync(source);
        (await WithImportsAsync(s => s.SetAutoSyncAsync(import, true, Ct))).Error.ShouldBeNull();

        var failed = await RunAsync(import, ImportRunKind.Commit);

        failed.State.ShouldBe(ImportRunState.Failed);
        failed.Error!.ShouldContain("Paste a new token");
        var email = new Outbox();
        var later = DateTimeOffset.UtcNow.AddHours(2);
        (await SyncAsync(import, email, later)).ShouldBeFalse();

        (await WithImportsAsync(s => s.ReplaceTokenAsync(import, "new", Ct))).Error.ShouldBeNull();
        (await SyncAsync(import, email, later)).ShouldBeTrue();
        var resumed = await WaitForAsync(await LatestRunAsync(import));
        resumed.State.ShouldBe(ImportRunState.Succeeded, resumed.Error);
        (await IssueAsync("WEB-1")).Title.ShouldBe("Item a");
    }

    [Fact]
    public async Task Auto_sync_runs_hourly_and_turns_itself_off_after_thirty_quiet_days()
    {
        var source = Source(Item("a", key: "WEB-1"));
        Serve(source);
        var import = await ConnectedAsync(source);
        (await RunAsync(import, ImportRunKind.Commit)).State.ShouldBe(ImportRunState.Succeeded);
        (await WithImportsAsync(s => s.SetAutoSyncAsync(import, true, Ct))).Error.ShouldBeNull();
        var email = new Outbox();
        var now = DateTimeOffset.UtcNow;

        (await SyncAsync(import, email, now)).ShouldBeFalse("it ran less than an hour ago");
        (await SyncAsync(import, email, now.AddHours(2))).ShouldBeTrue();
        (await WaitForAsync(await LatestRunAsync(import))).State.ShouldBe(ImportRunState.Succeeded);

        (await SyncAsync(import, email, now.AddDays(40))).ShouldBeFalse();
        await WithDbAsync(async db => (await db.WorkspaceImports.SingleAsync(i => i.Id == import, Ct)).AutoSync.ShouldBeFalse());
        email.Sent.ShouldContain(m => m.Subject.Contains("Auto-sync", StringComparison.Ordinal) && m.Subject.Contains("Importing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tokens_are_only_kept_for_sources_kupro_can_read_and_auto_sync_needs_one()
    {
        (await WithImportsAsync(s => s.CreateAsync(new CreateImportRequest("asana", Token: "t"), Ct))).Error!.Code.ShouldBe("source_unsupported");

        var upload = await WithImportsAsync(s => s.CreateAsync(new CreateImportRequest(SourceKinds.Csv), Ct));
        (await WithImportsAsync(s => s.SetAutoSyncAsync(upload.Value!.Id, true, Ct))).Error!.Code.ShouldBe("auto_sync_needs_token");

        var source = Source(Item("a", key: "WEB-1"));
        Serve(source);
        var import = await ConnectedAsync(source);
        (await WithImportsAsync(s => s.DescribeAsync(import, Ct))).Value!.Projects.Single().Key.ShouldBe("WEB");
    }

    /// <summary>Runs the scheduler at <paramref name="now"/> and says whether it queued a run for this import (other tests' imports share the database).</summary>
    private async Task<bool> SyncAsync(Guid import, IEmailSender email, DateTimeOffset now)
    {
        var before = 0;
        await WithDbAsync(async db => before = await db.ImportRuns.CountAsync(r => r.ImportId == import, Ct));
        await WithDbAsync(async db => await ImportJob.AutoSyncAsync(db, email, now, Ct));
        Factory.Services.GetRequiredService<ImportSignal>().Signal();
        var after = 0;
        await WithDbAsync(async db => after = await db.ImportRuns.CountAsync(r => r.ImportId == import, Ct));
        return after > before;
    }

    private async Task<Guid> LatestRunAsync(Guid import)
    {
        Guid id = default;
        await WithDbAsync(async db => id = await db.ImportRuns.Where(r => r.ImportId == import).OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Select(r => r.Id).FirstAsync(Ct));
        return id;
    }

    /// <summary>What a source does with a token it doesn't know.</summary>
    private sealed class Refusing : ISourceConnector
    {
        public Task<SourceDescription> DescribeAsync(CancellationToken ct) => throw Rejected();

        public async IAsyncEnumerable<ISnapshotRecord> ReadAsync(ReadScope scope, string? cursor, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            throw Rejected();
#pragma warning disable CS0162 // An iterator needs a yield to be one.
            yield break;
#pragma warning restore CS0162
        }

        public Task<Stream> FetchAttachmentAsync(SourceAttachment attachment, CancellationToken ct) => throw Rejected();

        private static SourceAccessException Rejected() => new("The source rejected the token.", HttpStatusCode.Unauthorized);
    }

    private sealed class Outbox : IEmailSender
    {
        private readonly ConcurrentQueue<EmailMessage> _sent = new();

        public IReadOnlyList<EmailMessage> Sent => [.. _sent];

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            _sent.Enqueue(message);
            return Task.CompletedTask;
        }
    }
}
