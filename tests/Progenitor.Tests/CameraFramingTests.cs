using System;
using Progenitor.Cameras;
using Progenitor.Units;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// CAMOFFSET1: the camera preview's framing offset (<see cref="CameraFraming"/>), game-free. A pinhole camera that
/// reproduces Unity's projection (<c>fieldOfView</c> vertical, <c>aspect</c> width over height, an optional lens shift
/// and viewport rect) stands in for the game's orbit camera; the runtime's procedure (measure the look-at point and one
/// meter right of it with the camera's own projection, shift the anchor along the camera's own right axis) must land
/// the previewed point at <c>0.5 + offset</c> of the screen at 16:9, 21:9 and 32:9, at every yaw and pitch, and under a
/// different yaw convention, where the first version's yaw-derived model fails.
/// </summary>
public sealed class CameraFramingTests
{
    private const float Distance = 6f;

    /// <summary>A Unity-style perspective camera: pose plus the projection's m00 and m02 and the viewport rect on screen.</summary>
    private sealed class PinholeCamera
    {
        public (double X, double Y, double Z) Position, Right, Up, Forward;
        public double M00, M02, RectX, RectWidth = 1;

        /// <summary>Unity's <c>WorldToViewportPoint</c>: view space (x right, y up, z backward), clip, divide, 0..1.</summary>
        public (double X, double Depth) Viewport(UnitVec3 p)
        {
            var rel = (X: p.X - Position.X, Y: p.Y - Position.Y, Z: p.Z - Position.Z);
            double cx = rel.X * Right.X + rel.Y * Right.Y + rel.Z * Right.Z;
            double depth = rel.X * Forward.X + rel.Y * Forward.Y + rel.Z * Forward.Z;
            double cz = -depth;
            double ndc = (M00 * cx + M02 * cz) / -cz;
            return ((ndc + 1) * 0.5, depth);
        }

        public double ScreenX(UnitVec3 p) => RectX + Viewport(p).X * RectWidth;
    }

    /// <summary>How a rig turns the written yaw into the camera's rotation.</summary>
    private enum Convention { Game, MirroredYaw, LooksBack }

    /// <summary>
    /// The orbit rig: the camera <see cref="Distance"/> behind its view direction from the anchor, rotated
    /// <c>quaternion.Euler(pitch, yaw, 0)</c> (Unity: yaw about Y after pitch about X) under <paramref name="convention"/>.
    /// </summary>
    private static PinholeCamera Rig(UnitVec3 anchor, float yaw, float pitch, float vfov, float aspect, Convention convention = Convention.Game,
        double lensShift = 0, double rectX = 0, double rectWidth = 1)
    {
        double y = (convention == Convention.MirroredYaw ? -yaw : convention == Convention.LooksBack ? yaw + 180 : yaw) * Math.PI / 180;
        double p = pitch * Math.PI / 180;
        var forward = (X: Math.Sin(y) * Math.Cos(p), Y: -Math.Sin(p), Z: Math.Cos(y) * Math.Cos(p));
        var right = (X: Math.Cos(y), Y: 0.0, Z: -Math.Sin(y));
        var up = (X: Math.Sin(y) * Math.Sin(p), Y: Math.Cos(p), Z: Math.Cos(y) * Math.Sin(p));
        return new PinholeCamera
        {
            Position = (anchor.X - forward.X * Distance, anchor.Y - forward.Y * Distance, anchor.Z - forward.Z * Distance),
            Right = right, Up = up, Forward = forward,
            M00 = 1 / (aspect * Math.Tan(vfov * Math.PI / 360)),
            M02 = lensShift,
            RectX = rectX, RectWidth = rectWidth,
        };
    }

    /// <summary>The runtime's procedure (CameraPreview.TryMeasure, then Framed) against <paramref name="cam"/>.</summary>
    private static UnitVec3 Measure(PinholeCamera cam, UnitVec3 point, float offset, out float lateral)
    {
        var rel = (X: point.X - cam.Position.X, Y: point.Y - cam.Position.Y, Z: point.Z - cam.Position.Z);
        double depth = rel.X * cam.Forward.X + rel.Y * cam.Forward.Y + rel.Z * cam.Forward.Z;
        var look = new UnitVec3((float)(cam.Position.X + cam.Forward.X * depth), (float)(cam.Position.Y + cam.Forward.Y * depth), (float)(cam.Position.Z + cam.Forward.Z * depth));
        var side = new UnitVec3((float)(look.X + cam.Right.X), (float)(look.Y + cam.Right.Y), (float)(look.Z + cam.Right.Z));
        float target = CameraFraming.ViewportTarget(offset, (float)cam.RectX, (float)cam.RectWidth);
        Assert.True(CameraFraming.TryLateral(target, (float)cam.Viewport(look).X, (float)cam.Viewport(side).X, out lateral));
        Assert.True(CameraFraming.TryUnit(new UnitVec3((float)cam.Right.X, (float)cam.Right.Y, (float)cam.Right.Z), out var right));
        return CameraFraming.Anchor(point, right, lateral);
    }

    private static readonly UnitVec3 Point = new(-1444.5f, 5.02f, -740.25f);

    [Theory]
    [InlineData(16f / 9f, 50f, 0.15f, 35f)]
    [InlineData(21f / 9f, 50f, 0.15f, 35f)]
    [InlineData(32f / 9f, 50f, 0.15f, 35f)]
    [InlineData(32f / 9f, 40f, -0.2f, 60f)]
    [InlineData(32f / 9f, 70f, 0.3f, 10f)]
    [InlineData(4f / 3f, 60f, 0.4f, 35f)]
    public void The_measured_shift_lands_the_point_at_its_share_of_the_screen_at_every_yaw(float aspect, float vfov, float offset, float pitch)
    {
        foreach (var convention in new[] { Convention.Game, Convention.MirroredYaw, Convention.LooksBack })
        {
            double? range = null;
            for (float yaw = 0f; yaw < 720f; yaw += 22.5f)
            {
                // The orbit starts centered; the next tick measures the orbit camera and moves the anchor.
                var centered = Rig(Point, yaw, pitch, vfov, aspect, convention);
                var anchor = Measure(centered, Point, offset, out _);
                var framed = Rig(anchor, yaw, pitch, vfov, aspect, convention);
                Assert.Equal(0.5 + offset, framed.ScreenX(Point), 4);
                // Measuring the framed camera again changes nothing (no drift, no feedback).
                var again = Measure(framed, Point, offset, out _);
                Assert.Equal(anchor.X, again.X, 3);
                Assert.Equal(anchor.Z, again.Z, 3);
                // The camera circles the point itself: always the same distance away.
                var d = (X: Point.X - framed.Position.X, Y: Point.Y - framed.Position.Y, Z: Point.Z - framed.Position.Z);
                double r = Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
                range ??= r;
                Assert.Equal(range.Value, r, 2);
            }
        }
    }

    [Theory]
    [InlineData(16f / 9f)]
    [InlineData(21f / 9f)]
    [InlineData(32f / 9f)]
    public void A_lens_shift_and_a_pillarboxed_viewport_still_land_on_the_screen_share(float aspect)
    {
        // A camera drawn into the middle 75% of the screen, with an off-center projection.
        var centered = Rig(Point, 40f, 35f, 50f, aspect, lensShift: 0.08, rectX: 0.125, rectWidth: 0.75);
        var anchor = Measure(centered, Point, 0.15f, out float lateral);
        var framed = Rig(anchor, 40f, 35f, 50f, aspect, lensShift: 0.08, rectX: 0.125, rectWidth: 0.75);
        Assert.Equal(0.65, framed.ScreenX(Point), 4);
        // The first version's pinhole model ignores both and lands elsewhere.
        float pinhole = CameraFraming.PinholeLateral(0.65f, Distance, 50f, aspect);
        Assert.True(Math.Abs(pinhole - lateral) > 0.1f);
    }

    [Theory]
    [InlineData(16f / 9f)]
    [InlineData(21f / 9f)]
    [InlineData(32f / 9f)]
    public void The_first_versions_model_matches_an_ideal_camera_and_fails_under_another_yaw_convention(float aspect)
    {
        // Under the game's documented convention and an ideal camera, the first version's math was right: vertical fov
        // times aspect gives the same shift the measurement gives.
        var ideal = Rig(Point, 30f, 35f, 50f, aspect);
        Measure(ideal, Point, 0.15f, out float measured);
        Assert.Equal(measured, CameraFraming.PinholeLateral(0.65f, Distance, 50f, aspect), 2);
        Assert.Equal(1f, CameraFraming.RightAgreement(30f, new UnitVec3((float)ideal.Right.X, (float)ideal.Right.Y, (float)ideal.Right.Z)), 4);

        // With a mirrored yaw the yaw-derived right axis points the wrong way at most yaws, and the point leaves the
        // place it should be (on a 32:9 screen by most of the screen); the measured axis does not care.
        double worst = 0;
        for (float yaw = 0f; yaw < 360f; yaw += 15f)
        {
            double y = yaw * Math.PI / 180;
            float pinhole = CameraFraming.PinholeLateral(0.65f, Distance, 50f, aspect);
            var model = new UnitVec3((float)(Point.X - Math.Cos(y) * pinhole), Point.Y, (float)(Point.Z + Math.Sin(y) * pinhole));
            var cam = Rig(model, yaw, 35f, 50f, aspect, Convention.MirroredYaw);
            worst = Math.Max(worst, Math.Abs(cam.ScreenX(Point) - 0.65));
        }

        Assert.True(worst > 0.2, "the yaw model should miss under a mirrored convention; worst " + worst);
    }

    [Fact]
    public void Readings_that_cannot_frame_are_refused_and_no_offset_is_the_point()
    {
        Assert.False(CameraFraming.TryLateral(0.65f, 0.5f, 0.5f, out _));
        Assert.False(CameraFraming.TryLateral(0.65f, float.NaN, 0.6f, out _));
        Assert.False(CameraFraming.TryLateral(0.65f, 0.5f, 0.5000001f, out _));
        Assert.True(CameraFraming.TryLateral(0.5f, 0.5f, 0.6f, out float none));
        Assert.Equal(0f, none);
        Assert.Equal(Point, CameraFraming.Anchor(Point, new UnitVec3(1, 0, 0), 0f));
        Assert.False(CameraFraming.TryUnit(new UnitVec3(0, 0, 0), out _));
        Assert.Equal(0.65f, CameraFraming.ViewportTarget(0.15f), 5);
        Assert.Equal(0.65f, CameraFraming.ViewportTarget(0.15f, 0f, 0f), 5); // an unusable rect counts as full
        Assert.True(CameraFraming.IsValidOffset(0.4f));
        Assert.False(CameraFraming.IsValidOffset(0.41f));
        Assert.False(CameraFraming.IsValidOffset(float.NaN));
        Assert.Equal(0f, new CameraPreviewOptions().ScreenOffset);
    }
}
