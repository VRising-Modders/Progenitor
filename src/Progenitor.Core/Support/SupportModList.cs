using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Progenitor.Install;
using Progenitor.Plugins;

namespace Progenitor.Support;

/// <summary>
/// One installed mod as <c>mods.md</c> lists it. The live half fills the first block on the main thread; the
/// rest is read from disk on the worker thread by <see cref="SupportModList.Resolve"/>.
/// </summary>
internal sealed class SupportMod
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";

    /// <summary>The DLL it was loaded from. Read for the manifest beside it and the help page; never written to the zip.</summary>
    public string Location { get; set; } = "";

    /// <summary>A <c>ProgenitorPlugin&lt;T&gt;</c> or a plugin that depends on Progenitor.</summary>
    public bool OnProgenitor { get; set; }

    /// <summary>Only the joined server's join list names it: id and version only.</summary>
    public bool ServerOnly { get; set; }

    /// <summary>The joined server's join list also names it.</summary>
    public bool AlsoOnServer { get; set; }

    /// <summary>What the plugin's assembly says about its help (resource names only), read on the main thread.</summary>
    public ModHelpFacts? HelpFacts { get; set; }

    /// <summary>The help path <c>ctx.Menu.Help</c> declared, or empty.</summary>
    public string DeclaredHelp { get; set; } = "";

    // ---- Resolved from disk.

    /// <summary>The Thunderstore team (the package's owner), or empty.</summary>
    public string Author { get; set; } = "";

    /// <summary><c>Owner-Name</c>, or empty.</summary>
    public string Package { get; set; } = "";

    public string StoreUrl { get; set; } = "";
    public string WebsiteUrl { get; set; } = "";

    /// <summary>The help page and where it is (<c>help/index.html (in the mod's DLL)</c>), or empty.</summary>
    public string Help { get; set; } = "";

    /// <summary><c>BepInEx/plugins</c> or <c>BepInEx/progenitor-reload</c>, or empty.</summary>
    public string Folder { get; set; } = "";
}

/// <summary>
/// SUPPORT1: <c>mods.md</c> in the log zip. Every installed mod with what an AI assistant (or a helper) needs to find
/// its docs: name, id, version, author, whether it is built on Progenitor, its Thunderstore page, its website or
/// repository and its help page. Game-free; written through the bundle's redactor like every other entry.
/// </summary>
internal static class SupportModList
{
    internal const string EntryName = "mods.md";

    internal const string ProgenitorId = "progenitor";
    internal const string ProgenitorRepository = "https://github.com/VRising-Modders/Progenitor";
    internal const string ProgenitorStore = "https://thunderstore.io/c/v-rising/p/VRising_Modders/Progenitor/";
    internal const string ProgenitorLlms = ProgenitorRepository + "/blob/main/llms.txt";

    internal const int MaxUrlLength = 300;

    /// <summary>The template's placeholder <c>website_url</c>; a mod that kept it has no real link.</summary>
    internal const string TemplatePlaceholderUrl = "https://github.com/YourTeam/";

    /// <summary>
    /// Fill each mod's package, author, links, help page and folder from disk: this side's install ledger
    /// (<c>progenitor-install.json</c>) first, then a package <c>manifest.json</c> beside the DLL. Never throws.
    /// </summary>
    internal static void Resolve(IList<SupportMod> mods, string? bepInExRoot)
    {
        if (mods == null || mods.Count == 0) return;
        string bep = (bepInExRoot ?? "").Trim();
        string? gameRoot = null;
        IReadOnlyList<ServerInstallLedgerEntry> ledger = Array.Empty<ServerInstallLedgerEntry>();
        if (bep.Length > 0)
        {
            try
            {
                gameRoot = ServerInstall.GameRootOf(bep);
                if (!string.IsNullOrEmpty(gameRoot)) ledger = ServerInstallLedger.TryRead(gameRoot!)?.Files ?? ledger;
            }
            catch { /* no ledger */ }
        }
        string cache = bep.Length > 0 ? Path.Combine(bep, "progenitor-cache") : "";

        foreach (var mod in mods)
        {
            if (mod == null || mod.ServerOnly) continue;
            try { ResolveOne(mod, bep, gameRoot, ledger, cache); }
            catch { /* the list is evidence; one unreadable mod keeps what it has */ }
        }
    }

    private static void ResolveOne(SupportMod mod, string bep, string? gameRoot, IReadOnlyList<ServerInstallLedgerEntry> ledger, string cache)
    {
        if (string.Equals(mod.Id, ProgenitorId, StringComparison.OrdinalIgnoreCase))
        {
            // Its BepInEx name is "Progenitor Bootstrap"; players know the framework as Progenitor.
            mod.Name = "Progenitor";
            mod.OnProgenitor = true;
            mod.Author = "VRising_Modders";
            mod.Package = "VRising_Modders-Progenitor";
            mod.StoreUrl = ProgenitorStore;
            mod.WebsiteUrl = ProgenitorRepository;
            mod.Help = ModHelp.ProgenitorGuidePath + " (Mods > Progenitor > Help)";
            return;
        }

        string location = (mod.Location ?? "").Trim();
        mod.Folder = FolderOf(bep, location);

        string package = PackageFromLedger(mod, location, gameRoot, ledger);
        var beside = location.Length > 0 ? PackageManifestBeside.TryRead(location) : null;
        if (package.Length == 0 && beside != null) package = beside.Package;
        if (package.Length > 0 && SplitPackage(package, out string owner, out string name))
        {
            mod.Package = owner + "-" + name;
            if (mod.Author.Length == 0) mod.Author = owner;
            if (mod.StoreUrl.Length == 0) mod.StoreUrl = StoreUrlOf(owner, name);
        }
        if (beside != null && mod.WebsiteUrl.Length == 0) mod.WebsiteUrl = SafeUrl(beside.WebsiteUrl);
        if (string.Equals(mod.WebsiteUrl, mod.StoreUrl, StringComparison.OrdinalIgnoreCase)) mod.WebsiteUrl = "";

        if (mod.Help.Length == 0)
        {
            var target = ModHelp.Resolve(mod.Id, location.Length > 0 ? location : null, mod.HelpFacts, mod.DeclaredHelp, cache.Length > 0 ? cache : null);
            if (target.Found)
                mod.Help = target.Relative + (target.Extract != null ? " (in the mod's DLL; its Help button opens it)" : " (in the mod's folder; its Help button opens it)");
        }
    }

    /// <summary>The ledger row's package for this mod: by plugin id, else by the file it names.</summary>
    private static string PackageFromLedger(SupportMod mod, string location, string? gameRoot, IReadOnlyList<ServerInstallLedgerEntry> ledger)
    {
        string full = "";
        if (location.Length > 0)
        {
            try { full = Path.GetFullPath(location); }
            catch { full = ""; }
        }
        foreach (var row in ledger)
        {
            if (row == null) continue;
            bool mine = row.PluginId.Length > 0 && string.Equals(row.PluginId, mod.Id, StringComparison.OrdinalIgnoreCase);
            if (!mine && full.Length > 0 && !string.IsNullOrEmpty(gameRoot))
            {
                try { mine = string.Equals(Path.GetFullPath(ServerInstallLedger.Absolute(gameRoot!, row.Path)), full, StringComparison.OrdinalIgnoreCase); }
                catch { mine = false; }
            }
            if (mine && InstalledState.TrySplitPackageSource(row.Source, out string package, out _)) return package;
        }
        return "";
    }

    /// <summary><c>Owner-Name</c> into its halves. Thunderstore names hold letters, digits and underscores only.</summary>
    internal static bool SplitPackage(string? package, out string owner, out string name)
    {
        owner = name = "";
        string p = (package ?? "").Trim();
        int dash = p.IndexOf('-');
        if (dash <= 0 || dash == p.Length - 1) return false;
        string o = p.Substring(0, dash), n = p.Substring(dash + 1);
        if (!IsPackageWord(o) || !IsPackageWord(n)) return false;
        owner = o;
        name = n;
        return true;
    }

    private static bool IsPackageWord(string s)
    {
        if (s.Length == 0 || s.Length > 128) return false;
        foreach (char c in s)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) return false;
        return true;
    }

    internal static string StoreUrlOf(string owner, string name) =>
        "https://thunderstore.io/c/v-rising/p/" + owner + "/" + name + "/";

    /// <summary>
    /// An <c>http(s)</c> link as it may be shared: no user name or password, no query string, at most
    /// <see cref="MaxUrlLength"/> characters, not the template's placeholder. Anything else is empty.
    /// </summary>
    internal static string SafeUrl(string? url)
    {
        string u = (url ?? "").Trim();
        if (u.Length == 0 || u.Length > MaxUrlLength) return "";
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri)) return "";
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return "";
        if (uri.UserInfo.Length > 0) return "";
        foreach (char c in u)
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return "";
        if (u.StartsWith(TemplatePlaceholderUrl, StringComparison.OrdinalIgnoreCase)) return "";
        int cut = u.IndexOfAny(new[] { '?', '#' });
        return cut >= 0 ? u.Substring(0, cut) : u;
    }

    /// <summary>The BepInEx folder the DLL sits in, relative (<c>BepInEx/plugins</c>), or empty.</summary>
    internal static string FolderOf(string bep, string location)
    {
        if (bep.Length == 0 || location.Length == 0) return "";
        try
        {
            string root = Path.GetFullPath(bep).TrimEnd('\\', '/');
            string full = Path.GetFullPath(location);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "";
            string rel = full.Substring(root.Length + 1).Replace('\\', '/');
            int slash = rel.IndexOf('/');
            return slash > 0 ? "BepInEx/" + rel.Substring(0, slash) : "";
        }
        catch { return ""; }
    }

    /// <summary>The words the redactor must never treat as player names: the mods' names, ids, authors and packages.</summary>
    internal static List<string> Words(IEnumerable<SupportMod>? mods)
    {
        var words = new List<string>();
        if (mods == null) return words;
        foreach (var m in mods)
        {
            if (m == null) continue;
            foreach (var w in new[] { m.Id, m.Name, m.Author, m.Package })
                if (!string.IsNullOrWhiteSpace(w)) words.Add(w);
        }
        return words;
    }

    /// <summary><c>mods.md</c>: a short note for whoever reads it, then one section per mod, then the server-only ones.</summary>
    internal static string Format(string side, IReadOnlyList<SupportMod>? mods)
    {
        var list = new List<SupportMod>();
        var serverOnly = new List<SupportMod>();
        if (mods != null)
            foreach (var m in mods)
                if (m != null && m.Id.Trim().Length > 0) (m.ServerOnly ? serverOnly : list).Add(m);
        Comparison<SupportMod> order = (a, b) =>
        {
            bool pa = string.Equals(a.Id, ProgenitorId, StringComparison.OrdinalIgnoreCase);
            bool pb = string.Equals(b.Id, ProgenitorId, StringComparison.OrdinalIgnoreCase);
            if (pa != pb) return pa ? -1 : 1;
            return string.Compare(Title(a), Title(b), StringComparison.OrdinalIgnoreCase);
        };
        list.Sort(order);
        serverOnly.Sort(order);

        var sb = new StringBuilder();
        sb.AppendLine("# Installed mods (" + (string.IsNullOrWhiteSpace(side) ? "client" : side.Trim()) + ")");
        sb.AppendLine();
        sb.AppendLine("For a question about one mod, read its page, repository or help page below first. Mods built on Progenitor");
        sb.AppendLine("also follow Progenitor's docs: " + ProgenitorLlms);
        sb.AppendLine();
        if (list.Count == 0) sb.AppendLine("(no mods read)").AppendLine();
        foreach (var m in list)
        {
            sb.AppendLine("## " + Title(m));
            sb.AppendLine("- Id: " + m.Id.Trim());
            sb.AppendLine("- Version: " + (m.Version.Trim().Length > 0 ? m.Version.Trim() : "unknown"));
            if (m.Author.Length > 0) sb.AppendLine("- Author: " + m.Author);
            sb.AppendLine("- Built on Progenitor: " + (string.Equals(m.Id, ProgenitorId, StringComparison.OrdinalIgnoreCase) ? "it is Progenitor" : m.OnProgenitor ? "yes" : "no"));
            if (m.AlsoOnServer) sb.AppendLine("- On the joined server too: yes");
            if (m.StoreUrl.Length > 0) sb.AppendLine("- Thunderstore: " + m.StoreUrl);
            if (m.WebsiteUrl.Length > 0) sb.AppendLine("- Website or repository: " + m.WebsiteUrl);
            if (m.Help.Length > 0) sb.AppendLine("- Help page: " + m.Help);
            if (m.Folder.Length > 0) sb.AppendLine("- Folder: " + m.Folder);
            sb.AppendLine();
        }

        if (serverOnly.Count > 0)
        {
            sb.AppendLine("## On the joined server only");
            sb.AppendLine("From the server's join list: id and version only.");
            foreach (var m in serverOnly)
                sb.AppendLine("- " + m.Id.Trim() + " " + (m.Version.Trim().Length > 0 ? m.Version.Trim() : "version unknown"));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string Title(SupportMod m) => m.Name.Trim().Length > 0 ? m.Name.Trim() : m.Id.Trim();
}
