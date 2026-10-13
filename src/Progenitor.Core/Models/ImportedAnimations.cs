#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Progenitor.Models;

/// <summary>Owned visual animation playback for an imported GLB's own node hierarchy.
/// Main thread only. Does not cast abilities, move gameplay entities or replace native controllers.</summary>
public sealed class ImportedAnimations : IDisposable
{
    private readonly GameObject _root;
    private readonly Transform[] _nodes;
    private readonly Dictionary<string, GltfAnimationSampler> _clips = new(StringComparer.Ordinal);
    private readonly float[] _position, _rotation, _scale, _restPosition, _restRotation, _restScale;
    private readonly float[] _fromPosition, _fromRotation, _fromScale;
    private GltfAnimationSampler? _current;
    private bool _disposed, _subscribed, _loop;
    private float _time, _speed, _blendTime, _blendDuration;

    internal ImportedAnimations(GameObject root, GltfScene scene, Transform[] nodes)
    {
        _root = root; _nodes = nodes;
        int count = nodes.Length;
        _position = new float[count * 3]; _rotation = new float[count * 4]; _scale = new float[count * 3];
        _fromPosition = new float[count * 3]; _fromRotation = new float[count * 4]; _fromScale = new float[count * 3];
        _restPosition = new float[count * 3]; _restRotation = new float[count * 4]; _restScale = new float[count * 3];
        for (int i = 0; i < count; i++)
        {
            Array.Copy(scene.Nodes[i].Translation, 0, _restPosition, i * 3, 3);
            Array.Copy(scene.Nodes[i].Rotation, 0, _restRotation, i * 4, 4);
            Array.Copy(scene.Nodes[i].Scale, 0, _restScale, i * 3, 3);
        }
        var names = new List<string>();
        foreach (var clip in scene.Animations) { _clips.Add(clip.Name, new GltfAnimationSampler(scene, clip)); names.Add(clip.Name); }
        ClipNames = names.AsReadOnly();
    }

    public IReadOnlyList<string> ClipNames { get; }
    public string? CurrentClip { get; private set; }
    public bool IsPlaying { get; private set; }
    public float TimeSeconds => _time;
    public float Duration => _current?.Duration ?? 0;

    /// <summary>Starts at time zero; optional crossfade snapshots the currently displayed pose.
    /// Speed must be positive. A non-looping clip holds its last pose until Stop or another Play.</summary>
    public void Play(string clip, bool loop = true, float speed = 1, float blendSeconds = 0)
    {
        EnsureAlive();
        if (clip == null || !_clips.TryGetValue(clip, out var sampler)) throw new ArgumentException("Unknown animation clip: " + clip, nameof(clip));
        if (!float.IsFinite(speed) || speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));
        if (!float.IsFinite(blendSeconds) || blendSeconds < 0) throw new ArgumentOutOfRangeException(nameof(blendSeconds));
        ReadPose(_fromPosition, _fromRotation, _fromScale);
        _current = sampler; CurrentClip = clip; _time = 0; _speed = speed; _loop = loop;
        _blendTime = 0; _blendDuration = blendSeconds;
        IsPlaying = true;
        Apply(); Subscribe();
    }

    public void Pause() { EnsureAlive(); IsPlaying = false; Unsubscribe(); }
    public void Resume()
    {
        EnsureAlive();
        if (_current == null) throw new InvalidOperationException("Play a clip before Resume.");
        IsPlaying = true; Subscribe();
    }
    /// <summary>Clamps to [0, duration], ends the crossfade and preserves paused/playing state.</summary>
    public void Seek(float seconds)
    {
        EnsureAlive();
        if (_current == null) throw new InvalidOperationException("Play a clip before Seek.");
        if (!float.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        _time = Math.Clamp(seconds, 0, _current.Duration); _blendDuration = 0; Apply();
    }
    /// <summary>Stops playback and restores the GLB's authored node transforms.</summary>
    public void Stop()
    {
        EnsureAlive(); Unsubscribe(); IsPlaying = false; CurrentClip = null; _current = null; _time = 0;
        WritePose(_restPosition, _restRotation, _restScale);
    }
    private void Subscribe() { if (_subscribed) return; Plugin.Frame += Tick; _subscribed = true; }
    private void Unsubscribe() { if (!_subscribed) return; Plugin.Frame -= Tick; _subscribed = false; }
    private void Tick()
    {
        if (_disposed || _root == null) { Dispose(); return; }
        if (!IsPlaying || _current == null) return;
        try
        {
            float dt = UnityEngine.Time.deltaTime;
            if (!float.IsFinite(dt) || dt < 0) return;
            double next = (double)_time + (double)dt * _speed;
            float duration = _current.Duration;
            _time = duration > 0 && _loop ? (float)(next % duration) : (float)Math.Min(next, duration);
            _blendTime = Math.Min(_blendDuration, _blendTime + dt);
            Apply();
            if ((duration == 0 || (!_loop && _time >= duration)) && _blendTime >= _blendDuration)
            { IsPlaying = false; Unsubscribe(); }
        }
        catch (Exception error)
        {
            IsPlaying = false; Unsubscribe();
            Plugin.Logger?.LogWarning("Models: animation stopped: " + error.Message);
        }
    }
    private void Apply()
    {
        _current!.Sample(_time, _position, _rotation, _scale);
        if (_blendDuration > 0 && _blendTime < _blendDuration)
        {
            float amount = _blendTime / _blendDuration;
            for (int i = 0; i < _position.Length; i++)
            {
                _position[i] = _fromPosition[i] + (_position[i] - _fromPosition[i]) * amount;
                _scale[i] = _fromScale[i] + (_scale[i] - _fromScale[i]) * amount;
            }
            for (int i = 0; i < _nodes.Length; i++)
            {
                int r = i * 4;
                var q = Quaternion.Slerp(new Quaternion(_fromRotation[r], _fromRotation[r+1], _fromRotation[r+2], _fromRotation[r+3]),
                    new Quaternion(_rotation[r], _rotation[r+1], _rotation[r+2], _rotation[r+3]), amount);
                _rotation[r] = q.x; _rotation[r+1] = q.y; _rotation[r+2] = q.z; _rotation[r+3] = q.w;
            }
        }
        WritePose(_position, _rotation, _scale);
    }
    private void ReadPose(float[] position, float[] rotation, float[] scale)
    {
        for (int i = 0; i < _nodes.Length; i++)
        {
            if (_nodes[i] == null) continue;
            var p = _nodes[i].localPosition; var q = _nodes[i].localRotation; var s = _nodes[i].localScale;
            int v = i * 3, r = i * 4;
            position[v] = p.x; position[v+1] = p.y; position[v+2] = p.z;
            rotation[r] = q.x; rotation[r+1] = q.y; rotation[r+2] = q.z; rotation[r+3] = q.w;
            scale[v] = s.x; scale[v+1] = s.y; scale[v+2] = s.z;
        }
    }
    private void WritePose(float[] position, float[] rotation, float[] scale)
    {
        for (int i = 0; i < _nodes.Length; i++)
        {
            if (_nodes[i] == null) continue;
            int v = i * 3, r = i * 4;
            _nodes[i].localPosition = new Vector3(position[v], position[v+1], position[v+2]);
            _nodes[i].localRotation = new Quaternion(rotation[r], rotation[r+1], rotation[r+2], rotation[r+3]);
            _nodes[i].localScale = new Vector3(scale[v], scale[v+1], scale[v+2]);
        }
    }
    private void EnsureAlive()
    {
        if (_disposed || _root == null) throw new ObjectDisposedException(nameof(ImportedAnimations));
    }
    public void Dispose() { if (_disposed) return; Unsubscribe(); IsPlaying = false; _disposed = true; _current = null; _clips.Clear(); }
}
#endif
