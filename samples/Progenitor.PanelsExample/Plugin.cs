using System;
using System.Collections.Generic;
using BepInEx;
using Progenitor.Plugins;
using Progenitor.Ui;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.PanelsExample;

/// <summary>
/// The vanilla panel sites (spec P6), one attachment each, for live acceptance:
///
/// <list type="bullet">
/// <item><description>Container window: a <c>Deposit</c> button and a status row in the left
/// column, an <c>Output</c> button in the right column and an <c>Options</c> button beside the
/// rename button.</description></item>
/// <item><description>Workstation and refinement windows: <c>Inputs</c> left, <c>Outputs</c> right.</description></item>
/// <item><description>Player inventory: <c>Stash</c> beside the sort button.</description></item>
/// <item><description>Castle heart window (also reachable as <c>ThronePanel</c>): <c>Castles</c> left, <c>Fuel</c> right.</description></item>
/// <item><description>Item tooltip: one line naming the hovered item and its stack.</description></item>
/// </list>
///
/// Every open, close and target change and every click is one log line starting
/// <c>Panels example:</c>. Nothing here moves an item. The container's <c>Locate</c> button is
/// the one that sends something: the window's network id to the server, which resolves it to a
/// stable container id and answers with the id's network id, which the client finds and pings
/// (<see cref="Locate"/>). Every handle is tracked, so unload removes every button, row, frame
/// watch and the server's request handler.
/// </summary>
[BepInPlugin("progenitor.panelsexample", "Progenitor Panels Example", "0.1.0")]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Both)]
public sealed class Plugin : ProgenitorPlugin<PanelsContext>
{
    protected override void OnLoad(PanelsContext ctx)
    {
        if (ctx.IsServer)
        {
            // The server half is only the Locate round trip's request handler.
            Locate.AttachServer(ctx);
            ctx.Log.LogInfo("Panels example: loaded (server: resolve handler)");
            return;
        }

        var container = Attach(ctx, VanillaUiSite.ContainerPanel, VanillaPanelAnchor.LeftEdge, "Deposit");
        var status = container.Row("No container open");
        ctx.Track(container.OnPanel(e => status.Text = e.Change == VanillaPanelChange.Closed
            ? "No container open"
            : "Container " + e.Target.NetworkIdIndex));
        Attach(ctx, VanillaUiSite.ContainerPanel, VanillaPanelAnchor.RightEdge, "Output");
        // Locate: the round trip through the server (Locate.cs). The only button that sends anything.
        Attach(ctx, VanillaUiSite.ContainerPanel, VanillaPanelAnchor.BesideRename, "Locate", h => Locate.Send(ctx, h.Target));
        Attach(ctx, VanillaUiSite.StationPanel, VanillaPanelAnchor.LeftEdge, "Inputs");
        Attach(ctx, VanillaUiSite.StationPanel, VanillaPanelAnchor.RightEdge, "Outputs");
        Attach(ctx, VanillaUiSite.InventoryPanel, VanillaPanelAnchor.BesideSort, "Stash");
        Attach(ctx, VanillaUiSite.CastleHeartPanel, VanillaPanelAnchor.LeftEdge, "Castles");
        Attach(ctx, VanillaUiSite.CastleHeartPanel, VanillaPanelAnchor.RightEdge, "Fuel");

        // The tooltip site takes rows only. The line is rewritten on every hovered item, before the
        // frame's rows are drawn, so it never shows the previous item's text.
        var tooltip = UiApi.AttachToVanilla(VanillaUiSite.InventoryTooltip, VanillaPanelAnchor.TooltipBody, ctx.Owner, ctx.Guid);
        ctx.Track(tooltip);
        var line = new List<string> { "" };
        ctx.Track(tooltip.OnPanel(e =>
        {
            if (e.Change == VanillaPanelChange.Closed) return;
            line[0] = "Example: item " + e.Target.ItemGuid + " x" + e.Target.ItemAmount;
            tooltip.SetRows(line);
        }));

        ctx.Log.LogInfo("Panels example: loaded (tooltip enabled=" + tooltip.Enabled + ")");
    }

    protected override void OnUnload(PanelsContext ctx, PluginUnloadReason reason) =>
        ctx.Log.LogInfo("Panels example: unloading, reason=" + reason);

    /// <summary>One attachment with one button, its open/close log and its click log, all tracked.</summary>
    private static IVanillaAttachHandle Attach(PanelsContext ctx, VanillaUiSite site, VanillaPanelAnchor anchor, string caption,
        Action<IVanillaAttachHandle>? then = null)
    {
        var handle = UiApi.AttachToVanilla(site, anchor, ctx.Owner, ctx.Guid);
        ctx.Track(handle);
        var log = ctx.Log;
        var button = handle.Button(caption);
        ctx.Track(button.OnClick(() =>
        {
            log.LogInfo("Panels example: click " + site + " " + handle.Anchor + " '" + caption + "' " + handle.Target);
            then?.Invoke(handle);
        }));
        ctx.Track(handle.OnPanel(e =>
            log.LogInfo("Panels example: " + site + " " + handle.Anchor + " " + e.Change + " "
                + (e.Change == VanillaPanelChange.Closed ? e.Previous : e.Target))));
        if (!handle.Enabled) log.LogInfo("Panels example: " + site + " " + anchor + " is disabled on this side");
        return handle;
    }
}

/// <summary>The example's context. Empty, as every context should be.</summary>
public sealed class PanelsContext : PluginContext { }
