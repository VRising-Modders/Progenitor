using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Castles;
using Progenitor.Inventory;
using Progenitor.Materials;

namespace Progenitor.SharedResourcesTest;

public sealed partial class TestContext
{
    private IDisposable? _provider;

    /// <summary>Fault injection for the provider-failure checks: none, candidates, sources (every item) or item (one item).</summary>
    internal string ProviderFault = "none";
    internal int ProviderFaultItem;

    /// <summary>Items the provider leaves to the game (its Sources returns null for them): the per-item fallback check.</summary>
    internal readonly HashSet<int> VanillaItems = new();

    internal bool ToggleVanilla(int item)
    {
        if (item == 0) throw new ArgumentException("Name a nonzero item hash.");
        bool on = VanillaItems.Add(item) || !VanillaItems.Remove(item);
        InvalidateProvider();
        return on;
    }

    internal void SetFault(string mode, int item)
    {
        if (mode is not ("none" or "candidates" or "sources" or "item")) throw new ArgumentException("Fault must be none, candidates, sources or item.");
        if (mode == "item" && item == 0) throw new ArgumentException("Name the item hash for an item fault.");
        ProviderFault = mode;
        ProviderFaultItem = mode == "item" ? item : 0;
        InvalidateProvider();
    }

    partial void EnableProvider(ref bool enabled)
    {
        if (Territory < 0 || SourceIds.Count == 0 || Fixture.Costs.Count == 0)
            throw new InvalidOperationException("Set scope, sources, ranks and all ingredient costs first.");
        var policy = new FixedMaterialProvider(this);
        var registration = MaterialSource.Register(policy);
        try
        {
            var status = MaterialSource.Status;
            ProviderStatus = status.ToString();
            if (!status.HooksInstalled || !status.Wired.Contains(MaterialOperation.Build) || !status.Wired.Contains(MaterialOperation.Craft))
                throw new InvalidOperationException("Framework hooks not ready: " + status);
            _provider = registration;
            enabled = true;
            MaterialSource.Invalidate(Actor);
        }
        catch
        {
            registration.Dispose(); _provider = null;
            ProviderStatus = MaterialSource.Status.ToString();
            throw;
        }
    }

    partial void DisableProvider()
    {
        _provider?.Dispose(); _provider = null;
        ProviderEnabled = false;
        ProviderStatus = "disabled";
    }

    partial void InvalidateProvider()
    {
        if (_provider != null) MaterialSource.Invalidate(Actor);
    }

    partial void RefreshProviderStatus() => ProviderStatus = MaterialSource.Status.ToString();

    private sealed class FixedMaterialProvider : IMaterialSourceProvider
    {
        private readonly TestContext _test;
        private readonly CastleId _actor;
        private readonly int _epoch;
        private readonly int _territory;
        private readonly long _revision;
        private readonly Dictionary<int, ItemSources> _items = new();
        private readonly IReadOnlyList<ContainerId> _candidates;
        private static readonly ItemSources Empty = new(Array.Empty<ContainerId>());
        public IReadOnlyCollection<MaterialOperation> Operations { get; } =
            Array.AsReadOnly(new[] { MaterialOperation.Build, MaterialOperation.Craft });

        internal FixedMaterialProvider(TestContext test)
        {
            _test = test; _actor = CastleId.From(test.Actor); _epoch = test.WorldState.LoadEpoch;
            _territory = test.Territory; _revision = test.Fixture.Revision;
            foreach (int item in test.Fixture.Costs.Keys)
            {
                var order = test.Fixture.Order(item);
                if (order.Length == 0 || order.Any(alias => !test.SourceIds.ContainsKey(alias)))
                    throw new InvalidOperationException("Each ingredient needs a ranked, bound source.");
                _items.Add(item, new ItemSources(Array.AsReadOnly(order.Select(alias => test.SourceIds[alias]).ToArray()), test.Fixture.Reserve(item)));
            }
            _candidates = Array.AsReadOnly(_items.Values.SelectMany(x => x.SpendOrder).Distinct().ToArray());
        }

        private bool Applies(in MaterialContext context) => context.User.Equals(_actor)
            && context.WorldEpoch == _epoch && _test.WorldState.LoadEpoch == _epoch
            && context.StandingTerritory == _territory
            && (context.Operation == MaterialOperation.Build || context.Operation == MaterialOperation.Craft);

        // This deliberately governs only one testing admin on one plot. It is not a sharing policy for real players.
        public long Revision(in MaterialContext context) => _revision;
        public IReadOnlyList<ContainerId> Candidates(in MaterialContext context)
        {
            if (!Applies(context)) return Array.Empty<ContainerId>();
            if (_test.ProviderFault == "candidates") throw new InvalidOperationException("injected test fault (candidates)");
            return _candidates;
        }

        public ItemSources? Sources(in MaterialContext context, int item)
        {
            if (!Applies(context) || _test.VanillaItems.Contains(item)) return null;
            if (_test.ProviderFault == "sources" || (_test.ProviderFault == "item" && _test.ProviderFaultItem == item))
                throw new InvalidOperationException("injected test fault (sources, item " + item + ")");
            // Items without a configured cost are governed with no remote sources (never the game's storage).
            return _items.TryGetValue(item, out var sources) ? sources : Empty;
        }
    }
}
