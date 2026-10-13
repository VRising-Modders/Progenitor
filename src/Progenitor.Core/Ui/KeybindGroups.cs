using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// A read-only row of a keybind group: a control the keybind API cannot bind (the right mouse button,
/// the wheel), shown with its fixed input so the player finds every control of the feature in one place.
/// Never editable, never reset, never in a conflict. <paramref name="Note"/> says why it is fixed.
/// </summary>
internal sealed record KeybindInfoRow(string Id, string Name, string ChordText, string Description = "", string Note = "");

/// <summary>One group as defined (<see cref="KeybindGroups.Define"/>). Plain data.</summary>
internal sealed class KeybindGroupDefinition
{
    internal KeybindGroupDefinition(string ownerPluginId, string groupId, string title, IReadOnlyList<string> bindIds,
        IReadOnlyList<KeybindInfoRow> infoRows, Func<string?>? unavailableReason, long order)
    {
        OwnerPluginId = ownerPluginId;
        GroupId = groupId;
        Title = title;
        BindIds = bindIds;
        InfoRows = infoRows;
        UnavailableReasonSource = unavailableReason;
        Order = order;
    }

    internal string OwnerPluginId { get; }

    internal string GroupId { get; }

    internal string Title { get; }

    /// <summary>The owner's bind ids this group takes, in the order its rows are drawn.</summary>
    internal IReadOnlyList<string> BindIds { get; }

    internal IReadOnlyList<KeybindInfoRow> InfoRows { get; }

    internal Func<string?>? UnavailableReasonSource { get; }

    /// <summary>Definition order: groups of one owner follow its section in this order.</summary>
    internal long Order { get; }

    /// <summary>The section-level note while the feature is unavailable (a server setting), else null. Never throws.</summary>
    internal string? UnavailableReason
    {
        get
        {
            var f = UnavailableReasonSource;
            if (f == null) return null;
            try
            {
                string? r = f();
                return string.IsNullOrWhiteSpace(r) ? null : r!.Trim();
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: keybind group '" + GroupId + "' unavailable reason threw: " + e.Message);
                return null;
            }
        }
    }

    /// <summary>The position of <paramref name="bindId"/> in this group, or -1 when it is not one of its binds.</summary>
    internal int IndexOf(string bindId)
    {
        for (int i = 0; i < BindIds.Count; i++)
            if (string.Equals(BindIds[i], bindId, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}

/// <summary>
/// Keybind groups (docs/mods-menu.md#keybind-groups): a feature's binds, moved out of their owner's
/// section on the Keybinds tab into a titled section of their own right after it, in a chosen order, with
/// read-only rows for controls the keybind API cannot bind. Display only: the binds, their ids, their
/// cfg entries and the winner rule are unchanged. Game-free.
/// </summary>
internal static class KeybindGroups
{
    private static readonly object Gate = new();
    private static readonly List<KeybindGroupDefinition> Live = new();
    private static long _order;

    /// <summary>
    /// Show <paramref name="orderedBindIds"/> of <paramref name="ownerPluginId"/> as their own section titled
    /// <paramref name="title"/>, right after the owner's section, rows in this order (an id that is not
    /// registered is skipped), then <paramref name="infoRows"/> as read-only rows. While
    /// <paramref name="unavailableReason"/> returns text, the section shows it as a note (the rows stay and
    /// can still be changed). Defining the same <paramref name="groupId"/> again replaces it. Dispose the
    /// result to remove the group: its binds go back to the owner's section.
    /// </summary>
    internal static IDisposable Define(string ownerPluginId, string groupId, string title, IReadOnlyList<string> orderedBindIds,
        IReadOnlyList<KeybindInfoRow> infoRows, Func<string?>? unavailableReason = null)
    {
        if (string.IsNullOrWhiteSpace(ownerPluginId)) throw new ArgumentException("ownerPluginId is required", nameof(ownerPluginId));
        if (string.IsNullOrWhiteSpace(groupId)) throw new ArgumentException("groupId is required", nameof(groupId));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("title is required", nameof(title));
        var ids = new List<string>();
        foreach (var id in orderedBindIds ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            string t = id.Trim();
            if (!ids.Exists(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) ids.Add(t);
        }

        var infos = new List<KeybindInfoRow>();
        foreach (var r in infoRows ?? Array.Empty<KeybindInfoRow>())
        {
            if (r == null || string.IsNullOrWhiteSpace(r.Id)) continue;
            if (infos.Exists(x => string.Equals(x.Id, r.Id.Trim(), StringComparison.OrdinalIgnoreCase))) continue;
            infos.Add(r with
            {
                Id = r.Id.Trim(),
                Name = string.IsNullOrWhiteSpace(r.Name) ? r.Id.Trim() : r.Name.Trim(),
                ChordText = (r.ChordText ?? "").Trim(),
                Description = (r.Description ?? "").Trim(),
                Note = (r.Note ?? "").Trim(),
            });
        }

        KeybindGroupDefinition def;
        lock (Gate)
        {
            string gid = groupId.Trim();
            long order = ++_order;
            int at = Live.FindIndex(g => string.Equals(g.GroupId, gid, StringComparison.OrdinalIgnoreCase));
            // A replacement keeps its place among the owner's groups.
            if (at >= 0) order = Live[at].Order;
            def = new KeybindGroupDefinition(ownerPluginId.Trim(), gid, title.Trim(), ids, infos, unavailableReason, order);
            if (at >= 0) Live[at] = def;
            else Live.Add(def);
        }

        Keybinds.NoteViewChanged();
        return new Handle(def);
    }

    /// <summary>The groups defined now, in definition order.</summary>
    internal static IReadOnlyList<KeybindGroupDefinition> Snapshot()
    {
        lock (Gate)
        {
            var list = new List<KeybindGroupDefinition>(Live);
            list.Sort((a, b) => a.Order.CompareTo(b.Order));
            return list;
        }
    }

    internal static KeybindGroupDefinition? Find(string groupId)
    {
        lock (Gate)
            return Live.Find(g => string.Equals(g.GroupId, (groupId ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
    }

    internal static int Count
    {
        get { lock (Gate) return Live.Count; }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Live.Clear();
            _order = 0;
        }
    }

    private static void Remove(KeybindGroupDefinition def)
    {
        bool removed;
        // Only this definition: a later Define of the same id replaced it, and that one stays.
        lock (Gate) removed = Live.Remove(def);
        if (removed) Keybinds.NoteViewChanged();
    }

    private sealed class Handle : IDisposable
    {
        private KeybindGroupDefinition? _def;

        internal Handle(KeybindGroupDefinition def) => _def = def;

        public void Dispose()
        {
            var d = System.Threading.Interlocked.Exchange(ref _def, null);
            if (d != null) Remove(d);
        }
    }
}
