using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Networking;

/// <summary>INSTALLRESTART1: one client install, update or removal that waits for V Rising to restart.</summary>
internal sealed class PendingClientChange
{
    public const string ActionInstall = "install";
    public const string ActionUpdate = "update";
    public const string ActionRemove = "remove";

    /// <summary>The plugin GUID when known (the after-restart check), else empty.</summary>
    public string Id { get; set; } = "";

    /// <summary>The name the player knows it by (the listing's or the ledger's).</summary>
    public string Name { get; set; } = "";

    public string Version { get; set; } = "";

    /// <summary><see cref="ActionInstall"/>, <see cref="ActionUpdate"/> or <see cref="ActionRemove"/>.</summary>
    public string Action { get; set; } = ActionInstall;

    public bool IsRemoval => string.Equals(Action, ActionRemove, StringComparison.Ordinal);
}

/// <summary>INSTALLRESTART1: what the pending-restart bar offers.</summary>
internal enum ClientRestartKind
{
    /// <summary>Nothing waits for a restart: no bar.</summary>
    None = 0,

    /// <summary>Out of a world: <c>Restart now</c>, back at the title screen afterwards.</summary>
    Restart = 1,

    /// <summary>In a world on a dedicated server this client can dial again: <c>Restart and rejoin &lt;server&gt;</c>.</summary>
    RestartAndRejoin = 2,

    /// <summary>In a private game this client hosts: <c>Restart and resume &lt;save&gt;</c> through the game's own Continue.</summary>
    RestartAndResume = 3,

    /// <summary>In a world nothing can bring the player back to (an unnamed hosted save, a P2P join): <c>Restart now</c>, to the menu.</summary>
    RestartToMenu = 4,
}

/// <summary>INSTALLRESTART1: the facts the bar is decided from (live: the install pane, Bootstrap's connect record, the relauncher).</summary>
internal sealed class ClientRestartFacts
{
    public IReadOnlyList<PendingClientChange> Pending { get; init; } = Array.Empty<PendingClientChange>();

    /// <summary>A client world exists (in a game).</summary>
    public bool InWorld { get; init; }

    /// <summary>The connection this client is on (address, transport, name, hosting), or null.</summary>
    public JoinTarget? Target { get; init; }

    /// <summary>
    /// The hosted save's name as the title screen's <c>CONTINUE &lt;name&gt;</c> shows it (its server name), or empty when
    /// it is not known: then a hosted game gets no resume.
    /// </summary>
    public string HostedSave { get; init; } = "";

    /// <summary>The relaunch plan, or null when there is no relauncher at all.</summary>
    public RelaunchPlan? Plan { get; init; }
}

/// <summary>INSTALLRESTART1: the bar's sentence and button, the confirmation's words, and where the restart returns.</summary>
internal sealed class ClientRestartOffer
{
    public const string ReturnNone = "none";
    public const string ReturnDedicated = "dedicated";
    public const string ReturnHosted = "hosted";

    public ClientRestartKind Kind { get; init; }
    public int Count { get; init; }
    public IReadOnlyList<PendingClientChange> Pending { get; init; } = Array.Empty<PendingClientChange>();

    /// <summary><c>none</c>, <c>dedicated</c> or <c>hosted</c>.</summary>
    public string ReturnTo { get; init; } = ReturnNone;

    /// <summary>The server or save the restart returns to (empty for none).</summary>
    public string ReturnName { get; init; } = "";

    public JoinTarget? Target { get; init; }

    /// <summary>The relauncher cannot run here (no Steam, no handler, no PowerShell, none at all): the confirm offers Quit to desktop.</summary>
    public bool Manual { get; init; }

    public string ManualWhy { get; init; } = "";

    /// <summary>Why the restart only returns to the menu (<see cref="ClientRestartKind.RestartToMenu"/>), or empty.</summary>
    public string NoReturnWhy { get; init; } = "";

    public string Sentence { get; init; } = "";
    public string Button { get; init; } = "";
    public string ConfirmTitle { get; init; } = "";
    public string ConfirmBody { get; init; } = "";
    public string ConfirmYes { get; init; } = "";
    public string ConfirmNo { get; init; } = ClientRestartText.NotNow;

    public bool Shown => Kind != ClientRestartKind.None;

    /// <summary>A short stable key for list caches (the kind, the count and where it returns).</summary>
    public string Key =>
        ((int)Kind).ToString(CultureInfo.InvariantCulture) + "-" + Count.ToString(CultureInfo.InvariantCulture) + "-" + ReturnTo
        + "-" + (Manual ? "m" : "a") + "-" + ReturnName.Replace(";", "").Replace("=", "");

    public static readonly ClientRestartOffer None = new() { Kind = ClientRestartKind.None };

    /// <summary>The bar for these facts. Game-free.</summary>
    public static ClientRestartOffer Decide(ClientRestartFacts facts)
    {
        var pending = facts?.Pending ?? Array.Empty<PendingClientChange>();
        if (facts == null || pending.Count == 0) return None;
        var target = facts.Target;
        bool hosted = facts.InWorld && target != null && target.HostedByThisClient;
        string save = (facts.HostedSave ?? "").Trim();
        ClientRestartKind kind;
        string returnTo = ReturnNone, returnName = "", noReturn = "";
        if (!facts.InWorld) kind = ClientRestartKind.Restart;
        else if (hosted)
        {
            if (save.Length > 0)
            {
                kind = ClientRestartKind.RestartAndResume;
                returnTo = ReturnHosted;
                returnName = save;
            }
            else
            {
                kind = ClientRestartKind.RestartToMenu;
                noReturn = ClientRestartText.NoSaveName;
            }
        }
        else if (target != null && target.CanReconnect && (target.Protocol ?? "").Trim().Length > 0)
        {
            kind = ClientRestartKind.RestartAndRejoin;
            returnTo = ReturnDedicated;
            returnName = JoinText.ServerLabel(target.ServerName, target.Endpoint);
        }
        else
        {
            kind = ClientRestartKind.RestartToMenu;
            noReturn = ClientRestartText.NoAddress;
        }

        var plan = facts.Plan;
        bool manual = plan == null || plan.Mode != RelaunchMode.Automatic;
        string why = plan == null ? ClientRestartText.NoRelauncher : (plan.Why ?? "").Trim();
        if (manual && why.Length == 0) why = ClientRestartText.NoRelauncher;
        int count = pending.Count;
        int removalCount = 0;
        foreach (var p in pending) if (p != null && p.IsRemoval) removalCount++;
        bool removals = removalCount > 0;

        return new ClientRestartOffer
        {
            Kind = kind,
            Count = count,
            Pending = pending,
            ReturnTo = returnTo,
            ReturnName = returnName,
            Target = target,
            Manual = manual,
            ManualWhy = manual ? why : "",
            NoReturnWhy = noReturn,
            Sentence = ClientRestartText.Sentence(count, removals, kind),
            Button = ClientRestartText.Button(kind, returnName),
            ConfirmTitle = manual ? ClientRestartText.ManualTitle : ClientRestartText.ConfirmTitle(count, removalCount),
            ConfirmBody = manual ? ClientRestartText.ManualBody(why, kind, returnName) : ClientRestartText.ConfirmBody(kind, returnName, noReturn),
            ConfirmYes = manual ? ClientRestartText.QuitToDesktop : ClientRestartText.RestartYes,
        };
    }

    /// <summary>The bridge's <c>pendingRestart</c> readback.</summary>
    public Dictionary<string, object?> Describe()
    {
        var mods = new List<object?>();
        foreach (var p in Pending)
            mods.Add(new Dictionary<string, object?> { ["id"] = p.Id, ["name"] = p.Name, ["version"] = p.Version, ["action"] = p.Action });
        return new Dictionary<string, object?>
        {
            ["count"] = Count,
            ["mods"] = mods,
            ["kind"] = ClientRestartText.KindName(Kind),
            ["shown"] = Shown,
            ["canRestart"] = Shown && !Manual,
            ["manual"] = Manual,
            ["manualWhy"] = ManualWhy,
            ["sentence"] = Sentence,
            ["button"] = Button,
            ["confirm"] = Shown
                ? new Dictionary<string, object?> { ["title"] = ConfirmTitle, ["body"] = ConfirmBody, ["yes"] = ConfirmYes, ["no"] = ConfirmNo }
                : null,
            ["rejoinTarget"] = ReturnTo == ReturnNone
                ? new Dictionary<string, object?> { ["kind"] = ReturnNone, ["why"] = NoReturnWhy }
                : ReturnTo == ReturnHosted
                    ? new Dictionary<string, object?> { ["kind"] = ReturnHosted, ["save"] = ReturnName }
                    : new Dictionary<string, object?>
                    {
                        ["kind"] = ReturnDedicated,
                        ["name"] = ReturnName,
                        ["address"] = Target?.Address ?? "",
                        ["port"] = Target?.Port ?? 0,
                        ["protocol"] = JoinTarget.CanonicalProtocol(Target?.Protocol),
                        ["needsPassword"] = Target?.NeedsPassword ?? false,
                    },
        };
    }
}

/// <summary>INSTALLRESTART1: every sentence of the bar, the confirmation and the after-restart line (US English).</summary>
internal static class ClientRestartText
{
    public const string RestartNow = "Restart now";
    public const string RestartYes = "Restart";
    public const string NotNow = "Not now";
    public const string QuitToDesktop = "Quit to desktop";
    public const string ManualTitle = "V Rising can't restart itself here";
    public const string NoRelauncher = "the restart helper is not available";
    public const string NoSaveName = "Progenitor doesn't know this private game's save name.";
    public const string NoAddress = "this connection can't be dialed again.";
    public const string RestartFinishedTitle = "Restart finished";

    public static string KindName(ClientRestartKind kind) => kind switch
    {
        ClientRestartKind.Restart => "restart",
        ClientRestartKind.RestartAndRejoin => "restart-rejoin",
        ClientRestartKind.RestartAndResume => "restart-resume",
        ClientRestartKind.RestartToMenu => "restart-to-menu",
        _ => "none",
    };

    private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>2 mods load after a restart</c>; with a removal, <c>2 mod changes apply after a restart</c>.</summary>
    public static string Sentence(int count, bool removals, ClientRestartKind kind)
    {
        string s = removals
            ? Count(count) + (count == 1 ? " mod change applies" : " mod changes apply") + " after a restart"
            : Count(count) + (count == 1 ? " mod loads" : " mods load") + " after a restart";
        return kind == ClientRestartKind.RestartToMenu ? s + " · you'll return to the menu" : s;
    }

    public static string Button(ClientRestartKind kind, string name) => kind switch
    {
        ClientRestartKind.RestartAndRejoin => "Restart and rejoin " + name,
        ClientRestartKind.RestartAndResume => "Restart and resume " + name,
        _ => RestartNow,
    };

    /// <summary>
    /// <c>Restart V Rising to finish installing 2 mods?</c>; F29 (live 2026-10-08): with removals it names both kinds,
    /// <c>Restart V Rising to finish 1 install and 1 removal?</c>, or <c>…to finish removing 1 mod?</c>.
    /// </summary>
    public static string ConfirmTitle(int count, int removalCount)
    {
        int removals = Math.Max(0, Math.Min(count, removalCount));
        int installs = count - removals;
        if (removals == 0) return "Restart V Rising to finish installing " + Count(count) + (count == 1 ? " mod?" : " mods?");
        if (installs == 0) return "Restart V Rising to finish removing " + Count(removals) + (removals == 1 ? " mod?" : " mods?");
        return "Restart V Rising to finish " + Count(installs) + (installs == 1 ? " install" : " installs") + " and "
            + Count(removals) + (removals == 1 ? " removal?" : " removals?");
    }

    public static string ConfirmBody(ClientRestartKind kind, string name, string noReturnWhy) => kind switch
    {
        ClientRestartKind.RestartAndRejoin => "You'll rejoin " + name + " automatically.",
        ClientRestartKind.RestartAndResume => "You'll return to " + name + " automatically: V Rising continues your private game.",
        ClientRestartKind.RestartToMenu => "You'll return to the main menu" + (noReturnWhy.Length > 0 ? ": " + noReturnWhy : "."),
        _ => "V Rising closes and starts again.",
    };

    /// <summary>The relauncher cannot run: the reason, and the manual-start sentence.</summary>
    public static string ManualBody(string why, ClientRestartKind kind, string name)
    {
        string reason = (why ?? "").Trim();
        if (reason.Length > 0) reason = char.ToUpperInvariant(reason[0]) + reason.Substring(1) + ". ";
        string after = kind switch
        {
            ClientRestartKind.RestartAndRejoin => " Start it again within 10 minutes and Progenitor will rejoin " + name + ".",
            ClientRestartKind.RestartAndResume => " Start it again within 10 minutes and Progenitor will resume " + name + ".",
            _ => " Start it again to finish installing.",
        };
        return reason + "Quit to desktop, then start V Rising from Steam." + after;
    }

    /// <summary>After the restart: <c>Installed: ChatSkin. Rejoined Night Garden.</c></summary>
    public static string AfterLine(IReadOnlyList<string> installed, IReadOnlyList<string> removed, IReadOnlyList<string> notApplied, string returned)
    {
        var parts = new List<string>(4);
        if (installed.Count > 0) parts.Add("Installed: " + string.Join(", ", installed) + ".");
        if (removed.Count > 0) parts.Add("Removed: " + string.Join(", ", removed) + ".");
        if (notApplied.Count > 0) parts.Add("Not loaded: " + string.Join(", ", notApplied) + ".");
        if ((returned ?? "").Trim().Length > 0) parts.Add(returned!.Trim());
        return string.Join(" ", parts);
    }

    public static string Rejoined(string name) => "Rejoined " + name + ".";
    public static string Resumed(string name) => "Resumed " + name + ".";

    /// <summary>The Rejoin page's title while it resumes: <c>Resuming Night Garden</c>.</summary>
    public static string ResumeTitle(string name) => "Resuming " + (string.IsNullOrWhiteSpace(name) ? "your private game" : name.Trim());

    public const string ResumeStarting = "Continuing your private game…";

    public static string ResumeMismatch(string shown, string wanted) =>
        "The title screen's Continue is for " + (shown.Length > 0 ? shown : "nothing") + ", not " + wanted
        + ". Start " + wanted + " from Play.";
}

/// <summary>
/// INSTALLRESTART1: <c>BepInEx/config/progenitor-install-restart.json</c> — the client changes a Restart now (or Restart and
/// rejoin / resume) waited on, for the after-restart line. Written just before the quit; read once at the next title screen.
/// Plain values only (no password, no address).
/// </summary>
internal sealed class InstallRestartRecord
{
    public const int Schema = 1;
    public const string FileName = "progenitor-install-restart.json";
    public const int MaxBytes = 64 * 1024;
    public const int MaxMods = 64;

    public string Created { get; set; } = "";
    public bool DryRun { get; set; }

    /// <summary><c>none</c>, <c>dedicated</c> or <c>hosted</c>.</summary>
    public string ReturnTo { get; set; } = ClientRestartOffer.ReturnNone;

    public string ReturnName { get; set; } = "";

    /// <summary>REJOINSELF1: the process that wrote it (<see cref="RejoinStore.ProcessStamp"/>); empty in older records.</summary>
    public string Writer { get; set; } = "";

    public List<PendingClientChange> Mods { get; set; } = new();

    public static InstallRestartRecord From(ClientRestartOffer offer, DateTime nowUtc, bool dryRun)
    {
        var r = new InstallRestartRecord
        {
            Created = ModManifestJson.FormatIssued(nowUtc),
            DryRun = dryRun,
            ReturnTo = offer.ReturnTo,
            ReturnName = offer.ReturnName,
        };
        foreach (var p in offer.Pending)
            if (p != null && r.Mods.Count < MaxMods)
                r.Mods.Add(new PendingClientChange { Id = p.Id, Name = p.Name, Version = p.Version, Action = p.Action });
        return r;
    }

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("created", Created);
            w.WriteBoolean("dryRun", DryRun);
            w.WriteStartArray("mods");
            foreach (var m in Mods)
            {
                w.WriteStartObject();
                w.WriteString("action", m.Action);
                w.WriteString("id", m.Id);
                w.WriteString("name", m.Name);
                w.WriteString("version", m.Version);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteString("returnName", ReturnName);
            w.WriteString("returnTo", ReturnTo);
            w.WriteNumber("schema", Schema);
            w.WriteString("writer", Writer);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static bool TryParse(string? json, out InstallRestartRecord? record)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(json) || json!.Length > MaxBytes) return false;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 6 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int s) || s != Schema) return false;
            var r = new InstallRestartRecord
            {
                Created = Str(root, "created"),
                DryRun = root.TryGetProperty("dryRun", out var d) && d.ValueKind == JsonValueKind.True,
                ReturnTo = Str(root, "returnTo"),
                ReturnName = Str(root, "returnName"),
                Writer = Str(root, "writer").Trim(),
            };
            if (root.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Array)
                foreach (var el in mods.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object || r.Mods.Count >= MaxMods) continue;
                    r.Mods.Add(new PendingClientChange { Id = Str(el, "id"), Name = Str(el, "name"), Version = Str(el, "version"), Action = Str(el, "action") });
                }

            record = r;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Written at most <see cref="RejoinStore.MaxAge"/> ago (and not more than a minute ahead).</summary>
    public bool Fresh(DateTime nowUtc)
    {
        if (!ModManifestJson.TryParseIssued(Created, out var created)) return false;
        var age = nowUtc - created;
        return age <= RejoinStore.MaxAge && age >= -TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// The after-restart line's three lists: installs and updates this start runs, removals it no longer runs, and the rest.
    /// A row is matched by its plugin id, else by its name.
    /// </summary>
    public (List<string> Installed, List<string> Removed, List<string> NotApplied) Outcome(Func<PendingClientChange, bool> runsNow)
    {
        var installed = new List<string>();
        var removed = new List<string>();
        var not = new List<string>();
        foreach (var m in Mods)
        {
            bool runs;
            try { runs = runsNow?.Invoke(m) ?? false; }
            catch { runs = false; }
            string name = m.Name.Length > 0 ? m.Name : m.Id;
            if (m.IsRemoval) (runs ? not : removed).Add(name);
            else (runs ? installed : not).Add(name);
        }

        return (installed, removed, not);
    }

    public static void Write(string path, InstallRestartRecord record)
    {
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

    public static InstallRestartRecord? Read(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || new FileInfo(path).Length > MaxBytes) return null;
            return TryParse(File.ReadAllText(path), out var r) ? r : null;
        }
        catch { return null; }
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";
}

/// <summary>
/// INSTALLRESTART1: Restart and resume, after the relaunch. At the title screen it checks that the game's own Continue is
/// for the recorded save (<c>_ContinueText</c> names it), presses Continue (<c>MainMenuNewView.ContinueButton_OnClick</c>,
/// which hosts the newest save through <c>HostGame</c> with the save's own settings), and watches the private game come up.
/// Game-free; the live side is <see cref="IJoinConnectHost"/>.
/// </summary>
internal sealed class JoinResume
{
    /// <summary>How long the title screen may take to fill its Continue caption.</summary>
    public const double ContinueWaitSeconds = 10;

    /// <summary>Hosting starts a server process first: the world can take a while.</summary>
    public const double StartTimeoutSeconds = 180;

    /// <summary>A world that has stood this long counts as resumed even before the handshake answers.</summary>
    public const double WorldSettleSeconds = 20;

    private readonly IJoinConnectHost _host;
    private double _phaseStart;
    private double _worldSince = -1;

    public JoinResume(string save, IJoinConnectHost host, double now)
    {
        Save = (save ?? "").Trim();
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _phaseStart = now;
        Phase = "waiting";
    }

    public string Save { get; }

    /// <summary><c>waiting</c> (for the title screen), <c>starting</c>, <c>joined</c>, <c>failed</c>, <c>canceled</c>.</summary>
    public string Phase { get; private set; }

    public string Error { get; private set; } = "";

    /// <summary>The Continue caption read at the title screen (readback).</summary>
    public string ContinueSeen { get; private set; } = "";

    public bool Pressed { get; private set; }

    public bool Finished => Phase is "joined" or "failed" or "canceled";

    public int Progress(double now) => Phase switch
    {
        "waiting" => 5,
        "starting" => 20 + (int)Math.Min(70, Math.Max(0, now - _phaseStart) * 70 / 90),
        "joined" => 100,
        _ => 0,
    };

    /// <summary>The caption names the save (trimmed, case-insensitive).</summary>
    public static bool Matches(string? shown, string save) =>
        string.Equals((shown ?? "").Trim(), (save ?? "").Trim(), StringComparison.OrdinalIgnoreCase) && (save ?? "").Trim().Length > 0;

    public void Tick(double now)
    {
        if (Finished) return;
        switch (Phase)
        {
            case "waiting":
                bool menu;
                try { menu = _host.MainMenuReady; }
                catch { menu = false; }
                if (!menu)
                {
                    if (now - _phaseStart > 60) Fail("the title screen did not come up");
                    return;
                }

                string? shown;
                try { shown = _host.ContinueText; }
                catch { shown = null; }
                ContinueSeen = (shown ?? "").Trim();
                if (!Matches(shown, Save))
                {
                    // The caption is filled by the title screen; give it a moment before calling it a mismatch.
                    if (now - _phaseStart < ContinueWaitSeconds) return;
                    Fail(ClientRestartText.ResumeMismatch(ContinueSeen, Save));
                    return;
                }

                string error = "";
                bool ok;
                try { ok = _host.PressContinue(out error); }
                catch (Exception e)
                {
                    ok = false;
                    error = e.Message;
                }

                if (!ok)
                {
                    Fail(error.Length > 0 ? error : "the title screen's Continue did not start");
                    return;
                }

                Pressed = true;
                Phase = "starting";
                _phaseStart = now;
                return;
            case "starting":
                bool world;
                try { world = _host.WorldExists; }
                catch { world = false; }
                bool joined;
                try { joined = _host.JoinedOk; }
                catch { joined = false; }
                if (world && _worldSince < 0) _worldSince = now;
                if (!world) _worldSince = -1;
                if (joined || (world && now - _worldSince >= WorldSettleSeconds))
                {
                    Phase = "joined";
                    return;
                }

                if (now - _phaseStart > StartTimeoutSeconds) Fail("the private game did not start in time");
                return;
        }
    }

    public void Cancel()
    {
        if (Finished) return;
        Phase = "canceled";
    }

    private void Fail(string why)
    {
        Error = why ?? "";
        Phase = "failed";
    }
}
