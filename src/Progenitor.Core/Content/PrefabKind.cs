namespace Progenitor.Content;

/// <summary>
/// What a cloned prefab is. The definition packet carries this so the client applicator
/// does not require <c>ItemData</c> of a buff (or <c>Buff</c> of an item). Item clones
/// append <c>ItemKeyLookup</c>; buff clones skip that table (inventory slots never
/// index a buff GUID, and a one-sided append would desync join-time count+hash).
/// </summary>
public enum PrefabKind
{
    /// <summary>Cloned item. Requires a source with <c>ItemData</c>; syncs the key table.</summary>
    Item = 0,

    /// <summary>
    /// Cloned buff. Requires a source with <c>Buff</c>; collection maps only, no
    /// <c>ItemKeyLookup</c> / <c>ItemHashLookupMap</c>. The HUD reads
    /// <c>ShowOnBuffBar</c> + client <c>ShowOnBuffBarData</c> copied from the source.
    /// </summary>
    Buff = 1,
}
