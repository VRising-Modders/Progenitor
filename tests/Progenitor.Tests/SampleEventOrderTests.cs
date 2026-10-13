using System.Collections.Generic;
using Progenitor.Events;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Pins the Sample plugin's two WorldReady handlers: registered high-prio first, low-prio
/// second, with <c>after</c> so the second registration runs first. Constants are copies —
/// this project must not reference Sample (game assemblies). Keep in sync with
/// <c>samples/Progenitor.Sample/Plugin.cs</c> and <c>docs/hooks-ordering.md</c>.
/// </summary>
public class SampleEventOrderTests
{
    private const string FirstId = "progenitor.sample.order.first";
    private const string SecondId = "progenitor.sample.order.second";
    private const int FirstPriority = 0;
    private const int SecondPriority = 100;

    [Fact]
    public void Sample_worldready_handlers_run_second_registration_first()
    {
        var order = new List<int>();
        // Same registration order as Plugin.OnLoad: higher priority first, then lower,
        // with after=[first] flipping the numeric sort.
        using var second = EventBus.On<SampleWorldReadyOrderEvent>(
            _ => order.Add(2),
            priority: SecondPriority,
            after: new[] { FirstId },
            pluginId: SecondId);
        using var first = EventBus.On<SampleWorldReadyOrderEvent>(
            _ => order.Add(1),
            priority: FirstPriority,
            pluginId: FirstId);

        EventBus.Publish(new SampleWorldReadyOrderEvent());

        Assert.Equal(new[] { 1, 2 }, order);
    }

    [Fact]
    public void Sample_worldready_DescribeOrder_lists_first_then_second_with_after()
    {
        using var second = EventBus.On<SampleWorldReadyDescribeEvent>(
            _ => { },
            priority: SecondPriority,
            after: new[] { FirstId },
            pluginId: SecondId);
        using var first = EventBus.On<SampleWorldReadyDescribeEvent>(
            _ => { },
            priority: FirstPriority,
            pluginId: FirstId);

        string dump = EventBus.DescribeOrder<SampleWorldReadyDescribeEvent>();
        Assert.Equal(
            "EventBus SampleWorldReadyDescribeEvent:\n"
            + "  1. " + FirstId + " prio=" + FirstPriority + "\n"
            + "  2. " + SecondId + " prio=" + SecondPriority + " after=[" + FirstId + "]",
            dump);
    }

    [Fact]
    public void Sample_worldready_priority_alone_would_run_the_first_registration_first()
    {
        var order = new List<int>();
        using var second = EventBus.On<SampleWorldReadyPriorityOnlyEvent>(
            _ => order.Add(2),
            priority: SecondPriority,
            pluginId: SecondId);
        using var first = EventBus.On<SampleWorldReadyPriorityOnlyEvent>(
            _ => order.Add(1),
            priority: FirstPriority,
            pluginId: FirstId);

        EventBus.Publish(new SampleWorldReadyPriorityOnlyEvent());

        Assert.Equal(new[] { 2, 1 }, order);
    }

    private sealed class SampleWorldReadyOrderEvent { }
    private sealed class SampleWorldReadyDescribeEvent { }
    private sealed class SampleWorldReadyPriorityOnlyEvent { }
}
