#if !PROGENITOR_TESTS
using ProjectM.UI;
using UnityEngine;

namespace Progenitor.Ui;

internal sealed partial class UnityUiRuntime
{
    private static void DrawMapPings(PingView view, Vector3 target)
    {
        var world = Worlds.Client;
        if (world == null || !world.IsCreated) return;
        var map = world.GetExistingSystemManaged<MapMenuMapper>();
        var menu = map?._MapMenu;
        if (menu != null && menu.MapTexture != null && menu.MapTexture.gameObject.activeInHierarchy)
            PlaceMapPing(ref view.MapMarker, menu.MapTexture.rectTransform, menu.MapTexture.rectTransform,
                map!._WorldToAnchoredSpace, target, false);
        else if (view.MapMarker != null) view.MapMarker.gameObject.SetActive(false);
        var mini = world.GetExistingSystemManaged<MiniMapHUDSystem>();
        var parent = mini?._MiniMapParent;
        if (parent != null && parent.MapTexture != null && parent.MapTexture.gameObject.activeInHierarchy)
            PlaceMapPing(ref view.MiniMarker, parent.MapTexture.rectTransform, parent.Mask,
                mini!._CurrentZoneData.WorldToAnchoredSpace, target, true);
        else if (view.MiniMarker != null) view.MiniMarker.gameObject.SetActive(false);
    }

    private static void PlaceMapPing(ref RectTransform? marker, RectTransform texture, RectTransform parent,
        Matrix4x4 matrix, Vector3 target, bool clamp)
    {
        if (parent == null) return;
        if (marker == null)
        {
            var go = HudChild(parent.gameObject, "progenitor-find-pin", new Vector2(.5f, .5f),
                new Vector2(.5f, .5f), new Vector2(24, 24));
            HudImage(go, _markerSprite, ToColor(UiLocateLook.Accent));
            marker = go.GetComponent<RectTransform>();
        }
        else if (marker.parent != parent) marker.SetParent(parent, false);
        // Native maps project world X/Z (zero elevation) into their texture's anchored plane.
        var projected = matrix.MultiplyPoint3x4(new Vector3(target.x, 0, target.z));
        var local = parent.InverseTransformPoint(texture.TransformPoint(new Vector3(projected.x, projected.y, 0)));
        if (clamp)
        {
            var radius = Mathf.Max(0, Mathf.Min(parent.rect.width, parent.rect.height) * .5f - 15);
            var offset = Vector2.ClampMagnitude(new Vector2(local.x, local.y), radius);
            local.x = offset.x; local.y = offset.y;
        }
        marker.localPosition = new Vector3(local.x, local.y, 0);
        marker.localRotation = Quaternion.identity;
        marker.gameObject.SetActive(true);
        marker.SetAsLastSibling();
    }
}
#endif
