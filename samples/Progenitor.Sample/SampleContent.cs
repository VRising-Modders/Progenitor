using System;
using System.Collections.Generic;
using System.Globalization;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
using Progenitor.Content;
using ProjectM;
using Stunlock.Core;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Job FEX5 (RD6 J13). The registry members the Sample registered against and never read back:
/// <c>PrefabRegistry.Describe()</c>, <c>PrefabRegistry.Definitions</c> and
/// <c>PrefabRegistry.UnregisterAll(owner)</c> — the soft-unapply path, which is the one piece of
/// the content module a mod author meets only when something goes wrong.
///
/// <b>What <c>.content</c> shows.</b> <c>.content</c> prints the one-line registry summary and a
/// row per live definition; <c>.content define</c> registers a second cloned item at runtime; and
/// <c>.content drop</c> takes it away again with <c>UnregisterAll</c>, which is a <b>soft</b>
/// unapply: the clone entity and its GUID maps stay resolvable, the definition leaves
/// <c>Definitions</c>, connected clients are told with a <c>PrfU</c> packet, and the row this
/// process appended to <c>ItemKeyLookup</c> becomes a <b>tombstone</b> sent as <c>PrfK</c>
/// (<c>Content/PrefabRegistry.cs:26</c>, <c>:310-334</c>). Watch the summary line change from
/// <c>tombstones=0</c> to <c>tombstones=1</c> across the two commands — that is the whole
/// mechanism in one number.
///
/// <b>Why the key table is never shrunk.</b> An inventory slot travels as an <em>index</em> into
/// <c>ItemKeyLookup</c>, and the join handshake validates that table by count and hash. Removing a
/// row would renumber every slot after it and fail every future join. So an unregistered row stays
/// and is remembered as a tombstone, and a new joiner is told about it so its count still matches.
/// That is why <c>Describe()</c> reports tombstones at all, and why this example prints it.
///
/// <b>The separate owner, and why it is not a trick.</b> <c>UnregisterAll(owner)</c> drops
/// <em>every</em> definition registered under that object — for <c>ctx.Owner</c> that would take
/// the Ember Shard and the Sample's other definitions with it. So <c>.content define</c> registers
/// the second item under its own <see cref="CinderOwner"/> marker object through the static
/// <c>PrefabRegistry.DefineItem(...).Register(owner)</c> overload
/// (<c>Content/PrefabRegistry.cs:1161</c>), which is exactly what that overload's owner argument
/// is for: a plugin that manages several independent groups of content gives each one an owner it
/// can retire on its own. The handle is also tracked on the context, so an unload that never
/// reaches <see cref="Detach"/> still drops it.
///
/// <b>A read-back through the edit handle.</b> The define also registers an ordered editor whose
/// first job is <c>PrefabEditHandle.TryRead&lt;PrefabGUID&gt;</c>
/// (<c>Content/PrefabEditHandle.cs:91</c>) — a typed read of the clone's own GUID component,
/// logged next to the handle's <c>Guid</c>. Two identical numbers are the proof that the editor
/// was handed the clone and not the source prefab, which is the mistake a first edit pass makes.
/// The GUID itself is never written back: the registry refuses a <c>PrefabGUID</c> retarget.
///
/// <b>And the typed write beside it — <c>Has</c>, <c>TryRead</c>, <c>Write&lt;T&gt;</c>,
/// <c>Read</c>.</b> The same editor then moves one number on the clone:
/// <c>ProjectM.DestroyData.DestroyReason</c> becomes <see cref="DestroyReasonWritten"/>
/// (<c>Consume</c>). It is the four-call shape a prefab edit should have, in order.
/// <c>Has&lt;DestroyData&gt;()</c> first, because <c>Write&lt;T&gt;</c> needs the component to
/// already be on the entity (adding one is <c>Entity.Add&lt;T&gt;</c>, a structural change, not
/// an edit); <c>TryRead&lt;T&gt;</c> to get the vanilla value so the log can print both numbers;
/// <c>Write&lt;T&gt;(in value)</c>, which is a <c>SetComponentDataRaw</c> of <c>sizeof(T)</c> and
/// <b>never</b> a byte count the caller chose — <c>Ecs/RawWriteGuard.cs</c> allows the typed form
/// at <c>TypeManager</c> TypeSize 0 (which is what this interop reports for ordinary components)
/// and refuses a raw length there outright; and <c>Read&lt;T&gt;</c> to prove it stuck.
///
/// <b>Why this component.</b> The example needs one the clone actually carries: the live server
/// says a clone of <c>Item_Ingredient_Crystal</c> has exactly <c>InventoryItem</c>,
/// <c>ItemData</c>, <c>DestroyData</c>, <c>DestroyState</c>, <c>PrefabGUID</c>, <c>SpawnTag</c>,
/// <c>Prefab</c> and <c>Simulate</c> (research evidence 212 — the first draft of this example
/// wrote <c>Salvageable</c>, which the clone does not have, so the write half never ran).
/// <c>ItemData</c> carries bools, so it is not C# <c>unmanaged</c> and needs the
/// <c>SetComponent&lt;T&gt;</c> path (interop trap 9); <c>InventoryItem</c> is one
/// <c>Entity ContainerEntity</c> (server metadata), a live reference rather
/// than a value; <c>DestroyState.Value</c> is the destroy state machine itself (its enum has
/// <c>DestroyStateEnum.DestroyNow = 2</c>) and writing it would ask the game to
/// delete the prefab. <c>DestroyData</c> is what is left and it is the right shape:
/// one <c>DestroyReason</c> byte enum, so the struct is one plain byte and C# <c>unmanaged</c>.
///
/// <b>Why the write is safe, precisely.</b> Not because nothing reads the value: two systems
/// branch on <c>DestroyData</c>. <c>DropInventorySystem</c> runs
/// <c>CreateDropTableItemsJob_DestroyInventoryOnDestroy_Job</c>, whose lambda is
/// <c>OriginalLambdaBody(DynamicBuffer&lt;InventoryInstanceElement&gt;, in DestroyData)</c>
/// — the reason decides what happens to an
/// inventory when its owner dies; and <c>WeaponCoatingSourceDestroySystem</c>
/// resolves <c>(Buff, WeaponCoatingSource, DestroyData)</c>.
/// What makes the write safe is what the <em>edited entity</em> is: a
/// <c>Prefab</c> template, which is never destroyed and so is never resolved by either reader.
/// That needs no ordering argument at all, and it is the whole of the safety claim this example
/// makes.
///
/// <b>Instances, and the one step that is inferred.</b> <c>Instantiate</c> copies the template's
/// components, so every instance of this clone is born carrying <c>Consume</c>. What is expected
/// to keep that from reaching the two readers is the <b>ordering</b>: both readers are
/// <c>[UpdateInGroup(typeof(ReactToDestroyTagGroup))]</c>, that group is
/// <c>[UpdateAfter(typeof(DestroyTagGroup))]</c>, and <c>ProcessDestroyEventSystem</c> —
/// which owns <c>TagNewDestroyedEntity(ref SystemState, Entity, DestroyReason)</c> as a
/// <b>private</b> method of its own (it is <em>not</em> on
/// <c>DestroyUtility</c>, whose member list holds only
/// <c>Destroy</c>, <c>DestroyWithReason</c>, <c>CreateDestroyEvent</c>, <c>CheckForIssue</c> and
/// <c>AssertValidEntityToDestroy</c>) — is <c>OrderLast</c> inside <c>DestroyTagGroup</c>.
/// So <em>if</em> that method is what writes
/// <c>DestroyData</c>, the destroy path stamps the reason from the destroy event after whatever
/// the instance carried and before either reader looks, and the copied value never reaches them.
///
/// <b>What of that is verified, and what turns on the rest.</b> From the game's metadata: the two readers,
/// their group, that group's <c>UpdateAfter</c>, and <c>ProcessDestroyEventSystem</c>'s
/// <c>OrderLast</c> membership. <em>Not</em> verified: that <c>TagNewDestroyedEntity</c> is the
/// statement that writes <c>DestroyData</c> — its body is Burst-compiled and its native code was not read,
/// so that rests on its name and its <c>(Entity, DestroyReason)</c> signature. The group ordering
/// holds either way, but <b>the argument about instances does not</b>: the inferred write is the
/// load-bearing step in it, and if nothing inside <c>DestroyTagGroup</c> writes
/// <c>DestroyData</c>, the ordering is irrelevant and every instance carries <c>Consume</c> into
/// both readers untouched. The example does not rest on that being settled, because the entity it
/// edits is a never-destroyed <c>Prefab</c> template.
///
/// <b>Removing the editor puts the number back.</b> That is job FEDIT1's contract, and it is why
/// this write is safe to demonstrate: <c>PrefabEditHandle.Write&lt;T&gt;</c> captures the
/// pre-write <c>DestroyData</c> into <c>Content/PrefabEditBaselines.cs</c> the first time any
/// editor touches that component in this Core generation, and disposing the handle
/// <c>.content drop</c> holds — or unloading Core — restores it. The Sample does not undo the
/// edit by hand anywhere, on purpose: hand-undoing is the bug that contract removes.
///
/// <b>DestroyData does not travel, and Core says so once.</b> The <c>PrfE</c> allowlist
/// (<c>Content/PrefabEditWireTypes.cs</c>) is the network boundary: it vouches for
/// <c>ItemData</c>, <c>LifeTime</c> and three buff components, and nothing else. So this edit
/// lands on the side that ran it and is <em>not</em> shipped to clients, and Core logs one
/// "not vouched" warning per type per generation saying exactly that. Seeing that line is part
/// of the example — a mod author who needs an edit to reach clients has to get the type onto
/// that list, not work around it.
///
/// <b>Live-check split: server only.</b> Everything here runs and logs on the server. The client
/// half — the <c>PrfD</c> for the new item, then the <c>PrfU</c> and <c>PrfK</c> for the drop —
/// is visible in a connected client's log, but the server log alone proves the registry moved.
///
/// Admin-gated: <c>define</c> appends a row to the server's item key table and pushes packets to
/// every connected client, and <c>drop</c> tombstones it. Following <see cref="SampleCommands"/>'s
/// rule, the gate is one <c>adminOnly: true</c> on the whole command rather than a check inside
/// the destructive branch.
///
/// Reload-safe: the definition handle, the edit handle and the owner marker are dropped in
/// <see cref="Detach"/>, so a hot swap leaves no second item behind; the parse, the arithmetic and
/// every reply line are game-free and pinned by the test project.
/// </summary>
internal static class SampleContent
{
    /// <summary>Log/grep area: <c>Sample content: …</c>.</summary>
    internal const string Area = "content";

    /// <summary>Spawnable id of the second item. Hashed to its GUID exactly like the Ember Shard's.</summary>
    internal const string CinderId = "sample.cinder";

    /// <summary>Display name of the second item.</summary>
    internal const string CinderName = "Cinder Shard";

    /// <summary>Tooltip of the second item. At most 63 ASCII bytes: the definition packet (PrfD) carries it in a fixed 64-byte slot and Described() refuses anything longer (the first draft was 79 bytes and threw on the live server).</summary>
    internal const string CinderDescription =
        "A throwaway clone: made by .content define, gone on drop.";

    /// <summary>Stack size of the second item. Small on purpose: it is a demonstration, not loot.</summary>
    internal const int CinderMaxAmount = 5;

    /// <summary>How many definition rows <c>.content</c> prints before it says how many are left.</summary>
    internal const int MaxRowsListed = 12;

    /// <summary>What the caller asked <c>.content</c> for.</summary>
    internal enum Kind
    {
        /// <summary>No argument: <c>Describe()</c> plus a row per <c>Definitions</c> entry.</summary>
        Status = 0,

        /// <summary>Register the second cloned item under its own owner.</summary>
        Define = 1,

        /// <summary>Retire it with <c>UnregisterAll</c>, leaving a key-table tombstone.</summary>
        Drop = 2,
    }

    /// <summary>Usage line, replied on anything <see cref="TryParse"/> refuses.</summary>
    internal const string UsageReply = "Usage: .content [status] | .content define | .content drop";

    /// <summary>Parse <c>.content</c> / <c>.content define</c> / <c>.content drop</c>.</summary>
    internal static bool TryParse(string? token, out Kind kind)
    {
        kind = Kind.Status;
        if (string.IsNullOrWhiteSpace(token)) return true;

        switch (token!.Trim().ToLowerInvariant())
        {
            case "status":
                return true;
            case "define":
                kind = Kind.Define;
                return true;
            case "drop":
                kind = Kind.Drop;
                return true;
            default:
                return false;
        }
    }

    /// <summary>One <c>Definitions</c> row as <c>.content</c> prints it.</summary>
    internal static string DefinitionLine(string? id, int guidHash, int sourceHash, string? kind, string? owner) =>
        "  " + (string.IsNullOrEmpty(id) ? "<no id>" : id!)
        + " guid=" + N(guidHash) + " source=" + N(sourceHash)
        + " kind=" + (string.IsNullOrEmpty(kind) ? "?" : kind!)
        + " owner=" + (string.IsNullOrEmpty(owner) ? "?" : owner!);

    /// <summary>
    /// The <c>.content</c> reply. <paramref name="describe"/> is <c>PrefabRegistry.Describe()</c>
    /// verbatim — the summary line that carries the tombstone count this example exists to show.
    /// </summary>
    internal static IReadOnlyList<string> StatusLines(string? describe, IReadOnlyList<string>? rows, bool cinderDefined)
    {
        var lines = new List<string>(MaxRowsListed + 3)
        {
            "registry: " + (string.IsNullOrEmpty(describe) ? "<no summary>" : describe!),
        };

        int count = rows == null ? 0 : rows.Count;
        if (count == 0)
        {
            lines.Add("PrefabRegistry.Definitions is empty in this process.");
        }
        else
        {
            lines.Add("PrefabRegistry.Definitions (" + N(count) + "):");
            int shown = count < MaxRowsListed ? count : MaxRowsListed;
            for (int i = 0; i < shown; i++)
                lines.Add(rows![i]);
            if (shown < count)
                lines.Add("  … and " + N(count - shown) + " more");
        }

        lines.Add(cinderDefined
            ? "'" + CinderId + "' is defined. .content drop retires it: soft unapply (PrfU) plus a "
                + "key-table tombstone (PrfK), and the tombstone count above goes up by one."
            : "'" + CinderId + "' is not defined. .content define registers it under its own owner "
                + "so .content drop can retire it without touching the Ember Shard or the buff.");
        return lines;
    }

    /// <summary>Reply for a successful <c>.content define</c>.</summary>
    internal static string DefinedReply(int guidHash) =>
        "Defined '" + CinderId + "' (" + CinderName + ") guid=" + N(guidHash)
        + ", a clone of the same Crystal the Ember Shard uses, under its own owner object. "
        + "Connected clients get a PrfD for it.";

    /// <summary>Reply for <c>.content define</c> when it is already defined.</summary>
    internal static string AlreadyDefinedReply() =>
        "'" + CinderId + "' is already defined. .content drop retires it first.";

    /// <summary>Reply for <c>.content drop</c> when there is nothing to retire.</summary>
    internal static string NotDefinedReply() =>
        "'" + CinderId + "' is not defined. Run .content define first.";

    /// <summary>Reply for a successful <c>.content drop</c>.</summary>
    internal static string DroppedReply(int count) =>
        "UnregisterAll dropped " + N(count) + " definition(s) under the "
        + "CinderOwner marker. This is a soft unapply: the clone entity and its GUID "
        + "maps stay resolvable, clients get PrfU, and the ItemKeyLookup row this process appended "
        + "becomes a tombstone sent as PrfK — the table is never shrunk, because an inventory slot "
        + "travels as an index into it.";

    /// <summary>The line that reports what the registry says about one GUID after a change.</summary>
    internal static string GuidStateLine(int guidHash, string? state) =>
        "guid=" + N(guidHash) + " -> " + (string.IsNullOrEmpty(state) ? "<no state>" : state!);

    /// <summary>The edit handle's read-back line. Two equal numbers is the pass condition.</summary>
    internal static string ReadBackLine(int handleGuidHash, int componentGuidHash) =>
        "edit read-back: handle.Guid=" + N(handleGuidHash)
        + " TryRead<PrefabGUID>=" + N(componentGuidHash)
        + (handleGuidHash == componentGuidHash
            ? " (equal, so the editor was handed the clone and not the source prefab)"
            : " (DIFFERENT — the editor is looking at the wrong entity)");

    /// <summary>The line written when <c>TryRead</c> finds no component at all.</summary>
    internal const string ReadBackMissingLine =
        "edit read-back: TryRead<PrefabGUID> returned false; the clone has no PrefabGUID component.";

    /// <summary>
    /// What the editor writes into <c>DestroyData.DestroyReason</c> on the clone:
    /// <c>DestroyReason.Consume</c> (server metadata). Held as the raw byte so the
    /// value, the log lines and the tests exist without the game assemblies. Any member would do
    /// — the destroy path stamps its own reason before the two systems that branch on
    /// <c>DestroyData</c> run (see the type summary), and this entity is a
    /// prefab template that is never destroyed — so it is chosen only to differ from the vanilla
    /// <c>Default</c> and to read plausibly for an item.
    /// </summary>
    internal const byte DestroyReasonWritten = 2;

    /// <summary>Component the typed write goes to. Named once so the log and the docs agree.</summary>
    internal const string DestroyComponentName = "ProjectM.DestroyData";

    /// <summary>
    /// Written when <c>Has&lt;DestroyData&gt;()</c> (or the <c>TryRead</c> behind it) says no.
    /// Not an error: the guard is the example. <c>Write&lt;T&gt;</c> requires the component to
    /// be on the entity already, so a prefab that does not carry it is simply skipped.
    /// </summary>
    internal const string DestroyMissingLine =
        "edit write: Has<" + DestroyComponentName + ">() is false on this clone, so nothing was "
        + "written. Write<T> needs the component to be there already - adding one is "
        + "Entity.Add<T>, a structural change, not an edit.";

    /// <summary>
    /// Name of one <c>ProjectM.DestroyReason</c> member (server metadata),
    /// so the before/after line reads as names and not only as two bytes. A plain table rather
    /// than <c>ToString()</c> on the interop enum: this half of the file compiles without the
    /// game assemblies, and an unknown byte has to print rather than throw.
    /// </summary>
    internal static string DestroyReasonName(byte value) => value switch
    {
        0 => "Default",
        1 => "Duration",
        2 => "Consume",
        3 => "OnHit",
        4 => "Transform",
        5 => "NoPlayerInRangeTimeout",
        6 => "Absorb",
        7 => "Movement",
        8 => "ParentDestruction",
        9 => "ManualInterrupt",
        10 => "DurabilityLoss",
        11 => "CorruptionCleansed",
        12 => "HomingReturn",
        13 => "OnHitConsume",
        _ => "unknown",
    };

    /// <summary>The before/after line for the typed write.</summary>
    internal static string DestroyWriteLine(byte before, byte after) =>
        "edit write: " + DestroyComponentName + ".DestroyReason " + Enum8(before) + " -> " + Enum8(after)
        + " via TryRead<T> then Write<T> (a SetComponentDataRaw of sizeof(T), never a caller-chosen "
        + "byte count). Core captured the pre-write value, so disposing this editor restores it.";

    /// <summary>The <c>Read&lt;T&gt;</c> confirmation. Equal values is the pass condition.</summary>
    internal static string DestroyReadBackLine(byte wrote, byte read) =>
        "edit write read-back: Read<DestroyData>().DestroyReason=" + Enum8(read)
        + (read == wrote
            ? " (equal to what Write<T> was given, so the write stuck)"
            : " (DIFFERENT from the " + Enum8(wrote) + " that was written)");

#if !PROGENITOR_TESTS
    /// <summary>
    /// The owner of the second item's registration, and nothing else. A distinct object rather
    /// than <c>ctx.Owner</c> so <c>UnregisterAll</c> can retire this one group of content without
    /// touching the plugin's other definitions. The type exists only to give the log and the reply
    /// a readable name.
    /// </summary>
    internal sealed class CinderOwner
    {
        /// <inheritdoc />
        public override string ToString() => "Sample " + CinderId;
    }

    private static CinderOwner? _owner;
    private static IDisposable? _definition;
    private static IDisposable? _editor;

    /// <summary>Deterministic GUID of the second item, derived from <see cref="CinderId"/>.</summary>
    internal static PrefabGUID CinderGuid => new(PrefabGUID.CalculateGuidHash(CinderId));

    /// <summary>True while <c>.content define</c>'s registration is live.</summary>
    internal static bool IsDefined => _definition != null;

    /// <summary>Server entry point for <c>.content</c>.</summary>
    internal static void HandleCommand(ICommandContext ctx, Kind kind)
    {
        switch (kind)
        {
            case Kind.Define:
                Define(ctx);
                return;
            case Kind.Drop:
                Drop(ctx);
                return;
            default:
                Status(ctx);
                return;
        }
    }

    private static void Status(ICommandContext ctx)
    {
        var rows = new List<string>();
        var definitions = PrefabRegistry.Definitions;
        for (int i = 0; i < definitions.Count; i++)
        {
            var def = definitions[i];
            rows.Add(DefinitionLine(
                def.Id, def.Guid.GuidHash, def.Source.GuidHash, def.Kind.ToString(), Describe(def.Owner)));
        }

        var lines = StatusLines(PrefabRegistry.Describe(), rows, IsDefined);
        for (int i = 0; i < lines.Count; i++)
        {
            ctx.Reply(lines[i]);
            SampleLog.Info(Area, lines[i]);
        }
    }

    private static void Define(ICommandContext ctx)
    {
        if (IsDefined)
        {
            ctx.Reply(AlreadyDefinedReply());
            return;
        }

        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }

        var owner = new CinderOwner();
        var definition = PrefabRegistry.DefineItem(CinderId)
            .CloneOf(Plugin.EmberSource)
            .MaxAmount(CinderMaxAmount)
            .Named(CinderName)
            .Described(CinderDescription)
            .Register(owner);

        _owner = owner;
        _definition = definition;
        // Tracked as well as held: an unload that never reaches Detach still drops it.
        plugin.Track(definition);

        // The read-back editor. Registered under the plugin (ctx.Owner) because Content.Edit
        // refuses any other owner, so it is disposed here rather than by UnregisterAll.
        _editor = plugin.Content.Edit(CinderGuid, ReadBack);

        string reply = DefinedReply(CinderGuid.GuidHash);
        ctx.Reply(reply);
        SampleLog.Info(Area, reply);
        ReportGuidState(ctx);
    }

    private static void Drop(ICommandContext ctx)
    {
        var owner = _owner;
        if (owner == null || _definition == null)
        {
            ctx.Reply(NotDefinedReply());
            return;
        }

        // Drop the editor first: after the definition is retired there is nothing for it to run
        // against, and leaving it registered would re-run it on a later re-define.
        try { _editor?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogError("Sample content editor dispose threw: " + e); }
        _editor = null;

        int count = PrefabRegistry.UnregisterAll(owner);
        _definition = null;
        _owner = null;

        string reply = DroppedReply(count);
        ctx.Reply(reply);
        SampleLog.Info(Area, reply);

        // The summary line is the evidence: tombstones went up by one and never comes back down.
        string summary = "registry: " + PrefabRegistry.Describe();
        ctx.Reply(summary);
        SampleLog.Info(Area, summary);
        ReportGuidState(ctx);
    }

    private static void ReportGuidState(ICommandContext ctx)
    {
        try
        {
            string line = GuidStateLine(CinderGuid.GuidHash, PrefabRegistry.Describe(ctx.World, CinderGuid));
            ctx.Reply(line);
            SampleLog.Info(Area, line);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample content guid state threw: " + e);
        }
    }

    /// <summary>
    /// The ordered editor. Two halves. First it reads the clone's own <c>PrefabGUID</c> through
    /// the handle and logs it beside <c>handle.Guid</c>, which is the cheapest possible check that
    /// an edit pass is pointed at the entity its author thinks it is. Then
    /// <see cref="TuneDestroyReason"/> does the one typed write this example exists to show.
    /// </summary>
    private static void ReadBack(PrefabEditHandle prefab)
    {
        try
        {
            if (!prefab.TryRead<PrefabGUID>(out var guid))
                SampleLog.Info(Area, ReadBackMissingLine);
            else
                SampleLog.Info(Area, ReadBackLine(prefab.Guid.GuidHash, guid.GuidHash));

            TuneDestroyReason(prefab);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample content read-back threw: " + e);
        }
    }

    /// <summary>
    /// <c>Has</c> / <c>TryRead</c> / <c>Write</c> / <c>Read</c> on one unmanaged component, in
    /// that order. <c>DestroyData</c> is a single <c>DestroyReason</c> byte enum (server
    /// metadata), so it is C#
    /// <c>unmanaged</c> and takes the typed <c>Write&lt;T&gt;</c> path rather than the
    /// <c>SetComponent&lt;T&gt;</c> one <c>ItemData</c> needs (<c>ItemData</c> carries bools:
    /// interop trap 9). It is one of the eight components the live clone actually has (evidence
    /// 212), which is the property the first draft of this example got wrong.
    ///
    /// <para>Nothing here undoes the write. Removing the editor does, from the baseline Core
    /// captured on the first write (job FEDIT1) — which is the contract this example is
    /// demonstrating, and hand-undoing it would hide.</para>
    /// </summary>
    private static void TuneDestroyReason(PrefabEditHandle prefab)
    {
        if (!prefab.Has<DestroyData>() || !prefab.TryRead<DestroyData>(out var destroy))
        {
            SampleLog.Info(Area, DestroyMissingLine);
            return;
        }

        byte before = (byte)destroy.DestroyReason;
        destroy.DestroyReason = (DestroyReason)DestroyReasonWritten;
        prefab.Write(in destroy);

        byte after = (byte)prefab.Read<DestroyData>().DestroyReason;
        SampleLog.Info(Area, DestroyWriteLine(before, DestroyReasonWritten));
        SampleLog.Info(Area, DestroyReadBackLine(DestroyReasonWritten, after));
    }

    private static string Describe(object? owner)
    {
        if (owner == null) return "<none>";
        try { return owner.ToString() ?? owner.GetType().Name; }
        catch { return owner.GetType().Name; }
    }
#endif

    /// <summary>
    /// Unload. The second item is retired here rather than left behind: it is a demonstration
    /// artifact with no persisted state, and a kept registration would collide with the next
    /// generation's <c>.content define</c>.
    /// </summary>
    internal static void Detach()
    {
#if !PROGENITOR_TESTS
        try { _editor?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogError("Sample content editor dispose threw: " + e); }
        _editor = null;

        try { _definition?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogError("Sample content definition dispose threw: " + e); }
        _definition = null;
        _owner = null;
#endif
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>An enum byte as <c>Name(number)</c>, so a stale name table still shows the value.</summary>
    private static string Enum8(byte value) =>
        DestroyReasonName(value) + "(" + value.ToString(CultureInfo.InvariantCulture) + ")";
}
