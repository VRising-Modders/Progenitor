using System;
using System.Collections.Generic;

namespace Progenitor.Inventory;

/// <summary>How a container window was asked for (<c>ctx.Inventory.OnOpenRequest</c>).</summary>
public enum ContainerOpenKind
{
    /// <summary>The player interacted with the container in the world (a chest, a station, a castle heart).</summary>
    Interact = 0,

    /// <summary>The remote container preview (<c>ctx.Inventory.Preview</c>) asked for it.</summary>
    Preview = 1,
}

/// <summary>What an open handler answered.</summary>
public enum ContainerOpenVerdict
{
    /// <summary>This handler has no objection.</summary>
    Allow = 0,

    /// <summary>The open is refused; <see cref="ContainerOpenDecision.Reason"/> is shown to the player.</summary>
    Deny = 1,

    /// <summary>The player must answer a prompt first (a password, for example).</summary>
    Challenge = 2,
}

/// <summary>
/// One open handler's answer (<c>ctx.Inventory.OnOpenRequest</c>): <see cref="Allow"/>, <see cref="Deny"/> with a
/// player-facing sentence, or <see cref="Challenge"/> with a token the handler gets back with the player's answer.
/// </summary>
public readonly struct ContainerOpenDecision
{
    /// <summary>Longest prompt title, body or refusal sentence sent to the client; longer text is cut.</summary>
    public const int MaxText = 200;

    /// <summary>Longest token kept for the answer handler; a longer one is cut.</summary>
    public const int MaxToken = 256;

    /// <summary>Default and upper bound of <see cref="MaxLength"/>.</summary>
    public const int DefaultMaxLength = 64;

    /// <summary>Upper bound of <see cref="MaxLength"/>.</summary>
    public const int MaxAnswerLength = 128;

    private ContainerOpenDecision(ContainerOpenVerdict verdict, string reason, string token, string title, string prompt, int maxLength,
        bool offerRemember = false)
    {
        OfferRemember = offerRemember;
        Verdict = verdict;
        Reason = reason;
        Token = token;
        Title = title;
        Prompt = prompt;
        MaxLength = maxLength;
    }

    /// <summary>What the handler answered.</summary>
    public ContainerOpenVerdict Verdict { get; }

    /// <summary><see cref="ContainerOpenVerdict.Deny"/>: the sentence the player reads.</summary>
    public string Reason { get; }

    /// <summary><see cref="ContainerOpenVerdict.Challenge"/>: handed back to the answer handler (which lock, which salt).</summary>
    public string Token { get; }

    /// <summary><see cref="ContainerOpenVerdict.Challenge"/>: the prompt's title.</summary>
    public string Title { get; }

    /// <summary><see cref="ContainerOpenVerdict.Challenge"/>: the line under the title (may be empty).</summary>
    public string Prompt { get; }

    /// <summary><see cref="ContainerOpenVerdict.Challenge"/>: the most characters the masked field takes.</summary>
    public int MaxLength { get; }

    /// <summary>
    /// <see cref="ContainerOpenVerdict.Challenge"/>: the prompt shows a <b>Remember password</b> box; the player's choice
    /// reaches the answer handler (<c>ContainerOpenAnswer.Remember</c>). Keeping it is the plugin's own business: store
    /// your own grant and answer <see cref="Allow"/> next time. The gate persists nothing.
    /// </summary>
    public bool OfferRemember { get; }

    /// <summary>No objection.</summary>
    public static ContainerOpenDecision Allow => default;

    /// <summary>Refuse the open; <paramref name="reason"/> is shown to the player (a default sentence when empty).</summary>
    public static ContainerOpenDecision Deny(string reason) =>
        new(ContainerOpenVerdict.Deny, ContainerGateText.Clip(string.IsNullOrWhiteSpace(reason) ? ContainerGateText.Locked : reason), "", "", "", 0);

    /// <summary>
    /// Ask the player first. The client shows a masked prompt with <paramref name="title"/> and
    /// <paramref name="prompt"/>; the answer reaches this plugin's answer handler with <paramref name="token"/>.
    /// <paramref name="maxLength"/> caps the field (1 to <see cref="MaxAnswerLength"/>; anything else uses
    /// <see cref="DefaultMaxLength"/>).
    /// </summary>
    public static ContainerOpenDecision Challenge(string token, string title = "Password", string prompt = "", int maxLength = DefaultMaxLength,
        bool offerRemember = false)
    {
        string t = token ?? "";
        if (t.Length > MaxToken) t = t.Substring(0, MaxToken);
        return new(ContainerOpenVerdict.Challenge, "", t,
            ContainerGateText.Clip(string.IsNullOrWhiteSpace(title) ? "Password" : title),
            ContainerGateText.Clip(prompt ?? ""),
            maxLength is >= 1 and <= MaxAnswerLength ? maxLength : DefaultMaxLength, offerRemember);
    }

    /// <inheritdoc />
    public override string ToString() => Verdict switch
    {
        ContainerOpenVerdict.Deny => "Deny(" + Reason + ")",
        ContainerOpenVerdict.Challenge => "Challenge(" + Title + ")",
        _ => "Allow",
    };
}

/// <summary>
/// The text a player typed into a challenge prompt. <see cref="ToString"/> never shows it, so it cannot reach a log
/// line by accident; <see cref="Reveal"/> returns it for hashing. Progenitor never stores or logs it.
/// </summary>
public readonly struct ChallengeSecret
{
    private readonly string? _text;

    internal ChallengeSecret(string? text) => _text = text ?? "";

    /// <summary>The typed text, for a salted hash compare. Do not log or store it.</summary>
    public string Reveal() => _text ?? "";

    /// <summary>How many characters were typed.</summary>
    public int Length => (_text ?? "").Length;

    /// <summary>True when nothing was typed.</summary>
    public bool IsEmpty => Length == 0;

    /// <summary>Always <c>(hidden)</c>.</summary>
    public override string ToString() => "(hidden)";
}

/// <summary>An answer handler's verdict on a typed answer.</summary>
public readonly struct ContainerAnswerVerdict
{
    private ContainerAnswerVerdict(bool accepted, bool remember, string reason)
    {
        Accepted = accepted;
        RememberForSession = remember;
        Reason = reason;
    }

    /// <summary>True when the answer opens the container.</summary>
    public bool Accepted { get; }

    /// <summary>
    /// Accepted: also remember the answer for this (player, container) until the player disconnects, so later opens of
    /// this container are not prompted by this plugin again.
    /// </summary>
    public bool RememberForSession { get; }

    /// <summary>Rejected: the sentence shown to the player.</summary>
    public string Reason { get; }

    /// <summary>The answer is right. With <paramref name="rememberForSession"/> (default) the player is not asked again this session.</summary>
    public static ContainerAnswerVerdict Accept(bool rememberForSession = true) => new(true, rememberForSession, "");

    /// <summary>The answer is wrong; <paramref name="reason"/> is shown to the player.</summary>
    public static ContainerAnswerVerdict Reject(string reason = "Wrong password.") =>
        new(false, false, ContainerGateText.Clip(string.IsNullOrWhiteSpace(reason) ? ContainerGateText.Wrong : reason));

    /// <inheritdoc />
    public override string ToString() => Accepted ? "Accept" + (RememberForSession ? " (session)" : "") : "Reject(" + Reason + ")";
}

/// <summary>The gate's own sentences, and the clip every sentence goes through.</summary>
internal static class ContainerGateText
{
    internal const string Locked = "This container is locked.";
    internal const string Wrong = "Wrong password.";
    internal const string CheckFailed = "This container could not be checked. Try again.";
    internal const string NotReady = "This container is not ready yet. Try again in a moment.";
    internal const string Canceled = "Canceled.";
    internal const string TimedOut = "The prompt timed out.";
    internal const string TooMany = "Too many tries. Wait a minute and try again.";
    internal const string Changed = "The lock changed. Try again.";
    internal const string Unlocked = "Unlocked. Interact again to open it.";
    internal const string Replaced = "Another open replaced this one.";

    internal static string Clip(string? text)
    {
        string t = (text ?? "").Trim();
        return t.Length <= ContainerOpenDecision.MaxText ? t : t.Substring(0, ContainerOpenDecision.MaxText);
    }
}

/// <summary>One open handler in the gate's book, in registration order.</summary>
internal sealed class ContainerGateHandler<TRequest>
{
    internal ContainerGateHandler(object owner, string pluginId, string pluginName, long order,
        Func<TRequest, ContainerOpenDecision> decide, Func<TRequest, string, ChallengeSecret, bool, ContainerAnswerVerdict>? answer)
    {
        Owner = owner;
        PluginId = pluginId ?? "";
        PluginName = string.IsNullOrWhiteSpace(pluginName) ? PluginId : pluginName;
        Order = order;
        Decide = decide;
        Answer = answer;
    }

    internal object Owner { get; }
    internal string PluginId { get; }
    internal string PluginName { get; }
    internal long Order { get; }
    internal Func<TRequest, ContainerOpenDecision> Decide { get; }
    internal Func<TRequest, string, ChallengeSecret, bool, ContainerAnswerVerdict>? Answer { get; }
    internal bool Removed { get; set; }
}

/// <summary>One prompt the player still has to answer, owned by one handler.</summary>
internal sealed class ContainerGateStep<TRequest>
{
    internal ContainerGateStep(ContainerGateHandler<TRequest> handler, ContainerOpenDecision decision)
    {
        Handler = handler;
        Decision = decision;
    }

    internal ContainerGateHandler<TRequest> Handler { get; }
    internal ContainerOpenDecision Decision { get; }
}

/// <summary>The combined answer of every handler for one open (deny wins, then challenges, then allow).</summary>
internal sealed class ContainerGateOutcome<TRequest>
{
    internal ContainerOpenVerdict Verdict { get; init; }

    /// <summary>Deny: the sentence; the first denying handler's, in registration order.</summary>
    internal string Reason { get; init; } = "";

    /// <summary>Deny: the handler that refused (its plugin name prefixes the toast); null for the gate's own refusals.</summary>
    internal string? By { get; init; }

    /// <summary>Challenge: the prompts still to answer, in registration order.</summary>
    internal List<ContainerGateStep<TRequest>> Steps { get; init; } = new();

    internal static ContainerGateOutcome<TRequest> Allowed { get; } = new() { Verdict = ContainerOpenVerdict.Allow };
}

/// <summary>Key of a grant or a pass: one player, one container, one handler's plugin.</summary>
internal readonly record struct ContainerGateKey(ulong Platform, ContainerId Container, string PluginId);

/// <summary>
/// The gate's book, game-free: the handlers, the per-session grants (until disconnect) and the one-shot passes a correct
/// answer leaves for the re-run of the same open. Main thread only. <typeparamref name="TRequest"/> is the request the
/// handlers see (<c>ContainerOpenRequest</c> in the game).
/// </summary>
internal sealed class ContainerGateBook<TRequest>
{
    /// <summary>How long a correct answer's pass waits for the re-run of the same open.</summary>
    internal const double PassSeconds = 30;

    private readonly List<ContainerGateHandler<TRequest>> _handlers = new();
    private readonly HashSet<ContainerGateKey> _grants = new();
    private readonly Dictionary<ContainerGateKey, double> _passes = new();
    private readonly HashSet<string> _warnedNoAnswer = new(StringComparer.Ordinal);
    private long _order;

    /// <summary>A line for the server log (never a typed answer).</summary>
    internal Action<string>? Warn { get; set; }

    internal int Count => _handlers.Count;
    internal int GrantCount => _grants.Count;
    internal int PassCount => _passes.Count;

    internal IReadOnlyList<ContainerGateHandler<TRequest>> Handlers => _handlers;

    internal ContainerGateHandler<TRequest> Add(object owner, string pluginId, string pluginName,
        Func<TRequest, ContainerOpenDecision> decide, Func<TRequest, string, ChallengeSecret, bool, ContainerAnswerVerdict>? answer)
    {
        if (decide == null) throw new ArgumentNullException(nameof(decide));
        var h = new ContainerGateHandler<TRequest>(owner, pluginId, pluginName, ++_order, decide, answer);
        _handlers.Add(h);
        return h;
    }

    /// <summary>Remove one handler; its plugin's grants and passes go too when it has no other handler.</summary>
    internal bool Remove(ContainerGateHandler<TRequest> handler)
    {
        if (handler == null || !_handlers.Remove(handler)) return false;
        handler.Removed = true;
        foreach (var h in _handlers)
            if (string.Equals(h.PluginId, handler.PluginId, StringComparison.Ordinal)) return true;
        RevokeWhere(k => string.Equals(k.PluginId, handler.PluginId, StringComparison.Ordinal));
        return true;
    }

    /// <summary>
    /// Every handler's decision for one open. A handler that throws refuses (fail closed). A handler with a grant or a
    /// live pass for this (player, container) has its challenge counted as answered; a pass is spent only when the open
    /// is allowed. A challenge from a handler without an answer handler refuses.
    /// </summary>
    internal ContainerGateOutcome<TRequest> Evaluate(TRequest request, ulong platform, ContainerId container, double now,
        Func<TRequest, bool, TRequest>? withGrant = null)
    {
        if (_handlers.Count == 0) return ContainerGateOutcome<TRequest>.Allowed;
        ExpirePasses(now);
        var steps = new List<ContainerGateStep<TRequest>>();
        var passesUsed = new List<ContainerGateKey>();
        ContainerGateOutcome<TRequest>? denied = null;
        foreach (var h in _handlers.ToArray())
        {
            var key = new ContainerGateKey(platform, container, h.PluginId);
            bool granted = _grants.Contains(key);
            bool passed = _passes.ContainsKey(key);
            ContainerOpenDecision d;
            try { d = h.Decide(withGrant != null ? withGrant(request, granted) : request); }
            catch (Exception e)
            {
                // The type only: a handler's message may carry what it was checking.
                Warn?.Invoke("ContainerOpenGate: " + h.PluginName + "'s open handler threw (the open is refused): " + e.GetType().Name);
                d = ContainerOpenDecision.Deny(ContainerGateText.CheckFailed);
            }

            if (d.Verdict == ContainerOpenVerdict.Challenge && h.Answer == null)
            {
                if (_warnedNoAnswer.Add(h.PluginId))
                    Warn?.Invoke("ContainerOpenGate: " + h.PluginName + " answered Challenge without an answer handler; the open is refused.");
                d = ContainerOpenDecision.Deny(ContainerGateText.Locked);
            }

            switch (d.Verdict)
            {
                case ContainerOpenVerdict.Deny:
                    denied ??= new ContainerGateOutcome<TRequest> { Verdict = ContainerOpenVerdict.Deny, Reason = d.Reason, By = h.PluginName };
                    break;
                case ContainerOpenVerdict.Challenge:
                    if (granted) break;
                    if (passed) { passesUsed.Add(key); break; }
                    steps.Add(new ContainerGateStep<TRequest>(h, d));
                    break;
            }
        }

        if (denied != null) return denied;
        if (steps.Count > 0) return new ContainerGateOutcome<TRequest> { Verdict = ContainerOpenVerdict.Challenge, Steps = steps };
        foreach (var k in passesUsed) _passes.Remove(k);
        return ContainerGateOutcome<TRequest>.Allowed;
    }

    /// <summary>A correct answer: a pass for the re-run, and a session grant when the handler asked for one.</summary>
    internal void Accepted(ulong platform, ContainerId container, string pluginId, bool remember, double now)
    {
        var key = new ContainerGateKey(platform, container, pluginId ?? "");
        _passes[key] = now + PassSeconds;
        if (remember) _grants.Add(key);
    }

    internal bool HasGrant(ulong platform, ContainerId container, string pluginId) =>
        _grants.Contains(new ContainerGateKey(platform, container, pluginId ?? ""));

    /// <summary>A player left: their grants and passes go.</summary>
    internal int ForgetPlayer(ulong platform) => RevokeWhere(k => k.Platform == platform);

    /// <summary>Drop every grant and pass for <paramref name="container"/> of one plugin (a password changed).</summary>
    internal int RevokeContainer(string pluginId, ContainerId container) =>
        RevokeWhere(k => k.Container == container && string.Equals(k.PluginId, pluginId, StringComparison.Ordinal));

    /// <summary>Drop every grant and pass of one plugin for one player.</summary>
    internal int RevokePlayer(string pluginId, ulong platform) =>
        RevokeWhere(k => k.Platform == platform && string.Equals(k.PluginId, pluginId, StringComparison.Ordinal));

    internal void Clear()
    {
        foreach (var h in _handlers) h.Removed = true;
        _handlers.Clear();
        _grants.Clear();
        _passes.Clear();
        _warnedNoAnswer.Clear();
    }

    private void ExpirePasses(double now)
    {
        if (_passes.Count == 0) return;
        List<ContainerGateKey>? gone = null;
        foreach (var p in _passes)
            if (now >= p.Value || double.IsNaN(p.Value)) (gone ??= new()).Add(p.Key);
        if (gone != null) foreach (var k in gone) _passes.Remove(k);
    }

    private int RevokeWhere(Func<ContainerGateKey, bool> match)
    {
        int n = _grants.RemoveWhere(k => match(k));
        List<ContainerGateKey>? gone = null;
        foreach (var k in _passes.Keys) if (match(k)) (gone ??= new()).Add(k);
        if (gone != null) foreach (var k in gone) { _passes.Remove(k); n++; }
        return n;
    }
}

/// <summary>
/// The extra buffs of refused station interactions (<c>AdditionalInteractBuff</c>) the server still watches, game-free:
/// (character, extra buff prefab, the instance when known) for <see cref="Seconds"/>. Entities are kept as
/// index/version keys. Main thread only.
/// </summary>
internal sealed class ContainerGateExtras
{
    /// <summary>How long a refused interaction's extra buff is watched.</summary>
    internal const double Seconds = 1.0;

    internal readonly record struct Watch(long Character, int Prefab, long Instance, double Until);

    private readonly List<Watch> _watches = new();

    internal int Count => _watches.Count;

    internal void Add(long character, int prefab, long instance, double now)
    {
        if (_watches.Count > 256) _watches.RemoveAt(0);
        _watches.Add(new Watch(character, prefab, instance, now + Seconds));
    }

    internal void Expire(double now) => _watches.RemoveAll(w => now >= w.Until || double.IsNaN(w.Until));

    internal IReadOnlyList<Watch> Live() => _watches.ToArray();

    /// <summary>The character got an allowed interaction: its watches end (its new extra buff is the game's own).</summary>
    internal void ForgetCharacter(long character) => _watches.RemoveAll(w => w.Character == character);

    internal void Clear() => _watches.Clear();
}

/// <summary>
/// The gate's decision for one (player, container) over a short window, game-free. One interaction can bring more than
/// one interact buff for the same container in the same frame; each must get the same answer, and a refusal must not
/// send a second prompt or toast, nor a pass be spent twice. Main thread only.
/// </summary>
internal sealed class ContainerGateRecent
{
    /// <summary>How long a decision is reused for the same (player, container).</summary>
    internal const double Seconds = 0.5;

    private readonly Dictionary<(ulong Platform, ContainerId Container), (double Until, bool Allowed)> _recent = new();

    internal int Count => _recent.Count;

    /// <summary>True with the earlier answer when one for this pair is still fresh.</summary>
    internal bool TryGet(ulong platform, ContainerId container, double now, out bool allowed)
    {
        allowed = false;
        if (!_recent.TryGetValue((platform, container), out var e)) return false;
        if (now >= e.Until || double.IsNaN(e.Until)) { _recent.Remove((platform, container)); return false; }
        allowed = e.Allowed;
        return true;
    }

    internal void Record(ulong platform, ContainerId container, bool allowed, double now)
    {
        if (_recent.Count > 256) Prune(now);
        _recent[(platform, container)] = (now + Seconds, allowed);
    }

    /// <summary>A right answer: the next open of that pair is decided afresh (the pass lets it through).</summary>
    internal void Forget(ulong platform)
    {
        List<(ulong, ContainerId)>? gone = null;
        foreach (var k in _recent.Keys) if (k.Platform == platform) (gone ??= new()).Add(k);
        if (gone != null) foreach (var k in gone) _recent.Remove(k);
    }

    internal void Clear() => _recent.Clear();

    private void Prune(double now)
    {
        List<(ulong, ContainerId)>? gone = null;
        foreach (var p in _recent) if (now >= p.Value.Until) (gone ??= new()).Add(p.Key);
        if (gone != null) foreach (var k in gone) _recent.Remove(k);
    }
}

/// <summary>What happened to a typed answer (<see cref="ContainerGatePending{TRequest}.Answer"/>).</summary>
internal enum ContainerGateAnswerStep
{
    /// <summary>Not this prompt (an old sequence) or nothing pending: ignored.</summary>
    Ignored = 0,

    /// <summary>Right; another handler's prompt comes next (<see cref="ContainerGatePending{TRequest}.Current"/>).</summary>
    Next = 1,

    /// <summary>Right, and it was the last prompt: the open may run again.</summary>
    Done = 2,

    /// <summary>Wrong (or the handler threw, or it is gone): the open ends; the sentence says why.</summary>
    Failed = 3,
}

/// <summary>
/// One player's open that waits for answers, game-free: the prompts in order, the deadline and what each answer did.
/// The server keeps at most one per player; a new one replaces it.
/// </summary>
internal sealed class ContainerGatePending<TRequest>
{
    /// <summary>How long a prompt waits for its answer.</summary>
    internal const double PromptSeconds = 120;

    private readonly List<ContainerGateStep<TRequest>> _steps;
    private int _index;

    internal ContainerGatePending(int sequence, ulong platform, ContainerId container, ContainerOpenKind kind, TRequest request,
        List<ContainerGateStep<TRequest>> steps, double now)
    {
        if (steps == null || steps.Count == 0) throw new ArgumentException("A pending open needs at least one prompt.", nameof(steps));
        Sequence = sequence;
        Platform = platform;
        Container = container;
        Kind = kind;
        Request = request;
        _steps = steps;
        Deadline = now + PromptSeconds;
    }

    internal int Sequence { get; }
    internal ulong Platform { get; }
    internal ContainerId Container { get; }
    internal ContainerOpenKind Kind { get; }
    internal TRequest Request { get; }
    internal double Deadline { get; private set; }
    internal int Index => _index;
    internal int StepCount => _steps.Count;

    /// <summary>The prompt to show now.</summary>
    internal ContainerGateStep<TRequest> Current => _steps[Math.Min(_index, _steps.Count - 1)];

    internal bool Expired(double now) => now >= Deadline;

    /// <summary>
    /// True when any prompt of this open, answered or not, belongs to <paramref name="handler"/>: an unloading plugin's
    /// handler must not stay referenced (a hot reload would keep its generation alive).
    /// </summary>
    internal bool Owns(ContainerGateHandler<TRequest> handler)
    {
        for (int i = 0; i < _steps.Count; i++)
            if (ReferenceEquals(_steps[i].Handler, handler)) return true;
        return false;
    }

    /// <summary>
    /// The player's answer to prompt <paramref name="sequence"/>. Calls the current prompt's answer handler (a throw or a
    /// removed handler fails, closed). Right: records the pass and the grant in <paramref name="book"/>, then the next
    /// prompt or done. Wrong: failed with the handler's sentence.
    /// </summary>
    internal ContainerGateAnswerStep Answer(int sequence, ChallengeSecret secret, ContainerGateBook<TRequest> book, double now,
        out string reason, bool remember = false)
    {
        reason = "";
        if (sequence != Sequence || _index >= _steps.Count) return ContainerGateAnswerStep.Ignored;
        var step = _steps[_index];
        if (step.Handler.Removed || step.Handler.Answer == null)
        {
            reason = ContainerGateText.Changed;
            return ContainerGateAnswerStep.Failed;
        }

        ContainerAnswerVerdict verdict;
        try { verdict = step.Handler.Answer(Request, step.Decision.Token, secret, remember && step.Decision.OfferRemember); }
        catch (Exception e)
        {
            // The type only, never the message: an answer handler's exception may quote what was typed.
            book.Warn?.Invoke("ContainerOpenGate: " + step.Handler.PluginName + "'s answer handler threw (the answer is refused): " + e.GetType().Name);
            reason = ContainerGateText.CheckFailed;
            return ContainerGateAnswerStep.Failed;
        }

        if (!verdict.Accepted)
        {
            reason = string.IsNullOrWhiteSpace(verdict.Reason) ? ContainerGateText.Wrong : verdict.Reason;
            return ContainerGateAnswerStep.Failed;
        }

        book.Accepted(Platform, Container, step.Handler.PluginId, verdict.RememberForSession, now);
        _index++;
        if (_index < _steps.Count)
        {
            Deadline = now + PromptSeconds;
            return ContainerGateAnswerStep.Next;
        }
        return ContainerGateAnswerStep.Done;
    }
}

/// <summary>What the client's repeat of a closed interaction does this frame (<see cref="ContainerGateRetryPlan"/>).</summary>
internal enum ContainerGateRetryStep
{
    /// <summary>Keep hovering the container.</summary>
    Hover = 0,

    /// <summary>Press Interact now (once).</summary>
    Press = 1,

    /// <summary>Done: hand the input back.</summary>
    End = 2,
}

/// <summary>
/// The client's repeat of an interaction the open gate closed, game-free: hover the container for a few frames so the
/// hover reaches the outgoing command, press Interact once (held <see cref="PressSeconds"/>), then hand the input back.
/// The same order the Dev bot driver uses to interact with a hovered object.
/// </summary>
internal sealed class ContainerGateRetryPlan
{
    /// <summary>Frames of hover before the press.</summary>
    internal const int HoverFrames = 3;

    /// <summary>How long Interact is held.</summary>
    internal const float PressSeconds = 0.4f;

    /// <summary>How long the repeat holds the hover and the button in all.</summary>
    internal const double TotalSeconds = 1.0;

    private readonly double _started;
    private int _frames;
    private bool _pressed;

    internal ContainerGateRetryPlan(double now) => _started = now;

    internal bool Pressed => _pressed;

    /// <summary>One frame; <paramref name="windowOpen"/> is true once the container window shows (the repeat worked).</summary>
    internal ContainerGateRetryStep Step(double now, bool windowOpen)
    {
        if (windowOpen && _pressed) return ContainerGateRetryStep.End;
        if (now - _started >= TotalSeconds) return ContainerGateRetryStep.End;
        _frames++;
        if (!_pressed && _frames >= HoverFrames)
        {
            _pressed = true;
            return ContainerGateRetryStep.Press;
        }
        return ContainerGateRetryStep.Hover;
    }
}
