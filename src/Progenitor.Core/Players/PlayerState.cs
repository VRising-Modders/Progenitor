using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Players;

/// <summary>Whether <c>ctx.Players.State</c> could read the player, and if not, why.</summary>
public enum PlayerStateStatus
{
    /// <summary>Read. The state fields are meaningful.</summary>
    Ok,

    /// <summary>The user exists but has no character (never created one, or the character entity is gone).</summary>
    NoCharacter,

    /// <summary>The value given is not a player: no <c>PlayerInfo</c>, or an entity without a <c>User</c> component.</summary>
    NotAPlayer,

    /// <summary>
    /// The handle is from an earlier world load, the entity no longer exists, or the user entity
    /// now belongs to another account. Look the player up again.
    /// </summary>
    Stale,

    /// <summary>Not on the server, or the server world is not running.</summary>
    Unavailable,
}

/// <summary>
/// The form a player is in, from the shapeshift buff on the character
/// (<c>AB_Shapeshift_&lt;Form&gt;_…</c>). <see cref="Other"/> is a form this build does not name
/// (V Blood forms, for example), or the controller's <c>Shapeshift.IsShapeshifted</c> flag with no
/// recognized form buff.
/// </summary>
public enum PlayerShapeshiftKind
{
    /// <summary>Not shapeshifted.</summary>
    None,

    /// <summary>Bat form.</summary>
    Bat,

    /// <summary>Wolf form.</summary>
    Wolf,

    /// <summary>Bear form.</summary>
    Bear,

    /// <summary>Rat form.</summary>
    Rat,

    /// <summary>Toad form.</summary>
    Toad,

    /// <summary>Human disguise.</summary>
    Human,

    /// <summary>Shapeshifted into a form this enum does not name.</summary>
    Other,
}

/// <summary>
/// A read-only snapshot of one player's combat and form state from <c>ctx.Players.State</c>.
/// Read once per call from the live world; never cached. When <see cref="Status"/> is not
/// <see cref="PlayerStateStatus.Ok"/>, every flag is false and <see cref="Reason"/> says why.
/// </summary>
public sealed class PlayerStateInfo
{
    /// <summary>Whether the read succeeded.</summary>
    public PlayerStateStatus Status { get; init; }

    /// <summary>True when <see cref="Status"/> is <see cref="PlayerStateStatus.Ok"/>.</summary>
    public bool Ok => Status == PlayerStateStatus.Ok;

    /// <summary>Why the read did not succeed, or a note about a partial read; empty otherwise.</summary>
    public string Reason { get; init; } = "";

    /// <summary><c>User.IsConnected</c> now.</summary>
    public bool Online { get; init; }

    /// <summary>
    /// The character carries the ECS <c>Disabled</c> tag, which is how an offline player's
    /// character sits in the world. Its buffs are still read, and an offline character keeps the
    /// buffs it had when the player left, so <see cref="InPvpCombat"/>, <see cref="InCombat"/>,
    /// <see cref="Downed"/> and the form can read true while <see cref="Online"/> is false.
    /// </summary>
    public bool CharacterDisabled { get; init; }

    /// <summary>Dead: <c>Health.IsDead</c> or the <c>Dead</c> component on the character (awaiting respawn).</summary>
    public bool Dead { get; init; }

    /// <summary>
    /// Downed and not dead: the character carries <c>Buff_General_Vampire_Wounded_Buff</c>, the
    /// buff the game applies when a player at 0 health takes another hit.
    /// </summary>
    public bool Downed { get; init; }

    /// <summary>
    /// The character carries the PvP combat buff (<c>Buff_InCombat_PvPVampire</c>), the buff the
    /// game's own PvP-combat test looks for. The game's test also checks one other component
    /// condition that this read does not evaluate. An offline character keeps its last buffs, so
    /// this can read true while <see cref="Online"/> is false.
    /// </summary>
    public bool InPvpCombat { get; init; }

    /// <summary>In any combat: <see cref="InPvpCombat"/>, or the PvE combat buff <c>Buff_InCombat</c>.</summary>
    public bool InCombat { get; init; }

    /// <summary>In a form: <see cref="ShapeshiftKind"/> is not <see cref="PlayerShapeshiftKind.None"/>.</summary>
    public bool Shapeshifted => ShapeshiftKind != PlayerShapeshiftKind.None;

    /// <summary>Which form, from the form buff's name.</summary>
    public PlayerShapeshiftKind ShapeshiftKind { get; init; }

    /// <summary>PrefabGUID hash of the buff that decided <see cref="ShapeshiftKind"/>; 0 when none (or only the controller flag).</summary>
    public int ShapeshiftBuff { get; init; }

    /// <summary>Prefab name of <see cref="ShapeshiftBuff"/>; empty when none.</summary>
    public string ShapeshiftBuffName { get; init; } = "";

    /// <summary><c>Health.Value</c>; 0 when unreadable.</summary>
    public float Health { get; init; }

    /// <summary><c>Health.MaxHealth</c>; 0 when unreadable.</summary>
    public float MaxHealth { get; init; }

    /// <summary>Convenience: read, not dead and not downed. Says nothing about other impairments (stuns, menus, mounts).</summary>
    public bool Standing => Ok && !Dead && !Downed;

    /// <summary>One line for logs and chat.</summary>
    public override string ToString()
    {
        if (!Ok) return Status + (Reason.Length > 0 ? " (" + Reason + ")" : "");
        var ci = CultureInfo.InvariantCulture;
        return (Online ? "online" : "offline")
            + (CharacterDisabled ? " disabled" : "")
            + (Dead ? " dead" : Downed ? " downed" : " standing")
            + (InPvpCombat ? " pvp-combat" : InCombat ? " combat" : "")
            + " form=" + ShapeshiftKind
            + (ShapeshiftBuffName.Length > 0 ? "(" + ShapeshiftBuffName + ")" : "")
            + " hp=" + Health.ToString("0.#", ci) + "/" + MaxHealth.ToString("0.#", ci)
            + (Reason.Length > 0 ? " note: " + Reason : "");
    }

    internal static PlayerStateInfo Fail(PlayerStateStatus status, string reason, bool online = false) =>
        new() { Status = status, Reason = reason ?? "", Online = online };
}

/// <summary>What the live reader collected; game-free so the mapping is tested without the game.</summary>
internal struct PlayerStateFacts
{
    public bool Online;
    public bool HasCharacter;
    public bool CharacterExists;
    public bool CharacterDisabled;
    public bool HealthRead;
    public float Health;
    public float MaxHealth;
    public bool HealthIsDead;
    public bool HasDeadComponent;
    public bool BuffsRead;
    /// <summary>Buffs on the character: PrefabGUID hash and prefab name (empty when unnamed).</summary>
    public IReadOnlyList<(int Guid, string Name)>? Buffs;
    /// <summary>The controller's <c>Shapeshift.IsShapeshifted</c>; null when the component is absent or unreadable.</summary>
    public bool? ControllerShapeshifted;
    public int DownedBuff;
    public int PvpCombatBuff;
    public int CombatBuff;
}

/// <summary>
/// The game facts <c>ctx.Players.State</c> relies on, and the mapping from collected facts to a
/// <see cref="PlayerStateInfo"/>. Game-free.
/// </summary>
internal static class PlayerStateLogic
{
    /// <summary>The downed state: applied when a player at 0 health takes another hit.</summary>
    internal const string DownedBuffName = "Buff_General_Vampire_Wounded_Buff";

    /// <summary>The PvP combat buff (the game's <c>ServerRootPrefabCollection.VampirePvPBuffPrefabGUID</c> role).</summary>
    internal const string PvpCombatBuffName = "Buff_InCombat_PvPVampire";

    /// <summary>The PvE combat buff.</summary>
    internal const string CombatBuffName = "Buff_InCombat";

    /// <summary>GUID used when the name table cannot be read. Read live from the game's prefabs.</summary>
    internal const int DownedBuffGuid = -1992158531;

    /// <summary>GUID used when the name table cannot be read. Read live from the game's prefabs.</summary>
    internal const int PvpCombatBuffGuid = 697095869;

    /// <summary>GUID used when the name table cannot be read. Read live from the game's prefabs.</summary>
    internal const int CombatBuffGuid = 581443919;

    internal const string ShapeshiftPrefix = "AB_Shapeshift_";

    /// <summary>
    /// <c>AB_Shapeshift_*</c> buffs that are vampire powers cast from the same menu or the
    /// return to normal form, not forms.
    /// </summary>
    internal static readonly string[] NotForms =
    {
        "NormalForm", "BloodMend", "BloodHunger", "ShareBlood", "DominatingPresence", "CommandingPresence",
    };

    /// <summary>What a buff name says about forms.</summary>
    internal enum FormBuff
    {
        /// <summary>Not a shapeshift buff.</summary>
        NotShapeshift,

        /// <summary>A shapeshift power that is not a form.</summary>
        Power,

        /// <summary>A named form (<see cref="PlayerShapeshiftKind"/> other than Other).</summary>
        Known,

        /// <summary>A form buff whose form this enum does not name.</summary>
        Unknown,
    }

    /// <summary>
    /// Classify a buff prefab name: <c>AB_Shapeshift_&lt;Token&gt;_…Buff…</c>. The token after the
    /// prefix names the form (<c>AB_Shapeshift_Bat_TakeFlight_Buff</c> is bat form,
    /// <c>AB_Shapeshift_Wolf_PMK_Skin02_Buff</c> wolf form).
    /// </summary>
    internal static FormBuff Classify(string? name, out PlayerShapeshiftKind kind)
    {
        kind = PlayerShapeshiftKind.None;
        if (string.IsNullOrEmpty(name) || !name.StartsWith(ShapeshiftPrefix, StringComparison.Ordinal)
            || name.IndexOf("Buff", ShapeshiftPrefix.Length, StringComparison.Ordinal) < 0)
            return FormBuff.NotShapeshift;
        int end = name.IndexOf('_', ShapeshiftPrefix.Length);
        string token = end < 0 ? name.Substring(ShapeshiftPrefix.Length) : name.Substring(ShapeshiftPrefix.Length, end - ShapeshiftPrefix.Length);
        foreach (var power in NotForms)
            if (string.Equals(token, power, StringComparison.Ordinal)) return FormBuff.Power;
        kind = token switch
        {
            "Bat" => PlayerShapeshiftKind.Bat,
            "Wolf" => PlayerShapeshiftKind.Wolf,
            "Bear" => PlayerShapeshiftKind.Bear,
            "Rat" => PlayerShapeshiftKind.Rat,
            "Toad" => PlayerShapeshiftKind.Toad,
            "Human" => PlayerShapeshiftKind.Human,
            _ => PlayerShapeshiftKind.Other,
        };
        return kind == PlayerShapeshiftKind.Other ? FormBuff.Unknown : FormBuff.Known;
    }

    /// <summary>The state from collected facts. Never throws.</summary>
    internal static PlayerStateInfo Build(in PlayerStateFacts f)
    {
        if (!f.HasCharacter)
            return PlayerStateInfo.Fail(PlayerStateStatus.NoCharacter, "the user has no character", f.Online);
        if (!f.CharacterExists)
            return PlayerStateInfo.Fail(PlayerStateStatus.NoCharacter, "the user's character entity no longer exists", f.Online);

        bool downedBuff = false, pvp = false, combat = false;
        var kind = PlayerShapeshiftKind.None;
        int formGuid = 0;
        string formName = "";
        int unknownGuid = 0;
        string unknownName = "";
        if (f.Buffs != null)
        {
            for (int i = 0; i < f.Buffs.Count; i++)
            {
                var (guid, name) = f.Buffs[i];
                if (guid == 0) continue;
                if (guid == f.DownedBuff) downedBuff = true;
                if (guid == f.PvpCombatBuff) pvp = true;
                if (guid == f.CombatBuff) combat = true;
                switch (Classify(name, out var k))
                {
                    case FormBuff.Known when kind == PlayerShapeshiftKind.None:
                        kind = k;
                        formGuid = guid;
                        formName = name ?? "";
                        break;
                    case FormBuff.Unknown when unknownGuid == 0:
                        unknownGuid = guid;
                        unknownName = name ?? "";
                        break;
                }
            }
        }

        // A named form buff decides. Without one, the controller's flag (or an unnamed form buff,
        // unless the flag says no) means a form this enum does not name.
        if (kind == PlayerShapeshiftKind.None)
        {
            if (unknownGuid != 0 && f.ControllerShapeshifted != false)
            {
                kind = PlayerShapeshiftKind.Other;
                formGuid = unknownGuid;
                formName = unknownName;
            }
            else if (f.ControllerShapeshifted == true)
            {
                kind = PlayerShapeshiftKind.Other;
            }
        }

        bool dead = f.HealthIsDead || f.HasDeadComponent;
        var notes = new List<string>(2);
        if (!f.HealthRead) notes.Add("health unreadable");
        if (!f.BuffsRead) notes.Add("buffs unreadable");
        return new PlayerStateInfo
        {
            Status = PlayerStateStatus.Ok,
            Reason = string.Join("; ", notes),
            Online = f.Online,
            CharacterDisabled = f.CharacterDisabled,
            Dead = dead,
            Downed = !dead && downedBuff,
            InPvpCombat = pvp,
            InCombat = pvp || combat,
            ShapeshiftKind = kind,
            ShapeshiftBuff = formGuid,
            ShapeshiftBuffName = formName,
            Health = f.HealthRead ? f.Health : 0f,
            MaxHealth = f.HealthRead ? f.MaxHealth : 0f,
        };
    }
}
