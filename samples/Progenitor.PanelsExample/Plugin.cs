using System;
using System.Collections.Generic;
using BepInEx;
using Progenitor.Inventory;
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
/// <item><description>Recipe tooltip: a muted line under the product naming the recipe, product and
/// station, and one line per requirement under the requirement list (the first one green).</description></item>
/// <item><description>The game's sort (key or button): every <see cref="InventorySortUsed"/> is logged with
/// the client's hour of day (<c>ctx.Time.GetTimeOfDay</c>), and a second sort of the same inventory
/// within <see cref="DoubleSortMs"/> is logged as a double sort.</description></item>
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
    /// <summary>Two sorts of one inventory closer than this count as a double sort.</summary>
    internal const long DoubleSortMs = 400;

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

        // The recipe tooltip (workstation, refinement station, crafting tab): one line under the
        // product's description and one line per requirement under the requirement list, rebuilt on
        // every hovered recipe. A real mod would look the counts up here (ask its server once per hover).
        var recipeBody = UiApi.AttachToVanilla(VanillaUiSite.RecipeTooltip, VanillaPanelAnchor.TooltipBody, ctx.Owner, ctx.Guid);
        var recipeNeeds = UiApi.AttachToVanilla(VanillaUiSite.RecipeTooltip, VanillaPanelAnchor.TooltipRequirements, ctx.Owner, ctx.Guid);
        ctx.Track(recipeBody);
        ctx.Track(recipeNeeds);
        var recipeLog = ctx.Log;
        ctx.Track(recipeBody.OnPanel(e =>
        {
            if (e.Change == VanillaPanelChange.Closed) return;
            var t = e.Target;
            recipeLog.LogInfo("Panels example: recipe " + t);
            recipeBody.SetRows(new VanillaRow[]
            {
                new("Example: recipe " + t.RecipeGuid + " makes " + t.ItemAmount + " x " + t.ItemGuid
                    + (t.HasNetworkId ? " at station " + t.NetworkIdIndex : " (crafting tab)"), LabelTone.Muted),
            });
        }));
        ctx.Track(recipeNeeds.OnPanel(e =>
        {
            if (e.Change == VanillaPanelChange.Closed) return;
            var t = e.Target;
            var rows = new VanillaRow[t.Requirements.Count];
            for (int i = 0; i < rows.Length; i++)
            {
                var need = t.Requirements[i];
                rows[i] = new VanillaRow("Example: needs " + need.Amount + " x " + need.ItemGuid, i == 0 ? LabelTone.Ok : LabelTone.Normal);
            }

            recipeNeeds.SetRows(rows);
        }));

        // The game's sort, key or button: no input is read, the framework reports the request.
        var sortLog = ctx.Log;
        var time = ctx.Time;
        ctx.Events.On<InventorySortUsed>(e =>
        {
            var clock = time.GetTimeOfDay();
            sortLog.LogInfo("Panels example: " + e + " at " + (clock == null ? "unknown time" : clock.ToString()));
            if (e.SincePreviousMs >= 0 && e.SincePreviousMs <= DoubleSortMs)
                sortLog.LogInfo("Panels example: double sort " + e.Target + " (" + e.SincePreviousMs + " ms apart)");
        });

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
