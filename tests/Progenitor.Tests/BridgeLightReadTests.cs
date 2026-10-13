using System.Text.Json;
using Progenitor.Dev.Ops;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// Bridge cost (live 2026-09-30: polling menu_tab_state and nav_state once a second made the game stutter
    /// every second): the light reads skip the ~90 ms workspace status unless withStatus is set, and the two
    /// polled reads never build it.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        private static ModsUiArgs.Request ParseBridge(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return ModsUiArgs.Parse(doc.RootElement.Clone());
        }

        [Fact]
        public void The_polled_state_reads_never_build_the_workspace_status()
        {
            NavKeys();
            ModsMenu.Open();
            _keys.Frame();
            int before = ModsMenu.StatusBuilds;
            for (int i = 0; i < 5; i++)
            {
                ModsMenu.NavState();
                ModsMenu.MenuTabState();
                ModsMenu.KeysOp("conflict", null, null);
            }

            Assert.Equal(before, ModsMenu.StatusBuilds);
            ModsMenu.DescribeWorkspace();
            Assert.Equal(before + 1, ModsMenu.StatusBuilds);
        }

        [Theory]
        [InlineData("{\"action\":\"menu_tab_state\"}", true)]
        [InlineData("{\"action\":\"nav_state\"}", true)]
        [InlineData("{\"action\":\"rects\"}", true)]
        [InlineData("{\"action\":\"window\"}", true)]
        [InlineData("{\"action\":\"scene\"}", true)]
        [InlineData("{\"action\":\"tabs\"}", true)]
        [InlineData("{\"action\":\"keys\",\"op\":\"read\"}", true)]
        [InlineData("{\"action\":\"keys\",\"op\":\"conflict\"}", true)]
        [InlineData("{\"action\":\"keys\"}", true)]
        [InlineData("{\"action\":\"nav_state\",\"withStatus\":true}", false)]
        [InlineData("{\"action\":\"status\"}", false)]
        [InlineData("{\"action\":\"nav_back\"}", false)]
        [InlineData("{\"action\":\"keys\",\"op\":\"save\"}", false)]
        [InlineData("{\"action\":\"keys\",\"op\":\"conflict-choose\",\"target\":\"later\"}", false)]
        [InlineData("{\"action\":\"select_tab\",\"tab\":\"keybinds\"}", false)]
        public void Light_reads_skip_the_status_unless_asked_and_changing_ops_keep_it(string json, bool skips)
        {
            Assert.Equal(skips, ModsUiArgs.SkipsStatus(ParseBridge(json)));
        }

        [Fact]
        public void Execute_takes_the_status_only_when_the_request_does_not_skip_it()
        {
            string ops = SourcePin.ReadFile("src", "Progenitor.Dev", "Ops", "ModsUiOps.cs");
            string execute = SourcePin.MethodBody(ops, "private static object Execute(ModsUiArgs.Request request)");
            SourcePin.AssertUncommented(execute, "bool light = ModsUiArgs.SkipsStatus(request);");
            SourcePin.AssertUncommented(execute, "light ? ModsUiArgs.SkippedStatus(ModsMenu.IsOpen) : ReadStatus(fonts);");
            SourcePin.AssertUncommented(execute, "var before = status(false);");
            Assert.Equal(true, ModsUiArgs.SkippedStatus(false)["skipped"]);
        }
    }
}
