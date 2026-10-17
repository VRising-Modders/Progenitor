using System;
using System.Collections.Generic;

namespace Progenitor.Chat;

/// <summary>
/// CHAT8: the chat view's cache of game names by prefab id (an item link's name, its description), game-free.
///
/// <para>A miss is not final. The view restores a server's history the moment the chat window appears, which can be
/// before the game's item data and string table are loaded; a lookup then fails for every id in the restored lines.
/// A miss is kept with a retry time (1 s, doubling to at most 30 s), <see cref="Poll"/> tries the due ids again, and
/// <see cref="Version"/> rises whenever an id that missed resolves, so the view redraws and the restored lines show
/// the names. A hit is kept for the cache's life (the client world's).</para>
/// </summary>
internal sealed class ChatNameCache
{
    internal const double FirstRetrySeconds = 1.0;
    internal const double MaxRetrySeconds = 30.0;

    /// <summary>The most missing ids tracked; beyond it a miss is not remembered (looked up again on the next draw).</summary>
    internal const int MaxMissing = 1024;

    private readonly Func<int, string?> _lookup;
    private readonly Dictionary<int, string> _known = new();
    private readonly Dictionary<int, Miss> _missing = new();
    private readonly List<int> _due = new();

    private struct Miss
    {
        internal double RetryAt;
        internal double Wait;
    }

    internal ChatNameCache(Func<int, string?> lookup) => _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));

    /// <summary>Rises each time an id that missed resolves, and on <see cref="Clear"/>: the drawn lines are stale.</summary>
    internal int Version { get; private set; }

    /// <summary>How many ids are waiting for a retry.</summary>
    internal int MissingCount => _missing.Count;

    /// <summary>The name for <paramref name="id"/>, or null while it is unknown. Looks up again only once a miss is due.</summary>
    internal string? Get(int id, double now)
    {
        if (_known.TryGetValue(id, out var name)) return name;
        bool missed = _missing.TryGetValue(id, out var miss);
        if (missed && now < miss.RetryAt) return null;
        return Look(id, now, missed ? miss : null);
    }

    /// <summary>Retry every due miss. True when at least one resolved (and <see cref="Version"/> rose).</summary>
    internal bool Poll(double now)
    {
        if (_missing.Count == 0) return false;
        _due.Clear();
        foreach (var pair in _missing)
            if (now >= pair.Value.RetryAt) _due.Add(pair.Key);
        int before = Version;
        foreach (int id in _due)
            Look(id, now, _missing[id]);
        _due.Clear();
        return Version != before;
    }

    /// <summary>Forget everything (a new client world). Raises <see cref="Version"/>.</summary>
    internal void Clear()
    {
        _known.Clear();
        _missing.Clear();
        Version++;
    }

    private string? Look(int id, double now, Miss? previous)
    {
        string? name;
        try { name = _lookup(id); }
        catch { name = null; }

        if (!string.IsNullOrWhiteSpace(name))
        {
            _known[id] = name!;
            if (_missing.Remove(id)) Version++;
            return name;
        }

        double wait = previous is { } p ? Math.Min(MaxRetrySeconds, p.Wait * 2) : FirstRetrySeconds;
        if (previous != null || _missing.Count < MaxMissing)
            _missing[id] = new Miss { RetryAt = now + wait, Wait = wait };
        return null;
    }
}
