using System;
using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// P7 item 3: the game-free half of <c>ctx.Hud.PickInWorld</c> — the state machine, the one-pick rule,
/// which menus count as which kind, timeout and world-change endings, owner drop, re-arming from a
/// callback, and the list that keeps repeated panel requests closed after a suppressed pick.
/// </summary>
public sealed class UiWorldPickTests
{
    private sealed class FakeBackend : IUiWorldPickBackend
    {
        public bool IsReady = true;
        public int ArmedCalls;
        public int DisarmedCalls;
        public UiWorldPickHandle? LastArmed;

        public bool Ready(out string why)
        {
            why = IsReady ? "" : "no client world";
            return IsReady;
        }

        public void Armed(UiWorldPickHandle pick)
        {
            ArmedCalls++;
            LastArmed = pick;
        }

        public void Disarmed() => DisarmedCalls++;
    }

    private sealed class Rig
    {
        public readonly FakeBackend Backend = new();
        public double Now;
        public int Epoch = 7;
        public readonly List<string> Log = new();
        public readonly UiWorldPickCoordinator Coordinator;

        public Rig() => Coordinator = new UiWorldPickCoordinator(Backend, () => Now, () => Epoch, Log.Add);
    }

    private static readonly object OwnerA = new();
    private static readonly object OwnerB = new();

    [Fact]
    public void Arm_ShowsThePickAndWaits()
    {
        var rig = new Rig();
        var pick = rig.Coordinator.Arm(OwnerA, "A", _ => { }, null, null);

        Assert.Equal(UiWorldPickState.Armed, pick.State);
        Assert.True(pick.Active);
        Assert.Same(pick, rig.Coordinator.Current);
        Assert.Equal(1, rig.Backend.ArmedCalls);
        Assert.Equal(0, rig.Backend.DisarmedCalls);
    }

    [Fact]
    public void ContainerInteract_IsPickedAndSuppressedByDefault()
    {
        var rig = new Rig();
        UiWorldPickResult? got = null;
        bool cancelled = false;
        var pick = rig.Coordinator.Arm(OwnerA, "A", r => got = r, () => cancelled = true, null);

        Assert.Equal(UiWorldPickOffer.TakeAndSuppress, rig.Coordinator.Offer(UiWorldPickMenus.ContainerMenu));
        var result = rig.Coordinator.Complete(UiWorldPickMenus.ContainerMenu, 1234, 5, rig.Epoch, suppressed: true);

        Assert.NotNull(result);
        Assert.NotNull(got);
        Assert.False(cancelled);
        Assert.Equal(UiWorldPickState.Picked, pick.State);
        Assert.False(pick.Active);
        Assert.Null(rig.Coordinator.Current);
        Assert.Equal(1, rig.Backend.DisarmedCalls);
        var r = got!.Value;
        Assert.Equal(1234, r.EntityIndex);
        Assert.Equal(5, r.EntityVersion);
        Assert.Equal(UiWorldPickKind.Container, r.Kind);
        Assert.Equal("ContainerMenu", r.Menu);
        Assert.Equal(UiWorldPickMenus.ContainerMenu, r.MenuType);
        Assert.Equal(7, r.WorldEpoch);
        Assert.True(r.Suppressed);
        Assert.True(r.IsCurrentIn(7));
        Assert.False(r.IsCurrentIn(8));
    }

    [Fact]
    public void UnwantedKind_PassesThroughAndStaysArmed()
    {
        var rig = new Rig();
        var pick = rig.Coordinator.Arm(OwnerA, "A", _ => { }, null, null);

        Assert.Equal(UiWorldPickOffer.PassThrough, rig.Coordinator.Offer(UiWorldPickMenus.CastleHeartMenu));
        Assert.Equal(UiWorldPickOffer.PassThrough, rig.Coordinator.Offer(5 /* map, not an interact menu */));
        Assert.Null(rig.Coordinator.Complete(UiWorldPickMenus.CastleHeartMenu, 1, 1, rig.Epoch, true));
        Assert.Equal(UiWorldPickState.Armed, pick.State);
    }

    [Fact]
    public void KindsOption_SelectsWhatEndsThePick()
    {
        var rig = new Rig();
        rig.Coordinator.Arm(OwnerA, "A", _ => { }, null, new UiWorldPickOptions { Kinds = UiWorldPickKinds.Stations });

        Assert.Equal(UiWorldPickOffer.PassThrough, rig.Coordinator.Offer(UiWorldPickMenus.ContainerMenu));
        Assert.Equal(UiWorldPickOffer.TakeAndSuppress, rig.Coordinator.Offer(UiWorldPickMenus.RefinementstationMenu));
        Assert.Equal(UiWorldPickOffer.TakeAndSuppress, rig.Coordinator.Offer(UiWorldPickMenus.WorkstationMenu));
    }

    [Fact]
    public void SuppressPanelFalse_TakesButLetsThePanelOpen()
    {
        var rig = new Rig();
        rig.Coordinator.Arm(OwnerA, "A", _ => { }, null, new UiWorldPickOptions { SuppressPanel = false });
        Assert.Equal(UiWorldPickOffer.TakeAndOpen, rig.Coordinator.Offer(UiWorldPickMenus.ContainerMenu));
    }

    [Fact]
    public void SecondPick_CancelsTheFirstThenArms()
    {
        var rig = new Rig();
        var order = new List<string>();
        var first = rig.Coordinator.Arm(OwnerA, "A", _ => order.Add("A picked"), () => order.Add("A cancelled"), null);
        var second = rig.Coordinator.Arm(OwnerB, "B", _ => order.Add("B picked"), () => order.Add("B cancelled"), null);

        Assert.Equal(UiWorldPickState.Cancelled, first.State);
        Assert.Equal(UiWorldPickState.Armed, second.State);
        Assert.Same(second, rig.Coordinator.Current);
        Assert.Equal(new[] { "A cancelled" }, order);
        Assert.Equal(2, rig.Backend.ArmedCalls);
        Assert.Equal(1, rig.Backend.DisarmedCalls);
    }

    [Fact]
    public void Escape_CancelsOnlyWhenArmed()
    {
        var rig = new Rig();
        Assert.False(rig.Coordinator.Escape());

        int cancelled = 0;
        var pick = rig.Coordinator.Arm(OwnerA, "A", _ => { }, () => cancelled++, null);
        Assert.True(rig.Coordinator.Escape());
        Assert.Equal(UiWorldPickState.Cancelled, pick.State);
        Assert.Equal(1, cancelled);
        Assert.False(rig.Coordinator.Escape());
        Assert.Equal(1, cancelled);
    }

    [Fact]
    public void Timeout_EndsAsTimedOutAndRunsOnCancelled()
    {
        var rig = new Rig();
        int cancelled = 0;
        var pick = rig.Coordinator.Arm(OwnerA, "A", _ => { }, () => cancelled++, new UiWorldPickOptions { TimeoutSeconds = 5 });

        rig.Now = 4.9;
        rig.Coordinator.Tick();
        Assert.Equal(UiWorldPickState.Armed, pick.State);

        rig.Now = 5.0;
        rig.Coordinator.Tick();
        Assert.Equal(UiWorldPickState.TimedOut, pick.State);
        Assert.Equal(1, cancelled);
    }

    [Fact]
    public void NoTimeout_WaitsForever()
    {
        var rig = new Rig();
        var pick = rig.Coordinator.Arm(OwnerA, "A", _ => { }, null, null);
        rig.Now = 1e6;
        rig.Coordinator.Tick();
        Assert.Equal(UiWorldPickState.Armed, pick.State);
    }

    [Fact]
    public void WorldChange_CancelsOnTickAndOnComplete()
    {
        var rig = new Rig();
        int cancelled = 0;
        var a = rig.Coordinator.Arm(OwnerA, "A", _ => { }, () => cancelled++, null);
        rig.Epoch = 8;
        rig.Coordinator.Tick();
        Assert.Equal(UiWorldPickState.Cancelled, a.State);
        Assert.Equal(1, cancelled);

        bool picked = false;
        var b = rig.Coordinator.Arm(OwnerA, "A", _ => picked = true, () => cancelled++, null);
        Assert.Null(rig.Coordinator.Complete(UiWorldPickMenus.ContainerMenu, 1, 1, epoch: 9, suppressed: true));
        Assert.False(picked);
        Assert.Equal(UiWorldPickState.Cancelled, b.State);
        Assert.Equal(2, cancelled);
    }

    [Fact]
    public void NotReady_CancelsAtOnceWithoutArming()
    {
        var rig = new Rig();
        rig.Backend.IsReady = false;
        int cancelled = 0;
        var pick = rig.Coordinator.Arm(OwnerA, "A", _ => { }, () => cancelled++, null);

        Assert.Equal(UiWorldPickState.Cancelled, pick.State);
        Assert.Equal(1, cancelled);
        Assert.Equal(0, rig.Backend.ArmedCalls);
        Assert.Null(rig.Coordinator.Current);
    }

    [Fact]
    public void Dispose_EndsSilently_AndIsANoOpOnceEnded()
    {
        var rig = new Rig();
        bool any = false;
        var pick = rig.Coordinator.Arm(OwnerA, "A", _ => any = true, () => any = true, null);
        pick.Dispose();
        Assert.Equal(UiWorldPickState.Disposed, pick.State);
        Assert.False(any);
        Assert.Equal(1, rig.Backend.DisarmedCalls);

        var done = rig.Coordinator.Arm(OwnerA, "A", _ => { }, null, null);
        rig.Coordinator.Complete(UiWorldPickMenus.ContainerMenu, 1, 1, rig.Epoch, true);
        done.Dispose();
        done.Cancel();
        Assert.Equal(UiWorldPickState.Picked, done.State);
    }

    [Fact]
    public void Cancel_RunsOnCancelledOnce()
    {
        var rig = new Rig();
        int cancelled = 0;
        var pick = rig.Coordinator.Arm(OwnerA, "A", _ => { }, () => cancelled++, null);
        pick.Cancel();
        pick.Cancel();
        Assert.Equal(UiWorldPickState.Cancelled, pick.State);
        Assert.Equal(1, cancelled);
    }

    [Fact]
    public void DropOwner_OnlyDropsThatOwnersPick_Silently()
    {
        var rig = new Rig();
        bool any = false;
        var pick = rig.Coordinator.Arm(OwnerA, "A", _ => any = true, () => any = true, null);

        Assert.Equal(0, rig.Coordinator.DropOwner(OwnerB));
        Assert.Equal(UiWorldPickState.Armed, pick.State);
        Assert.Equal(1, rig.Coordinator.DropOwner(OwnerA));
        Assert.Equal(UiWorldPickState.Disposed, pick.State);
        Assert.False(any);
    }

    [Fact]
    public void Reset_EndsTheArmedPickSilently()
    {
        var rig = new Rig();
        bool any = false;
        var pick = rig.Coordinator.Arm(OwnerA, "A", _ => any = true, () => any = true, null);
        rig.Coordinator.Reset();
        Assert.Equal(UiWorldPickState.Disposed, pick.State);
        Assert.False(any);
        Assert.Null(rig.Coordinator.Current);
    }

    [Fact]
    public void Callback_CanArmTheNextPick()
    {
        var rig = new Rig();
        UiWorldPickHandle? next = null;
        var first = rig.Coordinator.Arm(OwnerA, "A",
            _ => next = rig.Coordinator.Arm(OwnerA, "A", _ => { }, null, null), null, null);
        rig.Coordinator.Complete(UiWorldPickMenus.WorkstationMenu, 2, 3, rig.Epoch, true);

        Assert.Equal(UiWorldPickState.Picked, first.State);
        Assert.NotNull(next);
        Assert.Equal(UiWorldPickState.Armed, next!.State);
        Assert.Same(next, rig.Coordinator.Current);
        Assert.Equal(2, rig.Backend.ArmedCalls);
    }

    [Fact]
    public void ThrowingCallback_IsLoggedAndStateStaysRight()
    {
        var rig = new Rig();
        var pick = rig.Coordinator.Arm(OwnerA, "A", _ => throw new InvalidOperationException("boom"), null, null);
        rig.Coordinator.Complete(UiWorldPickMenus.ContainerMenu, 1, 1, rig.Epoch, true);
        Assert.Equal(UiWorldPickState.Picked, pick.State);
        Assert.Contains(rig.Log, l => l.Contains("onPicked threw") && l.Contains("boom"));
    }

    [Fact]
    public void Options_AreCopiedAndNormalized()
    {
        var options = new UiWorldPickOptions { Prompt = "  ", TimeoutSeconds = double.NaN };
        var rig = new Rig();
        var pick = rig.Coordinator.Arm(OwnerA, "A", _ => { }, null, options);
        options.Kinds = UiWorldPickKinds.None;

        Assert.Equal(UiWorldPickOptions.DefaultPrompt, pick.Options.Prompt);
        Assert.Equal(0, pick.Options.TimeoutSeconds);
        Assert.Equal(UiWorldPickKinds.Containers | UiWorldPickKinds.Stations, pick.Options.Kinds);
    }

    [Fact]
    public void Inert_StaysIdle()
    {
        var pick = UiWorldPickHandle.Inert();
        pick.Cancel();
        pick.Dispose();
        Assert.Equal(UiWorldPickState.Idle, pick.State);
        Assert.False(pick.Active);
    }

    [Theory]
    [InlineData(UiWorldPickMenus.ContainerMenu, UiWorldPickKind.Container)]
    [InlineData(UiWorldPickMenus.WorkstationMenu, UiWorldPickKind.Station)]
    [InlineData(UiWorldPickMenus.RefinementstationMenu, UiWorldPickKind.Station)]
    [InlineData(UiWorldPickMenus.ResearchstationMenu, UiWorldPickKind.Station)]
    [InlineData(UiWorldPickMenus.ForgeMenu, UiWorldPickKind.Station)]
    [InlineData(UiWorldPickMenus.InventoryRouteStationMenu, UiWorldPickKind.Station)]
    [InlineData(UiWorldPickMenus.CastleHeartMenu, UiWorldPickKind.Other)]
    [InlineData(UiWorldPickMenus.TraderMenu, UiWorldPickKind.Other)]
    public void Menus_HaveTheirKind(int menu, UiWorldPickKind kind)
    {
        Assert.True(UiWorldPickMenus.TryGet(menu, out var info));
        Assert.Equal(kind, info.Kind);
        Assert.True(UiWorldPickMenus.Wants(UiWorldPickKinds.All, menu, out _));
        Assert.False(UiWorldPickMenus.Wants(UiWorldPickKinds.None, menu, out _));
    }

    [Theory]
    [InlineData(0)] // None
    [InlineData(1)] // CharacterInfo
    [InlineData(5)] // MapMenu
    [InlineData(15)] // WaypointMapMenu: an interact menu, but no target in its init data
    [InlineData(20)] // BloodAltarMenu: the same
    public void NonPickableMenus_AreNotListed(int menu) => Assert.False(UiWorldPickMenus.TryGet(menu, out _));

    [Fact]
    public void Menus_EveryKindIsRepresented()
    {
        var kinds = new HashSet<UiWorldPickKind>();
        foreach (var info in UiWorldPickMenus.All) kinds.Add(info.Kind);
        Assert.Equal(3, kinds.Count);
    }

    [Fact]
    public void SwallowList_RemembersUntilExpiryOrGone()
    {
        var list = new UiWorldPickSwallowList();
        long key = UiWorldPickSwallowList.Key(4096, 3);
        Assert.Equal(4096, (int)(key >> 32));
        Assert.Equal(3, (int)(uint)key);

        list.Add(key, 10);
        Assert.True(list.Contains(key, 10 + UiWorldPickSwallowList.Ttl - 0.01));
        Assert.False(list.Contains(key, 10 + UiWorldPickSwallowList.Ttl));
        Assert.False(list.Contains(UiWorldPickSwallowList.Key(4096, 4), 10));

        list.Prune(11, _ => true);
        Assert.Equal(1, list.Count);
        list.Prune(11, _ => false);
        Assert.Equal(0, list.Count);

        list.Add(key, 20);
        list.Add(key, 21);
        Assert.Equal(1, list.Count);
        list.Prune(21 + UiWorldPickSwallowList.Ttl);
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void SwallowList_KeyHandlesNegativeVersions()
    {
        long key = UiWorldPickSwallowList.Key(1, -1);
        Assert.Equal(1, (int)(key >> 32));
        Assert.Equal(-1, (int)(uint)key);
    }
}
