using Progenitor.Dev.Ops;
using Xunit;
using static Progenitor.Dev.Ops.CharacterCreatePlan;

namespace Progenitor.Tests;

/// <summary>The client <c>create_character</c> op: when it sends, and how it leaves the creation scene (<see cref="CharacterCreatePlan"/>).</summary>
public class BridgeCharacterCreatePlanTests
{
    private static State NoCharacter(string view = "None", string menu = "None", int scenes = 0) => new(false, menu, view, scenes);
    private static State Playing(string menu = "None", int scenes = 0) => new(true, menu, "None", scenes);
    private static State Loading(int scenes = 1) => new(true, "None", "GameConnectingAndLoading", scenes);

    /// <summary>Feeds <paramref name="s"/> until the tracker asks for something other than Wait.</summary>
    private static Step RunUntil(Tracker t, State s, long ms = 0, int max = 1000)
    {
        for (int i = 0; i < max; i++)
        {
            var step = t.Next(s, ms);
            if (step != Step.Wait) return step;
        }
        return Step.Wait;
    }

    [Fact]
    public void Sends_only_without_a_character()
    {
        Assert.Equal(Step.Send, Start(NoCharacter()));
        Assert.Equal(Step.Send, Start(NoCharacter(view: "GameConnectingAndLoading")));
        Assert.Equal(Step.Wait, Start(Playing()));
        Assert.Equal(Step.Wait, Start(Playing(CreationMenu)));
    }

    [Theory]
    [InlineData("Loading", true)]
    [InlineData("GameConnectingAndLoading", true)]
    [InlineData("None", false)]
    [InlineData("EscapeMenu", false)]
    [InlineData("", false)]
    public void Loading_views_are_the_loading_screens(string view, bool loading) =>
        Assert.Equal(loading, new State(true, "None", view, 0).Loading);

    [Fact]
    public void A_clean_client_is_done_after_the_stable_frames_without_any_call()
    {
        var t = new Tracker();
        for (int i = 1; i < StableFrames; i++) Assert.Equal(Step.Wait, t.Next(Playing(), 0));
        Assert.Equal(Step.Done, t.Next(Playing(), 0));
        Assert.False(t.LeaveIssued);
        Assert.False(t.ClearIssued);
        Assert.Equal("none", Handler(t));
        Assert.Null(Note(Step.Done));
    }

    [Fact]
    public void The_game_gets_its_grace_frames_to_leave_the_creation_menu_itself()
    {
        var t = new Tracker();
        for (int i = 1; i < GameExitGraceFrames; i++) Assert.Equal(Step.Wait, t.Next(Playing(CreationMenu, 1), 0));
        // The mapper's own exit: the menu goes to WakeUpMenu and OnStopRunning destroys the scene.
        Assert.Equal(Step.Done, RunUntil(t, Playing(WakeUpMenu)));
        Assert.False(t.LeaveIssued);
        Assert.True(t.SawCreation);
    }

    [Fact]
    public void A_creation_menu_that_stays_gets_GoToMenu_WakeUpMenu_once()
    {
        var t = new Tracker();
        Assert.Equal(Step.Leave, RunUntil(t, Playing(CreationMenu, 1)));
        Assert.Equal(GameExitGraceFrames, t.Frames);
        Assert.True(t.LeaveIssued);
        Assert.Equal(Step.Done, RunUntil(t, Playing(WakeUpMenu)));
        Assert.Equal("HUDMenuManager.GoToMenu(WakeUpMenu)", Handler(t));
    }

    [Fact]
    public void A_scene_left_by_the_loading_screen_is_cleared_once_in_game()
    {
        var t = new Tracker();
        Assert.Equal(Step.Clear, RunUntil(t, Playing(scenes: 1)));
        Assert.Equal(GameExitGraceFrames, t.Frames);
        Assert.Equal(Step.Done, RunUntil(t, Playing()));
        Assert.True(t.ClearIssued);
        Assert.False(t.LeaveIssued);
        Assert.Equal("Object.Destroy(creation scene)", Handler(t));
    }

    [Fact]
    public void The_scene_is_never_cleared_while_the_loading_screen_owns_it()
    {
        var t = new Tracker();
        for (int i = 0; i < GameExitGraceFrames * 3; i++) Assert.Equal(Step.Wait, t.Next(Loading(), 0));
        Assert.False(t.ClearIssued);
        // In game with the scene still there: a fresh grace period starts.
        for (int i = 1; i < GameExitGraceFrames; i++) Assert.Equal(Step.Wait, t.Next(Playing(scenes: 1), 0));
        Assert.Equal(Step.Clear, t.Next(Playing(scenes: 1), 0));
    }

    [Fact]
    public void A_menu_exit_that_leaves_the_scene_behind_gets_both_calls()
    {
        var t = new Tracker();
        Assert.Equal(Step.Leave, RunUntil(t, Playing(CreationMenu, 1)));
        Assert.Equal(Step.Clear, RunUntil(t, Playing(WakeUpMenu, 1)));
        Assert.Equal(Step.Done, RunUntil(t, Playing()));
        Assert.Equal("HUDMenuManager.GoToMenu(WakeUpMenu)+Object.Destroy(creation scene)", Handler(t));
    }

    [Fact]
    public void A_scene_that_survives_the_clear_is_reported_stuck()
    {
        var t = new Tracker();
        Assert.Equal(Step.Clear, RunUntil(t, Playing(scenes: 1)));
        for (int i = 1; i < VerifyFrames; i++) Assert.Equal(Step.Wait, t.Next(Playing(scenes: 1), 0));
        Assert.Equal(Step.Stuck, t.Next(Playing(scenes: 1), 0));
        Assert.Equal(Step.Stuck, t.Next(Playing(), 0));
        Assert.Equal("stuck", ResultName(Step.Stuck));
        Assert.Equal(StuckNote, Note(Step.Stuck));
    }

    [Fact]
    public void A_menu_that_survives_the_leave_is_reported_stuck()
    {
        var t = new Tracker();
        Assert.Equal(Step.Leave, RunUntil(t, Playing(CreationMenu)));
        Assert.Equal(Step.Stuck, RunUntil(t, Playing(CreationMenu)));
        Assert.Equal(GameExitGraceFrames + VerifyFrames, t.Frames);
    }

    [Fact]
    public void No_character_within_the_spawn_wait_is_not_spawned()
    {
        var t = new Tracker();
        Assert.Equal(Step.Wait, t.Next(NoCharacter(), SpawnWaitMs - 1));
        Assert.Equal(Step.NotSpawned, t.Next(NoCharacter(), SpawnWaitMs));
        Assert.Equal("not_spawned", ResultName(Step.NotSpawned));
        Assert.Equal(NotSpawnedNote, Note(Step.NotSpawned));
    }

    [Fact]
    public void A_character_that_arrives_late_still_gets_the_cleanup()
    {
        var t = new Tracker();
        Assert.Equal(Step.Wait, t.Next(NoCharacter(scenes: 1, menu: CreationMenu), 100));
        Assert.Equal(Step.Leave, RunUntil(t, Playing(CreationMenu, 1), 200));
    }

    [Fact]
    public void A_client_that_stays_on_its_loading_screen_is_reported_still_loading()
    {
        var t = new Tracker();
        Assert.Equal(Step.Wait, t.Next(NoCharacter(view: "GameConnectingAndLoading"), 0));
        Assert.Equal(Step.Wait, t.Next(Loading(), 1_000));
        Assert.Equal(Step.Wait, t.Next(Loading(), 1_000 + LoadingWaitMs - 1));
        Assert.Equal(Step.StillLoading, t.Next(Loading(), 1_000 + LoadingWaitMs));
        Assert.Equal("still_loading", ResultName(Step.StillLoading));
        Assert.Equal(LoadingNote, Note(Step.StillLoading));
    }

    [Fact]
    public void The_hard_deadline_ends_every_wait_within_the_bridge_timeout()
    {
        Assert.True(WaitMs + 5_000 < 30_000, "the reply must beat call.mjs's default 30 s timeout");
        Assert.Equal(Step.NotSpawned, new Tracker().Next(NoCharacter(), WaitMs));
        Assert.Equal(Step.StillLoading, new Tracker().Next(Loading(), WaitMs));
        Assert.Equal(Step.Stuck, new Tracker().Next(Playing(CreationMenu), WaitMs));
    }

    [Fact]
    public void The_scene_search_runs_every_tenth_poll_and_right_after_a_call()
    {
        Assert.True(ShouldScanScenes(0, false));
        Assert.False(ShouldScanScenes(1, false));
        Assert.False(ShouldScanScenes(SceneScanFrames - 1, false));
        Assert.True(ShouldScanScenes(SceneScanFrames, false));
        Assert.True(ShouldScanScenes(7, true));
        Assert.True(GameExitGraceFrames >= SceneScanFrames * 2, "the grace period must see more than one search");
    }

    [Fact]
    public void An_unreadable_read_counts_as_no_menu_and_no_loading_screen()
    {
        var unread = default(State);
        Assert.False(unread.OnCreationMenu);
        Assert.False(unread.Loading);
        Assert.Equal(Step.Send, Start(unread));
    }
}
