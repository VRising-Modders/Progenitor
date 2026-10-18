using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Progenitor.Networking;
using Progenitor.Players;
using Progenitor.Plugins;
using ProjectM.Network;
using Unity.Entities;

namespace Progenitor.Ui;

/// <summary>A server-owned preference stored separately for each stable player account. Main thread only.</summary>
public sealed class PlayerSetting<T> : IDisposable
{
    private ScopedSettings.Registration? _registration;
    private readonly string _id;
    private readonly bool _server;
    private bool _disposed;
    private readonly T _defaultValue;
    private readonly HashSet<IDisposable> _requests = new();

    internal PlayerSetting(PluginContext ctx, string section, string key, T defaultValue, string description,
        AcceptableValueBase? bounds, Func<T, string?>? validate)
    {
        ConfigSync.ThrowIfNotSyncable(typeof(T));
        _id = ScopedSettings.RowId(ctx.Guid, section, key); _server = ctx.IsServer; _defaultValue = defaultValue;
        if (_server)
            _registration = ScopedSettings.Personal(ctx.Guid, ctx.Name, ctx.Version, section, key, defaultValue, description, bounds, validate,
                (player, value) => Changed?.Invoke(new PlayerAccountId(player), value));
        else ScopedSettings.ClientValueChanged += OnClientValue;
    }

    /// <summary>Fires on the game thread when an accepted value changes. Client events use an empty account id (the local player)
    /// and also fire once when the server first provides the value; a re-sent catalog with the same value does not fire.</summary>
    public event Action<PlayerAccountId, T>? Changed;

    /// <summary>Read this account's saved preference on the server.</summary>
    public T For(PlayerAccountId player)
    {
        Check();
        if (!_server || _registration == null) throw new InvalidOperationException("For is server-only.");
        return Parse(_registration.Read(player.Value));
    }

    /// <summary>Resolve a User entity to its stable account id and read its preference.</summary>
    public T For(Entity userEntity)
    {
        Check();
        var world = Worlds.Server;
        if (!_server || world == null || !world.IsCreated || !world.EntityManager.HasComponent<User>(userEntity))
            throw new ArgumentException("A server User entity is required.", nameof(userEntity));
        return For(new PlayerAccountId(world.EntityManager.GetComponentData<User>(userEntity).PlatformId));
    }

    /// <summary>Whether the joined server has sent this preference with the matching type.</summary>
    public bool HasMine => !_disposed && !_server && ScopedSettings.HasClient(_id, typeof(T).FullName ?? "");

    /// <summary>This player's server value. Throws until the server has sent it; never mistakes a default for a server response.</summary>
    public T Mine => HasMine ? Parse(ScopedSettings.ReadClient(_id)!) : throw new InvalidOperationException("This server has not provided your setting.");

    /// <summary>Change this player's own value from the client, or report why it was refused.</summary>
    public IDisposable Set(T value, Action<ServerSettingResult> completed)
    {
        Check();
        if (_server) throw new InvalidOperationException("Use SetFor on the server.");
        if (!HasMine) throw new InvalidOperationException("This server has not provided a compatible personal setting.");
        if (completed == null) throw new ArgumentNullException(nameof(completed));
        IDisposable? handle = null;
        bool finished = false;
        handle = ScopedSettings.Request(_id, SettingsEditors.Format(value), SettingsEditors.Format(Mine), result =>
        {
            finished = true;
            if (handle != null) _requests.Remove(handle);
            if (!_disposed) completed(result);
        });
        if (!finished) _requests.Add(handle);
        return handle;
    }

    /// <summary>Server-side change for an account. Uses the same bounds and persistence as a player edit.</summary>
    public ServerSettingResult SetFor(PlayerAccountId player, T value)
    {
        Check();
        if (!_server || _registration == null || player.IsNone) throw new InvalidOperationException("A server setting and stable player account are required.");
        return _registration.Write(player.Value, SettingsEditors.Format(value), "");
    }

    private T Parse(string value) => SettingsEditors.TryParse(value, typeof(T), out var parsed, out _)
        ? (T)parsed! : _defaultValue;
    private void OnClientValue(string id, string value) { if (id == _id && HasMine) Changed?.Invoke(PlayerAccountId.None, Parse(value)); }
    private void Check() { if (_disposed) throw new ObjectDisposedException(nameof(PlayerSetting<T>)); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ScopedSettings.ClientValueChanged -= OnClientValue;
        Changed = null;
        foreach (var request in _requests) request.Dispose();
        _requests.Clear();
        _registration?.Dispose(); _registration = null;
    }
}
