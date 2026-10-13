using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ecs;
using Progenitor.Networking;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Content;

/// <summary>
/// The game-typed half of <c>PrfE</c> (job FEDW1, design <c>docs/edits-on-the-wire.md</c>): the
/// server serializes the components its ordered edit pass touched and ships them; the client
/// writes them as the final layer over its own editors.
///
/// <para>Every decision that can be taken without a world lives in <see cref="PrefabEditWire"/>
/// (caps, framing, the build rule, the assembly-prefix rule) or in
/// <see cref="PrefabEditWireLayer"/> (readers, dedupe, the stored layer). What is left here is
/// exactly the part that needs an <c>EntityManager</c>.</para>
/// </summary>
internal static class PrefabEditWireApply
{
    /// <summary>Cached type resolution per full name, including the negative answer.</summary>
    private static readonly Dictionary<string, Type?> ResolvedTypes = new(StringComparer.Ordinal);

    private static readonly object TypeGate = new();

    private static MethodInfo? _writeGeneric;
    private static MethodInfo? _readGeneric;

    /// <summary>One build-mismatch line per Core generation, not per packet.</summary>
    private static bool _buildMismatchLogged;

    /// <summary>Type + reason pairs already refused once this generation (R51 finding 4).</summary>
    private static readonly HashSet<string> TypeRefusalLogged = new(StringComparer.Ordinal);

    /// <summary>Types whose preserved-field line has been printed this generation.</summary>
    private static readonly HashSet<string> PreservedLogged = new(StringComparer.Ordinal);

    /// <summary>Prefabs whose send-rate coalescing line has been printed this generation.</summary>
    private static readonly HashSet<int> CoalesceLogged = new();

    /// <summary>
    /// User indices whose handshake has been seen at <c>ok</c>. The edge into this set is what
    /// triggers a full re-send of every stored layer (R51 finding 5).
    /// </summary>
    private static readonly HashSet<int> HandshakeOkUsers = new();

    /// <summary>Reset the once-per-generation state (Core install / uninstall).</summary>
    internal static void ResetGenerationState()
    {
        _buildMismatchLogged = false;
        _localIdentity = null;
        OnWithdraw = null;
        OnApplyPending = null;
        lock (TypeGate)
        {
            ResolvedTypes.Clear();
            TypeRefusalLogged.Clear();
            PreservedLogged.Clear();
            CoalesceLogged.Clear();
            HandshakeOkUsers.Clear();
        }
        // A cached shape holds a resolved Type, which must not outlive the generation that
        // resolved it — the same rule ResolvedTypes follows.
        PrefabEditShapeReflect.ResetCache();
    }

    /// <summary>This process's game identity, read once and latched. Empty when it could not be read.</summary>
    private static string? _localIdentity;

    /// <summary>
    /// The identity string both sides must agree on:
    /// <see cref="PrefabEditBuildIdentity.Compose"/> over <c>GameVersionUtils.GetVersionData()</c>
    /// and <c>GameVersionUtils.GetNetworkVersion()</c>.
    ///
    /// <para><b>Why not the address library.</b> The first two versions of this method hashed
    /// <c>AddressLibrary.DetectedBuild</c>, a fingerprint of the local <c>GameAssembly.dll</c>. The
    /// client's and the server's are different files, so that rule could never hold across a real
    /// join and would have dropped every packet in the two-process check (R50 finding 10; the
    /// server's id was <c>248598344-6a148fd0-ed22e34-3dcd792996235366</c>, evidence 189). The
    /// version constants are what the two binaries actually share, and the network version is the
    /// game's own compatibility number — the server refuses a connection whose hail disagrees with
    /// it before Progenitor sees anything.</para>
    ///
    /// <para>Read once per Core generation: the version data is generated from compile-time
    /// constants and cannot change while the process runs.</para>
    /// </summary>
    internal static string LocalIdentity()
    {
        if (_localIdentity != null) return _localIdentity;
        try
        {
            var data = GameVersionUtils.GetVersionData();
            if (data == null)
            {
                Plugin.Logger?.LogWarning(
                    "PrefabRegistry: PrfE identity unavailable: GetVersionData returned null.");
                _localIdentity = PrefabEditBuildIdentity.Unknown;
                return _localIdentity;
            }
            var facts = new PrefabGameVersionFacts(
                data.Name,
                data.Major,
                data.Minor,
                data.Patch,
                data.Revision,
                data.VcsRevision,
                data.Build,
                data.UnixTimestamp,
                GameVersionUtils.GetNetworkVersion());
            _localIdentity = PrefabEditBuildIdentity.Compose(facts);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: PrfE identity detection threw: " + e.Message);
            _localIdentity = PrefabEditBuildIdentity.Unknown;
        }
        return _localIdentity;
    }

    /// <summary>
    /// FNV-1a of <see cref="LocalIdentity"/>. An unreadable identity hashes to 0, and 0 matches
    /// only 0 — a process that does not know what it is running never accepts bytes laid out for
    /// a build it cannot name.
    /// </summary>
    internal static int LocalBuildHash() => PrefabEditWire.Fnv1a(LocalIdentity());


    // ---- server: serialize and send ------------------------------------------------------------

    /// <summary>
    /// Run this prefab's readers and hash what they produced. Cheap relative to the rest: no
    /// reflection, no caps walk, no framing. Splitting it out is what lets the dedupe run
    /// <em>before</em> the expensive half instead of after it (job FEDW3, R51 finding 8).
    /// </summary>
    internal static bool TryReadLayer(
        World world,
        PrefabEditTable edits,
        int guidHash,
        out List<PrefabEditComponentBytes> records,
        out int payloadHash)
    {
        records = new List<PrefabEditComponentBytes>();
        payloadHash = 0;
        if (world == null || !world.IsCreated || edits == null) return false;
        records = edits.Wire.Serialize(
            guidHash,
            world,
            (typeName, e) => Plugin.Logger?.LogWarning(
                PrefabEditLog.EditLayerReaderFailed(guidHash, typeName, e)));
        if (records.Count == 0) return false;
        payloadHash = PrefabEditWire.PayloadHash(records);
        return true;
    }

    /// <summary>
    /// Frame already-read records into a packet: refuse locally anything the receiver would have
    /// to refuse, apply the caps, write the bytes.
    ///
    /// <para><b>The shape rule runs here too</b> (job FEDW3, R51 finding 1). A component whose
    /// every field is reference-shaped can carry nothing across two processes, so it is not
    /// shipped at all rather than shipped and wholly ignored; a component with <em>some</em>
    /// reference-shaped fields still ships, and the receiver keeps its own bytes for those.</para>
    /// </summary>
    internal static bool TryFrameLayer(
        PrefabEditTable edits,
        int guidHash,
        IReadOnlyList<PrefabEditComponentBytes> records,
        out byte[] packet,
        out string[] components)
    {
        packet = Array.Empty<byte>();
        components = Array.Empty<string>();

        var checkedRecords = new List<PrefabEditComponentBytes>(records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            var rec = records[i];
            if (!TryResolveType(rec.TypeFullName, out _, out var shape, out string reason))
            {
                LogOncePerType(rec.TypeFullName, reason, () => Plugin.Logger?.LogWarning(
                    PrefabEditLog.EditLayerRecordRefused(guidHash, rec.TypeFullName, reason)));
                continue;
            }
            if (!PrefabEditWire.SizeMatches(rec.Bytes.Length, shape!.Size))
            {
                // Once per type per generation, not once per pass: since FEDW2 both ends take the
                // same measure, so a disagreement is a standing fact about that component and
                // repeating it every frame told the operator nothing new (R51 finding 4).
                string sizeReason = PrefabEditWire.SizeMismatchReason(
                    rec.Bytes.Length, shape.Size, shape.TypeFullName);
                LogOncePerType(rec.TypeFullName, sizeReason, () => Plugin.Logger?.LogWarning(
                    PrefabEditLog.EditLayerRecordRefused(guidHash, rec.TypeFullName, sizeReason)));
                continue;
            }
            // The value domain, on the sending end too (job FEDW5, R53 finding 2). A local editor
            // that writes a NaN duration or an undeclared enum member is told here, by name, once
            // — rather than the packet going out and every client refusing it in silence.
            var domain = shape.ValueDomain;
            if (domain != null && !domain.TryAccept(rec.Bytes, out string badField, out string valueReason))
            {
                LogValueRefusedOnce(guidHash, rec.TypeFullName, badField, valueReason);
                continue;
            }
            if (!shape.WholeValue) LogPreservedOnce(guidHash, shape);
            checkedRecords.Add(rec);
        }

        var selected = PrefabEditWire.SelectRecords(checkedRecords, out var refusals);
        for (int i = 0; i < refusals.Count; i++)
            Plugin.Logger?.LogWarning(PrefabEditLog.EditLayerRecordSkipped(guidHash, refusals[i]));
        if (selected.Count == 0) return false;

        int identityHash = PrefabEditWire.Fnv1a(edits.Stamp(guidHash));
        if (!PrefabEditWire.TryWrite(guidHash, LocalBuildHash(), identityHash, selected, out packet, out string error))
        {
            Plugin.Logger?.LogWarning(PrefabEditLog.EditLayerBuildFailed(guidHash, error));
            packet = Array.Empty<byte>();
            return false;
        }

        components = new string[selected.Count];
        for (int i = 0; i < selected.Count; i++) components[i] = selected[i].TypeFullName;
        return true;
    }

    /// <summary>
    /// Serialize one prefab's touched components. False when nothing is registered, every reader
    /// failed, or no record survived the caps; <paramref name="packet"/> is then empty.
    /// </summary>
    internal static bool TryBuildLayer(
        World world,
        PrefabEditTable edits,
        int guidHash,
        out byte[] packet,
        out string[] components,
        out int payloadHash)
    {
        packet = Array.Empty<byte>();
        components = Array.Empty<string>();
        if (!TryReadLayer(world, edits, guidHash, out var records, out payloadHash)) return false;
        return TryFrameLayer(edits, guidHash, records, out packet, out components);
    }

    /// <summary>
    /// Push one prefab's layer to every handshaken, connected user.
    ///
    /// <para>Order of the gates, and why: read the values, hash them, ask the dedupe, ask the rate
    /// floor, and only then resolve types, walk the caps and frame the packet. FEDW1 built first
    /// and deduped second, so a prefab whose values never change still paid a full serialize every
    /// pass and the dedupe saved only the transport (R51 finding 8).</para>
    /// </summary>
    internal static int SendToConnected(World server, PrefabEditTable edits, int guidHash)
    {
        if (!Plugin.IsServerProcess || server == null || !server.IsCreated || edits == null) return 0;
        try
        {
            if (!TryReadLayer(server, edits, guidHash, out var records, out int hash)) return 0;
            if (!edits.Wire.WouldSend(guidHash, hash)) return 0;
            long now = NowMs();
            if (!edits.Wire.TryTakeSendSlot(guidHash, now))
            {
                LogCoalescedOnce(guidHash);
                return 0;
            }
            if (!TryFrameLayer(edits, guidHash, records, out var packet, out var components)) return 0;

            int sent = PushToHandshaken(server, packet);
            if (sent > 0)
            {
                // Recorded only after a real send: a build or transport failure must not leave the
                // prefab deduped against a layer nobody ever received.
                edits.Wire.RecordSent(guidHash, hash, now);
                Plugin.Logger?.LogInfo(PrefabEditLog.SentEditLayer(guidHash, components, sent));
            }
            return sent;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: PrfE send for guid {guidHash} threw: {e}");
            return 0;
        }
    }

    /// <summary>
    /// Send the trailing packet for every prefab the rate floor suppressed and whose interval has
    /// now passed. Called from the registry's wire frame; a suppressed change is coalesced, never
    /// dropped, so the last value always lands.
    /// </summary>
    internal static int FlushDeferredSends(World? server, PrefabEditTable edits)
    {
        if (!Plugin.IsServerProcess || server == null || !server.IsCreated || edits == null) return 0;
        var due = edits.Wire.DueDeferred(NowMs());
        int sent = 0;
        for (int i = 0; i < due.Length; i++) sent += SendToConnected(server, edits, due[i]);
        return sent;
    }

    /// <summary>
    /// Retract this prefab's layer (job FEDW3, R51 finding 7): a <c>count == 0</c> packet telling
    /// every client to drop it and restore the baseline. Sent when the last editor of a prefab is
    /// removed, or when a pass stops touching it. Does nothing when this prefab never had a layer
    /// out, and consumes the right to withdraw so the two seams cannot both send one.
    /// </summary>
    internal static int SendWithdraw(World? server, PrefabEditTable edits, int guidHash)
    {
        if (!Plugin.IsServerProcess || server == null || !server.IsCreated || edits == null) return 0;
        if (!edits.Wire.TakeSentForWithdraw(guidHash)) return 0;
        try
        {
            var packet = PrefabEditWire.WriteWithdraw(guidHash, LocalBuildHash(), 0);
            int sent = PushToHandshaken(server, packet);
            if (sent > 0)
                Plugin.Logger?.LogInfo(
                    PrefabEditLog.Prefix + "sent PrfE withdraw for " + guidHash + " to " + sent + " user(s)");
            return sent;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: PrfE withdraw for guid {guidHash} threw: {e}");
            return 0;
        }
    }

    private static int PushToHandshaken(World server, byte[] packet)
    {
        var bootstrap = server.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap == null) return 0;
        var em = server.EntityManager;
        var users = Query.Entities(
            em,
            includeDisabled: true,
            includeSystems: false,
            ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
        int sent = 0;
        for (int i = 0; i < users.Count; i++)
        {
            try
            {
                var user = em.GetComponentData<User>(users[i]);
                if (!ShouldSendToUser(user.IsConnected, user.Index)) continue;
                if (CustomEvents.SendToUser(bootstrap, user.Index, PrefabEditWire.EventId, packet))
                    sent++;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"PrefabRegistry: PrfE push to a user threw: {e.Message}");
            }
        }
        return sent;
    }

    /// <summary>
    /// Send every prefab that has a layer to one user: the join path (after its <c>PrfD</c> /
    /// <c>PrfK</c> — reliable events keep order, so the definition, the key row and then the
    /// edited values land in that order) and the resend path.
    ///
    /// <para>Bypasses the broadcast dedupe and the rate floor, which are per prefab and say
    /// nothing about one client, and uses the <b>per-user</b> record instead (job FEDW4, R52
    /// finding 3): a layer this user already has is not sent again, so the resend is idempotent
    /// and the handshake-ok edge no longer duplicates the join send. <paramref name="freshJoin"/>
    /// clears that record first, because a user index is reused and a joining client has seen
    /// nothing.</para>
    /// </summary>
    internal static int SendAllToUser(
        World server, PrefabEditTable edits, Entity userEntity, string who, bool freshJoin = false)
    {
        if (!Plugin.IsServerProcess || server == null || !server.IsCreated || edits == null) return 0;
        int sent = 0;
        try
        {
            var bootstrap = server.GetExistingSystemManaged<ServerBootstrapSystem>();
            if (bootstrap == null) return 0;
            var em = server.EntityManager;
            if (!em.HasComponent<User>(userEntity)) return 0;
            var user = em.GetComponentData<User>(userEntity);
            if (freshJoin) edits.Wire.ForgetUser(user.Index);
            var guids = edits.Wire.ReaderGuids();
            Array.Sort(guids);
            long now = NowMs();
            for (int i = 0; i < guids.Length; i++)
            {
                if (!TryReadLayer(server, edits, guids[i], out var read, out int hash)) continue;
                if (!edits.Wire.WouldSendToUser(user.Index, guids[i], hash)) continue;
                if (!TryFrameLayer(edits, guids[i], read, out var packet, out var components)) continue;
                if (!CustomEvents.SendToUser(bootstrap, user.Index, PrefabEditWire.EventId, packet)) continue;
                edits.Wire.RecordSentToUser(user.Index, guids[i], hash);
                // The broadcast dedupe and the rate floor are only touched by the join send, which
                // is what the whole server has just told this prefab's story to. A resend must not
                // move them: it would delay the next broadcast and drop an owed trailing send.
                if (freshJoin) edits.Wire.RecordSent(guids[i], hash, now);
                sent++;
                Plugin.Logger?.LogInfo(
                    PrefabEditLog.SentEditLayer(guids[i], components, 1)
                    + (freshJoin ? " (join of " : " (resend to ") + who + ")");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: PrfE join send threw: {e}");
        }
        return sent;
    }

    /// <summary>
    /// Re-push every stored layer to any user whose handshake has just completed (job FEDW3,
    /// R51 finding 5).
    ///
    /// <para><b>Why the join send is not enough.</b> The received layer lives on
    /// <c>PrefabEditTable.Wire</c>, which is a Core static, so a <em>client-side</em> Core swap
    /// gives the next generation an empty one — the layer is gone. The server's dedupe hash is
    /// untouched by that, so it never pushes again, and the player did not rejoin, so the join
    /// path never runs either: from that moment the client's own editors win permanently.</para>
    ///
    /// <para><b>What this poll can and cannot see</b> (R52 finding 3). It observes the edge into
    /// <c>Handshake.UserAnswerStatus(index) == "ok"</c>, which fires on a <em>server</em> Core
    /// generation (the server re-arms and every user re-answers) but <b>not</b> on a client-side
    /// swap: the re-arm is server-only, <c>HandshakeAnswerWatch.Arm</c> returns early for a user
    /// already at <c>Ok</c>, and <c>OnAnswer</c> writes <c>Ok</c> over <c>Ok</c>, so the status
    /// never leaves <c>ok</c> and there is no edge to see. The client-swap case is served by
    /// <c>PrefabRegistry.ResendEditLayersTo(userIndex)</c>, which the <c>Networking</c> side calls
    /// when an already-ok user re-answers. Both paths go through
    /// <see cref="SendAllToUser"/> and are deduped per user, so a user that already has the layers
    /// costs one hash comparison per prefab however often either fires.</para>
    ///
    /// <para>Polled rather than subscribed because the seam lives in <c>Networking</c>, which this
    /// module does not edit; <c>Handshake.UserAnswerStatus</c> is public and is the same token
    /// <see cref="ShouldSendToUser"/> already consults.</para>
    /// </summary>
    internal static int PollHandshakeResends(World? server, PrefabEditTable edits)
    {
        if (!Plugin.IsServerProcess || server == null || !server.IsCreated || edits == null) return 0;
        if (edits.Wire.ReaderGuids().Length == 0) return 0;
        int resent = 0;
        try
        {
            var em = server.EntityManager;
            var users = Query.Entities(
                em,
                includeDisabled: true,
                includeSystems: false,
                ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
            var connected = new HashSet<int>();
            for (int i = 0; i < users.Count; i++)
            {
                User user;
                try { user = em.GetComponentData<User>(users[i]); }
                catch { continue; }
                if (user.IsConnected) connected.Add(user.Index);
                if (!ShouldSendToUser(user.IsConnected, user.Index))
                {
                    // Not ok, or not ok any more: arm the edge so the next ok re-sends.
                    lock (TypeGate) HandshakeOkUsers.Remove(user.Index);
                    continue;
                }
                bool isNewEdge;
                lock (TypeGate) isNewEdge = HandshakeOkUsers.Add(user.Index);
                if (!isNewEdge) continue;
                int n = SendAllToUser(server, edits, users[i], "user " + user.Index);
                if (n > 0)
                {
                    resent += n;
                    Plugin.Logger?.LogInfo(PrefabEditLog.EditLayerResent("user " + user.Index, n));
                }
            }
            lock (TypeGate) HandshakeOkUsers.IntersectWith(connected);
            // A user index is reused, so the per-user records of a departed user must go with it.
            edits.Wire.RetainUsers(connected);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: PrfE handshake poll threw: {e}");
        }
        return resent;
    }

    /// <summary>
    /// Send every stored layer to one connected user by index, deduped per user (job FEDW4, R52
    /// finding 3). The seam behind <c>PrefabRegistry.ResendEditLayersTo</c>; see that method for
    /// what calls it and why the handshake-ok edge cannot.
    /// </summary>
    internal static int ResendToUserIndex(World? server, PrefabEditTable edits, int userIndex)
    {
        if (!Plugin.IsServerProcess || server == null || !server.IsCreated || edits == null) return 0;
        if (edits.Wire.ReaderGuids().Length == 0) return 0;
        try
        {
            var em = server.EntityManager;
            var users = Query.Entities(
                em,
                includeDisabled: true,
                includeSystems: false,
                ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
            for (int i = 0; i < users.Count; i++)
            {
                User user;
                try { user = em.GetComponentData<User>(users[i]); }
                catch { continue; }
                if (user.Index != userIndex || !user.IsConnected) continue;
                int n = SendAllToUser(server, edits, users[i], "user " + userIndex);
                if (n > 0) Plugin.Logger?.LogInfo(PrefabEditLog.EditLayerResent("user " + userIndex, n));
                return n;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: PrfE resend to user {userIndex} threw: {e}");
        }
        return 0;
    }

    /// <summary>
    /// Same user set as the config-sync push: connected, and past the handshake. The handshake
    /// state is read through the public <c>Handshake.UserAnswerStatus</c> token, which is set on
    /// the same accepted-answer path that fills the module's own handshake-ok sets.
    /// </summary>
    internal static bool ShouldSendToUser(bool isConnected, int userIndex)
    {
        if (!isConnected) return false;
        try
        {
            return string.Equals(Handshake.UserAnswerStatus(userIndex), HandshakeOkToken, StringComparison.Ordinal);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"PrefabRegistry: handshake status for user {userIndex} threw: {e.Message}");
            return false;
        }
    }

    /// <summary><c>HandshakeAnswerWatch.Token(Status.Ok)</c>.</summary>
    internal const string HandshakeOkToken = "ok";

    // ---- client: receive and apply ---------------------------------------------------------------

    /// <summary>
    /// <c>PrfE</c> receive handler. Origin-gates first (a server refuses a received layer),
    /// then parses, checks the build, stores the layer, and applies it. Every type refusal is
    /// per record; a parse failure, a build mismatch, or a server-side receive drops the packet.
    ///
    /// <para><b>The bounds, in order</b> (job FEDW3, R51 finding 2). The per-join packet and byte
    /// caps are charged first, because they are the only thing that bounds a flood; then the
    /// payload dedupe, which makes an identical repeat free of stores, writes and log lines; then
    /// the per-prefab synced-row budget, which bounds how many prefabs a server may reach into.
    /// Before FEDW3 only the last of the three existed, and it was charged once per prefab — so
    /// after the first packet for a guid every further one was free.</para>
    /// </summary>
    internal static unsafe void OnPacket(PrefabEditTable edits, World? world, byte* payload, int length)
    {
        if (!PrefabEditWire.TryAdmitReceivedPacket(Plugin.IsServerProcess, out _))
        {
            Plugin.Logger?.LogWarning(PrefabEditLog.EditLayerRefusedOnServer());
            return;
        }
        if (payload == null || length <= 0)
        {
            Plugin.Logger?.LogWarning(PrefabEditLog.EditLayerDropped("empty payload"));
            return;
        }
        if (!PrefabSyncBudget.TryAdmitEditPacket(length, out var editCapLog))
        {
            if (!string.IsNullOrEmpty(editCapLog)) Plugin.Logger?.LogWarning(editCapLog);
            return;
        }
        var copy = new byte[length];
        for (int i = 0; i < length; i++) copy[i] = payload[i];
        if (!PrefabEditWire.TryRead(
                new ReadOnlySpan<byte>(copy),
                out int guidHash,
                out int buildHash,
                out _,
                out var records,
                out string error))
        {
            Plugin.Logger?.LogWarning(PrefabEditLog.EditLayerDropped(error));
            return;
        }

        int local = LocalBuildHash();
        if (!PrefabEditWire.BuildMatches(buildHash, local))
        {
            if (!_buildMismatchLogged)
            {
                _buildMismatchLogged = true;
                Plugin.Logger?.LogWarning(PrefabEditLog.EditLayerBuildMismatch(
                    guidHash, buildHash, local, LocalIdentity()));
            }
            return;
        }

        if (records.Count == 0)
        {
            WithdrawReceived(world, edits, guidHash);
            return;
        }

        if (edits.Wire.NeedsBudgetCharge(guidHash))
        {
            if (!PrefabSyncBudget.TryAdmitDefinition(guidHash, out var capLog))
            {
                edits.Wire.ReleaseBudgetCharge(guidHash);
                if (!string.IsNullOrEmpty(capLog)) Plugin.Logger?.LogWarning(capLog);
                return;
            }
        }

        // An identical repeat changes nothing on the entity, so it must not cost an apply, a
        // baseline capture or a log line either. After the budget, not before: a packet the budget
        // refuses must not leave its fingerprint recorded as applied, or the retry that a later
        // join allows would be deduped away.
        if (!edits.Wire.ShouldApplyReceived(guidHash, PrefabEditWire.PayloadHash(records))) return;

        edits.Wire.SetReceived(guidHash, records);
        ApplyReceived(world, edits, guidHash, out bool pending);
        // The prefab entity may not be in the collection yet: PrfE rides in on the join ordering
        // the same way PrfD does, and PrfD has an explicit gate and a bounded frame retry while
        // this used to have neither (R51 finding 7).
        OnApplyPending?.Invoke(guidHash, pending);
    }

    /// <summary>
    /// Set by the registry at Install: queue (or clear) the bounded retry for a layer whose prefab
    /// entity did not resolve. Here rather than inline because the frame hook and its budget live
    /// with the definition retry it copies.
    /// </summary>
    internal static Action<int, bool>? OnApplyPending;

    /// <summary>
    /// The server retracted its layer for one prefab. The stored layer goes here; writing the
    /// captured baseline back is <see cref="OnWithdraw"/>, which the registry installs, because
    /// the entity binding, the baseline store and any local edit pass all live there.
    /// </summary>
    private static void WithdrawReceived(World? world, PrefabEditTable edits, int guidHash)
    {
        if (!edits.Wire.DropReceived(guidHash)) return;
        var handler = OnWithdraw;
        if (handler == null)
        {
            Plugin.Logger?.LogInfo(PrefabEditLog.EditLayerWithdrawn(guidHash, 0));
            return;
        }
        try { handler(world, guidHash); }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: PrfE withdraw of guid {guidHash} threw: {e}");
        }
    }

    /// <summary>
    /// Set by the registry at Install: restore the baselines of one prefab (and re-run any local
    /// editors) after a withdraw. Cleared at Uninstall with the rest of the generation state.
    /// </summary>
    internal static Action<World?, int>? OnWithdraw;

    /// <summary>
    /// Write the stored server layer for one prefab onto its entity. Called on receive and again
    /// after every local edit pass on that prefab, so a client-side editor cannot win.
    /// </summary>
    internal static string[] ApplyReceived(World? world, PrefabEditTable edits, int guidHash)
        => ApplyReceived(world, edits, guidHash, out _);

    /// <summary>
    /// <see cref="ApplyReceived(World?,PrefabEditTable,int)"/> reporting whether the layer is still
    /// owed an apply (job FEDW3, R51 finding 7). A <c>PrfE</c> that lands one frame before
    /// <c>PrefabCollectionSystem</c> has the entity used to be dropped in silence, and for the case
    /// the feature exists for — a client with <em>no</em> local editor for that guid — no later
    /// pass ever revisits it, so the layer was lost for the session.
    /// </summary>
    internal static string[] ApplyReceived(World? world, PrefabEditTable edits, int guidHash, out bool pending)
    {
        pending = false;
        if (edits == null) return Array.Empty<string>();
        var records = edits.Wire.Received(guidHash);
        if (records.Length == 0) return Array.Empty<string>();
        if (world == null || !world.IsCreated)
        {
            pending = true;
            return Array.Empty<string>();
        }

        var guid = new PrefabGUID(guidHash);
        if (!PrefabLookup.TryGetEntity(world, guid, out var entity))
        {
            pending = true;
            return Array.Empty<string>();
        }

        var applied = ApplyRecords(world, entity, guid, records, edits.Baselines);
        if (applied.Length > 0)
            Plugin.Logger?.LogDiag(PrefabEditLog.AppliedEditLayer(guidHash, applied));
        return applied;
    }

    /// <summary>
    /// Write one already-parsed layer onto <paramref name="entity"/>. The decision ladder —
    /// resolve, shape, size, <c>HasComponent</c>, mask, baseline, write, with a per-record catch —
    /// is <see cref="PrefabEditApplyDecision"/>, which the suite drives; everything here is the
    /// game-typed half it calls into (job FEDW3, R51 finding 6).
    /// </summary>
    internal static string[] ApplyRecords(
        World world,
        Entity entity,
        PrefabGUID guid,
        IReadOnlyList<PrefabEditComponentBytes> records,
        PrefabEditBaselines? baselines)
    {
        var site = new LiveApplySite(world, entity, guid, baselines);
        return PrefabEditApplyDecision.Apply(
            records,
            site,
            (name, reason) => Plugin.Logger?.LogWarning(
                PrefabEditLog.EditLayerRecordRefused(guid.GuidHash, name, reason)),
            shape => LogPreservedOnce(guid.GuidHash, shape),
            (name, field, reason) => LogValueRefusedOnce(guid.GuidHash, name, field, reason));
    }

    /// <summary>
    /// <see cref="IPrefabEditApplySite"/> against the live world: the only part of the apply that
    /// needs an <c>EntityManager</c>. One per packet, bound to one entity.
    /// </summary>
    private sealed class LiveApplySite : IPrefabEditApplySite
    {
        private readonly World _world;
        private readonly Entity _entity;
        private readonly PrefabGUID _guid;
        private readonly PrefabEditBaselines? _baselines;

        public LiveApplySite(World world, Entity entity, PrefabGUID guid, PrefabEditBaselines? baselines)
        {
            _world = world;
            _entity = entity;
            _guid = guid;
            _baselines = baselines;
        }

        public bool TryAcceptType(string? typeFullName, out PrefabEditTypeShape shape, out string reason)
        {
            bool ok = PrefabEditWireApply.TryResolveType(typeFullName, out _, out var s, out reason);
            shape = s!;
            return ok;
        }

        public bool HasComponent(PrefabEditTypeShape shape, out string reason)
        {
            reason = "";
            if (shape.Token is not Type type)
            {
                reason = "the resolved type was lost";
                return false;
            }
            if (PrefabEditWireApply.HasComponent(_world.EntityManager, _entity, type)) return true;
            reason = "the prefab entity does not carry it";
            return false;
        }

        public byte[]? CurrentBytes(PrefabEditTypeShape shape)
        {
            if (shape.Token is not Type type) return null;
            try
            {
                object? current;
                if (type == typeof(ItemData))
                {
                    current = _world.EntityManager.GetComponentData<ItemData>(_entity);
                }
                else
                {
                    var read = ReadGeneric();
                    if (read == null) return null;
                    current = read.MakeGenericMethod(type)
                        .Invoke(null, new object[] { _entity, _world.EntityManager });
                }
                if (current == null) return null;
                return PrefabEditWire.TryMarshalBytes(current, type, out var bytes, out _) ? bytes : null;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning(
                    $"PrefabRegistry: PrfE could not read this client's own {type.Name} on guid {_guid.GuidHash}: {e.Message}");
                return null;
            }
        }

        public void CaptureBaseline(PrefabEditTypeShape shape, string shortName)
        {
            if (shape.Token is not Type type) return;
            PrefabEditWireApply.CaptureBaseline(_world, _entity, _guid, type, shortName, _baselines);
        }

        public void Write(PrefabEditTypeShape shape, byte[] bytes)
        {
            if (shape.Token is not Type type)
                throw new InvalidOperationException("the resolved type was lost");
            WriteBoxed(_world, _entity, _guid, type, Unbox(bytes, type, shape.Size));
        }
    }

    // ---- type resolution -------------------------------------------------------------------------

    /// <summary>
    /// Resolve a wire type name against assemblies that are <em>already loaded</em> and whose
    /// simple name starts with ProjectM, Stunlock or Unity, then classify its layout. Nothing is
    /// ever loaded to satisfy a packet.
    ///
    /// <para>The two halves are deliberate: the name and type rules
    /// (<see cref="PrefabEditWire.TryAcceptType"/>) and the shape walk
    /// (<see cref="PrefabEditShapeReflect"/>) are both game-free and both unit-tested; only the
    /// assembly search is here.</para>
    /// </summary>
    internal static bool TryResolveType(
        string? fullName, out Type? type, out PrefabEditTypeShape? shape, out string reason)
    {
        type = null;
        shape = null;
        if (string.IsNullOrEmpty(fullName))
        {
            reason = "type name is empty";
            return false;
        }
        if (PrefabEditWire.IsRefusedTypeName(fullName, out reason)) return false;

        Type? found;
        lock (TypeGate)
        {
            if (!ResolvedTypes.TryGetValue(fullName!, out found))
            {
                found = SearchLoadedAssemblies(fullName!);
                ResolvedTypes[fullName!] = found;
            }
        }
        if (!PrefabEditWire.TryAcceptType(found, out reason)) return false;
        // The boundary, before the shape walk is even consulted: a type Core does not vouch for is
        // refused on both ends (job FEDW4, R52 finding 1). This method is the single choke point —
        // the server calls it from TryFrameLayer, the client from LiveApplySite.TryAcceptType — so
        // "the server never ships it, the client never applies it" is one rule in one place.
        if (!PrefabEditWireTypes.TryGet(fullName, out var vouched))
        {
            reason = PrefabEditWireTypes.NotVouchedReason(fullName);
            return false;
        }
        bool describes = PrefabEditShapeReflect.TryDescribe(found, out var described, out reason);
        // The shape layer is game-free and never logs; whatever it had to say about this build —
        // today only the value domain's dump-literal cross-check — is written here, once per Core
        // generation because that is how often the layer produces it (job FEDW6, R54 finding 3).
        var notices = PrefabEditShapeReflect.DrainNotices();
        for (int i = 0; i < notices.Length; i++) Plugin.Logger?.LogWarning(notices[i]);
        if (!describes) return false;
        // The second gate: the classifier's answer must agree with the per-field policy, so a game
        // update that moves a field or changes a size fails closed rather than widening the list.
        if (!PrefabEditWireTypes.Agrees(vouched, described, out reason)) return false;
        type = found;
        shape = described;
        reason = "";
        return true;
    }

    // First match wins here, and that is deliberate: a component type resolved from this search is
    // re-checked against the seed's declared size and field set by PrefabEditWireTypes.Agrees, so a
    // wrong pick through a duplicate full name fails closed one gate later (R52 measured the
    // duplicates in this interop: 14, all zero-field compiler artifacts). The enum resolution in
    // PrefabEditShapeReflect.ResolveEnum has no such backstop — its answer *is* the mask — so it
    // collects every match and refuses when there is more than one (job FEDW7, R55 finding 6).
    private static Type? SearchLoadedAssemblies(string fullName)
    {
        Assembly[] loaded;
        try { loaded = AppDomain.CurrentDomain.GetAssemblies(); }
        catch { return null; }
        for (int i = 0; i < loaded.Length; i++)
        {
            string? name;
            try { name = loaded[i].GetName().Name; }
            catch { continue; }
            if (!PrefabEditWire.IsAllowedAssemblyName(name)) continue;
            try
            {
                var t = loaded[i].GetType(fullName, throwOnError: false);
                if (t != null) return t;
            }
            catch
            {
                // A reflection-only or partially loaded assembly is skipped, never loaded.
            }
        }
        return null;
    }

    private static bool HasComponent(EntityManager em, Entity entity, Type type)
    {
        try
        {
            if (!em.Exists(entity)) return false;
            return em.HasComponent(entity, new ComponentType(Il2CppType.From(type), ComponentType.AccessMode.ReadOnly));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"PrefabRegistry: PrfE HasComponent({type.Name}) threw: {e.Message}");
            return false;
        }
    }

    private static object Unbox(byte[] bytes, Type type, int size)
    {
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(bytes, 0, buffer, size);
            var boxed = Marshal.PtrToStructure(buffer, type);
            if (boxed == null)
                throw new InvalidOperationException("PtrToStructure returned null for " + type.FullName);
            return boxed;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The one writer. <c>ProjectM.ItemData</c> takes the known-good
    /// <c>SetComponentData&lt;ItemData&gt;</c> path plus the <c>ItemHashLookupMap</c> re-sync the
    /// edit handle does; everything else goes through <c>EntityExtensions.Write&lt;T&gt;</c>
    /// closed with <see cref="MethodInfo.MakeGenericMethod"/>.
    ///
    /// <para><b>Why this is not interop trap 2.</b> The trap is about calling into an IL2CPP
    /// generic (<c>SetComponentData&lt;T&gt;</c>, <c>NativeList&lt;T&gt;.Add</c>) whose native
    /// instantiation may not exist. <c>Write&lt;T&gt;</c> is a managed CoreCLR generic: it
    /// resolves a <c>TypeIndex</c> through <c>Il2CppType.From</c> and calls the non-generic
    /// <c>SetComponentDataRaw(entity, typeIndex, void*, int)</c>. No IL2CPP generic is
    /// instantiated, and <c>RawWriteGuard</c> still gates the size.</para>
    ///
    /// <para><b><c>ItemData</c> is held to the shape rule like every other type.</b> It used to be
    /// the one path with no shape check of any kind, and it carries an <c>Entity</c> field (R51
    /// finding 1). The bytes reaching here have already been through the preservation mask, so
    /// that field is this client's own whichever branch writes them.</para>
    /// </summary>
    private static void WriteBoxed(World world, Entity entity, PrefabGUID guid, Type type, object value)
    {
        var em = world.EntityManager;
        if (type == typeof(ItemData))
        {
            em.SetComponentData(entity, (ItemData)value);
            PrefabEditHandle.SyncItemDataAfterWire(world, entity, guid);
            return;
        }

        var write = WriteGeneric()
            ?? throw new InvalidOperationException("EntityExtensions.Write<T> was not found");
        MethodInfo closed;
        try
        {
            closed = write.MakeGenericMethod(type);
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException(
                "not a blittable unmanaged struct (Write<T> constraint): " + e.Message);
        }
        try
        {
            closed.Invoke(null, new[] { (object)entity, em, value });
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            throw e.InnerException;
        }
    }

    /// <summary>
    /// Capture the pre-write value the same way the edit handle does, so the server layer is
    /// restorable by exactly the machinery FEDIT1 shipped. Keyed by the short type name, which is
    /// what the handle uses, so a local editor and a server layer over the same component share
    /// one baseline and the first of them defines vanilla.
    /// </summary>
    private static void CaptureBaseline(
        World world, Entity entity, PrefabGUID guid, Type type, string shortName, PrefabEditBaselines? baselines)
    {
        if (!PrefabEditPass.NeedsCapture(baselines, guid.GuidHash, shortName)) return;
        try
        {
            var em = world.EntityManager;
            if (type == typeof(ItemData))
            {
                var original = em.GetComponentData<ItemData>(entity);
                PrefabEditPass.TryCapture(
                    baselines, guid.GuidHash, shortName, true, entity.Index, entity.Version, ctx =>
                    {
                        if (ctx is not World w || !w.IsCreated) return;
                        w.EntityManager.SetComponentData(entity, original);
                        PrefabEditHandle.SyncItemDataAfterWire(w, entity, guid);
                    });
                return;
            }

            var read = ReadGeneric();
            if (read == null) return;
            object? current;
            try
            {
                current = read.MakeGenericMethod(type).Invoke(null, new[] { (object)entity, em });
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning(
                    $"PrefabRegistry: PrfE baseline read of {type.Name} on guid {guid.GuidHash} failed: {e.Message}");
                return;
            }
            if (current == null) return;
            PrefabEditPass.TryCapture(
                baselines, guid.GuidHash, shortName, true, entity.Index, entity.Version, ctx =>
                {
                    if (ctx is not World w || !w.IsCreated) return;
                    WriteBoxed(w, entity, guid, type, current);
                });
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                $"PrefabRegistry: PrfE baseline capture of {type.Name} on guid {guid.GuidHash} failed: {e.Message}");
        }
    }

    private static MethodInfo? WriteGeneric()
    {
        if (_writeGeneric != null) return _writeGeneric;
        _writeGeneric = FindGeneric(nameof(EntityExtensions.Write), 3);
        return _writeGeneric;
    }

    private static MethodInfo? ReadGeneric()
    {
        if (_readGeneric != null) return _readGeneric;
        _readGeneric = FindGeneric(nameof(EntityExtensions.Read), 2);
        return _readGeneric;
    }

    private static MethodInfo? FindGeneric(string name, int parameterCount)
    {
        var methods = typeof(EntityExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static);
        for (int i = 0; i < methods.Length; i++)
        {
            if (!string.Equals(methods[i].Name, name, StringComparison.Ordinal)) continue;
            if (!methods[i].IsGenericMethodDefinition) continue;
            if (methods[i].GetParameters().Length != parameterCount) continue;
            return methods[i];
        }
        return null;
    }

    // ---- once-per-generation log guards -----------------------------------------------------------

    /// <summary>Monotonic milliseconds; the rate floor's clock, never a wall time.</summary>
    private static long NowMs() => Environment.TickCount64;

    /// <summary>
    /// One line per (type, reason) per Core generation. A record refused for a standing reason —
    /// a size disagreement, a shape refusal — is refused on every pass, and repeating it every
    /// frame is noise, not information (R51 finding 4).
    /// </summary>
    /// <summary>
    /// One "this component will not travel" line per type per Core generation, for the write site
    /// (job FEDW4, R52 finding 1). Shares the refusal set with <see cref="LogOncePerType"/> so a
    /// type refused at registration and again at framing says it once.
    /// </summary>
    internal static void LogUnvouchedType(int guidHash, string? typeFullName, string reason)
        => LogOncePerType(typeFullName, reason, () => Plugin.Logger?.LogWarning(
            PrefabEditLog.EditLayerTypeNotVouched(guidHash, typeFullName, reason)));

    private static void LogOncePerType(string? typeFullName, string reason, Action write)
    {
        string key = (typeFullName ?? "") + "|" + reason;
        lock (TypeGate)
        {
            if (!TypeRefusalLogged.Add(key)) return;
        }
        write();
    }

    /// <summary>
    /// One value-domain refusal per type and field per Core generation (job FEDW5, R53 finding 2).
    /// Keyed on the type and the field and <b>not</b> on the reason: the reason carries the value,
    /// which a hostile server can vary per packet, and keying on it would hand that server one log
    /// line per packet. The first line of each pair says everything the operator needs.
    /// </summary>
    internal static void LogValueRefusedOnce(int guidHash, string? typeFullName, string? field, string reason)
    {
        string key = "value|" + (typeFullName ?? "") + "|" + (field ?? "");
        lock (TypeGate)
        {
            if (!TypeRefusalLogged.Add(key)) return;
        }
        Plugin.Logger?.LogWarning(PrefabEditLog.EditLayerValueRefused(guidHash, typeFullName, field, reason));
    }

    private static void LogPreservedOnce(int guidHash, PrefabEditTypeShape shape)
    {
        lock (TypeGate)
        {
            if (!PreservedLogged.Add(shape.TypeFullName)) return;
        }
        Plugin.Logger?.LogInfo(
            PrefabEditLog.EditLayerPreserved(guidHash, shape.TypeFullName, shape.PreservedFields));
    }

    private static void LogCoalescedOnce(int guidHash)
    {
        lock (TypeGate)
        {
            if (!CoalesceLogged.Add(guidHash)) return;
        }
        Plugin.Logger?.LogInfo(
            PrefabEditLog.EditLayerCoalesced(guidHash, PrefabEditWireLayer.MinSendIntervalMs));
    }
}
