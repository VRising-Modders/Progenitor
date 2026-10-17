using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;

namespace Progenitor.Reload;

// MIRROR of src/Progenitor.Bootstrap/ReloadAllowStore.cs (same reason as ReloadAllowPolicy.cs:
// the two reloaders cannot reference each other). Only the log sink differs.

/// <summary>
/// Reads and caches <c>BepInEx/config/progenitor-allow.txt</c> for every automatic
/// code-loading path Bootstrap owns (job FRLD). The file is optional: when it is absent
/// nothing changes and load lines read <c>allowlist=none</c>.
///
/// Re-read whenever (exists, length, write time) changes, so an operator can edit the file
/// while the game runs without a restart. A read that throws is treated as
/// <see cref="AllowListStatus.Malformed"/> — fail closed, never fail open, because the
/// alternative is "an unreadable allowlist silently disables the allowlist".
///
/// Decisions and formatting live in <see cref="ReloadAllowPolicy"/> (game-free, unit tested).
/// </summary>
internal static class ReloadAllowStore
{
    private static readonly object Gate = new();
    private static ReloadAllowList _list = ReloadAllowPolicy.Absent();
    private static bool _haveStamp;
    private static bool _lastExists;
    private static long _lastLength;
    private static DateTime _lastWrite;
    private static string _lastLogged = "";
    private static string _pathError = "";

    /// <summary>
    /// Path of the allowlist, or "" when BepInEx's config folder cannot be resolved into an
    /// absolute path. R31 finding 27: a null <c>Paths.ConfigPath</c> threw and was already
    /// closed, but an <em>empty</em> one combined into a working-directory-relative name that
    /// simply did not exist, so the store published Absent and every load line read
    /// <c>allowlist=none</c>. <see cref="ReloadAllowPolicy.TryConfigPath"/> owns the rule now:
    /// anything that is not an absolute path is an unresolved path, and an unresolved path is
    /// Malformed like every other unreadable state.
    /// </summary>
    internal static string Path
    {
        get
        {
            try
            {
                if (!ReloadAllowPolicy.TryConfigPath(Paths.ConfigPath, out string p, out string why))
                {
                    _pathError = why;
                    return "";
                }
                _pathError = "";
                return p;
            }
            catch (Exception e)
            {
                _pathError = e.GetType().Name + ": " + e.Message;
                return "";
            }
        }
    }

    /// <summary>
    /// Current allowlist, re-read if the file changed since the last call. Never throws:
    /// an I/O failure comes back Malformed so callers refuse rather than load.
    /// </summary>
    internal static ReloadAllowList Current()
    {
        lock (Gate)
        {
            string path = Path;
            if (path.Length == 0)
            {
                // R30 finding 13: this used to be Absent -- "there is no allowlist" -- which is
                // the one way an unreadable allowlist could silently fail OPEN, exactly what the
                // rule above forbids. A path we cannot resolve is Malformed like every other
                // unreadable state, and Publish logs it once.
                _haveStamp = false;
                return Publish(ReloadAllowPolicy.UnresolvedPath(_pathError));
            }

            bool exists;
            long length = 0;
            DateTime write = default;
            try
            {
                var info = new FileInfo(path);
                exists = info.Exists;
                if (exists)
                {
                    length = info.Length;
                    write = info.LastWriteTimeUtc;
                }
            }
            catch (Exception e)
            {
                return Publish(ParseFailure("cannot stat the file: " + e.Message));
            }

            if (_haveStamp && exists == _lastExists && length == _lastLength && write == _lastWrite)
                return _list;

            _haveStamp = true;
            _lastExists = exists;
            _lastLength = length;
            _lastWrite = write;

            if (!exists)
                return Publish(ReloadAllowPolicy.Absent());

            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception e)
            {
                // Leave the stamp unset so a transient lock is retried on the next scan.
                _haveStamp = false;
                return Publish(ParseFailure("cannot read the file: " + e.Message));
            }

            return Publish(ReloadAllowPolicy.Parse(text));
        }
    }

    /// <summary>Verdict for one file's bytes, plus the line that names it.</summary>
    internal static AllowDecision Decide(string fileName, string sha256Hex) =>
        Current().Decide(fileName, sha256Hex);

    /// <summary>
    /// Add or remove one <c>sha256  filename</c> row in the allowlist at <paramref name="path"/>
    /// (job FMAN3: a package the player chose to keep from a server needs a row, and removing that
    /// package must take the row with it). Game-free on purpose — the path is a parameter, so
    /// nothing in this method touches BepInEx and both mirrors behave identically.
    ///
    /// <para>Fail closed in three places. A hash that is not 64 hex, or a name that is not a row
    /// name under <see cref="ReloadAllowPolicy.RowGrammar"/>, is refused before anything is read.
    /// An existing file that does not <em>parse</em> is refused too: appending to a malformed
    /// allowlist would leave the operator with a file that still fails closed while looking as if
    /// the row had been written. And the write is temp + move, so a torn write cannot turn a good
    /// allowlist into a malformed one.</para>
    ///
    /// <para>Adding is idempotent (an identical row is not duplicated) and returns true.
    /// Removing returns true only when a row actually went away, so the caller can log the
    /// difference between "removed" and "there was nothing to remove".</para>
    ///
    /// <para><b>This method never creates the file</b> (job FMAN4, R35 finding 2). The allowlist
    /// is not per-file opt-in; it is a whole-install mode switch. While it is
    /// <see cref="AllowListStatus.Absent"/> every image loads as before, and the moment it exists
    /// everything not on it is <see cref="AllowDecision.Unlisted"/> — the player's own plugins, the
    /// Sample, and <c>Progenitor.dll</c> itself, which <c>CoreLoader</c> then refuses at the next
    /// start. Writing one row into a file that did not exist therefore disabled the framework that
    /// owns the only button which could undo it. Creating an allowlist is
    /// <see cref="TryCreateAllowList"/>, which reads Core's own image off disk and refuses any
    /// starting content that does not already list it.</para>
    /// </summary>
    internal static bool TryWriteAllowRow(string path, string sha256Hex, string fileName, bool add, out string error)
    {
        error = "";
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                error = "the allowlist path is empty";
                return false;
            }

            string hex = (sha256Hex ?? "").Trim().ToLowerInvariant();
            if (hex.Length != ReloadAllowPolicy.HashLength)
            {
                error = "expected " + ReloadAllowPolicy.HashLength + " hex sha256 characters";
                return false;
            }

            if (!ReloadAllowPolicy.TryRowName(fileName, out string name, out error))
                return false;

            bool exists = File.Exists(path);
            if (!exists)
            {
                error = ReloadAllowPolicy.RefusedToCreate;
                return false;
            }

            string text = File.ReadAllText(path);
            if (ReloadAllowPolicy.Parse(text).Status == AllowListStatus.Malformed)
            {
                error = ReloadAllowPolicy.Parse(text).Error;
                return false;
            }

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var kept = new System.Text.StringBuilder();
            bool found = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (i == lines.Length - 1 && line.Trim().Length == 0) continue;
                if (IsAllowRowFor(line, hex, name))
                {
                    found = true;
                    continue;
                }

                kept.Append(line).Append('\n');
            }

            if (!add && !found) return false;
            if (add) kept.Append(ReloadAllowPolicy.FormatSumsLine(hex, name)).Append('\n');

            // System.IO.Path, not this class's own Path property.
            string dir = System.IO.Path.GetDirectoryName(path) ?? "";
            if (dir.Length > 0) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, kept.ToString(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            try
            {
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                File.Copy(tmp, path, overwrite: true);
                try { File.Delete(tmp); }
                catch { /* leftover tmp is ignored on the next read */ }
            }

            return true;
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            return false;
        }
    }

    /// <summary>
    /// Create <c>progenitor-allow.txt</c> from a complete set of rows. The only writer that may
    /// bring the file into existence, and it refuses unless <paramref name="rows"/> already
    /// contains Core's own current image — <b>the image this method reads and hashes itself</b>,
    /// at <paramref name="coreImagePath"/>.
    ///
    /// <para>Belt and braces for R35 finding 2, made real by R36 finding 3. The check used to take
    /// the hash and the file name as parameters, so it verified only that the caller was
    /// self-consistent: a caller that hashed the Core it was <em>about to stage</em>, or
    /// <c>Progenitor.dll.next</c>, or a string, got a file listing a <c>Progenitor.dll</c> that
    /// does not exist — the precise state the method claims to make impossible, reached through
    /// the method written to prevent it. The caller now names the file and nothing else; the
    /// writer opens it, hashes those bytes, and requires a row for that hash under that leaf name.
    /// Bootstrap's copy of Core's live path is <c>CoreLayout.CorePath</c>; Core's own image is the
    /// <c>Progenitor.dll</c> beside the assembly this code is running from.</para>
    ///
    /// <para>The failure it guards against is severe and silent: Bootstrap refuses
    /// <c>Progenitor.dll</c> on the next start, so the settings panel that would undo the mistake
    /// never exists. An allowlist that does not carry Core is not an allowlist; it is an uninstall
    /// with extra steps.</para>
    ///
    /// <para><b>The name comes from the image too (R37 finding 3).</b> The hash stopped being a
    /// parameter; the leaf name did not, and <c>ReloadAllowPolicy.TryRowName</c> accepts any leaf
    /// — so the very caller the paragraph above names, one that passes
    /// <c>Progenitor.dll.next</c>, produced a file whose only Core-ish row was
    /// <c>&lt;hash&gt;  Progenitor.dll.next</c>. That satisfies the check and carries no row for
    /// <c>Progenitor.dll</c>, which is the row <c>CoreLoader</c> looks for: the brick this method
    /// exists to prevent, reached through the method. <see cref="TryCoreRowName"/> now derives the
    /// row name from the image that was hashed — staging and rotation suffixes
    /// (<c>.next</c>, <c>.prev</c>, <c>.promote.tmp</c>, <c>.last-good.tmp</c>, <c>.tmp</c>)
    /// normalized away to the live leaf — and refuses outright any path that is not a Core image.
    /// A staged <c>.next</c> is therefore listed under the name it will have when it is live —
    /// <b>and only when it already is</b> (R38 finding 7): naming a staged image asserts a promote
    /// that may still be refused, quarantined or rotated back, so the writer requires the live
    /// <c>Progenitor.dll</c> beside it to exist and hash identically, and refuses with the two
    /// hashes when it does not. The row can never name a Core that is not on disk.</para>
    ///
    /// <para>Refuses an existing file outright: this is creation, not an append. Use
    /// <see cref="TryWriteAllowRow"/> for that.</para>
    /// </summary>
    /// <summary>Core's own image leaf name — Bootstrap's <c>CoreLayout.FileName</c>, spelled here so both mirrors stay one file.</summary>
    internal const string CoreImageFileName = "Progenitor.dll";

    /// <summary>Core's image leaf in the single-folder install (<c>InstallLayout.CoreFileName</c>).</summary>
    internal const string SingleFolderCoreImageFileName = "Progenitor.core";

    /// <summary>
    /// Suffixes a Core image on disk carries while it is being staged or rotated
    /// (<c>CoreWatchPolicy.NextSuffix</c>, <c>CoreImagePolicy.PrevSuffix</c> /
    /// <c>PromoteTempSuffix</c> / <c>LastGoodTempSuffix</c>, and the bare <c>.tmp</c> the atomic
    /// writes use). Stripped repeatedly, longest form first, so <c>Progenitor.dll.next</c> and
    /// <c>Progenitor.dll.last-good.tmp</c> both normalize to <c>Progenitor.dll</c>.
    /// </summary>
    private static readonly string[] CoreImageSuffixes =
        { ".last-good.tmp", ".promote.tmp", ".next", ".prev", ".tmp" };

    /// <summary>
    /// The allowlist row name for the image at <paramref name="imagePath"/>: its leaf with every
    /// staging suffix removed, and only if what remains is Core's own image name. R37 finding 3 —
    /// a row must name the file <c>CoreLoader</c> will look up on the next start, which is always
    /// <see cref="CoreImageFileName"/>, never the transient name the bytes happen to sit under.
    ///
    /// <para><b>Staged bytes are only listable once the live file agrees (R38 finding 7).</b> R37's
    /// fix wrote the hash of whatever it was handed under the live name, which asserts on the
    /// caller's behalf that this <c>.next</c> <em>will be</em> the live Core. It might not: a
    /// refused promote, a quarantine to <c>Progenitor.dll.failed-*</c>, a rotation that restores
    /// <c>.prev</c> — all paths <c>docs/modules.md</c> "Core swap" documents — leave an allowlist
    /// whose Core row is an image that never became live, and <c>CoreLoader</c> then finds no row
    /// for the real <c>Progenitor.dll</c> and refuses to load Progenitor. So when the caller names
    /// a staged image, the live <c>Progenitor.dll</c> beside it must exist and hash to
    /// <paramref name="imageSha256"/>; otherwise this refuses, with the reason, and no allowlist
    /// is created. A caller that names the live file directly never reaches that check.</para>
    /// </summary>
    /// <param name="imagePath">The image the caller named and the writer hashed.</param>
    /// <param name="imageSha256">The hash of that image, read from disk by the caller.</param>
    internal static bool TryCoreRowName(string? imagePath, string imageSha256, out string name, out string error)
    {
        name = "";
        error = "";
        string leaf;
        try
        {
            leaf = System.IO.Path.GetFileName(imagePath ?? "") ?? "";
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            return false;
        }

        if (leaf.Length == 0)
        {
            error = "the Core image path names no file";
            return false;
        }

        string live = leaf;
        for (bool stripped = true; stripped;)
        {
            stripped = false;
            for (int i = 0; i < CoreImageSuffixes.Length; i++)
            {
                string suffix = CoreImageSuffixes[i];
                if (live.Length > suffix.Length && live.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    live = live.Substring(0, live.Length - suffix.Length);
                    stripped = true;
                    break;
                }
            }
        }

        // Single-folder install: the image is Progenitor.core on disk; a row under either name lists Core.
        if (!string.Equals(live, CoreImageFileName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(live, SingleFolderCoreImageFileName, StringComparison.OrdinalIgnoreCase))
        {
            error = leaf + " is not " + CoreImageFileName + " (or a staged copy of it)";
            return false;
        }

        string liveName = string.Equals(live, SingleFolderCoreImageFileName, StringComparison.OrdinalIgnoreCase)
            ? SingleFolderCoreImageFileName
            : CoreImageFileName;

        // R38 finding 7. The leaf was a staging or rotation name, so the row we are about to write
        // names a file the caller did not hash. Listing it is only honest when the file that will
        // actually be looked up already holds these bytes.
        if (!string.Equals(leaf, liveName, StringComparison.OrdinalIgnoreCase))
        {
            string livePath;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(imagePath ?? "") ?? "";
                livePath = System.IO.Path.Combine(dir, liveName);
            }
            catch (Exception e)
            {
                error = leaf + " is a staged copy and " + liveName
                    + " could not be resolved beside it (" + e.GetType().Name + ": " + e.Message + ")";
                return false;
            }

            if (!File.Exists(livePath))
            {
                error = leaf + " is a staged copy and there is no " + liveName
                    + " beside it, so a row under that name would list a Core that is not on disk";
                return false;
            }

            string liveHex;
            try
            {
                liveHex = ReloadAllowPolicy.Sha256Hex(File.ReadAllBytes(livePath));
            }
            catch (Exception e)
            {
                error = leaf + " is a staged copy and " + liveName
                    + " beside it could not be read (" + e.GetType().Name + ": " + e.Message + ")";
                return false;
            }

            if (!string.Equals(liveHex, (imageSha256 ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            {
                error = leaf + " is a staged copy whose promote has not happened: "
                    + liveName + " is sha256=" + ReloadAllowPolicy.Short(liveHex)
                    + ", the staged bytes are sha256=" + ReloadAllowPolicy.Short(imageSha256)
                    + "; list " + liveName + " itself, or create the allowlist after the promote";
                return false;
            }
        }

        if (!ReloadAllowPolicy.TryRowName(liveName, out name, out error))
            return false;

        return true;
    }

    internal static bool TryCreateAllowList(
        string path,
        IReadOnlyList<KeyValuePair<string, string>> rows,
        string coreImagePath,
        out string error)
    {
        error = "";
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                error = "the allowlist path is empty";
                return false;
            }

            if (File.Exists(path))
            {
                error = ReloadAllowPolicy.DisplayPath + " already exists; append a row instead of creating it";
                return false;
            }

            // The hash of the image on disk, read here. Never a parameter: see R36 finding 3.
            string coreHex;
            string coreName;
            try
            {
                if (string.IsNullOrEmpty(coreImagePath) || !File.Exists(coreImagePath))
                {
                    error = ReloadAllowPolicy.RefusedWithoutCore;
                    return false;
                }

                coreHex = ReloadAllowPolicy.Sha256Hex(File.ReadAllBytes(coreImagePath));

                // The name of the image we just hashed, not the spelling the caller used for it.
                if (!TryCoreRowName(coreImagePath, coreHex, out coreName, out string whyName))
                {
                    error = ReloadAllowPolicy.RefusedWithoutCore + " (" + whyName + ")";
                    return false;
                }
            }
            catch (Exception e)
            {
                error = ReloadAllowPolicy.RefusedWithoutCore + " (" + e.GetType().Name + ": " + e.Message + ")";
                return false;
            }

            var text = new System.Text.StringBuilder();
            bool listsCore = false;
            for (int i = 0; rows != null && i < rows.Count; i++)
            {
                string hex = (rows[i].Key ?? "").Trim().ToLowerInvariant();
                if (hex.Length != ReloadAllowPolicy.HashLength)
                {
                    error = "expected " + ReloadAllowPolicy.HashLength + " hex sha256 characters";
                    return false;
                }

                if (!ReloadAllowPolicy.TryRowName(rows[i].Value, out string name, out error))
                    return false;

                if (string.Equals(hex, coreHex, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(name, coreName, StringComparison.OrdinalIgnoreCase))
                    listsCore = true;
                text.Append(ReloadAllowPolicy.FormatSumsLine(hex, name)).Append('\n');
            }

            if (!listsCore)
            {
                error = ReloadAllowPolicy.RefusedWithoutCore;
                return false;
            }

            string dir = System.IO.Path.GetDirectoryName(path) ?? "";
            if (dir.Length > 0) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text.ToString(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            try
            {
                File.Move(tmp, path, overwrite: false);
            }
            catch
            {
                File.Copy(tmp, path, overwrite: false);
                try { File.Delete(tmp); }
                catch { /* leftover tmp is ignored on the next read */ }
            }

            return true;
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            return false;
        }
    }

    /// <summary>True when <paramref name="line"/> is the entry row for exactly this hash and name.</summary>
    private static bool IsAllowRowFor(string line, string hex, string name)
    {
        string trimmed = (line ?? "").Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#') return false;
        int split = -1;
        for (int c = 0; c < trimmed.Length; c++)
        {
            if (!char.IsWhiteSpace(trimmed[c])) continue;
            split = c;
            break;
        }

        if (split < 0) return false;
        if (!string.Equals(trimmed.Substring(0, split), hex, StringComparison.OrdinalIgnoreCase)) return false;
        if (!ReloadAllowPolicy.TryRowName(trimmed.Substring(split + 1), out string rowName, out _)) return false;
        return string.Equals(rowName, name, StringComparison.OrdinalIgnoreCase);
    }

    private static ReloadAllowList ParseFailure(string why) =>
        ReloadAllowPolicy.MalformedFor(why);

    /// <summary>Log the allowlist state once per change, so the log says which posture is in force.</summary>
    private static ReloadAllowList Publish(ReloadAllowList list)
    {
        _list = list;
        string line = list.Status switch
        {
            AllowListStatus.Malformed => ReloadAllowPolicy.FormatMalformed(list.Error),
            AllowListStatus.Active => ReloadAllowPolicy.FormatActive(list.Count),
            _ => "",
        };
        if (line.Length > 0 && !string.Equals(line, _lastLogged, StringComparison.Ordinal))
        {
            _lastLogged = line;
            if (list.Status == AllowListStatus.Malformed)
                Progenitor.Plugin.Logger?.LogError(line);
            else
                Progenitor.Plugin.Logger?.LogWarning(line);
        }
        else if (line.Length == 0)
        {
            _lastLogged = "";
        }
        return list;
    }

    /// <summary>Test/diagnostic reset. Not used by the runtime.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _list = ReloadAllowPolicy.Absent();
            _haveStamp = false;
            _lastLogged = "";
        }
    }
}
