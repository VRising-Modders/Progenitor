using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Plugins;

namespace Progenitor.Networking;

/// <summary>
/// The server's read of the Thunderstore index for the packages its catalog and its client mod policy name
/// (2026-10-05), so the install window opens only for a package that serves the version the server needs. The same
/// index and lookup the join screens use (<see cref="JoinPackageIndex"/>, <see cref="ThunderstoreVersions"/>): the
/// configured local index (<c>[Install] LocalIndexPath</c>) in front of the fetched one, reused for 30 minutes
/// (TSTRAFFIC1: was 10; a check past that is a conditional request that costs a 304 while the index is unchanged), the
/// server's cached copy when the fetch fails.
///
/// <para>The handshake path never waits on the network: <see cref="Availability"/> answers from the last completed read,
/// and <see cref="Want"/> starts a background read when a package is new or the read is older than
/// <see cref="MaxAge"/>. A package not read yet answers <see cref="PackageVersionAvailability.Unknown"/>, which is not a
/// fix, so a server that cannot reach the index kicks exactly as before.</para>
/// </summary>
internal sealed class ServerPackageAvailability : IDisposable
{
    public static readonly TimeSpan MaxAge = JoinPackageIndex.ServerMaxAge;

    /// <summary>After a read that threw, no new read starts for this long (a handshake must not refetch every time).</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);

    private readonly Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<JoinIndexLoad>> _load;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private readonly HashSet<string> _wanted = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, PackageVersions> _versions = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _read = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _readUtc = DateTime.MinValue;
    private DateTime _failedUtc = DateTime.MinValue;
    private Task? _running;
    private string _lastError = "";
    private IDisposable[] _owned;
    private bool _disposed;

    public ServerPackageAvailability(
        Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<JoinIndexLoad>> load,
        Func<DateTime>? now = null,
        params IDisposable[] owned)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _now = now ?? (() => DateTime.UtcNow);
        _owned = owned ?? Array.Empty<IDisposable>();
    }

    /// <summary>Release what the live reader owns (its HTTP transport and handler); no read starts afterwards.</summary>
    public void Dispose()
    {
        IDisposable[] owned;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            owned = _owned;
            _owned = Array.Empty<IDisposable>();
        }

        foreach (var d in owned)
        {
            try { d?.Dispose(); }
            catch { /* best effort */ }
        }
    }

    /// <summary>The read in flight, or null (tests await it).</summary>
    public Task? Running
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>Why the last read could not reach the Thunderstore index (the local index may still have answered), or empty.</summary>
    public string LastError
    {
        get { lock (_gate) return _lastError; }
    }

    /// <summary>
    /// The packages to keep read. Starts a background read when one of them was not part of the last read, or that read
    /// is older than <see cref="MaxAge"/>; never waits. Returns the read it started, or null.
    /// </summary>
    public Task? Want(IEnumerable<string>? packages)
    {
        Dictionary<string, string> wanted;
        lock (_gate)
        {
            bool fresh = _now() - _readUtc < MaxAge;
            bool missing = false;
            if (packages != null)
                foreach (string p in packages)
                {
                    string k = (p ?? "").Trim();
                    if (k.Length == 0) continue;
                    _wanted.Add(k);
                    if (!_read.Contains(k)) missing = true;
                }

            if (_disposed || _wanted.Count == 0 || _running != null || (fresh && !missing)) return null;
            if (_now() - _failedUtc < RetryAfterFailure) return null;
            wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in _wanted) wanted[k] = "";
            _running = Task.Run(() => ReadAsync(wanted));
            return _running;
        }
    }

    private async Task ReadAsync(Dictionary<string, string> wanted)
    {
        try
        {
            var load = await _load(wanted, CancellationToken.None).ConfigureAwait(false);
            var versions = new Dictionary<string, PackageVersions>(StringComparer.OrdinalIgnoreCase);
            if (load?.Versions != null)
                foreach (var kv in load.Versions) versions[kv.Key] = kv.Value;
            lock (_gate)
            {
                _versions = versions;
                _read = new HashSet<string>(wanted.Keys, StringComparer.OrdinalIgnoreCase);
                _readUtc = _now();
                _lastError = load?.Error ?? "";
            }

            Plugin.Logger?.LogInfo("ModManifest: read Thunderstore versions of " + wanted.Count + " package(s) for the install window"
                + (versions.Count < wanted.Count ? " (" + (wanted.Count - versions.Count) + " not listed)" : "")
                + (load != null && load.Error.Length > 0 ? "; index: " + load.Error : ""));
        }
        catch (Exception e)
        {
            lock (_gate)
            {
                _lastError = e.GetType().Name + ": " + e.Message;
                _failedUtc = _now();
            }
            Plugin.Logger?.LogWarning("ModManifest: reading Thunderstore versions for the install window threw: " + e.Message);
        }
        finally
        {
            lock (_gate) _running = null;
        }
    }

    /// <summary>
    /// Whether <paramref name="package"/> serves a version the server accepts for <paramref name="version"/> under
    /// <paramref name="rule"/> (<see cref="ThunderstoreVersions.Availability"/>). <see cref="PackageVersionAvailability.Unknown"/>
    /// when the package was not part of a completed read.
    /// </summary>
    public PackageVersionAvailability Availability(string package, string version, NetworkVersionRule rule)
    {
        string k = (package ?? "").Trim();
        Dictionary<string, PackageVersions> versions;
        lock (_gate)
        {
            if (k.Length == 0 || !_read.Contains(k)) return PackageVersionAvailability.Unknown;
            versions = _versions;
        }

        return ThunderstoreVersions.Availability(versions, k, version, rule);
    }
}
