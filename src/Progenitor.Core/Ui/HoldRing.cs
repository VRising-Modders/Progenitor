using System;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// A hold-to-act charge ring: one <c>Image</c> (type Filled, Radial360, from the top, clockwise)
/// parented into a UI tree the caller names, so it moves and hides with it. Raycasts are off, so it
/// never takes a click the game is waiting for. Pair it with <see cref="HoldMachine"/>: show on the
/// press, <see cref="SetProgress"/> while charging, <see cref="SetPulse"/> after the fire
/// (<see cref="HoldRingMath.Pulse"/>), <see cref="Hide"/> on a cancel. Client only, main thread.
///
/// <para><b>Sprite.</b> The caller may pass a game sprite (Dye all borrows the swatch's own round
/// selection frame — borrowed, never copied, so it must be safe for the ring to hold it). Otherwise the
/// ring draws its own texture once (<see cref="HoldRingMath.Alpha"/>) and destroys it on
/// <see cref="Dispose"/>.</para>
///
/// <para><b>Lifetime.</b> One GameObject at a time. It is re-parented by the next show, hidden between
/// holds and destroyed on <see cref="Dispose"/>; a parent destroyed under it takes it along, which the
/// next show notices and rebuilds. Dispose it from the owner's unload: a ring parented into a vanilla
/// tree is not under the plugin's own root.</para>
/// </summary>
public sealed class HoldRing : IDisposable
{
    /// <summary>The ring color Dye all and the debug tools' map teleport use (warm gold, slightly translucent).</summary>
    public static readonly Color DefaultColor = new(1f, 0.86f, 0.55f, 0.95f);

    private readonly string _name;
    private readonly Color _color;
    private GameObject? _go;
    private Image? _image;
    private Texture2D? _texture;
    private Sprite? _drawn;
    private bool _disposed;

    /// <summary>
    /// A ring whose GameObject is called <paramref name="objectName"/> (name it after the owner, e.g.
    /// <c>progenitor-dyeall-ring</c>, so a hierarchy dump says whose it is).
    /// </summary>
    public HoldRing(string objectName, Color? color = null)
    {
        _name = string.IsNullOrWhiteSpace(objectName) ? "progenitor-hold-ring" : objectName.Trim();
        _color = color ?? DefaultColor;
    }

    /// <summary>True while the ring is built and shown.</summary>
    public bool Visible => Alive(_go) && _go!.activeSelf;

    /// <summary>
    /// Show an empty ring stretched over <paramref name="parent"/>, <paramref name="outset"/> units
    /// beyond its edges (Dye all: over the held swatch). <paramref name="sprite"/> null draws the ring.
    /// </summary>
    public void ShowOver(Transform parent, float outset, Sprite? sprite = null)
    {
        if (_disposed || !Alive(parent)) return;
        if (!Attach(parent, sprite)) return;
        var rt = _go!.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(-outset, -outset);
        rt.offsetMax = new Vector2(outset, outset);
        _go.SetActive(true);
    }

    /// <summary>
    /// Show an empty ring <paramref name="diameter"/> units across, centered on
    /// <paramref name="localPoint"/> in <paramref name="parent"/>'s local space (the map teleport: at the
    /// cursor). <see cref="MoveTo"/> keeps it under a moving pointer.
    /// </summary>
    public void ShowAt(Transform parent, Vector2 localPoint, float diameter, Sprite? sprite = null)
    {
        if (_disposed || !Alive(parent)) return;
        if (!Attach(parent, sprite)) return;
        var rt = _go!.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        float d = diameter > 0f ? diameter : 48f;
        rt.sizeDelta = new Vector2(d, d);
        rt.localPosition = new Vector3(localPoint.x, localPoint.y, 0f);
        _go.SetActive(true);
    }

    /// <summary>Move a ring shown with <see cref="ShowAt"/> to <paramref name="localPoint"/>.</summary>
    public void MoveTo(Vector2 localPoint)
    {
        if (!Alive(_go)) return;
        try { _go!.transform.localPosition = new Vector3(localPoint.x, localPoint.y, 0f); }
        catch { /* the parent took it along */ }
    }

    /// <summary>Fill to <paramref name="progress"/> (0 to 1).</summary>
    public void SetProgress(float progress)
    {
        if (!Alive(_image)) return;
        _image!.fillAmount = Mathf.Clamp01(progress);
    }

    /// <summary>The completion pulse: a full ring that grows and fades.</summary>
    public void SetPulse(float scale, float alpha)
    {
        if (!Alive(_go) || !Alive(_image)) return;
        _image!.fillAmount = 1f;
        var c = _color;
        c.a = _color.a * Mathf.Clamp01(alpha);
        _image.color = c;
        _go!.transform.localScale = new Vector3(scale, scale, 1f);
    }

    /// <summary>Hide the ring (it stays built for the next hold).</summary>
    public void Hide()
    {
        if (!Alive(_go)) return;
        try
        {
            _go!.SetActive(false);
            _go.transform.localScale = Vector3.one;
        }
        catch { /* the parent took it along */ }
    }

    /// <summary>Destroy the ring and the drawn sprite.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DestroyObject(_go);
        DestroyObject(_drawn);
        DestroyObject(_texture);
        _go = null;
        _image = null;
        _drawn = null;
        _texture = null;
    }

    private bool Attach(Transform parent, Sprite? sprite)
    {
        if (!Alive(_go) || !Alive(_image)) Build();
        if (!Alive(_go) || !Alive(_image)) return false;
        var rt = _go!.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        _go.layer = parent.gameObject.layer; // a camera-space canvas culls by layer
        rt.localScale = Vector3.one;
        rt.SetAsLastSibling();
        _image!.sprite = Alive(sprite) ? sprite : Drawn();
        _image.fillAmount = 0f;
        _image.color = _color;
        return true;
    }

    private void Build()
    {
        _go = new GameObject(_name);
        _go.AddComponent<RectTransform>();
        _image = _go.AddComponent<Image>();
        _image.type = Image.Type.Filled;
        _image.fillMethod = Image.FillMethod.Radial360;
        _image.fillOrigin = (int)Image.Origin360.Top;
        _image.fillClockwise = true;
        _image.preserveAspect = true;
        _image.raycastTarget = false;
        _go.SetActive(false);
    }

    private Sprite? Drawn()
    {
        if (Alive(_drawn) && Alive(_texture)) return _drawn;
        int size = HoldRingMath.TextureSize;
        _texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = _name,
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
        };
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
                _texture.SetPixel(x, y, new Color(1f, 1f, 1f, HoldRingMath.Alpha(x, y, size)));
        }

        _texture.Apply(false, false);
        _drawn = Sprite.Create(_texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        _drawn.name = _name;
        _drawn.hideFlags = HideFlags.HideAndDontSave;
        return _drawn;
    }

    private static void DestroyObject(UnityEngine.Object? o)
    {
        if (!Alive(o)) return;
        try { UnityEngine.Object.Destroy(o); }
        catch { /* already gone */ }
    }

    private static bool Alive(UnityEngine.Object? o) => UnityAlive.Of(o);
}
