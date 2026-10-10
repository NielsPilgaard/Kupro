using Microsoft.AspNetCore.Mvc;

namespace Flowstate.Api.Common;

/// <summary>
/// Problem Details (RFC 9457) with stable machine-readable codes (ADR-001). Clients and agents switch on
/// <c>code</c>; the SPA localizes from it (ADR-011).
/// </summary>
public static class Problems
{
    public const string TypePrefix = "urn:flowstate:error:";

    public static ObjectResult Problem(this ControllerBase controller, int status, string code, string? detail = null)
    {
        var result = controller.Problem(statusCode: status, type: TypePrefix + code, detail: detail);
        ((ProblemDetails)result.Value!).Extensions["code"] = code;
        return result;
    }

    /// <summary>Gives model-validation failures the same stable <c>type</c>/<c>code</c> shape.</summary>
    public static IActionResult ValidationProblem(ActionContext context)
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = StatusCodes.Status400BadRequest,
            Type = TypePrefix + "validation",
        };
        problem.Extensions["code"] = "validation";
        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    }
}
