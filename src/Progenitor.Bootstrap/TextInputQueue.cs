using System;
using System.Globalization;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Progenitor.Bootstrap;

/// <summary>
/// TEXTINPUT1. The OS's typed characters for Core's text fields, from the Input System's
/// <c>Keyboard.onTextInput</c>. Characters arrive already translated by the player's keyboard
/// layout, Shift/AltGr state and dead keys, which a physical-key map cannot reproduce.
///
/// <para>
/// <b>Why Bootstrap.</b> This build exposes the event only as
/// <c>Keyboard.add_onTextInput(Il2CppSystem.Action&lt;char&gt;)</c> (the IL viewer,
/// <c>Unity.InputSystem.dll</c>). Subscribing means converting a managed delegate into an IL2CPP
/// delegate, and a converted delegate over a collectible Core method pins that Core generation
/// for the life of the process. So the converted delegate targets <see cref="OnChar"/> here,
/// subscribes once per keyboard device, and only queues characters; Core drains the queue
/// through <c>BootstrapLink</c> each frame. No Core delegate is ever handed to native code.
/// </para>
///
/// <para>
/// <b>Capability, not protocol.</b> Core reads <see cref="Capability"/> by name; a Bootstrap
/// that predates this type reports <c>0</c> and Core keeps its physical-key map. Deploying this
/// file is a Bootstrap change: the client must be restarted for it to exist.
/// </para>
/// </summary>
internal static class TextInputQueue
{
    /// <summary>Read by Core through reflection. <c>1</c>: <see cref="Drain"/> with a frame floor.</summary>
    public const int Capability = 1;

    private const int RingCapacity = 512;

    private static readonly object Gate = new();
    private static readonly TextInputRing Ring = new(RingCapacity);
    private static Keyboard? _keyboard;
    private static int _deviceId = int.MinValue;
    private static Il2CppSystem.Action<char>? _native;
    private static long _received;
    private static int _lastFrame;
    private static string _state = "not-installed";

    /// <summary>True while <see cref="OnChar"/> is subscribed to the current keyboard.</summary>
    public static bool Subscribed => Volatile.Read(ref _keyboard) != null;

    /// <summary>Characters received since the process started (all, focused field or not).</summary>
    public static long Received => Interlocked.Read(ref _received);

    /// <summary><c>subscribed</c>, <c>no-keyboard</c>, <c>not-installed</c> or <c>failed:&lt;reason&gt;</c>.</summary>
    public static string State => Volatile.Read(ref _state);

    /// <summary>
    /// Main thread, once per frame (from <c>UiInputContext.Update</c>). Subscribes to the current
    /// keyboard, and moves the subscription when the device is replaced. A failed subscribe is
    /// logged once per device and not retried every frame.
    /// </summary>
    internal static void Pump()
    {
        Keyboard? kb;
        try { kb = Keyboard.current; }
        catch { return; }
        if (kb == null)
        {
            if (_keyboard == null) Volatile.Write(ref _state, "no-keyboard");
            return;
        }

        int id;
        try { id = kb.deviceId; }
        catch { return; }
        if (id == _deviceId) return;
        _deviceId = id;

        var old = _keyboard;
        Volatile.Write(ref _keyboard, null);
        if (old != null && _native != null)
        {
            try { old.remove_onTextInput(_native); }
            catch { /* the old device is gone with its list */ }
        }

        try
        {
            // Implicit conversion is ConvertDelegate over a Bootstrap method: process-lifetime,
            // never a Core method (that would pin the collectible generation).
            if (_native == null)
            {
                Il2CppSystem.Action<char> converted = new Action<char>(OnChar);
                _native = converted;
            }
            kb.add_onTextInput(_native);
            Volatile.Write(ref _keyboard, kb);
            Volatile.Write(ref _state, "subscribed");
            ProcessHost.Log?.LogInfo("TextInputQueue: subscribed to Keyboard.onTextInput (device "
                + id.ToString(CultureInfo.InvariantCulture) + ").");
        }
        catch (Exception e)
        {
            Volatile.Write(ref _state, "failed:" + e.GetType().Name);
            ProcessHost.Log?.LogWarning("TextInputQueue: subscribe failed; Core keeps its physical-key map: " + e.Message);
        }
    }

    private static void OnChar(char c)
    {
        // Runs inside the Input System's native event dispatch: never throw from here.
        try
        {
            int frame;
            try { frame = Time.frameCount; _lastFrame = frame; }
            catch { frame = _lastFrame; }
            lock (Gate) Ring.Push(c, frame);
            Interlocked.Increment(ref _received);
        }
        catch { /* a dropped character is better than a throw into native */ }
    }

    /// <summary>
    /// Core's drain. Copies the characters that arrived on frame <paramref name="minFrame"/> or
    /// later into <paramref name="buffer"/> and discards older ones. Returns the count.
    /// </summary>
    public static int Drain(char[] buffer, int minFrame)
    {
        try
        {
            lock (Gate) return Ring.Drain(buffer, minFrame);
        }
        catch { return 0; }
    }

    public static string DescribePins() =>
        "pin-diag TextInputQueue capability=" + Capability.ToString(CultureInfo.InvariantCulture)
        + " state=" + State
        + " received=" + Received.ToString(CultureInfo.InvariantCulture);
}
