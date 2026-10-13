using System;

namespace Progenitor.Install;

/// <summary>Where a listing's side came from. Shown in the row readback as <c>sideSource</c>.</summary>
internal enum PackageSideSource
{
    /// <summary>No signal: no manifest side, no Server/Client tag, no side keyword in the one-liner.</summary>
    Unknown = 0,

    /// <summary><c>progenitor.json</c> <c>side</c>, read from the inspected zip. Authoritative when present.</summary>
    Manifest = 1,

    /// <summary>Thunderstore <c>categories</c> <c>Server</c> / <c>Client</c> tags (both → Both).</summary>
    Categories = 2,

    /// <summary>The one-line description says <c>server-only</c> / <c>client-only</c> (or <c>server side</c> / <c>client side</c>).</summary>
    Description = 3,
}

/// <summary>A derived side and the signal that produced it.</summary>
internal readonly struct DerivedPackageSide
{
    public DerivedPackageSide(PackageSide side, PackageSideSource source)
    {
        Side = side;
        Source = source;
    }

    public PackageSide Side { get; }
    public PackageSideSource Source { get; }

    /// <summary><c>manifest</c> | <c>categories</c> | <c>description</c> | <c>unknown</c>.</summary>
    public string SourceName => PackageSideDerivation.SourceName(Source);

    /// <summary><c>server</c> | <c>client</c> | <c>both</c> | <c>unknown</c>.</summary>
    public string SideName => PackageSideDerivation.SideName(Side);
}

/// <summary>
/// Side of a listing for the Install filter row. Precedence: the <c>progenitor.json</c> side
/// when known, else the Thunderstore <c>Server</c>/<c>Client</c> category tags, else a
/// <c>server-only</c>/<c>client-only</c> keyword in the one-line description, else Unknown.
/// Unknown never means Both (<see cref="PackageSide"/>).
/// </summary>
internal static class PackageSideDerivation
{
    public const string ServerCategory = "Server";
    public const string ClientCategory = "Client";

    public static DerivedPackageSide Derive(CatalogEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (entry.Side != PackageSide.Unknown)
            return new DerivedPackageSide(entry.Side, PackageSideSource.Manifest);

        bool server = HasCategory(entry, ServerCategory);
        bool client = HasCategory(entry, ClientCategory);
        if (server || client)
        {
            PackageSide tagged = server && client ? PackageSide.Both : server ? PackageSide.Server : PackageSide.Client;
            return new DerivedPackageSide(tagged, PackageSideSource.Categories);
        }

        PackageSide fromText = FromDescription(entry.Description);
        if (fromText != PackageSide.Unknown)
            return new DerivedPackageSide(fromText, PackageSideSource.Description);

        return new DerivedPackageSide(PackageSide.Unknown, PackageSideSource.Unknown);
    }

    public static bool HasCategory(CatalogEntry entry, string category)
    {
        if (entry == null || string.IsNullOrEmpty(category)) return false;
        var categories = entry.Categories;
        for (int i = 0; i < categories.Count; i++)
        {
            if (string.Equals(categories[i]?.Trim(), category, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Keyword read of the one-liner. <c>server-only</c> / <c>server only</c> / <c>server-side</c>
    /// / <c>serverside</c> mean Server; the client forms mean Client; both present means Both.
    /// A bare word "server" is not a signal (too many mods mention the server they talk to).
    /// </summary>
    public static PackageSide FromDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return PackageSide.Unknown;
        bool server = MentionsSide(description, "server");
        bool client = MentionsSide(description, "client");
        if (server && client) return PackageSide.Both;
        if (server) return PackageSide.Server;
        if (client) return PackageSide.Client;
        return PackageSide.Unknown;
    }

    private static bool MentionsSide(string text, string side)
    {
        return Contains(text, side + "-only")
            || Contains(text, side + " only")
            || Contains(text, side + "-side")
            || Contains(text, side + " side")
            || Contains(text, side + "side");
    }

    private static bool Contains(string text, string needle) =>
        text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    public static string SourceName(PackageSideSource source) => source switch
    {
        PackageSideSource.Manifest => "manifest",
        PackageSideSource.Categories => "categories",
        PackageSideSource.Description => "description",
        _ => "unknown",
    };

    public static string SideName(PackageSide side) => side switch
    {
        PackageSide.Server => "server",
        PackageSide.Client => "client",
        PackageSide.Both => "both",
        _ => "unknown",
    };
}
