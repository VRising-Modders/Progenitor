using System;
using Progenitor.Players.Forms;
using Xunit;

// Only identity-shaped placeholders; these tests never execute Unity or native code.
namespace Unity.Entities
{
    public sealed class World { }
    public readonly record struct Entity(int Index, int Version)
    { public static Entity Null => default; }
}

namespace Progenitor.Tests
{
    public sealed class PlayerFormsContractTests : IDisposable
    {
        public PlayerFormsContractTests()
        {
            Fake.Ready = true; Fake.BeginResult = true; Fake.BeginId = 42;
            Fake.ReturnResult = true; Fake.Status = "Reserved"; Fake.Throw = false;
            PlayerForms.TypeOverrideForTests = () => typeof(Fake);
        }
        public void Dispose() => PlayerForms.TypeOverrideForTests = null;

        public static class Fake
        {
            public static bool Ready, BeginResult, ReturnResult, Throw;
            public static long BeginId;
            public static string Status = "";
            public static bool IsReady => Ready;
            public static bool TryBegin(Unity.Entities.World world, Unity.Entities.Entity user,
                int prefab, double duration, string owner, out long id, out string error)
            { id = BeginId; error = "partial native failure"; if (Throw) throw new InvalidOperationException("backend unavailable"); return BeginResult; }
            public static bool TryReturn(long id, out string error)
            { error = ReturnResult ? "" : "Unknown or unresolved lease"; return ReturnResult; }
            public static string Describe(long id) => Status;
        }

        private static bool Begin(out PlayerFormHandle? handle, out string error) =>
            PlayerForms.TryBegin(new(), new(1, 1), 1112948824, TimeSpan.FromSeconds(30), "test", out handle, out error);

        [Fact]
        public void Failed_begin_preserves_recovery_handle_and_error()
        {
            Fake.BeginResult = false;
            Assert.False(Begin(out var handle, out var error));
            Assert.NotNull(handle); Assert.Equal(42, handle!.Id);
            Assert.Contains("partial", error);
            Fake.ReturnResult = false; handle.Dispose();
            Assert.False(handle.TryReturn(out _));
            Fake.ReturnResult = true; Assert.True(handle.TryReturn(out _));
        }

        [Theory]
        [InlineData("World ended; no replacement world was mutated")]
        [InlineData("Restored; owned-body cleanup pending")]
        public void Successful_return_preserves_backend_status(string status)
        {
            Assert.True(Begin(out var handle, out _));
            Assert.True(handle!.TryReturn(out _));
            Fake.Status = status; Assert.Equal(status, handle.State);
            Fake.Status = "Unknown or expired lease record";
            Fake.ReturnResult = false;
            Assert.False(handle.TryReturn(out _));
            Assert.Equal(Fake.Status, handle.State);
        }

        [Fact]
        public void Refusal_without_lease_does_not_invent_handle()
        {
            Fake.BeginResult = false; Fake.BeginId = 0;
            Assert.False(Begin(out var handle, out _)); Assert.Null(handle);
            Fake.BeginResult = true;
            Assert.False(Begin(out handle, out _)); Assert.Null(handle);
        }

        [Fact]
        public void Unavailable_or_throwing_backend_returns_inspectable_failure()
        {
            Fake.Throw = true;
            Assert.False(Begin(out _, out var error)); Assert.Contains("backend unavailable", error);
            PlayerForms.TypeOverrideForTests = () => null;
            Assert.False(PlayerForms.IsReady);
            Assert.False(Begin(out var handle, out error)); Assert.Null(handle);
            Assert.Contains("unavailable", error);
        }
    }
}
