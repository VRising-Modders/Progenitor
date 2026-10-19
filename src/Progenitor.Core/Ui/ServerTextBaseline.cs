using System;

namespace Progenitor.Ui;

/// <summary>
/// APPLYONCE1: the server value a server-wide field-and-Apply row is based on, which every Apply sends as the expected
/// (conflict) value. Game-free.
///
/// <para><b>Why.</b> Live 2026-10-19 (release 2026.10.133, the server message on the Server tab): the page kept the value
/// it drew, or the one its own last Apply stored, for as long as it stayed open. A change from outside the page (a bridge
/// setting request, a cfg edit on the server, another admin) reached the client through ConfigSync and was never noted,
/// so the next Apply was sent against the old value, the server refused it as a conflict (<c>This setting changed on
/// the server.</c>), and only the second click, now checked against the refreshed value, saved.</para>
///
/// <para><b>The rule.</b> When a synced value arrives, the baseline follows it and the field shows it, unless a request
/// from this row is on its way (its answer decides) or the field holds unsent text that differs from the baseline (a
/// draft is never overwritten; its Apply is still checked against the value it was typed over, so a change the admin
/// has not seen is refused rather than silently replaced). Every answer sets the baseline: the stored value on success,
/// the value the server holds on a refusal.</para>
/// </summary>
internal sealed class ServerTextBaseline
{
    public ServerTextBaseline(string? shown) => Shown = shown ?? "";

    /// <summary>The server value the field's text is based on: the expected value the next Apply sends.</summary>
    public string Shown { get; private set; }

    /// <summary>
    /// A synced value arrived (<paramref name="server"/>) while the field reads <paramref name="field"/>. Returns the
    /// text the field should show now, or null to leave the field as it is.
    /// </summary>
    public string? OnServerValue(string? server, string? field, bool pending)
    {
        string now = server ?? "";
        if (pending || string.Equals(now, Shown, StringComparison.Ordinal)) return null;
        // An unsent draft keeps its text and the baseline it was typed over.
        if (!string.Equals(field ?? "", Shown, StringComparison.Ordinal)) return null;
        Shown = now;
        return now;
    }

    /// <summary>A request from this row was answered: the value the server holds now becomes the baseline.</summary>
    public void Answered(string? serverNow) => Shown = serverNow ?? "";
}
