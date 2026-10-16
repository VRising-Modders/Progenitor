using System;

namespace Progenitor.Reload;

/// <summary>
/// Game-free sweep of the owner-keyed <b>static</b> module APIs. Plugin unload must call
/// <c>Assets.RemoveAll</c>, <c>Ui.RemoveAll</c> and <c>ServerBeam.RemoveAll</c> or a plugin
/// that used <c>Assets.Borrow(..., this)</c> / <c>Ui.Panel(..., this)</c> /
/// <c>ServerBeam.Spawn(..., owner: this)</c> without <c>PluginContext</c> keeps its ALC alive
/// (R26 finding 6 / R25 #7, and R27 finding 4 for <c>ServerBeam</c>).
///
/// <para>R27 finding 25: this type used to have <b>no production caller</b> — the live path
/// duplicated two of the three calls inline in <see cref="PluginReloader.SweepRegistrations"/>
/// and this one hardcoded <c>FormatSwept(0, 0)</c>, so the log line it defines could never be
/// emitted with a true count and the third API went unswept. <c>SweepRegistrations</c> now
/// calls <see cref="Run"/> and each <c>RemoveAll</c> returns how many handles it disposed.</para>
/// </summary>
internal static class PluginStaticApiSweep
{
    /// <summary>
    /// Exact reload log. Grep <c>swept</c> + <c>effect(s)</c> + <c>panel(s)</c> +
    /// <c>server beam(s)</c>.
    /// </summary>
    public static string FormatSwept(int effects, int panels, int beams) =>
        "Reload: swept " + effects + " effect(s) / " + panels + " panel(s) / "
        + beams + " server beam(s)";

    /// <summary>
    /// Handles dropped by one <see cref="Run"/>, so a caller (and a test) can assert what the
    /// sweep actually did rather than that a string formats.
    /// </summary>
    internal readonly struct SweepCounts
    {
        internal SweepCounts(int effects, int panels, int beams)
        {
            Effects = effects;
            Panels = panels;
            Beams = beams;
        }

        internal int Effects { get; }

        internal int Panels { get; }

        internal int Beams { get; }

        internal int Total => Effects + Panels + Beams;
    }

    /// <summary>
    /// Dispose every static-API effect, panel and server beam recorded for
    /// <paramref name="plugin"/>. Idempotent; each API is guarded on its own so one throwing
    /// module cannot leave the others pinned. Logs <see cref="FormatSwept"/> with the real
    /// counts.
    /// </summary>
    public static SweepCounts Run(object plugin)
    {
        if (plugin == null) return default;
        int effects = 0;
        int panels = 0;
        int beams = 0;

        try { effects = Progenitor.Effects.Assets.RemoveAll(plugin); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Reload: Assets.RemoveAll: " + e.Message); }

        // PINS1: inside the unload sweep, registrations made without an owner are also matched to this plugin by the
        // assembly that declared their callback.
        try
        {
            using (Progenitor.Ui.PluginDelegateSource.UnloadSweep(plugin))
                panels = Progenitor.Ui.Ui.RemoveAll(plugin);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Reload: Ui.RemoveAll: " + e.Message); }

        // R27 finding 4: EV11-EV14 shipped ServerBeam as a new static API in the same batch
        // that fixed R26 #6 and did not add it here. ServerBeamHandle.Owner is a strong
        // reference to the plugin instance, so a missed sweep is a refused Core reload.
        try { beams = Progenitor.Effects.ServerBeam.RemoveAll(plugin); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Reload: ServerBeam.RemoveAll: " + e.Message); }

        Plugin.Logger?.LogInfo(FormatSwept(effects, panels, beams));
        return new SweepCounts(effects, panels, beams);
    }
}
