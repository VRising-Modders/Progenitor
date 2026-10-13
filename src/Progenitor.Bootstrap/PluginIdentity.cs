using System;
using System.Reflection;

namespace Progenitor.Bootstrap;

/// <summary>
/// Who a plugin instance is, resolved <b>once</b> per unload and never null (job FREL1,
/// R61 finding 1).
///
/// <para><b>Why this type exists.</b> The reloader used to read
/// <c>MetadataHelper.GetMetadata(plugin)</c> in five separate places and dereference the result
/// in four of them. BepInEx returns <c>null</c> from that method whenever the type carries no
/// <c>[BepInPlugin]</c> that <b>it</b> can see, and every one of those dereferences then threw an
/// <c>NullReferenceException</c> into a surrounding <c>catch</c>. Two of them cost real damage:
/// the Harmony unpatch-by-id at the end of <c>SweepRegistrations</c> (an NRE there leaves the
/// unloading plugin's IL2CPP trampolines applied, which is <em>the</em> pin that keeps a
/// collectible generation alive — CLAUDE.md's Step D breakthrough) and the read that sat in front
/// of <c>plugin.Unload()</c> (an NRE there skipped the plugin's own teardown entirely).
/// Job FEX9 guarded one of the five; this resolves the identity once, from a fixed order of
/// sources, and hands the same value to all of them.</para>
///
/// <para><b>Two readers, not three</b> (job FREL3, R62 finding 1). FREL1's docstrings described
/// "BepInEx's metadata, then the reloader's own attribute read, then the type name" as three
/// sources. They are not three. Analyzing the game's own <c>BepInEx\core\BepInEx.Core.dll</c>
/// shows <c>MetadataHelper.GetMetadata(plugin)</c> <b>is</b>
/// <c>plugin.GetType().GetCustomAttributes(typeof(BepInPlugin), inherit: false)</c>, returning null
/// on the empty array, so the second reader asks the first reader's question. It can never answer
/// where the first answered <em>null</em>. What it is for is the first reader <em>throwing</em>
/// (<see cref="Resolve"/> treats a throw as a non-answer and moves on) and the one substantive
/// difference between the two calls: <see cref="AttributeReader"/> matches the attribute by
/// <em>name</em> rather than by <c>typeof</c>, so it also answers when the plugin carries a
/// <c>[BepInPlugin]</c> from a different <c>BepInEx.Core</c> image than the one this assembly
/// binds. The real fallback — the third answer, and the only one that is a different question —
/// is the type's full name.</para>
///
/// <para><b>The name match is qualified to BepInEx's own image</b> (job FREL4, R63 finding 4;
/// corrected by job FREL5, R64 finding 3). A bare simple-name match is not the same question as
/// <c>typeof(BepInPlugin)</c>: it accepts <em>any</em> attribute type called <c>BepInPlugin</c> in
/// any namespace in any assembly, so it could answer exactly where BepInEx answered null — which
/// is what this docstring says it cannot do — and multi-loader mods really do ship same-named
/// compat shims. FREL4 qualified the match by <em>namespace</em>, which was the wrong axis: a
/// compat shim exists so that <c>[BepInPlugin("…","…","…")]</c> compiles without a BepInEx
/// reference, and for the mod's own source to bind to it the shim must be declared in
/// <c>namespace BepInEx</c> — so the namespace check excluded the shim shape that cannot occur and
/// admitted the one that does. The axis that separates them is the <b>assembly</b>: BepInEx
/// declares the attribute in <c>BepInEx.Core</c> (verified against this build's
/// <c>BepInEx\core\BepInEx.Core.xml</c>, which carries <c>T:BepInEx.BepInPlugin</c>; no other
/// assembly in <c>BepInEx\core</c> defines it), a second <c>BepInEx.Core</c> image has that same
/// simple name — which is why the match is by assembly <em>name</em> and not by assembly identity
/// — and a mod's shim is not expected to. <b>Expected is not prevented</b> (job FREL6, R65
/// finding 5): an assembly name is a line in somebody else's build file, and a mod that sets
/// <c>&lt;AssemblyName&gt;BepInEx.Core&lt;/AssemblyName&gt;</c> satisfies a name check. What a
/// compat shim is normally unable to do is come from somewhere other than the mod's own DLL —
/// being compiled into it is the usual reason it exists — so the gate is a name match
/// <em>plus</em> "not declared in the plugin's own image". A candidate therefore counts only when its type's full
/// name is <c>BepInEx.BepInPlugin</c>/<c>BepInEx.BepInPluginAttribute</c>, it was declared in an
/// assembly simply named <c>BepInEx.Core</c>, <em>and</em> that assembly is not the one the plugin
/// type itself came from. When a shim and the real attribute are both present
/// the real one is chosen and the choice is logged with the assembly each came from; when a shim is
/// the <em>only</em> candidate the reader declines and the identity falls through to the type name,
/// because the value would otherwise reach
/// <c>HarmonyFallback.UnpatchOwnerAndSuffixes</c>, which unpatches that owner <b>and every owner
/// prefixed with it</b> — a shim claiming <c>progenitor</c> would take Core's own
/// <c>progenitor.core.patches</c> with it.</para>
///
/// <para><b>What the gate is, exactly, and the shape it still accepts</b> (job FREL7, R66 finding
/// 10). Both halves are conventions, not properties of shims, and the paragraph above states the
/// second one too strongly: a mod that ships its shim in a <em>second</em> assembly of its own and
/// names that assembly <c>BepInEx.Core</c> satisfies all three clauses — right full name, right
/// assembly name, and not the plugin's own image — and is accepted, silently, because a single
/// accepted candidate with nothing refused beside it produces no note. That is measured rather than
/// argued: <c>PluginReloaderTests.A_shim_in_a_second_assembly_named_BepInEx_Core_is_still_accepted</c>
/// builds exactly that mod and asserts the GUID it yields. It is a contrived shape — the shim
/// multi-loader mods actually ship is compiled into the plugin's own DLL, and that one is refused —
/// and a mod carrying a second <c>BepInEx.Core</c> is fighting the loader everywhere rather than
/// only here, since a plugin ALC asking for <c>BepInEx.Core</c> binds by simple name to the image
/// already loaded (<c>CoreAssemblyResolver.Resolve</c>). It is <em>not</em> bounded by the
/// type-name fallback, which bounds only the refusals. The two obvious stronger axes are ruled out
/// on <see cref="BepInExAssembly"/> (assembly identity: a second <c>BepInEx.Core</c> image is a
/// real thing this reader exists to read) and <see cref="IsBepInExsOwn"/> (the load context: a
/// chainloader plugin shares the default one with the real <c>BepInEx.Core</c>). The axis nobody
/// has spent yet, if a later job wants a real discriminator, is provenance: BepInEx's own
/// <c>BepInEx.Core</c> is loaded from a file under <c>BepInEx\core</c>, which neither a byte-loaded
/// reloadable plugin nor a <c>BepInEx\plugins</c> assembly is.</para>
///
/// <para><b>The inherited-attribute case is refused at discovery, not read here.</b> FEX9's
/// fallback used <c>inherit: true</c> and so could name a plugin BepInEx could not, which reads
/// like a third source and is really a disagreement: BepInEx's chainloader builds its plugin info
/// from <c>BepInPlugin.FromCecilType</c>, also <c>inherit: false</c>, so a type that inherits the
/// attribute is not a BepInEx plugin at all. Both reloaders now refuse such a type at discovery
/// with a line saying why, which is why <see cref="AttributeReader"/> is called with the same
/// <c>inherit</c> discovery uses: handing out a GUID for a plugin BepInEx disowns would make the
/// Harmony unpatch-by-id and the Units sweep key on an id nothing else in the process agrees
/// with.</para>
///
/// <para><b>Game-free on purpose</b> so the readers themselves can be tested, not just the order:
/// the metadata read is the one BepInEx-bound line and it is handed in as a delegate by
/// <c>PluginReloader</c>, while both projections and the attribute lookup live here and are
/// composed by <c>PluginReloaderTests</c> over real types.</para>
/// </summary>
internal readonly struct PluginIdentity
{
    internal PluginIdentity(string guid, string name, string version, string source)
    {
        Guid = guid ?? "";
        Name = name ?? "";
        Version = version ?? "";
        Source = source ?? "";
    }

    /// <summary>The <c>[BepInPlugin]</c> GUID, or <c>""</c> when nothing readable supplied one.</summary>
    internal string Guid { get; }

    /// <summary>
    /// A human name for log lines. Never empty: falls back to the type's full name, and to
    /// <c>"plugin"</c> when even that is unreadable.
    /// </summary>
    internal string Name { get; }

    /// <summary>The plugin version, or <c>""</c>.</summary>
    internal string Version { get; }

    /// <summary>
    /// Which reader answered: <c>metadata</c> (BepInEx), <c>attribute</c> (the same attribute read
    /// without BepInEx — reachable only when the metadata read threw or matched no type, see the
    /// type docstring) or <c>type</c> (the name fallback). Carried so a log line can say why a
    /// GUID is missing.
    /// </summary>
    internal string Source { get; }

    /// <summary>True when <see cref="Guid"/> is usable as a Harmony id / Units owner id.</summary>
    internal bool HasGuid => !string.IsNullOrWhiteSpace(Guid);

    public override string ToString() =>
        HasGuid ? Name + " (" + Guid + ")" : Name;

    /// <summary>
    /// Resolve in a fixed order — BepInEx's metadata, then the same attribute read without
    /// BepInEx, then the type's full name — and never return null or an empty <see cref="Name"/>.
    /// The first two ask one question twice on purpose; the type docstring says why, and says
    /// which of the three is the actual fallback.
    ///
    /// <para>A reader that <b>throws</b> is treated exactly like a reader that returned null:
    /// the throw is handed to <paramref name="warn"/> and the next source is tried. That is the
    /// residue R61 finding 1 named in FEX9's own fix, where the fallback read sat inside the try
    /// that guarded the sweep, so an attribute type that failed to resolve skipped the sweep the
    /// fallback existed to protect.</para>
    ///
    /// <para>The first reader that yields <em>anything</em> usable (a GUID or a name) decides the
    /// identity; blanks in it are then filled from the type name. Sources are not merged field by
    /// field: two readers disagreeing about the same plugin is not a case that arises (they read
    /// the same attribute), and taking one answer keeps <see cref="Source"/> meaningful.</para>
    /// </summary>
    /// <param name="typeFullName">The plugin type's full name, the last-resort identity.</param>
    /// <param name="readers">Candidate readers, in priority order. Nulls are skipped.</param>
    /// <param name="warn">Optional sink for a reader that threw.</param>
    internal static PluginIdentity Resolve(
        string? typeFullName,
        Func<PluginIdentity?>?[]? readers,
        Action<string>? warn = null)
    {
        string fallbackName = string.IsNullOrWhiteSpace(typeFullName) ? "plugin" : typeFullName!;

        if (readers != null)
        {
            for (int i = 0; i < readers.Length; i++)
            {
                var reader = readers[i];
                if (reader == null) continue;

                PluginIdentity? candidate;
                try { candidate = reader(); }
                catch (Exception e)
                {
                    warn?.Invoke("identity read for " + fallbackName + " threw: " + e.Message);
                    continue;
                }

                if (candidate == null) continue;
                var value = candidate.Value;
                if (string.IsNullOrWhiteSpace(value.Guid) && string.IsNullOrWhiteSpace(value.Name))
                    continue;

                return new PluginIdentity(
                    value.Guid ?? "",
                    string.IsNullOrWhiteSpace(value.Name) ? fallbackName : value.Name,
                    value.Version ?? "",
                    string.IsNullOrWhiteSpace(value.Source) ? "unknown" : value.Source);
            }
        }

        return new PluginIdentity("", fallbackName, "", "type");
    }

    /// <summary>
    /// Reader one, as a composable value (job FREL3, R62 finding 1): BepInEx's own answer.
    /// <paramref name="readMetadata"/> is the single BepInEx-bound line — the reloaders pass
    /// <c>() =&gt; MetadataHelper.GetMetadata(plugin)</c> — and the projection off the returned
    /// <c>BepInPlugin</c> lives here, so a test can compose the production projection over a
    /// stand-in attribute instance without referencing BepInEx (which the test project may not
    /// do). A throwing <paramref name="readMetadata"/> is a non-answer, handled by
    /// <see cref="Resolve"/>.
    /// </summary>
    internal static Func<PluginIdentity?> MetadataReader(Func<object?>? readMetadata) =>
        () => readMetadata == null ? null : FromAttributeLike(readMetadata(), "metadata");

    /// <summary>
    /// Reader two: the same <c>[BepInPlugin]</c>, read without going through BepInEx.
    ///
    /// <para><paramref name="inherit"/> is the reloader's <c>DiscoveryInherit</c> and is false, so
    /// this asks exactly the question <c>MetadataHelper.GetMetadata</c> asks and cannot answer
    /// where that returned null. It earns its place in two ways only: it still runs when the
    /// metadata read <b>threw</b>, and it matches the attribute by <em>name</em> rather than by
    /// <c>typeof(BepInPlugin)</c>, so a plugin whose attribute comes from a second
    /// <c>BepInEx.Core</c> image is still named rather than falling through to the type name. The
    /// name match is the same one <c>PrefabEditCompatibility</c> already uses for the same reason.
    /// </para>
    ///
    /// <para>Job FREL4 (R63 finding 4), corrected by job FREL5 (R64 finding 3) and completed by
    /// job FREL6 (R65 finding 5): the name match is <b>qualified by the attribute type's
    /// assembly</b>, not by its namespace, and the qualification is a name match plus "not the
    /// plugin's own image". Only a type named
    /// <see cref="BepInExNamespace"/><c>.BepInPlugin(Attribute)</c> declared in an assembly simply
    /// named <see cref="BepInExAssembly"/> that is <em>not</em> the assembly
    /// <paramref name="pluginType"/> came from counts, so a same-named attribute a mod ships as its
    /// own compat shim cannot supply a GUID <em>even though the shim is itself declared in
    /// namespace <c>BepInEx</c></em> (the only place it can be declared) <em>and even if the mod
    /// names its own assembly <c>BepInEx.Core</c></em>. The one shim shape this does not refuse —
    /// a shim in a <em>second</em> assembly the mod names <c>BepInEx.Core</c> — is stated and
    /// measured rather than denied; the type docstring's last paragraph has it (job FREL7, R66
    /// finding 10). A shim alongside the real
    /// attribute loses to it (and the choice is logged through <paramref name="note"/> with the
    /// assembly each candidate came from, because <c>Type.GetCustomAttributes</c> does not specify
    /// an order and the answer keys a prefix-matched Harmony unpatch); a shim on its own is refused
    /// and the identity falls through to the type name, which costs a log line's precision and
    /// cannot unpatch anyone else's trampolines. Two <em>accepted</em> candidates — two
    /// <c>BepInEx.Core</c> images on one type — are logged the same way rather than taken
    /// silently. The type docstring has the failure this prevents.</para>
    /// </summary>
    /// <param name="note">Optional sink for the one line a decoy produces. Never called otherwise.</param>
    internal static Func<PluginIdentity?> AttributeReader(Type? pluginType, bool inherit, Action<string>? note = null) =>
        () =>
        {
            if (pluginType == null) return null;

            object? chosen = null;
            string accepted = "";
            int acceptedCount = 0;
            string shims = "";
            foreach (var attribute in pluginType.GetCustomAttributes(inherit))
            {
                if (attribute == null) continue;
                var attributeType = attribute.GetType();
                string typeName = attributeType.Name;
                if (!string.Equals(typeName, "BepInPlugin", StringComparison.Ordinal)
                    && !string.Equals(typeName, "BepInPluginAttribute", StringComparison.Ordinal))
                    continue;

                if (!IsBepInExsOwn(attributeType, pluginType))
                {
                    shims = Append(shims, Describe(attributeType));
                    continue;
                }

                acceptedCount++;
                accepted = Append(accepted, Describe(attributeType));
                // First candidate from a BepInEx.Core image wins; a second one is the same
                // attribute from another image of the same assembly and carries the same values.
                // It is still reported below rather than taken silently.
                chosen ??= attribute;
            }

            string owner = pluginType.FullName ?? pluginType.Name;
            if (chosen == null)
            {
                if (shims.Length > 0)
                    note?.Invoke("the only [BepInPlugin] on " + owner + " is " + shims
                        + ", which is not BepInEx's own attribute (it is not declared in an "
                        + BepInExAssembly + " image other than the plugin's own assembly); "
                        + "using the type name rather than a GUID "
                        + "that would key the Harmony unpatch-by-id.");
                return null;
            }

            if (shims.Length > 0 || acceptedCount > 1)
                note?.Invoke("[BepInPlugin] on " + owner + " resolved to " + Describe(chosen.GetType())
                    + "; BepInEx candidates: " + accepted
                    + (shims.Length > 0 ? "; not BepInEx's: " + shims : "") + ".");
            return FromAttributeLike(chosen, "attribute");
        };

    /// <summary>
    /// The namespace BepInEx declares <c>BepInPlugin</c> in. Necessary but not sufficient: a mod's
    /// compat shim is declared here too, because that is the only namespace the mod's own
    /// <c>[BepInPlugin]</c> usage can bind through (R64 finding 3).
    /// </summary>
    private const string BepInExNamespace = "BepInEx";

    /// <summary>
    /// The simple name of the assembly BepInEx declares <c>BepInPlugin</c> in, and the axis that
    /// actually separates BepInEx's attribute from a mod's same-named, same-namespace shim.
    /// Verified against this build's <c>BepInEx\core\BepInEx.Core.xml</c> (<c>T:BepInEx.BepInPlugin</c>);
    /// no other assembly in <c>BepInEx\core</c> defines it. Compared by <em>simple name</em> on
    /// purpose: a second <c>BepInEx.Core</c> image is a different assembly identity with the same
    /// name, and reading it is the whole reason this reader matches by name at all. Being a
    /// <em>name</em>, it is also a convention rather than a guarantee — see
    /// <see cref="IsBepInExsOwn"/> for the clause that makes the gate hold anyway (R65 finding 5).
    /// </summary>
    private const string BepInExAssembly = "BepInEx.Core";

    /// <summary>
    /// True when <paramref name="type"/> is the attribute type BepInEx's own metadata reader would
    /// have used: full name <c>BepInEx.BepInPlugin(Attribute)</c>, declared in an assembly simply
    /// named <see cref="BepInExAssembly"/> — and (job FREL6, R65 finding 5) declared in an assembly
    /// that is <b>not</b> the one <paramref name="pluginType"/> itself came from. The name half is
    /// a convention a mod could satisfy with one line of its own build file; the second half is
    /// what the shim shape ordinarily cannot satisfy, because a compat shim exists so that
    /// <c>[BepInPlugin(…)]</c> compiles without a BepInEx reference and is therefore compiled into
    /// the mod's own image. BepInEx's real <c>BepInEx.Core</c> never is. A type whose assembly
    /// cannot be named is not it.
    ///
    /// <para><b>Ordinarily, not never</b> (job FREL7, R66 finding 10): a mod is free to put the
    /// shim in a second assembly of its own and name that assembly <c>BepInEx.Core</c>, which
    /// satisfies both halves and is accepted here. That residual is stated in full, with the axis
    /// that would close it, in the type docstring's last paragraph, and it is measured by
    /// <c>PluginReloaderTests.A_shim_in_a_second_assembly_named_BepInEx_Core_is_still_accepted</c>.</para>
    ///
    /// <para>The load context is deliberately <em>not</em> the axis, though it is as cheap to read:
    /// a plugin the BepInEx chainloader loaded lives in the default context alongside the real
    /// <c>BepInEx.Core</c>, so an "different ALC" clause would refuse BepInEx's own attribute for
    /// every non-reloadable plugin and drop it to the type name.</para>
    /// </summary>
    private static bool IsBepInExsOwn(Type type, Type pluginType)
    {
        if (!string.Equals(type.Namespace, BepInExNamespace, StringComparison.Ordinal)) return false;
        if (!string.Equals(AssemblyNameOf(type), BepInExAssembly, StringComparison.Ordinal)) return false;
        return !ReferenceEquals(type.Assembly, pluginType.Assembly);
    }

    /// <summary>The type plus the assembly it came from: the two candidates can share a full name.</summary>
    private static string Describe(Type type) => (type.FullName ?? type.Name) + " from " + AssemblyNameOf(type);

    private static string AssemblyNameOf(Type type)
    {
        try { return type.Assembly.GetName().Name ?? "<unnamed>"; }
        catch (Exception e) { return "<unreadable: " + e.GetType().Name + ">"; }
    }

    private static string Append(string list, string item) => list.Length == 0 ? item : list + ", " + item;

    /// <summary>
    /// The one projection both readers use: <c>GUID</c>, <c>Name</c> and <c>Version</c> off a
    /// <c>BepInPlugin</c>-shaped object, by property name. BepInEx declares all three as public
    /// instance properties (<c>Version</c> is a <c>SemanticVersioning.Version</c>, so it is
    /// stringified exactly as the old <c>meta.Version?.ToString()</c> did). A property getter that
    /// throws propagates to <see cref="Resolve"/>, which treats it as a non-answer.
    /// </summary>
    private static PluginIdentity? FromAttributeLike(object? attribute, string source)
    {
        if (attribute == null) return null;
        var type = attribute.GetType();
        return new PluginIdentity(
            ReadProperty(type, attribute, "GUID"),
            ReadProperty(type, attribute, "Name"),
            ReadProperty(type, attribute, "Version"),
            source);
    }

    private static string ReadProperty(Type type, object instance, string property)
    {
        var found = type.GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        return found?.GetValue(instance)?.ToString() ?? "";
    }
}
