using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Ui;

/// <summary>KEYS1: the Keybinds tab's filter chips.</summary>
internal enum KeybindsFilter
{
    All = 0,
    Conflicts = 1,
    Changed = 2,
}

/// <summary>One key row of the Keybinds tab (a game action, or one mod key).</summary>
internal sealed class KeybindsRowModel
{
    internal KeybindsRowModel(string sectionId, KeybindVerdict verdict, KeybindVerdict? secondary = null, string groupId = "")
    {
        SectionId = sectionId;
        Verdict = verdict;
        Secondary = secondary;
        GroupId = groupId ?? "";
    }

    private KeybindsRowModel(string sectionId, string groupId, KeybindInfoRow info, KeybindVerdict verdict)
        : this(sectionId, verdict, null, groupId)
    {
        Info = info;
    }

    /// <summary>
    /// A keybind group's read-only row (<see cref="KeybindGroups"/>): a control the keybind API cannot bind,
    /// drawn as a muted key cap with its fixed input. Never editable, reset, cleared or in a conflict.
    /// </summary>
    internal static KeybindsRowModel ForInfo(string sectionId, string ownerId, string groupId, KeybindInfoRow info)
    {
        string note = info.Note.Length > 0 ? info.Note : KeybindsTabLayout.InfoRowDefaultNote;
        var claim = new KeybindClaim(KeybindSource.Progenitor, ownerId, info.Id, info.Name, KeyChord.None,
            chordText: info.ChordText.Length == 0 ? "None" : info.ChordText, help: info.Description, comparable: false,
            editable: false, notEditableReason: note);
        var verdict = new KeybindVerdict(claim, true, null, "", Array.Empty<KeybindClaim>(), "");
        return new KeybindsRowModel(sectionId, groupId, info, verdict);
    }

    internal string SectionId { get; }

    /// <summary>The keybind group this row is drawn in (<see cref="KeybindGroups"/>), or empty.</summary>
    internal string GroupId { get; }

    /// <summary>A group's read-only row; null for a key row.</summary>
    internal KeybindInfoRow? Info { get; }

    internal bool IsInfo => Info != null;

    /// <summary>The primary claim's verdict (a game action's primary slot, or the mod key).</summary>
    internal KeybindVerdict Verdict { get; }

    /// <summary>A game action's secondary slot; null for a mod key.</summary>
    internal KeybindVerdict? Secondary { get; }

    internal KeybindClaim Claim => Verdict.Claim;

    /// <summary>The claim's key; a group's read-only row is <c>info:&lt;owner&gt;/&lt;group&gt;/&lt;id&gt;</c>, never a bind's.</summary>
    internal string Id => Info != null ? KeybindsTabLayout.InfoRowId(Claim.OwnerId, GroupId, Info.Id) : Claim.Key;

    internal bool IsGame => Claim.Source == KeybindSource.Game;

    internal string Name => Claim.DisplayName;

    /// <summary>What the key cell draws: <c>F8 + Ctrl</c>, a game action's <c>Q / E</c>, or <c>None</c>.</summary>
    internal string KeyText
    {
        get
        {
            if (!IsGame) return Claim.ChordText;
            string a = Claim.ChordText;
            string b = Secondary?.Claim.ChordText ?? "None";
            return string.Equals(b, "None", StringComparison.OrdinalIgnoreCase) ? a : a + " / " + b;
        }
    }

    /// <summary>
    /// KEYS3: the primary key cap's caption — the game's key text, the mod key's chord — or empty for
    /// an unbound slot, which the game draws as an empty cap.
    /// </summary>
    internal string PrimaryText => CapText(Claim.ChordText);

    /// <summary>
    /// KEYS3: the secondary key cap's caption. A game action's second slot (empty when unbound); always
    /// empty for a mod key, whose cap is drawn disabled (<see cref="SecondaryEnabled"/>).
    /// </summary>
    internal string SecondaryText => IsGame ? CapText(Secondary?.Claim.ChordText) : "";

    /// <summary>KEYS3: what the caps draw (<see cref="KeybindsTabLayout.CapCaption"/>); the readback keeps the full text too.</summary>
    internal string PrimaryCaption => KeybindsTabLayout.CapCaption(PrimaryText);

    internal string SecondaryCaption => KeybindsTabLayout.CapCaption(SecondaryText);

    /// <summary>KEYS3: only a game row's second cap is live (it shows the read-only note); a mod key has one chord.</summary>
    internal bool SecondaryEnabled => IsGame;

    /// <summary>KEYS3: the warn tone on the cap whose key is not active; the kit's muted caption on a read-only mod key.</summary>
    internal CellTone PrimaryTone => !Verdict.Active ? CellTone.Warn : !IsGame && !Editable ? CellTone.Muted : CellTone.Normal;

    internal CellTone SecondaryTone => !IsGame ? CellTone.Disabled
        : Secondary is { Claim.GameSlotExists: false } ? CellTone.Disabled
        : Secondary != null && !Secondary.Active ? CellTone.Warn : CellTone.Normal;

    /// <summary>KEYS3: the game's own description of the action (a game row), or the mod key's help.</summary>
    internal string Description => string.IsNullOrWhiteSpace(Claim.Help) ? "" : ModSettingsLayout.Sentence(Claim.Help);

    /// <summary>KEYS3: a game row's Bindings Menu category id (<see cref="GameKeybindCatalog.CategoryOrder"/>); empty for a mod key.</summary>
    internal string Category => IsGame ? Claim.Category : "";

    private static string CapText(string? chordText)
    {
        string t = (chordText ?? "").Trim();
        return t.Length == 0 || string.Equals(t, "None", StringComparison.OrdinalIgnoreCase) ? "" : t;
    }

    internal bool Active => Verdict.Active && (Secondary?.Active ?? true);

    internal bool Conflict => Verdict.InConflict || (Secondary?.InConflict ?? false);

    /// <summary>A mod key off its default; KEYS9: a game key whose primary or secondary slot is off the game's default.</summary>
    internal bool Changed => IsGame ? Claim.Modified || (Secondary?.Claim.Modified ?? false) : Claim.Modified;

    /// <summary>
    /// A mod key this tab can write (a Progenitor bind, or a cfg key the catalog allows). KEYS9: a game key on the
    /// game's own Bindings Menu, when the game's controls can be written (<see cref="GameKeyRemap.Writer"/>; in a
    /// game-free host they cannot, and the row stays read-only).
    /// </summary>
    internal bool Editable => IsGame ? GameKeyRemap.Writer != null && GameKeyRemap.IsEditable(Claim) : Claim.Editable;

    /// <summary>KEYS9: the claim of a game row's slot (0 primary, 1 secondary); a mod row's own claim.</summary>
    internal KeybindClaim SlotClaim(int slot) => IsGame && slot == 1 && Secondary != null ? Secondary.Claim : Claim;

    /// <summary>
    /// KEYS8: also false when the default is a chord the remap guard always refuses in this row's scope (another
    /// mod's bare <c>Mouse0</c> default): Reset could never succeed, and Reset all would log a refusal for it.
    /// </summary>
    internal bool CanReset => IsGame ? Editable && Changed
        : Editable && Changed && Claim.DefaultChord is { } d && MouseChords.Allowed(d, Claim.Scope);

    internal bool CanClear => Editable && (Claim.Chord.IsBound || !Claim.Comparable && !string.Equals(Claim.ChordText, "None", StringComparison.OrdinalIgnoreCase));

    /// <summary>The conflict sentence (loser, winner or the game's), both slots joined for a game row.</summary>
    internal string ConflictText
    {
        get
        {
            string a = Verdict.Reason;
            string b = Secondary?.Reason ?? "";
            if (a.Length == 0) return b;
            if (b.Length == 0 || string.Equals(a, b, StringComparison.Ordinal)) return a;
            return a + " " + b;
        }
    }

    /// <summary>The help line: the key's own help, its conflict sentence, a cfg key's note, the default when changed.</summary>
    internal string HelpLine
    {
        get
        {
            // The conflict sentence first: the note under the name is one line, and it is what matters most.
            var parts = new List<string>(6);
            if (ReservedText.Length > 0) parts.Add(ReservedText);
            if (DecisionNote.Length > 0) parts.Add(DecisionNote);
            if (ConflictText.Length > 0) parts.Add(ConflictText);
            if (!string.IsNullOrWhiteSpace(Claim.Help)) parts.Add(ModSettingsLayout.Sentence(Claim.Help));
            if (Claim.Source == KeybindSource.Cfg)
            {
                parts.Add(KeybindsTabLayout.FromConfigFile);
                if (!Claim.Editable && Claim.NotEditableReason.Length > 0) parts.Add(Claim.NotEditableReason);
            }

            if (Info != null) parts.Add(Claim.NotEditableReason);
            if (Changed && IsGame) parts.Add(GameDefaultSentence);
            else if (Changed) parts.Add(ModSettingsLayout.ChangedFromPrefix + (Claim.DefaultText.Length == 0 ? "(empty)" : Claim.DefaultText) + ".");
            return string.Join(" ", parts);
        }
    }

    /// <summary>KEYS9: <c>The game's default is W.</c> (both slots when the second one has a key: <c>Q / E</c>).</summary>
    internal string GameDefaultSentence => "The game's default is " + GameDefaultText + ".";

    /// <summary>KEYS9: the game's default keys for this row: <c>W</c>, <c>Q / E</c>, or <c>no key</c>.</summary>
    internal string GameDefaultText
    {
        get
        {
            string a = Claim.DefaultText.Length == 0 ? "None" : Claim.DefaultText;
            string b = Secondary?.Claim.DefaultText ?? "";
            bool second = b.Length > 0 && !string.Equals(b, "None", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(a, "None", StringComparison.OrdinalIgnoreCase) && !second) return "no key";
            return a + (second ? " / " + b : "");
        }
    }

    /// <summary>
    /// KEYS2: the reserved-key sentence. A Progenitor bind whose default or cfg value Windows or Steam
    /// reserves (registered unbound or on its default, until the player picks a key); an ordinary mod's key
    /// on a reserved chord (<c>Reserved by Windows; this key may not reach the mod.</c>). Empty otherwise.
    /// </summary>
    internal string ReservedText
    {
        get
        {
            if (IsGame) return "";
            if (Claim.Source == KeybindSource.Cfg) return ReservedChords.CfgRowWarning(Claim);
            return Keybinds.ReservedNoteOf(Claim.OwnerId, Claim.ActionId)?.Sentence ?? "";
        }
    }

    /// <summary>
    /// KEYS4: what a key-conflict dialog did to this unbound mod key (<c>Unbound: F8 was given to …</c>,
    /// <c>Left unbound: F8 is used by …</c>), for this Core generation; empty once the key is bound again.
    /// </summary>
    internal string DecisionNote => IsGame || Claim.Chord.IsBound ? "" : KeybindConflictPrompt.NoteOf(Claim.Key);

    /// <summary>KEYS2: <c>windows</c>, <c>steam</c> or empty, for the readback.</summary>
    internal string Reserved
    {
        get
        {
            if (IsGame) return "";
            var by = Claim.Source == KeybindSource.Cfg
                ? ReservedChords.CfgReservedBy(Claim)
                : Keybinds.ReservedNoteOf(Claim.OwnerId, Claim.ActionId)?.By ?? ReservedBy.None;
            return by == ReservedBy.None ? "" : ReservedChords.NameOf(by).ToLowerInvariant();
        }
    }

    /// <summary>
    /// KEYS2: a Progenitor bind that is not active (it lost its chord to the game or to a mod installed
    /// first). The Conflicts view counts these in its header line.
    /// </summary>
    internal bool Inactive => !IsGame && Claim.Source == KeybindSource.Progenitor && !Verdict.Active;

    /// <summary>
    /// Live check 2026-09-29: only the side that is not active is drawn in the warn tone; a winning key
    /// (or one that only warns) looks normal with a muted note.
    /// </summary>
    internal bool Loses => !Verdict.Active || (Secondary != null && !Secondary.Active);

    /// <summary>The row's tone: <c>warn</c> when this key is not active, <c>readonly</c> for a game key, else <c>normal</c>.</summary>
    internal string Tone => Loses ? "warn" : (IsGame && !Editable) || IsInfo ? "readonly" : "normal";

    /// <summary>A read-only row has no reset or clear. KEYS9: a game row the tab can change has both (Unbind clears the primary key, as the game's Clear does).</summary>
    internal bool HasGutter => Editable;

    /// <summary>Search: the name, the key, the action id, the help and the owner's name.</summary>
    internal bool Matches(string query, string ownerName)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        string q = query.Trim();
        return Has(Name, q) || Has(KeyText, q) || Has(Claim.ActionId, q) || Has(Claim.Help, q) || Has(ownerName, q)
            || Has(Claim.Category, q) || (Info != null && Has(Info.Note, q));
    }

    private static bool Has(string? text, string q) =>
        !string.IsNullOrEmpty(text) && text!.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>The <c>mods_ui widget_read</c> row (docs/mods-menu.md#keybinds-tab).</summary>
    internal Dictionary<string, object?> Readback(bool visible)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Id,
            ["section"] = SectionId,
            ["owner"] = IsGame ? KeybindArbiter.GameOwnerId : Claim.OwnerId,
            ["action"] = Claim.ActionId,
            ["name"] = Name,
            ["chord"] = Claim.ChordText,
            ["keyText"] = KeyText,
            ["default"] = Claim.DefaultText,
            ["source"] = KeybindClaim.NameOf(Claim.Source),
            ["active"] = Active,
            ["changed"] = Changed,
            ["editable"] = Editable,
            ["notEditableReason"] = IsGame ? GameKeyRemap.NotEditableNow(Claim) : Claim.NotEditableReason,
            ["comparable"] = Claim.Comparable,
            ["tone"] = Tone,
            ["help"] = HelpLine,
            ["reserved"] = Reserved,
            ["decision"] = DecisionNote,
            ["scope"] = Claim.Scope switch { KeybindScope.Menu => "menu", KeybindScope.Photo => "photo", KeybindScope.Map => "map", KeybindScope.Chat => "chat", KeybindScope.Window => "window", _ => "gameplay" },
            ["visible"] = visible,
            ["conflict"] = KeybindArbiter.ConflictReadback(Verdict) ?? (Secondary == null ? null : KeybindArbiter.ConflictReadback(Secondary)),
        };
        // KEYS3: the two key caps as drawn.
        d["primary"] = PrimaryText;
        d["primaryCaption"] = PrimaryCaption;
        if (IsGame) d["secondaryCaption"] = SecondaryCaption;
        d["marker"] = KeybindsTabLayout.RowMarker(this);
        d["primaryTone"] = UiCellRowHandle.ToneName(PrimaryTone);
        // A mod row has no second cap (decided 2026-09-30), so it reports none.
        if (IsGame) d["secondaryCap"] = SecondaryText;
        d["secondaryEnabled"] = SecondaryEnabled;
        d["description"] = Description;
        // Settings organization stage 2: a mod key is one stored value, also drawn on its mod page.
        if (!IsGame)
        {
            SettingPlaces.AddReadback(d, Claim.OwnerId + "/" + Keybinds.Section + "/" + Claim.ActionId, Claim.OwnerId, keybind: true);
            d["value"] = Claim.ChordText;
        }

        if (IsGame)
        {
            d["category"] = Claim.Category;
            d["categoryTitle"] = GameKeybindCategories.TitleOf(Claim.Category);
            d["secondary"] = Secondary?.Claim.ChordText ?? "None";
            d["secondaryDefault"] = Secondary?.Claim.DefaultText ?? "";
            d["secondaryExists"] = Secondary?.Claim.GameSlotExists ?? false;
            d["primaryChanged"] = Claim.Modified;
            d["secondaryChanged"] = Secondary?.Claim.Modified ?? false;
        }

        // Keybind groups: the group a row is drawn in, and a group's read-only row (a fixed control).
        if (GroupId.Length > 0) d["group"] = GroupId;
        if (Info != null)
        {
            d["info"] = true;
            d["source"] = "info";
            d["note"] = Claim.NotEditableReason;
        }

        return d;
    }
}

/// <summary>
/// KEYS3: what the description column on the right shows for the hovered or selected row — as the
/// game's Bindings Menu details panel: the action's name as the heading, then its description — plus
/// the tab's own notes: the conflict, reserved-key and refusal sentences and the read-only note of a
/// game key. Game-free; <see cref="KeybindsPane"/> writes it into the column's labels.
/// </summary>
internal sealed class KeybindsDescription
{
    internal const string SourceHover = "hover";
    internal const string SourceSelected = "selected";
    internal const string SourceNone = "none";

    internal KeybindsDescription(string rowId, string heading, string body, string warning, string warningTone, string info, string source, string hint = "")
    {
        Hint = hint ?? "";
        RowId = rowId ?? "";
        Heading = heading ?? "";
        Body = body ?? "";
        Warning = warning ?? "";
        WarningTone = warningTone ?? "";
        Info = info ?? "";
        Source = source ?? SourceNone;
    }

    internal string RowId { get; }

    internal string Heading { get; }

    /// <summary>The game's description of the action, or the mod key's help.</summary>
    internal string Body { get; }

    /// <summary>Refusal / capture status, reserved-key and conflict sentences, in that order.</summary>
    internal string Warning { get; }

    /// <summary><c>warn</c> when this key is not active, reserved or just refused; <c>muted</c> for an informational note; empty when none.</summary>
    internal string WarningTone { get; }

    /// <summary>The read-only note of a game key, a config-file key's notes, <c>The default is …</c>.</summary>
    internal string Info { get; }

    /// <summary><c>hover</c>, <c>selected</c> or <c>none</c>.</summary>
    internal string Source { get; }

    /// <summary>KEYS3 polish: what the hovered gutter button does (or why it is dimmed); empty otherwise.</summary>
    internal string Hint { get; }

    internal static readonly KeybindsDescription Empty = new(
        "", KeybindsTabLayout.DescriptionTitle, KeybindsTabLayout.DescriptionIntro, "", "", "", SourceNone);

    internal Dictionary<string, object?> Readback() => new(StringComparer.Ordinal)
    {
        ["row"] = RowId,
        ["source"] = Source,
        ["heading"] = Heading,
        ["body"] = Body,
        ["warning"] = Warning,
        ["warningTone"] = WarningTone,
        ["info"] = Info,
        ["hint"] = Hint,
    };
}

/// <summary>One section: the game, or one mod.</summary>
internal sealed class KeybindsSectionModel
{
    internal KeybindsSectionModel(string id, string title, string ownerId, bool isGame, bool hasModPage, string category = "")
    {
        Id = id;
        Title = title;
        OwnerId = ownerId;
        IsGame = isGame;
        HasModPage = hasModPage;
        Category = category ?? "";
    }

    /// <summary><c>game:&lt;category&gt;</c> (KEYS3: one per Bindings Menu category), or <c>mod:&lt;plugin id&gt;</c>.</summary>
    internal string Id { get; }

    /// <summary>KEYS3: a game section's category id (<see cref="GameKeybindCatalog.CategoryOrder"/>); empty for a mod.</summary>
    internal string Category { get; }

    internal string Title { get; }

    internal string OwnerId { get; }

    internal bool IsGame { get; }

    /// <summary>The mod has an entry in the Mods list, so the heading links to its page.</summary>
    internal bool HasModPage { get; }

    internal List<KeybindsRowModel> Rows { get; } = new();

    /// <summary>A keybind group's section (<see cref="KeybindGroups"/>): its group id, else empty.</summary>
    internal string GroupId { get; private set; } = "";

    internal bool IsGroup => GroupId.Length > 0;

    private KeybindGroupDefinition? _group;

    /// <summary>A keybind group's section, after its owner's (<see cref="KeybindGroups"/>).</summary>
    internal static KeybindsSectionModel ForGroup(KeybindGroupDefinition group) =>
        new(KeybindsTabLayout.GroupSectionId(group.GroupId), group.Title, group.OwnerPluginId, false, false)
        {
            GroupId = group.GroupId,
            _group = group,
        };

    /// <summary>
    /// A group's section note while its feature is unavailable (<c>Unavailable on this server: …</c>),
    /// read now; null otherwise. The rows stay and can still be changed.
    /// </summary>
    internal string? UnavailableReason => _group?.UnavailableReason;

    /// <summary>The group can report itself unavailable, so its section keeps a note row (shown only while it is).</summary>
    internal bool CanBeUnavailable => _group?.UnavailableReasonSource != null;

    /// <summary>The key rows (a group's read-only rows are not keys).</summary>
    internal int KeyRowCount
    {
        get
        {
            int n = 0;
            foreach (var r in Rows) if (!r.IsInfo) n++;
            return n;
        }
    }

    /// <summary>The count on the right of the heading: <c>1 key</c> / <c>12 keys</c> (a group's read-only rows are not counted).</summary>
    internal string CountText => KeybindsTabLayout.KeyCount(KeyRowCount);

    internal bool IsCfg
    {
        get
        {
            if (IsGame || Rows.Count == 0) return false;
            foreach (var r in Rows)
                if (r.Claim.Source != KeybindSource.Cfg) return false;
            return true;
        }
    }
}

/// <summary>
/// KEYS1 (docs/mods-menu.md#keybinds-tab): the Keybinds tab's content and filtering, game-free. The
/// runtime (<see cref="KeybindsPane"/>) draws what this decides: one section per mod first
/// in the Mods list's order (mods not in the list after them, by name), then the game's categories; rows; the
/// filter chips' counts; which rows the search and the chips leave visible; and the jump targets.
/// </summary>
internal sealed class KeybindsTabModel
{
    private KeybindsTabModel(KeybindPicture picture, List<KeybindsSectionModel> sections, string gameCacheNote = "")
    {
        Picture = picture;
        Sections = sections;
        GameCacheNote = gameCacheNote ?? "";
    }

    /// <summary>
    /// KEYS3: the game's keys are shown from the last in-world read (<see cref="KeybindGameCache"/>) because
    /// they cannot be read here (the title screen); this is the muted note that says so, else empty.
    /// </summary>
    internal string GameCacheNote { get; }

    internal bool GameFromCache => GameCacheNote.Length > 0;

    internal KeybindPicture Picture { get; }

    internal IReadOnlyList<KeybindsSectionModel> Sections { get; }

    internal bool GameAvailable => Picture.GameAvailable;

    /// <summary>KEYS3: there are game sections to draw (read now, or from the last in-world read).</summary>
    internal bool HasGameSections
    {
        get
        {
            foreach (var s in Sections) if (s.IsGame) return true;
            return false;
        }
    }

    internal string GameNote => Picture.GameNote;

    internal KeybindsSectionModel? Find(string sectionId)
    {
        foreach (var s in Sections)
            if (string.Equals(s.Id, sectionId, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    internal KeybindsRowModel? FindRow(string rowId)
    {
        foreach (var s in Sections)
            foreach (var r in s.Rows)
                if (string.Equals(r.Id, rowId, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    internal IEnumerable<KeybindsRowModel> ModRows()
    {
        foreach (var s in Sections)
        {
            if (s.IsGame) continue;
            foreach (var r in s.Rows) yield return r;
        }
    }

    /// <summary>The Conflicts chip's N: mod keys in a clash (a game key wanted by a mod counts once, through the mod key).</summary>
    internal int ConflictCount
    {
        get
        {
            int n = 0;
            foreach (var r in ModRows()) if (r.Conflict) n++;
            return n;
        }
    }

    /// <summary>KEYS2: Progenitor binds that are not active because of a conflict (the Conflicts header's N).</summary>
    internal int InactiveCount
    {
        get
        {
            int n = 0;
            foreach (var r in ModRows()) if (r.Inactive) n++;
            return n;
        }
    }

    internal int ChangedCount
    {
        get
        {
            int n = 0;
            foreach (var r in ModRows()) if (r.Changed) n++;
            return n;
        }
    }

    /// <summary>Mod keys the Reset-all button would reset (editable, changed, with a default).</summary>
    internal List<KeybindsRowModel> ResettableRows()
    {
        var list = new List<KeybindsRowModel>();
        foreach (var r in ModRows()) if (r.CanReset) list.Add(r);
        return list;
    }

    internal static bool RowVisible(KeybindsRowModel row, KeybindsSectionModel section, KeybindsFilter filter, string? query)
    {
        if (filter == KeybindsFilter.Conflicts && !row.Conflict) return false;
        if (filter == KeybindsFilter.Changed && !row.Changed) return false;
        string q = (query ?? "").Trim();
        if (q.Length == 0) return true;
        // A matching section title keeps all its rows, as the mod page's search does.
        if (section.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return row.Matches(q, section.Title);
    }

    /// <summary>
    /// Keybind groups (<see cref="KeybindGroups"/>): each group's binds leave their owner's section for a
    /// section of their own placed right after it (an owner's groups in definition order), rows in the
    /// group's order, then its read-only rows. Only Progenitor binds move; a cfg key never does. A group
    /// whose owner has no keys is not drawn; an owner's section left empty is dropped.
    /// </summary>
    private static void ApplyGroups(List<KeybindsSectionModel> sections, IReadOnlyList<KeybindGroupDefinition> groups)
    {
        if (groups == null || groups.Count == 0) return;
        for (int i = 0; i < sections.Count; i++)
        {
            var owner = sections[i];
            if (owner.IsGame || owner.IsGroup) continue;
            int insert = i + 1;
            foreach (var g in groups)
            {
                if (!string.Equals(g.OwnerPluginId, owner.OwnerId, StringComparison.OrdinalIgnoreCase)) continue;
                var section = KeybindsSectionModel.ForGroup(g);
                var taken = new List<(int Index, KeybindsRowModel Row)>();
                for (int r = owner.Rows.Count - 1; r >= 0; r--)
                {
                    var row = owner.Rows[r];
                    if (row.Claim.Source != KeybindSource.Progenitor) continue;
                    int at = g.IndexOf(row.Claim.ActionId);
                    if (at < 0) continue;
                    taken.Add((at, new KeybindsRowModel(section.Id, row.Verdict, row.Secondary, g.GroupId)));
                    owner.Rows.RemoveAt(r);
                }

                taken.Sort((a, b) => a.Index.CompareTo(b.Index));
                foreach (var (_, row) in taken) section.Rows.Add(row);
                foreach (var info in g.InfoRows)
                    section.Rows.Add(KeybindsRowModel.ForInfo(section.Id, owner.OwnerId, g.GroupId, info));
                if (section.Rows.Count == 0) continue;
                sections.Insert(insert++, section);
            }

            if (owner.Rows.Count == 0)
            {
                sections.RemoveAt(i);
                insert--;
            }

            i = insert - 1;
        }
    }

    internal static int VisibleRows(KeybindsSectionModel section, KeybindsFilter filter, string? query)
    {
        int n = 0;
        foreach (var r in section.Rows) if (RowVisible(r, section, filter, query)) n++;
        return n;
    }

    /// <summary>
    /// Build the tab. <paramref name="modOrder"/> is the Mods list's order (id, name); <paramref name="hasModPage"/>
    /// says whether a mod has an entry there to link to.
    /// </summary>
    internal static KeybindsTabModel Build(
        KeybindPicture picture, IReadOnlyList<(string Id, string Name)>? modOrder, Func<string, bool>? hasModPage = null,
        (IReadOnlyList<KeybindClaim> Claims, DateTime SavedUtc)? cachedGame = null,
        IReadOnlyList<KeybindGroupDefinition>? groups = null)
    {
        var sections = new List<KeybindsSectionModel>();
        // Requested 2026-09-30: the mods' keys come first, then the game's categories.
        var gameSections = new List<KeybindsSectionModel>();
        // KEYS3: at the title screen the game's keys come from the last in-world read, shown read-only
        // with a note. Their verdicts are the game's own (no mod key is compared with them here; the
        // remap guard already checks a new mod key against the same cache).
        IReadOnlyList<KeybindVerdict>? gameVerdicts = picture.GameAvailable ? picture.Verdicts : null;
        string cacheNote = "";
        if (gameVerdicts == null && cachedGame is { } cached && cached.Claims.Count > 0)
        {
            gameVerdicts = KeybindArbiter.Compute(cached.Claims, Array.Empty<KeybindClaim>(), Array.Empty<KeybindClaim>()).Verdicts;
            cacheNote = KeybindGameCache.ReadNote(cached.SavedUtc);
        }

        if (gameVerdicts != null)
        {
            // KEYS3: one section per Bindings Menu category, in the game's order (an unknown category
            // after the known ones, in the order first seen); rows keep the reader's (the page's) order.
            var secondaries = new Dictionary<string, KeybindVerdict>(StringComparer.Ordinal);
            foreach (var v in gameVerdicts)
                if (v.Claim.Source == KeybindSource.Game && v.Claim.Slot == 1) secondaries[v.Claim.ActionId] = v;
            var byCategory = new Dictionary<string, KeybindsSectionModel>(StringComparer.OrdinalIgnoreCase);
            var seen = new List<string>();
            foreach (var v in gameVerdicts)
            {
                if (v.Claim.Source != KeybindSource.Game || v.Claim.Slot != 0) continue;
                string category = string.IsNullOrWhiteSpace(v.Claim.Category) ? GameKeybindCatalog.General : v.Claim.Category.Trim();
                if (!byCategory.TryGetValue(category, out var section))
                {
                    string id = KeybindsTabLayout.GameCategorySectionId(category);
                    section = new KeybindsSectionModel(id, GameKeybindCategories.TitleOf(category), KeybindArbiter.GameOwnerId, true, false, category);
                    byCategory[category] = section;
                    seen.Add(category);
                }

                secondaries.TryGetValue(v.Claim.ActionId, out var second);
                section.Rows.Add(new KeybindsRowModel(section.Id, v, second));
            }

            var order = new List<string>(seen);
            order.Sort((a, b) =>
            {
                int c = GameKeybindCatalog.CategoryIndex(a).CompareTo(GameKeybindCatalog.CategoryIndex(b));
                return c != 0 ? c : seen.IndexOf(a).CompareTo(seen.IndexOf(b));
            });
            foreach (var category in order) gameSections.Add(byCategory[category]);
        }

        var byOwner = new Dictionary<string, List<KeybindVerdict>>(StringComparer.OrdinalIgnoreCase);
        var ownerOrder = new List<string>();
        foreach (var v in picture.Verdicts)
        {
            if (v.Claim.Source == KeybindSource.Game) continue;
            if (!byOwner.TryGetValue(v.Claim.OwnerId, out var list))
            {
                byOwner[v.Claim.OwnerId] = list = new List<KeybindVerdict>();
                ownerOrder.Add(v.Claim.OwnerId);
            }

            list.Add(v);
        }

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        if (modOrder != null)
        {
            foreach (var (id, name) in modOrder)
            {
                if (string.IsNullOrWhiteSpace(id) || !byOwner.ContainsKey(id) || names.ContainsKey(id)) continue;
                names[id] = string.IsNullOrWhiteSpace(name) ? KeybindArbiter.DisplayNameOf(id) : name.Trim();
                ordered.Add(id);
            }
        }

        var rest = new List<string>();
        foreach (var id in ownerOrder)
            if (!names.ContainsKey(id)) rest.Add(id);
        rest.Sort((a, b) =>
        {
            int c = StringComparer.OrdinalIgnoreCase.Compare(KeybindArbiter.DisplayNameOf(a), KeybindArbiter.DisplayNameOf(b));
            return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(a, b);
        });
        foreach (var id in rest)
        {
            names[id] = KeybindArbiter.DisplayNameOf(id);
            ordered.Add(id);
        }

        foreach (var id in ordered)
        {
            bool page = false;
            try { page = hasModPage?.Invoke(id) ?? false; }
            catch { page = false; }
            var section = new KeybindsSectionModel(KeybindsTabLayout.ModSectionId(id), names[id], id, false, page);
            var verdicts = byOwner[id];
            // Progenitor binds first, in id order, then the mod's own config-file keys.
            verdicts.Sort((a, b) =>
            {
                int s = a.Claim.Source.CompareTo(b.Claim.Source);
                return s != 0 ? s : StringComparer.OrdinalIgnoreCase.Compare(a.Claim.ActionId, b.Claim.ActionId);
            });
            foreach (var v in verdicts) section.Rows.Add(new KeybindsRowModel(section.Id, v));
            sections.Add(section);
        }

        ApplyGroups(sections, groups ?? KeybindGroups.Snapshot());
        sections.AddRange(gameSections);
        return new KeybindsTabModel(picture, sections, gameSections.Count > 0 ? cacheNote : "");
    }
}

/// <summary>KEYS1: words, keys and sizes of the Keybinds tab. Sizes reuse the settings grid's (<see cref="ModSettingsGrid"/>).</summary>
internal static class KeybindsTabLayout
{
    internal const string GameSectionId = "game";
    internal const string GameSectionPrefix = "game:";
    internal const string ModSectionPrefix = "mod:";
    internal const string GameTitle = "Game";

    /// <summary>KEYS3: the list with the description column on its right (the game's Bindings Menu).</summary>
    internal const MenuPaneMode PaneMode = MenuPaneMode.ListWithAside;

    /// <summary>KEYS3: the note a click on a game key cap puts in the description column.</summary>
    internal const string GameCapNote = "Change game keys in Esc > Options > Controls.";

    /// <summary>
    /// KEYS3 review M2: the note on the first game category heading shown: the read-only note, after the
    /// age of the snapshot when the keys come from the last in-world read (the title screen).
    /// </summary>
    internal static string GameHeadingNote(string cacheNote)
    {
        // KEYS9: where the game's keys can be written, the note says so; at the title (the last read) a change waits for a world.
        string note = GameKeyRemap.Writer == null ? GameCapNote
            : string.IsNullOrWhiteSpace(cacheNote) ? GameEditNote
            : GameKeyRemap.OutsideWorldReason;
        return string.IsNullOrWhiteSpace(cacheNote) ? note : cacheNote.Trim() + " " + note;
    }

    /// <summary>KEYS9: the Game group's note where the game's keys can be changed here.</summary>
    internal const string GameEditNote = "Click a game key to change it. This changes the game's own controls (also in Esc > Options > Controls).";

    /// <summary>KEYS3: the description column before any row is hovered or selected.</summary>
    internal const string DescriptionTitle = "Keybinds";

    internal const string DescriptionIntro = "Point at a key to see what it does. Click a mod's key to change it.";

    /// <summary>KEYS3: <c>game:movement</c>, <c>game:skills-and-attacks</c>, … for a Bindings Menu category.</summary>
    internal static string GameCategorySectionId(string category)
    {
        var sb = new System.Text.StringBuilder();
        bool dash = false;
        foreach (char ch in (category ?? "").Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); dash = false; }
            else if (ch == '&') { if (!dash && sb.Length > 0) sb.Append('-'); sb.Append("and"); dash = false; }
            else if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; }
        }

        string slug = sb.ToString().Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return GameSectionPrefix + (slug.Length == 0 ? "other" : slug);
    }

    /// <summary>KEYS3: <c>game</c> (the jump target) or any <c>game:&lt;category&gt;</c> section.</summary>
    internal static bool IsGameSectionId(string? id) =>
        string.Equals(id, GameSectionId, StringComparison.OrdinalIgnoreCase)
        || (id ?? "").StartsWith(GameSectionPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The category underline: its share of the heading's width and its height.</summary>
    internal const float CategoryRuleShare = 0.45f;

    internal const float CategoryRuleHeight = 2f;

    /// <summary>The highlight band's borrowed top gradient: a thin lighter rule, at most this tall.</summary>
    internal const float HighlightTopMaxHeight = 3f;

    /// <summary>The top rule's alpha is capped here, so the borrowed gradient can only ever be a faint line.</summary>
    internal const float HighlightTopMaxAlpha = 0.35f;

    /// <summary>
    /// The hovered or selected row's band: a solid fill (KEYS3 polish, live 2026-09-30: the borrowed
    /// <c>ListButton_Glow2</c> sprite drew as a harsh white-to-gray band, so no sprite is drawn) in the
    /// game's own row tint. MENUKIT2 probe M1 read that tint from the Options page entries'
    /// <c>ButtonTarget</c>: hue (0.163, 0.395, 0.585), drawn here at
    /// <see cref="Progenitor.Ui.GameArt.GameSettingsLook.FlatBandAlpha"/> so the flat fill matches the
    /// gradient's measured top. The settings rows use the same band.
    /// </summary>
    internal static PaletteColor HighlightBand
    {
        get
        {
            var c = Progenitor.Ui.GameArt.GameSettingsLook.FlatRowBand();
            return new PaletteColor(c.R, c.G, c.B, c.A);
        }
    }

    /// <summary>Where <see cref="HighlightBand"/> comes from, for <c>art.highlight.tint</c>.</summary>
    internal const string HighlightBandSource = "game-row-tint";

    /// <summary>
    /// The band may never wash out the row's name: its brightest channel times its alpha stays at or
    /// under this (0.45 at 0.6 alpha), whatever the source.
    /// </summary>
    internal const float MaxBandBrightness = 0.45f * 0.6f;

    internal static bool BandIsDim(float r, float g, float b, float a) =>
        a <= 0.6f + 1e-4f && Math.Max(r, Math.Max(g, b)) * a <= MaxBandBrightness + 1e-4f;

    /// <summary>
    /// The top rule's color: the donor image's color times the game's lit <c>ButtonTarget</c> tint for the
    /// <c>Gradient</c> (M1: (0.688, 0.883, 0.953, 0.502)), alpha capped at <see cref="HighlightTopMaxAlpha"/>.
    /// </summary>
    internal static (float R, float G, float B, float A) HighlightTopColor(float r, float g, float b, float a)
    {
        var t = Progenitor.Ui.GameArt.GameSettingsLook.RowBandTopLit;
        float alpha = float.IsNaN(a) ? 1f : Math.Max(0f, a);
        return (r * t.R, g * t.G, b * t.B, Math.Min(HighlightTopMaxAlpha, alpha * t.A));
    }

    // ---- KEYS3 live follow-up: compact key caps -----------------------------------------------

    /// <summary>The smallest caption size a key cap shrinks to before it is cut with an ellipsis.</summary>
    internal const float CapMinFontSize = 11f;

    private static readonly string[] ModifierOrder = { "ctrl", "control", "shift", "alt", "altgr", "win", "cmd" };

    /// <summary>
    /// A key cap's caption: a chord written modifiers first and joined without spaces, as the game writes
    /// its keys (<c>F12 + Ctrl + Shift</c> → <c>Ctrl+Shift+F12</c>; <c>E + Left Ctrl</c> → <c>Left Ctrl+E</c>).
    /// A single key is unchanged. The readback keeps the full text.
    /// </summary>
    internal static string CapCaption(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0 || t.IndexOf(KeyChord.Separator, StringComparison.Ordinal) < 0) return t;
        var parts = t.Split(new[] { KeyChord.Separator }, StringSplitOptions.RemoveEmptyEntries);
        var mods = new List<(int Rank, int Index, string Text)>();
        var keys = new List<string>();
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (part.Length == 0) continue;
            int rank = ModifierRank(part);
            if (rank >= 0) mods.Add((rank, i, part));
            else keys.Add(part);
        }

        mods.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Index.CompareTo(b.Index));
        var ordered = new List<string>(parts.Length);
        foreach (var m in mods) ordered.Add(m.Text);
        ordered.AddRange(keys);
        return string.Join("+", ordered);
    }

    private static int ModifierRank(string part)
    {
        string p = part.ToLowerInvariant();
        foreach (var side in new[] { "left ", "right ", "l", "r" })
        {
            if (!p.StartsWith(side, StringComparison.Ordinal)) continue;
            string rest = p.Substring(side.Length);
            int r = Array.IndexOf(ModifierOrder, rest);
            if (r >= 0) return Norm(r);
        }

        int direct = Array.IndexOf(ModifierOrder, p);
        return direct >= 0 ? Norm(direct) : -1;

        static int Norm(int r) => r == 1 ? 0 : r;
    }

    /// <summary>
    /// The caption size that fits <paramref name="caption"/> in a cap <paramref name="capWidth"/> wide:
    /// the button size, shrunk a point at a time to <see cref="CapMinFontSize"/>; the ellipsis only past that.
    /// </summary>
    internal static float CapFontSize(string? caption, float capWidth = KeyCapWidth)
    {
        float size = MenuPanelLayout.HeaderButtonFontSize;
        string c = caption ?? "";
        if (c.Length == 0) return size;
        float room = Math.Max(1f, capWidth - CellLayout.ButtonPad);
        while (size > CapMinFontSize && CellLayout.EstimateTextWidth(c, size) > room) size -= 1f;
        return size;
    }

    // ---- KEYS3 review L1: a short inline marker on a conflict or reserved row ----------------

    internal const string CellMarker = "marker";

    internal const string MarkerNotActive = "Not active";

    internal const string MarkerReserved = "Reserved";

    internal const float MarkerFontSize = 14f;

    /// <summary><c>Not active</c> for a key that lost its chord, <c>Reserved</c> for one on a reserved chord, else empty.</summary>
    internal static string RowMarker(KeybindsRowModel row) =>
        row.Loses ? MarkerNotActive : row.ReservedText.Length > 0 ? MarkerReserved : "";

    // ---- KEYS3 row: name, two key caps, the reset / clear gutter ------------------------------

    internal const string CellName = "name";
    internal const string CellPrimary = "key";
    internal const string CellSecondary = "key2";

    /// <summary>The empty space a mod row keeps where a game row draws its second cap.</summary>
    internal const string CellSecondarySpace = "key2-space";
    internal const string CellReset = "reset";
    internal const string CellClear = "clear";

    /// <summary>One line, like the game's rows.</summary>
    internal const float RowHeight = 38f;

    internal const string CellGutter = "gutter";

    /// <summary>A key cap's width (the game's two caps are equal and side by side, one cell gap apart).</summary>
    internal const float KeyCapWidth = 150f;

    internal const float MinNameWidth = 120f;

    /// <summary>
    /// KEYS3 polish (user report 2026-09-30: "I dont understand what the &lt;&lt; and x are for"): reset is
    /// the game's own reset icon when borrowed (else a small <b>Reset</b> button), and clear is a small
    /// <b>Unbind</b> button. Both explain themselves on hover in the description column.
    /// </summary>
    internal const string ResetCaption = "Reset";

    internal const string UnbindCaption2 = "Unbind";

    /// <summary>A disabled reset icon is drawn at this share of its alpha.</summary>
    internal const float DisabledIconAlpha = 0.35f;

    internal static float ResetCellWidth(bool resetIcon) => resetIcon ? GutterCellWidth : ButtonWidth(ResetCaption);

    internal static float UnbindCellWidth => ButtonWidth(UnbindCaption2);

    /// <summary>Reset and Unbind with the cell gap between them: what a read-only row keeps empty.</summary>
    internal static float GutterWidthFor(bool resetIcon) => ResetCellWidth(resetIcon) + CellLayout.Gap + UnbindCellWidth;

    internal static float GutterWidth => GutterWidthFor(resetIcon: false);

    /// <summary>The reset icon is drawn when the game's <c>Icon_Reset</c> was borrowed for this build.</summary>
    internal static bool ResetIconBorrowed => KeybindArtLadder.Current.ResetIcon.Borrowed;

    internal const string HintResetPrefix = "Reset to default: ";
    internal const string HintAlreadyDefault = "Already the default.";
    internal const string HintUnbind = "Unbind this key (no key).";
    internal const string HintAlreadyUnbound = "Already unbound (no key).";
    internal const string HintReadOnly = "This key can't be changed here.";

    /// <summary>
    /// KEYS3 polish: what hovering the gutter's <paramref name="cell"/> (<see cref="CellReset"/> or
    /// <see cref="CellClear"/>) explains in the description column, including why it is dimmed.
    /// </summary>
    internal static string GutterHint(KeybindsRowModel row, string cell, bool editing = false)
    {
        if (row == null) return "";
        if (!row.Editable) return HintReadOnly;
        if (string.Equals(cell, CellReset, StringComparison.Ordinal))
        {
            // User report 2026-10-10: during a change the arrow is live; on a key already at its default it
            // drops the change, which leaves the default.
            if (editing && !row.CanReset) return HintCancelToDefault(row.IsGame ? row.GameDefaultText : row.Claim.DefaultChord == null ? "" : DefaultTextOf(row));
            if (row.CanReset && row.IsGame) return HintResetPrefix + row.GameDefaultText + ".";
            if (row.CanReset) return HintResetPrefix + CapCaption(DefaultTextOf(row)) + ".";

            if (row.IsGame) return HintAlreadyDefault;
            return row.Claim.DefaultChord == null ? "This key has no default to reset to." : HintAlreadyDefault;
        }

        if (string.Equals(cell, CellClear, StringComparison.Ordinal))
            return row.CanClear ? HintUnbind : HintAlreadyUnbound;
        return "";
    }

    private static string DefaultTextOf(KeybindsRowModel row) =>
        row.Claim.DefaultText.Length > 0 ? row.Claim.DefaultText
        : row.Claim.DefaultChord is { } c && c.IsBound ? c.DisplayText : "no key";

    /// <summary>The reset arrow's hint during a change to a key that is at its default.</summary>
    internal static string HintCancelToDefault(string defaultText) =>
        string.IsNullOrWhiteSpace(defaultText) ? "Cancel this change." : "Cancel this change and keep the default, " + CapCaption(defaultText) + ".";

    /// <summary>The category heading: the game's larger heading type.</summary>
    internal const float CategoryFontSize = 22f;

    internal const float CategoryRowHeight = 40f;

    // ---- the group headers and the spacing hierarchy (requested 2026-09-30) -----------------

    internal const string ModGroupTitle = "Mod keys";
    internal const string GameGroupTitle = "Game keys";

    /// <summary>The group title's size: larger than a category heading.</summary>
    internal const float GroupFontSize = 28f;

    /// <summary>The space above a group header: a row of its own (live 2026-09-30: see <see cref="GroupHeaderHeight"/>).</summary>
    internal const float GroupSpaceAbove = 30f;

    /// <summary>
    /// The group title's own row, the title centered in it. Live 2026-09-30: with the space folded into
    /// the title row and the title bottom-aligned, the runtime gives a bottom-aligned cell one short line
    /// (<see cref="BottomCellHeight"/>), too short for 28 px type, so the title was not drawn at all and
    /// the band showed empty. The space is now a row of its own and the title fills its row.
    /// </summary>
    internal const float GroupHeaderHeight = 44f;

    /// <summary>The runtime's height for a full-height text cell asked to sit at the bottom of a taller row (UnityCellRow.PlaceCells).</summary>
    internal const float BottomCellHeight = UnityUiRuntimeMetrics.RowHeight * 0.75f;

    /// <summary>One line of <paramref name="fontSize"/> type needs about this much height (TMP's line height).</summary>
    internal const float LineHeightFactor = 1.3f;

    /// <summary>
    /// Whether a one-line text cell of <paramref name="fontSize"/> is drawn in a row <paramref name="rowHeight"/>
    /// tall, mirroring UnityCellRow.PlaceCells: a bottom-aligned cell is at most <see cref="BottomCellHeight"/>
    /// tall, any other the row's height; TMP truncates a line that does not fit, drawing nothing.
    /// </summary>
    internal static bool TitleFits(float fontSize, float rowHeight, CellVAlign vAlign)
    {
        float cell = vAlign == CellVAlign.Bottom ? Math.Min(rowHeight, BottomCellHeight) : rowHeight;
        return fontSize * LineHeightFactor <= cell + 0.01f;
    }

    /// <summary>The extra space above each mod heading, less than a group's and more than a row gap.</summary>
    internal const float ModSpaceAbove = 12f;

    /// <summary>A mod heading: a category heading plus <see cref="ModSpaceAbove"/>.</summary>
    internal const float ModHeadingHeight = CategoryRowHeight + ModSpaceAbove;

    internal const float DescriptionHeadingFontSize = 22f;

    /// <summary>The ornament under the description heading.</summary>
    internal const float OrnamentHeight = 18f;

    /// <summary>
    /// KEYS3: a key row's cells. The name fills the left; two equal key caps sit at the right (primary,
    /// then secondary), then the gutter: reset (<c>«</c>) and clear (<c>×</c>) for a key this tab can
    /// write, else an equal spacer so every row's caps line up. The row is one line high.
    /// <paramref name="primaryText"/> is what the primary cap shows now (the chord, <c>press a key…</c>
    /// while capturing, or the pending chord). <paramref name="current"/> brightens the name.
    /// </summary>
    internal static List<CellSpec> RowCells(KeybindsRowModel row, string primaryText, bool current) =>
        RowCells(row, primaryText, current, ResetIconBorrowed);

    internal static List<CellSpec> RowCells(KeybindsRowModel row, string primaryText, bool current, bool resetIcon)
    {
        var nameTone = row.Loses || row.ReservedText.Length > 0 ? CellTone.Warn : current ? CellTone.Normal : CellTone.Muted;
        string marker = RowMarker(row);
        string primary = string.Equals(primaryText, CapturingText, StringComparison.Ordinal) ? primaryText : CapCaption(primaryText);
        string secondary = CapCaption(row.SecondaryText);
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Text, CellName).With(text: row.Name, width: CellWidth.Fill(MinNameWidth), wrap: CellWrap.Ellipsis,
                tone: nameTone, minWidth: MinNameWidth),
            // Review L1: the conflict is visible without hovering; hidden (no width) when there is none.
            new CellSpec(CellKind.Muted, CellMarker).With(text: marker, align: CellAlign.Right, tone: CellTone.Warn,
                fontSize: MarkerFontSize),
            new CellSpec(CellKind.Button, CellPrimary).With(text: primary, width: CellWidth.Px(KeyCapWidth),
                align: CellAlign.Right, tone: row.PrimaryTone, skin: CellSkin.KeyCap, minWidth: KeyCapWidth,
                fontSize: CapFontSize(primary)),
            // Decided 2026-09-30: a mod key has one chord, so its row has no second cap (an empty,
            // unclickable cap confused the player). The column stays as empty space, so every row's
            // primary cap and gutter line up with the game rows' (second keys for mod binds: roadmap).
            row.SecondaryEnabled
                ? new CellSpec(CellKind.Button, CellSecondary).With(text: secondary, width: CellWidth.Px(KeyCapWidth),
                    align: CellAlign.Right, tone: row.SecondaryTone, skin: CellSkin.KeyCap, minWidth: KeyCapWidth,
                    fontSize: CapFontSize(secondary))
                : new CellSpec(CellKind.Muted, CellSecondarySpace).With(text: "", width: CellWidth.Px(KeyCapWidth),
                    align: CellAlign.Right, minWidth: KeyCapWidth),
        };
        cells[1].Visible = marker.Length > 0;
        if (row.HasGutter)
        {
            float resetWidth = ResetCellWidth(resetIcon);
            var reset = new CellSpec(CellKind.Button, CellReset).With(text: resetIcon ? "" : ResetCaption,
                width: CellWidth.Px(resetWidth), align: CellAlign.Right, minWidth: resetWidth,
                tone: row.CanReset ? CellTone.Normal : CellTone.Disabled, skin: resetIcon ? CellSkin.ResetIcon : CellSkin.Kit,
                fontSize: MarkerFontSize);
            reset.Hover = true;
            cells.Add(reset);
            var clear = new CellSpec(CellKind.Button, CellClear).With(text: UnbindCaption2,
                width: CellWidth.Px(UnbindCellWidth), align: CellAlign.Right, minWidth: UnbindCellWidth,
                tone: row.CanClear ? CellTone.Normal : CellTone.Disabled, fontSize: MarkerFontSize);
            clear.Hover = true;
            cells.Add(clear);
        }
        else
        {
            // An empty cell as wide as the gutter's two buttons and the gap between them, so a read-only
            // row's caps line up with an editable row's (a spacer would take no width here: the name fills).
            float gutter = GutterWidthFor(resetIcon);
            cells.Add(new CellSpec(CellKind.Muted, CellGutter).With(text: "", width: CellWidth.Px(gutter),
                align: CellAlign.Right, minWidth: gutter));
        }

        return cells;
    }

    /// <summary>
    /// KEYS3: the description column for <paramref name="row"/> (null: the intro).
    /// <paramref name="status"/> is the edit bar's status while this row is being changed (a refusal,
    /// the capture prompt, the preview); <paramref name="note"/> is a one-off note for this row (a game
    /// key cap's read-only note, a not-editable reason).
    /// </summary>
    internal static KeybindsDescription Describe(KeybindsRowModel? row, string source, string status = "", string note = "", string hint = "", bool warnStatus = false)
    {
        if (row == null) return KeybindsDescription.Empty;
        var warning = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(status)) warning.Add(status.Trim());
        if (row.ReservedText.Length > 0) warning.Add(row.ReservedText);
        if (row.ConflictText.Length > 0) warning.Add(row.ConflictText);
        bool refused = !string.IsNullOrWhiteSpace(status) && status.TrimEnd().EndsWith(StillWaitingSuffix, StringComparison.Ordinal);
        string tone = warning.Count == 0 ? ""
            : row.Loses || row.ReservedText.Length > 0 || refused || warnStatus ? "warn"
            : "muted";

        var info = new List<string>(5);
        if (!string.IsNullOrWhiteSpace(note)) info.Add(note.Trim());
        if (row.DecisionNote.Length > 0) info.Add(row.DecisionNote);
        if (row.IsGame && row.Claim.Mode.Length > 0) info.Add("Works only " + row.Claim.Mode + ".");
        if (row.IsGame && string.Equals(row.Claim.Category, GameKeybindCatalog.Debug, StringComparison.Ordinal))
            info.Add(DebugKeyNote);
        if (row.IsInfo && row.Claim.NotEditableReason.Length > 0) info.Add(row.Claim.NotEditableReason);
        if (row.Claim.Scope == KeybindScope.Menu) info.Add(MenuScopeNote);
        else if (row.Claim.Scope == KeybindScope.Photo) info.Add(PhotoScopeNote);
        else if (row.Claim.Scope == KeybindScope.Map) info.Add(MapScopeNote);
        else if (row.Claim.Scope == KeybindScope.Chat) info.Add(ChatScopeNote);
        else if (row.Claim.Scope == KeybindScope.Window) info.Add(WindowScopeNote);
        if (row.Claim.Source == KeybindSource.Cfg)
        {
            info.Add(FromConfigFile);
            if (!row.Claim.Editable && row.Claim.NotEditableReason.Length > 0 && !info.Contains(row.Claim.NotEditableReason))
                info.Add(row.Claim.NotEditableReason);
        }

        if (row.Changed)
            info.Add(ModSettingsLayout.ChangedFromPrefix + (row.Claim.DefaultText.Length == 0 ? "(empty)" : row.Claim.DefaultText) + ".");
        return new KeybindsDescription(row.Id, row.Name, row.Description, string.Join(" ", warning), tone, string.Join(" ", info), source, hint);
    }

    /// <summary>KEYS4: the refusal for a capture of modifiers alone (<c>Shift</c>).</summary>
    internal const string ModifiersOnlyReason = "Ctrl, Shift and Alt alone can't be a key; hold them with another key (for example F9 + Shift).";

    /// <summary>KEYS4: the description column's note on a menu key (the workspace's back and forward).</summary>
    internal const string MenuScopeNote = "Works only while the Mods menu is open, so it never clashes with a gameplay key.";

    /// <summary>HUB1: the note on a window key (the tab-history keys): live in play too, so clashes are checked.</summary>
    internal const string WindowScopeNote = "Works while a window with tabs is under the pointer or holds the input, in play too, so it is checked against gameplay keys and the game's own keys. When a game key has the same key, the game's key wins, except while the window holds the input: the game's keys are blocked then, so this key works and the game's action does not run. A mod's key on the same key always wins over it.";

    internal const string PhotoScopeNote = "Works only in photo mode, which holds your character, so it never clashes with a gameplay key.";

    /// <summary>MAPTP1: the description column's note on a map key (<c>ctx.Keybinds.RegisterInMap</c>).</summary>
    internal const string MapScopeNote = "Works only while the map is open, so it never clashes with a gameplay key.";

    /// <summary>CHAT2: a chat bind's note in the Keybinds tab.</summary>
    internal const string ChatScopeNote = "Works only while typing in chat, so it never clashes with a gameplay key.";

    /// <summary>KEYS4: the description column's note on a game debug key.</summary>
    internal const string DebugKeyNote = "A debug key. The game's debug menu is off for players, so it never conflicts with a mod key.";

    /// <summary>The tail <see cref="StillWaiting"/> adds after a refused key.</summary>
    internal const string StillWaitingSuffix = "Press another key, or Escape to cancel.";

    internal const string SearchCaption = "Search keys";

    /// <summary>KEYS3 polish: the toolbar search's floor; it grows into what the buttons leave.</summary>
    internal const float MinSearchWidth = 140f;
    internal const string JumpToModButton = "Jump to mod…";

    // ---- the toolbar's groups (requested 2026-09-30) ---------------------------------------

    /// <summary>The filter group's lead-in, so All / Conflicts N / Changed read as one three-way choice.</summary>
    internal const string ShowCaption = "Show";

    /// <summary>The section jumps, as quiet link-style captions with a down arrow (U+2193, in the game font's WGL4 set).</summary>
    internal const string ModJumpLink = "\u2193 Mod keys";

    internal const string GameJumpLink = "\u2193 Game keys";

    /// <summary>The mod picker, drawn as a bar with a chevron (U+25BC, the sort indicator the kit already draws; the key-cap sprite, like the game's selectors).</summary>
    internal const string JumpToModPicker = "Jump to mod  \u25BC";

    internal const string CellShow = "show";
    internal const string CellFilterGap = "filter-gap";
    internal const string CellJumpGap = "jump-gap";

    /// <summary>The space that separates two toolbar groups.</summary>
    internal const float GroupGap = 14f;

    /// <summary>
    /// The toolbar's cells, in three visibly separate groups after the search: the filter (a <b>Show</b>
    /// lead-in and three joined choices, the chosen one in the kit's selected accent, the others muted),
    /// the section jumps (quiet muted captions with a down arrow; the current section's is bright), and
    /// the mod picker (a bar with a chevron). On a narrow pane the jumps drop first (Mod keys, Game keys),
    /// then Changed, then the Show lead-in; the picker and the search never drop.
    /// <para>Built from existing cell kinds only; the true segmented, link and dropdown looks need runtime
    /// cell skins (see docs/mods-menu.md, Keybinds tab: runtime requests).</para>
    /// </summary>
    internal static List<CellSpec> ToolbarCells(string query, KeybindsFilter filter, int conflicts, string currentJump)
    {
        CellSpec Filter(string key, string caption, KeybindsFilter f) =>
            new CellSpec(CellKind.Button, key).With(text: caption, width: CellWidth.Px(ButtonWidth(caption)),
                tone: filter == f ? CellTone.Gold : CellTone.Muted);
        CellSpec Jump(string key, string caption, string target) =>
            new CellSpec(CellKind.Button, key).With(text: caption, width: CellWidth.Px(ButtonWidth(caption)),
                tone: string.Equals(currentJump, target, StringComparison.OrdinalIgnoreCase) ? CellTone.Normal : CellTone.Muted,
                fontSize: MarkerFontSize);
        CellSpec Gap(string key) =>
            new CellSpec(CellKind.Muted, key).With(text: "", width: CellWidth.Px(GroupGap), minWidth: GroupGap);
        static CellSpec Drop(CellSpec c, int order)
        {
            c.Optional = true;
            c.DropOrder = order;
            return c;
        }

        return new List<CellSpec>
        {
            new CellSpec(CellKind.Input, KeybindsPane.CellSearch).With(text: query ?? "", placeholder: SearchCaption,
                width: CellWidth.Fill(MinSearchWidth), minWidth: MinSearchWidth),
            Drop(Gap(CellFilterGap), 5),
            Drop(new CellSpec(CellKind.Muted, CellShow).With(text: ShowCaption, fontSize: MarkerFontSize, tone: CellTone.Muted,
                minWidth: CellLayout.EstimateTextWidth(ShowCaption, MarkerFontSize) + 8f), 4),
            Filter(KeybindsPane.CellAll, AllCaption, KeybindsFilter.All),
            Filter(KeybindsPane.CellConflicts, ConflictsCaption(conflicts), KeybindsFilter.Conflicts),
            Drop(Filter(KeybindsPane.CellChanged, ChangedCaption, KeybindsFilter.Changed), 3),
            Drop(Gap(CellJumpGap), 2),
            Drop(Jump(KeybindsPane.CellModJump, ModJumpLink, KeybindsPane.JumpMods), 1),
            Drop(Jump(KeybindsPane.CellGameJump, GameJumpLink, KeybindsPane.JumpGame), 2),
            new CellSpec(CellKind.Button, KeybindsPane.CellJumpToMod).With(text: JumpToModPicker,
                width: CellWidth.Px(ButtonWidth(JumpToModPicker) + 16f), minWidth: ButtonWidth(JumpToModPicker) + 16f,
                skin: CellSkin.KeyCap),
        };
    }

    /// <summary>The heading icon that opens a mod's page (the kit's link glyph; U+00BB is in the game font).</summary>
    internal const string OpenModPageGlyph = "\u00BB";

    internal const string OpenModPageHint = "Open this mod's page.";

    /// <summary>
    /// A mod heading's title width: the caption's estimate at the heading size with the same slack the
    /// kit gives measured captions, so the page icon sits right after the name without cutting it.
    /// </summary>
    internal static float HeadingTitleWidth(string title) =>
        CellLayout.EstimateTextWidth(title ?? "", CategoryFontSize) * HeadingTitleSlack + 2f;

    /// <summary>
    /// Live 2026-09-30: the kit's 1.3 slack (and a 120 px floor) left short names like "Progenitor" far from
    /// their page icon. A smaller slack keeps the glyph about one space after the name; the estimate is
    /// not a measurement, so a name much wider than its estimate could still be cut (runtime request:
    /// measure the caption and place the glyph after it).
    /// </summary>
    internal const float HeadingTitleSlack = 1.15f;
    internal const string AllCaption = "All";
    internal const string ChangedCaption = "Changed";
    internal const string GameJumpCaption = "Game keys";
    internal const string ModJumpCaption = "Mod keys";
    internal const string JumpToModCaption = "Jump to mod";
    internal const string JumpToModNone = "Choose a mod";
    internal const string ResetAllCaption = "Reset all mod keys";
    internal const string OpenModPageCaption = "Open mod page";
    internal const string SaveCaption = "Save";
    internal const string UnbindCaption = "Unbind";
    internal const string CancelCaption = "Cancel";
    internal const string CapturingText = "press a key…";
    internal const string ResetGlyph = ModSettingsView.ResetGlyph;
    internal const string ClearGlyph = MenuRowStyle.SearchClearGlyph;
    internal const string NoMatches = "No keys match.";

    internal const string FromConfigFile = "From its config file.";

    /// <summary>The game's Controls page, in words: there is no safe way to open it from here (docs/mods-menu.md#keybinds-tab).</summary>
    internal const string GameControlsWhere =
        "Read-only here. Change these in the game: Esc > Options > Controls > Keyboard & Mouse Bindings.";

    internal const string GameNoteHeading = "Game keys";

    /// <summary>When the Conflicts filter is chosen and nothing clashes.</summary>
    internal const string NoConflicts = "No key conflicts.";

    internal const string CaptureHelp = "Press the new key or chord; a left or right click needs Ctrl, Shift or Alt. Escape cancels.";

    internal const string SwapCaption = "Swap";

    internal const string SwapTitle = "Swap keys?";

    internal const string SwapYes = "Yes, swap";

    /// <summary>
    /// KEYS2: the line at the top of the Conflicts view while any Progenitor key is not active:
    /// <c>3 keys are inactive because of conflicts. Pick new keys to turn them back on.</c> Empty for none.
    /// </summary>
    internal static string ConflictsHeader(int inactive) =>
        inactive <= 0 ? ""
        : inactive == 1 ? "1 key is inactive because of a conflict. Pick a new key to turn it back on."
        : inactive.ToString(CultureInfo.InvariantCulture) + " keys are inactive because of conflicts. Pick new keys to turn them back on.";

    /// <summary>KEYS2: the edit bar after a refused key, while the capture keeps waiting.</summary>
    internal static string StillWaiting(string reason) => reason + " " + StillWaitingSuffix;

    internal static string ConflictsCaption(int n) => "Conflicts " + n.ToString(CultureInfo.InvariantCulture);

    internal static string KeyCount(int n) => n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " key" : " keys");

    internal static string ModSectionId(string pluginId) => ModSectionPrefix + (pluginId ?? "");

    internal const string GroupSectionPrefix = "group:";

    /// <summary>A keybind group's section (<see cref="KeybindGroups"/>): <c>group:&lt;group id&gt;</c>, a jump target.</summary>
    internal static string GroupSectionId(string groupId) => GroupSectionPrefix + (groupId ?? "").Trim();

    internal static bool IsGroupSectionId(string? id) => (id ?? "").StartsWith(GroupSectionPrefix, StringComparison.OrdinalIgnoreCase);

    internal const string InfoRowPrefix = "info:";

    /// <summary>A group's read-only row: <c>info:&lt;owner&gt;/&lt;group&gt;/&lt;id&gt;</c> (never a bind's key).</summary>
    internal static string InfoRowId(string ownerId, string groupId, string id) =>
        InfoRowPrefix + (ownerId ?? "") + "/" + (groupId ?? "") + "/" + (id ?? "");

    /// <summary>A group's read-only row without its own note.</summary>
    internal const string InfoRowDefaultNote = "Fixed: this control can't be rebound.";

    internal static string EditTitle(string owner, string name) => owner + " · " + name;

    /// <summary>KEYS9: after a game action's name in the edit bar while its second slot is being changed.</summary>
    internal const string SecondKeySuffix = " (second key)";

    internal const string EditBarWaiting = "press a key";

    /// <summary>
    /// KEYS3 polish: the edit bar's one short line, <c>&lt;Mod&gt; · &lt;key&gt;: press a key</c> while
    /// waiting, else the chord about to be saved (<c>… : Ctrl+F7</c>, <c>… : no key</c>). Refusals and the
    /// preview sentence go to the description column only.
    /// </summary>
    internal static string EditBarText(string title, bool capturing, KeyChord? pending)
    {
        string what = capturing || pending == null ? EditBarWaiting
            : pending.Value.IsBound ? CapCaption(pending.Value.DisplayText) : "no key";
        return title + ": " + what;
    }

    /// <summary>
    /// The preview line before a save: free, the same key again, or what it would clash with and
    /// whether this key would then be active.
    /// </summary>
    /// <summary>
    /// Requested 2026-10-10: the preview when Save takes the key: <c>W is used by the game (Move Up). Save moves it
    /// to Move Left; Move Up will have no key.</c>
    /// </summary>
    internal static string TakeSentence(KeyChord chord, string forName, IReadOnlyList<(KeybindClaim Holder, string Kept)> from)
    {
        var holders = new List<string>(from.Count);
        var losers = new List<string>(from.Count);
        foreach (var (h, kept) in from)
        {
            holders.Add(KeybindRemapGuard.HolderName(h));
            losers.Add(h.DisplayName + (kept.Length > 0 ? " will keep only " + kept : " will have no key"));
        }

        return chord.DisplayText + " is used by " + AndList(holders) + ". Save moves it to " + forName + "; " + AndList(losers) + ".";
    }

    /// <summary>The note on the row that took a key, after Save: <c>Took W from the game (Move Up), which has no key now.</c></summary>
    internal static string TookNote(KeyChord chord, IReadOnlyList<(KeybindClaim Holder, string Kept)> from)
    {
        var parts = new List<string>(from.Count);
        foreach (var (h, kept) in from)
            parts.Add(KeybindRemapGuard.HolderName(h) + (kept.Length > 0 ? ", which keeps only " + kept : ", which has no key now"));
        return "Took " + chord.DisplayText + " from " + AndList(parts) + ".";
    }

    /// <summary>The note on a row that lost its key: <c>W moved to Move Left; this action has no key now.</c></summary>
    internal static string LostNote(KeyChord chord, string toName, string kept) =>
        chord.DisplayText + " moved to " + toName + (kept.Length > 0 ? "; this action keeps only " + kept + "." : "; this action has no key now.");

    /// <summary>The status when a holder could not be unbound; nothing was changed.</summary>
    internal static string TakeFailed(KeyChord chord, KeybindClaim holder, string why) =>
        "Couldn't take " + chord.DisplayText + " from " + KeybindRemapGuard.HolderName(holder)
        + (string.IsNullOrWhiteSpace(why) ? "" : " (" + why.Trim().TrimEnd('.') + ")") + ". Nothing was changed.";

    private static string AndList(List<string> items) =>
        items.Count <= 1 ? (items.Count == 1 ? items[0] : "")
        : string.Join(", ", items.GetRange(0, items.Count - 1)) + " and " + items[items.Count - 1];

    internal static string PreviewSentence(KeybindClaim claim, KeyChord chord, KeybindVerdict? would)
    {
        string text = chord.IsBound ? chord.DisplayText : "None";
        if (!chord.IsBound) return "Unbind this key: it will do nothing until you pick another.";
        if (claim.Comparable && claim.Chord == chord) return text + " is already this key.";
        if (would == null || !would.InConflict) return text + " is free. Save to use it.";
        if (!would.Active)
        {
            string who = KeybindArbiter.HolderPhrase(would.Winner!) + KeybindArbiter.WhyPhrase(would.Winner!, would.Rule);
            return claim.Source == KeybindSource.Cfg
                ? text + " is used by " + who + ". Both would fire; Progenitor can't stop this mod."
                : text + " is used by " + who + ", so this key would not be active.";
        }

        return text + " is also used by " + KeybindArbiter.OthersNames(would.Others) + "; this key would win and theirs would stop.";
    }

    // ---- sizes: the settings grid's columns, then a two-glyph gutter -------------------------

    /// <summary>The name row: name in the caption column, the key cell in the fixed control column, reset and clear in the gutter.</summary>
    internal const float NameRowHeight = ModSettingsGrid.ControlHeight + 4f;

    internal const float HelpLineHeight = 20f;

    internal const int HelpMaxLines = 6;

    internal const float SectionRowHeight = ModSettingsView.SectionRowHeight;

    internal const float SectionFontSize = ModSettingsView.SectionFontSize;

    internal static float GutterCellWidth => MenuPanelLayout.IconLinkSize;

    /// <summary>A toolbar button sized to its caption (the mod page header's rule).</summary>
    internal static float ButtonWidth(string caption) => HeadBlockLayout.ButtonWidth(caption);

    internal static float HelpRowHeight(string help, float rowWidth)
    {
        var grid = ModSettingsGrid.For(rowWidth);
        int lines = MenuPanelLayout.WrappedLines(help, grid.LabelWidth, ModSettingsGrid.HelpFontSize, HelpMaxLines);
        return lines <= 0 ? 0f : lines * HelpLineHeight + 4f;
    }
}
