using BepInEx;
using BepInEx.Configuration;
using Progenitor.Plugins;
using Progenitor.Ui;

namespace Progenitor.SampleCompanion;

/// <summary>
/// SETTINGS1 stage 3: the Sample's companion, a second small plugin that shows two things on the Mods
/// page (docs/mod-settings.md, "Stage 3"):
///
/// <list type="bullet">
/// <item><b>A counterpart declaration.</b> <c>[Counterpart(CounterpartNeed.Limited)]</c>: its client
/// part runs without its server part, so its <c>[NetworkCompatibility(ServerMustHave)]</c> row is
/// declared <c>NoNeedForSync</c> instead (the load log says so), it is never paused for a server that
/// lacks it, and on such a server its page says <c>Server part not on this server · limited</c> and
/// grays the one setting marked <c>needsServerPart</c>, with its fallback.</item>
/// <item><b>A problem.</b> Its keybind is F8 on purpose — the key the Sample's ping uses. The winner
/// rule (docs/mods-menu.md, "Who gets a key") keeps F8 for one of them and the other is not active;
/// the losing mod's page and list line show the conflict, and when the companion loads the key
/// conflict dialog asks what to do (KEYS4; README.md in this folder has the steps). Rebind one of
/// them in its Keys section and the conflict goes.</item>
/// </list>
///
/// <para><b>Why a DLL of its own, with no dependency on the Sample.</b> Per-server mod pause takes a
/// DLL and every plugin that depends on it as one unit, and never takes a plugin that declares it
/// works without the server. Inside the Sample's DLL, or depending on it, this companion would make
/// the Sample unpausable. Nothing here can make it fail to load: one config entry, one settings page
/// and one keybind, all tracked by its context.</para>
/// </summary>
[BepInPlugin(Guid, "Progenitor Sample Companion", MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Both)]
[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor)]
[Counterpart(CounterpartNeed.Limited, Features = new[] { "Server echo of the F8 press" })]
public sealed class Plugin : ProgenitorPlugin<CompanionContext>
{
    /// <summary>The companion's plugin GUID.</summary>
    public const string Guid = "progenitor.samplecompanion";

    /// <summary>The keybind id; F8 by default, the same key as the Sample's ping (a deliberate conflict).</summary>
    public const string KeybindId = "CompanionPing";

    /// <inheritdoc />
    protected override void OnLoad(CompanionContext ctx)
    {
        if (ctx.IsServer)
        {
            ctx.Log.LogInfo("Sample companion: server part loaded; a client with the companion runs fully here.");
            return;
        }

        ConfigEntry<bool> echo = ctx.Bind(
            "Companion", "ServerEcho", true,
            "Ask the server part to log a line when F8 is pressed.");
        ctx.Settings.Page(page => page
            .Section("Companion")
                .Toggle(echo, help: "The server logs a line when you press F8.",
                    needsServerPart: true, fallback: "Without the server part, only your client logs the press."));
        ctx.Keybinds.Register(
            KeybindId,
            new KeyChord(UiKey.F8),
            "Log a line. F8 on purpose: the Sample's ping uses it too (a documented keybind conflict).",
            () => ctx.Log.LogInfo("Sample companion: CompanionPing pressed."));
    }
}

/// <summary>The companion's context. Empty, as every context should be.</summary>
public sealed class CompanionContext : PluginContext { }
