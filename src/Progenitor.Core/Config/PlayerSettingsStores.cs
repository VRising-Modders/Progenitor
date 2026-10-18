using System;
using System.Collections.Generic;
using System.IO;

namespace Progenitor.Config;

/// <summary>
/// One <see cref="PlayerSettingsStore"/> per mod GUID, game thread only. A store opened for a
/// registration is kept only when that registration succeeds, and a released store is written
/// before it is forgotten.
/// </summary>
internal sealed class PlayerSettingsStores
{
    private readonly Dictionary<string, PlayerSettingsStore> _stores = new(StringComparer.Ordinal);
    private readonly Func<string, PlayerSettingsStore> _open;
    private readonly Action<string>? _warn;

    internal PlayerSettingsStores(Func<string, PlayerSettingsStore> open, Action<string>? warn = null)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _warn = warn;
    }

    internal int Count => _stores.Count;
    internal bool Contains(string pluginId) => _stores.ContainsKey(pluginId);

    /// <summary>Run <paramref name="register"/> with the mod's store, opening it if needed. When
    /// it throws, a store opened for this call is dropped and the exception propagates.</summary>
    internal TResult Register<TResult>(string pluginId, Func<PlayerSettingsStore, TResult> register)
    {
        bool created = !_stores.TryGetValue(pluginId, out var store);
        store ??= _open(pluginId);
        var result = register(store);
        if (created) _stores.Add(pluginId, store);
        return result;
    }

    /// <summary>Write and forget the mod's store (its last personal setting went).</summary>
    internal void Release(string pluginId)
    {
        if (!_stores.Remove(pluginId, out var store)) return;
        Flush(pluginId, store);
    }

    /// <summary>Poll pump: coalesced writes, retries and external edits.</summary>
    internal void Tick()
    {
        foreach (var store in _stores.Values) store.Tick();
    }

    /// <summary>Write every store and forget them all (Core unload).</summary>
    internal void Clear()
    {
        foreach (var pair in _stores) Flush(pair.Key, pair.Value);
        _stores.Clear();
    }

    private void Flush(string pluginId, PlayerSettingsStore store)
    {
        try { store.Flush(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { _warn?.Invoke("Player settings for " + pluginId + " could not be saved: " + e.Message); }
    }
}
