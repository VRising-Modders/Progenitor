using System;

namespace Progenitor.Ui;

/// <summary>
/// JOINFLOW4: the "Kept versions" group in <see cref="SettingsPanel"/>: every mod with versions the version store keeps
/// (parked, not loaded), with <b>Restore</b> for the player's own. It used to follow the "Server mods" group, which went
/// with server-to-client distribution (2026-10-05).
/// </summary>
internal static class KeptVersions
{
    /// <summary>JOINFLOW4: the group header of the kept versions.</summary>
    public const string KeptGroupLabel = "— Kept versions —";

    /// <summary>Append the group to an open settings panel. Nothing when no versions are kept; never throws into the panel build.</summary>
    internal static void AddGroup(IUiPanel panel)
    {
        if (panel == null) return;
        try
        {
            var rows = Progenitor.Networking.ModVersionJoin.AllPageLines();
            if (rows.Count == 0) return;
            panel.Label(KeptGroupLabel);
            foreach (var (id, line, restore) in rows)
            {
                panel.Label(id + ": " + line);
                if (restore.Length == 0) continue;
                string modId = id, version = restore;
                panel.Button("Restore " + version).OnClick(() =>
                {
                    string note = Progenitor.Networking.ModVersionJoin.RestoreFromMenu(modId, version);
                    Plugin.Logger?.LogInfo("Ui: settings kept versions: " + note);
                    SettingsPanel.Open();
                });
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: kept versions group threw: " + e.Message); }
    }
}
