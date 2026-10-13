using Progenitor.Players;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The direct respawn call is refused. This pins the game-free reason. It does not
/// call <c>RespawnCharacter</c>.
/// </summary>
public class PlayerRespawnRefusalTests
{
    [Fact]
    public void Refusal_names_the_abort_and_the_in_game_path()
    {
        Assert.Contains("Allocator.Temp", PlayerRespawnPolicy.UnsafeDirectCallRefusal);
        Assert.Contains("RespawnCharacter", PlayerRespawnPolicy.UnsafeDirectCallRefusal);
        Assert.Contains("waygate", PlayerRespawnPolicy.UnsafeDirectCallRefusal);
    }
}
