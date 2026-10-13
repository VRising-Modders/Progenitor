using System;
using System.Globalization;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
using Progenitor.Plugins;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Job J18 (RD6). The three registration forms of <c>ctx.Commands</c>, side by side, with the one
/// that is easiest to get wrong shown last.
///
/// <b>The forms.</b> <c>Register(Type)</c> is what <c>Plugin.cs</c> uses for the C# <c>static</c>
/// class <see cref="SampleCommands"/> — a static class cannot be a generic type argument, so it is
/// the only form that works there. <c>Register&lt;T&gt;()</c> is what
/// <see cref="SampleSystems"/> uses for <c>SampleSystemCommands</c>: Core constructs the object
/// itself through a parameterless constructor and shares it across that class's instance methods.
/// <c>Register(object)</c> — this file, and <see cref="SampleGroup"/>'s group — hands Core an
/// object the <em>plugin</em> constructed.
///
/// <b>Why the object form is the one that matters.</b> A command class that needs anything from its
/// plugin — the <c>PluginContext</c>, a config entry, a store, a counter — cannot get it from a
/// type Core constructs. The alternative every mod reaches for first is a process-wide static
/// (<c>Plugin.Active</c> in this sample), which has to be nulled on unload, is fought over by two
/// Core generations during a hot swap, and outlives the thing it points at if anyone forgets. An
/// instance registration removes the question: the command method reads a field.
///
/// <b>What <c>.counter</c> makes visible.</b> The count lives on the instance
/// (<see cref="SampleCounter"/>), not in a static, so:
/// <list type="bullet">
/// <item><description><c>.counter</c> increments and reports it;</description></item>
/// <item><description><c>.counter reset</c> puts it back to zero and says what it was;</description></item>
/// <item><description>a <b>hot reload constructs a new instance and the count starts at zero
/// again</b> — which is the observable difference between instance state and a static, and the
/// live check worth doing.</description></item>
/// </list>
///
/// <b>The handle.</b> <c>ctx.Commands.Register(object)</c> tracks its <c>IDisposable</c> on the
/// context, so unload drops the command whether or not anyone kept it. This file keeps it anyway
/// and disposes it in <see cref="Detach"/> (the handle is idempotent), because a plugin that wants
/// to <em>retire a command while staying loaded</em> — a feature toggle, a command that only exists
/// while a world is up — has no other way to do it, and the returned handle is the whole mechanism.
///
/// <b>Live-check split: server only.</b> Registered under <c>ctx.IsServer</c>; the reply is chat.
///
/// The counter arithmetic and every reply line are game-free, so the test project compiles this
/// file and pins the exact text.
/// </summary>
internal static class SampleInstanceCommands
{
    /// <summary>Log/grep area: <c>Sample counter: …</c>.</summary>
    internal const string Area = "counter";

    /// <summary>The command name. <c>.counter</c>, <c>.counter reset</c>.</summary>
    internal const string CommandName = "counter";

    /// <summary>What the caller asked for.</summary>
    internal enum Kind
    {
        /// <summary>No argument: count this call and report the total.</summary>
        Count = 0,

        /// <summary>Put the instance's counter back to zero.</summary>
        Reset = 1,
    }

    /// <summary>Parse <c>.counter</c> / <c>.counter reset</c>. Anything else is refused.</summary>
    internal static bool TryParse(string? token, out Kind kind)
    {
        kind = Kind.Count;
        if (string.IsNullOrWhiteSpace(token)) return true;
        string head = token!.Trim().ToLowerInvariant();
        if (head == "reset")
        {
            kind = Kind.Reset;
            return true;
        }

        return head == "count" || head == "status";
    }

    /// <summary>Usage line, replied on anything <see cref="TryParse"/> refuses.</summary>
    internal const string UsageReply = "Usage: .counter | .counter reset";

    /// <summary>The line written when the instance is registered.</summary>
    internal static string RegisteredLine() =>
        "registered ." + CommandName + " as an instance (ctx.Commands.Register(object)); the count "
        + "lives on that object, so a hot reload starts it at zero.";

    /// <summary>Reply for <c>.counter</c>.</summary>
    internal static string CountReply(int count) =>
        "This command instance has been used " + N(count) + " time(s) since it was registered. The "
        + "number is a field on the object the plugin handed to ctx.Commands.Register(object), not a "
        + "static: reload the plugin and it starts again at 1.";

    /// <summary>Reply for <c>.counter reset</c>.</summary>
    internal static string ResetReply(int previous) =>
        "Reset the instance counter; it was " + N(previous) + ". The instance itself is unchanged — "
        + "only a reload replaces it.";

    /// <summary>Reply for <c>.counter reset</c> when it is already zero.</summary>
    internal static string ResetWhenZeroReply =>
        "The instance counter is already 0. Nothing to reset.";

    /// <summary>What <see cref="Detach"/> logs.</summary>
    internal static string DetachLine(int count) =>
        "disposed the ." + CommandName + " registration handle after " + N(count)
        + " use(s); the instance goes with it.";

#if !PROGENITOR_TESTS
    private static SampleCounter? _instance;
    private static IDisposable? _handle;

    /// <summary>
    /// Construct the counter and register it as an <b>instance</b>. The handle is tracked on the
    /// context by <c>Register(object)</c> and kept here as well, so <see cref="Detach"/> can show
    /// the explicit undo.
    /// </summary>
    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        Detach();

        try
        {
            var counter = new SampleCounter();
            _handle = ctx.Commands.Register(counter);
            _instance = counter;
            SampleLog.Info(Area, RegisteredLine());
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample counter attach threw: " + e);
        }
    }

    /// <summary>The live instance's count, for a diagnostic that is not the command itself.</summary>
    internal static int Count => _instance?.Count ?? 0;
#else
    /// <summary>Test build: there is no registered instance, so nothing has been counted.</summary>
    internal static int Count => 0;
#endif

    /// <summary>
    /// Unload. Disposes the registration handle explicitly — Core would do it anyway from the
    /// context, and the handle is idempotent, so the double undo is free — then drops the local
    /// reference so the instance is not held by a static in a generation being torn down.
    /// </summary>
    internal static void Detach()
    {
#if !PROGENITOR_TESTS
        var handle = _handle;
        int count = Count;
        _handle = null;
        _instance = null;
        if (handle == null) return;

        try
        {
            handle.Dispose();
            SampleLog.Info(Area, DetachLine(count));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample counter detach threw: " + e);
        }
#endif
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}

#if !PROGENITOR_TESTS
/// <summary>
/// The counter itself (job J18). An ordinary class the plugin constructs and hands to
/// <c>ctx.Commands.Register(object)</c>, so the dispatcher invokes <see cref="Counter"/> on
/// <b>this</b> object and it can read <see cref="_count"/> instead of a process-wide static.
/// Nothing here is <c>static</c> on purpose.
/// </summary>
internal sealed class SampleCounter
{
    private int _count;

    /// <summary>How many times this instance has served <c>.counter</c>.</summary>
    internal int Count => _count;

    // Not gated: it reports a number this instance owns, to the caller, and changes nothing in
    // the world. `.counter reset` only resets that same number.
    [Command(
        SampleInstanceCommands.CommandName,
        usage: ".counter | .counter reset",
        description: "Count your uses of this command. The number lives on the registered instance, so a plugin reload starts it over.")]
    public void Counter(ICommandContext ctx, string what = "")
    {
        try
        {
            if (!SampleInstanceCommands.TryParse(what, out var kind))
            {
                ctx.Reply(SampleInstanceCommands.UsageReply);
                return;
            }

            if (kind == SampleInstanceCommands.Kind.Reset)
            {
                int previous = _count;
                _count = 0;
                string resetReply = previous == 0
                    ? SampleInstanceCommands.ResetWhenZeroReply
                    : SampleInstanceCommands.ResetReply(previous);
                ctx.Reply(resetReply);
                SampleLog.Info(SampleInstanceCommands.Area, resetReply);
                return;
            }

            _count++;
            string reply = SampleInstanceCommands.CountReply(_count);
            ctx.Reply(reply);
            SampleLog.Info(SampleInstanceCommands.Area, reply);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .counter threw: " + e);
            ctx.Reply("Sample .counter failed. See the server log.");
        }
    }
}
#endif
