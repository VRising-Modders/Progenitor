#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using Progenitor.Config;
using ProjectM.Network;
using Unity.Entities;

namespace Progenitor.Networking;

/// <summary>
/// NOPROGNOTICE: how the notice reaches a player without Progenitor. One channel is chosen in one place
/// (<see cref="Handshake.NoAnswerNoticeSender"/>); the countdown and the words do not depend on it.
/// </summary>
internal interface INoProgenitorNoticeSender
{
    /// <summary>Send <paramref name="notice"/> to the user entity <paramref name="user"/> only. False when nothing could be sent.</summary>
    bool Send(Entity user, NoProgenitorNotice notice);
}

/// <summary>
/// The default channels (live 2026-10-05, Steam client; the vanilla UI is the same on an unmodded client):
/// <list type="bullet">
/// <item>the short line as <b>Local chat addressed to the player as the speaker</b>: the game draws it as floating text over the
/// player's own character, mid-screen, plus a <c>[Local] &lt;name&gt; …</c> chat line; only that user receives it;</item>
/// <item>the full line as <b>System chat</b>: it pops the chat box open, unfocused, bottom-left, small orange text, for about
/// 14 s: visible but easy to miss.</item>
/// </list>
/// Both through <see cref="Progenitor.Chat.Chat.SendTo(World?, Entity, string, ChatMessageType, Entity)"/>, which sends to
/// one <c>ConnectedUser</c> only.
/// </summary>
internal sealed class ChatNoticeSender : INoProgenitorNoticeSender
{
    public bool Send(Entity user, NoProgenitorNotice notice)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return false;
        bool any = false;
        if (notice.Short.Length > 0)
            any |= Progenitor.Chat.Chat.SendTo(world, user, notice.Short, ChatMessageType.Local, user);
        if (!string.IsNullOrEmpty(notice.Full))
            any |= Progenitor.Chat.Chat.SendTo(world, user, notice.Full!, ChatMessageType.System, Entity.Null);
        return any;
    }
}

/// <summary>
/// NOPROGNOTICE (docs/modules.md, "What a player without Progenitor sees"): the answer deadline as a countdown that
/// is held while the player has no character, with a repeated notice before the kick.
/// </summary>
public static unsafe partial class Handshake
{
    /// <summary>The notice's channel. Change it here (or set another sender) without touching the countdown.</summary>
    internal static INoProgenitorNoticeSender NoAnswerNoticeSender = new ChatNoticeSender();

    private static ConfigEntry<int>? _noAnswerNoticeSeconds;
    private static ConfigEntry<int>? _noAnswerNoticeRepeatSeconds;
    private static ConfigEntry<int>? _noAnswerCreationLimitSeconds;
    private static readonly Dictionary<int, NoAnswerNoticeState> NoAnswerCountdowns = new();

    private static void BindNoAnswerNotice(ConfigFile file)
    {
        _noAnswerNoticeSeconds = ConfigHelper.Bind(file, "Networking", "NoAnswerNoticeSeconds", NoAnswerNoticeSettings.DefaultNoticeSeconds,
            "Seconds after the countdown starts before a player whose game never answered the handshake (no Progenitor) gets a chat "
            + "message saying what this server needs and when they will be disconnected. 0: no chat message is sent (the disconnect still happens). It must be shorter than "
            + "HandshakeAnswerTimeoutSeconds (a longer value is moved to one second before the disconnect).");
        _noAnswerNoticeRepeatSeconds = ConfigHelper.Bind(file, "Networking", "NoAnswerNoticeRepeatSeconds", NoAnswerNoticeSettings.DefaultRepeatSeconds,
            "Seconds between repeats of that message until the disconnect (at least 2); each repeat says the seconds left.");
        _noAnswerCreationLimitSeconds = ConfigHelper.Bind(file, "Networking", "NoAnswerCreationLimitSeconds", NoAnswerNoticeSettings.DefaultCreationLimitSeconds,
            "While such a player has no character yet (character creation shows no chat), the message and the disconnect wait; the "
            + "countdown starts when the character is in the world. After this many seconds from the ask without a character, the "
            + "disconnect happens anyway.");
    }

    private static NoAnswerNoticeSettings ReadNoAnswerNoticeSettings(int timeout)
    {
        int Read(ConfigEntry<int>? e, int fallback)
        {
            try { return e?.Value ?? fallback; }
            catch { return fallback; }
        }

        return new NoAnswerNoticeSettings(timeout,
            Read(_noAnswerNoticeSeconds, NoAnswerNoticeSettings.DefaultNoticeSeconds),
            Read(_noAnswerNoticeRepeatSeconds, NoAnswerNoticeSettings.DefaultRepeatSeconds),
            Read(_noAnswerCreationLimitSeconds, NoAnswerNoticeSettings.DefaultCreationLimitSeconds));
    }

    /// <summary>The user has a character in the world (<c>User.LocalCharacter</c> set to an entity that exists).</summary>
    private static bool HasCharacter(World world, Entity userEntity)
    {
        try
        {
            if (world == null || !world.IsCreated) return false;
            var em = world.EntityManager;
            if (!em.Exists(userEntity) || !em.HasComponent<User>(userEntity)) return false;
            var character = em.GetComponentData<User>(userEntity).LocalCharacter._Entity;
            return character != Entity.Null && em.Exists(character);
        }
        catch { return false; }
    }

    /// <summary>The readable list the notice names: the server's enforced mods (name and version), then the admins' required client mods.</summary>
    internal static List<string> NoAnswerNoticeMods()
    {
        var list = new List<string>();
        Dictionary<string, string> names;
        try { names = ModManifest.RequiredModNames(ModManifest.EnumerateLoaded()); }
        catch { names = new Dictionary<string, string>(); }
        foreach (var rec in CopyRequired())
        {
            if (string.IsNullOrEmpty(rec.Guid) || Compare.IsReservedGuid(rec.Guid) || !Compare.LevelRequiresAnswer(rec.Level)) continue;
            string name = names.TryGetValue(rec.Guid.ToLowerInvariant(), out var n) ? n : rec.Guid;
            list.Add(rec.Version.Length > 0 ? name + " " + rec.Version : name);
        }

        var policy = SafePolicy();
        if (policy != null)
            foreach (var r in policy.Required)
                list.Add(r.Version.Length > 0 && r.Rule != ClientModRule.Any ? r.Display + " " + r.Version : r.Display);
        return list;
    }

    /// <summary>
    /// The armed answer deadline, run as a countdown: held while the user has no character (up to the creation limit),
    /// notices from <c>NoAnswerNoticeSeconds</c> every <c>NoAnswerNoticeRepeatSeconds</c>, then the existing kick. Stops
    /// at once when the user answers, leaves, or nothing requires an answer any more.
    /// </summary>
    private static void RunNoAnswerCountdown(World world, Entity userEntity, int userIndex, string name, int generation, int timeout)
    {
        var state = new NoAnswerNoticeState(generation, name, LiveClock());
        lock (NoAnswerCountdowns) NoAnswerCountdowns[userIndex] = state;
        NoAnswerNoticeRecords.Note(userIndex, state);

        void Tick()
        {
            try
            {
                lock (NoAnswerCountdowns)
                    if (!NoAnswerCountdowns.TryGetValue(userIndex, out var current) || !ReferenceEquals(current, state)) return;
                if (AnswerWatch.GetStatus(userIndex) != HandshakeAnswerWatch.Status.Pending)
                {
                    EndCountdown(userIndex, state, "answered or left (" + AnswerWatch.Token(userIndex) + ")");
                    return;
                }

                if (!RequiresAnswer(CopyRequired()))
                {
                    AnswerWatch.TryExpire(userIndex, generation, serverStillRequiresAnswer: false, out _);
                    Plugin.Logger?.LogInfo("Handshake: pending deadline canceled; no plugin requires a handshake answer");
                    EndCountdown(userIndex, state, "nothing requires an answer");
                    return;
                }

                bool wasHeld = state.CountdownStart == null;
                var step = NoAnswerNoticePlan.Step(state, LiveClock(), HasCharacter(world, userEntity), ReadNoAnswerNoticeSettings(timeout));
                if (wasHeld && state.CountdownStart != null && state.HeldForCreation)
                    Plugin.Logger?.LogInfo("Handshake: " + name + " has a character now; the " + timeout.ToString(CultureInfo.InvariantCulture)
                        + " s answer countdown starts");
                switch (step.Step)
                {
                    case NoAnswerStep.Kick:
                        if (!AnswerWatch.TryExpire(userIndex, generation, RequiresAnswer(CopyRequired()), out var due))
                        {
                            EndCountdown(userIndex, state, "not due");
                            return;
                        }

                        EndCountdown(userIndex, state, state.CountdownStart == null ? "kicked (character creation limit)" : "kicked");
                        SendResultAndKick(world, userEntity, userIndex, due.Name, due.Reason, timeoutKick: true);
                        return;

                    case NoAnswerStep.Notice:
                        var notice = NoProgenitorNotice.For(state.NoticesSent, step.SecondsLeft, JoinServerSettings.NoProgenitorTemplate(),
                            NoAnswerNoticeText.FullLineDue(state.NoticesSent) ? NoAnswerNoticeMods() : null);
                        bool sent;
                        try { sent = NoAnswerNoticeSender.Send(userEntity, notice); }
                        catch (Exception e)
                        {
                            sent = false;
                            Plugin.Logger?.LogWarning("Handshake: the notice to " + name + " threw: " + e.Message);
                        }

                        if (sent)
                        {
                            state.NoticesSent++;
                            state.LastShort = notice.Short;
                            if (notice.Full != null)
                            {
                                state.FullSent++;
                                state.LastText = notice.Full;
                            }

                            state.SecondsShown.Add(step.SecondsLeft);
                        }

                        Plugin.Logger?.LogInfo("Handshake: notice to " + name + " (no handshake answer; " + step.SecondsLeft.ToString(CultureInfo.InvariantCulture)
                            + " s left): sent=" + (sent ? "true" : "false") + (notice.Full != null && sent ? " \"" + notice.Full + "\"" : ""));
                        NoAnswerNoticeRecords.Note(userIndex, state);
                        break;
                }

                Dev.MainThread.PostDelayed(step.Delay, Tick, DeadlineOwner);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"Handshake.AnswerDeadline: {e}");
            }
        }

        Tick();
    }

    private static void EndCountdown(int userIndex, NoAnswerNoticeState state, string why)
    {
        lock (NoAnswerCountdowns)
            if (NoAnswerCountdowns.TryGetValue(userIndex, out var current) && ReferenceEquals(current, state)) NoAnswerCountdowns.Remove(userIndex);
        NoAnswerNoticeRecords.Note(userIndex, state, why);
    }

    private static void NoAnswerOnDisconnect(int userIndex)
    {
        lock (NoAnswerCountdowns) NoAnswerCountdowns.Remove(userIndex);
    }

    private static void NoAnswerUninstall()
    {
        lock (NoAnswerCountdowns) NoAnswerCountdowns.Clear();
        _noAnswerNoticeSeconds = null;
        _noAnswerNoticeRepeatSeconds = null;
        _noAnswerCreationLimitSeconds = null;
    }
}
#endif
