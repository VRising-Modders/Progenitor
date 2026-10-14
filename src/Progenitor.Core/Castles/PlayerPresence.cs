using System;
using System.Collections.Generic;

namespace Progenitor.Castles;

/// <summary>
/// A connected player now stands on a different castle territory (or left every territory).
/// Server only, published on the main thread through <c>EventBus</c> by a once-a-second poll of
/// connected players' positions, and only while something is subscribed. Debounced: the new
/// territory must be read on <see cref="PlayerTerritoryTracker.DefaultConfirmPolls"/>
/// consecutive polls, so walking along a border does not flap.
/// </summary>
public sealed class PlayerTerritoryChanged
{
    /// <summary>Construct the event.</summary>
    public PlayerTerritoryChanged(CastleId user, CastleId character, int oldTerritory, int newTerritory, ICastleHandle castle, bool initial)
    {
        User = user;
        Character = character;
        OldTerritory = oldTerritory;
        NewTerritory = newTerritory;
        Castle = castle;
        Initial = initial;
    }

    /// <summary>User entity.</summary>
    public CastleId User { get; }

    /// <summary>Character entity.</summary>
    public CastleId Character { get; }

    /// <summary>Territory index the player stood on before; -1 for none (open world).</summary>
    public int OldTerritory { get; }

    /// <summary>Territory index the player stands on now; -1 when they left every castle plot.</summary>
    public int NewTerritory { get; }

    /// <summary>The castle on <see cref="NewTerritory"/>; a disabled handle when -1 or when the plot holds no heart.</summary>
    public ICastleHandle Castle { get; }

    /// <summary>
    /// True for the first reading after the player was first seen (login, or the poll starting):
    /// <see cref="OldTerritory"/> is -1 and the player is already standing on a plot.
    /// </summary>
    public bool Initial { get; }

    /// <inheritdoc />
    public override string ToString() =>
        "PlayerTerritoryChanged " + User + " " + OldTerritory + " -> " + NewTerritory + (Initial ? " (initial)" : "");
}

/// <summary>
/// A player finished loading in: once per session, after the connection was approved, the
/// character exists in the world and the client has started acknowledging the server's
/// snapshots. Server only. See <see cref="PlayerReadyTracker"/> for the signal.
/// </summary>
public sealed class PlayerReady
{
    /// <summary>Construct the event.</summary>
    public PlayerReady(CastleId user, CastleId character, ulong platformId, string name)
    {
        User = user;
        Character = character;
        PlatformId = platformId;
        Name = name ?? "";
    }

    /// <summary>User entity.</summary>
    public CastleId User { get; }

    /// <summary>Character entity.</summary>
    public CastleId Character { get; }

    /// <summary><c>User.PlatformId</c>.</summary>
    public ulong PlatformId { get; }

    /// <summary><c>User.CharacterName</c>.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public override string ToString() => "PlayerReady " + (Name.Length > 0 ? Name : User.ToString());
}

/// <summary>Per-player territory book with a debounce. Game-free.</summary>
internal sealed class PlayerTerritoryTracker
{
    /// <summary>Consecutive equal readings needed before a change is reported.</summary>
    public const int DefaultConfirmPolls = 2;

    private readonly int _confirm;
    private readonly Dictionary<long, State> _players = new();

    /// <summary>Construct with a debounce of <paramref name="confirmPolls"/> readings (minimum 1).</summary>
    public PlayerTerritoryTracker(int confirmPolls = DefaultConfirmPolls) => _confirm = Math.Max(1, confirmPolls);

    /// <summary>Players being tracked.</summary>
    public int Count => _players.Count;

    /// <summary>
    /// One reading. Returns true with the old and new territory when a change is confirmed. The
    /// first reading of a player sets their territory and reports it as <paramref name="initial"/>
    /// only when it is a castle plot (not -1).
    /// </summary>
    public bool Observe(long player, int territory, out int oldTerritory, out int newTerritory, out bool initial)
    {
        oldTerritory = newTerritory = -1;
        initial = false;
        if (!_players.TryGetValue(player, out var s))
        {
            _players[player] = new State { Current = territory, Candidate = territory, Count = 0 };
            if (territory < 0) return false;
            oldTerritory = -1;
            newTerritory = territory;
            initial = true;
            return true;
        }

        if (territory == s.Current)
        {
            s.Candidate = territory;
            s.Count = 0;
            return false;
        }

        if (territory != s.Candidate)
        {
            s.Candidate = territory;
            s.Count = 1;
        }
        else
        {
            s.Count++;
        }

        if (s.Count < _confirm) return false;
        oldTerritory = s.Current;
        newTerritory = territory;
        s.Current = territory;
        s.Count = 0;
        return true;
    }

    /// <summary>Forget players not in <paramref name="present"/> (disconnected). Returns how many.</summary>
    public int Retain(ICollection<long> present)
    {
        List<long>? gone = null;
        foreach (var k in _players.Keys)
            if (present == null || !present.Contains(k)) (gone ??= new List<long>()).Add(k);
        if (gone == null) return 0;
        foreach (var k in gone) _players.Remove(k);
        return gone.Count;
    }

    /// <summary>Forget everyone (world reload).</summary>
    public void Clear() => _players.Clear();

    private sealed class State
    {
        internal int Current;
        internal int Candidate;
        internal int Count;
    }
}

/// <summary>
/// Decides when a connected player has finished loading in. Game-free.
///
/// <para><b>Signal.</b> The server cannot see the client's loading screen. What it does see: the
/// user's <c>LocalCharacter</c> becomes a live entity once the character is in the world, and
/// <c>User.LastFullyAckedFrame</c> (the last snapshot frame the client confirmed) starts moving
/// once the client is simulating the world. Ready = the character is live and the acknowledged
/// frame has advanced past the first non-zero value seen this session. A player already in the
/// world when tracking starts (a Core hot swap) is marked done without an event.</para>
/// </summary>
internal sealed class PlayerReadyTracker
{
    private readonly Dictionary<long, State> _sessions = new();

    /// <summary>Sessions still waiting.</summary>
    public int Pending
    {
        get
        {
            int n = 0;
            foreach (var s in _sessions.Values) if (!s.Done) n++;
            return n;
        }
    }

    /// <summary>Copy the keys of the sessions still waiting into <paramref name="into"/> (cleared first).</summary>
    public void CopyPending(List<long> into)
    {
        into.Clear();
        foreach (var pair in _sessions)
            if (!pair.Value.Done) into.Add(pair.Key);
    }

    /// <summary>A session began (the connection was approved). Restarts the wait for that user.</summary>
    public void Connected(long user) => _sessions[user] = new State();

    /// <summary>Mark a user as already in the world (no event).</summary>
    public void AlreadyReady(long user) => _sessions[user] = new State { Done = true };

    /// <summary>The session ended.</summary>
    public void Disconnected(long user) => _sessions.Remove(user);

    /// <summary>True when <paramref name="user"/> is being waited on.</summary>
    public bool IsPending(long user) => _sessions.TryGetValue(user, out var s) && !s.Done;

    /// <summary>
    /// One reading of a pending user. Returns true exactly once per session, when the player
    /// has finished loading in.
    /// </summary>
    public bool Observe(long user, bool characterLive, int lastAckedFrame)
    {
        if (!_sessions.TryGetValue(user, out var s) || s.Done) return false;
        if (!characterLive || lastAckedFrame <= 0) return false;
        if (s.FirstAck == 0)
        {
            s.FirstAck = lastAckedFrame;
            return false;
        }

        if (lastAckedFrame == s.FirstAck) return false;
        s.Done = true;
        return true;
    }

    /// <summary>Forget everything.</summary>
    public void Clear() => _sessions.Clear();

    private sealed class State
    {
        internal bool Done;
        internal int FirstAck;
    }
}
