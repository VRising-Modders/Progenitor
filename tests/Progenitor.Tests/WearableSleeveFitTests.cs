using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public class WearableSleeveFitTests
{
    [Fact]
    public void Torso_dominant_underarm_samples_expand_only_the_local_sleeve()
    {
        var pose = new Dictionary<string, WearableVec3>
        {
            ["Left_Shoulder_JNT"] = new(.2f, 1.4f, 0f),
            ["Right_Shoulder_JNT"] = new(-.2f, 1.4f, 0f),
            ["Left_Elbow_JNT"] = new(.5f, 1.4f, 0f),
            ["Left_Wrist_JNT"] = new(.8f, 1.4f, 0f)
        };
        var graphics = WearableGraphics.Define(WearableSlot.Chest,
            s => s.Sleeve("Left_Shoulder_JNT", "Left_Elbow_JNT", "Left_Wrist_JNT", WearableSurface.Cloth));
        var samples = new List<WearableSkinSample>();
        foreach (float x in new[] { .285f, .3f, .315f })
            foreach (float z in new[] { -.02f, .02f })
                samples.Add(new WearableSkinSample(new(x, 1.26f, z),
                    new("Ribcage_JNT", .85f), new("Left_Shoulder_roll02_JNT", .15f)));
        var original = graphics.Build(pose);
        var fitted = graphics.Build(pose, bodySamples: samples);
        Assert.True(fitted.Vertices.Where(p => MathF.Abs(p.X - .3f) < .03f).Min(p => p.Y) < 1.249f);
        Assert.True(original.Vertices.Where(p => MathF.Abs(p.X - .3f) < .03f).Min(p => p.Y) > 1.30f);
        // Forearm/wrist geometry must not expand to solve an upper-arm problem.
        Assert.Equal(original.Vertices.Where(p => p.X > .51f).ToArray(), fitted.Vertices.Where(p => p.X > .51f).ToArray());
        var opposite = samples.Select(p => new WearableSkinSample(p.Position,
            new("Ribcage_JNT", .85f), new("Right_Shoulder_roll02_JNT", .15f))).ToArray();
        Assert.Equal(original.Vertices, graphics.Build(pose, bodySamples: opposite).Vertices);
    }
}
