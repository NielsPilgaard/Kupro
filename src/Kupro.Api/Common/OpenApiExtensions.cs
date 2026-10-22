using System.Text.Json;
using Microsoft.OpenApi;

namespace Kupro.Api.Common;

public static class OpenApiExtensions
{
    public static IServiceCollection AddKuproOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options => options.AddOperationTransformer((operation, context, _) =>
        {
            // XML-comment text joins lines with the OS newline; normalize so a Windows build matches CI's spec.
            operation.Summary = Lf(operation.Summary);
            operation.Description = Lf(operation.Description);
            foreach (var parameter in operation.Parameters ?? [])
            {
                parameter.Description = Lf(parameter.Description);
            }

            foreach (var response in operation.Responses ?? [])
            {
                response.Value.Description = Lf(response.Value.Description);
            }

            if (operation.RequestBody is { } body)
            {
                body.Description = Lf(body.Description);
            }

            if (context.Description.HttpMethod is not ("GET" or "HEAD"))
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = IdempotencyMiddleware.Header,
                    In = ParameterLocation.Header,
                    Description = "Retry-safe key: repeating a request with the same key replays the first response.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 255 },
                });
            }

            return Task.CompletedTask;
        }).AddSchemaTransformer((schema, _, _) =>
        {
            schema.Description = Lf(schema.Description);
            foreach (var property in schema.Properties?.Values ?? [])
            {
                property.Description = Lf(property.Description);
            }

            // An optional enum query parameter (IssueQuery.Sort) would otherwise add null to the shared enum schema,
            // and every generated client would treat null as a valid value everywhere.
            if (schema.Enum is { Count: > 0 } values)
            {
                for (var i = values.Count - 1; i >= 0; i--)
                {
                    if (values[i] is null || values[i]!.GetValueKind() == JsonValueKind.Null)
                    {
                        values.RemoveAt(i);
                    }
                }
            }

            return Task.CompletedTask;
        }).AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "Kupro API";
            document.Info.Version = "v1";
            document.Info.Description =
                "Agent-first issue tracker. Authenticate with a Keycloak access token or a Kupro API token "
                + "(kp_pat_..., kp_agent_...) as a Bearer token. Errors are Problem Details with a stable `code`.";

            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                Description = "Keycloak JWT or Kupro API token.",
            };
            document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", document)] = [] }];
            return Task.CompletedTask;
        }));

    private static string? Lf(string? text) => text?.ReplaceLineEndings("\n");
}
