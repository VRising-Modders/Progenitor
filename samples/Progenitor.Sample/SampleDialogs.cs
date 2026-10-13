using System;
using BepInEx.Configuration;
using Progenitor.Plugins;
using Progenitor.Ui;

namespace Progenitor.Sample;

/// <summary>
/// DIALOGS1: one working example of each <c>ctx.Dialogs</c> kind, opened from the Progenitor Sample's
/// settings page (Mods workspace → Progenitor Sample → Dialogs). Nothing here styles anything: the
/// title, body, centered equal-width buttons (main action on the right) and the
/// <c>Progenitor Sample &lt;version&gt; · Progenitor &lt;version&gt;</c> footer all come from the
/// framework. Each result is written to the Sample's log.
/// </summary>
internal static class SampleDialogs
{
    /// <summary>
    /// A destructive confirmation that does what it says: Yes clears the Sample's Notes setting (the
    /// multi-line text on the Personal section). Enter does not answer it; the button needs a click.
    /// </summary>
    internal static void Confirm(PluginContext ctx, ConfigEntry<string> notes) =>
        ctx.Dialogs.Confirm(
            "Clear the Sample's notes?",
            "This empties the Notes setting on the Personal section. The old text is not kept.",
            "Clear notes",
            () =>
            {
                notes.Value = "";
                ctx.Log.LogInfo("Sample dialogs: confirm -> yes; Notes cleared.");
            },
            no: "Keep them",
            onNo: () => ctx.Log.LogInfo("Sample dialogs: confirm -> no."),
            danger: true);

    internal static void Notice(PluginContext ctx) =>
        ctx.Dialogs.Notice(
            "Welcome to the Sample",
            "Every dialog here is the framework's own layout. The footer names this mod and its version.",
            onClose: () => ctx.Log.LogInfo("Sample dialogs: notice closed."));

    internal static void Choose(PluginContext ctx)
    {
        // Three short options: buttons on the button row. Pass more (or longer) and they become a list.
        var options = new[] { "Wolf", "Bear", "Bat" };
        ctx.Dialogs.Choose(
            "Pick a form",
            "Just a demonstration; nothing changes in the game.",
            options,
            index => ctx.Log.LogInfo("Sample dialogs: choose -> " + options[index] + "."),
            onCancel: () => ctx.Log.LogInfo("Sample dialogs: choose canceled."));
    }

    internal static void Prompt(PluginContext ctx) =>
        ctx.Dialogs.Prompt(
            "Name your camp",
            "Up to 24 characters. Letters, digits and spaces.",
            "",
            name => ctx.Log.LogInfo("Sample dialogs: prompt -> '" + name + "'."),
            placeholder: "Riverside",
            validate: Validate,
            submit: "Save",
            maxLength: 24,
            onCancel: () => ctx.Log.LogInfo("Sample dialogs: prompt canceled."));

    /// <summary>
    /// DIALOGS2: a confirmation whose content is label/value facts — the labels muted in a column as
    /// wide as the widest one, the values bright, a second <c>Requires</c> line continuing the first
    /// (empty label), and one short note under them. Nothing changes in the game.
    /// </summary>
    internal static void Facts(PluginContext ctx) =>
        ctx.Dialogs.Confirm(
            "Plant the herb garden?",
            new[]
            {
                new DialogFact("Plot", "Castle courtyard, north wall"),
                new DialogFact("Seeds", "Sunflower × 4"),
                new DialogFact("Requires", "Gardening table — built", DialogFactTone.Ok),
                new DialogFact("", "Water barrel — missing", DialogFactTone.Danger),
                new DialogFact("Takes effect", "At the next sunrise"),
            },
            "Plant",
            () => ctx.Log.LogInfo("Sample dialogs: facts -> yes."),
            note: "Just a demonstration; nothing is planted.",
            onNo: () => ctx.Log.LogInfo("Sample dialogs: facts -> no."));

    /// <summary>The Prompt's validator: an error to show under the field, or null to accept.</summary>
    internal static string? Validate(string text)
    {
        string name = (text ?? "").Trim();
        if (name.Length == 0) return "Enter a name.";
        foreach (char c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != ' ') return "Use letters, digits and spaces only.";
        }

        return null;
    }
}
