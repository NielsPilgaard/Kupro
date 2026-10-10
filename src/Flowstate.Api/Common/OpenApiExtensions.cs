using Microsoft.OpenApi;

namespace Flowstate.Api.Common;

public static class OpenApiExtensions
{
    public static IServiceCollection AddFlowstateOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "Flowstate API";
            document.Info.Version = "v1";
            document.Info.Description =
                "Agent-first issue tracker. Authenticate with a Keycloak access token or a Flowstate API token "
                + "(fs_pat_..., fs_agent_...) as a Bearer token. Errors are Problem Details with a stable `code`.";

            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                Description = "Keycloak JWT or Flowstate API token.",
            };
            document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", document)] = [] }];
            return Task.CompletedTask;
        }));
}
