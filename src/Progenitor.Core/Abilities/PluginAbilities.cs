using System;
using Progenitor.Plugins;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Abilities;

/// <summary>
/// <see cref="PluginContext.Abilities"/>: server ability control for one plugin. Every member
/// throws <see cref="InvalidOperationException"/> on the client (a programming error, like
/// <c>ctx.Items</c>); ordinary refusals on the server return false instead. Main thread (a
/// command, <c>Plugin.Frame</c> or an event handler).
/// </summary>
public sealed class PluginAbilities
{
    private readonly PluginContext _ctx;

    internal PluginAbilities(PluginContext ctx) =>
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    /// <summary>
    /// Start <paramref name="abilityGroupGuid"/> on <paramref name="character"/> through
    /// <c>ServerGameManager.ForceCastAbilityGroup</c>. The group does not have to be on the
    /// character's bar (live 2026-09-25), unlike <see cref="AbilityCast.Cast"/>, which writes
    /// <c>AbilityBar_Shared.ForceCastGroup</c> and whose off-bar requests are discarded.
    ///
    /// <para>Returns false when the world or <c>ServerScriptMapper</c> is not ready, the character
    /// is gone or has no ability bar, or the group is 0. True means the game accepted the call,
    /// not that a cast started: compare <see cref="AbilityCast.CastStartedCounter"/> a frame later.
    /// Aimed abilities fire at the character's <c>EntityInput.AimPosition</c>, which for a player is
    /// their mouse. Cooldowns apply; see <see cref="NoCooldowns"/>.</para>
    /// </summary>
    public bool ForceCast(Entity character, int abilityGroupGuid) =>
        ForceCast(character, abilityGroupGuid, out _);

    /// <summary><see cref="ForceCast(Entity, int)"/> with the refusal reason.</summary>
    public bool ForceCast(Entity character, int abilityGroupGuid, out string why)
    {
        RequireServer("ForceCast");
        bool ok = AbilityCast.ForceCastGroup(_ctx.World, character, new PrefabGUID(abilityGroupGuid), out why);
        if (!ok) _ctx.Log.LogDebug("Abilities.ForceCast: " + why);
        return ok;
    }

    /// <summary>
    /// Keep every ability <paramref name="character"/> owns off cooldown with full charges until the
    /// returned handle is disposed, this plugin unloads (the handle is tracked), the character is
    /// destroyed, or the server world is replaced. About every 0.1 s it zeroes
    /// <c>AbilityCooldownState.CooldownEndTime</c> and <c>CurrentCooldown</c> on each ability entity
    /// whose <c>EntityOwner</c> is the character, and raises <c>AbilityChargesState.CurrentCharges</c>
    /// to <c>AbilityChargesData.MaxCharges</c>. Cast times and animations are untouched. Release
    /// restores nothing: cooldowns resume from the next cast.
    ///
    /// <para>Holds nest: two holds on one character (this plugin or another) keep it until both
    /// are released. When the world is not ready or the character does not exist, returns an inert
    /// handle and logs why; <see cref="IsNoCooldowns"/> then reports false.</para>
    /// </summary>
    public IDisposable NoCooldowns(Entity character)
    {
        RequireServer("NoCooldowns");
        var hold = NoCooldownsRuntime.Take(_ctx.World, character, _ctx.Owner, out string why);
        if (hold == null)
        {
            _ctx.Log.LogWarning("Abilities.NoCooldowns: " + why);
            return Inert.Instance;
        }
        _ctx.Track(hold);
        return hold;
    }

    /// <summary>True while this plugin holds <see cref="NoCooldowns"/> on <paramref name="character"/>.</summary>
    public bool IsNoCooldowns(Entity character)
    {
        RequireServer("IsNoCooldowns");
        return NoCooldownsRuntime.IsHeldBy(character, _ctx.Owner);
    }

    /// <summary>
    /// Put <paramref name="abilityGroupGuid"/> on bar <paramref name="slot"/> of
    /// <paramref name="character"/> until the returned handle is disposed or this plugin unloads
    /// (the handle is tracked); then the slot goes back to what the character had. The game shows
    /// it on the ability bar and the player can cast it like any slotted spell.
    ///
    /// <para>Slots are 0-8 and their meaning is per character, so read the bar before choosing
    /// (<see cref="AbilityCast.TryGetSlotGroup"/>). On a player character, measured live 2026-09-25:
    /// 0 primary attack, 1 and 4 the weapon's Q and E, 2 Space (travel), 5 R, 6 C, 7 T (ultimate).
    /// Nothing checks that the group suits the slot: an ordinary spell on the ultimate slot works.</para>
    ///
    /// <para>How: a Progenitor-owned clone of <c>Buff_VBlood_Ability_Replace</c> (the buff the
    /// game uses when a player slots a V Blood power) carrying one <c>ReplaceAbilityOnSlotBuff</c>
    /// element, applied to the character; the game's <c>ReplaceAbilityOnSlotSystem</c> does the
    /// swap. The element has priority <see cref="AbilitySlotPolicy.Priority"/>, above the game's
    /// own replace buffs. Two of this plugin's handles for the same slot and group share one buff;
    /// each plugin has its own clone.</para>
    ///
    /// <para>A slot outside 0-8 or group 0 throws <see cref="ArgumentOutOfRangeException"/> /
    /// <see cref="ArgumentException"/>. When the world is not ready, the character is gone or has
    /// no ability slots, returns an inert handle and logs why.</para>
    /// </summary>
    public IDisposable SetSlot(Entity character, int slot, int abilityGroupGuid)
    {
        RequireServer("SetSlot");
        if (!AbilitySlotPolicy.IsBarSlot(slot))
            throw new ArgumentOutOfRangeException(nameof(slot), AbilitySlotPolicy.NotBarSlotRefusal(slot));
        if (!AbilityCastPolicy.IsCastableGroup(abilityGroupGuid))
            throw new ArgumentException(AbilitySlotPolicy.EmptyGroupRefusal(), nameof(abilityGroupGuid));

        var world = _ctx.World;
        if (world == null || !world.IsCreated)
        {
            _ctx.Log.LogWarning("Abilities.SetSlot: " + AbilityCooldownPolicy.NotReadyRefusal());
            return Inert.Instance;
        }

        string id = AbilitySlotPolicy.CloneId(_ctx.Guid, slot, abilityGroupGuid);
        var clone = new PrefabGUID(PrefabGUID.CalculateGuidHash(id));
        if (!_slotBuffs.ContainsKey(id))
            _slotBuffs[id] = _ctx.Content.DefineBuff(id).CloneOf(new PrefabGUID(AbilitySlotPolicy.ReplaceBuffSource)).Register();

        if (!AbilitySlots.EnsureElement(world, clone, slot, abilityGroupGuid, out string why)
            || !AbilitySlots.Apply(world, character, clone, out why))
        {
            _ctx.Log.LogWarning("Abilities.SetSlot: " + why);
            return Inert.Instance;
        }
        var handle = new SlotHandle(_ctx, character, clone);
        _ctx.Track(handle);
        return handle;
    }

    // One registered clone per slot/group this plugin has used; released with the plugin.
    private readonly System.Collections.Generic.Dictionary<string, IDisposable> _slotBuffs = new();

    private sealed class SlotHandle : IDisposable
    {
        private readonly PluginContext _ctx;
        private readonly Entity _character;
        private readonly PrefabGUID _clone;
        private bool _disposed;

        internal SlotHandle(PluginContext ctx, Entity character, PrefabGUID clone)
        {
            _ctx = ctx;
            _character = character;
            _clone = clone;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            AbilitySlots.Release(_ctx.World, _character, _clone);
        }
    }

    private void RequireServer(string op)
    {
        _ctx.EnsureAttached();
        if (!_ctx.IsServer)
            throw new InvalidOperationException("ctx.Abilities." + op + " is server-only.");
    }

    private sealed class Inert : IDisposable
    {
        internal static readonly Inert Instance = new();
        public void Dispose() { }
    }
}
