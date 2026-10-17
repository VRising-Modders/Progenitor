using System;

namespace Progenitor.Ui;

/// <summary>
/// Where a world pick (<c>ctx.Hud.PickInWorld</c>) is in its life. A pick starts <see cref="Armed"/>
/// and ends exactly once in one of the other states; it is never re-armed (call
/// <c>PickInWorld</c> again for another pick). A repeating pick (<see cref="UiWorldPickOptions.Repeat"/>)
/// stays <see cref="Armed"/> across its picks and never ends as <see cref="Picked"/>: it ends as
/// <see cref="Cancelled"/> (Escape or <see cref="IUiWorldPick.Cancel"/>: the player finished), <see cref="TimedOut"/>
/// or <see cref="Disposed"/>, with <see cref="IUiWorldPick.PickCount"/> saying how many it delivered.
/// </summary>
public enum UiWorldPickState
{
    /// <summary>Not started (a handle that was never armed, such as the server's no-op handle).</summary>
    Idle = 0,

    /// <summary>Waiting for the local player's next interact on a matching container or station.</summary>
    Armed = 1,

    /// <summary>The player interacted with a matching target; <c>onPicked</c> ran.</summary>
    Picked = 2,

    /// <summary>
    /// Ended without a target: Escape, <see cref="IUiWorldPick.Cancel"/>, a newer pick replacing it,
    /// the world ending, or no world to pick in. <c>onCancelled</c> ran.
    /// </summary>
    Cancelled = 3,

    /// <summary><see cref="UiWorldPickOptions.TimeoutSeconds"/> elapsed; <c>onCancelled</c> ran.</summary>
    TimedOut = 4,

    /// <summary>Disposed (or its plugin unloaded) while armed; neither callback ran.</summary>
    Disposed = 5,
}

/// <summary>What a world pick accepts. Combine with <c>|</c>.</summary>
[Flags]
public enum UiWorldPickKinds
{
    /// <summary>Nothing (a pick that can only end by Escape, cancel or timeout).</summary>
    None = 0,

    /// <summary>Storage: chests, stashes and other containers the game opens with its container panel.</summary>
    Containers = 1,

    /// <summary>Crafting and processing stations: workbenches, furnaces, research desks, the forge, and the like.</summary>
    Stations = 2,

    /// <summary>Other interactables with their own panel: the castle heart, prison cells, servants, traders, the arena station.</summary>
    Other = 4,

    /// <summary>Everything above.</summary>
    All = Containers | Stations | Other,
}

/// <summary>The kind of one picked target.</summary>
public enum UiWorldPickKind
{
    /// <summary>A container (<see cref="UiWorldPickKinds.Containers"/>).</summary>
    Container = 1,

    /// <summary>A station (<see cref="UiWorldPickKinds.Stations"/>).</summary>
    Station = 2,

    /// <summary>Another interactable with a panel (<see cref="UiWorldPickKinds.Other"/>).</summary>
    Other = 4,
}

/// <summary>Options for <c>ctx.Hud.PickInWorld</c>. Every property has a usable default.</summary>
public sealed class UiWorldPickOptions
{
    /// <summary>The prompt shown at the bottom of the screen while the pick is armed.</summary>
    public const string DefaultPrompt = "Pick a container or station";

    /// <summary>P7b: the prompt of a repeating pick (<see cref="Repeat"/>) that kept <see cref="DefaultPrompt"/>.</summary>
    public const string DefaultRepeatPrompt = "Pick containers or stations";

    /// <summary>Which targets end the pick. Interacting with anything else behaves as usual. Default containers and stations.</summary>
    public UiWorldPickKinds Kinds { get; set; } = UiWorldPickKinds.Containers | UiWorldPickKinds.Stations;

    /// <summary>The prompt band's text. Default <see cref="DefaultPrompt"/>.</summary>
    public string Prompt { get; set; } = DefaultPrompt;

    /// <summary>
    /// Seconds until the pick ends as <see cref="UiWorldPickState.TimedOut"/>; 0 or less (the default) waits forever. A
    /// repeating pick counts them from its last pick (an idle timeout).
    /// </summary>
    public double TimeoutSeconds { get; set; }

    /// <summary>
    /// P7b, experimental: keep picking. Each matching interact runs <c>onPicked</c> and the pick stays
    /// <see cref="UiWorldPickState.Armed"/> (the same handle) until Escape, <see cref="IUiWorldPick.Cancel"/>, the timeout,
    /// leaving the world, a newer pick or dispose. Escape and <c>Cancel()</c> mean "finished": they end it as
    /// <see cref="UiWorldPickState.Cancelled"/> and run <c>onCancelled</c>; read <see cref="IUiWorldPick.PickCount"/> there.
    /// The band counts the picks. The same target interacted with twice in a row within ten seconds is reported once (its
    /// panel still stays closed). False (the default): the first matching interact ends the pick.
    /// </summary>
    public bool Repeat { get; set; }

    /// <summary>
    /// True (the default): the target's panel does not open and the game is told the interaction ended.
    /// False: the panel opens as usual and the pick only reports the target.
    /// </summary>
    public bool SuppressPanel { get; set; } = true;

    /// <summary>
    /// True: arming the pick first closes the game window the player has open, as Escape would: a container's or a
    /// station's window (with the character inventory beside it) or the character inventory itself. The game is told the
    /// interaction ended, as when the player closes it. Nothing is reopened when the pick ends: the window stays closed.
    /// Any other game menu (the map, the full-screen menu) is left alone, and so is a pick that could not arm (no world).
    /// Works with <see cref="Repeat"/>: the window closes once, when the pick arms. False (the default): nothing is closed.
    /// </summary>
    public bool CloseGameWindows { get; set; }

    internal UiWorldPickOptions Copy() => new()
    {
        CloseGameWindows = CloseGameWindows,
        Kinds = Kinds,
        Prompt = string.IsNullOrWhiteSpace(Prompt) || (Repeat && Prompt.Trim() == DefaultPrompt)
            ? (Repeat ? DefaultRepeatPrompt : DefaultPrompt)
            : Prompt.Trim(),
        TimeoutSeconds = double.IsNaN(TimeoutSeconds) ? 0 : TimeoutSeconds,
        SuppressPanel = SuppressPanel,
        Repeat = Repeat,
    };
}

/// <summary>
/// The target a world pick captured. The entity is valid only in the world it was picked in:
/// check <see cref="IsCurrent"/> (or compare <see cref="WorldEpoch"/>) before using it later, and
/// never store the raw index and version as a lasting identity.
/// </summary>
public readonly struct UiWorldPickResult
{
    internal UiWorldPickResult(int entityIndex, int entityVersion, UiWorldPickKind kind, int menuType, string menu, int worldEpoch, bool suppressed,
        int networkId0 = 0, int networkId1 = 0, int networkId2 = 0)
    {
        NetworkId0 = networkId0;
        NetworkId1 = networkId1;
        NetworkId2 = networkId2;
        EntityIndex = entityIndex;
        EntityVersion = entityVersion;
        Kind = kind;
        MenuType = menuType;
        Menu = menu ?? "";
        WorldEpoch = worldEpoch;
        Suppressed = suppressed;
    }

    /// <summary>The picked entity's index in the client world.</summary>
    public int EntityIndex { get; }

    /// <summary>The picked entity's version in the client world.</summary>
    public int EntityVersion { get; }

    /// <summary>Container, station or other.</summary>
    public UiWorldPickKind Kind { get; }

    /// <summary>The game's menu type number for the panel this interact would have opened.</summary>
    public int MenuType { get; }

    /// <summary>The game's name for that panel (<c>ContainerMenu</c>, <c>WorkstationMenu</c>, ...).</summary>
    public string Menu { get; }

    /// <summary>The world epoch the pick happened in (<c>Progenitor.WorldState.WorldEpoch.Current</c> at the time).</summary>
    public int WorldEpoch { get; }

    /// <summary>True when the panel was kept closed (<see cref="UiWorldPickOptions.SuppressPanel"/>).</summary>
    public bool Suppressed { get; }

    /// <summary>
    /// The picked entity's <c>NetworkId</c>, first word: <c>Normal_Index</c> for a normal id. Same
    /// words and rules as <see cref="VanillaPanelTarget"/> (read only when the interop's
    /// <c>NetworkId</c> is 12 bytes; all three 0 when unreadable). Send <see cref="NetworkIdIndex"/>,
    /// <see cref="NetworkIdGeneration"/> and <see cref="NetworkId2"/> to the server and resolve them
    /// with <c>ctx.Inventory.TryResolveNetworkId</c>. Session-local: never persist them.
    /// </summary>
    public int NetworkId0 { get; }

    /// <summary>The second word: <c>Normal_Generation</c> (0..255, padding cleared) for a normal id.</summary>
    public int NetworkId1 { get; }

    /// <summary>The id's type (<c>NetworkIdType</c>: 0 none, 1 normal, 2 static map object).</summary>
    public int NetworkId2 { get; }

    /// <summary>The <c>NetworkId</c>'s <c>Normal_Index</c>.</summary>
    public int NetworkIdIndex => NetworkId0;

    /// <summary>The <c>NetworkId</c>'s <c>Normal_Generation</c>.</summary>
    public int NetworkIdGeneration => NetworkId1 & 0xFF;

    /// <summary>True when a <c>NetworkId</c> was read for the picked entity.</summary>
    public bool HasNetworkId => NetworkId0 != 0 || NetworkId1 != 0 || NetworkId2 != 0;

    /// <summary>True when <paramref name="currentEpoch"/> is the epoch this pick happened in.</summary>
    public bool IsCurrentIn(int currentEpoch) => currentEpoch == WorldEpoch;

#if !PROGENITOR_TESTS
    /// <summary>The picked entity in the client world.</summary>
    public Unity.Entities.Entity Entity => new() { Index = EntityIndex, Version = EntityVersion };

    /// <summary>True while the world the pick happened in is still the current one.</summary>
    public bool IsCurrent => IsCurrentIn(global::Progenitor.WorldState.WorldEpoch.Current);
#endif

    /// <inheritdoc />
    public override string ToString() =>
        Kind + " " + EntityIndex + ":" + EntityVersion
        + (HasNetworkId ? " net=" + NetworkIdIndex + ":" + NetworkIdGeneration : "")
        + " via " + Menu + " (epoch " + WorldEpoch + (Suppressed ? ", panel kept closed" : "") + ")";
}

/// <summary>
/// An armed (or finished) world pick. Dispose to end an armed pick silently; <see cref="Cancel"/>
/// ends it and runs <c>onCancelled</c>.
/// </summary>
public interface IUiWorldPick : IDisposable
{
    /// <summary>True while the pick waits for an interact.</summary>
    bool Active { get; }

    /// <summary>Where the pick is in its life.</summary>
    UiWorldPickState State { get; }

    /// <summary>End an armed pick as <see cref="UiWorldPickState.Cancelled"/> and run <c>onCancelled</c>. No-op once ended.</summary>
    void Cancel();

    /// <summary>P7b: how many targets this pick delivered to <c>onPicked</c> (0 or 1 for a one-shot pick).</summary>
    int PickCount { get; }
}
