#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>
/// SERVERDESC1: the Core half of the dev bridge's <c>server_desc</c> op. Dev reaches this type by reflection
/// (Core grants Dev no <c>InternalsVisibleTo</c>); it is not a plugin API and no network path reaches it. Main thread.
///
/// <list type="bullet">
/// <item><c>Read</c> (both sides): the rows as this side holds them, the text composed from them now and the
/// counter line; on the server also the game's live <c>Description</c>, what was last published to Steam (the
/// <c>desc{i}</c> chunks as written; Steam offers no read-back of its own key-values), the settings layers with
/// each file's <c>Description</c>, the write target, the start-command override and the last apply report.</item>
/// <item><c>Set</c> (server): write the rows on the server, exactly as an accepted administrator edit does
/// (ConfigSync then sends them to clients), and run the apply step at once. A client's edit goes through the
/// existing <c>setting request</c> op, the row's own Apply path.</item>
/// <item><c>Apply</c> (server): run the apply step now, as an edit would (publishes only when the text differs).</item>
/// </list>
/// </summary>
internal static class ServerListDescriptionTestBridge
{
    public static Dictionary<string, object?> Read()
    {
        var v = ServerListDescriptionSettings.CurrentValues();
        string typed = ModSettingsView.TypedText(ServerListDescriptionSettings.RowId(ServerListDescriptionSettings.DescriptionKey)) ?? "";
        var now = DateTime.UtcNow;
        string rendered = ServerListDescription.Render(v.Raw, v.Reset, now, v.Zone);
        var composition = ServerListDescription.Compose(rendered, v.Signature);
        var d = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["side"] = Plugin.IsServerProcess ? "server" : "client",
            ["rows"] = new Dictionary<string, object?>
            {
                ["description"] = ServerListDescriptionSettings.RowId(ServerListDescriptionSettings.DescriptionKey),
                ["signature"] = ServerListDescriptionSettings.RowId(ServerListDescriptionSettings.SignatureKey),
                ["status"] = ServerListDescriptionSettings.RowId(ServerListDescriptionSettings.StatusKey),
            },
            ["raw"] = v.Raw,
            ["signature"] = v.Signature,
            ["typed"] = typed,
            ["rendered"] = rendered,
            ["composed"] = composition.Text,
            ["composedLength"] = composition.Text.Length,
            ["cut"] = composition.Cut,
            ["room"] = composition.Room,
            ["counter"] = ServerListDescription.CounterLine(rendered, v.Signature),
            ["zone"] = v.Zone.Id,
            ["resetSet"] = v.Reset != null && v.Reset.NextUtc.Length > 0,
            ["hasTokens"] = ServerListDescription.HasTokens(v.Raw),
            ["status"] = ReadStatus(),
        };
        if (!Plugin.IsServerProcess) return d;

        var game = new ServerListGame();
        var applier = ServerListDescriptionSettings.Applier;
        d["settingsReady"] = Safe(() => game.SettingsReady);
        string? live;
        try { live = game.LiveDescription; }
        catch { live = null; }
        d["live"] = live;
        d["liveMatchesComposed"] = string.Equals(live, composition.Text, StringComparison.Ordinal);
        d["joinChat"] = JoinDescriptionChatHook.Readback();
        var tags = ServerListDescriptionSettings.TagPublisher;
        d["browserTag"] = new Dictionary<string, object?>
        {
            ["tag"] = ServerBrowserMarker.Tag,
            ["enabled"] = ServerListDescriptionSettings.BrowserTagOn(),
            ["status"] = tags.Status,
            ["fitsSteam"] = tags.FitsSteam,
            ["tags"] = Safe(() => new ServerTagGame().Tags),
            ["note"] = "the game's own tag list (ServerBootstrapSystem._GameTags); the game republishes it to Steam (game tags) and EOS (TAGS) within a second of a change",
        };
        d["listing"] = Safe(() => game.Listing is { } l ? new Dictionary<string, object?> { ["steam"] = l.Steam, ["eos"] = l.Eos } : null);
        string published = applier.Publisher.Published ?? "";
        var chunks = new List<object?>();
        for (int i = 0; i < applier.Publisher.SteamChunks; i++)
        {
            int start = i * ServerListDescription.SteamChunkChars;
            string chunk = start < published.Length
                ? published.Substring(start, Math.Min(ServerListDescription.SteamChunkChars, published.Length - start))
                : "";
            chunks.Add(new Dictionary<string, object?> { ["key"] = "desc" + i.ToString(CultureInfo.InvariantCulture), ["value"] = chunk });
        }

        d["steam"] = new Dictionary<string, object?>
        {
            ["ready"] = Safe(() => game.SteamReady),
            ["pending"] = applier.SteamPending,
            ["published"] = applier.Publisher.Published,
            ["keys"] = chunks,
            ["note"] = "the desc{i} values as Progenitor last wrote them; Steam's own copy is only readable from outside (an A2S_RULES query on the query port)",
        };

        try
        {
            var layers = game.Resolve();
            var list = new List<object?>();
            foreach (var layer in layers.Layers)
            {
                string? fileDescription = null;
                if (layer.Exists && layer.TryGetString("Description", out string fd)) fileDescription = fd;
                list.Add(new Dictionary<string, object?>
                {
                    ["kind"] = layer.Kind.ToString().ToLowerInvariant(),
                    ["path"] = layer.Path,
                    ["exists"] = layer.Exists,
                    ["readable"] = layer.Readable,
                    ["error"] = layer.Error,
                    ["description"] = fileDescription,
                    ["listOnSteam"] = layer.TryGetBool("ListOnSteam", out bool ls) ? ls : null,
                    ["listOnEos"] = layer.TryGetBool("ListOnEOS", out bool le) ? le : null,
                    ["lanMode"] = layer.TryGetBool("LanMode", out bool lm) ? lm : null,
                });
            }

            var target = layers.WriteTarget;
            var found = layers.OverrideOf("Description");
            d["files"] = new Dictionary<string, object?>
            {
                ["layers"] = list,
                ["writeTarget"] = target?.Path,
                ["writeLayer"] = target == null ? null : target.Kind == ServerHostSettingsLayerKind.Save ? "save" : "local",
                ["persistentDataPath"] = layers.PersistentDataPath,
                ["saveName"] = layers.SaveName,
                ["saveFolder"] = layers.SaveFolder,
                ["effectiveDescription"] = layers.EffectiveString("Description"),
                ["override"] = found == null ? null : new Dictionary<string, object?> { ["source"] = found.Value.Source, ["value"] = found.Value.Value },
                ["overrideWarning"] = ServerListDescription.OverrideWarning(found),
            };
        }
        catch (Exception e)
        {
            d["files"] = new Dictionary<string, object?> { ["error"] = e.Message };
        }

        d["last"] = applier.Last?.ToDictionary();
        return d;
    }

    /// <summary>Write the rows on the server (null leaves one as it is) and apply at once.</summary>
    public static Dictionary<string, object?> Set(string? text, string? signature)
    {
        if (!Plugin.IsServerProcess)
            return Error("set runs on the server; from a client use the setting op's request (the row's own Apply path)");
        if (!ServerListDescriptionSettings.TryWrite(text, signature, out string error)) return Error(error);
        return ApplyNow("bridge set");
    }

    /// <summary>Run the apply step now (publishes only when the composed text differs from what is published or live).</summary>
    public static Dictionary<string, object?> Apply()
    {
        if (!Plugin.IsServerProcess) return Error("apply runs on the server");
        return ApplyNow("bridge apply");
    }

    private static Dictionary<string, object?> ApplyNow(string reason)
    {
        ServerListDescriptionSettings.Applier.MarkDirty(reason);
        var report = ServerListDescriptionSettings.TickNow();
        var d = Read();
        d["applied"] = report != null;
        d["report"] = report?.ToDictionary();
        if (report == null) d["why"] = "nothing to publish: the composed text equals what is published and live (or the game's settings are not loaded yet)";
        return d;
    }

    private static string ReadStatus()
    {
        try { return ServerListDescriptionSettings.StatusText(); }
        catch { return ""; }
    }

    private static object? Safe<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception e) { return "error: " + e.Message; }
    }

    private static Dictionary<string, object?> Error(string message) => new() { ["ok"] = false, ["error"] = message };
}
#endif
