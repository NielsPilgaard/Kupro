using System.Net.Http.Headers;
using System.Security.Claims;
using Flowstate.Api.Auth;
using Flowstate.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Flowstate.Tests.Infrastructure;

/// <summary>
/// In-process API wired to the shared Testcontainers Postgres. Keycloak is replaced by a local signing key so tests
/// can mint JWTs that go through the real JwtBearer pipeline (including user provisioning).
/// </summary>
public sealed class FlowstateApiFactory(PostgresFixture postgres) : WebApplicationFactory<Program>
{
    private const string Issuer = "https://keycloak.test/realms/flowstate";
    private static readonly SymmetricSecurityKey SigningKey = new("flowstate-tests-signing-key-0123456789abcdef"u8.ToArray());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting($"ConnectionStrings:{DatabaseExtensions.ConnectionName}", postgres.ConnectionString);
        builder.UseSetting("services:keycloak:http:0", "http://keycloak.test");

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

    /// <summary>Starts the server and applies migrations, the same way the migrations resource does.</summary>
    public async Task InitializeDatabaseAsync() => await Services.MigrateDatabaseAsync();

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
        return new TestUser(id, $"{id}@test.flowstate", name);
    }
}
