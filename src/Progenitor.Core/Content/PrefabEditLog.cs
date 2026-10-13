using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Content;

/// <summary>
/// Exact log lines for ordered prefab edits. Shared by the live registry and the unit tests so a
/// conflict or cycle string cannot drift from what operators grep in BepInEx log.
/// </summary>
internal static class PrefabEditLog
{
    public const string Prefix = "PrefabRegistry: ";

    /// <summary>
    /// The editor that would close a before/after cycle is not registered. <paramref name="cycleIds"/>
    /// is the plugin-id path including the repeat of the start (A -&gt; B -&gt; A).
    /// </summary>
    public static string CycleRefused(string pluginId, int guidHash, IReadOnlyList<string> cycleIds)
    {
        string path = FormatCycle(cycleIds);
        return Prefix + "refused edit from " + (pluginId ?? "") + " on guid " + guidHash
            + ": before/after cycle with " + path;
    }

    /// <summary>
    /// Two plugins wrote the same component of the same prefab. <paramref name="loserName"/> /
    /// <paramref name="loserId"/> ran first; <paramref name="winnerName"/> / <paramref name="winnerId"/>
    /// ran last and is what the game sees. <paramref name="reason"/> is one of the Reason* helpers.
    /// </summary>
    public static string Conflict(
        int guidHash,
        string component,
        string loserName,
        string loserId,
        string winnerName,
        string winnerId,
        string reason)
    {
        return Prefix + "conflict on prefab guid=" + guidHash
            + " component=" + (component ?? "")
            + ": " + FormatPlugin(loserName, loserId)
            + " vs " + FormatPlugin(winnerName, winnerId)
            + "; winner=" + FormatPlugin(winnerName, winnerId)
            + " because " + (reason ?? "");
    }

    public static string IdentityRefused(string pluginId, int guidHash, string typeName)
    {
        return Prefix + "refused structural write of " + (typeName ?? "")
            + " by " + (pluginId ?? "") + " on guid " + guidHash;
    }

    /// <summary>
    /// An editor reached for <c>handle.EntityManager</c> (AddComponent / RemoveComponent /
    /// buffer edits). Those writes are not snapshotted and are not restored on unload.
    /// Warning, once per editor. Grep <c>unrestorable structural</c>.
    /// </summary>
    public static string UnrestorableStructuralEdit(string pluginId, int guidHash) =>
        Prefix + "unrestorable structural edit via EntityManager by " + (pluginId ?? "")
        + " on guid " + guidHash
        + ": AddComponent, RemoveComponent and buffer edits are not captured and are not restored on unload";

    /// <summary>
    /// A restore pass ran some baselines and skipped a throwing one. The prefab is not
    /// vanilla. Warning. Grep <c>restore incomplete</c>.
    /// </summary>
    public static string RestoreIncomplete(int guidHash, int owed, int restored) =>
        Prefix + "restore incomplete on guid " + guidHash + ": restored "
        + restored + " of " + owed
        + " captured baseline(s); a throwing restore was skipped and the remaining values still ran. This prefab is not vanilla.";

    /// <summary>
    /// <c>Content.Edit</c> refused because the plugin's handshake declaration does not
    /// require the peer to have it (R19 finding 3). <paramref name="declared"/> is
    /// <c>none</c> or <c>{Level}/{Rule}</c>. The fix is always ServerMustHave + Exact or Minor.
    /// </summary>
    public static string CompatibilityRefused(string pluginId, int guidHash, string declared)
    {
        return Prefix + "refused edit from " + (pluginId ?? "") + " on guid " + guidHash
            + ": Content.Edit requires [NetworkCompatibility(ServerMustHave, Exact or Minor)] so both sides apply the same pass; this plugin declared "
            + (declared ?? "none")
            + ". Set [NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact)] (or Minor).";
    }

    /// <summary>
    /// Live handshake no longer requires the peer to have this plugin (Unrequire, or
    /// Require at NoNeedForSync / ClientOnly / None). Editors for that guid are dropped.
    /// One line per plugin, not per prefab.
    /// </summary>
    public static string HandshakeDropped(string pluginId, string declared)
    {
        return Prefix + "dropped edits from " + (pluginId ?? "")
            + ": handshake is " + (declared ?? "none")
            + "; Content.Edit requires ServerMustHave + Exact or Minor";
    }

    /// <summary>
    /// <c>PrefabRegistry.Edit</c> was given an owner that is not the registering plugin
    /// (a decoy type with a matching attribute, or a different plugin instance).
    /// </summary>
    public static string DecoyOwnerRefused(string pluginId, int guidHash)
    {
        return Prefix + "refused edit from " + (pluginId ?? "") + " on guid " + guidHash
            + ": owner is not the registering plugin (pass ctx.Owner, not a decoy with a matching attribute)";
    }

    /// <summary>
    /// Vanilla GUID edit accepted. Since job FEDW1 the edited component values do travel (the
    /// server ships them as a <c>PrfE</c> layer), so the remaining caveat is a client without
    /// Progenitor at all. Logged once per plugin + guid per Core generation.
    /// </summary>
    public static string VanillaEditLocalOnly(string pluginId, int guidHash)
    {
        return Prefix + "edit of vanilla prefab guid " + guidHash + " by " + (pluginId ?? "")
            + " changes no definition; the edited component values reach Progenitor clients as a server layer (PrfE). "
            + "A client without Progenitor keeps unedited stats.";
    }

    /// <summary>
    /// The server serialized a prefab's touched components and sent them. Info, one line per
    /// send. Grep <c>sent PrfE</c>.
    /// </summary>
    public static string SentEditLayer(int guidHash, IReadOnlyList<string>? components, int users)
    {
        int n = components?.Count ?? 0;
        return Prefix + "sent PrfE for " + guidHash + ": " + n + " component(s) to " + users
            + " user(s)" + (n == 0 ? "" : ": " + JoinSafeNames(components!));
    }

    /// <summary>
    /// A reader closure threw while serializing one component; the rest of the layer still goes
    /// out. Warning.
    /// </summary>
    public static string EditLayerReaderFailed(int guidHash, string? typeName, Exception e) =>
        Prefix + "PrfE reader for " + SafeName(typeName) + " on guid " + guidHash + " failed: " + e.Message;

    /// <summary>One component refused by a wire cap before the packet was built. Warning.</summary>
    public static string EditLayerRecordSkipped(int guidHash, string refusal) =>
        Prefix + "PrfE for guid " + guidHash + " skipped a " + (refusal ?? "");

    /// <summary>The packet could not be built at all (every record refused, or a cap). Warning.</summary>
    public static string EditLayerBuildFailed(int guidHash, string? error) =>
        Prefix + "could not build PrfE for guid " + guidHash + ": " + (error ?? "");

    /// <summary>
    /// The client wrote a server layer onto the prefab. Info; this is the line the two-process
    /// check greps. Matches the design's <c>applied PrfE for &lt;guid&gt;: ItemData (server layer)</c>.
    /// </summary>
    public static string AppliedEditLayer(int guidHash, IReadOnlyList<string>? components)
    {
        int n = components?.Count ?? 0;
        return Prefix + "applied PrfE for " + guidHash + ": "
            + (n == 0 ? "nothing" : JoinSafeNames(components!)) + " (server layer)";
    }

    /// <summary>
    /// One record of an otherwise good packet was refused. Warning, per record.
    ///
    /// <para>The type name is chosen by whichever server the player joined, so it goes through
    /// <see cref="PrefabEditWire.SanitiseTypeName"/> before it reaches the log and the line says
    /// when it was altered (job FEDW3, R51 finding 2: the old parser masked bytes with
    /// <c>&amp; 0x7F</c> and kept control characters, so a name could carry a newline and forge
    /// whole log lines — 960 a second at the packet rate the client had no bound on).</para>
    /// </summary>
    public static string EditLayerRecordRefused(int guidHash, string? typeName, string reason) =>
        Prefix + "refused PrfE record " + SafeName(typeName)
        + " for guid " + guidHash + ": " + (reason ?? "");

    /// <summary>
    /// One record was refused because a field's <em>value</em> is outside the domain the wire-safe
    /// list declares for it (job FEDW5, R53 finding 2): an enum member this build does not
    /// declare, a bit outside the declared mask, an integer outside its stated range, a
    /// non-finite float, or a bool byte that is neither 0 nor 1. Warning, once per type and field
    /// per Core generation — the value in the text is attacker-chosen, so the line must not be a
    /// per-packet one. The field name comes from Core's own list, never from the packet. Grep
    /// <c>value policy</c>.
    /// </summary>
    public static string EditLayerValueRefused(int guidHash, string? typeName, string? field, string reason) =>
        Prefix + "refused PrfE record " + SafeName(typeName) + " for guid " + guidHash
        + ": " + (string.IsNullOrEmpty(field) ? "" : field + " ") + (reason ?? "")
        + " [value policy, docs/edits-on-the-wire.md]";

    /// <summary>
    /// The bit mask a <c>NamedEnumBits</c> field's enum declares on <em>this</em> build differs
    /// from the dump-cited literal the wire-safe list carries beside it (job FEDW6, R54 finding 3).
    /// Warning, once per Core generation. The resolved mask is what is enforced — the literal is a
    /// cross-check, never a fallback — so this is not a refusal: it is the line that says the
    /// seed's citation has gone stale against the game and wants re-deriving from the dump. Grep
    /// <c>value domain mask</c>.
    /// </summary>
    public static string ValueDomainMaskDrift(
        string? typeFullName, string? field, string? enumTypeName, long resolved, long declared) =>
        Prefix + "value domain mask for " + SafeName(typeFullName) + "." + (field ?? "")
        + ": this build's " + (enumTypeName ?? "") + " declares 0x"
        + resolved.ToString("X", System.Globalization.CultureInfo.InvariantCulture)
        + ", the dump-cited literal in PrefabEditWireTypes says 0x"
        + declared.ToString("X", System.Globalization.CultureInfo.InvariantCulture)
        + ". This build's mask is what is enforced; re-derive the literal and its dump citation "
        + "(docs/edits-on-the-wire.md).";

    /// <summary>
    /// A component an editor wrote is not on Core's wire-safe list, so its values stay local
    /// (job FEDW4, R52 finding 1). Warning, once per type per Core generation, and it names the
    /// list rather than the shape rule because the list is the boundary. Grep <c>wire-safe</c>.
    /// </summary>
    public static string EditLayerTypeNotVouched(int guidHash, string? typeName, string reason) =>
        Prefix + "PrfE will not carry " + SafeName(typeName) + " (guid " + guidHash
        + "): " + (reason ?? "") + "; the edit still applies on this side";

    /// <summary>
    /// The receiver kept its own bytes for one or more reference-shaped fields of a component the
    /// server sent (job FEDW3, R51 finding 1). Info, once per type per Core generation — the
    /// honest case is a component like <c>ItemData</c> whose <c>Entity</c> field simply cannot
    /// travel, and it is normal, not a warning. Grep <c>preserved</c>.
    /// </summary>
    public static string EditLayerPreserved(int guidHash, string? typeName, IReadOnlyList<string>? fields)
    {
        int n = fields?.Count ?? 0;
        return Prefix + "PrfE " + SafeName(typeName) + " for guid " + guidHash + ": kept this client's own "
            + n + " reference-shaped field(s) and took the rest from the server"
            + (n == 0 ? "" : ": " + JoinNames(fields!));
    }

    /// <summary>
    /// The server retracted its layer for one prefab (a <c>PrfE</c> with <c>count == 0</c>) and
    /// the client put the captured baseline back. Info. Grep <c>withdrew PrfE</c>.
    /// </summary>
    public static string EditLayerWithdrawn(int guidHash, int restored) =>
        Prefix + "withdrew PrfE for " + guidHash + ": the server stopped editing it; restored "
        + restored + " component(s) to their captured values";

    /// <summary>
    /// A received layer could not be applied because the prefab entity was not in the collection
    /// yet, and the bounded retry ran out. Warning, once per guid per Core generation.
    /// Grep <c>PrfE never applied</c>.
    /// </summary>
    public static string EditLayerRetryGaveUp(int guidHash, int attempts, string? reason) =>
        Prefix + "PrfE never applied for guid " + guidHash + " after " + attempts
        + " attempt(s): " + (reason ?? "the prefab entity never resolved")
        + "; this client keeps its own values for that prefab";

    /// <summary>
    /// The send rate floor suppressed a push and coalesced it into a trailing one. Info, once per
    /// guid per Core generation — a server whose editor changes value every frame should see it
    /// once, not once a frame.
    /// </summary>
    public static string EditLayerCoalesced(int guidHash, int intervalMs) =>
        Prefix + "PrfE for guid " + guidHash + " is changing faster than one push per "
        + intervalMs + " ms; coalescing into a trailing send";

    /// <summary>Every stored layer was pushed again because a user re-handshaked (a client Core
    /// swap re-runs the handshake, and its received layers went with the old generation).</summary>
    public static string EditLayerResent(string? who, int prefabs) =>
        Prefix + "re-sent " + prefabs + " PrfE layer(s) to " + (who ?? "a user")
        + " after its handshake completed";

    /// <summary><see cref="SafeName"/> over a list; the join a PrfE line uses.</summary>
    private static string JoinSafeNames(IReadOnlyList<string> names)
    {
        var safe = new string[names.Count];
        for (int i = 0; i < names.Count; i++) safe[i] = SafeName(names[i]);
        return JoinNames(safe);
    }

    private static string SafeName(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return "(unnamed)";
        string safe = PrefabEditWire.SanitiseTypeName(typeName, out bool altered);
        if (safe.Length == 0) return "(unnamed)";
        return altered ? safe + " (name sanitized for this log)" : safe;
    }

    /// <summary>The packet did not parse. Warning.</summary>
    public static string EditLayerDropped(string? error) =>
        Prefix + "dropped a PrfE packet: " + (error ?? "");

    /// <summary>
    /// A <c>PrfE</c> packet arrived on the server. The layer is server-to-client; applying it
    /// here would let a client overwrite a server-owned prefab. Warning. Grep
    /// <c>client-originated</c>.
    /// </summary>
    public static string EditLayerRefusedOnServer() =>
        Prefix + "refused PrfE: " + PrefabEditWire.ReceivedOnServerReason;

    /// <summary>
    /// The two processes do not agree on the game they are running. The whole packet is dropped;
    /// one line per Core generation, not per packet. The identity is the shared version string
    /// (<c>PrefabEditBuildIdentity</c>), not a per-binary fingerprint, so a mismatch is a real
    /// build difference or a process that could not read its own version — never the ordinary
    /// client/server asymmetry (R50 finding 10). Grep <c>PrfE build mismatch</c>.
    /// </summary>
    public static string EditLayerBuildMismatch(int guidHash, int packetBuild, int localBuild, string? localIdentity) =>
        Prefix + "PrfE build mismatch on guid " + guidHash + ": server game identity hash 0x"
        + packetBuild.ToString("x8") + " vs this process 0x" + localBuild.ToString("x8")
        + " (" + (string.IsNullOrEmpty(localIdentity) ? "no game identity detected" : localIdentity)
        + "); dropping server edit layers.";

    /// <summary>
    /// An editor threw. Logged once per plugin + guid per Core generation; the pass is not
    /// fingerprinted so the next trigger retries.
    /// </summary>
    public static string EditorThrew(string pluginId, int guidHash, Exception e)
    {
        return Prefix + "edit from " + (pluginId ?? "") + " on guid " + guidHash + " threw: " + e;
    }

    /// <summary>
    /// <c>SetComponent&lt;T&gt;</c> is documented for <c>ItemData</c> only. Unmanaged types
    /// use <c>Write&lt;T&gt;</c> (RawWriteGuard + <c>SetComponentDataRaw</c>).
    /// </summary>
    public static string SetComponentUseWrite(string pluginId, int guidHash, string typeName)
    {
        return Prefix + "refused SetComponent<" + (typeName ?? "") + "> by " + (pluginId ?? "")
            + " on guid " + guidHash
            + ": SetComponent is for ItemData; unmanaged types use Write<T> (RawWriteGuard).";
    }

    /// <summary><c>SetComponent</c> size gate threw; the write was not applied.</summary>
    public static string SetComponentGuardFailed(string pluginId, int guidHash, string typeName, Exception e)
    {
        return Prefix + "SetComponent<" + (typeName ?? "") + "> by " + (pluginId ?? "")
            + " on guid " + guidHash + " refused by RawWriteGuard: " + e;
    }

    /// <summary>
    /// The last editor of a prefab was removed (Dispose, plugin unload, Unrequire sweep) and
    /// the components it wrote were written back to their captured vanilla values. Info.
    /// </summary>
    public static string RestoredAfterLastEditor(int guidHash, IReadOnlyList<string>? components)
    {
        int n = components?.Count ?? 0;
        string line = Prefix + "restored " + n + " component(s) on guid " + guidHash
            + " after its last editor was removed";
        if (n == 0) return line;
        return line + ": " + JoinNames(components!);
    }

    /// <summary>
    /// An editor was removed but others remain: the prefab was restored to vanilla and the
    /// remaining ordered set re-ran on top of it. Info.
    /// </summary>
    public static string RestoredBeforeRerun(int guidHash, int restored, int editors)
    {
        return Prefix + "editor set changed on guid " + guidHash
            + "; restored " + restored + " component(s) before re-running " + editors + " editor(s)";
    }

    /// <summary>
    /// Core uninstall (generation swap): every captured baseline was written back so the next
    /// generation's first write captures true vanilla. Info.
    /// </summary>
    public static string RestoredAtUninstall(int components, int prefabs)
    {
        return Prefix + "restored " + components + " component(s) on " + prefabs
            + " prefab(s) at Core uninstall";
    }

    /// <summary>
    /// A drained removal was re-queued until its retry budget ran out and the prefab is still
    /// carrying the removed editor's values. Warning, once per guid per Core generation.
    /// Grep <c>removal never serviced</c>.
    ///
    /// <para>The line names the <em>last</em> answer, not "every time" (the outcome can change
    /// between retries), and the number of baselines still owed — which is why it is only ever
    /// printed when that number is above zero (R50 finding 6).</para>
    /// </summary>
    public static string RemovalGivenUp(int guidHash, string? outcome, int retries, int owedBaselines)
    {
        return Prefix + "removal never serviced on guid " + guidHash + " after " + retries
            + " retry(s): the last answer was " + (outcome ?? "") + " and " + owedBaselines
            + " baseline(s) are still owed, so this prefab keeps the removed editor's values"
            + " until Core uninstall";
    }

    /// <summary>
    /// A drained removal was not re-queued because another path owns the restore, or because no
    /// retry could help. Info, once per guid per Core generation: this is the ordinary
    /// hot-reload-before-the-collection-is-ready case, and before FEDW2 it produced thirty
    /// re-queues and a warning (R50 finding 6). Grep <c>removal not retried</c>.
    /// </summary>
    public static string RemovalNotRetried(int guidHash, string? outcome, string? reason)
    {
        return Prefix + "removal not retried on guid " + guidHash + ": the pass answered "
            + (outcome ?? "") + " and " + (reason ?? "");
    }

    /// <summary>One restore callback threw; the remaining baselines of that prefab still run.</summary>
    public static string RestoreThrew(string? typeName, int guidHash, Exception e)
    {
        return Prefix + "restore of " + (typeName ?? "") + " on guid " + guidHash + " threw: " + e;
    }

    private static string JoinNames(IReadOnlyList<string> names)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < names.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(names[i] ?? "");
        }
        return sb.ToString();
    }

    public static string ReasonPriority(int winnerPriority, int loserPriority) =>
        "later priority (" + winnerPriority + " over " + loserPriority + ")";

    public static string ReasonBeforeAfter(string winnerId, string loserId) =>
        "before/after (" + (winnerId ?? "") + " after " + (loserId ?? "") + ")";

    public static string ReasonLoadOrder() => "later plugin load order";

    public static string ReasonRegistration() => "later registration";

    /// <summary>
    /// Per-prefab fragment for the status op on both sides. <paramref name="editorCount"/> is
    /// how many editors are queued for this GUID; <paramref name="components"/> is the unique
    /// type names they wrote, insertion order (identity types omitted). Empty when there are
    /// no editors. Example: <c>edits=2 ItemData</c>.
    /// </summary>
    public static string EditSummary(int editorCount, IReadOnlyList<string>? components)
    {
        if (editorCount <= 0) return "";
        if (components == null || components.Count == 0)
            return "edits=" + editorCount;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.Append("edits=").Append(editorCount);
        for (int i = 0; i < components.Count; i++)
        {
            string c = components[i];
            if (string.IsNullOrEmpty(c)) continue;
            if (PrefabEditIdentity.IsIdentityType(c)) continue;
            if (!seen.Add(c)) continue;
            sb.Append(' ').Append(c);
        }
        return sb.ToString();
    }

    public static string FormatPlugin(string? name, string? id)
    {
        string n = name ?? "";
        string i = id ?? "";
        if (n.Length == 0) return i;
        if (i.Length == 0 || string.Equals(n, i, StringComparison.Ordinal)) return n;
        return n + " (" + i + ")";
    }

    private static string FormatCycle(IReadOnlyList<string> cycleIds)
    {
        if (cycleIds == null || cycleIds.Count == 0) return "";
        var parts = new string[cycleIds.Count];
        for (int i = 0; i < cycleIds.Count; i++)
            parts[i] = cycleIds[i] ?? "";
        return string.Join(" -> ", parts);
    }
}
