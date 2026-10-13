using System;
using Progenitor.Commands;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// CommandRegistry is process-wide static state. These tests run in one collection so they do
/// not rebuild the lookup table over each other. Command names are prefixed <c>erg_</c> so they
/// cannot collide with Core's built-ins even if those were linked (they are not).
/// </summary>
[Collection("CommandRegistry")]
public class CommandRegistryTests
{
    [Fact]
    public void RegisterType_static_class_invokes_methods_with_null_target()
    {
        var owner = new object();
        StaticOnlyCommands.Hits = 0;
        using var handle = CommandRegistry.RegisterType(typeof(StaticOnlyCommands), owner);
        try
        {
            Assert.True(CommandRegistry.TryGetRoot("erg_static", out var bundle));
            var overload = bundle.Overloads[0];
            Assert.True(overload.Method.IsStatic);
            Assert.Null(overload.Instance);

            overload.Method.Invoke(overload.Instance, new object[] { new FakeCtx() });
            Assert.Equal(1, StaticOnlyCommands.Hits);
        }
        finally
        {
            handle.Dispose();
            CommandRegistry.UnregisterOwner(owner);
        }

        Assert.False(CommandRegistry.TryGetRoot("erg_static", out _));
    }

    [Fact]
    public void RegisterType_instance_class_shares_one_constructed_instance()
    {
        var owner = new object();
        using var handle = CommandRegistry.RegisterType(typeof(InstanceCommands), owner);
        try
        {
            Assert.True(CommandRegistry.TryGetRoot("erg_inst_a", out var a));
            Assert.True(CommandRegistry.TryGetRoot("erg_inst_b", out var b));
            object? instA = a.Overloads[0].Instance;
            object? instB = b.Overloads[0].Instance;
            Assert.NotNull(instA);
            Assert.Same(instA, instB);

            a.Overloads[0].Method.Invoke(instA, new object[] { new FakeCtx() });
            b.Overloads[0].Method.Invoke(instB, new object[] { new FakeCtx() });
            Assert.Equal(2, ((InstanceCommands)instA!).Hits);
        }
        finally
        {
            handle.Dispose();
            CommandRegistry.UnregisterOwner(owner);
        }
    }

    [Fact]
    public void RegisterInstance_invokes_instance_methods_on_the_supplied_object()
    {
        var owner = new object();
        var cmds = new InstanceCommands();
        using var handle = CommandRegistry.RegisterInstance(cmds, owner);
        try
        {
            Assert.True(CommandRegistry.TryGetRoot("erg_inst_a", out var bundle));
            Assert.Same(cmds, bundle.Overloads[0].Instance);

            bundle.Overloads[0].Method.Invoke(bundle.Overloads[0].Instance, new object[] { new FakeCtx() });
            Assert.Equal(1, cmds.Hits);
        }
        finally
        {
            handle.Dispose();
            CommandRegistry.UnregisterOwner(owner);
        }
    }

    [Fact]
    public void Mixed_class_static_methods_use_null_target_instance_methods_use_the_object()
    {
        var owner = new object();
        MixedCommands.StaticHits = 0;
        using var handle = CommandRegistry.RegisterType(typeof(MixedCommands), owner);
        try
        {
            Assert.True(CommandRegistry.TryGetRoot("erg_mix_s", out var stat));
            Assert.True(CommandRegistry.TryGetRoot("erg_mix_i", out var inst));
            Assert.True(stat.Overloads[0].Method.IsStatic);
            Assert.Null(stat.Overloads[0].Instance);
            Assert.False(inst.Overloads[0].Method.IsStatic);
            Assert.NotNull(inst.Overloads[0].Instance);

            stat.Overloads[0].Method.Invoke(stat.Overloads[0].Instance, new object[] { new FakeCtx() });
            inst.Overloads[0].Method.Invoke(inst.Overloads[0].Instance, new object[] { new FakeCtx() });
            Assert.Equal(1, MixedCommands.StaticHits);
            Assert.Equal(1, ((MixedCommands)inst.Overloads[0].Instance!).InstanceHits);
        }
        finally
        {
            handle.Dispose();
            CommandRegistry.UnregisterOwner(owner);
        }
    }

    [Fact]
    public void RegisterType_keeps_static_methods_when_the_type_cannot_be_constructed()
    {
        var owner = new object();
        using var handle = CommandRegistry.RegisterType(typeof(NoCtorCommands), owner);
        try
        {
            Assert.True(CommandRegistry.TryGetRoot("erg_noct_s", out var stat));
            Assert.Null(stat.Overloads[0].Instance);
            Assert.False(CommandRegistry.TryGetRoot("erg_noct_i", out _));
        }
        finally
        {
            handle.Dispose();
            CommandRegistry.UnregisterOwner(owner);
        }
    }

    [Fact]
    public void RegisterInstance_registers_instance_methods_on_a_type_with_no_parameterless_constructor()
    {
        var owner = new object();
        var cmds = new NoCtorCommands(7);
        using var handle = CommandRegistry.RegisterInstance(cmds, owner);
        try
        {
            Assert.True(CommandRegistry.TryGetRoot("erg_noct_i", out var inst));
            Assert.Same(cmds, inst.Overloads[0].Instance);
            inst.Overloads[0].Method.Invoke(inst.Overloads[0].Instance, new object[] { new FakeCtx() });
            Assert.Equal(1, cmds.Hits);
        }
        finally
        {
            handle.Dispose();
            CommandRegistry.UnregisterOwner(owner);
        }
    }

    [Fact]
    public void RegisterType_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => CommandRegistry.RegisterType(null!, new object()));
        Assert.Throws<ArgumentNullException>(() => CommandRegistry.RegisterType(typeof(StaticOnlyCommands), null!));
    }

    [Fact]
    public void RegisterInstance_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => CommandRegistry.RegisterInstance(null!, new object()));
        Assert.Throws<ArgumentNullException>(() => CommandRegistry.RegisterInstance(new InstanceCommands(), null!));
    }

    [Fact]
    public void RegisterInstance_rejects_a_Type_so_RegisterType_is_the_by_type_path()
    {
        Assert.Throws<ArgumentException>(() => CommandRegistry.RegisterInstance(typeof(StaticOnlyCommands), new object()));
    }

    private sealed class FakeCtx : ICommandContext { }

    /// <summary>
    /// The admin gate a plugin author is meant to use: one named argument on <c>[Command]</c>.
    /// R27 finding 3 — the Sample's debug commands now carry it, and this pins that the
    /// mechanism they lean on actually denies a non-admin and hides the command from
    /// <c>.help</c>, without every plugin re-implementing the check in its own body.
    ///
    /// Fails if <c>adminOnly</c> ever stops reaching <c>CommandOverload.AdminOnly</c> or
    /// <c>CommandBundle.VisibleTo</c>, which is the whole gate: <c>CommandDispatcher.Invoke</c>
    /// filters on exactly those two before binding any argument.
    /// </summary>
    [Fact]
    public void AdminOnly_commands_are_hidden_and_denied_for_non_admins()
    {
        var owner = new object();
        using var handle = CommandRegistry.RegisterType(typeof(GatedCommands), owner);
        try
        {
            Assert.True(CommandRegistry.TryGetRoot("erg_gated", out var gated));
            Assert.True(gated.Overloads[0].AdminOnly);
            Assert.False(gated.VisibleTo(isAdmin: false));
            Assert.True(gated.VisibleTo(isAdmin: true));

            Assert.True(CommandRegistry.TryGetRoot("erg_open", out var open));
            Assert.False(open.Overloads[0].AdminOnly);
            Assert.True(open.VisibleTo(isAdmin: false));

            // The dispatcher's own filter, spelled out: no usable overload for a non-admin.
            int usableForPlayer = 0;
            foreach (var o in gated.Overloads)
                if (!o.AdminOnly) usableForPlayer++;
            Assert.Equal(0, usableForPlayer);
        }
        finally
        {
            handle.Dispose();
            CommandRegistry.UnregisterOwner(owner);
        }
    }

    private static class GatedCommands
    {
        [Command("erg_gated", adminOnly: true)]
        public static void Gated(ICommandContext ctx) { }

        [Command("erg_open")]
        public static void Open(ICommandContext ctx) { }
    }

    private static class StaticOnlyCommands
    {
        public static int Hits;

        [Command("erg_static")]
        public static void Run(ICommandContext ctx) => Hits++;
    }

    private sealed class InstanceCommands
    {
        public int Hits;

        [Command("erg_inst_a")]
        public void A(ICommandContext ctx) => Hits++;

        [Command("erg_inst_b")]
        public void B(ICommandContext ctx) => Hits++;
    }

    private sealed class MixedCommands
    {
        public static int StaticHits;
        public int InstanceHits;

        [Command("erg_mix_s")]
        public static void Stat(ICommandContext ctx) => StaticHits++;

        [Command("erg_mix_i")]
        public void Inst(ICommandContext ctx) => InstanceHits++;
    }

    private sealed class NoCtorCommands
    {
        public int Hits;
        public NoCtorCommands(int _) { }

        [Command("erg_noct_s")]
        public static void Stat(ICommandContext ctx) { }

        [Command("erg_noct_i")]
        public void Inst(ICommandContext ctx) => Hits++;
    }
}
