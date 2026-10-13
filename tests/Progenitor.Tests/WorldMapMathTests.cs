using System;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// MAPTP1: the world map's coordinate math (<see cref="MapTransformMath"/>, the game's
/// <c>MapUtils.GetWorldPositionFromAnchoredPosition</c> / <c>GetAnchoredPositionFromWorldPos2d</c> and
/// the null-camera screen ray its map click uses) and the reveal leases (<see cref="MapRevealLeases"/>).
/// Game-free; the matrices are built the way <c>MapUtils.UpdateMatrixGlobalMap</c> builds the real one.
/// </summary>
public class WorldMapMathTests
{
    // ---- column-major Unity matrices -----------------------------------------------------------

    private static float[] Identity()
    {
        var m = new float[16];
        m[0] = m[5] = m[10] = m[15] = 1f;
        return m;
    }

    private static float[] Mul(float[] a, float[] b)
    {
        var r = new float[16];
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < 4; col++)
            {
                double s = 0;
                for (int k = 0; k < 4; k++) s += MapTransformMath.At(a, row, k) * MapTransformMath.At(b, k, col);
                r[col * 4 + row] = (float)s;
            }

        return r;
    }

    private static float[] Scale(float sx, float sy, float sz)
    {
        var m = Identity();
        m[0] = sx;
        m[5] = sy;
        m[10] = sz;
        return m;
    }

    private static float[] RotateZ(double degrees)
    {
        double r = degrees * Math.PI / 180.0;
        var m = Identity();
        m[0] = (float)Math.Cos(r);
        m[1] = (float)Math.Sin(r);
        m[4] = (float)-Math.Sin(r);
        m[5] = (float)Math.Cos(r);
        return m;
    }

    /// <summary>
    /// A zone projection like the game's: world (x, y, z) → normalized map (-1..1) across a zone centered on
    /// (cx, cz) with half-extents (hx, hz); world Y goes to the third axis.
    /// </summary>
    private static float[] Projection(float cx, float cz, float hx, float hz)
    {
        var m = new float[16];
        // row 0: (x - cx) / hx
        m[0 * 4 + 0] = 1f / hx;
        m[3 * 4 + 0] = -cx / hx;
        // row 1: (z - cz) / hz
        m[2 * 4 + 1] = 1f / hz;
        m[3 * 4 + 1] = -cz / hz;
        // row 2: y
        m[1 * 4 + 2] = 1f;
        m[15] = 1f;
        return m;
    }

    /// <summary>UpdateMatrixGlobalMap: Scale(texture/2) × TRS(0, rotation, zoom) × projection.</summary>
    private static float[] WorldToAnchored(float texW, float texH, double rotation, float zoom, float[] projection) =>
        Mul(Mul(Scale(texW * 0.5f, texH * 0.5f, 1f), Mul(RotateZ(-rotation), Scale(zoom, zoom, zoom))), projection);

    [Fact]
    public void Inverse_times_matrix_is_identity()
    {
        var m = WorldToAnchored(2048, 2048, 17, 1.3f, Projection(-1200, 800, 1600, 1600));
        Assert.True(MapTransformMath.TryInvert(m, out var inv));
        var id = Mul(m, inv);
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                Assert.Equal(r == c ? 1f : 0f, MapTransformMath.At(id, r, c), 4);
    }

    [Theory]
    [InlineData(0.0, 1.0f, -1200f, 800f)]
    [InlineData(0.0, 2.5f, -2500f, -300f)]
    [InlineData(33.0, 0.7f, 140f, 1950f)]
    public void Anchored_to_world_inverts_the_games_forward_mapping(double rotation, float zoom, float wx, float wz)
    {
        var m = WorldToAnchored(1024, 1024, rotation, zoom, Projection(-1200, 800, 1600, 1600));
        Assert.True(MapTransformMath.TryWorldToAnchored(m, wx, wz, out float ax, out float ay));
        Assert.True(MapTransformMath.TryAnchoredToWorld(m, ax, ay, out float bx, out float bz));
        Assert.Equal(wx, bx, 1);
        Assert.Equal(wz, bz, 1);
    }

    [Fact]
    public void The_map_center_is_the_zone_center_and_the_texture_edge_is_the_zone_edge()
    {
        var m = WorldToAnchored(1000, 1000, 0, 1f, Projection(-1200, 800, 1600, 1600));
        Assert.True(MapTransformMath.TryAnchoredToWorld(m, 0f, 0f, out float cx, out float cz));
        Assert.Equal(-1200f, cx, 2);
        Assert.Equal(800f, cz, 2);
        // The right edge of the texture (half width 500) is the zone's +X edge, 1600 m away.
        Assert.True(MapTransformMath.TryAnchoredToWorld(m, 500f, 0f, out float ex, out _));
        Assert.Equal(400f, ex, 1);
    }

    [Fact]
    public void A_map_without_data_has_no_world_point()
    {
        Assert.False(MapTransformMath.TryAnchoredToWorld(new float[16], 10f, 10f, out _, out _));
        Assert.False(MapTransformMath.TryAnchoredToWorld(new float[3], 10f, 10f, out _, out _));
        var nan = Identity();
        nan[5] = float.NaN;
        Assert.False(MapTransformMath.TryAnchoredToWorld(nan, 0f, 0f, out _, out _));
    }

    [Fact]
    public void Null_camera_screen_ray_hits_an_unrotated_rect_at_the_screen_point()
    {
        Assert.True(MapTransformMath.TryScreenRayOnRectPlane(0, 0, 0, 1, 960, 540, 0, 400, 300,
            out float hx, out float hy, out float hz));
        Assert.Equal(400f, hx);
        Assert.Equal(300f, hy);
        Assert.Equal(0f, hz, 4);

        // A rect plane at z = 25 is still in front of the ray's origin (z = -100).
        Assert.True(MapTransformMath.TryScreenRayOnRectPlane(0, 0, 0, 1, 0, 0, 25, 1, 2, out _, out _, out float hz2));
        Assert.Equal(25f, hz2, 4);

        // Turned edge-on (90 degrees about Y) the ray runs along the plane: no hit.
        float s = (float)Math.Sqrt(0.5);
        Assert.False(MapTransformMath.TryScreenRayOnRectPlane(0, s, 0, s, 0, 0, 0, 1, 2, out _, out _, out _));
    }

    // ---- reveal leases ---------------------------------------------------------------------------

    [Fact]
    public void Reveal_restores_the_flag_only_when_the_last_lease_goes()
    {
        var book = new MapRevealLeases();
        object a = new(), b = new();
        Assert.True(book.Acquire(a, () => false));
        Assert.False(book.Acquire(b, () => true)); // the second lease does not re-read the (now on) flag
        Assert.False(book.Acquire(a, () => true)); // the same holder twice counts once
        Assert.Equal(2, book.Count);
        Assert.Null(book.Release(a));
        Assert.Equal(false, book.Release(b));
        Assert.Null(book.Prior);
        Assert.Null(book.Release(b));
    }

    [Fact]
    public void A_flag_the_player_had_on_stays_on_and_an_unreadable_flag_restores_to_off()
    {
        var book = new MapRevealLeases();
        object a = new();
        book.Acquire(a, () => true);
        Assert.Equal(true, book.Release(a));

        book.Acquire(a, () => null);
        Assert.Equal(false, book.Release(a));

        book.Acquire(a, () => throw new InvalidOperationException("no world"));
        Assert.Equal(false, book.ReleaseAll());
        Assert.Null(book.ReleaseAll());
    }
}
