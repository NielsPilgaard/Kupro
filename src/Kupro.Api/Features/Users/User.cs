using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kupro.Api.Features.Users;

/// <summary>
/// A person known to Kupro. Global (not tenant-owned): workspace membership is a separate entity (task 003).
/// Keycloak answers "who"; everything else about the user lives here (ADR-005).
/// </summary>
public sealed class User
{
    public Guid Id { get; set; }

    /// <summary>Keycloak subject (<c>sub</c>).</summary>
    public required string ExternalId { get; set; }

    public required string Email { get; set; }

    /// <summary>Keycloak verified the email; required before invites addressed to it can be accepted.</summary>
    public bool EmailVerified { get; set; }
    public required string DisplayName { get; set; }

    /// <summary>UI language, <c>en</c> or <c>da</c> (ADR-011).</summary>
    public required string Locale { get; set; }

    /// <summary>IANA time zone id.</summary>
    public required string TimeZone { get; set; }

    /// <summary>Which push-to-talk button the web shows (task 036).</summary>
    public VoiceButton VoiceButton { get; set; }

    /// <summary>Voice control applies sure changes without asking; held ones still wait (task 036).</summary>
    public bool VoiceAutoApply { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasIndex(u => u.ExternalId).IsUnique();
        builder.Property(u => u.ExternalId).HasMaxLength(255);
        builder.Property(u => u.Email).HasMaxLength(320);
        builder.Property(u => u.DisplayName).HasMaxLength(200);
        builder.Property(u => u.Locale).HasMaxLength(8);
        builder.Property(u => u.TimeZone).HasMaxLength(64);
        // Defaults in the database too: first login inserts users with raw SQL that doesn't name these columns.
        builder.Property(u => u.VoiceButton).HasConversion<string>().HasMaxLength(8).HasDefaultValue(VoiceButton.TopBar);
        builder.Property(u => u.VoiceAutoApply).HasDefaultValue(false);
    }
}

/// <summary>The push-to-talk button's style, each person's choice (task 036).</summary>
public enum VoiceButton
{
    /// <summary>A Talk button next to search; results drop down under the top bar. The default.</summary>
    TopBar,

    /// <summary>A large round button within thumb reach; results in a bottom sheet with big buttons.</summary>
    Thumb,
}
