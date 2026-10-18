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
/// column, an <c>Output</c> button in the right column, and beside the rename button a <c>Locate</c>
/// button and a log icon button (the drawn <c>log</c> mark) whose tooltip is its own title and body.</description></item>
/// <item><description>Workstation and refinement windows: <c>Inputs</c> left, <c>Outputs</c> right.</description></item>
/// <item><description>Player inventory: <c>Stash</c> beside the sort button.</description></item>
/// <item><description>Castle heart window (also reachable as <c>ThronePanel</c>): <c>Castles</c> left, <c>Fuel</c> right.</description></item>
/// <item><description>Item tooltip: one line naming the hovered item and its stack.</description></item>
/// <item><description>Recipe tooltip: a muted line under the product naming the recipe, product and
/// station, one line per requirement under the requirement list (the first one green), one muted line
/// under the title naming the window the recipe is shown in, an indented line under each requirement line
/// (lined up with the line's name, past its icon) and a footer: the key hint <i>Hold [Alt] Show storage</i>
/// in the game's own hint style, plus one more line while the key is held.</description></item>
/// <item><description>Item tooltip: a muted header line under the title and the same key-hint footer.</description></item>
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
        var rename = Attach(ctx, VanillaUiSite.ContainerPanel, VanillaPanelAnchor.BesideRename, "Locate", h => Locate.Send(ctx, h.Target));
        // An icon button beside the rename pencil: the drawn log mark (a page with lines). Its hover tooltip is the title
        // and body given here, in the game-control tooltip under the button; without Tooltip it would be the caption.
        var history = rename.Button("Log", VanillaButtonIcon.Drawn("log"));
        ctx.Track(history.Tooltip("Deposits and withdrawals of this chest.", "Chest log"));
        var historyLog = ctx.Log;
        ctx.Track(history.OnClick(() => historyLog.LogInfo("Panels example: click chest log " + rename.Target)));
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
                new("Example: recipe " + t.RecipeGuid + " makes " + t.ProductAmount + " x " + t.ProductGuid + ", tooltip shows " + t.ItemGuid
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

        // The recipe tooltip's header: one muted line right under the title, above the description.
        var recipeHeader = UiApi.AttachToVanilla(VanillaUiSite.RecipeTooltip, VanillaPanelAnchor.TooltipHeader, ctx.Owner, ctx.Guid);
        ctx.Track(recipeHeader);
        ctx.Track(recipeHeader.OnPanel(e =>
        {
            if (e.Change == VanillaPanelChange.Closed) return;
            recipeHeader.SetRows(new VanillaRow[] { new("Example: header, " + e.Target.Kind, LabelTone.Muted) });
        }));

        // Under each requirement line: one row indented one level, so it starts where the line's name starts.
        for (int i = 0; i < 6; i++)   // the tooltip shows at most six requirement lines (0 to 5)
        {
            int lineIndex = i;
            var under = UiApi.AttachToRequirementLine(lineIndex, ctx.Owner, ctx.Guid);
            ctx.Track(under);
            ctx.Track(under.OnPanel(e =>
            {
                if (e.Change == VanillaPanelChange.Closed) return;
                var needs = e.Target.Requirements;
                if (lineIndex >= needs.Count) { under.ClearRows(); return; }
                under.SetRows(new VanillaRow[] { new("Example: line " + lineIndex + ", " + needs[lineIndex].ItemGuid, LabelTone.Muted, 1) });
            }));
        }

        // Footers with a key hint in the game's own style. The key is a hold bind of this plugin (default Alt), so
        // the glyph follows a remap in the Keybinds tab, and a controller button when one is bound and in use.
        var recipeFooter = UiApi.AttachToVanilla(VanillaUiSite.RecipeTooltip, VanillaPanelAnchor.TooltipFooter, ctx.Owner, ctx.Guid);
        var itemFooter = UiApi.AttachToVanilla(VanillaUiSite.InventoryTooltip, VanillaPanelAnchor.TooltipFooter, ctx.Owner, ctx.Guid);
        var itemHeader = UiApi.AttachToVanilla(VanillaUiSite.InventoryTooltip, VanillaPanelAnchor.TooltipHeader, ctx.Owner, ctx.Guid);
        ctx.Track(recipeFooter);
        ctx.Track(itemFooter);
        ctx.Track(itemHeader);
        var held = new bool[1];
        ctx.Keybinds.RegisterHold(StorageBind, KeyChord.Alt, "Panels example: show more in the tooltip footers while held",
            onPressed: () => { held[0] = true; recipeFooter.Refresh(); itemFooter.Refresh(); },
            onReleased: () => { held[0] = false; recipeFooter.Refresh(); itemFooter.Refresh(); });
        VanillaRow[] FooterRows(VanillaPanelTarget t) => held[0]
            ? new VanillaRow[] { VanillaRow.KeyHint(StorageBind, "Show storage", hold: true), new("Example: footer, held, " + t.Kind, LabelTone.Ok) }
            : new VanillaRow[] { VanillaRow.KeyHint(StorageBind, "Show storage", hold: true) };
        ctx.Track(recipeFooter.OnPanel(e => { if (e.Change != VanillaPanelChange.Closed) recipeFooter.SetRows(FooterRows(e.Target)); }));
        ctx.Track(itemFooter.OnPanel(e => { if (e.Change != VanillaPanelChange.Closed) itemFooter.SetRows(FooterRows(e.Target)); }));
        ctx.Track(itemHeader.OnPanel(e =>
        {
            if (e.Change != VanillaPanelChange.Closed) itemHeader.SetRows(new VanillaRow[] { new("Example: header, item " + e.Target.ItemGuid, LabelTone.Muted) });
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

    /// <summary>The hold bind the footers' key hint names (default Alt).</summary>
    internal const string StorageBind = "ShowStorage";

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
