using BepInEx;
using Progenitor.Buffs;
using Progenitor.Commands;
using Progenitor.Plugins;

namespace Progenitor.TimedBuffExample;

/// <summary>
/// The whole timed-custom-stat-buff workflow, in one server plugin and three calls:
///
/// <list type="number">
/// <item><c>ctx.Content.DefineBuff(id).CloneOf(vanilla)…Register()</c> — the definition. Once, at
/// load. A registration is not an application: nothing is on anybody yet.</item>
/// <item><c>ctx.Buffs.Stats(id, …)</c> — what the buff does. Once, at load, beside the
/// definition.</item>
/// <item><c>ctx.Buffs.Apply(…)</c> — put it on someone, for a while. Per use.</item>
/// </list>
///
/// <para>There is no <c>OnUnload</c> teardown for the buff: <c>ctx.Buffs</c> tracks every
/// application and the context removes them on every unload reason. The log line below is only so
/// the reader can watch it happen.</para>
///
/// <para><b>Applies on the server; loads on both sides.</b> The client copy registers the same
/// definition (that is how the client learns the clone's HUD name, description and icon) and never
/// applies: <c>ctx.Buffs.Apply</c> refuses there with an actionable reason.</para>
/// </summary>
[BepInPlugin("progenitor.timedbuffexample", "Progenitor Timed Buff Example", "0.1.0")]
[BepInDependency("progenitor")]
// NOT [ProgenitorSide(Side.Server)] — deliberately, and this was also found live on 2026-09-13.
// The NetworkCompatibility row below is ServerMustHave, which is what Content.Edit requires, and
// that makes the server refuse any client that does not have this plugin. A server-only buff
// plugin therefore kicks every client at the handshake ("Mod mismatch: expected 0.1.0, got
// missing"). It also matches what this buff needs anyway: the client half registers the definition
// so the HUD can resolve the clone's name, description and icon. The client never applies the buff
// — ctx.Buffs.Apply refuses on the client with an actionable reason. Deploy this to BOTH sides.
// REQUIRED for ctx.Buffs.Stats, and found the hard way in live acceptance on 2026-09-13.
// Stats writes the stat component onto this plugin's buff clone through ctx.Content.Edit, and
// PrefabRegistry refuses any Content.Edit from a plugin that has not declared this attribute --
// "so both sides apply the same pass". Without it the buff still defines, still applies and still
// reports Active, but carries NO stat modification at all: a buff that does nothing, with the
// refusal visible only in the server log. See docs/buffs.md, "Declaring network compatibility".
[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact)]
public sealed class Plugin : ProgenitorPlugin<TimedBuffContext>
{
    /// <summary>Set so the command handlers can reach the context. Cleared on unload.</summary>
    internal static TimedBuffContext? Live;

    protected override void OnLoad(TimedBuffContext ctx)
    {
        // 1. The definition. CloneOf a verified vanilla base: the clone inherits its buff-bar
        //    visibility and its LifeTime, neither of which a plugin should be inventing.
        ctx.Content.DefineBuff(TimedBuffPolicy.BuffId)
            .CloneOf(TimedBuffPolicy.CloneOf)
            .Named(TimedBuffPolicy.Name)
            .Described(TimedBuffPolicy.Description)
            .Register();

        // 2. What it does. One supported stat modifier, written onto this plugin's own clone
        //    through Core's ordered prefab-edit pass. An unsupported modifier is refused here
        //    with its reason, in the log, at load - not silently at apply time.
        ctx.Buffs.Stats(TimedBuffPolicy.BuffId, TimedBuffPolicy.Stats());

        ctx.Commands.Register(typeof(TimedBuffCommands));
        Live = ctx;
        ctx.Log.LogInfo("Timed buff example: loaded, buff id '" + TimedBuffPolicy.BuffId + "'");
    }

    protected override void OnUnload(TimedBuffContext ctx, PluginUnloadReason reason)
    {
        Live = null;
        ctx.Log.LogInfo(
            "Timed buff example: unloading, reason=" + reason
            + "; the context removes every application this plugin applied and drops the definition");
    }
}

/// <summary>Nothing extra; the base context carries Content, Buffs and Commands.</summary>
public sealed class TimedBuffContext : PluginContext { }

/// <summary>
/// Three commands, one per verb of the workflow. Each one is careful about the difference between
/// <em>asked</em> and <em>on</em>: the apply reply says the buff was requested, and the handler
/// that fires when it is confirmed says it is on.
/// </summary>
public static class TimedBuffCommands
{
    /// <summary><c>.rested [seconds]</c> — apply, or refresh if it is already on.</summary>
    [Command("rested", description: "Give yourself the example buff for a number of seconds.")]
    public static void Rested(ICommandContext cmd, string? seconds = null)
    {
        var ctx = Plugin.Live;
        if (ctx == null)
        {
            cmd.Reply("The example plugin is not loaded.");
            return;
        }

        if (!TimedBuffPolicy.TryParseSeconds(seconds, out float duration, out string reason))
        {
            cmd.Reply(reason);
            return;
        }

        var application = ctx.Buffs.ApplyOrRefresh(
            TimedBuffPolicy.BuffId, cmd.CharacterEntity, duration);

        // A refusal is a value with a sentence in it, not an exception. Show it.
        if (application.State == BuffApplicationState.Refused)
        {
            cmd.Reply(application.RefusalReason);
            return;
        }

        if (application.IsActive)
        {
            // Refresh of something already confirmed. IsActive alone is NOT enough to report
            // success: on the ApplyOrRefresh route the handle we get back is the application that
            // was already live, so it stays Active and its Refusal still describes the original
            // apply even when the refresh itself was refused. Before this check, a refused refresh
            // printed "it is on for N seconds" to the player while nothing had been written.
            if (application.LastRefreshRefusal != BuffRefusal.None)
            {
                cmd.Reply(application.LastRefreshRefusalReason);
                return;
            }

            cmd.Reply(TimedBuffPolicy.ActiveReply(duration));
            return;
        }

        // Requested. Tell the truth about that, and report the real answer when it arrives.
        //
        // The handler fires on a later frame, so it captures the user entity and sends through
        // ctx.Chat rather than holding the ICommandContext: that context is a snapshot taken at
        // dispatch, and a player who disconnected in between should get no message rather than a
        // stale one. ctx.Chat.SendTo returns false for a user who is gone.
        var user = cmd.UserEntity;
        application.OnStateChanged += a =>
        {
            if (a.State == BuffApplicationState.Active)
                ctx.Chat.SendTo(user, TimedBuffPolicy.ActiveReply(duration));
            else if (a.State != BuffApplicationState.Requested)
                ctx.Chat.SendTo(user, TimedBuffPolicy.EndedReply(a.State));
        };
        cmd.Reply(TimedBuffPolicy.RequestedReply(duration));
    }

    /// <summary><c>.unrested</c> — remove this plugin's application, and only that one.</summary>
    [Command("unrested", description: "Remove the example buff this plugin gave you.")]
    public static void Unrested(ICommandContext cmd)
    {
        var ctx = Plugin.Live;
        if (ctx == null)
        {
            cmd.Reply("The example plugin is not loaded.");
            return;
        }

        var application = ctx.Buffs.Find(TimedBuffPolicy.BuffId, cmd.CharacterEntity);
        if (application == null)
        {
            cmd.Reply(TimedBuffPolicy.NothingToRemoveReply);
            return;
        }

        // Idempotent: calling this twice, or after the buff already expired, changes nothing.
        application.Remove();
        cmd.Reply(TimedBuffPolicy.RemovedReply);
    }

    /// <summary><c>.restedstatus</c> — what the API actually knows right now.</summary>
    [Command("restedstatus", description: "Show the example buff's application state.")]
    public static void Status(ICommandContext cmd)
    {
        var ctx = Plugin.Live;
        if (ctx == null)
        {
            cmd.Reply("The example plugin is not loaded.");
            return;
        }

        var application = ctx.Buffs.Find(TimedBuffPolicy.BuffId, cmd.CharacterEntity);
        if (application == null)
        {
            cmd.Reply(TimedBuffPolicy.StatusNoneReply);
            return;
        }

        cmd.Reply(TimedBuffPolicy.StatusReply(
            application.State,
            application.Instance.IsNone ? "" : application.Instance.ToString()));
    }
}
