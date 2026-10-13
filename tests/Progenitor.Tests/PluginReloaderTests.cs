using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using Progenitor.Dev;
using Progenitor.Effects;
using Progenitor.Reload;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Static-API Effects/UI/ServerBeam handles must be released by the plugin sweep
/// (R26 finding 6, R27 findings 4 and 25). <see cref="PluginStaticApiSweep.Run"/> is both the
/// game-free entry tests drive and the <b>only</b> thing
/// <c>PluginReloader.SweepRegistrations</c> calls for these three APIs.
/// </summary>
[Collection("PluginLogger")]
public class PluginReloaderTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly EffectsTests.FakeEffectRuntime _effects = new() { IsClient = true };
    private readonly UiTests.FakeUiRuntime _ui = new() { IsClient = true };
    private readonly ServerBeamTests.FakeServerBeamRuntime _beams = new() { IsServer = true };
    private readonly object _owner = new Owner("example.sweep.test");

    public PluginReloaderTests()
    {
        Plugin.Logger = _log;
        MainThread.ResetForTests();
        Assets.ResetForTests();
        UiApi.ResetForTests();
        ServerBeam.ResetForTests();
        _effects.Add(AssetKind.Image, "PassiveFrame_Normal");
        _beams.Add("ProfessorCoilBeam");
        Assets.SetRuntimeForTests(_effects);
        UiApi.SetRuntimeForTests(_ui);
        ServerBeam.SetRuntimeForTests(_beams);
    }

    public void Dispose()
    {
        Assets.ResetForTests();
        UiApi.ResetForTests();
        ServerBeam.ResetForTests();
        MainThread.ResetForTests();
        Plugin.Logger = null;
    }

    /// <summary>
    /// R27 finding 4: EV11-EV14 shipped <c>ServerBeam</c> as a new owner-keyed static API in
    /// the same batch that fixed R26 #6 and did not add it to the sweep, so a plugin that used
    /// <c>ServerBeam.Spawn(..., owner: this)</c> without <c>PluginContext</c> kept its ALC
    /// alive. R27 finding 25: <c>Run</c> hardcoded <c>FormatSwept(0, 0)</c>, so the counts were
    /// always a lie. Before EV15 the beam handle below survived the sweep and the log read
    /// "swept 0 effect(s) / 0 panel(s)".
    /// </summary>
    [Fact]
    public void Sweep_releases_static_api_effect_panel_and_server_beam_handles()
    {
        var effect = Assets.Borrow(AssetKind.Image, "PassiveFrame_Normal", _owner, "example.sweep.test");
        var panel = UiApi.Panel("Sample", _owner, "example.sweep.test");
        var beam = ServerBeam.Spawn(
            BeamPreset.Coil, default, new EffectVec3(1f, 0f, 0f), 8f, _owner, "example.sweep.test");
        Assert.False(effect.IsDisposed);
        Assert.False(panel.IsDisposed);
        Assert.False(beam.IsDisposed);

        var counts = PluginStaticApiSweep.Run(_owner);

        Assert.True(effect.IsDisposed);
        Assert.True(panel.IsDisposed);
        Assert.True(beam.IsDisposed);
        Assert.Equal(1, _beams.DestroyCalls);
        Assert.Equal(0, ServerBeam.LiveCountForTests);
        Assert.Contains(_effects.DestroyedRoots, r => r == "progenitor-effects-example.sweep.test");
        Assert.Contains(_ui.DestroyedRoots, r => r == "progenitor-ui-example.sweep.test");

        // The counts are the ones the RemoveAll methods returned, not zeros.
        Assert.Equal(1, counts.Effects);
        Assert.Equal(1, counts.Panels);
        Assert.Equal(1, counts.Beams);
        Assert.Contains(_log.Infos, l => l == PluginStaticApiSweep.FormatSwept(1, 1, 1));

        // Idempotent: a second sweep finds nothing and says so.
        var again = PluginStaticApiSweep.Run(_owner);
        Assert.Equal(0, again.Total);
    }

    /// <summary>
    /// R27 finding 25: <c>PluginStaticApiSweep</c> had no production caller at all — the live
    /// path duplicated two of the three <c>RemoveAll</c> calls inline and missed the third.
    /// This is a source grep and cannot see an early return above the call, but it can see the
    /// inline duplicates coming back.
    /// </summary>
    [Fact]
    public void PluginReloader_sweep_delegates_to_the_static_api_sweep()
    {
        var src = ReadReloaderFile();
        SourcePin.AssertUncommented(src, "PluginStaticApiSweep.Run(plugin)");
        Assert.DoesNotContain("Progenitor.Effects.Assets.RemoveAll(plugin)", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Progenitor.Ui.Ui.RemoveAll(plugin)", src, StringComparison.Ordinal);
        // Job FUNIT6 (R59 finding 4): the reloader's entry point is the assembly-wide sweep,
        // not the per-owner public RemoveAll, and it passes the plugin id rather than having
        // Units reflect for one.
        int units = src.IndexOf("Progenitor.Units.Units.SweepForUnload(plugin,", StringComparison.Ordinal);
        int sweep = src.IndexOf("PluginStaticApiSweep.Run(plugin)", StringComparison.Ordinal);
        Assert.True(units >= 0 && sweep > units);
    }

    /// <summary>
    /// Wearer before definition: <c>Buffs.SweepForUnload</c> must run before
    /// <c>PrefabRegistry.UnregisterAll</c>. Otherwise a plugin that applied through the static
    /// <c>Buffs</c> API has its buff definition retired while its instances are still on wearers.
    /// The tracked <c>ctx.Buffs</c> path is only ordered correctly by the context bag's LIFO undo
    /// when the plugin registered before it applied, so the guarantee is this order.
    /// </summary>
    [Fact]
    public void Buffs_are_swept_off_their_wearers_before_the_prefab_definitions_are_unregistered()
    {
        var src = ReadReloaderFile();
        SourcePin.AssertUncommented(src, "Progenitor.Buffs.Buffs.SweepForUnload(plugin, identity.Guid)");

        int buffs = src.IndexOf("Progenitor.Buffs.Buffs.SweepForUnload(plugin,", StringComparison.Ordinal);
        int unregister = src.IndexOf("PrefabRegistry.UnregisterAll(plugin)", StringComparison.Ordinal);

        Assert.True(buffs >= 0, "Buffs.SweepForUnload is not called by the reloader sweep at all");
        Assert.True(unregister >= 0, "PrefabRegistry.UnregisterAll is not called by the reloader sweep at all");
        Assert.True(
            buffs < unregister,
            "Buffs.SweepForUnload must precede PrefabRegistry.UnregisterAll: a buff has to come off "
            + "its wearer before its definition is retired. Found the buff sweep at " + buffs
            + " and the unregister at " + unregister + ".");
    }

    /// <summary>
    /// Job FEX9 (R60 finding 5), rewritten by job FREL1 (R61 finding 1). <c>MetadataHelper</c>
    /// returns null for a plugin BepInEx cannot describe, and the reloader used to dereference
    /// that result in five places. It now resolves the identity once through
    /// <see cref="PluginIdentity"/> and every consumer takes the resolved value, so no
    /// <c>GetMetadata(...)</c> dereference is left in the file at all.
    /// The reloader is not instantiable here (BepInEx + IL2CPP), so this is a source grep.
    /// </summary>
    [Fact]
    public void The_units_sweep_survives_a_plugin_with_no_readable_metadata()
    {
        var src = ReadReloaderFile();
        SourcePin.AssertUncommented(src, "Progenitor.Units.Units.SweepForUnload(plugin, identity.Guid)");
        Assert.DoesNotContain(
            "SweepForUnload(plugin, MetadataHelper.GetMetadata(plugin).GUID)", src, StringComparison.Ordinal);

        // Job FREL1: the identity is resolved once per unload and the file dereferences BepInEx's
        // metadata nowhere. Every `.GetMetadata(` occurrence must be inside IdentityOf's reader.
        int reads = 0;
        foreach (var line in src.Replace("\r\n", "\n").Split('\n'))
        {
            string code = line.Trim();
            if (code.StartsWith("//", StringComparison.Ordinal)) continue;
            if (!code.Contains("MetadataHelper.GetMetadata(", StringComparison.Ordinal)) continue;
            reads++;
            // Job FREL3: the projection moved into PluginIdentity.MetadataReader so a test can
            // compose it; the BepInEx-bound call itself is still this one line and no other.
            Assert.Contains("plugin == null ? null : MetadataHelper.GetMetadata(plugin)",
                code, StringComparison.Ordinal);
        }
        Assert.Equal(1, reads);
    }

    /// <summary>
    /// Job FREL1 (R61 finding 1): the trigger, not the symptom. BepInEx reads
    /// <c>[BepInPlugin]</c> with <c>inherit: false</c>; discovery used to take the generic
    /// overload's default (<c>inherit: true</c>) and so admitted plugins BepInEx could not
    /// describe. Discovery now agrees with BepInEx and refuses an inherited attribute with a line.
    /// </summary>
    [Fact]
    public void Discovery_reads_the_attribute_the_same_way_BepInEx_does()
    {
        var src = ReadReloaderFile();
        SourcePin.AssertUncommented(src, "private const bool DiscoveryInherit = false;");
        SourcePin.AssertUncommented(src, "if (type.GetCustomAttribute<BepInPlugin>(DiscoveryInherit) == null)");
        // The bare, inheriting read is gone from discovery; the only inherit:true read left is the
        // one that produces the refusal line.
        Assert.DoesNotContain("type.GetCustomAttribute<BepInPlugin>() == null", src, StringComparison.Ordinal);
        SourcePin.AssertUncommented(src, "if (type.GetCustomAttribute<BepInPlugin>(inherit: true) != null)");
    }

    /// <summary>
    /// Job FREL1: the Harmony unpatch-by-id is the dereference whose failure costs most (an NRE
    /// there leaves the unloading plugin's IL2CPP trampolines applied, the documented
    /// Core-generation pin), and the read in front of <c>plugin.Unload()</c> is the one that used
    /// to skip the plugin's own teardown. Both take the resolved identity now, the identity is
    /// resolved before the try that wraps <c>Unload()</c>, and the sweep call is itself guarded so
    /// a throw out of it cannot skip the steps below.
    /// </summary>
    [Fact]
    public void The_unload_sequence_resolves_the_identity_first_and_each_step_is_independent()
    {
        var src = ReadReloaderFile();
        SourcePin.AssertUncommented(src, "HarmonyFallback.UnpatchOwnerAndSuffixes(identity.Guid)");
        Assert.DoesNotContain("MetadataHelper.GetMetadata(plugin).GUID", src, StringComparison.Ordinal);
        Assert.DoesNotContain("MetadataHelper.GetMetadata(plugin).Name", src, StringComparison.Ordinal);

        int resolved = src.IndexOf("var identity = IdentityOf(plugin);", StringComparison.Ordinal);
        int unload = src.IndexOf("ok = plugin.Unload();", StringComparison.Ordinal);
        Assert.True(resolved >= 0 && unload > resolved, "the identity must be resolved before Unload() runs");

        // The sweep runs whatever Unload did, and a throw out of it does not eat the rest.
        SourcePin.AssertStatement(src, "try { SweepRegistrations(plugin, owned, identity); }");
    }

    /// <summary>
    /// The game-free half: the resolution order itself, driven with stand-in delegates so each
    /// rung can be made to answer, decline or throw on demand. First readable source wins, blanks
    /// are filled from the type name, and the result is never null and never has an empty name.
    /// What the two production readers actually answer for a real type is
    /// <see cref="The_production_readers_answer_over_real_types"/> — job FREL3 added it because
    /// this test, driving synthetic delegates, could not have noticed that the middle rung asks
    /// the first rung's question (R62 finding 1).
    /// </summary>
    [Fact]
    public void The_identity_prefers_metadata_then_the_attribute_then_the_type_name()
    {
        var metadata = new PluginIdentity("guid.meta", "Meta", "1.2.3", "metadata");
        var attribute = new PluginIdentity("guid.attr", "Attr", "0.1.0", "attribute");

        var both = PluginIdentity.Resolve("Some.Plugin.Type", new Func<PluginIdentity?>?[]
        {
            () => metadata,
            () => attribute,
        });
        Assert.Equal("guid.meta", both.Guid);
        Assert.Equal("metadata", both.Source);

        var noMetadata = PluginIdentity.Resolve("Some.Plugin.Type", new Func<PluginIdentity?>?[]
        {
            () => null,
            () => attribute,
        });
        Assert.Equal("guid.attr", noMetadata.Guid);
        Assert.Equal("Attr", noMetadata.Name);
        Assert.Equal("attribute", noMetadata.Source);

        var neither = PluginIdentity.Resolve("Some.Plugin.Type", new Func<PluginIdentity?>?[]
        {
            () => null,
            () => null,
        });
        Assert.Equal("", neither.Guid);
        Assert.False(neither.HasGuid);
        Assert.Equal("Some.Plugin.Type", neither.Name);
        Assert.Equal("type", neither.Source);

        // Never null, never an empty name, even with nothing at all to go on.
        var nothing = PluginIdentity.Resolve(null, null);
        Assert.Equal("plugin", nothing.Name);
        Assert.Equal("", nothing.Guid);

        // A source that answers with a name but no GUID is still an answer.
        var nameOnly = PluginIdentity.Resolve("Some.Plugin.Type", new Func<PluginIdentity?>?[]
        {
            () => new PluginIdentity("", "Named", "", "metadata"),
        });
        Assert.Equal("Named", nameOnly.Name);
        Assert.False(nameOnly.HasGuid);
    }

    /// <summary>
    /// A throwing reader is the residue R61 finding 1 named in FEX9's own fix: the fallback read
    /// sat inside the try that guarded the sweep, so an attribute type that failed to resolve
    /// skipped the sweep the fallback existed to protect. A throw is a warning and the next
    /// source, never an escaped exception and never a null identity.
    /// </summary>
    [Fact]
    public void A_throwing_metadata_read_still_yields_an_identity()
    {
        var warnings = new System.Collections.Generic.List<string>();

        var fellThrough = PluginIdentity.Resolve("Some.Plugin.Type", new Func<PluginIdentity?>?[]
        {
            () => throw new TypeLoadException("attribute type is missing"),
            () => new PluginIdentity("guid.attr", "Attr", "0.1.0", "attribute"),
        }, warnings.Add);
        Assert.Equal("guid.attr", fellThrough.Guid);
        Assert.Single(warnings);
        Assert.Contains("Some.Plugin.Type", warnings[0], StringComparison.Ordinal);

        warnings.Clear();
        var allThrew = PluginIdentity.Resolve("Some.Plugin.Type", new Func<PluginIdentity?>?[]
        {
            () => throw new TypeLoadException("one"),
            () => throw new TypeLoadException("two"),
        }, warnings.Add);
        Assert.Equal("Some.Plugin.Type", allThrew.Name);
        Assert.Equal("type", allThrew.Source);
        Assert.Equal(2, warnings.Count);

        // And with no sink at all, a throwing reader is still not an escaping one.
        var noSink = PluginIdentity.Resolve("Some.Plugin.Type", new Func<PluginIdentity?>?[]
        {
            () => throw new TypeLoadException("three"),
        });
        Assert.Equal("Some.Plugin.Type", noSink.Name);
    }

    /// <summary>
    /// Job FREL3 (R62 finding 1): the readers themselves, composed the way production composes
    /// them, over real types rather than over lambdas that answer whatever the test says.
    ///
    /// <para>Both reloaders build the chain as
    /// <c>PluginIdentity.MetadataReader(() =&gt; MetadataHelper.GetMetadata(plugin))</c> then
    /// <c>PluginIdentity.AttributeReader(type, DiscoveryInherit)</c>, and everything but that one
    /// BepInEx call lives in <see cref="PluginIdentity"/> — the projection off the attribute
    /// object and the attribute lookup — precisely so this test can drive the production code.
    /// The test project may not reference BepInEx (see the csproj header), so the metadata reader
    /// is fed the same shape of object BepInEx hands back and the plugin types carry the suite's
    /// stand-in <c>[BepInPlugin]</c>, which the by-name match accepts for the same reason
    /// <c>PrefabEditCompatibility</c> matches by name. What cannot be reproduced here is
    /// <c>BasePlugin</c> itself; nothing in the chain reads it — <c>IdentityOf</c> takes
    /// <c>plugin.GetType()</c> and stops there.</para>
    ///
    /// <para>The claim under test is the corrected one: the attribute reader can only answer when
    /// the metadata read <em>threw</em> or named a different attribute type, never when it
    /// returned null for want of the attribute — because then there is no attribute to find
    /// either, and the answer is the type name.</para>
    /// </summary>
    [Fact]
    public void The_production_readers_answer_over_real_types()
    {
        var warnings = new System.Collections.Generic.List<string>();

        // 1. BepInEx answers: its projection is the production one, and it wins the chain.
        var attributed = typeof(AttributedTestPlugin);
        var fromMetadata = PluginIdentity.Resolve(attributed.FullName, new Func<PluginIdentity?>?[]
        {
            PluginIdentity.MetadataReader(() => new BepInPluginAttribute("id.meta", "Meta", "3.2.1")),
            PluginIdentity.AttributeReader(attributed, inherit: false),
        }, warnings.Add);
        Assert.Equal("id.meta", fromMetadata.Guid);
        Assert.Equal("Meta", fromMetadata.Name);
        Assert.Equal("3.2.1", fromMetadata.Version);
        Assert.Equal("metadata", fromMetadata.Source);
        Assert.Empty(warnings);

        // 2. The metadata read THROWS. This, and only this, is what the second reader is for:
        //    it asks the same question and reads the attribute off the same type. The type here
        //    carries a [BepInPlugin] emitted into namespace BepInEx from a second assembly whose
        //    simple name is BepInEx.Core — the second-image case the by-name match exists for, and
        //    since jobs FREL4/FREL5 the only kind of attribute the reader accepts (R63 finding 4,
        //    R64 finding 3). The type itself lives in the mod's own assembly, which is where a
        //    plugin type lives and, since job FREL6, part of what makes this one accepted.
        var second = SecondImage.Value;
        var fromAttribute = PluginIdentity.Resolve(second.Plugin.FullName, new Func<PluginIdentity?>?[]
        {
            PluginIdentity.MetadataReader(() => throw new TypeLoadException("BepInEx.Core is a different image")),
            PluginIdentity.AttributeReader(second.Plugin, inherit: false, warnings.Add),
        }, warnings.Add);
        Assert.Equal("example.identity.test", fromAttribute.Guid);
        Assert.Equal("Identity Test", fromAttribute.Name);
        Assert.Equal("2.1.0", fromAttribute.Version);
        Assert.Equal("attribute", fromAttribute.Source);
        Assert.Single(warnings);
        Assert.Contains("threw", warnings[0], StringComparison.Ordinal);

        // 3. No attribute at all: BOTH readers decline, because they ask one question. The
        //    honest fallback — the only rung that is a different question — is the type name.
        warnings.Clear();
        var bare = typeof(UnattributedTestPlugin);
        var fromTypeName = PluginIdentity.Resolve(bare.FullName, new Func<PluginIdentity?>?[]
        {
            PluginIdentity.MetadataReader(() => null),
            PluginIdentity.AttributeReader(bare, inherit: false),
        }, warnings.Add);
        Assert.Equal("", fromTypeName.Guid);
        Assert.False(fromTypeName.HasGuid);
        Assert.Equal(bare.FullName, fromTypeName.Name);
        Assert.Equal("type", fromTypeName.Source);
        Assert.Empty(warnings);

        // 4. A type whose attribute LOOKUP throws (the constructor does). Reflection raises it out
        //    of the reader, Resolve treats it as a non-answer, the throw is reported once and the
        //    unload still has a name to log and sweep by.
        var hostile = typeof(HostileAttributeTestPlugin);
        var survived = PluginIdentity.Resolve(hostile.FullName, new Func<PluginIdentity?>?[]
        {
            PluginIdentity.MetadataReader(() => null),
            PluginIdentity.AttributeReader(hostile, inherit: false),
        }, warnings.Add);
        Assert.Equal(hostile.FullName, survived.Name);
        Assert.Equal("type", survived.Source);
        Assert.Single(warnings);
        Assert.Contains(hostile.FullName!, warnings[0], StringComparison.Ordinal);

        // 5. And the inherited-attribute case is not read here at all: with the inherit discovery
        //    uses, a type that takes [BepInPlugin] from a base class is anonymous to this chain —
        //    which is the point, because BepInEx cannot describe it either and both reloaders
        //    refuse it at discovery with a line rather than inventing a GUID for it.
        warnings.Clear();
        var derived = second.Derived;
        var refused = PluginIdentity.Resolve(derived.FullName, new Func<PluginIdentity?>?[]
        {
            PluginIdentity.MetadataReader(() => null),
            PluginIdentity.AttributeReader(derived, inherit: false),
        });
        Assert.Equal("type", refused.Source);
        var inherited = PluginIdentity.AttributeReader(derived, inherit: true)();
        Assert.True(inherited.HasValue);
        Assert.Equal("example.identity.test", inherited!.Value.Guid);
    }

    /// <summary>
    /// Job FREL4 (R63 finding 4), corrected by job FREL5 (R64 finding 3): the by-name match is
    /// qualified by the attribute type's <b>assembly</b>, so a same-named attribute that is not
    /// BepInEx's own cannot supply a GUID.
    ///
    /// <para>The reader matches <c>[BepInPlugin]</c> by simple name so that a second
    /// <c>BepInEx.Core</c> image is still read (case 2 of
    /// <see cref="The_production_readers_answer_over_real_types"/>). Unqualified, that accepts any
    /// attribute type called <c>BepInPlugin</c> in any namespace in any assembly — which
    /// multi-loader mods really do ship as compat shims — and it would then answer exactly where
    /// BepInEx's own read returned null, the one thing the chain's docstring says it cannot do.
    /// The cost is not cosmetic: the GUID goes to
    /// <c>HarmonyFallback.UnpatchOwnerAndSuffixes</c>, which unpatches that owner <b>and every
    /// owner prefixed with it</b>, so a shim declaring <c>progenitor</c> would unpatch
    /// Core's own <c>progenitor.core.patches</c> during a plugin unload. R63's probe
    /// produced exactly that value from exactly this shape of type.</para>
    ///
    /// <para><b>The shim shape is the one this test now carries</b> (R64 finding 3). FREL4
    /// qualified by <em>namespace</em>, and a real compat shim is declared in
    /// <c>namespace BepInEx</c> — it has to be, or the mod's own <c>[BepInPlugin(…)]</c> usage
    /// would not bind to it — so the namespace check refused only a shim nobody writes. The three
    /// cases here are therefore: an out-of-namespace decoy (the suite's own stand-in, which is
    /// what FREL4 tested), a <b>same-namespace shim in a mod assembly</b>, which is the case that
    /// passes without the fix, and a plugin carrying that shim beside the real attribute from an
    /// assembly named <c>BepInEx.Core</c>, which is what a second image looks like to
    /// reflection.</para>
    ///
    /// <para><b>And the shape the assembly-name check alone does not close</b> (job FREL6, R65
    /// finding 5): a mod that puts <c>&lt;AssemblyName&gt;BepInEx.Core&lt;/AssemblyName&gt;</c> in
    /// its own build file and ships the shim inside it satisfies every clause FREL5 had. Case 4
    /// is that mod. What refuses it is the clause FREL6 added — the attribute may not come from
    /// the plugin's <em>own</em> image — and it costs the accepted cases nothing, because a compat
    /// shim is compiled into the mod's DLL (that is why it exists) while BepInEx's real
    /// <c>BepInEx.Core</c> never is. Without the clause case 4 resolves to
    /// <c>Source == "attribute"</c> with <c>Guid == "progenitor"</c>, the prefix-matched
    /// unpatch value again.</para>
    /// </summary>
    [Fact]
    public void A_same_named_attribute_that_is_not_BepInEx_s_supplies_no_guid()
    {
        var notes = new System.Collections.Generic.List<string>();

        // 1. An out-of-namespace decoy on its own answers nothing: the identity falls through to
        //    the type name, and the reader says so once rather than silently.
        var decoyOnly = typeof(AttributedTestPlugin);
        var refused = PluginIdentity.Resolve(decoyOnly.FullName, new Func<PluginIdentity?>?[]
        {
            PluginIdentity.MetadataReader(() => null),
            PluginIdentity.AttributeReader(decoyOnly, inherit: false, notes.Add),
        }, notes.Add);
        Assert.Equal("", refused.Guid);
        Assert.False(refused.HasGuid);
        Assert.Equal(decoyOnly.FullName, refused.Name);
        Assert.Equal("type", refused.Source);
        Assert.Single(notes);
        Assert.Contains("BepInPluginAttribute", notes[0], StringComparison.Ordinal);
        Assert.Contains(decoyOnly.FullName!, notes[0], StringComparison.Ordinal);

        // The reader on its own, with no sink at all, is still a non-answer rather than a GUID.
        Assert.Null(PluginIdentity.AttributeReader(decoyOnly, inherit: false)());

        // 2. The shape that actually exists, and the one FREL4's namespace check let through:
        //    BepInEx.BepInPluginAttribute — right name, right namespace — declared in the MOD's
        //    own assembly. It is refused, the identity falls to the type name, and the line says
        //    which assembly disqualified it. Without the assembly qualification this resolves to
        //    Source == "attribute" with Guid == "progenitor", which is R63's probe value
        //    and the one that would unpatch progenitor.core.patches.
        notes.Clear();
        var shimOnly = SecondImage.Value.ShimOnly;
        var shimRefused = PluginIdentity.Resolve(shimOnly.FullName, new Func<PluginIdentity?>?[]
        {
            PluginIdentity.MetadataReader(() => null),
            PluginIdentity.AttributeReader(shimOnly, inherit: false, notes.Add),
        }, notes.Add);
        Assert.Equal("", shimRefused.Guid);
        Assert.False(shimRefused.HasGuid);
        Assert.Equal(shimOnly.FullName, shimRefused.Name);
        Assert.Equal("type", shimRefused.Source);
        Assert.Single(notes);
        Assert.Contains("BepInEx.BepInPluginAttribute from " + ShimAssemblyName, notes[0], StringComparison.Ordinal);
        Assert.Contains("BepInEx.Core", notes[0], StringComparison.Ordinal);

        // 3. Both present: BepInEx's own image wins whatever order reflection hands them back in
        //    (it does not specify one), and the choice is logged with the assembly each came from,
        //    because the two candidates share a full name and the loser is a plausible answer.
        notes.Clear();
        var both = SecondImage.Value.ShimAndReal;
        var chosen = PluginIdentity.Resolve(both.FullName, new Func<PluginIdentity?>?[]
        {
            PluginIdentity.MetadataReader(() => null),
            PluginIdentity.AttributeReader(both, inherit: false, notes.Add),
        }, notes.Add);
        Assert.Equal("example.identity.test", chosen.Guid);
        Assert.Equal("attribute", chosen.Source);
        Assert.Single(notes);
        Assert.Contains("BepInEx.BepInPluginAttribute from BepInEx.Core", notes[0], StringComparison.Ordinal);
        Assert.Contains("BepInEx.BepInPluginAttribute from " + ShimAssemblyName, notes[0], StringComparison.Ordinal);

        // 4. The naming convention, exercised (job FREL6, R65 finding 5): the mod's own assembly
        //    is NAMED BepInEx.Core and declares the attribute itself. Right name, right namespace,
        //    right assembly name — and refused, because the image is the plugin's own. This case
        //    passes the FREL5 gate; only the "not the plugin's own image" clause stops it.
        notes.Clear();
        var selfNamed = SecondImage.Value.SelfNamedImage;
        Assert.Equal(SelfNamedAssemblyName, selfNamed.Assembly.GetName().Name);
        var selfNamedRefused = PluginIdentity.Resolve(selfNamed.FullName, new Func<PluginIdentity?>?[]
        {
            PluginIdentity.MetadataReader(() => null),
            PluginIdentity.AttributeReader(selfNamed, inherit: false, notes.Add),
        }, notes.Add);
        Assert.Equal("", selfNamedRefused.Guid);
        Assert.False(selfNamedRefused.HasGuid);
        Assert.Equal(selfNamed.FullName, selfNamedRefused.Name);
        Assert.Equal("type", selfNamedRefused.Source);
        Assert.Single(notes);
        Assert.Contains("BepInEx.BepInPluginAttribute from " + SelfNamedAssemblyName, notes[0], StringComparison.Ordinal);
        Assert.Contains("the plugin's own assembly", notes[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Job FREL7 (R66 finding 10): the shape the gate does <b>not</b> close, measured rather than
    /// reasoned about.
    ///
    /// <para>FREL6 closed the mod that names its <em>own</em> assembly <c>BepInEx.Core</c> by
    /// requiring the attribute to come from an image other than the plugin's, and the note beside
    /// it said a compat shim cannot do that — "being compiled into the mod's DLL is the entire
    /// reason it exists". That is why shims are usually written that way, not a property of them.
    /// A mod that ships the shim in a <em>second</em> assembly of its own and names that assembly
    /// <c>BepInEx.Core</c> passes every clause: right full name, right assembly simple name, and a
    /// different assembly from the one the plugin type came from. This test builds that mod and
    /// asserts what actually happens — the shim's GUID is taken, with <c>Source == "attribute"</c>,
    /// and <b>silently</b>, because one accepted candidate with nothing refused beside it produces
    /// no note. It is a characterization test: it passes before and after FREL7, which changed only
    /// the prose, and it exists so the prose is a measurement.</para>
    ///
    /// <para>The GUID it yields is <c>progenitor</c>, R63's probe value, which is the one
    /// that would reach <c>HarmonyFallback.UnpatchOwnerAndSuffixes</c> and take Core's own
    /// <c>progenitor.core.patches</c> with it — so this is the residual's cost, not a
    /// harmless one. What makes it low is that the shape is contrived and that such a mod is
    /// fighting the assembly resolver everywhere; the axis that would close it (provenance) is
    /// named in <see cref="PluginIdentity"/>'s type docstring.</para>
    /// </summary>
    [Fact]
    public void A_shim_in_a_second_assembly_named_BepInEx_Core_is_still_accepted()
    {
        var notes = new System.Collections.Generic.List<string>();
        var plugin = SecondImage.Value.SeparateShimImage;

        // The shape first, so a later edit to the emitter cannot quietly turn this into one of the
        // cases above: the attribute is BepInEx.BepInPluginAttribute out of an assembly simply
        // named BepInEx.Core, and that assembly is not the plugin type's own.
        Type? attributeType = null;
        foreach (var attribute in plugin.GetCustomAttributes(false))
            if (attribute.GetType().Name == "BepInPluginAttribute") attributeType = attribute.GetType();
        Assert.NotNull(attributeType);
        Assert.Equal("BepInEx.BepInPluginAttribute", attributeType!.FullName);
        Assert.Equal(SelfNamedAssemblyName, attributeType.Assembly.GetName().Name);
        Assert.NotSame(plugin.Assembly, attributeType.Assembly);
        Assert.Equal(ShimAssemblyName, plugin.Assembly.GetName().Name);

        var taken = PluginIdentity.Resolve(plugin.FullName, new Func<PluginIdentity?>?[]
        {
            PluginIdentity.MetadataReader(() => null),
            PluginIdentity.AttributeReader(plugin, inherit: false, notes.Add),
        }, notes.Add);
        Assert.Equal("progenitor", taken.Guid);
        Assert.True(taken.HasGuid);
        Assert.Equal("attribute", taken.Source);
        Assert.Empty(notes);
    }

    /// <summary>
    /// Job FREL7 (R66 finding 10): the pin that fails without the prose change, because the change
    /// <em>is</em> prose. <see cref="A_shim_in_a_second_assembly_named_BepInEx_Core_is_still_accepted"/>
    /// measures the residual; this asserts both copies of <c>PluginIdentity</c> state it instead of
    /// denying it, and that the sentence which denied it ("What a compat shim cannot do is come
    /// from somewhere other than the mod's own DLL") is gone from both.
    ///
    /// <para>The pinned text is a comment, which is the one case
    /// <see cref="SourcePin"/> says to use <c>Assert.Contains</c> over source for. Both copies are
    /// read because the two files are the same file twice
    /// (<c>ReloadHardeningTests.The_two_plugin_identity_copies_are_the_same_code</c> pins that for
    /// the code, and strips <c>//</c> lines, so the docstrings are exactly what that mirror test
    /// cannot see).</para>
    /// </summary>
    [Fact]
    public void Both_identity_copies_state_the_residual_the_assembly_gate_leaves_open()
    {
        string[][] copies =
        {
            new[] { "src", "Progenitor.Core", "Reload", "PluginIdentity.cs" },
            new[] { "src", "Progenitor.Bootstrap", "PluginIdentity.cs" },
        };

        foreach (string[] copy in copies)
        {
            string text = SourcePin.ReadFile(copy);
            Assert.Contains(
                "a mod that ships its shim in a <em>second</em> assembly of its own and",
                text, StringComparison.Ordinal);
            Assert.Contains(
                nameof(A_shim_in_a_second_assembly_named_BepInEx_Core_is_still_accepted),
                text, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "What a compat shim cannot do is", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The name of the dynamic assembly standing in for a multi-loader mod's own DLL. It is not
    /// <c>BepInEx.Core</c>, and that — not its namespace — is what disqualifies the
    /// <c>BepInEx.BepInPluginAttribute</c> it declares (job FREL5, R64 finding 3).
    /// </summary>
    private const string ShimAssemblyName = "Multiloader.Mod";

    /// <summary>
    /// Four dynamic assemblies, each declaring <c>BepInEx.BepInPluginAttribute</c>: one named
    /// <c>BepInEx.Core</c> (a second <c>BepInEx.Core</c> image — same simple name, different
    /// assembly identity, which is exactly what the by-name match exists to read), one named
    /// <see cref="ShimAssemblyName"/> (the mod, whose compat shim must be declared in namespace
    /// <c>BepInEx</c> for the mod's own attribute usage to bind to it) and one named
    /// <see cref="SelfNamedAssemblyName"/> that is <em>itself</em> the mod (job FREL6, R65 finding
    /// 5), and a fourth, also named <see cref="SelfNamedAssemblyName"/> but <em>separate</em> from
    /// the mod, which is the shape the gate accepts and job FREL7 (R66 finding 10) measures rather
    /// than denies. Plus the plugin types that carry them. None of this can be written in C# here: the
    /// project may not reference BepInEx and may not declare a second namespace in a
    /// file-scoped-namespace file, and two types with the same full name cannot live in one
    /// assembly at all. <c>RunAndCollect</c> so the suite does not keep them. Built once — nothing
    /// in the chain compares attribute types by reference identity, but emitting twice would
    /// double the cost for nothing.
    ///
    /// <para>Every plugin type lives in the <em>mod</em> assembly, which is where plugin types
    /// really live and is what job FREL6 made load-bearing: the accepted cases are accepted
    /// because the attribute comes from a <c>BepInEx.Core</c> image that is not the plugin's own
    /// DLL. Reading those attributes back therefore resolves an <c>AssemblyRef</c> to a dynamic
    /// assembly, which name binding does not find, so the <c>BepInEx.Core</c> stand-in is the one
    /// registered with the resolver — matched on its <b>full</b> name, so the partial-name probe in
    /// <c>ConfigSyncTests.Test_project_does_not_reference_live_BepInEx_ConfigFile</c> (which
    /// asserts <c>Type.GetType("BepInEx.Configuration.ConfigFile, BepInEx.Core")</c> is null) still
    /// finds nothing. The self-named image needs no registration at all: its attribute is declared
    /// in the same assembly as the type that carries it, which is the whole shape of the
    /// finding.</para>
    /// </summary>
    private static readonly Lazy<SecondImageTypes> SecondImage = new(EmitSecondImage);

    private sealed class SecondImageTypes
    {
        public Type Plugin = null!;
        public Type Derived = null!;
        public Type ShimOnly = null!;
        public Type ShimAndReal = null!;
        public Type SelfNamedImage = null!;
        public Type SeparateShimImage = null!;
    }

    /// <summary>
    /// The name a mod would have to put in its own build file to satisfy FREL5's assembly-name
    /// check with its own compat shim: <c>BepInEx.Core</c> (job FREL6, R65 finding 5). It is the
    /// same string as the real image's, which is the point — the assembly identity behind it is
    /// the plugin's own, and that is what the gate reads.
    /// </summary>
    private const string SelfNamedAssemblyName = "BepInEx.Core";

    private static SecondImageTypes EmitSecondImage()
    {
        var signature = new[] { typeof(string), typeof(string), typeof(string) };

        // Both attribute types are public: the suite's own compiled stand-in is `internal`, so a
        // dynamic assembly cannot see it and reflection drops it off an emitted type without a
        // word — which would have made the shim invisible here and the preference untested.
        var image = DefineDynamicAssembly("BepInEx.Core", resolvable: true);
        var attributeCtor = EmitPluginAttribute(image, "BepInEx.BepInPluginAttribute", signature);

        var mod = DefineDynamicAssembly(ShimAssemblyName, resolvable: false);
        var shimCtor = EmitPluginAttribute(mod, "BepInEx.BepInPluginAttribute", signature);

        object[] values = { "example.identity.test", "Identity Test", "2.1.0" };
        object[] shimValues = { "progenitor", "Decoy", "0.0.1" };

        // The plugin types live in the MOD's assembly, which is where plugin types really live
        // and — since job FREL6 — what makes the accepted cases accepted: the attribute has to
        // come from a BepInEx.Core image that is not the plugin's own DLL.
        var plugin = mod.DefineType("Progenitor.Tests.Emitted.SecondImagePlugin", TypeAttributes.Public);
        plugin.SetCustomAttribute(new CustomAttributeBuilder(attributeCtor, values));
        var pluginType = plugin.CreateType()!;

        var derived = mod.DefineType(
            "Progenitor.Tests.Emitted.DerivedFromSecondImagePlugin", TypeAttributes.Public, pluginType);

        var shimOnly = mod.DefineType("Progenitor.Tests.Emitted.ShimOnlyPlugin", TypeAttributes.Public);
        shimOnly.SetCustomAttribute(new CustomAttributeBuilder(shimCtor, shimValues));

        // The shim is applied FIRST, so a reader that took the first name match would take it.
        var shimAndReal = mod.DefineType("Progenitor.Tests.Emitted.ShimAndRealPlugin", TypeAttributes.Public);
        shimAndReal.SetCustomAttribute(new CustomAttributeBuilder(shimCtor, shimValues));
        shimAndReal.SetCustomAttribute(new CustomAttributeBuilder(attributeCtor, values));

        // Job FREL6 (R65 finding 5): a mod whose own assembly is NAMED BepInEx.Core, carrying its
        // own BepInEx.BepInPluginAttribute. Nothing but the second clause of the gate separates
        // this from the real thing — an assembly name is a line in somebody's build file — and
        // nothing here needs the resolver, because the attribute is declared where the type is.
        var selfNamed = DefineDynamicAssembly(SelfNamedAssemblyName, resolvable: false);
        var selfNamedCtor = EmitPluginAttribute(selfNamed, "BepInEx.BepInPluginAttribute", signature);
        var selfNamedPlugin = selfNamed.DefineType(
            "Progenitor.Tests.Emitted.SelfNamedBepInExCorePlugin", TypeAttributes.Public);
        selfNamedPlugin.SetCustomAttribute(new CustomAttributeBuilder(selfNamedCtor, shimValues));

        // Job FREL7 (R66 finding 10): the residual. The same mod, shipping its shim in a SECOND
        // assembly of its own that it names BepInEx.Core, with the plugin type still in its main
        // assembly — so the attribute is not declared in the plugin's own image and every clause of
        // the gate passes. The distinct assembly VERSION is a test-rig detail only: two stand-ins
        // share the simple name BepInEx.Core, and the resolver below is keyed on the FULL name, so
        // they must differ somewhere the resolver can see. The gate compares simple names and
        // cannot see it.
        var separateShim = DefineDynamicAssembly(
            SelfNamedAssemblyName, resolvable: true, new Version(9, 9, 9, 9));
        var separateShimCtor = EmitPluginAttribute(separateShim, "BepInEx.BepInPluginAttribute", signature);
        var separateShimPlugin = mod.DefineType(
            "Progenitor.Tests.Emitted.SeparateShimImagePlugin", TypeAttributes.Public);
        separateShimPlugin.SetCustomAttribute(new CustomAttributeBuilder(separateShimCtor, shimValues));

        return new SecondImageTypes
        {
            Plugin = pluginType,
            Derived = derived.CreateType()!,
            ShimOnly = shimOnly.CreateType()!,
            ShimAndReal = shimAndReal.CreateType()!,
            SelfNamedImage = selfNamedPlugin.CreateType()!,
            SeparateShimImage = separateShimPlugin.CreateType()!,
        };
    }

    /// <summary>
    /// One collectible dynamic assembly with one module. <paramref name="resolvable"/> registers it
    /// with the resolver so a cross-assembly attribute read can find it by name — true for the two
    /// images an emitted type actually has to follow a reference to: the <c>BepInEx.Core</c>
    /// stand-in and FREL7's separate shim image (the mod's own shim and the self-named image both
    /// declare their attribute where the type that carries it lives, so neither needs it).
    /// <paramref name="version"/> distinguishes two stand-ins that share a simple name, which the
    /// full-name registration below requires and the production gate cannot see.
    ///
    /// <para>The registration is keyed and matched on the <b>full</b> assembly name, never the
    /// simple one, and that is load-bearing rather than tidy: the rest of the suite asserts that
    /// <c>Type.GetType("BepInEx.Configuration.ConfigFile, BepInEx.Core")</c> finds nothing
    /// (<c>ConfigSyncTests.Test_project_does_not_reference_live_BepInEx_ConfigFile</c>), and that
    /// request carries a partial name with no version. An emitted module's own assembly reference
    /// carries the full one, so this answers the attribute read and declines the probe.</para>
    /// </summary>
    private static ModuleBuilder DefineDynamicAssembly(string simpleName, bool resolvable, Version? version = null)
    {
        var name = new AssemblyName(simpleName);
        if (version != null) name.Version = version;
        var assembly = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.RunAndCollect);
        if (resolvable)
            lock (EmittedImages)
            {
                if (EmittedImages.Count == 0)
                    AssemblyLoadContext.Default.Resolving += ResolveEmittedImage;
                EmittedImages[assembly.GetName().FullName] = assembly;
            }
        return assembly.DefineDynamicModule(simpleName);
    }

    private static readonly System.Collections.Generic.Dictionary<string, Assembly> EmittedImages = new(StringComparer.Ordinal);

    private static Assembly? ResolveEmittedImage(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Version == null) return null;
        lock (EmittedImages)
            return EmittedImages.TryGetValue(name.FullName, out var found) ? found : null;
    }

    /// <summary>
    /// One <c>[BepInPlugin]</c>-shaped attribute type — <c>GUID</c>, <c>Name</c> and
    /// <c>Version</c> as public string properties over a three-string constructor, which is the
    /// shape <c>PluginIdentity.FromAttributeLike</c> projects — at <paramref name="fullName"/>.
    /// No <c>[AttributeUsage]</c>, so it takes the default the real attribute also has for the
    /// purpose that matters here: <c>Inherited = true</c>, which is what makes the derived-type
    /// case in <see cref="The_production_readers_answer_over_real_types"/> real.
    /// </summary>
    private static ConstructorInfo EmitPluginAttribute(ModuleBuilder module, string fullName, Type[] signature)
    {
        var builder = module.DefineType(
            fullName, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, typeof(Attribute));
        string[] names = { "GUID", "Name", "Version" };
        var fields = new FieldBuilder[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            fields[i] = builder.DefineField("_" + names[i], typeof(string), FieldAttributes.Private);
            var property = builder.DefineProperty(names[i], PropertyAttributes.None, typeof(string), null);
            var getter = builder.DefineMethod("get_" + names[i],
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                typeof(string), Type.EmptyTypes);
            var body = getter.GetILGenerator();
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Ldfld, fields[i]);
            body.Emit(OpCodes.Ret);
            property.SetGetMethod(getter);
        }

        var constructor = builder.DefineConstructor(MethodAttributes.Public, CallingConventions.HasThis, signature);
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!);
        OpCode[] arguments = { OpCodes.Ldarg_1, OpCodes.Ldarg_2, OpCodes.Ldarg_3 };
        for (int i = 0; i < fields.Length; i++)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(arguments[i]);
            il.Emit(OpCodes.Stfld, fields[i]);
        }
        il.Emit(OpCodes.Ret);

        return builder.CreateType()!.GetConstructor(signature)!;
    }

    /// <summary>
    /// Carries the suite's stand-in <c>[BepInPlugin]</c>, which lives in <c>Progenitor.Tests</c>
    /// because this project may not reference BepInEx. Since job FREL4 that makes it a <b>decoy</b>
    /// to the attribute reader rather than an answer — see
    /// <see cref="A_same_named_attribute_that_is_not_BepInEx_s_supplies_no_guid"/> — so it is used
    /// here only where the metadata reader is the one answering.
    /// </summary>
    [BepInPlugin("example.identity.test", "Identity Test", "2.1.0")]
    private class AttributedTestPlugin { }

    private sealed class UnattributedTestPlugin { }

    [HostileIdentity]
    private sealed class HostileAttributeTestPlugin { }

    /// <summary>An attribute whose construction fails, i.e. a type whose attribute lookup throws.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    private sealed class HostileIdentityAttribute : Attribute
    {
        public HostileIdentityAttribute() => throw new InvalidOperationException("attribute ctor");
    }

    private static string ReadReloaderFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Progenitor.Core", "Reload", "PluginReloader.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("src/Progenitor.Core/Reload/PluginReloader.cs");
    }

    private sealed class Owner
    {
        public Owner(string id) => Id = id;
        public string Id { get; }
    }
}
