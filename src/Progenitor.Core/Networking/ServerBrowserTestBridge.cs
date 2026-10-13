#if !PROGENITOR_TESTS
using System.Collections.Generic;

namespace Progenitor.Networking;

/// <summary>
/// BROWSERFILTER1: the Core half of the dev bridge's <c>server_browser</c> op (client). Dev reaches this type by
/// reflection; it is not a plugin API and no network path reaches it. Main thread.
///
/// <list type="bullet">
/// <item><c>Read(maxRows)</c>: the switch, the checkbox, the filter's last pass, the browser's count line and the shown
/// rows with their tags and whether each carries the Progenitor tag.</item>
/// <item><c>Set(on)</c>: write the switch as the Preferences row does (the checkbox follows on the next frame).</item>
/// <item><c>Click()</c>: flip the cloned checkbox through <c>Toggle.isOn</c>, so its <c>onValueChanged</c> reaches the
/// Bootstrap slot exactly as a player's click does.</item>
/// <item><c>Rebuild()</c>: make the game rebuild the shown list from its full set now (as a filter change would).</item>
/// </list>
/// </summary>
internal static class ServerBrowserTestBridge
{
    public static Dictionary<string, object?> Read(int maxRows) => ServerBrowserFilter.Read(maxRows < 0 ? 0 : maxRows);

    public static Dictionary<string, object?> Set(bool on)
    {
        ServerBrowserFilter.Set(on, "bridge");
        return new Dictionary<string, object?> { ["ok"] = true, ["enabled"] = ServerBrowserFilter.Enabled };
    }

    public static Dictionary<string, object?> Click() => ServerBrowserFilter.Click();

    public static Dictionary<string, object?> Rebuild() => ServerBrowserFilter.Rebuild();
}
#endif
