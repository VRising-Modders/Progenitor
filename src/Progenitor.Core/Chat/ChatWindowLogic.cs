using System;
using System.Collections.Generic;
using System.Threading;
using Progenitor.Ui;

namespace Progenitor.Chat;

/// <summary>
/// CHAT1: how long the game's chat box stays up after a message, game-free. The live half is
/// <c>ChatWindow</c>.
///
/// <para><b>The game's rule</b> (<c>ProjectM.UI.ClientChatSystem.OnUpdate</c>, client, 2026-10-07): while
/// the chat is open and not focused, once <c>Time.time - _MessageTime</c> passes
/// <see cref="GameFadeDelaySeconds"/> the box fades out over about a second and closes. The 14 is a constant
/// in the game's code, not a setting, so Progenitor keeps the box up by moving <c>_MessageTime</c> forward:
/// never past now, never backward, and only as far as the player's setting asks. The box therefore still
/// fades <see cref="GameFadeDelaySeconds"/> after the time written here, which is why the target subtracts it.</para>
/// </summary>
internal static class ChatVisibility
{
    /// <summary>The game's own delay before the unfocused chat box starts to fade, in seconds.</summary>
    internal const float GameFadeDelaySeconds = 14f;

    /// <summary>Progenitor's default: how long the box stays up after the last message.</summary>
    internal const int DefaultVisibleSeconds = 30;

    /// <summary>The shortest setting: the game's own delay (Progenitor only ever extends it).</summary>
    internal const int MinVisibleSeconds = 14;

    /// <summary>The longest timed setting; Keep chat visible covers "always".</summary>
    internal const int MaxVisibleSeconds = 600;

    internal static int ClampSeconds(int seconds) =>
        seconds < MinVisibleSeconds ? MinVisibleSeconds : seconds > MaxVisibleSeconds ? MaxVisibleSeconds : seconds;

    /// <summary>
    /// The <c>_MessageTime</c> to write this frame, or null to leave the game's value alone.
    /// <paramref name="lastActivity"/> is when the newest message arrived, or now while the pointer rests on
    /// the box and Stay while hovered is on. Every time is the game's <c>Time.time</c> clock.
    /// </summary>
    internal static float? MessageTimeToWrite(float now, float current, float lastActivity, int visibleSeconds, bool keepVisible)
    {
        if (float.IsNaN(now) || float.IsInfinity(now)) return null;
        float target = keepVisible ? now : lastActivity + (ClampSeconds(visibleSeconds) - GameFadeDelaySeconds);
        if (target > now) target = now;
        if (float.IsNaN(current) || target > current) return target;
        return null;
    }
}

/// <summary>
/// CHAT1: the chat click guard, game-free. Clicking the game's chat box used to attack or turn the camera
/// as well: the game's hover context for the chat box (<c>ClientChatSystem.ChatHoveredInputContext</c>) takes
/// only the zoom axis, so the mouse buttons fall through to gameplay. Progenitor's own context
/// (<see cref="UiInputCapture.ChatClickGuardOrder"/>) takes <see cref="BlockedActions"/> while <see cref="Active"/>.
///
/// <para><b>When it is active.</b> While the pointer rests over the visible chat box, so the press frame is
/// already covered, and for as long as a press that started there stays held. A press that started anywhere
/// else is never cut off when the pointer crosses the box (a camera drag or a held attack carries on). With no
/// button held the latch simply follows the pointer.</para>
///
/// <para><b>Which actions.</b> Only gameplay actions a mouse button triggers: the left and right buttons'
/// defaults (attack, camera turn, build place and pick up), plus every eligible action the player has bound to
/// the middle, back or forward button (read from the game's bindings). A keyboard key is never in the set, so
/// abilities on keys keep working while the pointer is over chat. Push-to-talk, the map, menus and inventory
/// actions are never taken.</para>
/// </summary>
internal static class ChatClickGuard
{
    /// <summary>Primary attack (left button), <c>ButtonInputAction.Primary</c>.</summary>
    internal const int Primary = 100;

    /// <summary>Hold to turn the camera (right button), <c>ButtonInputAction.RotateCamera</c>.</summary>
    internal const int RotateCamera = ButtonInputActions.RotateCamera;

    /// <summary>The game's default back-button action, <c>ButtonInputAction.Interrupt</c>.</summary>
    internal const int Interrupt = 2;

    /// <summary>The game's default forward-button action, <c>ButtonInputAction.AutoMove</c>.</summary>
    internal const int AutoMove = 4;

    /// <summary>Build mode, left button: <c>Building_Place</c> and <c>Building_Pickup</c>.</summary>
    internal const int BuildingPlace = 303;

    internal const int BuildingPickup = 308;

    /// <summary>
    /// Actions on the left and right buttons by default. The game's bindings name these buttons only as
    /// display text, so they are always taken; a player who moved one of them to a key loses that key only
    /// while the pointer rests on the chat box.
    /// </summary>
    internal static readonly int[] LeftRightDefaults = { Primary, RotateCamera, BuildingPlace, BuildingPickup };

    /// <summary>The game's defaults for the extra buttons, used while its bindings cannot be read.</summary>
    internal static readonly int[] ExtraButtonDefaults = { Interrupt, AutoMove };

    /// <summary>
    /// Gameplay actions that may be taken when bound to the middle, back or forward button, by the game's
    /// <c>ButtonInputAction</c> name. Not listed, so never taken: push-to-talk, chat, the map, menus,
    /// inventory and crafting, emotes and forms, admin keys and menu navigation.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, int> Eligible = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Interact"] = 1, ["Interrupt"] = 2, ["CliffJump"] = 3, ["AutoMove"] = 4, ["Walk"] = 35,
        ["Primary"] = 100, ["TravelSkill"] = 101, ["WeaponSkill1"] = 102, ["WeaponSkill2"] = 103,
        ["OffensiveSpell"] = 104, ["DefensiveSpell"] = 105, ["UltimateSpell"] = 106, ["Ability4"] = 112,
        ["BlockCombatState"] = 113,
        ["ActionBar1"] = 200, ["ActionBar2"] = 201, ["ActionBar3"] = 202, ["ActionBar4"] = 203,
        ["ActionBar5"] = 204, ["ActionBar6"] = 205, ["ActionBar7"] = 206, ["ActionBar8"] = 207,
        ["Building_Place"] = 303, ["Building_Pickup"] = 308, ["Building_Dismantle"] = 305, ["Building_Repair"] = 306,
        ["Building_RotateLeft"] = 301, ["Building_RotateRight"] = 302,
        ["RotateCamera"] = 500, ["ResetCamera"] = 501, ["CameraQuickTurnLeft"] = 502, ["CameraQuickTurnRight"] = 503,
    };

    private static volatile bool _active;
    private static bool _latch;
    private static int[] _blocked = Build(null);

    /// <summary>Read by the native mask callback once per frame; a field read, never throws.</summary>
    internal static bool Active => _active;


    /// <summary>The live array the mask walks; replaced whole, never mutated, so the callback allocates nothing.</summary>
    internal static int[] BlockedActions() => Volatile.Read(ref _blocked);

    /// <summary>
    /// One frame's decision. <paramref name="overChat"/>: the pointer is inside the visible chat box this
    /// frame; <paramref name="anyButtonHeld"/>: any mouse button is down.
    /// </summary>
    internal static bool Update(bool enabled, bool overChat, bool anyButtonHeld)
    {
        if (!enabled)
        {
            _latch = false;
            _active = false;
            return false;
        }

        if (!anyButtonHeld) _latch = overChat;
        _active = _latch;
        return _active;
    }

    /// <summary>Drop the decision (disabled, world gone, Core unload). The mask then takes nothing.</summary>
    internal static void Reset()
    {
        _latch = false;
        _active = false;
    }

    /// <summary>Replace the blocked set from the game's bindings (null: they could not be read).</summary>
    internal static void Use(IEnumerable<(string Action, UiKey Key)>? mouseBound) =>
        Volatile.Write(ref _blocked, Build(mouseBound));

    /// <summary>
    /// The set for a binding read: <see cref="LeftRightDefaults"/>, plus each eligible action bound to the
    /// middle, back or forward button, or <see cref="ExtraButtonDefaults"/> when nothing could be read.
    /// Sorted and without repeats.
    /// </summary>
    internal static int[] Build(IEnumerable<(string Action, UiKey Key)>? mouseBound)
    {
        var set = new SortedSet<int>(LeftRightDefaults);
        if (mouseBound == null)
        {
            foreach (int id in ExtraButtonDefaults) set.Add(id);
        }
        else
        {
            foreach (var (action, key) in mouseBound)
            {
                if (!KeyChord.IsMouseKey(key)) continue;
                if (action != null && Eligible.TryGetValue(action, out int id)) set.Add(id);
            }
        }

        var result = new int[set.Count];
        set.CopyTo(result);
        return result;
    }
}

/// <summary>
/// When Progenitor's chat view steps aside for another mod that replaces the game's chat window, game-free. The
/// live half is <c>ChatView</c>.
///
/// <para>Two signals, either is enough. <b>Configured:</b> <c>[Chat] ReplacedBy</c> lists plugin GUIDs or names
/// (separated by commas or semicolons, empty by default); a loaded plugin whose GUID or name equals an entry, ignoring
/// case, turns the view off for the session. <b>Observed:</b> when the view is about to attach and the chat window's
/// scroll area no longer shows the game's own line node, another mod already drives the lines, and the view stays off
/// too. Progenitor names no chat mod itself.</para>
/// </summary>
internal static class ChatReplacement
{
    /// <summary>The entries of a <c>[Chat] ReplacedBy</c> value: trimmed, non-empty, at most 20.</summary>
    internal static IReadOnlyList<string> Entries(string? setting)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(setting)) return result;
        foreach (var part in setting!.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string entry = part.Trim();
            if (entry.Length == 0) continue;
            result.Add(entry);
            if (result.Count == 20) break;
        }

        return result;
    }

    /// <summary>
    /// The display name (name, else GUID) of the first loaded plugin listed in <paramref name="setting"/>, or null.
    /// <paramref name="plugins"/> is (GUID, name) per loaded plugin.
    /// </summary>
    internal static string? FirstListed(IEnumerable<KeyValuePair<string, string>> plugins, string? setting)
    {
        var entries = Entries(setting);
        if (entries.Count == 0 || plugins == null) return null;
        foreach (var pair in plugins)
        {
            string id = pair.Key ?? "";
            string name = pair.Value ?? "";
            foreach (var entry in entries)
            {
                if (!string.Equals(id, entry, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, entry, StringComparison.OrdinalIgnoreCase)) continue;
                return name.Length > 0 ? name : id;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether another mod already drives the chat window's lines: the scroll area's content is neither the game's
    /// line node nor Progenitor's own clone. Zero pointers (nothing to compare yet) never count.
    /// </summary>
    internal static bool ContentTakenOver(IntPtr scrollContent, IntPtr gameNode, IntPtr ownClone) =>
        scrollContent != IntPtr.Zero && gameNode != IntPtr.Zero && scrollContent != gameNode && scrollContent != ownClone;
}
