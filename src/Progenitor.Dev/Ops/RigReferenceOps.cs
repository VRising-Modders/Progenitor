using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Models;
using UnityEngine;

namespace Progenitor.Dev.Ops;

internal static class RigReferenceOps
{
    internal static object Run(JsonElement args)
    {
        if (Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException("rig_reference is client-only.");
        string action = args.TryGetProperty("action", out var a) ? a.GetString() ?? "list" : "list";
        var available = UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>();
        if (action == "list")
        {
            // A scoped inventory includes separate head/face meshes: Body_GEO alone
            // is not a complete anatomical reference on the vampire models.
            int? requestedRoot = args.TryGetProperty("rootId", out var scopedRoot) ? scopedRoot.GetInt32() : null;
            bool inspectMaterials = false;
            if (args.TryGetProperty("inspectMaterials", out var inspectArg))
            {
                if (inspectArg.ValueKind != JsonValueKind.True && inspectArg.ValueKind != JsonValueKind.False)
                    throw new ArgumentException("inspectMaterials must be a boolean.");
                inspectMaterials = inspectArg.GetBoolean();
            }
            if (inspectMaterials && requestedRoot == null)
                throw new ArgumentException("inspectMaterials requires a scoped rootId list.");
            int? inspectRendererId = args.TryGetProperty("inspectRendererId", out var inspectRendererArg)
                ? inspectRendererArg.GetInt32() : null;
            if (inspectRendererId != null && !inspectMaterials)
                throw new ArgumentException("inspectRendererId requires inspectMaterials=true.");
            // WearableAppearance creates each overlay directly under its exact runtime model root.
            var wearableRoots = new HashSet<int>();
            foreach (var r in available)
                if (r != null && r.name.StartsWith("progenitor-wear-", StringComparison.Ordinal) && r.transform.parent != null)
                    wearableRoots.Add(r.transform.parent.GetInstanceID());
            var candidates = new List<object>();
            int inspectedRenderers = 0;
            bool foundInspectRenderer = false;
            foreach (var r in available)
            {
                if (r == null || r.sharedMesh == null) continue;
                if (requestedRoot == null && r.name != "Body_GEO" && !r.name.StartsWith("progenitor-wear-", StringComparison.Ordinal)) continue;
                var ancestors = new List<object>();
                int? recommendedModelRootId = null;
                bool inScope = requestedRoot == null || r.transform.GetInstanceID() == requestedRoot;
                for (var p = r.transform.parent; p != null; p = p.parent)
                {
                    ancestors.Add(new { id = p.GetInstanceID(), name = p.name });
                    if (p.GetInstanceID() == requestedRoot) inScope = true;
                    if (wearableRoots.Contains(p.GetInstanceID())) recommendedModelRootId ??= p.GetInstanceID();
                }
                if (!inScope) continue;
                bool inspectRenderer = inspectMaterials && (inspectRendererId != null
                    ? r.GetInstanceID() == inspectRendererId.Value : inspectedRenderers < 16);
                if (inspectRenderer) inspectedRenderers++;
                if (inspectRendererId != null && r.GetInstanceID() == inspectRendererId.Value)
                    foundInspectRenderer = true;
                var materials = new List<object>();
                var sharedMaterials = r.sharedMaterials;
                for (int i = 0; i < sharedMaterials.Length && i < 16; i++)
                {
                    var material = sharedMaterials[i];
                    var entry = new Dictionary<string, object?>
                    {
                        ["slot"] = i,
                        ["name"] = material == null ? null : material.name,
                        ["shader"] = material == null || material.shader == null ? null : material.shader.name
                    };
                    if (inspectRenderer && i < 8 && material != null)
                        entry["inspection"] = InspectMaterial(material);
                    materials.Add(entry);
                }
                candidates.Add(new { rendererId = r.GetInstanceID(), name = r.name, vertices = r.sharedMesh.vertexCount,
                    readable = r.sharedMesh.isReadable, enabled = r.enabled, subMeshCount = r.sharedMesh.subMeshCount,
                    materialSlotCount = sharedMaterials.Length, materialSlotsTruncated = sharedMaterials.Length > 16,
                    materialInspectionTruncated = inspectMaterials && (inspectRendererId == null && !inspectRenderer
                        || inspectRenderer && sharedMaterials.Length > 8),
                    materials, recommendedModelRootId, ancestors });
            }
            if (inspectRendererId != null && !foundInspectRenderer)
                throw new ArgumentException("inspectRendererId is stale or outside rootId. List again.");
            return new { candidates, note = "For authored wearable export, use recommendedModelRootId shared by the chosen body and overlay. It is inferred from a currently attached Progenitor overlay's direct parent. If absent, equip a procedural wearable first or resolve the exact HybridModelUser model Transform through the API; an arbitrary ancestor will not qualify for runtime garment binding." };
        }
        if (action != "capture" && action != "surface") throw new ArgumentException("action must be list, capture or surface.");
        int rootId = args.GetProperty("rootId").GetInt32();
        var ids = new HashSet<int>();
        foreach (var id in args.GetProperty("rendererIds").EnumerateArray())
            if (!ids.Add(id.GetInt32())) throw new ArgumentException("Duplicate renderer ID.");
        if (ids.Count == 0 || ids.Count > 16) throw new ArgumentException("Select 1..16 renderers.");
        var bindIds = new HashSet<int>();
        if (args.TryGetProperty("bindRendererIds", out var bindArg))
        {
            if (action != "capture" || bindArg.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("bindRendererIds requires a capture action and an array of renderer IDs.");
            foreach (var id in bindArg.EnumerateArray())
            {
                int value = id.GetInt32();
                if (!bindIds.Add(value) || ids.Contains(value))
                    throw new ArgumentException("Bind-reference renderer IDs must be distinct from each other and exported renderers.");
            }
            if (bindIds.Count > 16) throw new ArgumentException("Select at most 16 bind-reference renderers.");
        }
        var selected = new List<SkinnedMeshRenderer>();
        var bindSelected = new List<SkinnedMeshRenderer>();
        Transform? root = null;
        foreach (var r in available)
        {
            if (r == null) continue;
            int id = r.GetInstanceID();
            if (!ids.Contains(id) && !bindIds.Contains(id)) continue;
            if (ids.Contains(id)) selected.Add(r); else bindSelected.Add(r);
            for (var p = r.transform; p != null; p = p.parent)
                if (p.GetInstanceID() == rootId) { root = p; break; }
        }
        if (selected.Count != ids.Count || bindSelected.Count != bindIds.Count || root == null)
            throw new ArgumentException("Selection is stale or the root is not an ancestor. List again.");
        foreach (var renderer in selected)
            if (!Under(renderer.transform, root)) throw new ArgumentException("An exported renderer is outside rootId.");
        foreach (var renderer in bindSelected)
            if (!Under(renderer.transform, root)) throw new ArgumentException("A bind-reference renderer is outside rootId.");
        string path = args.GetProperty("path").GetString() ?? throw new ArgumentException("path is required.");
        if (action == "surface")
        {
            var surface = RigReference.CapturePoseSurface(root, selected.ToArray());
            surface.Save(path);
            return new { path = System.IO.Path.GetFullPath(path), surface.MeshCount, surface.VertexCount,
                note = "Posed surface reference only. This contains no authored bind mesh or skin weights and cannot be exported as a garment." };
        }
        var snapshot = RigReference.Capture(root, selected.ToArray(), bindSelected.ToArray());
        snapshot.Save(path);
        return new { path = System.IO.Path.GetFullPath(path), snapshot.MeshCount, snapshot.VertexCount, snapshot.JointCount,
            note = "Authored reference and one current pose captured; this is not an animation clip or a fitting approval." };
    }

    private static bool Under(Transform transform, Transform root)
    {
        for (var p = transform; p != null; p = p.parent)
            if (p == root) return true;
        return false;
    }

    private static object InspectMaterial(Material material)
    {
        const int maxProperties = 64;
        var properties = new List<object>();
        int propertyCount = 0;
        string? error = null;
        try
        {
            var shader = material.shader;
            propertyCount = shader == null ? 0 : shader.GetPropertyCount();
            for (int i = 0; i < propertyCount && i < maxProperties; i++)
            {
                var row = new Dictionary<string, object?> { ["index"] = i };
                try
                {
                    string name = shader!.GetPropertyName(i) ?? "";
                    int type = (int)shader.GetPropertyType(i);
                    row["name"] = name;
                    row["type"] = type switch
                    {
                        0 => "Color", 1 => "Vector", 2 => "Float", 3 => "Range",
                        4 => "Texture", 5 => "Int", _ => "Unknown"
                    };
                    row["typeId"] = type;
                    switch (type)
                    {
                        case 0:
                            var c = material.GetColor(name);
                            row["value"] = new float?[] { Finite(c.r), Finite(c.g), Finite(c.b), Finite(c.a) };
                            break;
                        case 1:
                            var v = material.GetVector(name);
                            row["value"] = new float?[] { Finite(v.x), Finite(v.y), Finite(v.z), Finite(v.w) };
                            break;
                        case 4:
                            var texture = material.GetTexture(name);
                            row["value"] = texture == null ? null : texture.name;
                            break;
                        case 2:
                        case 3:
                        case 5:
                            row["value"] = Finite(material.GetFloat(name));
                            break;
                    }
                }
                catch (Exception exception) { row["error"] = exception.GetType().Name; }
                properties.Add(row);
            }
        }
        catch (Exception exception) { error = exception.GetType().Name; }
        return new { propertyCount, propertiesTruncated = propertyCount > maxProperties,
            properties, error };
    }

    private static float? Finite(float value) => float.IsFinite(value) ? value : null;
}
