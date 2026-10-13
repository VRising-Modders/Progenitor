#if !PROGENITOR_TESTS
using System;
using Progenitor.Plugins;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityObject = UnityEngine.Object;

namespace Progenitor.Film;

/// <summary>
/// An offscreen camera that renders the game world into a fixed-size target without the HUD, for
/// film frames. Before each render it copies the game camera's HDRP settings (volume layer mask,
/// frame settings, anti-aliasing) and culling mask, and anchors volumes at itself. A plain new
/// camera missed the church's interior volume and lit characters bright blue under the moonlight;
/// with the copied settings they light as in the game view (live 2026-09-28).
/// Client only. Dispose releases the camera and its render target.
/// </summary>
public sealed class FilmCamera : IDisposable
{
    private readonly GameObject _go;
    private readonly Camera _cam;
    private readonly HDAdditionalCameraData _hd;
    private readonly RenderTexture _target;
    private Texture2D? _readback;

    internal FilmCamera(string owner, int width, int height)
    {
        Width = width;
        Height = height;
        _go = new GameObject("progenitor-film-camera-" + owner);
        UnityObject.DontDestroyOnLoad(_go);
        _cam = _go.AddComponent<Camera>();
        _cam.enabled = false;
        _cam.nearClipPlane = 0.05f;
        _cam.farClipPlane = 600f;
        _hd = _go.AddComponent<HDAdditionalCameraData>();
        _target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { name = "progenitor-film-target" };
        _target.Create();
        _cam.targetTexture = _target;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The frame last rendered.</summary>
    public RenderTexture Target => _target;

    public Vector3 Position => _go.transform.position;

    /// <summary>Places the camera: world position, look direction, up, vertical field of view in degrees.</summary>
    public void Set(Vector3 position, Vector3 forward, Vector3 up, float verticalFov)
    {
        _go.transform.SetPositionAndRotation(position, Quaternion.LookRotation(forward, up));
        _cam.fieldOfView = Mathf.Clamp(verticalFov, 1f, 170f);
    }

    public void LookAt(Vector3 position, Vector3 target, float verticalFov) =>
        Set(position, target - position, Vector3.up, verticalFov);

    /// <summary>Renders one frame into <see cref="Target"/>.</summary>
    public void Render()
    {
        var main = Camera.main;
        if (main != null)
        {
            var mainHd = main.GetComponent<HDAdditionalCameraData>();
            if (mainHd != null)
            {
                mainHd.CopyTo(_hd);
                _hd.volumeAnchorOverride = null;
            }
            _cam.cullingMask = main.cullingMask;
        }
        _cam.Render();
    }

    /// <summary>
    /// Reads the last frame back as raw RGB24, rows bottom-up (Unity's order), into
    /// <paramref name="into"/>, which must hold <c>Width × Height × 3</c> bytes.
    /// </summary>
    public void ReadRgb24(byte[] into)
    {
        if (into == null || into.Length < Width * Height * 3) throw new ArgumentException("buffer too small.", nameof(into));
        _readback ??= new Texture2D(Width, Height, TextureFormat.RGB24, false);
        var previous = RenderTexture.active;
        RenderTexture.active = _target;
        _readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
        RenderTexture.active = previous;
        var raw = _readback.GetRawTextureData();
        int bytes = Width * Height * 3;
        if (raw.Length < bytes) throw new InvalidOperationException("readback returned " + raw.Length + " bytes, expected " + bytes + ".");
        // One block copy instead of 6 MB of indexer calls: an IL2CPP array's elements start after
        // its header (klass, monitor, bounds, max_length: four pointer-sized fields).
        System.Runtime.InteropServices.Marshal.Copy(raw.Pointer + IntPtr.Size * 4, into, 0, bytes);
    }

    /// <summary>Writes the last frame as a PNG (stills, checks).</summary>
    public void SavePng(string path)
    {
        _readback ??= new Texture2D(Width, Height, TextureFormat.RGB24, false);
        var previous = RenderTexture.active;
        RenderTexture.active = _target;
        _readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
        _readback.Apply(false);
        RenderTexture.active = previous;
        System.IO.File.WriteAllBytes(path, ImageConversion.EncodeToPNG(_readback));
    }

    public void Dispose()
    {
        try { if (_cam != null) _cam.targetTexture = null; } catch { }
        try { if (_target != null) { _target.Release(); UnityObject.Destroy(_target); } } catch { }
        try { if (_readback != null) UnityObject.Destroy(_readback); } catch { }
        try { if (_go != null) UnityObject.Destroy(_go); } catch { }
    }
}
#endif
