using System.Security.Cryptography;
using Flowstate.Api.Auth;
using Flowstate.Api.Data;
using Flowstate.Api.Features.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql;

namespace Flowstate.Api.Common;

/// <summary>A stored response for an <c>Idempotency-Key</c>, per user (ADR-001). Pending while StatusCode is null.</summary>
public sealed class IdempotencyRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Key { get; set; }
    public required byte[] RequestHash { get; set; }
    public int? StatusCode { get; set; }
    public string? ContentType { get; set; }
    public byte[]? ResponseBody { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(r => new { r.UserId, r.Key }).IsUnique();
        builder.HasIndex(r => r.CreatedAt);
        builder.Property(r => r.Key).HasMaxLength(255);
        builder.Property(r => r.ContentType).HasMaxLength(100);
    }
}

/// <summary>
/// Makes retried mutations safe for agents: the same <c>Idempotency-Key</c> with the same request replays the first
/// response instead of running the mutation again. Keys live 24 hours. 5xx responses are not stored, so they can be
/// retried.
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next, TimeProvider clock)
{
    public const string Header = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private const int MaxStoredBody = 1024 * 1024;

    public async Task InvokeAsync(HttpContext context, IDbContextFactory<FlowstateDbContext> dbFactory)
    {
        var key = context.Request.Headers[Header].ToString();
        if (key.Length == 0 || HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)
            || context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        if (key.Length > 255)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "idempotency_key_invalid");
            return;
        }

        var userId = context.User.GetUserId();
        var hash = await HashRequestAsync(context.Request);
        var ct = context.RequestAborted;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var existing = await db.IdempotencyRecords.AsNoTracking()
                .SingleOrDefaultAsync(r => r.UserId == userId && r.Key == key, ct);
            if (existing is not null && existing.CreatedAt < clock.GetUtcNow() - Lifetime)
            {
                await db.IdempotencyRecords.Where(r => r.Id == existing.Id).ExecuteDeleteAsync(ct);
                existing = null;
            }

            if (existing is null)
            {
                existing = await TryClaimAsync(db, userId, key, hash, ct);
            }

            if (existing is not null)
            {
                await ReplayAsync(context, existing, hash);
                return;
            }
        }

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            var records = db.IdempotencyRecords.Where(r => r.UserId == userId && r.Key == key);
            if (context.Response.StatusCode >= 500 || buffer.Length > MaxStoredBody)
            {
                await records.ExecuteDeleteAsync(CancellationToken.None);
            }
            else
            {
                var body = buffer.ToArray();
                var status = context.Response.StatusCode;
                var contentType = context.Response.ContentType;
                await records.ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.StatusCode, status)
                    .SetProperty(r => r.ContentType, contentType)
                    .SetProperty(r => r.ResponseBody, body), CancellationToken.None);
            }
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody, ct);
    }

    /// <summary>Inserts a pending record. Returns null when claimed, or the record another request claimed first.</summary>
    private async Task<IdempotencyRecord?> TryClaimAsync(
        FlowstateDbContext db, Guid userId, string key, byte[] hash, CancellationToken ct)
    {
        db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Key = key,
            RequestHash = hash,
            CreatedAt = clock.GetUtcNow(),
        });
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return await db.IdempotencyRecords.AsNoTracking().SingleAsync(r => r.UserId == userId && r.Key == key, ct);
        }
    }

    private static async Task ReplayAsync(HttpContext context, IdempotencyRecord record, byte[] hash)
    {
        if (!record.RequestHash.AsSpan().SequenceEqual(hash))
        {
            await WriteProblemAsync(context, StatusCodes.Status422UnprocessableEntity, "idempotency_key_reused",
                "This Idempotency-Key was used for a different request.");
            return;
        }

        if (record.StatusCode is not { } status)
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, "idempotency_in_progress",
                "A request with this Idempotency-Key is still running.");
            return;
        }

        context.Response.StatusCode = status;
        context.Response.Headers[ReplayedHeader] = "true";
        if (record.ContentType is not null)
        {
            context.Response.ContentType = record.ContentType;
        }

        if (record.ResponseBody is { Length: > 0 } body)
        {
            await context.Response.Body.WriteAsync(body, context.RequestAborted);
        }
    }

    private static async Task<byte[]> HashRequestAsync(HttpRequest request)
    {
        request.EnableBuffering();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(System.Text.Encoding.UTF8.GetBytes(request.Method + " " + request.Path + request.QueryString + "\n"));
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
        {
            sha.AppendData(chunk, 0, read);
        }

        request.Body.Position = 0;
        return sha.GetHashAndReset();
    }

    private static Task WriteProblemAsync(HttpContext context, int status, string code, string? detail = null)
    {
        var result = Problems.Create(context, status, code, detail);
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(result.Value, (System.Text.Json.JsonSerializerOptions?)null,
            "application/problem+json", context.RequestAborted);
    }
}
