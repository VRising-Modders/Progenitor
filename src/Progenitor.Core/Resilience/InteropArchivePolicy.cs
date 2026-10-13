using System;

namespace Progenitor.Resilience;

/// <summary>Why the interop archive is on or off this run. The report prints the reason.</summary>
public sealed class InteropArchiveDecision
{
    public InteropArchiveDecision(bool archive, string reason)
    {
        Archive = archive;
        Reason = reason ?? "";
    }

    /// <summary>True when this process should keep a copy of its interop set.</summary>
    public bool Archive { get; }

    /// <summary>Why, in the words the log uses. Never "".</summary>
    public string Reason { get; }
}

/// <summary>
/// Job FUPD6 (R53 finding 9). <b>Who pays for the archive.</b>
///
/// <para>FUPD4 shipped <c>ArchiveInterop = true</c> unconditionally. Measured on this machine an
/// interop set is ~180 MB, the default retention is three, and a player who uses the hosted-game
/// launcher has a second game root with its own interop and its own retention — so the shipped
/// default wrote up to about half a gigabyte per root, inside a Steam-managed game folder that
/// "verify integrity of game files" enumerates and a game update does not clean, for an artifact
/// whose only consumer is <c>tools/driftmap --old</c>: a developer tool that is not in the
/// Thunderstore package. An end user could not read what they were paying for.</para>
///
/// <para>So the archive follows the rule <c>Reload.WatchCore</c> already set for the other
/// disk-and-code-path feature that only developers want: <b>off unless this is a developer
/// tree</b>. The dev-tree signal is reused rather than reinvented —
/// <c>tools/deploy.ps1</c> and <c>tools/stage-test-server.ps1</c> write
/// <c>[Reload] WatchCore = true</c> into the client and test-server
/// <c>BepInEx/config/progenitor.cfg</c> and refuse to write it anywhere else, so that
/// key being true <i>is</i> the machine-readable statement "this install is one of ours". An
/// operator who wants the archive without the hot-swap watcher sets
/// <c>[Resilience] ArchiveInterop</c> explicitly, and an explicit setting always wins in both
/// directions. Nothing here branches on client versus server: a server process defaults on for
/// exactly the same reason a client does, because the test server is a developer tree and a
/// stranger's dedicated server is not.</para>
/// </summary>
public static class InteropArchivePolicy
{
    /// <summary>The explicit operator switch: <c>[Resilience] ArchiveInterop</c>.</summary>
    public const string ConfigSection = InteropArchive.ConfigSection;

    public const string ConfigKey = InteropArchive.ConfigKeyArchive;

    /// <summary>The dev-tree signal, owned by <c>CoreWatchPolicy</c>: <c>[Reload] WatchCore</c>.</summary>
    public const string DevTreeSection = "Reload";

    public const string DevTreeKey = "WatchCore";

    /// <summary>Shipped default when nothing says otherwise. <b>False</b> — see the type docs.</summary>
    public const bool DefaultArchiveInterop = false;

    /// <summary>
    /// What the key holds when nobody has decided: <c>auto</c> (job FUPD7, R54 finding 9).
    ///
    /// <para>The key is written into the <c>.cfg</c> so an operator can find it, and that is
    /// exactly why its default value may not be <c>false</c>. <see cref="Decide(bool,bool,bool)"/>
    /// treats a <b>written</b> key as a decision that beats the dev-tree signal in both
    /// directions, so a bind-time default of <c>false</c> would turn the archive off inside every
    /// developer tree the moment BepInEx first wrote the file — the switch would disable itself by
    /// being documented. <c>auto</c> does not parse as a boolean, and an unparseable value is
    /// already "nothing was said", so writing it changes no decision. <c>true</c> and
    /// <c>false</c> are the two values that do.</para>
    /// </summary>
    public const string ConfigValueAuto = "auto";

    public const string ConfigDescription =
        "Keep a per-build copy of BepInEx/interop beside the address tables, so tools/driftmap has "
        + "an --old input after a game update. auto (the default) means: off, unless this is a "
        + "developer tree ([Reload] WatchCore = true, written by tools/deploy.ps1), which turns it "
        + "on by itself. true and false decide it outright and both beat the developer tree. Off is "
        + "the default because each copy is ~180 MB and the default retention of 3 makes ~540 MB per "
        + "game root inside your Steam install, and only the developer tool tools/driftmap can read "
        + "it. auto / true / false.";

    public const string ReasonConfigOn = "[Resilience] " + ConfigKey + " = true";
    public const string ReasonConfigOff = "[Resilience] " + ConfigKey + " = false";
    public const string ReasonDevTree = "developer tree ([Reload] " + DevTreeKey + " = true)";

    public const string ReasonDefaultOff =
        "not a developer tree and [Resilience] " + ConfigKey + " is not set - a copy is ~180 MB "
        + "per build, ~540 MB per game root at the default retention of 3, and only tools/driftmap reads it";

    /// <summary>
    /// The whole rule, as a function of two config reads. <paramref name="configSet"/> is
    /// "the operator wrote the key at all"; a written key wins over the dev-tree signal in both
    /// directions, because an operator who typed <c>false</c> into a developer tree meant it.
    /// </summary>
    public static InteropArchiveDecision Decide(bool configSet, bool configValue, bool devTree)
    {
        if (configSet)
            return new InteropArchiveDecision(configValue, configValue ? ReasonConfigOn : ReasonConfigOff);
        if (devTree)
            return new InteropArchiveDecision(true, ReasonDevTree);
        return new InteropArchiveDecision(DefaultArchiveInterop, ReasonDefaultOff);
    }

    /// <summary>
    /// Read both keys out of <c>&lt;bepInExRoot&gt;/config/progenitor.cfg</c> and decide.
    /// Never throws: an unreadable config is "nothing said", which is off.
    /// </summary>
    public static InteropArchiveDecision Decide(string bepInExRoot)
    {
        bool configSet = InteropArchive.TryReadFlagFromConfig(
            bepInExRoot, ConfigSection, ConfigKey, out bool configValue);
        bool devTree = InteropArchive.TryReadFlagFromConfig(
            bepInExRoot, DevTreeSection, DevTreeKey, out bool watch) && watch;
        return Decide(configSet, configValue, devTree);
    }

    /// <summary>The note the resilience block carries so the choice is greppable in a log.</summary>
    public static string FormatNote(InteropArchiveDecision decision) =>
        "interop archive " + ((decision?.Archive ?? false) ? "on" : "off") + ": "
        + (decision?.Reason ?? ReasonDefaultOff);
}
