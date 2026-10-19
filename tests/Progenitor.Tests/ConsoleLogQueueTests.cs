using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Bootstrap;
using Progenitor.SessionLogs;
using Xunit;

namespace Progenitor.Tests;

public class ConsoleLogQueueTests
{
    [Fact]
    public void Format_matches_the_BepInEx_console_line()
    {
        string line = ConsoleLogQueue.Format("Info", "Progenitor", "tick hb frame=1", 2000, out bool cut);
        Assert.Equal("[Info   :Progenitor] tick hb frame=1", line);
        Assert.False(cut);
        Assert.Equal("[Warning:  Autoloot] x", ConsoleLogQueue.Format("Warning", "Autoloot", "x", 2000, out _));
        Assert.Equal("[Info   :         ?] ", ConsoleLogQueue.Format("Info", "?", null, 2000, out _));
    }

    [Fact]
    public void A_long_line_is_shortened_for_the_console_with_the_count_left_out()
    {
        string data = new string('a', 5000);
        string line = ConsoleLogQueue.Format("Info", "Satisvampory", data, 2000, out bool cut);
        Assert.True(cut);
        Assert.StartsWith("[Info   :Satisvampory] " + new string('a', 2000) + " ... [3000 more characters;", line);
        Assert.EndsWith("the whole line is in LogOutput.log]", line);
    }

    [Fact]
    public void Shorten_never_splits_a_surrogate_pair()
    {
        string text = "ab\U0001F600cd"; // a, b, high, low, c, d
        string cut = ConsoleLogQueue.Shorten(text, 3, out bool truncated);
        Assert.True(truncated);
        Assert.StartsWith("ab ...", cut);
        Assert.Equal(text, ConsoleLogQueue.Shorten(text, 6, out truncated));
        Assert.False(truncated);
    }

    [Fact]
    public void A_full_queue_drops_lines_counts_them_and_the_next_batch_says_so_first()
    {
        var q = new ConsoleLogQueue(maxLines: 3, maxChars: 1000);
        for (int i = 0; i < 5; i++) q.Enqueue(ConsoleColor.DarkGray, "line " + i);
        var s = q.Snapshot();
        Assert.Equal(3, s.Enqueued);
        Assert.Equal(2, s.Dropped);
        Assert.Equal(3, s.Pending);

        var runs = q.TakeBatch(ConsoleColor.Yellow);
        Assert.Equal(2, runs.Count);
        Assert.Equal(ConsoleColor.Yellow, runs[0].Color);
        Assert.StartsWith("[Progenitor: 2 console line(s) dropped", runs[0].Text);
        Assert.Equal(0, runs[0].Lines);
        Assert.Equal("line 0" + Environment.NewLine + "line 1" + Environment.NewLine + "line 2" + Environment.NewLine, runs[1].Text);
        Assert.Equal(3, runs[1].Lines);

        // The notice is said once.
        q.Enqueue(ConsoleColor.DarkGray, "after");
        runs = q.TakeBatch();
        Assert.Single(runs);
        Assert.Equal("after" + Environment.NewLine, runs[0].Text);
    }

    [Fact]
    public void The_character_cap_bounds_memory_as_well_as_the_line_count()
    {
        var q = new ConsoleLogQueue(maxLines: 100, maxChars: 25);
        Assert.True(q.Enqueue(ConsoleColor.Gray, new string('x', 20)));
        Assert.False(q.Enqueue(ConsoleColor.Gray, new string('y', 10)));
        Assert.True(q.Enqueue(ConsoleColor.Gray, "12345"));
        Assert.Equal(1, q.Snapshot().Dropped);
        q.TakeBatch();
        // Taking the batch freed the characters.
        Assert.True(q.Enqueue(ConsoleColor.Gray, new string('z', 25)));
    }

    [Fact]
    public void A_batch_is_bounded_by_lines_and_characters_and_keeps_order()
    {
        var q = new ConsoleLogQueue(batchLines: 4, batchChars: 12);
        for (int i = 0; i < 10; i++) q.Enqueue(ConsoleColor.Gray, "l" + i); // two characters each

        var first = q.TakeBatch();
        Assert.Equal(4, first.Sum(r => r.Lines));
        var all = string.Concat(first.Select(r => r.Text));
        Assert.Equal(string.Join("", Enumerable.Range(0, 4).Select(i => "l" + i + Environment.NewLine)), all);

        var q2 = new ConsoleLogQueue(batchLines: 100, batchChars: 12);
        for (int i = 0; i < 10; i++) q2.Enqueue(ConsoleColor.Gray, "l" + i);
        Assert.Equal(6, q2.TakeBatch().Sum(r => r.Lines)); // 6 x 2 characters = 12

        // A single line larger than the character budget still goes out (alone), never stalls the queue.
        var q3 = new ConsoleLogQueue(batchChars: 4);
        q3.Enqueue(ConsoleColor.Gray, "0123456789");
        q3.Enqueue(ConsoleColor.Gray, "next");
        Assert.Equal("0123456789" + Environment.NewLine, Assert.Single(q3.TakeBatch()).Text);
        Assert.Equal("next" + Environment.NewLine, Assert.Single(q3.TakeBatch()).Text);
        Assert.Empty(q3.TakeBatch());
    }

    [Fact]
    public void Consecutive_lines_of_one_color_become_one_write()
    {
        var q = new ConsoleLogQueue();
        q.Enqueue(ConsoleColor.DarkGray, "a");
        q.Enqueue(ConsoleColor.DarkGray, "b");
        q.Enqueue(ConsoleColor.Yellow, "warn");
        q.Enqueue(ConsoleColor.DarkGray, "c");
        var runs = q.TakeBatch();
        Assert.Equal(new[] { ConsoleColor.DarkGray, ConsoleColor.Yellow, ConsoleColor.DarkGray }, runs.Select(r => r.Color).ToArray());
        Assert.Equal(new[] { 2, 1, 1 }, runs.Select(r => r.Lines).ToArray());
    }

    [Fact]
    public void Counters_add_up_and_maxima_reset_per_window()
    {
        var q = new ConsoleLogQueue();
        q.Enqueue(ConsoleColor.Gray, "a", truncated: true);
        q.Enqueue(ConsoleColor.Gray, "b");
        q.NoteWritten(2, 500);
        q.NoteWritten(0, 100);
        var s = q.Snapshot(resetMaxima: true);
        Assert.Equal(2, s.Enqueued);
        Assert.Equal(1, s.Truncated);
        Assert.Equal(2, s.Written);
        Assert.Equal(600, s.WriteTicks);
        Assert.Equal(500, s.MaxWriteTicks);
        Assert.Equal(2, s.MaxPending);
        Assert.True(s.MaxEnqueueTicks > 0);

        var after = q.Snapshot();
        Assert.Equal(0, after.MaxWriteTicks);
        Assert.Equal(0, after.MaxEnqueueTicks);
        Assert.Equal(600, after.WriteTicks); // totals stay

        long[] flat = ConsoleLogQueue.ToArray(s);
        Assert.Equal(11, flat.Length);
        Assert.Equal(2, flat[0]);
        Assert.Equal(System.Diagnostics.Stopwatch.Frequency, flat[10]);
    }

    [Fact]
    public void The_dropped_notice_is_said_at_most_once_a_second_with_the_running_count()
    {
        long now = 10_000;
        var q = new ConsoleLogQueue(maxLines: 1) { NowMs = () => now };
        q.Enqueue(ConsoleColor.Gray, "kept");
        q.Enqueue(ConsoleColor.Gray, "dropped 1");
        var runs = q.TakeBatch();
        Assert.StartsWith("[Progenitor: 1 console line(s) dropped", runs[0].Text);

        // Within the second: lines still go out, the notice waits and the count adds up.
        for (int i = 0; i < 3; i++)
        {
            now += 200;
            q.Enqueue(ConsoleColor.Gray, "kept " + i);
            q.Enqueue(ConsoleColor.Gray, "dropped");
            runs = q.TakeBatch();
            Assert.Single(runs);
            Assert.StartsWith("kept " + i, runs[0].Text);
        }
        Assert.False(q.WaitForWork(0)); // a waiting notice alone does not spin the writer before it is due

        now += 400; // 1,000 ms since the last notice
        Assert.True(q.WaitForWork(0));
        runs = q.TakeBatch();
        Assert.StartsWith("[Progenitor: 3 console line(s) dropped", Assert.Single(runs).Text);
    }

    [Fact]
    public void Stop_returns_within_its_budget_while_the_console_write_is_stuck()
    {
        var q = new ConsoleLogQueue();
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var writer = new ConsoleBatchWriter(q, (_, _) => { entered.Set(); release.Wait(30_000); }, coalesceMs: 0);
        writer.Start();
        try
        {
            q.Enqueue(ConsoleColor.Gray, "stuck");
            Assert.True(entered.Wait(5_000));
            q.Enqueue(ConsoleColor.Gray, "behind it");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            Assert.False(writer.Stop(200));
            Assert.InRange(clock.ElapsedMilliseconds, 150, 2_000);
        }
        finally
        {
            release.Set();
            writer.Dispose();
        }
    }

    [Fact]
    public void Stop_waits_for_the_queue_and_lines_logged_while_stopping_still_go_out()
    {
        var q = new ConsoleLogQueue();
        var written = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var writer = new ConsoleBatchWriter(q, (_, text) => written.Enqueue(text), coalesceMs: 0);
        writer.Start();
        try
        {
            for (int i = 0; i < 50; i++) q.Enqueue(ConsoleColor.Gray, "before " + i);
            Assert.True(writer.Stop(5_000));
            Assert.Equal(0, q.Pending);
            Assert.True(writer.Stopping);

            // A later exit handler logs (the self-update promote, the unload lines): still written.
            q.Enqueue(ConsoleColor.Gray, "after stop");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!string.Concat(written).Contains("after stop") && clock.ElapsedMilliseconds < 5_000) Thread.Sleep(5);
            string all = string.Concat(written);
            Assert.Contains("before 49", all);
            Assert.Contains("after stop", all);
            Assert.True(all.IndexOf("before 0", StringComparison.Ordinal) < all.IndexOf("after stop", StringComparison.Ordinal));
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Fact]
    public void Many_producers_and_one_writer_lose_nothing_that_fit()
    {
        var q = new ConsoleLogQueue(maxLines: 100_000, maxChars: 10_000_000, batchLines: 64);
        const int threads = 4, per = 2_000;
        int seen = 0;
        using var done = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            while (true)
            {
                bool work = q.WaitForWork(20);
                var runs = q.TakeBatch();
                foreach (var r in runs) Interlocked.Add(ref seen, r.Lines);
                if (!work && done.IsCancellationRequested && q.Pending == 0) return;
            }
        });
        Parallel.For(0, threads, t =>
        {
            for (int i = 0; i < per; i++) q.Enqueue(ConsoleColor.Gray, "t" + t + " " + i);
        });
        done.Cancel();
        q.Wake();
        Assert.True(writer.Wait(10_000));
        Assert.Equal(threads * per, seen);
        Assert.Equal(0, q.Snapshot().Dropped);
    }
}

public class SessionLogWriterTests
{
    private sealed class SlowStream : MemoryStream
    {
        public readonly ManualResetEventSlim Release = new(false);
        public int Flushes;

        public override void Flush()
        {
            Interlocked.Increment(ref Flushes);
            Release.Wait(5_000);
            base.Flush();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Release.Wait(5_000);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Release.Wait(5_000);
            base.Write(buffer);
        }
    }

    [Fact]
    public void Lines_wait_in_memory_until_a_flush_writes_them_in_order()
    {
        var ms = new MemoryStream();
        var writer = new SessionLogWriter(ms, 1024 * 1024, timedFlush: false);
        writer.WriteLine("one");
        writer.WriteLine("two");
        Assert.Equal(2, writer.PendingLines);
        Assert.Equal(0, ms.Length);
        writer.Flush();
        Assert.Equal(0, writer.PendingLines);
        Assert.Equal("one" + Environment.NewLine + "two" + Environment.NewLine, Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public void A_slow_disk_never_blocks_the_logging_thread()
    {
        var stream = new SlowStream();
        var writer = new SessionLogWriter(stream, 1024 * 1024, timedFlush: false);
        writer.WriteLine("before");
        // A flush stuck on the disk (another thread, as the timer would be).
        var flusher = Task.Run(writer.Flush);
        Thread.Sleep(50);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) writer.WriteLine("during " + i);
        Assert.True(clock.ElapsedMilliseconds < 1000, "WriteLine waited for the disk: " + clock.ElapsedMilliseconds + " ms");
        Assert.Equal(1000, writer.PendingLines);
        stream.Release.Set();
        Assert.True(flusher.Wait(5_000));
        writer.Dispose();
        string text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.StartsWith("before" + Environment.NewLine + "during 0" + Environment.NewLine, text);
        Assert.EndsWith("during 999" + Environment.NewLine, text);
    }

    [Fact]
    public void An_urgent_line_is_written_at_once_without_a_timer()
    {
        var ms = new MemoryStream();
        using var writer = new SessionLogWriter(ms, 1024 * 1024, timedFlush: false);
        writer.WriteLine("ordinary");
        writer.WriteLine("error", flushNow: true);
        Assert.Equal(0, writer.PendingLines);
        Assert.Contains("error", Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public void An_urgent_line_is_on_disk_when_WriteLine_returns_if_nothing_else_is_writing()
    {
        var ms = new MemoryStream();
        using var writer = new SessionLogWriter(ms, 1024 * 1024, timedFlush: true);
        writer.WriteLine("ordinary");
        writer.WriteLine("error", flushNow: true);
        Assert.Equal(0, writer.PendingLines);
        Assert.Equal("ordinary" + Environment.NewLine + "error" + Environment.NewLine, Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public void An_urgent_line_during_a_slow_write_is_handed_to_the_timer_without_blocking()
    {
        var stream = new SlowStream();
        using var writer = new SessionLogWriter(stream, 1024 * 1024, timedFlush: true);
        writer.WriteLine("first");
        var flusher = Task.Run(writer.Flush); // holds the write lock, stuck on the disk
        Thread.Sleep(50);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        writer.WriteLine("error", flushNow: true);
        Assert.True(clock.ElapsedMilliseconds < 500, "the error line waited for the disk: " + clock.ElapsedMilliseconds + " ms");
        stream.Release.Set();
        Assert.True(flusher.Wait(5_000));
        clock.Restart();
        while (writer.PendingLines > 0 && clock.ElapsedMilliseconds < 900) Thread.Sleep(5);
        Assert.Equal(0, writer.PendingLines); // the timer ran at once, not a second later
        Assert.EndsWith("error" + Environment.NewLine, Encoding.UTF8.GetString(stream.ToArray()));
    }
}
