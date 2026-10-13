#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using HarmonyLib;
using ProjectM;
using Progenitor.Patches;

namespace Progenitor.Networking;

/// <summary>
/// NODESCCHAT (docs/server-list-description.md, "The description in chat"): the live half of
/// <see cref="JoinDescriptionChatGuard"/>. Server only.
///
/// <para><b>Seam.</b> One slot-routed prefix (Core owner, <see cref="CorePatchBudget"/>) on
/// <c>ServerBootstrapSystem.OnUserConnected(NetConnectionId)</c>, the instance method Bootstrap already postfixes for
/// <c>PlayerConnected</c>. The prefix reads no argument (the one parameter is a reference type anyway), never skips
/// the original, and touches only the managed string field <c>ServerHostSettings.Description</c> through the
/// static field <c>SettingsManager._ServerHostSettings</c>, the accessors <see cref="ServerListGame"/> already uses.
/// No by-ref struct, no new generic instantiation, no native call. The restore runs from Core's existing
/// <c>OnUserConnected</c> postfix (<c>EventHooks.ConnectionHooks.Connected</c>, before its own work) and, should that
/// not run (the original threw, the events module skipped), from the next frame.</para>
///
/// <para><b>Setting.</b> <c>[ServerList] DescriptionInChat</c> in <c>progenitor.serverlist.cfg</c>
/// (cfg-only, default false), read on every join.</para>
/// </summary>
internal static class JoinDescriptionChatHook
{
    internal const string OwnerId = MyPluginInfo.PLUGIN_GUID + PatchSitePolicy.CoreOwnerSuffix;

    private static readonly JoinDescriptionChatGuard Guard = new();
    private static readonly LiveField Field = new();
    private static Func<bool>? _descriptionInChat;
    private static PatchHandle? _handle;
    private static int _held;
    private static int _passed;

    /// <summary>Arm the prefix (server only). <paramref name="descriptionInChat"/> is read on every join.</summary>
    internal static void Install(Func<bool> descriptionInChat)
    {
        _descriptionInChat = descriptionInChat;
        if (_handle != null || !Plugin.IsServerProcess) return;
        try
        {
            var original = AccessTools.Method(typeof(ServerBootstrapSystem), nameof(ServerBootstrapSystem.OnUserConnected));
            if (original == null)
            {
                Plugin.Logger?.LogWarning("ServerList: ServerBootstrapSystem.OnUserConnected was not found; the server's description will also be posted to chat on join.");
                return;
            }

            Patches.Patches.Install();
            _handle = PatchRegistry.PrefixSlot(owner: null, ownerId: OwnerId, target: original, prefix: static (_, _) =>
            {
                BeforeUserConnected();
                return true;
            });
            if (_handle == null)
                Plugin.Logger?.LogWarning("ServerList: the OnUserConnected prefix was refused (see the patches: line above); the server's description will also be posted to chat on join.");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ServerList: the OnUserConnected prefix could not be installed: " + e.Message);
        }
    }

    /// <summary>Put a held description back and remove the prefix.</summary>
    internal static void Uninstall()
    {
        AfterUserConnected();
        try { _handle?.Dispose(); }
        catch { /* teardown */ }
        _handle = null;
        _descriptionInChat = null;
    }

    /// <summary>The prefix body: hold the description out unless the operator wants it in chat.</summary>
    private static void BeforeUserConnected()
    {
        try
        {
            bool inChat = _descriptionInChat?.Invoke() ?? false;
            if (Guard.Before(Field, inChat))
            {
                _held++;
                Plugin.Logger?.LogInfo("ServerList: a player is joining; the server's description is kept out of their chat (DescriptionInChat off).");
            }
            else if (inChat)
            {
                _passed++;
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ServerList: the join chat guard threw before the join: " + e.Message); }
    }

    /// <summary>From Core's <c>OnUserConnected</c> postfix: put the description back. Cheap when nothing is held.</summary>
    internal static void AfterUserConnected()
    {
        if (!Guard.Pending) return;
        try { Guard.Restore(Field); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ServerList: the join chat guard could not restore the description: " + e.Message); }
    }

    /// <summary>Each frame: a description still held here means the postfix did not run; put it back.</summary>
    internal static void OnFrame()
    {
        if (!Guard.Pending) return;
        Plugin.Logger?.LogWarning("ServerList: the description was still held after a join (the OnUserConnected postfix did not run); restored.");
        AfterUserConnected();
    }

    /// <summary>
    /// <c>server_desc read</c>'s <c>joinChat</c>: <c>descriptionInChat</c> (the setting now), <c>armed</c> (the prefix
    /// is registered), <c>pending</c> (a description is held right now; false between joins), <c>held</c> (joins whose
    /// description was kept out of chat since Core loaded) and <c>passed</c> (joins with the setting on).
    /// </summary>
    internal static Dictionary<string, object?> Readback() => new()
    {
        ["descriptionInChat"] = _descriptionInChat?.Invoke() ?? false,
        ["armed"] = _handle != null && !_handle.Disposed,
        ["pending"] = Guard.Pending,
        ["held"] = _held,
        ["passed"] = _passed,
    };

    private sealed class LiveField : IJoinDescriptionField
    {
        public string? Read()
        {
            try { return SettingsManager._ServerHostSettings?.Description; }
            catch { return null; }
        }

        public void Write(string text)
        {
            var settings = SettingsManager._ServerHostSettings
                ?? throw new InvalidOperationException("the game's ServerHostSettings is not loaded");
            settings.Description = text ?? "";
        }
    }
}
#endif
