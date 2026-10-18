namespace Aspire.Hosting;

/// <summary>Production SMTP (Scaleway TEM, ADR-006), shared by Keycloak's and Grafana's emails.</summary>
internal static class Smtp
{
    public const string Host = "smtp.tem.scaleway.com";
    public const string Port = "587";

    /// <summary>One credential pair for every service that sends mail; safe to call more than once.</summary>
    public static (IResourceBuilder<ParameterResource> User, IResourceBuilder<ParameterResource> Password) AddSmtpCredentials(
        this IDistributedApplicationBuilder builder) =>
        (GetOrAdd(builder, "smtp-user", secret: false), GetOrAdd(builder, "smtp-password", secret: true));

    private static IResourceBuilder<ParameterResource> GetOrAdd(IDistributedApplicationBuilder builder, string name, bool secret) =>
        builder.Resources.OfType<ParameterResource>().FirstOrDefault(p => p.Name == name) is { } existing
            ? builder.CreateResourceBuilder(existing)
            : builder.AddParameter(name, secret: secret);
}
