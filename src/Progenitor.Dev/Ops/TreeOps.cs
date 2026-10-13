using System;
using System.Collections.Generic;
using System.Text.Json;
using ProjectM;
using ProjectM.Behaviours;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Read a boss behavior tree and its cast-option conditions. Does not edit the blob.
/// Node and condition layouts are from the server metadata: BehaviourTreeNode,
/// BlobArray offset/length (the metadata overlays both at 0x0; the live pair is 8 bytes),
/// BlobAssetHeader.Length at header 0x8.
/// </summary>
public static unsafe class TreeOps
{
    const int HeaderBytes = 32;
    const int HeaderLengthAt = 8;
    const int NodeBytes = 24;
    const int MaxNodes = 512;
    const int MaxBlob = 1_048_576;

    public static object Run(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("tree runs on the server side only");
        var em = world.EntityManager;
        var boss = ReadEntity(args);
        if (!em.Exists(boss))
            throw new InvalidOperationException("entity does not exist");

        var bindingType = ComponentIndex.Of<BehaviourTreeBinding>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(boss, bindingType))
            throw new InvalidOperationException("entity has no behavior tree binding");
        int treeGuid = *(int*)em.GetComponentDataRawRO(boss, bindingType.TypeIndex);
        if (!TryPrefab(world, treeGuid, out var treeEntity, out var treeName))
            throw new InvalidOperationException("behavior tree prefab was not found");

        var treeType = ComponentIndex.Of<BehaviourTree>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(treeEntity, treeType))
            throw new InvalidOperationException("tree prefab has no BehaviourTree");
        byte* blob = *(byte**)em.GetComponentDataRawRO(treeEntity, treeType.TypeIndex);
        if (blob == null)
            throw new InvalidOperationException("behavior tree blob pointer was null");
        int blobLength = *(int*)(blob - HeaderBytes + HeaderLengthAt);
        if (blobLength < 16 || blobLength > MaxBlob)
            throw new InvalidOperationException("behavior tree blob length " + blobLength + " was refused");

        var nodes = ReadNodes(blob, blobLength);
        var casts = ReadCastOptions(em, world, boss);
        return new { tree = treeName, guid = treeGuid, nodes = nodes.Count, gates = Gates(nodes), castOptions = casts };
    }

    static List<Node> ReadNodes(byte* blob, int blobLength)
    {
        int count = *(int*)(blob + 4);
        int offset = *(int*)blob;
        if (count < 0 || count > MaxNodes)
            throw new InvalidOperationException("behavior tree node count " + count + " was refused");
        byte* data = blob + offset;
        if (data < blob || data + (count * NodeBytes) > blob + blobLength)
            throw new InvalidOperationException("behavior tree node array is outside the blob");
        var nodes = new List<Node>(count);
        for (int i = 0; i < count; i++)
        {
            byte* node = data + (i * NodeBytes);
            int type = *(int*)(node + 4);
            var board = Slice(node + 8, blob, blobLength, 1);
            nodes.Add(new Node
            {
                Index = *(ushort*)(node + 2),
                Parent = *(ushort*)node,
                Type = type,
                Blackboard = board,
            });
        }
        return nodes;
    }

    static List<object> Gates(List<Node> nodes)
    {
        var floats = new Dictionary<int, float>();
        foreach (var node in nodes)
        {
            if (node.Type != (int)BehaviourTreeNodeType.Float_Set || node.Blackboard == null || node.Blackboard.Length < 8)
                continue;
            float value = BitConverter.ToSingle(node.Blackboard, 0);
            int register = BitConverter.ToInt32(node.Blackboard, 4);
            floats[register] = value;
        }

        var gates = new List<object>();
        foreach (var node in nodes)
        {
            if (node.Type != (int)BehaviourTreeNodeType.AttackEntityCastOption
                && node.Type != (int)BehaviourTreeNodeType.EvaluateCastOptions)
                continue;
            var conditions = new List<object>();
            int parent = node.Parent;
            int guard = 0;
            while (parent != ushort.MaxValue && parent >= 0 && parent < nodes.Count && guard++ < 32)
            {
                var above = nodes[parent];
                object described = Describe(above, floats);
                if (described != null) conditions.Add(described);
                parent = above.Parent;
            }
            gates.Add(new { node = node.Index, kind = Name(node.Type), conditions });
        }
        return gates;
    }

    static object Describe(Node node, Dictionary<int, float> floats)
    {
        var board = node.Blackboard;
        if (node.Type == (int)BehaviourTreeNodeType.HealthPercentageLessThanInstant && board != null && board.Length >= 4)
        {
            int register = BitConverter.ToInt32(board, 0);
            floats.TryGetValue(register, out float value);
            return new { condition = "health<", register, value, known = floats.ContainsKey(register) };
        }
        if (node.Type == (int)BehaviourTreeNodeType.HealthPercentageLessThanMonitor && board != null && board.Length >= 8)
        {
            int register = BitConverter.ToInt32(board, 4);
            floats.TryGetValue(register, out float value);
            return new { condition = board[0] == 0 ? "health<" : "health>=", register, value, known = floats.ContainsKey(register) };
        }
        if (node.Type == (int)BehaviourTreeNodeType.IsWithinRangeInstant && board != null && board.Length >= 4)
            return new { condition = "range<=", range = BitConverter.ToSingle(board, 0) };
        if (node.Type == (int)BehaviourTreeNodeType.IsWithinRangeMonitor && board != null && board.Length >= 8)
            return new { condition = board[0] == 0 ? "range<=" : "range>", range = BitConverter.ToSingle(board, 4) };
        if (node.Type == (int)BehaviourTreeNodeType.EntityInLineOfSightInstant
            || node.Type == (int)BehaviourTreeNodeType.EntityInLineOfSightMonitor
            || node.Type == (int)BehaviourTreeNodeType.EntityInPathfindingLineOfSightInstant
            || node.Type == (int)BehaviourTreeNodeType.EntityInPathfindingLineOfSightMonitor)
            return new { condition = "lineOfSight" };
        if (node.Type == (int)BehaviourTreeNodeType.DamageTakenInstant
            || node.Type == (int)BehaviourTreeNodeType.DamageTakenMonitor)
            return new { condition = "damageTaken" };
        if (node.Type == (int)BehaviourTreeNodeType.IsBehindInstant
            || node.Type == (int)BehaviourTreeNodeType.IsBehindMonitor)
            return new { condition = "behind" };
        return null;
    }

    static List<object> ReadCastOptions(EntityManager em, World world, Entity boss)
    {
        var list = new List<object>();
        var userType = ComponentIndex.Of<CastOptionsUser>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(boss, userType)) return list;
        int hash = *(int*)em.GetComponentDataRawRO(boss, userType.TypeIndex);
        if (!TryPrefab(world, hash, out var root, out _)) return list;
        var stateType = ComponentIndex.Of<CastOptionStateBuffer>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(root, stateType)) return list;
        int states = em.GetBufferLength(root, stateType.TypeIndex);
        byte* stateRaw = (byte*)em.GetBufferRawRO(root, stateType.TypeIndex);
        var groupType = ComponentIndex.Of<CastOptionGroupBuffer>(ComponentType.AccessMode.ReadOnly);
        var optionType = ComponentIndex.Of<CastOptionBuffer>(ComponentType.AccessMode.ReadOnly);
        for (int s = 0; s < states && s < 8; s++)
        {
            int state = *(int*)(stateRaw + (s * 12));
            if ((state & 8) == 0) continue;
            var holder = new Entity { Index = *(int*)(stateRaw + (s * 12) + 4), Version = *(int*)(stateRaw + (s * 12) + 8) };
            if (!em.Exists(holder) || !em.HasComponent(holder, groupType)) continue;
            int groups = em.GetBufferLength(holder, groupType.TypeIndex);
            byte* groupRaw = (byte*)em.GetBufferRawRO(holder, groupType.TypeIndex);
            for (int g = 0; g < groups && g < 16; g++)
            {
                var optionEntity = new Entity { Index = *(int*)(groupRaw + (g * 8)), Version = *(int*)(groupRaw + (g * 8) + 4) };
                if (!em.Exists(optionEntity) || !em.HasComponent(optionEntity, optionType)) continue;
                int rows = em.GetBufferLength(optionEntity, optionType.TypeIndex);
                byte* rowRaw = (byte*)em.GetBufferRawRO(optionEntity, optionType.TypeIndex);
                for (int r = 0; r < rows && r < 8; r++)
                {
                    byte* row = rowRaw + (r * 120);
                    list.Add(new
                    {
                        abilityIndex = *(int*)row,
                        rangeMin = *(float*)(row + 8),
                        rangeMax = *(float*)(row + 12),
                        cooldownMin = *(float*)(row + 16),
                        cooldownMax = *(float*)(row + 20),
                        aggro = *(int*)(row + 24),
                        lineOfSight = *(int*)(row + 40),
                        conditions = ReadConditions(*(byte**)(row + 48)),
                    });
                }
            }
        }
        return list;
    }

    static List<object> ReadConditions(byte* blob)
    {
        var found = new List<object>();
        if (blob == null) return found;
        int blobLength = *(int*)(blob - HeaderBytes + HeaderLengthAt);
        if (blobLength < 16 || blobLength > 65536) return found;
        int count = *(int*)(blob + 4);
        int offset = *(int*)blob;
        if (count <= 0 || count > 32) return found;
        byte* data = blob + offset;
        const int element = 32;
        if (data < blob || data + (count * element) > blob + blobLength) return found;
        for (int i = 0; i < count; i++)
        {
            byte* item = data + (i * element);
            int source = *(int*)item;
            int target = *(int*)(item + 16);
            int compare = *(int*)(item + 20);
            float value = *(float*)(item + 24);
            if (source == 0 || source == 1)
                found.Add(new { source = source == 0 ? "health" : "healthFactor", target, compare, value });
            else
                found.Add(new { source, target, compare, value });
        }
        return found;
    }

    static byte[] Slice(byte* field, byte* blob, int blobLength, int elementBytes)
    {
        int offset = *(int*)field;
        int count = *(int*)(field + 4);
        if (count <= 0 || count > 256) return null;
        byte* data = field + offset;
        int bytes = count * elementBytes;
        if (data < blob || data + bytes > blob + blobLength) return null;
        var copy = new byte[bytes];
        for (int i = 0; i < bytes; i++) copy[i] = data[i];
        return copy;
    }

    static string Name(int type)
    {
        if (Enum.IsDefined(typeof(BehaviourTreeNodeType), type))
            return ((BehaviourTreeNodeType)type).ToString();
        return type.ToString();
    }

    static Entity ReadEntity(JsonElement args)
    {
        if (!args.TryGetProperty("entity", out var el) || el.ValueKind != JsonValueKind.String)
            throw new ArgumentException("args.entity is required");
        var parts = el.GetString().Split(':');
        if (parts.Length != 2)
            throw new ArgumentException("args.entity must be index:version");
        return new Entity { Index = int.Parse(parts[0]), Version = int.Parse(parts[1]) };
    }

    static bool TryPrefab(World world, int hash, out Entity entity, out string name)
    {
        entity = Entity.Null;
        name = "";
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
        if (prefabs == null) return false;
        var guid = new PrefabGUID { _Value = hash };
        if (!prefabs._PrefabGuidToEntityMap.TryGetValue(guid, out entity) || entity == Entity.Null)
            return false;
        name = prefabs._PrefabLookupMap.TryGetName(guid, out var found) ? found.ToString() : null;
        return world.EntityManager.Exists(entity);
    }

    sealed class Node
    {
        public int Index;
        public int Parent;
        public int Type;
        public byte[] Blackboard;
    }

}
