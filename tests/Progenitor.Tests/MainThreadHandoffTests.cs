using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Progenitor.Bootstrap;
using Progenitor.Dev;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// R9 finding 11: <see cref="MainThread.Post"/> work enqueued before attach must move onto
/// Bootstrap's queue in order and must not stay on Core's leftover list. Uses a fake
/// destination queue; the production path is the same <c>HandoffPending</c> call with
/// Bootstrap's <c>Post</c> / <c>PostDelayed</c>.
/// </summary>
[Collection("MainThreadQueue")]
public class MainThreadHandoffTests
{
    private static readonly object Gate = new();

    [Fact]
    public void Handoff_moves_pre_attach_posts_onto_the_fake_queue_in_order()
    {
        lock (Gate)
        {
            DiscardPending();
            try
            {
                var ran = new List<int>();
                MainThread.Post(() => ran.Add(1));
                MainThread.Post(() => ran.Add(2));
                MainThread.Post(() => ran.Add(3));

                var fake = new FakeQueue();
                MainThread.HandoffPending(fake.Post, fake.PostDelayed);

                Assert.Empty(ran);
                Assert.Equal(3, fake.Posts.Count);
                Assert.Empty(fake.Delayed);

                MainThread.Drain();
                Assert.Empty(ran);

                fake.RunPosted();
                Assert.Equal(new[] { 1, 2, 3 }, ran);
            }
            finally
            {
                DiscardPending();
            }
        }
    }

    [Fact]
    public async Task Handoff_does_not_lose_a_Run_completion()
    {
        lock (Gate)
        {
            DiscardPending();
        }
        try
        {
            Task<int> task;
            FakeQueue fake;
            lock (Gate)
            {
                task = MainThread.Run(() => 42);
                Assert.False(task.IsCompleted);

                fake = new FakeQueue();
                MainThread.HandoffPending(fake.Post, fake.PostDelayed);
                Assert.False(task.IsCompleted);
                Assert.Single(fake.Posts);

                MainThread.Drain();
                Assert.False(task.IsCompleted);

                fake.RunPosted();
                Assert.True(task.IsCompletedSuccessfully);
            }
            Assert.Equal(42, await task);
        }
        finally
        {
            lock (Gate) DiscardPending();
        }
    }

    [Fact]
    public void Handoff_forwards_delayed_work_with_remaining_time()
    {
        lock (Gate)
        {
            DiscardPending();
            try
            {
                bool ran = false;
                MainThread.PostDelayed(30, () => ran = true);

                var fake = new FakeQueue();
                MainThread.HandoffPending(fake.Post, fake.PostDelayed);

                Assert.Empty(fake.Posts);
                Assert.Single(fake.Delayed);
                Assert.InRange(fake.Delayed[0].seconds, 29, 30.5);
                Assert.False(ran);

                MainThread.Drain();
                Assert.False(ran);

                fake.Delayed[0].action();
                Assert.True(ran);
            }
            finally
            {
                DiscardPending();
            }
        }
    }

    [Fact]
    public void Handoff_preserves_owner_on_the_fake_queue()
    {
        lock (Gate)
        {
            DiscardPending();
            try
            {
                var owner = new object();
                MainThread.Post(() => { }, owner);

                var fake = new FakeQueue();
                MainThread.HandoffPending(fake.Post, fake.PostDelayed);

                Assert.Single(fake.Posts);
                Assert.Same(owner, fake.Posts[0].owner);
            }
            finally
            {
                DiscardPending();
            }
        }
    }

    [Fact]
    public void Null_handoff_destination_throws()
    {
        Assert.Throws<ArgumentNullException>(() => MainThread.HandoffPending(null!, (_, _, _) => { }));
        Assert.Throws<ArgumentNullException>(() => MainThread.HandoffPending((_, _) => { }, null!));
    }

    [Fact]
    public async Task Handoff_drop_on_the_destination_completes_the_Run_tcs()
    {
        lock (Gate)
        {
            MainThread.ResetForTests();
            WorkQueue.ResetForTests();
        }
        try
        {
            Task<int> task;
            lock (Gate)
            {
                task = MainThread.Run(() => 42);
                Assert.False(task.IsCompleted);
                MainThread.HandoffPending(WorkQueue.Post, WorkQueue.PostDelayed);
                Assert.False(task.IsCompleted);
                WorkQueue.DropOwnedBy(null, typeof(MainThreadHandoffTests).Assembly);
            }

            var dropped = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
            Assert.Equal(MainThread.DroppedBecauseUnloading, dropped.Message);
        }
        finally
        {
            lock (Gate)
            {
                MainThread.ResetForTests();
                WorkQueue.ResetForTests();
            }
        }
    }

    [Fact]
    public void UnretireNamed_forwards_to_WorkQueue()
    {
        lock (Gate)
        {
            MainThread.ResetForTests();
            WorkQueue.ResetForTests();
            try
            {
                MainThread.UnretireNamed(null);
                MainThread.UnretireNamed("progenitor-core:1");
            }
            finally
            {
                MainThread.ResetForTests();
                WorkQueue.ResetForTests();
            }
        }
    }

    [Fact]
    public void Skipping_handoff_leaves_posts_on_Core_queue_and_Drain_runs_them()
    {
        lock (Gate)
        {
            DiscardPending();
            try
            {
                var ran = new List<int>();
                MainThread.Post(() => ran.Add(1));
                MainThread.Post(() => ran.Add(2));

                Assert.Empty(ran);
                MainThread.Drain();
                Assert.Equal(new[] { 1, 2 }, ran);
            }
            finally
            {
                DiscardPending();
            }
        }
    }

    private static void DiscardPending() => MainThread.ResetForTests();

    private sealed class FakeQueue
    {
        public List<(Action action, object? owner)> Posts { get; } = new();
        public List<(double seconds, Action action, object? owner)> Delayed { get; } = new();

        public void Post(Action action, object? owner) => Posts.Add((action, owner));

        public void PostDelayed(double seconds, Action action, object? owner) =>
            Delayed.Add((seconds, action, owner));

        public void RunPosted()
        {
            for (int i = 0; i < Posts.Count; i++)
                Posts[i].action();
        }
    }
}

[CollectionDefinition("MainThreadQueue")]
public sealed class MainThreadQueueCollection
{
}
