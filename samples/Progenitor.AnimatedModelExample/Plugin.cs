using System;
using BepInEx;
using Progenitor.Models;
using Progenitor.Plugins;
using Progenitor.Ui;
using UnityEngine;

namespace Progenitor.AnimatedModelExample;

[BepInPlugin("progenitor.animatedmodel.example", "Progenitor Animated Model Example", "0.1.0")]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Client)]
public sealed class Plugin : ProgenitorPlugin<AnimatedModelContext>
{
    protected override void OnLoad(AnimatedModelContext ctx)
    {
        var path = ctx.Bind("Model", "Path", "", "Absolute authored GLB path. Empty: import nothing.");
        var x = ctx.Bind("Placement", "X", 0f, "Client world X coordinate.");
        var y = ctx.Bind("Placement", "Y", 0f, "Client world Y (height) coordinate.");
        var z = ctx.Bind("Placement", "Z", 0f, "Client world Z coordinate.");
        var scale = ctx.Bind("Placement", "Scale", 1f, "Positive uniform model scale.");
        var lifetime = ctx.Bind("Model", "LifetimeSeconds", 60f, "Preview lifetime, from 1 to 300 seconds.");

        ctx.Keybinds.Register("Preview", new KeyChord(UiKey.F6, KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt),
            "Animated model: show/remove the configured GLB preview.", () =>
            {
                if (ctx.Model != null) { Remove(ctx); return; }
                if (string.IsNullOrWhiteSpace(path.Value))
                {
                    ctx.Log.LogInfo("Animated model: configure [Model] Path before showing a preview.");
                    return;
                }
                try
                {
                    if (!float.IsFinite(x.Value) || !float.IsFinite(y.Value) || !float.IsFinite(z.Value)
                        || !float.IsFinite(scale.Value) || scale.Value <= 0
                        || !float.IsFinite(lifetime.Value) || lifetime.Value < 1 || lifetime.Value > 300)
                        throw new ArgumentException("Placement must be finite, Scale positive, and LifetimeSeconds within 1–300.");
                    var model = ctx.Models.ImportGlb(path.Value.Trim(), "AnimatedBannerExample");
                    ctx.Model = model;
                    model.Root.transform.position = new Vector3(x.Value, y.Value, z.Value);
                    model.Root.transform.localScale = Vector3.one * scale.Value;
                    ctx.ClipIndex = 0;
                    ctx.Paused = false;
                    if (model.Animations.ClipNames.Count == 0)
                        throw new ArgumentException("The configured GLB contains no animations.");
                    model.Animations.Play(model.Animations.ClipNames[0]);
                    ctx.Log.LogInfo("Animated model: playing " + model.Animations.ClipNames[0]
                        + "; available clips: " + string.Join(", ", model.Animations.ClipNames));
                    Progenitor.Dev.MainThread.PostDelayed(lifetime.Value, () =>
                    {
                        if (ReferenceEquals(ctx.Model, model)) Remove(ctx);
                    }, ctx.Owner);
                }
                catch (Exception e)
                {
                    Remove(ctx);
                    ctx.Log.LogWarning("Animated model preview failed: " + e.Message);
                }
            });
        ctx.Keybinds.Register("NextClip", new KeyChord(UiKey.F7, KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt),
            "Animated model: play the next authored clip.", () =>
            {
                var model = ctx.Model;
                if (model == null) return;
                ctx.ClipIndex = (ctx.ClipIndex + 1) % model.Animations.ClipNames.Count;
                model.Animations.Play(model.Animations.ClipNames[ctx.ClipIndex]);
                ctx.Paused = false;
                ctx.Log.LogInfo("Animated model: playing " + model.Animations.ClipNames[ctx.ClipIndex]);
            });
        ctx.Keybinds.Register("PauseResume", new KeyChord(UiKey.F8, KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt),
            "Animated model: pause/resume the current clip.", () =>
            {
                if (ctx.Model?.Animations.CurrentClip == null) return;
                if (ctx.Paused) ctx.Model.Animations.Resume();
                else ctx.Model.Animations.Pause();
                ctx.Paused = !ctx.Paused;
            });
        ctx.Keybinds.Register("Seek", new KeyChord(UiKey.F9, KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt),
            "Animated model: seek the current clip to 0.5 seconds.", () =>
            { if (ctx.Model?.Animations.CurrentClip != null) ctx.Model.Animations.Seek(0.5f); });
        ctx.Keybinds.Register("RestPose", new KeyChord(UiKey.F11, KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt),
            "Animated model: stop playback and reset the authored rest pose.", () =>
            {
                ctx.Model?.Animations.Stop();
                ctx.Paused = false;
            });
        ctx.Log.LogInfo("Animated model: configured preview controls are registered in Keys. Show creates a bounded preview.");
    }

    protected override void OnUnload(AnimatedModelContext ctx, PluginUnloadReason reason) => Remove(ctx);

    private static void Remove(AnimatedModelContext ctx)
    {
        ctx.Model?.Dispose();
        ctx.Model = null;
        ctx.Paused = false;
    }
}

public sealed class AnimatedModelContext : PluginContext
{
    internal ImportedModel? Model;
    internal int ClipIndex;
    internal bool Paused;
}
