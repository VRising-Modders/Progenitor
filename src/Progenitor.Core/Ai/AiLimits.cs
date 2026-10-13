using System.Globalization;

namespace Progenitor.Ai;

/// <summary>
/// Measured capacity guidance for boss AI (live 2026-09-24, isolated server, Dracula's arena, one
/// player, Frostmaws engaged on that player): about 200 fighting bosses keep the server at its 30 fps
/// simulation rate, 300 run at about 19 fps, 400 or more collapse. Private tree copies add no
/// measurable cost; the limit is fighting bosses. See docs/ai.md, "Capacity". Guidance only:
/// nothing in Progenitor refuses a spawn because of it.
/// </summary>
public static class AiLimits
{
    /// <summary>Live units one plugin can keep spawned before <c>ctx.Units</c> logs one warning.</summary>
    public const int RecommendedActiveUnits = 200;

    /// <summary>
    /// True the first time <paramref name="liveUnits"/> (units this plugin spawned and still owns)
    /// exceeds <see cref="RecommendedActiveUnits"/>. <paramref name="alreadyWarned"/> makes it once
    /// per plugin generation.
    /// </summary>
    public static bool ShouldWarn(int liveUnits, bool alreadyWarned) =>
        !alreadyWarned && liveUnits > RecommendedActiveUnits;

    /// <summary>The one warning line.</summary>
    public static string FormatWarning(string pluginId, int liveUnits) =>
        "Units: plugin " + (string.IsNullOrEmpty(pluginId) ? "?" : pluginId) + " now owns "
        + liveUnits.ToString(CultureInfo.InvariantCulture) + " live units, more than the measured "
        + RecommendedActiveUnits.ToString(CultureInfo.InvariantCulture)
        + " fighting bosses that keep a server at 30 fps (docs/ai.md, Capacity). Spawns are not refused; this is logged once.";
}
