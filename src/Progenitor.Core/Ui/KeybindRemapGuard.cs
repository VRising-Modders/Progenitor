using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>Why a remap was refused (<see cref="KeybindRemapGuard.Check"/>).</summary>
internal sealed class RemapRefusal
{
    internal RemapRefusal(string reason, KeybindClaim? holder, ReservedBy reserved)
    {
        Reason = reason ?? "";
        Holder = holder;
        Reserved = reserved;
    }

    /// <summary>The row-sized sentence: <c>F8 is used by Progenitor Sample (Net ping). Pick another key.</c></summary>
    internal string Reason { get; }

    /// <summary>What already has the chord; null for a reserved chord.</summary>
    internal KeybindClaim? Holder { get; }

    /// <summary>Windows or Steam when the chord is reserved; <see cref="ReservedBy.None"/> for a clash.</summary>
    internal ReservedBy Reserved { get; }
}

/// <summary>
/// KEYS2 (requested 2026-09-29, "make sure there are no conflicts"): a remap never creates a
/// conflict. A chord is refused when it is reserved (<see cref="ReservedChords"/>) or when something
/// else already has it:
/// <list type="bullet">
/// <item><description>a game action on the game's Controls page, mode-only ones included (the player chose the key, so a
/// clash that would only warn is still a clash they can avoid);</description></item>
/// <item><description>any other Progenitor bind, the same mod's included;</description></item>
/// <item><description>any ordinary mod's key setting that reads as a keyboard chord.</description></item>
/// </list>
/// Choosing the key a bind already has is allowed (a no-op). The game's keys are checked only while
/// they can be read (in a world); at the title screen only mod keys are compared. Game-free: the
/// sources are <see cref="KeybindArbiter"/>'s.
///
/// <para>
/// Conflicts that already exist — two mods' defaults, an install order — are not refused here; the
/// winner rule (<see cref="KeybindArbiter"/>) still decides them and the loser is shown.
/// </para>
/// </summary>
internal static class KeybindRemapGuard
{
    /// <summary>
    /// Why moving <paramref name="self"/> (source, owner, action) from <paramref name="current"/> to
    /// <paramref name="chord"/> is refused, or null when it may move. <paramref name="reset"/> words the
    /// refusal for a reset to the default. <paramref name="alsoIgnore"/> is a claim key left out of the
    /// comparison (a swap's partner).
    /// </summary>
    internal static RemapRefusal? Check(
        KeybindSource source, string ownerId, string actionId, KeyChord current, KeyChord chord,
        bool reset = false, string? alsoIgnore = null, KeybindScope scope = KeybindScope.Gameplay, bool ignoreGame = false)
    {
        if (!chord.IsBound) return null;
        if (chord == current) return null;
        if (ReservedChords.Find(chord, out var hit))
            return new RemapRefusal(reset ? ReservedChords.ResetReason(chord)! : ReservedChords.Reason(chord)!, null, hit.By);
        // KEYS8: a bare left or right click, or a click in a scope where it presses something of the game's or ours.
        if (MouseChords.Refusal(chord, scope) is { } click) return new RemapRefusal(click, null, ReservedBy.None);

        // Fail closed (KEYS2 review): a check that cannot run refuses the key; it never allows a possible clash.
        KeybindClaim? holder;
        string age;
        try { holder = HolderOf(source, ownerId, actionId, chord, alsoIgnore, out age, scope, ignoreGame); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: keybind remap check threw: " + e.Message + "; the key is refused.");
            return new RemapRefusal(CheckFailedReason, null, ReservedBy.None);
        }

        if (holder == null) return null;
        string reason = reset ? ResetClashReason(chord, holder, age) : ClashReason(chord, holder, age);
        return new RemapRefusal(reason, holder, ReservedBy.None);
    }

    /// <summary>
    /// The Keybinds tab's remap (requested 2026-10-10: "if I bind a key that is already in use it should get rid of
    /// the old binding that had that key and notify me"). Instead of refusing a key something else has, the tab takes
    /// it: every claim on <paramref name="chord"/> that meets <paramref name="moving"/> and can be written now is added
    /// to <paramref name="take"/>, and Save unbinds those before it stores the key. Still refused (the return value,
    /// with <paramref name="take"/> left empty): a reserved chord, a bare click, and a holder that cannot be changed here
    /// (a read-only mod key, or a game key while the game's keys cannot be written). For a game key moving, another
    /// game action this tab cannot change (one not on the game's Controls page) is not taken and not refused: the game
    /// ships such overlaps; it comes back in <paramref name="alsoGame"/> for the warning.
    /// </summary>
    internal static RemapRefusal? TakeFrom(KeybindClaim moving, KeyChord chord, List<KeybindClaim> take, out KeybindClaim? alsoGame)
    {
        take.Clear();
        alsoGame = null;
        if (moving == null || !chord.IsBound) return null;
        if (moving.Comparable && moving.Chord == chord) return null;
        if (ReservedChords.Find(chord, out var hit)) return new RemapRefusal(ReservedChords.Reason(chord)!, null, hit.By);
        if (MouseChords.Refusal(chord, moving.Scope) is { } click) return new RemapRefusal(click, null, ReservedBy.None);
        bool game = moving.Source == KeybindSource.Game;
        try
        {
            var picture = PictureSource();
            if (!picture.GameAvailable && !game && KeybindGameCache.Claims() is { } cached)
            {
                // Outside a world a game key can't be written, so a remembered one is still refused.
                foreach (var c in cached.Claims)
                {
                    if (!c.Comparable || c.Chord != chord || !KeybindArbiter.ScopesMeet(c, moving.Scope)) continue;
                    if (SharedLoneModifier(moving.Source, c)) continue;
                    take.Clear();
                    return new RemapRefusal(ClashReason(chord, c, KeybindGameCache.AgeText(cached.SavedUtc)), c, ReservedBy.None);
                }
            }

            foreach (var v in picture.Verdicts)
            {
                var c = v.Claim;
                if (!c.Comparable || c.Chord != chord) continue;
                if (string.Equals(c.Key, moving.Key, StringComparison.OrdinalIgnoreCase)) continue;
                if (!KeybindArbiter.ScopesMeet(c, moving)) continue;
                // HOLDMOD1: a lone modifier is shared with the game and ordinary mods; neither side is taken from.
                if (SharedLoneModifier(moving.Source, c)) continue;
                if (!CanTake(c) || ShippedOverlap(moving, c))
                {
                    if (game && c.Source == KeybindSource.Game)
                    {
                        alsoGame ??= c;
                        continue;
                    }

                    take.Clear();
                    alsoGame = null;
                    return new RemapRefusal(ClashReason(chord, c), c, ReservedBy.None);
                }

                if (!take.Exists(t => string.Equals(t.Key, c.Key, StringComparison.OrdinalIgnoreCase))) take.Add(c);
            }

            return null;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: keybind remap check threw: " + e.Message + "; the key is refused.");
            take.Clear();
            alsoGame = null;
            return new RemapRefusal(CheckFailedReason, null, ReservedBy.None);
        }
    }

    /// <summary>
    /// A game key going back to its own default where another game action still has that same default (Space is Travel
    /// Skill, Drop Item and Respawn): the game ships that overlap, so it only warns and nothing is taken.
    /// </summary>
    internal static bool ShippedOverlap(KeybindClaim moving, KeybindClaim holder) =>
        moving.Source == KeybindSource.Game && holder.Source == KeybindSource.Game && !holder.GameChanged
        && moving.DefaultText.Length > 0 && !string.Equals(moving.DefaultText, "None", StringComparison.OrdinalIgnoreCase)
        && string.Equals(moving.DefaultText, holder.DefaultText, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the Keybinds tab can unbind <paramref name="holder"/> now (see <see cref="TakeFrom"/>).</summary>
    internal static bool CanTake(KeybindClaim holder) =>
        holder.Source == KeybindSource.Game
            ? GameKeyRemap.Writer != null && GameKeyRemap.IsEditable(holder) && GameKeyRemap.CanWriteNow()
            : holder.Editable;

    /// <summary>The picture the check reads; a seam so a test can make it fail.</summary>
    internal static Func<KeybindPicture> PictureSource { get; set; } = KeybindArbiter.Current;

    /// <summary>The refusal when the check itself could not run.</summary>
    internal const string CheckFailedReason = "Couldn't check this key; try again.";

    /// <summary>
    /// Said when a key is free among mod keys but the game's keys could not be compared: no world is loaded
    /// and no earlier read is remembered (<see cref="KeybindGameCache"/>).
    /// </summary>
    internal const string GameUncheckedNote =
        "Game keys can't be checked outside a world; this key may clash with a game action.";

    /// <summary>
    /// <see cref="GameUncheckedNote"/> when the game's keys cannot be compared right now (no world, nothing
    /// remembered), else empty.
    /// </summary>
    internal static string GameCheckNote()
    {
        try
        {
            if (KeybindArbiter.GameSnapshot().Available) return "";
            return KeybindGameCache.Claims() == null ? GameUncheckedNote : "";
        }
        catch { return GameUncheckedNote; }
    }

    /// <summary>The claim key of a moving claim (<see cref="KeybindClaim.Key"/>'s form).</summary>
    internal static string KeyOf(KeybindSource source, string ownerId, string actionId) =>
        KeybindClaim.NameOf(source) + ":" + (ownerId ?? "").Trim() + "/" + (actionId ?? "").Trim();

    /// <summary>
    /// The first claim on <paramref name="chord"/> other than the moving one: the game, then Progenitor, then
    /// cfg keys. Outside a world the game's keys come from the last in-world read (<see cref="KeybindGameCache"/>)
    /// and <paramref name="age"/> says how old it is; otherwise it is empty. Throws when the picture cannot be
    /// built (the caller refuses).
    /// </summary>
    internal static KeybindClaim? HolderOf(
        KeybindSource source, string ownerId, string actionId, KeyChord chord, string? alsoIgnore, out string age,
        KeybindScope scope = KeybindScope.Gameplay, bool ignoreGame = false)
    {
        age = "";
        string self = KeyOf(source, ownerId, actionId);
        bool Skip(KeybindClaim c) =>
            string.Equals(c.Key, self, StringComparison.OrdinalIgnoreCase)
            || (alsoIgnore != null && string.Equals(c.Key, alsoIgnore, StringComparison.OrdinalIgnoreCase));

        // The winner picture: built only when something changed (KEYS1), and it lists the game's keys (when
        // readable), every Progenitor bind and every cfg key, in that order, so the game is named first.
        var picture = PictureSource();
        if (!picture.GameAvailable && !ignoreGame)
        {
            var cached = KeybindGameCache.Claims();
            if (cached != null)
            {
                foreach (var c in cached.Value.Claims)
                {
                    if (!c.Comparable || c.Chord != chord) continue;
                    if (!KeybindArbiter.ScopesMeet(c, scope)) continue;
                    if (SharedLoneModifier(source, c)) continue;
                    age = KeybindGameCache.AgeText(cached.Value.SavedUtc);
                    return c;
                }
            }
        }

        foreach (var v in picture.Verdicts)
        {
            var c = v.Claim;
            if (!c.Comparable || c.Chord != chord) continue;
            // KEYS4: a menu key and a gameplay key never meet (the game's menu keys meet both).
            if (!KeybindArbiter.ScopesMeet(c, scope)) continue;
            if (c.Source != KeybindSource.Game && Skip(c)) continue;
            // HOLDMOD1: a lone modifier the game or an ordinary mod also reads is shared, not taken.
            if (SharedLoneModifier(source, c)) continue;
            // KEYS9: a game key moving among the game's own keys; the caller words those as a warning.
            if (ignoreGame && c.Source == KeybindSource.Game) continue;
            return c;
        }

        return null;
    }

    /// <summary>
    /// HOLDMOD1: true when a claim of <paramref name="moving"/> and <paramref name="other"/> meet on a lone modifier
    /// unless both are Progenitor binds: the game and ordinary mods share it (<see cref="KeybindArbiter.SharesLoneModifier(KeybindClaim, KeybindClaim)"/>).
    /// </summary>
    internal static bool SharedLoneModifier(KeybindSource moving, KeybindClaim other) =>
        other.Comparable && other.Chord.IsLoneModifier
        && !(moving == KeybindSource.Progenitor && other.Source == KeybindSource.Progenitor);

    /// <summary>Who holds a key, as the refusal names it: <c>the game (Interact)</c>, <c>Progenitor Sample (Net ping)</c>.</summary>
    internal static string HolderName(KeybindClaim holder) =>
        holder.Source == KeybindSource.Game
            ? KeybindArbiter.GameName + " (" + holder.DisplayName + (holder.Mode.Length > 0 ? ", " + holder.Mode : "") + ")"
            : KeybindArbiter.DisplayNameOf(holder.OwnerId) + " (" + holder.DisplayName + ")";

    /// <summary>
    /// <c>F8 is used by Progenitor Sample (Net ping). Pick another key.</c> With <paramref name="age"/> (a game
    /// key remembered from the last world): <c>E is used by the game (Interact), as last read in a world 2 days ago. …</c>
    /// </summary>
    internal static string ClashReason(KeyChord chord, KeybindClaim holder, string age = "") =>
        KeybindArbiter.ChordPhrase(chord.DisplayText, holder) + " is used by " + HolderName(holder) + Aged(age) + ". " + KeybindArbiter.PickAnother;

    /// <summary><c>Its default, F8, is now used by X. Unbind it or pick another key.</c></summary>
    internal static string ResetClashReason(KeyChord chord, KeybindClaim holder, string age = "") =>
        "Its default, " + KeybindArbiter.ChordPhrase(chord.DisplayText, holder) + ", is now used by " + HolderName(holder) + Aged(age) + ". Unbind it or pick another key.";

    private static string Aged(string age) => string.IsNullOrEmpty(age) ? "" : ", as last read in a world " + age;

    /// <summary>The swap offer's sentence: <c>Swap keys: X gets F8 and Y gets F9.</c></summary>
    internal static string SwapSentence(string aName, KeyChord aGets, string bName, KeyChord bGets) =>
        aName + " gets " + Text(aGets) + " and " + bName + " gets " + Text(bGets) + ".";

    private static string Text(KeyChord chord) => chord.IsBound ? chord.DisplayText : "no key";
}

/// <summary>
/// KEYS2 review: the game's keys as last read in a world, so a remap at the title screen is still checked
/// against them. Kept in memory and in <c>progenitor-game-keys.txt</c> next to the other framework files
/// (one line per key: action, name, chord, mode, slot, category, tab-separated, after a <c>saved</c> line
/// with the UTC time; KEYS3 appends the game's key text, the action's description and a comparable flag,
/// and also writes the mouse and unbound slots so the Keybinds tab can show the whole Bindings Menu at
/// the title screen), so it survives a restart. Refusals made from it say how old it is. Game-free; tests
/// leave <see cref="Directory"/> null (memory only).
/// </summary>
internal static class KeybindGameCache
{
    internal const string FileName = "progenitor-game-keys.txt";

    private static readonly object Gate = new();
    private static IReadOnlyList<KeybindClaim>? _claims;
    private static IReadOnlyList<KeybindClaim>? _display;
    private static DateTime _savedUtc;
    private static bool _loaded;

    /// <summary>Where the file lives; null keeps the cache in memory only.</summary>
    internal static Func<string?> Directory { get; set; } = DefaultDirectory;

    /// <summary>The clock for the age text; a seam for tests.</summary>
    internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>The zone the note's dates are in (default: the machine's); a seam for tests.</summary>
    internal static Func<TimeZoneInfo> Zone { get; set; } = () => TimeZoneInfo.Local;

    private static string? DefaultDirectory()
    {
#if PROGENITOR_TESTS
        return null;
#else
        try { return Progenitor.Networking.ModManifest.ConfigDirectory; }
        catch { return null; }
#endif
    }

    /// <summary>Remember an in-world read. Only a readable snapshot with at least one keyboard key replaces the old one.</summary>
    internal static void Remember(GameKeybindSnapshot game)
    {
        if (game == null || !game.Available) return;
        var keys = new List<KeybindClaim>();
        foreach (var c in game.Claims) if (c.Comparable) keys.Add(c);
        if (keys.Count == 0) return;
        var now = UtcNow();
        var display = new List<KeybindClaim>(game.Claims);
        lock (Gate)
        {
            _claims = keys;
            _display = display;
            _savedUtc = now;
            _loaded = true;
        }

        string? dir = SafeDirectory();
        if (dir == null) return;
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("saved\t").Append(now.ToString("o", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            foreach (var c in display)
                sb.Append(Clean(c.ActionId)).Append('\t').Append(Clean(c.DisplayName)).Append('\t').Append(c.Chord)
                    .Append('\t').Append(Clean(c.Mode)).Append('\t').Append(c.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append('\t').Append(Clean(c.Category))
                    .Append('\t').Append(Clean(c.ChordText)).Append('\t').Append(Clean(c.Help))
                    .Append('\t').Append(c.Comparable ? '1' : '0').Append('\n');
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, FileName), sb.ToString());
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: game keys cache write failed: " + e.Message); }
    }

    /// <summary>The remembered keys and when they were read, or null when nothing is remembered.</summary>
    internal static (IReadOnlyList<KeybindClaim> Claims, DateTime SavedUtc)? Claims()
    {
        lock (Gate)
        {
            if (!_loaded)
            {
                _loaded = true;
                Load();
            }

            return _claims == null ? null : (_claims, _savedUtc);
        }
    }

    /// <summary>
    /// KEYS3: every remembered game slot for display (mouse and unbound slots included, with the game's
    /// key text and the action's description when the file has them), and when they were read; null
    /// when nothing is remembered. A file written before KEYS3 gives its keyboard keys only.
    /// </summary>
    internal static (IReadOnlyList<KeybindClaim> Claims, DateTime SavedUtc)? DisplayClaims()
    {
        lock (Gate)
        {
            if (!_loaded)
            {
                _loaded = true;
                Load();
            }

            var list = _display ?? _claims;
            return list == null ? null : (list, _savedUtc);
        }
    }

    private static void Load()
    {
        string? dir = SafeDirectory();
        if (dir == null) return;
        try
        {
            string path = System.IO.Path.Combine(dir, FileName);
            if (!System.IO.File.Exists(path)) return;
            var list = new List<KeybindClaim>();
            var display = new List<KeybindClaim>();
            DateTime saved = DateTime.MinValue;
            foreach (var line in System.IO.File.ReadAllLines(path))
            {
                var f = line.Split('\t');
                if (f.Length >= 2 && f[0] == "saved")
                {
                    DateTime.TryParse(f[1], System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out saved);
                    continue;
                }

                if (f.Length < 6) continue;
                bool parsed = KeyChord.TryParse(f[2], out var chord);
                int.TryParse(f[4], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int slot);
                bool extended = f.Length >= 9;
                bool comparable = parsed && chord.IsBound && (!extended || f[8] == "1");
                if (!comparable && !extended) continue;
                var claim = new KeybindClaim(KeybindSource.Game, KeybindArbiter.GameOwnerId, f[0], f[1],
                    comparable ? chord : KeyChord.None, chordText: extended ? f[6] : "", help: extended ? f[7] : "",
                    comparable: comparable, slot: slot, category: f[5], mode: f[3]);
                display.Add(claim);
                if (comparable) list.Add(claim);
            }

            if (display.Count > 0 && saved != DateTime.MinValue)
            {
                _claims = list;
                _display = display;
                _savedUtc = saved.ToUniversalTime();
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: game keys cache read failed: " + e.Message); }
    }

    /// <summary>
    /// KEYS3: the Keybinds tab's note over game keys shown from this cache at the title screen:
    /// <c>As last read in a world today.</c> or <c>As last read in a world on September 28, 2026.</c>
    /// (local dates).
    /// </summary>
    internal static string ReadNote(DateTime savedUtc)
    {
        var now = UtcNow();
        var saved = ToLocal(savedUtc);
        bool today = saved.Date == ToLocal(now).Date;
        return today ? "As last read in a world today."
            : "As last read in a world on " + saved.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture) + ".";
    }

    /// <summary>A UTC time in <see cref="Zone"/> (the machine's zone: exactly <see cref="DateTime.ToLocalTime"/>).</summary>
    private static DateTime ToLocal(DateTime utc)
    {
        TimeZoneInfo zone;
        try { zone = Zone() ?? TimeZoneInfo.Local; }
        catch { zone = TimeZoneInfo.Local; }
        if (ReferenceEquals(zone, TimeZoneInfo.Local)) return utc.ToLocalTime();
        if (utc.Kind == DateTimeKind.Local) utc = utc.ToUniversalTime();
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
    }

    /// <summary><c>today</c>, <c>1 day ago</c>, <c>5 days ago</c>.</summary>
    internal static string AgeText(DateTime savedUtc)
    {
        var span = UtcNow() - savedUtc;
        int days = (int)Math.Floor(Math.Max(0, span.TotalDays));
        return days == 0 ? "today" : days == 1 ? "1 day ago" : days.ToString(System.Globalization.CultureInfo.InvariantCulture) + " days ago";
    }

    private static string Clean(string? text) => (text ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private static string? SafeDirectory()
    {
        try { return Directory(); }
        catch { return null; }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _claims = null;
            _display = null;
            _savedUtc = default;
            _loaded = false;
        }

        Directory = DefaultDirectory;
        UtcNow = () => DateTime.UtcNow;
        Zone = () => TimeZoneInfo.Local;
    }
}
