using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Dev;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The bridge's reply serializer writes non-finite floats as strings instead of failing the whole
/// reply (a castle heart's component carries an infinite float; <c>get</c>/<c>inspect</c> on it failed).
/// </summary>
public class BridgeJsonTests
{
    [Fact]
    public void Non_finite_floats_are_written_as_strings_in_boxed_component_fields()
    {
        var fields = new Dictionary<string, object?>
        {
            ["max"] = float.PositiveInfinity,
            ["min"] = float.NegativeInfinity,
            ["ratio"] = double.NaN,
            ["value"] = 1.5f,
        };
        var json = JsonSerializer.Serialize(new { id = 1, ok = true, result = new { fields } }, BridgeJson.Options);
        Assert.Contains("\"max\":\"Infinity\"", json);
        Assert.Contains("\"min\":\"-Infinity\"", json);
        Assert.Contains("\"ratio\":\"NaN\"", json);
        Assert.Contains("\"value\":1.5", json);
    }

    [Fact]
    public void Replies_keep_camel_case_and_one_line()
    {
        var json = JsonSerializer.Serialize(new { Ok = true, SomeValue = 2 }, BridgeJson.Options);
        Assert.Equal("{\"ok\":true,\"someValue\":2}", json);
    }
}
