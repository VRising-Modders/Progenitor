using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Networking;
using Progenitor.Plugins;

#if !PROGENITOR_TESTS
using System.Reflection;
using Progenitor.Commands;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Job J19 (RD6). The handshake facade's unexampled half: <c>ctx.Handshake.RequireSelf()</c> /
/// <c>ProvideSelf()</c> (two public members that were not even in <c>docs/api.md</c>), the
/// explicit <c>Require</c> / <c>Provide</c> overloads that carry a
/// <see cref="NetworkCompatibilityLevel"/> and a <see cref="NetworkVersionRule"/>, and
/// <c>ctx.Handshake.Track(IDisposable)</c>. <c>.hs</c> prints what this plugin declared, by which
/// call, and how many handshake request slots that costs.
///
/// <b>The constraint this file was written under, and it is the interesting part.</b> The Sample
/// already declares itself: <c>Plugin.cs</c> carries
/// <c>[NetworkCompatibility(ServerMustHave, Exact)]</c> and calls <c>ctx.Handshake.DeclareSelf()</c>,
/// which is what kicks a peer that does not have this plugin at this version. An example must not
/// change that — the user's own client joins the test server through it — so <b>every row this
/// file adds is strictly weaker than the row that is already there</b>, and cannot create a kick
/// condition that did not exist:
/// <list type="bullet">
/// <item><description><see cref="ProtocolGuid"/> is <see cref="NetworkCompatibilityLevel.NoNeedForSync"/>:
/// either side may lack it. Both sides ship in the same DLL, so both always have it; the version
/// rule is <see cref="NetworkVersionRule.Minor"/>, which is weaker than the <c>Exact</c> the
/// plugin's own row already enforces on the same DLL. Nothing can fail here that the plugin's own
/// row has not already failed.</description></item>
/// <item><description><see cref="UiGuid"/> is <see cref="NetworkCompatibilityLevel.ClientOnly"/>:
/// never compared in either direction, and — the reason it is here — a server-side ClientOnly row
/// <b>does not consume a request slot</b> and is not sent on the request at all
/// (<c>Handshake.SlotCount</c>). That is the one piece of slot arithmetic a mod author has to know
/// before they hit <see cref="Handshake.MaxMods"/>, and <c>.hs</c> prints it.</description></item>
/// </list>
///
/// <b>How <see cref="Attach"/> reaches <c>RequireSelf</c> / <c>ProvideSelf</c> without changing
/// anything.</b> Those two record the plugin's own guid as an <em>undeclared</em> exact-match row,
/// which is the pre-attribute rule and is <b>weaker</b> than the declared row <c>DeclareSelf</c>
/// writes (an undeclared row is not evaluated in the reverse direction). Calling them after
/// <c>DeclareSelf</c> would therefore be a real downgrade of the Sample's compatibility. So they
/// are called <b>before</b> it: <c>Plugin.cs</c> runs <c>SampleHandshake.Attach(ctx)</c> on the
/// line above <c>ctx.Handshake.DeclareSelf()</c>, the self helper records the undeclared row, and
/// <c>DeclareSelf</c> then overwrites that same guid with the declared one
/// (<c>Handshake.TryRecord</c>: "updating an existing guid is allowed"). The table Core ends up
/// with is identical to the one it had before this file existed — and the API is genuinely
/// exercised rather than only described. <c>SampleHandshakeTests</c> pins that ordering in
/// <c>Plugin.cs</c>, because it is the whole safety argument and a later edit that moves one line
/// would silently downgrade the Sample instead of failing.
///
/// <b>What <c>.hs</c> can and cannot report.</b> Core does not publish its required/provided
/// tables — <c>Handshake.Required</c> and <c>Handshake.Provided</c> are private, deliberately, so
/// nothing builds policy on another mod's rows — so this command reports <em>this plugin's own</em>
/// declarations from a ledger this file keeps as it makes the calls, and says so in the reply
/// rather than implying it can see the whole table. The slot count is therefore a floor: at least
/// this many of <see cref="Handshake.MaxMods"/>.
///
/// <b>Live-check split.</b> The ledger, the slot arithmetic and every reply line are game-free and
/// unit tested. Only a real join proves the other half: that the added rows change nothing — a
/// client with this plugin still joins, and <c>Handshake.LastClientResult</c> is still <c>ok</c>.
/// </summary>
internal static class SampleHandshake
{
    /// <summary>Log/grep area: <c>Sample handshake: …</c>.</summary>
    internal const string Area = "handshake";

    /// <summary>
    /// Guid of the Sample's own wire protocol row. Not a second mod: a mod that changes its packet
    /// layout without changing its version can bump <em>this</em> and let the handshake say so.
    /// </summary>
    internal const string ProtocolGuid = "progenitor.sample.protocol";

    /// <summary>Version of the protocol row. Bump when a <c>Sm??</c> packet layout changes.</summary>
    internal const string ProtocolVersion = "1.0.0";

    /// <summary>Guid of the client-only half (the Sample's panels and keybinds).</summary>
    internal const string UiGuid = "progenitor.sample.ui";

    /// <summary>Version of the client-only row. Never compared: the level says so.</summary>
    internal const string UiVersion = "1.0.0";

    /// <summary>Which call recorded a row. The point of the ledger: <em>how</em>, not only what.</summary>
    internal enum Route
    {
        /// <summary>Server: <c>ctx.Handshake.RequireSelf()</c> — this plugin's guid, undeclared.</summary>
        RequireSelf = 0,

        /// <summary>Client: <c>ctx.Handshake.ProvideSelf()</c> — this plugin's guid, undeclared.</summary>
        ProvideSelf = 1,

        /// <summary>Server: <c>ctx.Handshake.Require(guid, version, level, rule)</c>.</summary>
        RequireDeclared = 2,

        /// <summary>Client: <c>ctx.Handshake.Provide(guid, version, level, rule)</c>.</summary>
        ProvideDeclared = 3,

        /// <summary>
        /// <c>ctx.Handshake.DeclareSelf()</c>, from <c>Plugin.cs</c>. Reads
        /// <c>[NetworkCompatibility]</c> and turns into a declared Require or Provide.
        /// </summary>
        DeclareSelf = 4,
    }

    /// <summary>
    /// One row this plugin recorded. Value data only, so the ledger holds nothing live and the
    /// whole thing is testable without a game.
    /// </summary>
    internal readonly struct Declaration
    {
        /// <param name="guid">The handshake guid.</param>
        /// <param name="version">The version string that goes on the wire.</param>
        /// <param name="route">Which call recorded it.</param>
        /// <param name="level">The level the row carries (the in-memory default for an undeclared row).</param>
        /// <param name="rule">The version rule the row carries.</param>
        /// <param name="declared">
        /// Whether level and rule actually travel. An undeclared row (<see cref="Route.RequireSelf"/> /
        /// <see cref="Route.ProvideSelf"/> and the two-argument overloads) is exact-match in one
        /// direction only; the level shown for it is what Core defaults to in memory.
        /// </param>
        internal Declaration(
            string guid,
            string version,
            Route route,
            NetworkCompatibilityLevel level,
            NetworkVersionRule rule,
            bool declared)
        {
            Guid = guid ?? "";
            Version = version ?? "";
            Route = route;
            Level = level;
            Rule = rule;
            Declared = declared;
        }

        /// <summary>The handshake guid.</summary>
        internal string Guid { get; }

        /// <summary>The version string.</summary>
        internal string Version { get; }

        /// <summary>Which call recorded it.</summary>
        internal Route Route { get; }

        /// <summary>Presence rule.</summary>
        internal NetworkCompatibilityLevel Level { get; }

        /// <summary>Version rule.</summary>
        internal NetworkVersionRule Rule { get; }

        /// <summary>Whether level and rule travel on the wire.</summary>
        internal bool Declared { get; }

        /// <summary>
        /// Whether this row costs one of <see cref="Handshake.MaxMods"/>. Only
        /// <see cref="NetworkCompatibilityLevel.ClientOnly"/> is free
        /// (<c>Handshake.SlotCount</c> skips exactly that level, on both tables).
        /// </summary>
        internal bool ConsumesSlot => Level != NetworkCompatibilityLevel.ClientOnly;
    }

    private static readonly object Gate = new();
    private static readonly List<Declaration> Rows = new(4);
    private static bool _server;

    /// <summary>Everything this plugin declared, in the order it declared it.</summary>
    internal static IReadOnlyList<Declaration> Declarations
    {
        get { lock (Gate) return Rows.ToArray(); }
    }

    /// <summary>Record a row. Called as each handshake call is made, never before it.</summary>
    internal static void Note(Declaration row)
    {
        lock (Gate) Rows.Add(row);
    }

    /// <summary>Forget the ledger. <see cref="Detach"/> and the tracked disposable both land here.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            Rows.Clear();
            _server = false;
        }
    }

    /// <summary>Which side the ledger was filled on, for the reply header.</summary>
    internal static bool IsServerSide
    {
        get { lock (Gate) return _server; }
    }

    /// <summary>
    /// The rows a given side declares, in the order <see cref="Attach"/> makes the calls, with
    /// <see cref="Route.DeclareSelf"/> last because <c>Plugin.cs</c> calls it on the next line.
    /// Game-free and shared with the live path, so the tests drive the same list the server does.
    /// </summary>
    /// <param name="server">True for the server side.</param>
    /// <param name="selfGuid">The plugin's own BepInEx guid.</param>
    /// <param name="selfVersion">The plugin's own version.</param>
    /// <param name="attributeLevel">The level on <c>[NetworkCompatibility]</c>, or null when absent.</param>
    /// <param name="attributeRule">The rule on <c>[NetworkCompatibility]</c>, or null when absent.</param>
    internal static IReadOnlyList<Declaration> Plan(
        bool server,
        string selfGuid,
        string selfVersion,
        NetworkCompatibilityLevel? attributeLevel,
        NetworkVersionRule? attributeRule)
    {
        var rows = new List<Declaration>(4)
        {
            // 1. The self helper. Undeclared, and therefore weaker than what DeclareSelf writes a
            //    moment later over the same guid - which is why it is safe to run first and only
            //    first. See the type docstring.
            new(
                selfGuid,
                selfVersion,
                server ? Route.RequireSelf : Route.ProvideSelf,
                NetworkCompatibilityLevel.ServerMustHave,
                NetworkVersionRule.Exact,
                declared: false),

            // 2. The explicit overload: a second row for this mod's wire protocol, at a level that
            //    cannot kick anybody who is not already kicked by the plugin's own row.
            new(
                ProtocolGuid,
                ProtocolVersion,
                server ? Route.RequireDeclared : Route.ProvideDeclared,
                NetworkCompatibilityLevel.NoNeedForSync,
                NetworkVersionRule.Minor,
                declared: true),

            // 3. The free row: ClientOnly costs no request slot and is never compared.
            new(
                UiGuid,
                UiVersion,
                server ? Route.RequireDeclared : Route.ProvideDeclared,
                NetworkCompatibilityLevel.ClientOnly,
                NetworkVersionRule.None,
                declared: true),

            // 4. Not made here: Plugin.cs's next line. Recorded so `.hs` shows the whole picture
            //    and shows what the attribute turned into.
            new(
                selfGuid,
                selfVersion,
                Route.DeclareSelf,
                attributeLevel ?? NetworkCompatibilityLevel.ServerMustHave,
                attributeRule ?? NetworkVersionRule.Exact,
                declared: attributeLevel.HasValue),
        };

        return rows;
    }

    /// <summary>
    /// Request slots the rows cost, which is <b>not</b> the row count. Core's table is keyed by
    /// guid, so two rows on one guid are one slot, and a
    /// <see cref="NetworkCompatibilityLevel.ClientOnly"/> row is free. The level that counts for a
    /// repeated guid is the last one written, because that is the row that survives in the table.
    /// </summary>
    internal static int SlotsUsed(IReadOnlyList<Declaration>? rows)
    {
        if (rows == null || rows.Count == 0) return 0;

        var last = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rows.Count; i++)
            last[rows[i].Guid] = rows[i].ConsumesSlot;

        int slots = 0;
        foreach (var consumes in last.Values)
        {
            if (consumes) slots++;
        }

        return slots;
    }

    /// <summary>The exact call a route stands for, so the reply teaches the API and not a label.</summary>
    internal static string CallOf(Route route, bool declared) => route switch
    {
        Route.RequireSelf => "ctx.Handshake.RequireSelf()",
        Route.ProvideSelf => "ctx.Handshake.ProvideSelf()",
        Route.RequireDeclared => "ctx.Handshake.Require(guid, version, level, rule)",
        Route.ProvideDeclared => "ctx.Handshake.Provide(guid, version, level, rule)",
        _ => declared
            ? "ctx.Handshake.DeclareSelf() -> the declared overload, from [NetworkCompatibility]"
            : "ctx.Handshake.DeclareSelf() -> the self helper, no [NetworkCompatibility]",
    };

    /// <summary>One row of the <c>.hs</c> reply.</summary>
    internal static string RowLine(int ordinal, in Declaration row) =>
        N(ordinal) + ". " + row.Guid + " " + row.Version
        + " via " + CallOf(row.Route, row.Declared)
        + " level=" + row.Level + " rule=" + row.Rule
        + (row.Declared ? " (declared: evaluated in both directions)" : " (undeclared: one direction, exact match)")
        + " slot=" + (row.ConsumesSlot ? "1" : "0 (ClientOnly rows are free)");

    /// <summary>The header, naming the side because Require and Provide are the same list seen twice.</summary>
    internal static string HeaderLine(int count, bool server) =>
        "This plugin declared " + N(count) + " handshake row(s) on the "
        + (server ? "server" : "client") + " side ("
        + (server ? "Require" : "Provide") + " direction):";

    /// <summary>The slot line, and the sentence that stops a reader trusting it as a total.</summary>
    internal static string SlotLine(int slots) =>
        "Slots: these rows cost " + N(slots) + " of Handshake.MaxMods=" + N(Handshake.MaxMods)
        + " request slot(s) - keyed by guid, so two calls on one guid cost one, and a ClientOnly "
        + "row costs none. Core keeps its required/provided tables private, so this is a floor: "
        + "other plugins' rows are not visible here.";

    /// <summary>
    /// The line about the ordering, printed every time because it is the one thing a reader
    /// copying this file must not get wrong.
    /// </summary>
    internal const string OrderNote =
        "RequireSelf/ProvideSelf run BEFORE Plugin.cs's DeclareSelf() on purpose: they write an "
        + "undeclared row for this plugin's own guid and DeclareSelf then overwrites it with the "
        + "declared one, so the live table is unchanged. Calling them after it would silently "
        + "downgrade this mod's compatibility to one-direction exact match.";

    /// <summary>The whole <c>.hs</c> reply. Game-free: the command handler only forwards these.</summary>
    internal static IReadOnlyList<string> Lines(IReadOnlyList<Declaration>? rows, bool server)
    {
        int count = rows == null ? 0 : rows.Count;
        var lines = new List<string>(count + 3);
        if (count == 0)
        {
            lines.Add("This plugin has declared no handshake rows. Either it is not attached, or "
                + "the side that declares them is the other one.");
            return lines;
        }

        lines.Add(HeaderLine(count, server));
        for (int i = 0; i < count; i++)
            lines.Add("  " + RowLine(i + 1, rows![i]));
        lines.Add(SlotLine(SlotsUsed(rows)));
        lines.Add(OrderNote);
        return lines;
    }

    /// <summary>The one Attach line, for the log.</summary>
    internal static string AttachedLine(int count, int slots, bool server) =>
        "declared " + N(count) + " row(s) on the " + (server ? "server" : "client")
        + " side costing " + N(slots) + " of " + N(Handshake.MaxMods)
        + " slot(s); every one is weaker than this plugin's own ServerMustHave/Exact row, so no "
        + "new join can be refused because of them. `.hs` prints them.";

#if !PROGENITOR_TESTS
    /// <summary>
    /// Runs on both sides, and <b>must</b> run before <c>Plugin.cs</c>'s
    /// <c>ctx.Handshake.DeclareSelf()</c>. See the type docstring for why; a test pins the order.
    /// </summary>
    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        Reset();

        try
        {
            var attr = ctx.Owner.GetType().GetCustomAttribute<NetworkCompatibilityAttribute>();
            var plan = Plan(
                ctx.IsServer,
                ctx.Guid,
                ctx.Version,
                attr?.Level,
                attr?.VersionRule);

            lock (Gate) _server = ctx.IsServer;

            for (int i = 0; i < plan.Count; i++)
            {
                var row = plan[i];
                switch (row.Route)
                {
                    // The self helpers. Undeclared, and overwritten by DeclareSelf on the next
                    // line of Plugin.cs - which is the only reason it is safe to call them at all.
                    case Route.RequireSelf:
                        ctx.Handshake.RequireSelf();
                        break;
                    case Route.ProvideSelf:
                        ctx.Handshake.ProvideSelf();
                        break;

                    // The explicit overloads. Both sides declare the same two guids at the same
                    // level so the reverse-direction evaluation has something to agree with.
                    case Route.RequireDeclared:
                        ctx.Handshake.Require(row.Guid, row.Version, row.Level, row.Rule);
                        break;
                    case Route.ProvideDeclared:
                        ctx.Handshake.Provide(row.Guid, row.Version, row.Level, row.Rule);
                        break;

                    // Not ours to call: Plugin.cs does it. Recorded only.
                    default:
                        break;
                }

                Note(row);
            }

            // ctx.Handshake.Track: the escape hatch for anything the helpers above do not wrap.
            // Core disposes it on unload, so the ledger cannot survive into the next generation
            // and claim rows that were unrequired with it.
            ctx.Handshake.Track(new SampleDisposable(Reset));

            if (ctx.IsServer)
                ctx.Commands.Register(typeof(SampleHandshakeCommands));

            SampleLog.Info(Area, AttachedLine(plan.Count, SlotsUsed(plan), ctx.IsServer));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample handshake attach threw: " + e);
        }
    }

    /// <summary>Server entry point for <c>.hs</c>.</summary>
    internal static void HandleCommand(ICommandContext ctx)
    {
        var lines = Lines(Declarations, IsServerSide);
        for (int i = 0; i < lines.Count; i++)
            ctx.Reply(lines[i]);
    }
#endif

    /// <summary>
    /// Unload. Every <c>Require</c> / <c>Provide</c> above is undone by Core (each helper tracks
    /// an <c>Unrequire</c> / <c>Unprovide</c> on the context), so all this has to drop is the
    /// ledger — which the tracked disposable also does, whichever runs first.
    /// </summary>
    internal static void Detach() => Reset();

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A disposable over one action, idempotent, for <c>ctx.Handshake.Track</c>.</summary>
    private sealed class SampleDisposable : IDisposable
    {
        private Action? _action;

        internal SampleDisposable(Action action) => _action = action;

        public void Dispose()
        {
            var action = _action;
            _action = null;
            action?.Invoke();
        }
    }
}

#if !PROGENITOR_TESTS
/// <summary>
/// <c>.hs</c>. Registered from <see cref="SampleHandshake.Attach"/> with
/// <c>ctx.Commands.Register(Type)</c>, server side only.
/// </summary>
internal static class SampleHandshakeCommands
{
    // adminOnly: this prints which mods this server insists on and at what version, which is
    // reconnaissance for anybody deciding what to spoof. It changes nothing, but the Sample's
    // gating rule is that the attribute decides for the whole command and a read that names the
    // server's requirements is not a read every player needs.
    [Command("hs", adminOnly: true, usage: ".hs", description: "Admin only. Print the handshake rows this plugin declared, which call declared each, and what they cost in request slots.")]
    public static void Hs(ICommandContext ctx)
    {
        try
        {
            SampleHandshake.HandleCommand(ctx);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .hs threw: " + e);
            ctx.Reply("Sample handshake failed. See the server log.");
        }
    }
}
#endif
