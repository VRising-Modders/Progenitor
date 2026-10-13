using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Shared;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.Ui;

/// <summary>Effective component values selected by the native character sheet getter.</summary>
// Source-checked against the client's native AttributeValueGetter.Create/GetValue
// (2026-10-11).
// Values stay in native units; presentation and cap metadata are separate.
internal static class CharacterStatReader
{
    internal static Dictionary<int, float> Read(EntityManager em, Entity character)
    {
        var values = new Dictionary<int, float>();
        if (!character.Exists(em)) return values;
        if (TryRead(em, character, out UnitStats unit))
        {
            Add(0, unit.PhysicalPower.Value); Add(1, unit.ResourcePower.Value); Add(2, unit.SiegePower.Value);
            Add(8, unit.PhysicalResistance.Value); Add(9, unit.FireResistance.Value);
            Add(23, unit.SpellResistance.Value); Add(25, unit.SpellPower.Value);
            Add(26, unit.PassiveHealthRegen.Value); Add(69, unit.CCReduction.Value);
            Add(74, unit.DamageReduction.Value); Add(75, unit.HealingReceived.Value);
            Add(95, unit.CorruptionDamageReduction.Value);
        }
        if (TryRead(em, character, out Health health)) Add(4, health.MaxHealth.Value);
        if (TryRead(em, character, out LifeLeech leech))
        {
            Add(27, leech.PhysicalLifeLeechFactor.Value); Add(28, leech.SpellLifeLeechFactor.Value);
            Add(61, leech.PrimaryLeechFactor.Value);
        }
        if (TryRead(em, character, out VampireSpecificAttributes vampire))
        {
            Add(3, vampire.ResourceYieldModifier.Value); Add(10, vampire.HolyResistance.Value);
            Add(11, vampire.SilverResistance.Value); Add(19, vampire.SunResistance.Value);
            Add(20, vampire.GarlicResistance.Value); Add(29, vampire.PhysicalCriticalStrikeChance.Value);
            Add(30, vampire.PhysicalCriticalStrikeDamage.Value); Add(31, vampire.SpellCriticalStrikeChance.Value);
            Add(32, vampire.SpellCriticalStrikeDamage.Value); Add(58, vampire.ReducedResourceDurabilityLoss.Value);
            Add(65, vampire.PvPResilience.Value); Add(67, vampire.BonusPhysicalPower.Value);
            Add(68, vampire.BonusSpellPower.Value); Add(76, vampire.IncreasedShieldEfficiency.Value);
            Add(79, vampire.SilverCoinResistance.Value); Add(82, vampire.BonusMaxHealth.Value);
            Add(83, vampire.BonusMovementSpeed.Value); Add(84, vampire.BonusShapeshiftMovementSpeed.Value);
            Add(86, vampire.UltimateEfficiency.Value); Add(90, vampire.WeaponSkillPower.Value);
            Add(92, vampire.BloodMendHealEfficiency.Value);
        }
        if (TryRead(em, character, out AbilityBar_Shared ability))
        {
            Add(7, ability.CooldownRecoveryRate.Value); Add(33, ability.AbilityAttackSpeed.Value);
            Add(59, ability.PrimaryAttackSpeed.Value); Add(70, ability.SpellCooldownRecoveryRate.Value);
            Add(71, ability.WeaponCooldownRecoveryRate.Value); Add(72, ability.UltimateCooldownRecoveryRate.Value);
            Add(80, ability.TravelCooldownRecoveryRate.Value); Add(91, ability.FeedCooldownRecoveryRate.Value);
        }
        if (TryRead(em, character, out DamageCategoryStats damage))
        {
            Add(38, damage.DamageVsUndeads.Value); Add(39, damage.DamageVsHumans.Value);
            Add(40, damage.DamageVsDemons.Value); Add(41, damage.DamageVsMechanical.Value);
            Add(42, damage.DamageVsBeasts.Value); Add(43, damage.DamageVsCastleObjects.Value);
            Add(44, damage.DamageVsVampires.Value); Add(52, damage.DamageVsWood.Value);
            Add(53, damage.DamageVsMineral.Value); Add(54, damage.DamageVsVegetation.Value);
            Add(55, damage.DamageVsLightArmor.Value); Add(56, damage.DamageVsVBloods.Value);
            Add(57, damage.DamageVsMagic.Value);
        }
        if (TryRead(em, character, out TakeDamageInSun sun)) Add(12, sun.SunChargeUpTime.Value);
        if (TryRead(em, character, out MinionMasterStats minion)) Add(73, minion.MinionDamageModifier.Value);
        if (TryRead(em, character, out Blood blood))
        {
            Add(66, blood.LossPerSecond.Value); Add(77, blood.BloodEfficiency.Value);
            Add(81, blood.ReducedBloodDrain.Value); Add(94, blood.BloodDrainMultiplier.Value);
        }
        if (TryRead(em, character, out Mounter mount))
        {
            Add(85, mount.GallopMaxSpeedMultiplier.Value);
            // Mount overrides the movement/shapeshift choice in native GetValue(5).
            if (mount.MountEntity != Entity.Null)
                Add(5, mount.IsGalopping
                    ? (1f + mount.GallopMaxSpeedMultiplier.Value) * mount.GallopMaxSpeed.Value
                    : mount.WalkMaxSpeed);
            else if (TryRead(em, character, out ControlledBy controlled)
                && TryRead(em, controlled.Controller, out Shapeshift shape))
            {
                // Create copies ControlledBy at getter offset 0x4B8 and reads
                // Shapeshift from its Controller, not from the character body.
                if (shape.IsShapeshifted.Value) Add(5, shape.Speed.Value);
                else if (TryRead(em, character, out Movement movement)) Add(5, movement.Speed.Value);
            }
        }
        return values;

        void Add(int id, float value)
        {
            if (float.IsFinite(value)) values[id] = value;
        }
    }

    private static bool TryRead<T>(EntityManager em, Entity character, out T value) where T : unmanaged
    {
        try { return character.TryRead(em, out value); }
        // Missing components or rejected layouts must not become invented zero values.
        catch (Exception) { value = default; return false; }
    }
}
