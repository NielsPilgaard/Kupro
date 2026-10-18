using System.Net.Http.Headers;
using System.Security.Claims;
using Kupro.Api.Auth;
using Kupro.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Kupro.Tests.Infrastructure;

/// <summary>
/// In-process API wired to the shared Testcontainers Postgres. Keycloak is replaced by a local signing key so tests
/// can mint JWTs that go through the real JwtBearer pipeline (including user provisioning).
/// </summary>
/// <param name="settings">Extra configuration, e.g. <see cref="StorageFixture.Settings"/>.</param>
public sealed class KuproApiFactory(PostgresFixture postgres, IReadOnlyDictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>
{
    private const string Issuer = "https://keycloak.test/realms/kupro";
    private static readonly SymmetricSecurityKey SigningKey = new("kupro-tests-signing-key-0123456789abcdef"u8.ToArray());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting($"ConnectionStrings:{DatabaseExtensions.ConnectionName}", postgres.ConnectionString);
        builder.UseSetting("services:keycloak:http:0", "http://keycloak.test");
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                configuration.SigningKeys.Add(SigningKey);
                options.Configuration = configuration;
                options.ConfigurationManager = null;
                options.TokenValidationParameters.ValidIssuer = Issuer;
                options.TokenValidationParameters.IssuerSigningKey = SigningKey;
            }));
    }

    private static readonly SemaphoreSlim MigrationGate = new(1, 1);
    private static bool _migrated;

    /// <summary>
    /// Starts the server and applies migrations once per test run (test classes run in parallel against one database),
    /// the same way the migrations resource does.
    /// </summary>
    public async Task InitializeDatabaseAsync()
    {
        await MigrationGate.WaitAsync();
        try
        {
            if (!_migrated)
            {
                await Services.MigrateDatabaseAsync();
                _migrated = true;
            }
        }
        finally
        {
            MigrationGate.Release();
        }
    }

    /// <summary>A client authenticated as a Keycloak user (created on first request).</summary>
    public HttpClient CreateUserClient(TestUser user) => CreateBearerClient(CreateJwt(user));

    public HttpClient CreateBearerClient(string bearer)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    public static string CreateJwt(TestUser user) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = AuthExtensions.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", user.Subject),
                new Claim("email", user.Email),
                new Claim("email_verified", "true", ClaimValueTypes.Boolean),
                new Claim("name", user.Name),
            ]),
        });
}

/// <summary>A unique Keycloak identity per test, so tests sharing the database don't collide.</summary>
public sealed record TestUser(string Subject, string Email, string Name)
{
    public static TestUser New(string name = "Test User")
    {
        var id = Guid.NewGuid().ToString("N");
        return new TestUser(id, $"{id}@test.kupro", name);
    }
}
