using Progenitor.Inventory;
using Xunit;

namespace Progenitor.Tests;

public class InventoryAcceptanceTests
{
    [Fact]
    public void Full_container_still_describes_native_admission()
    {
        var backend = InventoryEngineTests.NewBackend();
        var restriction = new InventoryRestriction(InventoryEngineTests.IronOre, InventoryEngineTests.CatMineral);
        var chest = backend.AddContainer(1, 1, restriction);
        backend.Put(chest, 0, InventoryEngineTests.IronOre, 250);
        var engine = new InventoryEngine(backend);
        var read = engine.DescribeAcceptance(chest);
        Assert.True(read.Ok);
        Assert.False(read.IsOpen);
        Assert.True(read.Allows(InventoryEngineTests.IronOre, InventoryEngineTests.CatMineral));
        Assert.False(read.Allows(InventoryEngineTests.Copper, InventoryEngineTests.CatMineral));
        Assert.False(read.Allows(InventoryEngineTests.IronOre, InventoryEngineTests.CatBloodEssence));
        Assert.Equal(0, engine.FreeSpace(chest, InventoryEngineTests.IronOre));
    }

    [Fact]
    public void Missing_inventory_is_unknown_not_unrestricted()
    {
        var engine = new InventoryEngine(InventoryEngineTests.NewBackend());
        var read = engine.DescribeAcceptance(new InventoryRef(999, 1, 0, 1));
        Assert.False(read.Ok);
        Assert.False(read.IsOpen);
        Assert.NotEmpty(read.Reason);
        Assert.False(read.Allows(InventoryEngineTests.IronOre, InventoryEngineTests.CatMineral));
    }
}
