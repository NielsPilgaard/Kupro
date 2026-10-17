using System.Globalization;

namespace Flowstate.BackupAgent;

/// <summary>Console formatting. Times are stored in UTC and shown in Europe/Copenhagen, in English.</summary>
public static class Fmt
{
    public static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    public static readonly TimeZoneInfo Copenhagen = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");

    public static DateTimeOffset Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Copenhagen);

    public static DateOnly LocalDate(DateTimeOffset at) => DateOnly.FromDateTime(Local(at).DateTime);

    public static string DateTime(DateTimeOffset? at) =>
        at is { } value ? Local(value).ToString("d MMM yyyy HH:mm", Culture) : "—";

    public static string DateTimeSeconds(DateTimeOffset? at) =>
        at is { } value ? Local(value).ToString("d MMM yyyy HH:mm:ss", Culture) : "—";

    public static string Date(DateTimeOffset? at) =>
        at is { } value ? Local(value).ToString("d MMM yyyy", Culture) : "—";

    public static string Date(DateOnly date) => date.ToString("d MMM yyyy", Culture);

    public static string Number(long value) => value.ToString("N0", Culture);

    public static string Ago(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } value)
        {
            return "never";
        }

        var span = now - value;
        return span.TotalMinutes switch
        {
            < 1 => "just now",
            < 90 => $"{(int)span.TotalMinutes} min ago",
            < 48 * 60 => $"{(int)span.TotalHours} h ago",
            _ => $"{(int)span.TotalDays} days ago",
        };
    }

    public static string Duration(double seconds) => seconds switch
    {
        < 90 => string.Create(Culture, $"{seconds:0} s"),
        < 90 * 60 => string.Create(Culture, $"{seconds / 60:0.#} min"),
        _ => string.Create(Culture, $"{seconds / 3600:0.#} h"),
    };

    public static string Bytes(long? bytes) => bytes switch
    {
        null => "—",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(Culture, $"{bytes / 1024.0:0.#} KB"),
        < 1024L * 1024 * 1024 => string.Create(Culture, $"{bytes / 1024.0 / 1024:0.#} MB"),
        _ => string.Create(Culture, $"{bytes / 1024.0 / 1024 / 1024:0.##} GB"),
    };

    /// <summary>Parses a local Copenhagen time from an &lt;input type="datetime-local"&gt;.</summary>
    public static DateTimeOffset? ParseLocal(string? value)
    {
        if (!System.DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        var unspecified = System.DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, Copenhagen.GetUtcOffset(unspecified));
    }

    public static string InputValue(DateTimeOffset at) => Local(at).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    /// <summary>A target pgBackRest and Postgres both read unambiguously.</summary>
    public static string PgTimestamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.ffffff'+00'", CultureInfo.InvariantCulture);
}
