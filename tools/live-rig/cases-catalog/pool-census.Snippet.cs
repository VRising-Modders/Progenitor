using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;

/// <summary>
/// Bounded client census of crystal-shader renderers for same-instance pool reuse.
/// Per-slot lines go through Scratch.Log (prefix POOLID). The return value is a compact summary.
/// Candidates are the recipe crystal-ring family: material slots whose shader name is
/// Shader Graphs/Shader_VFX_Ice_Crystal_Unlit_Final.
/// </summary>
public static class Snippet
{
    const string CrystalShader = "Shader Graphs/Shader_VFX_Ice_Crystal_Unlit_Final";
    const int CountCap = 256;
    const float Radius = 60f;

    sealed class Cand
    {
        public Renderer Renderer = null!;
        public GameObject Go = null!;
        public int RendererId;
        public int GoId;
        public bool Active;
        public string GoName = "";
        public float Dist;
        public Material[] Materials = null!;
    }

    public static object Run()
    {
        try
        {
            return Census();
        }
        catch (Exception e)
        {
            string summary = "POOLID summary error=" + e.GetType().Name;
            try { Scratch.Log(summary); } catch { }
            return summary;
        }
    }

    static object Census()
    {
        var world = Scratch.Client;
        if (world == null || !world.IsCreated)
            return "POOLID summary error=no-client-world";

        bool haveOrigin = TryOrigin(out var origin);
        string originText = haveOrigin
            ? F(origin.x) + "," + F(origin.y) + "," + F(origin.z)
            : "none";

        var cands = new List<Cand>();
        int scanned = 0, skippedRadius = 0, failures = 0;
        MaterialPropertyBlock? block = null;
        try { block = new MaterialPropertyBlock(); }
        catch { return "POOLID summary error=no-property-block"; }
        if (block == null)
            return "POOLID summary error=no-property-block";

        var type = Il2CppType.Of<Renderer>();
        if (type == null)
            return "POOLID summary error=no-renderer-type";

        Il2CppSystem.Object[]? arr = null;
        try { arr = Resources.FindObjectsOfTypeAll(type); }
        catch { return "POOLID summary error=find-threw"; }
        if (arr == null)
            return "POOLID summary error=find-null";

        int length = 0;
        try { length = arr.Length; }
        catch { return "POOLID summary error=find-length"; }

        for (int i = 0; i < length; i++)
        {
            Renderer? renderer = null;
            try
            {
                var obj = arr[i];
                if (obj == null) continue;
                renderer = obj.TryCast<Renderer>();
            }
            catch { continue; }
            // Unity fake-null: Il2CppInterop overloaded == / implicit bool, not C# is-null.
            if (renderer == null) continue;

            try
            {
                var materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0) continue;

                bool crystal = false;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    Material? material = null;
                    try { material = materials[slot]; }
                    catch { continue; }
                    if (ShaderName(material) == CrystalShader)
                    {
                        crystal = true;
                        break;
                    }
                }
                if (!crystal) continue;

                GameObject? go = null;
                int rendererId = 0, goId = 0;
                bool inHierarchy = false;
                string goName = "";
                Vector3 pos = default;
                try
                {
                    go = renderer.gameObject;
                    rendererId = renderer.GetInstanceID();
                    if (go != null)
                    {
                        goId = go.GetInstanceID();
                        inHierarchy = go.activeInHierarchy;
                        goName = go.name ?? "";
                        var xf = go.transform;
                        if (xf != null) pos = xf.position;
                    }
                }
                catch { failures++; continue; }

                scanned++;
                float dist = 0f;
                if (haveOrigin)
                {
                    float dx = pos.x - origin.x;
                    float dy = pos.y - origin.y;
                    float dz = pos.z - origin.z;
                    dist = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (dist > Radius)
                    {
                        skippedRadius++;
                        continue;
                    }
                }

                var cand = new Cand();
                cand.Renderer = renderer;
                cand.Go = go!;
                cand.RendererId = rendererId;
                cand.GoId = goId;
                cand.Active = inHierarchy;
                cand.GoName = goName;
                cand.Dist = dist;
                cand.Materials = materials;
                cands.Add(cand);
            }
            catch
            {
                failures++;
            }
        }

        cands.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        int skippedCap = 0;
        if (cands.Count > CountCap)
        {
            skippedCap = cands.Count - CountCap;
            cands.RemoveRange(CountCap, skippedCap);
        }

        var ids = new List<int>();
        int renderers = 0, active = 0, withBlock = 0, green = 0;

        for (int c = 0; c < cands.Count; c++)
        {
            var cand = cands[c];
            var renderer = cand.Renderer;
            var materials = cand.Materials;
            if (renderer == null || materials == null) { failures++; continue; }

            renderers++;
            ids.Add(cand.RendererId);
            if (cand.Active) active++;

            bool anyBlock = false, anyGreen = false;
            for (int slot = 0; slot < materials.Length; slot++)
            {
                Material? slotMaterial = null;
                try { slotMaterial = materials[slot]; }
                catch { continue; }
                if (ShaderName(slotMaterial) != CrystalShader) continue;

                bool hasBlock = false;
                string colorText = "none";
                try
                {
                    renderer.GetPropertyBlock(block, slot);
                    hasBlock = !block.isEmpty;
                    if (hasBlock)
                    {
                        var col = block.GetColor("_BaseColor");
                        colorText = F(col.r) + "," + F(col.g) + "," + F(col.b) + "," + F(col.a);
                        if (IsGreen(col)) anyGreen = true;
                    }
                }
                catch
                {
                    failures++;
                    continue;
                }

                if (hasBlock) anyBlock = true;
                Scratch.Log(
                    "POOLID rendererId=" + cand.RendererId.ToString(CultureInfo.InvariantCulture)
                    + " goId=" + cand.GoId.ToString(CultureInfo.InvariantCulture)
                    + " active=" + (cand.Active ? "True" : "False")
                    + " slot=" + slot.ToString(CultureInfo.InvariantCulture)
                    + " hasBlock=" + (hasBlock ? "True" : "False")
                    + " baseColor=" + colorText
                    + " go=" + cand.GoName);
            }

            if (anyBlock) withBlock++;
            if (anyGreen) green++;
        }

        ids.Sort();
        var idset = new StringBuilder();
        for (int i = 0; i < ids.Count; i++)
        {
            if (i > 0) idset.Append(',');
            idset.Append(ids[i].ToString(CultureInfo.InvariantCulture));
        }

        bool truncated = skippedCap > 0;
        string summary = "POOLID summary renderers=" + renderers.ToString(CultureInfo.InvariantCulture)
            + " active=" + active.ToString(CultureInfo.InvariantCulture)
            + " withBlock=" + withBlock.ToString(CultureInfo.InvariantCulture)
            + " green=" + green.ToString(CultureInfo.InvariantCulture)
            + " failures=" + failures.ToString(CultureInfo.InvariantCulture)
            + " truncated=" + (truncated ? "True" : "False")
            + " cap=" + CountCap.ToString(CultureInfo.InvariantCulture)
            + " radius=" + F(Radius)
            + " origin=" + originText
            + " scanned=" + scanned.ToString(CultureInfo.InvariantCulture)
            + " emitted=" + renderers.ToString(CultureInfo.InvariantCulture)
            + " skippedRadius=" + skippedRadius.ToString(CultureInfo.InvariantCulture)
            + " skippedCap=" + skippedCap.ToString(CultureInfo.InvariantCulture)
            + " ids=" + idset;
        Scratch.Log(summary);
        return summary;
    }

    static bool TryOrigin(out Vector3 origin)
    {
        origin = default;
        try
        {
            var cam = Camera.main;
            if (cam == null) return false;
            var xf = cam.transform;
            if (xf == null) return false;
            origin = xf.position;
            return true;
        }
        catch
        {
            return false;
        }
    }

    static string ShaderName(Material? material)
    {
        if (material == null) return "";
        Shader? shader = null;
        try { shader = material.shader; }
        catch { return ""; }
        if (shader == null) return "";
        try { return shader.name ?? ""; }
        catch { return ""; }
    }

    static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    static bool IsGreen(Color c) =>
        Math.Abs(c.r - 0.12f) < 0.05f
        && Math.Abs(c.g - 0.65f) < 0.08f
        && Math.Abs(c.b - 0.25f) < 0.08f;
}
