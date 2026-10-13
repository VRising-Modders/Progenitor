using System;
using System.Collections.Generic;

namespace Progenitor.NecroArmyExample;

/// <summary>Which rank a summoned unit stands in.</summary>
public enum ArmyRank { DeathKnight, Melee, Crossbow, Mage }

/// <summary>One pre-mapped spot: rank, offset from the necromancer (forward, right, in meters) and rise order.</summary>
public readonly record struct FormationSlot(ArmyRank Rank, float Forward, float Right, int Order);

/// <summary>
/// The army's layout, relative to the necromancer facing the enemy. Pure arithmetic so it can be
/// unit-tested and tuned without the game: death knights flank her, two melee ranks form a crescent
/// in front, crossbows and mages stand in two ranks behind. Rise order runs back to front, so the
/// last units out of the ground are the melee line nearest the enemy.
/// </summary>
public static class Formation
{
    public const int Melee = 20, Crossbow = 10, Mage = 10, DeathKnights = 2;

    public static IReadOnlyList<FormationSlot> Build(float spacing = 1.2f)
    {
        var slots = new List<FormationSlot>();
        int order = 0;
        // Back to front: mages, crossbows, melee rank 2, melee rank 1, then the death knights last.
        AddRow(slots, ArmyRank.Mage, Mage, -4.2f, spacing, 0f, ref order);
        AddRow(slots, ArmyRank.Crossbow, Crossbow, -2.8f, spacing, 0f, ref order);
        AddRow(slots, ArmyRank.Melee, Melee / 2, 1.6f, spacing, 0.12f, ref order);
        AddRow(slots, ArmyRank.Melee, Melee / 2, 3.0f, spacing, 0.12f, ref order);
        slots.Add(new FormationSlot(ArmyRank.DeathKnight, 0.6f, -2.2f, order++));
        slots.Add(new FormationSlot(ArmyRank.DeathKnight, 0.6f, 2.2f, order++));
        return slots;
    }

    /// <summary>A centered row; <paramref name="curve"/> pulls the outer units back into a crescent.</summary>
    private static void AddRow(List<FormationSlot> slots, ArmyRank rank, int count, float forward, float spacing, float curve, ref int order)
    {
        float half = (count - 1) / 2f;
        for (int i = 0; i < count; i++)
        {
            float right = (i - half) * spacing;
            float f = forward - curve * right * right / spacing;
            slots.Add(new FormationSlot(rank, f, right, order++));
        }
    }

    /// <summary>World XZ for a slot, given the necromancer's position and a unit facing vector toward the enemy.</summary>
    public static (float X, float Z) ToWorld(FormationSlot s, float originX, float originZ, float faceX, float faceZ)
    {
        float len = MathF.Sqrt(faceX * faceX + faceZ * faceZ);
        if (len < 1e-4f) { faceX = 1f; faceZ = 0f; } else { faceX /= len; faceZ /= len; }
        // Right-hand perpendicular on the XZ plane (Y up): right = (faceZ, -faceX).
        return (originX + faceX * s.Forward + faceZ * s.Right, originZ + faceZ * s.Forward - faceX * s.Right);
    }
}
