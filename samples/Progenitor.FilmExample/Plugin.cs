using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using Progenitor.Models;
using Progenitor.Plugins;
using UnityEngine;

namespace Progenitor.FilmExample;

/// <summary>
/// Demo: the set of "The Comments", a short film shot in the game. So far it builds the two
/// actors as puppets with <c>ctx.Models</c>: Dracula's evolved model (which the game only loads
/// during his boss fight) without wings or sword, and TestPlayer, the resident female vampire model
/// dressed in the Dracula armor set and the Razer hood; then dresses the set in the monastery
/// (<see cref="SetDressing"/>) and seats both at the desk in a placeholder pose. Client only.
/// Unload destroys everything it made, shows the hidden scenery again, and releases every model.
/// </summary>
[BepInPlugin("progenitor.film", "Progenitor Film Example", "0.1.0")]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Client)]
public sealed partial class Plugin : ProgenitorPlugin<FilmContext>
{
    protected override void OnLoad(FilmContext ctx)
    {
        var folder = ctx.Bind("Film", "Folder", "",
            "Folder that holds the set (set/film_set.glb), the take (take/take.json), capture requests (capture.json), "
            + "marked shots and screen frames. Empty: BepInEx/" + FilmPaths.DefaultFolderName + ".");
        FilmPaths.Root = FilmPaths.Resolve(folder.Value);
        ctx.Log.LogInfo("Film: files under " + FilmPaths.Root);
        ctx.Dracula =ctx.Models.Load(Cast.DraculaEvolved.Section, Cast.DraculaEvolved.Prefab);
        ctx.TestPlayerBody = ctx.Models.LoadResident(Cast.FemaleBody);
        foreach (var (section, prefab) in Cast.TestPlayerCostume)
            ctx.TestPlayerCostume.Add(ctx.Models.Load(section, prefab));
        ctx.Log.LogInfo("Film: loading the cast (" + (2 + ctx.TestPlayerCostume.Count) + " models)");
        CameraTools.Start(ctx);
        WaitForCast(ctx, DateTime.UtcNow);
    }

    private static void WaitForCast(FilmContext ctx, DateTime started)
    {
        var all = new List<ModelHandle> { ctx.Dracula!, ctx.TestPlayerBody! };
        all.AddRange(ctx.TestPlayerCostume);
        if (all.Any(h => h.State == ModelLoadState.Pending))
        {
            Progenitor.Dev.MainThread.PostDelayed(0.1, () => WaitForCast(ctx, started), ctx.Owner);
            return;
        }

        try { Stage.Build(ctx, all, started); }
        catch (Exception e) { ctx.Log.LogError("Film: building the cast threw: " + e); }
    }
}

/// <summary>
/// Where the film's files live: <c>[Film] Folder</c> in this plugin's cfg, else <c>BepInEx/progenitor-film</c>.
/// Nothing machine-specific is compiled in.
///
/// <para><b>Why raw files and not a store</b> (docs/modules.md "Where should my data go?"). This is
/// a client-only working folder shared with tools outside the game: the set is a <c>.glb</c> made
/// elsewhere, the take and capture requests are written by a script, marked shots and frames are read
/// back by it. None of it is world state or server data, so it belongs neither in <c>ctx.Save</c>
/// nor in <c>ctx.ServerSave</c> (both are no-ops in a client process), and a wipe or restore should
/// not touch it.</para>
/// </summary>
internal static class FilmPaths
{
    internal const string DefaultFolderName = "progenitor-film";

    internal static string Root { get; set; } = Resolve(null);

    internal static string Resolve(string? configured) =>
        string.IsNullOrWhiteSpace(configured)
            ? System.IO.Path.Combine(Paths.BepInExRootPath ?? "", DefaultFolderName)
            : configured!.Trim();
}

internal static partial class Stage
{
    internal static void Build(FilmContext ctx, List<ModelHandle> all, DateTime started)
    {
        double ms = (DateTime.UtcNow - started).TotalMilliseconds;
        foreach (var h in all.Where(h => !h.IsReady))
            ctx.Log.LogWarning("Film: " + h.PrefabName + " did not load: " + h.State + " " + h.Reason);
        ctx.Log.LogInfo("Film: cast loaded in " + ms.ToString("0") + " ms");

        // Build both copies first. Dressing destroys equipment roots, and the engine then resets
        // poses and clears the materials of freshly made copies once (live 2026-09-28), so posing
        // and the costume wait for the next frame.
        if (ctx.TestPlayerBody!.IsReady)
        {
            var testplayer = ctx.TestPlayerBody.InstantiatePuppet("FilmTestPlayer");
            foreach (var piece in ctx.TestPlayerCostume.Where(h => h.IsReady))
            {
                var dressed = testplayer.Dress(piece.Instantiate());
                ctx.Log.LogInfo("Film: dressed " + piece.PrefabName + ": " + dressed.SkinnedMeshes + " meshes, " + dressed.Attachments + " attachments"
                                + (dressed.MissingBones.Count > 0 ? ", missing " + string.Join(",", dressed.MissingBones.Take(8)) : ""));
            }
            ctx.TestPlayerPuppet = testplayer;
        }

        if (ctx.Dracula!.IsReady)
        {
            var dracula = ctx.Dracula.InstantiatePuppet("FilmDracula");
            foreach (var r in dracula.Root.GetComponentsInChildren<Renderer>(true))
                if (r.name.StartsWith("Wings", StringComparison.Ordinal) || r.name.Contains("Sword")) r.enabled = false;
            Scale.ToPlayerHeight(ctx, dracula, 1.1f);
            ctx.DraculaPuppet = dracula;
        }

        Progenitor.Dev.MainThread.PostDelayed(0.05, () =>
        {
            try
            {
                var set = SetDressing.Build(ctx);
                SetDressing.WatchSetFile(ctx);
                if (ctx.TestPlayerPuppet != null) { Costume.Apply(ctx, ctx.TestPlayerPuppet); SetDressing.Seat(ctx.TestPlayerPuppet, set, SetDressing.TestPlayerChair); }
                if (ctx.DraculaPuppet != null) SetDressing.Seat(ctx.DraculaPuppet, set, SetDressing.DraculaChair);
                TakePlayer.Start(ctx, set);
                Shoot.Watch(ctx, set);
                Report(ctx, "posed");
            }
            catch (Exception e) { ctx.Log.LogError("Film: posing threw: " + e); }
        }, ctx.Owner);
        Report(ctx, "built");
        Progenitor.Dev.MainThread.PostDelayed(1.5, () => Report(ctx, "1.5 s later"), ctx.Owner);
    }

    /// <summary>One bone and one material per puppet, to show that poses and costume hold.</summary>
    internal static void Report(FilmContext ctx, string when)
    {
        string Hip(Puppet? p) => p != null && p.Has("Left_Hip_JNT") ? p.Bone("Left_Hip_JNT").localRotation.eulerAngles.ToString() : "-";
        string Mat(Puppet? p, string r) => p != null && p.TryGetBone(r, out var t) && t.GetComponent<Renderer>()?.sharedMaterial != null ? t.GetComponent<Renderer>().sharedMaterial.name : "null";
        ctx.Log.LogInfo("Film: " + when + ": Dracula hip " + Hip(ctx.DraculaPuppet) + " scale " + (ctx.DraculaPuppet?.Root.transform.localScale.x.ToString("0.00") ?? "-")
                        + "; TestPlayer hip " + Hip(ctx.TestPlayerPuppet) + " chest " + Mat(ctx.TestPlayerPuppet, "CHest_GEO"));
    }
}

public sealed partial class Plugin
{
    protected override void OnUnload(FilmContext ctx, PluginUnloadReason reason) =>
        ctx.Log.LogInfo("Film: unloading, reason=" + reason);
}

public sealed class FilmContext : PluginContext
{
    internal ModelHandle? Dracula;
    internal ModelHandle? TestPlayerBody;
    internal readonly List<ModelHandle> TestPlayerCostume = new();
    internal Puppet? DraculaPuppet;
    internal Puppet? TestPlayerPuppet;
    internal ImportedModel? Set;
    internal Progenitor.Film.TakeFile? Take;
    internal Progenitor.Film.FilmCamera? Camera;
    internal Progenitor.Film.FilmCapture? Capture;
    internal readonly List<SceneryHide> Hides = new();
}

/// <summary>Models used by the film. Sections are for the 2026-08-25 build (catalog b8bd8fa6…).</summary>
internal static class Cast
{
    private const string Characters = "ddfc6a4db4009e2448b8f8b6f0965b8f";
    private const string Equipment = "34436c5342ba99a468fbcb5f21d18bfa";

    internal static readonly (ModelSection Section, string Prefab) DraculaEvolved =
        (new ModelSection(Characters, 373), "HYB_NPCDracula_Evolved");

    internal const string FemaleBody = "HYB_VampireFemale_Prefab";

    internal static readonly (ModelSection Section, string Prefab)[] TestPlayerCostume =
    {
        (new ModelSection(Equipment, 111), "VampireFemale_ArmorSet04_Dracula_Chest"),
        (new ModelSection(Equipment, 261), "VampireFemale_ArmorSet04_Dracula_Legs"),
        (new ModelSection(Equipment, 155), "VampireFemale_ArmorSet04_Dracula_Gloves"),
        (new ModelSection(Equipment, 58), "VampireFemale_ArmorSet04_Dracula_Boots"),
        (new ModelSection(Equipment, 194), "VampireMale_HeadGear_RazerHood_Prefab"),
    };
}

/// <summary>
/// A placeholder seated pose to prove posing holds, written as world directions so no joint axes
/// are guessed: thighs forward and a little down, shins straight down, head tipped forward.
/// The film's real poses come from the take file.
/// </summary>
internal static class TestPose
{
    internal static void Seated(Puppet p)
    {
        var t = p.Root.transform;
        Vector3 forward = t.forward, down = -t.up;
        foreach (var side in new[] { "Left", "Right" })
        {
            Aim(p, side + "_Hip_JNT", side + "_Knee_JNT", (forward + down * 0.15f).normalized);
            Aim(p, side + "_Knee_JNT", side + "_Ankle_JNT", down);
        }
        if (p.Has("Head_JNT")) p.Bone("Head_JNT").rotation = Quaternion.AngleAxis(15f, t.right) * p.Bone("Head_JNT").rotation;
    }

    private static void Aim(Puppet p, string bone, string child, Vector3 dir)
    {
        if (p.Has(bone) && p.Has(child)) p.AimAt(bone, child, dir);
    }
}

/// <summary>
/// TestPlayer's costume materials. Equipment prefabs have empty material slots; these are the
/// materials the live game puts on her gear (read from her character, 2026-09-28).
/// </summary>
internal static class Costume
{
    internal static void Apply(FilmContext ctx, Puppet testplayer)
    {
        Set(ctx, testplayer, "VampireMale_ArmorSet04_Dracula", "CHest_GEO", "Legs_GEO", "Gloves_GEO2", "Boots_GEO2");
        Set(ctx, testplayer, "VampireMale_RazerCloak01", "RazerHood_GEO");
        if (testplayer.TryGetBone("Cape_GEO", out var cape))
        {
            cape.gameObject.SetActive(true);
            Set(ctx, testplayer, "VampireMale_Cloak08", "Cape_GEO");
        }

        // Her tints. The game sets exactly these four on the Dracula set (read from the Director
        // wearing it, 2026-09-28) and no _BaseColor; the transmog shader's _CustomizationMask picks
        // _HairColor (crimson), _ExtraColor (steel) and _EyeColor (dark leather). The hood and cape
        // carry no property block in the game. Skin is the live character's _BaseColor.
        foreach (var piece in new[] { "CHest_GEO", "Legs_GEO", "Gloves_GEO2", "Boots_GEO2" })
        {
            testplayer.SetColor(piece, "_SkinColor", new Color(0.515f, 0.577f, 0.670f));
            testplayer.SetColor(piece, "_HairColor", new Color(0.314f, 0.012f, 0.078f));
            testplayer.SetColor(piece, "_ExtraColor", new Color(0.302f, 0.343f, 0.425f));
            testplayer.SetColor(piece, "_EyeColor", new Color(0.078f, 0.043f, 0.027f));
        }
        testplayer.SetColor("Body_GEO", "_BaseColor", new Color(0.497f, 0.672f, 0.701f));
    }

    private static void Set(FilmContext ctx, Puppet p, string material, params string[] renderers)
    {
        var m = ctx.Models.FindLoadedMaterial(material);
        if (m == null)
        {
            ctx.Log.LogWarning("Film: material " + material + " is not loaded");
            return;
        }
        foreach (var r in renderers)
        {
            if (!p.SetMaterial(r, m)) { ctx.Log.LogWarning("Film: TestPlayer has no renderer " + r); continue; }
        }
    }
}

/// <summary>
/// The set, laid out as the director drew it (2026-09-28): a U-shaped desk at the top of the nave
/// carpet with its opening toward the pews, two office chairs inside it facing the altar, a big
/// screen where the podium stood, and server racks down the south side (OpenAI, Anthropic, Grok,
/// Suno). The podium, the candles, the candle stands and the banisters around the chancel are hidden, and so are Raziel and the player models
/// (the camera operator and a logged-out character). Set-local +Z faces the altar (world +X); +X is the south aisle.
/// </summary>
internal static class SetDressing
{
    internal static string SetGlb => System.IO.Path.Combine(FilmPaths.Root, "set", "film_set.glb");
    internal static readonly Vector3 SetOrigin = new(-1639f, 5f, -697.5f);
    internal static readonly Quaternion SetFacing = Quaternion.Euler(0f, 90f, 0f);
    internal static readonly Vector3 TestPlayerChair = new(0.55f, 0f, 0.35f);
    internal static readonly Vector3 DraculaChair = new(-0.6f, 0f, 0.35f);
    private const float SeatTop = 0.60f;

    internal static Transform Build(FilmContext ctx)
    {
        if (ctx.Set == null)
        {
            ctx.Set = ctx.Models.ImportGlb(SetGlb, "FilmSet");
            ctx.Set.Root.transform.SetPositionAndRotation(SetOrigin, SetFacing);
            ctx.Hides.Add(ctx.Models.HideScenery(new Vector3(-1633.5f, 4f, -710f), new Vector3(-1627f, 8f, -685f), "Church03_Podium", "Church_Candleholder01"));
            // The banisters and standing candle stands around the chancel (breakable props; the
            // director marked them for removal, 2026-09-29).
            ctx.Hides.Add(ctx.Models.HideScenery(new Vector3(-1641f, 4f, -709f), new Vector3(-1631.5f, 8f, -686f), "Church03_Fence0", "Church_Candleholder02"));
            ctx.Hides.Add(ctx.Models.HideUnitModels("HYB_NPCNecromancer_MilitiaBishop"));
            // The camera operator (the local player) and the logged-out TestPlayer character lying in the
            // nave are real players; keep them out of frame on this client only.
            ctx.Hides.Add(ctx.Models.HideUnitModels("HYB_VampireMale_Prefab"));
            ctx.Hides.Add(ctx.Models.HideUnitModels("HYB_VampireFemale_Prefab"));
            foreach (var h in ctx.Hides)
                ctx.Log.LogInfo("Film: hid " + h.Hidden + " (" + string.Join(", ", h.Names) + ")" + (h.SkippedStatic > 0 ? ", " + h.SkippedStatic + " static pieces stay" : ""));
        }
        return ctx.Set.Root.transform;
    }

    /// <summary>
    /// Re-imports the set when <see cref="SetGlb"/> changes on disk, so the set can be reworked in
    /// Blender and seen in the game without reloading the plugin. Checks every 2 s.
    /// </summary>
    internal static void WatchSetFile(FilmContext ctx)
    {
        DateTime stamp = System.IO.File.Exists(SetGlb) ? System.IO.File.GetLastWriteTimeUtc(SetGlb) : DateTime.MinValue;
        void Check()
        {
            try
            {
                var now = System.IO.File.Exists(SetGlb) ? System.IO.File.GetLastWriteTimeUtc(SetGlb) : DateTime.MinValue;
                if (now != stamp && ctx.Set != null)
                {
                    stamp = now;
                    var fresh = ctx.Models.ImportGlb(SetGlb, "FilmSet");
                    fresh.Root.transform.SetPositionAndRotation(SetOrigin, SetFacing);
                    ctx.Set.Dispose();
                    ctx.Set = fresh;
                    ScreenPlayer.Detach();
                    ctx.Log.LogInfo("Film: set re-imported from " + SetGlb);
                }
            }
            catch (Exception e) { ctx.Log.LogWarning("Film: set re-import failed: " + e.Message); }
            Progenitor.Dev.MainThread.PostDelayed(2.0, Check, ctx.Owner);
        }
        Progenitor.Dev.MainThread.PostDelayed(2.0, Check, ctx.Owner);
    }

    /// <summary>Puts a puppet in a chair facing the screen, seated, with its pelvis on the cushion.</summary>
    internal static void Seat(Puppet p, Transform set, Vector3 chairLocal)
    {
        var root = p.Root.transform;
        root.SetPositionAndRotation(set.TransformPoint(chairLocal), set.rotation);
        TestPose.Seated(p);
        if (p.Has("Pelvis_JNT"))
        {
            float pelvis = p.Bone("Pelvis_JNT").position.y - root.position.y;
            root.position += Vector3.up * (SeatTop + 0.06f - pelvis);
        }
    }
}

/// <summary>
/// Plays the take exported from the Blender film on the puppets, looping, in real time at the
/// take's frame rate. The take is in the Blender set's frame; <see cref="SetOffset"/> moves it onto
/// this set so the Blender chairs land on these chairs. Dracula's in-game scale differs from the
/// take's, so his root is shifted to keep his pelvis at the take's seat height.
/// </summary>
internal static class TakePlayer
{
    internal static string TakeDir => System.IO.Path.Combine(FilmPaths.Root, "take");
    internal static readonly Vector3 SetOffset = new(0f, 0f, 0.23f);

    /// <summary>
    /// Per-actor height added to the take. The desk here is 5 cm taller than the Blender film's, which
    /// left TestPlayer's hands just under its top; she and her chair go up 10 cm (6 still clipped once
    /// her hands were turned palm-down). Dracula, scaled larger
    /// than in the film, clears it as he is.
    /// </summary>
    internal static float Lift(string actor) => actor == "TestPlayer" ? 0.10f : 0f;

    /// <summary>
    /// Per-actor distance back from the desk. Dracula is scaled 0.62 here against 0.55 in the film,
    /// so his arms reach about 13 % further and his hands hung over the far edge; he and his chair
    /// sit 15 cm further back.
    /// </summary>
    internal static float Back(string actor) => actor == "Dracula" ? 0.15f : 0f;

    internal static void Start(FilmContext ctx, Transform set)
    {
        if (!System.IO.File.Exists(System.IO.Path.Combine(TakeDir, "take.json"))) return;
        ctx.Take = Progenitor.Film.TakeFile.Load(TakeDir);
        ctx.Log.LogInfo("Film: take " + ctx.Take.Frames + " frames at " + ctx.Take.Fps + " fps from " + TakeDir);
        var started = DateTime.UtcNow;
        void Tick()
        {
            try
            {
                var take = ctx.Take;
                if (take == null) return;
                if (ctx.Capture != null) { Progenitor.Dev.MainThread.PostDelayed(0.5, Tick, ctx.Owner); return; }
                int frame = take.FrameAt((DateTime.UtcNow - started).TotalSeconds % take.DurationSeconds);
                Pose(take, frame, "TestPlayer", ctx.TestPlayerPuppet, set);
                Pose(take, frame, "Dracula", ctx.DraculaPuppet, set);
                ScreenPlayer.Show(ctx, frame);
            }
            catch (Exception e) { ctx.Log.LogWarning("Film: take frame threw: " + e.Message); return; }
            Progenitor.Dev.MainThread.PostDelayed(1.0 / 48, Tick, ctx.Owner);
        }
        Tick();
    }

    internal static void Pose(Progenitor.Film.TakeFile take, int frame, string actor, Puppet? p, Transform set)
    {
        int a = take.ActorIndex(actor);
        if (a < 0 || p == null) return;
        var r = take.ActorRoot(frame, a);
        var root = p.Root.transform;
        root.rotation = set.rotation * new Quaternion(r.Qx, r.Qy, r.Qz, r.Qw);
        var (px, py, pz) = take.Pelvis(frame, a);
        float gameScale = root.localScale.x;
        var pos = set.TransformPoint(new Vector3(r.X, r.Y, r.Z) + SetOffset + Vector3.up * Lift(actor) + Vector3.back * Back(actor));
        pos.y += (r.Scale - gameScale) * py;
        root.position = pos;
        var bones = take.Actors[a].Bones;
        var index = new System.Collections.Generic.Dictionary<string, int>(bones.Count);
        for (int i = 0; i < bones.Count; i++) index[bones[i]] = i;
        p.ApplyModelPose(name =>
        {
            if (!index.TryGetValue(name, out int i)) return null;
            var (x, y, z, w) = take.BoneDelta(frame, a, i);
            return new Quaternion(x, y, z, w);
        }, new Vector3(px, py, pz));
        Arms.Settle(p, set);
        Gaze.Retarget(p, set);
    }
}

/// <summary>
/// Captures shots on request: when <see cref="RequestFile"/> appears it is read, deleted, and a
/// lock-step capture starts that stages each take frame (puppets and, with <c>"camera": "take"</c>,
/// the take's camera mapped onto this set). The real-time loop pauses while a capture runs.
/// Request: <c>{ "first": 0, "frames": 120, "out": "C:\film\shot.mp4", "width": 1920, "height": 1080,
/// "camera": "take" }</c>, or a fixed camera <c>"camera": [x, y, z, lookX, lookY, lookZ, vfov]</c> in world space.
/// </summary>
internal static class Shoot
{
    internal static string RequestFile => System.IO.Path.Combine(FilmPaths.Root, "capture.json");

    internal static void Watch(FilmContext ctx, Transform set)
    {
        void Check()
        {
            try
            {
                if (ctx.Capture == null && System.IO.File.Exists(RequestFile))
                {
                    string json = System.IO.File.ReadAllText(RequestFile);
                    System.IO.File.Delete(RequestFile);
                    Start(ctx, set, json);
                }
            }
            catch (Exception e) { ctx.Log.LogWarning("Film: capture request failed: " + e.Message); }
            Progenitor.Dev.MainThread.PostDelayed(1.0, Check, ctx.Owner);
        }
        Progenitor.Dev.MainThread.PostDelayed(1.0, Check, ctx.Owner);
    }

    private static void Start(FilmContext ctx, Transform set, string json)
    {
        var take = ctx.Take ?? throw new InvalidOperationException("no take loaded");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var r = doc.RootElement;
        int first = r.TryGetProperty("first", out var f) ? f.GetInt32() : 0;
        int frames = r.TryGetProperty("frames", out var n) ? n.GetInt32() : take.Frames - first;
        int width = r.TryGetProperty("width", out var w) ? w.GetInt32() : 1920;
        int height = r.TryGetProperty("height", out var h) ? h.GetInt32() : 1080;
        string output = r.GetProperty("out").GetString()!;
        frames = Math.Min(frames, take.Frames - first);
        float[]? fixedCam = null;
        if (r.TryGetProperty("camera", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            fixedCam = new float[7];
            int i = 0;
            foreach (var x in c.EnumerateArray()) if (i < 7) fixedCam[i++] = x.GetSingle();
        }

        if (ctx.Camera == null || ctx.Camera.Width != width || ctx.Camera.Height != height)
        {
            ctx.Camera?.Dispose();
            ctx.Camera = ctx.Film.CreateCamera(width, height);
        }
        var cam = ctx.Camera;
        var plan = new Progenitor.Film.CapturePlan(output, take.Fps, width, height, frames);
        ctx.Capture = ctx.Film.Capture(cam, plan, frame =>
        {
            int tf = first + frame;
            TakePlayer.Pose(take, tf, "TestPlayer", ctx.TestPlayerPuppet, set);
            TakePlayer.Pose(take, tf, "Dracula", ctx.DraculaPuppet, set);
            ScreenPlayer.Show(ctx, tf);
            if (fixedCam != null)
                cam.LookAt(new Vector3(fixedCam[0], fixedCam[1], fixedCam[2]), new Vector3(fixedCam[3], fixedCam[4], fixedCam[5]), fixedCam[6]);
            else if (take.HasCamera)
            {
                var tc = take.Camera(tf);
                var pos = set.TransformPoint(new Vector3(tc.Position.X, tc.Position.Y, tc.Position.Z) + TakePlayer.SetOffset);
                cam.Set(pos, set.rotation * new Vector3(tc.Forward.X, tc.Forward.Y, tc.Forward.Z), set.rotation * new Vector3(tc.Up.X, tc.Up.Y, tc.Up.Z), tc.VerticalFov);
            }
        });
        ctx.Capture.Finished += done =>
        {
            ctx.Log.LogInfo("Film: shot done, " + done.Written + " frames at " + done.RealFps.ToString("0.0") + " real fps" + (done.Error != null ? ", error: " + done.Error : ""));
            ctx.Capture = null;
        };
    }
}

/// <summary>
/// Camera tools for scouting shots from the player's own view: the top-down camera's zoom and pitch
/// limits widened (0.8-40 m, about 3-86 degrees), F7 toggles the game's free-flying camera, and F8
/// appends the current view (world and set-local position, look direction, vertical FOV) to
/// <see cref="MarkedShots"/> so a framing found by hand can become a film shot.
/// </summary>
internal static class CameraTools
{
    internal static string MarkedShots => System.IO.Path.Combine(FilmPaths.Root, "marked_shots.jsonl");
    // TopdownCamera.StandardZoomSettings at 0x60 and TopdownCameraState.ZoomSettings at 0x94 (client
    // metadata): MinZoom, MaxZoom, MaxPitch, MinPitch (radians). The game shipped 3, 14,
    // 1.012, 0.663 (live 2026-09-29).
    private const float MinZoom = 0.8f, MaxZoom = 40f, MaxPitch = 1.5f, MinPitch = 0.05f;
    private static bool _free;

    internal static void Start(FilmContext ctx)
    {
        ctx.Keybinds.Register("ToggleFreeCamera", new Progenitor.Ui.KeyChord(Progenitor.Ui.UiKey.F7), "Film: toggle the free-flying camera.", () =>
        {
            _free = !_free;
            if (_free) ((PluginContext)ctx).Camera.SetMode(Progenitor.Cameras.CameraMode.Free);
            else ((PluginContext)ctx).Camera.Reset();
            ctx.Log.LogInfo("Film: free camera " + (_free ? "on" : "off"));
        }, allowWhileCaptured: true);
        ctx.Keybinds.Register("MarkShot", new Progenitor.Ui.KeyChord(Progenitor.Ui.UiKey.F8), "Film: save the current view as a marked shot.", () => Mark(ctx), allowWhileCaptured: true);
        void Widen()
        {
            try { WidenLimits(); } catch (Exception e) { ctx.Log.LogWarning("Film: camera limits: " + e.Message); }
            Progenitor.Dev.MainThread.PostDelayed(1.0, Widen, ctx.Owner);
        }
        Widen();
    }

    private static unsafe void WidenLimits()
    {
        var world = Progenitor.Worlds.Client;
        if (world == null || !world.IsCreated) return;
        var em = world.EntityManager;
        var stateType = Progenitor.Ecs.ComponentIndex.Of<ProjectM.TopdownCameraState>(Unity.Entities.ComponentType.AccessMode.ReadWrite);
        var camType = Progenitor.Ecs.ComponentIndex.Of<ProjectM.TopdownCamera>(Unity.Entities.ComponentType.AccessMode.ReadWrite);
        var q = em.CreateEntityQuery(new Unity.Entities.EntityQueryDesc { All = new[] { stateType } });
        var all = q.ToEntityArray(Unity.Collections.Allocator.TempJob);
        try
        {
            foreach (var e in all)
            {
                Set((float*)((byte*)em.GetComponentDataRawRW(e, stateType.TypeIndex) + 0x94));
                if (em.HasComponent(e, camType)) Set((float*)((byte*)em.GetComponentDataRawRW(e, camType.TypeIndex) + 0x60));
            }
        }
        finally { all.Dispose(); q.Dispose(); }
    }

    private static unsafe void Set(float* z)
    {
        if (z == null) return;
        z[0] = MinZoom; z[1] = MaxZoom; z[2] = MaxPitch; z[3] = MinPitch;
    }

    private static void Mark(FilmContext ctx)
    {
        var cam = Camera.main;
        if (cam == null) return;
        var t = cam.transform;
        var set = ctx.Set?.Root.transform;
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        string V(Vector3 v) => "[" + v.x.ToString("0.###", ic) + "," + v.y.ToString("0.###", ic) + "," + v.z.ToString("0.###", ic) + "]";
        string line = "{\"time\":\"" + DateTime.Now.ToString("s", ic) + "\",\"position\":" + V(t.position) + ",\"forward\":" + V(t.forward) + ",\"up\":" + V(t.up)
                      + ",\"vfov\":" + cam.fieldOfView.ToString("0.##", ic)
                      + (set != null ? ",\"setPosition\":" + V(set.InverseTransformPoint(t.position)) + ",\"setForward\":" + V(set.InverseTransformDirection(t.forward)) : "") + "}";
        System.IO.File.AppendAllText(MarkedShots, line + Environment.NewLine);
        ctx.Log.LogInfo("Film: marked shot " + line);
    }
}

/// <summary>
/// Plays the film's screen video (the necro reel, then the forum page) on the big screen, one
/// pre-extracted frame per take frame (<c>ffmpeg -i screen2.mp4 -vf scale=1280:720 s_%05d.jpg</c>,
/// frame numbers from 0 like the take). The screen surface gets an unlit material so the picture
/// is not shaded by the church's lights.
/// </summary>
internal static class ScreenPlayer
{
    internal static string FramesDir => System.IO.Path.Combine(FilmPaths.Root, "screen_frames");
    private const float Brightness = 0.85f;
    private static Texture2D? _tex;
    private static Material? _mat;
    private static Renderer? _surface;
    private static int _shown = -1;

    internal static void Detach()
    {
        _surface = null;
        _shown = -1;
    }

    internal static void Show(FilmContext ctx, int frame)
    {
        if (ctx.Set == null) return;
        if (_surface == null)
        {
            var go = ctx.Set.Find("ScreenSurface");
            if (go == null) return;
            _surface = go.GetComponent<Renderer>();
            _tex ??= new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "film-screen" };
            if (_mat == null)
            {
                _mat = new Material(Shader.Find("HDRP/Unlit")) { name = "film-screen" };
                _mat.SetColor("_UnlitColor", new Color(Brightness, Brightness, Brightness, 1f));
                _mat.SetTexture("_UnlitColorMap", _tex);
            }
            _surface.sharedMaterial = _mat;
            _shown = -1;
        }
        if (frame == _shown) return;
        string path = System.IO.Path.Combine(FramesDir, "s_" + frame.ToString("00000") + ".jpg");
        if (!System.IO.File.Exists(path)) return;
        ImageConversion.LoadImage(_tex!, System.IO.File.ReadAllBytes(path), false);
        _shown = frame;
    }
}

/// <summary>
/// In the Blender film the characters watch a monitor on the desk; here the screen is big and
/// 5.5 m away. When a head points near where that monitor was (within <see cref="Cone"/> degrees),
/// the head and neck turn by the same rotation that carries the old monitor direction onto the big
/// screen, fading out as the head turns away, so looks at each other are left alone.
/// </summary>
internal static class Gaze
{
    /// <summary>The Blender monitor's center, in this set's coordinates.</summary>
    private static readonly Vector3 OldMonitor = new(0f, 1.2f, 1.15f);
    /// <summary>The big screen's center, in this set's coordinates.</summary>
    private static readonly Vector3 Screen = new(0f, 0.47f + 1.35f + 3.375f / 2f, 5.5f);
    private const float Cone = 35f;
    private const float NeckShare = 0.4f;

    internal static void Retarget(Puppet p, Transform set)
    {
        if (!p.Has("Head_JNT")) return;
        var head = p.Bone("Head_JNT");
        var root = p.Root.transform;
        // A direction fixed to the head that points forward at bind: root * delta * (0,0,1).
        var bind = p.BindModelRotation("Head_JNT");
        if (bind == null) return;
        var delta = Quaternion.Inverse(root.rotation) * head.rotation * Quaternion.Inverse(bind.Value);
        var look = root.rotation * (delta * Vector3.forward);
        var toOld = set.TransformPoint(OldMonitor) - head.position;
        float angle = Vector3.Angle(look, toOld);
        if (angle > Cone) return;
        float weight = 1f - angle / Cone;
        weight = weight * weight * (3f - 2f * weight);
        var toNew = set.TransformPoint(Screen) - head.position;
        var turn = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(toOld, toNew), weight);
        string neck = p.Has("Neck_JNT") ? "Neck_JNT" : p.Has("NeckHead02_JNT") ? "NeckHead02_JNT" : "";
        if (neck.Length > 0)
        {
            var nb = p.Bone(neck);
            var part = Quaternion.Slerp(Quaternion.identity, turn, NeckShare);
            nb.rotation = part * nb.rotation;
            head.rotation = Quaternion.Slerp(Quaternion.identity, turn, 1f - NeckShare) * head.rotation;
        }
        else head.rotation = turn * head.rotation;
    }
}

/// <summary>
/// Corrections to the take's arms for this taller desk, applied after each pose. The hand keeps the
/// take's position; (1) the upper arm swings about the shoulder-to-wrist line so the elbow hangs down
/// and a little out and back, as a seated typist's does (the take's elbows sat high and tucked,
/// director 2026-09-29); (2) a hand resting low near the desk rolls with its forearm until the palm
/// faces down (the take often rested them palm up). Only hands resting at the keyboard are touched:
/// applied to a hand held at the chest the same corrections curled the wrists (director, 2026-09-29).
/// </summary>
internal static class Arms
{
    private const float DeskTop = 0.85f;
    private const float ElbowWeight = 0.8f;

    internal static void Settle(Puppet p, Transform set)
    {
        Side(p, set, "Left", -1f);
        Side(p, set, "Right", 1f);
    }

    private static void Side(Puppet p, Transform set, string side, float outward)
    {
        string sh = side + "_Shoulder_JNT", el = side + "_Elbow_JNT", wr = side + "_Wrist_JNT";
        if (!p.Has(sh) || !p.Has(el) || !p.Has(wr)) return;
        var root = p.Root.transform;
        var S = p.Bone(sh); var E = p.Bone(el); var W = p.Bone(wr);

        // Only a hand resting at this character's keyboard is corrected; gestures (hands at the chest,
        // raised, pointing) play as animated. The keyboard is in front of the chair, 0.72 m out.
        var local = set.InverseTransformPoint(W.position);
        var chair = set.InverseTransformPoint(root.position);
        bool atKeyboard = Mathf.Abs(local.x - chair.x) < 0.45f && local.z > 0.5f && local.y < DeskTop + 0.22f;
        if (!atKeyboard) return;

        // (1) elbow direction
        var handRot = W.rotation;
        var axis = (W.position - S.position).normalized;
        var pole = root.TransformDirection(new Vector3(0.35f * outward, -1f, -0.35f)).normalized;
        var ePerp = Vector3.ProjectOnPlane(E.position - S.position, axis);
        var pPerp = Vector3.ProjectOnPlane(pole, axis);
        if (ePerp.sqrMagnitude > 1e-6f && pPerp.sqrMagnitude > 1e-6f)
        {
            float swing = Vector3.SignedAngle(ePerp, pPerp, axis) * ElbowWeight;
            S.rotation = Quaternion.AngleAxis(swing, axis) * S.rotation;
            W.rotation = handRot;
        }

        // (2) palm down when resting near the desk
        float aboveDesk = W.position.y - set.TransformPoint(new Vector3(0f, DeskTop, 0f)).y;
        if (aboveDesk > 0.22f) return;
        var bind = p.BindModelRotation(wr);
        if (bind == null) return;
        // The palm faces model-down in the bind pose; carry that direction with the hand.
        var palmLocal = Quaternion.Inverse(bind.Value) * Vector3.down;
        var palm = W.rotation * palmLocal;
        var forearm = (W.position - E.position).normalized;
        var palmPerp = Vector3.ProjectOnPlane(palm, forearm);
        var downPerp = Vector3.ProjectOnPlane(Vector3.down, forearm);
        if (palmPerp.sqrMagnitude < 1e-6f || downPerp.sqrMagnitude < 1e-6f) return;
        float roll = Vector3.SignedAngle(palmPerp, downPerp, forearm);
        float weight = Mathf.Clamp01((0.22f - aboveDesk) / 0.12f);
        E.rotation = Quaternion.AngleAxis(roll * weight, forearm) * E.rotation;
    }
}

/// <summary>Scales a puppet so its head sits at a multiple of the player's standing head height.</summary>
internal static class Scale
{
    /// <summary>The male player model's head height standing at rest, measured live 2026-09-28.</summary>
    internal const float PlayerHeadHeight = 1.72f;

    internal static void ToPlayerHeight(FilmContext ctx, Puppet puppet, float factor)
    {
        if (!puppet.Has("Head_JNT")) return;
        float puppetHeight = puppet.Bone("Head_JNT").position.y - puppet.Root.transform.position.y;
        if (puppetHeight <= 0.1f) return;
        float scale = factor * PlayerHeadHeight / puppetHeight;
        puppet.Root.transform.localScale = Vector3.one * scale;
        ctx.Log.LogInfo("Film: " + puppet.Root.name + " head " + puppetHeight.ToString("0.00") + " m at rest, scale " + scale.ToString("0.00"));
    }
}

internal static partial class Stage
{
    /// <summary>The local player's hybrid model root (a child of <c>HybridModels</c>), or null.</summary>
    internal static Transform? LocalCharacterModel()
    {
        var root = GameObject.Find("HybridModels");
        if (root == null) return null;
        for (int i = 0; i < root.transform.childCount; i++)
        {
            var child = root.transform.GetChild(i);
            if (child.name.StartsWith("HYB_VampireMale", StringComparison.Ordinal)) return child;
        }
        return null;
    }
}
