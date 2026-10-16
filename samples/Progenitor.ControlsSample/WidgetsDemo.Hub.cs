using Progenitor.Ui;

namespace Progenitor.ControlsSample;

/// <summary>
/// The Widgets page's hub controls (HUB1): a row of compact dropdowns (<see cref="UiWidgets.CompactDropdown"/>), a search
/// box with its Find button (<see cref="UiWidgets.ActionButton"/>), and a centered button with a key hint
/// (<see cref="UiWidgets.Centered"/>, <see cref="UiWidgets.KeyHint"/>). Every event writes a <c>Controls sample: widget
/// hub …</c> log line.
/// </summary>
internal sealed partial class WidgetsDemo
{
    internal IUiDropdown? HubScope { get; private set; }

    internal IUiButton? HubFind { get; private set; }

    internal IUiButton? HubReturn { get; private set; }

    private void BuildHub(IUiPanel panel, IUiTabs tabs, int tab)
    {
        tabs.Add(tab, panel.Heading("Find"));
        panel.BeginInline();
        HubScope = tabs.Add(tab, panel.CompactDropdown("Scope", new[] { "castle", "all" }, "castle", new[] { "Castle", "All" }));
        var match = tabs.Add(tab, panel.CompactDropdown("Match", new[] { "any", "all" }, "any", new[] { "Any word", "All words" }));
        panel.EndInline();
        HubScope.OnChanged(v => Report("hub scope = " + v));
        match.OnChanged(v => Report("hub match = " + v));

        var search = tabs.Add(tab, panel.SearchInput("Search", ""));
        HubFind = tabs.Add(tab, search.ActionButton("Find", text => Report("hub find \"" + text + "\"")));

        // A centered button showing the sample's Open key (Ctrl+F7): pressing the key does what the button does.
        panel.BeginInline();
        HubReturn = tabs.Add(tab, panel.Button("Close this page")).Centered().KeyHint(ControlsDemo.OpenBindId);
        HubReturn.OnClick(() =>
        {
            Report("hub close page");
            _hud?.TogglePage();
        });
        panel.EndInline();
    }
}
