using System;
using System.Collections.Generic;
using System.Globalization;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
using Progenitor.Plugins;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Job FEX5 (RD6 J12). The command forms the Sample never showed: a
/// <c>[CommandGroup]</c> class, an <b>instance</b> registration through
/// <c>ctx.Commands.Register(object)</c>, and a command that reads
/// <c>ICommandContext.RawText</c> and <c>ICommandContext.IsAdmin</c>.
///
/// <b>What a group buys you.</b> Every other Sample command is a top-level name — <c>.ember</c>,
/// <c>.rat</c>, <c>.hooks</c> — and top-level names are a shared namespace with every other mod on
/// the server. <c>[CommandGroup("sample", "sam")]</c> (<c>Commands/Attributes.cs:54-70</c>) puts a
/// first token in front of every <c>[Command]</c> on the class, so these are <c>.sample echo</c>
/// and <c>.sample who</c> (or <c>.sam echo</c>), and the class can add a third member tomorrow
/// without picking another global word. <c>.help</c> lists them by
/// <c>CommandBundle.DisplayPath</c> (<c>Commands/CommandRegistry.cs:581</c>), which is
/// <c>"&lt;group&gt; &lt;name&gt;"</c>.
///
/// <b>Why an instance and not a static class.</b> The other command classes are C# <c>static</c>
/// ones registered with <c>Register(typeof(T))</c>, and they reach the plugin through
/// <c>Plugin.Active</c> — a process-wide static that has to be nulled on unload and that a second
/// generation of the plugin would fight over. An instance registration
/// (<c>PluginContext.cs:583-589</c> → <c>CommandRegistry.RegisterInstance</c>) hands the
/// dispatcher an object the plugin constructed, so the command methods just read a field. The
/// <see cref="Uses"/> counter below is there to make that visible: it is per instance, so a hot
/// reload gives you a new group starting at zero rather than a stale count, which is exactly the
/// difference between instance state and a static.
///
/// <b><c>RawText</c> and <c>IsAdmin</c>.</b> <c>RawText</c> is the whole chat line including the
/// prefix (<c>Commands/ICommandContext.cs:27-28</c>) — the parsed parameters are the normal way
/// to read arguments, and <c>RawText</c> is for the cases parsing throws away: the exact spacing,
/// a quoted form, or an audit line. It is <b>player input</b>, so <c>.sample who</c> cleans and
/// elides it through <see cref="SampleChat.Elide"/> before it reaches the log, the same as every
/// other chat text in the Sample. <c>IsAdmin</c> is the flag the dispatcher itself checks for
/// <c>adminOnly</c>; reading it inside a method is for <em>reporting</em> or for widening a reply,
/// never for hand-rolling a gate — that rule is <see cref="SampleCommands"/>'s, and it is why
/// <c>.sample who</c> only prints the flag.
///
/// <b>The explicit-owner registry forms (job J21).</b> <c>ctx.Commands.Register</c> is a thin
/// wrapper over <c>CommandRegistry.RegisterType/RegisterInstance(…, owner)</c> that also records
/// the handle for unload. <see cref="Attach"/> makes both calls directly as well — one small class
/// each, <c>.sample owned</c> and <c>.sample registry</c> — so the only difference the forms have
/// is visible: the handle comes back to the caller and <see cref="Detach"/> is what disposes it.
/// <c>RegisterAssembly</c> is deliberately not called; <see cref="WhyNoRegisterAssembly"/> says why.
///
/// <b>Live-check split: server only.</b> Both members reply in chat and log one line each.
///
/// Reload-safe: the registration handle is tracked on the context, so unload drops both commands
/// and the instance with them; there is no static to clear. The reply text and the counter
/// arithmetic are game-free, so the test project compiles and pins them.
/// </summary>
internal static class SampleGroup
{
    /// <summary>Log/grep area: <c>Sample group: …</c>.</summary>
    internal const string Area = "group";

    /// <summary>The group token. <c>.sample echo</c>, <c>.sample who</c>.</summary>
    internal const string GroupName = "sample";

    /// <summary>The group's alias. <c>.sam echo</c>.</summary>
    internal const string GroupShortHand = "sam";

    /// <summary>Longest echo this example will send back, in characters.</summary>
    internal const int MaxEchoChars = 200;

    /// <summary>How much of <c>RawText</c> reaches chat and the log.</summary>
    internal const int RawTextChars = 120;

    /// <summary>Usage line for <c>.sample echo</c>.</summary>
    internal const string EchoUsageReply = "Usage: .sample echo <text> (the rest of the line is one [Remainder] argument)";

    // ---- Job J21: the explicit-owner CommandRegistry forms -------------------------------------

    /// <summary>
    /// Why the Sample never calls <c>RegisterAssembly</c>, in one sentence, for the api.md row and
    /// for <c>.sample registry</c>'s reply. Every Sample command class is already registered by
    /// name — <c>Register(typeof(SampleCommands))</c>, <c>Register&lt;T&gt;</c>,
    /// <c>Register(object)</c>, and the two below — so scanning this assembly would register all of
    /// them a second time under the same owner: the name map would keep the last bundle for each
    /// name and the first would sit in the registry unreachable until unload. The assembly form is
    /// for a plugin that registers <em>nothing</em> by hand.
    /// </summary>
    internal const string WhyNoRegisterAssembly =
        "This plugin does not call RegisterAssembly: every command class here is already registered "
        + "by name, so an assembly scan would register each of them twice under one owner and the "
        + "duplicate bundles would sit unreachable until unload. RegisterAssembly is for a plugin "
        + "that registers nothing by hand - one call in OnLoad and no per-class list to maintain.";

    /// <summary>
    /// Reply for <c>.sample registry</c>: the two <c>CommandRegistry</c> forms that take the owner
    /// explicitly, and the thing that follows from taking it explicitly — <b>nothing tracks the
    /// handle for you</b>. <c>ctx.Commands.Register(...)</c> records its handle on the context and
    /// unload disposes it; <c>CommandRegistry.RegisterType/Instance(type, owner)</c> hands the
    /// handle to the caller and that is the whole difference.
    /// </summary>
    internal static IReadOnlyList<string> RegistryLines(bool typeRegistered, bool instanceRegistered, int uses) =>
        new[]
        {
            "CommandRegistry.RegisterType(typeof(SampleGroupOwnedCommands), ctx.Owner) -> "
                + (typeRegistered ? "registered" : "NOT registered")
                + "; CommandRegistry.RegisterInstance(new SampleGroupRegistryCommands(...), ctx.Owner) -> "
                + (instanceRegistered ? "registered" : "NOT registered")
                + ". Both are tagged with the same owner ctx.Commands uses, so UnregisterOwner and "
                + "the unload sweep reach them.",
            "The difference from ctx.Commands.Register is only who holds the handle: these two "
                + "return it to you and nothing else keeps it, so this file disposes both in "
                + "Detach(). Never reach for CommandRegistry.UnregisterOwner(ctx.Owner) to undo "
                + "one of them - it would drop every command this plugin has.",
            WhyNoRegisterAssembly,
            "This instance has served " + N(uses) + " command(s).",
        };

    /// <summary>The line written when the group is registered.</summary>
    internal static string RegisteredLine() =>
        "registered the [CommandGroup(\"" + GroupName + "\", \"" + GroupShortHand + "\")] instance: ."
        + GroupName + " echo, ." + GroupName + " who (aliased ." + GroupShortHand + ")";

    /// <summary>
    /// Reply for <c>.sample echo</c>. The text comes back verbatim after cleaning, so a reader can
    /// see that <c>[Remainder]</c> kept the spaces and quotes the tokenizer would have eaten.
    /// </summary>
    internal static string EchoReply(int uses, string? text) =>
        "echo #" + N(uses) + ": \"" + SampleChat.Elide(text, MaxEchoChars) + "\""
        + " (one [Remainder] parameter, so everything after 'echo' arrived as a single string)";

    /// <summary>
    /// Reply for <c>.sample echo</c> with nothing to echo. Refused rather than echoing an empty
    /// string, so the caller learns the argument was empty and not that the group is broken.
    /// </summary>
    internal static string EchoEmptyReply() => EchoUsageReply;

    /// <summary>
    /// Reply for <c>.sample who</c>: the two <c>ICommandContext</c> members nothing in the
    /// repository read, plus the instance counter that shows the same object served both calls.
    /// </summary>
    internal static IReadOnlyList<string> WhoLines(bool isAdmin, string? rawText, int uses) =>
        new[]
        {
            "IsAdmin=" + (isAdmin ? "True" : "False")
                + " (the dispatcher already checked this for any adminOnly command; reading it "
                + "here is for reporting, never for a hand-rolled gate)",
            "RawText=\"" + SampleChat.Elide(rawText, RawTextChars) + "\""
                + " (the whole line including the prefix, before parsing)",
            "This group instance has served " + N(uses) + " command(s) since it was registered; a "
                + "hot reload constructs a new one and the count starts again.",
        };

#if !PROGENITOR_TESTS
    private static SampleGroupCommands? _instance;
    private static IDisposable? _ownedTypeHandle;
    private static IDisposable? _ownedInstanceHandle;

    /// <summary>
    /// Construct the group and register it as an <b>instance</b>. The handle is tracked on the
    /// context (<c>PluginCommands.Register(object)</c> does the tracking), so unload drops both
    /// commands; the local reference exists only so <see cref="Detach"/> can forget the object.
    /// </summary>
    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        Detach();

        var group = new SampleGroupCommands(ctx);
        ctx.Commands.Register(group);
        _instance = group;
        SampleLog.Info(Area, RegisteredLine());

        // Job J21. The same two registrations again, through CommandRegistry with the owner
        // passed explicitly. ctx.Commands.Register is a thin wrapper over exactly these calls
        // that also records the handle for unload; here the handle comes back to us and Detach()
        // is what disposes it. One small class per form, so neither name is registered twice.
        var registryGroup = new SampleGroupRegistryCommands();
        _ownedTypeHandle = CommandRegistry.RegisterType(typeof(SampleGroupOwnedCommands), ctx.Owner);
        _ownedInstanceHandle = CommandRegistry.RegisterInstance(registryGroup, ctx.Owner);
        SampleLog.Info(Area, "registered ." + GroupName + " owned (RegisterType) and ."
            + GroupName + " registry (RegisterInstance), both under ctx.Owner");
    }

    /// <summary>The live instance's use count, for a diagnostic that is not the command itself.</summary>
    internal static int Uses => _instance?.Uses ?? 0;
#else
    /// <summary>Test build: there is no registered instance, so nothing has been used.</summary>
    internal static int Uses => 0;
#endif

    /// <summary>
    /// Unload. Core disposes the registration handle on every reason; this only drops the local
    /// reference so the instance (and the <c>PluginContext</c> it holds) is not kept alive by a
    /// static in a generation that is being torn down.
    /// </summary>
    internal static void Detach()
    {
#if !PROGENITOR_TESTS
        _instance = null;

        // Job J21. These two handles are ours: CommandRegistry took the owner as an argument and
        // recorded nothing on the context, so nothing else will dispose them. Individually, never
        // through UnregisterOwner(ctx.Owner) - that would drop every Sample command.
        try { _ownedTypeHandle?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogError("SampleGroup: owned type handle dispose: " + e); }
        try { _ownedInstanceHandle?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogError("SampleGroup: owned instance handle dispose: " + e); }
        _ownedTypeHandle = null;
        _ownedInstanceHandle = null;
#endif
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}

#if !PROGENITOR_TESTS
/// <summary>
/// The group itself (job FEX5, RD6 J12). An ordinary class, not a C# <c>static</c> one: it is
/// constructed by <see cref="SampleGroup.Attach"/> and registered with
/// <c>ctx.Commands.Register(object)</c>, so the dispatcher invokes these methods on <b>this</b>
/// object and they can read <see cref="_ctx"/> and <see cref="_uses"/> instead of a process-wide
/// static. See <see cref="SampleGroup"/> for why that matters.
/// </summary>
[CommandGroup(SampleGroup.GroupName, SampleGroup.GroupShortHand)]
internal sealed class SampleGroupCommands
{
    private readonly PluginContext _ctx;
    private int _uses;

    /// <summary>Capture the context the group belongs to. One instance per plugin load.</summary>
    internal SampleGroupCommands(PluginContext ctx) =>
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    /// <summary>How many of this group's commands this instance has served.</summary>
    internal int Uses => _uses;

    // Not gated: it sends the caller's own text back to the caller and to nobody else. Contrast
    // .shout, which puts the caller's text into everyone else's chat and is admin-only for that
    // reason.
    [Command("echo", usage: ".sample echo <text>", description: "Echo the rest of the line back to you verbatim. Shows a [CommandGroup] member with a [Remainder] parameter.")]
    public void Echo(ICommandContext ctx, [Remainder] string text = "")
    {
        try
        {
            string cleaned = SampleChat.Clean(text);
            if (cleaned.Length == 0)
            {
                ctx.Reply(SampleGroup.EchoEmptyReply());
                return;
            }

            _uses++;
            string reply = SampleGroup.EchoReply(_uses, cleaned);
            ctx.Reply(reply);
            SampleLog.Info(SampleGroup.Area, reply);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .sample echo threw: " + e);
            ctx.Reply("Sample echo failed. See the server log.");
        }
    }

    // Not gated: it reports the caller's own admin flag and their own chat line back to them.
    [Command("who", usage: ".sample who", description: "Report your IsAdmin flag, the RawText of the line you just typed, and how many commands this group instance has served.")]
    public void Who(ICommandContext ctx)
    {
        try
        {
            _uses++;
            var lines = SampleGroup.WhoLines(ctx.IsAdmin, ctx.RawText, _uses);
            for (int i = 0; i < lines.Count; i++)
            {
                ctx.Reply(lines[i]);
                SampleLog.Info(SampleGroup.Area, lines[i]);
            }

            SampleLog.Info(SampleGroup.Area, "instance is alive on plugin '" + _ctx.Guid + "'");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .sample who threw: " + e);
            ctx.Reply("Sample who failed. See the server log.");
        }
    }
}

/// <summary>
/// Job J21. The <c>CommandRegistry.RegisterType(type, owner)</c> half: a C# <c>static</c> class,
/// so <c>RegisterType</c> is the only form that can take it (an instance form has nothing to
/// construct). Deliberately tiny — the example is the registration call in
/// <see cref="SampleGroup.Attach"/>, not this method.
/// </summary>
[CommandGroup(SampleGroup.GroupName, SampleGroup.GroupShortHand)]
internal static class SampleGroupOwnedCommands
{
    // Not gated: it reports how it was registered, to the caller only.
    [Command("owned", usage: ".sample owned", description: "Report that this command was registered with CommandRegistry.RegisterType(type, ctx.Owner) rather than through ctx.Commands.")]
    public static void Owned(ICommandContext ctx)
    {
        try
        {
            ctx.Reply("This command class is a C# static class registered with "
                + "CommandRegistry.RegisterType(typeof(SampleGroupOwnedCommands), ctx.Owner). The "
                + "owner is the same object ctx.Commands.Register would have used, so unload sweeps "
                + "it either way; what changes is that SampleGroup.Detach() holds and disposes the handle.");
            SampleLog.Info(SampleGroup.Area, "." + SampleGroup.GroupName + " owned served");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .sample owned threw: " + e);
            ctx.Reply("Sample owned failed. See the server log.");
        }
    }
}

/// <summary>
/// Job J21. The <c>CommandRegistry.RegisterInstance(instance, owner)</c> half. It holds a counter
/// for the same reason <see cref="SampleGroupCommands"/> does: instance state is the only thing
/// the instance form buys, so an example without it would not show anything the type form cannot.
/// </summary>
[CommandGroup(SampleGroup.GroupName, SampleGroup.GroupShortHand)]
internal sealed class SampleGroupRegistryCommands
{
    private int _uses;

    // Not gated: it prints how this plugin registered its own commands.
    [Command("registry", usage: ".sample registry", description: "Report the two CommandRegistry forms that take the owner explicitly, and why the Sample never calls RegisterAssembly.")]
    public void Registry(ICommandContext ctx)
    {
        try
        {
            _uses++;
            var lines = SampleGroup.RegistryLines(
                typeRegistered: true, instanceRegistered: true, uses: _uses);
            for (int i = 0; i < lines.Count; i++)
            {
                ctx.Reply(lines[i]);
                SampleLog.Info(SampleGroup.Area, lines[i]);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .sample registry threw: " + e);
            ctx.Reply("Sample registry failed. See the server log.");
        }
    }
}
#endif
