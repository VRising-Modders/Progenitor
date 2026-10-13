using System;
using System.Collections.Generic;

#if !PROGENITOR_TESTS
using ProjectM;
using Progenitor.Commands;
using Progenitor.Plugins;
using LocalizationApi = Progenitor.Localization.Localization;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Job FEX3 (RD6 J10). The <b>programmatic</b> localization path: a string that exists in no file,
/// registered from code with <c>Localization.Register</c> and read back with
/// <c>IsRegistered</c> / <c>KeyFor</c> / <c>TryGetGameString</c>, standing next to the Sample's
/// file-loaded keys so a reader can see both routes at once.
///
/// <b>The two routes, and why a mod needs the second one.</b> <c>localization/en.json</c> beside
/// the DLL is auto-loaded before <c>OnLoad</c> (<c>Localization.cs:227</c>,
/// <c>LoadForPlugin</c>) and is the right answer for anything a translator should be able to edit
/// — the Sample's item and buff names come from there. It cannot answer for a string whose
/// <em>key</em> is only known at runtime: one per generated item, per config value, per server.
/// <see cref="Register"/> is that path, and it is the same call the file loader ends in, so
/// dispose and owner cleanup behave identically.
///
/// <b>What each side actually does</b> (<c>Localization.cs:78-85</c>). The client writes the
/// string into the game's own table (<c>Stunlock.Localization.Localization.LocalizedStrings</c>)
/// so UI can resolve it, and re-writes it after a language change. The server has no string table
/// at all: <c>Register</c> only records, which is enough for <c>IsRegistered</c> and for the
/// prefab applicator's <c>id.desc</c> lookup, and is why
/// <c>TryGetGameString</c> is documented as always false there
/// (<c>Localization.cs:270-278</c>). <c>.loc</c> says which of those two it is running as instead
/// of leaving a reader to wonder why the server prints no text.
///
/// <b>Live-check split.</b> The server half is provable from the server log and the <c>.loc</c>
/// reply alone. The half that matters visually — the string being in the game's table — needs a
/// client: it is the <c>Sample loc: client table …</c> line the client writes at world-ready.
///
/// Not admin-gated: it reads this plugin's own strings and nothing else. Contrast <c>.castle</c>
/// and <c>.access</c>, which are gated because they print operator-shaped detail about the
/// server; a mod's own display string is not that.
///
/// <b>The two subcommands, and why they are a <c>[CommandGroup]</c>.</b> <c>.loc</c> itself is a
/// bare command declared in <see cref="SampleCommands"/>. The two that take arguments live here
/// instead, on a <c>[CommandGroup("loc")]</c> class, because the dispatcher looks a two-token line
/// up in the group table <em>before</em> it looks the first token up in the root table
/// (<c>Commands/CommandDispatcher.cs:96,103</c>) — so <c>.loc</c> keeps reaching the bare command
/// and <c>.loc key …</c> / <c>.loc apply …</c> reach these, with no second registration of the
/// name <c>loc</c> anywhere. It is the pattern for adding arguments to somebody else's command
/// without editing their file.
///
/// <list type="bullet">
/// <item><description><c>.loc key &lt;id&gt;</c> — <c>KeyFor</c>, <c>IsRegistered</c> and
/// <c>TryGetGameString</c> for any key, not just the two this file owns. <c>KeyFor</c> is
/// deterministic (MD5 of the key's UTF-8 bytes packed into an <c>AssetGuid</c>,
/// <c>Localization.cs:367</c>), which is what lets a server and a client agree on a key nobody
/// shipped in an asset bundle; the reply prints the first word of that GUID so two processes can
/// be compared by eye.</description></item>
/// <item><description><c>.loc apply &lt;id&gt; &lt;text&gt;</c> — <c>Register</c> under
/// <c>&lt;id&gt;</c> and <c>&lt;id&gt;.desc</c>, then <c>ApplyName</c> / <c>ApplyDescription</c>
/// pointing the Ember Shard's cloned <c>ManagedItemData</c> at those two keys. That is the pair
/// the prefab applicator itself uses for <c>Named()</c> / <c>Described()</c>
/// (<c>Content/PrefabApplicator.cs:1257,1270</c>), done by hand so a reader can see what those
/// builder calls actually do.</description></item>
/// </list>
///
/// <b>What <c>.loc apply</c> can and cannot finish on the server.</b> Chat commands run on the
/// server, and the server's <c>ManagedDataRegistry</c> is empty — the whole managed-data layer is
/// client-side (<c>PrefabApplicator.cs:1176-1181</c> short-circuits on exactly that). So the
/// registration half really happens and <c>IsRegistered</c> flips; the <c>ApplyName</c> /
/// <c>ApplyDescription</c> half finds no <c>ManagedItemData</c> to point and says so in as many
/// words. The call is written unconditionally against whatever the local world hands back, so the
/// same code is the working path in a client-side plugin; it is not a server-only stub.
///
/// Admin-gated, unlike bare <c>.loc</c>: <c>apply</c> writes a display string other players will
/// read, and <c>key</c> takes a caller-supplied string. Both are validated
/// (<see cref="TryParseId"/>, <see cref="TryParseText"/>) before anything is registered.
///
/// Reload-safe: the registration is an <c>IDisposable</c> tracked on the context, so unload drops
/// the string; the <c>.loc apply</c> registrations are held in <see cref="Detach"/>-cleared
/// fields as well, so a repeat replaces rather than stacks.
/// </summary>
internal static class SampleLocalization
{
    /// <summary>Log/grep area: <c>Sample loc: …</c>.</summary>
    internal const string Area = "loc";

    /// <summary>
    /// The runtime key. Deliberately <b>not</b> in <c>localization/en.json</c> — a key that is
    /// also in the file would prove nothing about this path.
    /// </summary>
    internal const string RuntimeKey = "sample.loc.runtime";

    /// <summary>Text registered under <see cref="RuntimeKey"/>.</summary>
    internal const string RuntimeText = "Registered from code, not from a file.";

    /// <summary>
    /// A key the file loader owns, printed beside the runtime one so the reply shows both routes.
    /// This is the Ember Shard's name in <c>localization/en.json</c>.
    /// </summary>
    internal const string FileKey = "sample.ember";

    /// <summary>How the reply names a string the game table does not have.</summary>
    internal const string NoGameString = "<not in the game table>";

    /// <summary>The line written when the string is registered, on either side.</summary>
    internal static string RegisteredLine(bool isServer) =>
        "registered '" + RuntimeKey + "' = \"" + RuntimeText + "\" at runtime ("
        + (isServer
            ? "server: recorded only, there is no game string table here"
            : "client: also written into the game's table")
        + ")";

    /// <summary>
    /// The client's read-back line, written once the world is ready. This is the one line that
    /// proves the string reached the game rather than only this module's list.
    /// </summary>
    internal static string ClientTableLine(string? gameText) =>
        "client table: '" + RuntimeKey + "' -> " + Quote(gameText);

    /// <summary>A game-table value as the reply prints it, or the not-present marker.</summary>
    internal static string Quote(string? text) =>
        string.IsNullOrEmpty(text) ? NoGameString : "\"" + text + "\"";

    /// <summary>
    /// The <c>.loc</c> reply. <paramref name="runtimeGameText"/> and
    /// <paramref name="fileGameText"/> are what <c>TryGetGameString</c> returned (empty when it
    /// returned false), so on the server they are always empty and the closing line says why.
    /// </summary>
    internal static IReadOnlyList<string> Lines(
        bool isServer,
        bool runtimeRegistered,
        string? runtimeGameText,
        bool fileRegistered,
        string? fileGameText)
    {
        var lines = new List<string>(4)
        {
            "runtime key '" + RuntimeKey + "': IsRegistered=" + Yn(runtimeRegistered)
                + " game table=" + Quote(runtimeGameText),
            "file key '" + FileKey + "' (localization/en.json): IsRegistered=" + Yn(fileRegistered)
                + " game table=" + Quote(fileGameText),
        };

        lines.Add(isServer
            ? "This is the server: Register records the string and IsRegistered answers, but there "
                + "is no game string table here, so both game-table reads are false by design. The "
                + "client writes the table; grep its log for 'Sample loc: client table'."
            : "This is the client: the game table is the real one, so a quoted value above is what "
                + "UI would resolve for that key.");
        return lines;
    }

    /// <summary>Longest key <c>.loc key</c> / <c>.loc apply</c> will take.</summary>
    internal const int MaxIdLength = 64;

    /// <summary>Longest display string <c>.loc apply</c> will register.</summary>
    internal const int MaxTextLength = 120;

    /// <summary>Suffix the applicator uses for an item's tooltip key, and so does this command.</summary>
    internal const string DescriptionSuffix = ".desc";

    /// <summary>Usage line for the two argument-taking subcommands.</summary>
    internal const string UsageReply = "Usage: .loc key <id> | .loc apply <id> <text>";

    /// <summary>
    /// Accept a localization key from chat. Printable ASCII with no whitespace, at most
    /// <see cref="MaxIdLength"/> characters: a key is a lookup token, and letting a player put a
    /// space or a control character in one only produces a key nothing can ever match.
    /// </summary>
    internal static bool TryParseId(string? id, out string clean, out string error)
    {
        clean = "";
        error = "";
        string trimmed = (id ?? "").Trim();
        if (trimmed.Length == 0)
        {
            error = "A key id is required. " + UsageReply;
            return false;
        }

        if (trimmed.Length > MaxIdLength)
        {
            error = "That key is " + trimmed.Length + " characters; the limit is " + MaxIdLength + ".";
            return false;
        }

        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c > ' ' && c < (char)127) continue;
            error = "A key is printable ASCII with no spaces; '" + SampleChat.Elide(trimmed, 40)
                + "' has a character at position " + (i + 1) + " that is not.";
            return false;
        }

        clean = trimmed;
        return true;
    }

    /// <summary>
    /// Accept the display string. Non-empty after trimming and at most
    /// <see cref="MaxTextLength"/> characters; unlike a definition packet's description this one
    /// never goes on the wire, so the cap is a chat-sanity limit rather than a protocol one.
    /// </summary>
    internal static bool TryParseText(string? text, out string clean, out string error)
    {
        clean = "";
        error = "";
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            error = "Some text to register is required. " + UsageReply;
            return false;
        }

        if (trimmed.Length > MaxTextLength)
        {
            error = "That text is " + trimmed.Length + " characters; the limit is " + MaxTextLength + ".";
            return false;
        }

        clean = trimmed;
        return true;
    }

    /// <summary>
    /// The <c>.loc key &lt;id&gt;</c> reply. <paramref name="keyWord"/> is the first 32 bits of
    /// the <c>AssetGuid</c> <c>KeyFor</c> derived, printed so two processes can be compared by
    /// eye; <paramref name="gameText"/> is what <c>TryGetGameString</c> returned, empty when it
    /// returned false.
    /// </summary>
    internal static IReadOnlyList<string> KeyLines(
        string id, int keyWord, bool registered, bool isServer, string? gameText)
    {
        return new[]
        {
            "KeyFor(\"" + id + "\") -> AssetGuid starting " + keyWord.ToString("X8")
                + ". It is MD5 of the key's own bytes, so every process derives the same one "
                + "without anybody shipping an asset for it.",
            "IsRegistered(\"" + id + "\") = " + Yn(registered)
                + " - that is this module's own record, and it answers on the server too.",
            "TryGetGameString -> " + Quote(gameText)
                + (isServer
                    ? " (server: always false, there is no game string table in this process)"
                    : " (client: this is what UI would resolve)"),
        };
    }

    /// <summary>
    /// The <c>.loc apply &lt;id&gt; &lt;text&gt;</c> reply. <paramref name="managedFound"/> is
    /// whether the local world had a <c>ManagedItemData</c> for the Sample's own item to point.
    /// </summary>
    internal static IReadOnlyList<string> ApplyLines(
        string id, string text, string itemId, bool isServer, bool managedFound)
    {
        var lines = new List<string>(3)
        {
            "Registered '" + id + "' and '" + id + DescriptionSuffix + "' = \"" + text
                + "\" at runtime; both are now IsRegistered.",
        };

        if (managedFound)
        {
            lines.Add("ApplyName(data, \"" + id + "\") and ApplyDescription(data, \"" + id
                + DescriptionSuffix + "\") pointed " + itemId + "'s cloned ManagedItemData at "
                + "them. That is exactly what the .Named()/.Described() builder calls do for you.");
            lines.Add("Nothing was copied: the two calls write a LocalizationKey, and the text "
                + "itself is resolved out of the string table at draw time.");
            return lines;
        }

        lines.Add("No ManagedItemData for " + itemId + " in this world, so ApplyName and "
            + "ApplyDescription had nothing to point.");
        lines.Add(isServer
            ? "That is expected here: the managed-data registry is a client-side layer and is "
              + "empty on a server, which is why the applicator skips it too. The registration "
              + "above is the half a server can do, and it is the half that matters - a client "
              + "with this key registered will resolve it."
            : "On a client that usually means the item has not been applied yet; run it again "
              + "once the world is up.");
        return lines;
    }

#if !PROGENITOR_TESTS
    private static IDisposable? _appliedName;
    private static IDisposable? _appliedDescription;

    /// <summary>
    /// Register the runtime string on whichever side this is, and — on the client — read it back
    /// out of the game's table once the world exists. The registration is tracked on the context,
    /// so unload disposes it and the string goes with the plugin.
    /// </summary>
    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        try
        {
            ctx.Track(LocalizationApi.Register(RuntimeKey, RuntimeText, "English", ctx.Owner));
            SampleLog.Info(Area, RegisteredLine(ctx.IsServer));

            if (ctx.IsServer)
            {
                // The [CommandGroup] half. Server only: chat commands are a server thing, and the
                // group table is what keeps `.loc key x` off the bare `.loc` in SampleCommands.
                ctx.Commands.Register(typeof(SampleLocalizationCommands));
                SampleLog.Info(Area, "registered .loc key <id> and .loc apply <id> <text>");
                return;
            }

            // The game's own Localization may not have built its table when a plugin loads, so
            // the read-back waits for the world rather than reporting a miss that is only early.
            ctx.Track(ProgenitorPlugin.WhenWorldReady(ctx, _ =>
            {
                LocalizationApi.TryGetGameString(LocalizationApi.KeyFor(RuntimeKey), out string text);
                SampleLog.Info(Area, ClientTableLine(text));
            }));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample localization attach threw: " + e);
        }
    }

    /// <summary>Server entry point for <c>.loc</c>.</summary>
    internal static void HandleCommand(ICommandContext ctx)
    {
        var plugin = Plugin.Active;
        bool isServer = plugin == null || plugin.IsServer;

        LocalizationApi.TryGetGameString(LocalizationApi.KeyFor(RuntimeKey), out string runtimeText);
        LocalizationApi.TryGetGameString(LocalizationApi.KeyFor(FileKey), out string fileText);

        var lines = Lines(
            isServer,
            LocalizationApi.IsRegistered(RuntimeKey),
            runtimeText,
            LocalizationApi.IsRegistered(FileKey),
            fileText);

        for (int i = 0; i < lines.Count; i++)
        {
            ctx.Reply(lines[i]);
            SampleLog.Info(Area, lines[i]);
        }
    }

    /// <summary>Server entry point for <c>.loc key &lt;id&gt;</c>.</summary>
    internal static void HandleKey(ICommandContext ctx, string id)
    {
        var plugin = Plugin.Active;
        bool isServer = plugin == null || plugin.IsServer;

        var key = LocalizationApi.KeyFor(id);
        LocalizationApi.TryGetGameString(key, out string gameText);

        var lines = KeyLines(id, key.Key._a, LocalizationApi.IsRegistered(id), isServer, gameText);
        for (int i = 0; i < lines.Count; i++)
        {
            ctx.Reply(lines[i]);
            SampleLog.Info(Area, lines[i]);
        }
    }

    /// <summary>
    /// Server entry point for <c>.loc apply &lt;id&gt; &lt;text&gt;</c>. Registers the two keys,
    /// then points the Sample's own item at them through <c>ApplyName</c> /
    /// <c>ApplyDescription</c>. The previous pair of registrations is disposed first so a repeat
    /// replaces rather than stacks.
    /// </summary>
    internal static void HandleApply(ICommandContext ctx, string id, string text)
    {
        var plugin = Plugin.Active;
        bool isServer = plugin == null || plugin.IsServer;
        object? owner = plugin?.Owner;

        DropApplied();
        _appliedName = LocalizationApi.Register(id, text, "English", owner);
        _appliedDescription = LocalizationApi.Register(id + DescriptionSuffix, text, "English", owner);

        bool managedFound = TryApplyToEmber(ctx, id);

        var lines = ApplyLines(id, text, Plugin.EmberId, isServer, managedFound);
        for (int i = 0; i < lines.Count; i++)
        {
            ctx.Reply(lines[i]);
            SampleLog.Info(Area, lines[i]);
        }
    }

    /// <summary>
    /// Point the Ember Shard's cloned <c>ManagedItemData</c> at <paramref name="id"/> and
    /// <c>&lt;id&gt;.desc</c>. False when this world has no managed data for it — the normal
    /// server answer, since the registry is a client-side layer.
    /// <c>ManagedDataRegistry.TryGet&lt;T&gt;</c> takes the GUID as an int and builds the key
    /// natively, so unlike the interop dictionary's own <c>ContainsKey</c> it is trustworthy
    /// (interop trap 3).
    /// </summary>
    private static bool TryApplyToEmber(ICommandContext ctx, string id)
    {
        try
        {
            var gameData = ctx.World?.GetExistingSystemManaged<GameDataSystem>();
            var registry = gameData?.ManagedDataRegistry;
            if (registry == null) return false;

            var map = registry._DataLookupMap;
            if (map == null || map.Count == 0) return false;

            if (!registry.TryGet<ManagedItemData>(Plugin.EmberGuid, out var data) || data == null)
                return false;

            LocalizationApi.ApplyName(data, id);
            LocalizationApi.ApplyDescription(data, id + DescriptionSuffix);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample loc apply threw: " + e);
            return false;
        }
    }

    private static void DropApplied()
    {
        try { _appliedName?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogError("Sample loc name registration dispose threw: " + e); }
        _appliedName = null;

        try { _appliedDescription?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogError("Sample loc description registration dispose threw: " + e); }
        _appliedDescription = null;
    }
#endif

    /// <summary>
    /// Unload. The world-ready registration is tracked on the context, so Core disposes it on
    /// every reason and the string leaves the game table with the plugin. The pair
    /// <c>.loc apply</c> made is dropped here as well: they are demonstration strings with no
    /// persisted state, and leaving them would let one generation's text outlive its plugin.
    /// </summary>
    internal static void Detach()
    {
#if !PROGENITOR_TESTS
        DropApplied();
#endif
    }

    private static string Yn(bool value) => value ? "True" : "False";
}

#if !PROGENITOR_TESTS
/// <summary>
/// The argument-taking half of <c>.loc</c>, as a <c>[CommandGroup]</c> so it can be added from
/// this file without touching <see cref="SampleCommands"/>'s bare <c>.loc</c>. See
/// <see cref="SampleLocalization"/>'s remarks for why the dispatcher keeps the two apart.
/// </summary>
[CommandGroup("loc")]
internal static class SampleLocalizationCommands
{
    // adminOnly: `apply` registers a display string other players will read, and both take a
    // caller-supplied key. The bare .loc stays open because it only reports this plugin's own
    // two strings.
    [Command("key", adminOnly: true, usage: ".loc key <id>", description: "Admin only. KeyFor, IsRegistered and TryGetGameString for one localization key.")]
    public static void Key(ICommandContext ctx, string id = "")
    {
        try
        {
            if (!SampleLocalization.TryParseId(id, out string clean, out string error))
            {
                ctx.Reply(error);
                return;
            }

            SampleLocalization.HandleKey(ctx, clean);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .loc key threw: " + e);
            ctx.Reply("Sample loc key failed. See the server log.");
        }
    }

    [Command("apply", adminOnly: true, usage: ".loc apply <id> <text>", description: "Admin only. Register a name and a .desc string at runtime, then point the Sample item's ManagedItemData at them with ApplyName/ApplyDescription.")]
    public static void Apply(ICommandContext ctx, string id = "", [Remainder] string text = "")
    {
        try
        {
            if (!SampleLocalization.TryParseId(id, out string cleanId, out string idError))
            {
                ctx.Reply(idError);
                return;
            }

            if (!SampleLocalization.TryParseText(text, out string cleanText, out string textError))
            {
                ctx.Reply(textError);
                return;
            }

            SampleLocalization.HandleApply(ctx, cleanId, cleanText);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .loc apply threw: " + e);
            ctx.Reply("Sample loc apply failed. See the server log.");
        }
    }
}
#endif
