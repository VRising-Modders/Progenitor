using System;
using System.Collections.Generic;

#if !PROGENITOR_TESTS
using Stunlock.Core;
#endif

namespace Progenitor.Content;

/// <summary>
/// Station kind for one row of <see cref="RecipeDefinitionBuilder.At"/>. This increment
/// accepts <see cref="Workstation"/> only; refinement and character crafting are phase 2.
/// </summary>
public enum RecipeStationKind
{
    /// <summary><c>WorkstationRecipesBuffer</c> on a crafting bench. Not networked.</summary>
    Workstation = 0,

    /// <summary><c>RefinementstationRecipesBuffer</c>. Networked; not in this increment.</summary>
    Refinement = 1,

    /// <summary>Player-prefab inventory crafting. Not in this increment (U2).</summary>
    Character = 2,
}

/// <summary>
/// One cloned recipe: a source <c>RecipeData</c> prefab, one output, 1–8 inputs, 1–4
/// workstation rows. GUID is <c>PrefabGUID.CalculateGuidHash</c> of <see cref="Id"/> so both
/// sides compute the same value without a handshake. There is no <c>ManagedRecipeData</c>;
/// the crafting UI paints the output item.
/// </summary>
public sealed class RecipeDefinition
{
    internal RecipeDefinition(
        string id,
        int guidHash,
        int sourceGuidHash,
        string? sourceName,
        RecipeItemRef output,
        IReadOnlyList<RecipeItemRef> inputs,
        IReadOnlyList<RecipeStationRef> stations,
        float craftDuration,
        int hudSortingOrder,
        bool alwaysUnlocked,
        object owner)
    {
        Id = id;
        GuidHash = guidHash;
        SourceGuidHash = sourceGuidHash;
        SourceName = sourceName;
        Output = output;
        // Copies, read-only: a caller casting these to IList must not bypass the builder's
        // validation or redirect a registered recipe.
        Inputs = new List<RecipeItemRef>(inputs).AsReadOnly();
        Stations = new List<RecipeStationRef>(stations).AsReadOnly();
        CraftDuration = craftDuration;
        HudSortingOrder = hudSortingOrder;
        AlwaysUnlocked = alwaysUnlocked;
        Owner = owner;
        Plan = RecipeApplyPlan.From(this);
    }

    /// <summary>Spawnable name and hash input. ASCII, 1–63 bytes.</summary>
    public string Id { get; }

    /// <summary>Deterministic GUID hash of <see cref="Id"/>.</summary>
    public int GuidHash { get; }

    /// <summary>Resolved source recipe GUID hash. 0 when the source was a name not yet resolved.</summary>
    public int SourceGuidHash { get; internal set; }

    /// <summary>Spawnable / lookup name passed to <c>CloneOf(string)</c>.</summary>
    public string? SourceName { get; }

    /// <summary>Single output row written onto <c>RecipeOutputBuffer</c>.</summary>
    public RecipeItemRef Output { get; }

    /// <summary>1–8 requirement rows written onto <c>RecipeRequirementBuffer</c>.</summary>
    public IReadOnlyList<RecipeItemRef> Inputs { get; }

    /// <summary>1–4 station rows. This increment requires <see cref="RecipeStationKind.Workstation"/>.</summary>
    public IReadOnlyList<RecipeStationRef> Stations { get; }

    /// <summary><c>RecipeData.CraftDuration</c>.</summary>
    public float CraftDuration { get; }

    /// <summary><c>RecipeData.HudSortingOrder</c>. The UI sorts by this, not buffer position.</summary>
    public int HudSortingOrder { get; }

    /// <summary><c>RecipeData.AlwaysUnlocked</c>. Default true.</summary>
    public bool AlwaysUnlocked { get; }

    /// <summary>Plugin (or other object) that called <see cref="RecipeDefinitionBuilder.Register"/>.</summary>
    public object Owner { get; }

    /// <summary>Ordered apply / unapply steps derived from this definition. Game-free.</summary>
    public RecipeApplyPlan Plan { get; }

    /// <summary>Starts a cloned-recipe definition. <c>CloneOf</c> is required before Register.</summary>
    public static RecipeDefinitionBuilder Define(string id) => new(id);

    /// <summary>
    /// Live path uses <c>PrefabGUID.CalculateGuidHash</c>. Tests use a stable FNV-1a so the
    /// builder can be driven without Stunlock.Core; the live call is source-pinned.
    /// </summary>
    internal static int GuidHashOf(string id)
    {
#if PROGENITOR_TESTS
        return RecipeGuidHash.Fnv1a32(id);
#else
        return PrefabGUID.CalculateGuidHash(id);
#endif
    }
}

/// <summary>One input or output item: a spawnable name, a Progenitor id, or a GUID hash.</summary>
public readonly struct RecipeItemRef
{
    public RecipeItemRef(string? name, int guidHash, int amount)
    {
        Name = name;
        GuidHash = guidHash;
        Amount = amount;
    }

    /// <summary>Spawnable name or Progenitor item id. Null when the caller passed a GUID hash.</summary>
    public string? Name { get; }

    /// <summary>0 until apply resolves a name, or the hash passed to the GUID overload.</summary>
    public int GuidHash { get; }

    public int Amount { get; }
}

/// <summary>One station this recipe is listed on.</summary>
public readonly struct RecipeStationRef
{
    public RecipeStationRef(RecipeStationKind kind, string? name, int guidHash)
    {
        Kind = kind;
        Name = name;
        GuidHash = guidHash;
    }

    public RecipeStationKind Kind { get; }

    /// <summary>Spawnable / lookup name. Null when the caller passed a GUID hash.</summary>
    public string? Name { get; }

    public int GuidHash { get; }
}

/// <summary>
/// Fluent description of one cloned recipe. Validation runs at <see cref="Register(object)"/>
/// with a readable message; apply does not throw for a malformed builder.
/// </summary>
public sealed class RecipeDefinitionBuilder
{
    public const int MaxInputs = 8;
    public const int MaxStations = 4;

    private readonly string _id;
    private int _sourceGuidHash;
    private string? _sourceName;
    private RecipeItemRef? _output;
    private readonly List<RecipeItemRef> _inputs = new();
    private readonly List<RecipeStationRef> _stations = new();
    private float? _craftDuration;
    private int _hudSortingOrder;
    private bool _alwaysUnlocked = true;
    private bool _registered;
    private object? _boundOwner;
    private Action<IDisposable>? _track;

    internal RecipeDefinitionBuilder(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Recipe id is required.", nameof(id));
        PrefabWire.ValidateAscii(id, nameof(id), "id");
        _id = id;
    }

    internal void BindPlugin(object owner, Action<IDisposable> track)
    {
        _boundOwner = owner ?? throw new ArgumentNullException(nameof(owner));
        _track = track ?? throw new ArgumentNullException(nameof(track));
    }

    /// <summary>Vanilla (or already-registered) recipe prefab to clone, identified by GUID hash.</summary>
    public RecipeDefinitionBuilder CloneOf(int sourceGuidHash)
    {
        if (sourceGuidHash == 0)
            throw new ArgumentOutOfRangeException(nameof(sourceGuidHash), "CloneOf GUID hash must be non-zero.");
        _sourceGuidHash = sourceGuidHash;
        _sourceName = null;
        return this;
    }

    /// <summary>
    /// Vanilla recipe to clone, identified by lookup name
    /// (for example <c>Recipe_Ingredient_CoarseThread</c>). Resolved at apply time.
    /// </summary>
    public RecipeDefinitionBuilder CloneOf(string spawnableName)
    {
        if (string.IsNullOrEmpty(spawnableName))
            throw new ArgumentException("Spawnable name is required.", nameof(spawnableName));
        _sourceName = spawnableName;
        _sourceGuidHash = 0;
        return this;
    }

#if !PROGENITOR_TESTS
    /// <summary>Vanilla (or already-registered) recipe prefab to clone, identified by GUID.</summary>
    public RecipeDefinitionBuilder CloneOf(PrefabGUID source) => CloneOf(source.GuidHash);
#endif

    /// <summary>Single output. A Progenitor item id, a spawnable name, or a GUID hash.</summary>
    public RecipeDefinitionBuilder Output(string idOrName, int amount)
    {
        if (string.IsNullOrEmpty(idOrName))
            throw new ArgumentException("Output id or spawnable name is required.", nameof(idOrName));
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Output amount must be positive.");
        if (_output.HasValue)
            throw new InvalidOperationException($"DefineRecipe('{_id}') already has Output; a recipe takes exactly one output.");
        _output = new RecipeItemRef(idOrName, 0, amount);
        return this;
    }

    /// <summary>Single output identified by GUID hash.</summary>
    public RecipeDefinitionBuilder Output(int guidHash, int amount)
    {
        if (guidHash == 0)
            throw new ArgumentOutOfRangeException(nameof(guidHash), "Output GUID hash must be non-zero.");
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Output amount must be positive.");
        if (_output.HasValue)
            throw new InvalidOperationException($"DefineRecipe('{_id}') already has Output; a recipe takes exactly one output.");
        _output = new RecipeItemRef(null, guidHash, amount);
        return this;
    }

#if !PROGENITOR_TESTS
    /// <summary>Single output identified by GUID.</summary>
    public RecipeDefinitionBuilder Output(PrefabGUID guid, int amount) => Output(guid.GuidHash, amount);
#endif

    /// <summary>One requirement row. Repeatable, at most <see cref="MaxInputs"/>.</summary>
    public RecipeDefinitionBuilder Input(string idOrName, int amount)
    {
        if (string.IsNullOrEmpty(idOrName))
            throw new ArgumentException("Input id or spawnable name is required.", nameof(idOrName));
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Input amount must be positive.");
        _inputs.Add(new RecipeItemRef(idOrName, 0, amount));
        return this;
    }

    /// <summary>One requirement row identified by GUID hash.</summary>
    public RecipeDefinitionBuilder Input(int guidHash, int amount)
    {
        if (guidHash == 0)
            throw new ArgumentOutOfRangeException(nameof(guidHash), "Input GUID hash must be non-zero.");
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Input amount must be positive.");
        _inputs.Add(new RecipeItemRef(null, guidHash, amount));
        return this;
    }

#if !PROGENITOR_TESTS
    /// <summary>One requirement row identified by GUID.</summary>
    public RecipeDefinitionBuilder Input(PrefabGUID guid, int amount) => Input(guid.GuidHash, amount);
#endif

    /// <summary>
    /// Station this recipe is listed on. Repeatable, at most <see cref="MaxStations"/>.
    /// This increment requires <see cref="RecipeStationKind.Workstation"/>.
    /// </summary>
    public RecipeDefinitionBuilder At(RecipeStationKind kind, string stationName)
    {
        if (string.IsNullOrEmpty(stationName))
            throw new ArgumentException("Station name is required.", nameof(stationName));
        _stations.Add(new RecipeStationRef(kind, stationName, 0));
        return this;
    }

    /// <summary>Station identified by GUID hash.</summary>
    public RecipeDefinitionBuilder At(RecipeStationKind kind, int stationGuidHash)
    {
        if (stationGuidHash == 0)
            throw new ArgumentOutOfRangeException(nameof(stationGuidHash), "Station GUID hash must be non-zero.");
        _stations.Add(new RecipeStationRef(kind, null, stationGuidHash));
        return this;
    }

#if !PROGENITOR_TESTS
    /// <summary>Station identified by GUID.</summary>
    public RecipeDefinitionBuilder At(RecipeStationKind kind, PrefabGUID station) =>
        At(kind, station.GuidHash);
#endif

    /// <summary><c>RecipeData.CraftDuration</c> in seconds. Must be finite and positive.</summary>
    public RecipeDefinitionBuilder CraftTime(float seconds)
    {
        _craftDuration = seconds;
        return this;
    }

    /// <summary><c>RecipeData.HudSortingOrder</c>. The crafting UI sorts by this, not buffer index.</summary>
    public RecipeDefinitionBuilder HudOrder(int order)
    {
        _hudSortingOrder = order;
        return this;
    }

    /// <summary><c>RecipeData.AlwaysUnlocked</c>. Default on; pass false to leave it to progression.</summary>
    public RecipeDefinitionBuilder AlwaysUnlocked(bool value = true)
    {
        _alwaysUnlocked = value;
        return this;
    }

    /// <summary>
    /// Refused. There is no <c>ManagedRecipeData</c>; the station row's name is the output
    /// item's <c>ManagedItemData.Name</c>, set with <c>DefineItem(...).Named(...)</c>.
    /// </summary>
    public RecipeDefinitionBuilder Named(string localizedName)
    {
        throw new InvalidOperationException(
            "Recipes have no ManagedRecipeData; the crafting UI paints the output item's name. Call DefineItem(...).Named(...) on the output.");
    }

    /// <summary>
    /// Refused. The station row has no description of its own; put tooltip text on the output item.
    /// </summary>
    public RecipeDefinitionBuilder Described(string text)
    {
        throw new InvalidOperationException(
            "Recipes have no ManagedRecipeData; the crafting UI paints the output item. Call DefineItem(...).Described(...) on the output.");
    }

    /// <summary>
    /// Accepts the definition under this plugin. No owner argument: the context's plugin is used.
    /// </summary>
    public IDisposable Register()
    {
        if (_boundOwner == null)
            throw new InvalidOperationException($"DefineRecipe('{_id}') Register() needs an owner; use Register(owner) or ctx.Content.DefineRecipe.");
        var handle = Register(_boundOwner);
        _track?.Invoke(handle);
        return handle;
    }

    /// <summary>
    /// Accepts the definition under <paramref name="owner"/>. Returns a handle whose disposal
    /// detaches the station rows this owner appended and destroys the clone this process created,
    /// without touching vanilla recipes. Dispose from a reloadable plugin's Unload (or let
    /// <c>ctx.Content</c> track it).
    /// </summary>
    public IDisposable Register(object owner)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        if (_registered)
            throw new InvalidOperationException($"DefineRecipe('{_id}') has already been Register()'d.");
        if (_sourceGuidHash == 0 && string.IsNullOrEmpty(_sourceName))
            throw new InvalidOperationException($"DefineRecipe('{_id}') requires CloneOf(...) before Register.");
        if (!_output.HasValue)
            throw new InvalidOperationException($"DefineRecipe('{_id}') requires exactly one Output(...) before Register.");
        if (_inputs.Count == 0)
            throw new InvalidOperationException($"DefineRecipe('{_id}') requires at least one Input(...) (1–{MaxInputs}).");
        if (_inputs.Count > MaxInputs)
            throw new InvalidOperationException($"DefineRecipe('{_id}') has {_inputs.Count} inputs; the limit is {MaxInputs}.");
        if (_stations.Count == 0)
            throw new InvalidOperationException($"DefineRecipe('{_id}') requires at least one At(...) station (1–{MaxStations}).");
        if (_stations.Count > MaxStations)
            throw new InvalidOperationException($"DefineRecipe('{_id}') has {_stations.Count} stations; the limit is {MaxStations}.");
        for (int i = 0; i < _stations.Count; i++)
        {
            if (_stations[i].Kind != RecipeStationKind.Workstation)
            {
                throw new InvalidOperationException(
                    $"DefineRecipe('{_id}') station {_stations[i].Kind} is not supported in this increment; RecipeStationKind.Workstation is the supported path (Refinement and Character are phase 2).");
            }
        }
        if (!_craftDuration.HasValue)
            throw new InvalidOperationException($"DefineRecipe('{_id}') requires CraftTime(...) before Register.");
        float duration = _craftDuration.Value;
        if (float.IsNaN(duration) || float.IsInfinity(duration) || duration <= 0f)
            throw new InvalidOperationException($"DefineRecipe('{_id}') CraftTime must be finite and positive.");
        int guidHash = RecipeDefinition.GuidHashOf(_id);
        if (_sourceGuidHash != 0 && guidHash == _sourceGuidHash)
            throw new InvalidOperationException($"DefineRecipe('{_id}') must not reuse the source recipe GUID.");

        _registered = true;
        var def = new RecipeDefinition(
            _id,
            guidHash,
            _sourceGuidHash,
            _sourceName,
            _output.Value,
            _inputs.ToArray(),
            _stations.ToArray(),
            duration,
            _hudSortingOrder,
            _alwaysUnlocked,
            owner);
        return RecipeApply.Add(def);
    }
}

/// <summary>
/// Ordered apply steps for one recipe, and the inverse unapply list. Game-free so tests can
/// pin the shape without a world. Live <see cref="RecipeApply"/> executes these against the
/// clone, the recipe buffers, <c>RecipeHashLookupMap</c>, and the workstation recipe list.
/// </summary>
public sealed class RecipeApplyPlan
{
    public const string CloneSource = "clone-source";
    public const string WriteRecipeData = "write-recipe-data";
    public const string RewriteOutputBuffer = "rewrite-output-buffer";
    public const string RewriteRequirementBuffer = "rewrite-requirement-buffer";
    public const string ClearOutputUnitBuffer = "clear-output-unit-buffer";
    public const string ClearLinkBuffer = "clear-link-buffer";
    public const string InsertPrefabMaps = "insert-prefab-maps";
    public const string InsertRecipeHashLookup = "insert-recipe-hash-lookup";
    public const string InsertOriginalDurations = "insert-original-durations";
    public const string AppendStationPrefabRow = "append-station-prefab-row";
    public const string AppendStationLiveInstances = "append-station-live-instances";

    public const string RemoveStationLiveInstances = "remove-station-live-instances";
    public const string RemoveStationPrefabRow = "remove-station-prefab-row";
    public const string RemoveOriginalDurations = "remove-original-durations";
    public const string RemoveRecipeHashLookup = "remove-recipe-hash-lookup";
    public const string RemovePrefabMaps = "remove-prefab-maps";
    public const string DestroyClone = "destroy-clone";

    private static readonly string[] ApplyOrder =
    {
        CloneSource,
        WriteRecipeData,
        RewriteOutputBuffer,
        RewriteRequirementBuffer,
        ClearOutputUnitBuffer,
        ClearLinkBuffer,
        InsertPrefabMaps,
        InsertRecipeHashLookup,
        InsertOriginalDurations,
        AppendStationPrefabRow,
        AppendStationLiveInstances,
    };

    private static readonly string[] UnapplyOrder =
    {
        RemoveStationLiveInstances,
        RemoveStationPrefabRow,
        RemoveOriginalDurations,
        RemoveRecipeHashLookup,
        RemovePrefabMaps,
        DestroyClone,
    };

    /// <summary>Read-only views of the shared orders: every plan hands out the same instance.</summary>
    private static readonly IReadOnlyList<string> ApplyOrderView = Array.AsReadOnly(ApplyOrder);
    private static readonly IReadOnlyList<string> UnapplyOrderView = Array.AsReadOnly(UnapplyOrder);

    internal RecipeApplyPlan(RecipeDefinition def, IReadOnlyList<string> steps, IReadOnlyList<string> unapplySteps)
    {
        Definition = def;
        Steps = steps;
        UnapplySteps = unapplySteps;
    }

    public RecipeDefinition Definition { get; }
    public IReadOnlyList<string> Steps { get; }
    public IReadOnlyList<string> UnapplySteps { get; }

    internal static RecipeApplyPlan From(RecipeDefinition def) =>
        new(def, ApplyOrderView, UnapplyOrderView);

    /// <summary>
    /// Inverse of <see cref="Steps"/> for the owned clone and station rows. Station removal is
    /// per-owner (see <see cref="RecipeStationLedger"/>); vanilla rows are never written.
    /// </summary>
    public static bool IsInverseOf(IReadOnlyList<string> apply, IReadOnlyList<string> unapply)
    {
        if (apply == null || unapply == null) return false;
        if (unapply.Count != UnapplyOrder.Length) return false;
        if (apply.Count != ApplyOrder.Length) return false;
        for (int i = 0; i < UnapplyOrder.Length; i++)
        {
            if (!string.Equals(unapply[i], UnapplyOrder[i], StringComparison.Ordinal))
                return false;
        }
        for (int i = 0; i < ApplyOrder.Length; i++)
        {
            if (!string.Equals(apply[i], ApplyOrder[i], StringComparison.Ordinal))
                return false;
        }
        return InversePairs(apply, unapply);
    }

    internal static bool InversePairs(IReadOnlyList<string> apply, IReadOnlyList<string> unapply)
    {
        // Station appends invert to station removes (live first, then prefab). Map / clone
        // inserts invert to map removes then destroy. Buffer rewrites die with the clone.
        if (IndexOf(apply, AppendStationLiveInstances) < 0) return false;
        if (IndexOf(apply, AppendStationPrefabRow) < 0) return false;
        if (IndexOf(apply, CloneSource) < 0) return false;
        if (unapply[0] != RemoveStationLiveInstances) return false;
        if (unapply[1] != RemoveStationPrefabRow) return false;
        if (unapply[unapply.Count - 1] != DestroyClone) return false;
        if (IndexOf(unapply, RemoveRecipeHashLookup) < 0) return false;
        if (IndexOf(unapply, RemovePrefabMaps) < 0) return false;
        return true;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.Ordinal)) return i;
        return -1;
    }
}

/// <summary>Stable FNV-1a 32 used only when Stunlock.Core is not referenced (the test assembly).</summary>
internal static class RecipeGuidHash
{
    public static int Fnv1a32(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            for (int i = 0; i < text.Length; i++)
            {
                hash ^= text[i];
                hash *= 16777619;
            }
            return (int)hash;
        }
    }
}
