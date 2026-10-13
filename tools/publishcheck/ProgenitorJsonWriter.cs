using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Progenitor.Install.Admission;
using Progenitor.Networking;

namespace Progenitor.PublishCheck;

/// <summary>
/// Writes a package's <c>progenitor.json</c> from the plugin DLL's own attributes, so the file can never disagree with
/// the image: <c>id</c> and <c>version</c> from <c>[BepInPlugin]</c>, <c>side</c> from <c>[ProgenitorSide]</c> (both when
/// undeclared), <c>compatibility.peers</c> from <c>[NetworkCompatibility]</c>'s <c>Peers</c>, and <c>sha256</c> of the DLL
/// bytes. Only <c>hotLoadable</c> is the author's call, and it is refused for an image the Install tab would not load hot.
/// Error texts name the options of <c>tools/publish-check.ps1</c>, the documented way to run this.
/// </summary>
internal static class ProgenitorJsonWriter
{
    /// <summary>The JSON text, or null with <paramref name="error"/> set.</summary>
    public static string? Build(byte[] dll, bool hotLoadable, string? pluginId, out string? error)
    {
        error = null;
        var facts = ImageFacts.Read(dll);
        if (!facts.IsManagedAssembly)
        {
            error = "the file is not a managed assembly";
            return null;
        }

        var candidates = facts.Plugins
            .Where(p => string.IsNullOrEmpty(pluginId) || string.Equals(p.Guid, pluginId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
        {
            error = string.IsNullOrEmpty(pluginId) ? "no type declares [BepInPlugin]" : "no [BepInPlugin] has the GUID '" + pluginId + "'";
            return null;
        }

        if (candidates.Count > 1)
        {
            error = "more than one [BepInPlugin] (" + string.Join(", ", candidates.Select(c => c.Guid)) + "); pass -Id <guid>";
            return null;
        }

        var entry = candidates[0];
        if (entry.SideText == "unknown")
        {
            error = "[ProgenitorSide] carries an unknown value (" + entry.SideRaw + ")";
            return null;
        }

        if (!ModManifestIds.TryPluginFileName(entry.Guid, ".dll", out _, out var idError))
        {
            error = "the [BepInPlugin] GUID '" + entry.Guid + "' cannot be installed by Progenitor: " + idError;
            return null;
        }

        // The Install tab's own rule (HotImageCheck, shared with HotLoadTrust.CleanProgenitorImage).
        if (hotLoadable && !HotImageCheck.IsClean(dll, out var why))
        {
            error = "the Install tab would not load this image hot (" + why + "); remove the cause or run without -HotLoadable";
            return null;
        }

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("id", entry.Guid);
            w.WriteString("version", entry.Version);
            w.WriteString("side", entry.SideText);
            w.WriteBoolean("hotLoadable", hotLoadable);
            w.WriteString("sha256", PublishChecker.Sha256(dll));
            if (entry.Peers.Length > 0)
            {
                w.WriteStartObject("compatibility");
                w.WriteString("peers", entry.Peers);
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    /// <summary>Writes UTF-8 without a byte-order mark.</summary>
    public static void Write(string path, string json) => File.WriteAllText(path, json, new UTF8Encoding(false));
}
