using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using ProjectM.Transmog;
using Progenitor.Ecs;
using Progenitor.Ui;
using Unity.Entities;
using UnityEngine;

namespace Progenitor.Dye;

/// <summary>
/// Core's Dye all, the client half (all of it): finds the equipment panel's color swatches, watches each one
/// for a press through <see cref="Ui.Ui.WatchPointer"/>, drives <see cref="DyeHoldMachine"/> and the
/// <see cref="ChargeRing"/> from <c>Plugin.Frame</c>, and on a completed hold dyes every piece that shows
/// through <see cref="DyeSender"/>. Created and stopped by <see cref="DyeAllFeature"/>.
///
/// <para><b>Finding the swatches.</b> <c>TransmogContainer</c> holds the color
/// grid; its <c>TransmogColorGridSelectionEntry</c> children are a fixed pool of 32 cells
/// (<c>FillColorSlotsWithEmpty(…, 0x20)</c> in <c>OnUpdateFromParent</c>): the X
/// hide/show cell, one cell per color option, then empty cells. The cells are refreshed in place
/// when the selected slot changes, so the data is read at press time, never cached. There is more than
/// one container: the character menu and the interaction (chest, station) menu each build their own
/// inventory sub-menu with its own picker, and the second exists only once such a menu was opened. Every
/// container is kept; a scene search (including inactive objects) runs whenever none of the known ones is
/// active (<see cref="DyePickerSearch"/>, at most every two seconds), and the cells of every active one
/// are re-listed four times a second, so a rebuilt pool is watched again. Keeping only the first
/// container found was the 2026-10-08 regression: it was the interaction menu's, which stays inactive
/// while Character &gt; Equipment is open, so no swatch was ever watched. Both run only while a HUD menu is open (<see cref="DyeScanGate"/>): never on the title
/// screen, while loading or in game with no menu open. With the feature off, every watch is released and
/// nothing runs but the quarter-second check of the setting.</para>
///
/// <para><b>The key tip.</b> <see cref="HintLabel"/> adds "Hold [left mouse] Set All", copied from the game's own
/// "[left mouse] Set Color" entry, to the inventory menu's key-hint row while the palette shows a color and the
/// feature is on (<see cref="DyeHint"/> holds the rules). It is re-checked with each cell listing and destroyed in
/// <see cref="Stop"/>.</para>
///
/// <para><b>Standing down.</b> While the former Dye all mod (<c>progenitor.dyeall</c>) still runs in this
/// process (<see cref="DyeAllLegacy.OldModRunning"/>, checked with each cell listing), this copy watches no
/// swatch, shows no ring or line and sends nothing; the old mod does the work. The change is logged both
/// ways, and this copy picks up again within a quarter second of the old mod unloading or being turned off.</para>
/// </summary>
internal sealed class DyeAllClient
{
    private const double ScanSeconds = 0.25;
    private const double FindSeconds = 2.0;

    private readonly ManualLogSource _log;
    private readonly object _owner;
    private readonly DyeAllEntries _entries;
    private readonly Func<bool> _oldModRunning;
    private readonly DyeHoldMachine _machine = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<IntPtr, Swatch> _swatches = new();
    private readonly ChargeRing _ring;
    private readonly HintLabel _hint;
    private readonly List<TransmogContainer> _containers = new();
    private TransmogContainer? _active;
    private int _cellsListed;
    private int _finds;
    private DyeScanStep _lastStep = DyeScanStep.Pause;
    private int _lastMenu = DyeScanGate.MenuNone;
    private bool _lastHasWorld;
    private string _lastHold = "none";
    private DateTime _lastHoldUtc;
    private double _nextScan;
    private double _nextFind;
    private int _nextSwatchId = 1;
    private Swatch? _held;
    private byte _heldIndex;
    private Entity _heldItem;
    private double _pulseStart = -1;
    private bool _stopped;
    private bool _hasColorSwatch;
    private bool _standingDown;
    private bool _frameHooked;
    private bool _released;
    private bool _menuUnknownLogged;
    private int _frameErrors;

    /// <summary>Frames in a row that may throw before the feature stops for this Core generation.</summary>
    private const int MaxFrameErrors = 30;

    internal DyeAllClient(ManualLogSource log, object owner, DyeAllEntries entries, Func<bool> oldModRunning)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _entries = entries ?? throw new ArgumentNullException(nameof(entries));
        _oldModRunning = oldModRunning ?? throw new ArgumentNullException(nameof(oldModRunning));
        _ring = new ChargeRing(log);
        _hint = new HintLabel(log);
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    internal void Attach()
    {
        Progenitor.Plugin.Frame += OnFrame;
        _frameHooked = true;
        _log.LogInfo("Dye all: watching the equipment color picker (hold " + _entries.HoldTime.ToString("0.0")
                     + " s, " + (_entries.Enabled.Value ? "on" : "off") + ").");
    }

    /// <summary>Release everything: the frame hook, the swatch watches, the ring and the line. Idempotent.</summary>
    internal void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        if (_frameHooked)
        {
            try { Progenitor.Plugin.Frame -= OnFrame; }
            catch { /* */ }
            _frameHooked = false;
        }

        Release(HoldCancel.Unloaded);
        _containers.Clear();
        _active = null;
        _ring.Dispose();
        _hint.Dispose();
    }

    /// <summary>Cancel a live hold and drop every swatch watch; the ring and the line hide (they stay built).</summary>
    private void Release(HoldCancel why)
    {
        _machine.Cancel(why);
        foreach (var s in _swatches.Values) s.Dispose();
        _swatches.Clear();
        _held = null;
        _pulseStart = -1;
        _hasColorSwatch = false;
        _ring.Hide();
        _hint.Hide();
    }

    /// <summary>
    /// Re-read whether the former mod runs; on a change, release (or resume) and log once. Returns whether
    /// this copy stands down now.
    /// </summary>
    private bool CheckStandDown()
    {
        bool old;
        try { old = _oldModRunning(); }
        catch { old = false; }
        if (old == _standingDown) return old;
        _standingDown = old;
        if (old)
        {
            Release(HoldCancel.Unloaded);
            _log.LogInfo("Dye all: the former Dye all mod (" + DyeAllLegacy.PluginGuid + ") is running; Progenitor's copy "
                         + "stands down so nothing is dyed twice. Dye all is now part of Progenitor; that mod can be uninstalled.");
        }
        else
        {
            _log.LogInfo("Dye all: the former Dye all mod is not running; Progenitor's copy is active.");
        }

        return old;
    }

    private void OnFrame()
    {
        if (_stopped) return;
        try
        {
            double now = Now;
            if (now >= _nextScan)
            {
                _nextScan = now + ScanSeconds;
                bool standDown = CheckStandDown();
                bool enabled = _entries.Enabled.Value;
                var world = enabled && !standDown ? Worlds.Client : null;
                bool hasWorld = world != null && world.IsCreated;
                int menu = hasWorld ? MenuType(world!) : DyeScanGate.MenuNone;
                var scanStep = DyeScanGate.Decide(enabled, standDown, hasWorld, menu);
                _lastStep = scanStep;
                _lastMenu = menu;
                _lastHasWorld = hasWorld;
                switch (scanStep)
                {
                    case DyeScanStep.Release:
                        if (!_released)
                        {
                            // Off (or standing down): swatches behave exactly as in the game, with no watch on them.
                            Release(standDown ? HoldCancel.Unloaded : HoldCancel.Disabled);
                            _released = true;
                        }

                        break;
                    case DyeScanStep.Pause:
                        // The title screen, loading, or in game with no menu open: no scene search, no listing.
                        _released = false;
                        _hasColorSwatch = false;
                        _active = null;
                        _cellsListed = 0;
                        _hint.Hide();
                        break;
                    default:
                        _released = false;
                        Scan(now);
                        UpdateHint();
                        break;
                }
            }

            if (!_released) Drive(now);
            _frameErrors = 0;
        }
        catch (Exception e) when (e is TypeLoadException or MissingMethodException or MissingFieldException)
        {
            // A game update that removed an interop member this reads: stop once with one clear line
            // instead of failing every frame. The next Core generation tries again.
            _log.LogError("Dye all: a game type or member it reads is missing (" + e.GetType().Name + ": "
                + e.Message + "); hold-to-dye is off for this Core generation.");
            Stop();
        }
        catch (Exception e)
        {
            // The full exception once; a run of failures stops the feature for this generation instead of
            // logging every frame.
            _frameErrors++;
            if (_frameErrors == 1)
                _log.LogError("Dye all: frame threw: " + e);
            if (_frameErrors >= MaxFrameErrors)
            {
                _log.LogError("Dye all: " + _frameErrors + " frames in a row threw (last: " + e.GetType().Name + ": " + e.Message
                              + "); hold-to-dye is off for this Core generation.");
                Stop();
                return;
            }

            _machine.Cancel(HoldCancel.Closed);
            _ring.Hide();
            _hint.Hide();
        }
    }

    /// <summary>
    /// The open HUD menu (<c>OpenHUDMenuSystem.CurrentMenuType</c>), or <see cref="DyeScanGate.MenuUnknown"/>,
    /// logged once, when it cannot be read; the gate then scans as before.
    /// </summary>
    private int MenuType(Unity.Entities.World world)
    {
        int menu = BuildMenuOpener.CurrentMenuType(world);
        if (menu < 0 && !_menuUnknownLogged)
        {
            _menuUnknownLogged = true;
            _log.LogWarning("Dye all: the open HUD menu cannot be read; looking for the color picker on every scan instead.");
        }

        return menu < 0 ? DyeScanGate.MenuUnknown : menu;
    }

    // ---- swatches ----------------------------------------------------------------------------

    private void Scan(double now)
    {
        _hasColorSwatch = false;
        _active = null;
        _cellsListed = 0;
        _containers.RemoveAll(c => !Alive(c));
        int knownActive = 0;
        foreach (var c in _containers)
            if (c.gameObject.activeInHierarchy) knownActive++;
        if (DyePickerSearch.ShouldFind(knownActive, now, _nextFind))
        {
            _nextFind = now + FindSeconds;
            _finds++;
            FindContainers(_containers);
        }

        PruneDead();
        foreach (var container in _containers)
        {
            if (!container.gameObject.activeInHierarchy) continue;
            _active ??= container;
            foreach (var cell in container.GetComponentsInChildren<TransmogColorGridSelectionEntry>(true))
            {
                if (cell == null) continue;
                _cellsListed++;
                if (!_swatches.TryGetValue(cell.Pointer, out var swatch))
                {
                    int id = _nextSwatchId++;
                    var added = new Swatch(id, cell);
                    added.Watch(_owner, ev => OnPointer(added, ev));
                    _swatches[cell.Pointer] = added;
                    swatch = added;
                }

                if (!_hasColorSwatch && cell.gameObject.activeInHierarchy && swatch.TryRead(out var data) && data.IsColor)
                    _hasColorSwatch = true;
            }
        }
    }

    /// <summary>Show or hide the palette's instruction line (<see cref="DyeHint.ShouldShow"/>).</summary>
    private void UpdateHint()
    {
        var container = Alive(_active) ? _active : null;
        bool paletteShown = false;
        if (container != null && container.gameObject.activeInHierarchy)
        {
            var grid = container.ColorSelectionParent;
            paletteShown = Alive(grid) && grid.gameObject.activeInHierarchy;
        }

        if (container != null && DyeHint.ShouldShow(_stopped, _entries.Enabled.Value, paletteShown, _hasColorSwatch))
            _hint.Show(container);
        else
            _hint.Hide();
    }

    /// <summary>Replace <paramref name="into"/> with every <c>TransmogContainer</c> in the scene, inactive ones included.</summary>
    private static void FindContainers(List<TransmogContainer> into)
    {
        into.Clear();
        var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<TransmogContainer>(), true);
        if (found == null) return;
        foreach (var o in found)
        {
            var c = o?.TryCast<TransmogContainer>();
            if (c != null && Alive(c)) into.Add(c);
        }
    }

    private void PruneDead()
    {
        List<IntPtr>? dead = null;
        foreach (var pair in _swatches)
        {
            if (Alive(pair.Value.Cell)) continue;
            (dead ??= new List<IntPtr>()).Add(pair.Key);
        }

        if (dead == null) return;
        foreach (var key in dead)
        {
            var swatch = _swatches[key];
            if (ReferenceEquals(swatch, _held)) _machine.Cancel(HoldCancel.Closed);
            swatch.Dispose();
            _swatches.Remove(key);
        }
    }

    // ---- the hold ----------------------------------------------------------------------------

    private void OnPointer(Swatch swatch, UiPointerEvent ev)
    {
        if (_stopped || _standingDown || _released) return;
        try
        {
            switch (ev.Kind)
            {
                case UiPointerEventKind.Down:
                    if (!swatch.TryRead(out var data)) return;
                    if (_machine.Press(swatch.Id, ev.PrimaryHeld, data.IsColor, _entries.Enabled.Value, _entries.HoldTime, Now))
                    {
                        _held = swatch;
                        _heldIndex = data.Index;
                        _heldItem = data.Item;
                        _pulseStart = -1;
                        _ring.Show(swatch.Cell);
                    }

                    break;
                case UiPointerEventKind.Up:
                    _machine.Release(swatch.Id);
                    break;
                case UiPointerEventKind.Exit:
                    _machine.Leave(swatch.Id);
                    break;
            }
        }
        catch (Exception e)
        {
            _log.LogError("Dye all: pointer " + ev.Kind + " threw: " + e);
        }
    }

    private void Drive(double now)
    {
        if (_pulseStart >= 0)
        {
            var (scale, alpha, done) = HoldRingMath.Pulse(now - _pulseStart);
            _ring.SetPulse(scale, alpha);
            if (done)
            {
                _pulseStart = -1;
                _ring.Hide();
            }
        }

        if (_machine.Phase == HoldPhase.Idle && _held == null) return;

        bool visible = _held != null && HeldStillShown(_held);
        var step = _machine.Tick(now, visible, _entries.Enabled.Value);
        if (step.Fire)
        {
            _pulseStart = now;
            _ring.SetProgress(1f);
            var color = new DyeColor(_heldIndex, _entries.MatchPalettes.Value ? ReadPalettesOf(_heldItem) : null);
            Apply(color, "hold");
            return;
        }

        if (step.Phase == HoldPhase.Charging)
        {
            _ring.SetProgress(step.Progress);
            return;
        }

        if (step.Phase == HoldPhase.Idle)
        {
            if (step.Cancelled != HoldCancel.None)
            {
                _log.LogInfo("Dye all: hold canceled (" + step.Cancelled + ").");
                NoteHold("canceled (" + step.Cancelled + ")");
            }
            if (_pulseStart < 0) _ring.Hide();
            _held = null;
        }
    }

    /// <summary>The held cell still shows the same color: panel open (the cell is active in the hierarchy), data unchanged.</summary>
    private bool HeldStillShown(Swatch swatch)
    {
        if (!Alive(swatch.Cell) || !swatch.Cell.gameObject.activeInHierarchy) return false;
        return swatch.TryRead(out var data) && data.IsColor && data.Index == _heldIndex;
    }

    // ---- applying -----------------------------------------------------------------------------

    private void Apply(DyeColor color, string why)
    {
        var world = Worlds.Client;
        if (world == null || !world.IsCreated)
        {
            Report("Dye all (" + why + "): no client world.");
            return;
        }

        var em = world.EntityManager;
        if (!DyeSender.TryLocalCharacter(em, out var character))
        {
            Report("Dye all (" + why + "): no local character.");
            return;
        }

        var slots = DyeSender.ReadSlots(em, character, _entries.MatchPalettes.Value, _log);
        var plan = DyePlanner.Plan(slots, color);
        int sent = DyeSender.Send(em, character, plan, _log);
        if (why == "hold") NoteHold("color " + color.Index + ", sent " + sent + " of " + plan.Targets.Count);
        Report("Dye all (" + why + "): color " + color.Index + ", sent " + sent + " of " + plan.Targets.Count
               + (plan.MatchedByPalette ? " (palette-matched)" : "") + ": " + plan.Describe());
    }

    private IReadOnlyList<DyePaletteKey>? ReadPalettesOf(Entity item)
    {
        var world = Worlds.Client;
        if (world == null || !world.IsCreated || item == Entity.Null) return null;
        return DyeSender.ReadPalettes(world.EntityManager, item, _log);
    }

    private void NoteHold(string what)
    {
        _lastHold = what;
        _lastHoldUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// The dev bridge's <c>dye</c> readback (through <see cref="DyeAllTestBridge"/>): the last scan step and its
    /// inputs, every known container, the listed and watched cells, the hint, and the last hold. With
    /// <paramref name="cells"/>, the active picker's shown cells with their screen centers (Input System pixels,
    /// origin bottom left), so the bridge's <c>mouse</c> op can press one. Main thread.
    /// </summary>
    internal Dictionary<string, object?> Describe(bool cells)
    {
        bool activeAlive = _active != null && Alive(_active);
        var r = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["enabled"] = _entries.Enabled.Value,
            ["holdSeconds"] = _entries.HoldTime,
            ["stopped"] = _stopped,
            ["standingDown"] = _standingDown,
            ["released"] = _released,
            ["step"] = _lastStep.ToString(),
            ["menu"] = _lastMenu,
            ["hasWorld"] = _lastHasWorld,
            ["finds"] = _finds,
            ["containerFound"] = activeAlive,
            ["containerActive"] = activeAlive && _active!.gameObject.activeInHierarchy,
            ["cellsListed"] = _cellsListed,
            ["watched"] = _swatches.Count,
            ["watchHandles"] = WatchHandleCount(),
            ["hasColorSwatch"] = _hasColorSwatch,
            ["hintShown"] = _hint.Shown,
            ["hintRow"] = _hint.Where,
            ["hintText"] = DyeHint.Text,
            ["holdPhase"] = _machine.Phase.ToString(),
            ["heldSwatch"] = _held?.Id,
            ["lastHold"] = _lastHold,
            ["lastHoldUtc"] = _lastHoldUtc == default ? null : _lastHoldUtc.ToString("o"),
            ["frameErrors"] = _frameErrors,
        };
        var list = new List<object?>();
        foreach (var c in _containers)
        {
            bool alive = Alive(c);
            list.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["menu"] = alive ? MenuOf(c.transform) : null,
                ["alive"] = alive,
                ["active"] = alive && c.gameObject.activeInHierarchy,
                ["current"] = alive && ReferenceEquals(c, _active),
            });
        }

        r["containers"] = list;
        if (cells && activeAlive && _active!.gameObject.activeInHierarchy)
        {
            var rows = new List<object?>();
            foreach (var cell in _active.GetComponentsInChildren<TransmogColorGridSelectionEntry>(true))
            {
                if (cell == null || !cell.gameObject.activeInHierarchy) continue;
                var row = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["watched"] = _swatches.ContainsKey(cell.Pointer),
                };
                if (_swatches.TryGetValue(cell.Pointer, out var sw))
                {
                    row["id"] = sw.Id;
                    if (sw.TryRead(out var data))
                    {
                        row["color"] = data.IsColor;
                        row["index"] = data.Index;
                    }
                }

                if (TryScreenCenter(cell.gameObject, out var x, out var y))
                {
                    row["x"] = Math.Round(x, 1);
                    row["y"] = Math.Round(y, 1);
                }

                rows.Add(row);
            }

            r["cells"] = rows;
        }

        return r;
    }

    private int WatchHandleCount()
    {
        int n = 0;
        foreach (var s in _swatches.Values) n += s.WatchCount;
        return n;
    }

    /// <summary>The HUD menu a container sits under (the child of <c>HUDMenuParent</c>), for the readback.</summary>
    private static string MenuOf(Transform t)
    {
        string name = t.name;
        for (var p = t; p != null; p = p.parent)
        {
            var parent = p.parent;
            if (parent != null && parent.name == "HUDMenuParent") return p.name;
            name = p.name;
        }

        return name;
    }

    /// <summary>A UI object's rect center in screen pixels (origin bottom left), through its root canvas' camera.</summary>
    private static bool TryScreenCenter(GameObject go, out float x, out float y)
    {
        x = y = 0f;
        try
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return false;
            var canvas = go.GetComponentInParent<Canvas>();
            Camera? cam = null;
            if (canvas != null)
            {
                var root = canvas.rootCanvas;
                if (root != null && root.renderMode != RenderMode.ScreenSpaceOverlay) cam = root.worldCamera;
            }

            var world = rt.TransformPoint(rt.rect.center);
            var p = RectTransformUtility.WorldToScreenPoint(cam, world);
            x = p.x;
            y = p.y;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Report(string line)
    {
        _log.LogInfo(line);
    }

    private static bool Alive(UnityEngine.Object? o) => o != null && o.Pointer != IntPtr.Zero;

    /// <summary>One watched color cell.</summary>
    private sealed class Swatch : IDisposable
    {
        private readonly List<IDisposable> _watches = new();

        internal Swatch(int id, TransmogColorGridSelectionEntry cell)
        {
            Id = id;
            Cell = cell;
        }

        internal int Id { get; }

        internal int WatchCount => _watches.Count;

        internal TransmogColorGridSelectionEntry Cell { get; }

        /// <summary>
        /// Watch the cell's own object and, when it is a different object, its button's: the press
        /// goes to whichever raycast target is deepest, and both report the same swatch.
        /// </summary>
        internal void Watch(object owner, Action<UiPointerEvent> handler)
        {
            _watches.Add(Ui.Ui.WatchPointer(Cell.gameObject, handler, owner));
            GameObject? button = null;
            try { button = Cell.Button != null ? Cell.Button.gameObject : null; }
            catch { button = null; }
            if (button != null && button.Pointer != Cell.gameObject.Pointer)
                _watches.Add(Ui.Ui.WatchPointer(button, handler, owner));
        }

        /// <summary>Read the cell's <c>EntryData</c> (client dump 391515).</summary>
        internal bool TryRead(out CellData data)
        {
            data = default;
            try
            {
                var d = Cell.EntryData;
                if (d == null) return false;
                var slot = d.EquipmentSlot;
                var item = slot.SlotEntity._WaitingForSync ? Entity.Null : slot.SlotEntity._Entity;
                data = new CellData(d.EntryType == TransmogColorGridSelectionEntry.ColorEntryType.Normal, d.Index, item);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            foreach (var w in _watches)
            {
                try { w.Dispose(); }
                catch { /* the cell is already gone */ }
            }

            _watches.Clear();
        }
    }

    private readonly struct CellData
    {
        internal CellData(bool isColor, byte index, Entity item)
        {
            IsColor = isColor;
            Index = index;
            Item = item;
        }

        internal bool IsColor { get; }

        internal byte Index { get; }

        internal Entity Item { get; }
    }
}
