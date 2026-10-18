#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;

namespace Progenitor.Models;

// Dev reflection seam. One bounded preview; no gameplay entity, server request or OS input.
internal static class ModelPreviewBridge
{
    private static ModelHandle? _model;
    private static ModelPreview? _preview;
    private static DateTime _deadline;
    private static string? _error;
    private static bool _armed;
    private static int _epoch;

    public static Dictionary<string, object?> Open(string name, string? section, int seconds)
    {
        if (Plugin.IsServerProcess) throw new InvalidOperationException("model preview is client-only");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("model name is required");
        if (seconds < 1 || seconds > 300) throw new ArgumentOutOfRangeException(nameof(seconds), "duration must be 1..300 seconds");
        var parsed = string.IsNullOrWhiteSpace(section) ? ModelSection.None : ModelSection.Parse(section!);
        Close();
        _error = null;
        _model = new ModelHandle(parsed, name, "dev-preview");
        _model.Start();
        _deadline = DateTime.UtcNow.AddSeconds(seconds);
        _epoch = Progenitor.WorldState.WorldEpoch.Current;
        Plugin.Frame += Tick;
        _armed = true;
        return Status();
    }

    public static Dictionary<string, object?> Status() => new()
    {
        ["state"] = _error != null ? "failed" : _preview?.IsOpen == true ? "visible" : _model?.State.ToString().ToLowerInvariant() ?? "closed",
        ["name"] = _model?.PrefabName,
        ["section"] = _model?.Section.ToString(),
        ["cloneName"] = _preview?.CloneName,
        ["reason"] = _error ?? _model?.Reason,
        ["secondsRemaining"] = _model == null ? 0 : Math.Max(0, (int)(_deadline - DateTime.UtcNow).TotalSeconds),
        ["appearanceVerified"] = false,
    };

    public static Dictionary<string, object?> Close()
    {
        if (_armed) Plugin.Frame -= Tick;
        _armed = false;
        _preview?.Dispose();
        _preview = null;
        _model?.Dispose();
        _model = null;
        return Status();
    }

    private static void Tick()
    {
        try
        {
            if (_preview != null && !_preview.IsOpen) _preview = null;
            if (DateTime.UtcNow >= _deadline || _epoch != Progenitor.WorldState.WorldEpoch.Current) { Close(); return; }
            if (_model?.State == ModelLoadState.Failed)
            {
                _error = _model.Reason;
                Close();
            }
            else if (_preview == null && _model?.IsReady == true) _preview = _model.Preview();
        }
        catch (Exception e) { _error = e.Message; Close(); }
    }
}
#endif
