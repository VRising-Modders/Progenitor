#if PROGENITOR_TESTS && !PROGENITOR_SCOPED_RUNTIME_TESTS
using System;
using System.Collections.Generic;
using System.Linq;

namespace Progenitor.Networking;

// UI-only transport seam. Define PROGENITOR_SCOPED_RUNTIME_TESTS when the real runtime is linked.
internal static class ScopedSettings
{
    internal static IReadOnlyList<ScopedSettingRecord> ClientRows { get; private set; } = Array.Empty<ScopedSettingRecord>();
    internal static bool ClientReady { get; private set; }
    internal static bool ClientCanEditServerWide { get; private set; }
    internal static string ClientServerName { get; private set; } = "";
    internal static int ClientRevision { get; private set; }
    internal static event Action<string, string>? ClientValueChanged;
    internal static readonly List<(string Id, string Value, string? Expected)> Requests = new();
    internal static string? ReadClient(string id) => ClientReady ? ClientRows.FirstOrDefault(r => r.Id == id)?.Value : null;
    internal static IDisposable Request(string id, string value, string? expected, Action<ServerSettingResult> done)
    {
        Requests.Add((id, value, expected));
        var row = ClientRows.FirstOrDefault(r => r.Id == id);
        bool ok = ClientReady && row != null && row.Editable && (row.Personal || ClientCanEditServerWide)
            && row.Value == expected;
        if (ok) { row!.Value = value; ClientValueChanged?.Invoke(id, value); }
        done(new ServerSettingResult(ok, row?.Value ?? "", ok ? "" : "Refused."));
        return new Lease();
    }
    internal static void SetForUiTests(bool ready, bool admin, params ScopedSettingRecord[] rows)
    {
        ClientReady = ready; ClientCanEditServerWide = admin; ClientRows = rows;
        ClientServerName = ready ? "Test server" : ""; ClientRevision++; Requests.Clear();
    }
    private sealed class Lease : IDisposable { public void Dispose() { } }
}
#endif
