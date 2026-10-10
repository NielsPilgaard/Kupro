using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flowstate.Tests.Infrastructure;

/// <summary>Same JSON shape as the API (camelCase, enums as camelCase strings).</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
