using System;
using System.Collections.Generic;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FMAN19: the client half of FMAN18's deferred kick. Servers no longer send mod files (2026-10-05), so the window
/// only arms and remembers the server's sentence; the join screens fix the rows (Thunderstore, Turn on, Use kept, Pause)
/// and re-answer. What is pinned here: deferred is not denied, one window per join, and no countdown is invented.
/// </summary>
[Collection("Handshake")]
public class ModInstallWindowTests : IDisposable
{
    private const string Sample = "progenitor.sample";

    private readonly FakeHost _host = new();
    private readonly ModInstallWindow _window;

    public ModInstallWindowTests()
    {
        Handshake.ResetTables();
        _window = new ModInstallWindow(_host);
    }

    public void Dispose() => Handshake.ResetTables();

    [Fact]
    public void The_three_result_bytes_are_three_different_things()
    {
        Assert.Equal(HandshakeResultKind.Ok, ModInstallWindowPolicy.Classify(1));
        Assert.Equal(HandshakeResultKind.Denied, ModInstallWindowPolicy.Classify(0));
        Assert.Equal(HandshakeResultKind.Deferred, ModInstallWindowPolicy.Classify(Handshake.ResultDeferred));
        // Anything else is a refusal: fail closed on a byte no server we know sends.
        Assert.Equal(HandshakeResultKind.Denied, ModInstallWindowPolicy.Classify(7));
    }

    [Fact]
    public void A_deferred_result_opens_the_window_and_not_the_deny_report()
    {
        var route = _window.OnDeferredResult(
            Handshake.ResultDeferred,
            Handshake.Compare.DeferredResultMessage(new[] { Sample }, 60));

        Assert.Equal(DeferredRoute.Opened, route);
        Assert.True(_window.IsArmed);
        Assert.StartsWith(Handshake.Compare.DeferredPrefix, _window.Headline);
        Assert.Contains(_host.InfoLines, l => l.StartsWith(ModInstallWindowPolicy.Prefix + " opened", StringComparison.Ordinal));
    }

    [Fact]
    public void An_ok_or_denied_result_is_not_this_windows_business()
    {
        Assert.Equal(DeferredRoute.NotDeferred, _window.OnDeferredResult(1, "ok"));
        Assert.Equal(DeferredRoute.NotDeferred, _window.OnDeferredResult(0, "Mod mismatch: x"));
        Assert.False(_window.IsArmed);
        Assert.Empty(_host.InfoLines);
    }

    [Fact]
    public void A_second_deferred_result_in_one_join_is_ignored()
    {
        Assert.Equal(DeferredRoute.Opened, _window.OnDeferredResult(Handshake.ResultDeferred, "first"));
        Assert.Equal(DeferredRoute.Ignored, _window.OnDeferredResult(Handshake.ResultDeferred, "second"));
        Assert.Equal("first", _window.Headline);
        Assert.Contains(_host.Warnings, w => w.Contains("a second deferred result"));
    }

    [Fact]
    public void There_is_no_countdown_number_only_the_servers_own_sentence()
    {
        string message = Handshake.Compare.DeferredResultMessage(new[] { Sample }, 60);
        _window.OnDeferredResult(Handshake.ResultDeferred, message);

        Assert.Equal(message, _window.Headline);
        Assert.Contains("no countdown", ModInstallWindowPolicy.WaitingNote);
        var fresh = new ModInstallWindow(new FakeHost());
        fresh.OnDeferredResult(Handshake.ResultDeferred, "");
        Assert.Equal(ModInstallWindowPolicy.WaitingNote, fresh.Headline);
    }

    [Fact]
    public void Reset_disarms_so_the_next_join_gets_its_own_window()
    {
        _window.OnDeferredResult(Handshake.ResultDeferred, "first");
        _window.Reset();
        Assert.False(_window.IsArmed);
        Assert.Equal("", _window.Headline);
        Assert.Equal(DeferredRoute.Opened, _window.OnDeferredResult(Handshake.ResultDeferred, "next join"));
    }

    private sealed class FakeHost : IModInstallWindowHost
    {
        public List<string> InfoLines { get; } = new();
        public List<string> Warnings { get; } = new();

        public void Info(string line) => InfoLines.Add(line);

        public void Warn(string line) => Warnings.Add(line);
    }
}
