using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Progenitor.Localization;
using Stunlock.Core;

namespace Progenitor.Content.Json;

/// <summary>
/// The disk half of JSON content definition files (roadmap M4.6). Probes a plugin's
/// <c>content</c> folder the same way Localization probes <c>localization</c> and
/// <see cref="PluginAssetRoot"/> probes <c>assets</c>, reads every <c>*.json</c> in it, and hands
/// them to <see cref="ContentFileLoad"/> with a sink that calls the ordinary
/// <c>PrefabRegistry.DefineItem</c> builder.
///
/// <para>Everything interesting is in the game-free half; this file only opens files, logs and
/// owns the handles. It is wired from <c>ProgenitorPlugin.Load</c> <b>after</b> <c>OnLoad</c>, so
/// the ids a plugin registers in C# already exist and a file that collides with one is refused
/// rather than silently winning.</para>
/// </summary>
internal static class ContentFiles
{
    /// <summary>Folder name probed next to <c>localization</c> and <c>assets</c>.</summary>
    public const string FolderName = ContentFileSchema.FolderName;

    /// <summary>
    /// Load <paramref name="asm"/>'s content files under <paramref name="owner"/>. Returns a
    /// disposable that unregisters everything the files defined; never null, never throws.
    /// Logs nothing when the plugin has no <c>content</c> folder — that is the normal case.
    /// </summary>
    public static IDisposable LoadForPlugin(Assembly asm, object owner)
    {
        if (asm == null) throw new ArgumentNullException(nameof(asm));
        if (owner == null) throw new ArgumentNullException(nameof(owner));

        var handles = new List<IDisposable>();
        try
        {
            if (!PluginAssetRoot.TryGetDllDirectory(asm, out var dllDir))
                return new Handles(handles);

            var asmName = asm.GetName().Name ?? "";
            var dir = LocalizationParser.ResolvePluginFolder(dllDir, asmName, Directory.Exists, FolderName);
            if (dir == null) return new Handles(handles);

            var paths = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
            Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
            if (paths.Length == 0)
            {
                Plugin.Logger?.LogInfo($"{ContentFileLoad.LogPrefix}'{dir}' has no .json files.");
                return new Handles(handles);
            }

            var sources = new List<ContentFileSource>(paths.Length);
            for (int i = 0; i < paths.Length; i++)
            {
                var label = $"{asmName}/{FolderName}/{Path.GetFileName(paths[i])}";
                try
                {
                    sources.Add(new ContentFileSource(label, File.ReadAllText(paths[i])));
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogError($"{ContentFileLoad.LogPrefix}{label} could not be read: {e.Message}");
                }
            }

            var sink = new RegistrySink(owner, handles);
            var result = ContentFileLoad.Run(sources, sink);
            for (int i = 0; i < result.Info.Count; i++) Plugin.Logger?.LogInfo(result.Info[i]);
            for (int i = 0; i < result.Errors.Count; i++) Plugin.Logger?.LogError(result.Errors[i]);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"{ContentFileLoad.LogPrefix}load for '{asm.GetName().Name}' failed: {e}");
        }

        return new Handles(handles);
    }

    /// <summary>
    /// The one place a content file reaches the registry: the same fluent builder a C# plugin
    /// calls, in the same order, under the same owner. There is deliberately no second
    /// registration path — a file cannot express anything <c>DefineItem</c> cannot.
    /// </summary>
    private sealed class RegistrySink : IContentFileSink
    {
        private readonly object _owner;
        private readonly List<IDisposable> _handles;

        public RegistrySink(object owner, List<IDisposable> handles)
        {
            _owner = owner;
            _handles = handles;
        }

        public bool IsDefined(string id)
        {
            var definitions = PrefabRegistry.Definitions;
            for (int i = 0; i < definitions.Count; i++)
            {
                var def = definitions[i];
                if (ReferenceEquals(def.Owner, _owner) && string.Equals(def.Id, id, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        public void DefineItem(ContentItemSpec spec)
        {
            var builder = PrefabRegistry.DefineItem(spec.Id);
            if (spec.CloneOfGuidHash.HasValue)
                builder.CloneOf(new PrefabGUID(spec.CloneOfGuidHash.Value));
            else
                builder.CloneOf(spec.CloneOfName!);
            if (spec.Stack.HasValue) builder.MaxAmount(spec.Stack.Value);
            if (spec.StatBonuses != null) builder.StatBonuses(spec.StatBonuses);
            if (!string.IsNullOrEmpty(spec.Name)) builder.Named(spec.Name!);
            if (!string.IsNullOrEmpty(spec.Description)) builder.Described(spec.Description!);
            _handles.Add(builder.Register(_owner));
        }
    }

    /// <summary>Reverse order, each in its own try/catch, same shape as the plugin bag.</summary>
    private sealed class Handles : IDisposable
    {
        private readonly List<IDisposable> _handles;

        public Handles(List<IDisposable> handles) => _handles = handles;

        public void Dispose()
        {
            for (int i = _handles.Count - 1; i >= 0; i--)
            {
                try { _handles[i].Dispose(); }
                catch (Exception e)
                {
                    Plugin.Logger?.LogError($"{ContentFileLoad.LogPrefix}undo threw: {e.Message}");
                }
            }
            _handles.Clear();
        }
    }
}
