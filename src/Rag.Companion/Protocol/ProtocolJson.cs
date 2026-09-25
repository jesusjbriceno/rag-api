using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rag.Companion.Protocol;

/// <summary>
/// Shared JSON contract for the companion protocol: camelCase names, string enums,
/// and strict rejection of any unknown member (paths, content, commands, provider data).
/// </summary>
public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
