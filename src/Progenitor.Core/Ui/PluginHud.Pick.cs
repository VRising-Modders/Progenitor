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
    /// <para><b>Escape</b> cancels the pick only when the pick is the topmost thing. Precedence, first
    /// match wins: a framework dialog (or open dropdown) &gt; a key capture &gt; the game's focused chat
    /// box &gt; any other shown Progenitor panel (the Mods workspace, a mod's panel) &gt; an open game
    /// menu or panel &gt; the pick. Anything above the pick closes as usual and the pick stays armed;
    /// when the pick is topmost, one Escape cancels it and the game's system menu does not open. The
    /// band never blocks gameplay input or clicks.</para>
    ///
    /// <para>The panel is kept closed only when the game could be told the interaction ended; if that
    /// message cannot be sent, the panel opens as usual and <see cref="UiWorldPickResult.Suppressed"/>
    /// is false. Leaving the world (or a kick or disconnect) cancels the pick.</para>
    ///
    /// <para><b>One pick at a time, process-wide.</b> A second call (from this plugin or another)
    /// ends the armed pick as Cancelled, running its <paramref name="onCancelled"/>, before arming
    /// the new one. With no client world (the title screen) the pick is cancelled at once.
    /// The pick also ends as Cancelled when its world ends, and as TimedOut after
    /// <see cref="UiWorldPickOptions.TimeoutSeconds"/>; both run <paramref name="onCancelled"/>.
    /// Disposing the handle, unloading the plugin or swapping Core ends it without a callback.</para>
    ///
    /// <para><b>Repeat</b> (<see cref="UiWorldPickOptions.Repeat"/>, P7b): every matching interact runs
    /// <paramref name="onPicked"/> and the pick stays armed, the band counting the picks, until Escape or
    /// <see cref="IUiWorldPick.Cancel"/> (both mean "finished": <paramref name="onCancelled"/> runs, read
    /// <see cref="IUiWorldPick.PickCount"/>), the timeout (counted from the last pick), leaving the world, a newer pick or
    /// dispose.</para>
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
