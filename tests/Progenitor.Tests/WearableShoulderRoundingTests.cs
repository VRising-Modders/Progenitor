using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Content;
using Xunit;
namespace Progenitor.Tests;
public class WearableShoulderRoundingTests
{
    [Fact]
    public void Complete_body_reference_removes_raised_circular_shoulder_floor_but_keeps_clearance()
    {
        var pose=new Dictionary<string,WearableVec3> {
            ["Left_Shoulder_JNT"]=new(.2f,1.4f,0),["Right_Shoulder_JNT"]=new(-.2f,1.4f,0),
            ["Left_Elbow_JNT"]=new(.5f,1.4f,0),["Left_Wrist_JNT"]=new(.8f,1.4f,0)};
        var graphics=WearableGraphics.Define(WearableSlot.Chest,s=>s.Sleeve("Left_Shoulder_JNT","Left_Elbow_JNT","Left_Wrist_JNT",WearableSurface.Cloth));
        var body=new List<WearableSkinSample>();
        foreach(float x in new[]{.14f,.16f,.18f,.2f,.22f,.24f,.26f,.28f,.3f,.32f})
            for(int i=0;i<24;i++)
            {
                float angle=i*MathF.PI/12;
                body.Add(new WearableSkinSample(new(x,1.4f+.04f*MathF.Cos(angle),.04f*MathF.Sin(angle)),
                    new WearableSkinInfluence("Left_Shoulder_roll02_JNT",1f)));
            }
        var fallback=graphics.Build(pose);var fitted=graphics.Build(pose,bodySamples:body);
        var proximal=fitted.Vertices.Where(v=>v.X<.25f).ToArray();
        Assert.True(proximal.Max(v=>v.Y)<fallback.Vertices.Where(v=>v.X<.25f).Max(v=>v.Y)-.04f);
        Assert.True(proximal.Max(v=>v.Y)>=1.451f); // 40 mm body radius + 12 mm cloth allowance.
        Assert.True(proximal.Min(v=>v.Y)<=1.349f);
        Assert.True(proximal.Min(v=>v.X)>=.15499f); // No lifted/overlong cap toward the collar.
        Assert.Equal(fallback.Vertices.Where(v=>v.X>.51f).ToArray(),fitted.Vertices.Where(v=>v.X>.51f).ToArray());
    }

    [Fact]
    public void Root_cap_meets_measured_circumference_without_a_right_angle_or_shrinking_it()
    {
        var pose=new Dictionary<string,WearableVec3> {
            ["Left_Shoulder_JNT"]=new(.2f,1.4f,0),["Right_Shoulder_JNT"]=new(-.2f,1.4f,0),
            ["Left_Elbow_JNT"]=new(.5f,1.4f,0),["Left_Wrist_JNT"]=new(.8f,1.4f,0)};
        var mesh=WearableGraphics.Define(WearableSlot.Chest,s=>s.Sleeve("Left_Shoulder_JNT","Left_Elbow_JNT","Left_Wrist_JNT",WearableSurface.Cloth)).Build(pose);
        var root=new WearableVec3(.2f-.08f*.95f,1.4f+.08f*.2f,0);
        var rim=Enumerable.Range(0,mesh.Vertices.Length).Where(i=>Math.Abs(mesh.Vertices[i].X-root.X)<1e-5f).ToHashSet();
        Assert.Equal(24,rim.Count);
        foreach(int i in rim) Assert.InRange((mesh.Vertices[i]-root).Length(),.08959f,.08961f);
        var normals=new Dictionary<(int,int),List<WearableVec3>>();
        for(int t=0;t<mesh.Cloth.Length;t+=3)
        {
            int a=mesh.Cloth[t],b=mesh.Cloth[t+1],c=mesh.Cloth[t+2];
            var u=mesh.Vertices[b]-mesh.Vertices[a];var v=mesh.Vertices[c]-mesh.Vertices[a];
            var normal=new WearableVec3(u.Y*v.Z-u.Z*v.Y,u.Z*v.X-u.X*v.Z,u.X*v.Y-u.Y*v.X).Normalized();
            foreach(var edge in new[]{(a,b),(b,c),(c,a)})
            {
                if(!rim.Contains(edge.Item1)||!rim.Contains(edge.Item2)) continue;
                var key=(Math.Min(edge.Item1,edge.Item2),Math.Max(edge.Item1,edge.Item2));
                if(!normals.TryGetValue(key,out var values)) normals[key]=values=new();
                values.Add(normal);
            }
        }
        Assert.Equal(24,normals.Count);
        foreach(var pair in normals.Values)
        {
            Assert.Equal(2,pair.Count);
            Assert.True(pair[0].X*pair[1].X+pair[0].Y*pair[1].Y+pair[0].Z*pair[1].Z>.80f,
                "The root rim should meet a rounded cap, not the former 90-degree flat fan.");
        }
        // Rounding adds depth toward the torso, never pulls the cuff or outer sleeve inward.
        Assert.InRange(mesh.Vertices.Min(v=>v.X),root.X-.02801f,root.X-.02799f);
    }
}
