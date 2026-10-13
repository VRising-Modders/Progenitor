using System.Collections.Generic;

namespace Progenitor.Effects;

/// <summary>
/// Game (or a test fake) behind <see cref="AssetAcquisition"/>. Tests inject a fake so
/// they never call Unity. The live adapter is <c>UnityContentArchiveLoader</c>, which
/// refuses unless <see cref="AssetAcquisitionOptIn.Granted"/>.
/// </summary>
internal interface IContentArchiveLoader
{
    bool IsClient { get; }

    /// <summary>Opt-in already checked by the facade. Mount one archive into PGbrowser.</summary>
    ContentLoadStepResult Mount(string archiveId, string diskPath);

    ContentLoadStepResult LoadFile(string archiveId, IReadOnlyList<string> dependencyArchiveIds);

    ContentGetObjectResult GetObject(string archiveId, ulong pathId);

    ContentUnloadStepResult UnloadFile(string archiveId);

    ContentUnloadStepResult Unmount(string archiveId);

    ContentResidencyScan ScanResidency(string name, string? archiveId);

    ContentInstantiateResult InstantiatePreview(string archiveId, ulong pathId, string pluginId, bool hostMenu);
}

internal readonly struct ContentLoadStepResult
{
    public ContentLoadStepResult(bool ok, string? reason, bool completed)
    {
        Ok = ok;
        Reason = reason;
        Completed = completed;
    }

    public bool Ok { get; }
    public string? Reason { get; }
    public bool Completed { get; }

    public static ContentLoadStepResult Fail(string reason) => new(false, reason, completed: true);
    public static ContentLoadStepResult Done() => new(true, null, completed: true);
    public static ContentLoadStepResult Pending() => new(true, null, completed: false);
}

internal readonly struct ContentGetObjectResult
{
    public ContentGetObjectResult(bool ok, string? reason, string? name, string? unityType, int objectCount)
    {
        Ok = ok;
        Reason = reason;
        Name = name;
        UnityType = unityType;
        ObjectCount = objectCount;
    }

    public bool Ok { get; }
    public string? Reason { get; }
    public string? Name { get; }
    public string? UnityType { get; }
    public int ObjectCount { get; }

    public static ContentGetObjectResult Fail(string reason) =>
        new(false, reason, null, null, 0);
}

internal readonly struct ContentUnloadStepResult
{
    public ContentUnloadStepResult(bool ok, string? reason, bool waitVerified)
    {
        Ok = ok;
        Reason = reason;
        WaitVerified = waitVerified;
    }

    public bool Ok { get; }
    public string? Reason { get; }

    /// <summary>
    /// False when UnloadAsync ran but <c>WaitForUnloadCompletion</c> is an unresolved
    /// icall (absent from the game's metadata). The file was still requested to unload.
    /// </summary>
    public bool WaitVerified { get; }

    public static ContentUnloadStepResult Fail(string reason) => new(false, reason, waitVerified: false);
    public static ContentUnloadStepResult Done(bool waitVerified) => new(true, null, waitVerified);
}

internal sealed class ContentResidencyScan
{
    public int NameCount { get; set; }
    public IReadOnlyList<int> InstanceIds { get; set; } = System.Array.Empty<int>();
    public string? RtcDump { get; set; }
    public bool RtcMentionsTarget { get; set; }
    public int? PgMounted { get; set; }
    public int? PgFiles { get; set; }
    public bool IcallsResolved { get; set; }
    public string ScanApi { get; set; } = "FindObjectsOfTypeAll";
}

internal readonly struct ContentInstantiateResult
{
    public ContentInstantiateResult(bool ok, string? reason, bool hosted, string? cloneName, bool? particlePlaying, string? unsupported)
    {
        Ok = ok;
        Reason = reason;
        Hosted = hosted;
        CloneName = cloneName;
        ParticlePlaying = particlePlaying;
        Unsupported = unsupported;
    }

    public bool Ok { get; }
    public string? Reason { get; }
    public bool Hosted { get; }
    public string? CloneName { get; }
    public bool? ParticlePlaying { get; }
    public string? Unsupported { get; }

    public static ContentInstantiateResult Fail(string reason) =>
        new(false, reason, false, null, null, null);
}
