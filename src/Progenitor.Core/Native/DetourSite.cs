using System;

namespace Progenitor.Native;

/// <summary>
/// Resolves a native detour target: address-library table first, <c>MethodPointerByPrefix</c>
/// only when the table for the running GameAssembly build is missing. A loaded table that
/// lacks the id does not fall back — that is a wanted-list gap, not a reason to pick an
/// overload by name. Never throws; a failed resolve leaves the detour uninstalled.
/// </summary>
public static class DetourSite
{
    /// <summary>
    /// Look up <paramref name="addressId"/> through <see cref="AddressLibrary.TryResolve"/>.
    /// When that returns false because no table is attached, invoke
    /// <paramref name="prefixFallback"/> (the interop <c>NativeMethodInfoPtr_*</c> path).
    /// Logs one line naming the path and relative address, or one readable error naming the site, the
    /// build id, and <c>node tools/addresslib/build.mjs --side {side}</c>.
    /// </summary>
    public static DetourResolveResult Resolve(string site, string addressId, Func<IntPtr>? prefixFallback)
    {
        site ??= "";
        addressId ??= "";
        try
        {
            if (AddressLibrary.TryResolve(addressId, out var fromTable) && fromTable != IntPtr.Zero)
            {
                long imageOffset = ImageOffsetFromAddress(fromTable);
                if (AddressLibrary.Current != null && AddressLibrary.Current.TryGetImageOffset(addressId, out var tableImageOffset))
                    imageOffset = tableImageOffset;
                if (AddressLibrary.ImageOffsetInExecutableSection(imageOffset))
                    return Record(site, addressId, AddressId.PathTable, imageOffset, fromTable, ok: true, tablePresent: true);

                // In-module but not in .text: do not JMP. Prefix fallback is allowed here
                // (unlike a wanted-list gap) so a leftover colliding table cannot hook .rdata.
                AddressLibrary.EmitSiteLog(AddressId.FormatImageOffsetNotExecutable(site, addressId, imageOffset), warning: true);
                if (prefixFallback != null)
                {
                    IntPtr fromPrefix = IntPtr.Zero;
                    try { fromPrefix = prefixFallback(); }
                    catch (Exception e)
                    {
                        return RecordMissing(site, addressId, tablePresent: true, e.Message);
                    }
                    if (fromPrefix != IntPtr.Zero)
                    {
                        long prefixImageOffset = ImageOffsetFromAddress(fromPrefix);
                        return Record(site, addressId, AddressId.PathFallback, prefixImageOffset, fromPrefix, ok: true, tablePresent: true);
                    }
                }
                return RecordMissing(site, addressId, tablePresent: true, prefixError: null);
            }

            bool tablePresent = AddressLibrary.IsLoaded;
            if (!tablePresent && prefixFallback != null)
            {
                IntPtr fromPrefix = IntPtr.Zero;
                try { fromPrefix = prefixFallback(); }
                catch (Exception e)
                {
                    return RecordMissing(site, addressId, tablePresent, e.Message);
                }

                if (fromPrefix != IntPtr.Zero)
                {
                    long imageOffset = ImageOffsetFromAddress(fromPrefix);
                    return Record(site, addressId, AddressId.PathFallback, imageOffset, fromPrefix, ok: true, tablePresent: false);
                }
            }

            return RecordMissing(site, addressId, tablePresent, prefixError: null);
        }
        catch (Exception e)
        {
            return RecordMissing(site, addressId, AddressLibrary.IsLoaded, e.Message);
        }
    }

    /// <summary>Structured ping/status payload: build id, table present, per-site path.</summary>
    public static NativePingSnapshot DescribeForPing() => AddressLibrary.DescribeForPing();

    /// <summary>Same snapshot as a single grep line (<c>native side=… table=yes|no sites=…</c>).</summary>
    public static string Describe() => AddressLibrary.Describe();

    private static DetourResolveResult Record(
        string site,
        string id,
        string path,
        long imageOffset,
        IntPtr address,
        bool ok,
        bool tablePresent)
    {
        var outcome = new DetourSiteOutcome(site, id, path, imageOffset, address);
        AddressLibrary.RecordSite(outcome);
        string line = AddressId.FormatResolvedLine(site, id, path, imageOffset, address);
        AddressLibrary.EmitSiteLog(line, warning: false);
        return new DetourResolveResult(ok, address, path, imageOffset, site, id, line, tablePresent);
    }

    private static DetourResolveResult RecordMissing(string site, string id, bool tablePresent, string? prefixError)
    {
        string side = AddressLibrary.DetectedSide ?? AddressLibrary.LoadedSide ?? AddressLibrary.DetectSide();
        string build = AddressLibrary.DetectedBuild ?? AddressLibrary.LoadedBuild ?? "";
        string message = AddressId.FormatNotInstalled(site, id, side, build, tablePresent);
        if (!string.IsNullOrEmpty(prefixError))
            message = message + " Prefix fallback: " + prefixError;
        var outcome = new DetourSiteOutcome(site, id, AddressId.PathMissing, 0, IntPtr.Zero);
        AddressLibrary.RecordSite(outcome);
        AddressLibrary.EmitSiteLog(message, warning: true);
        return new DetourResolveResult(ok: false, IntPtr.Zero, AddressId.PathMissing, 0, site, id, message, tablePresent);
    }

    private static long ImageOffsetFromAddress(IntPtr address)
    {
        var moduleBase = AddressLibrary.ModuleBase;
        if (moduleBase == IntPtr.Zero || address == IntPtr.Zero) return 0;
        return (long)((ulong)(long)address - (ulong)(long)moduleBase);
    }
}

/// <summary>Result of <see cref="DetourSite.Resolve"/>. <see cref="Ok"/> false means do not install.</summary>
public readonly struct DetourResolveResult
{
    public DetourResolveResult(
        bool ok,
        IntPtr address,
        string path,
        long imageOffset,
        string site,
        string id,
        string message,
        bool tablePresent)
    {
        Ok = ok;
        Address = address;
        Path = path ?? "";
        ImageOffset = imageOffset;
        Site = site ?? "";
        Id = id ?? "";
        Message = message ?? "";
        TablePresent = tablePresent;
    }

    public bool Ok { get; }
    public IntPtr Address { get; }
    public string Path { get; }
    public long ImageOffset { get; }
    public string Site { get; }
    public string Id { get; }
    public string Message { get; }
    public bool TablePresent { get; }
}
