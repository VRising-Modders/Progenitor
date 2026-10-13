using System;
using System.Collections.Generic;
using HarmonyLib;
using Progenitor.Events;

namespace Progenitor.Commands;

/// <summary>
/// Connects the command dispatcher to chat. Commands do not patch the chat system themselves: the
/// event bus owns that one hook (priority 300, ahead of another chat-command framework) and publishes a
/// cancellable <see cref="ChatMessage"/>. This subscriber runs first among bus handlers and cancels
/// the message when a command handled it, which destroys the chat entity before the game or any
/// other framework sees it. Unmatched dot-commands are left alone on purpose.
/// </summary>
public static class CommandSystem
{
    private static IDisposable? _subscription;

    /// <summary>
    /// Server only. Runs the parser self-check, registers the built-ins, and subscribes to chat.
    /// Safe to call twice. This module has no Bootstrap Harmony site: chat is an event-bus
    /// subscriber. Per-site ownership of <c>ChatMessageSystem.OnUpdate</c> lives on EventHooks.
    /// </summary>
    public static void Install(Harmony? harmony)
    {
        if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.CommandsModule))
        {
            Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(HarmonySlotHandoff.CommandsModule));
            return;
        }

        _ = harmony;
        _ = HarmonySlotHandoff.HostOwnsSite(HarmonySlotHandoff.ChatOnUpdate);
        try
        {
            if (CommandParser.RunSelfCheck(out var report))
                Plugin.Logger?.LogInfo("Commands: parser self-check passed.");
            else
                Plugin.Logger?.LogError("Commands: parser self-check failed:\n" + report);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Commands: parser self-check threw: {e}");
        }

        if (!Plugin.IsServerProcess)
        {
            Plugin.Logger?.LogInfo("Commands: client process, chat subscription not installed.");
            return;
        }

        CommandConfig.Install();
        CommandRegistry.EnsureBuiltIns();
        _subscription ??= EventBus.On<ChatMessage>(OnChat, priority: 500);
        Plugin.Logger?.LogInfo($"Commands: listening for '{CommandRegistry.Prefix}' commands on chat.");
    }

    /// <summary>
    /// What this module registered that can outlive a collectible Core generation
    /// (EventBus chat subscriber, live command table).
    /// </summary>
    internal static string DescribePins()
    {
        var holds = new List<string>();
        try
        {
            if (_subscription != null) holds.Add("EventBus.ChatMessage");
            int n = CommandRegistry.LiveCount;
            if (n > 0) holds.Add("commands=" + n);
        }
        catch (Exception e)
        {
            holds.Add("threw:" + e.Message);
        }
        return HarmonySlotHandoff.FormatModulePins(HarmonySlotHandoff.CommandsModule, holds);
    }

    public static void Uninstall()
    {
        _subscription?.Dispose();
        _subscription = null;
        CommandConfig.Uninstall();
    }

    private static void OnChat(ChatMessage ev)
    {
        if (ev.IsCancelled) return;
        // ChatMessage.CharacterEntity is FromCharacter.Character (the speaker). Fall back to
        // User.LocalCharacter inside TryExecute only when that is Entity.Null.
        var result = CommandDispatcher.TryExecute(ev.World, ev.UserEntity, ev.CharacterEntity, ev.Text);
        if (result is CommandResult.NotACommand or CommandResult.Unmatched) return;
        ev.Cancel();
    }
}
