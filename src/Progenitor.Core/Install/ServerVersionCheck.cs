using System;
using Progenitor.Networking;
using Progenitor.Plugins;

namespace Progenitor.Install;

/// <summary>
/// Mod compatibility C3 (docs/mod-compatibility.md, Tie-in with auto-update): would a server whose remembered version of a
/// mod is <c>S</c> accept this client at version <c>V</c>? The client's prediction of the server's join evaluation, used by
/// the pre-join prompt, the "not now" check while connected, and the kept-copy choice. Game-free.
/// <list type="number">
/// <item>The server checks nothing for this mod (no requirement): any version.</item>
/// <item>V equals S: passes.</item>
/// <item>The server's match is known (<see cref="ServerRequirement.Match"/> from its signed catalog, else Exact or Same minor
/// for all from its request bits, read through the rewritten rule bytes): <c>exact</c> only S; <c>minor</c> the same
/// major.minor; <c>at-least</c> S or newer; <c>any</c> anything.</item>
/// <item>Declared ranges: V newer than S needs V's own range (from its inspected <c>progenitor.json</c>) to include S; V
/// older than S needs the server's range (<see cref="ServerRequirement.Peers"/>) to include V. When the deciding range is
/// not known, today's cap applies: the server's request rule on S. Ranges are used only once the server's catalog has
/// given this mod's facts (<see cref="ServerRequirement.Peers"/>); before that, today's cap.</item>
/// </list>
/// </summary>
internal static class ServerVersionCheck
{
    internal const string MatchExact = "exact";
    internal const string MatchMinor = "minor";
    internal const string MatchAtLeast = "at-least";
    internal const string MatchAny = "any";

    /// <summary>The remembered subject for the pre-join prompt.</summary>
    internal const string ThisServer = "this server";

    /// <summary>The subject while connected (the Install tab's <c>not now</c>).</summary>
    internal const string ServerYouAreOn = "the server you are on";

    /// <summary>A match word normalized (<c>exact</c>, <c>minor</c>, <c>at-least</c>, <c>any</c>), or empty when unknown.</summary>
    internal static string NormalizeMatch(string? word)
    {
        string w = (word ?? "").Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        return w switch
        {
            "exact" => MatchExact,
            "minor" or "same-minor" => MatchMinor,
            "at-least" or "atleast" or "or-newer" => MatchAtLeast,
            "any" or "none" => MatchAny,
            _ => "",
        };
    }

    /// <summary>
    /// How the server judges this mod when it does not use ranges: its catalog's <c>match</c>, else (Exact or Same minor
    /// for all) the rule byte its request carried, which the server rewrote for the mode and any per-mod rule. Empty:
    /// declared ranges.
    /// </summary>
    internal static string EffectiveMatch(ServerRequirement? req)
    {
        if (req == null) return "";
        string m = NormalizeMatch(req.Match);
        if (m.Length > 0) return m;
        string mode = NormalizeMatch(req.Matching);
        if (mode != MatchExact && mode != MatchMinor) return "";
        return req.Rule switch
        {
            NetworkVersionRule.Minor => MatchMinor,
            NetworkVersionRule.None => MatchAny,
            _ => MatchExact,
        };
    }

    /// <summary>True when the server accepts <paramref name="version"/>.</summary>
    internal static bool Accepts(ServerRequirement? req, string? version, string? candidateRange) =>
        Reason(req, version, candidateRange, ThisServer).Length == 0;

    /// <summary>
    /// Why the server would refuse <paramref name="version"/>, or empty when it accepts it. <paramref name="candidateRange"/>
    /// is the effective peer range of the build at <paramref name="version"/> (its inspected <c>progenitor.json</c>, or the
    /// loaded declaration), or null when not known. <paramref name="subject"/> names the server in the sentence.
    /// </summary>
    internal static string Reason(ServerRequirement? req, string? version, string? candidateRange, string subject = ThisServer)
    {
        if (req == null) return "";
        string s = (req.Version ?? "").Trim();
        string v = (version ?? "").Trim();
        if (string.Equals(s, v, StringComparison.OrdinalIgnoreCase)) return "";
        int? order = CompatVersion.Compare(v, s);

        // Review fix: the server's exact checks compare the version strings (case-insensitive), so 1.2.0+abc and 1.2.0+def,
        // equal as versions, are still two versions to an exact match or rule; only the range judgment treats them as one.
        switch (EffectiveMatch(req))
        {
            case MatchAny:
                return "";
            case MatchExact:
                return subject + " requires exactly " + s;
            case MatchMinor:
                return Handshake.Compare.SameMinor(s, v) ? "" : subject + " requires " + (CompatVersion.MinorOf(s) ?? s) + ".x";
            case MatchAtLeast:
                return order >= 0 ? "" : subject + " requires " + s + " or newer";
        }

        // Review fix: ranges decide only when this server's catalog facts for the mod are known (its peers). Without them
        // (a first join, an older server that judges by its rule bytes, a per-mod rule the request row carries as a
        // rewritten rule byte under declared ranges) the request's rule is the only evidence, so today's cap applies.
        if ((req.Peers ?? "").Trim().Length == 0) return LegacyReason(req, v, subject);
        if (order == 0) return "";
        if (order > 0 && TryRange(candidateRange, out var mine))
            return mine!.Includes(s) ? "" : v + " works with " + mine.Describe() + ", not " + Possessive(subject) + " " + s;
        if (order < 0 && TryRange(req.Peers, out var theirs))
            return theirs!.Includes(v) ? "" : Possessive(subject) + " " + s + " works with " + theirs.Describe();
        return LegacyReason(req, v, subject);
    }

    /// <summary>Today's cap (phase 1): the request rule on the server's version, with its words.</summary>
    internal static string LegacyReason(ServerRequirement req, string version, string subject = ThisServer)
    {
        if (req.Rule == NetworkVersionRule.None) return "";
        if (Handshake.Compare.VersionsMatch(req.Version, version, req.Rule)) return "";
        // C4 wording fix: an Exact rule says so (the Install tab's "not now" and the pre-join prompt's "Not offered").
        return subject + " needs " + (req.Rule == NetworkVersionRule.Exact ? "exactly " + req.Version : PreJoinOffers.MinorOf(req.Version) + ".x");
    }

    private static bool TryRange(string? text, out CompatRange? range)
    {
        range = null;
        string t = (text ?? "").Trim();
        return t.Length > 0 && CompatRange.TryParse(t, out range, out _);
    }

    private static string Possessive(string subject) => subject + "'s";
}
