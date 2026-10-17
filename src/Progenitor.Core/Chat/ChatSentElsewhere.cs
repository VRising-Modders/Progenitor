using System.Collections.Generic;
using System.Text;
using Progenitor.Ui;

namespace Progenitor.Chat;

/// <summary>
/// CHAT14: what a line you send shows when the tab you sent it from does not show its channel ("sent elsewhere").
/// Game-free; the chat view draws the note and the tab row pulses the tab.
///
/// <list type="bullet">
/// <item>Lines keep their filtering: a whisper sent from a Clan tab shows only in tabs that show whispers. It is never
/// copied into the Clan tab's log.</item>
/// <item>The tab that shows it pulses briefly with the row's own hover look (<see cref="PulseLit"/>), and a short muted
/// note shows at the bottom of the current tab (<see cref="Hint"/>: <c>Whisper to Bob sent · see Whispers</c>,
/// <c>Sent to Global · see All</c>). It fades after <see cref="NoteSeconds"/>, is never added to the log (so never
/// saved), and a click on it shows that tab.</item>
/// <item>The tab named is the most specific one that shows the line (fewest channels; the first in the row on a tie),
/// so a Whispers tab is named before All.</item>
/// <item>When no tab shows the line, the note is the line itself (an echo, <see cref="Echo"/>), so a message never seems
/// to vanish.</item>
/// </list>
/// </summary>
internal sealed class ChatSentElsewhere
{
    /// <summary>How long the note shows, its last <see cref="FadeSeconds"/> fading out.</summary>
    internal const double NoteSeconds = 5.0;

    internal const double FadeSeconds = 1.0;

    /// <summary>How long the target tab pulses (two short blinks of the hover look).</summary>
    internal const double PulseSeconds = 1.0;

    /// <summary>The note shown now, or null.</summary>
    internal ChatSentNote? Current { get; private set; }

    /// <summary>Raised when the note appears or goes, so the view knows to redraw it.</summary>
    internal int Version { get; private set; }

    private ChatTab? _pulseTab;
    private double _pulseFrom;

    /// <summary>
    /// The most specific tab, other than <paramref name="active"/>, that shows <paramref name="r"/>: the one with the
    /// fewest channels, the first in the row on a tie. -1 when none does.
    /// </summary>
    internal static int TargetTab(IReadOnlyList<ChatTab> tabs, int active, ChatRecord r)
    {
        int best = -1, bestBits = int.MaxValue;
        for (int i = 0; i < tabs.Count; i++)
        {
            if (i == active || !tabs[i].Shows(r)) continue;
            int bits = Bits((int)tabs[i].Scope);
            if (bits < bestBits)
            {
                best = i;
                bestBits = bits;
            }
        }

        return best;
    }

    private static int Bits(int v)
    {
        int n = 0;
        for (; v != 0; v &= v - 1) n++;
        return n;
    }

    /// <summary>
    /// The note's words: <c>Whisper to Bob sent · see Whispers</c>, <c>Sent to Global · see All</c>, or with no tab
    /// (<paramref name="tabName"/> empty) <c>· no tab shows Global</c> after the echoed line.
    /// </summary>
    internal static string Hint(ChatRecord r, string? whisperTarget, string? tabName)
    {
        string what = Sent(r, whisperTarget);
        if (string.IsNullOrEmpty(tabName)) return "no tab shows " + Channel(r.Kind);
        return what + " · see " + tabName;
    }

    private static string Sent(ChatRecord r, string? whisperTarget)
    {
        if (r.Kind == ChatKind.WhisperTo || r.Kind == ChatKind.WhisperFrom)
        {
            string name = (whisperTarget ?? "").Trim();
            return name.Length > 0 ? "Whisper to " + name + " sent" : "Whisper sent";
        }

        string channel = ChatFormat.ChannelLabel(r.Kind);
        return channel.Length > 0 ? "Sent to " + channel : "Sent";
    }

    /// <summary>The channel's name in a note: <c>whispers</c>, <c>Global</c>, <c>Clan</c>, or <c>this channel</c>.</summary>
    private static string Channel(ChatKind kind)
    {
        if (kind == ChatKind.WhisperTo || kind == ChatKind.WhisperFrom) return "whispers";
        string label = ChatFormat.ChannelLabel(kind);
        return label.Length > 0 ? label : "this channel";
    }

    /// <summary>
    /// A line this player sent arrived in the log while <paramref name="active"/> was shown. When that tab does not show
    /// it, a note starts (and the target tab pulses); returns the note, or null when nothing needs saying.
    /// </summary>
    internal ChatSentNote? NoteSent(ChatRecord r, IReadOnlyList<ChatTab> tabs, int active, string? whisperTarget, double now)
    {
        if (r == null || !r.FromSelf || r.Restored || r.Kind == ChatKind.Session) return null;
        if (active < 0 || active >= tabs.Count || tabs[active].Shows(r)) return null;
        int target = TargetTab(tabs, active, r);
        var tab = target >= 0 ? tabs[target] : null;
        var note = new ChatSentNote(r, tabs[active], tab, Hint(r, whisperTarget, tab?.Name), now);
        Current = note;
        Version++;
        _pulseTab = tab;
        _pulseFrom = now;
        return note;
    }

    /// <summary>The note to draw now in <paramref name="shown"/>, or null (none, it timed out, or it belongs to another tab).</summary>
    internal ChatSentNote? Visible(ChatTab? shown, double now)
    {
        var n = Current;
        if (n == null) return null;
        if (now - n.Since >= NoteSeconds)
        {
            Clear();
            return null;
        }

        return ReferenceEquals(n.From, shown) ? n : null;
    }

    /// <summary>How opaque the note is now: 1, then down to 0 over the last <see cref="FadeSeconds"/>.</summary>
    internal static float Alpha(ChatSentNote note, double now)
    {
        double left = NoteSeconds - (now - note.Since);
        if (left <= 0) return 0f;
        return left >= FadeSeconds ? 1f : (float)(left / FadeSeconds);
    }

    /// <summary>Whether <paramref name="tab"/> wears the hover look now: two short blinks after a note named it.</summary>
    internal bool PulseLit(ChatTab? tab, double now)
    {
        if (tab == null || !ReferenceEquals(tab, _pulseTab)) return false;
        double t = now - _pulseFrom;
        return (t >= 0 && t < 0.35) || (t >= 0.6 && t < PulseSeconds);
    }

    /// <summary>Forget the note and the pulse (it was clicked, the world changed, the view detached).</summary>
    internal void Clear()
    {
        if (Current == null && _pulseTab == null) return;
        Current = null;
        _pulseTab = null;
        Version++;
    }

    /// <summary>
    /// The note's TMP text: muted words, or for an echo the line itself (<see cref="ChatFormat.Render"/>) followed by the
    /// muted words. Player text stays literal.
    /// </summary>
    internal static string Rich(ChatSentNote note, ChatFormat.Options options)
    {
        var sb = new StringBuilder(96);
        if (note.Echo)
        {
            sb.Append(ChatFormat.Render(note.Line, options, out _));
            sb.Append(' ');
        }

        sb.Append("<color=").Append(ChatFormat.MutedHex).Append("><i>").Append(UiRichText.Literal(note.Text)).Append("</i></color>");
        return sb.ToString();
    }
}

/// <summary>CHAT14: one "sent elsewhere" note.</summary>
internal sealed class ChatSentNote
{
    internal ChatSentNote(ChatRecord line, ChatTab from, ChatTab? target, string text, double since)
    {
        Line = line;
        From = from;
        Target = target;
        Text = text;
        Since = since;
    }

    /// <summary>The line sent (it is in the log already; the note never adds it).</summary>
    internal ChatRecord Line { get; }

    /// <summary>The tab shown when it was sent: the note shows only there.</summary>
    internal ChatTab From { get; }

    /// <summary>The tab that shows the line (a click on the note shows it), or null when none does.</summary>
    internal ChatTab? Target { get; }

    /// <summary>No tab shows the line: the note repeats it.</summary>
    internal bool Echo => Target == null;

    /// <summary>The plain words (<see cref="ChatSentElsewhere.Hint"/>).</summary>
    internal string Text { get; }

    internal double Since { get; }
}
