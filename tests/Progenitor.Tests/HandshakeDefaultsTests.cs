using System;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job HS1 (2026-09-11). Pins the <b>shipped defaults</b> of the missing-mod join path: which
/// rows kick, when the clock starts, what the kick string actually says, and that a silent
/// client's timeout never invents an inventory the server never received.
///
/// <para>Existing suites cover the helpers in isolation. This file is the one that fails when a
/// default or a guard is changed: 20 s / 60 s, fail-closed vs optional vs undeclared, grace
/// <c>Disabled</c>/<c>NothingToWaitFor</c>/<c>AlreadyUsed</c>/<c>Opened</c>, and the bind keys the
/// operator's cfg actually receives. Game-free; the coordinator's live matrix consumes these
/// outcomes.</para>
/// </summary>
[Collection("Handshake")]
public sealed class HandshakeDefaultsTests : IDisposable
{
    public HandshakeDefaultsTests() => Handshake.ResetTables();

    public void Dispose() => Handshake.ResetTables();

    // ---- shipped numeric defaults -------------------------------------------------------------

    /// <summary>
    /// Breaks if <c>HandshakeAnswerWatch.DefaultTimeoutSeconds</c> or the Handshake alias moves
    /// off 20, or if someone treats <see cref="NetworkCompatibilityLevel.ServerMustHave"/> (enum
    /// value 0) as the timeout. 0 on the timeout key disables the deadline; it is not the default.
    /// </summary>
    [Fact]
    public void Shipped_answer_timeout_is_20_seconds_and_is_not_enum_zero()
    {
        Assert.Equal(20, HandshakeAnswerWatch.DefaultTimeoutSeconds);
        Assert.Equal(20, Handshake.DefaultAnswerTimeoutSeconds);
        Assert.Equal(2.0, HandshakeAnswerWatch.KickFlushSeconds);
        Assert.Equal(0, (byte)NetworkCompatibilityLevel.ServerMustHave);
        Assert.NotEqual(Handshake.DefaultAnswerTimeoutSeconds, (int)NetworkCompatibilityLevel.ServerMustHave);
        Assert.Equal(0, (int)NetworkCompatibilityLevel.ServerMustHave);
    }

    /// <summary>
    /// Breaks if <c>HandshakeInstallGrace.DefaultGraceSeconds</c> or the Handshake alias moves
    /// off 60, or if 0 is documented as the default. 0 is the separately labeled override that
    /// disables the window.
    /// </summary>
    [Fact]
    public void Shipped_install_grace_is_60_seconds_and_zero_is_the_off_switch()
    {
        Assert.Equal(60, HandshakeInstallGrace.DefaultGraceSeconds);
        Assert.Equal(60, Handshake.DefaultInstallGraceSeconds);
        Assert.NotEqual(0, Handshake.DefaultInstallGraceSeconds);
    }

    /// <summary>
    /// Breaks if <c>BindAnswerTimeout</c> stops passing those constants as BepInEx defaults, or
    /// if the keys leave <c>Networking</c>. The handoff forbids inferring defaults from enum
    /// zero or from a stale generated cfg: these are the arguments the Bind call actually uses.
    /// </summary>
    [Fact]
    public void Config_binds_pass_the_20s_and_60s_constants_not_zero()
    {
        string body = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "Handshake.cs"),
            "private static void BindAnswerTimeoutFrom(ConfigFile file)");

        SourcePin.AssertUncommented(body, "\"Networking\"");
        SourcePin.AssertUncommented(body, "\"HandshakeAnswerTimeoutSeconds\"");
        SourcePin.AssertUncommented(body, "DefaultAnswerTimeoutSeconds");
        SourcePin.AssertUncommented(body, "\"InstallGraceSeconds\"");
        SourcePin.AssertUncommented(body, "DefaultInstallGraceSeconds");

        int timeoutKey = body.IndexOf("\"HandshakeAnswerTimeoutSeconds\"", StringComparison.Ordinal);
        int timeoutDefault = body.IndexOf("DefaultAnswerTimeoutSeconds", timeoutKey, StringComparison.Ordinal);
        int graceKey = body.IndexOf("\"InstallGraceSeconds\"", StringComparison.Ordinal);
        int graceDefault = body.IndexOf("DefaultInstallGraceSeconds", graceKey, StringComparison.Ordinal);
        Assert.True(timeoutKey >= 0 && timeoutDefault > timeoutKey, "timeout Bind must take DefaultAnswerTimeoutSeconds after the key");
        Assert.True(graceKey >= 0 && graceDefault > graceKey, "grace Bind must take DefaultInstallGraceSeconds after the key");
        Assert.True(timeoutDefault < graceKey, "timeout default must be the HandshakeAnswerTimeoutSeconds Bind, not the grace one");
    }

    /// <summary>
    /// Breaks if a missing ConfigFile stops falling through to the same 20 s constant the Bind
    /// default uses. Until the shared file binds (SharedConfig defers it at process start with an
    /// older Bootstrap) the entry is null and the reader answers the constant.
    /// </summary>
    [Fact]
    public void Missing_config_file_uses_the_same_timeout_constant()
    {
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "Handshake.cs");
        string body = SourcePin.MethodBody(source, "private static void BindAnswerTimeout()");
        SourcePin.AssertUncommented(body, "_answerTimeoutSeconds = null");
        SourcePin.AssertUncommented(body, "SharedConfig.BindWhenAvailable");
        SourcePin.AssertUncommented(source, "_answerTimeoutSeconds?.Value ?? DefaultAnswerTimeoutSeconds");
    }

    // ---- which rows kick a silent client ------------------------------------------------------

    /// <summary>
    /// Breaks if an empty table, optional-only, or ClientOnly starts fail-closing a silent
    /// client. That is the unmodified-vanilla case against a server that has not required
    /// anything: the watch records unverified and does not schedule a kick.
    /// </summary>
    [Fact]
    public void Optional_only_or_empty_server_does_not_kick_a_silent_client()
    {
        double t = 0;
        var watch = new HandshakeAnswerWatch(() => t);

        Assert.False(Handshake.Compare.ServerRequiresAnswer(null));
        Assert.False(Handshake.Compare.ServerRequiresAnswer(Array.Empty<Handshake.ModRecord>()));
        Assert.False(Handshake.Compare.ServerRequiresAnswer(new[]
        {
            Declared("opt.mod", "1.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact),
            Declared("ui.mod", "1.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.None),
        }));

        var arm = watch.Arm(
            1, "TestPlayer", Handshake.DefaultAnswerTimeoutSeconds,
            skipUser: false, serverRequiresAnswer: false, requiredList: "none");
        Assert.True(arm.Unverified);
        Assert.False(arm.Scheduled);
        Assert.Equal(HandshakeAnswerWatch.Status.Unverified, watch.GetStatus(1));
        t = 120;
        Assert.False(watch.TryExpire(1, arm.Generation, out _));
        Assert.Equal("unverified", watch.Token(1));
    }

    /// <summary>
    /// Breaks if a declared or undeclared <c>ServerMustHave</c> row stops arming the 20 s
    /// fail-closed deadline, or if the kick reason starts claiming a client inventory.
    /// </summary>
    [Fact]
    public void A_required_row_fail_closes_a_silent_client_after_20s_naming_the_server_list()
    {
        var required = new[]
        {
            Declared("progenitor.sample", "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        Assert.True(Handshake.Compare.ServerRequiresAnswer(required));
        string summary = Handshake.Compare.FormatRequiredSummary(required);
        Assert.Equal("progenitor.sample 0.1.0 ServerMustHave/Exact", summary);

        double t = 0;
        var watch = new HandshakeAnswerWatch(() => t);
        var arm = watch.Arm(
            7, "TestPlayer", Handshake.DefaultAnswerTimeoutSeconds,
            skipUser: false, serverRequiresAnswer: true, requiredList: summary);
        Assert.True(arm.Scheduled);
        Assert.Equal("asked", watch.Token(7));

        t = 19.99;
        Assert.False(watch.TryExpire(7, arm.Generation, out _));
        t = 20;
        Assert.True(watch.TryExpire(7, arm.Generation, out var due));
        Assert.Equal(
            Handshake.Compare.NoAnswerReason(Handshake.DefaultAnswerTimeoutSeconds, summary),
            due.Reason);
        Assert.Equal(
            "Progenitor: no handshake answer within 20 s (server requires: progenitor.sample 0.1.0 ServerMustHave/Exact)",
            due.Reason);
        Assert.DoesNotContain("got ", due.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("missing", due.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("you have", due.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("timeout", watch.Token(7));
    }

    /// <summary>
    /// Breaks if the timeout sentence is rewritten to assert what the silent client "has". The
    /// server never received an answer, so the only honest list is the server's own required
    /// snapshot taken at Arm.
    /// </summary>
    [Fact]
    public void Timeout_reason_never_invents_a_client_inventory()
    {
        string reason = Handshake.Compare.NoAnswerReason(
            Handshake.DefaultAnswerTimeoutSeconds,
            "need.mod 1.2.0 ServerMustHave/Exact, other.mod 3.0 ServerMustHave/Minor");
        Assert.StartsWith("Progenitor: no handshake answer within 20 s (server requires: ", reason, StringComparison.Ordinal);
        Assert.Contains("need.mod 1.2.0 ServerMustHave/Exact", reason, StringComparison.Ordinal);
        Assert.Contains("other.mod 3.0 ServerMustHave/Minor", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("got missing", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("expected ", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("version ", reason, StringComparison.Ordinal);
        Assert.Equal(reason, Handshake.Compare.WireMessageText(Handshake.Compare.WireMessageBytes(reason)));
    }

    // ---- undeclared vs explicit registration --------------------------------------------------

    /// <summary>
    /// Breaks if an installed plugin that never called Require/DeclareSelf and has no
    /// <c>[NetworkCompatibility]</c> starts occupying a handshake slot. Being on disk is not a
    /// requirement.
    /// </summary>
    [Fact]
    public void A_plugin_that_never_registers_does_not_occupy_the_handshake()
    {
        Assert.Empty(Handshake.CopyRequired());
        Assert.False(Handshake.Compare.ServerRequiresAnswer(Handshake.CopyRequired()));
        Handshake.Require("registered.mod", "1.0");
        Assert.True(Handshake.Compare.ServerRequiresAnswer(Handshake.CopyRequired()));
        Handshake.Unrequire("registered.mod");
        Assert.False(Handshake.Compare.ServerRequiresAnswer(Handshake.CopyRequired()));
    }

    /// <summary>
    /// Breaks if the two-argument <c>Require</c> stops recording an undeclared exact
    /// <c>ServerMustHave</c> row, or if that row stops kicking a missing client.
    /// </summary>
    [Fact]
    public void Undeclared_Require_is_exact_ServerMustHave_and_kicks_a_missing_client()
    {
        Handshake.Require("legacy.mod", "3.0");
        var required = Handshake.CopyRequired();
        var rec = Assert.Single(required);
        Assert.Equal("legacy.mod", rec.Guid);
        Assert.Equal("3.0", rec.Version);
        Assert.False(rec.Declared);
        Assert.Equal(NetworkCompatibilityLevel.ServerMustHave, rec.Level);
        Assert.Equal(NetworkVersionRule.Exact, rec.VersionRule);

        var problems = Handshake.Compare.Evaluate(required, Array.Empty<Handshake.ModRecord>());
        Assert.Equal(new[] { "legacy.mod: the server needs it (3.0), but your game doesn't have it. Install it from the join screens" }, problems);
    }

    /// <summary>
    /// Breaks if <c>DeclareSelf</c> without the attribute stops calling RequireSelf/ProvideSelf,
    /// or if the attribute path stops using the declared overload. Auto-apply after OnLoad is
    /// only for types that actually carry the attribute.
    /// </summary>
    [Fact]
    public void DeclareSelf_is_opt_in_without_the_attribute_and_auto_only_with_it()
    {
        string declare = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Plugins", "PluginContext.cs"),
            "public void DeclareSelf()");
        SourcePin.AssertStatement(declare, "var attr = _ctx.Owner.GetType().GetCustomAttribute<NetworkCompatibilityAttribute>();");
        // SETTINGS1 stage 3: the declared level goes through the counterpart rule first
        // ([Counterpart(Limited or Optional)] turns ServerMustHave into NoNeedForSync).
        SourcePin.AssertUncommented(declare, "CounterpartPolicy.EffectiveLevel(");
        SourcePin.AssertUncommented(declare, "attr.Level, counterpart?.WithoutCounterpart, _ctx.IsServer, counterpart?.ClientExtras)");
        SourcePin.AssertUncommented(declare, "Require(_ctx.Guid, _ctx.Version, level, attr.VersionRule)");
        SourcePin.AssertUncommented(declare, "Provide(_ctx.Guid, _ctx.Version, level, attr.VersionRule)");
        SourcePin.AssertUncommented(declare, "RequireSelf()");
        SourcePin.AssertUncommented(declare, "ProvideSelf()");

        string load = SourcePin.ReadFile("src", "Progenitor.Core", "Plugins", "ProgenitorPlugin.cs");
        SourcePin.AssertUncommented(load, "GetType().GetCustomAttribute<NetworkCompatibilityAttribute>() != null");
        SourcePin.AssertUncommented(load, "ctx.Handshake.DeclareSelf()");
    }

    // ---- extras, client-only, reverse, wrong version ------------------------------------------

    /// <summary>
    /// Breaks if an undeclared extra the client reports starts failing the join. The original
    /// handshake ignores extras; only a <em>declared</em> client <c>ServerMustHave</c> reverses.
    /// </summary>
    [Fact]
    public void Undeclared_client_extras_are_ignored()
    {
        var server = new[] { Handshake.ModRecord.Undeclared("keep", "1.0") };
        var client = new[]
        {
            Handshake.ModRecord.Undeclared("keep", "1.0"),
            Handshake.ModRecord.Undeclared("bonus.mod", "9.9"),
        };
        Assert.Empty(Handshake.Compare.Evaluate(server, client));
    }

    /// <summary>
    /// Breaks if a ClientOnly row is packed onto the request, compared, or used to fail-close a
    /// silent client.
    /// </summary>
    [Fact]
    public void ClientOnly_is_not_required_not_packed_and_not_compared()
    {
        var row = Handshake.ModRecord.CreateDeclared(
            "ui.mod", "1.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.Exact);
        Assert.False(Handshake.Compare.LevelRequiresAnswer(row.Level));
        Assert.False(Handshake.Compare.ServerRequiresAnswer(new[] { row }));

        var packed = Handshake.Compare.PackRequest(new[] { row });
        Assert.Empty(packed.Rows);
        Assert.Empty(Handshake.Compare.Evaluate(new[] { row }, Array.Empty<Handshake.ModRecord>()));
    }

    /// <summary>
    /// Breaks if the reverse walk stops kicking a server that lacks a client-declared
    /// <c>ServerMustHave</c> plugin, or if an undeclared extra starts reversing.
    /// </summary>
    [Fact]
    public void Client_declared_ServerMustHave_missing_on_the_server_is_a_kick()
    {
        var server = Array.Empty<Handshake.ModRecord>();
        var declared = new[]
        {
            Handshake.ModRecord.CreateDeclared(
                "client.need", "2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        Assert.Equal(
            new[] { "client.need: your game needs it on the server (2.0), but the server doesn't have it. Turn it off, or ask the admins to install it" },
            Handshake.Compare.Evaluate(server, declared));

        var undeclaredExtra = new[] { Handshake.ModRecord.Undeclared("client.need", "2.0") };
        Assert.Empty(Handshake.Compare.Evaluate(server, undeclaredExtra));
    }

    /// <summary>
    /// Breaks if the mismatch sentence stops naming both versions, or if Exact starts treating
    /// <c>1.0</c> and <c>1.0.0</c> as equal.
    /// </summary>
    [Fact]
    public void Wrong_version_names_required_and_received_from_Check()
    {
        var server = new[]
        {
            Handshake.ModRecord.CreateDeclared(
                "need.mod", "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        var client = new[]
        {
            Handshake.ModRecord.CreateDeclared(
                "need.mod", "1.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        Assert.Equal(new[] { "need.mod: expected 1.2.0, got 1.1.0" }, Handshake.Compare.Evaluate(server, client));

        var minorServer = new[]
        {
            Handshake.ModRecord.CreateDeclared(
                "need.mod", "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor),
        };
        var minorClient = new[]
        {
            Handshake.ModRecord.CreateDeclared(
                "need.mod", "1.3.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor),
        };
        Assert.Equal(
            new[] { "need.mod: expected 1.2.0 (minor), got 1.3.0" },
            Handshake.Compare.Evaluate(minorServer, minorClient));

        Assert.Equal(
            new[] { "need.mod: expected 1.0, got 1.0.0" },
            Handshake.Compare.Evaluate(
                new[]
                {
                    Handshake.ModRecord.CreateDeclared(
                        "need.mod", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
                },
                new[]
                {
                    Handshake.ModRecord.CreateDeclared(
                        "need.mod", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
                }));
    }

    // ---- multiple missing: complete in Evaluate, capped on the kick note, remainder elsewhere -

    /// <summary>
    /// Breaks if Evaluate drops a failing id, if the kick note stops counting remainder after
    /// <see cref="Handshake.Compare.MaxNoteIds"/>, or if the deny report loses a row Evaluate
    /// still has. The wire then truncates the kick string; the remainder path is the deny panel
    /// (Progenitor) and the server log (full text before TruncateReason).
    /// </summary>
    [Fact]
    public void Several_missing_mods_are_complete_in_Evaluate_and_the_deny_report()
    {
        var server = new[]
        {
            Declared("a.mod", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
            Declared("b.mod", "2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
            Declared("c.mod", "3.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
            Declared("d.mod", "4.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
            Declared("e.mod", "5.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        var problems = Handshake.Compare.Evaluate(server, Array.Empty<Handshake.ModRecord>(), out var failedIds);
        Assert.Equal(5, problems.Count);
        Assert.Equal(new[] { "a.mod", "b.mod", "c.mod", "d.mod", "e.mod" }, failedIds);
        Assert.Equal("a.mod: the server needs it (1.0), but your game doesn't have it. Install it from the join screens", problems[0]);
        Assert.Equal("e.mod: the server needs it (5.0), but your game doesn't have it. Install it from the join screens", problems[4]);

        Assert.Equal(3, Handshake.Compare.MaxNoteIds);
        string named = Handshake.Compare.NameIdList(failedIds);
        Assert.Contains("a.mod", named, StringComparison.Ordinal);
        Assert.Contains("and 2 more", named, StringComparison.Ordinal);
        Assert.DoesNotContain("d.mod", named, StringComparison.Ordinal);

        var rows = ModManifestDeny.BuildLocalRows(server, Array.Empty<Handshake.ModRecord>());
        Assert.Equal(5, rows.Count);
        Assert.Equal("a.mod", rows[0].Id);
        Assert.Equal("e.mod", rows[4].Id);
        Assert.All(rows, r => Assert.True(r.Missing));
        Assert.All(rows, r => Assert.Equal(ModDenyOrigin.ServerRequires, r.Origin));
    }

    // ---- install grace sequence ---------------------------------------------------------------

    /// <summary>
    /// Breaks if <c>Begin</c> stops returning the four grants, if 0/negative is no longer
    /// Disabled, or if an empty id list is no longer NothingToWaitFor.
    /// </summary>
    [Fact]
    public void Grace_Begin_returns_Disabled_NothingToWaitFor_AlreadyUsed_Opened()
    {
        double now = 0;
        var grace = new HandshakeInstallGrace(() => now);

        Assert.Equal(
            HandshakeInstallGrace.Grant.Disabled,
            grace.Begin(1, "TestPlayer", new[] { "a.mod" }, 0).Verdict);
        Assert.Equal(
            HandshakeInstallGrace.Grant.Disabled,
            grace.Begin(1, "TestPlayer", new[] { "a.mod" }, -5).Verdict);
        Assert.Equal(
            HandshakeInstallGrace.Grant.NothingToWaitFor,
            grace.Begin(1, "TestPlayer", Array.Empty<string>(), Handshake.DefaultInstallGraceSeconds).Verdict);

        var opened = grace.Begin(1, "TestPlayer", new[] { "a.mod" }, Handshake.DefaultInstallGraceSeconds);
        Assert.Equal(HandshakeInstallGrace.Grant.Opened, opened.Verdict);
        Assert.True(opened.Opened);
        Assert.Equal(60, opened.GraceSeconds);
        Assert.Equal(
            HandshakeInstallGrace.Grant.AlreadyUsed,
            grace.Begin(1, "TestPlayer", new[] { "a.mod" }, Handshake.DefaultInstallGraceSeconds).Verdict);
    }

    /// <summary>
    /// Breaks if expiry stops using the 60 s clock opened at Begin, or if the kick text stops
    /// naming the window.
    /// </summary>
    [Fact]
    public void Grace_expires_at_60s_with_NotInstalledReason()
    {
        double now = 0;
        var grace = new HandshakeInstallGrace(() => now);
        var opened = grace.Begin(4, "TestPlayer", new[] { "a.mod", "b.mod" }, Handshake.DefaultInstallGraceSeconds);
        now = 59.9;
        Assert.False(grace.TryExpire(4, opened.Generation, out _));
        now = 60;
        Assert.True(grace.TryExpire(4, opened.Generation, out var due));
        Assert.Equal(
            Handshake.Compare.NotInstalledReason(new[] { "a.mod", "b.mod" }, Handshake.DefaultInstallGraceSeconds),
            due.Reason);
        Assert.Equal(
            "Mod mismatch: a.mod, b.mod were not confirmed within 60 s (this client did not answer again in time). If they are installed now, join again; if not, install them from the join screens first.",
            due.Reason);
    }

    /// <summary>
    /// Breaks if a miss whose every failing row the client can fix (a Thunderstore package) stops deferring for a capable client,
    /// or if a client that did not declare the install-window capability starts getting a window
    /// (vanilla and pre-FMAN19 Progenitor both declare nothing).
    /// </summary>
    [Fact]
    public void Fixable_required_miss_defers_only_for_a_capable_client()
    {
        var server = new[]
        {
            Declared("need.mod", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        var problems = Handshake.Compare.Evaluate(server, Array.Empty<Handshake.ModRecord>(), out var failedIds);

        var capable = Handshake.Compare.DecideMiss(
            problems, failedIds, server, Array.Empty<Handshake.ModRecord>(),
            _ => MissFix.Thunderstore, graceAvailable: true, clientCanInstall: true);
        Assert.True(capable.Defer);
        Assert.Equal(new[] { "need.mod" }, capable.Ids);

        var silent = Handshake.Compare.DecideMiss(
            problems, failedIds, server, Array.Empty<Handshake.ModRecord>(),
            _ => MissFix.Thunderstore, graceAvailable: true, clientCanInstall: false);
        Assert.False(silent.Defer);
        Assert.Equal(Handshake.Compare.NoInstallWindowCapability, silent.Why);

        var unavailable = Handshake.Compare.DecideMiss(
            problems, failedIds, server, Array.Empty<Handshake.ModRecord>(),
            _ => MissFix.None, graceAvailable: true, clientCanInstall: true);
        Assert.False(unavailable.Defer);
        Assert.Contains("need.mod", unavailable.Why, StringComparison.Ordinal);
        Assert.Contains("cannot be fixed during the join", unavailable.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// Breaks if <c>InstallGraceSeconds = 0</c> is treated as the shipped default, or if a capable
    /// client still defers when the operator has turned the window off. This is the separately
    /// labeled override, never the default.
    /// </summary>
    [Fact]
    public void InstallGraceSeconds_zero_is_an_override_that_kicks_immediately()
    {
        var server = new[]
        {
            Declared("need.mod", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
        };
        var problems = Handshake.Compare.Evaluate(server, Array.Empty<Handshake.ModRecord>(), out var failedIds);
        var verdict = Handshake.Compare.DecideMiss(
            problems, failedIds, server, Array.Empty<Handshake.ModRecord>(),
            _ => MissFix.Thunderstore, graceAvailable: false, clientCanInstall: true);
        Assert.False(verdict.Defer);
        Assert.Equal("no install window is available for this user", verdict.Why);
        Assert.NotEqual(0, Handshake.DefaultInstallGraceSeconds);
    }

    /// <summary>
    /// Breaks if the deferred Result sentence stops naming the mods and the 60 s budget, or if it
    /// stops being ASCII-safe on the wire.
    /// </summary>
    [Fact]
    public void Deferred_result_message_names_the_mods_and_the_shipped_60s()
    {
        string one = Handshake.Compare.DeferredResultMessage(
            new[] { "need.mod" }, Handshake.DefaultInstallGraceSeconds);
        Assert.Equal(
            "Mods needed: need.mod. Install from the join screens within 60 s, or this join is refused.",
            one);
        Assert.Equal(one, Handshake.Compare.WireMessageText(Handshake.Compare.WireMessageBytes(one)));

        string two = Handshake.Compare.DeferredResultMessage(
            new[] { "a.mod", "b.mod" }, Handshake.DefaultInstallGraceSeconds);
        Assert.Contains("a.mod, b.mod", two, StringComparison.Ordinal);
        Assert.Contains("within 60 s", two, StringComparison.Ordinal);
    }

    /// <summary>
    /// Breaks if Arm on join stops using the bound timeout, or if the expire callback stops
    /// sending Result then Disconnect after the 2 s flush. Those two clocks are the whole
    /// vanilla-client sequence.
    /// </summary>
    [Fact]
    public void Join_arms_the_bound_timeout_and_mismatch_kicks_flush_after_2s()
    {
        string arm = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "Handshake.cs"),
            "private static void ArmAnswerDeadline(World world, Entity userEntity, int userIndex, string name, bool skip)");
        SourcePin.AssertUncommented(arm, "ReadAnswerTimeoutSeconds()");
        SourcePin.AssertUncommented(arm, "AnswerWatch.Arm(");
        // NOPROGNOTICE: the bound timeout runs as the no-answer countdown (held during character creation, with the
        // notice), whose kick is the same timeout kick; NoAnswerNoticeTests drive its schedule.
        SourcePin.AssertUncommented(arm, "RunNoAnswerCountdown(world, userEntity, userIndex, name, arm.Generation, timeout)");
        string countdown = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "Handshake.NoAnswerNotice.cs");
        SourcePin.AssertUncommented(countdown, "SendResultAndKick(world, userEntity, userIndex, due.Name, due.Reason, timeoutKick: true)");
        SourcePin.AssertUncommented(countdown, "Dev.MainThread.PostDelayed(step.Delay, Tick, DeadlineOwner)");

        string kick = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "Handshake.cs"),
            "private static void SendResultAndKick(");
        SourcePin.AssertUncommented(kick, "HandshakeAnswerWatch.KickFlushSeconds");
        SourcePin.AssertUncommented(kick, "Players.PlayerControl.Disconnect(world, userEntity, wire)");
        SourcePin.AssertUncommented(kick, "Result.Create(false, wire)");
    }

    private static Handshake.ModRecord Declared(
        string guid,
        string version,
        NetworkCompatibilityLevel level,
        NetworkVersionRule rule) =>
        Handshake.ModRecord.CreateDeclared(guid, version, level, rule);
}
