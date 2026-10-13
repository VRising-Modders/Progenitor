using System;
using System.Collections.Generic;
using System.Globalization;

#if !PROGENITOR_TESTS
using ProjectM;
using Progenitor.Access;
using Progenitor.Commands;
using Progenitor.Plugins;
using CoreAccess = Progenitor.Access.Access;
using SampleAccess = Progenitor.Sample.Access.Access;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Job FSAMP, feature 3. <c>.access</c> — the generated access layer
/// (<c>docs/modules.md</c> "Access", <c>docs/tutorial.md</c> "Reaching a private member")
/// exercised from a <b>plugin's own</b> declaration file rather than from Core's.
///
/// <b>What is checked in.</b> <c>samples/Progenitor.Sample/Access/access.txt</c> (the
/// declaration) and <c>samples/Progenitor.Sample/Access/Access.Generated.cs</c> (the generated
/// C#, a normal source file in this csproj — no build-time step, no source generator). Both are
/// produced by the same tool Core uses:
///
/// <code>
/// dotnet run --project tools/accessgen -- \
///   --declarations samples/Progenitor.Sample/Access/access.txt \
///   --out samples/Progenitor.Sample/Access \
///   --namespace Progenitor.Sample.Access --build 248598344
/// </code>
///
/// <b>What it reads.</b> <c>ProjectM.GameDataSystem.RandomLocalizationKeys</c>, a reference-typed
/// instance field (server metadata, <c>ProjectM.Shared</c>)
/// on the system <c>.ember</c> already fetches. It is interop-public on purpose: the point of the
/// demo is that the generated accessor resolves and reads on the live build, and an
/// interop-public member is the one case where the answer can be checked against something else.
/// The offset the runtime hands back through <c>il2cpp_field_get_offset</c> is reported next to
/// the metadata's <c>0x100</c> — the Sample never writes an offset down, it prints the one the
/// runtime computed.
///
/// Parsing and every reply line are game-free so the test project compiles this file. The
/// generated file is <b>not</b> on the test project's compile list: it names game types.
/// </summary>
internal static class SampleAccessDemo
{
    /// <summary>Log/grep area for this feature: <c>Sample access: …</c>.</summary>
    internal const string Area = "access";

    /// <summary>The member the Sample's own <c>access.txt</c> declares.</summary>
    internal const string DeclaredMember = "ProjectM.GameDataSystem.RandomLocalizationKeys";

    /// <summary>The field offset the server metadata records for that field.</summary>
    internal const uint DumpOffset = 0x100;

    /// <summary>Reply when the server world has no <c>GameDataSystem</c> yet.</summary>
    internal const string NoSystemReply =
        "GameDataSystem is not available yet; nothing to read the declared field from.";

    /// <summary>
    /// The <c>.access</c> reply lines. <paramref name="describe"/> is the <b>Sample's own</b>
    /// generated facade's <c>Describe()</c> — <c>access: resolved=N declared=M
    /// interop-verified=X</c> — read <b>after</b> the accessor ran, so the resolved count includes
    /// this read. It is deliberately not Core's: <c>interop-verified</c> is a property of the
    /// artifact the accessor above came from, and printing Core's answer beside a plugin's
    /// accessors was R42 finding 3.
    /// </summary>
    internal static IReadOnlyList<string> Lines(
        string describe,
        int declaredCount,
        string declarationsHash,
        uint offset,
        bool valuePresent)
    {
        string hash = string.IsNullOrEmpty(declarationsHash) ? "(none)" : Short(declarationsHash);
        return new[]
        {
            "Read " + DeclaredMember + " through the Sample's own generated accessor: "
                + (valuePresent ? "value present" : "value null")
                + ", runtime offset 0x" + offset.ToString("X", CultureInfo.InvariantCulture)
                + " (the server metadata records 0x" + DumpOffset.ToString("X", CultureInfo.InvariantCulture)
                + "; nothing in Progenitor writes an offset down).",
            "Sample access.txt: declared=" + declaredCount.ToString(CultureInfo.InvariantCulture)
                + " sha256=" + hash + ". Sample access facade: "
                + (string.IsNullOrEmpty(describe) ? "(unavailable)" : describe) + ".",
        };
    }

    /// <summary>Reply when the accessor threw - the readable one-sentence failure the layer promises.</summary>
    internal static string FailureReply(string message) =>
        "The declared member did not resolve on this build: "
        + (string.IsNullOrWhiteSpace(message) ? "(no message)" : message.Trim());

    /// <summary>First 12 characters of a hash, enough to compare two runs in chat.</summary>
    internal static string Short(string hash) =>
        string.IsNullOrEmpty(hash) ? "" : (hash.Length <= 12 ? hash : hash.Substring(0, 12));

    // ---- Job J21: the three generated forms the field read does not cover, and the failure ----

    /// <summary>
    /// The member <c>.access method</c> reaches, from <b>Core's</b> own declaration file
    /// (<c>src/Progenitor.Core/Access/access.txt</c>). A plugin may use another assembly's
    /// generated facade exactly like its own — the accessors are ordinary public static members —
    /// and the Sample does so here rather than adding a second line to its own
    /// <c>access.txt</c>, because the interesting thing about the method forms is that the two of
    /// them answer different questions about the <em>same</em> member.
    /// </summary>
    internal const string DeclaredMethod = "ProjectM.DeathUtilities.Kill";

    /// <summary>How that member is spelled through Core's generated facade.</summary>
    internal const string MethodAccessor = "Access.DeathUtilities.Kill";

    /// <summary>
    /// A member name that exists on no build. <c>.access missing</c> asks for it on purpose:
    /// <see cref="FailureReply"/> above is only ever seen when something is wrong, so without a
    /// deliberate miss nothing in the repository shows what an <c>AccessException</c> reads like.
    /// </summary>
    internal const string MissingMember = "NoSuchFieldOnAnyBuild";

    /// <summary>Reply for <c>.access type</c>: the interop stub <c>System.Type</c> of each facade.</summary>
    internal static IReadOnlyList<string> TypeLines(string sampleType, string coreType) =>
        new[]
        {
            "Access.<Type>.Type is the interop stub type the accessors resolve against, not the game's "
                + "native class: Sample facade GameDataSystem.Type = "
                + (string.IsNullOrEmpty(sampleType) ? "(unavailable)" : sampleType) + ".",
            "Core facade DeathUtilities.Type = " + (string.IsNullOrEmpty(coreType) ? "(unavailable)" : coreType)
                + ". It is a plain typeof(), so it costs nothing and never throws - it is what you hand "
                + "Harmony or a resolver when you need the type rather than one member of it.",
        };

    /// <summary>
    /// Reply for <c>.access method</c>. Two forms of the one member: the interop stub's
    /// <c>MethodBase</c> (what a Harmony target wants) and the compiled IL2CPP function pointer
    /// (what a native detour wants). Neither is a call — nothing is invoked.
    /// </summary>
    internal static IReadOnlyList<string> MethodLines(string signature, ulong pointer) =>
        new[]
        {
            MethodAccessor + "() (declared " + DeclaredMethod
                + ") resolved the interop stub's MethodBase: "
                + (string.IsNullOrEmpty(signature) ? "(unavailable)" : signature)
                + ". That is a Harmony/ctx.Patches target, not a call - nothing was invoked.",
            MethodAccessor + "Pointer() resolved the compiled function pointer: 0x"
                + pointer.ToString("X", CultureInfo.InvariantCulture)
                + ". That is a native-detour target. It moves with every game build, which is why "
                + "nothing writes one down and the accessor asks the runtime each generation.",
        };

    /// <summary>
    /// Reply for <c>.access missing</c>: the one sentence <c>AccessException</c> promises. The
    /// point of printing it is that it names the member and says where the build is recorded,
    /// so an operator who sees it after a game patch knows what to do next.
    /// </summary>
    internal static string MissingReply(string message) =>
        "Asked for " + DeclaredMember.Substring(0, DeclaredMember.LastIndexOf('.') + 1) + MissingMember
        + ", which exists on no build. AccessException: "
        + (string.IsNullOrWhiteSpace(message) ? "(no message)" : message.Trim());

    /// <summary>
    /// Reply for <c>.access missing</c> when the resolver did <b>not</b> throw. That would mean
    /// the layer answered for a member it cannot have found, which is worse than a miss.
    /// </summary>
    internal static string MissingDidNotThrowReply(uint offset) =>
        "Asked for " + MissingMember + " and the resolver ANSWERED (offset 0x"
        + offset.ToString("X", CultureInfo.InvariantCulture)
        + ") instead of throwing AccessException. Treat that as a bug in the access layer, not as a pass.";

#if !PROGENITOR_TESTS
    /// <summary>Server entry point for <c>.access</c>.</summary>
    internal static void HandleCommand(ICommandContext ctx)
    {
        try
        {
            var gameData = ctx.World.GetExistingSystemManaged<GameDataSystem>();
            if (gameData == null)
            {
                ctx.Reply(NoSystemReply);
                SampleLog.Info(Area, "no GameDataSystem in this world");
                return;
            }

            // One generated accessor, one read. GetObject goes through il2cpp_field_get_value;
            // FieldOffset goes through il2cpp_field_get_offset. Neither is arithmetic we do.
            var value = SampleAccess.GameDataSystem.RandomLocalizationKeysObject(gameData);
            uint offset = SampleAccess.GameDataSystem.RandomLocalizationKeysOffset();

            // The Sample's facade, not Core's: this line's interop-verified= is about
            // samples/Progenitor.Sample/Access/Access.Generated.cs, the file the two calls
            // above went through (R42 finding 3).
            string describe = SampleAccess.Describe();
            var lines = Lines(
                describe,
                SampleAccess.DeclaredCount,
                SampleAccess.DeclarationsHash,
                offset,
                value != IntPtr.Zero);
            for (int i = 0; i < lines.Count; i++)
                ctx.Reply(lines[i]);

            SampleLog.Info(
                Area,
                "read " + DeclaredMember + " offset=0x" + offset.ToString("X", CultureInfo.InvariantCulture)
                + " present=" + (value != IntPtr.Zero)
                + " declared=" + SampleAccess.DeclaredCount
                + " sha256=" + Short(SampleAccess.DeclarationsHash)
                + " | " + describe);
        }
        catch (Exception e)
        {
            ctx.Reply(FailureReply(e.Message));
            SampleLog.Info(Area, "read failed: " + e.Message);
            Plugin.Logger?.LogError("Sample .access threw: " + e);
        }
    }

    // ---- Job J21 -----------------------------------------------------------------------------

    /// <summary>
    /// Register the <c>.access &lt;form&gt;</c> subcommands. They are a
    /// <c>[CommandGroup("access")]</c>, which the dispatcher resolves before the root
    /// <c>.access</c> (<c>CommandDispatcher.cs:96</c> tries a two-token group match first), so the
    /// bare <c>.access</c> is unchanged and <c>.access type</c> / <c>method</c> / <c>missing</c>
    /// are new. The handle is tracked on the context, so unload drops all four.
    /// </summary>
    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        ctx.Commands.Register(typeof(SampleAccessCommands));
        SampleLog.Info(Area, "registered .access field | type | method | missing");
    }

    /// <summary>Unload. Core disposes the tracked registration; there is no local state to drop.</summary>
    internal static void Detach()
    {
    }

    /// <summary>Server entry point for <c>.access type</c>.</summary>
    internal static void HandleType(ICommandContext ctx)
    {
        try
        {
            var lines = TypeLines(
                SampleAccess.GameDataSystem.Type?.FullName ?? "",
                CoreAccess.DeathUtilities.Type?.FullName ?? "");
            for (int i = 0; i < lines.Count; i++) ctx.Reply(lines[i]);
            SampleLog.Info(Area, "type sample=" + (SampleAccess.GameDataSystem.Type?.FullName ?? "?")
                + " core=" + (CoreAccess.DeathUtilities.Type?.FullName ?? "?"));
        }
        catch (Exception e)
        {
            ctx.Reply(FailureReply(e.Message));
            Plugin.Logger?.LogError("Sample .access type threw: " + e);
        }
    }

    /// <summary>Server entry point for <c>.access method</c>.</summary>
    internal static void HandleMethod(ICommandContext ctx)
    {
        try
        {
            var method = CoreAccess.DeathUtilities.Kill();
            var pointer = CoreAccess.DeathUtilities.KillPointer();
            string signature = method == null
                ? ""
                : (method.DeclaringType?.FullName ?? "?") + "::" + method.Name
                  + "/" + method.GetParameters().Length.ToString(CultureInfo.InvariantCulture) + " params";

            var lines = MethodLines(signature, unchecked((ulong)pointer.ToInt64()));
            for (int i = 0; i < lines.Count; i++) ctx.Reply(lines[i]);
            SampleLog.Info(Area, "method " + DeclaredMethod + " signature=" + signature
                + " pointer=0x" + pointer.ToInt64().ToString("X", CultureInfo.InvariantCulture));
        }
        catch (AccessException e)
        {
            // A declared member that does not resolve on this build is the layer's own failure
            // shape, and it is a sentence rather than a stack trace on purpose.
            ctx.Reply(FailureReply(e.Message));
            SampleLog.Info(Area, "method " + DeclaredMethod + " did not resolve: " + e.Message);
        }
        catch (Exception e)
        {
            ctx.Reply(FailureReply(e.Message));
            Plugin.Logger?.LogError("Sample .access method threw: " + e);
        }
    }

    /// <summary>
    /// Server entry point for <c>.access missing</c>. It asks <c>AccessResolver</c> — the same
    /// call a generated field accessor makes — for a member that exists on no build, and prints
    /// the <c>AccessException</c>. It goes to the resolver rather than through a generated
    /// accessor because a generated accessor can only name a member the declaration file
    /// declares, and a checked-in declaration of a member that does not exist would fail
    /// <c>accessgen --check</c> on every machine.
    /// </summary>
    internal static void HandleMissing(ICommandContext ctx)
    {
        try
        {
            uint offset = AccessResolver.FieldOffset(
                SampleAccess.GameDataSystem.Type, "ProjectM.GameDataSystem", MissingMember);
            ctx.Reply(MissingDidNotThrowReply(offset));
            SampleLog.Info(Area, "missing: resolver ANSWERED offset=0x"
                + offset.ToString("X", CultureInfo.InvariantCulture));
        }
        catch (AccessException e)
        {
            ctx.Reply(MissingReply(e.Message));
            SampleLog.Info(Area, "missing: " + e.Message);
        }
        catch (Exception e)
        {
            ctx.Reply(FailureReply(e.Message));
            Plugin.Logger?.LogError("Sample .access missing threw: " + e);
        }
    }
#else
    /// <summary>Test build: there is nothing to register.</summary>
    internal static void Detach()
    {
    }
#endif
}

#if !PROGENITOR_TESTS
/// <summary>
/// Job J21. The <c>.access &lt;form&gt;</c> subcommands, one per generated-accessor shape, so each
/// row of <c>docs/api.md</c>'s Access table has a command that drives exactly it.
///
/// <para>A <c>[CommandGroup]</c> named after an existing root command is deliberate and legal: the
/// dispatcher matches <c>&lt;group&gt; &lt;member&gt;</c> before it matches a root name, so
/// <c>.access</c> stays the field read it always was and the group adds the forms beside it
/// instead of renaming anything an operator already knows.</para>
/// </summary>
[CommandGroup("access")]
internal static class SampleAccessCommands
{
    // adminOnly, like the root .access: these print runtime offsets and function pointers, which
    // is diagnostics for whoever runs the server.
    [Command("field", adminOnly: true, usage: ".access field", description: "Admin only. Same as bare .access: read a declared field through this plugin's own generated accessor.")]
    public static void Field(ICommandContext ctx) => SampleAccessDemo.HandleCommand(ctx);

    [Command("type", adminOnly: true, usage: ".access type", description: "Admin only. Print Access.<Type>.Type for this plugin's facade and for Core's.")]
    public static void TypeForm(ICommandContext ctx) => SampleAccessDemo.HandleType(ctx);

    [Command("method", adminOnly: true, usage: ".access method", description: "Admin only. Resolve a declared method's MethodBase and its compiled function pointer. Nothing is invoked.")]
    public static void Method(ICommandContext ctx) => SampleAccessDemo.HandleMethod(ctx);

    [Command("missing", adminOnly: true, usage: ".access missing", description: "Admin only. Ask for a member that exists on no build and print the AccessException.")]
    public static void Missing(ICommandContext ctx) => SampleAccessDemo.HandleMissing(ctx);
}
#endif
