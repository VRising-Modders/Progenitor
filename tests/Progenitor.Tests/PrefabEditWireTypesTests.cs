using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// <b>The network boundary for <c>PrfE</c> is a list, not a classifier</b> (job FEDW4, R52
    /// finding 1).
    ///
    /// <para>R52 measured what the shape rule alone admits in this game's interop:
    /// <c>Unity.Entities.TypeIndex</c> is <c>struct { int Value; }</c> and is carried by 4,585
    /// structs; <c>Unity.Entities.RuntimeBlobHeaderRef</c> keeps a blob-asset pointer in a plain
    /// <c>long</c>; 351 raw <c>*Index</c> / <c>*Offset</c> integer fields are quantities by shape and
    /// addresses by meaning; and 1,916 structs surface no fields at all, for which the classifier
    /// used to return "write every byte from the wire" — the exact inverse of its own docstring. So
    /// the boundary is <see cref="PrefabEditWireTypes"/>: an explicit list of components Core vouches
    /// for, with a per-field policy, checked on both ends. This file drives that list, its agreement
    /// check against the classifier, and the fail-closed cases.</para>
    /// </summary>
    public class PrefabEditWireTypesTests
    {
    #pragma warning disable CS0649

        // ---- fixtures mirroring the interop declarations verbatim ---------------------------------
        // Il2CppInterop emits [StructLayout(LayoutKind.Explicit)] with the game's own native field
        // offsets and [MarshalAs(UnmanagedType.U1)] on every bool. These fixtures copy those
        // declarations field for field, so Marshal.SizeOf over them is the same number the live
        // client measures — which is how the sizes pinned in the list are checked without a game.

        /// <summary>Named as the game names it: the shape rule matches identity types by name.</summary>
        private struct Entity { public int Index; public int Version; }

        private struct PrefabGUID { public int GuidHash; }

        // The enums are mirrored member for member as well as field for field: since FEDW5 the value
        // domain of an enum field is read from the receiver's own copy of the enum, so a fixture with
        // one member would be testing a domain no build has.

        /// <summary>ProjectM.ItemType (server metadata). Eleven dense members.</summary>
        private enum FakeItemType
        {
            Stackable = 0, Consumable = 1, Equippable = 2, ItemBuilding = 3, Memory = 4, Tech = 5,
            OrbOfKnowledge = 6, VBloodEssence = 7, Jewel = 8, Bag = 9, Saddle = 10,
        }

        /// <summary>ProjectM.ItemCategory (server metadata). No value policy is expressible.</summary>
        [Flags]
        private enum FakeItemCategory : long
        {
            NONE = 0, Weapon = 1, Armor = 2, Gem = 4, BloodBound = 8, SoulBound = 256,
            Locked = -2147483648, ALL = -1,
        }

        /// <summary>ProjectM.LifeTimeEndAction (server metadata).</summary>
        private enum FakeLifeTimeEndAction { None = 0, Kill = 1, Destroy = 2, RemoveBuffStack = 3 }

        /// <summary>ProjectM.DisableAggroBuffMode (server metadata). [Flags], so 3 is legal.</summary>
        [Flags]
        private enum FakeAggroMode { None = 0, OthersDontAttackTarget = 1, TargetDontAttackOthers = 2 }

        /// <summary>ProjectM.ModificationType (server metadata). Nine of 256 byte values.</summary>
        private enum FakeModificationType : byte
        {
            Set = 0, SetMin = 1, SetMax = 2, Add = 3, Multiply = 4, MultiplyBaseAdd = 5,
            AddToBase = 6, BitwiseOR = 7, BitwiseNOT = 8,
        }

        private struct FakeModificationId { public int Id; }

        /// <summary>ProjectM.ItemData (server metadata / interop ProjectM.Shared).</summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct ItemDataShape
        {
            [FieldOffset(0)] public float SilverValue;
            [FieldOffset(4)] public Entity Entity;
            [FieldOffset(12)] public PrefabGUID ItemTypeGUID;
            [FieldOffset(16)] public PrefabGUID DropItemPrefab;
            [FieldOffset(20)] public PrefabGUID DropItemArc;
            [FieldOffset(24)] public int MaxAmount;
            [FieldOffset(28)] public FakeItemType ItemType;
            [FieldOffset(32)] public FakeItemCategory ItemCategory;
            [FieldOffset(40)] [MarshalAs(UnmanagedType.U1)] public bool RemoveOnConsume;
            [FieldOffset(42)] public ushort SortOrder;
        }

        /// <summary>ProjectM.LifeTime (server metadata).</summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct LifeTimeShape
        {
            [FieldOffset(0)] public float Duration;
            [FieldOffset(4)] public FakeLifeTimeEndAction EndAction;
        }

        /// <summary>ProjectM.BuffModificationFlagData (server metadata).</summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct BuffModificationFlagDataShape
        {
            [FieldOffset(0)] public long ModificationTypes;
            [FieldOffset(8)] public FakeModificationId ModificationId;
        }

        /// <summary>ProjectM.DisableAggroBuff (server metadata).</summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct DisableAggroBuffShape
        {
            [FieldOffset(0)] public FakeAggroMode Mode;
        }

        /// <summary>ProjectM.ModifyBloodDrainBuff (server metadata).</summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct ModifyBloodDrainBuffShape
        {
            [FieldOffset(0)] public float BloodValue;
            [FieldOffset(4)] public float BloodIdleValue;
            [FieldOffset(8)] public FakeModificationId ModificationId;
            [FieldOffset(12)] public FakeModificationId IgnoreIdleDrainModId;
            [FieldOffset(16)] public FakeModificationId ModificationIdleId;
            [FieldOffset(20)] public FakeModificationType ModificationType;
            [FieldOffset(21)] public FakeModificationType ModificationIdleType;
            [FieldOffset(24)] public int ModificationPriority;
            [FieldOffset(28)] public int ModificationIdlePriority;
            [FieldOffset(32)] [MarshalAs(UnmanagedType.U1)] public bool AffectBloodValue;
            [FieldOffset(33)] [MarshalAs(UnmanagedType.U1)] public bool AffectIdleBloodValue;
            [FieldOffset(34)] [MarshalAs(UnmanagedType.U1)] public bool IgnoreIdleDrainWhileActive;
        }

    #pragma warning restore CS0649

        private static readonly (string Name, Type Fixture)[] Seeded =
        {
            ("ProjectM.ItemData", typeof(ItemDataShape)),
            ("ProjectM.LifeTime", typeof(LifeTimeShape)),
            ("ProjectM.BuffModificationFlagData", typeof(BuffModificationFlagDataShape)),
            ("ProjectM.DisableAggroBuff", typeof(DisableAggroBuffShape)),
            ("ProjectM.ModifyBloodDrainBuff", typeof(ModifyBloodDrainBuffShape)),
        };

        // ---- the list is the boundary --------------------------------------------------------------

        [Fact]
        public void The_list_is_exactly_the_components_the_sample_edits()
        {
            Assert.Equal(
                new[]
                {
                    "ProjectM.ItemData",
                    "ProjectM.LifeTime",
                    "ProjectM.BuffModificationFlagData",
                    "ProjectM.DisableAggroBuff",
                    "ProjectM.ModifyBloodDrainBuff",
                },
                PrefabEditWireTypes.Names());
        }

        /// <summary>
        /// The component R51 built its proof-of-concept from, and the two integer-encoded handles R52
        /// measured, are refused by the list before any shape question is asked.
        /// </summary>
        [Theory]
        [InlineData("ProjectM.CreateGameplayEventOnKill")]
        [InlineData("ProjectM.StaticTransformIndex")]
        [InlineData("Unity.Entities.SceneSectionData")]
        [InlineData("ProjectM.Health")]
        [InlineData("")]
        public void A_type_that_is_not_on_the_list_is_refused_and_the_reason_names_the_list(string name)
        {
            Assert.False(PrefabEditWireTypes.IsVouched(name));
            Assert.False(PrefabEditWireTypes.TryVouch(name, null, out string reason));
            Assert.Contains("not on Core's wire-safe list", reason);
            Assert.Contains("PrefabEditWireTypes", reason);
            Assert.Contains("docs/edits-on-the-wire.md", reason);
        }

        /// <summary>
        /// The sending end: no reader is registered for a component Core does not vouch for, so the
        /// server never serializes it, let alone ships it.
        /// </summary>
        [Fact]
        public void The_sender_registers_no_reader_for_a_type_that_is_not_on_the_list()
        {
            var layer = new PrefabEditWireLayer();
            Assert.False(layer.RegisterReader(1, "ProjectM.CreateGameplayEventOnKill", _ => new byte[8]));
            Assert.Empty(layer.ReaderTypes(1));

            Assert.True(layer.RegisterReader(1, "ProjectM.ItemData", _ => new byte[48]));
            Assert.Equal(new[] { "ProjectM.ItemData" }, layer.ReaderTypes(1));
        }

        /// <summary>
        /// The receiving end: the same list, through the ladder the live client runs. The site here
        /// answers exactly as <c>LiveApplySite</c> does — by asking <see cref="PrefabEditWireTypes"/>.
        /// </summary>
        [Fact]
        public void The_receiver_refuses_a_record_whose_type_is_not_on_the_list()
        {
            var site = new ListSite();
            var applied = PrefabEditApplyDecision.Apply(
                new[]
                {
                    new PrefabEditComponentBytes("ProjectM.CreateGameplayEventOnKill", new byte[60]),
                    new PrefabEditComponentBytes("ProjectM.LifeTime", new byte[8]),
                },
                site,
                (name, reason) => site.Refusals.Add(name + ": " + reason));

            Assert.Equal(new[] { "LifeTime" }, applied);
            string refusal = Assert.Single(site.Refusals);
            Assert.Contains("CreateGameplayEventOnKill", refusal);
            Assert.Contains("not on Core's wire-safe list", refusal);
        }

        /// <summary>
        /// Both ends really do consult the list, in the live files the suite cannot instantiate: the
        /// one type-resolution choke point the server's framing and the client's apply share, and the
        /// reader registration on the send side.
        /// </summary>
        [Fact]
        public void Both_ends_go_through_the_list_in_the_live_code()
        {
            string apply = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabEditWireApply.cs");
            string resolve = SourcePin.MethodBody(
                apply,
                "internal static bool TryResolveType(");
            SourcePin.AssertUncommented(resolve, "PrefabEditWireTypes.TryGet(fullName, out var vouched)");
            SourcePin.AssertUncommented(resolve, "PrefabEditWireTypes.Agrees(vouched, described, out reason)");

            string layer = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabEditWireLayer.cs");
            SourcePin.AssertUncommented(layer, "if (!PrefabEditWireTypes.IsVouched(typeFullName)) return false;");
        }

        // ---- the classifier must agree with the policy ---------------------------------------------

        /// <summary>
        /// Every seeded entry, against a fixture that copies the interop declaration field for field:
        /// the pinned size is the size the marshaler measures, and the classifier preserves exactly
        /// the fields the policy names.
        /// </summary>
        [Fact]
        public void Every_seeded_policy_matches_the_classifier_over_the_declared_layout()
        {
            for (int i = 0; i < Seeded.Length; i++)
            {
                (string name, var fixture) = Seeded[i];
                Assert.True(PrefabEditWireTypes.TryGet(name, out var entry), name);
                Assert.True(
                    PrefabEditShapeReflect.TryDescribe(fixture, out var shape, out string why),
                    name + ": " + why);
                Assert.Equal(entry.ExpectedSize, Marshal.SizeOf(fixture));
                Assert.True(PrefabEditWireTypes.Agrees(entry, shape, out string reason), name + ": " + reason);
            }
        }

        /// <summary>
        /// The sizes, spelled out. Four were measured live (evidence 191); <c>ModifyBloodDrainBuff</c>
        /// was derived from the interop declaration, and this is where the derivation is checked.
        /// </summary>
        [Theory]
        [InlineData("ProjectM.ItemData", 48)]
        [InlineData("ProjectM.LifeTime", 8)]
        [InlineData("ProjectM.BuffModificationFlagData", 16)]
        [InlineData("ProjectM.DisableAggroBuff", 4)]
        [InlineData("ProjectM.ModifyBloodDrainBuff", 36)]
        public void The_pinned_size_is_what_the_marshaller_measures(string name, int size)
        {
            Assert.True(PrefabEditWireTypes.TryGet(name, out var entry));
            Assert.Equal(size, entry.ExpectedSize);
            for (int i = 0; i < Seeded.Length; i++)
                if (Seeded[i].Name == name) Assert.Equal(size, Marshal.SizeOf(Seeded[i].Fixture));
        }

        [Fact]
        public void ItemData_keeps_its_entity_and_its_three_guids_and_takes_the_rest()
        {
            Assert.True(PrefabEditWireTypes.TryGet("ProjectM.ItemData", out var entry));
            Assert.Equal(
                new[] { "Entity", "ItemTypeGUID", "DropItemPrefab", "DropItemArc" },
                entry.PreserveFields);
            Assert.True(PrefabEditShapeReflect.TryDescribe(typeof(ItemDataShape), out var shape, out _));
            Assert.Equal(entry.PreserveFields, shape.PreservedFields);
            // 4..24: the Entity and the three guids, one contiguous run.
            var range = Assert.Single(shape.Preserved);
            Assert.Equal(4, range.Offset);
            Assert.Equal(20, range.Length);
        }

        /// <summary>
        /// A bool is four marshaled bytes only by default. Every interop struct here declares
        /// <c>MarshalAs(U1)</c>, and measuring the three packed bools of
        /// <c>ModifyBloodDrainBuff</c> as four bytes each ran the last one past the end of its own
        /// 36-byte component, which the fit check refuses.
        /// </summary>
        [Fact]
        public void A_bool_marshalled_as_one_byte_is_measured_as_one_byte()
        {
            var field = typeof(ModifyBloodDrainBuffShape).GetField("IgnoreIdleDrainWhileActive");
            Assert.Equal(1, PrefabEditShapeReflect.MarshalledBoolSize(field));
            Assert.Equal(4, PrefabEditShapeReflect.MarshalledBoolSize(null));
            Assert.True(PrefabEditShapeReflect.TryDescribe(typeof(ModifyBloodDrainBuffShape), out var shape, out string why), why);
            Assert.Equal(36, shape.Size);
            Assert.Empty(shape.PreservedFields);
        }

        // ---- the list fails closed against a game update -------------------------------------------

        [Fact]
        public void A_size_the_build_disagrees_with_refuses_the_type_as_a_stale_list()
        {
            var entry = new PrefabEditWireType("ProjectM.LifeTime", 8, new[] { "Duration", "EndAction" });
            var shape = new PrefabEditTypeShape(
                "ProjectM.LifeTime", 12, Array.Empty<PrefabEditByteRange>(), Array.Empty<string>(), null,
                new[] { "Duration", "EndAction" });
            Assert.False(PrefabEditWireTypes.Agrees(entry, shape, out string reason));
            Assert.Contains("wire-safe list is stale", reason);
            Assert.Contains("the list says 8 byte(s), this build measures 12", reason);
        }

        [Fact]
        public void A_field_the_build_added_refuses_the_type_as_a_stale_list()
        {
            var entry = new PrefabEditWireType("ProjectM.LifeTime", 8, new[] { "Duration", "EndAction" });
            var shape = new PrefabEditTypeShape(
                "ProjectM.LifeTime", 8, Array.Empty<PrefabEditByteRange>(), Array.Empty<string>(), null,
                new[] { "Duration", "EndAction", "Flags" });
            Assert.False(PrefabEditWireTypes.Agrees(entry, shape, out string reason));
            Assert.Contains("wire-safe list is stale", reason);
            Assert.Contains("Flags", reason);
        }

        /// <summary>
        /// The disagreement that matters most: the list calls a field a value, this build's classifier
        /// calls it reference-shaped (a number that became a handle). Refused, not written.
        /// </summary>
        [Fact]
        public void A_value_field_the_shape_rule_calls_reference_shaped_refuses_the_type()
        {
            var entry = new PrefabEditWireType("ProjectM.Thing", 8, new[] { "Count", "Slot" });
            var shape = new PrefabEditTypeShape(
                "ProjectM.Thing",
                8,
                new[] { new PrefabEditByteRange(4, 4) },
                new[] { "Slot" },
                null,
                new[] { "Count", "Slot" });
            Assert.False(PrefabEditWireTypes.Agrees(entry, shape, out string reason));
            Assert.Contains("the list preserves [], the shape rule preserves [Slot]", reason);
        }

        // ---- the classifier's own fail-closed cases (R52 finding 1) --------------------------------

        [Fact]
        public void A_struct_whose_fields_reflection_cannot_enumerate_is_refused()
        {
            Assert.False(PrefabEditShape.TryClassify("T", 16, null, out _, out string reason));
            Assert.Contains("could not enumerate", reason);
        }

        [Fact]
        public void A_struct_reflection_reports_no_fields_for_is_refused_rather_than_written_whole()
        {
            Assert.False(
                PrefabEditShape.TryClassify("T", 16, Array.Empty<PrefabEditFieldFact>(), out _, out string reason));
            Assert.Contains("no fields for a 16-byte component", reason);

            // A genuine one-byte tag is not a threat and is still allowed.
            Assert.True(PrefabEditShape.TryClassify("Tag", 1, Array.Empty<PrefabEditFieldFact>(), out var tag, out _));
            Assert.True(tag.WholeValue);
        }

        [Fact]
        public void An_integer_encoded_handle_is_preserved_by_its_type_name()
        {
            Assert.True(PrefabEditShape.IsReferenceShapedName("Unity.Entities.TypeIndex"));
            Assert.True(PrefabEditShape.IsReferenceShapedName("Unity.Entities.RuntimeBlobHeaderRef"));
            Assert.True(PrefabEditShape.IsReferenceShapedName("Unity.Collections.AllocatorManager+AllocatorHandle"));
            Assert.True(PrefabEditShape.IsReferenceShapedName("Unity.Physics.ColliderKey"));
            Assert.False(PrefabEditShape.IsReferenceShapedName("ProjectM.ItemData"));
            Assert.False(PrefabEditShape.IsReferenceShapedName("ProjectM.ModificationId"));

            var fields = new[]
            {
                new PrefabEditFieldFact("Amount", "System.Int32", 0, 4, PrefabEditFieldForm.Numeric),
                new PrefabEditFieldFact("ComponentTypeIndex", "Unity.Entities.TypeIndex", 4, 4, PrefabEditFieldForm.Struct,
                    new[] { new PrefabEditFieldFact("Value", "System.Int32", 0, 4, PrefabEditFieldForm.Numeric) }),
            };
            Assert.True(PrefabEditShape.TryClassify("T", 8, fields, out var shape, out _));
            Assert.Equal(new[] { "ComponentTypeIndex" }, shape.PreservedFields);
        }

        /// <summary>
        /// The 351 cases the type system cannot help with: a plain <c>int</c> whose <em>name</em> says
        /// it addresses something. Preserved, because a wrong index in Burst code with bounds checks
        /// off is the primitive R51 finding 1 was written about.
        /// </summary>
        [Fact]
        public void An_integer_field_named_like_an_index_is_preserved()
        {
            Assert.True(PrefabEditShape.IsReferenceShapedFieldName("Slot" + "Index"));
            Assert.True(PrefabEditShape.IsReferenceShapedFieldName("FieldOffset"));
            Assert.True(PrefabEditShape.IsReferenceShapedFieldName("BlackboardHandle"));
            Assert.True(PrefabEditShape.IsReferenceShapedFieldName("BlobRef"));
            Assert.False(PrefabEditShape.IsReferenceShapedFieldName("SortOrder"));
            Assert.False(PrefabEditShape.IsReferenceShapedFieldName("MaxAmount"));

            var fields = new[]
            {
                new PrefabEditFieldFact("MaxAmount", "System.Int32", 0, 4, PrefabEditFieldForm.Numeric),
                new PrefabEditFieldFact("StaticTransformIndex", "System.Int32", 4, 4, PrefabEditFieldForm.Numeric),
            };
            Assert.True(PrefabEditShape.TryClassify("T", 8, fields, out var shape, out _));
            Assert.Equal(new[] { "StaticTransformIndex" }, shape.PreservedFields);
            Assert.Equal(4, Assert.Single(shape.Preserved).Offset);
        }

        /// <summary>
        /// The sharpest measured case: a blob-asset pointer in a plain <c>long</c>, missed by the
        /// <c>Blob</c> prefix because Unity spelled the type <c>Runtime…</c> in the client
        /// metadata. Preserved now — and, being off the list, refused outright.
        /// </summary>
        [Fact]
        public void A_blob_pointer_stored_in_a_long_is_preserved_and_its_carrier_is_off_the_list()
        {
            var fields = new[]
            {
                new PrefabEditFieldFact(
                    "BlobHeader", "Unity.Entities.RuntimeBlobHeaderRef", 0, 8, PrefabEditFieldForm.Struct,
                    new[] { new PrefabEditFieldFact("m_BlobAssetRefStorage", "System.Int64", 0, 8, PrefabEditFieldForm.Numeric) }),
                new PrefabEditFieldFact("Count", "System.Int32", 8, 4, PrefabEditFieldForm.Numeric),
            };
            Assert.True(PrefabEditShape.TryClassify("Unity.Entities.SceneSectionData", 12, fields, out var shape, out _));
            Assert.Equal(new[] { "BlobHeader" }, shape.PreservedFields);
            Assert.False(PrefabEditWireTypes.IsVouched("Unity.Entities.SceneSectionData"));
        }

        // ---- the value domain (job FEDW5, R53 finding 2) -------------------------------------------

        /// <summary>
        /// Every policy in the seed resolves against a fixture that copies the interop declaration, and
        /// resolves to the field it names at the offset the marshaler measures. A policy naming a
        /// field, an enum or a width this build does not have is the case the domain must fail closed
        /// on, and it is two tests below.
        /// </summary>
        [Fact]
        public void Every_seeded_value_policy_resolves_against_the_declared_layout()
        {
            int policied = 0;
            for (int i = 0; i < Seeded.Length; i++)
            {
                (string name, var fixture) = Seeded[i];
                Assert.True(PrefabEditWireTypes.TryGet(name, out var entry));
                var domain = PrefabEditShapeReflect.BuildValueDomain(fixture, entry);
                Assert.Equal("", domain.Error);
                Assert.Equal(entry.ValuePolicies.Length, domain.Checks.Length);
                for (int c = 0; c < domain.Checks.Length; c++)
                {
                    var check = domain.Checks[c];
                    Assert.Equal((int)Marshal.OffsetOf(fixture, check.Policy.Field), check.Offset);
                    Assert.True(check.Size > 0, name + "." + check.Policy.Field);
                    Assert.NotEqual("", check.Policy.Reason);
                    // A policy that names an enum must have found members or bits on this build.
                    if (check.Policy.Rule == PrefabEditValueRule.EnumMember)
                        Assert.NotEmpty(check.Members);
                    if (check.Policy.Rule == PrefabEditValueRule.EnumBits
                        || check.Policy.Rule == PrefabEditValueRule.NamedEnumBits)
                        Assert.NotEqual(0, check.Mask);
                }
                policied += domain.Checks.Length;
                // The bytes a well-behaved server sends: the default value of every field.
                Assert.True(domain.TryAccept(Zeroes(fixture), out _, out string why), name + ": " + why);
            }
            Assert.Equal(15, policied);
        }

        /// <summary>
        /// The three dense enums R53 named, at the receiver, through the whole ladder: an undeclared
        /// member refuses that record and writes nothing, and the rest of the packet still applies.
        /// </summary>
        [Theory]
        [InlineData("ProjectM.ItemType", 0x7FFFFFFF)]
        [InlineData("ProjectM.LifeTimeEndAction", 99)]
        [InlineData("ProjectM.ModificationType", 255)]
        public void An_enum_value_this_build_does_not_declare_refuses_the_record(string enumName, int value)
        {
            var site = new FixtureSite();
            PrefabEditComponentBytes record;
            switch (enumName)
            {
                case "ProjectM.ItemType":
                    record = Record("ProjectM.ItemData", new ItemDataShape { ItemType = (FakeItemType)value });
                    break;
                case "ProjectM.LifeTimeEndAction":
                    record = Record("ProjectM.LifeTime", new LifeTimeShape { EndAction = (FakeLifeTimeEndAction)value });
                    break;
                default:
                    record = Record(
                        "ProjectM.ModifyBloodDrainBuff",
                        new ModifyBloodDrainBuffShape { ModificationType = (FakeModificationType)value });
                    break;
            }

            var applied = PrefabEditApplyDecision.Apply(
                new[] { record, Record("ProjectM.DisableAggroBuff", new DisableAggroBuffShape()) },
                site,
                (n, r) => site.Refusals.Add(n + ": " + r),
                null,
                (n, f, r) => site.ValueRefusals.Add(n + "|" + f + "|" + r));

            // The clean record still lands; only the poisoned one is refused.
            Assert.Equal(new[] { "DisableAggroBuff" }, applied);
            Assert.Empty(site.Refusals);
            string refusal = Assert.Single(site.ValueRefusals);
            Assert.Contains(enumName, refusal);
            Assert.Contains(value.ToString(CultureInfo.InvariantCulture), refusal);
            Assert.Single(site.Written);
            Assert.Equal("DisableAggroBuff", site.Written[0]);
        }

        /// <summary>
        /// A <c>[Flags]</c> field is bits, not members: a combination of declared bits is legal and an
        /// undeclared bit is not. Checking <c>DisableAggroBuffMode</c> for a "declared member" would
        /// refuse the perfectly ordinary <c>OthersDontAttackTarget | TargetDontAttackOthers</c>.
        /// </summary>
        [Theory]
        [InlineData(0, true)]
        [InlineData(1, true)]
        [InlineData(3, true)]
        [InlineData(4, false)]
        [InlineData(-1, false)]
        public void A_flags_field_takes_any_combination_of_declared_bits_and_nothing_else(int mode, bool ok)
        {
            var domain = Domain("ProjectM.DisableAggroBuff", typeof(DisableAggroBuffShape));
            byte[] bytes = Bytes(new DisableAggroBuffShape { Mode = (FakeAggroMode)mode });
            Assert.Equal(ok, domain.TryAccept(bytes, out string field, out string reason));
            if (!ok)
            {
                Assert.Equal("Mode", field);
                Assert.Contains("outside the 0x3", reason);
            }
        }

        /// <summary>
        /// The bare <c>long</c> bitfield: <c>BuffModificationFlagData.ModificationTypes</c> is not an
        /// enum-typed field, so its mask is resolved from <c>ProjectM.BuffModificationTypes</c> by
        /// name — and since FEDW6 that is the <em>only</em> source of the mask. The suite declares
        /// that enum (bottom of this file), so this drives the same path a live client drives, and
        /// the union of its single-bit members is asserted against the seed's dump-cited literal.
        /// </summary>
        [Theory]
        [InlineData(0L, true)]
        [InlineData(7L, true)]                       // what the Sample writes (Admin_Invulnerable_Buff)
        [InlineData(1L << 47, true)]                 // IsShapeshiftForm, the highest declared bit
        [InlineData(256L, false)]                    // bit 8: not declared on this build
        [InlineData(1024L, false)]                   // bit 10: not declared either
        [InlineData(1L << 48, false)]
        [InlineData(-1L, false)]                     // BuffModificationTypes.All, every bit
        public void A_named_bitfield_takes_only_the_bits_the_enum_declares(long flags, bool ok)
        {
            var domain = Domain("ProjectM.BuffModificationFlagData", typeof(BuffModificationFlagDataShape));
            byte[] bytes = Bytes(new BuffModificationFlagDataShape { ModificationTypes = flags });
            Assert.Equal(ok, domain.TryAccept(bytes, out string field, out string reason));
            if (!ok)
            {
                Assert.Equal("ModificationTypes", field);
                Assert.Contains("BuffModificationTypes", reason);
            }
            // The cross-check literal is the union of the single-bit members, nothing more — and
            // the mask actually enforced came from the enum, so the two agree and nothing drifted.
            Assert.Equal(0x0000FFFFFFFFFAFFL, PrefabEditWireTypes.DeclaredBuffModificationTypes);
            Assert.Equal(
                PrefabEditWireTypes.DeclaredBuffModificationTypes,
                domain.Checks[0].Mask);
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(1, true)]
        [InlineData(1073741823, true)]
        [InlineData(-1, false)]
        [InlineData(int.MinValue, false)]
        [InlineData(int.MaxValue, false)]
        public void A_bounded_integer_is_bounded_at_both_ends(int maxAmount, bool ok)
        {
            var domain = Domain("ProjectM.ItemData", typeof(ItemDataShape));
            byte[] bytes = Bytes(new ItemDataShape { MaxAmount = maxAmount });
            Assert.Equal(ok, domain.TryAccept(bytes, out string field, out string reason));
            if (!ok)
            {
                Assert.Equal("MaxAmount", field);
                Assert.Contains("outside [0..1073741823]", reason);
                Assert.Contains("stack bound", reason);
            }
        }

        /// <summary>
        /// Finite, and <b>not</b> clamped: <c>-1</c> is the vanilla "never expires" convention and the
        /// Sample writes it, so a range taken from the game's own <c>[SyncRangedFloat(0, 10000)]</c>
        /// would refuse a legitimate edit.
        /// </summary>
        [Theory]
        [InlineData(0f, true)]
        [InlineData(-1f, true)]
        [InlineData(99999f, true)]
        [InlineData(float.NaN, false)]
        [InlineData(float.PositiveInfinity, false)]
        [InlineData(float.NegativeInfinity, false)]
        public void A_float_field_must_be_finite_and_is_not_otherwise_clamped(float duration, bool ok)
        {
            var domain = Domain("ProjectM.LifeTime", typeof(LifeTimeShape));
            byte[] bytes = Bytes(new LifeTimeShape { Duration = duration });
            Assert.Equal(ok, domain.TryAccept(bytes, out string field, out string reason));
            if (!ok)
            {
                Assert.Equal("Duration", field);
                Assert.Contains("not a finite number", reason);
            }
        }

        /// <summary>A marshaled bool is one byte and exactly 0 or 1; 0x37 is not a bool this build wrote.</summary>
        [Fact]
        public void A_bool_byte_that_is_neither_zero_nor_one_refuses_the_record()
        {
            var domain = Domain("ProjectM.ModifyBloodDrainBuff", typeof(ModifyBloodDrainBuffShape));
            byte[] bytes = Bytes(new ModifyBloodDrainBuffShape { AffectBloodValue = true });
            Assert.True(domain.TryAccept(bytes, out _, out _));

            bytes[32] = 0x37;
            Assert.False(domain.TryAccept(bytes, out string field, out string reason));
            Assert.Equal("AffectBloodValue", field);
            Assert.Contains("neither 0 nor 1", reason);
        }

        /// <summary>
        /// The fields with no policy really do take anything, which is the honest half of the analysis:
        /// <c>ItemCategory</c> cannot be bit-checked (every bit 0..30 is declared, <c>Locked</c> is a
        /// sign-extended int inside a long enum, and <c>ALL = -1</c> is itself a member), so the doc
        /// table records the residual instead of pretending to a gate.
        /// </summary>
        [Fact]
        public void A_field_with_no_policy_takes_every_value_of_its_type()
        {
            var domain = Domain("ProjectM.ItemData", typeof(ItemDataShape));
            Assert.True(domain.TryAccept(
                Bytes(new ItemDataShape { ItemCategory = FakeItemCategory.ALL, SortOrder = ushort.MaxValue }),
                out _, out _));
            Assert.True(PrefabEditWireTypes.TryGet("ProjectM.ItemData", out var entry));
            Assert.Null(entry.PolicyFor("ItemCategory"));
            Assert.Null(entry.PolicyFor("SortOrder"));
            Assert.NotNull(entry.PolicyFor("MaxAmount"));
        }

        /// <summary>
        /// Fail closed, the same way the size and field-set gates do: a policy this build's type cannot
        /// satisfy refuses <em>every</em> record of that type rather than checking nothing.
        /// </summary>
        [Fact]
        public void A_policy_this_build_cannot_resolve_refuses_every_record_of_the_type()
        {
            var missingField = new PrefabEditWireType(
                "ProjectM.LifeTime", 8, new[] { "Duration", "EndAction" }, null, "fixture",
                new[] { PrefabEditFieldValuePolicy.Finite("DurationRenamedByAGameUpdate", "because") });
            var domain = PrefabEditShapeReflect.BuildValueDomain(typeof(LifeTimeShape), missingField);
            Assert.Contains("wire-safe list is stale", domain.Error);
            Assert.Contains("DurationRenamedByAGameUpdate", domain.Error);
            Assert.False(domain.TryAccept(new byte[8], out _, out string reason));
            Assert.Equal(domain.Error, reason);

            // A number that used to be an enum: the rule no longer fits the field.
            var wrongKind = new PrefabEditWireType(
                "ProjectM.LifeTime", 8, new[] { "Duration", "EndAction" }, null, "fixture",
                new[] { PrefabEditFieldValuePolicy.EnumMember("Duration", "ProjectM.LifeTimeEndAction", "because") });
            var domain2 = PrefabEditShapeReflect.BuildValueDomain(typeof(LifeTimeShape), wrongKind);
            Assert.Contains("carries an enum rule but is", domain2.Error);
            Assert.False(domain2.TryAccept(new byte[8], out _, out _));
        }

        /// <summary>
        /// <b>The one rule that used to fail open</b> (job FEDW6, R54 finding 3). A
        /// <c>NamedEnumBits</c> policy names its enum in a string, and an enum that does not
        /// resolve used to drop silently onto the literal beside it — no log, no marker, the
        /// domain no longer following the build for that field. It now refuses every record of the
        /// component, and the line says which enum and where it was looked for.
        /// </summary>
        [Fact]
        public void A_named_bit_enum_that_does_not_resolve_refuses_every_record_and_says_where_it_looked()
        {
            var gone = new PrefabEditWireType(
                "ProjectM.BuffModificationFlagData", 16,
                new[] { "ModificationTypes", "ModificationId" }, null, "fixture",
                new[]
                {
                    PrefabEditFieldValuePolicy.NamedEnumBits(
                        "ModificationTypes",
                        "ProjectM.BuffModificationTypesRenamedByAGameUpdate",
                        // No declaring assembly: this fixture is the "not on this build anywhere"
                        // case, and it must fail closed through the searched path too.
                        "",
                        PrefabEditWireTypes.DeclaredBuffModificationTypes,
                        "the seed's literal, which must not be what a build like this falls back on"),
                });

            var domain = PrefabEditShapeReflect.BuildValueDomain(typeof(BuffModificationFlagDataShape), gone);

            Assert.Contains("wire-safe list is stale", domain.Error);
            Assert.Contains("ProjectM.BuffModificationTypesRenamedByAGameUpdate", domain.Error);
            Assert.Contains("ModificationTypes", domain.Error);
            // The searched set is named, so the refusal says what the next reader has to check.
            Assert.Contains("searched", domain.Error);
            Assert.Contains("Progenitor.Tests", domain.Error);

            // Every record of the type, not just one with a hostile value: the bytes a
            // well-behaved server sends are refused too, which is what "fails closed" means.
            Assert.False(domain.TryAccept(Zeroes(typeof(BuffModificationFlagDataShape)), out _, out string reason));
            Assert.Equal(domain.Error, reason);
        }

        /// <summary>
        /// The dump-cited literal is a <b>cross-check</b>, never a fallback (job FEDW6, R54
        /// finding 3): a build whose enum declares a different mask is still checked against
        /// <em>its own</em> enum — the record applies — and the difference is said out loud once
        /// per Core generation so the seed's citation gets re-derived rather than quietly outlived.
        /// </summary>
        [Fact]
        public void A_resolved_mask_that_differs_from_the_seed_literal_is_enforced_and_logged_once()
        {
            PrefabEditShapeReflect.ResetCache();
            var stale = new PrefabEditWireType(
                "ProjectM.BuffModificationFlagData", 16,
                new[] { "ModificationTypes", "ModificationId" }, null, "fixture",
                new[]
                {
                    PrefabEditFieldValuePolicy.NamedEnumBits(
                        // No declaring assembly, so the resolution takes step 2 — the naming type's
                        // own assembly — and lands on the fixtures' copy of the enum whatever the
                        // test host has loaded beside it.
                        "ModificationTypes", "ProjectM.BuffModificationTypes", "", 0x7L,
                        "a literal three bits wide, as a seed written against an older build would be"),
                });

            var domain = PrefabEditShapeReflect.BuildValueDomain(typeof(BuffModificationFlagDataShape), stale);

            // Enforced: this build's enum, not the literal. Bit 47 is declared and is accepted
            // even though the stale literal stops at bit 2.
            Assert.Equal("", domain.Error);
            Assert.Equal(PrefabEditWireTypes.DeclaredBuffModificationTypes, domain.Checks[0].Mask);
            Assert.True(domain.TryAccept(
                Bytes(new BuffModificationFlagDataShape { ModificationTypes = 1L << 47 }), out _, out _));

            // Said once, and only once, whatever the number of resolves.
            PrefabEditShapeReflect.BuildValueDomain(typeof(BuffModificationFlagDataShape), stale);
            var notices = PrefabEditShapeReflect.DrainNotices();
            string only = Assert.Single(notices);
            Assert.Contains("value domain mask", only);
            Assert.Contains("ProjectM.BuffModificationFlagData.ModificationTypes", only);
            Assert.Contains("0xFFFFFFFFFAFF", only);
            Assert.Contains("0x7", only);
            Assert.Empty(PrefabEditShapeReflect.DrainNotices());

            // And the seed as it stands produces no such line: its literal agrees with the enum.
            PrefabEditShapeReflect.ResetCache();
            Assert.True(PrefabEditWireTypes.TryGet("ProjectM.BuffModificationFlagData", out var seeded));
            PrefabEditShapeReflect.BuildValueDomain(typeof(BuffModificationFlagDataShape), seeded);
            Assert.Empty(PrefabEditShapeReflect.DrainNotices());
        }

        /// <summary>
        /// <b>Two loaded assemblies declaring one enum full name refuse the component</b> (job
        /// FEDW7, R55 finding 6). FEDW6's search took the first match over
        /// <c>PrefabEditWire.IsAllowedAssemblyName</c>, which is a <c>StartsWith</c> prefix test —
        /// <c>ProjectMagic.dll</c> and <c>ProjectMonitor.dll</c> pass it — so assembly load order
        /// could decide the mask enforced against bytes another machine wrote, and the two sides of
        /// one build could enforce different masks because they have different plugin sets. There
        /// is no downstream backstop for this the way <c>Agrees</c> backstops a component type, so
        /// the searched path now collects every match and refuses when there is more than one.
        ///
        /// <para>Two assemblies declaring one name cannot be built inside one test assembly, so the
        /// search itself is the seam: <c>PrefabEditShapeReflect.EnumSearchOverride</c> stands in for
        /// the walk over <c>AppDomain.CurrentDomain</c> and hands back the two matches.</para>
        /// </summary>
        [Fact]
        public void A_bit_enum_two_loaded_assemblies_declare_refuses_rather_than_picking_one()
        {
            var domain = WithEnumSearch(
                TwoPluginsDeclareIt,
                () => PrefabEditShapeReflect.BuildValueDomain(
                    typeof(BuffModificationFlagDataShape),
                    ContestedEntry(statedAssembly: "")));

            Assert.Contains("wire-safe list is stale", domain.Error);
            Assert.Contains("ambiguous", domain.Error);
            Assert.Contains("ProjectM.SomeBitsTwoAssembliesDeclare", domain.Error);
            // Both claimants are named, because the fix is to work out which one should not be
            // answering for a game type.
            Assert.Contains("ProjectMagic", domain.Error);
            Assert.Contains("ProjectMayhem", domain.Error);

            // Fails closed: not "check against whichever we picked", but "carry nothing of this
            // component at all", for a well-behaved record as much as a hostile one.
            Assert.False(domain.TryAccept(
                Zeroes(typeof(BuffModificationFlagDataShape)), out _, out string reason));
            Assert.Equal(domain.Error, reason);
        }

        /// <summary>
        /// <b>A policy that names the declaring assembly does not reach the search at all</b> (job
        /// FEDW7, R55 finding 6), so the ambiguous case above cannot arise for a seed entry: the
        /// same two claimants are on offer and the named one is taken, deterministically and
        /// whatever order they loaded in. The seed's own entry names <c>ProjectM.Shared</c>.
        /// </summary>
        [Fact]
        public void A_policy_that_names_the_declaring_assembly_takes_that_one_and_ignores_the_other()
        {
            var domain = WithEnumSearch(
                TwoPluginsDeclareIt,
                () => PrefabEditShapeReflect.BuildValueDomain(
                    typeof(BuffModificationFlagDataShape),
                    ContestedEntry(statedAssembly: "ProjectMayhem")));

            Assert.Equal("", domain.Error);
            // ProjectMayhem's copy declares four bits; ProjectMagic's declares two. The mask is the
            // named assembly's, which is the whole point.
            Assert.Equal(0xFL, domain.Checks[0].Mask);
            Assert.True(domain.TryAccept(
                Bytes(new BuffModificationFlagDataShape { ModificationTypes = 0x8L }), out _, out _));
            Assert.False(domain.TryAccept(
                Bytes(new BuffModificationFlagDataShape { ModificationTypes = 0x10L }), out _, out _));

            // And the other way round, to show the name is read rather than a fixed preference.
            var other = WithEnumSearch(
                TwoPluginsDeclareIt,
                () => PrefabEditShapeReflect.BuildValueDomain(
                    typeof(BuffModificationFlagDataShape),
                    ContestedEntry(statedAssembly: "ProjectMagic")));
            Assert.Equal("", other.Error);
            Assert.Equal(0x3L, other.Checks[0].Mask);
        }

        /// <summary>
        /// A named assembly is a preference, not a precondition (job FEDW7): a build that moved the
        /// enum — this interop has moved types between <c>ProjectM.dll</c> and
        /// <c>ProjectM.Shared.dll</c> before, which is why FEDW6 widened the search in the first
        /// place — falls through to the same steps an unnamed enum takes, and only then to the
        /// refusals.
        /// </summary>
        [Fact]
        public void A_named_assembly_that_does_not_answer_falls_through_to_the_search()
        {
            var domain = WithEnumSearch(
                TwoPluginsDeclareIt,
                () => PrefabEditShapeReflect.BuildValueDomain(
                    typeof(BuffModificationFlagDataShape),
                    ContestedEntry(statedAssembly: "ProjectM.AnAssemblyThisBuildDoesNotHave")));

            // Two claimants and no name to choose between them: refused, not guessed.
            Assert.Contains("ambiguous", domain.Error);

            // One claimant, and it is taken even though the policy named a different assembly.
            var single = WithEnumSearch(
                _ => new[]
                {
                    new PrefabEditShapeReflect.PrefabEditEnumCandidate(
                        "ProjectM.Shared", typeof(BitsAsProjectMayhemDeclaresThem)),
                },
                () => PrefabEditShapeReflect.BuildValueDomain(
                    typeof(BuffModificationFlagDataShape),
                    ContestedEntry(statedAssembly: "ProjectM.AnAssemblyThisBuildDoesNotHave")));
            Assert.Equal("", single.Error);
            Assert.Equal(0xFL, single.Checks[0].Mask);
        }

        /// <summary>
        /// The seed states the declaring assembly for every enum it resolves by name (job FEDW7).
        /// An entry that does not is an entry whose mask can be decided by load order, which is the
        /// defect this rule exists to remove — so the list is checked rather than trusted.
        /// </summary>
        [Fact]
        public void Every_named_bit_enum_in_the_seed_names_the_assembly_that_declares_it()
        {
            int checkedPolicies = 0;
            foreach (var entry in PrefabEditWireTypes.All)
                foreach (var policy in entry.ValuePolicies)
                {
                    if (policy.Rule != PrefabEditValueRule.NamedEnumBits) continue;
                    checkedPolicies++;
                    Assert.False(
                        string.IsNullOrEmpty(policy.EnumAssemblyName),
                        entry.TypeFullName + "." + policy.Field + " resolves '" + policy.EnumTypeName
                        + "' by name and does not say which assembly declares it");
                    // A simple name, not a file name and not a qualified one: it is compared
                    // against Assembly.GetName().Name.
                    Assert.DoesNotContain(".dll", policy.EnumAssemblyName);
                    Assert.DoesNotContain(",", policy.EnumAssemblyName);
                }

            // The one such policy today, and the assembly the interop declares its enum in:
            // the IL viewer -t ProjectM.BuffModificationTypes on ProjectM.dll answers "not found in the
            // module being native, but only in ProjectM.Shared".
            Assert.Equal(1, checkedPolicies);
            Assert.True(PrefabEditWireTypes.TryGet("ProjectM.BuffModificationFlagData", out var buff));
            var named = Assert.Single(
                Array.FindAll(buff.ValuePolicies, p => p.Rule == PrefabEditValueRule.NamedEnumBits));
            Assert.Equal("ProjectM.Shared", named.EnumAssemblyName);
        }

        // ---- the contested-enum fixtures ----------------------------------------------------------

        private static PrefabEditWireType ContestedEntry(string statedAssembly) =>
            new(
                "ProjectM.BuffModificationFlagData", 16,
                new[] { "ModificationTypes", "ModificationId" }, null, "fixture",
                new[]
                {
                    PrefabEditFieldValuePolicy.NamedEnumBits(
                        "ModificationTypes",
                        // A name the test assembly itself does not declare, so resolution reaches
                        // the searched step instead of stopping at the naming type's own assembly.
                        "ProjectM.SomeBitsTwoAssembliesDeclare",
                        statedAssembly,
                        0x3L,
                        "a fixture bitfield whose enum two loaded plugins claim to declare"),
                });

        private static IReadOnlyList<PrefabEditShapeReflect.PrefabEditEnumCandidate> TwoPluginsDeclareIt(
            string fullName) =>
            new[]
            {
                new PrefabEditShapeReflect.PrefabEditEnumCandidate(
                    "ProjectMagic", typeof(BitsAsProjectMagicDeclaresThem)),
                new PrefabEditShapeReflect.PrefabEditEnumCandidate(
                    "ProjectMayhem", typeof(BitsAsProjectMayhemDeclaresThem)),
            };

        private static T WithEnumSearch<T>(
            Func<string, IReadOnlyList<PrefabEditShapeReflect.PrefabEditEnumCandidate>> search, Func<T> body)
        {
            PrefabEditShapeReflect.ResetCache();
            PrefabEditShapeReflect.EnumSearchOverride = search;
            try { return body(); }
            finally
            {
                PrefabEditShapeReflect.EnumSearchOverride = null;
                PrefabEditShapeReflect.ResetCache();
            }
        }

        [Flags]
        private enum BitsAsProjectMagicDeclaresThem : long { None = 0, First = 1, Second = 2 }

        [Flags]
        private enum BitsAsProjectMayhemDeclaresThem : long
        {
            None = 0, First = 1, Second = 2, Third = 4, Fourth = 8,
        }

        /// <summary>
        /// The domain travels with the shape, so the one choke point that resolves a type is the one
        /// place that resolves its value domain — and both ends read it off the same object.
        /// </summary>
        [Fact]
        public void Both_ends_check_the_value_domain_in_the_live_code()
        {
            string apply = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabEditWireApply.cs");
            string frame = SourcePin.MethodBody(apply, "internal static bool TryFrameLayer(");
            SourcePin.AssertUncommented(frame, "var domain = shape.ValueDomain;");
            SourcePin.AssertUncommented(frame, "LogValueRefusedOnce(guidHash, rec.TypeFullName, badField, valueReason)");

            string decision = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabEditApplyDecision.cs");
            string step = SourcePin.MethodBody(decision, "public static string[] Apply(");
            SourcePin.AssertUncommented(step, "var domain = shape.ValueDomain;");
            SourcePin.AssertUncommented(step, "domain.TryAccept(final, out string badField, out string valueReason)");

            // The log key must not carry the reason: the reason carries the attacker's value, and a key
            // that varies with it is one log line per packet.
            string once = SourcePin.MethodBody(apply, "internal static void LogValueRefusedOnce(");
            SourcePin.AssertUncommented(once, "string key = \"value|\" + (typeFullName ?? \"\") + \"|\" + (field ?? \"\");");
            Assert.DoesNotContain("+ reason", once);
        }

        /// <summary>
        /// The analysis lives in two places on purpose — the table in <c>docs/edits-on-the-wire.md</c>
        /// that a reader finds and the comment block beside the seed that an editor of the list finds —
        /// so this compares the field names in both against the list itself. A field added to the seed
        /// without its row, or a row for a field the seed dropped, fails here.
        /// </summary>
        [Fact]
        public void The_doc_table_the_seed_comment_and_the_list_name_the_same_fields()
        {
            var fromList = new SortedSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < PrefabEditWireTypes.All.Length; i++)
            {
                var entry = PrefabEditWireTypes.All[i];
                string shortName = PrefabEditWire.ShortTypeName(entry.TypeFullName);
                for (int f = 0; f < entry.ValueFields.Length; f++) fromList.Add(shortName + "." + entry.ValueFields[f]);
                for (int f = 0; f < entry.PreserveFields.Length; f++) fromList.Add(shortName + "." + entry.PreserveFields[f]);
            }
            Assert.Equal(27, fromList.Count);

            string source = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabEditWireTypes.cs");
            var fromComment = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var m in Regex.Matches(source, @"^\s*//\s{3}([A-Za-z]+\.[A-Za-z]+)\s*\|", RegexOptions.Multiline))
                fromComment.Add(((Match)m).Groups[1].Value);

            string doc = SourcePin.ReadFile("docs", "edits-on-the-wire.md");
            var fromDoc = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var m in Regex.Matches(doc, @"^\|\s*`([A-Za-z]+\.[A-Za-z]+)`\s*\|", RegexOptions.Multiline))
            {
                string name = ((Match)m).Groups[1].Value;
                // The seed table one section up lists the types themselves, not their fields.
                if (name.StartsWith("ProjectM.", StringComparison.Ordinal)) continue;
                fromDoc.Add(name);
            }

            Assert.Equal(fromList, fromComment);
            Assert.Equal(fromList, fromDoc);
        }

        /// <summary>
        /// <b>The seed and the doc table cite by name, never by metadata line</b> (job FEDW6, R54
        /// finding 5). Line citations used to anchor each per-field policy; R54 found eleven off by
        /// one or two, several on a neighboring field — <c>ItemData.MaxAmount</c> cited the line of
        /// <c>DropItemPrefab</c>. A line number changes on every game update, so neither the seed's
        /// policy reasons nor the mirrored table in <c>docs/edits-on-the-wire.md</c> may carry one.
        /// </summary>
        [Fact]
        public void The_seed_and_the_doc_table_cite_by_name_and_never_by_metadata_line()
        {
            var lineCitation = new Regex(@"\.cs:\d|\blines? \d{3,}", RegexOptions.CultureInvariant);
            var offenders = new List<string>();

            for (int i = 0; i < PrefabEditWireTypes.All.Length; i++)
            {
                var entry = PrefabEditWireTypes.All[i];
                string shortName = PrefabEditWire.ShortTypeName(entry.TypeFullName);
                foreach (var policy in entry.ValuePolicies)
                {
                    Assert.False(string.IsNullOrWhiteSpace(policy.Reason), "seed " + shortName + "." + policy.Field + " has no reason");
                    if (lineCitation.IsMatch(policy.Reason)) offenders.Add("seed " + shortName + "." + policy.Field);
                }
            }

            string doc = SourcePin.ReadFile("docs", "edits-on-the-wire.md");
            foreach (Match row in Regex.Matches(doc, @"^\|\s*`([A-Za-z]+)\.([A-Za-z]+)`\s*\|.*$", RegexOptions.Multiline))
            {
                if (lineCitation.IsMatch(row.Value))
                    offenders.Add("docs/edits-on-the-wire.md " + row.Groups[1].Value + "." + row.Groups[2].Value);
            }

            Assert.True(offenders.Count == 0, "line citations: " + string.Join("; ", offenders));
        }

        // ---- helpers for the value-domain tests ----------------------------------------------------

        private static PrefabEditValueDomain Domain(string wireName, Type fixture)
        {
            Assert.True(PrefabEditWireTypes.TryGet(wireName, out var entry), wireName);
            var domain = PrefabEditShapeReflect.BuildValueDomain(fixture, entry);
            Assert.Equal("", domain.Error);
            return domain;
        }

        private static byte[] Bytes<T>(T value) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            var bytes = new byte[size];
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, buffer, false);
                Marshal.Copy(buffer, bytes, 0, size);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return bytes;
        }

        private static byte[] Zeroes(Type fixture) => new byte[Marshal.SizeOf(fixture)];

        private static PrefabEditComponentBytes Record<T>(string wireName, T value) where T : struct
            => new(wireName, Bytes(value));

        // ---- a receiving site that answers the way LiveApplySite does ------------------------------

        private sealed class ListSite : IPrefabEditApplySite
        {
            public List<string> Refusals { get; } = new();

            public bool TryAcceptType(string? typeFullName, out PrefabEditTypeShape shape, out string reason)
            {
                shape = null!;
                if (!PrefabEditWireTypes.TryGet(typeFullName, out var entry))
                {
                    reason = PrefabEditWireTypes.NotVouchedReason(typeFullName);
                    return false;
                }
                var described = new PrefabEditTypeShape(
                    entry.TypeFullName,
                    entry.ExpectedSize,
                    Array.Empty<PrefabEditByteRange>(),
                    Array.Empty<string>(),
                    typeof(object),
                    entry.ValueFields);
                if (!PrefabEditWireTypes.Agrees(entry, described, out reason)) return false;
                shape = described;
                return true;
            }

            public bool HasComponent(PrefabEditTypeShape shape, out string reason)
            {
                reason = "";
                return true;
            }

            public byte[]? CurrentBytes(PrefabEditTypeShape shape) => new byte[shape.Size];

            public void CaptureBaseline(PrefabEditTypeShape shape, string shortName) { }

            public void Write(PrefabEditTypeShape shape, byte[] bytes) { }
        }

        /// <summary>
        /// The same site over the layout fixtures, with the value domain resolved the way the live
        /// choke point resolves it: one shape per type, carrying the domain built from the receiver's
        /// own copy of the type (job FEDW5).
        /// </summary>
        private sealed class FixtureSite : IPrefabEditApplySite
        {
            public List<string> Refusals { get; } = new();
            public List<string> ValueRefusals { get; } = new();
            public List<string> Written { get; } = new();

            private string _pending = "";

            public bool TryAcceptType(string? typeFullName, out PrefabEditTypeShape shape, out string reason)
            {
                shape = null!;
                if (!PrefabEditWireTypes.TryGet(typeFullName, out var entry))
                {
                    reason = PrefabEditWireTypes.NotVouchedReason(typeFullName);
                    return false;
                }
                Type? fixture = null;
                for (int i = 0; i < Seeded.Length; i++)
                    if (Seeded[i].Name == entry.TypeFullName) fixture = Seeded[i].Fixture;
                if (fixture == null)
                {
                    reason = "no fixture for " + entry.TypeFullName;
                    return false;
                }
                if (!PrefabEditShapeReflect.TryDescribe(fixture, out var described, out reason)) return false;
                if (!PrefabEditWireTypes.Agrees(entry, described, out reason)) return false;
                described.SetValueDomain(PrefabEditShapeReflect.BuildValueDomain(fixture, entry));
                shape = described;
                return true;
            }

            public bool HasComponent(PrefabEditTypeShape shape, out string reason)
            {
                reason = "";
                return true;
            }

            public byte[]? CurrentBytes(PrefabEditTypeShape shape) => new byte[shape.Size];

            public void CaptureBaseline(PrefabEditTypeShape shape, string shortName) => _pending = shortName;

            public void Write(PrefabEditTypeShape shape, byte[] bytes) => Written.Add(_pending);
        }
    }
}


/// <summary>
/// <b>The fixtures' copy of <c>ProjectM.BuffModificationTypes</c></b> (job FEDW6, R54 finding 3).
///
/// <para>It is declared under the game's own namespace on purpose. <c>NamedEnumBits</c> resolves
/// its enum by full name across the loaded assemblies, and since FEDW6 an enum that does not
/// resolve <em>refuses every record of its component</em> rather than dropping onto the literal in
/// the seed. Before that the suite drove this field down the fallback branch, which meant the one
/// branch written for the fixtures was also the branch a game update would silently take — the
/// defect R54 found. With a real enum here the fixtures take the same path a live client takes, and
/// the fallback has no legitimate caller left to hide behind.</para>
///
/// <para>Mirrored member for member from the server metadata: the 46
/// single-bit members, bits 0..47 with 8 and 10 absent, plus <c>None</c> and <c>All</c>, which are
/// not single bits and contribute nothing to the mask. Their union is
/// <c>PrefabEditWireTypes.DeclaredBuffModificationTypes</c>, and a test asserts exactly that.</para>
/// </summary>
namespace ProjectM
{
    [System.Flags]
    internal enum BuffModificationTypes : long
    {
        None = 0,
        All = -1,
        ImmuneToHazards = 1L << 0,
        ImmuneToSun = 1L << 1,
        Invulnerable = 1L << 2,
        Immaterial = 1L << 3,
        MovementImpair = 1L << 4,
        BuildMenuImpair = 1L << 5,
        IsFlying = 1L << 6,
        ConsumableImpair = 1L << 7,
        DisableDynamicCollision = 1L << 9,
        DisableMapCollision = 1L << 11,
        MountImpaired = 1L << 12,
        FishingImpaired = 1L << 13,
        SleepInCoffinImpaired = 1L << 14,
        ContainerInteractionImpaired = 1L << 15,
        HayStackImpaired = 1L << 16,
        ThroneImpaired = 1L << 17,
        RelicImpaired = 1L << 18,
        FeedImpaired = 1L << 19,
        PrisonImpaired = 1L << 20,
        ReviveImpaired = 1L << 21,
        DragPlayerImpaired = 1L << 22,
        TargetSpellImpaired = 1L << 23,
        AlwaysInSun = 1L << 24,
        AbilityCastImpair = 1L << 25,
        FlyOnlyMapCollision = 1L << 26,
        DisableHeightCorrection = 1L << 27,
        KeepMountOnAbilityImpair = 1L << 28,
        DisableUnitVisibility = 1L << 29,
        RotationImpair = 1L << 30,
        TradeImpair = 1L << 31,
        WaypointImpair = 1L << 32,
        SiegeStructureImpair = 1L << 33,
        CastOptionImpair = 1L << 34,
        RelocateImpair = 1L << 35,
        VeilActive = 1L << 36,
        LegDirectionImpair = 1L << 37,
        BehaviourImpair = 1L << 38,
        IsVbloodGhost = 1L << 39,
        IsWounded = 1L << 40,
        CannotBeDisconnectDragged = 1L << 41,
        LocalTeleporterImpaired = 1L << 42,
        PickupItemImpaired = 1L << 43,
        PickupRaidItemImpaired = 1L << 44,
        IgnoreAbilityMovementSpeedChanges = 1L << 45,
        IsHumanForm = 1L << 46,
        IsShapeshiftForm = 1L << 47,
    }
}
