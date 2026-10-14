#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM.Network;
using Progenitor.Ecs;
using TMPro;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// P7 <c>ctx.Hud.Locate</c>, the Unity half. The game has no ping or world-marker HUD (only the map's single custom
/// marker, which is persisted per player on the server), so a ping is framework UI: each frame its target's position
/// (the client entity's <c>Translation</c>, read through <c>TryRead</c>) goes through the game camera's
/// <c>WorldToScreenPoint</c>; on screen a gold diamond marker floats over it with "label · 14 m", off screen or behind
/// the camera an arrow sits on the screen edge pointing the way to turn. The frame tick runs only while a ping exists.
/// A ping ends when its time is up, its entity no longer exists, the world epoch changes, or its owner unloads.
/// </summary>
internal sealed partial class UnityUiRuntime
{
    private static readonly UiLocateSet LocateSet = new();
    private static bool _locateTicking;
    private static bool _locateHooked;
    private static GameObject? _locateCanvas;
    private static Sprite? _markerSprite, _arrowSprite;
    private static Texture2D? _markerTexture, _arrowTexture;
    private static Entity _localCharacter = Entity.Null;
    private static double _localCharacterAt = double.NegativeInfinity;
    private static bool _locateLogged;

    private sealed class PingView
    {
        internal GameObject Root = null!;
        internal CanvasGroup? Group;
        internal RectTransform Marker = null!;
        internal RectTransform Arrow = null!;
        internal TextMeshProUGUI? Label;
        internal string LastLabel = "";
    }

    /// <summary>The live pings (the bridge and tests read it).</summary>
    internal static UiLocateSet HudPings => LocateSet;

    internal static UiLocatePingHandle HudLocate(object owner, UiLocateTarget target, string label, double seconds)
    {
        if (!_locateHooked)
        {
            _locateHooked = true;
            LocateSet.Ended += OnPingEnded;
            UiWidgetLifetime.Register("hud-locate", TeardownPings);
        }

        var ping = LocateSet.Add(owner, target, label, seconds, HudNow(), Progenitor.WorldState.WorldEpoch.Current);
        if (!_locateLogged)
        {
            _locateLogged = true;
            Plugin.Logger?.LogInfo("Ui: ctx.Hud.Locate draws pings as framework UI over the game camera "
                + "(marker on screen, edge arrow off screen; at most " + UiLocateLook.MaxPings + ").");
        }

        if (!_locateTicking)
        {
            _locateTicking = true;
            Plugin.Frame += LocateTick;
        }

        return ping;
    }

    internal static int HudDropPings(object owner) => LocateSet.DropOwner(owner);

    private static void StopLocateTick()
    {
        if (!_locateTicking) return;
        _locateTicking = false;
        Plugin.Frame -= LocateTick;
    }

    private static void LocateTick()
    {
        try
        {
            double now = HudNow();
            LocateSet.Sweep(now, Progenitor.WorldState.WorldEpoch.Current);
            // Readiness every tick: no client world ends every ping at once; no local character (a kick leaves the
            // client world alive) ends them after a short grace. The epoch sweep above covers a world swap.
            var world = Worlds.Client;
            bool worldAlive = world != null && world.IsCreated;
            bool hasCharacter = false;
            Vector3 characterAt = default;
            if (worldAlive) hasCharacter = TryLocalCharacterPosition(world!.EntityManager, now, out characterAt);
            if (LocateSet.Readiness(now, worldAlive, hasCharacter) > 0)
                Plugin.Logger?.LogInfo("Ui: ctx.Hud.Locate ended every ping: the player left the world.");
            if (LocateSet.Count == 0)
            {
                StopLocateTick();
                if (Alive(_locateCanvas)) _locateCanvas!.SetActive(false);
                return;
            }

            var cam = Camera.main;
            if (!worldAlive || !hasCharacter || cam == null || !Alive(cam))
            {
                // A respawn or loading hitch: keep the pings (within the grace) and draw nothing this frame.
                if (Alive(_locateCanvas)) _locateCanvas!.SetActive(false);
                return;
            }

            if (!EnsureLocateCanvas()) return;
            _locateCanvas!.SetActive(true);
            var em = world.EntityManager;
            var canvasRt = _locateCanvas.GetComponent<RectTransform>();
            float canvasW = canvasRt.rect.width, canvasH = canvasRt.rect.height;
            float screenW = Screen.width, screenH = Screen.height;
            if (canvasW <= 0f || canvasH <= 0f || screenW <= 0f || screenH <= 0f) return;
            float k = screenW / canvasW; // screen pixels per design pixel

            foreach (var ping in LocateSet.Snapshot())
                DrawPing(ping, em, cam, now, k, screenW, screenH, characterAt);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: ctx.Hud.Locate tick failed: " + e.Message + "; every ping is ended.");
            LocateSet.Clear();
            StopLocateTick();
        }
    }

    private static void DrawPing(UiLocatePingHandle ping, EntityManager em, Camera cam, double now, float k,
        float screenW, float screenH, Vector3 from)
    {
        if (!TryPingPosition(ping, em, out var target, out bool gone))
        {
            if (gone)
            {
                LocateSet.End(ping, "entity-gone");
                return;
            }

            if (ping.PresenterState is PingView hidden) hidden.Root.SetActive(false);
            return;
        }

        var view = ping.PresenterState as PingView ?? BuildPingView(ping);
        if (view == null) return;

        double age = now - ping.StartedAt;
        var above = target + new Vector3(0f, UiLocateLook.HeightOffset, 0f);
        Vector3 sp = cam.WorldToScreenPoint(above);
        float margin = (UiLocateLook.EdgeMargin + UiLocateLook.MarkerSize * 0.5f) * k;
        float labelRoom = (UiLocateLook.LabelGap + UiLocateLook.LabelHeight) * k;
        var place = UiLocateProjection.Place(sp.x, sp.y, sp.z, screenW, screenH, margin, labelRoom);

        var pos = new Vector2(place.X / k, place.Y / k);
        view.Root.SetActive(true);
        if (view.Group != null) view.Group.alpha = UiLocateLook.Alpha(age, ping.Seconds);

        view.Marker.gameObject.SetActive(place.OnScreen);
        view.Arrow.gameObject.SetActive(!place.OnScreen);
        float scale = UiLocateLook.Scale(age);
        Vector2 labelPos;
        if (place.OnScreen)
        {
            view.Marker.anchoredPosition = pos;
            view.Marker.localScale = new Vector3(scale, scale, 1f);
            labelPos = pos + new Vector2(0f, UiLocateLook.MarkerSize * 0.5f + UiLocateLook.LabelGap + UiLocateLook.LabelHeight * 0.5f);
        }
        else
        {
            view.Arrow.anchoredPosition = pos;
            view.Arrow.localEulerAngles = new Vector3(0f, 0f, place.AngleDegrees);
            view.Arrow.localScale = new Vector3(scale, scale, 1f);
            // The label sits inward from the arrow, toward the screen center, so it never leaves the screen.
            double rad = place.AngleDegrees * Math.PI / 180.0;
            var inward = new Vector2(-(float)Math.Cos(rad), -(float)Math.Sin(rad));
            labelPos = pos + inward * (UiLocateLook.ArrowSize + UiLocateLook.LabelHeight);
        }

        if (view.Label != null)
        {
            double meters = Vector3.Distance(from, target);
            string text = UiLocateLook.Label(ping.Label, meters);
            if (!string.Equals(text, view.LastLabel, StringComparison.Ordinal))
            {
                view.LastLabel = text;
                view.Label.text = text;
            }

            float half = UiLocateLook.LabelMaxWidth * 0.5f;
            float cw = screenW / k, ch = screenH / k;
            labelPos.x = Mathf.Clamp(labelPos.x, half, Math.Max(half, cw - half));
            labelPos.y = Mathf.Clamp(labelPos.y, UiLocateLook.LabelHeight, Math.Max(UiLocateLook.LabelHeight, ch - UiLocateLook.LabelHeight));
            view.Label.rectTransform.anchoredPosition = labelPos;
        }
    }

    /// <summary>
    /// The ping's world position. False with <paramref name="gone"/> when its entity no longer exists (the ping ends);
    /// false without it when the entity has no readable position this frame (the ping hides and waits).
    /// </summary>
    private static bool TryPingPosition(UiLocatePingHandle ping, EntityManager em, out Vector3 position, out bool gone)
    {
        position = default;
        gone = false;
        var t = ping.Target;
        if (!t.IsEntity)
        {
            position = new Vector3(t.X, t.Y, t.Z);
            return true;
        }

        var e = new Entity { Index = t.EntityIndex, Version = t.EntityVersion };
        if (!em.Exists(e))
        {
            gone = true;
            return false;
        }

        if (e.TryRead<Translation>(em, out var tr))
        {
            position = new Vector3(tr.Value.x, tr.Value.y, tr.Value.z);
            return true;
        }

        if (e.TryRead<LocalToWorld>(em, out var ltw))
        {
            var p = ltw.Position;
            position = new Vector3(p.x, p.y, p.z);
            return true;
        }

        return false;
    }

    /// <summary>The local character's position (cached entity, re-found at most once a second); false when there is none.</summary>
    private static bool TryLocalCharacterPosition(EntityManager em, double now, out Vector3 position)
    {
        position = default;
        try
        {
            if ((_localCharacter == Entity.Null || !em.Exists(_localCharacter)) && now - _localCharacterAt >= 1.0)
            {
                _localCharacterAt = now;
                _localCharacter = Entity.Null;
                var locals = Query.Entities(em, includeDisabled: false, includeSystems: false,
                    ComponentIndex.Of<LocalCharacter>(ComponentType.AccessMode.ReadOnly));
                if (locals.Count > 0) _localCharacter = locals[0];
            }

            if (_localCharacter != Entity.Null && em.Exists(_localCharacter)
                && _localCharacter.TryRead<Translation>(em, out var tr))
            {
                position = new Vector3(tr.Value.x, tr.Value.y, tr.Value.z);
                return true;
            }
        }
        catch { /* treated as no character this frame */ }

        return false;
    }

    private static bool EnsureLocateCanvas()
    {
        if (Alive(_locateCanvas)) return true;
        _locateCanvas = CreateHudCanvas("hud-locate", UiOwnership.OverlaySorting - 20);
        if (_locateCanvas == null) return false;
        if (!Alive(_markerSprite))
            _markerSprite = HudSprite("hud-locate-marker", UiLocateGlyph.TextureSize, UiLocateGlyph.TextureSize, UiLocateGlyph.MarkerAlpha, out _markerTexture);
        if (!Alive(_arrowSprite))
            _arrowSprite = HudSprite("hud-locate-arrow", UiLocateGlyph.TextureSize, UiLocateGlyph.TextureSize, UiLocateGlyph.ArrowAlpha, out _arrowTexture);
        return true;
    }

    private static PingView? BuildPingView(UiLocatePingHandle ping)
    {
        if (!Alive(_locateCanvas)) return null;
        try
        {
            var zero = Vector2.zero;
            var root = HudChild(_locateCanvas!, "ping-" + ping.Serial, zero, zero, zero);
            Stretch(root);
            var view = new PingView { Root = root, Group = TryAdd<CanvasGroup>(root) };
            if (view.Group != null)
            {
                view.Group.interactable = false;
                view.Group.blocksRaycasts = false;
                view.Group.alpha = 0f;
            }

            var gold = ToColor(UiLocateLook.Accent);
            var marker = HudChild(root, "marker", zero, new Vector2(0.5f, 0.5f), new Vector2(UiLocateLook.MarkerSize, UiLocateLook.MarkerSize));
            HudImage(marker, _markerSprite, gold);
            var arrow = HudChild(root, "arrow", zero, new Vector2(0.5f, 0.5f), new Vector2(UiLocateLook.ArrowSize, UiLocateLook.ArrowSize));
            HudImage(arrow, _arrowSprite, gold);
            var label = HudChild(root, "label", zero, new Vector2(0.5f, 0.5f), new Vector2(UiLocateLook.LabelMaxWidth, UiLocateLook.LabelHeight));
            view.Label = HudText(label, UiLocateLook.LabelSize, ToColor(UiLocateLook.LabelColor));
            view.Marker = marker.GetComponent<RectTransform>();
            view.Arrow = arrow.GetComponent<RectTransform>();
            ping.PresenterState = view;
            return view;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: ctx.Hud.Locate could not build a ping (" + e.Message + "); it is ended.");
            LocateSet.End(ping, "build-failed");
            return null;
        }
    }

    private static void OnPingEnded(UiLocatePingHandle ping)
    {
        if (ping.PresenterState is PingView view && Alive(view.Root)) TryDestroy(view.Root, immediate: false);
        ping.PresenterState = null;
    }

    /// <summary>Core teardown (every unload path, via <see cref="UiWidgetLifetime"/>): pings, canvas, art.</summary>
    private static void TeardownPings()
    {
        StopLocateTick();
        LocateSet.Clear();
        LocateSet.Ended -= OnPingEnded;
        _locateHooked = false;
        if (Alive(_locateCanvas)) TryDestroy(_locateCanvas, immediate: false);
        _locateCanvas = null;
        FreeHudAsset(_markerSprite);
        FreeHudAsset(_arrowSprite);
        FreeHudAsset(_markerTexture);
        FreeHudAsset(_arrowTexture);
        _markerSprite = _arrowSprite = null;
        _markerTexture = _arrowTexture = null;
        _localCharacter = Entity.Null;
        _localCharacterAt = double.NegativeInfinity;
    }
}
#endif
