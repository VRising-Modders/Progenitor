// SPDX-License-Identifier: MIT
using System;
using Progenitor.Dev;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;

namespace ProgenitorMod;

/// <summary>
/// The client window: a standard Basic window (<c>ctx.Windows.Create</c>), the preset for a small tool the player opens
/// during play. Header (a toolbar and tabs for one thing the player works on) and Tabbed (a large screen with an info
/// column) are the other presets; docs/ui-controls.md in the Progenitor repository lists every control and the Gallery
/// shows each one live.
/// Fonts, colors and alignment: every line takes a named style (<c>panel.Text(text, UiTextStyle.X)</c>) or a control
/// that fixes its own. Never set a color, a size, a font or an alignment yourself; the window lays its rows out.
/// </summary>
internal sealed class ModWindow : IDisposable
{
    private readonly PluginContext _ctx;
    private readonly ModSettings _settings;
    private IUiWindow? _window;
    private IUiToneLabel? _status;
    private RequestHandle? _request;
    private bool _disposed;

    internal ModWindow(PluginContext ctx, ModSettings settings)
    {
        _ctx = ctx;
        _settings = settings;
    }

    // IsOpen reads the window's real state, so a window the player closed with Escape reads closed.
    internal void Toggle()
    {
        if (_window is { IsOpen: true }) Close();
        else Open();
    }

    private void Open()
    {
        Close();
        // The window draws the title, the frame and the version stamp (mod name, version, Progenitor version).
        var window = _ctx.Windows.Create(UiWindowPreset.Basic, MyPluginInfo.PLUGIN_NAME);
        var panel = window.Panel;

        panel.Text("Server greeting", UiTextStyle.Section);
        panel.Text("The server's admins set the greeting on this mod's settings page.", UiTextStyle.Muted);
        // A status that changes in place: a tone label in a style's tone, rewritten with SetText.
        _status = panel.Label("Asking the server…", UiTextStyles.ToneOf(UiTextStyle.Warning));
        panel.BeginInline();
        panel.Button("Ask again").OnClick(() => Ask(1));
        panel.Button("Close").OnClick(Close);
        panel.EndInline();

        panel.Text("This mod", UiTextStyle.Section);
        // Numbers (a version, a count, a time) use the Number style, so they line up on the right.
        panel.Text("Version", MyPluginInfo.PLUGIN_VERSION, UiTextStyle.Number);
        panel.Info("Server part", ServerPartText, 1);

        _window = window;
        window.Show();
        Ask(1);
    }

    private void Close()
    {
        _request?.Cancel();
        _request = null;
        _status = null;
        _window?.Dispose();
        _window = null;
    }

    private void Ask(int attempt)
    {
        _request?.Cancel();
        _request = _ctx.Requests.Send<GreetingRequest, GreetingReply>(
            new GreetingRequest { ClientVersion = MyPluginInfo.PLUGIN_VERSION },
            result => Answered(result, attempt),
            timeoutSeconds: 10);
    }

    private void Answered(RequestResult<GreetingReply> result, int attempt)
    {
        if (_disposed || _window is not { IsOpen: true }) return;
        if (result.Ok && result.Reply is { } reply)
        {
            _status?.SetText(reply.Text, UiTextStyles.ToneOf(UiTextStyle.Success));
            // Progenitor puts "<Mod Name>: " in front of every notice, chat message and command reply: pass the
            // message alone.
            if (_settings.ShowNotices.Value)
                _ctx.Hud.Notify(reply.Text, kind: UiNoticeKind.Success);
            return;
        }

        // Just works: retry quietly, keep the last good text, never show an error wall.
        if (ModRules.ShouldRetry(result.Status) && attempt < ModRules.MaxRetries)
        {
            MainThread.PostDelayed(ModRules.RetryDelaySeconds(attempt), () =>
            {
                if (!_disposed && _window is { IsOpen: true }) Ask(attempt + 1);
            }, _ctx.Owner);
            return;
        }

        _status?.SetText(ModRules.Describe(result.Status, result.Reason), UiTextStyles.ToneOf(UiTextStyle.Muted));
    }

    private string ServerPartText() => _ctx.Handshake.ServerPart switch
    {
        CounterpartPresence.Running => "Running on this server",
        CounterpartPresence.Absent => "Not on this server",
        _ => "Unknown",
    };

    public void Dispose()
    {
        _disposed = true;
        Close();
    }
}
