using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using Progenitor.Config;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The queue behind <c>SharedConfig.BindWhenAvailable</c>: at process start Core installs before
/// the chainloader can hand out progenitor.cfg, and a section asked for then must bind
/// once the file resolves, with one log line per section rather than silence.
/// </summary>
public sealed class DeferredBindsTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigFile _shared;
    private readonly List<string> _log = new();
    private ConfigFile? _available;

    public DeferredBindsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-deferred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _shared = new ConfigFile(Path.Combine(_dir, ConfigHelper.SharedFileName), saveOnInit: true);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private DeferredBinds NewQueue() => new(() => _available, _log.Add);

    [Fact]
    public void Binds_immediately_and_silently_when_the_file_resolves()
    {
        _available = _shared;
        var queue = NewQueue();
        ConfigFile? boundInto = null;

        Assert.True(queue.Request("[Commands]", f => boundInto = f));

        Assert.Same(_shared, boundInto);
        Assert.Equal(0, queue.PendingCount);
        Assert.Empty(_log);
    }

    [Fact]
    public void Missing_file_queues_the_section_and_logs_one_line_naming_it()
    {
        var queue = NewQueue();
        int runs = 0;

        Assert.False(queue.Request("[Compat] RewriteMode", _ => runs++));

        Assert.Equal(0, runs);
        Assert.Equal(1, queue.PendingCount);
        var line = Assert.Single(_log);
        Assert.Contains("[Compat] RewriteMode", line);
        Assert.Contains(ConfigHelper.SharedFileName, line);
    }

    [Fact]
    public void Repeated_request_for_a_queued_section_is_not_queued_or_logged_twice()
    {
        var queue = NewQueue();
        int first = 0, second = 0;

        queue.Request("[Networking] mod transfer limits", _ => first++);
        queue.Request("[Networking] mod transfer limits", _ => second++);
        Assert.Equal(1, queue.PendingCount);
        Assert.Single(_log);

        _available = _shared;
        Assert.Equal(1, queue.Retry("chainloader ready"));
        Assert.Equal(0, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public void Retry_does_nothing_while_the_file_is_still_missing()
    {
        var queue = NewQueue();
        int runs = 0;
        queue.Request("[Hosted]", _ => runs++);
        _log.Clear();

        Assert.Equal(0, queue.Retry("chainloader ready"));

        Assert.Equal(0, runs);
        Assert.Equal(1, queue.PendingCount);
        Assert.Empty(_log);
    }

    [Fact]
    public void Retry_binds_every_queued_section_once_and_logs_each_late_bind()
    {
        var queue = NewQueue();
        var bound = new List<string>();
        queue.Request("[Commands]", f => bound.Add("commands:" + ReferenceEquals(f, _shared)));
        queue.Request("[Hosted]", f => bound.Add("hosted:" + ReferenceEquals(f, _shared)));
        _log.Clear();
        _available = _shared;

        Assert.Equal(2, queue.Retry("chainloader ready"));
        Assert.Equal(new[] { "commands:True", "hosted:True" }, bound);
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(2, _log.Count);
        Assert.Contains("[Commands] bound late", _log[0]);
        Assert.Contains("(chainloader ready)", _log[0]);
        Assert.Contains("[Hosted] bound late", _log[1]);

        Assert.Equal(0, queue.Retry("chainloader ready"));
        Assert.Equal(2, bound.Count);
    }

    [Fact]
    public void Retry_uses_an_offered_file_when_the_resolver_still_has_none()
    {
        var queue = NewQueue();
        ConfigFile? boundInto = null;
        queue.Request("[Persistence] Directory", f => boundInto = f);

        Assert.Equal(1, queue.Retry("shared config reload", _shared));
        Assert.Same(_shared, boundInto);
    }

    [Fact]
    public void Report_unbound_logs_each_waiting_section_and_keeps_it_queued()
    {
        var queue = NewQueue();
        int runs = 0;
        queue.Request("[Commands]", _ => runs++);
        queue.Request("[Hosted]", _ => runs++);
        _log.Clear();

        Assert.Equal(2, queue.ReportUnbound());
        Assert.Equal(2, _log.Count);
        Assert.Contains("[Commands] could not be bound", _log[0]);
        Assert.Contains("[Hosted] could not be bound", _log[1]);
        Assert.Equal(2, queue.PendingCount);

        _available = _shared;
        Assert.Equal(2, queue.Retry("shared config reload"));
        Assert.Equal(2, runs);
    }

    [Fact]
    public void A_throwing_bind_is_logged_and_not_retried()
    {
        _available = _shared;
        var queue = NewQueue();

        Assert.True(queue.Request("[Networking] receive limits", _ => throw new InvalidOperationException("boom")));

        var line = Assert.Single(_log);
        Assert.Contains("[Networking] receive limits", line);
        Assert.Contains("boom", line);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void A_throwing_resolver_counts_as_not_available()
    {
        var queue = new DeferredBinds(() => throw new InvalidOperationException("no chainloader"), _log.Add);

        Assert.False(queue.Request("[Mods]", _ => { }));
        Assert.Equal(1, queue.PendingCount);
    }

    [Fact]
    public void Clear_drops_queued_binds_without_running_them()
    {
        var queue = NewQueue();
        int runs = 0;
        queue.Request("[Commands]", _ => runs++);

        queue.Clear();
        _available = _shared;

        Assert.Equal(0, queue.Retry("chainloader ready"));
        Assert.Equal(0, runs);
    }

    [Fact]
    public void Deferred_section_binds_a_real_key_into_the_shared_file()
    {
        var queue = NewQueue();
        ConfigEntry<bool>? entry = null;
        queue.Request("[Hosted]", f => entry = f.Bind("Hosted", "Enabled", true, "test"));
        Assert.Null(entry);

        _available = _shared;
        queue.Retry("chainloader ready");

        Assert.NotNull(entry);
        Assert.Same(_shared, entry!.ConfigFile);
        Assert.True(entry.Value);
    }
}
