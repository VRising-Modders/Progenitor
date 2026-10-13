using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Ai;

/// <summary>
/// A private, editable copy of a boss's behavior-tree blob (<see cref="AiTrees.CreateVariant"/>).
///
/// <para>Edits change bytes in this copy only; the shared tree prefab and its blob are never
/// touched (tree blobs are deduplicated across bosses, and changing a node's type under a unit that
/// is running it aborts the whole server through Burst, incident INC-20260924-01). The first
/// <see cref="AiTrees.Apply"/> materializes the copy into native memory and freezes it: after that
/// every edit method throws, because units may already be executing the frozen bytes.</para>
/// </summary>
public sealed class TreeVariant
{
    private readonly byte[] _data;
    private readonly List<string> _changes = new();

    internal TreeVariant(string bossName, int bossGuid, string treeName, int treeGuid, byte[] data)
    {
        BossName = bossName ?? "";
        BossGuid = bossGuid;
        TreeName = treeName ?? "";
        TreeGuid = treeGuid;
        _data = data ?? throw new ArgumentNullException(nameof(data));
        if (!TreeBlobReader.TryNodes(_data, out _, out int count))
            throw new ArgumentException("the behavior-tree blob does not parse");
        NodeCount = count;
    }

    public string BossName { get; }
    public int BossGuid { get; }
    public string TreeName { get; }
    public int TreeGuid { get; }
    public int NodeCount { get; }

    /// <summary>True once the copy has been applied to a unit. Frozen variants refuse edits.</summary>
    public bool IsFrozen { get; private set; }

    /// <summary>One line per edit, in order.</summary>
    public IReadOnlyList<string> Changes => _changes;

    /// <summary>Blob data bytes (no header). Live code copies these into native memory once.</summary>
    internal byte[] Data => _data;

    internal void Freeze() => IsFrozen = true;

    /// <summary>
    /// Replace every <c>AttackEntityCastOption</c> node with <c>Succeed</c>: the boss still chases,
    /// strafes and relocates but never casts (live test T5B on a fresh spawn). Returns how many nodes
    /// changed. Safe to apply to a running unit only through <see cref="AiTrees.Apply"/>, which swaps
    /// the unit onto this copy through the job's own abort path.
    /// </summary>
    public int DisableCombat()
    {
        EnsureEditable();
        int changed = 0;
        foreach (int node in NodesOfType(Layout.BehaviourTreeNodeTypeAttackEntityCastOption))
        {
            TreeBlobReader.SetType(_data, node, Layout.BehaviourTreeNodeTypeSucceed);
            changed++;
        }
        _changes.Add("DisableCombat: " + changed.ToString(CultureInfo.InvariantCulture) + " AttackEntityCastOption node(s) -> Succeed");
        return changed;
    }

    /// <summary>Set a <c>Delay</c> node's random duration range. Refused on any other node type.</summary>
    public void SetDelay(int nodeIndex, float minSeconds, float maxSeconds)
    {
        if (!(minSeconds >= 0f) || !(maxSeconds >= minSeconds) || float.IsInfinity(maxSeconds))
            throw new ArgumentException("Delay needs 0 <= min <= max, both finite.");
        EnsureEditable();
        RequireType(nodeIndex, Layout.BehaviourTreeNodeTypeDelay);
        WriteFloat(nodeIndex, Layout.DelayMinTimeOffset, minSeconds);
        WriteFloat(nodeIndex, Layout.DelayMaxTimeOffset, maxSeconds);
        _changes.Add("node " + nodeIndex.ToString(CultureInfo.InvariantCulture) + " Delay " + AiCastOptionRow.F(minSeconds) + ".." + AiCastOptionRow.F(maxSeconds) + " s");
    }

    /// <summary>
    /// Write one documented literal parameter. The node's type is checked against the parameter
    /// first; register fields cannot be written through this.
    /// </summary>
    public void SetParameter(int nodeIndex, TreeParameter parameter, float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException("value must be finite");
        EnsureEditable();
        switch (parameter)
        {
            case TreeParameter.DelayMinTime:
                RequireType(nodeIndex, Layout.BehaviourTreeNodeTypeDelay);
                WriteFloat(nodeIndex, Layout.DelayMinTimeOffset, value);
                break;
            case TreeParameter.DelayMaxTime:
                RequireType(nodeIndex, Layout.BehaviourTreeNodeTypeDelay);
                WriteFloat(nodeIndex, Layout.DelayMaxTimeOffset, value);
                break;
            case TreeParameter.HealthChangePercentageAmount:
                RequireType(nodeIndex, Layout.BehaviourTreeNodeTypeHealthChangePercentage);
                WriteFloat(nodeIndex, Layout.HealthChangePercentageAmountOffset, value);
                break;
            case TreeParameter.CombatAreaRelocationDistance:
                RequireType(nodeIndex, Layout.BehaviourTreeNodeTypeSetCombatAreaRelocationDistance);
                WriteFloat(nodeIndex, Layout.SetCombatAreaRelocationDistanceValueOffset, value);
                break;
            case TreeParameter.IntSetValue:
                RequireType(nodeIndex, Layout.BehaviourTreeNodeTypeIntSet);
                if (value != Math.Floor(value)) throw new ArgumentException("Int_Set.Value takes a whole number");
                WriteInt(nodeIndex, Layout.IntSetValueOffset, (int)value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(parameter));
        }
        _changes.Add("node " + nodeIndex.ToString(CultureInfo.InvariantCulture) + " " + parameter + " = " + value.ToString("0.###", CultureInfo.InvariantCulture));
    }

    /// <summary>Indexes of every node of the named type (for example <c>Delay</c>). Empty for an unknown name.</summary>
    public IReadOnlyList<int> FindNodes(string nodeTypeName)
    {
        int type = AiNames.NodeTypeValue(nodeTypeName ?? "");
        return type < 0 ? Array.Empty<int>() : NodesOfType(type);
    }

    /// <summary>The node's type name, parent and blackboard size.</summary>
    public string DescribeNode(int nodeIndex)
    {
        var node = TreeBlobReader.Node(_data, nodeIndex);
        return "node " + nodeIndex.ToString(CultureInfo.InvariantCulture) + " " + AiNames.NodeType(node.Type)
            + " parent=" + (node.Parent == ushort.MaxValue ? "none" : node.Parent.ToString(CultureInfo.InvariantCulture))
            + " blackboard=" + node.BlackboardLength.ToString(CultureInfo.InvariantCulture) + "B children=" + node.ChildCount.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The node's current type name.</summary>
    public string NodeType(int nodeIndex) => AiNames.NodeType(TreeBlobReader.Node(_data, nodeIndex).Type);

    private List<int> NodesOfType(int type)
    {
        var found = new List<int>();
        for (int i = 0; i < NodeCount; i++)
            if (TreeBlobReader.Node(_data, i).Type == type) found.Add(i);
        return found;
    }

    private void RequireType(int nodeIndex, int type)
    {
        var node = TreeBlobReader.Node(_data, nodeIndex);
        if (node.Type != type)
            throw new InvalidOperationException("node " + nodeIndex.ToString(CultureInfo.InvariantCulture) + " is " + AiNames.NodeType(node.Type) + ", not " + AiNames.NodeType(type));
    }

    private void WriteFloat(int nodeIndex, int fieldOffset, float value) => Bytes.PutF32(_data, Field(nodeIndex, fieldOffset, sizeof(float)), value);

    private void WriteInt(int nodeIndex, int fieldOffset, int value) => Bytes.PutI32(_data, Field(nodeIndex, fieldOffset, sizeof(int)), value);

    private int Field(int nodeIndex, int fieldOffset, int width)
    {
        var node = TreeBlobReader.Node(_data, nodeIndex);
        if (fieldOffset + width > node.BlackboardLength)
            throw new InvalidOperationException("node " + nodeIndex.ToString(CultureInfo.InvariantCulture) + " blackboard is " + node.BlackboardLength + " bytes; the field does not fit");
        return node.BlackboardStart + fieldOffset;
    }

    private void EnsureEditable()
    {
        if (IsFrozen)
            throw new InvalidOperationException("TreeVariant '" + TreeName + "' is frozen: it has been applied to a unit and units may be executing it. Create a new variant instead.");
    }
}

/// <summary>Reads <c>BehaviourTreeBlob</c> data bytes (no header).</summary>
internal static class TreeBlobReader
{
    internal readonly struct NodeView
    {
        internal NodeView(ushort parent, ushort index, int type, int blackboardStart, int blackboardLength, int childCount)
        {
            Parent = parent;
            Index = index;
            Type = type;
            BlackboardStart = blackboardStart;
            BlackboardLength = blackboardLength;
            ChildCount = childCount;
        }

        internal ushort Parent { get; }
        internal ushort Index { get; }
        internal int Type { get; }
        internal int BlackboardStart { get; }
        internal int BlackboardLength { get; }
        internal int ChildCount { get; }
    }

    /// <summary>Node stride: <c>Children</c> is the last field and is one BlobArray wide (0x18).</summary>
    internal static int NodeStride => Layout.BehaviourTreeNodeChildrenOffset + Bytes.BlobArrayWidth;

    internal const int MaxNodes = 4096;

    internal static bool TryNodes(ReadOnlySpan<byte> blob, out int first, out int count)
    {
        if (!Bytes.TryBlobArray(blob, Layout.BehaviourTreeBlobNodesOffset, NodeStride, out first, out count)) return false;
        return count > 0 && count <= MaxNodes;
    }

    internal static NodeView Node(ReadOnlySpan<byte> blob, int index)
    {
        if (!TryNodes(blob, out int first, out int count))
            throw new InvalidOperationException("the behavior-tree blob does not parse");
        if (index < 0 || index >= count)
            throw new ArgumentOutOfRangeException(nameof(index), "node " + index + " is outside 0.." + (count - 1));
        int at = first + index * NodeStride;
        if (!Bytes.TryBlobArray(blob, at + Layout.BehaviourTreeNodeBlackboardOffset, 1, out int bbStart, out int bbLength))
            throw new InvalidOperationException("node " + index + " blackboard is outside the blob");
        if (!Bytes.TryBlobArray(blob, at + Layout.BehaviourTreeNodeChildrenOffset, sizeof(ushort), out _, out int children))
            throw new InvalidOperationException("node " + index + " children are outside the blob");
        return new NodeView(
            Bytes.U16(blob, at + Layout.BehaviourTreeNodeParentOffset),
            Bytes.U16(blob, at + Layout.BehaviourTreeNodeIndexOffset),
            Bytes.I32(blob, at + Layout.BehaviourTreeNodeTypeOffset),
            bbStart,
            bbLength,
            children);
    }

    internal static void SetType(Span<byte> blob, int index, int type)
    {
        if (!TryNodes(blob, out int first, out int count) || index < 0 || index >= count)
            throw new ArgumentOutOfRangeException(nameof(index));
        Bytes.PutI32(blob, first + index * NodeStride + Layout.BehaviourTreeNodeTypeOffset, type);
    }

    /// <summary>Node type name to count, most frequent first.</summary>
    internal static List<KeyValuePair<string, int>> TypeHistogram(ReadOnlySpan<byte> blob)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (TryNodes(blob, out _, out int count))
        {
            for (int i = 0; i < count; i++)
            {
                string name = AiNames.NodeType(Node(blob, i).Type);
                counts[name] = counts.TryGetValue(name, out int n) ? n + 1 : 1;
            }
        }
        var list = new List<KeyValuePair<string, int>>(counts);
        list.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
        return list;
    }
}
