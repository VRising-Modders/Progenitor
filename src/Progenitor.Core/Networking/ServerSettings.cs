using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Progenitor.Ui;
#if !PROGENITOR_TESTS
using ProjectM;
using ProjectM.Network;
using Unity.Entities;
#endif

namespace Progenitor.Networking;

/// <summary>The server's response to a settings edit. Value is display text, not TOML.</summary>
public readonly struct ServerSettingResult
{
    public ServerSettingResult(bool success, string value, string error)
    { Success = success; Value = value; Error = error; }
    public bool Success { get; }
    public string Value { get; }
    public string Error { get; }
}

/// <summary>
/// Opt-in administrator edits of synced settings. Register each entry on both sides after
/// ConfigSync registration; track both registration and request handles for plugin unload.
/// Client values change only through ConfigSync. Call from the game thread.
/// </summary>
public static partial class ServerSettings
{
    public const int EventId = 0x50725365; // PrSe
    public const double TimeoutSeconds = 8;
    internal const int MaxPending = 32;
    private static readonly Dictionary<string, Registration> Entries = new(StringComparer.Ordinal);
    private static readonly Dictionary<Guid, Pending> Requests = new();
    private static readonly Dictionary<int, HashSet<string>> PeerRows = new();
    // Every live declaration per row, oldest first; the newest live one applies (two owners of one
    // row — a page and a binding — can come and go independently).
    private static readonly Dictionary<string, List<TextDeclaration>> TextDeclarations = new(StringComparer.Ordinal);
    private static bool _installed;

    /// <summary>
    /// TEXTLIMIT1: declare how long the text setting <paramref name="rowId"/> may be — the author's
    /// <c>MaxLength</c> (null: the default for its kind) and whether it is a text area. The settings page
    /// builder and <c>SettingsCatalog.RegisterSetting</c> call this on both sides; the server checks every
    /// edit against its own declaration (<see cref="TextLimitFor"/>), never a limit the client sends.
    /// Dispose to withdraw it. The newest live declaration of a row applies; withdrawing it falls back
    /// to the one before it.
    /// </summary>
    internal static IDisposable DeclareText(string rowId, int? maxLength, bool multiline)
    {
        if (string.IsNullOrEmpty(rowId)) return Empty.Instance;
        var declaration = new TextDeclaration(rowId, maxLength is int m && m > 0 ? m : null, multiline);
        if (!TextDeclarations.TryGetValue(rowId, out var list)) TextDeclarations[rowId] = list = new List<TextDeclaration>(1);
        list.Add(declaration);
        return declaration;
    }

    /// <summary>TEXTLIMIT1: the declared cap and kind of <paramref name="rowId"/>; a row nobody declared is a single-line field with the default cap.</summary>
    internal static (int? MaxLength, bool Multiline) TextLimitFor(string? rowId) =>
        rowId != null && TextDeclarations.TryGetValue(rowId, out var list) && list.Count > 0
            ? (list[list.Count - 1].MaxLength, list[list.Count - 1].Multiline)
            : (null, false);

    /// <summary>TEXTLIMIT1: the most characters a server-wide edit of <paramref name="rowId"/> takes (<see cref="ServerSettingsLimits.Effective"/>).</summary>
    internal static int EffectiveTextLimit(string? rowId)
    {
        var (max, multiline) = TextLimitFor(rowId);
        return ServerSettingsLimits.Effective(max, multiline);
    }

    /// <summary>TEXTLIMIT1: null when <paramref name="text"/> fits <paramref name="rowId"/>'s limits, else the reason (<see cref="ServerSettingsLimits.Check"/>).</summary>
    internal static string? CheckText(string? rowId, string? text)
    {
        var (max, multiline) = TextLimitFor(rowId);
        return ServerSettingsLimits.Check(text, max, multiline);
    }

    private sealed class TextDeclaration : IDisposable
    {
        internal readonly string RowId;
        internal readonly int? MaxLength;
        internal readonly bool Multiline;
        internal TextDeclaration(string rowId, int? maxLength, bool multiline)
        { RowId = rowId; MaxLength = maxLength; Multiline = multiline; }
        public void Dispose()
        {
            if (!TextDeclarations.TryGetValue(RowId, out var list)) return;
            list.Remove(this);
            if (list.Count == 0) TextDeclarations.Remove(RowId);
        }
    }

    /// <summary>Enable remote administrator edits of this exact synced entry. No foreign discovery.</summary>
    public static IDisposable Register(string pluginId, ConfigEntryBase entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (string.IsNullOrWhiteSpace(pluginId)) throw new ArgumentException("Plugin id is required.", nameof(pluginId));
        if (!ConfigSync.IsRegistered(pluginId, entry))
            throw new InvalidOperationException("Register the same entry with ConfigSync before enabling server edits.");
        if (SettingsEditors.KindFor(entry.SettingType) == SettingsEditorKind.ReadOnly)
            throw new ArgumentException("This setting type has no supported editor.", nameof(entry));
        // RowId is intentionally identical to SettingsRow. Refuse delimiter ambiguity instead
        // of allowing a section containing '/' to impersonate a different section/key pair.
        if (pluginId.Contains('/') || entry.Definition.Section.Contains('/') || entry.Definition.Key.Contains('/'))
            throw new ArgumentException("Server-editable plugin ids, sections and keys cannot contain '/'.");
        var id = pluginId + "/" + entry.Definition.Section + "/" + entry.Definition.Key;
        ServerSettingsWire.CheckText(id);
        ServerSettingsWire.CheckText(SettingsEditors.Format(entry.BoxedValue));
        if (Entries.TryGetValue(id, out var previous)) previous.Dispose();
        var registration = new Registration(id, pluginId, entry);
        Entries.Add(id, registration);
        return registration;
    }

    /// <summary>SETTINGS1 stage 2: true when this exact row is opted into administrator edits.</summary>
    internal static bool IsEditable(string rowId) => rowId != null && Entries.ContainsKey(rowId);

    /// <summary>Local opt-in and a received server value permit a request; the server checks authority.</summary>
    public static bool CanRequest(string rowId) => _installed && Entries.TryGetValue(rowId, out var entry)
        && entry.Entry != null && ConfigSync.IsRegistered(entry.PluginId, entry.Entry)
        && ConfigSync.IsLocked(entry.PluginId, entry.Entry.Definition.Section, entry.Entry.Definition.Key);

    /// <summary>
    /// Request an edit using the currently displayed server value as the conflict token.
    /// Completion may be synchronous on refusal. Disposing silently forgets the callback;
    /// it cannot retract an edit already sent. Keep the handle until completion or panel close.
    /// </summary>
    public static IDisposable Request(string rowId, string value, Action<ServerSettingResult> completed)
        => Request(rowId, value, null, completed);

    /// <summary>Use the value captured when the editor displayed the setting to detect unseen updates.</summary>
    public static IDisposable Request(string rowId, string value, string? expectedValue, Action<ServerSettingResult> completed)
        => RequestCore(rowId, value, expectedValue, completed, clientCheck: true, own: true);

    /// <summary>
    /// TEXTLIMIT1: <paramref name="clientCheck"/> false skips this side's length pre-check, as a client
    /// that does not run it would (the dev bridge's hostile-client test). The server's own check is
    /// unchanged; the wire still caps each field at <see cref="ServerSettingsWire.MaxTextBytes"/>.
    /// JOINNOTICE2: <paramref name="own"/> records the request as this player's own edit (<see cref="ClaimOwnEdit"/>):
    /// the public <see cref="Request(string, string, string, Action{ServerSettingResult})"/> (the settings pages, a mod
    /// acting for the player) does; the dev bridge's request does not, so its change notifies like any outside change.
    /// </summary>
    internal static IDisposable RequestCore(
        string rowId, string value, string? expectedValue, Action<ServerSettingResult> completed, bool clientCheck, bool own = false)
    {
        if (completed == null) throw new ArgumentNullException(nameof(completed));
        if (!CanRequest(rowId)) return Refuse(completed, "This setting has no active server edit registration.");
        var registration = Entries[rowId];
        foreach (var pending in Requests.Values)
            if (ReferenceEquals(pending.Registration, registration))
                return Refuse(completed, "A change to this setting is already pending.");
        if (Requests.Count >= MaxPending) return Refuse(completed, "Too many pending settings changes.");
        var entry = registration.Entry!;
        if (!SettingsEditors.TryParse(value, entry.SettingType, out var parsedValue, out var parseError))
            return Refuse(completed, parseError);
        // TEXTUNDO1 polish: a text value is sent without the leading and trailing spaces the cfg read
        // would drop (UiCommitText), so the length checked here is the length the server stores.
        if (parsedValue is string untrimmed)
        {
            value = UiCommitText.Trim(untrimmed);
            parsedValue = value;
        }
        // TEXTLIMIT1: the limits the server applies, from this side's own declaration, so an over-long
        // value is refused here in the server's words instead of being dropped or timing out.
        if (clientCheck && parsedValue is string textValue && CheckText(rowId, textValue) is { } tooLong)
            return Refuse(completed, tooLong);
        var message = new ServerSettingsMessage(Guid.NewGuid(), rowId, entry.SettingType.FullName ?? "",
            expectedValue ?? SettingsEditors.Format(entry.BoxedValue), value, false, "");
        byte[] packet;
        try { packet = ServerSettingsWire.Encode(message, response: false); }
        catch (Exception e) { return Refuse(completed, e.Message); }
        var request = new Pending(message.Id, registration, completed, Now() + TimeoutSeconds);
        Requests.Add(message.Id, request);
        if (own) NoteOwnSent(message.Id, rowId, value, message.Expected);
        try
        {
            if (!Send(packet)) Complete(message.Id, new ServerSettingResult(false, "", "The connection did not accept the request."));
        }
        catch { Complete(message.Id, new ServerSettingResult(false, "", "The request could not be sent.")); }
        return request;
    }

    private static IDisposable Refuse(Action<ServerSettingResult> callback, string error)
    { Notify(callback, new ServerSettingResult(false, "", error)); return Empty.Instance; }

    // Called only from ConfigSync's version-filtered successful-handshake snapshot.
    internal static void RecordPeerSnapshot(int userIndex, IReadOnlyList<ConfigSyncRecord> records)
    {
        if (userIndex < 0) return;
        var rows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records) rows.Add(record.PluginId + "/" + record.Section + "/" + record.Key);
        PeerRows[userIndex] = rows;
    }

    internal static bool PeerAllows(int userIndex, string rowId) =>
        PeerRows.TryGetValue(userIndex, out var rows) && rows.Contains(rowId);

    /// <summary>Administrator edits per SteamID per <see cref="RateWindowSeconds"/> (docs/settings-scopes.md).</summary>
    internal const int RateMaxEdits = 10;
    internal const double RateWindowSeconds = 60;
    internal const string RateLimitedError = "Too many changes. Wait a moment and try again.";
    internal const string NotAdminError = "Only server administrators can change this setting.";

    private static Progenitor.Install.RemoteInstallRateLimiter _limiter = new(RateMaxEdits, RateWindowSeconds);
    private static readonly Dictionary<ulong, double> RateAudited = new();

    /// <summary>
    /// Where a decided request's audit line goes. The server's <c>Install</c> points it at the BepInEx
    /// log and <c>BepInEx/progenitor-settings.log</c>; tests capture it. Null writes nothing.
    /// </summary>
    internal static Action<string>? Audit;

    /// <summary>Wall clock for audit lines; tests pin it.</summary>
    internal static Func<DateTime> AuditClock = () => DateTime.UtcNow;

    // Game-free tests that predate the admin list: isAdmin stands for adminauth, SteamID 0.
    internal static ServerSettingResult Apply(ServerSettingsMessage request, bool handshakeOk, bool isAdmin) =>
        Apply(request, handshakeOk, new Progenitor.Install.RemoteInstallActor("", 0, isAdmin));

    /// <summary>
    /// Decide one administrator edit. The same path serves production packets and game-free tests.
    /// <paramref name="actor"/> is what the server read off the sender's <c>User</c>: authorized by
    /// the admin list (no <c>adminauth</c> needed) or by <c>User.IsAdmin</c>, exactly as remote
    /// installs are (<see cref="Progenitor.Install.ServerAdminList.AuthorizeShared"/>).
    /// Order: rate limit (every request spends one, so a refused sender cannot flood the audit),
    /// handshake, authorization, registration, type, expected value, parse, bounds. Every request
    /// past the rate limit is audited; a rate-limited one at most once per window per sender.
    /// </summary>
    internal static ServerSettingResult Apply(ServerSettingsMessage request, bool handshakeOk, in Progenitor.Install.RemoteInstallActor actor)
    {
        double now = Now();
        if (!_limiter.TryTake(actor.SteamId, now))
        {
            var limited = new ServerSettingResult(false, "", RateLimitedError);
            if (!RateAudited.TryGetValue(actor.SteamId, out double last) || now - last >= RateWindowSeconds || now < last)
            {
                if (RateAudited.Count >= 1024) RateAudited.Clear();
                RateAudited[actor.SteamId] = now;
                WriteAudit(request, actor, "", limited);
            }
            return limited;
        }

        string before = CurrentText(request.RowId);
        var result = Decide(request, handshakeOk, actor);
        WriteAudit(request, actor, before, result);
        return result;
    }

    /// <summary>Forget the rate windows and drop the audit sink (uninstall; a new generation starts fresh).</summary>
    private static void ResetEditPolicy()
    {
        _limiter = new Progenitor.Install.RemoteInstallRateLimiter(RateMaxEdits, RateWindowSeconds);
        RateAudited.Clear();
        Audit = null;
        AuditClock = () => DateTime.UtcNow;
        ForgetOwnEdits();
    }

    // ---- JOINNOTICE2: this player's own edits --------------------------------------------------------------------

    /// <summary>At most this many own edits are remembered (the oldest goes first).</summary>
    internal const int MaxOwnEdits = 16;

    private static readonly object OwnGate = new();
    private static readonly List<OwnEdit> OwnEdits = new();

    private sealed class OwnEdit
    {
        internal Guid Id;
        internal string RowId = "";
        internal string Value = "";
        internal string Before = "";
    }

    private static void NoteOwnSent(Guid id, string rowId, string value, string before)
    {
        lock (OwnGate)
        {
            if (OwnEdits.Count >= MaxOwnEdits) OwnEdits.RemoveAt(0);
            OwnEdits.Add(new OwnEdit { Id = id, RowId = rowId ?? "", Value = value ?? "", Before = before ?? "" });
        }
    }

    // An accepted answer replaces the sent value with the stored one; one that stored what was already there changes
    // nothing (no re-signed catalog follows it) and is dropped, as is every refusal.
    private static void NoteOwnAnswer(Guid id, string? rowId, bool success, string stored)
    {
        lock (OwnGate)
        {
            for (int i = 0; i < OwnEdits.Count; i++)
            {
                var edit = OwnEdits[i];
                if (edit.Id != id) continue;
                if (rowId != null && !string.Equals(rowId, edit.RowId, StringComparison.Ordinal)) return;
                if (!success || string.Equals(stored ?? "", edit.Before, StringComparison.Ordinal)) OwnEdits.RemoveAt(i);
                else edit.Value = stored ?? "";
                return;
            }
        }
    }

    /// <summary>
    /// JOINNOTICE2: whether a change of row <paramref name="rowId"/> that the client just received is one this player's
    /// own request asked for: a request this client sent through <see cref="Request(string, string, string, Action{ServerSettingResult})"/>,
    /// still waiting or accepted, whose value <paramref name="isThisChange"/> recognizes. The match is consumed (one
    /// request accounts for one change). Correlated by the request, never by time: a change nobody here asked for, or
    /// the same text set later by someone else, is not claimed.
    /// </summary>
    internal static bool ClaimOwnEdit(string rowId, Func<string, bool> isThisChange)
    {
        if (string.IsNullOrEmpty(rowId) || isThisChange == null) return false;
        lock (OwnGate)
        {
            for (int i = 0; i < OwnEdits.Count; i++)
            {
                var edit = OwnEdits[i];
                if (!string.Equals(edit.RowId, rowId, StringComparison.Ordinal)) continue;
                bool match;
                try { match = isThisChange(edit.Value); }
                catch { match = false; }
                if (!match) continue;
                OwnEdits.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>JOINNOTICE2: forget this player's own edits (a new join, Core unload).</summary>
    internal static void ForgetOwnEdits()
    {
        lock (OwnGate) OwnEdits.Clear();
    }

    /// <summary>JOINNOTICE2: the own edits remembered now (readback and tests).</summary>
    internal static int OwnEditCount
    {
        get { lock (OwnGate) return OwnEdits.Count; }
    }

    private static string CurrentText(string rowId)
    {
        try
        {
            return Entries.TryGetValue(rowId, out var registration) && registration.Entry != null
                ? SettingsEditors.Format(registration.Entry.BoxedValue) : "";
        }
        catch { return ""; }
    }

    private static void WriteAudit(ServerSettingsMessage request, in Progenitor.Install.RemoteInstallActor actor, string before, ServerSettingResult result)
    {
        var sink = Audit;
        if (sink == null) return;
        try
        {
            sink(Progenitor.Install.SettingsAuditLog.Line(AuditClock(), result.Success, actor.CharacterName, actor.SteamId,
                Progenitor.Install.ServerAdminList.Word(actor.AuthorizedBy), request.RowId, before,
                result.Success ? result.Value : request.Value, result.Error));
        }
        catch { /* the audit never decides a request */ }
    }

    private static ServerSettingResult Decide(ServerSettingsMessage request, bool handshakeOk, in Progenitor.Install.RemoteInstallActor actor)
    {
        if (!handshakeOk) return new(false, "", "The framework handshake is not ready.");
        if (!actor.Authorized) return new(false, "", NotAdminError);
        // The operator's lock list ([ServerSettings] LockedInGame): refused whatever a client sends.
        if (ServerSettingsLock.LockedBy(request.RowId) is { } lockedBy) return new(false, CurrentText(request.RowId), ServerSettingsLock.NoteFor(lockedBy));
        if (!Entries.TryGetValue(request.RowId, out var registration) || registration.Entry == null
            || !ConfigSync.IsRegistered(registration.PluginId, registration.Entry))
            return new(false, "", "This setting is not enabled for remote editing.");
        var entry = registration.Entry;
        string current = SettingsEditors.Format(entry.BoxedValue);
        if (!string.Equals(request.Type, entry.SettingType.FullName, StringComparison.Ordinal))
            return new(false, current, "The client and server setting types differ.");
        if (!string.Equals(request.Expected, current, StringComparison.Ordinal))
            return new(false, current, "This setting changed on the server. Review the current value and try again.");
        if (!SettingsEditors.TryParse(request.Value, entry.SettingType, out var parsed, out var error))
            return new(false, current, error);
        // TEXTUNDO1 polish: trimmed before the length check, as the client does (a client that does not
        // trim cannot store a value the next cfg read would shorten).
        if (parsed is string untrimmed) parsed = UiCommitText.Trim(untrimmed);
        // TEXTLIMIT1: the setting's own limit from the server's declaration (never one the client
        // sends), then the packet and ConfigSync byte limits (ServerSettingsLimits.Check).
        if (parsed is string sized && CheckText(request.RowId, sized) is { } tooLong)
            return new(false, current, tooLong);
        if (!Accepts(entry, parsed)) return new(false, current, "Outside the values this setting accepts.");
        // TEXTAREA1: a server-wide value is shown to every player, so control characters other than
        // line breaks and Tab are refused here.
        if (parsed is string text && HasDisallowedControl(text))
            return new(false, current, ControlCharactersError);
        try
        {
            if (!Equals(entry.BoxedValue, parsed)) entry.BoxedValue = parsed;
            string actual = SettingsEditors.Format(entry.BoxedValue);
            if (!Equals(entry.BoxedValue, parsed)) return new(false, actual, "The server did not retain the requested value.");
            return new(true, actual, "");
        }
        catch { return new(false, SettingsEditors.Format(entry.BoxedValue), "The server could not apply this change."); }
    }

    internal const string ControlCharactersError = "The value contains control characters.";

    /// <summary>
    /// TEXTLIMIT1: true for a refusal of the value itself (too long, too many bytes, control
    /// characters) — the editor keeps the typed text so it can be corrected — rather than a refusal
    /// of the request (not an admin, a conflict, a timeout), after which it shows the server's value.
    /// </summary>
    internal static bool IsValueRefusal(string? error) =>
        !string.IsNullOrEmpty(error) && error!.StartsWith("The value ", StringComparison.Ordinal);

    /// <summary>TEXTAREA1: a control character other than CR, LF or Tab (NUL, ESC, DEL, …).</summary>
    internal static bool HasDisallowedControl(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char c in text!)
        {
            if (c == '\n' || c == '\r' || c == '\t') continue;
            if (char.IsControl(c)) return true;
        }
        return false;
    }

    private static bool Accepts(ConfigEntryBase entry, object? value)
    {
        // Unlike the local editor's best-effort decoration path, remote writes fail closed
        // if acceptable-value metadata exists but cannot be read or checked.
        try
        {
#if !PROGENITOR_TESTS
            var bounds = entry.Description.AcceptableValues;
            return bounds == null || bounds.IsValid(value);
#else
            var description = entry.GetType().GetProperty("Description")?.GetValue(entry);
            if (description == null) return true;
            var property = description.GetType().GetProperty("AcceptableValues");
            if (property == null) return false;
            var bounds = property.GetValue(description);
            if (bounds == null) return true;
            return bounds.GetType().GetMethod("IsValid")?.Invoke(bounds, new[] { value }) is true;
#endif
        }
        catch { return false; }
    }

    internal static void AcceptResponse(ServerSettingsMessage response)
    {
        // JOINNOTICE2: the answer to this player's own edit counts even when the page that sent it has closed.
        NoteOwnAnswer(response.Id, response.RowId, response.Success, response.Value);
        if (!Requests.TryGetValue(response.Id, out var pending) || pending.Registration == null
            || pending.Registration.Id != response.RowId) return;
        Complete(response.Id, new ServerSettingResult(response.Success, response.Value, response.Error));
    }

    private static void Complete(Guid id, ServerSettingResult result)
    {
        // JOINNOTICE2: a refusal, a timeout or a failed send stored nothing: no change of this player's to expect.
        if (!result.Success) NoteOwnAnswer(id, null, success: false, "");
        if (!Requests.TryGetValue(id, out var pending)) return;
        var callback = pending.Callback;
        pending.Dispose();
        if (callback != null) Notify(callback, result);
    }

    private static void Notify(Action<ServerSettingResult> callback, ServerSettingResult result)
    {
        try { callback(result); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ServerSettings callback: " + e.Message); }
    }

    internal static void Tick()
    {
        if (Requests.Count == 0) return;
        double now = Now();
        var snapshot = new List<Pending>(Requests.Values);
        foreach (var pending in snapshot)
        {
            if (pending.Registration == null) continue;
            if (!CanRequest(pending.Registration.Id))
                Complete(pending.Id, new(false, "", "The setting or connection is no longer available."));
            else if (now >= pending.Deadline)
                Complete(pending.Id, new(false, "", "No server confirmation arrived. Check the current value before retrying."));
        }
    }

    private static void CancelPending()
    {
        foreach (var pending in new List<Pending>(Requests.Values)) pending.Dispose();
    }

    private sealed class Registration : IDisposable
    {
        internal readonly string Id;
        internal readonly string PluginId;
        internal ConfigEntryBase? Entry;
        internal Registration(string id, string pluginId, ConfigEntryBase entry)
        { Id = id; PluginId = pluginId; Entry = entry; }
        public void Dispose()
        {
            if (Entries.TryGetValue(Id, out var active) && ReferenceEquals(active, this)) Entries.Remove(Id);
            foreach (var pending in new List<Pending>(Requests.Values))
                if (ReferenceEquals(pending.Registration, this)) pending.Dispose();
            Entry = null;
        }
    }

    private sealed class Pending : IDisposable
    {
        internal readonly Guid Id;
        internal readonly double Deadline;
        internal Registration? Registration;
        internal Action<ServerSettingResult>? Callback;
        internal Pending(Guid id, Registration registration, Action<ServerSettingResult> callback, double deadline)
        { Id = id; Registration = registration; Callback = callback; Deadline = deadline; }
        public void Dispose()
        { Requests.Remove(Id); Callback = null; Registration = null; }
    }

    private sealed class Empty : IDisposable
    { internal static readonly Empty Instance = new(); public void Dispose() { } }

#if PROGENITOR_TESTS
    internal static Func<double>? TestClock;
    internal static Func<byte[], bool>? TestSend;
    internal static int PendingCount => Requests.Count;
    private static double Now() => TestClock?.Invoke() ?? 0;
    private static bool Send(byte[] packet) => TestSend?.Invoke(packet) == true;
    internal static void Install() => _installed = true;
    internal static void Uninstall()
    {
        CancelPending();
        foreach (var registration in new List<Registration>(Entries.Values)) registration.Dispose();
        _installed = false; TestClock = null; TestSend = null;
        PeerRows.Clear();
        TextDeclarations.Clear();
        ResetEditPolicy();
    }
#else
    private static IDisposable? _worldReady;
    private static IDisposable? _disconnected;
    private static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    private static bool Send(byte[] packet) => CustomEvents.SendToServer(EventId, packet);

    internal static unsafe void Install()
    {
        if (_installed || HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.ConfigModule)
            || HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.NetworkingModule)) return;
        if (Plugin.IsServerProcess)
        {
            CustomEvents.OnReceiveFromClient(EventId, OnRequest);
            _disconnected = Events.EventBus.On<Events.PlayerDisconnected>(ev => PeerRows.Remove(ev.User.Index), priority: 899);
            _worldReady = Events.EventBus.On<Events.WorldReady>(ev => { if (ev.IsServer) PeerRows.Clear(); }, priority: 899);
            string root = "";
            try { root = BepInEx.Paths.BepInExRootPath ?? ""; } catch { /* no file copy; the log line stays */ }
            Audit = line =>
            {
                Plugin.Logger?.LogInfo("ServerSettings audit: " + line);
                Progenitor.Install.SettingsAuditLog.Append(root, line);
            };
        }
        else
        {
            CustomEvents.OnReceive(EventId, OnResponse);
            _worldReady = Events.EventBus.On<Events.WorldReady>(ev => { if (!ev.IsServer) CancelPending(); }, priority: 899);
            Plugin.Frame += Tick;
        }
        _installed = true;
    }

    internal static unsafe void Uninstall()
    {
        Plugin.Frame -= Tick;
        _worldReady?.Dispose(); _worldReady = null;
        _disconnected?.Dispose(); _disconnected = null;
        CustomEvents.RemoveHandler(EventId, OnResponse);
        CustomEvents.RemoveClientHandler(EventId, OnRequest);
        CancelPending();
        foreach (var registration in new List<Registration>(Entries.Values)) registration.Dispose();
        _installed = false;
        PeerRows.Clear();
        TextDeclarations.Clear();
        ResetEditPolicy();
    }

    private static unsafe void OnRequest(int _, Entity userEntity, User user, byte* payload, int length)
    {
        if (payload == null || !ServerSettingsWire.TryDecode(new ReadOnlySpan<byte>(payload, Math.Max(0, length)),
                response: false, out var request)) return;
        try
        {
            // CustomEvents resolved this User from the connection. Re-read it to avoid trusting
            // a cached admin flag; no privilege or user identity travels inside the request.
            var world = Worlds.Server;
            if (world == null || !world.IsCreated || !world.EntityManager.HasComponent<User>(userEntity)) return;
            var currentUser = world.EntityManager.GetComponentData<User>(userEntity);
            if (!currentUser.IsConnected) return;
            // Settings-scopes decision (2026-10-03): authorize like remote install — the server's
            // admin list (no adminauth needed), else User.IsAdmin — off this fresh User read.
            var actor = new Progenitor.Install.RemoteInstallActor(currentUser.CharacterName.ToString(), currentUser.PlatformId,
                currentUser.IsAdmin, Progenitor.Install.ServerAdminList.AuthorizeShared(currentUser.PlatformId, currentUser.IsAdmin));
            var result = Apply(request, Handshake.UserAnswerStatus(currentUser.Index) == "ok"
                && PeerAllows(currentUser.Index, request.RowId), actor);
            var response = request.WithResult(result);
            var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
            if (bootstrap != null) CustomEvents.SendToUser(bootstrap, currentUser.Index, EventId, ServerSettingsWire.Encode(response, true));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ServerSettings request: " + e.Message); }
    }

    private static unsafe void OnResponse(int _, byte* payload, int length)
    {
        if (payload != null && ServerSettingsWire.TryDecode(new ReadOnlySpan<byte>(payload, Math.Max(0, length)),
            response: true, out var response)) AcceptResponse(response);
    }
#endif
}
