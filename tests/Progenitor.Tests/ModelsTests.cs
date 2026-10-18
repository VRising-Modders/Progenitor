using System;
using System.Collections.Generic;
using Progenitor.Models;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of <c>ctx.Models</c>: the section id and its in-memory Hash128 words, the
/// name table, the load/hold state machine against a fake streaming runtime, and which components
/// a copy has switched off. What only a client can show (the section streaming in, the prefab
/// registering, the copy rendering) is listed in docs/modules.md, "Models".
/// </summary>
public class ModelsTests
{
    private const string Scene = "ddfc6a4db4009e2448b8f8b6f0965b8f";

    // ---- ModelSection ------------------------------------------------------------------------

    [Fact]
    public void Catalog_spelling_maps_to_the_live_hash_words()
    {
        // Read live 2026-09-28 from SceneSectionData.SceneGUID of this scene's section entities.
        new ModelSection(Scene, 373).ToHashWords(out uint a, out uint b, out uint c, out uint d);
        Assert.Equal(0xd4a6cfddu, a);
        Assert.Equal(0x42e9004bu, b);
        Assert.Equal(0x6b8f8b84u, c);
        Assert.Equal(0xf8b5690fu, d);
        Assert.Equal(Scene, ModelSection.SceneGuidFromHashWords(a, b, c, d));
    }

    [Theory]
    [InlineData("ddfc6a4db4009e2448b8f8b6f0965b8f:373", 373)]
    [InlineData("DDFC6A4DB4009E2448B8F8B6F0965B8F:0", 0)]
    public void Parse_reads_the_catalog_object_spelling(string text, int index)
    {
        var s = ModelSection.Parse(text);
        Assert.Equal(Scene, s.SceneGuid);
        Assert.Equal(index, s.Index);
        Assert.Equal(Scene + ":" + index, s.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ddfc6a4d:373")]
    [InlineData("ddfc6a4db4009e2448b8f8b6f0965b8f")]
    [InlineData("ddfc6a4db4009e2448b8f8b6f0965b8f:-1")]
    [InlineData("ddfc6a4db4009e2448b8f8b6f0965b8f:x")]
    [InlineData("zzfc6a4db4009e2448b8f8b6f0965b8f:1")]
    public void Parse_refuses_anything_else(string? text)
    {
        Assert.False(ModelSection.TryParse(text, out _));
    }

    [Fact]
    public void Sections_compare_by_scene_and_index()
    {
        Assert.Equal(new ModelSection(Scene, 1), ModelSection.Parse(Scene.ToUpperInvariant() + ":1"));
        Assert.NotEqual(new ModelSection(Scene, 1), new ModelSection(Scene, 2));
    }

    // ---- ModelCatalog ------------------------------------------------------------------------

    [Fact]
    public void Catalog_reads_the_generator_output()
    {
        var cat = ModelCatalog.Parse("{\"catalogSha256\":\"abc\",\"models\":[" +
                                     "{\"name\":\"HYB_NPCDracula_Evolved\",\"section\":\"" + Scene + ":373\",\"archive\":\"7111a83adee39efe57f05830d61ce3ab\"}," +
                                     "{\"name\":\"HYB_NPCDracula_Standard\",\"section\":\"" + Scene + ":374\"}]}");
        Assert.Equal("abc", cat.BuildStamp);
        Assert.Equal(2, cat.Count);
        Assert.True(cat.TryFind("HYB_NPCDracula_Evolved", out var s));
        Assert.Equal(373, s.Index);
        Assert.False(cat.TryFind("HYB_nope", out _));
    }

    [Fact]
    public void Catalog_accepts_a_bare_array_and_keeps_the_first_duplicate()
    {
        var cat = ModelCatalog.Parse("[{\"name\":\"A\",\"section\":\"" + Scene + ":1\"},{\"name\":\"A\",\"section\":\"" + Scene + ":2\"}]");
        Assert.True(cat.TryFind("A", out var s));
        Assert.Equal(1, s.Index);
    }

    [Theory]
    [InlineData("{\"models\":[{\"name\":\"A\",\"section\":\"bad\"}]}")]
    [InlineData("{\"models\":[{\"name\":\"A\"}]}")]
    [InlineData("{\"rows\":[]}")]
    [InlineData("42")]
    public void Catalog_fails_loudly_on_a_bad_table(string json)
    {
        Assert.Throws<FormatException>(() => ModelCatalog.Parse(json));
    }

    // ---- ModelLoader -------------------------------------------------------------------------

    private sealed class FakeStreaming : IModelStreamingRuntime
    {
        public readonly HashSet<ModelSection> Sections = new();
        public readonly HashSet<ModelSection> Requested = new();
        public readonly HashSet<ModelSection> Loaded = new();
        public readonly Dictionary<ModelSection, int> LoadDelay = new();
        public readonly Dictionary<ModelSection, int> RegisterDelay = new();
        public readonly HashSet<string> GameUses = new();
        public readonly Dictionary<string, object> Prefabs = new();
        public int Requests, Unrequests;
        public bool World = true;
        public bool RefuseRequests;

        public bool HasClientWorld => World;

        public SectionProbe Probe(ModelSection s) =>
            Sections.Contains(s) ? new SectionProbe(true, Requested.Contains(s), Loaded.Contains(s)) : SectionProbe.Missing;

        public bool Request(ModelSection s)
        {
            if (RefuseRequests || !Sections.Contains(s)) return false;
            Requests++;
            Requested.Add(s);
            return true;
        }

        public bool Unrequest(ModelSection s)
        {
            Unrequests++;
            return Requested.Remove(s);
        }

        public object? FindPrefab(ModelSection s, string name)
        {
            if (RegisterDelay.TryGetValue(s, out int d) && d > 0) { RegisterDelay[s] = d - 1; return null; }
            // Resident prefabs are always loaded; the live runtime looks them up by name only.
            return (s.IsNone || Loaded.Contains(s)) && Prefabs.TryGetValue(name, out var p) ? p : null;
        }

        public bool GameUsesPrefab(string name) => GameUses.Contains(name);

        /// <summary>The engine's streaming: a requested section loads after its delay; an unrequested one unloads.</summary>
        public void Stream()
        {
            foreach (var s in Sections)
            {
                if (Requested.Contains(s))
                {
                    int d = LoadDelay.TryGetValue(s, out var v) ? v : 0;
                    if (d > 0) LoadDelay[s] = d - 1;
                    else Loaded.Add(s);
                }
                else Loaded.Remove(s);
            }
        }
    }

    private static readonly ModelSection Evolved = new(Scene, 373);
    private static readonly ModelSection Standard = new(Scene, 374);

    private static FakeStreaming Game()
    {
        var g = new FakeStreaming();
        g.Sections.Add(Evolved);
        g.Sections.Add(Standard);
        g.Prefabs["HYB_NPCDracula_Evolved"] = "evolved-prefab";
        g.Prefabs["HYB_NPCDracula_Standard"] = "standard-prefab";
        return g;
    }

    private static void Run(ModelLoader loader, FakeStreaming game, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            loader.Tick();
            game.Stream();
        }
    }

    [Fact]
    public void A_cold_section_is_requested_streams_in_and_becomes_Ready()
    {
        var game = Game();
        game.LoadDelay[Evolved] = 3;
        var loader = new ModelLoader(game);
        var load = loader.Acquire(Evolved, "HYB_NPCDracula_Evolved");
        Assert.Equal(ModelLoadState.Pending, load.State);
        Assert.Equal(0, game.Requests); // nothing touches the game until the first tick

        Run(loader, game, 6);

        Assert.Equal(ModelLoadState.Ready, load.State);
        Assert.Equal("evolved-prefab", load.Prefab);
        Assert.Equal(1, game.Requests);
        Assert.True(loader.IsRequestedByUs(Evolved));
    }

    [Fact]
    public void Assets_registered_a_few_frames_after_the_load_still_resolve()
    {
        var game = Game();
        game.RegisterDelay[Evolved] = 5;
        var loader = new ModelLoader(game);
        var load = loader.Acquire(Evolved, "HYB_NPCDracula_Evolved");
        Run(loader, game, 10);
        Assert.Equal(ModelLoadState.Ready, load.State);
    }

    [Fact]
    public void Release_of_our_request_unrequests_so_the_game_may_unload_it()
    {
        var game = Game();
        var loader = new ModelLoader(game);
        var load = loader.Acquire(Evolved, "HYB_NPCDracula_Evolved");
        Run(loader, game, 3);

        Assert.Equal(ModelReleaseResult.Unrequested, loader.Release(load));
        Assert.DoesNotContain(Evolved, game.Requested);
        Assert.Equal(ModelLoadState.Released, load.State);
        Assert.Null(load.Prefab);
        Assert.Equal(0, loader.HeldSections);
        Assert.Equal(ModelReleaseResult.AlreadyReleased, loader.Release(load));
    }

    [Fact]
    public void Two_loads_of_one_section_share_one_request()
    {
        var game = Game();
        var loader = new ModelLoader(game);
        var a = loader.Acquire(Evolved, "HYB_NPCDracula_Evolved");
        var b = loader.Acquire(Evolved, "HYB_NPCDracula_Evolved");
        Run(loader, game, 3);
        Assert.Equal(1, game.Requests);

        Assert.Equal(ModelReleaseResult.StillHeld, loader.Release(a));
        Assert.Contains(Evolved, game.Requested);
        Assert.Equal(ModelLoadState.Ready, b.State);
        Assert.Equal(ModelReleaseResult.Unrequested, loader.Release(b));
    }

    [Fact]
    public void A_section_the_game_already_requested_is_never_unrequested_by_us()
    {
        var game = Game();
        game.Requested.Add(Standard);
        game.Stream();
        var loader = new ModelLoader(game);
        var load = loader.Acquire(Standard, "HYB_NPCDracula_Standard");
        Run(loader, game, 2);
        Assert.Equal(ModelLoadState.Ready, load.State);
        Assert.Equal(0, game.Requests);

        Assert.Equal(ModelReleaseResult.NotOurRequest, loader.Release(load));
        Assert.Contains(Standard, game.Requested);
        Assert.Equal(0, game.Unrequests);
    }

    [Fact]
    public void Our_request_stays_when_a_game_unit_uses_the_prefab_at_release()
    {
        var game = Game();
        var loader = new ModelLoader(game);
        var load = loader.Acquire(Evolved, "HYB_NPCDracula_Evolved");
        Run(loader, game, 3);
        game.GameUses.Add("HYB_NPCDracula_Evolved");

        Assert.Equal(ModelReleaseResult.KeptForGame, loader.Release(load));
        Assert.Contains(Evolved, game.Requested);
    }

    [Fact]
    public void A_held_section_that_loses_its_request_is_requested_again()
    {
        var game = Game();
        var loader = new ModelLoader(game);
        var load = loader.Acquire(Evolved, "HYB_NPCDracula_Evolved");
        Run(loader, game, 3);
        Assert.Equal(ModelLoadState.Ready, load.State);

        // A game unit with this model came and went and took the request with it.
        game.Requested.Remove(Evolved);
        game.Stream();
        Run(loader, game, ModelLoader.WatchIntervalFrames + 3);

        Assert.Equal(ModelLoadState.Ready, load.State);
        Assert.Equal(2, game.Requests);
    }

    [Fact]
    public void Ready_loads_report_Reloading_while_their_section_comes_back()
    {
        var game = Game();
        var loader = new ModelLoader(game);
        var load = loader.Acquire(Evolved, "HYB_NPCDracula_Evolved");
        Run(loader, game, 3);
        game.Requested.Remove(Evolved);
        game.Stream();
        game.LoadDelay[Evolved] = 100;
        Run(loader, game, ModelLoader.WatchIntervalFrames + 2);

        Assert.Equal(ModelLoadState.Pending, load.State);
        Assert.Equal(ModelLoadReasons.Reloading, load.Reason);
        Assert.Null(load.Prefab);
    }

    [Fact]
    public void Presentation_is_invalidated_before_release_and_again_after_reload()
    {
        var game = Game();
        var loader = new ModelLoader(game);
        var load = loader.Acquire(Evolved, "HYB_NPCDracula_Evolved");
        Run(loader, game, 3);
        int invalidations = 0;
        bool wasReady = true;
        bool held = true;
        load.BeforeLeavingReady = () =>
        {
            invalidations++;
            wasReady &= load.State == ModelLoadState.Ready && load.Prefab != null;
            held &= loader.HeldSections == 1;
        };
        game.Requested.Remove(Evolved);
        game.Stream();
        Run(loader, game, ModelLoader.WatchIntervalFrames + 3);
        Assert.Equal(ModelLoadState.Ready, load.State);
        Assert.Equal(1, invalidations);
        loader.Release(load);
        Assert.Equal(2, invalidations);
        Assert.True(wasReady);
        Assert.True(held);
        Assert.Equal(0, loader.HeldSections);
    }

    [Fact]
    public void Broken_presentation_cleanup_cannot_strand_a_section_hold()
    {
        var game = Game();
        var loader = new ModelLoader(game);
        var load = loader.Acquire(Evolved, "HYB_NPCDracula_Evolved");
        Run(loader, game, 3);
        load.BeforeLeavingReady = () => throw new InvalidOperationException("destroy failed");
        Assert.Equal(ModelReleaseResult.Unrequested, loader.Release(load));
        Assert.Equal(ModelLoadState.Released, load.State);
        Assert.Equal(0, loader.HeldSections);
        Assert.DoesNotContain(Evolved, game.Requested);
    }

    [Fact]
    public void Unknown_section_fails_and_drops_its_hold()
    {
        var game = Game();
        var loader = new ModelLoader(game);
        var load = loader.Acquire(new ModelSection(Scene, 9999), "HYB_x");
        Run(loader, game, 1);
        Assert.Equal(ModelLoadState.Failed, load.State);
        Assert.Equal(ModelLoadReasons.SectionNotFound, load.Reason);
        Assert.Equal(0, loader.HeldSections);
        Assert.False(loader.Tick());
    }

    [Fact]
    public void A_refused_request_fails()
    {
        var game = Game();
        game.RefuseRequests = true;
        var loader = new ModelLoader(game);
        var load = loader.Acquire(Evolved, "HYB_NPCDracula_Evolved");
        Run(loader, game, 1);
        Assert.Equal(ModelLoadReasons.RequestRefused, load.Reason);
    }

    [Fact]
    public void A_section_that_never_loads_times_out_and_is_unrequested()
    {
        var game = Game();
        game.LoadDelay[Evolved] = int.MaxValue;
        var loader = new ModelLoader(game, timeoutFrames: 10);
        var load = loader.Acquire(Evolved, "HYB_NPCDracula_Evolved");
        Run(loader, game, 12);
        Assert.Equal(ModelLoadReasons.Timeout, load.Reason);
        Assert.DoesNotContain(Evolved, game.Requested);
    }

    [Fact]
    public void A_loaded_section_without_the_prefab_fails_after_the_grace_frames()
    {
        var game = Game();
        var loader = new ModelLoader(game, missingPrefabFrames: 4);
        var load = loader.Acquire(Evolved, "HYB_SomethingElse");
        Run(loader, game, 8);
        Assert.Equal(ModelLoadReasons.PrefabNotInSection, load.Reason);
    }

    [Fact]
    public void Without_a_client_world_loads_wait_then_fail()
    {
        var game = Game();
        game.World = false;
        var loader = new ModelLoader(game, timeoutFrames: 5);
        var load = loader.Acquire(Evolved, "HYB_NPCDracula_Evolved");
        Run(loader, game, 4);
        Assert.Equal(ModelLoadState.Pending, load.State);
        Run(loader, game, 3);
        Assert.Equal(ModelLoadReasons.NoClientWorld, load.Reason);
        Assert.Equal(0, game.Requests);
    }

    [Fact]
    public void ReleaseAll_releases_every_load_with_the_reason()
    {
        var game = Game();
        var loader = new ModelLoader(game);
        var a = loader.Acquire(Evolved, "HYB_NPCDracula_Evolved");
        var b = loader.Acquire(Standard, "HYB_NPCDracula_Standard");
        Run(loader, game, 3);
        loader.ReleaseAll(ModelLoadReasons.CoreUnloaded);
        Assert.Equal(ModelLoadState.Released, a.State);
        Assert.Equal(ModelLoadReasons.CoreUnloaded, b.Reason);
        Assert.Empty(game.Requested);
        Assert.Equal(0, loader.LiveLoads);
    }

    [Fact]
    public void A_resident_load_is_found_by_name_and_holds_no_section()
    {
        var game = Game();
        game.Prefabs["HYB_VampireFemale_Prefab"] = "female";
        var loader = new ModelLoader(game);
        var load = loader.Acquire(ModelSection.None, "HYB_VampireFemale_Prefab");
        Run(loader, game, 1);
        Assert.Equal(ModelLoadState.Ready, load.State);
        Assert.Equal("female", load.Prefab);
        Assert.Equal(0, loader.HeldSections);
        Assert.Equal(0, game.Requests);
        Assert.Equal(ModelReleaseResult.Resident, loader.Release(load));
        Assert.Equal(0, game.Unrequests);
    }

    [Fact]
    public void A_resident_load_of_an_unloaded_name_fails_after_the_grace_frames()
    {
        var game = Game();
        var loader = new ModelLoader(game, missingPrefabFrames: 3);
        var load = loader.Acquire(ModelSection.None, "HYB_NotLoaded");
        Run(loader, game, 5);
        Assert.Equal(ModelLoadReasons.PrefabNotLoaded, load.Reason);
    }

    [Fact]
    public void None_is_the_default_and_has_no_scene()
    {
        Assert.True(ModelSection.None.IsNone);
        Assert.False(new ModelSection(Scene, 0).IsNone);
        Assert.Equal("resident", ModelSection.None.ToString());
        Assert.Throws<InvalidOperationException>(() => ModelSection.None.ToHashWords(out _, out _, out _, out _));
    }

    // ---- PuppetBinding -----------------------------------------------------------------------

    [Fact]
    public void Equipment_bones_bind_by_name_and_missing_ones_are_listed_once()
    {
        var rig = new HashSet<string> { "Root_JNT", "Spine_JNT", "Left_Elbow_JNT" };
        var (mapped, missing) = PuppetBinding.Map(new[] { "Spine_JNT", "Cape01_JNT", "Left_Elbow_JNT", "Cape01_JNT", null, "" }, rig.Contains);
        Assert.Equal(new string?[] { "Spine_JNT", null, "Left_Elbow_JNT", null, null, null }, mapped);
        Assert.Equal(new[] { "Cape01_JNT", "<unnamed #4>", "<unnamed #5>" }, missing);
    }

    // ---- ModelInertPolicy --------------------------------------------------------------------

    [Theory]
    [InlineData("Animator", true)]
    [InlineData("LimbIK", true)]
    [InlineData("AimIK", true)]
    [InlineData("CCDIK", true)]
    [InlineData("HybridModelAnimationComponent", true)]
    [InlineData("HybridEquipmentUser", true)]
    [InlineData("MagicaBoneCloth", true)]
    [InlineData("FootstepAnimationEventReceiver", true)]
    [InlineData("VampireSecondaryAnimationHandler", true)]
    [InlineData("SkinnedMeshRenderAuthoring", true)]
    [InlineData("SkinnedMeshRenderer", false)]
    [InlineData("MeshRenderer", false)]
    [InlineData("Transform", false)]
    [InlineData("", false)]
    public void The_game_drivers_are_switched_off_and_renderers_are_not(string type, bool inert)
    {
        Assert.Equal(inert, ModelInertPolicy.IsInert(type));
    }
}
