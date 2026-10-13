using System;
using System.Collections.Generic;

namespace Progenitor.Install.Admission;

/// <summary>
/// HOT2: the production <see cref="IAdmissionSnapshotSource"/>. Reads Bootstrap's narrow loader-state
/// snapshot through <see cref="BootstrapLink"/> (additive, versioned by
/// <see cref="BootstrapLink.AdmissionSnapshotCapability"/>) and turns its plain values into a
/// <see cref="RuntimeGenerationSnapshot"/>.
///
/// <para><b>An older Bootstrap is not an error.</b> Capability <c>0</c> — Bootstrap absent, or a
/// Bootstrap built before HOT2 — is a valid pairing, and this source then returns
/// <see cref="RuntimeGenerationSnapshot.Unavailable"/> naming the capability. Admission is
/// <see cref="AdmissionDecision.Unknown"/>, the planner keeps the restart route, and the pane says
/// so. That is the whole failure mode; nothing half-admits.</para>
///
/// <para><b>The server half is never inferred from a folder.</b>
/// <see cref="RuntimeGenerationSnapshot.LocalServerRuntimeConnected"/> is true only when this
/// process <em>is</em> the server Bootstrap reports (<c>ServerProcess</c>). A configured local-server
/// folder tells us where files go, not that a running server can be hot-managed — the hot-load
/// handoff (§hot-load item 3) forbids reading a running-server decision out of a folder, so a
/// LocalServer or Both target from a client process is refused with
/// <see cref="AdmissionReasonCode.LocalServerRuntimeUnavailable"/> and offered its real path,
/// install for the server's next start.</para>
/// </summary>
internal sealed record BootstrapSnapshotValues(
    bool ServerProcess,
    long Generation,
    bool LoaderBusy,
    bool PriorGenerationPinned,
    bool PluginLoaded,
    string PluginName,
    string PluginVersion,
    string PluginImageSha256,
    long PluginImageSize,
    bool UnsafeHarmonyMarked,
    bool AllowlistAllows,
    string AllowlistStatus,
    string ReloadFolder,
    bool ProcessLifetimeMarked = false);

internal sealed class BootstrapAdmissionSnapshotSource : IAdmissionSnapshotSource
{
    /// <summary>The unavailable reason when this Bootstrap has no snapshot API.</summary>
    public const string CapabilityMissingReason =
        "this Bootstrap does not publish ProcessHost.AdmissionSnapshotCapability >= 1 "
        + "(Bootstrap is absent or predates job HOT2); a Bootstrap change needs a process restart";

    /// <summary>The unavailable reason when the capability is there but the capture answered nothing.</summary>
    public const string CaptureFailedReason =
        "the Bootstrap admission snapshot capture returned nothing";

    private readonly Func<int> _capability;
    private readonly CaptureValues? _capture;
    private readonly IReadOnlyList<CompatibilityIdentity> _identities;

    /// <summary>
    /// The link call this source makes, injectable so a test can drive a fake Bootstrap (available,
    /// unavailable, and a Bootstrap too old to answer) with no game and no reflection.
    /// </summary>
    internal delegate bool CaptureValues(
        string pluginId,
        string? fileName,
        string? sha256,
        out BootstrapSnapshotValues? values);

    public BootstrapAdmissionSnapshotSource(
        IReadOnlyList<CompatibilityIdentity>? identities = null,
        Func<int>? capability = null,
        CaptureValues? capture = null)
    {
        _identities = identities ?? Array.Empty<CompatibilityIdentity>();
#if PROGENITOR_TESTS
        // The test build does not compile BootstrapLink; a test always passes both.
        _capability = capability ?? (() => 0);
        _capture = capture;
#else
        _capability = capability ?? (() => BootstrapLink.AdmissionSnapshotCapability);
        _capture = capture ?? BootstrapLink.TryCaptureAdmissionSnapshot;
#endif
    }

    public RuntimeGenerationSnapshot Capture(string pluginId, InstallTarget target) =>
        Capture(pluginId, target, null);

    public RuntimeGenerationSnapshot Capture(string pluginId, InstallTarget target, AdmissionCandidate? candidate)
    {
        int capability;
        try { capability = _capability(); }
        catch { capability = 0; }
        if (capability < 1 || _capture == null) return RuntimeGenerationSnapshot.Unavailable(CapabilityMissingReason);

        BootstrapSnapshotValues? values;
        try
        {
            if (!_capture(pluginId ?? "", candidate?.FileName, candidate?.Sha256, out values) || values == null)
                return RuntimeGenerationSnapshot.Unavailable(CaptureFailedReason);
        }
        catch (Exception e)
        {
            return RuntimeGenerationSnapshot.Unavailable(CaptureFailedReason + ": " + e.Message);
        }

        return Compose(values, pluginId ?? "", _identities);
    }

    /// <summary>
    /// The pure mapping from Bootstrap's values to the snapshot, separated so the model's own tests
    /// do not need a link at all.
    /// </summary>
    internal static RuntimeGenerationSnapshot Compose(
        BootstrapSnapshotValues values,
        string pluginId,
        IReadOnlyList<CompatibilityIdentity>? identities)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));

        // CurrentPlugin is "what stands in the way of a fresh load of this id", which is wider than
        // "an instance is alive": a same-name ALC that is still retiring, and a process-lifetime
        // UnsafeHarmony mark (never cleared, even after an unload), both block one too, and each has
        // its own reason code in the evaluator. Folding them into LoaderBusy instead would refuse
        // for the right cause under the wrong sentence.
        bool blocks = values.PluginLoaded || values.PriorGenerationPinned || values.UnsafeHarmonyMarked
            || values.ProcessLifetimeMarked;
        LoadedPluginState? current = blocks
            ? new LoadedPluginState(
                pluginId ?? "",
                QualificationPolicy.IsSha256(values.PluginImageSha256) && values.PluginImageSize > 0
                    ? new ArtifactIdentity(values.PluginImageSha256, values.PluginImageSize)
                    : null,
                values.UnsafeHarmonyMarked,
                values.PriorGenerationPinned,
                values.ProcessLifetimeMarked)
            : null;

        return new RuntimeGenerationSnapshot(
            Available: true,
            UnavailableReason: null,
            ProcessSide: values.ServerProcess ? AdmissionSide.Server : AdmissionSide.Client,
            Generation: values.Generation,
            LoaderBusy: values.LoaderBusy,
            AllowlistAllows: values.AllowlistAllows,
            // The handoff's rule, in one expression: only a process that IS the server can report a
            // running-server lifecycle connection. A folder never can.
            LocalServerRuntimeConnected: values.ServerProcess,
            CurrentPlugin: current,
            Identities: identities ?? Array.Empty<CompatibilityIdentity>());
    }

    /// <summary>
    /// The loader state an approval is bound to, as one canonical string (HOT2 / handoff item 4).
    /// It rides in <see cref="AdmissionResult.Facts"/> under <see cref="LoaderStateFact"/>, so the
    /// executor can recheck immediately before its write without any new value threaded through the
    /// plan — and so the readback shows exactly what was compared.
    /// </summary>
    internal static string LoaderStateKey(RuntimeGenerationSnapshot? snapshot)
    {
        if (snapshot == null) return "available=false";
        if (!snapshot.Available) return "available=false";
        return "available=true;gen=" + snapshot.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ";busy=" + Text(snapshot.LoaderBusy)
            + ";pinned=" + Text(snapshot.CurrentPlugin?.PriorGenerationPinned ?? false)
            + ";blocked=" + Text(snapshot.CurrentPlugin != null)
            + ";side=" + snapshot.ProcessSide;
    }

    /// <summary>The <see cref="AdmissionResult.Facts"/> key <see cref="LoaderStateKey"/> is stored under.</summary>
    public const string LoaderStateFact = "loaderState";

    /// <summary>
    /// True when a freshly captured snapshot describes the same loader state the approval was bound
    /// to. A missing or unavailable approved key is never "the same": there was nothing to bind to.
    /// </summary>
    internal static bool SameLoaderState(string? approvedKey, RuntimeGenerationSnapshot? current)
    {
        if (string.IsNullOrEmpty(approvedKey) || approvedKey == "available=false") return false;
        return string.Equals(approvedKey, LoaderStateKey(current), StringComparison.Ordinal);
    }

    /// <summary>Why two loader states differ, for the refusal detail: both keys, verbatim.</summary>
    internal static string DescribeDrift(string? approvedKey, RuntimeGenerationSnapshot? current)
    {
        if (current == null || !current.Available)
            return "the snapshot is no longer available (" + (current?.UnavailableReason ?? "no snapshot") + ")";
        if (string.IsNullOrEmpty(approvedKey) || approvedKey == "available=false")
            return "the plan carried no available snapshot";
        return approvedKey + " -> " + LoaderStateKey(current);
    }

    private static string Text(bool value) => value ? "true" : "false";
}
