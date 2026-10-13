#if !PROGENITOR_TESTS
using System;
using System.Globalization;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Progenitor.Config;

namespace Progenitor.Resilience;

/// <summary>
/// Job FUPD7 (R54 finding 9). <b>The two <c>[Resilience]</c> keys, written where an operator can
/// read them.</b>
///
/// <para>Both archive settings are read by hand off disk, and they have to be: the pass runs on a
/// background thread in a file that compiles without BepInEx and without the game
/// (<see cref="InteropArchive.TryReadFromConfig"/>). But "read by hand" was silently also "never
/// written": BepInEx only puts a key and its description into
/// <c>BepInEx/config/progenitor.cfg</c> when something <see cref="ConfigFile.Bind{T}"/>s
/// it, and nothing did. So both <c>ConfigDescription</c> constants — including the one R53 finding
/// 9 was raised to rewrite — were prose no human path could reach, and an operator opening the
/// file found no mention of the archive at all, on exactly the machine where the switch matters
/// (a player's install, where it now ships off).</para>
///
/// <para>The fix is the shape <c>[Compat] RewriteMode</c> already uses (<c>Plugin.Install</c>):
/// the <b>main thread</b> binds the keys purely so they appear in the file with their
/// descriptions, and the background pass goes on reading the file by hand. The bind is not on the
/// read path and must never become one — a background thread cannot hold a
/// <see cref="ConfigEntry{T}"/>, and Core must not leave a Core-typed entry in Bootstrap's
/// process-lifetime <see cref="ConfigFile"/> (evidence 65). Both keys are BCL types, so
/// <see cref="ConfigHelper.Bind{T}"/> accepts them on the shared file and
/// <c>ConfigHelper.UninstallAll</c> drops the entries on Core Uninstall, leaving the on-disk
/// values alone.</para>
///
/// <para><b>Order.</b> This runs from <see cref="Resilience.Install"/> before the pass thread is
/// started, so on a fresh install the keys are usually on disk by the time the pass reads them —
/// but nothing depends on that. Both hand reads treat a missing key as "nothing was said" and
/// fall back to the same defaults the bind writes, so the pass behaves identically whether it
/// wins the race, loses it, or runs in a process where the bind never happened at all (the
/// offline runner, and any host with no chainloader).</para>
///
/// <para><b>Why the on/off key is bound as a string.</b> A <c>bool</c> bind would write
/// <c>ArchiveInterop = false</c> as its default, and
/// <see cref="InteropArchivePolicy.Decide(bool,bool,bool)"/> reads a written key as a decision
/// that beats the developer-tree signal — so documenting the switch would have turned it off in
/// every developer tree. The bound default is
/// <see cref="InteropArchivePolicy.ConfigValueAuto"/>, which does not parse as a boolean and is
/// therefore already "nothing was said".</para>
/// </summary>
internal static class InteropArchiveConfig
{
    /// <summary>
    /// Bind <c>[Resilience] ArchiveInterop</c> and <c>[Resilience] InteropArchives</c> on the
    /// shared config file. Never throws and never returns a value anything reads: the only effect
    /// that matters is the two keys and their descriptions existing in the <c>.cfg</c>.
    /// </summary>
    internal static void Bind() =>
        SharedConfig.BindWhenAvailable("[" + InteropArchivePolicy.ConfigSection + "] archive keys", BindFrom);

    private static void BindFrom(ConfigFile file)
    {
        try
        {
            ConfigHelper.Bind(
                file,
                InteropArchivePolicy.ConfigSection,
                InteropArchivePolicy.ConfigKey,
                InteropArchivePolicy.ConfigValueAuto,
                InteropArchivePolicy.ConfigDescription);

            ConfigHelper.Bind(
                file,
                InteropArchive.ConfigSection,
                InteropArchive.ConfigKey,
                InteropArchive.DefaultRetain,
                InteropArchive.ConfigDescription);
        }
        catch (Exception e)
        {
            // A config that cannot be bound is a documentation loss, never a start failure: the
            // pass reads the same file by hand and defaults the same way.
            Plugin.Logger?.LogWarning(
                "Resilience: binding the [" + InteropArchive.ConfigSection
                + "] archive keys threw (" + e.Message + "); they are still read from "
                + InteropArchive.ConfigFileName + " by hand, defaulting to "
                + InteropArchivePolicy.ConfigValueAuto + " / "
                + InteropArchive.DefaultRetain.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }
}
#endif
