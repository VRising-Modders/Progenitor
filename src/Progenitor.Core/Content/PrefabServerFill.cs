namespace Progenitor.Content;

/// <summary>The definition fields a <c>PrfD</c> may fill on a local registration of the same GUID.</summary>
internal readonly struct PrefabFillFields
{
    public readonly int MaxAmount;
    public readonly int SourceHash;
    public readonly string? Name;
    public readonly string? Description;
    public readonly PrefabKind Kind;

    public PrefabFillFields(int maxAmount, int sourceHash, string? name, string? description, PrefabKind kind)
    {
        MaxAmount = maxAmount;
        SourceHash = sourceHash;
        Name = name;
        Description = description;
        Kind = kind;
    }
}

/// <summary>
/// Server values a <c>PrfD</c> wrote into a <b>local</b> definition of the same GUID (a plugin
/// both sides run), and the local values they replaced. Game-free so the suite drives it.
///
/// <para>The merge rule is unchanged: the server fills only what the local registration left
/// empty (stack size, <c>CloneOf</c>, name, description) and may promote an item to a buff,
/// never demote a local buff. What is new is that the fill is <b>connection-scoped</b>. The
/// local definition outlives the join, so before this record existed server A's name and stack
/// size stayed on it for every later join — including a server B running the same plugin, whose
/// own <c>PrfD</c> could then no longer fill those fields.</para>
/// </summary>
internal sealed class PrefabServerFill
{
    private PrefabFillFields _before;
    private bool _active;

    /// <summary>True between the first fill of a join and <see cref="TryRevert"/>.</summary>
    public bool Active => _active;

    /// <summary>The current name came from the server, not from the local registration.</summary>
    public bool NameFromServer { get; private set; }

    /// <summary>The current description came from the server, not from the local registration.</summary>
    public bool DescriptionFromServer { get; private set; }

    /// <summary>
    /// Merge <paramref name="server"/> into <paramref name="current"/> and return the result.
    /// The first fill of a join remembers <paramref name="current"/> as the local values
    /// <see cref="TryRevert"/> puts back; later fills of the same join keep that first snapshot.
    /// </summary>
    public PrefabFillFields Apply(PrefabFillFields current, PrefabFillFields server)
    {
        if (!_active)
        {
            _before = current;
            _active = true;
        }

        int maxAmount = current.MaxAmount <= 0 && server.MaxAmount > 0 ? server.MaxAmount : current.MaxAmount;
        int source = current.SourceHash == 0 ? server.SourceHash : current.SourceHash;
        string? name = PrefabWire.FillEmpty(current.Name, server.Name);
        string? description = PrefabWire.FillEmpty(current.Description, server.Description);
        var kind = server.Kind == PrefabKind.Buff ? PrefabKind.Buff : current.Kind;

        if (!ReferenceEquals(name, current.Name)) NameFromServer = true;
        if (!ReferenceEquals(description, current.Description)) DescriptionFromServer = true;
        return new PrefabFillFields(maxAmount, source, name, description, kind);
    }

    /// <summary>
    /// The connection ended: hand back the local values from before the first fill and forget
    /// the join. False when this join never filled anything.
    /// </summary>
    public bool TryRevert(out PrefabFillFields before)
    {
        before = _before;
        if (!_active) return false;
        _active = false;
        _before = default;
        NameFromServer = false;
        DescriptionFromServer = false;
        return true;
    }
}
