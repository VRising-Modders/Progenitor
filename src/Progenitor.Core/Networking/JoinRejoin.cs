using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Networking;

/// <summary>
/// JOINFLOW2: the server a join went to, as the rejoin needs it: the address, port and transport the game's
/// own <c>LaunchGame</c> used (Bootstrap's connect record), the server's name and operator key, and whether
/// the join used a password (whether — never the password).
/// </summary>
internal sealed class JoinTarget
{
    public const string ProtocolSteamIPv4 = "SteamIPv4";
    public const string ProtocolLidgren = "Lidgren";

    public string Address { get; set; } = "";
    public int Port { get; set; }

    /// <summary><c>SteamIPv4</c> or <c>Lidgren</c>; anything else cannot be rejoined.</summary>
    public string Protocol { get; set; } = "";

    public string ServerName { get; set; } = "";
    public string Fingerprint { get; set; } = "";

    /// <summary>The join used a password: the rejoin asks for it again and never stores it.</summary>
    public bool NeedsPassword { get; set; }

    /// <summary>A private game this client hosts: restarting ends it, so no rejoin is offered.</summary>
    public bool HostedByThisClient { get; set; }

    /// <summary>
    /// SERVEREMPTY1: connect through the game's own password prompt (<c>GameHelper.LaunchPasswordProtectedGame</c>, which
    /// opens <c>Join_PasswordView</c>) instead of connecting at once. Set for the Server tab's Last server Join of a server
    /// that was joined with a password; the password is typed there (or filled by a mod such as RetainedPassword), never
    /// stored by Progenitor.
    /// </summary>
    public bool PromptForPassword { get; set; }

    /// <summary>
    /// JOINPW1: connect without a password first, and open the game's own password prompt (as
    /// <see cref="PromptForPassword"/> does) only when the server refuses that connect with <c>IncorrectPassword</c>. Set
    /// for the Server tab's Last server Join: the record's <c>needsPassword</c> is a hint, never the decision.
    /// </summary>
    public bool PromptOnPasswordRefusal { get; set; }

    /// <summary>The transport's canonical spelling, or empty when it is not one the rejoin can use.</summary>
    public static string CanonicalProtocol(string? protocol)
    {
        string p = (protocol ?? "").Trim();
        if (p.Length == 0 || p.Equals(ProtocolSteamIPv4, StringComparison.OrdinalIgnoreCase)) return ProtocolSteamIPv4;
        if (p.Equals(ProtocolLidgren, StringComparison.OrdinalIgnoreCase)) return ProtocolLidgren;
        return "";
    }

    /// <summary>An address and port the game can connect to again with a known transport.</summary>
    public bool CanReconnect =>
        Address.Trim().Length > 0 && Port is > 0 and <= 65535 && CanonicalProtocol(Protocol).Length > 0;

    /// <summary><c>1.2.3.4:9876</c>.</summary>
    public string Endpoint => Address.Trim() + ":" + Port.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One installed row the rejoin record remembers (for the after-relaunch summary).</summary>
internal sealed class RejoinRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Package { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Required { get; set; }

    /// <summary>
    /// <c>loaded</c>, <c>after-restart</c>, <c>failed</c>, <c>refused</c>, or <c>server-must-update</c>; for a required row
    /// the batch did not touch <c>ready</c> or <c>missing</c>; JOINTURNON: <c>turned-on</c> (the player's turned-off copy was
    /// switched on for the next start; <see cref="Version"/> is that copy's) or <c>turned-off</c> (still off).
    /// </summary>
    public string Result { get; set; } = "";
}

/// <summary>
/// JOINFLOW2: <c>BepInEx/config/progenitor-rejoin.json</c> — written just before "Restart and rejoin" quits the
/// game, read once at the next start when the main menu is ready. It holds where to reconnect and what was
/// installed, and <b>never</b> a password: the type has no field for one, and the reader ignores any.
/// </summary>
internal sealed class RejoinRecord
{
    public const int Schema = 1;

    public string Address { get; set; } = "";
    public int Port { get; set; }
    public string Protocol { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string Fingerprint { get; set; } = "";

    /// <summary>The server asked for a password when it was joined (never the password itself).</summary>
    public bool NeedsPassword { get; set; }

    /// <summary>When the record was written (ISO UTC).</summary>
    public string Created { get; set; } = "";

    /// <summary>Set when a start picked the record up, so a second start never uses it again.</summary>
    public bool Used { get; set; }

    /// <summary>
    /// Written by the bridge's dry run (nothing quit): a start leaves it alone; only <c>join_ui rejoin_now</c>
    /// picks it up.
    /// </summary>
    public bool DryRun { get; set; }

    public List<RejoinRow> Rows { get; set; } = new();

    public const string KindDedicated = "dedicated";
    public const string KindHosted = "hosted";

    /// <summary>INSTALLRESTART1: <c>dedicated</c> (a server to dial again; the default and every older record) or <c>hosted</c>.</summary>
    public string Kind { get; set; } = KindDedicated;

    /// <summary>INSTALLRESTART1: the hosted save to resume (its name as the title screen's Continue shows it); empty for a server.</summary>
    public string Save { get; set; } = "";

    public bool IsHosted => string.Equals(Kind, KindHosted, StringComparison.Ordinal);

    /// <summary>
    /// The server to dial. INSTALLRESTART1: a server joined with a password goes through the game's own password prompt
    /// (<see cref="JoinTarget.PromptForPassword"/>, as the Server tab's Last server Join does), where RetainedPassword or the
    /// player fills it; Progenitor never stores it.
    /// </summary>
    public JoinTarget ToTarget() => new()
    {
        Address = Address,
        Port = Port,
        Protocol = Protocol,
        ServerName = ServerName,
        Fingerprint = Fingerprint,
        NeedsPassword = NeedsPassword,
        // F19 (live 2026-10-07): a join "used a password" whenever the game's connect data carried one, which a password
        // manager's autofill does for a server that has none; the rejoin then opened the prompt for a password-free server
        // and the player was left at the title screen. As the Last server Join does (JOINPW1), the rejoin connects without
        // a password first and opens the game's prompt only when the server refuses that connect for its password.
        PromptForPassword = false,
        PromptOnPasswordRefusal = true,
    };

    /// <summary>A record a start can act on: a hosted save with a name, or a server with an address, port and transport.</summary>
    public bool Usable => IsHosted ? Save.Trim().Length > 0 : ToTarget().CanReconnect;

    public static RejoinRecord From(JoinTarget target, IEnumerable<RejoinRow> rows, DateTime nowUtc)
    {
        var r = new RejoinRecord
        {
            Address = target.Address.Trim(),
            Port = target.Port,
            // A transport the rejoin cannot use is kept as it was, so the pickup refuses it (empty would read as SteamIPv4).
            Protocol = JoinTarget.CanonicalProtocol(target.Protocol) is { Length: > 0 } p ? p : (target.Protocol ?? "").Trim(),
            ServerName = target.ServerName ?? "",
            Fingerprint = target.Fingerprint ?? "",
            NeedsPassword = target.NeedsPassword,
            Created = ModManifestJson.FormatIssued(nowUtc),
        };
        foreach (var row in rows ?? Array.Empty<RejoinRow>())
            if (row != null && r.Rows.Count < RejoinStore.MaxRows) r.Rows.Add(row);
        return r;
    }

    /// <summary>The JSON on disk: sorted keys, plain values. There is no password key.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("address", Address);
            w.WriteString("created", Created);
            w.WriteBoolean("dryRun", DryRun);
            w.WriteString("fingerprint", Fingerprint);
            w.WriteString("kind", Kind);
            w.WriteBoolean("needsPassword", NeedsPassword);
            w.WriteNumber("port", Port);
            w.WriteString("protocol", Protocol);
            w.WriteStartArray("rows");
            foreach (var row in Rows)
            {
                w.WriteStartObject();
                w.WriteString("id", row.Id);
                w.WriteString("name", row.Name);
                w.WriteString("package", row.Package);
                w.WriteBoolean("required", row.Required);
                w.WriteString("result", row.Result);
                w.WriteString("version", row.Version);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteString("save", Save);
            w.WriteNumber("schema", Schema);
            w.WriteString("serverName", ServerName);
            w.WriteBoolean("used", Used);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Parse; false (and null) for anything that is not a record of this schema. Unknown keys are ignored.</summary>
    public static bool TryParse(string? json, out RejoinRecord? record)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(json) || json!.Length > RejoinStore.MaxBytes) return false;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != Schema) return false;
            var r = new RejoinRecord
            {
                Address = Str(root, "address"),
                Port = root.TryGetProperty("port", out var port) && port.ValueKind == JsonValueKind.Number && port.TryGetInt32(out int p) ? p : 0,
                Protocol = Str(root, "protocol"),
                ServerName = Str(root, "serverName"),
                Fingerprint = Str(root, "fingerprint"),
                NeedsPassword = Bool(root, "needsPassword"),
                Created = Str(root, "created"),
                Used = Bool(root, "used"),
                DryRun = Bool(root, "dryRun"),
                Kind = Str(root, "kind") == KindHosted ? KindHosted : KindDedicated,
                Save = Str(root, "save").Trim(),
            };
            if (root.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in rows.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object || r.Rows.Count >= RejoinStore.MaxRows) continue;
                    r.Rows.Add(new RejoinRow
                    {
                        Id = Str(el, "id"),
                        Name = Str(el, "name"),
                        Package = Str(el, "package"),
                        Version = Str(el, "version"),
                        Required = Bool(el, "required"),
                        Result = Str(el, "result"),
                    });
                }
            }

            record = r;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static bool Bool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>What a start found in <c>progenitor-rejoin.json</c>.</summary>
internal enum RejoinPickup
{
    /// <summary>No record.</summary>
    None = 0,

    /// <summary>A fresh, unused record: rejoin now (it is marked used first).</summary>
    Rejoin = 1,

    /// <summary>Older than <see cref="RejoinStore.MaxAge"/>: deleted, ignored.</summary>
    Expired = 2,

    /// <summary>A start already picked it up: deleted, ignored.</summary>
    AlreadyUsed = 3,

    /// <summary>Not a record (malformed, another schema, no address): deleted, ignored.</summary>
    Invalid = 4,
}

/// <summary>
/// JOINFLOW2: the rejoin record's file rules, game-free. Written atomically; picked up once — a fresh record
/// is marked <c>used</c> on disk before it is acted on, so a crash during the rejoin never loops; a record
/// older than <see cref="MaxAge"/> (or one written in the future by a wrong clock, beyond a minute) is
/// deleted unread.
/// </summary>
internal static class RejoinStore
{
    public const string FileName = "progenitor-rejoin.json";
    public const int MaxRows = 64;
    public const int MaxBytes = 64 * 1024;
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FutureSkew = TimeSpan.FromMinutes(1);

    public static void Write(string path, RejoinRecord record)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("path is required", nameof(path));
        string dir = Path.GetDirectoryName(path) ?? "";
        if (dir.Length > 0) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, record.ToJson(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try { File.Move(tmp, path, overwrite: true); }
        catch
        {
            try { File.Copy(tmp, path, overwrite: true); }
            finally
            {
                try { File.Delete(tmp); }
                catch { /* a leftover tmp is never read */ }
            }
        }
    }

    /// <summary>The record on disk without changing it (readback), or null.</summary>
    public static RejoinRecord? Peek(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length > MaxBytes) return null;
            return RejoinRecord.TryParse(File.ReadAllText(path), out var r) ? r : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Take the record for this start: <see cref="RejoinPickup.Rejoin"/> with the record (now marked used on
    /// disk), or why it was ignored (and deleted). Never throws.
    /// </summary>
    public static RejoinPickup Take(string path, DateTime nowUtc, out RejoinRecord? record)
    {
        record = null;
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return RejoinPickup.None;
            string json;
            var info = new FileInfo(path);
            if (info.Length > MaxBytes)
            {
                Delete(path);
                return RejoinPickup.Invalid;
            }

            json = File.ReadAllText(path);
            if (!RejoinRecord.TryParse(json, out var r) || r == null || !r.Usable)
            {
                Delete(path);
                return RejoinPickup.Invalid;
            }

            var verdict = Judge(r, nowUtc);
            if (verdict != RejoinPickup.Rejoin)
            {
                Delete(path);
                return verdict;
            }

            r.Used = true;
            Write(path, r);
            record = r;
            return RejoinPickup.Rejoin;
        }
        catch
        {
            return RejoinPickup.None;
        }
    }

    /// <summary>The age and use rules alone.</summary>
    public static RejoinPickup Judge(RejoinRecord r, DateTime nowUtc)
    {
        if (r.Used) return RejoinPickup.AlreadyUsed;
        if (!ModManifestJson.TryParseIssued(r.Created, out var created)) return RejoinPickup.Invalid;
        var age = nowUtc - created;
        if (age > MaxAge || age < -FutureSkew) return RejoinPickup.Expired;
        return RejoinPickup.Rejoin;
    }

    public static bool Delete(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    public static string PickupName(RejoinPickup p) => p switch
    {
        RejoinPickup.Rejoin => "rejoin",
        RejoinPickup.Expired => "expired",
        RejoinPickup.AlreadyUsed => "already-used",
        RejoinPickup.Invalid => "invalid",
        _ => "none",
    };
}

/// <summary>How "Restart and rejoin" can bring the game back.</summary>
internal enum RelaunchMode
{
    /// <summary>A detached relauncher waits for this process to exit, then asks Steam to start V Rising.</summary>
    Automatic = 0,

    /// <summary>The game closes; the player starts it again (the record still reconnects them).</summary>
    Manual = 1,
}

/// <summary>
/// JOINFLOW2: the relauncher — the one place a process is started for "Restart and rejoin". It is the inbox
/// Windows PowerShell with an inline command (no new executable, no script file, no encoded command), started
/// detached and hidden. It waits for this exact process (its id and start time, so a reused id is never waited
/// on), lets Steam notice the exit, and asks Steam to start the game once through <c>steam://run/1604030</c>
/// (the way <c>tools/launch-client.ps1</c> does). It asks a second time only when no process with this game's
/// executable path appeared, so two copies never start; and it never starts the game while this one is still
/// running (it gives up after <see cref="WaitSeconds"/>).
/// </summary>
internal static class RelaunchCommand
{
    public const string SteamRunUrl = "steam://run/1604030";

    /// <summary>How long the relauncher waits for the old process to exit.</summary>
    public const int WaitSeconds = 180;

    /// <summary>Steam keeps an app "running" for a few seconds after it exits (seen live 2026-09-12).</summary>
    public const int SettleSeconds = 8;

    /// <summary>
    /// How long it watches for the new process before asking Steam once more. A slow Steam start took more than 30 s
    /// (2026-10-09), and a second ask while the first launch was still starting made Steam show "Game already running".
    /// </summary>
    public const int AppearSeconds = 90;

    /// <summary>Steam's own record that it is running or launching the app (<c>Running</c> = 1).</summary>
    public const string SteamAppKey = @"HKCU:\Software\Valve\Steam\Apps\1604030";

    /// <summary>The PowerShell script (one line): wait, settle, start once, re-ask only if nothing appeared.</summary>
    public static string Script(int pid, long startTicksUtc, string gameExePath, string url = SteamRunUrl)
    {
        if (pid <= 0) throw new ArgumentOutOfRangeException(nameof(pid));
        string exe = Quote(gameExePath ?? "");
        string u = Quote(url ?? SteamRunUrl);
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("$ErrorActionPreference='SilentlyContinue';");
        sb.Append("$p=Get-Process -Id ").Append(pid.ToString(inv)).Append(';');
        // The same process only: a reused id has another start time.
        sb.Append("if($p -and [math]::Abs($p.StartTime.ToUniversalTime().Ticks-").Append(startTicksUtc.ToString(inv))
            .Append(") -lt 20000000){if(-not $p.WaitForExit(").Append((WaitSeconds * 1000).ToString(inv)).Append(")){exit 2}};");
        sb.Append("Start-Sleep -Seconds ").Append(SettleSeconds.ToString(inv)).Append(';');
        sb.Append("$exe=").Append(exe).Append(';');
        sb.Append("function Up{$exe.Length -gt 0 -and @(Get-Process|Where-Object{$_.Path -eq $exe}).Count -gt 0};");
        // Steam's own "running or launching" flag: a launch Steam has started counts as up, so it is never asked twice.
        sb.Append("function Launching{(Get-ItemProperty -Path ").Append(Quote(SteamAppKey)).Append(" -Name Running).Running -eq 1};");
        sb.Append("if(Up){exit 3};");
        sb.Append("Start-Process ").Append(u).Append(';');
        sb.Append("$t=(Get-Date).AddSeconds(").Append(AppearSeconds.ToString(inv)).Append(");");
        sb.Append("while((Get-Date) -lt $t -and -not (Up)){Start-Sleep -Seconds 2};");
        sb.Append("if($exe.Length -gt 0 -and -not (Up) -and -not (Launching)){Start-Process ").Append(u).Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// The arguments for <c>powershell.exe</c>. The script contains no double quote (paths cannot, and every
    /// string in it is single-quoted with <c>'</c> doubled), so wrapping it in one pair of double quotes is
    /// exact under the Windows command-line rules.
    /// </summary>
    public static string Arguments(string script)
    {
        if (script == null) throw new ArgumentNullException(nameof(script));
        if (script.IndexOf('"') >= 0) throw new ArgumentException("the relaunch script must not contain a double quote", nameof(script));
        return "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + script + "\"";
    }

    /// <summary>The inbox Windows PowerShell under <paramref name="systemDirectory"/> (<c>C:\Windows\System32</c>).</summary>
    public static string PowerShellPath(string systemDirectory) =>
        Path.Combine(systemDirectory ?? "", "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>A PowerShell single-quoted string literal: <c>'</c> doubled, nothing else special.</summary>
    internal static string Quote(string value)
    {
        string v = (value ?? "").Replace("\r", "").Replace("\n", "");
        // Every character PowerShell reads as a single quote is doubled: ' and U+2018, U+2019, U+201A, U+201B.
        var sb = new StringBuilder(v.Length + 2);
        sb.Append('\'');
        foreach (char c in v)
        {
            sb.Append(c);
            if (c is '\'' or '\u2018' or '\u2019' or '\u201a' or '\u201b') sb.Append(c);
        }

        return sb.Append('\'').ToString();
    }

    /// <summary>
    /// Automatic when Steam is running and handles <c>steam://</c> and PowerShell is there; else Manual (the
    /// game still quits, after saying "Start it again and Progenitor will reconnect you").
    /// </summary>
    public static RelaunchMode Decide(bool steamUrlHandler, bool steamRunning, bool powerShellPresent) =>
        steamUrlHandler && steamRunning && powerShellPresent ? RelaunchMode.Automatic : RelaunchMode.Manual;
}

/// <summary>JOINFLOW2: the after-relaunch summary (step 4), from the record and what this start loaded.</summary>
internal sealed class RejoinSummary
{
    public int RequiredReady { get; set; }
    public int RequiredTotal { get; set; }
    public int RecommendedAdded { get; set; }

    /// <summary>One line per required row: name and <c>Ready</c>, <c>Still missing</c> or <c>Server must update</c>.</summary>
    public List<KeyValuePair<string, string>> Required { get; } = new();

    /// <summary>
    /// Build from the record's rows: a required row is Ready when this start runs the version it needed
    /// (part by part equal), Server must update when the record says so, else Still missing; a recommended row
    /// counts as added when this start runs it.
    /// </summary>
    public static RejoinSummary From(RejoinRecord record, Func<string, string?> loadedVersionOf)
    {
        var s = new RejoinSummary();
        foreach (var row in record?.Rows ?? new List<RejoinRow>())
        {
            string? have = null;
            if (row.Id.Length > 0)
            {
                try { have = loadedVersionOf?.Invoke(row.Id); }
                catch { have = null; }
            }

            bool runs = have != null && (row.Version.Length == 0 || JoinPageModel.CompareVersions(have, row.Version) == 0);
            if (row.Required)
            {
                s.RequiredTotal++;
                string word = row.Result == "server-must-update" ? JoinText.SummaryServerMustUpdate
                    : runs ? JoinText.ReadyWord
                    : row.Result is "turned-on" or "turned-off" ? JoinText.SummaryTurnedOff
                    : JoinText.SummaryStillMissing;
                if (word == JoinText.ReadyWord) s.RequiredReady++;
                s.Required.Add(new KeyValuePair<string, string>(row.Name.Length > 0 ? row.Name : row.Id, word));
            }
            else if (runs) s.RecommendedAdded++;
        }

        return s;
    }

    /// <summary>
    /// The same from the join screens' data once the join answered: every required row of the page model
    /// (Ready, Server must update when this client is ahead with nothing to install, else Still missing), and the
    /// recommended rows of the record this start runs.
    /// </summary>
    public static RejoinSummary FromModel(JoinPageModel model, RejoinRecord? record, Func<string, string?> loadedVersionOf)
    {
        var s = new RejoinSummary();
        foreach (var r in model?.Required ?? Array.Empty<JoinRequiredRow>())
        {
            s.RequiredTotal++;
            string word = r.Ready ? JoinText.ReadyWord
                // C4: the server's Progenitor build, not a mod the server must update.
                : r.Origin == JoinRowOrigin.Progenitor ? "Needs exactly " + r.RequiredVersion
                : r.Action == JoinRowAction.TellAdmin ? JoinText.SummaryServerMustUpdate
                : r.Action == JoinRowAction.TurnOn ? JoinText.SummaryTurnedOff
                : JoinText.SummaryStillMissing;
            if (r.Ready) s.RequiredReady++;
            s.Required.Add(new KeyValuePair<string, string>(r.Name, word));
        }

        if (record != null)
            s.RecommendedAdded = From(new RejoinRecord { Rows = record.Rows.FindAll(x => !x.Required) }, loadedVersionOf).RecommendedAdded;
        return s;
    }

    /// <summary><c>After the restart: 2 of 3 required mods ready.</c></summary>
    public string Line(bool afterRestart) =>
        (afterRestart ? "After the restart: " : "") + RequiredReady.ToString(CultureInfo.InvariantCulture) + " of "
        + RequiredTotal.ToString(CultureInfo.InvariantCulture) + " required mods ready"
        + (RecommendedAdded > 0 ? ", " + RecommendedAdded.ToString(CultureInfo.InvariantCulture) + " recommended added" : "") + ".";
}

/// <summary>JOINFLOW2: the live side the reconnect drives (tests supply a fake).</summary>
internal interface IJoinConnectHost
{
    /// <summary>A client world exists (in a game, or on its way in or out).</summary>
    bool WorldExists { get; }

    /// <summary>No client world, the title view on show: a connect can start.</summary>
    bool MainMenuReady { get; }

    /// <summary>In the world and the handshake answered ok.</summary>
    bool JoinedOk { get; }

    /// <summary>The game's own Leave (the production ExitGame view).</summary>
    void RequestLeave();

    /// <summary>Back to the title view when no world is left but the menu is not on show (the disconnect screen).</summary>
    void RequestMainMenu();

    /// <summary>Connect to <paramref name="target"/> the way the dev bridge's <c>connect</c> does (<c>GameHelper.LaunchGame</c>).</summary>
    bool Launch(JoinTarget target, out string error);

    /// <summary>
    /// SERVEREMPTY1: open the game's own password prompt for <paramref name="target"/> (<c>GameHelper.LaunchPasswordProtectedGame</c>);
    /// the game connects once the player confirms it. False with the reason when it cannot.
    /// </summary>
    bool LaunchPasswordPrompt(JoinTarget target, out string error)
    {
        error = "the game's password prompt is not available here";
        return false;
    }

    /// <summary>
    /// Live 2026-10-05: the game's own connect failed (its client is in the error state: the loading screen's
    /// <c>Error: Connection timed out</c>). <paramref name="reasonName"/> is the game's
    /// <c>ConnectionStatusChangeReason</c> by name (<c>Timeout</c>, <c>IncorrectPassword</c>; words come from
    /// <see cref="JoinText.ConnectFailureWords"/>); <paramref name="kicked"/> when the server disconnected the client (a
    /// refusal, whose own screens follow).
    /// </summary>
    bool TryConnectFailure(out string reasonName, out bool kicked)
    {
        reasonName = "";
        kicked = false;
        return false;
    }

    /// <summary>
    /// INSTALLRESTART1: the title screen's Continue caption (<c>MainMenuNewView._ContinueText</c>: the newest hosted save's
    /// server name, or the newest server's), or null when it cannot be read.
    /// </summary>
    string? ContinueText => null;

    /// <summary>
    /// INSTALLRESTART1: press the title screen's Continue (<c>MainMenuNewView.ContinueButton_OnClick</c>, the production
    /// path that hosts the newest save through <c>HostGame</c>). False with the reason when it cannot.
    /// </summary>
    bool PressContinue(out string error)
    {
        error = "the title screen's Continue is not available here";
        return false;
    }
}

/// <summary>
/// JOINFLOW2: Join now and the after-relaunch rejoin, game-free. Leaves the server (or the disconnect screen)
/// for the main menu, then connects to the same address, port and transport, and watches the join: joined when
/// the world is up and the handshake said ok, failed when it was refused or nothing happened in time.
/// </summary>
internal sealed class JoinReconnect
{
    public const double LeaveTimeoutSeconds = 30;
    public const double ConnectTimeoutSeconds = 120;

    /// <summary>A kick waits this long for the refusal's own screens before the page says it failed.</summary>
    public const double KickGraceSeconds = 3;

    private readonly IJoinConnectHost _host;
    private bool _sawConnectUnderway;
    private double _failureSeenAt = -1;
    private double _phaseStart;
    private bool _leaveRequested;
    private bool _menuRequested;
    private bool _cancelLeaveRequested;

    public JoinReconnect(JoinTarget target, IJoinConnectHost host, double now, JoinTarget? fallback = null)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _fallback = fallback;
        _phaseStart = now;
        Phase = "leaving";
    }

    /// <summary>The address being connected to now (LOCALREJOIN1: the loopback first, then the original as the fallback).</summary>
    public JoinTarget Target { get; private set; }

    private JoinTarget? _fallback;

    /// <summary>LOCALREJOIN1: the first address failed and the reconnect moved on to the original one.</summary>
    public bool UsedFallback { get; private set; }

    /// <summary>LOCALREJOIN1: why the first address was given up (empty unless <see cref="UsedFallback"/>).</summary>
    public string FallbackReason { get; private set; } = "";

    /// <summary>
    /// <c>leaving</c>, <c>connecting</c>, <c>joined</c>, <c>failed</c>, <c>canceled</c>; and <c>prompted</c> when the game's own
    /// password prompt took over (<see cref="JoinTarget.PromptForPassword"/>): the game connects from there.
    /// </summary>
    public string Phase { get; private set; }

    public string Error { get; private set; } = "";

    /// <summary><c>LaunchGame</c> was called.</summary>
    public bool Launched { get; private set; }

    public bool Canceled { get; private set; }

    public bool Finished => Phase is "joined" or "failed" or "canceled" or "prompted";

    /// <summary>The game's reason name for <c>IncorrectPassword</c>, the server's refusal of a connect without its password.</summary>
    public const string IncorrectPasswordReason = "IncorrectPassword";

    /// <summary>
    /// JOINPW1: the server refused the connect without a password (<see cref="JoinTarget.PromptOnPasswordRefusal"/>); the
    /// reconnect went back to the title view to open the game's password prompt.
    /// </summary>
    public bool PasswordRefused { get; private set; }

    private bool _passwordRefusalTaken;

    /// <summary>True once, after <see cref="PasswordRefused"/> was set: the caller notes the refusal (the last-server hint).</summary>
    public bool TakePasswordRefusal()
    {
        if (!PasswordRefused || _passwordRefusalTaken) return false;
        _passwordRefusalTaken = true;
        return true;
    }

    /// <summary>A canceled connect still has a world to leave.</summary>
    public bool NeedsTicking => !Finished || (Canceled && Launched && !_cancelLeaveRequested);

    /// <summary>The progress bar: leaving 10, connecting 30 rising to 90, joined 100.</summary>
    public int Progress(double now) => Phase switch
    {
        "leaving" => 10,
        "connecting" => 30 + (int)Math.Min(60, Math.Max(0, now - _phaseStart) * 60 / 45),
        "joined" => 100,
        _ => 0,
    };

    public void Tick(double now)
    {
        if (Canceled)
        {
            // A canceled connect that already launched leaves the world it reaches.
            if (Launched && !_cancelLeaveRequested && _host.WorldExists)
            {
                _cancelLeaveRequested = true;
                Try(_host.RequestLeave);
            }

            return;
        }

        switch (Phase)
        {
            case "leaving":
                if (_host.MainMenuReady)
                {
                    if (Target.PromptForPassword || PasswordRefused)
                    {
                        if (_host.LaunchPasswordPrompt(Target, out string promptError))
                        {
                            Launched = true;
                            Phase = "prompted";
                        }
                        else Fail(promptError.Length > 0 ? promptError : "the game did not open its password prompt");
                        return;
                    }

                    if (_host.Launch(Target, out string error))
                    {
                        Launched = true;
                        Phase = "connecting";
                        _phaseStart = now;
                    }
                    else Fail(error.Length > 0 ? error : "the game did not start the connect");
                    return;
                }

                if (_host.WorldExists)
                {
                    if (!_leaveRequested)
                    {
                        _leaveRequested = true;
                        Try(_host.RequestLeave);
                    }
                }
                else if (!_menuRequested && now - _phaseStart >= 2)
                {
                    _menuRequested = true;
                    Try(_host.RequestMainMenu);
                }

                if (now - _phaseStart > LeaveTimeoutSeconds) Fail("the game did not return to the main menu");
                return;
            case "connecting":
                if (_host.JoinedOk)
                {
                    Phase = "joined";
                    return;
                }

                // Live 2026-10-05: the game showed "Error: Connection timed out" while this page waited for its own
                // 120 s. Its failure ends the connect at once, with its reason; a kick (a refusal) waits a moment for
                // the refusal's own screens, which take over. Only a failure seen after the connect was under way counts.
                bool failed = false;
                string reasonName = "";
                bool kicked = false;
                try { failed = _host.TryConnectFailure(out reasonName, out kicked); }
                catch { failed = false; }
                if (!failed) _sawConnectUnderway = true;
                else if (_sawConnectUnderway)
                {
                    // JOINPW1: a connect without a password that the server refused for its password goes back to the
                    // title view and opens the game's own prompt there (once; the prompt's own connect is the game's).
                    if (Target.PromptOnPasswordRefusal && !PasswordRefused
                        && string.Equals((reasonName ?? "").Trim(), IncorrectPasswordReason, StringComparison.Ordinal))
                    {
                        PasswordRefused = true;
                        Phase = "leaving";
                        _phaseStart = now;
                        _leaveRequested = _menuRequested = false;
                        _sawConnectUnderway = false;
                        _failureSeenAt = -1;
                        return;
                    }

                    string reason = JoinText.ConnectFailureWords(reasonName);
                    if (_failureSeenAt < 0) _failureSeenAt = now;
                    if (!kicked || now - _failureSeenAt >= KickGraceSeconds)
                    {
                        // LOCALREJOIN1 review: only a connect that reached no server moves on to the other address; any answer
                        // from a server (a kick, a ban, full, wrong version, starting up, a password, blocked) is its reason.
                        string why = reason.Length > 0 ? reason : "the connection failed";
                        // F22 review (M2): only a connect no server answered may be tried again while waiting for a restart.
                        NoServerAnswered = !kicked && NoAnswer(reasonName);
                        if (kicked || !NoAnswer(reasonName) || !FallBack(why, now)) Fail(why);
                        return;
                    }
                }

                if (now - _phaseStart > ConnectTimeoutSeconds && !FallBack("no answer from the server in time", now))
                {
                    NoServerAnswered = true;
                    Fail("no answer from the server in time");
                }
                return;
        }
    }

    /// <summary>The join this connect started was refused (its screens take over).</summary>
    public void Refused()
    {
        if (Finished) return;
        Fail("the server refused the join");
    }

    /// <summary>F22 review (M2): the connect failed with no answer from any server (a timeout, no connection).</summary>
    public bool NoServerAnswered { get; private set; }

    public void Cancel()
    {
        if (Finished) return;
        Canceled = true;
        Phase = "canceled";
    }

    /// <summary>
    /// LOCALREJOIN1 review: the game's connect failures that mean no server answered (the only ones the fallback address
    /// is tried for): <c>Timeout</c>, <c>FailedToEstablishConnection</c>, <c>Unknown</c>, <c>ClientError</c>, or no reason.
    /// </summary>
    internal static bool NoAnswer(string? reasonName) => (reasonName ?? "").Trim() switch
    {
        "" or "Timeout" or "FailedToEstablishConnection" or "Unknown" or "ClientError" => true,
        _ => false,
    };

    /// <summary>
    /// LOCALREJOIN1: the connect to the first address failed without an answer from the server: start over (leave the
    /// failed connect for the title view, then connect) at the fallback address, once. False when there is none.
    /// </summary>
    private bool FallBack(string why, double now)
    {
        if (_fallback == null || UsedFallback || Canceled) return false;
        string from = Target.Endpoint;
        Target = _fallback;
        _fallback = null;
        UsedFallback = true;
        FallbackReason = why ?? "";
        Phase = "leaving";
        _phaseStart = now;
        _leaveRequested = _menuRequested = false;
        _sawConnectUnderway = false;
        _failureSeenAt = -1;
        Launched = false;
        Plugin.Logger?.LogInfo("JoinFlow: reconnect to " + from + " failed (" + FallbackReason + "); trying " + Target.Endpoint + " instead");
        return true;
    }

    private void Fail(string why)
    {
        Error = why ?? "";
        Phase = "failed";
    }

    private static void Try(Action a)
    {
        try { a(); }
        catch { /* a failed request times out like any other */ }
    }
}
