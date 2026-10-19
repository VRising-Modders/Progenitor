using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Ui;

/// <summary>
/// Settings organization stage 5: the page's admin-channel rows (<see cref="AdminSettingSpec"/>) — a value of the joined
/// server, drawn with a switch, a selector or a field with <b>Apply</b>, sent through the admin channel and shown as the
/// server answered. The same help-line statuses as the other server rows (waiting, saved by the server, the refusal);
/// a change the spec asks to confirm opens the kit dialog first.
/// </summary>
internal sealed partial class ModSettingsView
{
    internal const string AdminApplyCaption = "Apply";
    internal const string AdminUnknownValue = "…";
    internal const string AdminNotChangedNote = "Not changed.";
    internal const string AdminConfirmTag = "settings-admin-confirm";
    internal const string PendingRestartNote = "Changed: applies at the next restart.";

    private UiDialog? _adminConfirm;

    /// <summary>The confirmation an admin row opened, while it is open (tests and the bridge read it).</summary>
    internal UiDialog? AdminConfirm => _adminConfirm is { IsOpen: true } open ? open : null;

    private void AddAdminRow(IUiPanel panel, ModSettingsSection section, ModSettingsItem item, AdminSettingSpec spec, Action<IUiHandle, string, object?> add)
    {
        var binding = new ItemBinding(item, section, SettingControl.ServerRequest);
        string caption = spec.Label;
        binding.ServerShown = spec.SafeRead();
        IUiHandle handle;
        binding.BuiltViewOnly = spec.ViewOnlyNow();
        if (binding.BuiltViewOnly)
        {
            // Live 2026-10-06: a player who is not an admin reads the row as every other server row does: "Admin only ·
            // <value>" in the control column (the password only Set / Not set), never a grayed control.
            var info = panel.Info(caption, () => (spec.Personal || (spec.Manifest && spec.ReadOnlyNow() != SettingAvailability.AdminOnlyReason) ? "" : AdminOnlyPrefix) + AdminShown(binding, spec), 1);
            binding.Editor = "readonly";
            binding.Sync = () => SyncAdmin(binding, spec, _ => { });
            handle = info;
        }
        else
        switch (spec.Kind)
        {
            case AdminSettingKind.Toggle:
            {
                var toggle = panel.Toggle(caption, IsTrue(binding.ServerShown ?? ""));
                void Show(string? shown)
                {
                    if (toggle.IsDisposed) return;
                    toggle.Value = IsTrue(shown ?? "");
                    toggle.Text = caption;
                }

                toggle.OnChanged(v => SendAdmin(binding, spec, v ? "true" : "false", Show));
                binding.Editor = "toggle";
                binding.Apply = () => SendAdmin(binding, spec, toggle.Value ? "true" : "false", Show);
                binding.Sync = () => SyncAdmin(binding, spec, Show);
                handle = toggle;
                break;
            }

            case AdminSettingKind.Choice when spec.ChoiceRadio:
            {
                // RADIO1: a "this or that" choice is a radio list; each option carries its own explanation.
                var radio = panel.Radio(caption, spec.Choices, binding.ServerShown ?? "", spec.ChoiceLabels, spec.ChoiceDescriptions);
                void Show(string? shown)
                {
                    if (!radio.IsDisposed) radio.Value = shown ?? "";
                }

                radio.OnChanged(v => SendAdmin(binding, spec, v, Show));
                binding.Editor = "radio";
                binding.Apply = () => SendAdmin(binding, spec, radio.Value, Show);
                binding.Sync = () => SyncAdmin(binding, spec, Show);
                handle = radio;
                break;
            }

            case AdminSettingKind.Date:
            {
                // WIPECONFIRM1: the date-time picker, in the server's zone; Done goes through the row's confirmation.
                var field = UiDateTimeField.Create(panel, caption, binding.ServerShown ?? "", Networking.ServerRulesRows.ServerZone(),
                    DateTimePickerPrecision.Minute);
                field.ZoneSource = Networking.ServerRulesRows.ServerZone;
                void Show(string? shown)
                {
                    if (!field.IsDisposed) field.Value = shown ?? "";
                }

                field.OnChanged(iso => SendAdmin(binding, spec, iso, Show));
                binding.DateField = field;
                binding.Editor = "datetime";
                binding.Apply = () => SendAdmin(binding, spec, field.Value, Show);
                binding.Sync = () => SyncAdmin(binding, spec, Show);
                handle = field;
                break;
            }

            case AdminSettingKind.Slider:
            {
                // Scoped settings: the slider with its exact number box, as a local slider row; a settled drag is sent.
                var slider = panel.SliderField(caption, (float)spec.SliderMin, (float)spec.SliderMax, (float)spec.Step,
                    (float)AdminNumber(binding.ServerShown, spec));
                void Show(string? shown)
                {
                    if (!slider.IsDisposed) slider.Value = (float)AdminNumber(shown, spec);
                }

                slider.OnChanged(v => SendAdmin(binding, spec, AdminNumberText(v, spec), Show));
                binding.Editor = "slider";
                binding.Apply = () => SendAdmin(binding, spec, AdminNumberText(slider.Value, spec), Show);
                binding.Sync = () => SyncAdmin(binding, spec, Show);
                handle = slider;
                break;
            }

            case AdminSettingKind.ItemList:
                handle = AddItemListRow(panel, binding, spec, caption);
                break;

            case AdminSettingKind.Choice:
            {
                // OPTIONHOVER1: the options' explanations are not drawn in the list; the column shows the hovered one's.
                var dropdown = panel.Dropdown(caption, spec.Choices, binding.ServerShown ?? "", spec.ChoiceLabels, spec.ChoiceDescriptions);
                void Show(string? shown)
                {
                    if (!dropdown.IsDisposed) dropdown.Value = shown ?? "";
                }

                dropdown.OnChanged(v => SendAdmin(binding, spec, v, Show));
                binding.Editor = "dropdown";
                binding.Apply = () => SendAdmin(binding, spec, dropdown.Value, Show);
                binding.Sync = () => SyncAdmin(binding, spec, Show);
                handle = dropdown;
                break;
            }

            default:
            {
                // A number or a list keeps its field and Apply: one request per change, inside the server's edit rate.
                // A write-only row (the password) never shows the server's value: its field starts and ends empty.
                // Settings organization review (live): Apply sits at the right of the field, on the field's row.
                panel.BeginInline();
                IUiInput input;
                IUiButton apply;
                try
                {
                    input = panel.Input(caption, spec.WriteOnly ? "" : binding.ServerShown ?? "");
                    apply = panel.Button(AdminApplyCaption);
                }
                finally { panel.EndInline(); }

                // INLINEAPPLY1: the steel game button, like every other button on the page.
                UseGameButton(binding, apply);

                // The password is typed masked.
                if (spec.WriteOnly) input.SetMasked(true);
                string lastShown = spec.WriteOnly ? "" : binding.ServerShown ?? "";
                void Show(string? shown)
                {
                    if (input.IsDisposed) return;
                    lastShown = spec.WriteOnly ? "" : shown ?? "";
                    input.Value = lastShown;
                }

                void Press()
                {
                    string typed = spec.WriteOnly || spec.Manifest ? input.Value ?? "" : UiCommitText.Trim(input.Value ?? "");
                    int max = spec.MaxNowValue();
                    if (spec.Kind == AdminSettingKind.Number
                        && (!int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                            || (spec.RangeChecked && (n < spec.Min || n > max))))
                    {
                        binding.Status = "Give whole " + (spec.Units.Length > 0 ? spec.Units : "numbers") + ", "
                            + spec.Min.ToString(CultureInfo.InvariantCulture) + " to " + max.ToString(CultureInfo.InvariantCulture) + ".";
                        WriteHelp(binding);
                        return;
                    }

                    if (spec.Kind == AdminSettingKind.Text && typed.Length > spec.MaxLength)
                    {
                        binding.Status = "At most " + spec.MaxLength.ToString(CultureInfo.InvariantCulture) + " characters.";
                        WriteHelp(binding);
                        return;
                    }

                    SendAdmin(binding, spec, typed, Show);
                }

                apply.OnClick(Press);
                binding.Apply = Press;
                binding.Input = input;
                binding.Handles.Add(apply);
                // A value the server sent replaces the field's text only while nothing new is typed there.
                binding.Sync = () => SyncAdmin(binding, spec, shown =>
                {
                    if (input.IsDisposed) return;
                    if (spec.WriteOnly) return;
                    if (string.Equals(input.Value ?? "", lastShown, StringComparison.Ordinal)) Show(shown);
                    else binding.ServerShown = lastShown; // Keep the displayed baseline while a draft is in progress.
                });
                handle = input;
                break;
            }
        }

        binding.Handles.Insert(0, handle);
        binding.Main = handle;
        if (spec.Manifest && spec.DefaultValue != null && !binding.BuiltViewOnly)
        {
            var reset = panel.GutterLink(handle, ResetGlyph, ResetTooltipPrefix + spec.DefaultValue);
            reset.OnClick(() => ResetRemote(binding, spec));
            binding.Reset = reset;
            add(reset, RowSettingReset, (Func<object?>)(() => new Dictionary<string, object?>
            {
                ["id"] = spec.SettingId,
                ["visible"] = !reset.IsDisposed && reset.Visible,
                ["state"] = ResetStateName(ResetState(binding)),
            }));
        }
        _bindings.Add(binding);
        WatchHover(panel, binding);
        add(handle, RowSetting, (Func<object?>)(() => DescribeAdmin(binding, spec)));
    }

    /// <summary>A slider row's value as a number (the bounds' low end while the server has not said).</summary>
    private static double AdminNumber(string? text, AdminSettingSpec spec) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && !double.IsNaN(v) && !double.IsInfinity(v)
            ? Math.Max(spec.SliderMin, Math.Min(spec.SliderMax, v)) : spec.SliderMin;

    /// <summary>A slider value as the server parses it: a whole number, or the step's precision.</summary>
    internal static string AdminNumberText(double value, AdminSettingSpec spec)
    {
        if (spec.Integer) return ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        double step = spec.Step > 0 ? spec.Step : 0;
        if (step > 0) value = spec.SliderMin + Math.Round((value - spec.SliderMin) / step) * step;
        value = Math.Max(spec.SliderMin, Math.Min(spec.SliderMax, value));
        return Math.Round(value, 6).ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>The value a read-only admin row shows: the server's, as a person reads it, or <see cref="AdminUnknownValue"/>.</summary>
    private static string AdminShown(ItemBinding binding, AdminSettingSpec spec)
    {
        string? value = binding.ServerShown ?? spec.SafeRead();
        return value == null ? AdminUnknownValue : OrNotSet(spec.DisplayOf(value));
    }

    /// <summary>True when an admin row's read-only drawing no longer matches this player (an admin now, or not): rebuild.</summary>
    private bool AdminViewChanged()
    {
        foreach (var b in _bindings)
            if (b.Item.Admin is { } spec && b.BuiltViewOnly != spec.ViewOnlyNow()) return true;
        return false;
    }

    /// <summary>The row's state each time the page re-evaluates: the lock, and the server's latest value.</summary>
    private void AdminPredicates(ItemBinding binding, AdminSettingSpec spec, ref bool enabled, ref string why)
    {
        string? locked = spec.LockNote();
        string? readOnly = spec.ReadOnlyNow();
        if (binding.BuiltViewOnly)
        {
            // Live 2026-10-06: a row a non-admin only reads is drawn like the other "Admin only · value" rows, at normal
            // brightness (they are not grayed: nothing on them is a control); the help line says why.
            why = locked ?? readOnly ?? why;
        }
        else if (locked != null)
        {
            enabled = false;
            why = locked;
        }
        else if (readOnly != null)
        {
            enabled = false;
            why = readOnly;
        }
        else if (enabled && spec.SafeRead() == null)
        {
            why = WaitingNote;
        }
        else if (enabled)
        {
            // Settings organization stage 6: a start-command override, and a value that waits for a restart.
            var parts = new List<string>(2);
            if (spec.NoteNow() is { } note) parts.Add(note);
            if (spec.PendingNow()) parts.Add(PendingRestartNote);
            if (parts.Count > 0) why = string.Join(" ", parts);
        }

        try { binding.Sync(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings admin row sync threw: " + e.Message); }
    }

    private static void SyncAdmin(ItemBinding binding, AdminSettingSpec spec, Action<string?> write)
    {
        if (binding.RequestPending) return;
        string? now = spec.SafeRead();
        if (string.Equals(now, binding.ServerShown, StringComparison.Ordinal)) return;
        binding.ServerShown = now;
        if (binding.Main is { IsDisposed: true }) return;
        write(now);
    }

    /// <summary>
    /// Send an admin's value: refused while a request waits or the row is grayed or locked (the control is put back);
    /// nothing for an unchanged value; else, after the spec's confirmation when it asks one, the spec's request.
    /// </summary>
    private void SendAdmin(ItemBinding binding, AdminSettingSpec spec, string value, Action<string?> shown)
    {
        string current = binding.ServerShown ?? "";
        if (binding.RequestPending || !binding.Enabled || spec.ViewOnlyNow() || spec.ReadOnlyNow() != null || binding.ServerShown == null)
        {
            string why = binding.RequestPending ? "a request is already waiting for the server"
                : !binding.Enabled ? binding.Why
                : "the server has not sent this value yet";
            Plugin.Logger?.LogInfo("Ui: settings " + spec.SettingId + " refused: " + why);
            if (!binding.RequestPending)
            {
                shown(binding.ServerShown);
                if (binding.Enabled)
                {
                    binding.Status = WaitingNote;
                    WriteHelp(binding);
                }
            }

            return;
        }

        if (!spec.WriteOnly && string.Equals(value ?? "", current, StringComparison.Ordinal))
        {
            shown(current);
            return;
        }

        AdminSettingConfirm? confirm = null;
        try { confirm = spec.Confirm?.Invoke(current, value ?? ""); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings " + spec.SettingId + " confirmation check threw: " + e.Message); }
        if (confirm == null)
        {
            SendAdminNow(binding, spec, value ?? "", shown);
            return;
        }

        var factory = ModPagePane.DialogFactory;
        if (factory == null)
        {
            // Never send a change that needs a yes without asking for it.
            Plugin.Logger?.LogWarning("Ui: settings " + spec.SettingId + " needs a confirmation and no dialog can be opened; not sent.");
            shown(current);
            binding.Status = AdminNotChangedNote;
            WriteHelp(binding);
            return;
        }

        CloseAdminConfirm();
        UiDialog dialog;
        try
        {
            dialog = confirm.Word.Length > 0
                ? OpenTypedConfirm(factory, spec.Key, confirm)
                : UiDialog.Open(factory, AdminConfirmTag, "confirm", spec.Key, confirm.Heading, confirm.Body,
                    confirm.Yes, LabelTone.Orange, ModPageCopy.PromptNo, facts: confirm.Facts, danger: confirm.Danger);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings " + spec.SettingId + " confirmation dialog threw: " + e.Message);
            shown(current);
            return;
        }

        _adminConfirm = dialog;
        // Until the answer, the control shows the server's value.
        shown(current);
        string pending = value ?? "";
        void Confirmed()
        {
            _adminConfirm = null;
            if (binding.Main is { IsDisposed: true } || !binding.Enabled)
            {
                Plugin.Logger?.LogWarning("Ui: settings " + spec.SettingId + " confirmed after its row was gone or grayed; not sent.");
                return;
            }

            SendAdminNow(binding, spec, pending, shown);
        }

        dialog.OnYes(Confirmed);
        // WIPECONFIRM1: a typed confirmation answers through its submit (only once the word matches).
        if (confirm.Word.Length > 0) dialog.OnSubmit(_ => Confirmed(), null);
        dialog.OnNo(() =>
        {
            _adminConfirm = null;
            if (binding.Main is { IsDisposed: true }) return;
            shown(binding.ServerShown);
            binding.Status = AdminNotChangedNote;
            WriteHelp(binding);
        });
    }

    /// <summary>
    /// WIPECONFIRM1: a confirmation the admin answers by typing <see cref="AdminSettingConfirm.Word"/> (a Prompt whose
    /// main button stays disabled until the word matches, case-insensitive), destructive in tone when it can wipe.
    /// </summary>
    private static UiDialog OpenTypedConfirm(Func<string, IUiPanel> factory, string key, AdminSettingConfirm confirm)
    {
        var content = new DialogContent
        {
            Shape = UiDialogShape.Prompt,
            Title = confirm.Heading,
            Body = confirm.Body,
            // WIPEPOLISH1: the label/value lines, left-aligned (the warning among them in the danger tone).
            Facts = DialogFacts.Clean(confirm.Facts),
            InputInitial = "",
            InputPlaceholder = "Type " + confirm.Word,
            MaxLength = 16,
            ConfirmWord = confirm.Word,
            MainCaption = confirm.Yes,
            MainTone = confirm.Danger ? CellTone.Danger : CellTone.Orange,
            CancelCaption = ModPageCopy.PromptNo,
            Danger = confirm.Danger,
            RowKey = AdminConfirmTag + "-buttons",
        };
        return UiDialog.OpenContent(factory, content, AdminConfirmTag, "confirm-typed", key);
    }

    private void SendAdminNow(ItemBinding binding, AdminSettingSpec spec, string value, Action<string?> shown)
    {
        binding.RequestPending = true;
        binding.Status = WaitingNote;
        Select(binding.Item.Id);
        WriteHelp(binding);
        bool answered = false;
        void Done(bool ok, string message)
        {
            if (answered) return;
            answered = true;
            binding.RequestPending = false;
            binding.ServerShown = spec.SafeRead();
            if (binding.Main is { IsDisposed: true }) return;
            string text = (message ?? "").Trim();
            binding.Status = ok ? (text.Length > 0 ? text : ServerSavedNote) : (text.Length > 0 ? text : "The request failed.");
            try { shown(binding.ServerShown); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings admin control update threw: " + e.Message); }
            WriteHelp(binding);
        }

        try
        {
            if (spec.SendExpected != null) _requests.Add(spec.SendExpected(value, binding.ServerShown, Done));
            else spec.Send(value, Done);
        }
        catch (Exception e) { Done(false, e.Message); }
    }

    private void CloseAdminConfirm()
    {
        var open = _adminConfirm;
        _adminConfirm = null;
        try { open?.Close(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings admin confirmation close threw: " + e.Message); }
    }

    private Dictionary<string, object?> DescribeAdmin(ItemBinding binding, AdminSettingSpec spec)
    {
        string? value = binding.ServerShown;
        string? lockedBy = spec.LockedByNow();
        string? locked = lockedBy != null ? Networking.ServerSettingsLock.NoteFor(lockedBy) : null;
        var d = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = spec.SettingId,
            ["kind"] = "admin-setting",
            ["key"] = spec.Key,
            ["section"] = AuthorTitle(binding.Section),
            ["control"] = binding.BuiltViewOnly ? "readonly" : spec.Kind switch
            {
                AdminSettingKind.Toggle => "toggle",
                AdminSettingKind.Choice => spec.ChoiceRadio ? "radio" : "dropdown",
                AdminSettingKind.Number => "number",
                AdminSettingKind.Slider => "slider",
                AdminSettingKind.ItemList => "itemlist",
                _ => "text",
            },
            ["label"] = spec.Label,
            ["displayName"] = spec.Label,
            ["help"] = spec.Help,
            ["helpLine"] = binding.HelpLine,
            ["visible"] = binding.Visible,
            ["enabled"] = binding.Enabled,
            ["why"] = binding.Why,
            ["value"] = value,
            ["display"] = value == null ? AdminUnknownValue : spec.DisplayOf(value),
            ["known"] = value != null,
            ["locked"] = locked != null,
            ["lockNote"] = locked ?? "",
            ["lockedBy"] = lockedBy,
            ["requestPending"] = binding.RequestPending,
            ["status"] = binding.Status,
            ["store"] = spec.Manifest ? (spec.Personal ? "server-player" : "server-config") : SettingPlaces.StoreJoinAdmin,
            ["default"] = spec.DefaultValue,
            ["bounds"] = spec.Bounds,
            ["confirmOpen"] = AdminConfirm != null,
            ["pending"] = spec.PendingNow(),
            ["needsRestart"] = binding.Item.Options.NeedsRestart,
            ["readOnlyReason"] = spec.ReadOnlyNow() ?? "",
            ["note"] = spec.NoteNow() ?? "",
            ["writeOnly"] = spec.WriteOnly,
            ["viewOnly"] = binding.BuiltViewOnly,
        };
        if (spec.ExtraReadback != null)
        {
            try { d["serverRule"] = spec.ExtraReadback(); }
            catch { d["serverRule"] = null; }
        }
        if (spec.Kind == AdminSettingKind.Choice)
        {
            d["choices"] = new List<string>(spec.Choices);
            d["choiceLabels"] = new List<string>(spec.ChoiceLabels);
        }

        if (spec.Kind == AdminSettingKind.Slider)
        {
            d["min"] = spec.SliderMin;
            d["max"] = spec.SliderMax;
            d["step"] = spec.Step;
            d["units"] = spec.Units;
        }

        if (spec.Kind == AdminSettingKind.ItemList) AddItemListReadback(d, binding, spec);

        if (spec.Kind == AdminSettingKind.Number)
        {
            d["min"] = spec.Min;
            d["max"] = spec.MaxNowValue();
            d["rangeChecked"] = spec.RangeChecked;
            d["units"] = spec.Units;
        }

        // A write-only field's text is never read back (only its length).
        if (binding.Input is { IsDisposed: false } input)
        {
            if (spec.WriteOnly)
            {
                d["fieldLength"] = (input.Value ?? "").Length;
                d["masked"] = true;
            }
            else d["field"] = input.Value ?? "";
        }
        AddScopeReadback(d, binding.Section, binding);
        if (!spec.Manifest) SettingPlaces.AddReadback(d, spec.SettingId, SettingPlaces.PluginId, keybind: false);
        return d;
    }
}
