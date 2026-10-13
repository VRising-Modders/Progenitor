using System;
using System.Collections.Generic;
using Stunlock.Core;

namespace Progenitor.Content;

/// <summary>
/// A cloned item or buff prefab the registry knows about. The GUID is
/// <see cref="PrefabGUID.CalculateGuidHash"/> of <see cref="Id"/>, so both sides compute the same
/// value without a handshake. <see cref="LocalizedName"/> and <see cref="LocalizedDescription"/>
/// are sent to the client and, when set on an <see cref="PrefabKind.Item"/>, written onto the
/// cloned <c>ManagedItemData</c> via <c>Progenitor.Localization</c> (name key is <see cref="Id"/>;
/// description key is <c>id.desc</c>). Buff clones write the same strings onto client
/// <c>ShowOnBuffBarData.Entries[i]</c> (name key is <c>id.name</c>, description key is
/// <c>id.desc</c>) and clear <c>UseParentNameAndDescription</c>. <see cref="IconPath"/> is a
/// plugin-relative PNG under the probed <c>assets</c> folder; the client writes it onto every
/// <c>ShowOnBuffBarData.Entries[i].Icon</c> (buffs) or cloned <c>ManagedItemData.Icon</c>
/// (items). It does not travel on <c>PrfD</c>; the same plugin registers it on the client.
/// </summary>
public sealed class PrefabDefinition
{
    private readonly List<IDisposable> _undo = new();

    internal PrefabDefinition(
        string id,
        PrefabGUID guid,
        PrefabGUID source,
        string? sourceName,
        int maxAmount,
        string? localizedName,
        string? localizedDescription,
        object owner,
        PrefabKind kind = PrefabKind.Item,
        string? iconPath = null,
        bool refreshLiveInstances = false)
    {
        Id = id;
        Guid = guid;
        Source = source;
        SourceName = sourceName;
        MaxAmount = maxAmount;
        LocalizedName = localizedName;
        LocalizedDescription = localizedDescription;
        Owner = owner;
        Kind = kind;
        IconPath = iconPath;
        RefreshLiveInstances = refreshLiveInstances;
    }

    /// <summary>
    /// Worn graphics registered with <c>DefineItem(...).Wearable</c>. Null for an ordinary
    /// clone. Not a wire field: the same plugin registers it on the client, which is where
    /// the mesh is built. See <c>docs/wearables.md</c>.
    /// </summary>
    public WearableGraphics? Wearable { get; private set; }

    /// <summary>Local opt-in clone baseline, registered by the same item plugin on each side.</summary>
    internal ItemArmorBaseline? ArmorBaseline { get; private set; }

    internal void AssignArmorBaseline(ItemArmorBaseline baseline) => ArmorBaseline = baseline;

    /// <summary>Explicit additive equipment bonuses; null leaves the donor unchanged.</summary>
    public ItemStatBonuses? StatBonuses { get; private set; }
    internal void AssignStatBonuses(ItemStatBonuses bonuses) => StatBonuses = bonuses;

    /// <summary>
    /// Local opt-in keep rules (<c>BloodBound</c> / <c>Indestructible</c>), registered by the same
    /// item plugin on each side. Not a wire field. Null keeps the donor's rules.
    /// </summary>
    internal ItemKeepPolicy? Keep { get; private set; }

    internal void AssignKeep(ItemKeepPolicy keep) => Keep = keep;

    internal void AssignWearable(WearableGraphics graphics) => Wearable = graphics;

    /// <summary>Spawnable name and hash input. Also the wire id, ASCII, at most 63 characters.</summary>
    public string Id { get; }

    /// <summary>Deterministic GUID derived from <see cref="Id"/>.</summary>
    public PrefabGUID Guid { get; }

    /// <summary>Vanilla (or previously registered) prefab this one clones. Empty until a name resolves.</summary>
    public PrefabGUID Source { get; internal set; }

    /// <summary>Spawnable name passed to <c>CloneOf(string)</c>, if the source was named rather than hashed.</summary>
    public string? SourceName { get; }

    /// <summary>Overridden stack size written onto the clone's <c>ItemData.MaxAmount</c>.</summary>
    public int MaxAmount { get; internal set; }

    /// <summary>
    /// Client display name. Empty or null keeps the source prefab's managed name (items) or
    /// HUD Header (buffs). When set, the client registers it (items: <see cref="Id"/>; buffs:
    /// <c>id.name</c>) and points the cloned managed data / <c>ShowOnBuffBarData.Entry.Header</c>
    /// at that key.
    /// </summary>
    public string? LocalizedName { get; internal set; }

    /// <summary>
    /// Client tooltip / description. Empty or null keeps the source prefab's managed description
    /// unless Localization already has <c>id.desc</c> (a plugin's <c>en.json</c>). When set, the
    /// client registers it under <c>id.desc</c> and points
    /// <c>ManagedItemData.Description.Key</c> (items) or
    /// <c>ShowOnBuffBarData.Entry.Description.Key</c> (buffs) at that key.
    /// </summary>
    public string? LocalizedDescription { get; internal set; }

    /// <summary>Plugin (or other object) that called <see cref="PrefabRegistry.ItemDefinitionBuilder.Register"/> or <see cref="PrefabRegistry.BuffDefinitionBuilder.Register"/>.</summary>
    public object Owner { get; internal set; }

    /// <summary>
    /// Item vs buff apply path. Set by <c>DefineItem</c> / <c>DefineBuff</c> and carried on
    /// <c>PrfD</c> so a client that never called the builder still takes the matching path.
    /// </summary>
    public PrefabKind Kind { get; internal set; }

    /// <summary>
    /// PNG path relative to the plugin's <c>assets</c> folder (for example
    /// <c>icons/nodrain.png</c>). Null or empty keeps the source clone's
    /// buff HUD or inventory sprite. Validated by <c>Icon()</c>: relative only, no
    /// <c>..</c>. Not a wire field — the client reads its own builder.
    /// </summary>
    public string? IconPath { get; internal set; }

    /// <summary>
    /// Opt-in: after this buff clone is re-applied (plugin reload with a changed
    /// definition, same-id retarget, or an edit pass that wrote the clone), the
    /// registry destroys live instances through <c>TryRemoveBuff</c> and re-applies
    /// through <c>ApplyBuff</c> so the new clone actually reaches characters.
    /// Default off — a plugin that stacks its own state on a live buff must not
    /// have Core destroy it. Never on first process start (save-restored instances
    /// of an unchanged definition are left alone). Not a wire field.
    /// </summary>
    public bool RefreshLiveInstances { get; internal set; }

    /// <summary>
    /// Core-internal: a version token for a prefab pass a Core module runs on this clone outside the
    /// registry's editors (the debug buff's <c>AfterApply</c> pass). It is part of the live-refresh
    /// fingerprint, so changing that pass between Core builds refreshes live instances the way a
    /// changed editor version does. Empty for plugin definitions. Not a wire field.
    /// </summary>
    internal string PassVersion { get; set; } = "";

    /// <summary>
    /// Server values a <c>PrfD</c> filled into this (local) definition during the current join,
    /// and the local values to put back when the connection ends. Not a wire field.
    /// </summary>
    internal PrefabServerFill ServerFill { get; } = new();

    /// <summary>
    /// Localization owner for server-supplied text (the registry's server-sync owner), set with
    /// the first fill of a join. Text the server filled is registered under this owner, not
    /// <see cref="Owner"/>, so connection end drops it with every other server string.
    /// </summary>
    internal object? ServerTextOwner { get; private set; }

    /// <summary>Owner the name (<paramref name="name"/> true) or description is registered under.</summary>
    internal object LocalizationOwner(bool name)
    {
        bool fromServer = name ? ServerFill.NameFromServer : ServerFill.DescriptionFromServer;
        return fromServer && ServerTextOwner != null ? ServerTextOwner : Owner;
    }

    /// <summary>
    /// Fill what this definition left empty from a server <c>PrfD</c> (see
    /// <see cref="PrefabServerFill.Apply"/>). The first fill of a join remembers the local values.
    /// </summary>
    internal void FillFromServer(PrefabFillFields server, object serverTextOwner)
    {
        var merged = ServerFill.Apply(CurrentFillFields(), server);
        ServerTextOwner = serverTextOwner;
        MaxAmount = merged.MaxAmount;
        Source = new PrefabGUID(merged.SourceHash);
        LocalizedName = merged.Name;
        LocalizedDescription = merged.Description;
        Kind = merged.Kind;
    }

    /// <summary>Replace a server-only definition on a later server update; local plugin definitions use fill semantics.</summary>
    internal void UpdateSyncedDefinition(PrefabFillFields server)
    {
        MaxAmount = server.MaxAmount;
        Source = new PrefabGUID(server.SourceHash);
        LocalizedName = server.Name;
        LocalizedDescription = server.Description;
        Kind = server.Kind;
        // The following stat packet supplies the new bonuses; never reuse an old donor's bonus set.
        StatBonuses = ItemStatBonuses.Empty;
    }

    /// <summary>
    /// The connection whose server filled this definition ended: restore the local values. False
    /// when nothing was filled this join.
    /// </summary>
    internal bool RevertServerFill()
    {
        if (!ServerFill.TryRevert(out var before)) return false;
        ServerTextOwner = null;
        MaxAmount = before.MaxAmount;
        Source = new PrefabGUID(before.SourceHash);
        LocalizedName = before.Name;
        LocalizedDescription = before.Description;
        Kind = before.Kind;
        return true;
    }

    private PrefabFillFields CurrentFillFields() =>
        new(MaxAmount, Source.GuidHash, LocalizedName, LocalizedDescription, Kind);

    /// <summary>
    /// Register <c>Named</c> / <c>Described</c> under this definition's <see cref="Owner"/>.
    /// Lifetime is the plugin registration, not the first apply: soft unapply does not
    /// drop the keys (the clone still points at them), and a later Register of the same
    /// owner is a no-op for the same text. <c>Localization.RemoveAll(owner)</c> on plugin
    /// unload still reverses them.
    /// </summary>
    internal void EnsureLocalizedKeys()
    {
        try
        {
            string? name = LocalizedName;
            if (!string.IsNullOrEmpty(name))
            {
                string nameKey = Kind == PrefabKind.Buff ? BuffBarText.NameKey(Id) : Id;
                Progenitor.Localization.Localization.Register(nameKey, name, owner: LocalizationOwner(name: true));
            }
            string? desc = LocalizedDescription;
            if (!string.IsNullOrEmpty(desc))
            {
                string descKey = PrefabWire.DescriptionKey(Id);
                Progenitor.Localization.Localization.Register(descKey, desc, owner: LocalizationOwner(name: false));
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabDefinition '{Id}': localization register threw: {e}");
        }
    }

    /// <summary>
    /// Record a side-effect that <see cref="RunUndo"/> must reverse (icon lease,
    /// anything else the applicator attaches to this definition). Last-in is disposed first.
    /// Localization keys are <see cref="EnsureLocalizedKeys"/> / owner-scoped, not undo.
    /// </summary>
    internal void RecordUndo(IDisposable disposable)
    {
        if (disposable == null) return;
        _undo.Add(disposable);
    }

    /// <summary>
    /// True after at least one <see cref="RecordUndo"/> that has not been <see cref="RunUndo"/>n.
    /// A retry of an already-applied definition must not stack another localization
    /// <see cref="RecordUndo"/>; a re-register after soft unapply starts with an empty list.
    /// </summary>
    internal bool HasRecordedUndo => _undo.Count > 0;

    /// <summary>
    /// Dispose every recorded side-effect. Safe to call twice; the list is cleared after the first
    /// run so unregister can still proceed if a later lookup-table cleanup fails.
    /// </summary>
    internal void RunUndo()
    {
        for (int i = _undo.Count - 1; i >= 0; i--)
        {
            try { _undo[i].Dispose(); }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"PrefabDefinition '{Id}': undo threw: {e}");
            }
        }
        _undo.Clear();
    }
}
