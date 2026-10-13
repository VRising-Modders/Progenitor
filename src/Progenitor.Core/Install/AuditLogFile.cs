using System;
using System.IO;

namespace Progenitor.Install;

/// <summary>
/// The one writer behind Progenitor's server audit files (<c>BepInEx/progenitor-remote-install.log</c>,
/// <c>BepInEx/progenitor-settings.log</c>): one line appended per event, the file rolled to <c>.1</c>
/// past <see cref="MaxBytes"/> so a busy server cannot grow it without bound. Plain file IO; a locked
/// or unwritable file costs this copy only (the caller's BepInEx log line is the other half).
/// </summary>
internal static class AuditLogFile
{
    public const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static void Append(string bepInExRoot, string fileName, string line)
    {
        if (string.IsNullOrWhiteSpace(bepInExRoot) || string.IsNullOrWhiteSpace(fileName) || line == null) return;
        string path = Path.Combine(bepInExRoot, fileName);
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    string rolled = path + ".1";
                    if (File.Exists(rolled)) File.Delete(rolled);
                    File.Move(path, rolled);
                }

                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // The BepInEx log line is the other half of the audit; a locked file costs this copy only.
            }
        }
    }
}

/// <summary>
/// The audit file for administrator edits of server settings, <c>BepInEx/progenitor-settings.log</c>
/// (docs/settings-scopes.md, decision 7: server-wide edits only). One line per request the server
/// decided, accepted or refused, through <see cref="AuditLogFile"/>.
/// </summary>
internal static class SettingsAuditLog
{
    public const string FileName = "progenitor-settings.log";

    /// <summary>The longest value text one audit line carries before it is cut.</summary>
    public const int MaxValueChars = 120;

    public static string PathFor(string bepInExRoot) => Path.Combine(bepInExRoot, FileName);

    public static void Append(string bepInExRoot, string line) => AuditLogFile.Append(bepInExRoot, FileName, line);

    /// <summary>
    /// One audit line: <c>2026-10-03T12:00:00Z settings accepted by Name (7656…, adminlist)
    /// mod/Section/Key "3" -> "5"</c>, plus <c>: reason</c> for a refusal. Values are quoted with
    /// control characters escaped and cut at <see cref="MaxValueChars"/>, so one request is one line.
    /// </summary>
    public static string Line(DateTime utc, bool accepted, string characterName, ulong steamId, string authorizedBy,
        string rowId, string oldValue, string newValue, string error)
    {
        string head = utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)
            + " settings " + (accepted ? "accepted" : "refused")
            + " by " + Clean(string.IsNullOrWhiteSpace(characterName) ? "?" : characterName)
            + " (" + steamId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", " + authorizedBy + ") "
            + Clean(rowId) + " " + Quote(oldValue) + " -> " + Quote(newValue);
        return accepted || string.IsNullOrEmpty(error) ? head : head + ": " + Clean(error);
    }

    private static string Quote(string? value) => "\"" + Clean(value) + "\"";

    /// <summary>
    /// A value as one log-safe line: <c>\</c>, line breaks, Tab and <c>"</c> escaped, other control
    /// characters as <c>?</c>, cut at <see cref="MaxValueChars"/>. Also used for the BepInEx log
    /// lines that echo a typed setting value (TEXTAREA1).
    /// </summary>
    internal static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = new System.Text.StringBuilder(Math.Min(value!.Length, MaxValueChars) + 8);
        foreach (char c in value)
        {
            if (text.Length >= MaxValueChars) { text.Append('…'); break; }
            switch (c)
            {
                // TEXTAREA1: escaped so a typed "\n" (two characters) cannot read as a line break.
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                case '"': text.Append("\\\""); break;
                default: text.Append(char.IsControl(c) ? '?' : c); break;
            }
        }
        return text.ToString();
    }
}
