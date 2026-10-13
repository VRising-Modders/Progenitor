using System;
using Progenitor.Bootstrap;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of R18/R20 findings 1 and evidence 77: a truncated or
/// non-PE <c>.next</c> is rejected without touching the live dll; last-good /
/// promote writes are a temp plus atomic replace; <c>.prev</c> stays until the
/// new image is proven; the PE section table and CLI metadata header must fit
/// inside the file.
/// </summary>
public class CoreImagePolicyTests
{
    [Fact]
    public void Empty_and_truncated_bytes_are_not_a_PE()
    {
        Assert.False(CoreImagePolicy.IsPeImage(null));
        Assert.False(CoreImagePolicy.IsPeImage(Array.Empty<byte>()));
        Assert.False(CoreImagePolicy.IsPeImage(new byte[8]));
        Assert.False(CoreImagePolicy.IsPeImage(new byte[CoreImagePolicy.MinimumPeLength]));
        var mzOnly = new byte[128];
        mzOnly[0] = (byte)'M';
        mzOnly[1] = (byte)'Z';
        Assert.False(CoreImagePolicy.IsPeImage(mzOnly));
        Assert.False(CoreImagePolicy.HasCompleteCliMetadata(mzOnly));
        Assert.False(CoreImagePolicy.HasCompleteCliMetadata(MinimalPe()));
    }

    [Fact]
    public void Minimal_PE_header_is_accepted_as_an_image_but_not_as_CLI()
    {
        Assert.True(CoreImagePolicy.IsPeImage(MinimalPe()));
        Assert.False(CoreImagePolicy.HasCompleteCliMetadata(MinimalPe()));
        Assert.True(CoreImagePolicy.HasCompleteCliMetadata(CompleteCliPe()));
    }

    [Fact]
    public void Truncated_real_looking_PE_is_rejected_before_any_rotate()
    {
        byte[] complete = CompleteCliPe();
        Assert.True(CoreImagePolicy.HasCompleteCliMetadata(complete));

        // Headers and section table intact; section raw data (and CLI) cut off.
        var truncated = new byte[0x1A0];
        Buffer.BlockCopy(complete, 0, truncated, 0, truncated.Length);
        Assert.True(CoreImagePolicy.IsPeImage(truncated));
        Assert.False(CoreImagePolicy.HasCompleteCliMetadata(truncated));

        Assert.False(CoreImagePolicy.AcceptNextImage(
            truncated, hasRuntimeType: true, hasInstallMethod: true, 1, 1, out var error));
        Assert.Contains("truncated PE", error);
    }

    [Fact]
    public void AcceptNextImage_refuses_junk_before_any_rotate()
    {
        Assert.False(CoreImagePolicy.AcceptNextImage(null, true, true, 1, 1, out var empty));
        Assert.Equal("empty", empty);
        Assert.False(CoreImagePolicy.AcceptNextImage(new byte[4], true, true, 1, 1, out var truncated));
        Assert.Equal("not a PE image or truncated", truncated);

        byte[] headerOnly = MinimalPe();
        Assert.False(CoreImagePolicy.AcceptNextImage(headerOnly, true, true, 1, 1, out var shallow));
        Assert.Contains("truncated PE", shallow);

        byte[] pe = CompleteCliPe();
        Assert.False(CoreImagePolicy.AcceptNextImage(pe, hasRuntimeType: false, hasInstallMethod: true, 1, 1, out var missing));
        Assert.Contains(CoreLayout.RuntimeTypeName, missing);

        Assert.False(CoreImagePolicy.AcceptNextImage(pe, hasRuntimeType: true, hasInstallMethod: false, 1, 1, out var noInstall));
        Assert.Contains(CoreLayout.InstallMethod, noInstall);

        Assert.False(CoreImagePolicy.AcceptNextImage(pe, hasRuntimeType: true, hasInstallMethod: true, bootstrapProtocol: 1, coreProtocol: 0, out var proto));
        Assert.Contains("Bootstrap protocol 1", proto);
        Assert.Contains("Core protocol 0", proto);

        Assert.True(CoreImagePolicy.AcceptNextImage(pe, hasRuntimeType: true, hasInstallMethod: true, 1, 1, out var ok));
        Assert.Equal("", ok);

        Assert.True(CoreImagePolicy.AcceptNextImage(
            pe, hasRuntimeType: true, hasInstallMethod: true,
            bootstrapProtocol: 2, coreProtocol: 0, requireProtocol: false, out var skipProto));
        Assert.Equal("", skipProto);
        Assert.False(CoreImagePolicy.AcceptNextImage(
            pe, hasRuntimeType: true, hasInstallMethod: true,
            bootstrapProtocol: 2, coreProtocol: 0, requireProtocol: true, out var stillProto));
        Assert.Contains("Bootstrap protocol 2", stillProto);
    }

    [Fact]
    public void Quarantine_never_moves_the_live_dll_without_last_good()
    {
        Assert.False(CoreImagePolicy.MayMoveLiveAway(hasLastGoodBytes: false));
        Assert.True(CoreImagePolicy.MayMoveLiveAway(hasLastGoodBytes: true));
        Assert.False(CoreImagePolicy.MayDeletePrev(newImageProven: false));
        Assert.True(CoreImagePolicy.MayDeletePrev(newImageProven: true));
        Assert.True(CoreImagePolicy.ShouldUsePrevAsReplaceBackup(prevExists: false));
        Assert.False(CoreImagePolicy.ShouldUsePrevAsReplaceBackup(prevExists: true));
        Assert.True(CoreImagePolicy.ShouldRestorePrev(liveMissingOrInvalid: true, prevIsValidPe: true));
        Assert.False(CoreImagePolicy.ShouldRestorePrev(liveMissingOrInvalid: false, prevIsValidPe: true));
        Assert.False(CoreImagePolicy.ShouldRestorePrev(liveMissingOrInvalid: true, prevIsValidPe: false));
        Assert.Equal("Progenitor.dll.prev", System.IO.Path.GetFileName(CoreImagePolicy.PrevPath("Progenitor.dll")));
        Assert.EndsWith(".last-good.tmp", CoreImagePolicy.LastGoodTempPath("Progenitor.dll"));
        Assert.EndsWith(".promote.tmp", CoreImagePolicy.PromoteTempPath("Progenitor.dll"));
        Assert.Equal(
            "Core watch: Progenitor.dll.next rejected: not a PE image or truncated",
            CoreImagePolicy.FormatRejectedNext("not a PE image or truncated"));
        Assert.Equal(
            "Core watch: quarantined invalid Progenitor.dll.next as Progenitor.dll.failed-1 (live Core untouched).",
            CoreImagePolicy.FormatQuarantinedNext("Progenitor.dll.failed-1"));
        Assert.Equal("Core: kept Progenitor.dll.prev until the new image is proven.", CoreImagePolicy.FormatKeptPrev());
        Assert.Equal("Core: deleted Progenitor.dll.prev after the new image was proven.", CoreImagePolicy.FormatDeletedPrev());
        Assert.Equal(
            "Core: restored Progenitor.dll from .prev (live file was not a valid PE).",
            CoreImagePolicy.FormatRestoredPrev());
        Assert.StartsWith("quarantine write last-good failed:", CoreImagePolicy.FormatAtomicLastGoodFailed("disk full"));
        Assert.Contains("live Core untouched", CoreImagePolicy.FormatAtomicLastGoodFailed("disk full"));
    }

    /// <summary>
    /// DOS + PE signature only. Enough for <see cref="CoreImagePolicy.IsPeImage"/>;
    /// not a CLI image.
    /// </summary>
    private static byte[] MinimalPe()
    {
        var bytes = new byte[256];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        const int pe = 0x80;
        bytes[CoreImagePolicy.PeOffsetPosition] = pe;
        bytes[pe] = (byte)'P';
        bytes[pe + 1] = (byte)'E';
        return bytes;
    }

    /// <summary>
    /// Tiny PE32 with one section, a COM descriptor, and a BSJB metadata root.
    /// File length matches the section table. Truncating it is the evidence-77 case.
    /// </summary>
    internal static byte[] CompleteCliPe()
    {
        const int pe = 0x80;
        const int coff = pe + 4;
        const int optional = coff + 20; // 0x98
        const int sizeOfOptional = 224;
        const int sectionTable = optional + sizeOfOptional; // 0x178
        const int pointerToRaw = 0x200;
        const int sizeOfRaw = 0x200;
        const int virtualAddress = 0x2000;
        const int cliImageOffset = virtualAddress;
        const int metadataImageOffset = virtualAddress + 0x50;
        var bytes = new byte[pointerToRaw + sizeOfRaw];

        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        WriteInt32(bytes, CoreImagePolicy.PeOffsetPosition, pe);
        bytes[pe] = (byte)'P';
        bytes[pe + 1] = (byte)'E';

        WriteUInt16(bytes, coff + 2, 1); // NumberOfSections
        WriteUInt16(bytes, coff + 16, sizeOfOptional);
        WriteUInt16(bytes, optional, CoreImagePolicy.Pe32Magic);
        WriteInt32(bytes, optional + 92, 16); // data directory count

        int cliDir = optional + 96 + (CoreImagePolicy.CliDirectoryIndex * 8);
        WriteInt32(bytes, cliDir, cliImageOffset);
        WriteInt32(bytes, cliDir + 4, CoreImagePolicy.CliHeaderSize);

        WriteInt32(bytes, sectionTable + 8, sizeOfRaw); // VirtualSize
        WriteInt32(bytes, sectionTable + 12, virtualAddress);
        WriteInt32(bytes, sectionTable + 16, sizeOfRaw);
        WriteInt32(bytes, sectionTable + 20, pointerToRaw);

        int cliOffset = pointerToRaw;
        WriteInt32(bytes, cliOffset, CoreImagePolicy.CliHeaderSize); // cb
        WriteInt32(bytes, cliOffset + 8, metadataImageOffset);
        WriteInt32(bytes, cliOffset + 12, 16);

        int metaOffset = pointerToRaw + 0x50;
        WriteInt32(bytes, metaOffset, CoreImagePolicy.CliMetadataSignature);
        return bytes;
    }

    private static void WriteInt32(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteUInt16(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }
}
