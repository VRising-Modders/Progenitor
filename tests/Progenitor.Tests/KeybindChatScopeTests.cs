using System.Linq;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// CHAT2: chat-scope keybinds (<see cref="KeybindScope.Chat"/>, Core's name completion key). A chat key fires
    /// only while the player types in chat, and is then the only kind of bind that fires; it shares the game's
    /// in-chat keys (Tab cycles the channel) without losing to them. Game-free, on <see cref="KeybindTabTests"/>'
    /// fakes.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        private static readonly KeyChord TabKey = new(UiKey.Tab);

        private void RegisterChat(string owner, string id, KeyChord chord, System.Action press) =>
            Keybinds.RegisterScoped(_owner, owner, id, File(owner), chord, "", press, null, null,
                allowWhileCaptured: false, allowWhenChatUnknown: false, KeybindScope.Chat);

        [Fact]
        public void A_chat_key_fires_only_while_typing_in_chat_and_gameplay_keys_stay_quiet_then()
        {
            int chat = 0, gameplay = 0;
            RegisterChat("progenitor", "ChatCompleteName", TabKey, () => chat++);
            Keybinds.Register(_owner, "b.mod", "Wave", File("b"), F8, "", () => gameplay++);

            _keys.Down(TabKey);
            _keys.Frame();
            _keys.Up(UiKey.Tab);
            _keys.Frame();
            Assert.Equal(0, chat);
            Assert.Equal(Keybinds.ChatClosedReason, Keybinds.SimulatePress("progenitor", "ChatCompleteName").Single().Reason);

            _keys.Chat = true;
            _keys.Down(TabKey);
            _keys.Frame();
            _keys.Up(UiKey.Tab);
            _keys.Down(F8);
            _keys.Frame();
            Assert.Equal(1, chat);
            Assert.Equal(0, gameplay);
            Assert.True(Keybinds.SimulatePress("progenitor", "ChatCompleteName").Single().Fired);
            Assert.Equal(2, chat);
        }

        [Fact]
        public void A_chat_key_shares_the_games_channel_cycle_and_meets_nothing_else()
        {
            var cycle = GameInMode("CycleChat", TabKey, GameKeybindCatalog.InChat);
            var gameplay = Game("Interact", TabKey);
            Assert.True(KeybindArbiter.ScopesMeet(cycle, KeybindScope.Chat));
            Assert.False(KeybindArbiter.GameWins(cycle, KeybindScope.Chat));
            Assert.False(KeybindArbiter.ScopesMeet(gameplay, KeybindScope.Chat));

            UseGame(cycle);
            RegisterChat("progenitor", "ChatCompleteName", TabKey, () => { });
            Keybinds.Register(_owner, "b.mod", "Wave", File("b"), TabKey, "", () => { });
            var picture = KeybindArbiter.Current();
            var complete = picture.FindProgenitor("progenitor", "ChatCompleteName")!;
            Assert.True(complete.Active);
            Assert.Equal(KeybindScope.Chat, complete.Claim.Scope);
            Assert.DoesNotContain(complete.Others, o => o.ActionId == "Wave");
        }

        [Fact]
        public void The_keybinds_tab_says_where_a_chat_key_works()
        {
            Assert.Contains("typing in chat", KeybindsTabLayout.ChatScopeNote);
        }
    }
}
