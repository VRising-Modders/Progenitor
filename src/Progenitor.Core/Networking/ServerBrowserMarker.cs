using System;
using System.Collections.Generic;

namespace Progenitor.Networking;

/// <summary>
/// BROWSERFILTER1 (docs/server-list-description.md, "Progenitor servers in Join Game"): the marker a Progenitor
/// server publishes in the game's own server-list tags, and the test the client's Join Game filter applies to a row.
/// Game-free.
///
/// <para><b>Why a tag.</b> Both list backends hand the client every server's tags with the row itself: a Steam row's
/// <c>gameserveritem_t</c> game tags and an EOS session's <c>TAGS</c> attribute, each lowercased and split on commas
/// into <c>ServerListEntryData.Tags</c>. The description is not in a Steam row at all (it arrives only when a player
/// selects that server, over a rules query), and an admin can edit, cut or imitate it. The tag is one fixed token the
/// server adds through the game's own <c>ServerBootstrapSystem.SetServerGameTag</c>; a vanilla client ignores tags it
/// does not know.</para>
///
/// <para><b>What it proves.</b> That the server says it runs Progenitor. Any server can publish any tag, so this is a
/// listing filter, not a trust check: joining still runs the handshake.</para>
/// </summary>
internal static class ServerBrowserMarker
{
    /// <summary>The tag a Progenitor server publishes. Lowercase: the client lowercases every tag it reads.</summary>
    public const string Tag = "progenitor";

    /// <summary>The Steam game-tag string's limit (<c>k_cbMaxGameServerTags</c>, bytes including the terminator).</summary>
    public const int SteamTagLimit = 128;

    /// <summary>
    /// True when <paramref name="tags"/> holds exactly <see cref="Tag"/> (case and surrounding spaces ignored). A tag
    /// that only contains the word (<c>notprogenitor</c>, <c>progenitor2</c>) does not count.
    /// </summary>
    public static bool IsMarked(IEnumerable<string?>? tags)
    {
        if (tags == null) return false;
        foreach (string? tag in tags)
        {
            if (tag != null && string.Equals(tag.Trim(), Tag, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// The server side: whether the joined tag string (the game's <c>string.Join(",", _GameTags)</c>) still fits Steam's
    /// limit once <see cref="Tag"/> is added. The tag is added either way; the server logs a warning when Steam would
    /// cut the string. The EOS <c>TAGS</c> attribute's own length limit was not checked.
    /// </summary>
    public static bool FitsSteam(IReadOnlyList<string>? currentTags)
    {
        int length = 0;
        int count = 0;
        bool present = false;
        if (currentTags != null)
        {
            foreach (string t in currentTags)
            {
                if (string.IsNullOrEmpty(t)) continue;
                if (string.Equals(t, Tag, StringComparison.Ordinal)) present = true;
                length += t.Length;
                count++;
            }
        }

        if (!present)
        {
            length += Tag.Length;
            count++;
        }

        int joined = length + Math.Max(0, count - 1);
        return joined < SteamTagLimit;
    }
}

/// <summary>
/// BROWSERFILTER1: the game's <c>Servers: N (M total)</c> count line while the filter is on. Game-free.
///
/// <para>Live 2026.10.143 (1920x1080): the earlier suffix <c> · Progenitor only</c> made the line
/// <c>Servers: 0 (1,538 total) · Progenitor only</c>, which wrapped and drew its last word below the filter panel. The
/// suffix is now <see cref="Suffix"/>, and it is added only when the whole line, measured with the line's own font,
/// fits the line's width; otherwise the game's own text is left as it is. With no measurement or no width the game's
/// text is left as well, so the suffix never makes the line wrap.</para>
/// </summary>
internal static class ServerBrowserCountLine
{
    /// <summary>Appended to the game's <c>Servers: N (M total)</c> line while the filter is on and the line fits.</summary>
    public const string Suffix = " · Progenitor";

    /// <summary>The suffix an earlier Core wrote; stripped before measuring so a line is never marked twice.</summary>
    internal const string OldSuffix = " · Progenitor only";

    /// <summary>The game's text without a suffix this filter (or an earlier Core) added.</summary>
    public static string Base(string? text)
    {
        text ??= "";
        if (text.EndsWith(OldSuffix, StringComparison.Ordinal)) return text.Substring(0, text.Length - OldSuffix.Length);
        if (text.EndsWith(Suffix, StringComparison.Ordinal)) return text.Substring(0, text.Length - Suffix.Length);
        return text;
    }

    /// <summary>
    /// The line to draw: <paramref name="gameText"/> + <see cref="Suffix"/> when <paramref name="measure"/> says it fits
    /// <paramref name="width"/> (half a pixel of slack), else the game's text. An empty text, a width that is not
    /// positive, or a measurement that is missing, negative or not finite leaves the game's text.
    /// </summary>
    public static string Fit(string? gameText, float width, Func<string, float>? measure)
    {
        string text = Base(gameText);
        if (text.Length == 0 || measure == null || !(width > 0f) || float.IsInfinity(width)) return text;
        string marked = text + Suffix;
        float w;
        try { w = measure(marked); }
        catch { return text; }
        if (w < 0f || float.IsNaN(w) || float.IsInfinity(w)) return text;
        return w <= width + 0.5f ? marked : text;
    }
}

/// <summary>
/// The rows the Join Game filter reads and trims: the game's shown list (<c>PlayJoinMenuView._CachedServerListEntries</c>,
/// the same object as <c>ServerListSystem._CachedResult</c>). Indexes are the list's own.
/// </summary>
internal interface IServerBrowserRows
{
    int Count { get; }

    /// <summary>
    /// Whether the row carries <see cref="ServerBrowserMarker.Tag"/>. The live list asks the game's own tag list
    /// (<c>Tags.Contains</c>; the client already lowercased every tag), so no tag is copied out per row.
    /// </summary>
    bool MarkedAt(int index);

    void RemoveAt(int index);
}

/// <summary>What one filter pass did.</summary>
internal readonly record struct ServerBrowserPass(int Before, int Removed, int Kept);

/// <summary>
/// BROWSERFILTER1: the client filter's state machine, game-free. The live half calls <see cref="Tick"/> once per frame
/// (after the game's own <c>PlayJoinMenuView.Update</c>) with the shown list's identity stamp. A pass runs only when the
/// stamp moved (the game rebuilt the list: every two seconds, on a filter, search or sort change, or a refresh) or the
/// switch was turned on; between rebuilds the game hands back the same already-trimmed list.
///
/// <para><b>Fail open.</b> A pass that throws turns the filter off for the rest of the session (<see cref="Failed"/>),
/// logs once, and asks the caller to rebuild the list from the game's full set, so a broken read never hides servers.</para>
/// </summary>
internal sealed class ServerBrowserFilterState
{
    private ServerBrowserStamp? _lastStamp;
    private bool _lastEnabled;

    /// <summary>Set once a pass threw; the filter stays off until Core reloads.</summary>
    public bool Failed { get; private set; }

    /// <summary>The first failure's message (empty when none).</summary>
    public string FailReason { get; private set; } = "";

    /// <summary>The last pass (null: none since the list was opened or the switch changed).</summary>
    public ServerBrowserPass? LastPass { get; private set; }

    /// <summary>Passes run since Core loaded.</summary>
    public int Passes { get; private set; }

    /// <summary>What the caller does after <see cref="Tick"/>.</summary>
    internal enum Next
    {
        /// <summary>Nothing: the list is unchanged, or the filter is off and nothing needs restoring.</summary>
        None,

        /// <summary>Rows were removed: redraw the list from the trimmed rows (<c>UpdateServerListView</c>).</summary>
        Redraw,

        /// <summary>The filter turned off or failed: make the game rebuild the full list now, then redraw.</summary>
        Restore,
    }

    /// <summary>
    /// One frame. <paramref name="stamp"/> identifies the shown list as it is now. After a redraw the caller passes the
    /// list's new stamp to <see cref="Settled"/>, so the redraw's own unchanged list is not filtered twice.
    /// </summary>
    internal Next Tick(bool enabled, ServerBrowserStamp stamp, IServerBrowserRows? rows, Action<string>? logOnce)
    {
        bool wasEnabled = _lastEnabled;
        _lastEnabled = enabled && !Failed;
        if (!enabled || Failed)
        {
            _lastStamp = null;
            LastPass = null;
            return wasEnabled ? Next.Restore : Next.None;
        }

        if (rows == null) return Next.None;
        if (wasEnabled && _lastStamp is { } last && last.Equals(stamp)) return Next.None;

        try
        {
            var pass = Filter(rows);
            LastPass = pass;
            Passes++;
            _lastStamp = stamp;
            return pass.Removed > 0 ? Next.Redraw : Next.None;
        }
        catch (Exception e)
        {
            Failed = true;
            FailReason = e.Message;
            _lastEnabled = false;
            _lastStamp = null;
            logOnce?.Invoke("ServerBrowser: the Progenitor servers filter could not read the server list (" + e.Message
                + "); it is off for this session and every server is shown.");
            return Next.Restore;
        }
    }

    /// <summary>
    /// Fail open from outside a pass (a redraw threw): the filter is off for the rest of the session and the caller
    /// rebuilds the full list. Returns false when it had already failed.
    /// </summary>
    public bool Fail(string reason, Action<string>? logOnce)
    {
        if (Failed) return false;
        Failed = true;
        FailReason = reason ?? "";
        _lastEnabled = false;
        _lastStamp = null;
        logOnce?.Invoke("ServerBrowser: the Progenitor servers filter failed (" + FailReason
            + "); it is off for this session and every server is shown.");
        return true;
    }

    /// <summary>After a redraw: the list the game drew from is the trimmed one; remember its stamp.</summary>
    public void Settled(ServerBrowserStamp stamp)
    {
        if (_lastEnabled) _lastStamp = stamp;
    }

    /// <summary>Forget the list (the browser closed or another view instance opened).</summary>
    public void Forget()
    {
        _lastStamp = null;
        LastPass = null;
    }

    /// <summary>Remove every row without the marker, from the end so indexes stay valid.</summary>
    public static ServerBrowserPass Filter(IServerBrowserRows rows)
    {
        int before = rows.Count;
        int removed = 0;
        for (int i = before - 1; i >= 0; i--)
        {
            if (rows.MarkedAt(i)) continue;
            rows.RemoveAt(i);
            removed++;
        }

        return new ServerBrowserPass(before, removed, before - removed);
    }
}

/// <summary>
/// The shown list's identity: the list object (the game swaps in a new one after a sort), its row count, and the
/// game's next internal refresh time (<c>ServerListSystem._NextGetInternalResultsTime</c>, set on every rebuild).
/// </summary>
internal readonly record struct ServerBrowserStamp(long List, int Count, double NextRebuild);

/// <summary>
/// BROWSERFILTER1, the server half: what the tag publisher needs from the game. The live implementation reads
/// <c>ServerBootstrapSystem._GameTags</c> and calls the game's own <c>SetServerGameTag</c> / <c>RemoveServerGameTag</c>
/// (each sets <c>_GameTagsModified</c>, so the game's one-second platform update republishes the tags to Steam and EOS).
/// </summary>
internal interface IServerTagGame
{
    /// <summary>The server's bootstrap system and its tag list exist.</summary>
    bool Ready { get; }

    /// <summary>The game's tag list now, or null when it cannot be read.</summary>
    IReadOnlyList<string>? Tags { get; }

    void Add(string tag);

    void Remove(string tag);
}

/// <summary>
/// BROWSERFILTER1: keeps <see cref="ServerBrowserMarker.Tag"/> in the server's game tags while the cfg switch is on
/// (and out of them while it is off). Game-free. Checks every second until the game's own tags are in place (see
/// <see cref="GameTagsSet"/>), then once a minute; the game's own calls ignore a duplicate, so a check that finds the
/// tag in place changes nothing. Core unloading leaves the tag in the game's list: it stays published until the server
/// restarts (or the next Core turns <c>BrowserTag</c> off).
/// </summary>
internal sealed class ServerBrowserTagPublisher
{
    private DateTime _next = DateTime.MinValue;
    private bool? _lastEnabled;
    private DateTime? _gameTagsSeen;

    /// <summary>
    /// How long to wait, after the game's startup tags exist, for its first activity-level tags (<c>dif_*</c>, added by
    /// <c>UpdateActivityLevel</c> up to a minute after start) before adding ours anyway, so ours comes after the game's own.
    /// </summary>
    public static readonly TimeSpan ActivityTagWait = TimeSpan.FromSeconds(90);

    /// <summary><c>added</c>, <c>present</c>, <c>removed</c>, <c>absent</c>, <c>waiting</c> or <c>failed: …</c>.</summary>
    public string Status { get; private set; } = "waiting";

    /// <summary>Whether Steam's 128-byte tag string still fits with the tag (false: Steam may cut it).</summary>
    public bool FitsSteam { get; private set; } = true;

    /// <summary>The tags as last read.</summary>
    public IReadOnlyList<string> LastTags { get; private set; } = Array.Empty<string>();

    /// <summary>One step. Returns a line to log when something changed, else null.</summary>
    public string? Tick(DateTime nowUtc, bool enabled, IServerTagGame game)
    {
        if (game == null) return null;
        bool switched = _lastEnabled != enabled;
        if (!switched && nowUtc < _next) return null;
        _lastEnabled = enabled;
        bool ready;
        try { ready = game.Ready; }
        catch { ready = false; }
        if (!ready)
        {
            Status = "waiting";
            _next = nowUtc.AddSeconds(1);
            return null;
        }

        IReadOnlyList<string> tags;
        try { tags = game.Tags ?? Array.Empty<string>(); }
        catch { tags = Array.Empty<string>(); }
        if (enabled && !Contains(tags) && !GameTagsSet(tags, nowUtc))
        {
            Status = "waiting";
            _next = nowUtc.AddSeconds(1);
            return null;
        }

        _next = nowUtc.AddSeconds(60);
        try
        {
            bool has = Contains(tags);
            string? line = null;
            if (enabled && !has)
            {
                game.Add(ServerBrowserMarker.Tag);
                Status = "added";
                line = "ServerList: the \"" + ServerBrowserMarker.Tag + "\" server-list tag is published (Join Game's Progenitor servers filter finds this server).";
            }
            else if (!enabled && has)
            {
                game.Remove(ServerBrowserMarker.Tag);
                Status = "removed";
                line = "ServerList: the \"" + ServerBrowserMarker.Tag + "\" server-list tag is removed (BrowserTag off).";
            }
            else Status = enabled ? "present" : "absent";

            var after = game.Tags ?? Array.Empty<string>();
            LastTags = new List<string>(after);
            bool fits = ServerBrowserMarker.FitsSteam(after);
            if (enabled && !fits && FitsSteam)
                line = (line == null ? "" : line + " ") + "ServerList: the server's tags with \"" + ServerBrowserMarker.Tag
                    + "\" pass Steam's " + ServerBrowserMarker.SteamTagLimit + "-byte limit; Steam may cut them.";
            FitsSteam = fits;
            return line;
        }
        catch (Exception e)
        {
            Status = "failed: " + e.Message;
            _next = nowUtc.AddSeconds(60);
            return "ServerList: the server-list tag could not be " + (enabled ? "published" : "removed") + ": " + e.Message;
        }
    }

    /// <summary>
    /// The game's own tags are in place: its startup tags (<c>pvp</c> or <c>pve</c>, from <c>FinalizeSetupServer</c>) and
    /// either a <c>dif_*</c> tag or <see cref="ActivityTagWait"/> since the startup tags appeared.
    /// </summary>
    private bool GameTagsSet(IReadOnlyList<string> tags, DateTime nowUtc)
    {
        bool mode = false;
        bool difficulty = false;
        foreach (string t in tags)
        {
            if (t == "pvp" || t == "pve") mode = true;
            else if (t != null && t.StartsWith("dif_", StringComparison.Ordinal)) difficulty = true;
        }

        if (!mode) return false;
        _gameTagsSeen ??= nowUtc;
        return difficulty || nowUtc - _gameTagsSeen.Value >= ActivityTagWait;
    }

    private static bool Contains(IReadOnlyList<string> tags)
    {
        foreach (string t in tags)
        {
            if (string.Equals(t, ServerBrowserMarker.Tag, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
