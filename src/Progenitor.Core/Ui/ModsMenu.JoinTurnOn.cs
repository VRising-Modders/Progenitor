using System;
using Progenitor.Networking;
using Progenitor.Plugins;

namespace Progenitor.Ui;

/// <summary>
/// JOINTURNON: the Mods list's switch as the join screens (Turn on) and the dev bridge (<c>join_ui mod_switch</c>) use it.
/// One source of truth: the same <see cref="ModSwitch"/> decision and <see cref="ModEnableState"/> / preloader requests
/// the switch on a mod's page uses, so the list shows the change afterwards.
/// </summary>
public static partial class ModsMenu
{
    /// <summary>
    /// The player's turned-off copy of <paramref name="id"/>, or null: not installed, running, part of Progenitor, off only
    /// because a dependency is off, or merely paused for a server (<c>ServerModPause</c> is not the player's choice).
    /// </summary>
    internal static JoinOffCopy? JoinOffCopyOf(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || ModEnableState.IsRequiredPart(id)) return null;
        try
        {
            if (ModEnableState.IsDisabled(id))
            {
                // A Progenitor mod the player turned off (live: unloaded and held; restart-only: its OnLoad skipped).
                string name = ModRegistry.TryGet(id, out var known) ? known.Name : id;
                var view = ModSwitch.Describe(id, name, ModOwner.Progenitor);
                if (!view.Interactive || view.On) return null;
                return new JoinOffCopy
                {
                    Version = OffProgenitorVersion(id),
                    AppliesNow = view.Mode == ModSwitchMode.Live,
                    TurnsOnWithInstall = true,
                };
            }

            // Live 2026-10-05: on, but held by the per-server pause (in Bootstrap's table, not held by the switch). Not the
            // player's choice; a recommended row reads it as installed ("Paused here") so its switch never downloads it.
            if (ModSwitch.ServerPausedNow().Exists(p => string.Equals(p, id, StringComparison.OrdinalIgnoreCase)))
                return new JoinOffCopy { Version = OffProgenitorVersion(id), PausedHere = true };

            // Turned on again after this start skipped it (restart-only): it loads at the next start.
            if (ModRegistry.TryGet(id, out var entry) && entry.Outcome == ModLoadOutcome.SkippedOff && entry.OffDependency.Length == 0)
            {
                var view = ModSwitch.Describe(id, entry.Name, ModOwner.Progenitor);
                return view.Mode == ModSwitchMode.Restart && view.Pending == ModSwitchPending.TurnsOn
                    ? new JoinOffCopy { Version = entry.Version, TurnsOnAtNextStart = true, TurnsOnWithInstall = true }
                    : null;
            }

            // An ordinary BepInEx plugin whose DLLs the preloader renamed off.
            if (ModSwitch.PlainOffOnDisk(id))
            {
                var view = ModSwitch.Describe(id, id, ModOwner.Plain, null);
                if (!view.Interactive) return null;
                string version = ModSwitch.DisabledVersionOf(id);
                if (view.On)
                    return view.Pending == ModSwitchPending.TurnsOn ? new JoinOffCopy { Version = version, TurnsOnAtNextStart = true } : null;
                return new JoinOffCopy { Version = version };
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("JoinFlow: the switch state of " + id + " could not be read: " + e.Message);
        }

        return null;
    }

    /// <summary>
    /// CLIENTPOLICY2: the player switched <paramref name="id"/> off in the Mods list and it is still loaded until the next
    /// start (an ordinary plugin the preloader renames off, or a restart-only Progenitor mod): the switch's own pending state.
    /// </summary>
    internal static bool JoinTurnsOffAtNextStart(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            id = id.Trim();
            var row = CurrentSidebarModel(applySearch: false).Find(id);
            var owner = OwnerOf(id, row);
            if (owner == ModOwner.Unknown) return false;
            var plain = owner == ModOwner.Plain ? PlainIdentityOf(id) : null;
            string name = ModRegistry.TryGet(id, out var entry) ? entry.Name : id;
            var view = ModSwitch.Describe(id, name, owner, plain);
            return !view.On && view.Pending == ModSwitchPending.TurnsOff;
        }
        catch { return false; }
    }

    /// <summary>A turned-off Progenitor mod's version: its last load's metadata, else Bootstrap's pause table.</summary>
    private static string OffProgenitorVersion(string id)
    {
        if (ModRegistry.TryGet(id, out var e) && e.Version.Length > 0) return e.Version;
        try
        {
            var backend = ModSwitch.Backend();
            if (backend.Supported)
                foreach (var p in backend.Suspended())
                    if (string.Equals(p.Guid, id, StringComparison.OrdinalIgnoreCase)) return p.Version;
        }
        catch { /* unknown */ }

        return "";
    }

    /// <summary>
    /// Press the switch of <paramref name="id"/> from outside its page: the list's own path (<see cref="ApplySwitch"/>'s
    /// steps, without the confirmation and without moving the workspace to the mod). A live change steps off the mod's
    /// page first when it is on show; turning a Progenitor mod off also turns off, at the next start, the ordinary
    /// plugins that need it. The list redraws through <see cref="ModSwitch.Changed"/>.
    /// </summary>
    internal static JoinSwitchResult SwitchForJoin(string id, bool on)
    {
        if (string.IsNullOrWhiteSpace(id)) return JoinSwitchResult.Fail("no mod id");
        id = id.Trim();
        var row = CurrentSidebarModel(applySearch: false).Find(id);
        var owner = OwnerOf(id, row);
        if (owner == ModOwner.Unknown && ModSwitch.PlainOffOnDisk(id)) owner = ModOwner.Plain;
        var plain = owner == ModOwner.Plain ? PlainIdentityOf(id) : null;
        string name = row != null ? SidebarNameOf(row, i => ResolveInstallLink(i)?.Key)
            : ModRegistry.TryGet(id, out var entry) ? entry.Name : id;
        var view = ModSwitch.Describe(id, name, owner, plain);
        string mode = ModSwitchView.NameOf(view.Mode);
        if (!view.Interactive)
            return new JoinSwitchResult { Ok = false, Message = view.Reason.Length > 0 ? view.Reason : "Its switch cannot act.", Mode = mode, Name = name };
        bool live = view.Mode == ModSwitchMode.Live;
        if (view.On == on)
            return new JoinSwitchResult { Ok = true, Message = on ? "It is already on." : "It is already off.", Now = live, Mode = mode, Name = name };

        if (live && IsOpen && string.Equals(_selected, id, StringComparison.OrdinalIgnoreCase))
            Open(ReasonUserButton);
        var outcome = ModSwitch.Set(id, name, owner, on, plain);
        string message = outcome.Message;
        if (outcome.Ok && !on && owner == ModOwner.Progenitor)
        {
            var also = ModSwitch.TurnOffPlainDependants(id, PlainIdentityOf);
            if (also.Count > 0) message += " " + PlainAlsoOffNote(also);
        }

        return new JoinSwitchResult
        {
            Ok = outcome.Ok,
            Message = message,
            Now = live,
            Mode = mode,
            Name = name,
            // Live 2026-10-05: the switch's own answer says the per-server pause keeps it unloaded here.
            PausedHere = outcome.Ok && on && string.Equals(outcome.Message, ModSwitch.TurnedOnPaused, StringComparison.Ordinal),
            LoadsAfterLeave = outcome.Ok && on && string.Equals(outcome.Message, ModSwitchWords.TurnedOnAfterLeave, StringComparison.Ordinal),
        };
    }
}
