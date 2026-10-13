using System;
using System.Runtime.CompilerServices;
using Progenitor.Commands;
using Progenitor.Networking;
using Progenitor.Plugins;

namespace Progenitor.Sample;

/// <summary>
/// Sample <c>.mods</c> command: the signed catalog report and the operator pin. Chat commands
/// run on the server; the trust decision must originate on the client. <c>.mods</c> forwards a
/// report packet; <c>.mods trust</c> on the server replies "run this on your client". (The
/// Sample's self-offer and the <c>.consentdemo</c> command went with server-to-client mod
/// distribution, 2026-10-05: mods come only from Thunderstore.)
/// </summary>
internal static class SampleMods
{
    /// <summary>Custom event id ('SmMd'). Distinctive so a collision with a game hash is silent.</summary>
    internal const int EventId = 0x536D4D64;

    internal const int OpReport = 1;

    /// <summary>Exact server reply when <c>.mods trust</c> is typed in chat (commands are server-only).</summary>
    internal const string RunOnClientReply =
        "Run .mods trust on your client. The server cannot pin an operator for you.";

    /// <summary>Exact client log when a local <c>.mods trust</c> has no pending catalog.</summary>
    internal const string NoCatalogLog = "Sample: .mods trust: no verified catalog this join";

    /// <summary>Usage line, replied on an action this command does not know.</summary>
    internal const string UsageReply = "Usage: .mods | .mods about | .mods trust | .mods trust always";

    /// <summary>
    /// Job J21. Reply for <c>.mods about</c>: <c>ctx.Name</c> and <c>ctx.Version</c>, which
    /// nothing in the repository read, beside the <c>ctx.Guid</c> that everything reads.
    ///
    /// <para>They matter here rather than anywhere else because this is the mod-manifest command:
    /// the three are the BepInEx metadata <c>ctx.Handshake.DeclareSelf</c> puts on the wire, so
    /// what an operator sees in a handshake row or a deny message is exactly this line. The GUID
    /// is the identity — it names this plugin's <c>GUID.cfg</c>, its reload folder and its
    /// handshake row — while the name and the version are what a <em>person</em> reads; a plugin
    /// should never key anything off the name.</para>
    /// </summary>
    internal static string AboutReply(string? name, string? version, string? guid, bool isServer) =>
        "ctx.Name=\"" + Blank(name) + "\" ctx.Version=\"" + Blank(version)
        + "\" ctx.Guid=\"" + Blank(guid) + "\" side=" + (isServer ? "server" : "client")
        + ". All three come from this plugin's [BepInPlugin] and are what ctx.Handshake.DeclareSelf "
        + "puts on the wire; the GUID is the identity (cfg file, reload folder, handshake row) and "
        + "the name and version are for people to read - never key logic off the name.";

    private static string Blank(string? value) => string.IsNullOrEmpty(value) ? "(unset)" : value!;

#if !PROGENITOR_TESTS
    internal struct Packet
    {
        public int Value;
    }
#endif

#if PROGENITOR_TESTS
    internal static bool TestForceClient;
    internal static string? TestLastReply;
#endif

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Handle(ICommandContext ctx, string action, string extra)
    {
        try
        {
            action = (action ?? "").Trim();
            extra = (extra ?? "").Trim();
            bool trust = string.Equals(action, "trust", StringComparison.OrdinalIgnoreCase);
            bool always = trust && string.Equals(extra, "always", StringComparison.OrdinalIgnoreCase);
            bool about = string.Equals(action, "about", StringComparison.OrdinalIgnoreCase);
            if (!trust && !about && action.Length > 0)
            {
                Reply(ctx, UsageReply);
                return;
            }

            if (about)
            {
                // Job J21. Runs on either side: the metadata is the plugin's own, not the world's.
                Reply(ctx, About());
                return;
            }

            if (trust)
            {
                if (IsClientCommand())
                {
                    TryTrustLocal(always);
                    return;
                }
                Reply(ctx, RunOnClientReply);
                LogInfo("Sample: .mods trust refused (server-origin; run on the client)");
                return;
            }

#if !PROGENITOR_TESTS
            var plugin = Plugin.Active;
            if (plugin == null)
            {
                Reply(ctx, "Sample plugin context is not available.");
                return;
            }

            var packet = new Packet { Value = OpReport };
            bool sent = plugin.Net.SendToUser(ctx.UserEntity, EventId, packet);
            Reply(ctx, sent
                ? "Asked your client to print the pending catalog (see the client log)."
                : "Could not reach your client.");
            LogInfo("Sample: .mods report sent=" + sent);
#else
            Reply(ctx, "Sample mods");
#endif
        }
        catch (Exception e)
        {
            LogError("Sample .mods threw: " + e);
            Reply(ctx, "Sample mods failed. See the server log.");
        }
    }

    internal static bool TryTrustLocal(bool always)
    {
        var pending = ModManifest.Pending;
        if (pending == null || !pending.SignatureValid)
        {
            LogWarning(NoCatalogLog);
            return false;
        }
        bool ok = ModManifest.TrustOperator(pending.Fingerprint, always, TrustOrigin.LocalUser);
        LogInfo("Sample: .mods trust fp=" + pending.Fingerprint
            + " always=" + always + " ok=" + ok);
        return ok;
    }

#if PROGENITOR_TESTS
    /// <summary>
    /// Historic server-sent trust opcodes (2 = once, 3 = always). A receive handler must
    /// not call <see cref="ModManifest.TrustOperator"/> for these.
    /// </summary>
    internal static void ApplyPacketForTests(int op)
    {
        if (op == OpReport)
        {
            var report = ModManifest.Pending;
            if (report == null)
            {
                LogInfo("Sample: .mods: no catalog received this join");
                return;
            }
            LogInfo(report.FormatReport());
            return;
        }
        LogWarning("Sample: .mods packet ignored (op=" + op + "; trust is client-origin only)");
    }
#endif

    /// <summary>
    /// Job J21. Read <c>ctx.Name</c> / <c>ctx.Version</c> / <c>ctx.Guid</c> off the live context.
    /// In the test build there is no context, so the reply is built from the same
    /// <see cref="AboutReply"/> with the metadata constants the plugin declares.
    /// </summary>
    private static string About()
    {
#if PROGENITOR_TESTS
        return AboutReply("Progenitor Sample", "0.0.0", "progenitor.sample", isServer: true);
#else
        var ctx = Plugin.Active;
        return ctx == null
            ? "Sample plugin context is not available."
            : AboutReply(ctx.Name, ctx.Version, ctx.Guid, ctx.IsServer);
#endif
    }

    private static bool IsClientCommand()
    {
#if PROGENITOR_TESTS
        return TestForceClient;
#else
        return Plugin.Active?.IsClient == true;
#endif
    }

    private static void Reply(ICommandContext ctx, string text)
    {
#if PROGENITOR_TESTS
        TestLastReply = text;
#endif
        ctx.Reply(text);
    }

    private static void LogInfo(string line)
    {
#if PROGENITOR_TESTS
        global::Progenitor.Plugin.Logger?.LogInfo(line);
#else
        Plugin.Logger?.LogInfo(line);
#endif
    }

    private static void LogWarning(string line)
    {
#if PROGENITOR_TESTS
        global::Progenitor.Plugin.Logger?.LogWarning(line);
#else
        Plugin.Logger?.LogWarning(line);
#endif
    }

    private static void LogError(string line)
    {
#if PROGENITOR_TESTS
        global::Progenitor.Plugin.Logger?.LogError(line);
#else
        Plugin.Logger?.LogError(line);
#endif
    }

#if !PROGENITOR_TESTS
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "CA2255:The 'ModuleInitializer' attribute is only intended to be used in application code",
        Justification = "Plugin.cs is not this job's to edit; the first Frame after OnLoad attaches through PluginContext.")]
    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            Progenitor.Plugin.Frame += KickAttach;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("SampleMods.Init: " + e);
        }
    }

    private static void KickAttach()
    {
        var ctx = Plugin.Active;
        if (ctx == null) return;
        Progenitor.Plugin.Frame -= KickAttach;
        Attach(ctx);
    }

    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        try
        {
            if (!ctx.IsServer) BindReceive(ctx);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("SampleMods.Attach: " + e);
        }
    }

    private static unsafe void BindReceive(PluginContext ctx) =>
        ctx.Handshake.Track(ctx.Net.OnReceive(EventId, OnPacket));

    private static unsafe void OnPacket(int eventId, byte* payload, int length)
    {
        try
        {
            if (eventId != EventId) return;
            if (!CustomEvents.TryRead<Packet>(payload, length, out var packet)) return;
            if (packet.Value != OpReport)
            {
                Plugin.Logger?.LogWarning("Sample: .mods packet ignored (op=" + packet.Value
                    + "; trust is client-origin only)");
                return;
            }

            var report = ModManifest.Pending;
            if (report == null)
            {
                Plugin.Logger?.LogInfo("Sample: .mods: no catalog received this join");
                return;
            }
            Plugin.Logger?.LogInfo(report.FormatReport());
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("SampleMods.OnPacket: " + e);
        }
    }
#endif
}
