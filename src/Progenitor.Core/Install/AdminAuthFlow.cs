using System;

namespace Progenitor.Install;

/// <summary>What an admin-gated control shows a player instead of a dead-end lock.</summary>
internal enum AdminAuthOfferKind
{
    /// <summary>Nothing to add: not gated, already authenticated, or no word from the server yet.</summary>
    None = 0,

    /// <summary>On the server's admin list, not authenticated, button enabled: "Authenticate as admin".</summary>
    Button = 1,

    /// <summary>On the list, not authenticated, button switched off: tell them to type adminauth.</summary>
    TypeCommand = 2,

    /// <summary>Not on the list: ask the host. No button.</summary>
    AskHost = 3,
}

/// <summary>Where the authentication attempt is.</summary>
internal enum AdminAuthState
{
    Idle = 0,
    Authenticating = 1,
    Done = 2,
    Failed = 3,
}

/// <summary>
/// REMOTEINST1 (user-approved): an "Authenticate as admin" button for players on the server's admin
/// list who have not run <c>adminauth</c>. Remote installs and ServerSettings administrator edits no
/// longer need it (they authorize by list membership, <see cref="ServerAdminList.AuthorizeShared"/>),
/// but Stop server's graceful path and adminOnly commands still gate on <c>User.IsAdmin</c>, which
/// only the game's own <c>adminauth</c> sets.
///
/// <para>The button performs the game's own command — the same call the game's debug menu makes,
/// <c>StunConsole.Commands.TryCommand("adminauth", false)</c> — so the server's <c>AdminAuthSystem</c>
/// checks its list and sets <c>IsAdmin</c>. Progenitor never sets it. That call was checked live on
/// 2026-10-01, so <c>[Ui] AdminAuthButton</c> defaults to on; with it off the help tells the
/// player to type <c>adminauth</c>. Game-free: the call is an injected <see cref="Executor"/>.</para>
/// </summary>
internal sealed class AdminAuthFlow
{
    public const string ConfigSection = "Ui";
    public const string ConfigKey = "AdminAuthButton";
    public const string ConfigDescription =
        "Show an \"Authenticate as admin\" button for players on the server's admin list who have not run adminauth. "
        + "It runs the game's own adminauth console command (StunConsole.Commands.TryCommand). On by default since the live check 2026-10-01; "
        + "off, the help text says to type adminauth in the console.";

    public const string ButtonCaption = "Authenticate as admin";
    public const string AuthenticatingText = "Authenticating…";
    public const string DoneText = "You are now authenticated as admin on this server.";
    public const string TypeCommandHelp =
        "You're on this server's admin list. Type adminauth in the game's console to unlock admin features (server settings, Stop server, admin commands).";
    public const string AskHostHelp =
        "Admin features need you on this server's admin list. Ask the host to add your SteamID to adminlist.txt.";
    public const string FailedNoAnswer =
        "The server did not accept adminauth. Check that you are on its admin list; the game's chat may say why.";
    public const string FailedNoCommand = "The game's console command could not be run.";

    /// <summary>How long after the click the server's status must say authenticated.</summary>
    public const double TimeoutSeconds = 8;

    public static AdminAuthFlow Shared { get; } = new();

    private readonly object _gate = new();
    private AdminAuthState _state;
    private string _message = "";
    private double _startedAt;

    /// <summary><c>[Ui] AdminAuthButton</c>.</summary>
    internal bool Enabled { get; set; }

    /// <summary>The game's own adminauth. Returns false when it could not be run. Null on a process without it.</summary>
    internal Func<bool>? Executor { get; set; }

    /// <summary>Asks the server for a fresh status (the answer says whether adminauth took).</summary>
    internal Func<bool>? RequestStatus { get; set; }

    internal Func<double> Now { get; set; } =
        () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    public AdminAuthState State
    {
        get { lock (_gate) return _state; }
    }

    public string Message
    {
        get { lock (_gate) return _message; }
    }

    /// <summary>
    /// What one admin-gated control should show, from the server's word for this player.
    /// <paramref name="featureGated"/>: the control is actually locked or refused for want of IsAdmin.
    /// </summary>
    public static AdminAuthOfferKind Offer(RemoteInstallStatus? status, bool flagOn, bool featureGated)
    {
        if (!featureGated || status == null) return AdminAuthOfferKind.None;
        if (status.AdminAuthed) return AdminAuthOfferKind.None;
        if (status.OnAdminList) return flagOn ? AdminAuthOfferKind.Button : AdminAuthOfferKind.TypeCommand;
        return AdminAuthOfferKind.AskHost;
    }

    public static string HelpText(AdminAuthOfferKind kind) => kind switch
    {
        AdminAuthOfferKind.TypeCommand => TypeCommandHelp,
        AdminAuthOfferKind.AskHost => AskHostHelp,
        _ => "",
    };

    /// <summary>The button's press. False (with <see cref="Message"/>) when it could not start.</summary>
    public bool Begin()
    {
        if (!Enabled)
        {
            Set(AdminAuthState.Failed, TypeCommandHelp);
            return false;
        }

        var run = Executor;
        bool ran;
        try { ran = run != null && run(); }
        catch { ran = false; }
        if (!ran)
        {
            Set(AdminAuthState.Failed, FailedNoCommand);
            return false;
        }

        lock (_gate)
        {
            _state = AdminAuthState.Authenticating;
            _message = AuthenticatingText;
            _startedAt = Now();
        }

        try { RequestStatus?.Invoke(); }
        catch { /* the next push answers too */ }
        return true;
    }

    /// <summary>A status arrived (or the UI polls): settle the attempt.</summary>
    public void Observe(RemoteInstallStatus? status)
    {
        lock (_gate)
        {
            if (_state != AdminAuthState.Authenticating) return;
            if (status != null && status.AdminAuthed)
            {
                _state = AdminAuthState.Done;
                _message = DoneText;
                return;
            }

            if (Now() - _startedAt >= TimeoutSeconds)
            {
                _state = AdminAuthState.Failed;
                _message = FailedNoAnswer;
            }
        }
    }

    public void Reset() => Set(AdminAuthState.Idle, "");

    internal void ResetForTests()
    {
        Reset();
        Enabled = false;
        Executor = null;
        RequestStatus = null;
        Now = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    }

    private void Set(AdminAuthState state, string message)
    {
        lock (_gate)
        {
            _state = state;
            _message = message ?? "";
        }
    }

    public string StateName => State switch
    {
        AdminAuthState.Authenticating => "authenticating",
        AdminAuthState.Done => "done",
        AdminAuthState.Failed => "failed",
        _ => "idle",
    };
}
