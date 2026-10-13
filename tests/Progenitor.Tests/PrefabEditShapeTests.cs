using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The <c>PrfE</c> shape rule and the apply ladder above it (job FEDW3, R51 findings 1, 2, 6, 7).
///
/// <para><b>What R51 measured, and why this file exists.</b> Before FEDW3 the receiver's filter was
/// an identity denylist: a value type, non-generic, not <c>PrefabGUID</c>, no managed reference,
/// the right <c>Marshal.SizeOf</c>, already on the entity. R51 counted what that admits in this
/// game's interop — 169 public value types with a direct <c>BlobAssetReference&lt;T&gt;</c> field,
/// 781 with an <c>Entity</c>, 286 with a raw pointer, every one of them C# <c>unmanaged</c> — so
/// one well-formed 60-byte packet from any server the player joined wrote an attacker-chosen
/// 64-bit pointer into a component Burst code dereferences. The rule is now an <em>allowlist with
/// preservation</em>, and it is here rather than behind an <c>EntityManager</c> so this file can
/// drive it against real structs.</para>
/// </summary>
public class PrefabEditShapeTests
{
    // ---- fixture types -----------------------------------------------------------------------
    // Read through Marshal, so most of these are never assigned in C#.
#pragma warning disable CS0649

    /// <summary>Plain numbers, a bool and an enum: everything travels.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PlainStats
    {
        public int MaxAmount;
        public float Weight;
        public Tier Grade;
        public bool Stackable;
    }

    private enum Tier { None = 0, High = 3 }

    /// <summary>The <c>Entity</c> shape: two ints, indistinguishable from data by layout alone.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Entity
    {
        public int Index;
        public int Version;
    }

    /// <summary>The <c>BlobAssetReference</c> shape: a struct wrapping a raw pointer.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BlobAssetReferenceData
    {
        public IntPtr Ptr;
    }

    /// <summary><c>ProjectM.ItemData</c>'s shape: real numbers next to an <c>Entity</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ItemDataLike
    {
        public int MaxAmount;
        public Entity Prefab;
        public float SilverValue;
    }

    /// <summary><c>CreateGameplayEventOnKill</c>'s shape: an id next to a blob pointer.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct EventOnKillLike
    {
        public int Id;
        public BlobAssetReferenceData Condition;
    }

    /// <summary>Nothing but a handle: refused outright, on both sides.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BehaviourTreeLike
    {
        public BlobAssetReferenceData Blob;
    }

    /// <summary>A bare pointer field, the cheapest version of the attack.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RawPointerHolder
    {
        public int Before;
        public IntPtr Pointer;
        public int After;
    }

    /// <summary>A struct whose reference-shaped field is two levels down.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NestedHolder
    {
        public int Head;
        public ItemDataLike Inner;
        public int Tail;
    }

#pragma warning restore CS0649

    // ---- the classifier ------------------------------------------------------------------------

    [Fact]
    public void A_struct_of_numbers_a_bool_and_an_enum_travels_whole()
    {
        Assert.True(PrefabEditShapeReflect.TryDescribe(typeof(PlainStats), out var shape, out var reason));
        Assert.Equal("", reason);
        Assert.Equal(Marshal.SizeOf(typeof(PlainStats)), shape.Size);
        Assert.True(shape.WholeValue);
        Assert.Empty(shape.PreservedFields);
    }

    [Fact]
    public void An_Entity_field_is_reference_shaped_even_though_it_is_only_two_ints()
    {
        Assert.True(PrefabEditShapeReflect.TryDescribe(typeof(ItemDataLike), out var shape, out _));
        Assert.False(shape.WholeValue);
        Assert.Equal(new[] { "Prefab" }, shape.PreservedFields);

        var range = Assert.Single(shape.Preserved);
        Assert.Equal((int)Marshal.OffsetOf(typeof(ItemDataLike), nameof(ItemDataLike.Prefab)), range.Offset);
        Assert.Equal(Marshal.SizeOf(typeof(Entity)), range.Length);
    }

    [Fact]
    public void A_pointer_field_and_a_blob_reference_are_both_reference_shaped()
    {
        Assert.True(PrefabEditShapeReflect.TryDescribe(typeof(RawPointerHolder), out var raw, out _));
        Assert.Equal(new[] { "Pointer" }, raw.PreservedFields);

        Assert.True(PrefabEditShapeReflect.TryDescribe(typeof(EventOnKillLike), out var blob, out _));
        Assert.Equal(new[] { "Condition" }, blob.PreservedFields);
        Assert.Equal(
            (int)Marshal.OffsetOf(typeof(EventOnKillLike), nameof(EventOnKillLike.Condition)),
            Assert.Single(blob.Preserved).Offset);
    }

    [Fact]
    public void A_type_that_is_nothing_but_a_handle_is_refused_outright()
    {
        Assert.False(PrefabEditShapeReflect.TryDescribe(typeof(BehaviourTreeLike), out _, out var reason));
        Assert.Contains("every byte is reference-shaped", reason);
        Assert.Contains("Blob", reason);
    }

    [Fact]
    public void A_reference_shaped_field_two_levels_down_is_found_at_its_absolute_offset()
    {
        Assert.True(PrefabEditShapeReflect.TryDescribe(typeof(NestedHolder), out var shape, out _));
        Assert.Equal(new[] { "Inner.Prefab" }, shape.PreservedFields);

        int expected = (int)Marshal.OffsetOf(typeof(NestedHolder), nameof(NestedHolder.Inner))
            + (int)Marshal.OffsetOf(typeof(ItemDataLike), nameof(ItemDataLike.Prefab));
        Assert.Equal(expected, Assert.Single(shape.Preserved).Offset);
    }

    [Fact]
    public void The_name_rule_matches_the_last_segment_and_the_family_prefixes()
    {
        Assert.True(PrefabEditShape.IsReferenceShapedName("Unity.Entities.Entity"));
        Assert.True(PrefabEditShape.IsReferenceShapedName("ProjectM.ModifiableEntity"));
        Assert.True(PrefabEditShape.IsReferenceShapedName("Stunlock.Core.PrefabGUID"));
        Assert.True(PrefabEditShape.IsReferenceShapedName("Unity.Entities.BlobAssetReference`1[[X]]"));
        Assert.True(PrefabEditShape.IsReferenceShapedName("Unity.Collections.FixedString64Bytes"));
        Assert.True(PrefabEditShape.IsReferenceShapedName("Unity.Collections.NativeArray`1"));
        Assert.True(PrefabEditShape.IsReferenceShapedName("System.IntPtr"));
        Assert.True(PrefabEditShape.IsReferenceShapedName("A.B+Entity"));

        Assert.False(PrefabEditShape.IsReferenceShapedName("ProjectM.ItemData"));
        Assert.False(PrefabEditShape.IsReferenceShapedName("System.Int32"));
        Assert.False(PrefabEditShape.IsReferenceShapedName("ProjectM.Health"));
    }

    [Fact]
    public void A_field_the_adapter_cannot_name_is_preserved_rather_than_written()
    {
        var fields = new[]
        {
            new PrefabEditFieldFact("Known", "System.Int32", 0, 4, PrefabEditFieldForm.Numeric),
            new PrefabEditFieldFact("Mystery", "Something.Odd", 4, 4, PrefabEditFieldForm.Unknown),
        };
        Assert.True(PrefabEditShape.TryClassify("T", 8, fields, out var shape, out _));
        Assert.Equal(new[] { "Mystery" }, shape.PreservedFields);
    }

    [Fact]
    public void A_nested_struct_the_adapter_could_not_open_is_preserved_whole()
    {
        var fields = new[]
        {
            new PrefabEditFieldFact("Opaque", "Some.Struct", 0, 8, PrefabEditFieldForm.Struct, null),
            new PrefabEditFieldFact("N", "System.Int32", 8, 4, PrefabEditFieldForm.Numeric),
        };
        Assert.True(PrefabEditShape.TryClassify("T", 12, fields, out var shape, out _));
        Assert.Equal(new[] { "Opaque" }, shape.PreservedFields);
        Assert.Equal(8, Assert.Single(shape.Preserved).Length);
    }

    [Fact]
    public void A_field_that_does_not_fit_the_type_refuses_it()
    {
        var fields = new[] { new PrefabEditFieldFact("Over", "System.Int32", 6, 4, PrefabEditFieldForm.Numeric) };
        Assert.False(PrefabEditShape.TryClassify("T", 8, fields, out _, out var reason));
        Assert.Contains("field Over occupies 6..10", reason);
    }

    [Fact]
    public void Overlapping_preserved_ranges_are_merged()
    {
        var merged = PrefabEditShape.Merge(new[]
        {
            new PrefabEditByteRange(8, 4),
            new PrefabEditByteRange(0, 4),
            new PrefabEditByteRange(2, 4),
        });
        Assert.Equal(2, merged.Length);
        Assert.Equal(0, merged[0].Offset);
        Assert.Equal(6, merged[0].Length);
        Assert.Equal(8, merged[1].Offset);
    }

    // ---- the mask ------------------------------------------------------------------------------

    [Fact]
    public void Preservation_takes_the_receivers_own_bytes_over_the_masked_ranges()
    {
        Assert.True(PrefabEditShapeReflect.TryDescribe(typeof(ItemDataLike), out var shape, out _));

        var server = Marshalled(new ItemDataLike
        {
            MaxAmount = 99,
            SilverValue = 5f,
            Prefab = new Entity { Index = 0x41414141, Version = 0x41414141 },
        });
        var mine = Marshalled(new ItemDataLike
        {
            MaxAmount = 1,
            SilverValue = 1f,
            Prefab = new Entity { Index = 1234, Version = 7 },
        });

        Assert.True(PrefabEditShape.TryPreserve(server, mine, shape, out var final, out var reason));
        Assert.Equal("", reason);

        var written = Unmarshal<ItemDataLike>(final);
        Assert.Equal(99, written.MaxAmount);          // the server's number
        Assert.Equal(5f, written.SilverValue);        // the server's number
        Assert.Equal(1234, written.Prefab.Index);     // this client's entity, not 0x41414141
        Assert.Equal(7, written.Prefab.Version);
    }

    [Fact]
    public void A_record_is_refused_when_the_receiver_cannot_read_its_own_bytes_to_preserve()
    {
        Assert.True(PrefabEditShapeReflect.TryDescribe(typeof(ItemDataLike), out var shape, out _));
        var server = Marshalled(default(ItemDataLike));
        Assert.False(PrefabEditShape.TryPreserve(server, null, shape, out _, out var reason));
        Assert.Contains("reference-shaped range(s) cannot be preserved", reason);
    }

    [Fact]
    public void A_whole_value_type_is_passed_through_untouched()
    {
        Assert.True(PrefabEditShapeReflect.TryDescribe(typeof(PlainStats), out var shape, out _));
        var bytes = Marshalled(new PlainStats { MaxAmount = 12 });
        Assert.True(PrefabEditShape.TryPreserve(bytes, null, shape, out var final, out _));
        Assert.Same(bytes, final);
    }

    // ---- the type rules the resolver runs before the shape walk --------------------------------

    [Fact]
    public void The_type_rules_refuse_identity_generic_and_managed_bearing_types()
    {
        Assert.True(PrefabEditWire.TryAcceptType(typeof(PlainStats), out var reason));
        Assert.Equal("", reason);

        Assert.False(PrefabEditWire.TryAcceptType(null, out reason));
        Assert.Contains("no loaded", reason);

        Assert.False(PrefabEditWire.TryAcceptType(typeof(string), out reason));
        Assert.Equal("not a value type", reason);

        Assert.False(PrefabEditWire.TryAcceptType(typeof(Nullable<>), out reason));
        Assert.Equal("generic type", reason);

        Assert.False(PrefabEditWire.TryAcceptType(typeof(WithManagedField), out reason));
        Assert.Contains("managed reference", reason);
    }

#pragma warning disable CS0649
    private struct WithManagedField
    {
        public string Text;
        public int N;
    }
#pragma warning restore CS0649

    // ---- the apply ladder ----------------------------------------------------------------------

    [Fact]
    public void The_ladder_refuses_per_record_and_the_rest_of_the_packet_still_applies()
    {
        var site = new RecordingSite();
        site.Values["Good"] = 1;
        site.Values["Throws"] = 1;
        site.Values["WrongSize"] = 1;
        site.Refused.Add("Unresolvable");
        site.Throws.Add("Throws");

        var applied = PrefabEditApplyDecision.Apply(
            new[]
            {
                Rec("N.Unresolvable", 1),
                Rec("N.Missing", 2),
                new PrefabEditComponentBytes("N.WrongSize", new byte[] { 1, 2 }),
                Rec("N.Throws", 3),
                Rec("N.Good", 4),
            },
            site,
            (name, reason) => site.Log.Add(name + ": " + reason));

        Assert.Equal(new[] { "Good" }, applied);
        Assert.Equal(4, site.Log.Count);
        Assert.Contains(site.Log, l => l.StartsWith("N.Unresolvable", StringComparison.Ordinal));
        Assert.Contains(site.Log, l => l.Contains("does not carry it"));
        Assert.Contains(site.Log, l => l.Contains("size mismatch"));
        Assert.Contains(site.Log, l => l.Contains("write threw"));
        Assert.Equal(4, site.Values["Good"]);
    }

    [Fact]
    public void The_ladder_asks_in_the_pinned_order_and_stops_at_the_first_refusal()
    {
        var site = new RecordingSite();
        // No component: the mask, the baseline and the write must never be reached, and
        // CurrentBytes must not be consulted for a record that will not be written.
        PrefabEditApplyDecision.Apply(new[] { Rec("N.Absent", 1) }, site);
        Assert.Equal(new[] { "accept:N.Absent", "has:Absent" }, site.Calls);

        site.Calls.Clear();
        site.Values["Present"] = 0;
        site.Shapes["Present"] = Preserving("N.Present");
        PrefabEditApplyDecision.Apply(new[] { Rec("N.Present", 5) }, site);
        Assert.Equal(
            new[] { "accept:N.Present", "has:Present", "current:Present", "baseline:Present", "write:Present" },
            site.Calls);
    }

    [Fact]
    public void A_masked_record_reports_the_preserved_fields_once_for_the_log()
    {
        var site = new RecordingSite();
        site.Values["Present"] = 0;
        site.Shapes["Present"] = Preserving("N.Present");
        var preserved = new List<string>();
        PrefabEditApplyDecision.Apply(
            new[] { Rec("N.Present", 5) }, site, null, shape => preserved.AddRange(shape.PreservedFields));
        Assert.Equal(new[] { "Handle" }, preserved);
    }

    [Fact]
    public void A_whole_value_record_never_reads_the_receivers_own_bytes()
    {
        var site = new RecordingSite();
        site.Values["Plain"] = 0;
        var preserved = new List<string>();
        PrefabEditApplyDecision.Apply(
            new[] { Rec("N.Plain", 9) }, site, null, shape => preserved.AddRange(shape.PreservedFields));
        Assert.DoesNotContain("current:Plain", site.Calls);
        Assert.Empty(preserved);
        Assert.Equal(9, site.Values["Plain"]);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static PrefabEditComponentBytes Rec(string name, int value) =>
        new(name, BitConverter.GetBytes(value));

    /// <summary>A four-byte component whose last two bytes belong to the receiver.</summary>
    private static PrefabEditTypeShape Preserving(string fullName) =>
        new(fullName, 4, new[] { new PrefabEditByteRange(2, 2) }, new[] { "Handle" }, fullName);

    private static byte[] Marshalled<T>(T value) where T : struct
    {
        Assert.True(PrefabEditWire.TryMarshalBytes(value, typeof(T), out var bytes, out var error), error);
        return bytes;
    }

    private static T Unmarshal<T>(byte[] bytes) where T : struct
    {
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            return (T)Marshal.PtrToStructure(buffer, typeof(T))!;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>An apply site that records the ladder's calls in order.</summary>
    private sealed class RecordingSite : IPrefabEditApplySite
    {
        public readonly Dictionary<string, int> Values = new(StringComparer.Ordinal);
        public readonly Dictionary<string, PrefabEditTypeShape> Shapes = new(StringComparer.Ordinal);
        public readonly HashSet<string> Refused = new(StringComparer.Ordinal);
        public readonly HashSet<string> Throws = new(StringComparer.Ordinal);
        public readonly List<string> Calls = new();
        public readonly List<string> Log = new();

        public bool TryAcceptType(string? typeFullName, out PrefabEditTypeShape shape, out string reason)
        {
            Calls.Add("accept:" + typeFullName);
            string shortName = PrefabEditWire.ShortTypeName(typeFullName);
            if (Refused.Contains(shortName))
            {
                shape = null!;
                reason = "refused by the test";
                return false;
            }
            shape = Shapes.TryGetValue(shortName, out var s)
                ? s
                : new PrefabEditTypeShape(typeFullName ?? "", 4, Array.Empty<PrefabEditByteRange>(), Array.Empty<string>(), shortName);
            reason = "";
            return true;
        }

        public bool HasComponent(PrefabEditTypeShape shape, out string reason)
        {
            Calls.Add("has:" + shape.ShortName);
            if (Values.ContainsKey(shape.ShortName))
            {
                reason = "";
                return true;
            }
            reason = "the prefab entity does not carry it";
            return false;
        }

        public byte[]? CurrentBytes(PrefabEditTypeShape shape)
        {
            Calls.Add("current:" + shape.ShortName);
            return Values.TryGetValue(shape.ShortName, out int v) ? BitConverter.GetBytes(v) : null;
        }

        public void CaptureBaseline(PrefabEditTypeShape shape, string shortName)
        {
            Calls.Add("baseline:" + shortName);
        }

        public void Write(PrefabEditTypeShape shape, byte[] bytes)
        {
            Calls.Add("write:" + shape.ShortName);
            if (Throws.Contains(shape.ShortName))
                throw new InvalidOperationException("the test's writer refuses " + shape.ShortName);
            Values[shape.ShortName] = BitConverter.ToInt32(bytes, 0);
        }
    }
}
