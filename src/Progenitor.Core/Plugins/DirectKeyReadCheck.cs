using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
#if !PROGENITOR_TESTS
using System.Reflection;
using BepInEx.Logging;
#endif

namespace Progenitor.Plugins;

/// <summary>
/// Load-time guidance for the keybind rule (docs/modules.md "Keybinds"): a plugin whose own
/// assembly references a direct key or device read — <c>UnityEngine.Input.GetKey*</c> and
/// friends, <c>Keyboard.current</c> / <c>Mouse.current</c>, Input System device polling, IMGUI
/// <c>Event.current</c>, or the game's <c>IsInputPressed</c>/<c>Down</c>/<c>Up</c> — gets one
/// warning line and an informational Mods-page problem. Its keys bypass <c>ctx.Keybinds</c>, so
/// they cannot be remapped or checked for conflicts.
///
/// <para>
/// Metadata only: the check walks the MemberRef table of the plugin's loaded image (the
/// rewritten one, when the compat rewriter applied) with <see cref="MetadataReader"/>, so it costs
/// one pass over the references and never resolves a type or runs plugin code. It reports a
/// reference, not a call that runs: dead code still counts, and so does a helper method nobody
/// calls. It sees only the plugin's own assembly — a key read inside a separate helper DLL the
/// plugin ships is not found — and it runs only for <c>ProgenitorPlugin&lt;TContext&gt;</c>
/// plugins. It is guidance, never a refusal: the plugin loads either way.
/// </para>
/// </summary>
internal static class DirectKeyReadCheck
{
    /// <summary>The Mods-page problem kind.</summary>
    internal const string Kind = "direct-key-read";

    internal const string Sentence = "Reads keys directly; its keys can't be remapped or checked for conflicts.";

    internal const string WhatToDo = "Ask its author to use ctx.Keybinds.";

    private static readonly HashSet<string> LegacyInputMembers = new(StringComparer.Ordinal)
    {
        "GetKey", "GetKeyDown", "GetKeyUp",
        "GetButton", "GetButtonDown", "GetButtonUp",
        "GetMouseButton", "GetMouseButtonDown", "GetMouseButtonUp",
        "get_anyKey", "get_anyKeyDown", "get_inputString",
    };

    private static readonly HashSet<string> InputSystemMembers = new(StringComparer.Ordinal)
    {
        "get_devices", "GetDevice", "add_onEvent", "get_onEvent", "get_onAnyButtonPress",
    };

    private static readonly HashSet<string> GameInputMembers = new(StringComparer.Ordinal)
    {
        "IsInputPressed", "IsInputDown", "IsInputUp",
    };

    /// <summary>True when a member reference on <paramref name="ns"/>.<paramref name="type"/> is a direct read.</summary>
    internal static bool IsDirectRead(string ns, string type, string member)
    {
        switch (ns)
        {
            case "UnityEngine":
                if (type == "Input") return LegacyInputMembers.Contains(member);
                return type == "Event" && member == "get_current";
            case "UnityEngine.InputSystem":
                if (type == "Keyboard" || type == "Mouse") return member == "get_current";
                return type == "InputSystem" && InputSystemMembers.Contains(member);
        }

        return ns.StartsWith("ProjectM", StringComparison.Ordinal) && GameInputMembers.Contains(member);
    }

    /// <summary>Every direct read the metadata references, as sorted distinct <c>Namespace.Type::member</c>.</summary>
    internal static IReadOnlyList<string> Find(MetadataReader reader)
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var handle in reader.MemberReferences)
        {
            var member = reader.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
            string ns = reader.GetString(type.Namespace);
            string name = reader.GetString(type.Name);
            string memberName = reader.GetString(member.Name);
            if (IsDirectRead(ns, name, memberName)) found.Add(ns + "." + name + "::" + memberName);
        }

        return new List<string>(found);
    }

    /// <summary>The same over a PE image on disk or in memory. An image without metadata has none.</summary>
    internal static IReadOnlyList<string> Find(byte[] image)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        using var pe = new PEReader(new MemoryStream(image, writable: false));
        if (!pe.HasMetadata) return Array.Empty<string>();
        return Find(pe.GetMetadataReader());
    }

    /// <summary>The warning line for <paramref name="pluginName"/>.</summary>
    internal static string WarningLine(string pluginName, IReadOnlyList<string> hits) =>
        pluginName + " reads keys directly (" + string.Join(", ", hits) + "). Those keys bypass "
        + "ctx.Keybinds: they are not listed in the Keybinds tab, cannot be remapped and take no part "
        + "in conflict resolution. Register them with ctx.Keybinds.Register instead.";

#if !PROGENITOR_TESTS
    /// <summary>
    /// Check one loaded plugin assembly. Logs one warning and reports an informational problem
    /// when it reads keys directly; clears an earlier generation's problem when it no longer does.
    /// Never throws.
    /// </summary>
    internal static unsafe void Run(Assembly assembly, string guid, string pluginName, ManualLogSource log)
    {
        try
        {
            if (!assembly.TryGetRawMetadata(out byte* blob, out int length)) return;
            var hits = Find(new MetadataReader(blob, length));
            if (hits.Count == 0)
            {
                Progenitor.Ui.ModProblems.Clear(guid, Kind);
                return;
            }

            log.LogWarning(WarningLine(pluginName, hits));
            Progenitor.Ui.ModProblems.Report(new Progenitor.Ui.ModProblem(
                Progenitor.Ui.ModProblemSeverity.Info, Kind, guid, Sentence, WhatToDo,
                details: string.Join("\n", hits)));
        }
        catch (Exception e)
        {
            log.LogDebug(pluginName + ": the direct key read check could not run: " + e.Message);
        }
    }
#endif
}
