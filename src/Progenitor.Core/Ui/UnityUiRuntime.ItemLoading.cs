using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

internal sealed partial class UnityUiRuntime
{
    /// <summary>Owned font/banner on the Core overlay, without cursor or input capture.</summary>
    internal void ShowItemLoadingNotice(IRuntimePanel panel, IRuntimeWidget label)
    {
        if (panel is not UnityPanel host || label is not UnityWidget widget
            || host.Overlay == null || !host.Alive || !widget.Alive) return;
        SelfPlacePanel(panel, true);
        var go = widget.GameObject;
        go.transform.SetParent(host.Overlay.transform, false);
        var rect = go.GetComponent<RectTransform>();
        if (rect == null) return;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(0, -85);
        rect.sizeDelta = new Vector2(520, 64);
        var graphics = go.GetComponentsInChildren<Graphic>(true);
        for (int i = 0; i < graphics.Length; i++) graphics[i].raycastTarget = false;
        var raycaster = host.Overlay.GetComponent<GraphicRaycaster>();
        if (raycaster != null) raycaster.enabled = false;
        host.Visible = false;
        host.CanvasVisible = true;
        widget.Visible = true;
    }
}
