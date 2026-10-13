using System;
using System.Collections.Generic;
using System.Globalization;

#if !PROGENITOR_TESTS
using ProjectM;
using ProjectM.Network;
using Progenitor.Commands;
using Progenitor.Ecs;
using Progenitor.Plugins;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Job J14 (RD6). The entity toolkit's two unexampled routes: <c>Query.Build</c> with an explicit
/// dispose, and <c>Singletons.TryGetEntity</c> / <c>TryGetRawRW</c>. Both were documented
/// <em>verified live</em> in <c>docs/api.md</c> with <b>none</b> in the Example column, which is
/// the gap this file closes.
///
/// <b><c>.ecs users</c> is interop trap 4, printed.</b> A default entity query skips disabled
/// entities, and a live world is full of them (units out of player range, characters the
/// inactivity timer disabled) — so the query a mod author writes without thinking about it
/// silently returns a subset while reading like "everything". The command runs the same query
/// twice, once with <c>includeDisabled: true</c> and once with <c>false</c>, and prints both
/// counts side by side. <c>SampleChat</c>'s whisper lookup is the case where the roster test
/// matters (you cannot whisper an offline user), so the two files together are the choice, not a
/// rule.
///
/// <b>The gap is never the offline roster</b> (R64 finding 6 and job RD9). Evidence 208 read
/// <c>withDisabled=2 default=2 connected=1 offline=1</c>, and that is the permanent state, not a
/// pending one: <c>OnUserDisconnected</c> removes <c>ConnectedUser</c>, the network buffers and
/// <c>IsConnected</c>, and never adds <c>Disabled</c> to the <c>User</c> entity — verified from
/// the server analyze. So an offline player's <c>User</c> entity is returned by both queries
/// for the life of the save. <see cref="UsersLines"/> branches on what was actually counted — a
/// gap (<c>hidden &gt; 0</c>), no gap but offline users present, or no gap and nobody offline —
/// and never explains a zero gap with a claim the next line of its own reply contradicts.
/// <c>User.IsConnected</c> is the roster; <c>Disabled</c> is an engine state a disconnect does
/// not touch.
///
/// <b>The dispose is the example.</b> <c>Query.Entities</c> builds, copies, and disposes for you;
/// <c>Query.Build</c> hands you the <see cref="Unity.Entities.EntityQuery"/> and you own it. The
/// count path below therefore uses <c>Build</c> inside a <c>try/finally</c> with an explicit
/// <c>query.Dispose()</c>, so a reader copying it copies the dispose too.
///
/// <b><c>.ecs singleton</c> is the anti-crash path.</b> <c>EntityQuery.GetSingleton&lt;T&gt;</c> is
/// a generic interop call over a game value type and can hard-crash the process with no
/// exception and no log (interop trap 2 — it is what killed the prefab spike on
/// <c>ItemKeyLookup</c>). <c>Singletons.TryGetEntity</c> is the replacement: a
/// <c>ComponentType</c>-valued query with <c>IncludeSystems</c>, a count check, and the
/// non-generic <c>GetSingletonEntity</c>. It returns <c>false</c> where the generic would have
/// crashed. The command then asks <c>TryGetRawRW</c> for the component's bytes and prints the
/// <c>TypeManager</c> TypeSize <em>without ever using it as a length</em>: 0 is what this interop
/// reports for ordinary components, and treating it (or a guessed <c>sizeof</c>) as a memcpy
/// budget is the mistake <c>RawWriteGuard</c> exists to refuse.
///
/// <b>One of the three allowlisted names is a tag on purpose.</b> <c>UserMuteSingleton</c> is
/// zero-sized, so <c>TryGetRawRW</c> refuses it: Core reads the
/// <c>ZeroSizeInChunkTypeFlag</c> bit out of the TypeIndex and returns false with one warning
/// rather than letting <c>GetComponentDataRawRW</c> throw (FECS1, evidence 211; evidence 208 is
/// the run before that, where the engine's <c>ArgumentException</c> reached the log as a Core
/// <c>[Error]</c> with an IL2CPP stack trace). The reply says so in those words, because the
/// point of keeping the name in the allowlist is to show what a refusal looks like — a resolved
/// entity with no bytes behind it is a normal, documented outcome and not a miss.
///
/// <b><c>.ecs tag</c> is the structural half of the toolkit.</b> <c>Add&lt;T&gt;()</c>,
/// <c>Has&lt;T&gt;()</c>, <c>Write&lt;T&gt;</c> and <c>Remove&lt;T&gt;</c>, walked in order on a
/// bare entity the command makes with <c>CreateEntity()</c> and destroys before it replies — so
/// no game system ever sees it and nothing reaches the save. That choice of target is the lesson:
/// every one of those calls is a structural change, and doing it to a player's character or
/// <c>User</c> entity is an edit the world keeps. The tag is <c>Unity.Entities.Disabled</c>, the
/// engine's own empty component, added through the <b>value-less</b> <c>Add&lt;T&gt;(em)</c>
/// overload — a zero-sized tag is 1 byte as a C# struct and the value-taking overload would fail
/// the size check on it. The data component is <c>Translation</c>, written typed: sizeof(T)
/// through <c>SetComponentDataRaw</c>, never a byte count the caller chose, because
/// <c>TypeManager</c> TypeSize reads 0 for ordinary components here (interop trap 7).
///
/// <b>The type name is an allowlist, not a lookup.</b> Resolving an arbitrary chat-supplied type
/// name into a <c>ComponentType</c> means handing player input to the interop's type resolver;
/// <see cref="KnownSingletons"/> is three names this file has type handles for, and anything else
/// is refused with the list. Admin-gated as well, for the same reason <c>.castle</c> is: it is a
/// pure read that prints entity ids and component lists, which is operator detail rather than chat.
///
/// Parsing and every reply line are game-free so the test project compiles this file.
/// </summary>
internal static class SampleEcs
{
    /// <summary>Log/grep area: <c>Sample ecs: …</c>.</summary>
    internal const string Area = "ecs";

    /// <summary>The singleton <c>.ecs singleton</c> reads when the caller names none.</summary>
    internal const string DefaultSingleton = "ServerRuntimeSettings";

    /// <summary>
    /// The component types <c>.ecs singleton</c> will resolve. Every one is a
    /// <c>ProjectM</c> <c>IComponentData</c> struct this file holds a compile-time handle for;
    /// <see cref="DefaultSingleton"/> is the one Core itself reads (<c>ModSave</c> takes the world
    /// save folder out of it), so the default is a singleton that is known to exist on a running
    /// server world rather than one picked for looking interesting.
    ///
    /// <c>UserMuteSingleton</c> is here for the opposite reason: it is a <b>zero-sized tag</b>, so
    /// it resolves as an entity and then <c>TryGetRawRW</c> refuses it (Core's
    /// <c>SingletonsZeroSize</c> gate, one warning, no engine exception). Running the two side by
    /// side is the lesson — <c>ServerRuntimeSettings</c> reports TypeSize 0 and hands back a valid
    /// pointer, <c>UserMuteSingleton</c> reports TypeSize 0 and hands back nothing — which is why
    /// TypeSize can never be the tag test (interop trap 7).
    /// </summary>
    internal static readonly IReadOnlyList<string> KnownSingletons = new[]
    {
        DefaultSingleton,
        "UserMuteSingleton",
        "ServerResetScheduleNoticeSettingsSingleton",
    };

    /// <summary>Usage line, replied on a token this command does not know.</summary>
    internal const string UsageReply = "Usage: .ecs users | .ecs singleton [name] | .ecs tag";

    /// <summary>What the caller asked for.</summary>
    internal enum Kind
    {
        /// <summary>No argument, or <c>users</c>: the User-entity census.</summary>
        Users = 0,

        /// <summary>Resolve one singleton entity and describe it.</summary>
        Singleton = 1,

        /// <summary>Add / Has / Write / Remove on a throwaway entity, then destroy it.</summary>
        Tag = 2,
    }

    /// <summary>Parse the first token. Empty means <see cref="Kind.Users"/>.</summary>
    internal static bool TryParse(string? token, out Kind kind)
    {
        kind = Kind.Users;
        if (string.IsNullOrWhiteSpace(token)) return true;

        switch (token.Trim().ToLowerInvariant())
        {
            case "users": kind = Kind.Users; return true;
            case "singleton": kind = Kind.Singleton; return true;
            case "tag": kind = Kind.Tag; return true;
            default: return false;
        }
    }

    /// <summary>
    /// Resolve the optional singleton name against <see cref="KnownSingletons"/>. Empty gives
    /// <see cref="DefaultSingleton"/>. Matching is whole-name and case-insensitive, and the
    /// <em>declared</em> spelling is returned so the reply and the log agree on capitalization
    /// whatever the caller typed.
    /// </summary>
    internal static bool TryParseSingleton(string? token, out string name)
    {
        name = DefaultSingleton;
        if (string.IsNullOrWhiteSpace(token)) return true;

        string wanted = token.Trim();
        for (int i = 0; i < KnownSingletons.Count; i++)
        {
            if (!string.Equals(KnownSingletons[i], wanted, StringComparison.OrdinalIgnoreCase)) continue;
            name = KnownSingletons[i];
            return true;
        }

        name = "";
        return false;
    }

    /// <summary>Reply when the caller named a component this command will not resolve.</summary>
    internal static string UnknownSingletonReply(string? asked) =>
        "'" + SampleChat.Elide(asked, 40) + "' is not one this sample resolves. Known: "
        + string.Join(", ", KnownSingletons)
        + ". The list is an allowlist on purpose - a chat-supplied type name is player input, and "
        + "the interop's type resolver is not a safe place to send it.";

    /// <summary>
    /// The <c>.ecs users</c> reply. <paramref name="withDisabled"/> is the
    /// <c>includeDisabled: true</c> count, <paramref name="withoutDisabled"/> the default one;
    /// the rest is read off the <c>User</c> components of the first query's entities.
    ///
    /// <para>The explanation of the gap is derived from the counts, never asserted. Three states:
    /// a gap (the hidden entities are disabled — trap 4, visible, and never explained by anyone
    /// being offline); no gap but offline users in both counts (the normal, permanent shape — a
    /// disconnect does not disable a <c>User</c> entity); no gap and nobody offline (nothing to
    /// hide). The middle one is what the live run produced (R64 finding 6, evidence 208), and the
    /// old single "they agree" sentence contradicted the very next line of its own reply.</para>
    /// </summary>
    internal static IReadOnlyList<string> UsersLines(int withDisabled, int withoutDisabled, int connected, int offline)
    {
        int hidden = withDisabled - withoutDisabled;
        return new[]
        {
            "Query.Build over ProjectM.Network.User: " + N(withDisabled)
                + " entities with includeDisabled=true, " + N(withoutDisabled) + " with the default. "
                + Agreement(hidden, offline),
            "Of the " + N(withDisabled) + " with disabled included: " + N(connected)
                + " connected, " + N(offline) + " offline (User.IsConnected).",
            "Both queries were disposed explicitly. Query.Build hands you the EntityQuery and you "
                + "own it; Query.Entities is the variant that builds, copies and disposes for you.",
        };
    }

    /// <summary>
    /// The sentence that explains the two counts, chosen from the counts themselves.
    /// <paramref name="hidden"/> is <c>withDisabled - withoutDisabled</c> and
    /// <paramref name="offline"/> is the <c>User.IsConnected == false</c> tally of the first
    /// query's entities. An offline user inside <em>both</em> counts is the ordinary state and
    /// gets its own sentence: being offline and being <c>Disabled</c> are different things, and
    /// a disconnect only ever causes the first, which is what R64 finding 6 caught and job RD9
    /// settled from the analyze.
    /// </summary>
    internal static string Agreement(int hidden, int offline)
    {
        if (hidden > 0)
            return "The " + N(hidden) + " missing from the default query are disabled entities - "
                + "that is interop trap 4. None of them is disabled merely for being offline: a "
                + "disconnect strips the connection components and clears User.IsConnected but "
                + "never disables the User entity, so something else disabled these."
                + (hidden > offline
                    ? " More are hidden than are offline, though: the two numbers come from "
                      + "different places (the engine's Disabled state and User.IsConnected), so "
                      + "this world has a disabled User entity that still reads as connected - "
                      + "worth a look rather than a conclusion."
                    : "");

        if (offline > 0)
            return "The counts agree even though " + N(offline) + " of these users "
                + (offline == 1 ? "is" : "are") + " offline, and that is the normal state, not a "
                + "pending one: a disconnect never disables a User entity - it removes "
                + "ConnectedUser and clears User.IsConnected - so an offline player's User entity "
                + "stays in both queries for the life of the save. User.IsConnected is the roster "
                + "test; the query's disabled option is not, and never was.";

        return "They agree, and none of these users is offline. They would still agree if some "
            + "were: an offline player's User entity is not disabled, so the default query keeps "
            + "returning it. What that option hides is units out of player range and characters "
            + "the inactivity timer disabled.";
    }

    /// <summary>Reply when the census could not be taken at all.</summary>
    internal const string UsersFailedReply =
        "Could not build the User query; nothing was counted. See the server log.";

    /// <summary>
    /// The <c>.ecs singleton</c> reply. <paramref name="describe"/> is
    /// <c>Entity.Describe(em)</c> for the resolved entity; <paramref name="ecsTypeSize"/> is what
    /// <c>TypeManager</c> reported, printed as a fact and never used as a length.
    /// <paramref name="zeroSizedTag"/> is the caller's own read of the TypeIndex's
    /// <c>ZeroSizeInChunkTypeFlag</c> bit, which is the reason a raw read gets refused and
    /// therefore the difference between "refused, as designed" and "resolved but unreadable".
    /// </summary>
    internal static IReadOnlyList<string> SingletonLines(
        string name, bool found, string? describe, bool rawPointer, bool zeroSizedTag, int ecsTypeSize)
    {
        if (!found)
        {
            return new[]
            {
                "Singletons.TryGetEntity(" + name + ") found no unique entity: the query (with "
                    + "IncludeSystems and disabled entities) did not match exactly one. That is a "
                    + "false, not a crash - EntityQuery.GetSingleton<T> is the generic interop call "
                    + "that can take the process down instead (interop trap 2).",
                "On a server world this usually means the component belongs to the client world, "
                    + "or the world is not finished loading.",
            };
        }

        return new[]
        {
            "Singletons.TryGetEntity(" + name + ") resolved exactly one entity: "
                + SampleChat.Elide(describe, DescribeChars),
            rawPointer
                ? "Singletons.TryGetRawRW gave a live pointer to its bytes; TypeManager TypeSize reads "
                  + N(ecsTypeSize) + ". " + TypeSizeNote(ecsTypeSize)
                : RawRefusedLine(name, zeroSizedTag),
            "Neither call went through EntityQuery.GetSingleton<T>: that generic can hard-crash "
                + "this interop with no exception and no log, which is why Core has this module at all.",
        };
    }

    /// <summary>
    /// What the reply says when the entity resolved but <c>TryGetRawRW</c> handed back no pointer.
    /// A zero-sized (tag) component is a <em>refusal</em> with a documented contract, not a
    /// mystery: Core tests the <c>ZeroSizeInChunkTypeFlag</c> bit in the TypeIndex and returns
    /// false with one warning before the engine can throw (FECS1). Saying "there is nothing to
    /// read" for both cases is what R64 finding 5 caught — an allowlisted, advertised choice whose
    /// ordinary outcome read like a fault.
    /// </summary>
    internal static string RawRefusedLine(string name, bool zeroSizedTag) =>
        zeroSizedTag
            ? "Singletons.TryGetRawRW refused it: " + name + " is a zero-sized (tag) component, so "
              + "it owns no bytes in the chunk and there is no pointer to give. Core decides that "
              + "from the ZeroSizeInChunkTypeFlag (0x"
              + Progenitor.Ecs.SingletonsZeroSize.ZeroSizeInChunkTypeFlag.ToString("X", CultureInfo.InvariantCulture)
              + ") bit carried by the TypeIndex itself and logs one warning; before that gate the "
              + "engine threw ArgumentException and Core filed it as an [Error] with an IL2CPP "
              + "stack trace. This is the expected answer for a tag and the reason the name is in "
              + "the allowlist: TryGetEntity above is the whole story for it, because a tag's "
              + "presence is its value. TypeSize is not the test - it reads 0 for ordinary "
              + "components too (interop trap 7), and one of the other names here proves it."
            : "Singletons.TryGetRawRW returned no pointer even though the entity resolved, and "
              + name + " is not a zero-sized tag - so this is not the refusal path. Something else "
              + "failed; the server log has the line.";

    /// <summary>How much of an <c>Entity.Describe</c> component list reaches chat.</summary>
    internal const int DescribeChars = 300;

    /// <summary>
    /// The zero-sized tag <c>.ecs tag</c> adds and removes. <c>Unity.Entities.Disabled</c> is the
    /// engine's own empty <c>IComponentData</c>, so it is guaranteed present in every interop
    /// build and has no gameplay meaning of its own — and it is the very tag interop trap 4 is
    /// about, which is why this file picked it over a ProjectM one.
    /// </summary>
    internal const string TagComponentName = "Unity.Entities.Disabled";

    /// <summary>The unmanaged component the typed <c>Write&lt;T&gt;</c> goes to.</summary>
    internal const string DataComponentName = "Unity.Transforms.Translation";

    /// <summary>X of the position <c>.ecs tag</c> writes. Arbitrary; it only has to read back.</summary>
    internal const float TagX = 1.5f;

    /// <summary>Y of the position <c>.ecs tag</c> writes.</summary>
    internal const float TagY = 2.5f;

    /// <summary>Z of the position <c>.ecs tag</c> writes.</summary>
    internal const float TagZ = 3.5f;

    /// <summary>Reply when the throwaway entity could not be made or the walk threw.</summary>
    internal const string TagFailedReply =
        "The Add/Write/Remove walk did not finish; the throwaway entity was destroyed anyway. "
        + "See the server log.";

    /// <summary>A position as <c>.ecs tag</c> prints it, game-free.</summary>
    internal static string Vec(float x, float y, float z) =>
        "(" + F(x) + ", " + F(y) + ", " + F(z) + ")";

    /// <summary>
    /// The <c>.ecs tag</c> reply: one line per step of the structural-change walk, each carrying
    /// the read-back that proves it. Every value comes from the caller so the whole reply is
    /// game-free and pinned by the test project.
    /// </summary>
    internal static IReadOnlyList<string> TagLines(
        string? created,
        bool hasAfterAdd,
        string wrote,
        string readBack,
        bool hasAfterRemove,
        bool existsAfterDestroy)
    {
        return new[]
        {
            "1. EntityManager.CreateEntity() -> " + SampleChat.Elide(created, DescribeChars)
                + ". A bare entity this command owns. No game system ever sees it: it is destroyed "
                + "at step 5, in the same command, on the same frame.",
            "2. Entity.Add<" + TagComponentName + ">(em) -> Has<" + TagComponentName + ">()="
                + Yn(hasAfterAdd)
                + ". That is the value-less overload, the one a zero-sized tag needs - an empty C# "
                + "struct is 1 byte and the value-taking overload would fail the size check on it.",
            "3. Entity.Add<" + DataComponentName + ">(em, in value) wrote " + wrote
                + " -> TryRead gives " + readBack
                + ". Typed Write<T> is a SetComponentDataRaw of sizeof(T); TypeManager TypeSize "
                + "reads 0 for ordinary components through this interop and is never the length "
                + "(RawWriteGuard refuses a caller-chosen byte count at TypeSize 0).",
            "4. Entity.Remove<" + TagComponentName + ">(em) -> Has<" + TagComponentName + ">()="
                + Yn(hasAfterRemove) + ".",
            "5. DestroyEntity -> Exists()=" + Yn(existsAfterDestroy)
                + ". Nothing is left behind, which is the point: this walk is only ever run on an "
                + "entity the plugin made. Never do it to a player's character or User entity - "
                + "a structural change there is a save-visible edit the game did not ask for.",
        };
    }

    /// <summary>
    /// What the printed TypeSize means. 0 is the normal reading for an ordinary component through
    /// this interop, and the sentence exists so nobody copies the number into a <c>memcpy</c>.
    /// </summary>
    internal static string TypeSizeNote(int ecsTypeSize) =>
        ecsTypeSize <= 0
            ? "0 is what this interop reports for ordinary components - it is not a length. Poke "
              + "fields through the pointer; a memcpy needs RawWriteGuard.EnsureOverlayMemcpy, "
              + "never a guessed sizeof."
            : "A nonzero TypeSize is a cross-check against the interop struct's own sizeof, not a "
              + "license to copy bytes on its say-so - RawWriteGuard still decides.";

#if !PROGENITOR_TESTS
    /// <summary>
    /// Server-side registration. The command class is a C# static one, so
    /// <c>Register(Type)</c> is the path (a static class cannot be a generic argument, and
    /// cannot be instantiated for the instance overload <see cref="SampleGroup"/> shows). The
    /// handle is tracked on the context, so unload drops the command on every reason.
    /// </summary>
    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        ctx.Commands.Register(typeof(SampleEcsCommands));
        SampleLog.Info(Area, "registered .ecs (users | singleton [" + string.Join("|", KnownSingletons) + "] | tag)");
    }

    /// <summary>Server entry point for <c>.ecs users</c>.</summary>
    internal static void HandleUsers(ICommandContext ctx)
    {
        if (!TryCountUsers(ctx.World, out int withDisabled, out int withoutDisabled, out int connected, out int offline))
        {
            ctx.Reply(UsersFailedReply);
            SampleLog.Info(Area, "users census failed");
            return;
        }

        var lines = UsersLines(withDisabled, withoutDisabled, connected, offline);
        for (int i = 0; i < lines.Count; i++)
            ctx.Reply(lines[i]);
        SampleLog.Info(Area, "users withDisabled=" + N(withDisabled) + " default=" + N(withoutDisabled)
            + " connected=" + N(connected) + " offline=" + N(offline));
    }

    /// <summary>
    /// The census. Two <c>Query.Build</c> calls, each disposed in a <c>finally</c> — that dispose
    /// is the half of the API this command exists to show. Entities come out of the first query
    /// as a <c>NativeArray&lt;Entity&gt;</c>, an instantiation the game itself uses everywhere,
    /// and each <c>User</c> is read with <c>GetComponentData&lt;User&gt;</c> because <c>User</c>
    /// carries bools and so is not <c>unmanaged</c> (interop trap 9).
    /// </summary>
    private static bool TryCountUsers(World world, out int withDisabled, out int withoutDisabled, out int connected, out int offline)
    {
        withDisabled = 0;
        withoutDisabled = 0;
        connected = 0;
        offline = 0;
        try
        {
            var em = world.EntityManager;
            var userType = ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly);

            var all = Query.Build(em, includeDisabled: true, includeSystems: false, userType);
            try
            {
                withDisabled = all.CalculateEntityCount();
                var entities = all.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < entities.Length; i++)
                    {
                        var user = em.GetComponentData<User>(entities[i]);
                        if (user.IsConnected) connected++;
                        else offline++;
                    }
                }
                finally
                {
                    entities.Dispose();
                }
            }
            finally
            {
                all.Dispose();
            }

            var onlineOnly = Query.Build(em, includeDisabled: false, includeSystems: false, userType);
            try
            {
                withoutDisabled = onlineOnly.CalculateEntityCount();
            }
            finally
            {
                onlineOnly.Dispose();
            }

            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .ecs users threw: " + e);
            return false;
        }
    }

    /// <summary>Server entry point for <c>.ecs singleton [name]</c>.</summary>
    internal static void HandleSingleton(ICommandContext ctx, string name)
    {
        bool found;
        string describe = "";
        bool rawPointer = false;
        bool zeroSizedTag = false;
        int ecsTypeSize = 0;

        switch (name)
        {
            case "UserMuteSingleton":
                found = Read<UserMuteSingleton>(ctx.World, out describe, out rawPointer, out zeroSizedTag, out ecsTypeSize);
                break;
            case "ServerResetScheduleNoticeSettingsSingleton":
                found = Read<ServerResetScheduleNoticeSettingsSingleton>(ctx.World, out describe, out rawPointer, out zeroSizedTag, out ecsTypeSize);
                break;
            default:
                found = Read<ServerRuntimeSettings>(ctx.World, out describe, out rawPointer, out zeroSizedTag, out ecsTypeSize);
                break;
        }

        var lines = SingletonLines(name, found, describe, rawPointer, zeroSizedTag, ecsTypeSize);
        for (int i = 0; i < lines.Count; i++)
            ctx.Reply(lines[i]);
        SampleLog.Info(Area, "singleton " + name + " found=" + found + " raw=" + rawPointer
            + " tag=" + zeroSizedTag + " typeSize=" + N(ecsTypeSize));
    }

    /// <summary>
    /// Server entry point for <c>.ecs tag</c>: the entity toolkit's structural half —
    /// <c>Add&lt;T&gt;()</c>, <c>Has&lt;T&gt;()</c>, <c>Write&lt;T&gt;</c>, <c>Remove&lt;T&gt;</c>
    /// — walked on an entity this command makes and destroys.
    ///
    /// <para><b>A throwaway, not a live entity.</b> Every step here is a structural change, and a
    /// structural change on a player's character or <c>User</c> entity is an edit the save keeps.
    /// So the walk runs on a bare <c>CreateEntity()</c> that no query will ever match and no
    /// system will ever tick: it is destroyed in the <c>finally</c> of this very method, on the
    /// frame it was made. That is also why the destroy is in a <c>finally</c> — a throw halfway
    /// through must not leave a componentless entity in the world.</para>
    /// </summary>
    internal static void HandleTag(ICommandContext ctx)
    {
        var em = ctx.World.EntityManager;
        Entity entity = Entity.Null;
        try
        {
            entity = em.CreateEntity();
            string created = entity.Describe(em);

            // The tag: the value-less Add overload, which is what a zero-sized component needs.
            entity.Add<Disabled>(em);
            bool hasAfterAdd = entity.Has<Disabled>(em);

            // The data component: add-then-write in one call, then read it back typed.
            var value = new Translation { Value = new float3(TagX, TagY, TagZ) };
            entity.Add(em, in value);
            string readBack = entity.TryRead(em, out Translation got)
                ? Vec(got.Value.x, got.Value.y, got.Value.z)
                : "<TryRead returned false>";

            entity.Remove<Disabled>(em);
            bool hasAfterRemove = entity.Has<Disabled>(em);

            em.DestroyEntity(entity);
            bool existsAfterDestroy = entity.Exists(em);
            entity = Entity.Null;

            var lines = TagLines(
                created, hasAfterAdd, Vec(TagX, TagY, TagZ), readBack, hasAfterRemove, existsAfterDestroy);
            for (int i = 0; i < lines.Count; i++)
                ctx.Reply(lines[i]);
            SampleLog.Info(Area, "tag added=" + hasAfterAdd + " wrote=" + Vec(TagX, TagY, TagZ)
                + " read=" + readBack + " removed=" + !hasAfterRemove + " destroyed=" + !existsAfterDestroy);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .ecs tag threw: " + e);
            ctx.Reply(TagFailedReply);
        }
        finally
        {
            // A throw halfway through must not leave the throwaway entity in the world.
            try
            {
                if (entity != Entity.Null && em.Exists(entity))
                    em.DestroyEntity(entity);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError("Sample .ecs tag cleanup threw: " + e);
            }
        }
    }

    /// <summary>
    /// <c>TryGetEntity</c> then <c>TryGetRawRW</c> for one component type. C# generic only: the
    /// interop calls underneath are <c>ComponentIndex.Of&lt;T&gt;</c> (an
    /// <c>Il2CppType.Of&lt;T&gt;</c> class pointer, not a native generic instantiation) and the
    /// non-generic <c>GetSingletonEntity</c> / <c>GetComponentDataRawRW</c>.
    /// </summary>
    private static bool Read<T>(World world, out string describe, out bool rawPointer, out bool zeroSizedTag, out int ecsTypeSize)
    {
        describe = "";
        rawPointer = false;
        zeroSizedTag = false;
        ecsTypeSize = 0;
        try
        {
            var em = world.EntityManager;
            var componentType = ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly);

            // The same bit Core's gate reads, from the TypeIndex this caller already holds: no
            // interop call, no TypeSize. It is what turns "no pointer" into "refused, as designed"
            // in the reply, and a mod wanting to skip the warning altogether can ask it first.
            zeroSizedTag = SingletonsZeroSize.IsZeroSized(ComponentIndex.IndexOf<T>().Value);

            if (!Singletons.TryGetEntity(em, componentType, out var entity))
                return false;

            describe = entity.Describe(em);
            rawPointer = Singletons.TryGetRawRW(em, ComponentIndex.IndexOf<T>(), out _, out ecsTypeSize);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .ecs singleton read threw: " + e);
            return false;
        }
    }
#endif

    /// <summary>
    /// Unload. The command registration is tracked on the context, so Core removes it on every
    /// reason; there is nothing else here to drop. Kept so a reader copying this file has the
    /// hook to put their own teardown in.
    /// </summary>
    internal static void Detach()
    {
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string F(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Yn(bool value) => value ? "True" : "False";
}

#if !PROGENITOR_TESTS
/// <summary>
/// The <c>.ecs</c> command (job J14). Separate from <see cref="SampleEcs"/> so the helper class
/// stays compilable in the game-free test project: this one binds <c>ICommandContext</c>.
/// </summary>
internal static class SampleEcsCommands
{
    // adminOnly: a pure read, gated like .castle, .epoch and .access - it prints entity ids,
    // component-type lists and a runtime TypeSize, which is operator diagnostics rather than chat.
    [Command("ecs", adminOnly: true, usage: ".ecs users | .ecs singleton [name] | .ecs tag", description: "Admin only. Count User entities with and without disabled ones, resolve a singleton entity through Singletons, or walk Add/Has/Write/Remove on a throwaway entity.")]
    public static void Ecs(ICommandContext ctx, string what = "", string name = "")
    {
        try
        {
            if (!SampleEcs.TryParse(what, out var kind))
            {
                ctx.Reply(SampleEcs.UsageReply);
                return;
            }

            if (kind == SampleEcs.Kind.Users)
            {
                SampleEcs.HandleUsers(ctx);
                return;
            }

            if (kind == SampleEcs.Kind.Tag)
            {
                SampleEcs.HandleTag(ctx);
                return;
            }

            if (!SampleEcs.TryParseSingleton(name, out string resolved))
            {
                ctx.Reply(SampleEcs.UnknownSingletonReply(name));
                return;
            }

            SampleEcs.HandleSingleton(ctx, resolved);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .ecs threw: " + e);
            ctx.Reply("Sample ecs failed. See the server log.");
        }
    }
}
#endif
