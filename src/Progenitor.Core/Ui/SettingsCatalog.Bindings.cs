using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Progenitor.Networking;

namespace Progenitor.Ui;

/// <summary>
/// Shared typed handler for settings writes and actions. UI, commands and a future MCP call
/// these methods; independently registered commands do not become UI actions on their own.
/// </summary>
public static partial class SettingsCatalog
{
    private static readonly Dictionary<string, SettingBinding> Bindings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, SettingActionBinding> Actions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> Generations = new(StringComparer.OrdinalIgnoreCase);
    private static int _generation;

    /// <summary>Changes whenever a setting binding is registered (the server re-derives published presentation).</summary>
    internal static int BindingGeneration
    {
        get { lock (Gate) return _generation; }
    }

    /// <summary>
    /// Wrap an existing config entry as the source of truth. Replacing a live registration of
    /// the same id starts a new generation: the previous pending completion is not delivered.
    /// </summary>
    public static SettingBinding RegisterSetting(
        string pluginId,
        ConfigEntryBase entry,
        SettingPresentation? presentation = null,
        object? owner = null,
        bool foreign = false)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (string.IsNullOrWhiteSpace(pluginId)) throw new ArgumentException("Plugin id is required.", nameof(pluginId));
        Register(pluginId, entry.ConfigFile, owner, foreign);
        var options = presentation ?? new SettingPresentation();
        string id = pluginId + "/" + entry.Definition.Section + "/" + entry.Definition.Key;
        IDisposable? server = null;
        if (options.AllowServerEdits)
            server = ServerSettings.Register(pluginId, entry);
        // TEXTLIMIT1: a declared MaxLength on a string binding is the limit the server checks.
        if (options.MaxLength is int max && max > 0 && entry.SettingType == typeof(string))
        {
            var declared = ServerSettings.DeclareText(id, max, multiline: false);
            server = server == null ? declared : new BothDisposables(server, declared);
        }

        SettingBinding binding;
        SettingBinding? previous;
        lock (Gate)
        {
            Bindings.TryGetValue(id, out previous);
            int generation = NextGeneration(id);
            binding = new SettingBinding(pluginId, entry, keybind: null, options, owner, generation, foreign);
            Bindings[id] = binding;
        }

        previous?.Dispose();
        if (server != null) binding.TrackServer(server);
        return binding;
    }

    private sealed class BothDisposables : IDisposable
    {
        private IDisposable? _first;
        private IDisposable? _second;
        internal BothDisposables(IDisposable first, IDisposable second) { _first = first; _second = second; }
        public void Dispose()
        {
            var first = _first; var second = _second;
            _first = null; _second = null;
            try { first?.Dispose(); }
            finally { second?.Dispose(); }
        }
    }

    /// <summary>
    /// Register a typed action. Existing independently-registered commands are not discovered;
    /// call this with the same handler a command already uses when both surfaces should exist.
    /// </summary>
    public static IDisposable RegisterAction(
        string pluginId,
        string id,
        Func<IReadOnlyList<string>, SettingActionResult> handler,
        SettingActionSpec? spec = null,
        object? owner = null)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) throw new ArgumentException("Plugin id is required.", nameof(pluginId));
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Action id is required.", nameof(id));
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        string settingId = pluginId + "/" + SettingBinding.ActionSection + "/" + id;
        SettingActionBinding action;
        SettingActionBinding? previous;
        lock (Gate)
        {
            Actions.TryGetValue(settingId, out previous);
            int generation = NextGeneration(settingId);
            action = new SettingActionBinding(pluginId, id, handler, spec, owner, generation);
            Actions[settingId] = action;
        }

        previous?.Dispose();
        return action;
    }

    /// <summary>Serializable snapshots of registered settings and actions. No delegates.</summary>
    public static IReadOnlyList<SettingDescriptor> SnapshotDescriptors()
    {
        SettingBinding[] bindings;
        SettingActionBinding[] actions;
        lock (Gate)
        {
            bindings = new SettingBinding[Bindings.Count];
            Bindings.Values.CopyTo(bindings, 0);
            actions = new SettingActionBinding[Actions.Count];
            Actions.Values.CopyTo(actions, 0);
        }

        var list = new List<SettingDescriptor>(bindings.Length + actions.Length);
        for (int i = 0; i < bindings.Length; i++)
        {
            if (bindings[i].IsDisposed) continue;
            list.Add(bindings[i].Describe());
        }

        for (int i = 0; i < actions.Length; i++)
        {
            if (actions[i].IsDisposed) continue;
            var action = actions[i].Describe();
            list.Add(new SettingDescriptor(
                action.PluginId,
                SettingBinding.ActionSection,
                action.Id,
                "Action",
                action.Busy ? "busy" : (action.Available ? "available" : "unavailable"),
                "",
                action.Help,
                action.Group,
                action.Order,
                SettingsEditorHint.Action,
                SettingCommitPolicy.Immediate,
                SettingScope.Local,
                action.Available ? SettingEditPath.LocalConfig : SettingEditPath.None,
                canEdit: action.Available,
                label: action.Label));
        }

        list.Sort(static (a, b) =>
        {
            int c = string.CompareOrdinal(a.PluginId, b.PluginId);
            if (c != 0) return c;
            c = a.Order.CompareTo(b.Order);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        });
        return list;
    }

    public static bool TryGetBinding(string id, out SettingBinding? binding)
    {
        lock (Gate)
        {
            if (id != null && Bindings.TryGetValue(id, out binding) && !binding.IsDisposed)
                return true;
        }

        binding = null;
        return false;
    }

    public static bool TryGetAction(string id, out SettingActionDescriptor descriptor)
    {
        SettingActionBinding? action;
        lock (Gate)
            Actions.TryGetValue(id ?? "", out action);
        if (action == null || action.IsDisposed)
        {
            descriptor = default!;
            return false;
        }

        descriptor = action.Describe();
        return true;
    }

    /// <summary>Shared write used by UI, commands and a future MCP.</summary>
    public static SettingWriteResult Write(string id, string value) => Commit(id, value);

    public static SettingWriteResult Preview(string id, string value)
    {
        if (TryGetBinding(id, out var binding) && binding != null)
            return binding.Preview(value);
        return SettingWriteResult.Refused("", "this setting is not registered");
    }

    public static SettingWriteResult Commit(string id, string? value = null)
    {
        if (TryGetBinding(id, out var binding) && binding != null)
            return binding.Commit(value);
        return SubmitUnregistered(id, value);
    }

    public static SettingWriteResult Cancel(string id)
    {
        if (TryGetBinding(id, out var binding) && binding != null)
            return binding.Cancel();
        return SettingWriteResult.Idle("");
    }

    /// <summary>Discard every uncommitted edit and cancel key capture. Does not undo applied writes.</summary>
    public static void EscapeAll()
    {
        SettingBinding[] bindings;
        lock (Gate)
        {
            bindings = new SettingBinding[Bindings.Count];
            Bindings.Values.CopyTo(bindings, 0);
        }

        for (int i = 0; i < bindings.Length; i++)
        {
            try { bindings[i].Escape(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings EscapeAll threw: " + e.Message); }
        }

        Keybinds.CancelRebind();
    }

    public static SettingWriteResult Reset(string id)
    {
        if (TryGetBinding(id, out var binding) && binding != null)
            return binding.Reset();
        return SettingWriteResult.Refused("", "this setting is not registered");
    }

    public static SettingActionResult InvokeAction(string id, params string[] arguments)
    {
        SettingActionBinding? action;
        lock (Gate)
            Actions.TryGetValue(id ?? "", out action);
        if (action == null || action.IsDisposed)
            return SettingActionResult.NotAvailable("this action is not registered");
        return action.Invoke(arguments ?? Array.Empty<string>(), action.Generation);
    }

    internal static SettingWriteResult Submit(SettingsRow row, string? text)
    {
        if (TryGetBinding(row.RowId, out var binding) && binding != null)
            return binding.Commit(text);
        if (!row.TryWrite(text, out var error))
            return SettingWriteResult.Refused(row.Read(), error);
        return SettingWriteResult.Accepted(row.Read());
    }

    internal static SettingPresentation? PresentationFor(string pluginId, string section, string key)
    {
        string id = pluginId + "/" + section + "/" + key;
        lock (Gate)
        {
            if (Bindings.TryGetValue(id, out var binding) && !binding.IsDisposed)
                return binding.Presentation;
        }

        return null;
    }

    internal static IReadOnlyList<SettingActionBinding> ActionsFor(string pluginId)
    {
        lock (Gate)
        {
            var list = new List<SettingActionBinding>();
            foreach (var action in Actions.Values)
            {
                if (action.IsDisposed) continue;
                if (string.Equals(action.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
                    list.Add(action);
            }

            return list;
        }
    }

    internal static IReadOnlyList<SettingActionBinding> AllActions()
    {
        lock (Gate)
        {
            var list = new List<SettingActionBinding>();
            foreach (var action in Actions.Values)
                if (!action.IsDisposed) list.Add(action);
            return list;
        }
    }

    internal static void DetachBinding(SettingBinding binding)
    {
        lock (Gate)
        {
            if (Bindings.TryGetValue(binding.Id, out var live) && ReferenceEquals(live, binding))
                Bindings.Remove(binding.Id);
        }
    }

    internal static void DetachAction(SettingActionBinding action)
    {
        lock (Gate)
        {
            if (Actions.TryGetValue(action.SettingId, out var live) && ReferenceEquals(live, action))
                Actions.Remove(action.SettingId);
        }
    }

    private static int NextGeneration(string id)
    {
        _generation++;
        Generations[id] = _generation;
        return _generation;
    }

    private static SettingWriteResult SubmitUnregistered(string? id, string? value)
    {
        if (string.IsNullOrEmpty(id))
            return SettingWriteResult.Refused("", "this setting is not registered");
        foreach (var row in Rows())
        {
            if (!string.Equals(row.RowId, id, StringComparison.OrdinalIgnoreCase)) continue;
            if (!row.TryWrite(value, out var error))
                return SettingWriteResult.Refused(row.Read(), error);
            return SettingWriteResult.Accepted(row.Read());
        }

        return SettingWriteResult.Refused("", "this setting is not registered");
    }

    private static void ClearBindings()
    {
        SettingBinding[] bindings;
        SettingActionBinding[] actions;
        lock (Gate)
        {
            bindings = new SettingBinding[Bindings.Count];
            Bindings.Values.CopyTo(bindings, 0);
            Bindings.Clear();
            actions = new SettingActionBinding[Actions.Count];
            Actions.Values.CopyTo(actions, 0);
            Actions.Clear();
            Generations.Clear();
            _generation = 0;
        }

        for (int i = 0; i < bindings.Length; i++)
        {
            try { bindings[i].Dispose(); }
            catch { /* teardown */ }
        }

        for (int i = 0; i < actions.Length; i++)
        {
            try { actions[i].Dispose(); }
            catch { /* teardown */ }
        }
    }

    private static int RemoveBindings(object owner)
    {
        var drop = new List<IDisposable>();
        lock (Gate)
        {
            foreach (var kv in Bindings)
                if (ReferenceEquals(kv.Value.Owner, owner)) drop.Add(kv.Value);
            foreach (var kv in Actions)
                if (ReferenceEquals(kv.Value.Owner, owner)) drop.Add(kv.Value);
        }

        for (int i = 0; i < drop.Count; i++)
        {
            try { drop[i].Dispose(); }
            catch { /* unload */ }
        }

        return drop.Count;
    }
}
