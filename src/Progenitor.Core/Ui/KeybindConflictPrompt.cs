using System;
using System.Collections.Generic;
using System.Linq;

namespace Progenitor.Ui;

/// <summary>KEYS4: one clashing key in a conflict dialog: the bind that lost and what holds its chord.</summary>
internal sealed class KeybindConflictClash
{
    internal KeybindConflictClash(KeybindClaim loser, KeybindClaim holder, IReadOnlyList<KeybindClaim> holders)
    {
        Loser = loser;
        Holder = holder;
        Holders = holders ?? Array.Empty<KeybindClaim>();
    }

    /// <summary>The newcomer's (or the displaced mod's) Progenitor bind that is not active.</summary>
    internal KeybindClaim Loser { get; }

    /// <summary>The claim that wins the chord (the game, the framework, or a mod installed first).</summary>
    internal KeybindClaim Holder { get; }

    /// <summary>Every other claim that stops the loser on this chord (the winner first); what Give unbinds.</summary>
    internal IReadOnlyList<KeybindClaim> Holders { get; }

    internal KeyChord Chord => Loser.Chord;

    /// <summary><c>owner/action/chord</c>: what a decision is recorded against.</summary>
    internal string Signature => KeybindConflictPrompt.SignatureOf(Loser.OwnerId, Loser.ActionId, Loser.Chord);

    /// <summary>
    /// Give is offered only when every holder is another mod's key this framework may unbind: a
    /// Progenitor bind of a mod, or an ordinary mod's key setting the Keybinds tab may write. Never the
    /// game, never the Progenitor framework.
    /// </summary>
    internal bool Givable
    {
        get
        {
            if (Holders.Count == 0) return false;
            foreach (var h in Holders)
            {
                if (h.Source == KeybindSource.Game || KeybindArbiter.IsFramework(h.OwnerId)) return false;
                if (h.Source == KeybindSource.Cfg && !h.Editable) return false;
            }

            return true;
        }
    }

    /// <summary><c>Companion ping wants F8, used by Progenitor Sample (Net ping).</c></summary>
    internal string Sentence =>
        Loser.DisplayName + " wants " + KeybindArbiter.ChordPhrase(Loser.ChordText, Holder) + ", used by " + KeybindRemapGuard.HolderName(Holder) + ".";

    /// <summary><c>Progenitor Sample's Net ping</c>, for the Give line (every holder).</summary>
    internal string HoldersPhrase
    {
        get
        {
            var parts = new List<string>(Holders.Count);
            foreach (var h in Holders)
            {
                string p = KeybindArbiter.DisplayNameOf(h.OwnerId) + "'s " + h.DisplayName;
                if (!parts.Contains(p)) parts.Add(p);
            }

            return KeybindConflictSource.JoinNames(parts);
        }
    }
}

/// <summary>KEYS4: one dialog's worth: the mod whose keys lost, and each clash.</summary>
internal sealed class KeybindConflictCase
{
    internal KeybindConflictCase(string pluginId, IReadOnlyList<KeybindConflictClash> clashes, string triggerId = "")
    {
        PluginId = pluginId ?? "";
        Clashes = clashes ?? Array.Empty<KeybindConflictClash>();
        TriggerId = string.IsNullOrWhiteSpace(triggerId) ? PluginId : triggerId.Trim();
    }

    internal string PluginId { get; }

    /// <summary>
    /// KEYS10: the mod whose loading raised the dialog: <see cref="PluginId"/> itself (it wants keys that are taken), or
    /// the mod that just loaded and comes before it for these keys (the first-installed rule).
    /// </summary>
    internal string TriggerId { get; }

    internal IReadOnlyList<KeybindConflictClash> Clashes { get; }

    internal string ModName => KeybindArbiter.DisplayNameOf(PluginId);
}

/// <summary>
/// KEYS4 (requested 2026-09-30, docs/mods-menu.md#key-conflict-dialog): a popup when a mod that loads
/// (at start, on a hot load or a reload) wants a key something else already has. The winner rule
/// (<see cref="KeybindArbiter"/>) still decides at once, so the loser is inactive from its first frame;
/// this asks the player what to do about it instead of leaving it to be found in the Keybinds tab.
///
/// <para><b>When.</b> <see cref="Keybinds.Register"/> notes the registering plugin
/// (<see cref="NoteRegistered"/>). On the next <see cref="Keybinds.Pump"/> (<see cref="Tick"/>), after
/// every bind that plugin registered in the same call is in, the picture is read: every Progenitor bind
/// that is not active and whose loss involves a noted plugin (it lost, or a noted plugin took its chord)
/// becomes one clash, grouped into one dialog per losing mod. A clash already decided "later" in this Core
/// generation is skipped. Nothing is shown while a key capture or a rebind waits, while any dialog is on
/// screen or queued, while the player types, or before a screen can take it (<see cref="ScreenReady"/>: the
/// Mods workspace open, the title screen's Mods entry up, or the HUD up in a world). Just before showing,
/// each clash is checked again; one that has gone is dropped.</para>
///
/// <para><b>The dialog</b> is the framework's own (<see cref="UiDialog"/>, a Choose with option rows; words in
/// <see cref="ContentFor"/>): <b>Pick another key for &lt;action&gt;</b> / <b>Pick other keys for &lt;mod&gt;'s
/// actions</b> (opens the Keybinds tab with the first clashing row in capture), <b>Give &lt;key&gt; to &lt;mod&gt;</b>
/// (only when every holder is another mod: they are unbound, so there is still no conflict), <b>Leave &lt;action&gt;
/// unbound</b> / <b>Leave &lt;mod&gt;'s N actions unbound</b> (the clashing binds are unbound; the holders keep
/// theirs), and <b>Later</b>, which is also Escape: nothing is written,
/// the binds stay inactive, and the dialog is not shown again in this Core generation (a hot reload of the
/// mod included); the next start asks again. Give and Leave write through <see cref="Keybinds.TryRebind"/>
/// (or the cfg key's own writer), i.e. the mod's <c>[Keybinds]</c> cfg value, so the decision is kept across
/// restarts and, the conflict being gone, the dialog does not come back.</para>
///
/// <para>Game-free; the Unity side is only the dialog's panel.</para>
/// </summary>
internal static class KeybindConflictPrompt
{
    internal const string DialogKind = "key-conflict";

    internal const string ChoiceEdit = "edit";
    internal const string ChoiceGive = "give";
    internal const string ChoiceLeave = "leave";
    internal const string ChoiceLater = "later";

    internal const string LaterCaption = "Later";

    /// <summary>Frames a "Choose new keys" waits for the Mods workspace to open before it gives up.</summary>
    internal const int EditTimeoutFrames = 900;

    /// <summary>Frames to wait after a dialog could not be drawn before trying again.</summary>
    internal const int RetryFrames = 300;

    /// <summary>The dialogs' owner (a framework dialog; queued as a mod's so it never replaces another).</summary>
    private static readonly object Owner = new();

    private static readonly List<string> Noted = new();
    private static readonly List<KeybindConflictCase> Waiting = new();
    private static readonly HashSet<string> Later = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Notes = new(StringComparer.OrdinalIgnoreCase);

    private static UiDialog? _dialog;
    private static KeybindConflictCase? _shown;
    private static List<string> _shownChoices = new();
    private static bool _answered;
    private static int _retry;
    private static (string Plugin, string Row)? _edit;
    private static int _editFrames;
    private static bool _editOpenRequested;
    private static Dictionary<string, object?>? _last;
    private static bool _ticking;

    /// <summary>
    /// Whether a screen can take the dialog now (the capture, dialog and typing gates are checked
    /// besides). Production: the Mods workspace is open, or the title screen's Mods entry is up, or a
    /// world's HUD is up (a chat window can be read). Tests: false unless set.
    /// </summary>
    internal static Func<bool> ScreenReady { get; set; } = DefaultScreenReady;

    /// <summary>
    /// Whether the game-art copies the dialog draws with (DIALOGS2) are prepared. A key conflict is not
    /// urgent, so it waits for them, at most <see cref="ArtWaitCapMs"/> after it was first ready to
    /// show; then it shows with whatever art there is, so nothing is lost. Production: set by
    /// <c>SettingsArtDonors.StartIdle</c> (true once the idle preparation finished). Default true.
    /// </summary>
    internal static Func<bool> DialogArtReady { get; set; } = AlwaysReady;

    /// <summary>How long a ready dialog waits for its art before showing anyway.</summary>
    internal const long ArtWaitCapMs = 3000;

    /// <summary>Milliseconds clock for the art wait. A seam for tests.</summary>
    internal static Func<long> NowMs { get; set; } = DefaultNowMs;

    private static bool AlwaysReady() => true;

    private static long DefaultNowMs() => Environment.TickCount64;

    /// <summary>When a ready dialog first waited for art; null while none waits.</summary>
    private static long? _artWaitSince;

    /// <summary>True when the art is ready, or a dialog has waited <see cref="ArtWaitCapMs"/> for it.</summary>
    internal static bool ArtGate()
    {
        bool ready;
        try { ready = DialogArtReady(); }
        catch { ready = true; }
        if (ready)
        {
            _artWaitSince = null;
            return true;
        }

        long now = NowMs();
        _artWaitSince ??= now;
        if (now - _artWaitSince.Value < ArtWaitCapMs) return false;
        _artWaitSince = null;
        Plugin.Logger?.LogInfo("Ui: key conflict dialog shown before the dialog art was prepared (waited " + ArtWaitCapMs + " ms).");
        return true;
    }

    /// <summary>Open the Mods workspace (closed) for "Choose new keys". A seam for tests.</summary>
    internal static Action OpenWorkspace { get; set; } = () => ModsMenu.OpenForKeyConflict();

    /// <summary>Panel source for the dialog. A seam for tests.</summary>
    internal static Func<string, IUiPanel> Factory { get; set; } = DefaultFactory;

    private static IUiPanel DefaultFactory(string title) => Ui.Panel(title, Owner, SettingsPanel.PluginId);

    private static bool DefaultScreenReady()
    {
#if PROGENITOR_TESTS
        return false;
#else
        try
        {
            if (!Ui.CurrentRuntime().IsClient) return false;
            if (ModsMenu.IsOpen) return true;
            if (string.Equals(NativeModsEntry.DescribeOwned().Site, "main-menu", StringComparison.Ordinal)) return true;
            // In a world: the HUD is up once a chat window can be read (a loading screen reads as Unknown).
            return Keybinds.ChatFocusNow == ChatFocus.NotFocused;
        }
        catch { return false; }
#endif
    }

    internal static string SignatureOf(string ownerId, string actionId, KeyChord chord) =>
        (ownerId ?? "").Trim() + "/" + (actionId ?? "").Trim() + "/" + chord;

    // ---- noting and deciding ---------------------------------------------------------------

    /// <summary>A plugin registered a bind: its conflicts are read on the next pump.</summary>
    internal static void NoteRegistered(string pluginId)
    {
        string id = (pluginId ?? "").Trim();
        if (id.Length == 0) return;
        lock (Noted)
            if (!Noted.Contains(id, StringComparer.OrdinalIgnoreCase)) Noted.Add(id);
    }

    /// <summary>
    /// One frame, from <see cref="Keybinds.Pump"/>: carry a "Choose new keys" forward, read the noted
    /// plugins' conflicts, and show the next dialog when nothing is in the way.
    /// </summary>
    internal static void Tick()
    {
        if (_ticking) return;
        _ticking = true;
        try
        {
            ContinueEdit();
            Evaluate();
            WatchShown();
            if (_retry > 0)
            {
                _retry--;
                return;
            }

            if (_dialog != null || Waiting.Count == 0 || !CanShowNow()) return;
            if (!ArtGate()) return;
            ShowNext();
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: key conflict prompt threw: " + e.Message); }
        finally { _ticking = false; }
    }

    /// <summary>The gates that hold every dialog back, then <see cref="ScreenReady"/>.</summary>
    internal static bool CanShowNow()
    {
        if (UiDialog.Current != null || UiDialog.QueuedCount > 0) return false;
        if (Keybinds.Capturing || Keybinds.RebindingId != null) return false;
        if (_edit != null) return false;
        if ((ModsMenu.KeybindsPane?.Editing ?? "").Length > 0) return false;
        if (Keybinds.PlayerIsTyping) return false;
        try { return ScreenReady(); }
        catch { return false; }
    }

    private static void Evaluate()
    {
        string[] noted;
        lock (Noted)
        {
            if (Noted.Count == 0) return;
            noted = Noted.ToArray();
            Noted.Clear();
        }

        var set = new HashSet<string>(noted, StringComparer.OrdinalIgnoreCase);
        foreach (var c in Find(KeybindArbiter.Current(), set))
        {
            Waiting.RemoveAll(w => string.Equals(w.PluginId, c.PluginId, StringComparison.OrdinalIgnoreCase));
            // A dialog about this mod already on screen stays; a new one waits only for what it does not cover.
            if (_shown != null && _dialog != null && string.Equals(_shown.PluginId, c.PluginId, StringComparison.OrdinalIgnoreCase))
                continue;
            Waiting.Add(c);
        }
    }

    /// <summary>
    /// The dialogs a picture calls for: every enforced loser whose owner or winner is in
    /// <paramref name="involved"/> (null: every loser), minus clashes decided "later", grouped per losing mod.
    /// </summary>
    internal static List<KeybindConflictCase> Find(KeybindPicture picture, ISet<string>? involved)
    {
        var byMod = new Dictionary<string, List<KeybindConflictClash>>(StringComparer.OrdinalIgnoreCase);
        var triggers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var v in picture.Verdicts)
        {
            if (!v.Enforced || v.Winner == null || !v.Claim.Chord.IsBound) continue;
            var loser = v.Claim;
            var winner = v.Winner;
            bool involves = involved == null || involved.Contains(loser.OwnerId)
                || (winner.Source != KeybindSource.Game && involved.Contains(winner.OwnerId));
            if (!involves) continue;
            if (Later.Contains(SignatureOf(loser.OwnerId, loser.ActionId, loser.Chord))) continue;
            var holders = new List<KeybindClaim> { winner };
            foreach (var o in v.Others)
            {
                if (ReferenceEquals(o, winner)) continue;
                // A mode-only game key only warns (it does not stop the loser); nothing to give or ask about.
                if (o.Source == KeybindSource.Game && !KeybindArbiter.GameWins(o, loser.Scope)) continue;
                holders.Add(o);
            }

            if (!byMod.TryGetValue(loser.OwnerId, out var list))
            {
                byMod[loser.OwnerId] = list = new List<KeybindConflictClash>();
                order.Add(loser.OwnerId);
            }

            list.Add(new KeybindConflictClash(loser, winner, holders));
            // KEYS10: what set the dialog off: the loser itself when it is one of the mods that just registered, else the
            // winner that just did (it comes before the loser by the first-installed rule).
            bool loserNoted = involved == null || involved.Contains(loser.OwnerId);
            if (loserNoted) triggers[loser.OwnerId] = loser.OwnerId;
            else if (!triggers.ContainsKey(loser.OwnerId)) triggers[loser.OwnerId] = winner.OwnerId;
        }

        var cases = new List<KeybindConflictCase>(order.Count);
        foreach (var id in order) cases.Add(new KeybindConflictCase(id, byMod[id], triggers.TryGetValue(id, out var t) ? t : id));
        return cases;
    }

    /// <summary>The clashes of <paramref name="c"/> still standing in the current picture.</summary>
    private static KeybindConflictCase? Refresh(KeybindConflictCase c)
    {
        var picture = KeybindArbiter.Current();
        foreach (var fresh in Find(picture, new HashSet<string>(new[] { c.PluginId }, StringComparer.OrdinalIgnoreCase)))
        {
            if (!string.Equals(fresh.PluginId, c.PluginId, StringComparison.OrdinalIgnoreCase)) continue;
            var keep = new List<KeybindConflictClash>();
            foreach (var clash in fresh.Clashes)
            {
                foreach (var old in c.Clashes)
                {
                    if (!string.Equals(old.Signature, clash.Signature, StringComparison.OrdinalIgnoreCase)) continue;
                    keep.Add(clash);
                    break;
                }
            }

            return keep.Count == 0 ? null : new KeybindConflictCase(c.PluginId, keep, c.TriggerId);
        }

        return null;
    }

    // ---- the dialog ----------------------------------------------------------------------------

    // ---- the dialog's words (pure: the case, names, versions and install times are all it reads) ------------------

    /// <summary>
    /// The dialog's words for <paramref name="c"/>, with the choice each option stands for. KEYS10 (live feedback
    /// 2026-10-16): it says which mod (and version) the dialog is about and which one triggered it, per key who wants it,
    /// who already has it and where each works, and why the holder keeps it (the first-installed rule); each option names
    /// what it changes, with a second line saying exactly which actions.
    /// </summary>
    internal static DialogContent ContentFor(KeybindConflictCase c, out List<string> choices)
    {
        choices = new List<string>();
        var options = new List<string>();
        var notes = new List<string>();
        var givable = new List<KeybindConflictClash>();
        foreach (var clash in c.Clashes) if (clash.Givable) givable.Add(clash);

        options.Add(EditCaptionFor(c));
        notes.Add(EditNote(c));
        choices.Add(ChoiceEdit);
        if (givable.Count > 0)
        {
            options.Add(GiveCaption(c, givable));
            notes.Add(GiveNote(c, givable));
            choices.Add(ChoiceGive);
        }

        options.Add(LeaveCaptionFor(c));
        notes.Add(LeaveNote(c));
        choices.Add(ChoiceLeave);
        return new DialogContent
        {
            Shape = UiDialogShape.Choose,
            Title = TitleFor(c),
            Body = BodyFor(c),
            Facts = FactsFor(c),
            Note = LaterNote(c),
            Options = options,
            OptionNotes = notes,
            CancelCaption = LaterCaption,
            Footer = Ui.FrameworkFooter,
        };
    }

    internal static string TitleFor(KeybindConflictCase c) => "Key conflict: " + c.ModName;

    /// <summary>A mod's version as BepInEx reports it; null or empty when unknown. A seam for tests.</summary>
    internal static Func<string, string?> VersionOf { get; set; } = DefaultVersionOf;

    private static string? DefaultVersionOf(string id)
    {
#if PROGENITOR_TESTS
        return null;
#else
        try
        {
            var plugins = BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance?.Plugins;
            if (plugins == null || string.IsNullOrWhiteSpace(id)) return null;
            return plugins.TryGetValue(id.Trim(), out var info) ? info?.Metadata?.Version?.ToString() : null;
        }
        catch { return null; }
#endif
    }

    /// <summary><c>Hunts 1.2.0</c>: a mod's name, with its version when known.</summary>
    internal static string NameWithVersion(string pluginId)
    {
        string name = KeybindArbiter.DisplayNameOf(pluginId);
        string? v = null;
        try { v = VersionOf(pluginId); }
        catch { v = null; }
        return string.IsNullOrWhiteSpace(v) ? name : name + " " + v!.Trim();
    }

    /// <summary><c>Oct 16, 2026</c>: a mod's install date (the first-installed rule's), or empty when unknown.</summary>
    internal static string InstalledOn(string pluginId)
    {
        var t = KeybindArbiter.InstallOf(pluginId);
        return t.Utc is { } utc ? DateText(utc) : "";
    }

    internal static string DateText(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// What the dialog is about, and what set it off: the mod whose actions are off (its version and install date), or,
    /// when another mod that just loaded comes first for these keys, that mod.
    /// </summary>
    internal static string BodyFor(KeybindConflictCase c)
    {
        string mod = NameWithVersion(c.PluginId);
        string on = InstalledOn(c.PluginId);
        string actions = c.Clashes.Count == 1 ? "this action of " + c.ModName + " is" : "these actions of " + c.ModName + " are";
        if (c.TriggerId.Length > 0 && !string.Equals(c.TriggerId, c.PluginId, StringComparison.OrdinalIgnoreCase))
        {
            string trigger = NameWithVersion(c.TriggerId);
            return trigger + " just loaded and comes before " + c.ModName + " for "
                + (c.Clashes.Count == 1 ? "a key" : "these keys") + ". Until you choose, " + actions + " off:";
        }

        return mod + (on.Length > 0 ? " (installed " + on + ")" : "") + " wants "
            + (c.Clashes.Count == 1 ? "a key that is" : "keys that are") + " already taken. Until you choose, " + actions + " off:";
    }

    /// <summary>
    /// Two facts per key: <c>Mouse Back: Dodge wants it (in gameplay).</c> and, under it, who has it, where, and why it
    /// keeps it.
    /// </summary>
    internal static List<DialogFact> FactsFor(KeybindConflictCase c)
    {
        var facts = new List<DialogFact>(c.Clashes.Count * 2);
        foreach (var clash in c.Clashes)
        {
            facts.Add(new DialogFact(KeybindArbiter.ChordPhrase(clash.Loser.ChordText, clash.Holder),
                clash.Loser.DisplayName + " wants it (" + WhereOf(clash.Loser) + ").", DialogFactTone.Warn));
            facts.Add(new DialogFact("", HolderLine(c, clash)));
        }

        return facts;
    }

    /// <summary>Where a key works, as a short phrase: <c>in gameplay</c>, <c>in menus</c>, <c>on the map</c>.</summary>
    internal static string WhereOf(KeybindClaim k)
    {
        if (k.Source == KeybindSource.Game) return k.Mode.Length > 0 ? k.Mode : "in gameplay";
        return k.Scope switch
        {
            KeybindScope.Menu => "in menus",
            KeybindScope.Window => "in menus and windows with tabs",
            KeybindScope.Map => "on the map",
            KeybindScope.Photo => "in photo mode",
            KeybindScope.Chat => "in chat",
            _ => "in gameplay",
        };
    }

    /// <summary>
    /// <c>The game has it for Interrupt Ability Cast (in gameplay). The game's own keys always come first.</c> or
    /// <c>Companion has it for Net ping (in gameplay). Companion was installed first (Oct 7, 2026; Hunts: Oct 15, 2026).</c>
    /// </summary>
    internal static string HolderLine(KeybindConflictCase c, KeybindConflictClash clash)
    {
        var h = clash.Holder;
        string holder = h.Source == KeybindSource.Game ? "The game" : KeybindArbiter.DisplayNameOf(h.OwnerId);
        string line = holder + " has it for " + h.DisplayName + " (" + WhereOf(h) + "). " + WhyLine(c, clash);
        var also = new List<string>();
        foreach (var o in clash.Holders)
        {
            if (ReferenceEquals(o, h)) continue;
            string p = (o.Source == KeybindSource.Game ? "the game" : KeybindArbiter.DisplayNameOf(o.OwnerId)) + "'s " + o.DisplayName;
            if (!also.Contains(p)) also.Add(p);
        }

        return also.Count == 0 ? line : line + " " + Capitalize(KeybindConflictSource.JoinNames(also)) + " also " + (also.Count == 1 ? "uses" : "use") + " it.";
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    /// <summary>Why the holder keeps the key, from the first-installed rule (<see cref="KeybindArbiter.Precedence"/>).</summary>
    internal static string WhyLine(KeybindConflictCase c, KeybindConflictClash clash)
    {
        var h = clash.Holder;
        string rule = KeybindArbiter.RuleFor(h, clash.Loser, null);
        string holder = KeybindArbiter.DisplayNameOf(h.OwnerId);
        string loser = KeybindArbiter.DisplayNameOf(clash.Loser.OwnerId);
        switch (rule)
        {
            case KeybindArbiter.RuleGame:
                return "The game's own keys always come first.";
            case KeybindArbiter.RuleFramework:
                return "Progenitor's own keys come right after the game's.";
            case KeybindArbiter.RuleInstall:
            {
                string a = InstalledOn(h.OwnerId);
                string b = InstalledOn(clash.Loser.OwnerId);
                return holder + " was installed first" + (a.Length > 0 && b.Length > 0 ? " (" + a + "; " + loser + ": " + b + ")" : "") + ".";
            }
            case KeybindArbiter.RuleUnknownTime:
                return holder + " counts as installed first: " + loser + "'s install time is unknown.";
            default:
                return "Their install times are the same or unknown, so " + holder + " comes first by mod id.";
        }
    }

    /// <summary>The losing actions' names, joined: <c>Dodge</c>, <c>Dodge and Sprint</c>.</summary>
    internal static string LosersPhrase(KeybindConflictCase c)
    {
        var names = new List<string>();
        foreach (var clash in c.Clashes) if (!names.Contains(clash.Loser.DisplayName)) names.Add(clash.Loser.DisplayName);
        return KeybindConflictSource.JoinNames(names);
    }

    private static int LoserCount(KeybindConflictCase c)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var clash in c.Clashes) names.Add(clash.Loser.ActionId);
        return names.Count;
    }

    /// <summary>Who keeps their keys: <c>the game</c>, <c>Companion</c>, <c>the game and Companion</c>.</summary>
    internal static string KeepersPhrase(KeybindConflictCase c)
    {
        var names = new List<string>();
        foreach (var clash in c.Clashes)
        {
            string n = clash.Holder.Source == KeybindSource.Game ? KeybindArbiter.GameName : KeybindArbiter.DisplayNameOf(clash.Holder.OwnerId);
            if (!names.Contains(n)) names.Add(n);
        }

        return KeybindConflictSource.JoinNames(names);
    }

    /// <summary><c>Pick another key for Dodge</c>; several: <c>Pick other keys for Hunts's actions</c>.</summary>
    internal static string EditCaptionFor(KeybindConflictCase c) =>
        LoserCount(c) == 1 ? "Pick another key for " + c.Clashes[0].Loser.DisplayName : "Pick other keys for " + c.ModName + "'s actions";

    /// <summary>What Pick does (<see cref="RequestEdit"/>: the Keybinds tab opens on the first row, waiting for a key).</summary>
    internal static string EditNote(KeybindConflictCase c) =>
        "Opens Mods > Keybinds on " + c.Clashes[0].Loser.DisplayName + ", waiting for its new key"
        + (LoserCount(c) > 1 ? "; " + c.ModName + "'s other actions are listed there too" : "") + ". " + Capitalize(KeepersPhrase(c)) + " "
        + (KeeperCount(c) == 1 ? "keeps its key." : "keep their keys.");

    private static int KeeperCount(KeybindConflictCase c)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var clash in c.Clashes) names.Add(clash.Holder.Source == KeybindSource.Game ? KeybindArbiter.GameOwnerId : clash.Holder.OwnerId);
        return names.Count;
    }

    /// <summary><c>Leave Dodge unbound</c>; several: <c>Leave Hunts's 2 actions unbound</c>.</summary>
    internal static string LeaveCaptionFor(KeybindConflictCase c)
    {
        int n = LoserCount(c);
        return n == 1
            ? "Leave " + c.Clashes[0].Loser.DisplayName + " unbound"
            : "Leave " + c.ModName + "'s " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + " actions unbound";
    }

    internal static string LeaveNote(KeybindConflictCase c) =>
        LosersPhrase(c) + (LoserCount(c) == 1 ? " gets" : " get") + " no key; " + KeepersPhrase(c) + " "
        + (KeeperCount(c) == 1 ? "keeps its key" : "keep their keys") + ". You can pick a key any time in Mods > Keybinds.";

    /// <summary><c>Give F8 to Progenitor Sample Companion</c>; several keys: <c>Give these keys to …</c>.</summary>
    internal static string GiveCaption(KeybindConflictCase c, IReadOnlyList<KeybindConflictClash> givable)
    {
        var chords = new HashSet<KeyChord>();
        foreach (var g in givable) chords.Add(g.Chord);
        return chords.Count == 1 ? "Give " + givable[0].Loser.ChordText + " to " + c.ModName : "Give these keys to " + c.ModName;
    }

    /// <summary><c>Progenitor Sample's Net ping becomes unbound, and Companion ping gets F8.</c></summary>
    internal static string GiveNote(KeybindConflictCase c, IReadOnlyList<KeybindConflictClash> givable)
    {
        var parts = new List<string>();
        foreach (var g in givable)
            parts.Add(g.HoldersPhrase + (g.Holders.Count == 1 ? " becomes" : " become") + " unbound, and " + g.Loser.DisplayName + " gets " + g.Loser.ChordText);
        return string.Join("; ", parts) + ".";
    }

    /// <summary>Later, which is also Escape: nothing is saved, and the next start asks again.</summary>
    internal static string LaterNote(KeybindConflictCase c) =>
        "Later (Escape): nothing is saved. " + LosersPhrase(c) + (LoserCount(c) == 1 ? " stays" : " stay") + " off, and you are asked again at the next start.";

    private static void ShowNext()
    {
        while (Waiting.Count > 0)
        {
            var head = Waiting[0];
            Waiting.RemoveAt(0);
            var c = Refresh(head);
            if (c == null) continue;
            var content = ContentFor(c, out var choices);
            UiDialog dialog;
            try
            {
                dialog = UiDialog.Enqueue(Factory, content, DialogKind, c.PluginId, Owner);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: key conflict dialog for " + c.PluginId + " threw: " + e.Message);
                Waiting.Insert(0, head);
                _retry = RetryFrames;
                return;
            }

            if (!dialog.IsOpen && !dialog.IsQueued)
            {
                // No screen could draw it (no client UI yet): try again later, nothing answered.
                Waiting.Insert(0, c);
                _retry = RetryFrames;
                return;
            }

            _dialog = dialog;
            _shown = c;
            _shownChoices = choices;
            _answered = false;
            dialog.OnPick(i => Answer(i >= 0 && i < _shownChoices.Count ? _shownChoices[i] : ChoiceLater));
            dialog.OnNo(() => Answer(ChoiceLater));
            Plugin.Logger?.LogInfo("Ui: key conflict dialog for " + c.PluginId + ": " + c.Clashes.Count + " key(s) ("
                + string.Join("; ", Sentences(c)) + ").");
            return;
        }
    }

    private static IEnumerable<string> Sentences(KeybindConflictCase c)
    {
        foreach (var clash in c.Clashes) yield return clash.Sentence;
    }

    /// <summary>
    /// The shown dialog left the screen: answered (done), or dropped without an answer (something swept its
    /// panel), which puts the case back at the head of the queue. Also closes a dialog whose clashes have all
    /// gone (the player fixed them in the tab meanwhile, or the mod unloaded).
    /// </summary>
    private static void WatchShown()
    {
        var dialog = _dialog;
        if (dialog == null) return;
        if (dialog.IsOpen || dialog.IsQueued)
        {
            if (_shown != null && Refresh(_shown) == null)
            {
                Plugin.Logger?.LogInfo("Ui: key conflict dialog for " + _shown.PluginId + " closed: its conflicts have gone.");
                Forget();
                dialog.Close();
            }

            return;
        }

        if (!_answered && _shown != null) Waiting.Insert(0, _shown);
        Forget();
    }

    private static void Forget()
    {
        _dialog = null;
        _shown = null;
        _shownChoices = new List<string>();
    }

    /// <summary>Answer the dialog on screen (a click, Escape, or the bridge). False when none is shown.</summary>
    internal static bool Choose(string choice)
    {
        var dialog = _dialog;
        if (dialog == null || !dialog.IsOpen) return false;
        string c = (choice ?? "").Trim().ToLowerInvariant();
        if (c == ChoiceLater)
        {
            dialog.DismissForBridge();
            return true;
        }

        int index = _shownChoices.IndexOf(c);
        if (index < 0) return false;
        dialog.PickForBridge(index);
        return true;
    }

    private static void Answer(string choice)
    {
        var c = _shown;
        _answered = true;
        Forget();
        if (c == null) return;
        var results = new List<object?>();
        switch (choice)
        {
            case ChoiceEdit:
                foreach (var clash in c.Clashes) Later.Add(clash.Signature);
                RequestEdit(c.PluginId, c.Clashes[0].Loser.Key);
                break;
            case ChoiceGive:
                foreach (var clash in c.Clashes)
                {
                    if (!clash.Givable)
                    {
                        Later.Add(clash.Signature);
                        continue;
                    }

                    foreach (var h in clash.Holders)
                    {
                        bool ok = Unbind(h, out string why);
                        if (ok)
                            Notes[h.Key] = "Unbound: " + clash.Loser.ChordText + " was given to " + c.ModName + " (" + clash.Loser.DisplayName + ").";
                        results.Add(Result(h, ok, why));
                    }
                }

                break;
            case ChoiceLeave:
                foreach (var clash in c.Clashes)
                {
                    bool ok = Unbind(clash.Loser, out string why);
                    if (ok)
                        Notes[clash.Loser.Key] = "Left unbound: " + KeybindArbiter.ChordPhrase(clash.Loser.ChordText, clash.Holder)
                            + " is used by " + KeybindRemapGuard.HolderName(clash.Holder) + ".";
                    results.Add(Result(clash.Loser, ok, why));
                }

                break;
            default:
                choice = ChoiceLater;
                foreach (var clash in c.Clashes) Later.Add(clash.Signature);
                break;
        }

        KeybindArbiter.Invalidate();
        _last = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["plugin"] = c.PluginId,
            ["choice"] = choice,
            ["results"] = results,
        };
        Plugin.Logger?.LogInfo("Ui: key conflict for " + c.PluginId + " answered: " + choice + ".");
        if (choice == ChoiceEdit) ContinueEdit();
    }

    private static Dictionary<string, object?> Result(KeybindClaim claim, bool ok, string why) => new(StringComparer.Ordinal)
    {
        ["owner"] = claim.OwnerId,
        ["action"] = claim.ActionId,
        ["unbound"] = ok,
        ["reason"] = why,
    };

    /// <summary>Unbind one mod key through its own writer (a Progenitor bind's cfg value, or the cfg key).</summary>
    private static bool Unbind(KeybindClaim claim, out string why)
    {
        why = "";
        try
        {
            if (claim.Source == KeybindSource.Progenitor)
            {
                bool ok = Keybinds.TryRebind(claim.OwnerId, claim.ActionId, KeyChord.None, Keybinds.RebindCheck.ReservedOnly, out string? reason);
                why = reason ?? "";
                return ok;
            }

            if (claim.Source == KeybindSource.Cfg)
            {
                var result = SettingsCatalog.WriteKey(claim, KeyChord.None);
                why = result.Error ?? "";
                return result.State == SettingWriteState.Accepted;
            }

            why = "The game's own keys are changed in the game's Controls page.";
            return false;
        }
        catch (Exception e)
        {
            why = e.Message;
            return false;
        }
    }

    // ---- "Choose new keys" ----------------------------------------------------------------------

    private static void RequestEdit(string pluginId, string rowId)
    {
        _edit = (pluginId, rowId);
        _editFrames = 0;
        _editOpenRequested = false;
    }

    /// <summary>Open the workspace (if closed), select the Keybinds tab, and start the edit of the row.</summary>
    private static void ContinueEdit()
    {
        if (_edit is not { } edit) return;
        if (++_editFrames > EditTimeoutFrames)
        {
            Plugin.Logger?.LogWarning("Ui: key conflict: the Mods workspace did not open for " + edit.Plugin + "; open Mods > Keybinds to pick a key.");
            _edit = null;
            return;
        }

        if (!ModsMenu.IsOpen)
        {
            if (_editOpenRequested) return;
            _editOpenRequested = true;
            try { OpenWorkspace(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: key conflict: opening the Mods workspace threw: " + e.Message); }
            if (!ModsMenu.IsOpen) return;
        }

        if (ModsMenu.TryBeginConflictEdit(edit.Plugin, edit.Row, out bool started))
        {
            _edit = null;
            if (!started)
                Plugin.Logger?.LogInfo("Ui: key conflict: row " + edit.Row + " could not be edited; the Keybinds tab is open on " + edit.Plugin + ".");
        }
    }

    // ---- notes and readback ----------------------------------------------------------------------

    /// <summary>The note a decision left on a key row (<c>Unbound: F8 was given to …</c>), or empty.</summary>
    internal static string NoteOf(string claimKey) =>
        Notes.TryGetValue(claimKey ?? "", out var n) ? n : "";

    /// <summary>The whole state for <c>mods_ui keys op=read</c> / <c>op=conflict</c>.</summary>
    internal static Dictionary<string, object?> Describe()
    {
        var waiting = new List<object?>();
        foreach (var w in Waiting) waiting.Add(w.PluginId);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["open"] = _dialog is { IsOpen: true },
            ["queued"] = _dialog is { IsQueued: true },
            ["dialog"] = _shown == null ? null : CaseReadback(_shown),
            ["waiting"] = waiting,
            ["noted"] = NotedCount,
            ["later"] = new List<object?>(SortedLater()),
            ["editRequest"] = _edit is { } e ? new Dictionary<string, object?> { ["plugin"] = e.Plugin, ["row"] = e.Row } : null,
            ["last"] = _last,
            ["notes"] = new Dictionary<string, object?>(NotesCopy(), StringComparer.Ordinal),
        };
    }

    /// <summary>The short form <see cref="UiDialog.Describe"/> carries.</summary>
    internal static Dictionary<string, object?> Summary() => new(StringComparer.Ordinal)
    {
        ["open"] = _dialog is { IsOpen: true },
        ["plugin"] = _shown?.PluginId ?? "",
        ["choices"] = new List<object?>(_shownChoices.ConvertAll(c => (object?)c)) { ChoiceLater },
        ["waiting"] = Waiting.Count,
    };

    private static Dictionary<string, object?> CaseReadback(KeybindConflictCase c)
    {
        var content = ContentFor(c, out var choices);
        var clashes = new List<object?>();
        foreach (var clash in c.Clashes)
        {
            var holders = new List<object?>();
            foreach (var h in clash.Holders)
                holders.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["owner"] = h.Source == KeybindSource.Game ? KeybindArbiter.GameOwnerId : h.OwnerId,
                    ["name"] = h.Source == KeybindSource.Game ? KeybindArbiter.GameName : KeybindArbiter.DisplayNameOf(h.OwnerId),
                    ["action"] = h.ActionId,
                    ["actionName"] = h.DisplayName,
                    ["source"] = KeybindClaim.NameOf(h.Source),
                });
            clashes.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["row"] = clash.Loser.Key,
                ["action"] = clash.Loser.ActionId,
                ["name"] = clash.Loser.DisplayName,
                ["chord"] = clash.Loser.ChordText,
                ["sentence"] = clash.Sentence,
                ["givable"] = clash.Givable,
                ["holders"] = holders,
            });
        }

        var options = new List<object?>();
        for (int i = 0; i < choices.Count; i++)
            options.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["index"] = i,
                ["choice"] = choices[i],
                ["caption"] = content.Options[i],
                ["note"] = content.OptionNote(i),
            });
        var facts = new List<object?>();
        foreach (var f in content.Facts) facts.Add(f.ToString());
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["plugin"] = c.PluginId,
            ["mod"] = c.ModName,
            ["trigger"] = c.TriggerId,
            ["title"] = content.Title,
            ["body"] = content.Body,
            ["lines"] = facts,
            ["note"] = content.Note,
            ["options"] = options,
            ["cancel"] = content.CancelCaption,
            ["clashes"] = clashes,
        };
    }

    private static IEnumerable<object?> SortedLater()
    {
        var l = new List<string>(Later);
        l.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var s in l) yield return s;
    }

    private static Dictionary<string, object?> NotesCopy()
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in Notes) d[pair.Key] = pair.Value;
        return d;
    }

    private static int NotedCount
    {
        get { lock (Noted) return Noted.Count; }
    }

    /// <summary>Core unload and tests: forget everything and close a shown dialog without an answer.</summary>
    internal static void Reset()
    {
        var dialog = _dialog;
        Forget();
        try { dialog?.Close(); }
        catch { /* a reset must not fail for the dialog it drops */ }
        try { UiDialog.DropOwner(Owner); }
        catch { /* */ }
        lock (Noted) Noted.Clear();
        Waiting.Clear();
        Later.Clear();
        Notes.Clear();
        _answered = false;
        _retry = 0;
        _edit = null;
        _editFrames = 0;
        _editOpenRequested = false;
        _last = null;
        _ticking = false;
        _artWaitSince = null;
    }

    internal static void ResetForTests()
    {
        Reset();
        ScreenReady = DefaultScreenReady;
        OpenWorkspace = () => ModsMenu.OpenForKeyConflict();
        Factory = DefaultFactory;
        DialogArtReady = AlwaysReady;
        NowMs = DefaultNowMs;
        VersionOf = DefaultVersionOf;
    }
}
