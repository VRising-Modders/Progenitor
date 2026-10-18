using System;

namespace Progenitor.Ui;

internal sealed partial class ModSettingsView
{
    internal int PersonalResetCount => ResettableRows().Count + RemotePersonalBindings();

    private bool CanResetRemote(ItemBinding binding, AdminSettingSpec spec) =>
        spec.Manifest && spec.DefaultValue != null && binding.Enabled && binding.ShownByIf
        && !binding.RequestPending && !spec.ViewOnlyNow() && spec.ReadOnlyNow() == null
        && spec.SafeRead() != null && !string.Equals(spec.SafeRead(), spec.DefaultValue, StringComparison.Ordinal);

    private int RemotePersonalBindings()
    {
        int count = 0;
        foreach (var binding in _bindings)
            if (binding.Item.Admin is { Personal: true } spec && CanResetRemote(binding, spec)) count++;
        return count;
    }

    internal int ResetRemotePersonal()
    {
        int count = 0;
        foreach (var binding in _bindings)
            if (binding.Item.Admin is { Personal: true } spec && CanResetRemote(binding, spec))
            {
                ResetRemote(binding, spec);
                count++;
            }
        return count;
    }

    private void ResetRemote(ItemBinding binding, AdminSettingSpec spec)
    {
        if (!CanResetRemote(binding, spec)) return;
        SendAdmin(binding, spec, spec.DefaultValue!, shown =>
        {
            // Force the existing control's synchronization even when the response already updated its baseline.
            binding.ServerShown = null;
            if (binding.Input is { IsDisposed: false } input) input.Value = shown ?? "";
            binding.Sync();
        });
    }
}
