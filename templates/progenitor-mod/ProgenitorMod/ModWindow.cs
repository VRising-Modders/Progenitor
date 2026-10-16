// SPDX-License-Identifier: MIT
using System;
using Progenitor.Dev;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using UiApi = Progenitor.Ui.Ui;

namespace ProgenitorMod;

/// <summary>
/// The client window: the game's popup frame (<c>GameWindow</c>) with the Mods page's grid. Other layouts and every
/// control: docs/ui.md and docs/ui-controls.md in the Progenitor repository.
/// Text uses kit styles only (<c>Heading</c>, <c>Note</c>, <c>Label</c> with a <c>LabelTone</c>): never raw colors,
/// sizes or fonts. The grid aligns text left and numbers right; do not position controls yourself.
/// </summary>
internal sealed class ModWindow : IDisposable
{
    private readonly PluginContext _ctx;
    private readonly ModSettings _settings;
    // Escape hides a panel without telling you; OwnedPanel reads the real state.
    private readonly OwnedPanel _owned = new();
    private IUiToneLabel? _status;
    private RequestHandle? _request;
    private bool _disposed;

    internal ModWindow(PluginContext ctx, ModSettings settings)
    {
        _ctx = ctx;
        _settings = settings;
    }

    internal void Toggle()
    {
        if (_owned.IsOpen) Close();
        else Open();
    }

    private void Open()
    {
        Close();
        var panel = UiApi.Panel(MyPluginInfo.PLUGIN_NAME, _ctx.Owner, _ctx.Guid);
        panel.GameWindow();
        panel.Footer = MyPluginInfo.PLUGIN_NAME + " " + MyPluginInfo.PLUGIN_VERSION;

        panel.Heading("Server greeting");
        panel.Note("The server's admins set the greeting on this mod's settings page.");
        _status = panel.Label("Asking the server…", LabelTone.Pending);
        panel.BeginInline();
        panel.Button("Ask again").OnClick(() => Ask(1));
        panel.Button("Close").OnClick(Close);
        panel.EndInline();

        panel.Heading("This mod");
        panel.Fact("Version", MyPluginInfo.PLUGIN_VERSION);
        panel.Info("Server part", ServerPartText, 1);

        _owned.Adopt(panel)?.Dispose();
        UiApi.Show(panel);
        Ask(1);
    }

    private void Close()
    {
        _request?.Cancel();
        _request = null;
        _status = null;
        _owned.Take()?.Dispose();
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
        if (_disposed || !_owned.IsOpen) return;
        if (result.Ok && result.Reply is { } reply)
        {
            _status?.SetText(reply.Text, LabelTone.Ok);
            if (_settings.ShowNotices.Value)
                _ctx.Hud.Notify(ModRules.Notice(MyPluginInfo.PLUGIN_NAME, reply.Text), kind: UiNoticeKind.Success);
            return;
        }

        // Just works: retry quietly, keep the last good text, never show an error wall.
        if (ModRules.ShouldRetry(result.Status) && attempt < ModRules.MaxRetries)
        {
            MainThread.PostDelayed(ModRules.RetryDelaySeconds(attempt), () =>
            {
                if (!_disposed && _owned.IsOpen) Ask(attempt + 1);
            }, _ctx.Owner);
            return;
        }

        _status?.SetText(ModRules.Describe(result.Status, result.Reason), LabelTone.Muted);
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
