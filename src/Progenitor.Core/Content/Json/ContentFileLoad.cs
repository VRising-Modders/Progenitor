using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Content.Json;

/// <summary>
/// Turns a folder's worth of parsed content files into registry calls, through
/// <see cref="IContentFileSink"/>. Game-free, so the whole decision table — which file is
/// refused, what the log says, what reaches the builder — is covered by tests that never open a
/// world.
/// </summary>
internal static class ContentFileLoad
{
    /// <summary>Prefix on every line this feature writes, so one grep finds the whole load.</summary>
    public const string LogPrefix = "Content files: ";

    /// <summary>
    /// Validate and register every file, in the order given. One file's refusal never stops
    /// another file; within a file it is all or nothing (see <see cref="ContentFileParser"/>).
    /// </summary>
    public static ContentFileLoadResult Run(IReadOnlyList<ContentFileSource> files, IContentFileSink sink)
    {
        if (files == null) throw new ArgumentNullException(nameof(files));
        if (sink == null) throw new ArgumentNullException(nameof(sink));

        var info = new List<string>();
        var errors = new List<string>();
        int accepted = 0;
        int refused = 0;
        int defined = 0;
        // Ids this load has actually registered. Two files in one folder claiming the same id is
        // the same mistake as a file claiming a C# id, and the second file is the one refused.
        var definedThisLoad = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (!ContentFileParser.TryParse(file.Label, file.Json, out var document, out var problems))
            {
                refused++;
                Refuse(errors, file.Label, problems);
                continue;
            }

            // The collision check is a second validation pass rather than a check inside the
            // registration loop, because a file that redefines a compiled id must register
            // none of its items rather than the ones that happened to sort first.
            var collisions = new List<string>();
            var items = document!.Items;
            for (int j = 0; j < items.Count; j++)
            {
                var spec = items[j];
                bool known;
                try
                {
                    known = sink.IsDefined(spec.Id);
                }
                catch (Exception e)
                {
                    collisions.Add(
                        $"{file.Label}: item {spec.Index.ToString(CultureInfo.InvariantCulture)} ('{spec.Id}'): "
                        + $"could not be checked against this plugin's existing definitions: {e.Message}");
                    continue;
                }

                if (known || definedThisLoad.Contains(spec.Id))
                {
                    collisions.Add(
                        $"{file.Label}: item {spec.Index.ToString(CultureInfo.InvariantCulture)} ('{spec.Id}'): "
                        + "this plugin already defines that id; a data file never replaces a definition "
                        + "the plugin made in C#.");
                }
            }

            if (collisions.Count > 0)
            {
                refused++;
                Refuse(errors, file.Label, collisions);
                continue;
            }

            int made = 0;
            for (int j = 0; j < items.Count; j++)
            {
                var spec = items[j];
                try
                {
                    sink.DefineItem(spec);
                    definedThisLoad.Add(spec.Id);
                    made++;
                }
                catch (Exception e)
                {
                    errors.Add(
                        $"{LogPrefix}{file.Label}: item {spec.Index.ToString(CultureInfo.InvariantCulture)} "
                        + $"('{spec.Id}') was refused by the registry: {e.Message}");
                }
            }

            accepted++;
            defined += made;
            info.Add($"{LogPrefix}{file.Label}: {made.ToString(CultureInfo.InvariantCulture)} item(s) defined");
        }

        return new ContentFileLoadResult(info, errors, accepted, refused, defined);
    }

    private static void Refuse(List<string> errors, string label, IReadOnlyList<string> problems)
    {
        errors.Add(
            $"{LogPrefix}{label} refused; nothing in it was registered "
            + $"({problems.Count.ToString(CultureInfo.InvariantCulture)} problem(s)):");
        for (int i = 0; i < problems.Count; i++)
            errors.Add($"{LogPrefix}  {problems[i]}");
    }
}
