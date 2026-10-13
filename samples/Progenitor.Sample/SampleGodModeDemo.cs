using System;
using System.Globalization;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
using Unity.Entities;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Job FSAMP, feature 5. <c>.godmode on|off</c> — <c>ctx.GodMode</c> on its own
/// (<c>docs/modules.md</c> "God mode"), with no buff, no <c>ProjectM.Immortal</c> write and no
/// ModSave grant ledger.
///
/// <b>Why it is separate from <c>.debug</c>.</b> <c>.debug</c> is the *product*: a debug buff
/// that writes <c>Immortal</c>, records an own-grant row, reconciles on world-ready and sweeps on
/// a watchdog — the guard is one line inside all of that. Anyone reading it to learn the guard
/// has to separate five mechanisms first. <c>.godmode</c> is the guard and nothing else, so the
/// live check for the kill-path prefixes is one command and one <c>godmode: guarded=N patched=3</c>
/// line. The two are independent, and since R40 finding 5 that is implemented rather than
/// asserted: each takes its guard under its own owner token —
/// <c>ctx.GodMode.Scope("<see cref="GuardOwner"/>")</c> here, <c>progenitor.debug</c> there — and
/// guards are reference-counted per (entity, owner), so <c>.godmode off</c> drops only this
/// command's hold and a character <c>.debug</c> still owns stays guarded (the reply says so).
/// Before that, one owner per entity plus an unconditional <c>Remove</c> meant whichever command
/// was switched off second silently unguarded a character the other believed was protected.
///
/// <b>Check the return.</b> <c>Add</c> answers <c>false</c> for three different reasons - already
/// guarded, a client process, and "the prefixes are not installed, so this entity would not be
/// protected". Only <c>Describe()</c> tells them apart (<c>patched=0</c>), which is why the reply
/// carries it verbatim: an admin who reads "on." and dies to the first hit is the failure this
/// API was shaped to prevent.
///
/// <b>The two routes nothing else in the repository takes (job J21).</b> <c>.godmode static</c>
/// toggles a guard through the <b>static</b> <c>GodMode.Add(entity, owner)</c> /
/// <c>Remove(entity, owner)</c> pair, passing <c>ctx.Owner</c>: the owner is an argument rather
/// than something the facade bound once, which is only the right shape when the owner is one the
/// unload sweep can reach. <c>.godmode sweep</c> calls <c>GodModeGuard.SweepStale()</c>, the
/// whole-set version of the per-entry stale drop; it runs before the character check, because
/// tidying the guard set has nothing to do with who typed the command.
///
/// Parsing and every reply line are game-free so the test project compiles this file.
/// </summary>
internal static class SampleGodModeDemo
{
    /// <summary>Log/grep area for this feature: <c>Sample godmode: …</c>.</summary>
    internal const string Area = "godmode";

    /// <summary>
    /// This command's guard owner label, distinct from Core's <c>DebugBuff.GuardOwnerLabel</c>
    /// so neither can drop the other's guard (R40 finding 5). It shows up in
    /// <c>ctx.GodMode.Describe()</c> as <c>45:1=[sample.godmode]</c>.
    /// </summary>
    internal const string GuardOwner = "sample.godmode";

    /// <summary>What the caller asked for.</summary>
    internal enum Kind
    {
        /// <summary>No argument: report <c>Describe()</c> without changing anything.</summary>
        Status = 0,

        /// <summary>Guard the caller's character.</summary>
        On = 1,

        /// <summary>Stop guarding it.</summary>
        Off = 2,

        /// <summary>
        /// Job J21. Toggle a guard through the <b>static</b>
        /// <c>GodMode.Add(entity, owner)</c> / <c>Remove(entity, owner)</c> pair instead of
        /// through <c>ctx.GodMode</c>.
        /// </summary>
        Static = 3,

        /// <summary>
        /// Job J21. <c>GodModeGuard.SweepStale()</c> — drop every guard stamped in an older
        /// world-load epoch in one pass, and report how many went.
        /// </summary>
        Sweep = 4,
    }

    /// <summary>Parse the single optional argument. Empty means <see cref="Kind.Status"/>.</summary>
    internal static bool TryParse(string? token, out Kind kind)
    {
        kind = Kind.Status;
        if (string.IsNullOrWhiteSpace(token)) return true;

        switch (token.Trim().ToLowerInvariant())
        {
            case "status": kind = Kind.Status; return true;
            case "on": kind = Kind.On; return true;
            case "off": kind = Kind.Off; return true;
            case "static": kind = Kind.Static; return true;
            case "sweep": kind = Kind.Sweep; return true;
            default: return false;
        }
    }

    /// <summary>Usage line, replied on a token this command does not know.</summary>
    internal const string UsageReply = "Usage: .godmode on | off | static | sweep | status";

    /// <summary>Reply when the caller has no character entity to guard.</summary>
    internal const string NoCharacterReply = "No character entity on this user; there is nothing to guard.";

    /// <summary>Reply for <c>.godmode on</c>. <paramref name="describe"/> is <c>ctx.GodMode.Describe()</c>.</summary>
    internal static string OnReply(bool added, bool alreadyGuarded, string describe)
    {
        if (added)
            return "Guarded. The kill-path prefixes are live and your character is on the guard set. " + describe;
        if (alreadyGuarded)
            return "Already guarded; nothing changed. " + describe;
        return "NOT guarded. Add refused - your character is not protected. " + describe
            + " (patched=0 means the prefix group did not install; the next .godmode on retries, up to 3 times per Core generation.)";
    }

    /// <summary>
    /// Reply for <c>.godmode off</c>. <paramref name="stillGuarded"/> is the state <b>after</b>
    /// the removal: this command drops only its own hold, so a character <c>.debug</c> also
    /// owns is still unkillable and the admin has to be told that, or they will read "Guard
    /// removed." and test a kill path that will not kill them (R40 finding 5).
    /// </summary>
    internal static string OffReply(bool removed, bool stillGuarded, string describe)
    {
        string head = removed
            ? "Guard removed. "
            : "There was no guard of this command's on your character. ";
        if (stillGuarded)
            head += "Your character is STILL guarded by another owner (.debug, or another plugin) - "
                + "this command only drops its own hold. ";
        return head + describe;
    }

    /// <summary>Reply for the bare <c>.godmode</c> / <c>.godmode status</c>.</summary>
    internal static string StatusReply(bool guarded, string describe) =>
        (guarded ? "Your character is guarded. " : "Your character is not guarded. ") + describe;

    // ---- Job J21: the static route, and the stale sweep ---------------------------------------

    /// <summary>
    /// Reply for <c>.godmode static</c>. The static <c>GodMode.Add(entity, owner)</c> takes the
    /// owner as an argument instead of binding it once, which makes it the right shape in exactly
    /// two situations and the wrong one everywhere else.
    ///
    /// <para>Right: (1) Core itself, which passes <c>null</c> and is swept by
    /// <c>GodModeGuard.Clear()</c>; (2) a caller that already holds the owner object and owns
    /// <b>every</b> removal path for the guards it takes. Wrong: anything that invents a private
    /// token, because the unload sweep is <c>RemoveAll(pluginInstance)</c> and it reaches a
    /// plain object only by reference equality — a private token nothing hands the reloader
    /// leaves an unkillable character behind after the plugin is gone. This command therefore
    /// passes <c>ctx.Owner</c>, the same object <c>ctx.GodMode</c> would have used, and the
    /// difference you can see in <c>Describe()</c> is the owner label: a
    /// <c>ctx.GodMode.Scope("…")</c> hold prints its label, this one prints the plugin type's
    /// name.</para>
    /// </summary>
    internal static string StaticReply(bool nowGuarded, bool changed, string describe)
    {
        string head = nowGuarded
            ? (changed
                ? "Guarded through the static GodMode.Add(entity, ctx.Owner). "
                : "The static route already held your character; nothing changed. ")
            : (changed
                ? "Dropped the static GodMode.Remove(entity, ctx.Owner) hold. "
                : "The static route held nothing on your character. ");
        return head
            + "The static form takes the owner per call instead of binding it, so it is only right "
            + "when the owner is one the unload sweep can reach - ctx.Owner here. A private token "
            + "would leave an unkillable character behind when this plugin unloads. " + describe;
    }

    /// <summary>
    /// Reply for <c>.godmode sweep</c>. Nothing is broken when this drops zero: guards are also
    /// dropped lazily by <c>IsGuarded</c> / <c>ShouldBlock</c> / <c>Count</c>, so the whole-set
    /// pass usually finds the set already tidy. It earns its place after a save reload, where
    /// every retained <c>index:version</c> pair now names somebody else's entity.
    /// </summary>
    internal static string SweepReply(int dropped, int epoch, string describe) =>
        "GodModeGuard.SweepStale() dropped " + dropped.ToString(CultureInfo.InvariantCulture)
        + " guard(s) stamped before world-load epoch "
        + epoch.ToString(CultureInfo.InvariantCulture)
        + ". Zero is the normal answer - IsGuarded, ShouldBlock and Count each drop a stale entry "
        + "on their own, so the set is usually tidy already; the whole-set pass is for just after a "
        + "save reload, when every retained entity id names something else. " + describe;

#if !PROGENITOR_TESTS
    /// <summary>Server entry point for <c>.godmode</c>.</summary>
    internal static void HandleCommand(ICommandContext ctx, Kind kind)
    {
        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }

        // The stale sweep is about the guard set, not about the caller, so it runs before the
        // character check - an admin with no character can still tidy the set.
        if (kind == Kind.Sweep)
        {
            int sweepEpoch = Progenitor.WorldState.WorldEpoch.Current;
            int sweepDropped = Progenitor.GodMode.GodModeGuard.SweepStale();
            string sweepDescribe = plugin.GodMode.Describe();
            ctx.Reply(SweepReply(sweepDropped, sweepEpoch, sweepDescribe));
            SampleLog.Info(Area, "sweep epoch=" + sweepEpoch + " dropped=" + sweepDropped
                + " | " + sweepDescribe);
            return;
        }

        var character = ctx.CharacterEntity;
        if (character == Entity.Null)
        {
            ctx.Reply(NoCharacterReply);
            SampleLog.Info(Area, "refused: no character entity");
            return;
        }

        // Own owner token: Core's .debug guards the same character under progenitor.debug, and neither
        // may drop the other's hold (R40 finding 5).
        var godMode = plugin.GodMode.Scope(GuardOwner);
        string label = character.Index.ToString(CultureInfo.InvariantCulture)
            + ":" + character.Version.ToString(CultureInfo.InvariantCulture);

        switch (kind)
        {
            case Kind.On:
            {
                // "Already guarded" means this command already holds it — a guard .debug owns
                // is somebody else's, and adding ours on top is a real change.
                bool wasHeld = godMode.HoldsGuard(character);
                bool added = godMode.Add(character);
                string describe = godMode.Describe();
                ctx.Reply(OnReply(added, !added && wasHeld, describe));
                SampleLog.Info(Area, "add entity=" + label + " added=" + added
                    + " wasHeld=" + wasHeld + " | " + describe);
                return;
            }

            case Kind.Off:
            {
                bool removed = godMode.Remove(character);
                bool stillGuarded = godMode.IsGuarded(character);
                string describe = godMode.Describe();
                ctx.Reply(OffReply(removed, stillGuarded, describe));
                SampleLog.Info(Area, "remove entity=" + label + " removed=" + removed
                    + " stillGuarded=" + stillGuarded + " | " + describe);
                return;
            }

            case Kind.Static:
            {
                // Job J21. The static pair, with the owner passed per call. ctx.Owner is the
                // object PluginReloader.SweepRegistrations hands GodMode.RemoveAll on unload, so
                // this hold is swept exactly like a ctx.GodMode one - the only difference is that
                // it carries no label and Describe() prints the plugin type's name instead.
                bool held = Progenitor.GodMode.GodMode.IsGuardedBy(character, plugin.Owner);
                bool changed = held
                    ? Progenitor.GodMode.GodMode.Remove(character, plugin.Owner)
                    : Progenitor.GodMode.GodMode.Add(character, plugin.Owner);
                bool nowGuarded = Progenitor.GodMode.GodMode.IsGuardedBy(character, plugin.Owner);
                string describe = godMode.Describe();
                ctx.Reply(StaticReply(nowGuarded, changed, describe));
                SampleLog.Info(Area, "static entity=" + label + " held=" + held
                    + " changed=" + changed + " nowGuarded=" + nowGuarded + " | " + describe);
                return;
            }

            default:
            {
                bool guarded = godMode.IsGuarded(character);
                string describe = godMode.Describe();
                ctx.Reply(StatusReply(guarded, describe));
                SampleLog.Info(Area, "status entity=" + label + " guarded=" + guarded + " | " + describe);
                return;
            }
        }
    }
#endif
}
