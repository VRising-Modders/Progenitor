using System;
using System.Globalization;
using System.Threading;

#if !PROGENITOR_TESTS
using System.Reflection;
using ProjectM;
using ProjectM.Network;
using Progenitor.Commands;
using Progenitor.Patches;
using Unity.Collections;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Job FSAMP, feature 2. <c>.patchdemo on|off|ping</c> — the first <c>ctx.Patches</c> consumer
/// outside Core's own god-mode guard, so <c>docs/modules.md</c> "Patches" has a live check that
/// is not the kill path.
///
/// <b>The target, and why this one.</b>
/// <c>ProjectM.ServerChatUtils.SendSystemMessageToClient(EntityManager, User, in FixedString512Bytes)</c>
/// — a server method (the sibling overload that takes an
/// <c>EntityCommandBuffer</c> and is <b>not</b> the one patched; both have three parameters, so
/// the overload is selected by the first parameter's type, never by parameter count). It is the
/// right demo target for three reasons: the Sample calls it itself, exactly once per
/// <c>.patchdemo ping</c>, so the prefix firing is caused by the command and not by traffic; it
/// has a unique native address, so it is not the shared empty-body thunk <c>PatchSitePolicy</c> refuses; and
/// nothing in Progenitor routes through it — Core's own <c>Chat</c> module builds the
/// <c>ChatMessageServerEvent</c> entity by hand rather than calling this helper
/// (<c>src/Progenitor.Core/Chat/Chat.cs</c>), so a fired line is unambiguous.
///
/// <b>The prefix is harmless.</b> It returns <c>void</c>: a prefix that cannot return
/// <c>false</c> cannot skip the original, so the worst this demo can do to a server is write one
/// log line per system message the Sample itself sends.
///
/// <b>Why the prefix has this shape (job FHARM5).</b> A Harmony patch pins the assembly its
/// patch method comes from for the life of the process, and <c>Unpatch</c> does not undo that:
/// HarmonyX's <c>StackTraceFixes.RealMethodMap</c> keeps the generated replacement forever for
/// every target, and Il2CppInterop's append-only <c>DelegateCache</c> keeps the trampoline for
/// an IL2CPP one. Measured, with a control. So <c>ctx.Patches</c> never hands your
/// body to Harmony: it parks it in one of <c>Progenitor.Bootstrap.PatchSlots</c>' 32
/// process-lifetime slots and patches with Bootstrap's own static method. That is why the
/// prefix below is <c>(object instance, object[] args)</c> and not a Harmony signature, and why
/// there is no <c>__result</c>. <c>.patchdemo on</c> is no longer a one-way door: <c>off</c>
/// (and every unload) unpatches <em>and</em> releases the slot, and the Sample's ALC collects.
///
/// <b>All three kinds, on the one target (job J14).</b> <c>ctx.Patches.Postfix</c> and
/// <c>ctx.Patches.Finalizer</c> carry the same contract and the same owner tagging as
/// <c>Prefix</c> and had no example anywhere in the repository, so <c>on</c> installs all three
/// at once and every reply reports the three counters separately. That is also the cheapest way
/// to see the shapes side by side: the prefix returns <c>bool</c> (whether the original still
/// runs), the postfix returns <c>void</c> (it cannot change anything), and the finalizer returns
/// <c>Exception?</c> — <b>the one that can silently change the game's behavior</b>.
///
/// <b>The finalizer never swallows.</b> A finalizer's return value replaces the exception the
/// original threw, and returning <c>null</c> makes it vanish: the game's caller then carries on
/// as though the method had succeeded. This one counts the exception, writes <b>one</b> log line
/// naming the type and message (once per Core generation, so a method throwing every frame
/// cannot fill the log), and returns the exception it was handed, unchanged. A demo that
/// swallowed would be the worst thing in this repository to copy.
///
/// <b>The atomic group, and the opt-out it refuses (job J21).</b> <c>.patchdemo group</c> is the
/// repository's only consumer example of <c>PatchRegistry.All</c> — the static, explicitly-owned
/// registration that either applies every member or leaves nothing applied. It runs two groups so
/// the interesting half is visible: the first asks for a member on a method that exists on no
/// build, so the member that already applied is unpatched again and the call returns null (the
/// reply prints <c>PatchRegistry.Count</c> either side, and the two numbers being equal is the
/// atomicity); the second is a prefix and a postfix on the real target and returns two handles.
/// <c>.patchdemo unsafe</c> is the counterpart for <c>ctx.UnsafeHarmony</c>: it explains the
/// property <b>without reading it</b>, because reading it is itself the opt-out of hot reload for
/// this plugin, and a Sample that demonstrated it would break every other hot-reload example here.
///
/// Parsing and every reply line are game-free so the test project compiles this file.
/// </summary>
internal static class SamplePatchDemo
{
    /// <summary>Log/grep area for this feature: <c>Sample patchdemo: …</c>.</summary>
    internal const string Area = "patchdemo";

    /// <summary>The message <c>.patchdemo ping</c> sends through the patched helper.</summary>
    internal const string PingText = "Sample .patchdemo ping (this line went through the patched helper).";

    /// <summary>Human-readable name of the patched site, used in replies and logs.</summary>
    internal const string TargetName =
        "ProjectM.ServerChatUtils::SendSystemMessageToClient(EntityManager, User, in FixedString512Bytes)";

    /// <summary>What the caller asked for.</summary>
    internal enum Kind
    {
        /// <summary>No argument: report whether the prefix is installed and how often it fired.</summary>
        Status = 0,

        /// <summary>Install the prefix.</summary>
        On = 1,

        /// <summary>Unpatch through <c>ctx.Patches.RemoveAll()</c>.</summary>
        Off = 2,

        /// <summary>Call the patched helper once.</summary>
        Ping = 3,

        /// <summary>
        /// Job J21. Register two <b>atomic groups</b> through <c>PatchRegistry.All</c>: one whose
        /// second member is deliberately refused (so the reply shows the rollback of the first),
        /// then one that lands.
        /// </summary>
        Group = 4,

        /// <summary>
        /// Job J21. Explain <c>ctx.UnsafeHarmony</c> <b>without reading it</b> — reading the
        /// property is itself the opt-out of hot reload.
        /// </summary>
        Unsafe = 5,
    }

    private static int _fires;
    private static int _postfixFires;
    private static int _finalizerFires;
    private static int _finalizerExceptions;
    private static int _finalizerLogged;
    private static int _groupFires;

    /// <summary>How many times the prefix has fired in this Core generation.</summary>
    internal static int Fires => Volatile.Read(ref _fires);

    /// <summary>How many times the postfix has fired in this Core generation.</summary>
    internal static int PostfixFires => Volatile.Read(ref _postfixFires);

    /// <summary>How many times the finalizer has run in this Core generation, throw or no throw.</summary>
    internal static int FinalizerFires => Volatile.Read(ref _finalizerFires);

    /// <summary>How many of those finalizer runs were handed a real exception.</summary>
    internal static int FinalizerExceptions => Volatile.Read(ref _finalizerExceptions);

    /// <summary>Parse the single optional argument. Empty means <see cref="Kind.Status"/>.</summary>
    internal static bool TryParse(string? token, out Kind kind)
    {
        kind = Kind.Status;
        if (string.IsNullOrWhiteSpace(token)) return true;

        switch (token.Trim().ToLowerInvariant())
        {
            case "status": kind = Kind.Status; return true;
            case "on": kind = Kind.On; return true;
            case "off": kind = Kind.Off; return true;
            case "ping": kind = Kind.Ping; return true;
            case "group": kind = Kind.Group; return true;
            case "unsafe": kind = Kind.Unsafe; return true;
            default: return false;
        }
    }

    /// <summary>Usage line, replied on a token this command does not know.</summary>
    internal const string UsageReply = "Usage: .patchdemo on | off | ping | group | unsafe | status";

    /// <summary>Reply for <c>.patchdemo on</c>.</summary>
    internal static string OnReply(bool installed, long id) =>
        installed
            ? "Prefix installed on " + TargetName + " (patch id " + N(id)
              + "). Run .patchdemo ping. The body sits in a Bootstrap patch slot, not in Harmony, "
              + "so .patchdemo off (and any unload) releases it and the Sample stays hot-reloadable."
            : "Prefix NOT installed on " + TargetName
              + ": the site was refused or the apply threw, and nothing was registered. See the server log.";

    /// <summary>Reply for <c>.patchdemo on</c> when it is already installed.</summary>
    internal const string AlreadyOnReply = "The prefix is already installed. .patchdemo ping fires it; .patchdemo off removes it.";

    /// <summary>Reply for <c>.patchdemo off</c>.</summary>
    internal static string OffReply(int removed) =>
        "ctx.Patches.RemoveAll() unpatched " + N(removed)
        + " patch(es). Unload does exactly this, on every unload reason - the handle is optional.";

    /// <summary>Reply for <c>.patchdemo ping</c>.</summary>
    internal static string PingReply(bool armed, bool sent, int firesBefore, int firesAfter)
    {
        if (!sent)
            return "Could not call " + TargetName + "; nothing was sent. See the server log.";
        if (!armed)
            return "Called the helper once. The prefix is not installed, so nothing fired - .patchdemo on first.";
        return firesAfter > firesBefore
            ? "Called the helper once and the prefix fired (fires=" + N(firesAfter) + ")."
            : "Called the helper once but the prefix did NOT fire (fires=" + N(firesAfter)
              + "). The patch is registered but not reached - check the refusal lines in the server log.";
    }

    /// <summary>Reply for the bare <c>.patchdemo</c> / <c>.patchdemo status</c>.</summary>
    internal static string StatusReply(bool armed, int fires) =>
        (armed ? "Prefix installed on " : "No prefix installed on ") + TargetName
        + "; fired " + N(fires) + " time(s) this Core generation.";

    /// <summary>
    /// Second line of the <c>.patchdemo on</c> reply (job J14): which of the three kinds landed.
    /// Kept separate from <see cref="OnReply"/> so the prefix line a reader greps for is
    /// unchanged, and so a partial install — two of three, which <c>ctx.Patches</c> allows
    /// because these are three independent registrations and not a
    /// <c>PatchRegistry.All</c> group — reads as a partial install rather than as a success.
    /// </summary>
    internal static string KindsReply(bool prefix, bool postfix, bool finalizer)
    {
        int landed = (prefix ? 1 : 0) + (postfix ? 1 : 0) + (finalizer ? 1 : 0);
        return "Kinds installed " + N(landed) + "/3: prefix=" + YesNo(prefix)
            + " postfix=" + YesNo(postfix) + " finalizer=" + YesNo(finalizer)
            + ". They are three separate registrations, not a group, so a refusal of one leaves "
            + "the others applied; .patchdemo off removes whatever is there.";
    }

    /// <summary>
    /// Second line of the <c>.patchdemo ping</c> / <c>status</c> reply (job J14): the postfix and
    /// finalizer counters, and what the finalizer did about an exception if it ever saw one.
    /// </summary>
    internal static string KindCountsReply(int prefixFires, int postfixFires, int finalizerFires, int finalizerExceptions) =>
        "Counters this Core generation: prefix=" + N(prefixFires) + " postfix=" + N(postfixFires)
        + " finalizer=" + N(finalizerFires) + " (of which " + N(finalizerExceptions)
        + " observed an exception, every one of them returned to the game unchanged - a finalizer "
        + "that returns null swallows the throw and the caller carries on as if it had succeeded).";

    // ---- Job J21: PatchRegistry.All, and the ctx.UnsafeHarmony refusal ----------------------

    /// <summary>
    /// The label the deliberately-refused member of the first group carries. It is what the
    /// registry prints in <c>patches: group refused …</c>, so the reply and the log line can be
    /// matched up by a reader who has never seen either before.
    /// </summary>
    internal const string RefusedMemberLabel = "sample.group.no-such-method";

    /// <summary>The label of the group's prefix member.</summary>
    internal const string GroupPrefixLabel = "sample.group.prefix";

    /// <summary>The label of the group's postfix member.</summary>
    internal const string GroupPostfixLabel = "sample.group.postfix";

    /// <summary>
    /// The name of the method the refused member asks for. It does not exist on any build, on
    /// purpose: <c>PatchRegistry.All</c> refuses a null <c>Target</c>, which is the same shape a
    /// real group hits when one of its sites has moved in a game patch.
    /// </summary>
    internal const string MissingTargetName = "ProjectM.ServerChatUtils::NoSuchMethodOnAnyBuild";

    /// <summary>
    /// Reply for the first half of <c>.patchdemo group</c>: the group whose second member cannot
    /// resolve. The numbers are <c>PatchRegistry.Count</c> read before the call and after it —
    /// equal is the whole point, because the first member <em>did</em> apply and was then
    /// unpatched again.
    /// </summary>
    internal static string GroupRefusedReply(bool refused, int liveBefore, int liveAfter)
    {
        if (!refused)
            return "The group that was supposed to be refused REGISTERED (" + N(liveAfter)
                + " live). That is a bug in this demo, not in the registry - .patchdemo off, then read the log.";
        return "Group 1 of 2 refused, as designed: member '" + GroupPrefixLabel
            + "' applied, member '" + RefusedMemberLabel + "' (" + MissingTargetName
            + ") did not resolve, so the registry unpatched the first one again. Live patches before="
            + N(liveBefore) + " after=" + N(liveAfter)
            + " - equal, which is what 'atomic' means here. PatchRegistry.All returned null; there are no handles to leak.";
    }

    /// <summary>
    /// Reply for the second half of <c>.patchdemo group</c>: the group that lands. Both members
    /// are on the one real target, so <c>.patchdemo ping</c> then fires them together.
    /// </summary>
    internal static string GroupLandedReply(int handles, int liveAfter)
    {
        if (handles <= 0)
            return "Group 2 of 2 also refused - nothing is registered. The registry logged which member and why; see the server log.";
        return "Group 2 of 2 registered " + N(handles) + " member(s) ('" + GroupPrefixLabel
            + "', '" + GroupPostfixLabel + "') as one unit on " + TargetName + "; live patches="
            + N(liveAfter) + ". .patchdemo ping fires both, .patchdemo off removes them with everything else.";
    }

    /// <summary>Reply for <c>.patchdemo group</c> when the applier had to be attached first.</summary>
    internal const string GroupWarmedReply =
        "ctx.Patches attaches the Harmony applier on its first call and PatchRegistry.All (the static form) does not, "
        + "so this demo made one throwaway ctx.Patches registration first and disposed it. Your own code will normally "
        + "have used ctx.Patches already; if it has not, call PatchRegistry.All after it, not before.";

    /// <summary>How many times a group member has fired in this Core generation.</summary>
    internal static int GroupFires => Volatile.Read(ref _groupFires);

    /// <summary>
    /// Status/ping line for the group members, kept separate from
    /// <see cref="KindCountsReply"/> so the three single registrations and the one atomic group
    /// are never added together into a number that means nothing.
    /// </summary>
    internal static string GroupCountsReply(int members, int groupFires) =>
        members <= 0
            ? "No PatchRegistry.All group is registered; .patchdemo group registers one (and shows a refused one first)."
            : "PatchRegistry.All group: " + N(members) + " member(s) live, fired " + N(groupFires)
              + " time(s) this Core generation.";

    /// <summary>Called by either group body. Counts the fire and writes the greppable line.</summary>
    internal static int RecordGroupFire(string label)
    {
        int fires = Interlocked.Increment(ref _groupFires);
        SampleLog.Info(Area, "group member '" + (string.IsNullOrEmpty(label) ? "?" : label)
            + "' fired group=" + N(fires));
        return fires;
    }

    /// <summary>
    /// Reply for <c>.patchdemo unsafe</c>. The Sample <b>never reads</b> <c>ctx.UnsafeHarmony</c>,
    /// and this reply is the example: the property's cost is paid by the read, not by the patch,
    /// so a worked example that demonstrated it would make the Sample permanently
    /// non-hot-swappable and every later hot-reload example in this plugin unreachable. The safe
    /// example of an opt-out is the one that declines it and says what it would have cost.
    /// </summary>
    internal const string UnsafeReply =
        "The Sample never reads ctx.UnsafeHarmony. Reading the property - not patching through it - logs a warning "
        + "and marks this plugin non-hot-swappable for the rest of the process, because a Harmony patch pins the "
        + "assembly its patch method came from even after Unpatch. Every other .patchdemo subcommand, and the Sample's "
        + "own hot reload, would stop working the first time this one demonstrated it. Use ctx.Patches: same three "
        + "kinds, bodies parked in Bootstrap's process-lifetime slots, and unload gives the slot back.";

    /// <summary>Called by the prefix. Counts the fire and writes the greppable line.</summary>
    internal static int RecordFire()
    {
        int fires = Interlocked.Increment(ref _fires);
        SampleLog.Info(Area, "prefix fired on " + TargetName + " fires=" + N(fires));
        return fires;
    }

    /// <summary>Called by the postfix. Counts the call and writes the greppable line.</summary>
    internal static int RecordPostfix()
    {
        int fires = Interlocked.Increment(ref _postfixFires);
        SampleLog.Info(Area, "postfix ran after " + TargetName + " postfix=" + N(fires));
        return fires;
    }

    /// <summary>
    /// The line the finalizer writes the <b>first</b> time it is handed an exception. Once per
    /// Core generation: a seam that throws every frame would otherwise turn the demo into a log
    /// flood, and the point being made — "an exception came out of here and we did not eat it" —
    /// is made by the first one.
    /// </summary>
    internal static string FinalizerExceptionLine(string? exceptionType, string? message) =>
        "finalizer OBSERVED an exception out of " + TargetName + ": "
        + (string.IsNullOrEmpty(exceptionType) ? "<unknown>" : exceptionType!) + ": \""
        + SampleChat.Elide(message, ExceptionMessageChars)
        + "\" - returned unchanged, not swallowed. This line is written once per Core generation.";

    /// <summary>How much of an observed exception message reaches the log.</summary>
    internal const int ExceptionMessageChars = 120;

    /// <summary>
    /// Called by the finalizer. Counts the run; when <paramref name="exception"/> is non-null,
    /// counts that too and writes <see cref="FinalizerExceptionLine"/> once. Returns the total
    /// number of finalizer runs. It deliberately does not decide anything about the exception —
    /// the body returns it either way.
    /// </summary>
    internal static int RecordFinalizer(Exception? exception)
    {
        int fires = Interlocked.Increment(ref _finalizerFires);
        if (exception == null)
        {
            SampleLog.Info(Area, "finalizer ran after a clean return from " + TargetName
                + " finalizer=" + N(fires));
            return fires;
        }

        Interlocked.Increment(ref _finalizerExceptions);
        if (Interlocked.Exchange(ref _finalizerLogged, 1) == 0)
            SampleLog.Info(Area, FinalizerExceptionLine(exception.GetType().Name, exception.Message));
        return fires;
    }

    /// <summary>Forget every counter. Unload does this; the patches themselves are swept by Core.</summary>
    internal static void ResetCount()
    {
        Volatile.Write(ref _fires, 0);
        Volatile.Write(ref _postfixFires, 0);
        Volatile.Write(ref _finalizerFires, 0);
        Volatile.Write(ref _finalizerExceptions, 0);
        Volatile.Write(ref _finalizerLogged, 0);
        Volatile.Write(ref _groupFires, 0);
    }

#if !PROGENITOR_TESTS
    private static readonly object Gate = new();
    private static PatchHandle? _handle;
    private static PatchHandle? _postfixHandle;
    private static PatchHandle? _finalizerHandle;
    private static PatchHandle[]? _groupHandles;

    /// <summary>True when this generation holds a live prefix handle.</summary>
    internal static bool Armed
    {
        get { lock (Gate) return _handle != null; }
    }

    /// <summary>How many members of the <c>PatchRegistry.All</c> group are live.</summary>
    internal static int GroupMembers
    {
        get { lock (Gate) return _groupHandles?.Length ?? 0; }
    }

    /// <summary>True when this generation holds any of the three handles.</summary>
    internal static bool AnyArmed
    {
        get
        {
            lock (Gate)
                return _handle != null || _postfixHandle != null || _finalizerHandle != null
                    || (_groupHandles != null && _groupHandles.Length > 0);
        }
    }

    /// <summary>Server entry point for <c>.patchdemo</c>.</summary>
    internal static void HandleCommand(ICommandContext ctx, Kind kind)
    {
        switch (kind)
        {
            case Kind.On: On(ctx); return;
            case Kind.Off: Off(ctx); return;
            case Kind.Ping: Ping(ctx); return;
            case Kind.Group: Group(ctx); return;
            case Kind.Unsafe:
                // Nothing here touches ctx.UnsafeHarmony. That is the example.
                ctx.Reply(UnsafeReply);
                SampleLog.Info(Area, "unsafe: declined to read ctx.UnsafeHarmony (reading it is the opt-out)");
                return;
            default:
                ctx.Reply(StatusReply(Armed, Fires));
                ctx.Reply(KindCountsReply(Fires, PostfixFires, FinalizerFires, FinalizerExceptions));
                ctx.Reply(GroupCountsReply(GroupMembers, GroupFires));
                SampleLog.Info(Area, "status armed=" + Armed + " fires=" + N(Fires)
                    + " postfix=" + N(PostfixFires) + " finalizer=" + N(FinalizerFires)
                    + " group=" + N(GroupMembers) + "/" + N(GroupFires));
                return;
        }
    }

    private static void On(ICommandContext ctx)
    {
        if (AnyArmed)
        {
            ctx.Reply(AlreadyOnReply);
            return;
        }

        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }

        var target = ResolveTarget();
        if (target == null)
        {
            ctx.Reply(OnReply(installed: false, id: 0));
            SampleLog.Info(Area, "target not found on this build: " + TargetName);
            return;
        }

        var prefix = typeof(SamplePatchDemo).GetMethod(
            nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static);
        if (prefix == null)
        {
            ctx.Reply(OnReply(installed: false, id: 0));
            SampleLog.Info(Area, "prefix method missing (renamed?)");
            return;
        }

        // Core-owned style: hand ctx.Patches the pair and check the return. A null handle means
        // refused or threw - already logged by the registry, and nothing was registered.
        var handle = plugin.Patches.Prefix(target, prefix);

        // Job J14. The postfix and the finalizer go in as delegates rather than as MethodInfo:
        // ctx.Patches takes either, and the delegate overload is the one that shows the shapes
        // (SlotPostfix returns void, SlotFinalizer returns Exception?). Three independent
        // registrations, so a refusal of one leaves the others applied - deliberate here, where
        // each kind is being demonstrated on its own; a caller whose correctness needs all three
        // uses PatchRegistry.All instead, which rolls back.
        var postfixHandle = plugin.Patches.Postfix(target, PostfixBody);
        var finalizerHandle = plugin.Patches.Finalizer(target, FinalizerBody);

        lock (Gate)
        {
            _handle = handle;
            _postfixHandle = postfixHandle;
            _finalizerHandle = finalizerHandle;
        }

        ctx.Reply(OnReply(handle != null, handle?.Id ?? 0));
        ctx.Reply(KindsReply(handle != null, postfixHandle != null, finalizerHandle != null));
        SampleLog.Info(
            Area,
            handle != null
                ? "prefix registered id=" + N(handle.Id) + " target=" + TargetName
                : "prefix refused target=" + TargetName + " (see the patches: lines above)");
        SampleLog.Info(
            Area,
            "postfix id=" + N(postfixHandle?.Id ?? 0) + " finalizer id=" + N(finalizerHandle?.Id ?? 0)
            + " target=" + TargetName);
    }

    /// <summary>
    /// Job J21. <c>PatchRegistry.All(owner, ownerId, params PatchRequest[])</c> — the atomic
    /// group, and the only registration shape in the module that rolls back.
    ///
    /// <para><b>Why it is the static form and not <c>ctx.Patches</c>.</b> A group is a statement
    /// about a <em>set</em> of sites ("either both kill paths are guarded or neither is"), and
    /// there is no per-plugin facade for it: the caller passes its own owner pair —
    /// <c>ctx.Owner</c> and <c>ctx.Guid</c> — so the entries are tagged exactly as a
    /// <c>ctx.Patches</c> one is and <c>PluginReloader.SweepRegistrations</c> unpatches them on
    /// every unload reason just the same. The one thing the static form does not do is attach the
    /// Harmony applier, which is what the warm-up below is for.</para>
    ///
    /// <para>Two groups run, in this order, because the interesting half is the failure:
    /// group 1 asks for a member on a method that does not exist, so the member that already
    /// applied is unpatched again and the call returns null; group 2 is the same prefix plus a
    /// postfix on the real target and returns two handles. The reply prints
    /// <c>PatchRegistry.Count</c> either side of group 1, and the two numbers being equal is the
    /// atomicity.</para>
    /// </summary>
    private static void Group(ICommandContext ctx)
    {
        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }

        var target = ResolveTarget();
        if (target == null)
        {
            ctx.Reply(OnReply(installed: false, id: 0));
            SampleLog.Info(Area, "group: target not found on this build: " + TargetName);
            return;
        }

        // PatchRegistry.All is the static entry point and never calls Patches.Install(), so on a
        // process where nothing has patched anything yet every member would be refused with
        // reason=not-installed and the demo would show a rollback that never happened. One
        // throwaway ctx.Patches registration attaches the applier; it is disposed at once.
        if (!Progenitor.Patches.Patches.Installed)
        {
            var warm = plugin.Patches.Prefix(target, WarmPrefix);
            warm?.Dispose();
            ctx.Reply(GroupWarmedReply);
            SampleLog.Info(Area, "group: warmed the applier through ctx.Patches (installed="
                + Progenitor.Patches.Patches.Installed + ")");
        }

        int before = PatchRegistry.Count;
        var refused = PatchRegistry.All(
            plugin.Owner,
            plugin.Guid,
            new PatchRequest(target, (SlotPrefix)GroupPrefixBody, PatchKind.Prefix, 400, GroupPrefixLabel),
            // Deliberately unresolvable: ResolveMissingTarget always returns null, which is what
            // a group member whose site moved in a game patch looks like.
            new PatchRequest(ResolveMissingTarget(), (SlotPostfix)GroupPostfixBody, PatchKind.Postfix, 400, RefusedMemberLabel));
        int afterRefused = PatchRegistry.Count;

        if (refused != null)
        {
            // Should not happen; if it ever does, do not leave the handles behind.
            for (int i = 0; i < refused.Length; i++) refused[i].Dispose();
            afterRefused = PatchRegistry.Count;
        }

        ctx.Reply(GroupRefusedReply(refused == null, before, afterRefused));
        SampleLog.Info(Area, "group 1 refused=" + (refused == null)
            + " live before=" + N(before) + " after=" + N(afterRefused));

        var landed = PatchRegistry.All(
            plugin.Owner,
            plugin.Guid,
            new PatchRequest(target, (SlotPrefix)GroupPrefixBody, PatchKind.Prefix, 400, GroupPrefixLabel),
            new PatchRequest(target, (SlotPostfix)GroupPostfixBody, PatchKind.Postfix, 400, GroupPostfixLabel));

        lock (Gate) _groupHandles = landed;
        ctx.Reply(GroupLandedReply(landed?.Length ?? 0, PatchRegistry.Count));
        SampleLog.Info(Area, "group 2 handles=" + N(landed?.Length ?? 0)
            + " live=" + N(PatchRegistry.Count));
    }

    /// <summary>
    /// The member the refused group asks for. It is resolved rather than hard-coded as
    /// <c>null</c> so the demo reads like real code: a lookup that comes back empty on this build.
    /// </summary>
    private static MethodBase? ResolveMissingTarget()
    {
        try
        {
            return typeof(ServerChatUtils).GetMethod(
                "NoSuchMethodOnAnyBuild", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Throwaway body used only to attach the Harmony applier. Never left applied.</summary>
    private static bool WarmPrefix(object? instance, object?[] args) => true;

    /// <summary>Group member 1: a prefix that only counts. Returns true, so it never skips.</summary>
    private static bool GroupPrefixBody(object? instance, object?[] args)
    {
        try { RecordGroupFire(GroupPrefixLabel); }
        catch (Exception e) { Plugin.Logger?.LogError("Sample .patchdemo group prefix threw: " + e); }
        return true;
    }

    /// <summary>Group member 2: a postfix that only counts.</summary>
    private static void GroupPostfixBody(object? instance, object?[] args)
    {
        try { RecordGroupFire(GroupPostfixLabel); }
        catch (Exception e) { Plugin.Logger?.LogError("Sample .patchdemo group postfix threw: " + e); }
    }

    private static void Off(ICommandContext ctx)
    {
        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }

        int removed = plugin.Patches.RemoveAll();
        lock (Gate)
        {
            _handle = null;
            _postfixHandle = null;
            _finalizerHandle = null;
            // RemoveAll is by owner, and PatchRegistry.All tagged the group with the same
            // (owner, ownerId) pair, so the group came off with everything else.
            _groupHandles = null;
        }
        ctx.Reply(OffReply(removed));
        SampleLog.Info(Area, "RemoveAll removed=" + N(removed));
    }

    private static void Ping(ICommandContext ctx)
    {
        bool armed = Armed;
        int before = Fires;
        bool sent = TrySendThroughTarget(ctx);
        ctx.Reply(PingReply(armed, sent, before, Fires));
        ctx.Reply(KindCountsReply(Fires, PostfixFires, FinalizerFires, FinalizerExceptions));
        ctx.Reply(GroupCountsReply(GroupMembers, GroupFires));
        SampleLog.Info(Area, "ping armed=" + armed + " sent=" + sent + " fires=" + N(Fires)
            + " postfix=" + N(PostfixFires) + " finalizer=" + N(FinalizerFires)
            + " group=" + N(GroupMembers) + "/" + N(GroupFires));
    }

    /// <summary>
    /// The one overload we want: three parameters, first one <c>EntityManager</c>. Selecting by
    /// parameter count would be ambiguous here (both overloads take three), which is exactly the
    /// case God mode's parameter-count selection warns about.
    /// </summary>
    private static MethodBase? ResolveTarget()
    {
        try
        {
            var methods = typeof(ServerChatUtils).GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                var m = methods[i];
                if (!string.Equals(m.Name, "SendSystemMessageToClient", StringComparison.Ordinal)) continue;
                var ps = m.GetParameters();
                if (ps.Length != 3) continue;
                if (ps[0].ParameterType.Name.IndexOf("EntityManager", StringComparison.Ordinal) < 0) continue;
                return m;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .patchdemo target lookup threw: " + e);
        }

        return null;
    }

    private static bool TrySendThroughTarget(ICommandContext ctx)
    {
        try
        {
            var em = ctx.World.EntityManager;
            var user = ctx.User;
            var message = new FixedString512Bytes(PingText);
            ServerChatUtils.SendSystemMessageToClient(em, user, ref message);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .patchdemo ping send threw: " + e);
            return false;
        }
    }

    /// <summary>
    /// The patch body, in the slot shape <c>ctx.Patches</c> requires: static, taking Harmony's
    /// <c>__instance</c> and <c>__args</c>, returning whether the original should still run.
    /// It always returns true, so it can never skip the original. Resolved by name in
    /// <see cref="On"/>; renaming it turns the demo off with a log line rather than silently.
    /// </summary>
    private static bool Prefix(object? instance, object?[] args)
    {
        RecordFire();
        return true;
    }

    /// <summary>
    /// The postfix body (job J14), in <c>SlotPostfix</c> shape: it returns <c>void</c>, so it
    /// cannot change the call's outcome at all. That is the whole reason to reach for a postfix
    /// over a prefix when all you want is to observe: there is no way to get it wrong.
    /// </summary>
    private static void PostfixBody(object? instance, object?[] args)
    {
        try
        {
            RecordPostfix();
        }
        catch (Exception e)
        {
            // A postfix that throws is a mod turning a working call into a failing one.
            Plugin.Logger?.LogError("Sample .patchdemo postfix threw: " + e);
        }
    }

    /// <summary>
    /// The finalizer body (job J14), in <c>SlotFinalizer</c> shape. It runs whether the original
    /// returned or threw, and <b>its return value is the exception the game then sees</b>:
    /// returning null makes a real throw disappear, which is why this one returns
    /// <paramref name="exception"/> unchanged on every path, including the path where its own
    /// bookkeeping threw. Copy this shape, not a <c>return null;</c>.
    /// </summary>
    private static Exception? FinalizerBody(object? instance, object?[] args, Exception? exception)
    {
        try
        {
            RecordFinalizer(exception);
        }
        catch (Exception own)
        {
            Plugin.Logger?.LogError("Sample .patchdemo finalizer bookkeeping threw: " + own);
        }

        return exception;
    }

    /// <summary>
    /// Unload. Core sweeps the patch itself on every reason
    /// (<c>PluginReloader.SweepRegistrations</c> → <c>PatchRegistry.RemoveAll</c>), so this only
    /// drops the local handle and the counter; it deliberately does <b>not</b> try to keep a
    /// patch alive across a <c>Swap</c>, which is the opposite of the buff rule.
    /// </summary>
    internal static void Detach()
    {
        lock (Gate)
        {
            _handle = null;
            _postfixHandle = null;
            _finalizerHandle = null;
            _groupHandles = null;
        }
        ResetCount();
    }
#else
    /// <summary>Test build: nothing is patched, so the demo is never armed.</summary>
    internal static bool Armed => false;

    /// <summary>Test build: nothing is patched, so none of the three kinds is armed.</summary>
    internal static bool AnyArmed => false;

    /// <summary>Test build: no group is registered.</summary>
    internal static int GroupMembers => 0;

    /// <summary>Test build: no handle to drop.</summary>
    internal static void Detach() => ResetCount();
#endif

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string YesNo(bool value) => value ? "yes" : "no";
}
