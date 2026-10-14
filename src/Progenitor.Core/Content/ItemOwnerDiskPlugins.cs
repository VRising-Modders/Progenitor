using System;
using System.Collections.Generic;
using System.IO;

namespace Progenitor.Content;

/// <summary>
/// Whether an item owner's plugin is absent from disk, for automatic item removal. Reads assembly
/// metadata without loading it; an unreadable or staged DLL, a link or too many files conservatively
/// counts as present.
///
/// <para>One instance is one look at the disk: the plugin folders are walked at most once and each
/// provenance file is checked at most once, however many owners are asked. The removal tick makes
/// one per tick; asking per owner re-read every DLL and stat-ed every row (two file checks for each
/// of 128 rows sharing one <c>pack.json</c>) for each of 80+ owners.</para>
/// </summary>
internal sealed class ItemOwnerDiskPlugins
{
    internal const int MaxFiles = 4096;
    internal static readonly string[] Folders = { "plugins", "progenitor-reload", "progenitor-pending" };

    private readonly string _root;
    private readonly Func<string, IReadOnlyList<string>?> _readGuids;
    private readonly Dictionary<string, bool> _fileExists = new(StringComparer.Ordinal);
    private HashSet<string>? _declared;
    private bool _walked;
    private bool _blocked;

    /// <summary>How many times the plugin folders were walked (tests).</summary>
    internal int Walks { get; private set; }

    /// <param name="readGuids">The GUIDs a DLL declares, or null when it cannot be read.</param>
    internal ItemOwnerDiskPlugins(string root, Func<string, IReadOnlyList<string>?>? readGuids = null)
    {
        _root = root;
        _readGuids = readGuids ?? DefaultReadGuids;
    }

    private static IReadOnlyList<string>? DefaultReadGuids(string file) =>
        PluginActions.PluginFileActions.TryReadPluginGuids(file, out var ids) ? ids : null;

    /// <summary>
    /// True when no provenance file of <paramref name="owner"/> (or its staged <c>.next</c>) exists and
    /// no readable DLL under the plugin folders declares it.
    /// </summary>
    internal bool Absent(ItemOwnerLedger ledger, string owner)
    {
        try
        {
            foreach (var row in ledger.RowsOf(owner))
                if (Exists(row.AssemblyRelativePath)) return false;
            Walk();
            return !_blocked && !_declared!.Contains(owner);
        }
        catch { return false; }
    }

    private bool Exists(string relative)
    {
        if (_fileExists.TryGetValue(relative, out bool exists)) return exists;
        var path = Path.Combine(_root, relative);
        exists = File.Exists(path) || File.Exists(path + ".next");
        _fileExists[relative] = exists;
        return exists;
    }

    private void Walk()
    {
        if (_walked) return;
        _walked = true;
        Walks++;
        _declared = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            int count = 0;
            foreach (var folder in Folders)
            {
                var path = Path.Combine(_root, folder);
                if (!Directory.Exists(path)) continue;
                var pending = new Stack<string>(); pending.Push(path);
                while (pending.Count != 0)
                {
                    var current = pending.Pop();
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) { _blocked = true; return; }
                    foreach (var child in Directory.GetDirectories(current)) pending.Push(child);
                    foreach (var file in Directory.GetFiles(current, "*.dll*"))
                    {
                        if (++count > MaxFiles) { _blocked = true; return; }
                        var ids = _readGuids(file);
                        if (ids == null) { _blocked = true; return; }
                        foreach (var id in ids) _declared.Add(id);
                    }
                }
            }
        }
        catch { _blocked = true; }
    }
}
