using System;
using System.Collections.Generic;
using System.IO;

namespace Progenitor.Install;

/// <summary>
/// AUTOUPDATE2 (shared by the client and, since AUTOUPDATE3, the server): judge each restart update queued for the next start
/// after that start, only on the exact image it queued (review H2). The queued checks are the <c>verify</c> entries of the side's
/// <c>progenitor-auto-update.json</c>, written when the update was queued (before any restart), so they are also the server's apply
/// journal for restart updates.
/// <list type="bullet">
/// <item>still queued with that image: wait for a later start; queued with another image, or the copy on disk is another image
/// (Roll back, Cancel install, another install): dropped silently, nothing rolled back or listed;</item>
/// <item>loaded (<see cref="LoadCheck"/>): done; its load threw: rolled back and entered in the failed list; not loaded with no
/// failure seen: rolled back, tried again next session (not listed); nothing can tell (an older Bootstrap and no reloader
/// record): dropped as unverified.</item>
/// </list>
/// Game-free: the store, the file and the load probe are passed in.
/// </summary>
internal static class UpdateStartChecks
{
    internal sealed class Result
    {
        public List<string> Lines { get; } = new();

        /// <summary>The updates rolled back (each with <see cref="PreparedUpdate.LoadFailed"/> when its load threw).</summary>
        public List<PreparedUpdate> Rolled { get; } = new();

        /// <summary>The updates confirmed loaded after the start.</summary>
        public List<PreparedUpdate> Loaded { get; } = new();
    }

    /// <param name="svc">The version store, or null (nothing can be rolled back).</param>
    /// <param name="file">The side's auto-update file as read now.</param>
    /// <param name="edit">Change the file under its lock (true: write it).</param>
    /// <param name="loadFacts">What can be read about a plugin's load (plugin id, file stem).</param>
    /// <param name="log">One line per rollback, for the side's log.</param>
    /// <param name="logPrefix">The log line's prefix (<c>AutoUpdate: </c>).</param>
    public static Result Run(ModVersionService? svc, UpdatePins file, Func<Func<UpdatePins, bool>, bool> edit,
        Func<string, string, LoadFacts>? loadFacts, Action<string>? log, string logPrefix)
    {
        var result = new Result();
        if (file == null || file.Verify.Count == 0) return result;
        foreach (var kv in new List<KeyValuePair<string, StartCheck>>(file.Verify))
        {
            var c = kv.Value;
            var entry = svc?.Manifest().Find(c.PluginId);
            if (entry?.Pending != null)
            {
                if (SameSha(entry.Pending.Sha256, c.Sha256))
                {
                    result.Lines.Add(kv.Key + " " + c.Version + ": still queued for the next start");
                    continue;
                }

                Drop(edit, kv.Key, kv.Key + " " + c.Version + ": the queued copy changed; not judged", result.Lines);
                continue;
            }

            string onDisk = DiskShaOf(svc, c);
            if (onDisk.Length > 0 && !SameSha(onDisk, c.Sha256))
            {
                Drop(edit, kv.Key, kv.Key + " " + c.Version + ": another copy of it is in place; not judged", result.Lines);
                continue;
            }

            LoadFacts facts;
            try { facts = loadFacts?.Invoke(c.PluginId, Path.GetFileNameWithoutExtension(c.File)) ?? new LoadFacts(); }
            catch { facts = new LoadFacts(); }
            var (verdict, detail) = LoadCheck.Judge(facts, c.Sha256, c.Utc);
            if (verdict == LoadVerdict.Loaded)
            {
                Drop(edit, kv.Key, kv.Key + " " + c.Version + ": loaded after the restart", result.Lines);
                result.Loaded.Add(new PreparedUpdate { Package = kv.Key, Name = kv.Key, To = c.Version, Previous = c.Previous, Outcome = "loaded" });
                continue;
            }

            if (verdict == LoadVerdict.Unknown)
            {
                Drop(edit, kv.Key, kv.Key + " " + c.Version + ": not checked (" + detail + ")", result.Lines);
                continue;
            }

            if (verdict == LoadVerdict.Waiting && onDisk.Length == 0)
            {
                Drop(edit, kv.Key, kv.Key + " " + c.Version + ": no copy of it is in place; not judged", result.Lines);
                continue;
            }

            bool failed = verdict == LoadVerdict.Failed;
            string why = failed ? detail : "it did not load after the restart (" + detail + ")";
            string back = "no previous version is kept";
            if (svc != null && c.Previous.Length > 0)
            {
                var r = StoreUpdate.RollBack(svc, c.PluginId, c.Previous, forget: failed ? c.Version : "");
                back = r.Ok ? (r.Route == "restart" ? "rolled back to " + c.Previous + " for the next start" : "rolled back to " + c.Previous) : "rolling back failed: " + r.Message;
            }

            edit(f =>
            {
                f.Verify.Remove(kv.Key);
                if (failed) f.AddFailed(kv.Key, c.Version, "failed to load: " + why, DateTime.UtcNow);
                return true;
            });
            result.Lines.Add(kv.Key + " " + c.Version + ": " + why + "; " + back);
            result.Rolled.Add(new PreparedUpdate
            {
                Package = kv.Key, Name = kv.Key, To = c.Version, Previous = c.Previous, Outcome = "failed", RolledBack = true, LoadFailed = failed,
                OutcomeDetail = why + "; " + back,
            });
            try
            {
                log?.Invoke(logPrefix + "op=rollback by=start package=" + kv.Key + " from=" + c.Version + " to=" + c.Previous + " why=\"" + why + "\" result=\""
                    + back + "\"" + (failed ? "" : " (not listed as failed)"));
            }
            catch { /* */ }
        }

        return result;
    }

    private static void Drop(Func<Func<UpdatePins, bool>, bool> edit, string key, string line, List<string> lines)
    {
        edit(f => f.Verify.Remove(key));
        lines.Add(line);
    }

    internal static bool SameSha(string a, string b) => a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The sha256 of the plugin's copy in progenitor-reload now (the store's active copy), or empty when there is none.</summary>
    private static string DiskShaOf(ModVersionService? svc, StartCheck c)
    {
        if (svc == null) return "";
        try
        {
            var active = svc.ActiveCopy(c.PluginId, out _, out _);
            return active == null ? "" : ModVersionFiles.ShaOf(active.Path) ?? "";
        }
        catch { return ""; }
    }
}
