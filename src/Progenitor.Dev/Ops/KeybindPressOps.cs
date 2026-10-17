using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Ui;

namespace Progenitor.Dev.Ops;

/// <summary>
/// <c>keybind_press</c>: press a registered keybind from the bridge, focus-free. The dispatch is
/// Core's <c>Keybinds.SimulatePress</c>, the same path a real key-down takes — chat and text-field
/// suppression, panel capture, the active-bind rules and winner enforcement all apply — so a bind
/// that would not fire for the player does not fire here either, and the reply says why.
///
/// <para>Arguments: <c>{bind, plugin?}</c> presses one registered bind by id (and owning plugin
/// GUID, when ids collide); <c>{chord}</c> presses a chord such as <c>"F10 + Ctrl"</c> and lets
/// the registry decide which binds it reaches (PAD1: a controller chord such as <c>"GamepadNorth + GamepadDpadDown"</c>
/// reaches the binds whose controller chord it is); <c>{bind, gamepad: true}</c> presses that bind's controller chord, with
/// <c>Keybinds.PressedWithGamepad</c> true in the handler. Exactly one form. Client only, main thread.</para>
///
/// <para>Reply: <c>{request, fired, dispatches:[{plugin, bind, chord, fired, reason}]}</c>.
/// <c>fired</c> counts handlers that ran; <c>reason</c> is empty for a fired bind and otherwise
/// Core's sentence (for example <c>suppressed: chat focused</c> or <c>unknown bind</c>).</para>
/// </summary>
internal static class KeybindPressOps
{
    internal const string ClientOnlyMessage = "keybind_press runs on the client only: the server has no key input.";

    internal const string UsageMessage =
        "keybind_press needs either args.bind (optionally with args.plugin, the owning plugin GUID) "
        + "or args.chord (for example \"F10\" or \"F10 + Ctrl\"), not both.";

    /// <summary>A parsed request: a bind by id, or a chord.</summary>
    internal sealed class Request
    {
        internal Request(string? plugin, string? bind, KeyChord? chord, int? hold = null, bool gamepad = false)
        {
            Gamepad = gamepad;
            Hold = hold;
            Plugin = plugin;
            Bind = bind;
            Chord = chord;
        }

        internal string? Plugin { get; }
        internal string? Bind { get; }
        internal KeyChord? Chord { get; }

        /// <summary>Review 2026-09-29: press, hold this many frames, release (a bind only); null for a press.</summary>
        internal int? Hold { get; }

        /// <summary>PAD1: press the bind's controller chord (<c>args.gamepad: true</c>), as a controller press reaches it.</summary>
        internal bool Gamepad { get; }

        internal object Describe() => Chord is KeyChord chord
            ? new Dictionary<string, object?> { ["chord"] = chord.ToString() }
            : new Dictionary<string, object?> { ["plugin"] = Plugin, ["bind"] = Bind, ["hold"] = Hold, ["gamepad"] = Gamepad };
    }

    /// <summary>Parse and validate the arguments. Throws <see cref="ArgumentException"/> with the usage line.</summary>
    internal static Request Parse(JsonElement args)
    {
        string? plugin = Text(args, "plugin");
        string? bind = Text(args, "bind");
        string? chordText = Text(args, "chord");

        if (chordText != null)
        {
            if (bind != null || plugin != null) throw new ArgumentException(UsageMessage);
            if (!KeyChord.TryParse(chordText, out var chord))
                throw new ArgumentException("keybind_press: args.chord '" + chordText + "' is not a key chord. " + UsageMessage);
            if (!chord.IsBound)
                throw new ArgumentException("keybind_press: args.chord '" + chordText + "' is unbound (None); name a key.");
            return new Request(null, null, chord);
        }

        if (bind == null) throw new ArgumentException(UsageMessage);
        int? hold = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("hold", out var h))
        {
            if (h.ValueKind != JsonValueKind.Number || !h.TryGetInt32(out int frames) || frames < 0 || frames > 600)
                throw new ArgumentException("keybind_press: args.hold is the number of frames to hold (0-600).");
            hold = frames;
        }

        bool gamepad = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("gamepad", out var g) && g.ValueKind == JsonValueKind.True;
        if (gamepad && hold != null)
            throw new ArgumentException("keybind_press: args.gamepad presses the controller chord once; args.hold is for the keyboard chord.");
        return new Request(plugin, bind, null, hold, gamepad);
    }

    /// <summary>A trimmed string property, or null when absent, not a string, or blank.</summary>
    private static string? Text(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        string? value = el.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
    }

    internal static object Run(JsonElement args)
    {
        var request = Parse(args);
#if PROGENITOR_TESTS
        throw new InvalidOperationException(ClientOnlyMessage);
#else
        if (Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException(ClientOnlyMessage);

        var dispatches = request.Chord is KeyChord chord
            ? Keybinds.SimulatePress(chord)
            : request.Gamepad
                ? Keybinds.SimulateGamepadPress(request.Plugin, request.Bind!)
            : request.Hold is int frames
                ? Keybinds.SimulateHold(request.Plugin, request.Bind!, frames)
                : Keybinds.SimulatePress(request.Plugin, request.Bind!);

        int fired = 0;
        var rows = new List<object>(dispatches.Count);
        for (int i = 0; i < dispatches.Count; i++)
        {
            var d = dispatches[i];
            if (d.Fired) fired++;
            rows.Add(new Dictionary<string, object?>
            {
                ["plugin"] = d.PluginId,
                ["bind"] = d.Id,
                ["chord"] = d.Chord,
                ["fired"] = d.Fired,
                ["reason"] = d.Reason ?? "",
            });
        }

        return new Dictionary<string, object?>
        {
            ["request"] = request.Describe(),
            ["fired"] = fired,
            ["dispatches"] = rows,
        };
#endif
    }
}
