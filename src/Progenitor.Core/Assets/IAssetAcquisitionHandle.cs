using System;

namespace Progenitor.Effects;

/// <summary>
/// One owned mount/load of a content-archive object. Dispose unloads and unmounts only
/// what this handle acquired. Double-dispose is a no-op. Track on
/// <c>PluginContext</c> so plugin unload releases it.
/// </summary>
public interface IAssetAcquisitionHandle : IDisposable
{
    string HandleId { get; }

    string ArchiveId { get; }

    ulong PathId { get; }

    string Name { get; }

    string UnityType { get; }

    AssetAcquisitionStatus Status { get; }

    string? Reason { get; }

    bool Visible { get; }

    bool IsDisposed { get; }

    int OwnedFileCount { get; }

    int OwnedArchiveCount { get; }

    int ObjectCount { get; }

    long BytesCompressed { get; }

    int Epoch { get; }

    bool IsStale { get; }
}
