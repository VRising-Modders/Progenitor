using System;
using System.Collections.Generic;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public class WearableBodyPaletteTests
{
    private static readonly string[] Controls = {
        "Left_Shoulder_JNT", "Right_Shoulder_JNT", "Left_Elbow_JNT", "Right_Elbow_JNT",
        "Left_Hip_JNT", "Right_Hip_JNT", "Left_Knee_JNT", "Right_Knee_JNT"
    };

    [Fact]
    public void Body_palette_without_control_joints_builds_every_robe_piece_using_real_bone_names()
    {
        // Original synthetic pivots with the sparse control/roll layout of a body palette.
        // No animated helper transforms or inferred control bind matrices are inserted.
        var pose = WearableMeshTests.Pose(.18f);
        foreach (string control in Controls)
        {
            pose.Add(Roll(control), pose[control]);
            pose.Remove(control);
        }
        var recipes = new[] { WearableLooks.RobeChest(), WearableLooks.RobeGloves(),
            WearableLooks.RobeShoes(), WearableLooks.RobeHood() };
        foreach (var recipe in recipes)
        {
            var mesh = recipe.Build(pose);
            Assert.NotEmpty(mesh.Vertices);
            Assert.Empty(mesh.Missing);
            Assert.All(mesh.Bones, bone => Assert.True(pose.ContainsKey(bone), bone));
            foreach (string control in Controls) Assert.DoesNotContain(control, mesh.Bones);
        }
    }

    [Theory]
    [InlineData("Left_Shoulder_JNT")]
    [InlineData("Right_Shoulder_JNT")]
    [InlineData("Left_Elbow_JNT")]
    [InlineData("Right_Elbow_JNT")]
    [InlineData("Left_Hip_JNT")]
    [InlineData("Right_Hip_JNT")]
    [InlineData("Left_Knee_JNT")]
    [InlineData("Right_Knee_JNT")]
    public void Fallback_retains_roll_identity_and_an_exact_control_takes_precedence(string control)
    {
        var pose = new Dictionary<string, WearableVec3>(StringComparer.Ordinal)
        { [Roll(control)] = new WearableVec3(0, 1, 0) };
        var recipe = WearableGraphics.Define(WearableSlot.Chest,
            s => s.Disc(control, .05f, 0, WearableSurface.Cloth));
        var fallback = recipe.Build(pose);
        Assert.Empty(fallback.Missing);
        Assert.Equal(new[] { Roll(control) }, fallback.Bones);
        pose[control] = new WearableVec3(0, 2, 0);
        var exact = recipe.Build(pose);
        Assert.Empty(exact.Missing);
        Assert.Equal(new[] { control }, exact.Bones);
        Assert.Equal(1f, exact.Vertices[0].Y - fallback.Vertices[0].Y, 5);
    }

    private static string Roll(string control) => control.Replace("_JNT", "_roll01_JNT");
}
