using System;

namespace Progenitor.Ui;

public sealed partial class PluginHud
{
    /// <summary>
    /// Experimental (P7). Capture the local player's next interact on a container or station and
    /// return the entity instead of opening its panel.
    ///
    /// <para><b>While armed</b> a prompt band sits at the bottom of the screen ("Pick a container or
    /// station", the game's Interact key, Esc). The player keeps moving and playing; only the band
    /// itself takes the pointer when hovered. Interacting with a target of a wanted kind
    /// (<see cref="UiWorldPickOptions.Kinds"/>) ends the pick: its panel stays closed (unless
    /// <see cref="UiWorldPickOptions.SuppressPanel"/> is false), the game is told the interaction
    /// ended, and <paramref name="onPicked"/> runs on the main thread with the entity. Interacting
    /// with anything else (a door, a waygate, a station of an unwanted kind) behaves as usual and
    /// the pick stays armed.</para>
    ///
    /// <para><b>Escape</b> cancels the pick and nothing else: the key is taken before the game's
    /// system menu and before the framework hides open panels. An open framework dialog still
    /// answers Escape first.</para>
    ///
    /// <para><b>One pick at a time, process-wide.</b> A second call (from this plugin or another)
    /// ends the armed pick as Cancelled, running its <paramref name="onCancelled"/>, before arming
    /// the new one. With no client world (the title screen) the pick is cancelled at once.
    /// The pick also ends as Cancelled when its world ends, and as TimedOut after
    /// <see cref="UiWorldPickOptions.TimeoutSeconds"/>; both run <paramref name="onCancelled"/>.
    /// Disposing the handle, unloading the plugin or swapping Core ends it without a callback.</para>
    ///
    /// <para>Client only: on the server this logs once and returns a handle that stays Idle.</para>
    /// </summary>
    public IUiWorldPick PickInWorld(Action<UiWorldPickResult> onPicked, Action? onCancelled = null, UiWorldPickOptions? options = null)
    {
        if (onPicked == null) throw new ArgumentNullException(nameof(onPicked));
        if (ServerSide(nameof(PickInWorld))) return UiWorldPickHandle.Inert();
#if PROGENITOR_TESTS
        return UiWorldPickHandle.Inert();
#else
        string name;
        try { name = _ctx.Owner?.GetType().Name ?? "plugin"; }
        catch { name = "plugin"; }
        return WorldPickBackend.Coordinator.Arm(_ctx.Owner!, name, onPicked, onCancelled, options);
#endif
    }

    static partial void DropPicks(object owner)
    {
#if !PROGENITOR_TESTS
        WorldPickBackend.DropOwner(owner);
#endif
    }
}
