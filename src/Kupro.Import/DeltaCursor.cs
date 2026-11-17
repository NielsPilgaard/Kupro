using System.Globalization;

namespace Kupro.Import;

/// <summary>
/// The cursor every connector with deltas writes (ADR-019 Mode, delta runs): the UTC time its read started, so anything
/// changed while a read ran is read again by the next. The server moves it back by an overlap for eventually consistent
/// searches; re-reading an item is harmless, as runs match by source ID.
/// </summary>
public static class DeltaCursor
{
    /// <summary>When to read changes from; null for a full read.</summary>
    public static DateTimeOffset? Since(string? cursor) =>
        cursor is null ? null
        : TryParse(cursor, out var at) ? at
        : throw new ArgumentException($"'{cursor}' is not a delta cursor (a UTC time).", nameof(cursor));

    /// <summary>The cursor for a read that started at <paramref name="started"/>.</summary>
    public static string At(DateTimeOffset started) => started.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>The cursor moved back by <paramref name="overlap"/>. Anything that isn't a time (a script's own cursor) passes as it is.</summary>
    public static string? WithOverlap(string? cursor, TimeSpan overlap) =>
        cursor is not null && TryParse(cursor, out var at) ? At(at - overlap) : cursor;

    private static bool TryParse(string cursor, out DateTimeOffset at) =>
        DateTimeOffset.TryParse(cursor, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out at);
}
