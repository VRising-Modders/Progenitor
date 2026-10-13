using System;

namespace Progenitor.Ui;

/// <summary>
/// MAPTP1: the game's world-map coordinate math, restated game-free so it can be tested. The client's
/// <c>ProjectM.UI.MapMenuMapper</c> keeps a <c>Matrix4x4 _WorldToAnchoredSpace</c> (field 0xD0)
/// that it rebuilds each frame with <c>ProjectM.MapUtils.UpdateMatrixGlobalMap</c> (texture
/// half-size scale × map rotation/zoom TRS × the zone's projection matrix). A map
/// click goes screen point → <c>RectTransformUtility.ScreenPointToLocalPointInRectangle</c> on the map
/// texture's rect (camera null) → <c>MapUtils.GetWorldPositionFromAnchoredPosition</c>,
/// which is <c>Matrix4x4.Inverse(worldToAnchored).MultiplyPoint(anchored.x, anchored.y, 0)</c>; the
/// vanilla admin map teleport (<c>MapMenuMapper.TeleportToMapPosition</c>) and the map
/// pin (<c>OnMapClicked</c>) both use exactly that. The forward direction
/// (<c>GetAnchoredPositionFromWorldPos2d</c>) is <c>MultiplyPoint(x, 0, z)</c>.
///
/// <para>Matrices are 16 floats in Unity's <c>Matrix4x4</c> field order (column-major:
/// <c>m00, m10, m20, m30, m01, …, m33</c>), the order the interop struct stores them in. Points go
/// through <see cref="MultiplyPoint"/> with the perspective divide, as Unity's does.</para>
/// </summary>
public static class MapTransformMath
{
    /// <summary>Element (<paramref name="row"/>, <paramref name="col"/>) of a column-major Unity matrix.</summary>
    public static float At(float[] m, int row, int col) => m[col * 4 + row];

    /// <summary>
    /// Unity <c>Matrix4x4.MultiplyPoint</c>: <paramref name="m"/> × (x, y, z, 1), divided by w. False when
    /// the matrix is malformed or w is zero.
    /// </summary>
    public static bool TryMultiplyPoint(float[] m, float x, float y, float z, out float ox, out float oy, out float oz)
    {
        ox = oy = oz = 0f;
        if (m == null || m.Length != 16) return false;
        double w = At(m, 3, 0) * x + At(m, 3, 1) * y + At(m, 3, 2) * z + At(m, 3, 3);
        if (Math.Abs(w) < 1e-12 || double.IsNaN(w)) return false;
        ox = (float)((At(m, 0, 0) * x + At(m, 0, 1) * y + At(m, 0, 2) * z + At(m, 0, 3)) / w);
        oy = (float)((At(m, 1, 0) * x + At(m, 1, 1) * y + At(m, 1, 2) * z + At(m, 1, 3)) / w);
        oz = (float)((At(m, 2, 0) * x + At(m, 2, 1) * y + At(m, 2, 2) * z + At(m, 2, 3)) / w);
        return Finite(ox) && Finite(oy) && Finite(oz);
    }

    /// <summary>
    /// The general 4×4 inverse (cofactors, in double). False for a malformed or singular matrix — a map
    /// whose projection has not been set yet reads as all zeros, which lands here.
    /// </summary>
    public static bool TryInvert(float[] m, out float[] inverse)
    {
        inverse = new float[16];
        if (m == null || m.Length != 16) return false;
        var a = new double[16];
        for (int i = 0; i < 16; i++)
        {
            if (!Finite(m[i])) return false;
            a[i] = m[i];
        }

        // Column-major storage: a[c * 4 + r]. Compute with row-major indexing helpers.
        double A(int r, int c) => a[c * 4 + r];
        var inv = new double[16];
        double s0 = A(0, 0) * A(1, 1) - A(1, 0) * A(0, 1);
        double s1 = A(0, 0) * A(1, 2) - A(1, 0) * A(0, 2);
        double s2 = A(0, 0) * A(1, 3) - A(1, 0) * A(0, 3);
        double s3 = A(0, 1) * A(1, 2) - A(1, 1) * A(0, 2);
        double s4 = A(0, 1) * A(1, 3) - A(1, 1) * A(0, 3);
        double s5 = A(0, 2) * A(1, 3) - A(1, 2) * A(0, 3);
        double c5 = A(2, 2) * A(3, 3) - A(3, 2) * A(2, 3);
        double c4 = A(2, 1) * A(3, 3) - A(3, 1) * A(2, 3);
        double c3 = A(2, 1) * A(3, 2) - A(3, 1) * A(2, 2);
        double c2 = A(2, 0) * A(3, 3) - A(3, 0) * A(2, 3);
        double c1 = A(2, 0) * A(3, 2) - A(3, 0) * A(2, 2);
        double c0 = A(2, 0) * A(3, 1) - A(3, 0) * A(2, 1);
        double det = s0 * c5 - s1 * c4 + s2 * c3 + s3 * c2 - s4 * c1 + s5 * c0;
        if (Math.Abs(det) < 1e-18 || double.IsNaN(det)) return false;
        double k = 1.0 / det;

        void Set(int r, int c, double v) => inv[c * 4 + r] = v * k;
        Set(0, 0, A(1, 1) * c5 - A(1, 2) * c4 + A(1, 3) * c3);
        Set(0, 1, -A(0, 1) * c5 + A(0, 2) * c4 - A(0, 3) * c3);
        Set(0, 2, A(3, 1) * s5 - A(3, 2) * s4 + A(3, 3) * s3);
        Set(0, 3, -A(2, 1) * s5 + A(2, 2) * s4 - A(2, 3) * s3);
        Set(1, 0, -A(1, 0) * c5 + A(1, 2) * c2 - A(1, 3) * c1);
        Set(1, 1, A(0, 0) * c5 - A(0, 2) * c2 + A(0, 3) * c1);
        Set(1, 2, -A(3, 0) * s5 + A(3, 2) * s2 - A(3, 3) * s1);
        Set(1, 3, A(2, 0) * s5 - A(2, 2) * s2 + A(2, 3) * s1);
        Set(2, 0, A(1, 0) * c4 - A(1, 1) * c2 + A(1, 3) * c0);
        Set(2, 1, -A(0, 0) * c4 + A(0, 1) * c2 - A(0, 3) * c0);
        Set(2, 2, A(3, 0) * s4 - A(3, 1) * s2 + A(3, 3) * s0);
        Set(2, 3, -A(2, 0) * s4 + A(2, 1) * s2 - A(2, 3) * s0);
        Set(3, 0, -A(1, 0) * c3 + A(1, 1) * c1 - A(1, 2) * c0);
        Set(3, 1, A(0, 0) * c3 - A(0, 1) * c1 + A(0, 2) * c0);
        Set(3, 2, -A(3, 0) * s3 + A(3, 1) * s1 - A(3, 2) * s0);
        Set(3, 3, A(2, 0) * s3 - A(2, 1) * s1 + A(2, 2) * s0);

        for (int i = 0; i < 16; i++)
        {
            if (double.IsNaN(inv[i]) || double.IsInfinity(inv[i])) return false;
            inverse[i] = (float)inv[i];
        }

        return true;
    }

    /// <summary>
    /// The game's <c>GetWorldPositionFromAnchoredPosition</c>: a point in the map texture's rect (local,
    /// pivot-relative, the value <c>ScreenPointToLocalPointInRectangle</c> returns) to world X and Z.
    /// False when the map's matrix cannot be inverted (no map data yet).
    /// </summary>
    public static bool TryAnchoredToWorld(float[] worldToAnchored, float anchoredX, float anchoredY, out float worldX, out float worldZ)
    {
        worldX = worldZ = 0f;
        if (!TryInvert(worldToAnchored, out var inv)) return false;
        if (!TryMultiplyPoint(inv, anchoredX, anchoredY, 0f, out worldX, out _, out worldZ)) return false;
        return true;
    }

    /// <summary>The game's <c>GetAnchoredPositionFromWorldPos2d</c>: world X/Z to the map texture's rect.</summary>
    public static bool TryWorldToAnchored(float[] worldToAnchored, float worldX, float worldZ, out float anchoredX, out float anchoredY) =>
        TryMultiplyPoint(worldToAnchored, worldX, 0f, worldZ, out anchoredX, out anchoredY, out _);

    /// <summary>
    /// What <c>RectTransformUtility.ScreenPointToWorldPointInRectangle(rect, screen, camera: null)</c>
    /// computes, the call the game's map click makes: the ray from (sx, sy, -100) along +Z meets the rect's
    /// plane (normal = rotation × back, through the rect's world position). The caller then takes
    /// <c>rect.InverseTransformPoint</c> of the hit. False when the ray runs parallel to the plane or the
    /// plane is behind it.
    /// </summary>
    public static bool TryScreenRayOnRectPlane(
        float qx, float qy, float qz, float qw,
        float px, float py, float pz,
        float sx, float sy,
        out float hx, out float hy, out float hz)
    {
        hx = hy = hz = 0f;
        // n = q * (0, 0, -1): the third column of the rotation matrix, negated.
        double nx = -(2.0 * (qx * qz + qw * qy));
        double ny = -(2.0 * (qy * qz - qw * qx));
        double nz = -(1.0 - 2.0 * (qx * qx + qy * qy));
        double ox = sx, oy = sy, oz = -100.0;
        // Unity Plane: normal n, distance d = -dot(n, p). Raycast: enter = -(dot(o, n) + d) / dot(dir, n).
        double d = -(nx * px + ny * py + nz * pz);
        double vdot = nz; // dir = (0, 0, 1)
        if (Math.Abs(vdot) < 1e-6) return false;
        double ndot = ox * nx + oy * ny + oz * nz + d;
        double enter = -ndot / vdot;
        if (!(enter > 0)) return false;
        hx = (float)ox;
        hy = (float)oy;
        hz = (float)(oz + enter);
        return true;
    }

    private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}
