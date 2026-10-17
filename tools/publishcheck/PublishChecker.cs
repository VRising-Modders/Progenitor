using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Progenitor.Install.Admission;
using Progenitor.Networking;
using Progenitor.Plugins;

namespace Progenitor.PublishCheck;

public enum CheckStatus { Pass, Warn, Fail }

/// <summary>One line of the report: a stable id (<c>manifest.bom</c>), the verdict and a sentence that says what to do.</summary>
internal sealed record CheckResult(string Id, CheckStatus Status, string Message);

internal sealed class PublishReport
{
    public string Package { get; init; } = "";
    public List<CheckResult> Results { get; } = new();

    public bool Failed => Results.Any(r => r.Status == CheckStatus.Fail);
    public int Count(CheckStatus status) => Results.Count(r => r.Status == status);

    public CheckResult? Find(string id) => Results.FirstOrDefault(r => r.Id == id);

    internal void Add(string id, CheckStatus status, string message) => Results.Add(new CheckResult(id, status, message));
    internal void Pass(string id, string message) => Add(id, CheckStatus.Pass, message);
    internal void Warn(string id, string message) => Add(id, CheckStatus.Warn, message);
    internal void Fail(string id, string message) => Add(id, CheckStatus.Fail, message);
}

/// <summary>
/// Checks a built mod package (a Thunderstore zip or the folder it is zipped from) against the publishing playbook
/// (docs/publishing.md). Reads files and metadata only; contacts nothing and loads no assembly.
/// </summary>
internal static class PublishChecker
{
    public const int MaxDescriptionChars = 250;
    public const string BepInExPackPrefix = "BepInEx-BepInExPack_V_Rising-";

    /// <summary>The Thunderstore team that publishes Progenitor (decided 2026-10-10).</summary>
    public const string ProgenitorNamespace = "VRising_Modders";
    public const string ProgenitorPackageName = "Progenitor";
    public const string ProgenitorPluginGuid = "progenitor";

    /// <summary>The side words a description starts with, by <c>progenitor.json</c> side.</summary>
    public static readonly IReadOnlyDictionary<string, string> SideLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["client"] = "Client-only",
        ["server"] = "Server-only",
        ["both"] = "Client and server",
    };

    public const string RequiresBlockMarker = "Requires Progenitor";
    public const string WorksBetterBlockMarker = "Works better with Progenitor";

    private static readonly Regex NameRule = new("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant);
    private static readonly Regex PascalRule = new("^[A-Z][A-Za-z0-9]*$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionRule = new(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex DependencyRule = new(@"^([A-Za-z0-9_]+)-([A-Za-z0-9_]+)-(\d+\.\d+\.\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex AvoidWords = new(@"\b(official|verified|approved)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly string[] FrameworkFiles = { "Progenitor.dll", "Progenitor.core", "Progenitor.Bootstrap.dll", "Progenitor.Preloader.dll", "Progenitor.Preloader.patcher" };

    public static PublishReport CheckPath(string path)
    {
        var report = new PublishReport { Package = path };
        Dictionary<string, byte[]> files;
        try
        {
            if (Directory.Exists(path)) files = ReadFolder(path);
            else if (File.Exists(path)) files = ReadZip(path);
            else
            {
                report.Fail("package.open", "Nothing at " + path + ". Pass the package zip or the folder you zip.");
                return report;
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            report.Fail("package.open", "Could not read " + path + ": " + e.Message);
            return report;
        }

        Check(files, report);
        return report;
    }

    /// <summary>Every check over the package's files, keyed by their zip path with forward slashes.</summary>
    public static void Check(IReadOnlyDictionary<string, byte[]> files, PublishReport report)
    {
        report.Pass("package.open", files.Count + " files read.");

        // ---- root files -------------------------------------------------------------------------
        foreach (var required in new[] { "manifest.json", "README.md", "icon.png" })
        {
            if (Root(files, required) == null) report.Fail("files." + required, required + " is missing at the package root; Thunderstore rejects the upload without it.");
        }
        foreach (var wanted in new[] { "CHANGELOG.md", "LICENSE" })
        {
            if (Root(files, wanted) == null && !(wanted == "LICENSE" && (Root(files, "LICENSE.md") != null || Root(files, "LICENSE.txt") != null)))
                report.Warn("files." + wanted, wanted + " is missing at the package root. Players and server operators read it on the listing.");
        }

        CheckForbiddenFiles(files, report);

        // ---- the plugin DLL ---------------------------------------------------------------------
        var dlls = files.Keys.Where(k => k.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
        var images = new List<(string Path, ImageFacts Facts, byte[] Bytes)>();
        foreach (var d in dlls)
        {
            var facts = ImageFacts.Read(files[d]);
            if (facts.IsManagedAssembly) images.Add((d, facts, files[d]));
        }

        // progenitor.json names the plugin; the main DLL is the one that declares it, wherever it is in the zip
        // (Progenitor's inspection picks the plugin image by that id the same way).
        var pj = ReadProgenitorJson(files, report);

        var plugins = images.Where(i => i.Facts.Plugins.Count > 0).ToList();
        (string Path, ImageFacts Facts, byte[] Bytes)? main = null;
        if (plugins.Count == 0)
            report.Fail("dll.plugin", dlls.Count == 0 ? "The package has no DLL." : "No DLL in the package declares a [BepInPlugin].");
        else
        {
            var named = pj == null || pj.Id.Length == 0
                ? new List<(string Path, ImageFacts Facts, byte[] Bytes)>()
                : plugins.Where(p => p.Facts.Plugins.Any(e => string.Equals(e.Guid, pj.Id, StringComparison.OrdinalIgnoreCase))).ToList();
            main = named.Count > 0 ? named[0] : plugins[0];
            var mainEntry = named.Count > 0
                ? main.Value.Facts.Plugins.First(e => string.Equals(e.Guid, pj!.Id, StringComparison.OrdinalIgnoreCase))
                : main.Value.Facts.Plugins[0];
            if (plugins.Count > 1)
                report.Warn("dll.plugin", "More than one DLL declares a plugin (" + string.Join(", ", plugins.Select(p => p.Path)) + "); checking " + main.Value.Path
                    + (named.Count > 0 ? ", the one progenitor.json names" : "") + ". Publish one mod per package; progenitor.json describes one plugin.");
            else
                report.Pass("dll.plugin", main.Value.Path + " declares " + Describe(mainEntry) + ".");
        }

        foreach (var d in dlls)
        {
            string folder = d.Contains('/') ? d.Substring(0, d.LastIndexOf('/')) : "";
            if (folder.Length == 0) continue;
            string lower = folder.ToLowerInvariant() + "/";
            if (lower.StartsWith("bepinex/plugins/") || lower.StartsWith("plugins/") || lower.StartsWith("bepinex/patchers/")
                || lower.StartsWith("patchers/") || lower.StartsWith("bepinex/core/") || lower.StartsWith("core/")
                || lower.StartsWith("bepinex/config/") || lower.StartsWith("config/"))
                report.Fail("dll.layout", d + " is under " + folder + "/. Put the DLL at the package root: Progenitor's Install tab refuses a package with progenitor.json whose DLL targets BepInEx/plugins, patchers, core or config.");
            else
                report.Warn("dll.layout", d + " is in a subfolder (" + folder + "/). Put the plugin DLL at the package root beside manifest.json.");
        }
        if (main != null && !report.Results.Any(r => r.Id == "dll.layout"))
            report.Pass("dll.layout", "The DLL is at the package root.");

        // ---- manifest.json ----------------------------------------------------------------------
        var manifestBytes = Root(files, "manifest.json");
        JsonElement? manifest = null;
        JsonDocument? manifestDoc = null;
        if (manifestBytes != null)
        {
            if (HasBom(manifestBytes))
                report.Fail("manifest.bom", "manifest.json starts with a UTF-8 byte-order mark; Thunderstore rejects it. Save it as UTF-8 without a BOM.");
            else
                report.Pass("manifest.bom", "manifest.json has no byte-order mark.");

            try
            {
                manifestDoc = JsonDocument.Parse(StripBom(manifestBytes));
                if (manifestDoc.RootElement.ValueKind == JsonValueKind.Object) manifest = manifestDoc.RootElement;
                else report.Fail("manifest.json", "manifest.json is not a JSON object.");
            }
            catch (JsonException e)
            {
                report.Fail("manifest.json", "manifest.json does not parse: " + e.Message);
            }
        }

        string? side = pj?.Side;
        var entry = main?.Facts.Plugins.FirstOrDefault(p => pj == null || string.Equals(p.Guid, pj.Id, StringComparison.OrdinalIgnoreCase))
            ?? main?.Facts.Plugins.FirstOrDefault();
        // Without progenitor.json the side is known only when the plugin declares [ProgenitorSide]; a plain BepInEx
        // plugin that soft-depends on Progenitor declares none, and its description is not compared.
        if (side == null && entry?.SideRaw != null) side = entry.SideText;

        bool referencesProgenitor = main?.Facts.ProgenitorReference != null;
        var progenitorDep = entry?.Dependencies.FirstOrDefault(d => string.Equals(d.Guid, ProgenitorPluginGuid, StringComparison.OrdinalIgnoreCase));
        // A declared soft dependency means the mod runs without Progenitor: no manifest dependency, no progenitor.json,
        // and the "Works better with Progenitor" README block. Otherwise a reference to Progenitor's assembly needs it.
        bool softProgenitor = progenitorDep is { Soft: true };
        bool requiresProgenitor = progenitorDep != null ? !progenitorDep.Soft : referencesProgenitor;

        string? manifestVersion = null;
        string? description = null;
        if (manifest is { } m)
        {
            var missing = new[] { "name", "version_number", "website_url", "description", "dependencies" }
                .Where(k => !m.TryGetProperty(k, out _)).ToList();
            if (missing.Count > 0) report.Fail("manifest.fields", "manifest.json is missing " + string.Join(", ", missing) + ".");
            else report.Pass("manifest.fields", "manifest.json has every required field.");

            string name = Str(m, "name");
            if (!NameRule.IsMatch(name)) report.Fail("manifest.name", "name '" + name + "' may use only letters, digits and underscores.");
            else if (!PascalRule.IsMatch(name)) report.Warn("manifest.name", "name '" + name + "' is not PascalCase (MyMod); Thunderstore shows underscores as spaces.");
            else report.Pass("manifest.name", "name '" + name + "' is PascalCase.");

            manifestVersion = Str(m, "version_number");
            if (!VersionRule.IsMatch(manifestVersion)) report.Fail("manifest.version", "version_number '" + manifestVersion + "' must be three numbers, major.minor.patch.");
            else report.Pass("manifest.version", "version_number " + manifestVersion + ".");

            string site = Str(m, "website_url");
            if (site.Length == 0) report.Warn("manifest.website", "website_url is empty. Point it at the mod's public source.");
            else if (!site.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !site.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                report.Fail("manifest.website", "website_url '" + site + "' is not an http(s) address.");
            else report.Pass("manifest.website", "website_url " + site + ".");

            description = Str(m, "description");
            CheckDescription(description, side, requiresProgenitor, report);

            CheckDependencies(m, main?.Facts, requiresProgenitor, report);
        }

        // ---- icon -------------------------------------------------------------------------------
        var icon = Root(files, "icon.png");
        if (icon != null)
        {
            if (!TryPngSize(icon, out int w, out int h)) report.Fail("icon", "icon.png is not a PNG.");
            else if (w != 256 || h != 256) report.Fail("icon", "icon.png is " + w + "x" + h + "; Thunderstore requires exactly 256x256.");
            else report.Pass("icon", "icon.png is a 256x256 PNG.");
        }

        // ---- README -----------------------------------------------------------------------------
        var readme = Root(files, "README.md");
        if (readme != null)
        {
            string text = Encoding.UTF8.GetString(StripBom(readme));
            bool requires = text.IndexOf(RequiresBlockMarker, StringComparison.OrdinalIgnoreCase) >= 0;
            bool better = text.IndexOf(WorksBetterBlockMarker, StringComparison.OrdinalIgnoreCase) >= 0;
            if (requiresProgenitor && !requires)
                report.Fail("readme.requirement", "README.md has no \"" + RequiresBlockMarker + "\" block. Paste the requirement block from docs/publishing.md.");
            else if (softProgenitor && !better && !requires)
                report.Fail("readme.requirement", "The plugin soft-depends on Progenitor but README.md has no \"" + WorksBetterBlockMarker + "\" block. Paste the second block from docs/publishing.md.");
            else if (!requiresProgenitor && !requires && !better)
                report.Warn("readme.requirement", "README.md has no Progenitor requirement block (\"" + RequiresBlockMarker + "\" or \"" + WorksBetterBlockMarker + "\").");
            else
                report.Pass("readme.requirement", "README.md carries the Progenitor requirement block.");

            foreach (var line in text.Split('\n'))
            {
                if (line.IndexOf("Progenitor", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var hit = AvoidWords.Match(line);
                if (!hit.Success) continue;
                report.Warn("readme.wording", "README.md says \"" + hit.Value + "\" next to Progenitor: \"" + line.Trim() + "\". Progenitor does not endorse mods; say \"built on\" or \"requires\".");
                break;
            }
        }

        // ---- progenitor.json against the DLL ------------------------------------------------------
        if (pj != null && main != null && entry != null)
            CheckProgenitorJson(pj, entry, main.Value.Path, main.Value.Bytes, images, manifestVersion, report);
        else if (pj == null && requiresProgenitor && Root(files, "progenitor.json") == null)
            report.Fail("pj.present", "progenitor.json is missing. Generate it from the DLL: tools/publish-check.ps1 -Dll <your.dll> -WriteProgenitorJson <package>/progenitor.json.");
        else if (pj == null && softProgenitor && Root(files, "progenitor.json") == null)
            report.Pass("pj.present", "No progenitor.json: the plugin only soft-depends on Progenitor, so Progenitor installs it as an ordinary BepInEx plugin.");

        // ---- the DLL scan -----------------------------------------------------------------------
        if (main != null && entry != null)
        {
            var f = main.Value.Facts;
            if (referencesProgenitor && progenitorDep == null)
                report.Fail("dll.bepindependency", entry.Name + " references Progenitor but has no [BepInDependency(\"progenitor\")]. Without it a mod manager install can load before Progenitor.");
            else if (progenitorDep != null)
                report.Pass("dll.bepindependency", "[BepInDependency(\"progenitor\")] is declared" + (progenitorDep.Soft ? " (soft)." : "."));

            // Key reads in the mod's own plugin DLL(s) fail; a bundled library the author does not control only warns.
            var ownReads = new SortedSet<string>(StringComparer.Ordinal);
            var libraryReads = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var img in images)
                foreach (var k in img.Facts.DirectKeyReads)
                    (img.Facts.Plugins.Count > 0 ? ownReads : libraryReads).Add(img.Path + ": " + k);
            if (ownReads.Count > 0)
                report.Fail("dll.keys", "Reads keys or devices directly (" + string.Join(", ", ownReads) + "). Register every key with ctx.Keybinds so players can remap it and conflicts are detected.");
            else
                report.Pass("dll.keys", "The plugin DLL reads no keys or devices directly.");
            if (libraryReads.Count > 0)
                report.Warn("dll.keys-libraries", "A bundled library reads keys or devices directly (" + string.Join(", ", libraryReads)
                    + "). If your mod acts on those keys, register them with ctx.Keybinds instead.");

            if (images.Any(i => i.Facts.BindsConfigFileDirectly))
                report.Warn("dll.config", "Calls BepInEx ConfigFile.Bind directly. Bind settings with ctx.Bind (synced: true where the server decides); a direct bind can never be synced.");
            else
                report.Pass("dll.config", "Settings are bound through Progenitor (no direct ConfigFile.Bind).");

            if (images.Any(i => i.Facts.CreatesConfigFile))
                report.Warn("dll.configfile", "Creates its own ConfigFile. The Mods menu shows only the plugin's own GUID.cfg; settings in another file are invisible there.");

            if (f.UsesProgenitorBind && !images.Any(i => i.Facts.RegistersSettingsSource))
                report.Warn("dll.settings-page", "Binds settings but never touches ctx.Settings, ctx.Menu or ctx.Keybinds, which register the cfg with the Mods menu. Add a settings page (ctx.Settings.Page) so the settings appear.");
        }

        manifestDoc?.Dispose();
    }

    // ---- pieces ---------------------------------------------------------------------------------

    internal sealed class ProgenitorJsonFacts
    {
        public string Id { get; init; } = "";
        public string Version { get; init; } = "";
        public string? Side { get; init; }
        public string Sha256 { get; init; } = "";
        public bool? HotLoadable { get; init; }
        public string Peers { get; init; } = "";

        /// <summary><c>compatibility</c> or <c>compatibility.peers</c> has the wrong JSON type; already reported.</summary>
        public bool PeersInvalid { get; init; }
    }

    private static ProgenitorJsonFacts? ReadProgenitorJson(IReadOnlyDictionary<string, byte[]> files, PublishReport report)
    {
        var bytes = Root(files, "progenitor.json");
        if (bytes == null)
        {
            if (files.Keys.Any(k => k.EndsWith("/progenitor.json", StringComparison.OrdinalIgnoreCase)))
                report.Fail("pj.present", "progenitor.json is not at the package root; Progenitor reads only the root copy.");
            return null;
        }

        report.Pass("pj.present", "progenitor.json is at the package root.");
        if (HasBom(bytes)) report.Warn("pj.bom", "progenitor.json starts with a byte-order mark. Write it as UTF-8 without a BOM.");

        try
        {
            using var doc = JsonDocument.Parse(StripBom(bytes), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                report.Fail("pj.fields", "progenitor.json is not a JSON object.");
                return null;
            }

            bool? hot = null;
            if (root.TryGetProperty("hotLoadable", out var h))
            {
                if (h.ValueKind == JsonValueKind.True) hot = true;
                else if (h.ValueKind == JsonValueKind.False) hot = false;
            }

            // The inspection's rule (PackageInspector.TryParseProgenitorJson): compatibility is absent, null or an object,
            // and its peers is absent, null or a string. Anything else refuses the package.
            string peers = "";
            bool peersInvalid = false;
            if (root.TryGetProperty("compatibility", out var c) && c.ValueKind != JsonValueKind.Null)
            {
                if (c.ValueKind != JsonValueKind.Object)
                {
                    peersInvalid = true;
                    report.Fail("pj.peers", "progenitor.json compatibility is " + c.ValueKind.ToString().ToLowerInvariant() + ", not an object; Progenitor refuses the package.");
                }
                else if (c.TryGetProperty("peers", out var p) && p.ValueKind != JsonValueKind.Null)
                {
                    if (p.ValueKind != JsonValueKind.String)
                    {
                        peersInvalid = true;
                        report.Fail("pj.peers", "progenitor.json compatibility.peers is " + p.ValueKind.ToString().ToLowerInvariant() + ", not a string; Progenitor refuses the package.");
                    }
                    else peers = (p.GetString() ?? "").Trim();
                }
            }

            var facts = new ProgenitorJsonFacts
            {
                Id = Str(root, "id"),
                Version = Str(root, "version"),
                Side = root.TryGetProperty("side", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null,
                Sha256 = Str(root, "sha256").ToLowerInvariant(),
                HotLoadable = hot,
                Peers = peers,
                PeersInvalid = peersInvalid,
            };

            var missing = new List<string>();
            if (facts.Id.Length == 0) missing.Add("id");
            if (facts.Version.Length == 0) missing.Add("version");
            if (facts.Side == null) missing.Add("side");
            if (facts.Sha256.Length == 0) missing.Add("sha256");
            if (facts.HotLoadable == null) missing.Add("hotLoadable");
            if (missing.Count > 0)
                report.Fail("pj.fields", "progenitor.json is missing " + string.Join(", ", missing) + ". Regenerate it with tools/publish-check.ps1 -WriteProgenitorJson.");
            else if (!SideLabels.ContainsKey(facts.Side!))
                report.Fail("pj.fields", "progenitor.json side '" + facts.Side + "' must be client, server or both.");
            else
                report.Pass("pj.fields", "progenitor.json has id, version, side, sha256 and hotLoadable.");
            return facts;
        }
        catch (JsonException e)
        {
            report.Fail("pj.fields", "progenitor.json does not parse: " + e.Message);
            return null;
        }
    }

    private static void CheckProgenitorJson(ProgenitorJsonFacts pj, PluginEntry entry, string dllPath, byte[] dll,
        IReadOnlyList<(string Path, ImageFacts Facts, byte[] Bytes)> images, string? manifestVersion, PublishReport report)
    {
        // The id is also the file name Progenitor writes the plugin under; the planner refuses one it cannot use.
        if (!string.Equals(pj.Id, entry.Guid, StringComparison.OrdinalIgnoreCase))
            report.Fail("pj.id", "progenitor.json id '" + pj.Id + "' is not the GUID of any [BepInPlugin] in the package (checked " + entry.Guid + " in " + dllPath + ").");
        else if (!ModManifestIds.TryPluginFileName(pj.Id, ".dll", out _, out var idError))
            report.Fail("pj.id", "id '" + pj.Id + "' cannot be installed by Progenitor: " + idError + ". Use letters, digits, '.', '_' and '-' in the [BepInPlugin] GUID.");
        else report.Pass("pj.id", "id matches the plugin GUID " + entry.Guid + ".");

        var versions = new List<string>();
        if (!string.Equals(pj.Version, entry.Version, StringComparison.Ordinal)) versions.Add("[BepInPlugin] says " + entry.Version);
        if (manifestVersion != null && !string.Equals(pj.Version, manifestVersion, StringComparison.Ordinal)) versions.Add("manifest.json says " + manifestVersion);
        if (versions.Count > 0) report.Fail("pj.version", "progenitor.json version " + pj.Version + " disagrees: " + string.Join("; ", versions) + ". One version everywhere.");
        else report.Pass("pj.version", "version " + pj.Version + " matches the DLL" + (manifestVersion != null ? " and manifest.json." : "."));

        if (pj.Side != null && !string.Equals(pj.Side, entry.SideText, StringComparison.OrdinalIgnoreCase))
            report.Fail("pj.side", "progenitor.json side '" + pj.Side + "' but the plugin's [ProgenitorSide] is '" + entry.SideText + "'" + (entry.SideRaw == null ? " (not declared: both)." : "."));
        else if (pj.Side != null) report.Pass("pj.side", "side " + pj.Side + " matches [ProgenitorSide].");

        // Progenitor accepts a sha256 that matches any DLL in the package (PackageInspector.HashMatchesInstallableBytes).
        string sha = Sha256(dll);
        var other = images.FirstOrDefault(i => !ReferenceEquals(i.Bytes, dll) && string.Equals(Sha256(i.Bytes), pj.Sha256, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(pj.Sha256, sha, StringComparison.OrdinalIgnoreCase))
            report.Pass("pj.sha256", "sha256 matches " + dllPath + ".");
        else if (other.Path != null)
            report.Warn("pj.sha256", "sha256 is " + other.Path + "'s, not the plugin DLL " + dllPath + "'s. Progenitor accepts it, but describe the plugin DLL.");
        else
            report.Fail("pj.sha256", "progenitor.json sha256 is not " + dllPath + "'s (" + sha + ") nor any other DLL's. Regenerate progenitor.json after every build.");

        if (!pj.PeersInvalid)
        {
            string imagePeers = entry.Peers;
            if (pj.Peers.Length > 0 && !CompatRange.TryParse(pj.Peers, out _, out var error))
                report.Fail("pj.peers", "compatibility.peers '" + pj.Peers + "' is not a valid range: " + error + ".");
            else if (!string.Equals(Squash(pj.Peers), Squash(imagePeers), StringComparison.OrdinalIgnoreCase))
                report.Fail("pj.peers", "compatibility.peers is '" + (pj.Peers.Length == 0 ? "not set" : pj.Peers) + "' but [NetworkCompatibility] Peers is '"
                    + (imagePeers.Length == 0 ? "not set" : imagePeers) + "'. Progenitor refuses a package whose two disagree.");
            else report.Pass("pj.peers", pj.Peers.Length == 0
                ? "No Peers declared; peers on another version are judged by " + (entry.EffectivePeers.Length == 0 ? "the declared rule" : "'" + entry.EffectivePeers + "'")
                    + (entry.RuleDeclared ? " (the declared NetworkVersionRule)." : " (the same minor version, since no rule is declared).")
                : "compatibility.peers matches the DLL: " + pj.Peers + ".");
        }

        // The Install tab's own image rule (HotImageCheck, shared with HotLoadTrust), so the two always agree.
        if (pj.HotLoadable == true)
        {
            if (HotImageCheck.IsClean(dll, out var why))
                report.Pass("pj.hotloadable", "hotLoadable, and the image passes the Install tab's hot-load checks: it can load without a restart.");
            else
                report.Fail("pj.hotloadable", "hotLoadable is true but the Install tab would not load this image hot: " + why + ". Remove the cause or set hotLoadable to false.");
        }
        else if (pj.HotLoadable == false) report.Pass("pj.hotloadable", "hotLoadable is false: every install waits for a game restart.");
    }

    private static void CheckDescription(string description, string? side, bool requiresProgenitor, PublishReport report)
    {
        if (description.Length > MaxDescriptionChars)
            report.Fail("manifest.description", "description is " + description.Length + " characters; Thunderstore allows " + MaxDescriptionChars + ".");
        else report.Pass("manifest.description", "description is " + description.Length + " characters.");

        string? label = SideLabels.Values.FirstOrDefault(l =>
            description.StartsWith(l + " ·", StringComparison.Ordinal) || description.StartsWith(l + ":", StringComparison.Ordinal));
        if (label == null)
            report.Fail("manifest.side", "description must start with the side: \"Client-only · \", \"Server-only · \" or \"Client and server · \".");
        else if (side != null && SideLabels.TryGetValue(side, out var expected) && expected != label)
            report.Fail("manifest.side", "description starts with \"" + label + "\" but the mod's side is " + side + " (\"" + expected + "\").");
        else report.Pass("manifest.side", "description starts with \"" + label + "\".");

        if (requiresProgenitor && description.IndexOf("Requires Progenitor", StringComparison.Ordinal) < 0)
            report.Warn("manifest.message", "description does not say \"Requires Progenitor.\" Use the standard form: \"Client-only · Requires Progenitor. <pitch>\".");

        var avoid = AvoidWords.Match(description);
        if (avoid.Success)
            report.Warn("manifest.wording", "description says \"" + avoid.Value + "\". Progenitor does not endorse mods; avoid official, verified and approved.");
    }

    private static void CheckDependencies(JsonElement manifest, ImageFacts? facts, bool requiresProgenitor, PublishReport report)
    {
        var deps = new List<string>();
        if (manifest.TryGetProperty("dependencies", out var d) && d.ValueKind == JsonValueKind.Array)
            foreach (var x in d.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) deps.Add(x.GetString() ?? "");

        var bad = deps.Where(x => !DependencyRule.IsMatch(x)).ToList();
        if (bad.Count > 0) report.Fail("deps.format", "Dependencies must read Team-Package-1.2.3: " + string.Join(", ", bad) + ".");

        if (deps.Any(x => x.StartsWith(BepInExPackPrefix, StringComparison.Ordinal)))
            report.Pass("deps.bepinex", "Depends on BepInExPack_V_Rising.");
        else
            report.Fail("deps.bepinex", "dependencies has no " + BepInExPackPrefix + "<version>.");

        var prog = deps.Select(x => DependencyRule.Match(x)).FirstOrDefault(x => x.Success && x.Groups[2].Value == ProgenitorPackageName);
        if (prog == null)
        {
            if (requiresProgenitor)
                report.Fail("deps.progenitor", "The DLL needs Progenitor but dependencies has no " + ProgenitorNamespace + "-" + ProgenitorPackageName + "-<version>.");
            return;
        }

        if (!string.Equals(prog.Groups[1].Value, ProgenitorNamespace, StringComparison.Ordinal))
            report.Warn("deps.progenitor", "Depends on " + prog.Value + "; Progenitor is published as " + ProgenitorNamespace + "-" + ProgenitorPackageName + ".");
        else
            report.Pass("deps.progenitor", "Depends on " + prog.Value + ".");

        var compiled = facts?.ProgenitorReference;
        if (compiled == null || !Version.TryParse(prog.Groups[3].Value, out var declared)) return;
        var core = new Version(compiled.Major, compiled.Minor, Math.Max(compiled.Build, 0));
        if (declared > core)
            report.Warn("deps.progenitor-version", "Depends on Progenitor " + declared + " but was compiled against Progenitor Core " + core
                + ". Depend on the oldest release that provides the APIs you use; a release whose Core did not change keeps the older Core version.");
        else if (declared < core)
            report.Warn("deps.progenitor-version", "Depends on Progenitor " + declared + " but was compiled against Progenitor Core " + core
                + ". Make sure every Progenitor API you call exists in " + declared + ", or raise the dependency.");
        else
            report.Pass("deps.progenitor-version", "The Progenitor dependency matches the Core it was compiled against (" + core + ").");
    }

    private static void CheckForbiddenFiles(IReadOnlyDictionary<string, byte[]> files, PublishReport report)
    {
        var dev = files.Keys.Where(k =>
        {
            string leaf = Leaf(k);
            return leaf.StartsWith("Progenitor.Dev", StringComparison.OrdinalIgnoreCase)
                || leaf.EndsWith(".token", StringComparison.OrdinalIgnoreCase)
                || string.Equals(leaf, "progenitor-dev.enable", StringComparison.OrdinalIgnoreCase);
        }).ToList();
        if (dev.Count > 0)
            report.Fail("files.dev", "The package carries Progenitor dev bridge files (" + string.Join(", ", dev) + "). The bridge is for development machines; never ship it or its token.");
        else
            report.Pass("files.dev", "No dev bridge files or tokens.");

        var framework = files.Keys.Where(k => FrameworkFiles.Any(f => string.Equals(Leaf(k), f, StringComparison.OrdinalIgnoreCase))).ToList();
        if (framework.Count > 0)
            report.Fail("files.framework", "The package bundles Progenitor itself (" + string.Join(", ", framework) + "). Depend on the Progenitor package instead.");
    }

    // ---- helpers --------------------------------------------------------------------------------

    internal static Dictionary<string, byte[]> ReadZip(string path)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(path);
        foreach (var e in zip.Entries)
        {
            string name = (e.FullName ?? "").Replace('\\', '/').TrimStart('/');
            if (name.Length == 0 || name.EndsWith("/", StringComparison.Ordinal)) continue;
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            files[name] = ms.ToArray();
        }

        return files;
    }

    internal static Dictionary<string, byte[]> ReadFolder(string path)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        string root = Path.GetFullPath(path);
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            files[Path.GetRelativePath(root, f).Replace('\\', '/')] = File.ReadAllBytes(f);
        return files;
    }

    private static byte[]? Root(IReadOnlyDictionary<string, byte[]> files, string name)
    {
        foreach (var kv in files)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    private static string Leaf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path.Substring(slash + 1);
    }

    internal static bool HasBom(byte[] bytes) => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static byte[] StripBom(byte[] bytes) => HasBom(bytes) ? bytes.AsSpan(3).ToArray() : bytes;

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "") : "";

    private static string Squash(string? text)
    {
        var sb = new StringBuilder();
        foreach (char c in text ?? "") if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    internal static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>The PNG signature and the IHDR width and height.</summary>
    internal static bool TryPngSize(byte[] png, out int width, out int height)
    {
        width = height = 0;
        byte[] sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (png.Length < 24) return false;
        for (int i = 0; i < 8; i++) if (png[i] != sig[i]) return false;
        if (png[12] != (byte)'I' || png[13] != (byte)'H' || png[14] != (byte)'D' || png[15] != (byte)'R') return false;
        width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return true;
    }

    private static string Describe(PluginEntry p) =>
        p.Name + " " + p.Version + " (" + p.Guid + ", side " + p.SideText + ")";
}
