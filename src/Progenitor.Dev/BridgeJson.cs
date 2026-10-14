using System.Text.Json;
using System.Text.Json.Serialization;

namespace Progenitor.Dev;

/// <summary>
/// The bridge's reply serializer settings. Game-free so the test project can pin them. Non-finite
/// floats (a component field holding infinity or NaN, e.g. on a castle heart) are written as the strings
/// <c>"Infinity"</c>, <c>"-Infinity"</c> and <c>"NaN"</c>; without that, one such field made a whole
/// <c>get</c> or <c>inspect</c> reply fail.
/// </summary>
internal static class BridgeJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
}
