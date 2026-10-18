#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM.UI;
using Progenitor.Ecs;
using Progenitor.Ui;
using TMPro;
using Unity.Entities;
using UnityEngine;

namespace Progenitor.Materials;

/// <summary>
/// Client: the refinement station recipe tooltip's requirement counts (<see cref="MaterialOperation.RefineView"/>).
///
/// <para><b>What the game does.</b> Every update of <c>RefinementstationMenuMapper</c> (its window job) rebuilds
/// the hovered recipe's cost list (the station's own count of each item via <c>InventoryUtilities.GetItemAmount</c>
/// on the station, the recipe amount scaled by the station's resource bonus) and calls
/// <c>FakeTooltip.SetTooltip</c>, which always ends in <c>TooltipRecipeRequiredItems.SetData</c>: per line the
/// <c>WorkstationRequirementEntry.Amount</c> text becomes <c>"{inStock}/{amount}"</c> and both <c>Amount</c> and
/// <c>Name</c> take <c>StunColor.Red2</c> (none in stock), <c>Green1</c> (enough) or <c>Yellow2</c> (some).</para>
///
/// <para><b>What Progenitor does.</b> After that update (<c>SystemHooks.After</c>, the same frame, before the
/// canvas draws), when the server sent a RefineView view for the very station the window shows, each line whose
/// item the view governs is rewritten with the game's count plus the pool total, in the game's format, and both
/// texts take the game's own color for the new state (read from <c>StunColor</c>). Before the window's next update
/// (<c>SystemHooks.Before</c>) every line still holding Progenitor's text is put back to the game's, so the game's
/// own count is what the next pass reads. Display only: refining takes from the station's slots. Only Unity text
/// and color writes on the tooltip's own rows; no game method is called and no game data is changed.</para>
/// </summary>
internal static class MaterialRefineTooltipRuntime
{
    private static readonly RefineRowMemo Memo = new();
    private static FakeTooltip? _written;
    private static readonly List<RefineLine> Last = new();
    private static string _lastState = "not run";
    private static long _lastFrame = -1;
    private static int _lastRecipe;
    private static ulong _lastStation;
    private static uint _lastSequence;
    private static bool _faultLogged;

    /// <summary>Before the window's update: put back the game's text on every line Progenitor wrote.</summary>
    internal static void Before()
    {
        if (Memo.Count == 0) return;
        try
        {
            var block = _written != null && UnityAlive.Of(_written) ? _written.RecipeRequiredItems : null;
            var list = block != null && UnityAlive.Of(block) ? block.RequiredItemsList : null;
            if (list != null)
                foreach (int row in Memo.Rows)
                {
                    if (row >= list.Count) continue;
                    var entry = list[row];
                    if (entry == null || !UnityAlive.Of(entry)) continue;
                    var amount = Tmp(entry.Amount);
                    if (amount == null || !Memo.TryRestore(row, amount.text, out string gameText, out var state)) continue;
                    Paint(entry, amount, gameText, state);
                }
        }
        catch (Exception e) { Fault("restore", e); }
        finally
        {
            Memo.Clear();
            _written = null;
        }
    }

    /// <summary>After the window's update: rewrite the governed lines of the hovered recipe.</summary>
    internal static void After(MaterialOverlayStore overlay)
    {
        Last.Clear();
        _lastFrame = Time.frameCount;
        try { _lastState = Apply(overlay); }
        catch (Exception e)
        {
            Fault("apply", e);
            _lastState = "fault: " + e.GetType().Name;
        }
    }

    private static string Apply(MaterialOverlayStore overlay)
    {
        _lastRecipe = 0;
        _lastStation = 0;
        _lastSequence = 0;
        if (!overlay.TryGet(MaterialOperation.RefineView, out _)) return "no RefineView view";
        var world = Worlds.Client;
        if (world == null || !world.IsCreated) return "no client world";
        if (!UnityUiRuntime.TryRefinementRecipeTooltip(world, out var tooltip, out var station, out _, out int recipe, out var requirements)
            || tooltip == null)
            return "no hovered refinement recipe";
        _lastRecipe = recipe;
        _lastStation = MaterialNet.Of(world.EntityManager, station);
        if (!MaterialRefineTooltip.TryTotals(overlay, _lastStation, out var view, out var totals)) return "view is for another station";
        _lastSequence = view.Sequence;
        if (requirements == null) return "recipe requirements unreadable";

        var block = tooltip.RecipeRequiredItems;
        if (block == null || !UnityAlive.Of(block)) return "requirement block missing";
        var list = block.RequiredItemsList;
        if (list == null) return "requirement rows missing";
        int shown = 0;
        int count = list.Count;
        for (int i = 0; i < count; i++)
        {
            var entry = list[i];
            if (entry != null && UnityAlive.Of(entry) && entry.gameObject.activeSelf) shown++;
        }
        if (!MaterialRefineTooltip.LinesMatch(shown, requirements.Length))
            return "lines " + shown + " != requirements " + requirements.Length;

        for (int i = 0; i < requirements.Length && i < count; i++)
        {
            var entry = list[i];
            var amount = Tmp(entry.Amount);
            if (amount == null) continue;
            string current = amount.text;
            var line = MaterialRefineTooltip.Decide(i, requirements[i].ItemGuid, current, totals);
            if (line.Write)
            {
                Paint(entry, amount, line.Text, line.State);
                Memo.Wrote(line, current);
                _written = tooltip;
            }
            Last.Add(line);
        }
        return "applied";
    }

    /// <summary>Write the amount text and the game's color for <paramref name="state"/> on the amount and the name.</summary>
    private static void Paint(WorkstationRequirementEntry entry, TextMeshProUGUI amount, string text, RefineLineState state)
    {
        if (!string.Equals(amount.text, text, StringComparison.Ordinal)) amount.text = text;
        if (!TryColor(state, out var color)) return;
        amount.color = color;
        var name = Tmp(entry.Name);
        if (name != null) name.color = color;
    }

    /// <summary>The game's color for a line state (<c>StunColor</c>); false when the color reads as unset.</summary>
    private static bool TryColor(RefineLineState state, out Color color)
    {
        color = state switch
        {
            RefineLineState.Met => StunColor.Green1,
            RefineLineState.Partial => StunColor.Yellow2,
            _ => StunColor.Red2,
        };
        return color.a > 0f;
    }

    private static string ColorName(RefineLineState state) => state switch
    {
        RefineLineState.Met => "green",
        RefineLineState.Partial => "yellow",
        _ => "red",
    };

    private static TextMeshProUGUI? Tmp(LocalizedText? localized)
    {
        try
        {
            if (localized == null || !UnityAlive.Of(localized)) return null;
            var tmp = localized.Text ?? localized.GetComponent<TextMeshProUGUI>();
            return tmp != null && UnityAlive.Of(tmp) ? tmp : null;
        }
        catch { return null; }
    }

    private static void Fault(string what, Exception e)
    {
        if (_faultLogged) return;
        _faultLogged = true;
        Plugin.Logger?.LogWarning("materials: refinement tooltip counts " + what + " threw (" + e.GetType().Name + ": " + e.Message
            + "); the tooltip keeps the game's own counts where it failed. Further faults are not logged.");
    }

    /// <summary>Drop everything (Core uninstall, disconnect). The game's next tooltip update writes its own text.</summary>
    internal static void Clear()
    {
        try { Before(); } catch { /* best effort */ }
        Memo.Clear();
        _written = null;
        Last.Clear();
        _lastState = "not run";
        _lastFrame = -1;
    }

    /// <summary>
    /// Readback for the development bridge (<c>materials</c> status, client): the last pass's frame, state, recipe,
    /// station and view sequence, and per requirement line the game's count, the provider's count, the shown text
    /// and the color.
    /// </summary>
    internal static Dictionary<string, object?> Describe()
    {
        var lines = new List<Dictionary<string, object?>>();
        foreach (var l in Last)
        {
            var row = new Dictionary<string, object?>
            {
                ["row"] = l.Row,
                ["item"] = l.Item,
                ["need"] = l.Need,
                ["gameHave"] = l.GameHave,
                ["providerHave"] = l.ProviderHave,
                ["shownHave"] = l.ShownHave,
                ["text"] = l.Text,
                ["color"] = ColorName(l.State),
                ["written"] = l.Write,
            };
            if (TryColor(l.State, out var c)) row["rgba"] = new[] { c.r, c.g, c.b, c.a };
            if (l.Note.Length > 0) row["note"] = l.Note;
            lines.Add(row);
        }
        return new Dictionary<string, object?>
        {
            ["frame"] = _lastFrame,
            ["state"] = _lastState,
            ["recipe"] = _lastRecipe,
            ["station"] = _lastStation,
            ["viewSequence"] = _lastSequence,
            ["lines"] = lines,
        };
    }
}
#endif
