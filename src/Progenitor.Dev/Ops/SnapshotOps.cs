using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Unity.Collections;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>snapshot</c> / <c>restore</c> pair: copy one entity's unmanaged component
/// blobs out as base64 (with the same decoded view <c>get</c> returns), then write those bytes
/// back later to undo an experiment on the test server.
///
/// <b>This is not a save.</b> It is one entity, one world generation, unmanaged
/// <c>IComponentData</c> only. Buffers, managed and shared components, the archetype, and
/// everything the entity points at are not captured, and an <c>Entity</c> field inside a
/// restored blob only means anything while that other entity still lives at the same
/// index:version. Use <c>world_save</c> for a real save.
///
/// Restore is fail-closed: a component is written back only when it is still on the entity,
/// still unmanaged <c>IComponentData</c>, still reports exactly the byte size the snapshot
/// recorded, and still carries the same <see cref="SnapshotArgs.LayoutStamp"/> — the
/// GameAssembly build id, the <c>TypeIndex</c> and the Unity <c>StableTypeHash</c>. Anything
/// else is refused with a reason naming the check that failed, and with the default
/// <c>strict</c> the whole call writes nothing if any one entry is refused. <c>strict: false</c>
/// overrides a stamp mismatch (and only a stamp mismatch, and only while the sizes agree),
/// reporting a <c>warning</c> on the row. The write itself is the same
/// <c>SetComponentDataRaw</c> call the <c>set</c> op uses, with the whole blob instead of a
/// patched field. Server and client, main thread. Policy: <see cref="SnapshotArgs"/>.
/// </summary>
public static unsafe class SnapshotOps
{
    /// <summary>
    /// <paramref name="args"/>: <c>entity</c> required (<c>index:version</c>); <c>max</c>
    /// optional (raw-byte budget, default 64 KiB); <c>only</c> optional (component names).
    /// Hand the whole result back to <see cref="Restore"/> as <c>args.snapshot</c>.
    /// </summary>
    public static object Snapshot(World world, JsonElement args)
    {
        try
        {
            if (world == null || !world.IsCreated)
                throw new InvalidOperationException("the game world does not exist yet (not in a game)");

            var request = SnapshotArgs.ParseSnapshot(args);
            var em = world.EntityManager;
            var entity = InspectOps.ResolveEntity(em, request.Entity);

            var budget = new InspectArgs.Budget(request.MaxBytes);
            string? truncatedAt = null;
            int captured = 0, skipped = 0;
            var rows = new List<Dictionary<string, object?>>();

            var types = em.GetComponentTypes(entity, Allocator.Temp);
            try
            {
                for (int i = 0; i < types.Length; i++)
                {
                    var row = Capture(em, entity, types[i], request, budget, ref truncatedAt, out bool copied);
                    if (copied) captured++; else skipped++;
                    rows.Add(row);
                }
            }
            finally
            {
                types.Dispose();
            }

            Plugin.Logger?.LogInfo(FormattableString.Invariant(
                $"Dev: snapshot {InspectOps.FormatEntity(entity)} captured={captured} skipped={skipped} bytes={budget.Used}"));

            return SnapshotArgs.SnapshotResult(
                InspectOps.FormatEntity(entity),
                world.Name,
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                LiveBuildId(),
                captured,
                skipped,
                budget,
                truncatedAt,
                rows);
        }
        catch (Exception e) when (e is not ArgumentException and not InvalidOperationException)
        {
            Plugin.Logger?.LogError($"snapshot op threw: {e}");
            throw new InvalidOperationException($"snapshot op failed: {e.Message}", e);
        }
    }

    /// <summary>
    /// <paramref name="args"/>: <c>entity</c> and <c>snapshot</c> required; <c>strict</c>
    /// defaults true (validate everything, then write, or write nothing at all).
    /// </summary>
    public static object Restore(World world, JsonElement args)
    {
        try
        {
            if (world == null || !world.IsCreated)
                throw new InvalidOperationException("the game world does not exist yet (not in a game)");

            var request = SnapshotArgs.ParseRestore(args);
            var em = world.EntityManager;
            var entity = InspectOps.ResolveEntity(em, request.Entity);
            string id = InspectOps.FormatEntity(entity);

            // Pass one: decide every entry before writing anything, so strict can refuse the
            // whole call without having already half-restored the entity.
            string? liveBuild = LiveBuildId();
            var plans = new List<Plan>(request.Entries.Count);
            var reasons = new List<string>();
            foreach (var entry in request.Entries)
            {
                var plan = Decide(em, entity, entry, request.Build, liveBuild, request.Strict);
                plans.Add(plan);
                if (plan.Reason != null) reasons.Add(plan.Reason);
            }

            if (request.Strict && reasons.Count > 0)
                throw new InvalidOperationException(SnapshotArgs.StrictRefusalMessage(id, reasons));

            // Pass two: write.
            int restored = 0, refused = 0, warnings = 0;
            var rows = new List<Dictionary<string, object?>>(plans.Count);
            foreach (var plan in plans)
            {
                if (plan.Reason != null)
                {
                    refused++;
                    Plugin.Logger?.LogWarning($"Dev: restore {id} refused {plan.Entry.Component}: {plan.Reason}");
                    rows.Add(SnapshotArgs.RestoreRow(plan.Entry.Component, plan.Entry.Size, false, plan.Reason));
                    continue;
                }

                if (plan.Warning != null)
                {
                    warnings++;
                    Plugin.Logger?.LogWarning($"Dev: restore {id} wrote {plan.Entry.Component} over a layout-stamp mismatch: {plan.Warning}");
                }

                try
                {
                    fixed (byte* bytes = plan.Entry.Bytes)
                        em.SetComponentDataRaw(entity, plan.TypeIndex, bytes, plan.Entry.Size);
                }
                catch (Exception e)
                {
                    refused++;
                    string reason = $"'{plan.Entry.Component}' write failed: {e.Message}";
                    Plugin.Logger?.LogError($"Dev: restore {id} refused {plan.Entry.Component}: {e.Message}");
                    rows.Add(SnapshotArgs.RestoreRow(plan.Entry.Component, plan.Entry.Size, false, reason));
                    continue;
                }

                restored++;
                Plugin.Logger?.LogInfo(SnapshotArgs.RestoreLogLine(id, plan.Entry.Component, plan.Entry.Size));
                rows.Add(SnapshotArgs.RestoreRow(plan.Entry.Component, plan.Entry.Size, true, null, plan.Warning));
            }

            return SnapshotArgs.RestoreResult(id, request.Strict, restored, refused, warnings, rows);
        }
        catch (Exception e) when (e is not ArgumentException and not InvalidOperationException)
        {
            Plugin.Logger?.LogError($"restore op threw: {e}");
            throw new InvalidOperationException($"restore op failed: {e.Message}", e);
        }
    }

    // ---- snapshot -----------------------------------------------------------------------------

    private static Dictionary<string, object?> Capture(
        EntityManager em,
        Entity entity,
        ComponentType componentType,
        SnapshotArgs.SnapshotRequest request,
        InspectArgs.Budget budget,
        ref string? truncatedAt,
        out bool copied)
    {
        copied = false;
        string name = InspectOps.TypeName(componentType);

        if (!request.Wants(name))
            return SnapshotArgs.SnapshotRow(name, KindOf(componentType), 0, null, null, "not requested");
        if (InspectOps.SafeIsManaged(componentType))
            return SnapshotArgs.SnapshotRow(name, SnapshotArgs.ComponentKind.Managed, 0, null, null, "managed component: there is no raw blob to copy");
        if (InspectOps.SafeIsShared(componentType))
            return SnapshotArgs.SnapshotRow(name, SnapshotArgs.ComponentKind.Shared, 0, null, null, "shared component: the value lives in the chunk header, not on this entity");
        if (InspectOps.SafeIsBuffer(componentType))
        {
            int length = 0;
            try { length = em.GetBufferLength(entity, componentType.TypeIndex); } catch { }
            return SnapshotArgs.SnapshotRow(name, SnapshotArgs.ComponentKind.Buffer, length, null, null,
                "buffer: snapshot records the name and length only, and restore never writes buffer elements");
        }

        Type managed;
        try
        {
            managed = EntityOps.FindInteropStruct(name);
        }
        catch (Exception e)
        {
            return SnapshotArgs.SnapshotRow(name, SnapshotArgs.ComponentKind.Missing, 0, null, null, $"no interop struct to size it with: {e.Message}");
        }

        int size;
        try
        {
            size = ComponentDump.SizeOf(managed, TypeManager.GetTypeInfo(componentType.TypeIndex).TypeSize);
        }
        catch (Exception e)
        {
            return SnapshotArgs.SnapshotRow(name, SnapshotArgs.ComponentKind.Missing, 0, null, null, $"size lookup failed: {e.Message}");
        }

        if (size <= 0)
            return SnapshotArgs.SnapshotRow(name, SnapshotArgs.ComponentKind.ZeroSized, 0, null, null, "zero-sized (a tag): nothing to copy");

        if (!budget.TryTake(size))
        {
            truncatedAt ??= name;
            return SnapshotArgs.SnapshotRow(name, SnapshotArgs.ComponentKind.Unmanaged, size, null, null,
                "size cap reached; re-run with a larger max or an only filter");
        }

        try
        {
            byte* raw = (byte*)em.GetComponentDataRawRO(entity, componentType.TypeIndex);
            if (raw == null)
                return SnapshotArgs.SnapshotRow(name, SnapshotArgs.ComponentKind.Unmanaged, size, null, null, "GetComponentDataRawRO returned null");

            var blob = new byte[size];
            Marshal.Copy((IntPtr)raw, blob, 0, size);

            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var spec in ComponentDump.FieldsOf(managed, size))
                fields[spec.Name] = ComponentDump.ReadField(raw, spec);

            copied = true;
            string? stableTypeHash = TryStableTypeHash(componentType, out string? hashUnread);
            if (hashUnread != null)
                Plugin.Logger?.LogWarning($"snapshot: '{name}' carries no StableTypeHash: {hashUnread}");

            return SnapshotArgs.SnapshotRow(
                name,
                SnapshotArgs.ComponentKind.Unmanaged,
                size,
                Convert.ToBase64String(blob),
                fields,
                null,
                componentType.TypeIndex.Value,
                stableTypeHash,
                hashUnread);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"snapshot: '{name}' on {InspectOps.FormatEntity(entity)} failed: {e.Message}");
            return SnapshotArgs.SnapshotRow(name, SnapshotArgs.ComponentKind.Unmanaged, size, null, null, $"read failed: {e.Message}");
        }
    }

    /// <summary>
    /// The GameAssembly build id the address tables are keyed on
    /// (<c>AddressId.ComputeBuildId</c>: file size, PE TimeDateStamp, PE CheckSum, SHA-256
    /// prefix). <c>AddressLibrary</c> fingerprints the module on its first load attempt, which
    /// a process that has not resolved a detour yet may not have made, so one warm-up
    /// <c>TryResolve</c> is issued before giving up. Null means the id is genuinely unknown;
    /// the stamp check then reports that it could not run rather than passing.
    /// </summary>
    private static string? LiveBuildId()
    {
        try
        {
            var known = Progenitor.Native.AddressLibrary.DetectedBuild;
            if (!string.IsNullOrWhiteSpace(known)) return known;

            // Read-only: this resolves an id, it never installs anything.
            Progenitor.Native.AddressLibrary.TryResolve(Progenitor.Native.AddressId.ServerOnDataReceivedServer, out _);
            known = Progenitor.Native.AddressLibrary.DetectedBuild;
            return string.IsNullOrWhiteSpace(known) ? null : known;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"snapshot: could not read the GameAssembly build id: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// <c>TypeManager.GetTypeInfo(...).StableTypeHash</c> as the wire form,
    /// or null when the check could not run.
    ///
    /// A 0 is <b>not</b> a hash (R31 finding 6). It is the same <c>TypeManager</c> interop that
    /// reads <c>TypeSize</c> 0 for ordinary game components (CLAUDE.md raw-write policy), and
    /// <c>StableTypeHash</c> has never been measured live on this build. Formatting a 0 would
    /// produce the non-empty <c>"0x0000000000000000"</c> on both sides of the comparison, so a
    /// re-ordered component that keeps its <c>TypeIndex</c> and byte count — the only case this
    /// stamp exists for — would pass silently under <c>strict: true</c>. Null instead routes it
    /// into the existing "does not report one" branch: strict refuses, <c>strict: false</c>
    /// warns per row.
    /// </summary>
    private static string? TryStableTypeHash(ComponentType componentType) =>
        TryStableTypeHash(componentType, out _);

    /// <summary>
    /// As above, with <paramref name="unread"/> set to the reason when the hash is null so a
    /// snapshot row can record that the check could not run rather than dropping the column
    /// without saying why.
    /// </summary>
    private static string? TryStableTypeHash(ComponentType componentType, out string? unread)
    {
        ulong hash;
        try
        {
            hash = TypeManager.GetTypeInfo(componentType.TypeIndex).StableTypeHash;
        }
        catch
        {
            unread = SnapshotArgs.StableTypeHashUnreadReason(readZero: false);
            return null;
        }

        if (hash == 0UL)
        {
            unread = SnapshotArgs.StableTypeHashUnreadReason(readZero: true);
            return null;
        }

        unread = null;
        return SnapshotArgs.FormatStableTypeHash(hash);
    }

    private static SnapshotArgs.ComponentKind KindOf(ComponentType componentType)
    {
        if (InspectOps.SafeIsBuffer(componentType)) return SnapshotArgs.ComponentKind.Buffer;
        if (InspectOps.SafeIsManaged(componentType)) return SnapshotArgs.ComponentKind.Managed;
        if (InspectOps.SafeIsShared(componentType)) return SnapshotArgs.ComponentKind.Shared;
        return SnapshotArgs.ComponentKind.Unmanaged;
    }

    // ---- restore ------------------------------------------------------------------------------

    private readonly struct Plan
    {
        public readonly SnapshotArgs.RestoreEntry Entry;
        public readonly TypeIndex TypeIndex;
        public readonly string? Reason;

        /// <summary>Set only on a write that goes ahead over a layout-stamp mismatch (strict false).</summary>
        public readonly string? Warning;

        public Plan(SnapshotArgs.RestoreEntry entry, TypeIndex typeIndex, string? reason, string? warning = null)
        {
            Entry = entry;
            TypeIndex = typeIndex;
            Reason = reason;
            Warning = warning;
        }
    }

    /// <summary>
    /// Resolve one snapshot row against the live entity and apply
    /// <see cref="SnapshotArgs.RefuseReason"/>, then the layout stamp
    /// (<see cref="SnapshotArgs.LayoutMismatch"/>). Never throws: an unresolvable name is a
    /// refusal reason, not a crash, so strict can list every problem at once.
    ///
    /// The two gates are not the same thing. The size rules are hard in both modes — they are
    /// what keeps <c>SetComponentDataRaw</c> from writing the wrong number of bytes. The stamp
    /// is an identity check on top: strict refuses a mismatch, <c>strict: false</c> writes
    /// anyway (the sizes have already been proven equal by then) and carries a warning.
    /// </summary>
    private static Plan Decide(
        EntityManager em,
        Entity entity,
        SnapshotArgs.RestoreEntry entry,
        string? snapshotBuild,
        string? liveBuild,
        bool strict)
    {
        ComponentType componentType;
        try
        {
            componentType = EntityOps.ResolveComponentType(entry.Component);
        }
        catch (Exception e)
        {
            return new Plan(entry, default, $"'{entry.Component}' does not resolve to a component in this build: {e.Message}");
        }

        bool present;
        try
        {
            present = em.HasComponent(entity, componentType);
        }
        catch (Exception e)
        {
            return new Plan(entry, default, $"'{entry.Component}' presence check failed: {e.Message}");
        }

        if (!present)
        {
            return new Plan(entry, default,
                SnapshotArgs.RefuseReason(entry.Component, SnapshotArgs.ComponentKind.Missing, entry.Size, 0));
        }

        var kind = KindOf(componentType);
        int currentSize = 0;
        if (kind == SnapshotArgs.ComponentKind.Unmanaged)
        {
            try
            {
                var managed = EntityOps.FindInteropStruct(entry.Component);
                currentSize = ComponentDump.SizeOf(managed, TypeManager.GetTypeInfo(componentType.TypeIndex).TypeSize);
            }
            catch (Exception e)
            {
                return new Plan(entry, default, $"'{entry.Component}' size lookup failed: {e.Message}");
            }
            if (currentSize <= 0) kind = SnapshotArgs.ComponentKind.ZeroSized;
        }

        var live = new SnapshotArgs.LayoutStamp(
            liveBuild,
            componentType.TypeIndex.Value,
            TryStableTypeHash(componentType),
            currentSize);

        SnapshotArgs.DecideRestore(
            entry.Component,
            kind,
            entry.Size,
            currentSize,
            entry.Stamp(snapshotBuild),
            live,
            strict,
            out var reason,
            out var warning);

        return reason != null
            ? new Plan(entry, default, reason)
            : new Plan(entry, componentType.TypeIndex, null, warning);
    }
}
