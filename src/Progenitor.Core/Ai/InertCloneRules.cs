using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Progenitor.Ai;

/// <summary>What a Core-stripped buff clone is for.</summary>
internal enum InertCloneRole
{
    /// <summary>Not one of Core's inert clones: left exactly as the registry built it.</summary>
    None = 0,

    /// <summary>
    /// A tag with no effect and no visuals: the API-spawn marker (<c>progenitor.core.api-spawned</c>) and
    /// every <c>ctx.Ai.Variants</c> marker.
    /// </summary>
    Marker = 1,

    /// <summary>A marker whose client copy also carries <c>HideTargetHUD</c>: <c>UnitSpawnOptions.HideHealthBar</c>.</summary>
    HideHud = 2,
}

/// <summary>
/// Game-free rules for Core's inert buff clones: the fixed registry ids, the components each side
/// removes, and the <c>HideTargetHUD</c> value the hide-HUD clone's client copy carries. The live halves
/// are <c>AiVariants.MakeInert</c> (server strip, shared by every marker), <c>InertClones</c> (client
/// strip and the server's announcements) and <c>UnitHealthBars</c> (the hide-HUD clone).
///
/// <para><b>Why both sides.</b> The registry rewrites a clone's <c>NetSnapshot</c> prefab GUID, so a
/// client spawns a replicated buff from <em>its own</em> copy of the clone, which a Progenitor client
/// builds from the definition packet by instantiating its own source prefab. The server strip never
/// reaches that copy.</para>
///
/// <para><b>Which clones the client strips.</b> Core's two fixed ids, recognized by id, and any other
/// clone GUID the server announced on <see cref="InertCloneWire.EventId"/> (the variant markers). The
/// client never infers a marker from an id's shape: a plugin's own clone is left alone unless the
/// server says Core stripped it.</para>
/// </summary>
internal static class InertCloneRules
{
    /// <summary>The source every Core marker is cloned from (the one whose strip list is verified).</summary>
    internal const string MarkerSource = "Buff_General_Haste";

    /// <summary>The API-spawn marker's registry id. Never change it: saved units reference its GUID.</summary>
    internal const string ApiSpawnMarkerId = "progenitor.core.api-spawned";

    /// <summary>The hide-health-bar clone's registry id. Never change it: saved units reference its GUID.</summary>
    internal const string HideHudId = "progenitor.core.hide-hud";

    /// <summary>
    /// <c>HideTargetHUD.Type</c> on the hide-HUD clone's client copy: -1, every flag (<c>CharacterHUD</c> 1
    /// and <c>TargetInfo</c> 2). The value the vanilla carriers read on the server prefab
    /// (<c>AB_Vampire_Sword_Shockwave_Recast_TravelEnd</c> 821686839 and <c>AB_Interact_CoffinSleeping</c>
    /// 845443603, bridge prefab op, 2026-09-25).
    /// </summary>
    internal const int HideTargetHudType = -1;

    /// <summary>
    /// Components removed from a marker's <b>server</b> prefab by <c>AiVariants.StripMarker</c>, in namespace
    /// <c>ProjectM</c> unless qualified. <c>LifeTime</c> is written <c>{-1, None}</c> and
    /// <c>Buff_Persists_Through_Death</c> is added besides. Those in <see cref="PrefabOnlyStrip"/> are only
    /// removed from a prefab (an instance losing <c>BuffSequenceSpawned</c> keeps its sequence playing).
    /// </summary>
    internal static readonly string[] ServerStrip =
    {
        "CreateGameplayEventsOnSpawn",
        "GameplayEventListeners",
        "RemoveBuffOnGameplayEvent",
        "RemoveBuffOnGameplayEventEntry",
        "ModifyMovementSpeedBuff",
        "ModifyMovementSpeedBuffModification",
        "BuffSequence",
        "Sequencer.BuffSequenceSpawned",
        "Sequencer.SpawnSequenceForEntity",
        "SequenceGUID",
    };

    /// <summary>The part of <see cref="ServerStrip"/> removed only from a prefab.</summary>
    internal static readonly string[] PrefabOnlyStrip =
    {
        "BuffSequence",
        "Sequencer.BuffSequenceSpawned",
        "Sequencer.SpawnSequenceForEntity",
        "SequenceGUID",
    };

    /// <summary>
    /// What the client removes on top of <see cref="ServerStrip"/>: the buff-bar entry, so the haste icon
    /// does not show in the target's buff list. Removed by the archetype's own component type.
    /// </summary>
    internal static readonly string[] ClientExtraStrip = { "ShowOnBuffBar", "ShowOnBuffBarData" };

    /// <summary>Everything the client removes from its copy: <see cref="ServerStrip"/> then <see cref="ClientExtraStrip"/>.</summary>
    internal static IReadOnlyList<string> ClientStrip
    {
        get
        {
            var all = new List<string>(ServerStrip);
            all.AddRange(ClientExtraStrip);
            return all;
        }
    }

    /// <summary>The component's short type name (what <c>GetManagedType().Name</c> reports): the part after the last dot.</summary>
    internal static string ShortName(string listed)
    {
        int dot = listed.LastIndexOf('.');
        return dot < 0 ? listed : listed.Substring(dot + 1);
    }

    /// <summary>
    /// The client's stripping decision for one prefab (review round 2): strip only a <c>Buff</c> prefab,
    /// and only when it is one of Core's two fixed clones (by id or by their GUIDs), or a clone the server
    /// announced <b>whose registry source is</b> <paramref name="markerSourceGuid"/>
    /// (<see cref="MarkerSource"/>). An announcement alone never strips anything: a GUID that names a
    /// vanilla prefab, an item, or another plugin's clone of some other buff is left alone.
    /// <paramref name="sourceGuid"/> is the applied definition's source, or after a client Core swap the
    /// source the registry remembered for the clone; 0 when unknown. <paramref name="markerSourceGuid"/> is 0
    /// when the client could not resolve it, and then no announced clone is stripped.
    /// </summary>
    internal static InertCloneRole Decide(
        string? id,
        int guid,
        bool isBuff,
        int apiSpawnMarkerGuid,
        int hideHudGuid,
        IReadOnlyDictionary<int, InertCloneRole>? announced,
        int sourceGuid,
        int markerSourceGuid)
    {
        if (!isBuff || guid == 0) return InertCloneRole.None;
        if (string.Equals(id, HideHudId, StringComparison.Ordinal) || guid == hideHudGuid) return InertCloneRole.HideHud;
        if (string.Equals(id, ApiSpawnMarkerId, StringComparison.Ordinal) || guid == apiSpawnMarkerGuid) return InertCloneRole.Marker;
        if (announced == null || !announced.TryGetValue(guid, out var role)) return InertCloneRole.None;
        if (markerSourceGuid == 0 || sourceGuid != markerSourceGuid) return InertCloneRole.None;
        return role;
    }

    /// <summary>
    /// True when <paramref name="type"/> hides both the character HUD and the target info, given the flag
    /// values the dump declares.
    /// </summary>
    internal static bool HidesHealthBar(int type, int characterHudFlag, int targetInfoFlag) =>
        (type & characterHudFlag) != 0 && (type & targetInfoFlag) != 0;
}

/// <summary>
/// Server → client, on <see cref="InertCloneWire.EventId"/> (<c>'PrIc'</c>): "Core made the clone with
/// this GUID inert on the server; strip your copy the same way." Fixed size, unmanaged and sequential.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct InertCloneMessage
{
    public const int CurrentSchema = 1;

    public int Schema;
    public int Guid;
    public int Role;
}

/// <summary>Encoding of <see cref="InertCloneMessage"/>. Game-free.</summary>
internal static class InertCloneWire
{
    /// <summary><c>'PrIc'</c>, in Progenitor's reserved event range.</summary>
    public const int EventId = 0x50724963;

    public static InertCloneMessage Announce(int guid, InertCloneRole role) =>
        new() { Schema = InertCloneMessage.CurrentSchema, Guid = guid, Role = (int)role };

    /// <summary>False (with why) for a wrong schema, a zero GUID or a role that is not Marker or HideHud.</summary>
    public static bool TryDecode(InertCloneMessage message, out int guid, out InertCloneRole role, out string? why)
    {
        guid = message.Guid;
        role = InertCloneRole.None;
        why = null;
        if (message.Schema != InertCloneMessage.CurrentSchema) { why = "schema " + message.Schema; return false; }
        if (message.Guid == 0) { why = "a zero GUID"; return false; }
        if (message.Role != (int)InertCloneRole.Marker && message.Role != (int)InertCloneRole.HideHud) { why = "role " + message.Role; return false; }
        role = (InertCloneRole)message.Role;
        return true;
    }
}

/// <summary>
/// The set of inert clone GUIDs, on either side: the server records what it stripped and replays it to
/// each joiner (and to a client whose Core swapped), the client records what it was told. Game-free.
/// </summary>
internal sealed class InertCloneBook
{
    private readonly Dictionary<int, InertCloneRole> _roles = new();

    public int Count => _roles.Count;

    public IReadOnlyDictionary<int, InertCloneRole> Roles => _roles;

    /// <summary>Record <paramref name="guid"/>; true when this is new or changed its role.</summary>
    public bool Announce(int guid, InertCloneRole role)
    {
        if (guid == 0 || role == InertCloneRole.None) return false;
        if (_roles.TryGetValue(guid, out var had) && had == role) return false;
        _roles[guid] = role;
        return true;
    }

    /// <summary>Drop <paramref name="guid"/>; true when it was recorded.</summary>
    public bool Forget(int guid) => _roles.Remove(guid);

    public void Clear() => _roles.Clear();

    public KeyValuePair<int, InertCloneRole>[] Snapshot()
    {
        var copy = new KeyValuePair<int, InertCloneRole>[_roles.Count];
        int i = 0;
        foreach (var pair in _roles) copy[i++] = pair;
        return copy;
    }
}
