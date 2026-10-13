using System;
using Progenitor.Hooks;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The rules SystemHooks applies to a TypeManager system-index lookup. The native method throws
/// before TypeManager builds its table and returns -1 for a type the table does not list; neither
/// may become a cached index (HookedIndexSet and the Harmony prefix only know positive indices).
/// </summary>
public class SystemTypeIndexLookupTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(42)]
    public void Missing_table_is_not_ready_whatever_the_value(int value) =>
        Assert.Equal(SystemTypeIndexState.NotReady, SystemTypeIndexLookup.Classify(tableReady: false, value));

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(int.MaxValue)]
    public void Positive_index_resolves(int value) =>
        Assert.Equal(SystemTypeIndexState.Resolved, SystemTypeIndexLookup.Classify(tableReady: true, value));

    [Theory]
    [InlineData(-1)] // native "not in s_ManagedSystemTypeToIndex"
    [InlineData(0)]  // SystemTypeIndex.Null
    [InlineData(int.MinValue)]
    public void Non_positive_index_with_a_table_is_not_registered(int value) =>
        Assert.Equal(SystemTypeIndexState.NotRegistered, SystemTypeIndexLookup.Classify(tableReady: true, value));

    [Fact]
    public void Not_registered_message_names_the_type_and_the_consequence()
    {
        string message = SystemTypeIndexLookup.NotRegisteredMessage("ProjectM.UI.ClientChatSystem");
        Assert.Contains("ProjectM.UI.ClientChatSystem", message);
        Assert.Contains("stay inactive", message);
    }

    [Fact]
    public void Threw_message_is_one_line_without_a_stack()
    {
        Exception e;
        try { throw new NullReferenceException("boom"); }
        catch (Exception caught) { e = caught; }
        string message = SystemTypeIndexLookup.ThrewMessage("ProjectM.UI.BuildMenuMapper", e);
        Assert.Contains("ProjectM.UI.BuildMenuMapper", message);
        Assert.Contains("NullReferenceException: boom", message);
        Assert.DoesNotContain("\n", message);
        Assert.DoesNotContain(" at ", message);
    }
}
