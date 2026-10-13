using System;
using System.IO;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FCFGEX (RD6 J11). The Sample's <c>ConfigHelper</c> example: <c>Bind</c>, <c>BindEnum</c>,
/// <c>ReadEnum</c>, <c>Watch</c>, <c>Reloaded</c> and <c>SharedFileName</c>, driven by <c>.cfg</c>.
///
/// The feature is a hand edit to a file on a running server, so the assertions here are of two
/// kinds: the parsing, the counters and the exact text a reader will find in the log, which
/// compile in this project; and source pins over the five <c>ConfigHelper</c> calls and the
/// unsubscribe, which do not (they bind BepInEx <c>ConfigFile</c>). The pins matter more than
/// usual for this file: the example is worthless if someone "simplifies" the enum bind to a typed
/// <c>ConfigEntry&lt;SampleVerbosity&gt;</c>, or drops the <c>Reloaded -=</c> that keeps a plugin
/// delegate off a Core static.
/// </summary>
public class SampleConfigTests
{
    // ---- parsing -----------------------------------------------------------------------

    [Fact]
    public void No_argument_and_status_both_mean_status()
    {
        Assert.True(SampleConfig.TryParse(null, null, out var a));
        Assert.Equal(SampleConfig.Kind.Status, a);

        Assert.True(SampleConfig.TryParse("", "", out var b));
        Assert.Equal(SampleConfig.Kind.Status, b);

        Assert.True(SampleConfig.TryParse("  STATUS  ", null, out var c));
        Assert.Equal(SampleConfig.Kind.Status, c);
    }

    [Fact]
    public void Watch_takes_an_explicit_direction()
    {
        Assert.True(SampleConfig.TryParse("watch", "on", out var on));
        Assert.Equal(SampleConfig.Kind.WatchOn, on);

        Assert.True(SampleConfig.TryParse("Watch", " OFF ", out var off));
        Assert.Equal(SampleConfig.Kind.WatchOff, off);
    }

    /// <summary>
    /// A bare <c>.cfg watch</c> is refused rather than guessed at. Guessing "on" would let a typo
    /// arm a FileSystemWatcher; guessing "off" would let one silently disarm it.
    /// </summary>
    [Fact]
    public void A_bare_watch_is_refused_rather_than_guessed()
    {
        Assert.False(SampleConfig.TryParse("watch", null, out _));
        Assert.False(SampleConfig.TryParse("watch", "", out _));
        Assert.False(SampleConfig.TryParse("watch", "yes", out _));
    }

    [Fact]
    public void Read_parses_and_an_unknown_token_does_not()
    {
        Assert.True(SampleConfig.TryParse("read", null, out var read));
        Assert.Equal(SampleConfig.Kind.Read, read);

        Assert.False(SampleConfig.TryParse("reload", null, out _));
        Assert.False(SampleConfig.TryParse("enabled", "true", out _));
    }

    [Fact]
    public void The_usage_line_names_every_form_the_parser_accepts()
    {
        Assert.Equal(
            "Usage: .cfg [status] | .cfg watch on|off | .cfg read",
            SampleConfig.UsageReply);
    }

    // ---- which file a callback was about -----------------------------------------------

    /// <summary>
    /// <c>ConfigHelper.Reloaded</c> is one static event for every watched file. The example filters
    /// on the whole path, not the file name: two BepInEx roots on one machine (a hosted child
    /// server beside the client) hold <c>GUID.cfg</c> files with identical names.
    /// </summary>
    [Fact]
    public void Our_file_is_matched_on_the_whole_path_not_the_file_name()
    {
        const string ours = @"C:\srv\BepInEx\config\progenitor.sample.cfg";
        Assert.True(SampleConfig.IsOurFile(ours, ours));
        Assert.True(SampleConfig.IsOurFile(
            @"c:/srv/BepInEx/config/progenitor.sample.cfg", ours));
        Assert.False(SampleConfig.IsOurFile(
            @"C:\other\BepInEx\config\progenitor.sample.cfg", ours));
        Assert.False(SampleConfig.IsOurFile(
            @"C:\srv\BepInEx\config\progenitor.cfg", ours));
    }

    [Fact]
    public void A_missing_path_is_never_our_file()
    {
        Assert.False(SampleConfig.IsOurFile(null, @"C:\a\b.cfg"));
        Assert.False(SampleConfig.IsOurFile(@"C:\a\b.cfg", null));
        Assert.False(SampleConfig.IsOurFile("  ", "  "));
    }

    [Fact]
    public void Only_the_file_name_reaches_a_reply_or_a_log_line()
    {
        Assert.Equal("progenitor.sample.cfg",
            SampleConfig.FileNameOf(@"C:\srv\BepInEx\config\progenitor.sample.cfg"));
        Assert.Equal("progenitor.sample.cfg",
            SampleConfig.FileNameOf("/srv/BepInEx/config/progenitor.sample.cfg"));
        Assert.Equal("(unknown)", SampleConfig.FileNameOf(null));
        Assert.Equal("(unknown)", SampleConfig.FileNameOf("   "));
    }

    /// <summary>
    /// The two counters have to move independently, because <c>.cfg status</c> reports them apart
    /// and that is how a reader learns the event is shared.
    /// </summary>
    [Fact]
    public void Observe_counts_our_reloads_and_other_files_separately()
    {
        SampleConfig.Reset();
        const string ours = @"C:\srv\BepInEx\config\progenitor.sample.cfg";
        const string theirs = @"C:\srv\BepInEx\config\progenitor.cfg";

        Assert.True(SampleConfig.Observe(ours, ours));
        Assert.False(SampleConfig.Observe(theirs, ours));
        Assert.True(SampleConfig.Observe(ours, ours));

        Assert.Equal(2, SampleConfig.OurReloads);
        Assert.Equal(1, SampleConfig.OtherReloads);

        SampleConfig.Reset();
        Assert.Equal(0, SampleConfig.OurReloads);
        Assert.Equal(0, SampleConfig.OtherReloads);
    }

    // ---- the line the whole example exists to write ------------------------------------

    [Fact]
    public void The_timestamp_is_a_fixed_width_utc_reading()
    {
        Assert.Equal("21:04:33.512Z",
            SampleConfig.Timestamp(new DateTime(2026, 9, 5, 21, 4, 33, 512, DateTimeKind.Utc)));
    }

    /// <summary>
    /// The acceptance criterion for this job: the server log alone must prove that a hand edit was
    /// picked up. That means the timestamp, the file, the callback number, and what each key read
    /// before and after — in one line.
    /// </summary>
    [Fact]
    public void The_reload_line_proves_a_hand_edit_from_the_log_alone()
    {
        string line = SampleConfig.ReloadedLine(
            "21:04:33.512Z",
            "progenitor.sample.cfg",
            3,
            beforeEnabled: true,
            afterEnabled: false,
            beforeVerbosity: SampleVerbosity.Normal,
            afterVerbosity: SampleVerbosity.Loud);

        Assert.Equal(
            "reload #3 at 21:04:33.512Z of progenitor.sample.cfg: "
            + "Watch.Enabled: True -> False, Watch.Verbosity: Normal -> Loud "
            + "(ConfigHelper.Reloaded, debounced 1.0s, main thread)",
            line);
    }

    /// <summary>
    /// A reload that changed nothing is a real answer — "your edit did not take" — so the key still
    /// prints, marked unchanged, rather than being dropped from the line.
    /// </summary>
    [Fact]
    public void An_unchanged_key_still_prints_its_value()
    {
        string line = SampleConfig.ReloadedLine(
            "00:00:01.000Z",
            "progenitor.sample.cfg",
            1,
            beforeEnabled: true,
            afterEnabled: true,
            beforeVerbosity: SampleVerbosity.Quiet,
            afterVerbosity: SampleVerbosity.Quiet);

        Assert.Equal(
            "reload #1 at 00:00:01.000Z of progenitor.sample.cfg: "
            + "Watch.Enabled=True (unchanged), Watch.Verbosity=Quiet (unchanged) "
            + "(ConfigHelper.Reloaded, debounced 1.0s, main thread)",
            line);
    }

    [Fact]
    public void The_line_written_for_another_watched_file_says_why_it_was_ignored()
    {
        Assert.Equal(
            "ignored ConfigHelper.Reloaded #2 for progenitor.cfg "
            + "(one static event for every watched file; filter on ConfigFilePath)",
            SampleConfig.OtherFileLine("progenitor.cfg", 2));
    }

    [Fact]
    public void The_reload_line_is_one_line_so_grep_finds_all_of_it()
    {
        string line = SampleConfig.ReloadedLine(
            "21:04:33.512Z", "x.cfg", 1, true, false, SampleVerbosity.Quiet, SampleVerbosity.Loud);
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", line, StringComparison.Ordinal);
    }

    // ---- replies -----------------------------------------------------------------------

    [Fact]
    public void Status_names_both_files_both_entries_and_both_counters()
    {
        var lines = SampleConfig.StatusLines(
            "progenitor.sample.cfg",
            "progenitor.cfg",
            enabled: false,
            verbosity: SampleVerbosity.Loud,
            ourReloads: 4,
            otherReloads: 1,
            watching: true);

        Assert.Contains(lines, l => l.StartsWith("file=progenitor.sample.cfg", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("shared=progenitor.cfg", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Watch.Enabled=False (ConfigHelper.Bind<bool>)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Watch.Verbosity=Loud (ConfigHelper.BindEnum<SampleVerbosity>", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("ConfigHelper.ReadEnum", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Reloaded callbacks: 4 for this file, 1 for other watched files", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("watch=on.", StringComparison.Ordinal));
    }

    /// <summary>
    /// The shared file is named in the status reply from <c>ConfigHelper.SharedFileName</c>, and
    /// the line beside it says why a plugin must not bind its own types there. That sentence is the
    /// only place in the Sample a reader meets the pin rule, so it is pinned.
    /// </summary>
    [Fact]
    public void The_shared_file_line_says_why_a_plugin_type_must_not_be_bound_there()
    {
        var lines = SampleConfig.StatusLines(
            "a.cfg", "progenitor.cfg", true, SampleVerbosity.Normal, 0, 0, false);
        Assert.Contains(lines, l =>
            l.Contains("ConfigHelper.SharedFileName", StringComparison.Ordinal)
            && l.Contains("pins the collectible Core generation", StringComparison.Ordinal));
    }

    [Fact]
    public void Status_with_the_watch_off_tells_the_reader_how_to_turn_it_on()
    {
        var lines = SampleConfig.StatusLines(
            "a.cfg", "b.cfg", true, SampleVerbosity.Normal, 0, 0, watching: false);
        Assert.Contains(lines, l => l.StartsWith("watch=off.", StringComparison.Ordinal)
            && l.Contains(".cfg watch on", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>.cfg read</c> calls <c>ConfigFile.Reload()</c> directly, which does not raise
    /// <c>ConfigHelper.Reloaded</c>. A counter that does not move otherwise reads as a bug, so the
    /// reply says it.
    /// </summary>
    [Fact]
    public void Read_prints_the_values_and_explains_the_unchanged_counter()
    {
        var lines = SampleConfig.ReadLines("progenitor.sample.cfg", false, SampleVerbosity.Quiet, 7);

        Assert.Equal(
            "re-read progenitor.sample.cfg with ConfigFile.Reload(): "
            + "Watch.Enabled=False, Watch.Verbosity=Quiet",
            lines[0]);
        Assert.Contains("counter is unchanged at 7", lines[1], StringComparison.Ordinal);
        Assert.Contains("that event belongs to the watch", lines[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// CLAUDE.md's rule from evidence 162, at small scale: an "already on" branch over a mechanism
    /// whose parts are all process-lifetime must re-arm and say so, never short-circuit.
    /// </summary>
    [Fact]
    public void Watch_on_while_already_on_says_it_re_armed_rather_than_doing_nothing()
    {
        string again = SampleConfig.WatchOnReply(wasAlreadyOn: true, "a.cfg");
        Assert.Contains("re-armed it anyway", again, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing to do", again, StringComparison.Ordinal);

        string first = SampleConfig.WatchOnReply(wasAlreadyOn: false, "a.cfg");
        Assert.StartsWith("Watch armed. ", first, StringComparison.Ordinal);
        Assert.Contains("ConfigHelper.Watch(a.cfg)", first, StringComparison.Ordinal);
        Assert.Contains("ConfigHelper.Reloaded handler", first, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>ConfigHelper.Bind</c> ends with a bare <c>Watch(file)</c> whose lease it discards, so the
    /// FileSystemWatcher outlives <c>.cfg watch off</c>. The reply must not claim otherwise — a
    /// reader who believes "off" stopped the watcher will build the wrong thing on top of it.
    /// </summary>
    [Fact]
    public void Watch_off_does_not_claim_to_have_stopped_the_file_system_watcher()
    {
        foreach (bool wasOn in new[] { true, false })
        {
            string reply = SampleConfig.WatchOffReply(wasOn);
            Assert.Contains("FileSystemWatcher itself keeps running", reply, StringComparison.Ordinal);
            Assert.Contains("ConfigHelper.Bind took a lease of its own", reply, StringComparison.Ordinal);
        }

        Assert.StartsWith("Watch off:", SampleConfig.WatchOffReply(true), StringComparison.Ordinal);
        Assert.StartsWith("Watch was already off;", SampleConfig.WatchOffReply(false), StringComparison.Ordinal);
    }

    [Fact]
    public void The_attach_line_names_both_binds_and_both_files()
    {
        string line = SampleConfig.AttachedLine("progenitor.sample.cfg", "progenitor.cfg");
        Assert.Equal(
            "bound Watch.Enabled (Bind<bool>) and Watch.Verbosity (BindEnum<SampleVerbosity>) in "
            + "progenitor.sample.cfg; shared file is progenitor.cfg; "
            + "watch is off until .cfg watch on",
            line);
    }

    // ---- source pins over the calls this project cannot compile ------------------------

    /// <summary>
    /// The five <c>ConfigHelper</c> members this example exists to demonstrate. If one of these
    /// calls is refactored away the api.md row it fills goes back to <b>none</b>, silently.
    /// </summary>
    [Fact]
    public void All_five_ConfigHelper_members_are_actually_called()
    {
        string src = Source();
        SourcePin.AssertStatement(src,
            "_enabled = ConfigHelper.Bind(file, Section, EnabledKey, EnabledDefault, EnabledDescription);");
        SourcePin.AssertStatement(src,
            "_verbosity = ConfigHelper.BindEnum(file, Section, VerbosityKey, VerbosityDefault, VerbosityDescription);");
        SourcePin.AssertStatement(src,
            "internal static SampleVerbosity ReadVerbosity() => ConfigHelper.ReadEnum(_verbosity, VerbosityDefault);");
        SourcePin.AssertStatementExactly(src, "_lease = ConfigHelper.Watch(file);");
        SourcePin.AssertStatementExactly(src, "ConfigHelper.Reloaded += _handler;");
        SourcePin.AssertStatement(src, "ConfigHelper.SharedFileName,");
    }

    /// <summary>
    /// The example has to state the multiplexing rule it depends on, because a reader counting
    /// <c>reload #N</c> lines is the only person who will notice when it changes. Two facts, both
    /// pinned as text because they are the comment: the event is shared by every watched file (so
    /// the handler filters on the path), and it is raised <b>once per reload of a path</b> whatever
    /// number of <c>ConfigFile</c> instances that path has registered (R69 #4). Before that second
    /// rule, a path with an undropped previous instance turned one hand edit into two lines, the
    /// second reporting both keys unchanged.
    /// </summary>
    [Fact]
    public void The_example_says_one_hand_edit_is_one_reload_line()
    {
        string src = Source();
        Assert.Contains("single static event for every watched", src, StringComparison.Ordinal);
        Assert.Contains("raised once per reload of a", src, StringComparison.Ordinal);
        Assert.Contains("hand edit is one <c>reload #N</c> line", src, StringComparison.Ordinal);
    }

    /// <summary>
    /// The enum is bound as a <b>string</b>. A typed <c>Bind&lt;SampleVerbosity&gt;</c> would
    /// compile and work on a plugin file, and would quietly delete the reason
    /// <c>BindEnum</c>/<c>ReadEnum</c> exist.
    /// </summary>
    [Fact]
    public void The_enum_entry_is_a_string_on_disk()
    {
        string src = Source();
        SourcePin.AssertStatement(src, "private static ConfigEntry<string>? _verbosity;");
        Assert.DoesNotContain("ConfigEntry<SampleVerbosity>", src, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>ConfigHelper.Reloaded</c> is a Core static. A plugin delegate left on it holds this
    /// plugin's ALC alive past a hot reload, which is the same class of pin as evidence 65's
    /// <c>ConfigEntry</c>. Both the unsubscribe and the tracked disposable are pinned, because
    /// either alone is a single point of failure.
    /// </summary>
    [Fact]
    public void The_reloaded_subscription_is_removed_and_the_removal_is_tracked_on_the_context()
    {
        string src = Source();
        SourcePin.AssertStatementExactly(src, "try { ConfigHelper.Reloaded -= handler; }");
        SourcePin.AssertStatementExactly(src, "ctx.Track(new SampleConfigDisarm());");
        SourcePin.AssertStatement(src, "public void Dispose() => SampleConfig.DisarmWatch();");
        SourcePin.AssertStatementExactly(src, "DisarmWatch();");
    }

    /// <summary>
    /// Arming always disarms first. That is what makes "already on" honest instead of a memory of
    /// an intention, and it is one line that a reader could easily take for redundancy.
    /// </summary>
    [Fact]
    public void Arming_disarms_first_so_it_can_never_stack_a_second_handler()
    {
        string src = Source();
        int arm = src.IndexOf("internal static bool ArmWatch()", StringComparison.Ordinal);
        Assert.True(arm > 0, "ArmWatch not found");
        int disarm = src.IndexOf("DisarmWatch();", arm, StringComparison.Ordinal);
        int subscribe = src.IndexOf("ConfigHelper.Reloaded += _handler;", arm, StringComparison.Ordinal);
        Assert.True(disarm > 0 && subscribe > disarm,
            "ArmWatch must call DisarmWatch() before subscribing");
    }

    /// <summary>
    /// <c>.cfg</c> names server-side files, arms a FileSystemWatcher and forces a disk read. The
    /// gate is the attribute, never a hand-rolled check in the body (<c>SampleCommands</c>'s rule).
    /// </summary>
    [Fact]
    public void The_cfg_command_is_admin_gated_by_the_attribute()
    {
        string src = Source();
        SourcePin.AssertStatement(src, "[Command(\"cfg\", adminOnly: true,");
        Assert.DoesNotContain("ctx.IsAdmin", src, StringComparison.Ordinal);
    }

    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "samples", "Progenitor.Sample", "SampleConfig.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).Replace("\r\n", "\n");
            dir = dir.Parent;
        }

        throw new FileNotFoundException("samples/Progenitor.Sample/SampleConfig.cs not found from " + AppContext.BaseDirectory);
    }
}
