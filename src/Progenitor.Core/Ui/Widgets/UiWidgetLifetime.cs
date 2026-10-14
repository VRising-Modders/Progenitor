using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7: the teardown list of the widget backends that hold process-wide state (the tooltip, the HUD
/// notification queue, Locate pings, an armed world pick, a drag in flight). <see cref="Ui.Uninstall"/>
/// runs it once per Core generation, before the handle sweep, so nothing a widget built survives into
/// the next generation or keeps a frame subscription into a dead one. A backend registers its teardown
/// the first time it starts; the list empties when it runs.
/// </summary>
internal static class UiWidgetLifetime
{
    private static readonly object Gate = new();
    private static readonly List<KeyValuePair<string, Action>> Teardowns = new();

    /// <summary>Add <paramref name="teardown"/> under <paramref name="name"/> (a second registration of a name is ignored).</summary>
    internal static void Register(string name, Action teardown)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("name is required", nameof(name));
        if (teardown == null) throw new ArgumentNullException(nameof(teardown));
        lock (Gate)
        {
            foreach (var t in Teardowns)
                if (string.Equals(t.Key, name, StringComparison.Ordinal)) return;
            Teardowns.Add(new KeyValuePair<string, Action>(name, teardown));
        }
    }

    /// <summary>The registered names, in registration order (tests and pin-diag).</summary>
    internal static IReadOnlyList<string> Names
    {
        get
        {
            lock (Gate)
            {
                var names = new List<string>(Teardowns.Count);
                foreach (var t in Teardowns) names.Add(t.Key);
                return names;
            }
        }
    }

    /// <summary>Run every teardown, newest first, each guarded; returns how many ran. The list is empty afterwards.</summary>
    internal static int Uninstall()
    {
        KeyValuePair<string, Action>[] snapshot;
        lock (Gate)
        {
            snapshot = Teardowns.ToArray();
            Teardowns.Clear();
        }

        int ran = 0;
        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            try
            {
                snapshot[i].Value();
                ran++;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: widget teardown '" + snapshot[i].Key + "' threw: " + e.Message);
            }
        }

        return ran;
    }
}
