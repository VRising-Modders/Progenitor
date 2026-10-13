using System;

#if !PROGENITOR_TESTS
using Progenitor.Buffs;
using Progenitor.Commands;
using Unity.Entities;
#endif

namespace Progenitor.Sample;

/// <summary>
/// <c>.protect on|alive|off</c> — <c>ctx.Debug</c> on the caller's character.
/// <c>on</c> is <c>Protect</c> (debug buff, <c>Immortal</c>, kill-path guard, ignored by foes).
/// <c>alive</c> is <c>KeepAlive</c> (death blocked, still targetable). <c>off</c> is
/// <c>Release</c>, which drops only this plugin's hold.
///
/// <para>Separate from <c>.godmode</c> (kill-path guard only) and from Core's <c>.debug</c>
/// command. This sample calls the plugin facade; Core's command is the product.</para>
///
/// <para>Parsing and every reply line are game-free. Source-checked and game-free tested;
/// not live-verified.</para>
/// </summary>
internal static class SampleDebugProtect
{
    internal const string Area = "protect";

    internal const string UsageReply = "Usage: .protect on | alive | off";

    internal const string NoCharacterReply = "No character entity on this user; there is nothing to protect.";

    internal enum Kind
    {
        On = 1,
        Alive = 2,
        Off = 3,
    }

    internal static bool TryParse(string? token, out Kind kind)
    {
        kind = Kind.On;
        switch ((token ?? "").Trim().ToLowerInvariant())
        {
            case "on": kind = Kind.On; return true;
            case "alive": kind = Kind.Alive; return true;
            case "off": kind = Kind.Off; return true;
            default: return false;
        }
    }

    internal const string PendingNote = " Buff removal lands next frame; the flags above already count it.";

    internal const string KeptOtherNote = " A foe-ignoring buff someone else owns (Core's .debug or another holder) was left on.";

    internal static string Reply(
        Kind kind, bool ok, bool immortal, bool guarded, bool ignoredByFoes, bool invulnerable,
        bool pending = false, bool keptOtherOwnersBuff = false)
    {
        string verb = kind switch
        {
            Kind.On => "Protect",
            Kind.Alive => "KeepAlive",
            _ => "Release",
        };
        return verb + (ok ? " applied." : " did not fully apply.")
            + " ok=" + ok
            + " immortal=" + immortal
            + " guarded=" + guarded
            + " ignoredByFoes=" + ignoredByFoes
            + " invulnerable=" + invulnerable
            + (pending ? PendingNote : "")
            + (keptOtherOwnersBuff ? KeptOtherNote : "")
            + " Unload releases every character this plugin still holds.";
    }

#if !PROGENITOR_TESTS
    internal static void HandleCommand(ICommandContext ctx, Kind kind)
    {
        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }

        var character = ctx.CharacterEntity;
        if (character == Entity.Null)
        {
            ctx.Reply(NoCharacterReply);
            SampleLog.Info(Area, "refused: no character");
            return;
        }

        DebugProtectionResult result = kind switch
        {
            Kind.On => plugin.Debug.Protect(character),
            Kind.Alive => plugin.Debug.KeepAlive(character),
            _ => plugin.Debug.Release(character),
        };
        string reply = Reply(kind, result.Ok, result.Immortal, result.Guarded, result.IgnoredByFoes, result.Invulnerable,
            result.Pending, result.KeptOtherOwnersBuff);
        ctx.Reply(reply);
        SampleLog.Info(Area, kind.ToString().ToLowerInvariant()
            + " " + character.Index + ":" + character.Version
            + " ok=" + result.Ok
            + " pending=" + result.Pending
            + " keptOther=" + result.KeptOtherOwnersBuff);
    }
#endif
}
