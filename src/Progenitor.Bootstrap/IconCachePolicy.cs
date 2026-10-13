using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Progenitor.Bootstrap;

/// <summary>
/// Game-free key, hash, and lifetime rules for the process-lifetime buff-icon cache.
/// Unity <c>Sprite</c> / <c>Texture2D</c> objects live in <c>IconCache</c> (Bootstrap,
/// non-collectible). This type is what tests compile: a Core swap must not
/// <c>Destroy</c> a sprite the next generation will put back on the clone.
///
/// Unity's own contract is <c>hideFlags = HideAndDontSave</c> on objects that must
/// survive scene unload / unused-asset sweep, so runtime-created textures are kept for
/// the process and a script reload does not drop HUD sprites. We do that, with the
/// cache keyed so a plugin PNG change (new SHA-256) replaces the old pair.
/// </summary>
public static class IconCachePolicy
{
    /// <summary>Joins plugin id, relative path, and SHA-256 hex in <see cref="FormatKey"/>.</summary>
    public const char KeySeparator = '\n';

    /// <summary>Used when the definition owner has no BepInEx GUID.</summary>
    public const string UnownedId = "unowned";

    /// <summary>Logged when a cached pair is reused because the plugin asset root is not on disk yet.</summary>
    public const string FromCacheToken = "from cache";

    /// <summary>How a <see cref="IconCacheTable.TryGetOrCreate"/> / lookup resolved.</summary>
    public enum Resolve
    {
        /// <summary>No row, and no bytes to create one.</summary>
        Miss = 0,
        /// <summary>Same key, live texture: return the existing pair.</summary>
        Hit = 1,
        /// <summary>No row; bytes were hashed and a new pair must be created.</summary>
        Create = 2,
        /// <summary>Same key, but the texture is Unity-null; recreate from stored bytes.</summary>
        Recreate = 3,
        /// <summary>Same plugin+path, different SHA-256: destroy the old pair, create the new one.</summary>
        Replace = 4,
    }

    /// <summary>
    /// Cache key: plugin id, normalized relative path, SHA-256 of the PNG bytes.
    /// Bytes-unknown lookup uses <see cref="PluginPathIndex"/> instead.
    /// </summary>
    public static string FormatKey(string? pluginId, string? relativePath, string? sha256Hex)
    {
        return NormalizePluginId(pluginId)
            + KeySeparator
            + NormalizePath(relativePath)
            + KeySeparator
            + (sha256Hex ?? "");
    }

    /// <summary>Plugin + path index used when the PNG bytes are not on disk yet (Core swap re-apply).</summary>
    public static string PluginPathIndex(string? pluginId, string? relativePath) =>
        NormalizePluginId(pluginId) + KeySeparator + NormalizePath(relativePath);

    /// <summary>Lowercase hex SHA-256 of <paramref name="bytes"/>. Empty input hashes the empty buffer.</summary>
    public static string HashBytes(byte[]? bytes)
    {
        bytes ??= Array.Empty<byte>();
        byte[] hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        for (int i = 0; i < hash.Length; i++)
            sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    /// <summary>True when <paramref name="bytes"/> can be decoded into a texture.</summary>
    public static bool CanCreate(byte[]? bytes) => bytes != null && bytes.Length > 0;

    /// <summary>
    /// Split a <see cref="FormatKey"/> result. False when the string has fewer than two separators
    /// (a plugin id itself may contain no newlines; the path must not).
    /// </summary>
    public static bool TryParseKey(string? key, out string pluginId, out string relativePath, out string sha256Hex)
    {
        pluginId = UnownedId;
        relativePath = "";
        sha256Hex = "";
        if (string.IsNullOrEmpty(key)) return false;
        int first = key.IndexOf(KeySeparator);
        if (first < 0) return false;
        int second = key.IndexOf(KeySeparator, first + 1);
        if (second < 0) return false;
        pluginId = key.Substring(0, first);
        if (pluginId.Length == 0) pluginId = UnownedId;
        relativePath = key.Substring(first + 1, second - first - 1);
        sha256Hex = key.Substring(second + 1);
        return true;
    }

    /// <summary>
    /// Recreate when the Unity fake-null check says the texture is gone
    /// (<c>texture == null</c> through the overloaded operator). A live sprite over a
    /// destroyed texture is the white-square HUD (evidence 122).
    /// </summary>
    public static bool MustRecreate(bool textureIsUnityNull) => textureIsUnityNull;

    /// <summary>
    /// Resolution when the caller already knows whether a row exists for this exact key,
    /// whether its texture is dead, whether PNG bytes are in hand, and whether a different
    /// hash is already cached for the same plugin+path.
    /// </summary>
    public static Resolve Decide(
        bool haveEntryForKey,
        bool textureIsUnityNull,
        bool haveBytes,
        bool haveOtherHashForPluginPath)
    {
        if (haveEntryForKey)
        {
            if (textureIsUnityNull) return haveBytes ? Resolve.Recreate : Resolve.Miss;
            return Resolve.Hit;
        }

        if (!haveBytes) return Resolve.Miss;
        if (haveOtherHashForPluginPath) return Resolve.Replace;
        return Resolve.Create;
    }

    /// <summary>Pin-diag line. Grep <c>pin-diag IconCache count=</c>.</summary>
    public static string FormatPinDiag(int count) =>
        "pin-diag IconCache count=" + count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Live client line when the clone is written from a cached sprite (asset root missing).</summary>
    public static string FormatFromCache(string path, string id) =>
        "PrefabRegistry: icon '" + (path ?? "") + "' from cache on '" + (id ?? "") + "'";

    /// <summary>Logged when a cache row's Texture2D failed the Unity null check and was rebuilt from stored bytes.</summary>
    public static string FormatDeadRecreated(string key) =>
        "icon cache entry '" + (key ?? "") + "' had a dead texture; recreated";

    /// <summary>
    /// Trim, forward slashes, lowercase. Lowercase matches the plugin+path index comparer
    /// (<c>OrdinalIgnoreCase</c>) so <c>icons/A.png</c> and <c>icons/a.png</c> are one row
    /// (R26 finding 11: mixed-case paths were a Replace that destroyed the other sprite).
    /// </summary>
    public static string NormalizePath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return "";
        return relativePath.Trim().Replace('\\', '/').ToLowerInvariant();
    }

    /// <summary>
    /// Trim and lowercase; empty / whitespace becomes <see cref="UnownedId"/>. Lowercasing
    /// matters as much as it does for <see cref="NormalizePath"/>: the plugin+path index is
    /// <c>OrdinalIgnoreCase</c>, so the same PNG arriving once as <c>MyPlugin</c> and once as
    /// <c>myplugin</c> used to miss the key table, hit the index under a different old key and
    /// resolve to <c>Replace</c> — retiring, or destroying outright when refs happened to be 0,
    /// the pair the live clone was drawing. Symmetrically <c>ReleasePlugin</c> compared ids
    /// ordinally, so a file removal reported with different casing dropped nothing and marked
    /// nothing pending (R26 finding 11 / R27 finding 14b).
    /// </summary>
    public static string NormalizePluginId(string? pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return UnownedId;
        return pluginId.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Cache owner id: BepInEx GUID when present, otherwise the assembly simple name.
    /// Non-GUID owners must use the same key ReloaderHost uses on file removal.
    /// </summary>
    public static string OwnerId(string? guid, string? assemblyName)
    {
        if (!string.IsNullOrWhiteSpace(guid)) return NormalizePluginId(guid);
        if (!string.IsNullOrWhiteSpace(assemblyName)) return NormalizePluginId(assemblyName);
        return UnownedId;
    }

    /// <summary>
    /// File-removal destroy rule: keep the Unity pair while Content still holds a clone
    /// (refs &gt; 0); drop the row and destroy only when nothing references it.
    /// </summary>
    public static bool ShouldDestroyOnPluginRelease(int refs) => refs <= 0;

    /// <summary>
    /// Replace rule (the plugin PNG changed, so plugin+path now hashes to a new key):
    /// destroy the superseded pair only when nothing references it. A live clone or buff
    /// instance still draws the old Sprite, and destroying it under them is the black
    /// square of evidence 112 / 131. A referenced pair is retired instead — kept alive
    /// until its last holder lets go (<see cref="RetiredIconTable.RetireReplaced"/>) or
    /// <see cref="IconCache.ClearAll"/> at process exit.
    /// </summary>
    public static bool ShouldDestroyReplaced(int refs) => refs <= 0;

    /// <summary>
    /// <b>Retire is a replacement rule, never a generation rule (R29 finding 10).</b> A pair may
    /// only be parked in <see cref="RetiredIconTable"/> when the key's <em>live</em> row was
    /// replaced — a new PNG for the same plugin+path, so plugin+path hashes to a different
    /// canonical key and the old row leaves <c>IconCacheTable</c>. A Core swap is not that: the
    /// cache deliberately hands the same Sprite and Texture2D object back to every generation
    /// for an unchanged PNG (<see cref="Resolve.Hit"/> / <see cref="Resolve.Recreate"/>, no
    /// <c>LastReplacedKey</c>), so a swap must retire nothing at all. Without this stated as a
    /// rule the table was a per-generation accumulator with no ceiling: every reload of a build
    /// whose icon had changed parked one more <c>HideAndDontSave</c> pair whose only reference
    /// belonged to a Core generation that had already unloaded, and no refcount ever reached
    /// zero to free it.
    /// </summary>
    public static bool ShouldRetireOnReplace(bool liveRowReplaced, int refs) =>
        liveRowReplaced && ShouldRetire(refs);

    /// <summary>Logged when a replaced pair is kept alive because a live clone still holds it.</summary>
    public static string FormatReplacedKept(string key, int refs) =>
        "icon cache entry '" + (key ?? "") + "' replaced but kept alive; "
        + refs.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " live reference(s)";

    /// <summary>
    /// How many superseded pairs may be kept alive at once. Iterating on an icon changes the
    /// PNG's SHA-256 on every save, so every save produced a new canonical key, a
    /// <see cref="Resolve.Replace"/>, and one more retired <c>Sprite</c>+<c>Texture2D</c> pair
    /// held to process exit — with <c>HideFlags.HideAndDontSave</c> Unity's unused-asset sweep
    /// will not reclaim them either (R27 finding 14a).
    ///
    /// <b>The cap only reclaims what nothing is drawing.</b> It used to pop the oldest row
    /// regardless of its reference count — but a row is in this table *because*
    /// <see cref="ShouldRetire"/> said <c>refs &gt; 0</c>, i.e. a live clone or buff instance is
    /// drawing it, so the cap destroyed exactly the pairs retire-not-destroy exists to protect
    /// and reintroduced the black square at a bound (R28 finding 4a). Now overflow evicts only
    /// zero-reference rows (oldest first); if every row is still referenced the table exceeds
    /// the cap and says so once (<see cref="FormatRetiredOverCap"/>). A leak that logs beats a
    /// sprite destroyed under the HUD.
    ///
    /// <b>That made the cap a report rather than a bound (R29 finding 10), so the bound now
    /// comes from the two rules above it</b> and this constant is only the backstop:
    /// <see cref="ShouldRetireOnReplace"/> keeps a Core swap from retiring anything at all, and
    /// <see cref="RetiredIconTable.RetireReplaced"/> hands the superseded holder on so the row
    /// retired by the <em>previous</em> replacement of the same plugin+path drops its reference
    /// and is destroyed the moment that count reaches zero. A dev loop that re-saves one icon
    /// therefore holds exactly one retired row however many generations it runs, instead of one
    /// per save. 64 rows is the ceiling for the pathological rest (many distinct icons replaced
    /// while genuinely on screen); beyond it the oldest zero-reference row is destroyed, and if
    /// there is none the table still refuses to destroy a referenced pair and warns once per
    /// episode.
    /// </summary>
    public const int MaxRetiredPairs = 64;

    /// <summary>Retire (keep alive) rather than destroy only while something still references it.</summary>
    public static bool ShouldRetire(int refs) => refs > 0;

    /// <summary>A retired pair is freeable once its last reference is released.</summary>
    public static bool ShouldFreeRetired(int refs) => refs <= 0;

    /// <summary>Logged when a retired pair's last reference went away and it was destroyed.</summary>
    public static string FormatRetiredFreed(string key) =>
        "icon cache entry '" + (key ?? "") + "' retired pair released; destroyed";

    /// <summary>
    /// Logged when a replacement handed the superseded holder on: the pair retired by the
    /// previous replacement of the same plugin+path lost that reference, and
    /// <paramref name="destroyed"/> says whether that was its last one. Grep
    /// <c>icon cache handed a superseded holder on</c>.
    /// </summary>
    public static string FormatRetiredHandedOn(string pluginPathIndex, bool destroyed) =>
        "icon cache handed a superseded holder on for '"
        + (pluginPathIndex ?? "").Replace(KeySeparator, '|')
        + "'; the previously retired pair "
        + (destroyed ? "had no references left and was destroyed." : "is still referenced and was kept.");

    /// <summary>
    /// Logged when the retired list hit its cap and the oldest <em>unreferenced</em> pairs were
    /// destroyed. Referenced rows are never counted here — see <see cref="MaxRetiredPairs"/>.
    /// </summary>
    public static string FormatRetiredCapped(int destroyed, int cap) =>
        "icon cache retired list over cap; destroyed "
        + destroyed.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " oldest unreferenced pair(s) (cap " + cap.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";

    /// <summary>
    /// Logged once per overflow episode when the retired list is over its cap and every row is
    /// still referenced, so nothing can be freed without destroying a sprite a HUD is drawing.
    /// Grep <c>retired list over cap and every pair is still referenced</c>.
    /// </summary>
    public static string FormatRetiredOverCap(int held, int cap) =>
        "icon cache retired list over cap and every pair is still referenced; holding "
        + held.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " (cap " + cap.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "). Nothing was destroyed: a referenced pair is still on screen.";

    /// <summary><c>pin-diag IconCache count=N retired=M</c>.</summary>
    public static string FormatPinDiag(int count, int retired) =>
        FormatPinDiag(count) + " retired="
        + retired.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Game-free bookkeeping for superseded icon pairs that a live clone still references.
/// <c>IconCache</c> stores the Unity <c>Sprite</c>+<c>Texture2D</c> pair as the opaque
/// <c>payload</c>; this type owns the key, the reference count, and the two rules that make
/// the list finite: a pair is destroyed as soon as its last reference is released, and the
/// list is capped at <see cref="IconCachePolicy.MaxRetiredPairs"/> (R27 finding 14a).
/// Insertion order is retirement order, so overflow drops the oldest first.
///
/// Two R28 finding 4 rules make it safe as well as finite:
/// <list type="number">
/// <item><b>One row per key.</b> <see cref="Retire"/> merges into an existing row instead of
/// appending a second one. The same canonical key can be retired twice in an ordinary dev loop
/// — save icon v2 (v1 retired), save it back to v1 (a fresh v1 pair is created), save v2 again
/// (v1 retired a second time). With two rows, <see cref="ReleaseRef"/> scanned from the front
/// and decremented the <em>older</em> one every time: it reached zero first and its pair was
/// destroyed while its real holders were still drawing it, and the newer row's count never
/// dropped at all. Merged rows sum the counts and keep every payload, so nothing is freed until
/// the last holder of that key is gone.</item>
/// <item><b>The cap never destroys a referenced pair.</b> <see cref="TakeOverflow"/> skips rows
/// with <c>Refs &gt; 0</c>; when nothing is releasable the table simply exceeds the cap and
/// <see cref="NoteCapExceeded"/> tells the caller to log it once.</item>
/// </list>
///
/// R29 finding 10 pointed out that those two rules together left the table with no ceiling at
/// all: every row over the cap being referenced is the <em>normal</em> state of a dev loop, so
/// the cap could free nothing and <c>_rows</c> grew for the life of the process. The missing
/// piece was never the cap — it was that nothing ever released the retired references, because
/// the holder of a superseded pair is the very thing the replacement re-points at the new one.
/// <see cref="RetireReplaced"/> makes that hand-off explicit: one replacement of a plugin+path
/// retires the pair it supersedes <b>and</b> drops one reference from the pair retired by the
/// previous replacement of that same plugin+path, which is destroyed the moment its count
/// reaches zero. Re-saving one icon 200 times therefore holds one retired row, not 200.
/// </summary>
public sealed class RetiredIconTable
{
    private sealed class Row
    {
        public string Key = "";

        /// <summary>
        /// <c>IconCachePolicy.PluginPathIndex</c> of the row, so a later replacement of the same
        /// plugin+path can find the pair it supersedes. Empty for rows retired through the
        /// index-less <see cref="Retire(string,int,object)"/> overload.
        /// </summary>
        public string Index = "";

        public int Refs;
        public readonly List<object?> Payloads = new();
    }

    private readonly List<Row> _rows = new();
    private bool _capLogged;

    /// <summary>How many retired keys are still held (not how many Unity pairs — see <see cref="PairCount"/>).</summary>
    public int Count => _rows.Count;

    /// <summary>
    /// How many Unity pairs are held across all rows. Larger than <see cref="Count"/> only when
    /// one key was retired more than once (the reverted-icon case in the type doc).
    /// </summary>
    public int PairCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _rows.Count; i++) n += _rows[i].Payloads.Count;
            return n;
        }
    }

    /// <summary>
    /// Keep a superseded pair alive. False when nothing references it — the caller must
    /// destroy it outright instead of parking it here forever. A second retire of the same key
    /// merges: reference counts add and the extra payload joins the row, so neither pair can be
    /// freed while the other's holders are still drawing it.
    /// </summary>
    public bool Retire(string? key, int refs, object? payload) =>
        Retire(key, pluginPathIndex: null, refs, payload);

    /// <summary>
    /// As <see cref="Retire(string,int,object)"/>, recording the plugin+path index so
    /// <see cref="RetireReplaced"/> can find the pair a later replacement supersedes.
    /// </summary>
    public bool Retire(string? key, string? pluginPathIndex, int refs, object? payload)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (!IconCachePolicy.ShouldRetire(refs)) return false;
        var row = Find(key!);
        if (row == null)
        {
            row = new Row { Key = key!, Index = pluginPathIndex ?? "", Refs = refs };
            _rows.Add(row);
        }
        else
        {
            row.Refs += refs;
            if (row.Index.Length == 0 && !string.IsNullOrEmpty(pluginPathIndex))
                row.Index = pluginPathIndex!;
        }

        if (payload != null && !ContainsPayload(row, payload))
            row.Payloads.Add(payload);
        return true;
    }

    /// <summary>
    /// One replacement step for <paramref name="pluginPathIndex"/> (R29 finding 10). Two things
    /// happen, in this order:
    /// <list type="number">
    /// <item>The pair retired by the <em>previous</em> replacement of this plugin+path drops one
    /// reference. That reference belonged to whatever was drawing the superseded sprite, and a
    /// replacement is precisely the moment that holder is re-pointed at the new pair, so keeping
    /// it would be counting a holder that no longer exists. If the count reaches zero the row is
    /// removed and its payloads come back in <paramref name="freed"/> for the caller to destroy —
    /// immediately, exactly as <see cref="ReleaseRef"/> does.</item>
    /// <item><paramref name="key"/> is retired under this index with <paramref name="refs"/>,
    /// the same merge rules as <see cref="Retire(string,int,object)"/>.</item>
    /// </list>
    /// A row whose count does not reach zero is left alone: more than one clone was drawing that
    /// pair and destroying it would be the black square R28 finding 4a is about. The return value
    /// is <see cref="Retire(string,int,object)"/>'s (false when nothing referenced
    /// <paramref name="key"/>, so the caller destroys it outright); the hand-off runs either way.
    /// <paramref name="freed"/> is never null.
    /// </summary>
    public bool RetireReplaced(
        string? pluginPathIndex,
        string? key,
        int refs,
        object? payload,
        out List<object?> freed,
        out bool handedOn)
    {
        freed = new List<object?>();
        handedOn = false;
        if (!string.IsNullOrEmpty(pluginPathIndex))
        {
            // Newest first: the pair this replacement actually supersedes is the most recent
            // row for the index, not the oldest one still hanging around.
            for (int i = _rows.Count - 1; i >= 0; i--)
            {
                var candidate = _rows[i];
                if (!string.Equals(candidate.Index, pluginPathIndex, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))
                    continue;   // the row we are about to merge into is not the one handed on
                handedOn = true;
                if (candidate.Refs > 0) candidate.Refs--;
                if (IconCachePolicy.ShouldFreeRetired(candidate.Refs))
                {
                    freed.AddRange(candidate.Payloads);
                    _rows.RemoveAt(i);
                }
                break;
            }
        }

        return Retire(key, pluginPathIndex, refs, payload);
    }

    /// <summary>
    /// Drop one reference to a retired key. True when that was the last one: the row is
    /// removed and <paramref name="freed"/> carries every payload held under that key for the
    /// caller to destroy. <paramref name="freed"/> is never null.
    /// </summary>
    public bool ReleaseRef(string? key, out List<object?> freed)
    {
        freed = new List<object?>();
        if (string.IsNullOrEmpty(key)) return false;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (!string.Equals(_rows[i].Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            if (_rows[i].Refs > 0) _rows[i].Refs--;
            if (!IconCachePolicy.ShouldFreeRetired(_rows[i].Refs)) return false;
            freed.AddRange(_rows[i].Payloads);
            _rows.RemoveAt(i);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Payloads of the oldest <em>unreferenced</em> rows while the table is over
    /// <see cref="IconCachePolicy.MaxRetiredPairs"/>, removed from the table. Empty while the
    /// list is inside the cap <b>and</b> when every row over the cap is still referenced — the
    /// cap is not allowed to destroy a sprite a HUD is drawing (R28 finding 4a). Ask
    /// <see cref="NoteCapExceeded"/> afterwards to find out whether that happened.
    /// </summary>
    public List<object?> TakeOverflow()
    {
        var taken = new List<object?>();
        int i = 0;
        while (_rows.Count > IconCachePolicy.MaxRetiredPairs && i < _rows.Count)
        {
            if (_rows[i].Refs > 0)
            {
                i++;                       // still on screen: keep it and look further down
                continue;
            }
            taken.AddRange(_rows[i].Payloads);
            _rows.RemoveAt(i);
        }
        return taken;
    }

    /// <summary>
    /// True exactly once per overflow episode: the table is over the cap and every remaining row
    /// is still referenced, so <see cref="TakeOverflow"/> could free nothing. Re-arms once the
    /// table is back inside the cap, so a second episode is logged again.
    /// </summary>
    public bool NoteCapExceeded()
    {
        if (_rows.Count <= IconCachePolicy.MaxRetiredPairs)
        {
            _capLogged = false;
            return false;
        }

        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Refs <= 0) return false;   // TakeOverflow can still free something
        }

        if (_capLogged) return false;
        _capLogged = true;
        return true;
    }

    /// <summary>Every payload, removed from the table (process exit).</summary>
    public List<object?> TakeAll()
    {
        var taken = new List<object?>();
        for (int i = 0; i < _rows.Count; i++)
            taken.AddRange(_rows[i].Payloads);
        _rows.Clear();
        _capLogged = false;
        return taken;
    }

    /// <summary>Remaining references for a retired key, or 0 when it is not held.</summary>
    public int RefsOf(string? key)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        var row = Find(key!);
        return row?.Refs ?? 0;
    }

    private Row? Find(string key)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (string.Equals(_rows[i].Key, key, StringComparison.OrdinalIgnoreCase))
                return _rows[i];
        }
        return null;
    }

    private static bool ContainsPayload(Row row, object payload)
    {
        for (int i = 0; i < row.Payloads.Count; i++)
        {
            if (ReferenceEquals(row.Payloads[i], payload)) return true;
            if (payload.Equals(row.Payloads[i])) return true;
        }
        return false;
    }
}

/// <summary>
/// Process-lifetime table of cache rows without Unity types. <c>IconCache</c> keeps the
/// matching Sprite/Texture2D pair; tests drive this directly for hit / miss / release /
/// dead-texture recreate.
/// </summary>
public sealed class IconCacheTable
{
    // Both maps are case-insensitive. _byKey used to be Ordinal while the index was
    // OrdinalIgnoreCase, so a plugin id differing only in case missed the key table, hit the
    // index, and resolved to Replace against a live sprite (R27 finding 14b).
    private readonly Dictionary<string, Row> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _currentByPluginPath = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Row
    {
        public string Key = "";
        public string PluginId = "";
        public string Path = "";
        public string Sha256 = "";
        public byte[] Bytes = Array.Empty<byte>();
        public bool TextureDead;
        public int Refs;
        public bool PendingDestroy;
    }

    /// <summary>How many Sprite/Texture2D pairs this table accounts for.</summary>
    public int Count => _byKey.Count;

    /// <summary>Key replaced by the last <see cref="IconCachePolicy.Resolve.Replace"/>, or null.</summary>
    public string? LastReplacedKey { get; private set; }

    /// <summary>
    /// Retain count the replaced row still had when it was dropped. The caller feeds this to
    /// <see cref="IconCachePolicy.ShouldDestroyReplaced"/>: a pair a live clone still draws
    /// must not be destroyed by the replace.
    /// </summary>
    public int LastReplacedRefs { get; private set; }

    /// <summary>
    /// Insert or reuse a row for these PNG bytes. <paramref name="key"/> is always the
    /// canonical plugin+path+hash key on return when the resolve is not
    /// <see cref="IconCachePolicy.Resolve.Miss"/>.
    /// </summary>
    public IconCachePolicy.Resolve TryGetOrCreate(string? pluginId, string? relativePath, byte[]? bytes, out string key)
    {
        LastReplacedKey = null;
        LastReplacedRefs = 0;
        key = "";
        if (!IconCachePolicy.CanCreate(bytes))
            return IconCachePolicy.Resolve.Miss;

        string id = IconCachePolicy.NormalizePluginId(pluginId);
        string path = IconCachePolicy.NormalizePath(relativePath);
        string sha = IconCachePolicy.HashBytes(bytes);
        key = IconCachePolicy.FormatKey(id, path, sha);
        string index = IconCachePolicy.PluginPathIndex(id, path);

        if (_byKey.TryGetValue(key, out var existing))
        {
            existing.Bytes = bytes!;
            if (existing.TextureDead)
            {
                existing.TextureDead = false;
                return IconCachePolicy.Resolve.Recreate;
            }

            return IconCachePolicy.Resolve.Hit;
        }

        bool replacing = _currentByPluginPath.TryGetValue(index, out var oldKey)
            && !string.Equals(oldKey, key, StringComparison.OrdinalIgnoreCase);
        if (replacing)
        {
            LastReplacedKey = oldKey;
            LastReplacedRefs = _byKey.TryGetValue(oldKey!, out var oldRow) ? oldRow.Refs : 0;
            _byKey.Remove(oldKey!);
        }

        _byKey[key] = new Row
        {
            Key = key,
            PluginId = id,
            Path = path,
            Sha256 = sha,
            Bytes = CopyBytes(bytes!),
            TextureDead = false,
            Refs = 0,
            PendingDestroy = false,
        };
        _currentByPluginPath[index] = key;
        return replacing ? IconCachePolicy.Resolve.Replace : IconCachePolicy.Resolve.Create;
    }

    /// <summary>
    /// Lookup by plugin + path when the PNG is not on disk (Core swap: plugin file not
    /// in <c>ByPath</c> yet). False on miss. <paramref name="textureDead"/> is set when the
    /// row exists but its texture failed the Unity null check — caller recreates from
    /// <paramref name="bytes"/>.
    /// </summary>
    public bool TryGetWithoutBytes(
        string? pluginId,
        string? relativePath,
        out string key,
        out byte[] bytes,
        out bool textureDead)
    {
        key = "";
        bytes = Array.Empty<byte>();
        textureDead = false;
        string index = IconCachePolicy.PluginPathIndex(pluginId, relativePath);
        if (!_currentByPluginPath.TryGetValue(index, out var found))
            return false;
        if (!_byKey.TryGetValue(found, out var row))
        {
            _currentByPluginPath.Remove(index);
            return false;
        }

        key = row.Key;
        bytes = row.Bytes;
        textureDead = row.TextureDead;
        return true;
    }

    /// <summary>Drop one key. True when a row was present.</summary>
    public bool Release(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (!_byKey.TryGetValue(key, out var row)) return false;
        _byKey.Remove(key);
        string index = IconCachePolicy.PluginPathIndex(row.PluginId, row.Path);
        if (_currentByPluginPath.TryGetValue(index, out var current)
            && string.Equals(current, key, StringComparison.OrdinalIgnoreCase))
            _currentByPluginPath.Remove(index);
        return true;
    }

    /// <summary>
    /// File-removed: drop unreferenced rows, mark referenced rows pending so Content's
    /// later <see cref="ReleaseRef"/> destroys them. Returns how many rows were dropped
    /// now (not the pending ones).
    /// </summary>
    public int ReleasePlugin(string? pluginId)
    {
        string id = IconCachePolicy.NormalizePluginId(pluginId);
        var drop = new List<string>();
        foreach (var kv in _byKey)
        {
            if (!string.Equals(kv.Value.PluginId, id, StringComparison.OrdinalIgnoreCase))
                continue;
            if (IconCachePolicy.ShouldDestroyOnPluginRelease(kv.Value.Refs))
                drop.Add(kv.Key);
            else
                kv.Value.PendingDestroy = true;
        }

        for (int i = 0; i < drop.Count; i++)
            Release(drop[i]);
        return drop.Count;
    }

    /// <summary>Content apply: the clone / instances now reference this sprite.</summary>
    public bool Retain(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (!_byKey.TryGetValue(key, out var row)) return false;
        if (row.Refs < int.MaxValue) row.Refs++;
        return true;
    }

    /// <summary>
    /// Content unapply / clone gone. True when the row was dropped (pending destroy
    /// and no remaining refs, or an explicit drop of an unreferenced row).
    /// </summary>
    public bool ReleaseRef(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (!_byKey.TryGetValue(key, out var row)) return false;
        if (row.Refs > 0) row.Refs--;
        if (row.PendingDestroy && row.Refs <= 0)
            return Release(key);
        return false;
    }

    /// <summary>Current retain count, or 0 when the key is missing.</summary>
    public int RefsOf(string? key)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        return _byKey.TryGetValue(key, out var row) ? row.Refs : 0;
    }

    /// <summary>
    /// True when this key still has a live row. Distinct from <c>RefsOf(key) &gt; 0</c>, which
    /// cannot tell "no row" from "a row nothing references": <c>IconCache.Release</c> needs the
    /// difference so one release does not decrement both the live row and a retired row that
    /// happens to share the key (a reverted icon re-creates a key that is also retired).
    /// </summary>
    public bool Contains(string? key) =>
        !string.IsNullOrEmpty(key) && _byKey.ContainsKey(key!);

    /// <summary>True when file-removal ran while a clone still held this key.</summary>
    public bool IsPendingDestroy(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        return _byKey.TryGetValue(key, out var row) && row.PendingDestroy;
    }

    /// <summary>Tests: mark the row's texture dead so the next get recreates.</summary>
    public bool MarkTextureDead(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (!_byKey.TryGetValue(key, out var row)) return false;
        row.TextureDead = true;
        return true;
    }

    /// <summary>Drop every row (process exit / tests).</summary>
    public void Clear()
    {
        _byKey.Clear();
        _currentByPluginPath.Clear();
        LastReplacedKey = null;
        LastReplacedRefs = 0;
    }

    private static byte[] CopyBytes(byte[] src)
    {
        var dst = new byte[src.Length];
        Buffer.BlockCopy(src, 0, dst, 0, src.Length);
        return dst;
    }
}
