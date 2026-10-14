using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// The game-free half of the <c>key</c> op (<see cref="KeyInjectOps"/>): argument parsing, the held-key
/// ledger that guarantees every press a release, and the bytes of the Input System events the op queues.
/// Tested in <c>BridgeKeyInjectTests</c>.
/// </summary>
internal static class KeyInjectPlan
{
    internal enum Action
    {
        Status = 0,
        Tap = 1,
        Down = 2,
        Up = 3,
        Hold = 4,
        Type = 5,
        Release = 6,
    }

    internal const string UsageMessage =
        "key needs args.action: tap, down, up or hold with args.keys (for example \"M\" or \"Ctrl+R\"), "
        + "type with args.text, release (every key this op holds), or status.";

    internal const int TapDefaultMs = 50;
    internal const int HoldDefaultMs = 200;
    internal const int MaxHoldMs = 10_000;

    /// <summary>A <c>down</c> is released after this many milliseconds unless <c>up</c> or <c>release</c> comes first.</summary>
    internal const int DownDefaultMs = 10_000;
    internal const int MaxDownMs = 60_000;

    internal const int MaxTextLength = 256;

    /// <summary>A parsed request.</summary>
    internal sealed class Request
    {
        internal Action Action { get; init; }

        /// <summary>The keys in press order: modifiers first, then the main key. Empty for type, release, status.</summary>
        internal IReadOnlyList<UiKey> Keys { get; init; } = Array.Empty<UiKey>();

        /// <summary>The press length (tap, hold) or the automatic release (down), in milliseconds.</summary>
        internal int Ms { get; init; }

        internal string Text { get; init; } = "";

        /// <summary>Opt-in: let the keyboard take events while the game window is in the background.</summary>
        internal bool Background { get; init; }

        internal Dictionary<string, object?> Describe() => new(StringComparer.Ordinal)
        {
            ["action"] = Action.ToString().ToLowerInvariant(),
            ["keys"] = Keys.Count == 0 ? null : Join(Keys),
            ["ms"] = Action is Action.Tap or Action.Hold or Action.Down ? Ms : null,
            ["textLength"] = Action == Action.Type ? Text.Length : null,
            ["background"] = Background,
        };
    }

    internal static string Join(IReadOnlyList<UiKey> keys)
    {
        var names = new string[keys.Count];
        for (int i = 0; i < keys.Count; i++) names[i] = keys[i].ToString();
        return string.Join("+", names);
    }

    /// <summary>Parse and validate. Throws <see cref="ArgumentException"/> naming the op and the usage.</summary>
    internal static Request Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException(UsageMessage);
        string? actionText = Text(args, "action");
        if (actionText == null) throw new ArgumentException(UsageMessage);
        Action action = actionText.ToLowerInvariant() switch
        {
            "status" => Action.Status,
            "tap" => Action.Tap,
            "down" => Action.Down,
            "up" => Action.Up,
            "hold" => Action.Hold,
            "type" => Action.Type,
            "release" => Action.Release,
            _ => throw new ArgumentException("key: args.action '" + actionText + "' is not known. " + UsageMessage),
        };

        bool background = false;
        if (args.TryGetProperty("background", out var bg))
        {
            if (bg.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("key: args.background is true or false.");
            background = bg.GetBoolean();
        }

        string? keysText = Text(args, "keys");
        string? text = args.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        bool hasMs = args.TryGetProperty("ms", out var msEl);

        switch (action)
        {
            case Action.Status:
            case Action.Release:
                if (keysText != null || text != null || hasMs)
                    throw new ArgumentException("key: " + actionText + " takes no keys, text or ms. " + UsageMessage);
                return new Request { Action = action, Background = background };
            case Action.Type:
                if (keysText != null || hasMs) throw new ArgumentException("key: type takes args.text only. " + UsageMessage);
                if (string.IsNullOrEmpty(text)) throw new ArgumentException("key: type needs a non-empty args.text.");
                if (text!.Length > MaxTextLength) throw new ArgumentException("key: args.text is at most " + MaxTextLength + " characters.");
                return new Request { Action = action, Text = text, Background = background };
        }

        if (text != null) throw new ArgumentException("key: args.text is only for type. " + UsageMessage);
        if (keysText == null) throw new ArgumentException("key: " + actionText + " needs args.keys. " + UsageMessage);
        if (!TryParseKeys(keysText, out var keys, out string why))
            throw new ArgumentException("key: args.keys '" + keysText + "': " + why);

        int ms = 0;
        if (action == Action.Up)
        {
            if (hasMs) throw new ArgumentException("key: up takes no ms.");
        }
        else
        {
            int def = action switch { Action.Tap => TapDefaultMs, Action.Hold => HoldDefaultMs, _ => DownDefaultMs };
            int max = action == Action.Down ? MaxDownMs : MaxHoldMs;
            ms = def;
            if (hasMs)
            {
                if (msEl.ValueKind != JsonValueKind.Number || !msEl.TryGetInt32(out ms) || ms < 1 || ms > max)
                    throw new ArgumentException("key: args.ms for " + actionText + " is 1-" + max + " milliseconds.");
            }
        }

        return new Request { Action = action, Keys = keys, Ms = ms, Background = background };
    }

    /// <summary>
    /// <c>"M"</c>, <c>"Ctrl+R"</c>, <c>"Shift + F12"</c>, <c>"LeftShift"</c>: tokens split on <c>+</c>.
    /// Side-agnostic modifiers press the left key (<c>Ctrl</c> is <c>LeftCtrl</c>); <c>RightCtrl</c>,
    /// <c>RightShift</c>, <c>RightAlt</c>/<c>AltGr</c> name the right one. At most one other key, read with
    /// <see cref="KeyChord.TryParse"/>'s names. Mouse buttons are refused (this op drives the keyboard).
    /// </summary>
    internal static bool TryParseKeys(string text, out IReadOnlyList<UiKey> keys, out string why)
    {
        keys = Array.Empty<UiKey>();
        why = "";
        var modifiers = new List<UiKey>();
        UiKey main = UiKey.None;
        foreach (string raw in text.Split('+'))
        {
            string token = raw.Trim();
            if (token.Length == 0)
            {
                why = KeyChord.EmptyPartReason;
                return false;
            }

            UiKey modifier = ModifierKey(token);
            if (modifier != UiKey.None)
            {
                if (!modifiers.Contains(modifier)) modifiers.Add(modifier);
                continue;
            }

            if (!KeyChord.TryParse(token, out var chord) || !chord.IsBound || chord.Modifiers != KeyModifiers.None)
            {
                why = "'" + token + "' is not a key name";
                return false;
            }
            if (KeyChord.IsMouseKey(chord.Key))
            {
                why = "'" + token + "' is a mouse button; this op presses keyboard keys";
                return false;
            }
            if (main != UiKey.None)
            {
                why = "more than one non-modifier key ('" + main + "' and '" + chord.Key + "')";
                return false;
            }
            main = chord.Key;
        }

        if (main == UiKey.None && modifiers.Count == 0)
        {
            why = "no key named";
            return false;
        }

        if (main != UiKey.None) modifiers.Add(main);
        keys = modifiers;
        return true;
    }

    private static UiKey ModifierKey(string token) => token.ToLowerInvariant() switch
    {
        "ctrl" or "control" or "leftctrl" or "leftcontrol" => UiKey.LeftCtrl,
        "rightctrl" or "rightcontrol" => UiKey.RightCtrl,
        "shift" or "leftshift" => UiKey.LeftShift,
        "rightshift" => UiKey.RightShift,
        "alt" or "leftalt" => UiKey.LeftAlt,
        "rightalt" or "altgr" => UiKey.RightAlt,
        _ => UiKey.None,
    };

    private static string? Text(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return null;
        string? value = el.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
    }
}

/// <summary>
/// Which keys the <c>key</c> op holds, and until when. Every key it presses is released by one of
/// three paths: an <c>up</c>/<c>release</c>, its deadline (<see cref="Expired"/>; a <c>down</c> has one too),
/// or <see cref="ReleaseAll"/> on unload and abort. Releases list the main key before its modifiers, the
/// order a player lets go.
/// </summary>
internal sealed class KeyHoldLedger
{
    private readonly List<(UiKey Key, long ReleaseAtMs)> _held = new();

    internal int Count => _held.Count;

    internal bool IsHeld(UiKey key) => IndexOf(key) >= 0;

    /// <summary>The held keys in press order.</summary>
    internal IReadOnlyList<UiKey> Held
    {
        get
        {
            var keys = new UiKey[_held.Count];
            for (int i = 0; i < keys.Length; i++) keys[i] = _held[i].Key;
            return keys;
        }
    }

    /// <summary>The earliest deadline, or null with nothing held.</summary>
    internal long? NextDeadlineMs
    {
        get
        {
            long? next = null;
            foreach (var h in _held) if (next == null || h.ReleaseAtMs < next) next = h.ReleaseAtMs;
            return next;
        }
    }

    /// <summary>Hold <paramref name="keys"/> until <paramref name="releaseAtMs"/>. Returns the keys that went down now; a key already held keeps the later deadline.</summary>
    internal IReadOnlyList<UiKey> Press(IReadOnlyList<UiKey> keys, long releaseAtMs)
    {
        var down = new List<UiKey>();
        foreach (var key in keys)
        {
            int i = IndexOf(key);
            if (i >= 0)
            {
                if (releaseAtMs > _held[i].ReleaseAtMs) _held[i] = (key, releaseAtMs);
                continue;
            }
            _held.Add((key, releaseAtMs));
            down.Add(key);
        }
        return down;
    }

    /// <summary>Release <paramref name="keys"/> that are held; returns them main key first.</summary>
    internal IReadOnlyList<UiKey> Release(IReadOnlyList<UiKey> keys)
    {
        var up = new List<UiKey>();
        foreach (var key in keys)
        {
            int i = IndexOf(key);
            if (i < 0) continue;
            _held.RemoveAt(i);
            up.Add(key);
        }
        return MainKeysFirst(up);
    }

    /// <summary>Release every key whose deadline is at or before <paramref name="nowMs"/>.</summary>
    internal IReadOnlyList<UiKey> Expired(long nowMs)
    {
        var due = new List<UiKey>();
        foreach (var h in _held) if (h.ReleaseAtMs <= nowMs) due.Add(h.Key);
        return Release(due);
    }

    /// <summary>Release everything (unload, abort).</summary>
    internal IReadOnlyList<UiKey> ReleaseAll() => Release(Held);

    private int IndexOf(UiKey key)
    {
        for (int i = 0; i < _held.Count; i++) if (_held[i].Key == key) return i;
        return -1;
    }

    private static IReadOnlyList<UiKey> MainKeysFirst(List<UiKey> keys)
    {
        var ordered = new List<UiKey>(keys.Count);
        for (int i = keys.Count - 1; i >= 0; i--) if (!KeyChord.IsModifierKey(keys[i])) ordered.Add(keys[i]);
        for (int i = keys.Count - 1; i >= 0; i--) if (KeyChord.IsModifierKey(keys[i])) ordered.Add(keys[i]);
        return ordered;
    }
}

/// <summary>
/// The bytes of the two Input System events the <c>key</c> op queues with <c>InputSystem.QueueEvent(InputEventPtr)</c>.
/// Layouts from the client metadata (Input System 1.7.0): <c>NativeInputEvent</c> (20 bytes:
/// type FourCC 0x0, ushort sizeInBytes 0x4, ushort deviceId 0x6, double time 0x8, int eventId 0x10);
/// <c>StateEvent</c> (stateFormat FourCC 0x14, state bytes from 0x18; <c>kStateDataSizeToSubtract</c> 1,
/// so the size is 0x18 + state); <c>KeyboardState</c> (110 bits in 14 bytes, bit n = <c>Key</c> value n);
/// <c>TextEvent</c> (int character 0x14, 24 bytes). FourCCs are stored as the int whose bytes read
/// big-end first: <c>STAT</c> = 1398030676 and <c>TEXT</c> = 1413830740 match the metadata's <c>Type</c> constants.
/// </summary>
internal static class KeyEventBytes
{
    internal const int StateType = 0x53544154;   // 'STAT'
    internal const int TextType = 0x54455854;    // 'TEXT'
    internal const int KeyboardFormat = 0x4B455953; // 'KEYS'

    internal const int KeyboardStateBytes = 14;
    internal const int KeyboardStateBits = 110;
    internal const int StateHeaderBytes = 0x18;
    internal const int KeyboardEventBytes = StateHeaderBytes + KeyboardStateBytes; // 38

    /// <summary>The buffer a keyboard state event is built in (rounded up to the Input System's 4-byte alignment).</summary>
    internal const int KeyboardBufferBytes = (KeyboardEventBytes + 3) & ~3; // 40

    internal const int TextEventBytes = 24;

    /// <summary>A <c>KeyboardState</c> with <paramref name="bits"/> (the <c>Key</c> values held) set.</summary>
    internal static byte[] KeyboardState(IEnumerable<int> bits)
    {
        var state = new byte[KeyboardStateBytes];
        foreach (int bit in bits)
        {
            if (bit <= 0 || bit >= KeyboardStateBits) throw new ArgumentOutOfRangeException(nameof(bits), bit, "not a keyboard key bit");
            state[bit >> 3] |= (byte)(1 << (bit & 7));
        }
        return state;
    }

    /// <summary>A whole keyboard state event for <paramref name="deviceId"/>.</summary>
    internal static byte[] KeyboardStateEvent(int deviceId, double time, byte[] state)
    {
        if (state.Length != KeyboardStateBytes) throw new ArgumentException("a keyboard state is " + KeyboardStateBytes + " bytes", nameof(state));
        var buffer = new byte[KeyboardBufferBytes];
        WriteHeader(buffer, StateType, KeyboardEventBytes, deviceId, time);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0x14), KeyboardFormat);
        Array.Copy(state, 0, buffer, StateHeaderBytes, KeyboardStateBytes);
        return buffer;
    }

    /// <summary>One typed character for <paramref name="deviceId"/> (what <c>Keyboard.onTextInput</c> reports).</summary>
    internal static byte[] TextEvent(int deviceId, double time, int character)
    {
        var buffer = new byte[TextEventBytes];
        WriteHeader(buffer, TextType, TextEventBytes, deviceId, time);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0x14), character);
        return buffer;
    }

    private static void WriteHeader(byte[] buffer, int type, int size, int deviceId, double time)
    {
        if (deviceId < 0 || deviceId > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(deviceId));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0x0), type);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0x4), (ushort)size);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0x6), (ushort)deviceId);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0x8), BitConverter.DoubleToInt64Bits(time));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0x10), 0);
    }
}
