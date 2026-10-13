using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Progenitor.Compat;

// MIRROR of src/Progenitor.Bootstrap/PluginRewriter.cs, byte-for-byte apart from this header and
// the namespace. Same reason as DriftMap.cs and ReloadAllowPolicy.cs: Bootstrap's ReloaderHost is
// the reloader that runs in production and it cannot compile-reference Core, so the load path's
// rewriter has to exist on both sides. Both copies are compiled by Progenitor.Tests and
// CompatMirrorTests compares them line for line. Edit Core's copy first, then re-mirror.
//
// Game-free: Mono.Cecil is BepInEx's own (BepInEx/core/Mono.Cecil.dll, already on Core's and
// Bootstrap's compile-time reference set via Directory.Build.targets), and nothing here touches
// ProjectM, Unity, Il2CppInterop or a live world. That is what lets Progenitor.Tests drive the
// whole pass against synthetic assemblies with no V Rising install.

/// <summary>One rule that matched, and how many instruction operands it covered.</summary>
public sealed class RewriteHit
{
    public RewriteHit(DriftRule rule) => Rule = rule;

    public DriftRule Rule { get; }
    public int Sites { get; set; }

    public string Describe() =>
        Rule.Describe() + ", " + Sites.ToString(CultureInfo.InvariantCulture) + " site"
        + (Sites == 1 ? "" : "s");
}

/// <summary>
/// What one <see cref="PluginRewriter.Rewrite"/> pass did. Pure data: the caller logs it, and
/// nothing here reaches the file system. Both SHA-256s are recorded so the log answers "what did
/// the operator vet" and "what actually ran" separately (research compat-rewriter.md §4.1) — the
/// rewritten hash is a log field only and is <b>never</b> gated on, compared, or put in a
/// manifest (Cecil 0.11.4 has no deterministic-write mode, §4.3).
/// </summary>
public sealed class RewriteReport
{
    /// <summary>How many detail lines the block prints before it truncates.</summary>
    public const int MaxDetailLines = 20;

    private readonly List<RewriteHit> _hits = new();
    private readonly List<string> _skipped = new();
    private readonly HashSet<string> _refusedSites = new(StringComparer.Ordinal);

    public RewriteReport(string fileName, RewriteMode mode)
    {
        FileName = fileName ?? "";
        Mode = mode;
    }

    public string FileName { get; }
    public RewriteMode Mode { get; }

    /// <summary>SHA-256 of the bytes the allowlist decided on. Always set.</summary>
    public string OriginalSha { get; set; } = "";

    /// <summary>SHA-256 of the bytes that reach LoadFromStream. Equal to <see cref="OriginalSha"/> unless applied.</summary>
    public string LoadedSha { get; set; } = "";

    /// <summary>The map line, or "" when there was no map for this build.</summary>
    public string MapLine { get; set; } = "";

    /// <summary>True when the returned array is the rewritten image rather than the input.</summary>
    public bool Applied { get; set; }

    /// <summary>Set when the pass refused; the original bytes were returned.</summary>
    public string Failure { get; set; } = "";

    /// <summary>Rules that matched at least one site.</summary>
    public IReadOnlyList<RewriteHit> Hits => _hits;

    /// <summary>Sites a rule matched but the pass would not touch, with the reason.</summary>
    public IReadOnlyList<string> Skipped => _skipped;

    public int SiteCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _hits.Count; i++) n += _hits[i].Sites;
            return n;
        }
    }

    public RewriteHit Hit(DriftRule rule)
    {
        for (int i = 0; i < _hits.Count; i++)
            if (ReferenceEquals(_hits[i].Rule, rule)) return _hits[i];
        var hit = new RewriteHit(rule);
        _hits.Add(hit);
        return hit;
    }

    /// <summary>
    /// The <b>sites</b> the pass declined to rewrite — <see cref="PluginRewriter.MemberSiteKey"/>
    /// for an instruction, <see cref="PluginRewriter.TypeRowSiteKey"/> for a TypeRef row. R34
    /// finding 3: the pass has five documented paths that leave the old key in the image
    /// <b>on purpose</b> (a frozen <c>ldtoken</c> row, a <c>typeMove</c> with no destination
    /// scope, a nested destination, an <c>ldflda</c> under <c>fieldToProperty</c>, a
    /// non-<c>call</c> property accessor), and <see cref="PluginRewriter"/>'s self-check used to
    /// read those surviving keys as proof that the rewrite had failed — discarding every rule
    /// that <i>did</i> land.
    ///
    /// R35 finding 6: the exemption used to be the rule's <c>from</c> key, which is a much
    /// bigger hole than the refusal that opened it. Four of those five paths refuse <b>one
    /// instruction</b>, not a rule — a plugin that reads <c>Health.Value</c> in ten places and
    /// takes its address in one had the self-check turned off for the other nine, so a retarget
    /// that silently did not land shipped as <c>Applied = true</c> and threw at JIT time. That is
    /// the exact failure the self-check exists to catch. Refusals are therefore recorded per
    /// site, and every other site the rule touched is still verified.
    /// </summary>
    public IReadOnlyCollection<string> RefusedSites => _refusedSites;

    public void Skip(string reason)
    {
        if (!string.IsNullOrEmpty(reason)) _skipped.Add(reason);
    }

    /// <summary>
    /// Record a refusal: the report line <b>and</b> the site, so the self-check does not then
    /// read that untouched instruction (or row) as a failure. Every refusal path goes through
    /// this, and every one of them passes the site it actually declined — a refusal that named
    /// the rule instead would exempt sites the pass never looked at.
    /// </summary>
    public void Skip(DriftRule? rule, string site, string reason)
    {
        if (!string.IsNullOrEmpty(site)) _refusedSites.Add(site);
        Skip((rule == null ? "" : rule.Describe() + ": ") + (reason ?? ""));
    }

    /// <summary>True when the block is worth printing at all.</summary>
    public bool HasAnythingToSay =>
        _hits.Count > 0 || _skipped.Count > 0 || Failure.Length > 0;

    /// <summary>
    /// The per-plugin log block. One line per fact, in the order the design fixes
    /// (compat-rewriter.md §2.4), capped at <see cref="MaxDetailLines"/> detail lines.
    /// </summary>
    public IReadOnlyList<string> Lines()
    {
        var lines = new List<string> { "Rewrite: " + FileName };
        lines.Add("  allowlist   sha256 " + Short(OriginalSha) + " (vetted)");
        string loadedNote = Applied ? "rewritten" :
            Mode == RewriteMode.DryRun ? "original; dry run" : "original";
        lines.Add("  loaded      sha256 " + Short(LoadedSha.Length > 0 ? LoadedSha : OriginalSha)
                  + " (" + loadedNote + ")");
        if (MapLine.Length > 0) lines.Add("  map         " + MapLine);
        lines.Add("  mode        " + CompatRewritePolicy.Text(Mode));

        int detail = 0;
        if (_hits.Count > 0)
        {
            string verb = Applied ? "applied    " : "would apply";
            lines.Add("  " + verb + " " + _hits.Count.ToString(CultureInfo.InvariantCulture)
                      + " rule" + (_hits.Count == 1 ? "" : "s") + ", "
                      + SiteCount.ToString(CultureInfo.InvariantCulture) + " site"
                      + (SiteCount == 1 ? "" : "s"));
            for (int i = 0; i < _hits.Count && detail < MaxDetailLines; i++, detail++)
                lines.Add("              " + _hits[i].Describe());
        }
        if (_skipped.Count > 0)
        {
            lines.Add("  skipped     " + _skipped.Count.ToString(CultureInfo.InvariantCulture)
                      + " site" + (_skipped.Count == 1 ? "" : "s"));
            for (int i = 0; i < _skipped.Count && detail < MaxDetailLines; i++, detail++)
                lines.Add("              " + _skipped[i]);
        }
        if (detail >= MaxDetailLines) lines.Add("              ... truncated");
        if (Failure.Length > 0)
            lines.Add("  refused     " + Failure + "; loaded the original bytes");
        if (Applied)
            lines.Add("  note        rewritten in memory only; the file on disk is unchanged");
        return lines;
    }

    private static string Short(string sha) => sha.Length >= 16 ? sha.Substring(0, 16) : sha;
}

/// <summary>
/// M4 item 2: absorb interop drift in a plugin's IL at load time.
///
/// <c>byte[] in, byte[] out</c>, with no file system and no clock, called between the reload
/// allowlist gate and <c>LoadFromStream</c>. The allowlist decision is taken on the <b>original</b>
/// bytes and is never re-taken, and the rewritten image is never hashed for a gate and never
/// written to disk (research compat-rewriter.md §4.1).
///
/// Scope, from §1.4: <b>it retargets metadata references and never edits an operand that is a
/// number.</b> Interop regeneration already absorbs every field-offset change (§1.2), and a baked
/// stride copied out of the game's metadata carries no metadata token for any rewriter to find — that
/// stays the plugin author's problem, forever.
///
/// Every failure — Cecil absent or version-skewed, an unreadable image, an Authenticode
/// signature, a rule whose target cannot be built, a <c>Write</c> that throws, a self-check that
/// fails — returns the <b>input array by reference</b> with one line in the report (§2.6). The
/// pass can only ever turn a working load into a working load, or a broken load into a
/// possibly-working load; it must never be the reason something that used to load stopped.
///
/// Collectible-ALC discipline (§4.2): synchronous on the loading thread, one <c>using</c> scope,
/// a fresh resolver per pass that resolves nothing, and <b>only a byte[] leaves the method</b> —
/// no <c>TypeReference</c>, no <c>ModuleDefinition</c>, nothing in a static.
/// </summary>
public static class PluginRewriter
{
    /// <summary>Opcodes whose operand is a <see cref="FieldReference"/>.</summary>
    private static bool IsFieldOpcode(OpCode op) =>
        op == OpCodes.Ldfld || op == OpCodes.Ldflda || op == OpCodes.Stfld
        || op == OpCodes.Ldsfld || op == OpCodes.Ldsflda || op == OpCodes.Stsfld;

    /// <summary>Opcodes whose operand is a <see cref="MethodReference"/>.</summary>
    private static bool IsMethodOpcode(OpCode op) =>
        op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj
        || op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn;

    /// <summary>SHA-256 as lowercase hex. Same encoding the reload allowlist logs.</summary>
    public static string Sha256Hex(byte[] bytes)
    {
        if (bytes == null) return "";
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// True when the PE carries an Authenticode certificate. Such a signature was attached to
    /// exactly those bytes, so the pass refuses to rewrite the image at all (§4.3).
    /// </summary>
    public static bool HasAuthenticodeSignature(byte[] image)
    {
        try
        {
            if (image == null || image.Length < 0x40) return false;
            int lfanew = BitConverter.ToInt32(image, 0x3C);
            if (lfanew <= 0 || lfanew + 24 >= image.Length) return false;
            if (image[lfanew] != (byte)'P' || image[lfanew + 1] != (byte)'E') return false;
            int optional = lfanew + 24;
            if (optional + 2 > image.Length) return false;
            ushort magic = BitConverter.ToUInt16(image, optional);
            int directories = optional + (magic == 0x20b ? 112 : 96);
            int certificate = directories + 4 * 8; // data directory 4 = Certificate Table
            if (certificate + 8 > image.Length) return false;
            return BitConverter.ToUInt32(image, certificate + 4) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The simple names on the image's <c>AssemblyRef</c> table, in table order. This is the one
    /// edge the plugin-local dependency walk follows (job FCOMPAT3): the walker turns a name into
    /// a candidate file inside the plugin's own folder and refuses everything else, so the graph
    /// is bounded by what the folder actually contains. Never throws — an unreadable image has
    /// no dependencies as far as the walk is concerned, which is the same "load the original"
    /// outcome every other failure has (§2.6).
    /// </summary>
    public static IReadOnlyList<string> ReferencedAssemblyNames(byte[]? image)
    {
        var names = new List<string>();
        if (image == null || image.Length == 0) return names;
        try
        {
            var parameters = new ReaderParameters
            {
                InMemory = true,
                ReadSymbols = false,
                ReadWrite = false,
                AssemblyResolver = new NoResolver(),
            };
            using var stream = new MemoryStream(image, writable: false);
            using var assembly = AssemblyDefinition.ReadAssembly(stream, parameters);
            foreach (var reference in assembly.MainModule.AssemblyReferences)
            {
                string name = reference?.Name ?? "";
                if (name.Length > 0) names.Add(name);
            }
        }
        catch
        {
            names.Clear();
        }
        return names;
    }

    /// <summary>
    /// Rewrite <paramref name="image"/> against <paramref name="map"/>. Returns the input array
    /// by reference whenever nothing is applied — the same bytes, not a re-serialization that
    /// happens to be equivalent.
    /// </summary>
    public static byte[] Rewrite(
        string fileName,
        byte[] image,
        DriftMap? map,
        RewriteMode mode,
        out RewriteReport report,
        IReadOnlyCollection<string>? interopAssemblies = null)
    {
        report = new RewriteReport(fileName ?? "", mode);
        if (image == null || image.Length == 0)
        {
            report.Failure = "empty image";
            return image ?? Array.Empty<byte>();
        }
        report.OriginalSha = Sha256Hex(image);
        report.LoadedSha = report.OriginalSha;
        if (mode == RewriteMode.Off) return image;
        if (map == null) return image;
        report.MapLine = DriftMapStore.FormatMapLine(map, 0);
        if (!map.HasApplicableRules) return image;
        if (HasAuthenticodeSignature(image))
        {
            report.Failure = "the image is Authenticode-signed";
            return image;
        }

        try
        {
            return RewriteCore(image, map, mode, report, interopAssemblies);
        }
        catch (Exception e)
        {
            // A Cecil version skew from a BepInEx upgrade shows up here as a TypeLoadException or
            // a MissingMethodException (§4.4). Degrade to report-only, never to a refused plugin.
            report.Failure = e.GetType().Name + ": " + e.Message;
            report.Applied = false;
            report.LoadedSha = report.OriginalSha;
            return image;
        }
    }

    private static byte[] RewriteCore(
        byte[] image,
        DriftMap map,
        RewriteMode mode,
        RewriteReport report,
        IReadOnlyCollection<string>? interopAssemblies)
    {
        var parameters = new ReaderParameters
        {
            // InMemory copies the stream, so nothing holds a handle on anything: the difference
            // between a rewrite and a plugin DLL its author can no longer overwrite.
            InMemory = true,
            ReadSymbols = false,
            ReadWrite = false,
            // A fresh resolver per pass that resolves nothing. Never DefaultAssemblyResolver
            // (its cache would accumulate modules for the process lifetime) and never anything
            // that consults a loaded ALC — that would take a hard reference from non-collectible
            // Cecil into a collectible LoaderAllocator, which is the Harmony pin with different
            // nouns (§4.2). Nothing in this pass calls Resolve(), so a null resolver is enough.
            AssemblyResolver = new NoResolver(),
        };

        using var input = new MemoryStream(image, writable: false);
        using var assembly = AssemblyDefinition.ReadAssembly(input, parameters);
        var module = assembly.MainModule;

        // Pass 1, read-only: find every operand a rule covers, keyed on the OLD names. Collected
        // before any mutation so a type rule cannot change the key a member rule is looked up by
        // (the design's stage order, §2.3, expressed as "decide first, then mutate").
        // `frozen` collects the TypeRef objects a paired ldtoken reached: those rows may not be
        // rewritten at all (R33 finding 4).
        var memberSites = new List<Site>();
        var frozen = new List<TypeReference>();
        CollectSites(module, map, memberSites, frozen, report);

        // Pass 2: type references (rename and move) — the whole TypeRef row, so every use follows.
        int typeSites = ApplyTypeRules(module, map, frozen, report, interopAssemblies);

        // Pass 3: member operands.
        int memberSitesApplied = ApplyMemberRules(module, memberSites, report);

        if (typeSites == 0 && memberSitesApplied == 0) return image;
        if (mode != RewriteMode.Apply)
        {
            // Dry run: the full analysis and the full report, and the ORIGINAL array back.
            report.Applied = false;
            return image;
        }

        byte[] rewritten;
        using (var output = new MemoryStream())
        {
            assembly.Write(output);
            rewritten = output.ToArray();
        }

        // Self-check on the rewritten image, not on the original (the finders-after-rewriters
        // ordering property of §2.2): every key the map claimed
        // to fix must be gone. A claimed fix that did not land discards the whole rewrite.
        //
        // R34 finding 3: "claimed" is the load-bearing word. The sites the pass refused above
        // were never claimed — the design requires their old keys to survive — so they are
        // excluded here. Without that exclusion one unrewritable site (an ldflda, a frozen
        // ldtoken row) discarded the two plain renames beside it and the plugin loaded
        // unrewritten, which is the crash the feature exists to prevent, with no operator remedy.
        //
        // R35 finding 6: the exclusion is a set of SITES, not of rule keys. A rule that landed in
        // nine places and was refused in one is exactly the case that still needs checking in the
        // other nine, and keying the skip on the rule turned the check off for all ten.
        if (!SelfCheck(rewritten, map, report.RefusedSites, out var stillThere))
        {
            report.Failure = "self-check failed: " + stillThere + " still references the old member";
            report.Applied = false;
            report.LoadedSha = report.OriginalSha;
            return image;
        }

        report.Applied = true;
        report.LoadedSha = Sha256Hex(rewritten);
        return rewritten;
    }

    /// <summary>One instruction a member rule covers.</summary>
    private sealed class Site
    {
        public Site(MethodBody body, Instruction instruction, DriftRule rule, string key)
        {
            Body = body;
            Instruction = instruction;
            Rule = rule;
            Key = key;
        }

        public MethodBody Body { get; }
        public Instruction Instruction { get; }
        public DriftRule Rule { get; }

        /// <summary>This instruction's identity across the write (<see cref="MemberSiteKey"/>).</summary>
        public string Key { get; }
    }

    /// <summary>
    /// Every method in the module, in one fixed order, numbered. Both the collect walk and the
    /// self-check walk go through this, which is what makes <see cref="MemberSiteKey"/> mean the
    /// same instruction in the input module and in the image Cecil wrote: the pass adds and
    /// removes no type, no method and no instruction, so the n-th method of this walk and the
    /// n-th instruction inside it are the same code on both sides.
    /// </summary>
    private static IEnumerable<KeyValuePair<int, MethodDefinition>> WalkMethods(ModuleDefinition module)
    {
        int index = 0;
        foreach (var type in module.GetTypes())
            foreach (var method in type.Methods)
                yield return new KeyValuePair<int, MethodDefinition>(index++, method);
    }

    /// <summary>
    /// The identity of one instruction site: its method's position in <see cref="WalkMethods"/>
    /// and the instruction's IL offset, with the declaring type and method name carried along so
    /// a refusal is readable in a debugger. Names are safe to include because the pass rewrites
    /// <b>references</b> only — a TypeDef's name and a MethodDef's name are the same before and
    /// after — while a method's <c>FullName</c> is not, since a <c>typeMove</c> in its signature
    /// would rewrite it out from under the key.
    ///
    /// The offset is stable for the same reason the ordinal is: every substitution this pass
    /// makes is size-preserving (each of ldfld/ldsfld/stfld/stsfld/ldflda/call/callvirt/ldtoken
    /// is one byte plus a four-byte token), and a rename touches an operand row, not the body.
    /// If that ever stopped holding, a refused site would stop matching and the self-check would
    /// fail — the image is refused and the original bytes load. Wrong, but in the safe direction.
    /// </summary>
    private static string MemberSiteKey(int methodIndex, MethodDefinition method, Instruction instruction) =>
        methodIndex.ToString(CultureInfo.InvariantCulture) + " "
        + (method.DeclaringType == null ? "" : method.DeclaringType.FullName) + "::" + method.Name
        + " @IL_" + instruction.Offset.ToString("x4", CultureInfo.InvariantCulture);

    /// <summary>
    /// The identity of one TypeRef row. A type rule is refused for the whole row and not for a
    /// use of it — Cecil hands out one <c>TypeReference</c> object per row and every use follows
    /// it — so the row's own <c>assembly|type</c> is the site, and the self-check walks rows.
    /// </summary>
    private static string TypeRowSiteKey(TypeReference reference) =>
        "typeref " + AssemblyOf(reference) + "|" + TypeText(reference);

    private static void CollectSites(
        ModuleDefinition module,
        DriftMap map,
        List<Site> sites,
        List<TypeReference> frozen,
        RewriteReport report)
    {
        foreach (var walked in WalkMethods(module))
        {
            var method = walked.Value;
            {
                if (!method.HasBody) continue;
                var body = method.Body;
                foreach (var instruction in body.Instructions)
                {
                    var op = instruction.OpCode;
                    if (IsFieldOpcode(op) && instruction.Operand is FieldReference field)
                    {
                        var rule = map.Find(KeyOf(field));
                        if (rule != null && rule.IsApplicable && !rule.IsTypeRule)
                            sites.Add(new Site(body, instruction, rule,
                                MemberSiteKey(walked.Key, method, instruction)));
                        continue;
                    }
                    if (IsMethodOpcode(op) && instruction.Operand is MethodReference method2)
                    {
                        var rule = map.Find(KeyOf(method2));
                        if (rule != null && rule.IsApplicable && !rule.IsTypeRule)
                            sites.Add(new Site(body, instruction, rule,
                                MemberSiteKey(walked.Key, method, instruction)));
                        continue;
                    }
                    if (op == OpCodes.Ldtoken)
                    {
                        // §4.6: a ctx.Patches registration pairs an ldtoken (rewritable) with an
                        // ldstr method name (not rewritable). Rewriting only the type half aims a
                        // patch at the right type and the wrong name. Detect the pairing and skip
                        // BOTH halves rather than emit a confusing half-fix.
                        //
                        // R33 finding 4: recording a skip was not enough. Cecil hands out ONE
                        // TypeReference object per TypeRef row, and the ldtoken operand is that
                        // object, so ApplyTypeRules mutating it rewrote the ldtoken anyway and
                        // the report then claimed both things at once. The row is frozen here
                        // instead; a per-use exemption is impossible (one row serves every use),
                        // so the rule is refused for the whole module — the safe direction, and
                        // the one the report already described.
                        if (instruction.Operand is TypeReference token && LooksLikeAPatchRegistration(instruction))
                        {
                            var rule = map.Find(DriftRule.TypeKey(AssemblyOf(token), TypeText(token)));
                            if (rule != null && rule.IsApplicable)
                            {
                                var row = RootReference(token);
                                if (row != null && !Contains(frozen, row)) frozen.Add(row);
                                report.Skip("ldtoken " + rule.Describe()
                                            + ": paired with an ldstr member name a rewrite cannot follow; both halves left alone");
                            }
                        }
                        else if (instruction.Operand is MethodReference tokenMethod)
                        {
                            var rule = map.Find(KeyOf(tokenMethod));
                            if (rule != null && rule.IsApplicable && !rule.IsTypeRule)
                                sites.Add(new Site(body, instruction, rule,
                                    MemberSiteKey(walked.Key, method, instruction)));
                        }
                        else if (instruction.Operand is FieldReference tokenField)
                        {
                            var rule = map.Find(KeyOf(tokenField));
                            if (rule != null && rule.IsApplicable && !rule.IsTypeRule)
                                sites.Add(new Site(body, instruction, rule,
                                    MemberSiteKey(walked.Key, method, instruction)));
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// True when an <c>ldtoken</c> is followed within a few instructions by an <c>ldstr</c> and a
    /// call — the shape a Harmony patch registration has (§4.6). Deliberately blunt: this only
    /// ever suppresses a rewrite, so a false positive costs a rule, never correctness.
    /// </summary>
    private static bool LooksLikeAPatchRegistration(Instruction ldtoken)
    {
        bool sawString = false;
        var current = ldtoken.Next;
        for (int i = 0; i < 6 && current != null; i++, current = current.Next)
        {
            if (current.OpCode == OpCodes.Ldstr) sawString = true;
            if (sawString && (current.OpCode == OpCodes.Call || current.OpCode == OpCodes.Callvirt))
                return true;
        }
        return false;
    }

    private static int ApplyTypeRules(
        ModuleDefinition module,
        DriftMap map,
        List<TypeReference> frozen,
        RewriteReport report,
        IReadOnlyCollection<string>? interopAssemblies)
    {
        int applied = 0;
        foreach (var reference in module.GetTypeReferences())
        {
            string assembly = AssemblyOf(reference);
            if (assembly.Length == 0) continue;
            var rule = map.Find(DriftRule.TypeKey(assembly, TypeText(reference)));
            if (rule == null || !rule.IsApplicable || !rule.IsTypeRule) continue;
            if (Contains(frozen, reference))
            {
                report.Skip(rule, TypeRowSiteKey(reference),
                    "this TypeRef row is used by a patch registration whose member name a rewrite "
                    + "cannot follow; the rule is refused for the whole module");
                continue;
            }

            if (rule.Kind == DriftKind.TypeMove)
            {
                var scope = ScopeFor(module, rule.ToAssembly, interopAssemblies);
                if (scope == null)
                {
                    report.Skip(rule, TypeRowSiteKey(reference), rule.ToAssembly
                        + " is neither referenced by this image nor an interop assembly of this install; "
                        + "refused rather than introducing a new dependency");
                    continue;
                }
                reference.Scope = scope;
            }
            else
            {
                if (!SplitFullName(rule.ToType, out var ns, out var name))
                {
                    report.Skip(rule, TypeRowSiteKey(reference), "destination type name is not usable");
                    continue;
                }
                // A nested destination would need a new declaring-type reference; out of this
                // cut's remit, and refusing is always safe.
                if (rule.ToType.IndexOf('/') >= 0 && reference.DeclaringType == null)
                {
                    report.Skip(rule, TypeRowSiteKey(reference), "destination is a nested type; not rewritten");
                    continue;
                }
                if (!string.Equals(rule.ToAssembly, assembly, StringComparison.Ordinal))
                {
                    var scope = ScopeFor(module, rule.ToAssembly, interopAssemblies);
                    if (scope == null)
                    {
                        report.Skip(rule, TypeRowSiteKey(reference), rule.ToAssembly
                            + " is neither referenced by this image nor an interop assembly of this install; "
                            + "refused rather than introducing a new dependency");
                        continue;
                    }
                    reference.Scope = scope;
                }
                reference.Namespace = ns;
                reference.Name = name;
            }
            report.Hit(rule).Sites++;
            applied++;
        }
        return applied;
    }

    private static int ApplyMemberRules(ModuleDefinition module, List<Site> sites, RewriteReport report)
    {
        int applied = 0;
        foreach (var site in sites)
        {
            var rule = site.Rule;
            bool ok;
            switch (rule.Kind)
            {
                case DriftKind.MemberRename:
                    ok = ApplyRename(site, report);
                    break;
                case DriftKind.FieldToProperty:
                    ok = ApplyFieldToProperty(module, site, report);
                    break;
                case DriftKind.PropertyToField:
                    ok = ApplyPropertyToField(module, site, report);
                    break;
                default:
                    report.Skip(rule, site.Key, "rule kind is report-only in this build");
                    ok = false;
                    break;
            }
            if (!ok) continue;
            report.Hit(rule).Sites++;
            applied++;
        }
        return applied;
    }

    private static bool ApplyRename(Site site, RewriteReport report)
    {
        var rule = site.Rule;
        if (rule.ToName.Length == 0)
        {
            report.Skip(rule, site.Key, "destination has no member name");
            return false;
        }
        // R33 finding 8: this check belongs above the switch, not inside the method branch. A
        // field's key signature IS its field type, so `T|X|System.Int32 -> T|Y|System.Single` is
        // a rename AND a retype; renaming the operand alone emits a MemberRef for the new name
        // with the old type, SelfCheck passes (the old key is gone), the report claims a fix and
        // every use throws MissingFieldException at JIT time. Both operand kinds assert the same
        // invariant; only one of them used to check it.
        if (!string.Equals(rule.ToSignature, rule.FromSignature, StringComparison.Ordinal))
        {
            report.Skip(rule, site.Key, "a rename may not change the signature");
            return false;
        }
        // Cecil writes the MemberRef row from the reference object, so the whole retarget for a
        // rename inside one declaring type is the name — no resolution against the new interop
        // assembly, and therefore no resolver and no cache (§4.2).
        switch (site.Instruction.Operand)
        {
            case MethodReference method:
                method.Name = rule.ToName;
                return true;
            case FieldReference field:
                field.Name = rule.ToName;
                return true;
            default:
                return false;
        }
    }

    private static bool ApplyFieldToProperty(ModuleDefinition module, Site site, RewriteReport report)
    {
        var rule = site.Rule;
        if (site.Instruction.Operand is not FieldReference field) return false;
        var op = site.Instruction.OpCode;
        if (op == OpCodes.Ldflda || op == OpCodes.Ldsflda)
        {
            // A property has no address. There is no equivalent instruction, and inventing a
            // temporary local would change the meaning of the surrounding code.
            report.Skip(rule, site.Key, "ldflda has no property equivalent; the call site is left alone");
            return false;
        }
        bool store = op == OpCodes.Stfld || op == OpCodes.Stsfld;
        bool instance = op == OpCodes.Ldfld || op == OpCodes.Stfld;
        string name = (store ? "set_" : "get_") + (rule.ToName.Length > 0 ? rule.ToName : field.Name);

        // Accessor selection is opcode-driven: ldfld/ldsfld take get, stfld/stsfld take set. The
        // evaluation-stack shape matches in both directions for an instance field (obj -> value).
        var accessor = new MethodReference(name, store ? module.TypeSystem.Void : field.FieldType, field.DeclaringType)
        {
            HasThis = instance,
        };
        if (store) accessor.Parameters.Add(new ParameterDefinition(field.FieldType));

        site.Instruction.OpCode = OpCodes.Call;
        site.Instruction.Operand = module.ImportReference(accessor);
        return true;
    }

    private static bool ApplyPropertyToField(ModuleDefinition module, Site site, RewriteReport report)
    {
        var rule = site.Rule;
        if (site.Instruction.Operand is not MethodReference accessor) return false;
        var op = site.Instruction.OpCode;
        if (op != OpCodes.Call && op != OpCodes.Callvirt)
        {
            report.Skip(rule, site.Key, "a property accessor reached by " + op.Name + " is not rewritten");
            return false;
        }
        bool store = accessor.Name.StartsWith("set_", StringComparison.Ordinal);
        bool getter = accessor.Name.StartsWith("get_", StringComparison.Ordinal);
        if (!store && !getter)
        {
            report.Skip(rule, site.Key, "not a property accessor");
            return false;
        }
        var fieldType = store
            ? (accessor.Parameters.Count > 0 ? accessor.Parameters[accessor.Parameters.Count - 1].ParameterType : null)
            : accessor.ReturnType;
        if (fieldType == null)
        {
            report.Skip(rule, site.Key, "the accessor's field type could not be read");
            return false;
        }
        string name = rule.ToName.Length > 0 ? rule.ToName : accessor.Name.Substring(4);
        var field = new FieldReference(name, fieldType, accessor.DeclaringType);
        site.Instruction.OpCode = accessor.HasThis
            ? (store ? OpCodes.Stfld : OpCodes.Ldfld)
            : (store ? OpCodes.Stsfld : OpCodes.Ldsfld);
        site.Instruction.Operand = module.ImportReference(field);
        return true;
    }

    /// <summary>
    /// The <see cref="AssemblyNameReference"/> for <paramref name="wanted"/>. Reuses one the
    /// module already has; otherwise it may be added <b>only</b> when
    /// <paramref name="interopAssemblies"/> — the names of the assemblies in this install's
    /// <c>BepInEx/interop</c> folder — actually contains it. Anything else returns null and the
    /// caller refuses the rule.
    ///
    /// R33 finding 2: this used to mint an <c>AssemblyRef</c> for whatever a rule named. A
    /// single <c>typeMove</c> to <c>ProjectMShim</c> therefore added that row and repointed every
    /// use of the type, and resolution then fell through to the default context's probing — an
    /// attacker who can write the drift map can write a DLL beside it. A move to an assembly the
    /// plugin never referenced and the install does not have is not drift; it is a new
    /// dependency, and refusing is always safe.
    /// </summary>
    private static IMetadataScope? ScopeFor(
        ModuleDefinition module,
        string wanted,
        IReadOnlyCollection<string>? interopAssemblies)
    {
        if (string.IsNullOrEmpty(wanted)) return null;
        AssemblyNameReference? source = null;
        foreach (var existing in module.AssemblyReferences)
        {
            if (string.Equals(existing.Name, wanted, StringComparison.Ordinal)) return existing;
            // The interop set is versioned as a unit, so any game reference's version is the
            // destination's; keep the first as the version donor.
            if (source == null && DriftMap.IsGameAssemblyName(existing.Name)) source = existing;
        }
        if (interopAssemblies == null || !ContainsName(interopAssemblies, wanted)) return null;
        var added = new AssemblyNameReference(wanted, source?.Version ?? new Version(0, 0, 0, 0));
        module.AssemblyReferences.Add(added);
        return added;
    }

    private static bool ContainsName(IReadOnlyCollection<string> names, string wanted)
    {
        foreach (string name in names)
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Reference equality: Cecil hands out one object per TypeRef row.</summary>
    private static bool Contains(List<TypeReference> rows, TypeReference reference)
    {
        for (int i = 0; i < rows.Count; i++)
            if (ReferenceEquals(rows[i], reference)) return true;
        return false;
    }

    /// <summary>The underlying TypeRef row behind a specification, or null.</summary>
    private static TypeReference? RootReference(TypeReference? type)
    {
        var current = type;
        for (int depth = 0; depth < 16 && current != null; depth++)
        {
            if (current is GenericInstanceType instance) { current = instance.ElementType; continue; }
            if (current is TypeSpecification spec) { current = spec.ElementType; continue; }
            return current;
        }
        return null;
    }

    /// <summary>
    /// Re-read the written image and confirm no applied rule's <c>from</c> key survives.
    /// <paramref name="refusedSites"/> carries the <b>sites</b> the pass deliberately left alone
    /// (<see cref="RewriteReport.RefusedSites"/>): those instructions and rows are not evidence of
    /// a failed rewrite. Every other site is checked, including the other sites of a rule that was
    /// refused somewhere else — R35 finding 6, and the reason this is a site set and not a key
    /// set: one <c>ldflda</c> a property cannot answer must not turn the check off for the nine
    /// plain reads beside it.
    /// </summary>
    private static bool SelfCheck(
        byte[] rewritten,
        DriftMap map,
        IReadOnlyCollection<string> refusedSites,
        out string stillThere)
    {
        stillThere = "";
        var parameters = new ReaderParameters
        {
            InMemory = true,
            ReadSymbols = false,
            ReadWrite = false,
            AssemblyResolver = new NoResolver(),
        };
        using var stream = new MemoryStream(rewritten, writable: false);
        using var assembly = AssemblyDefinition.ReadAssembly(stream, parameters);
        var module = assembly.MainModule;

        foreach (var reference in module.GetTypeReferences())
        {
            var rule = map.Find(DriftRule.TypeKey(AssemblyOf(reference), TypeText(reference)));
            if (rule != null && rule.IsApplicable && rule.IsTypeRule
                && !WasRefused(refusedSites, TypeRowSiteKey(reference)))
            {
                stillThere = rule.Describe() + " (" + TypeRowSiteKey(reference) + ")";
                return false;
            }
        }
        foreach (var walked in WalkMethods(module))
        {
            var method = walked.Value;
            {
                if (!method.HasBody) continue;
                foreach (var instruction in method.Body.Instructions)
                {
                    DriftRule? rule = null;
                    if (instruction.Operand is FieldReference field && IsFieldOpcode(instruction.OpCode))
                        rule = map.Find(KeyOf(field));
                    else if (instruction.Operand is MethodReference reference && IsMethodOpcode(instruction.OpCode))
                        rule = map.Find(KeyOf(reference));
                    if (rule == null || !rule.IsApplicable || rule.IsTypeRule) continue;
                    string site = MemberSiteKey(walked.Key, method, instruction);
                    if (WasRefused(refusedSites, site)) continue;
                    stillThere = rule.Describe() + " (" + site + ")";
                    return false;
                }
            }
        }
        return true;
    }

    private static bool WasRefused(IReadOnlyCollection<string>? refusedSites, string site)
    {
        if (refusedSites == null || refusedSites.Count == 0) return false;
        foreach (string refused in refusedSites)
            if (string.Equals(refused, site, StringComparison.Ordinal)) return true;
        return false;
    }

    // ---- key rendering -------------------------------------------------------------------
    // The strings below must match Progenitor.Resilience.MetadataScan's decoded form exactly:
    // a rule keys on MemberBinding.Key, and the generator writes those keys with
    // System.Reflection.Metadata while this side reads them with Cecil. CompatKeyTests pins the
    // two renderings against each other.
    //
    // R34 finding 10: the DECLARING type is the half where the two readers see different things.
    // The scan reduces a TypeSpec parent to the open type it instantiates (NativeArray`1); Cecil
    // hands this side the instantiated GenericInstanceType (NativeArray`1<Entity>). Both now go
    // through DriftRule.Key, which applies the one shared reduction (DriftRule.OpenTypeText), so
    // "fixable by rewrite" is a claim the rewriter can honor. Signatures are NOT reduced: both
    // sides render those instantiated, and that is the identity a rule needs.

    /// <summary><c>Assembly|Type|Name|Signature</c> for a field operand.</summary>
    public static string KeyOf(FieldReference field) =>
        DriftRule.Key(AssemblyOf(field.DeclaringType), TypeText(field.DeclaringType), field.Name,
            TypeText(field.FieldType));

    /// <summary><c>Assembly|Type|Name|Signature</c> for a method operand.</summary>
    public static string KeyOf(MethodReference method)
    {
        var element = method is GenericInstanceMethod generic ? generic.ElementMethod : method;
        var sb = new StringBuilder();
        sb.Append(TypeText(element.ReturnType));
        sb.Append('(');
        for (int i = 0; i < element.Parameters.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(TypeText(element.Parameters[i].ParameterType));
        }
        sb.Append(')');
        if (element.GenericParameters.Count > 0) sb.Append('`').Append(element.GenericParameters.Count);
        return DriftRule.Key(AssemblyOf(element.DeclaringType), TypeText(element.DeclaringType),
            element.Name, sb.ToString());
    }

    /// <summary>Simple assembly name a type reference resolves into, or "" for this module's own.</summary>
    public static string AssemblyOf(TypeReference? type)
    {
        var current = type;
        for (int depth = 0; depth < 16 && current != null; depth++)
        {
            if (current is GenericInstanceType instance) { current = instance.ElementType; continue; }
            if (current is TypeSpecification spec) { current = spec.ElementType; continue; }
            if (current.DeclaringType != null) { current = current.DeclaringType; continue; }
            return current.Scope is AssemblyNameReference reference ? reference.Name : "";
        }
        return "";
    }

    /// <summary>
    /// Type text in <c>MetadataScan.SignatureText</c>'s form: full names, <c>/</c> for nested,
    /// <c>[]</c> / <c>&amp;</c> / <c>*</c> suffixes, <c>G`1&lt;A&gt;</c> instantiations, and
    /// <c>!0</c> / <c>!!0</c> for generic parameters.
    /// </summary>
    public static string TypeText(TypeReference? type)
    {
        if (type == null) return "";
        switch (type)
        {
            case GenericParameter parameter:
                return (parameter.Type == GenericParameterType.Method ? "!!" : "!")
                       + parameter.Position.ToString(CultureInfo.InvariantCulture);
            case ArrayType array:
                return TypeText(array.ElementType) + "["
                       + new string(',', Math.Max(0, array.Rank - 1)) + "]";
            case ByReferenceType byRef:
                return TypeText(byRef.ElementType) + "&";
            case PointerType pointer:
                return TypeText(pointer.ElementType) + "*";
            case PinnedType pinned:
                return TypeText(pinned.ElementType) + " pinned";
            case RequiredModifierType required:
                return TypeText(required.ElementType);
            case OptionalModifierType optional:
                return TypeText(optional.ElementType);
            case SentinelType sentinel:
                return TypeText(sentinel.ElementType);
            case GenericInstanceType instance:
            {
                var sb = new StringBuilder(TypeText(instance.ElementType));
                sb.Append('<');
                for (int i = 0; i < instance.GenericArguments.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(TypeText(instance.GenericArguments[i]));
                }
                sb.Append('>');
                return sb.ToString();
            }
            case FunctionPointerType function:
                return "fnptr " + function.FullName;
        }
        if (type.DeclaringType != null) return TypeText(type.DeclaringType) + "/" + type.Name;
        return type.Namespace.Length == 0 ? type.Name : type.Namespace + "." + type.Name;
    }

    /// <summary>Split <c>Namespace.Name</c> (never a nested name) into its two halves.</summary>
    private static bool SplitFullName(string full, out string ns, out string name)
    {
        ns = "";
        name = full ?? "";
        if (string.IsNullOrEmpty(name)) return false;
        int slash = name.LastIndexOf('/');
        if (slash >= 0) name = name.Substring(slash + 1);
        int dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            ns = name.Substring(0, dot);
            name = name.Substring(dot + 1);
        }
        return name.Length > 0;
    }

    /// <summary>
    /// A resolver that resolves nothing. Nothing in this pass calls <c>Resolve()</c>, and a
    /// resolver that reads files (or worse, a loaded ALC) is exactly the pin §4.2 forbids.
    /// </summary>
    private sealed class NoResolver : IAssemblyResolver
    {
        public AssemblyDefinition? Resolve(AssemblyNameReference name) => null;

        public AssemblyDefinition? Resolve(AssemblyNameReference name, ReaderParameters parameters) => null;

        public void Dispose() { }
    }
}
