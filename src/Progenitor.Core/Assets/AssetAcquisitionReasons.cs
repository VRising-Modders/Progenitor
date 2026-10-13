namespace Progenitor.Effects;

/// <summary>
/// Fixed refusal vocabulary for catalog acquisition.
/// </summary>
public static class AssetAcquisitionReasons
{
    public const string NotInIndex = "not-in-index";
    public const string BudgetExceeded = "budget-exceeded";
    public const string MissingDependency = "missing-dependency";
    public const string Timeout = "timeout";
    public const string LoadError = "load-error";
    public const string UnsupportedClass = "unsupported-class";
    public const string NamespaceRejected = "namespace-rejected";
    public const string MountRefused = "mount-refused";
    public const string IcallUnresolved = "icall-unresolved";
    public const string OptInRequired = "opt-in-required";
    public const string ClientOnly = "client-only";
    public const string AlreadyResident = "already-resident";
    public const string StaleHandle = "stale-handle";
    public const string WaitUnloadUnsupported = "wait-unload-unsupported";
}
