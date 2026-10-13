using System;
using System.Collections.Generic;
using System.Reflection;
using Progenitor.Patches;
using Xunit;

namespace Il2CppSystem
{
    /// <summary>
    /// Name-only stand-in for the interop base of a non-blittable struct wrapper. The gate
    /// recognises it by <c>FullName</c>, so the test project needs no Il2Cpp reference.
    /// </summary>
    public class ValueType { }
}

namespace Progenitor.Tests
{
    /// <summary>
    /// 2026-10-07 facade smoke: a Harmony prefix on the 11-arg <c>KillEntity(…, ref Health, …)</c>
    /// wrote 8 bytes over <c>Health.MaxHealth</c> (4.03e20) because the game's Il2CppInterop
    /// (1.4.6-dev) marshals a by-ref struct through <c>Ldind_I</c>/<c>Stind_I</c>. The registry
    /// now refuses any target with a by-ref value-type parameter, for every owner.
    /// </summary>
    [Collection("PluginLogger")]
    public class PatchByRefStructTests : IDisposable
    {
        private readonly TestLogSource _log = new();

        public PatchByRefStructTests()
        {
            PatchRegistry.ResetForTests();
            Plugin.Logger = _log;
        }

        public void Dispose()
        {
            PatchRegistry.ResetForTests();
            Plugin.Logger = null;
        }

        private struct Health { public float MaxHealth; public float Value; }
        private sealed class Boxed : Il2CppSystem.ValueType { }
        private sealed class Ref { }

        private static class Targets
        {
            internal static void ByRef(int a, ref Health health) { _ = health; }
            internal static void ByIn(in Health health) { _ = health; }
            internal static void ByOut(out Health health) { health = default; }
            internal static void ByRefInt(ref int count) { _ = count; }
            internal static void ByRefWrapper(ref Boxed wrapper) { _ = wrapper; }
            internal static void ByRefClass(ref Ref r) { _ = r; }
            internal static void ByValue(Health health, int count) { _ = health; _ = count; }
        }

        private static class Patchers
        {
            internal static bool Pre() => true;
        }

        private sealed class FakeApplier : IPatchApplier
        {
            internal readonly List<string> Applied = new();

            public object Apply(MethodBase target, MethodInfo patch, PatchKind kind, int priority, string ownerId)
            {
                Applied.Add(target.Name);
                return new object();
            }

            public void Remove(object token, MethodBase target, MethodInfo patch, PatchKind kind, string ownerId) { }
        }

        private static MethodBase Target(string name) =>
            typeof(Targets).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

        private static MethodInfo Pre =>
            typeof(Patchers).GetMethod(nameof(Patchers.Pre), BindingFlags.NonPublic | BindingFlags.Static)!;

        private FakeApplier Install()
        {
            var applier = new FakeApplier();
            PatchRegistry.Applier = applier;
            PatchRegistry.SiteResolver = m => new PatchSiteInfo(PatchRegistry.DescribeTarget(m), "table", 0x79D0D40, isBurstJob: false);
            return applier;
        }

        [Theory]
        [InlineData(nameof(Targets.ByRef), "ref Health health")]
        [InlineData(nameof(Targets.ByIn), "in Health health")]
        [InlineData(nameof(Targets.ByOut), "out Health health")]
        [InlineData(nameof(Targets.ByRefInt), "ref Int32 count")]
        [InlineData(nameof(Targets.ByRefWrapper), "ref Boxed wrapper")]
        public void A_by_ref_value_type_parameter_is_found_and_named(string target, string expected)
        {
            Assert.Equal(expected, PatchSitePolicy.FindByRefValueTypeParameter(Target(target)));
        }

        [Theory]
        [InlineData(nameof(Targets.ByRefClass))]
        [InlineData(nameof(Targets.ByValue))]
        public void By_value_structs_and_by_ref_classes_are_not_the_trap(string target)
        {
            Assert.Null(PatchSitePolicy.FindByRefValueTypeParameter(Target(target)));
        }

        [Fact]
        public void A_null_target_has_no_by_ref_parameter()
        {
            Assert.Null(PatchSitePolicy.FindByRefValueTypeParameter(null));
        }

        [Fact]
        public void The_registry_refuses_a_by_ref_struct_target_and_says_why()
        {
            var applier = Install();

            var handle = PatchRegistry.Prefix(new object(), "plugin.a", Target(nameof(Targets.ByRef)), Pre);

            Assert.Null(handle);
            Assert.Empty(applier.Applied);
            Assert.Equal(0, PatchRegistry.Count);
            var line = Assert.Single(_log.Warnings, l => l.Contains("reason=" + PatchSitePolicy.ReasonByRefValueType, StringComparison.Ordinal));
            Assert.Contains("ref Health health", line, StringComparison.Ordinal);
        }

        [Fact]
        public void A_by_value_target_still_patches()
        {
            var applier = Install();

            var handle = PatchRegistry.Prefix(new object(), "plugin.a", Target(nameof(Targets.ByValue)), Pre);

            Assert.NotNull(handle);
            Assert.Equal(new[] { nameof(Targets.ByValue) }, applier.Applied);
        }
    }
}
