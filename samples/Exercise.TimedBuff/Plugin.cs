using BepInEx;
using Progenitor.Buffs;
using Progenitor.Commands;
using Progenitor.Plugins;

namespace Exercise.TimedBuff;

/// <summary>
/// Exercise plugin: one custom timed buff, named and visible on the action bar, that changes how
/// the wearer plays (blood drain cut to a tenth) for a chosen number of seconds. Three commands
/// cover turn-on, check, and take-off-early; there is nothing else to write down for cleanup —
/// <c>ctx.Buffs</c> removes every application this plugin owns on any unload reason, so an
/// unload or removal never leaves the buff on a player.
///
/// <para>Built from <c>docs/buffs.md</c> and the "Buffs" table in <c>docs/api.md</c>. The shape of
/// this file — a policy class with no game types plus a plugin file with the three commands —
/// follows <c>samples/Progenitor.TimedBuffExample</c>, which this repository's own README points a
/// mod author at for exactly this task; that sample was read for the API but this file's code,
/// naming, stat choice (<c>Multiply</c> instead of <c>Set</c>) and command set were written fresh
/// for this exercise.</para>
///
/// <para><b>Applies on the server; loads on both sides.</b> Buff application writes an entity in
/// the server world, so the client copy only registers the definition (needed for the client to
/// know the clone's HUD name/description/icon) and never applies; <c>ctx.Buffs.Apply</c> refuses on
/// the client anyway.</para>
/// </summary>
[BepInPlugin("exercise.timedbuff", "Exercise Timed Buff", "0.1.0")]
[BepInDependency("progenitor")]
// The fresh agent wrote [ProgenitorSide(Side.Server)] here, reasoning (correctly, from the docs it
// had) that only the server applies buffs. Live acceptance showed that is incompatible with the
// ServerMustHave row below: the server then refuses every client that lacks this plugin. Removed by
// the coordinator, with the agent's own reasoning preserved above — the client half exists exactly
// to register the definition for the HUD, which is what the agent's comment already described.
// Added by the coordinator during live acceptance, NOT by the fresh agent that wrote this plugin.
// ctx.Buffs.Stats is refused by PrefabRegistry without this declaration, and nothing in the public
// documentation said so, so the agent had no way to know. Recorded as a framework defect rather
// than an authoring mistake. See docs/buffs.md, "Declaring network compatibility".
[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact)]
public sealed class Plugin : ProgenitorPlugin<AdrenalineContext>
{
    /// <summary>Set on load, cleared on unload, so the command handlers can reach the context.</summary>
    internal static AdrenalineContext? Live;

    protected override void OnLoad(AdrenalineContext ctx)
    {
        // The definition: clone a vanilla base that is already visible on the buff bar and
        // already times out on its own, then register it. Nothing is on anybody yet.
        ctx.Content.DefineBuff(AdrenalinePolicy.BuffId)
            .CloneOf(AdrenalinePolicy.CloneOf)
            .Named(AdrenalinePolicy.Name)
            .Described(AdrenalinePolicy.Description)
            .Register();

        // What it changes about play: blood drain multiplied down to a tenth while it is worn.
        // An unsupported stat would be refused right here, at load, with its reason in the log —
        // not silently the first time someone tries to use the buff.
        ctx.Buffs.Stats(AdrenalinePolicy.BuffId, AdrenalinePolicy.Stats());

        ctx.Commands.Register(typeof(AdrenalineCommands));
        Live = ctx;
        ctx.Log.LogInfo("Exercise timed buff: loaded, buff id '" + AdrenalinePolicy.BuffId + "'");
    }

    protected override void OnUnload(AdrenalineContext ctx, PluginUnloadReason reason)
    {
        Live = null;
        ctx.Log.LogInfo(
            "Exercise timed buff: unloading (" + reason + "). Every application this plugin owns "
            + "is removed by ctx.Buffs as part of this unload, whatever the reason.");
    }
}

/// <summary>Nothing beyond the base context: this plugin only needs Content, Buffs and Commands.</summary>
public sealed class AdrenalineContext : PluginContext { }

/// <summary>
/// One command per verb the exercise asks for: turn it on, ask whether it is on, take it off
/// early. Duration handling and every reply sentence live in <see cref="AdrenalinePolicy"/> so the
/// commands here only wire the API together.
/// </summary>
public static class AdrenalineCommands
{
    /// <summary><c>.adrenaline [seconds]</c> — apply the buff for a number of seconds.</summary>
    [Command("adrenaline", description: "Give yourself Adrenaline Rush for a number of seconds.")]
    public static void Adrenaline(ICommandContext cmd, string? seconds = null)
    {
        var ctx = Plugin.Live;
        if (ctx == null)
        {
            cmd.Reply("The exercise plugin is not loaded.");
            return;
        }

        if (!AdrenalinePolicy.TryParseSeconds(seconds, out float duration, out string reason))
        {
            cmd.Reply(reason);
            return;
        }

        // Default refresh policy is Reject: if this owner already has it here, Apply refuses
        // with AlreadyActive rather than silently retiming it. That refusal already answers
        // "is it on" with an actionable sentence, so there is nothing extra to special-case here.
        var application = ctx.Buffs.Apply(AdrenalinePolicy.BuffId, cmd.CharacterEntity, duration);

        if (application.State == BuffApplicationState.Refused)
        {
            cmd.Reply(application.RefusalReason);
            return;
        }

        // Apply is asked-for, not on: the game builds the buff instance on a later update, so the
        // real answer arrives through OnStateChanged. Capture the user entity, not the command
        // context — a player who disconnects before confirmation should get silence, not a
        // message sent into nothing.
        var user = cmd.UserEntity;
        application.OnStateChanged += a =>
        {
            if (a.State == BuffApplicationState.Active)
                ctx.Chat.SendTo(user, AdrenalinePolicy.ActiveReply(duration));
            else if (a.State != BuffApplicationState.Requested)
                ctx.Chat.SendTo(user, AdrenalinePolicy.EndedReply(a.State));
        };
        cmd.Reply(AdrenalinePolicy.RequestedReply(duration));
    }

    /// <summary><c>.adrenalinestatus</c> — what this plugin's own application currently is.</summary>
    [Command("adrenalinestatus", description: "Show whether Adrenaline Rush is on and its state.")]
    public static void Status(ICommandContext cmd)
    {
        var ctx = Plugin.Live;
        if (ctx == null)
        {
            cmd.Reply("The exercise plugin is not loaded.");
            return;
        }

        var application = ctx.Buffs.Find(AdrenalinePolicy.BuffId, cmd.CharacterEntity);
        if (application == null)
        {
            cmd.Reply(AdrenalinePolicy.StatusNoneReply);
            return;
        }

        cmd.Reply(AdrenalinePolicy.StatusReply(
            application.State,
            application.Instance.IsNone ? "" : application.Instance.ToString()));
    }

    /// <summary><c>.calmdown</c> — take this plugin's own application off early. Idempotent.</summary>
    [Command("calmdown", description: "Take off the Adrenaline Rush buff this plugin gave you.")]
    public static void CalmDown(ICommandContext cmd)
    {
        var ctx = Plugin.Live;
        if (ctx == null)
        {
            cmd.Reply("The exercise plugin is not loaded.");
            return;
        }

        var application = ctx.Buffs.Find(AdrenalinePolicy.BuffId, cmd.CharacterEntity);
        if (application == null)
        {
            cmd.Reply(AdrenalinePolicy.NothingToRemoveReply);
            return;
        }

        // Remove is idempotent by contract (docs/buffs.md, "Removal is idempotent, and narrow"):
        // calling it again, or after expiry, changes nothing and destroys nothing a second time.
        application.Remove();
        cmd.Reply(AdrenalinePolicy.RemovedReply);
    }
}
