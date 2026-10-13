namespace Progenitor.Install;

/// <summary>Author-declared package side from <c>progenitor.json</c>. Missing means unknown, never Both.</summary>
/// <remarks>Its own file so game-free tools (tools/publishcheck) can link the image preflight without the catalog types.</remarks>
internal enum PackageSide
{
    Unknown = 0,
    Client = 1,
    Server = 2,
    Both = 3,
}
