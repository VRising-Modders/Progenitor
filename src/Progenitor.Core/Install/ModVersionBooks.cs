using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Networking;

namespace Progenitor.Install;

/// <summary>
/// JOINFLOW4: the live books — the client root's install ledger (<c>BepInEx/progenitor-install.json</c>, paths relative
/// to the game folder) and the server-mods record (<c>BepInEx/config/progenitor-server-mods.txt</c>). Both describe the
/// files that are active, so a parked copy's rows leave with it and come back with it.
/// </summary>
internal sealed class ModVersionBooks : IModVersionBooks
{
    private readonly ModVersionPaths _paths;
    private readonly string _recordPath;
    private readonly Func<DateTime> _now;

    public ModVersionBooks(ModVersionPaths paths, string recordPath, Func<DateTime>? now = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _recordPath = recordPath ?? "";
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>The ledger's spelling of a BepInEx-relative path (<c>BepInEx/progenitor-reload/x.dll</c>).</summary>
    private string LedgerPath(string bepRelPath) =>
        ServerInstallLedger.Relative(_paths.GameRoot, _paths.Absolute(bepRelPath));

    public ParkedBooks Peek(string modId, string bepRelPath, string fileName)
    {
        ParkedLedgerRow? ledger = null;
        ParkedRecordRow? record = null;
        var l = ServerInstallLedger.TryRead(_paths.GameRoot);
        if (l != null)
        {
            string want = LedgerPath(bepRelPath);
            foreach (var f in l.Files)
            {
                if (!string.Equals(f.Path, want, StringComparison.OrdinalIgnoreCase)) continue;
                ledger = new ParkedLedgerRow
                {
                    Path = f.Path,
                    Sha256 = f.Sha256,
                    Size = f.Size,
                    Category = f.Category,
                    Source = f.Source,
                    WrittenUtc = f.WrittenUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                    PluginId = f.PluginId,
                    Route = f.Route,
                    Origin = f.Origin,
                };
            }
        }

        foreach (var r in LegacyServerMods.ReadRecord(_recordPath))
        {
            if (!string.Equals(r.FileName, fileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(r.Id, modId, StringComparison.OrdinalIgnoreCase)) continue;
            record = new ParkedRecordRow
            {
                Sha256 = r.Sha256,
                FileName = r.FileName,
                Id = r.Id,
                Version = r.Version,
                HostKey = r.HostKey,
                Host = r.Host,
                Always = r.Always,
                InstalledUtc = r.InstalledUtc,
            };
        }

        return ledger == null && record == null ? ParkedBooks.None : new ParkedBooks { Ledger = ledger, Record = record };
    }

    public void Remove(string modId, string bepRelPath, string fileName)
    {
        var l = ServerInstallLedger.TryRead(_paths.GameRoot);
        if (l != null)
        {
            string want = LedgerPath(bepRelPath);
            var keep = new List<ServerInstallLedgerEntry>(l.Files.Count);
            foreach (var f in l.Files) if (!string.Equals(f.Path, want, StringComparison.OrdinalIgnoreCase)) keep.Add(f);
            if (keep.Count != l.Files.Count)
                new ServerInstallLedger(l.Version, l.BootstrapSha256, l.CoreSha256, l.InstalledUtc, keep).Write(_paths.GameRoot);
        }

        var rows = new List<InstalledServerMod>(LegacyServerMods.ReadRecord(_recordPath));
        int before = rows.Count;
        rows.RemoveAll(r => string.Equals(r.FileName, fileName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Id, modId, StringComparison.OrdinalIgnoreCase));
        if (rows.Count != before && _recordPath.Length > 0) LegacyServerMods.WriteRecordAt(_recordPath, rows);
    }

    public void Put(string modId, ParkedBooks books, string bepRelPath, string fileName, string sha256, long size, bool pending)
    {
        if (books == null) return;
        if (books.Ledger != null)
        {
            var l = ServerInstallLedger.TryRead(_paths.GameRoot) ?? new ServerInstallLedger("", "", "", _now());
            string category = pending ? ServerInstallLedger.CategoryPending
                : string.Equals(books.Ledger.Category, ServerInstallLedger.CategoryPending, StringComparison.Ordinal) ? ServerInstallLedger.CategoryMod
                : books.Ledger.Category.Length > 0 ? books.Ledger.Category : ServerInstallLedger.CategoryMod;
            l.With(new ServerInstallLedgerEntry(
                LedgerPath(bepRelPath), sha256, size, category, books.Ledger.Source, _now(),
                uninstallPending: false, hot: false, pluginId: books.Ledger.PluginId, route: books.Ledger.Route, origin: books.Ledger.Origin)).Write(_paths.GameRoot);
        }

        if (books.Record != null && _recordPath.Length > 0)
        {
            var rows = new List<InstalledServerMod>(LegacyServerMods.ReadRecord(_recordPath));
            // The record is keyed by mod id: the swapped-in copy's row replaces any row for the id.
            rows.RemoveAll(r => string.Equals(r.Id, modId, StringComparison.OrdinalIgnoreCase));
            var rec = books.Record;
            rows.Add(new InstalledServerMod(modId, rec.Version, fileName, sha256, rec.HostKey, rec.Host, rec.Always,
                rec.InstalledUtc.Length > 0 ? rec.InstalledUtc : _now().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
            LegacyServerMods.WriteRecordAt(_recordPath, rows);
        }
    }
}
