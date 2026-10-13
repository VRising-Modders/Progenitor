using System;
using System.Globalization;
using System.Text;
using Progenitor.Players.Forms;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
using Unity.Entities;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Experimental <c>.form</c> — <c>PlayerForms</c>, not a <c>ctx</c> property. <c>.form</c> lists
/// <c>AvailableForms</c> and <c>IsReady</c> (guard readiness, not qualification). <c>.form begin</c>
/// calls <c>TryBegin</c> for the named form, or the only listed form, and
/// <c>ctx.Track</c>s every non-null handle even when the call returns false. <c>.form return</c>
/// calls <c>TryReturn</c>.
///
/// <para>No profile is qualified on current main, so a normal begin is expected to come back
/// refused. The sample still makes the call and prints the error. It does not bypass
/// qualification. Source-checked and game-free tested; not live-verified.</para>
/// </summary>
internal static class SampleForms
{
    internal const string Area = "form";

    internal const string OwnerLabel = "progenitor.sample";

    internal const double DefaultSeconds = 30;

    internal const string UsageReply = "Usage: .form | .form begin [name] [seconds] | .form return   (experimental; seconds 0-300)";

    internal const string NoWorldReply = "No world yet; PlayerForms.TryBegin needs the server world and your user entity.";

    internal const string NoUserReply = "No user entity on this command; there is nobody to put in a form.";

    internal const string NothingHeldReply = "No form handle is held. .form begin is what takes one, including a recovery handle from a refused begin.";

    internal enum Kind
    {
        List = 1,
        Begin = 2,
        Return = 3,
    }

    internal static bool TryParse(string? verb, string? rest, out Kind kind, out string name, out double seconds)
    {
        kind = Kind.List;
        name = "";
        seconds = DefaultSeconds;
        string tail = (rest ?? "").Trim();
        switch ((verb ?? "").Trim().ToLowerInvariant())
        {
            case "":
            case "list":
                return tail.Length == 0;
            case "return":
                kind = Kind.Return;
                return tail.Length == 0;
            case "begin":
                kind = Kind.Begin;
                return TryBeginArgs(tail, out name, out seconds);
            default:
                return false;
        }
    }

    internal static string CatalogReply(bool ready)
    {
        var text = new StringBuilder();
        text.Append("PlayerForms is experimental. IsReady=");
        text.Append(ready);
        text.Append(" means the Bootstrap recovery guards answer, not that a form is qualified.");
        var forms = PlayerForms.AvailableForms;
        if (forms.Count == 0)
        {
            text.Append(" AvailableForms is empty.");
            return text.ToString();
        }

        for (int i = 0; i < forms.Count; i++)
        {
            var form = forms[i];
            text.Append(" [");
            text.Append(form.Name);
            text.Append(" prefab=");
            text.Append(form.PrefabName);
            text.Append(" guid=");
            text.Append(form.PrefabGuid.ToString(CultureInfo.InvariantCulture));
            text.Append(" verification=");
            text.Append(form.Verification);
            text.Append(']');
        }

        text.Append(" .form begin calls TryBegin and is expected to be refused until a profile is qualified.");
        return text.ToString();
    }

    internal static bool TryPick(string name, out int prefabGuid, out string label, out string? error)
    {
        prefabGuid = 0;
        label = "";
        error = null;
        var forms = PlayerForms.AvailableForms;
        if (forms.Count == 0)
        {
            error = "AvailableForms is empty; there is nothing to begin.";
            return false;
        }

        if (name.Length == 0)
        {
            if (forms.Count != 1)
            {
                error = "Name the form. AvailableForms has " + forms.Count.ToString(CultureInfo.InvariantCulture) + " entries.";
                return false;
            }

            prefabGuid = forms[0].PrefabGuid;
            label = forms[0].Name;
            return true;
        }

        for (int i = 0; i < forms.Count; i++)
        {
            var form = forms[i];
            if (string.Equals(form.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(form.PrefabName, name, StringComparison.OrdinalIgnoreCase))
            {
                prefabGuid = form.PrefabGuid;
                label = form.Name;
                return true;
            }
        }

        error = "No listed form matches '" + name + "'.";
        return false;
    }

    internal static string BeginReply(bool success, string error, long? handleId, string? state)
    {
        string head = success
            ? "TryBegin accepted."
            : "TryBegin refused. " + (string.IsNullOrEmpty(error) ? "No detail." : error);
        if (handleId is long id)
            head += " Recovery handle id=" + id.ToString(CultureInfo.InvariantCulture)
                + " was tracked. state=" + (state ?? "");
        else
            head += " No handle was returned.";
        return head;
    }

    internal static string ReturnReply(bool returned, string error, long id) =>
        returned
            ? "TryReturn released handle " + id.ToString(CultureInfo.InvariantCulture) + "."
            : "TryReturn did not release handle " + id.ToString(CultureInfo.InvariantCulture)
                + ". " + error + " The handle is still held; try .form return again.";

#if !PROGENITOR_TESTS
    private static PlayerFormHandle? _handle;

    internal static void HandleCommand(ICommandContext ctx, Kind kind, string name, double seconds)
    {
        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }

        switch (kind)
        {
            case Kind.List:
                ctx.Reply(CatalogReply(PlayerForms.IsReady));
                SampleLog.Info(Area, "list ready=" + PlayerForms.IsReady + " count=" + PlayerForms.AvailableForms.Count);
                return;
            case Kind.Return:
                ReturnHeld(ctx);
                return;
            default:
                Begin(ctx, plugin, name, seconds);
                return;
        }
    }

    internal static void Detach()
    {
        _handle = null;
    }

    private static void Begin(ICommandContext ctx, SampleContext plugin, string name, double seconds)
    {
        if (!TryPick(name, out int guid, out string label, out string? pickError))
        {
            ctx.Reply(pickError ?? UsageReply);
            return;
        }

        if (plugin.World is not World world)
        {
            ctx.Reply(NoWorldReply);
            return;
        }

        if (ctx.UserEntity == Entity.Null)
        {
            ctx.Reply(NoUserReply);
            return;
        }

        bool ok = PlayerForms.TryBegin(
            world, ctx.UserEntity, guid, TimeSpan.FromSeconds(seconds), OwnerLabel, out PlayerFormHandle? handle, out string error);
        if (handle != null)
            plugin.Track(handle);
        _handle = handle;
        ctx.Reply(BeginReply(ok, error, handle?.Id, handle?.State));
        SampleLog.Info(Area, "begin " + label + " ok=" + ok + " handle=" + (handle == null ? "none" : handle.Id.ToString(CultureInfo.InvariantCulture)));
    }

    private static void ReturnHeld(ICommandContext ctx)
    {
        var handle = _handle;
        if (handle == null)
        {
            ctx.Reply(NothingHeldReply);
            return;
        }

        bool returned = handle.TryReturn(out string error);
        if (returned) _handle = null;
        ctx.Reply(ReturnReply(returned, error, handle.Id));
        SampleLog.Info(Area, "return id=" + handle.Id + " returned=" + returned);
    }
#endif

    /// <summary>
    /// <paramref name="tail"/> is the text after <c>begin</c>. A trailing number is the duration;
    /// everything before it is the form name, so <c>Lord Styx 15</c> stays one name. A number on
    /// its own is the duration. An out-of-range number is refused.
    /// </summary>
    private static bool TryBeginArgs(string tail, out string name, out double seconds)
    {
        name = "";
        seconds = DefaultSeconds;
        if (tail.Length == 0) return true;
        int split = tail.LastIndexOf(' ');
        string last = split < 0 ? tail : tail.Substring(split + 1);
        string head = split < 0 ? "" : tail.Substring(0, split).Trim();
        if (double.TryParse(last, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            if (!TrySeconds(last, out seconds)) return false;
            name = head;
            return true;
        }

        if (head.Length != 0 && double.TryParse(head, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return false;
        name = tail;
        return true;
    }

    private static bool TrySeconds(string text, out double seconds) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
        && double.IsFinite(seconds) && seconds > 0 && seconds <= 300;
}
