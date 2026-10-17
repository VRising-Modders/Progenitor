using System.Text.Json;
using Progenitor.Chat;
using Progenitor.Dev.Ops;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Findings of the 2026-10-17 live test: a label-only <c>widget_click</c> reaches a dialog over the HUD with the
/// workspace closed, the chat log takes the bridge's synthetic clicks, <c>chat_tabs move</c> replies with the new
/// order, and the chat history is written once when the client leaves the server, not again at the next world load.
/// </summary>
public sealed class LiveFindings1017Tests
{
    private static ModsUiArgs.Request Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ModsUiArgs.Parse(doc.RootElement.Clone());
    }

    [Fact]
    public void A_label_only_widget_click_reaches_an_open_dialog_with_the_workspace_closed()
    {
        var byLabel = Parse("{\"action\":\"widget_click\",\"label\":\"Local\"}");
        Assert.True(ModsUiArgs.TargetsDialog(byLabel));
        Assert.False(ModsUiArgs.RefusedWithWorkspaceClosed(byLabel, dialogOpen: true));
        // No dialog: still the workspace's, refused.
        Assert.True(ModsUiArgs.RefusedWithWorkspaceClosed(byLabel, dialogOpen: false));

        // A pane or an index names a workspace widget, never the dialog: refused with the workspace closed.
        var byPane = Parse("{\"action\":\"widget_click\",\"label\":\"Local\",\"pane\":\"content\"}");
        Assert.True(ModsUiArgs.RefusedWithWorkspaceClosed(byPane, dialogOpen: true));
        var byIndex = Parse("{\"action\":\"widget_click\",\"index\":2}");
        Assert.True(ModsUiArgs.RefusedWithWorkspaceClosed(byIndex, dialogOpen: true));

        // Other workspace actions stay refused, dialog or not; actions that never need the workspace are never refused.
        Assert.True(ModsUiArgs.RefusedWithWorkspaceClosed(Parse("{\"action\":\"widget_read\"}"), dialogOpen: true));
        Assert.False(ModsUiArgs.RefusedWithWorkspaceClosed(Parse("{\"action\":\"status\"}"), dialogOpen: false));
    }

    [Fact]
    public void The_bridge_gate_lets_the_dialog_click_through()
    {
        string ops = SourcePin.ReadFile("src", "Progenitor.Dev", "Ops", "ModsUiOps.cs");
        SourcePin.AssertUncommented(ops, "if (!ModsMenu.IsOpen && ModsUiArgs.RefusedWithWorkspaceClosed(request, ModsMenu.DialogOpen))");
        // The menu's click tries the open dialog before its own workspace-closed refusal.
        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        string click = SourcePin.MethodBody(menu, "public static object ClickVisibleWidget(");
        Assert.True(click.IndexOf("dialog.TryClickForBridge(label", System.StringComparison.Ordinal)
            < click.IndexOf("throw new ArgumentException(WorkspaceClosedMessage)", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Synthetic_press_edges_come_from_the_held_state()
    {
        var edges = new UiSyntheticPressEdges();
        Assert.Equal(UiSyntheticButtons.None, edges.Sample(true, UiSyntheticButtons.None));
        Assert.Equal(UiSyntheticButtons.Left, edges.Sample(true, UiSyntheticButtons.Left));
        // Held: no second press.
        Assert.Equal(UiSyntheticButtons.None, edges.Sample(true, UiSyntheticButtons.Left));
        Assert.Equal(UiSyntheticButtons.Right, edges.Sample(true, UiSyntheticButtons.Left | UiSyntheticButtons.Right));
        Assert.Equal(UiSyntheticButtons.None, edges.Sample(true, UiSyntheticButtons.None));
        Assert.Equal(UiSyntheticButtons.Left, edges.Sample(true, UiSyntheticButtons.Left));

        // Inactive: nothing pressed, and the held state is forgotten, so a lease that starts held presses once.
        Assert.Equal(UiSyntheticButtons.None, edges.Sample(false, UiSyntheticButtons.Left));
        Assert.Equal(UiSyntheticButtons.Left, edges.Sample(true, UiSyntheticButtons.Left));
    }

    [Fact]
    public void The_chat_log_reads_the_synthetic_presses()
    {
        string view = SourcePin.ReadFile("src", "Progenitor.Core", "Chat", "ChatView.cs");
        SourcePin.AssertUncommented(view, "_synthPressed = SynthClicks.Sample(UiSyntheticInput.Active, UiSyntheticInput.Buttons);");
        string clicks = SourcePin.MethodBody(view, "private static void HandleClicks(");
        SourcePin.AssertUncommented(clicks, "|| (_synthPressed & UiSyntheticButtons.Left) != 0;");
        SourcePin.AssertUncommented(clicks, "|| (_synthPressed & UiSyntheticButtons.Right) != 0;");
    }

    [Fact]
    public void A_tab_move_rebuilds_the_row_before_it_returns()
    {
        string tabs = SourcePin.ReadFile("src", "Progenitor.Core", "Chat", "ChatTabs.cs");
        string move = SourcePin.MethodBody(tabs, "internal static bool MoveTab(");
        int saved = move.IndexOf("TabsSetting.Value = ChatTab.Save(Set.Tabs);", System.StringComparison.Ordinal);
        int built = move.IndexOf("BuildIfNeeded();", System.StringComparison.Ordinal);
        Assert.True(saved >= 0 && built > saved);
        SourcePin.AssertUncommented(move, "Paint();");
    }

    [Fact]
    public void The_history_is_written_once_at_the_leave()
    {
        var h = new ChatHistorySession();
        Assert.Equal("", h.SaveHost);
        // Leaving before anything opened writes nothing.
        Assert.Equal("", h.Close(1));

        h.Open("10.0.0.1:9876");
        Assert.Equal("10.0.0.1:9876", h.SaveHost);

        // The leave writes this server's file once and closes it.
        Assert.Equal("10.0.0.1:9876", h.Close(1));
        Assert.True(h.Closed);
        Assert.Equal("", h.SaveHost);
        // A second notice of the same leave (the error, then the world ending) and the next world's load write nothing.
        Assert.Equal("", h.Close(1));
        Assert.Equal("", h.Close(1));
        Assert.False(h.RejoinedAt(1));

        // The next world opens the next server's history.
        h.Reset();
        Assert.False(h.Opened);
        h.Open("10.0.0.2:9876");
        Assert.Equal("10.0.0.2:9876", h.SaveHost);
    }

    [Fact]
    public void A_rejoin_in_the_world_it_left_starts_a_new_log()
    {
        var h = new ChatHistorySession();
        h.Open("host");
        h.Close(3);
        Assert.False(h.RejoinedAt(3));
        Assert.True(h.RejoinedAt(4));
        h.Reset();
        Assert.False(h.RejoinedAt(4));
    }

    [Fact]
    public void A_local_game_has_no_history_to_write()
    {
        var h = new ChatHistorySession();
        h.Open(null);
        Assert.Equal("", h.SaveHost);
        Assert.Equal("", h.Close(1));
    }

    [Fact]
    public void The_chat_view_saves_on_the_leave_and_not_at_the_next_load()
    {
        string window = SourcePin.ReadFile("src", "Progenitor.Core", "Chat", "ChatWindow.cs");
        SourcePin.AssertUncommented(window, "Progenitor.Networking.Handshake.ClientConnectionEnded += OnConnectionEnded;");
        SourcePin.AssertUncommented(SourcePin.MethodBody(window, "private static void OnConnectionEnded("), "ChatView.OnLeftServer();");
        string view = SourcePin.ReadFile("src", "Progenitor.Core", "Chat", "ChatView.cs");
        string leave = SourcePin.MethodBody(view, "private static void Leave(");
        SourcePin.AssertUncommented(leave, "string host = marker ? History.Close(Networking.Handshake.ClientConnectionSerial) : History.SaveHost;");
        SourcePin.AssertUncommented(leave, "if (host.Length == 0) return;");
        SourcePin.AssertUncommented(view, "private static bool SavesHistory => (KeepHistory?.Value ?? true) && History.SaveHost.Length > 0;");
    }
}
