using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Serializes tests that reset or mutate the process-wide <c>RecipeApply</c> statics
/// (<c>ResetForTests</c>, <c>ForgetClientWorld</c>), so one class cannot clear another's
/// registrations or station ledger mid-test.
/// </summary>
[CollectionDefinition("RecipeApply")]
public sealed class RecipeApplyCollection
{
}
