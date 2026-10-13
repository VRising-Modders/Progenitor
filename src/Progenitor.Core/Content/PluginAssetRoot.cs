using System;
using System.IO;
using System.Reflection;
using Progenitor.Localization;
using Progenitor.Reload;

namespace Progenitor.Content;

/// <summary>
/// Resolves a plugin's <c>assets</c> folder the same way Localization resolves
/// <c>localization</c>: assembly <see cref="Assembly.Location"/> when present, else the
/// reload source path recorded for a byte-stream load, then
/// <c>&lt;dir&gt;/&lt;AssemblyName&gt;/assets</c> then <c>&lt;dir&gt;/assets</c>.
/// </summary>
internal static class PluginAssetRoot
{
    /// <summary>Folder name probed next to <c>localization</c>. Deploy copies a plugin's <c>assets</c> here.</summary>
    public const string FolderName = "assets";

    /// <summary>
    /// First existing assets folder for <paramref name="asm"/>, or null when the assembly has
    /// no path or neither probe exists. Does not log: the buff-icon applicator emits one line.
    /// </summary>
    public static string? Resolve(Assembly asm)
    {
        if (asm == null) throw new ArgumentNullException(nameof(asm));
        if (!TryGetDllDirectory(asm, out var dllDir))
            return null;
        var name = asm.GetName().Name ?? "";
        return LocalizationParser.ResolvePluginFolder(dllDir, name, Directory.Exists, LocalizationParser.AssetsFolderName);
    }

    /// <summary>
    /// Same location probe as <see cref="Localization.LoadForPlugin"/>: <c>Location</c>, then
    /// <see cref="PluginReloader.TryGetSourcePath"/>. False when neither is set (reloadable
    /// plugin whose record is already gone, or a test double with no path).
    /// </summary>
    public static bool TryGetDllDirectory(Assembly asm, out string dllDirectory)
    {
        dllDirectory = "";
        if (asm == null) return false;
        try
        {
            var location = asm.Location;
            if (string.IsNullOrEmpty(location)
                && (!PluginReloader.TryGetSourcePath(asm, out location) || string.IsNullOrEmpty(location)))
                return false;
            dllDirectory = Path.GetDirectoryName(location) ?? "";
            return !string.IsNullOrEmpty(dllDirectory);
        }
        catch
        {
            return false;
        }
    }
}
