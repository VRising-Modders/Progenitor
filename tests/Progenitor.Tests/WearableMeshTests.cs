using System.Collections.Generic;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The robe set's worn mesh, measured with no game attached. A wider pose and a narrower
/// pose stand in for the two body types: the sketch is the same, the triangles follow the bones.
/// </summary>
public class WearableMeshTests
{
    [Fact]
    public void Robe_chest_is_cloth_with_a_gold_tassel_below_the_belt()
    {
        var pose = Pose(0.22f);
        var mesh = WearableLooks.RobeChest().Build(pose);
        Assert.True(mesh.Has(WearableSurface.Cloth));
        Assert.True(mesh.Has(WearableSurface.Gold));
        Assert.False(mesh.Has(WearableSurface.Void));
        float pelvis = pose["Pelvis_JNT"].Y;
        float lowestGold = float.MaxValue;
        for (int i = 0; i < mesh.Gold.Length; i++)
            lowestGold = System.MathF.Min(lowestGold, mesh.Vertices[mesh.Gold[i]].Y);
        Assert.True(lowestGold < pelvis - 0.15f, "gold tassels should hang below the belt, lowest Y was " + lowestGold);
    }

    [Fact]
    public void Robe_chest_reaches_the_ankles_and_fits_a_narrower_body()
    {
        var wide = Pose(0.22f);
        var narrow = Pose(0.14f);
        var wideMesh = WearableLooks.RobeChest().Build(wide);
        var narrowMesh = WearableLooks.RobeChest().Build(narrow);
        float hem = wide["Left_Ankle_JNT"].Y;
        float lowest = float.MaxValue;
        for (int i = 0; i < wideMesh.Cloth.Length; i++)
            lowest = System.MathF.Min(lowest, wideMesh.Vertices[wideMesh.Cloth[i]].Y);
        Assert.InRange(lowest, hem, hem + 0.05f); // Sewn gold edging and a small ground clearance.

        float wideRadius = ChestRadius(wideMesh, wide["Chest_JNT"].Y);
        float narrowRadius = ChestRadius(narrowMesh, narrow["Chest_JNT"].Y);
        Assert.True(wideRadius > narrowRadius * 1.2f,
            "a wider shoulder span should make a wider robe (" + wideRadius + " vs " + narrowRadius + ")");
    }

    [Fact]
    public void Robe_sleeves_reach_both_wrists()
    {
        var pose = Pose(0.18f);
        var mesh = WearableLooks.RobeChest().Build(pose);
        Assert.True(Near(mesh, pose["Left_Wrist_JNT"]), "the left sleeve should reach the left wrist");
        Assert.True(Near(mesh, pose["Right_Wrist_JNT"]), "the right sleeve should reach the right wrist");
    }

    [Fact]
    public void Robe_hem_reaches_both_knees_and_ankles_for_leg_weights()
    {
        // Stiff Pelvis-only strip failed when seated; hem must touch thigh/calf joints.
        var pose = Pose(0.18f);
        var mesh = WearableLooks.RobeChest().Build(pose);
        Assert.True(ClothNearHeight(mesh, pose["Left_Knee_JNT"].Y, 0.08f), "hem should reach the left knee");
        Assert.True(ClothNearHeight(mesh, pose["Right_Knee_JNT"].Y, 0.08f), "hem should reach the right knee");
        Assert.True(ClothNearHeight(mesh, pose["Left_Ankle_JNT"].Y, 0.08f), "hem should reach the left ankle");
        Assert.True(ClothNearHeight(mesh, pose["Right_Ankle_JNT"].Y, 0.08f), "hem should reach the right ankle");
        Assert.Contains("Left_Knee_JNT", mesh.Bones);
        Assert.Contains("Right_Knee_JNT", mesh.Bones);
        Assert.Contains("Left_Ankle_JNT", mesh.Bones);
        Assert.Contains("Right_Ankle_JNT", mesh.Bones);
    }

    [Fact]
    public void Robe_chest_covers_neck_and_chest_not_only_the_waist()
    {
        // Regression: lofting from the low Spine_JNT left a bare torso (skirt + belt only).
        // Ring vertices sit at Fit radius from the bone, so check height bands, not Near().
        var pose = Pose(0.18f);
        var mesh = WearableLooks.RobeChest().Build(pose);
        Assert.True(ClothNearHeight(mesh, pose["Neck_JNT"].Y, 0.08f), "the robe should reach the neck");
        Assert.True(ClothNearHeight(mesh, pose["Chest_JNT"].Y, 0.08f), "the robe should cover the chest");
        float chestY = pose["Chest_JNT"].Y;
        float clothAtChest = 0f;
        int n = 0;
        for (int i = 0; i < mesh.Cloth.Length; i++)
        {
            var v = mesh.Vertices[mesh.Cloth[i]];
            if (System.MathF.Abs(v.Y - chestY) > 0.06f) continue;
            clothAtChest += System.MathF.Sqrt(v.X * v.X + v.Z * v.Z);
            n++;
        }
        Assert.True(n > 0, "expected cloth rings at chest height");
        Assert.True(clothAtChest / n > 0.08f, "chest cloth should wrap the torso, mean radius was " + (clothAtChest / n));
    }

    [Fact]
    public void Hood_puts_a_black_void_in_front_of_the_face()
    {
        var pose = Pose(0.18f);
        var mesh = WearableLooks.RobeHood().Build(pose);
        Assert.True(mesh.Has(WearableSurface.Cloth));
        Assert.True(mesh.Has(WearableSurface.Void));
        var head = pose["Head_JNT"];
        float voidZ = 0f;
        int n = 0;
        float nearest = float.MaxValue;
        for (int i = 0; i < mesh.Void.Length; i++)
        {
            var v = mesh.Vertices[mesh.Void[i]];
            voidZ += v.Z;
            n++;
            float dx = v.X - head.X, dy = v.Y - head.Y, dz = v.Z - head.Z;
            nearest = System.MathF.Min(nearest, System.MathF.Sqrt(dx * dx + dy * dy + dz * dz));
        }
        Assert.True(n > 0);
        Assert.True(voidZ / n > head.Z + 0.02f, "the void should sit in front of the head");
        Assert.True(nearest < 0.2f, "the void should cover the face, not float away from it");
    }

    [Fact]
    public void Hood_without_a_head_bone_reports_the_gap_and_draws_no_void()
    {
        var pose = Pose(0.18f);
        pose.Remove("Head_JNT");
        var mesh = WearableLooks.RobeHood().Build(pose);
        Assert.Contains("Head_JNT", mesh.Missing);
        Assert.False(mesh.Has(WearableSurface.Void));
    }

    [Fact]
    public void Gloves_and_shoes_cover_both_sides_and_carry_gold()
    {
        var pose = Pose(0.18f);
        var gloves = WearableLooks.RobeGloves().Build(pose);
        var shoes = WearableLooks.RobeShoes().Build(pose);
        Assert.True(gloves.Has(WearableSurface.Cloth));
        Assert.True(gloves.Has(WearableSurface.Gold));
        Assert.True(shoes.Has(WearableSurface.Cloth));
        Assert.True(shoes.Has(WearableSurface.Gold));
        Assert.True(Near(gloves, pose["Left_Wrist_JNT"]));
        Assert.True(Near(gloves, pose["Right_Wrist_JNT"]));
        Assert.True(Near(shoes, pose["Left_Ankle_JNT"]));
        Assert.True(Near(shoes, pose["Right_Ankle_JNT"]));
    }

    [Fact]
    public void Gloves_accept_the_hand_bone_alias()
    {
        var pose = Pose(0.18f);
        pose["Left_Hand_JNT"] = pose["Left_Wrist_JNT"];
        pose["Right_Hand_JNT"] = pose["Right_Wrist_JNT"];
        pose.Remove("Left_Wrist_JNT");
        pose.Remove("Right_Wrist_JNT");
        var mesh = WearableLooks.RobeGloves().Build(pose);
        Assert.True(mesh.Has(WearableSurface.Cloth));
        Assert.Empty(mesh.Missing);
    }

    [Fact]
    public void Staff_rises_off_the_hand_and_keeps_gold_at_the_top()
    {
        var pose = Pose(0.18f);
        var mesh = WearableLooks.Staff().Build(pose);
        Assert.True(mesh.Has(WearableSurface.Cloth));
        Assert.True(mesh.Has(WearableSurface.Gold));
        float wrist = pose["Right_Wrist_JNT"].Y;
        float topCloth = float.MinValue;
        float topGold = float.MinValue;
        for (int i = 0; i < mesh.Cloth.Length; i++)
            topCloth = System.MathF.Max(topCloth, mesh.Vertices[mesh.Cloth[i]].Y);
        for (int i = 0; i < mesh.Gold.Length; i++)
            topGold = System.MathF.Max(topGold, mesh.Vertices[mesh.Gold[i]].Y);
        Assert.True(topCloth > wrist + 0.9f, "the staff should stand up from the hand");
        Assert.True(topGold > wrist + 0.9f, "the gold tassels should sit at the top of the staff");
    }

    [Fact]
    public void Donor_hide_skips_the_body_even_when_a_prefix_asks_for_it()
    {
        var chest = WearableLooks.RobeChest();
        Assert.Contains("CHest_GEO", chest.HideNames);
        Assert.True(WearableHide.ShouldHide("CHest_GEO", chest.HideNames));
        Assert.True(WearableHide.ShouldHide("chest_geo_extra", chest.HideNames));
        Assert.True(WearableHide.ShouldHide("MECHest_GEO", chest.HideNames), "live meshes prefix the GEO name");
        Assert.False(WearableHide.ShouldHide("Body_GEO", chest.HideNames));
        Assert.False(WearableHide.ShouldHide("Body_GEO", new[] { "Body" }));
        Assert.False(WearableHide.ShouldHide("MEBody_GEO", new[] { "Body", "CHest_GEO" }));
        Assert.False(WearableHide.ShouldHide("Hair_GEO", WearableLooks.RobeHood().HideNames));
        Assert.True(WearableHide.ShouldHide("MERazerHood_GEO", WearableLooks.RobeHood().HideNames));
    }

    [Fact]
    public void Hood_explicitly_hides_hair_without_allowing_body_or_face_removal()
    {
        var hood = WearableLooks.RobeHood();
        Assert.Contains("Hair", hood.HideNames);
        Assert.True(WearableHide.ShouldHide("MEHair_Long_GEO", hood.HideNames, hood.Slot));
        Assert.True(WearableHide.ShouldHide("hair_geo", new[] { "hair" }, WearableSlot.Headgear));
        Assert.False(WearableHide.ShouldHide("Hair_GEO", new[] { "Hair" }, WearableSlot.Chest));
        Assert.False(WearableHide.ShouldHide("Hair_GEO", new[] { "Hair" }, WearableSlot.Gloves));
        Assert.False(WearableHide.ShouldHide("Hair_GEO", new[] { "Hair_GEO" }, WearableSlot.Headgear));
        Assert.False(WearableHide.ShouldHide("MEHair_GEO", new[] { "ME" }, WearableSlot.Headgear));
        foreach (var part in new[] { "Body", "Face", "Eye", "Ear", "Skin", "Teeth", "Brow", "Eyelash" })
            Assert.False(WearableHide.ShouldHide("ME" + part + "Hair_GEO", new[] { "Hair", part }, WearableSlot.Headgear));
    }

    [Fact]
    public void Model_names_fill_both_body_types_and_a_custom_sketch_is_not_the_robe()
    {
        var chest = WearableLooks.RobeChest();
        Assert.Equal(WearableSlot.Chest, chest.Slot);
        Assert.Equal("VampireMale_ArmorSet04_Dracula_Chest", chest.MaleModel);
        Assert.Equal("VampireFemale_ArmorSet04_Dracula_Chest", chest.FemaleModel);
        Assert.NotEqual(chest.MaleModel, chest.FemaleModel);

        var hood = WearableLooks.RobeHood();
        Assert.Equal(hood.MaleModel, hood.FemaleModelOrMale);

        var custom = WearableGraphics.Define(WearableSlot.Weapon, s => s
            .Models("OnlyMale")
            .Disc("Head_JNT", 0.1f, 0.05f, WearableSurface.Void));
        Assert.Equal("OnlyMale", custom.FemaleModelOrMale);
        Assert.Null(custom.FemaleModel);
        var mesh = custom.Build(Pose(0.18f));
        Assert.True(mesh.Has(WearableSurface.Void));
        Assert.False(mesh.Has(WearableSurface.Cloth));
        Assert.False(mesh.Has(WearableSurface.Gold));
    }

    private static bool ClothNearHeight(WearableMesh mesh, float y, float tol)
    {
        for (int i = 0; i < mesh.Cloth.Length; i++)
        {
            if (System.MathF.Abs(mesh.Vertices[mesh.Cloth[i]].Y - y) <= tol) return true;
        }
        return false;
    }

    private static bool Near(WearableMesh mesh, WearableVec3 point)
    {
        for (int i = 0; i < mesh.Vertices.Length; i++)
        {
            var v = mesh.Vertices[i];
            float dx = v.X - point.X, dy = v.Y - point.Y, dz = v.Z - point.Z;
            if (dx * dx + dy * dy + dz * dz < 0.08f * 0.08f) return true;
        }
        return false;
    }

    private static float ChestRadius(WearableMesh mesh, float spineY)
    {
        float best = 0f;
        for (int i = 0; i < mesh.Cloth.Length; i++)
        {
            var v = mesh.Vertices[mesh.Cloth[i]];
            if (System.MathF.Abs(v.Y - spineY) > 0.03f) continue;
            best = System.MathF.Max(best, System.MathF.Sqrt(v.X * v.X + v.Z * v.Z));
        }
        return best;
    }

    internal static Dictionary<string, WearableVec3> Pose(float halfShoulder)
    {
        var p = new Dictionary<string, WearableVec3>(System.StringComparer.Ordinal);
        void Add(string name, float x, float y, float z) => p[name] = new WearableVec3(x, y, z);
        // Heights mirror the live vampire rig: Spine low, Chest/Neck high, Head above.
        Add("Pelvis_JNT", 0f, 1.00f, 0f);
        Add("Spine_JNT", 0f, 1.12f, 0f);
        Add("Chest_JNT", 0f, 1.42f, 0f);
        Add("Neck_JNT", 0f, 1.55f, 0f);
        Add("Head_JNT", 0f, 1.68f, 0.02f);
        Add("Left_Shoulder_JNT", -halfShoulder, 1.48f, 0f);
        Add("Right_Shoulder_JNT", halfShoulder, 1.48f, 0f);
        Add("Left_Elbow_JNT", -halfShoulder - 0.18f, 1.22f, 0.02f);
        Add("Right_Elbow_JNT", halfShoulder + 0.18f, 1.22f, 0.02f);
        Add("Left_Wrist_JNT", -halfShoulder - 0.22f, 1.02f, 0.06f);
        Add("Right_Wrist_JNT", halfShoulder + 0.22f, 1.02f, 0.08f);
        Add("Left_Hip_JNT", -0.10f, 0.95f, 0.02f);
        Add("Right_Hip_JNT", 0.10f, 0.95f, 0.02f);
        Add("Left_Knee_JNT", -0.10f, 0.52f, 0.02f);
        Add("Right_Knee_JNT", 0.10f, 0.52f, 0.02f);
        Add("Left_Ankle_JNT", -0.10f, 0.08f, 0.02f);
        Add("Right_Ankle_JNT", 0.10f, 0.08f, 0.02f);
        Add("Left_Ball_JNT", -0.10f, 0.02f, 0.18f);
        Add("Right_Ball_JNT", 0.10f, 0.02f, 0.18f);
        foreach (string side in new[] { "Left_", "Right_" })
        {
            var wrist = p[side + "Wrist_JNT"];
            string[] fingers = { "Index", "Middle", "Ring", "Pinky", "Thumb" };
            for (int finger = 0; finger < fingers.Length; finger++)
                for (int joint = 1; joint <= 3; joint++)
                {
                    string name = finger == 4 && joint == 2 ? "Thum02" : fingers[finger] + "0" + joint;
                    float lateral = finger == 4 ? -0.065f : (finger - 1.5f) * 0.025f;
                    float reach = finger == 4 ? 0.035f + joint * 0.025f : 0.055f + joint * 0.028f;
                    Add(side + name + "_JNT", wrist.X + lateral, wrist.Y - reach, wrist.Z + (finger == 4 ? 0.025f : 0f));
                }
        }
        return p;
    }
}
