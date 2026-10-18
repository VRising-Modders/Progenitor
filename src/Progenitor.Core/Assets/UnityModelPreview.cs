#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;
using UnityObject = UnityEngine.Object;

namespace Progenitor.Effects;

/// <summary>
/// Client/main-thread-only, fixed 512px still preview. Copies presentation components only:
/// instantiating a gameplay prefab and disabling scripts afterwards would be too late for Awake.
/// The caller retains the source's asset lease until disposal. Original meshes/materials are
/// borrowed, never edited or destroyed. This does not run animation or game presentation drivers.
/// </summary>
internal sealed class UnityModelPreview : IDisposable
{
    private const int Resolution = 512;
    private const int MaxNodes = 2048;
    private const int MaxRenderers = 128;
    private const int MaxVertices = 2000000;
    private static readonly HashSet<UnityModelPreview> Live = new();
    private GameObject? _clone;
    private GameObject? _cameraObject;
    private GameObject? _lightObject;
    private GameObject? _canvasObject;
    private Camera? _camera;
    private RenderTexture? _texture;
    private bool _disposed;

    private UnityModelPreview() { }

    public string CloneName { get; private set; } = "";

    internal static UnityModelPreview Create(GameObject source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (Application.isBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            throw new InvalidOperationException("Model preview requires a rendering client.");

        var preview = new UnityModelPreview();
        try
        {
            preview.Build(source);
            Live.Add(preview);
            return preview;
        }
        catch
        {
            preview.Dispose();
            throw;
        }
    }

    private void Build(GameObject source)
    {
        int layer = FindUnusedLayer();
        CloneName = "progenitor-model-preview-" + Guid.NewGuid().ToString("N");
        _clone = new GameObject(CloneName);
        _clone.SetActive(false);
        _clone.hideFlags = HideFlags.HideAndDontSave;
        _clone.layer = layer;
        _clone.transform.localScale = source.transform.localScale;
        UnityObject.DontDestroyOnLoad(_clone);

        // Breadth-first traversal bounds both work and stack usage. All bone transforms are
        // copied before renderers, including inactive bones. Root placement is intentionally reset.
        var nodes = new List<(Transform Source, Transform Copy, bool Visible)>();
        var transforms = new Dictionary<int, Transform>();
        nodes.Add((source.transform, _clone.transform, true));
        transforms.Add(source.transform.GetInstanceID(), _clone.transform);
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            for (int j = 0; j < node.Source.childCount; j++)
            {
                if (nodes.Count >= MaxNodes) throw new InvalidOperationException("Preview exceeds 2048 transforms.");
                var child = node.Source.GetChild(j);
                var copy = new GameObject(child.name);
                // Attach immediately so any later setup failure is covered by root disposal.
                copy.transform.SetParent(node.Copy, false);
                copy.layer = layer;
                copy.transform.localPosition = child.localPosition;
                copy.transform.localRotation = child.localRotation;
                copy.transform.localScale = child.localScale;
                bool visible = node.Visible && child.gameObject.activeSelf;
                copy.SetActive(child.gameObject.activeSelf);
                nodes.Add((child, copy.transform, visible));
                transforms.Add(child.GetInstanceID(), copy.transform);
            }
        }

        // A still uses the first authored LOD. Copying every enabled LOD renderer would
        // superimpose several versions of the model after removing its LODGroup driver.
        var omittedLods = new HashSet<int>();
        foreach (var node in nodes)
        {
            var group = node.Source.GetComponent<LODGroup>();
            if (group == null) continue;
            var lods = group.GetLODs();
            if (lods.Length == 0) continue;
            for (int i = 1; i < lods.Length; i++)
                foreach (var renderer in lods[i].renderers)
                    if (renderer != null) omittedLods.Add(renderer.GetInstanceID());
            foreach (var renderer in lods[0].renderers)
                if (renderer != null) omittedLods.Remove(renderer.GetInstanceID());
        }

        var renderers = new List<Renderer>();
        int vertices = 0;
        foreach (var node in nodes)
        {
            if (!node.Visible) continue;
            foreach (var original in node.Source.GetComponents<Renderer>())
            {
                if (original == null || !original.enabled || omittedLods.Contains(original.GetInstanceID())) continue;
                var skin = original.TryCast<SkinnedMeshRenderer>();
                var meshRenderer = original.TryCast<MeshRenderer>();
                if (skin == null && meshRenderer == null) continue;
                var filter = node.Source.GetComponent<MeshFilter>();
                var mesh = skin != null ? skin.sharedMesh : filter?.sharedMesh;
                if (mesh == null || mesh.vertexCount == 0) continue;
                if (renderers.Count >= MaxRenderers || mesh.vertexCount > MaxVertices - vertices)
                    throw new InvalidOperationException("Preview exceeds 128 renderers or 2000000 vertices.");
                vertices += mesh.vertexCount;
                Renderer renderer;
                if (skin != null)
                {
                    var copy = node.Copy.gameObject.AddComponent<SkinnedMeshRenderer>();
                    copy.sharedMesh = mesh;
                    var bones = skin.bones;
                    var copiedBones = new Il2CppReferenceArray<Transform>(bones.Length);
                    for (int i = 0; i < bones.Length; i++)
                    {
                        if (bones[i] == null) continue;
                        if (!transforms.TryGetValue(bones[i].GetInstanceID(), out var bone))
                            throw new InvalidOperationException("Preview has a bone outside the source hierarchy.");
                        copiedBones[i] = bone;
                    }
                    copy.bones = copiedBones;
                    if (skin.rootBone != null)
                    {
                        if (!transforms.TryGetValue(skin.rootBone.GetInstanceID(), out var rootBone))
                            throw new InvalidOperationException("Preview root bone is outside the source hierarchy.");
                        copy.rootBone = rootBone;
                    }
                    copy.localBounds = skin.localBounds;
                    copy.quality = skin.quality;
                    copy.updateWhenOffscreen = true;
                    for (int i = 0; i < mesh.blendShapeCount; i++)
                        copy.SetBlendShapeWeight(i, skin.GetBlendShapeWeight(i));
                    renderer = copy;
                }
                else
                {
                    node.Copy.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                    renderer = node.Copy.gameObject.AddComponent<MeshRenderer>();
                }
                // sharedMaterials preserves the real shaders without allocating material instances.
                var materials = original.sharedMaterials;
                renderer.sharedMaterials = materials;
                var block = new MaterialPropertyBlock();
                original.GetPropertyBlock(block);
                renderer.SetPropertyBlock(block);
                for (int i = 0; i < materials.Length; i++)
                {
                    block.Clear();
                    original.GetPropertyBlock(block, i);
                    if (!block.isEmpty) renderer.SetPropertyBlock(block, i);
                }
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderers.Add(renderer);
            }
        }
        if (renderers.Count == 0) throw new InvalidOperationException("Source has no enabled mesh or skinned mesh renderers.");

        _texture = new RenderTexture(Resolution, Resolution, 24, RenderTextureFormat.ARGB32);
        _texture.name = CloneName + "-texture";
        if (!_texture.Create()) throw new InvalidOperationException("Could not allocate preview render texture.");

        _cameraObject = new GameObject(CloneName + "-camera");
        _cameraObject.SetActive(false);
        _camera = _cameraObject.AddComponent<Camera>();
        _camera.enabled = false;
        var hd = _cameraObject.AddComponent<HDAdditionalCameraData>();
        // Same camera-data copy seam as FilmCamera. Asset-specific HDRP shader, exposure and
        // lighting behavior still requires client observation; a successful Render is not proof.
        var mainHd = Camera.main?.GetComponent<HDAdditionalCameraData>();
        if (mainHd != null) mainHd.CopyTo(hd);
        hd.volumeLayerMask = 0;
        hd.volumeAnchorOverride = _cameraObject.transform;
        _camera.cullingMask = 1 << layer;
        _camera.clearFlags = CameraClearFlags.SolidColor;
        _camera.backgroundColor = new Color(.06f, .07f, .09f, 1f);
        hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
        hd.backgroundColorHDR = _camera.backgroundColor;
        hd.clearDepth = true;
        _camera.orthographic = true;
        _camera.aspect = 1f;
        _camera.useOcclusionCulling = false;
        _camera.allowMSAA = false;
        _camera.targetTexture = _texture;

        _lightObject = new GameObject(CloneName + "-light");
        _lightObject.SetActive(false);
        _lightObject.transform.rotation = Quaternion.Euler(35f, -35f, 0f);
        var light = _lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = Color.white;
        light.intensity = 2f;
        light.cullingMask = 1 << layer;
        light.shadows = LightShadows.None;
        _lightObject.AddComponent<HDAdditionalLightData>();

        // Only our explicit camera render sees active geometry. No clone scripts, animation,
        // colliders, rigidbodies, audio, particles, LOD drivers or custom components ever exist.
        var previousTarget = RenderTexture.active;
        try
        {
            _clone.SetActive(true);
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            var center = bounds.center;
            float radius = bounds.extents.magnitude;
            if (!float.IsFinite(center.x) || !float.IsFinite(center.y) || !float.IsFinite(center.z)
                || !float.IsFinite(radius) || radius < .00001f || radius > 10000f)
                throw new InvalidOperationException("Preview has invalid or unbounded renderer bounds.");
            radius = Math.Max(.01f, radius);
            // A sphere enclosing the world AABB fits every camera orientation, including wings.
            var direction = new Vector3(1f, .55f, -1f).normalized;
            _cameraObject.transform.position = center + direction * (3f * radius);
            _cameraObject.transform.LookAt(center);
            _camera.orthographicSize = radius * 1.15f;
            _camera.nearClipPlane = Math.Max(.001f, radius * .01f);
            _camera.farClipPlane = radius * 5f;
            _cameraObject.SetActive(true);
            _lightObject.SetActive(true);
            _camera.Render();
        }
        finally
        {
            Cleanup(() => _clone.SetActive(false));
            Cleanup(() => _lightObject.SetActive(false));
            Cleanup(() => _cameraObject.SetActive(false));
            Cleanup(() => RenderTexture.active = previousTarget);
        }

        _canvasObject = new GameObject(CloneName + "-canvas");
        var canvas = _canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5000;
        UnityObject.DontDestroyOnLoad(_canvasObject);
        var imageObject = new GameObject("model-preview-image");
        imageObject.transform.SetParent(_canvasObject.transform, false);
        var rect = imageObject.AddComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
        rect.anchoredPosition = Vector2.zero;
        float size = Math.Max(1f, Math.Min(Resolution, Math.Min(Screen.width, Screen.height) * .8f));
        rect.sizeDelta = new Vector2(size, size);
        var image = imageObject.AddComponent<RawImage>();
        image.texture = _texture;
        image.color = Color.white;
        image.raycastTarget = false;
    }

    private static int FindUnusedLayer()
    {
        var occupied = new HashSet<int>();
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GameObject>());
        foreach (var obj in all)
        {
            var go = obj?.TryCast<GameObject>();
            if (go != null) occupied.Add(go.layer);
        }
        for (int i = 31; i >= 8; i--)
            if (!occupied.Contains(i) && string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
        throw new InvalidOperationException("No unused unnamed layer is available for isolated model preview.");
    }

    internal static void DisposeAll()
    {
        foreach (var preview in new List<UnityModelPreview>(Live)) preview.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Live.Remove(this);
        // Attempt every release even when Unity has already destroyed part of the preview.
        Cleanup(() => { if (_camera != null) { _camera.enabled = false; _camera.targetTexture = null; } });
        Destroy(_canvasObject);
        Destroy(_cameraObject);
        Destroy(_lightObject);
        Destroy(_clone);
        Cleanup(() => { if (_texture != null) _texture.Release(); });
        Cleanup(() => { if (_texture != null) UnityObject.DestroyImmediate(_texture); });
        _canvasObject = _cameraObject = _lightObject = _clone = null;
        _camera = null;
        _texture = null;
    }

    private static void Destroy(GameObject? go)
    {
        Cleanup(() => { if (go != null) go.SetActive(false); });
        Cleanup(() => { if (go != null) UnityObject.DestroyImmediate(go); });
    }

    private static void Cleanup(Action action)
    {
        try { action(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Model preview cleanup: " + e.Message); }
    }
}
#endif
