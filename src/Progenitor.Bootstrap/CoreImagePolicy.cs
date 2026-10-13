using System;

namespace Progenitor.Bootstrap;

/// <summary>
/// Game-free rules for a staged <c>Progenitor.dll.next</c>: read the bytes first,
/// reject a truncated or non-PE image (and a PE that is not Core), and only then
/// rotate the live file. The live write is a same-folder temp plus
/// <c>File.Replace</c> so a crash mid-write never leaves the folder without a
/// loadable Core. Quarantine of a junk <c>.next</c> must not touch the live dll.
/// Last-good restore uses the same atomic replace. <c>.prev</c> is deleted only
/// after the new image has actually installed, never at the start of the next
/// promote.
/// </summary>
public static class CoreImagePolicy
{
    /// <summary>
    /// DOS stub plus <c>e_lfanew</c>. Anything shorter cannot be a PE and is
    /// treated as a truncated copy.
    /// </summary>
    public const int MinimumPeLength = 64;

    public const string PrevSuffix = ".prev";
    public const string LastGoodTempSuffix = ".last-good.tmp";
    public const string PromoteTempSuffix = ".promote.tmp";

    /// <summary>Offset of <c>e_lfanew</c> in a DOS header.</summary>
    public const int PeOffsetPosition = 0x3C;

    /// <summary>PE32 optional-header magic.</summary>
    public const int Pe32Magic = 0x10B;

    /// <summary>PE32+ optional-header magic.</summary>
    public const int Pe32PlusMagic = 0x20B;

    /// <summary><c>IMAGE_DIRECTORY_ENTRY_COMHEADER</c> (CLR runtime header).</summary>
    public const int CliDirectoryIndex = 14;

    /// <summary>Minimum <c>IMAGE_COR20_HEADER</c> size.</summary>
    public const int CliHeaderSize = 72;

    /// <summary>CLI metadata root signature <c>BSJB</c>.</summary>
    public const int CliMetadataSignature = 0x424A5342;

    public static string PrevPath(string dllPath) => dllPath + PrevSuffix;

    public static string LastGoodTempPath(string dllPath) => dllPath + LastGoodTempSuffix;

    /// <summary>
    /// The old constant promote temp. <b>No promote uses this any more</b> — R32 finding 4 was
    /// that a fixed, predictable path in the same attacker-writable folder as <c>.next</c> is
    /// exactly the file a spinning process overwrites between the gate and the rotate.
    /// <see cref="PromoteSequence.NewTempName"/> generates a random name per promote instead,
    /// and <see cref="PromoteSequence.Promote"/> re-reads and hashes the destination afterwards.
    /// Kept only so an operator (and the leftover-file cleanup in the deploy scripts) can still
    /// name the shape of a stale temp from an older build.
    /// </summary>
    public static string PromoteTempPath(string dllPath) => dllPath + PromoteTempSuffix;

    /// <summary>
    /// True when <paramref name="bytes"/> is at least a DOS/PE image: MZ magic,
    /// a sane <c>e_lfanew</c>, and PE\0\0. Truncated copies, empty files, and
    /// random bytes fail. Does not load the assembly and does not walk sections.
    /// </summary>
    public static bool IsPeImage(byte[]? bytes)
    {
        if (bytes == null || bytes.Length < MinimumPeLength) return false;
        if (bytes[0] != (byte)'M' || bytes[1] != (byte)'Z') return false;
        int pe = ReadInt32(bytes, PeOffsetPosition);
        if (pe < PeOffsetPosition + 4 || pe > bytes.Length - 4) return false;
        return bytes[pe] == (byte)'P'
            && bytes[pe + 1] == (byte)'E'
            && bytes[pe + 2] == 0
            && bytes[pe + 3] == 0;
    }

    /// <summary>
    /// True when the file is a PE whose section raw ranges and CLI metadata
    /// directory fit inside <paramref name="bytes"/>. A truncated Core with an
    /// intact DOS/PE header fails here, so Bootstrap can refuse the image
    /// before unloading the live generation (evidence 77).
    /// </summary>
    public static bool HasCompleteCliMetadata(byte[]? bytes)
    {
        if (!IsPeImage(bytes)) return false;
        byte[] data = bytes!;
        int pe = ReadInt32(data, PeOffsetPosition);
        int coff = pe + 4;
        if (coff + 20 > data.Length) return false;
        int numberOfSections = ReadUInt16(data, coff + 2);
        int sizeOfOptional = ReadUInt16(data, coff + 16);
        if (numberOfSections <= 0 || numberOfSections > 96) return false;
        if (sizeOfOptional < 2) return false;
        int optional = coff + 20;
        if (optional + sizeOfOptional > data.Length) return false;
        int magic = ReadUInt16(data, optional);
        int dataDirectoryCountOffset;
        int dataDirectoryOffset;
        if (magic == Pe32Magic)
        {
            dataDirectoryCountOffset = 92;
            dataDirectoryOffset = 96;
        }
        else if (magic == Pe32PlusMagic)
        {
            dataDirectoryCountOffset = 108;
            dataDirectoryOffset = 112;
        }
        else
            return false;
        if (sizeOfOptional < dataDirectoryOffset) return false;
        int dataDirectoryCount = sizeOfOptional >= dataDirectoryCountOffset + 4
            ? ReadInt32(data, optional + dataDirectoryCountOffset)
            : 0;
        if (dataDirectoryCount < CliDirectoryIndex + 1) return false;
        int cliDir = optional + dataDirectoryOffset + (CliDirectoryIndex * 8);
        if (cliDir + 8 > optional + sizeOfOptional) return false;
        if (cliDir + 8 > data.Length) return false;

        int sectionTable = optional + sizeOfOptional;
        int sectionBytes = numberOfSections * 40;
        if (sectionTable < 0 || sectionTable + sectionBytes > data.Length) return false;
        if (!SectionsFit(data, sectionTable, numberOfSections)) return false;

        int cliImageOffset = ReadInt32(data, cliDir);
        int cliSize = ReadInt32(data, cliDir + 4);
        if (cliImageOffset <= 0 || cliSize < CliHeaderSize) return false;
        if (!TryImageOffsetToFileOffset(data, sectionTable, numberOfSections, cliImageOffset, cliSize, out int cliOffset))
            return false;
        if (cliOffset + CliHeaderSize > data.Length) return false;

        int metadataImageOffset = ReadInt32(data, cliOffset + 8);
        int metadataSize = ReadInt32(data, cliOffset + 12);
        if (metadataImageOffset <= 0 || metadataSize < 4) return false;
        if (!TryImageOffsetToFileOffset(data, sectionTable, numberOfSections, metadataImageOffset, metadataSize, out int metaOffset))
            return false;
        if (ReadInt32(data, metaOffset) != CliMetadataSignature) return false;
        return true;
    }

    /// <summary>
    /// Whether a staged <c>.next</c> may replace the live dll. Complete CLI PE +
    /// <c>Progenitor.Runtime</c> + <c>Runtime.Install</c> + matching protocol.
    /// Invalid images are quarantined as <c>.next</c> only; the live file stays.
    /// </summary>
    public static bool AcceptNextImage(
        byte[]? bytes,
        bool hasRuntimeType,
        bool hasInstallMethod,
        int bootstrapProtocol,
        int coreProtocol,
        out string error) =>
        AcceptNextImage(bytes, hasRuntimeType, hasInstallMethod, bootstrapProtocol, coreProtocol, requireProtocol: true, out error);

    /// <summary>
    /// Same as <see cref="AcceptNextImage(byte[], bool, bool, int, int, out string)"/>
    /// with an explicit protocol gate. First load promotes a complete Core PE
    /// even when a throwaway inspect ALC reads protocol 0; <c>LoadAndInstall</c>
    /// is the source of truth for the handshake.
    /// </summary>
    public static bool AcceptNextImage(
        byte[]? bytes,
        bool hasRuntimeType,
        bool hasInstallMethod,
        int bootstrapProtocol,
        int coreProtocol,
        bool requireProtocol,
        out string error) =>
        AcceptNextImageCore(bytes, hasRuntimeType, hasInstallMethod, bootstrapProtocol, coreProtocol, requireProtocol, out error);

    private static bool AcceptNextImageCore(
        byte[]? bytes,
        bool hasRuntimeType,
        bool hasInstallMethod,
        int bootstrapProtocol,
        int coreProtocol,
        bool requireProtocol,
        out string error)
    {
        if (bytes == null || bytes.Length == 0)
        {
            error = "empty";
            return false;
        }
        if (!IsPeImage(bytes))
        {
            error = "not a PE image or truncated";
            return false;
        }
        if (!HasCompleteCliMetadata(bytes))
        {
            error = "truncated PE (section table or CLI metadata extends past file length)";
            return false;
        }
        if (!hasRuntimeType)
        {
            error = "Core image has no type " + CoreLayout.RuntimeTypeName;
            return false;
        }
        if (!hasInstallMethod)
        {
            error = "Core image has no method " + CoreLayout.RuntimeTypeName + "." + CoreLayout.InstallMethod;
            return false;
        }
        if (!requireProtocol)
        {
            error = "";
            return true;
        }
        return CoreReloadPolicy.AcceptProtocol(bootstrapProtocol, coreProtocol, out error);
    }

    /// <summary>
    /// True when quarantine may <c>Move</c> the live dll out of the way.
    /// Empty last-good means leave the live file and only rename <c>.next</c>.
    /// </summary>
    public static bool MayMoveLiveAway(bool hasLastGoodBytes) => hasLastGoodBytes;

    /// <summary>
    /// <c>.prev</c> is the previous live image. Delete it only after N+1
    /// <c>LoadAndInstall</c> (or a last-good restore) has actually run.
    /// </summary>
    public static bool MayDeletePrev(bool newImageProven) => newImageProven;

    /// <summary>
    /// <c>File.Replace(tmp, live, backup)</c> may use <c>.prev</c> as the backup
    /// only when that file is absent. An existing <c>.prev</c> is the last proven
    /// image and must not be overwritten; the unproven live file is quarantined
    /// as <c>failed-*</c> instead.
    /// </summary>
    public static bool ShouldUsePrevAsReplaceBackup(bool prevExists) => !prevExists;

    /// <summary>
    /// First boot: if the live file is missing or not a complete CLI PE and
    /// <c>.prev</c> is, restore <c>.prev</c> before giving up.
    /// </summary>
    public static bool ShouldRestorePrev(bool liveMissingOrInvalid, bool prevIsValidPe) =>
        liveMissingOrInvalid && prevIsValidPe;

    public static string FormatRejectedNext(string error) =>
        "Core watch: Progenitor.dll.next rejected: " + (error ?? "");

    public static string FormatQuarantinedNext(string failedFileName) =>
        "Core watch: quarantined invalid Progenitor.dll.next as " + (failedFileName ?? "")
        + " (live Core untouched).";

    public static string FormatKeptPrev() =>
        "Core: kept Progenitor.dll.prev until the new image is proven.";

    public static string FormatDeletedPrev() =>
        "Core: deleted Progenitor.dll.prev after the new image was proven.";

    public static string FormatRestoredPrev() =>
        "Core: restored Progenitor.dll from .prev (live file was not a valid PE).";

    public static string FormatAtomicLastGoodFailed(string error) =>
        "quarantine write last-good failed: " + (error ?? "") + " (live Core untouched).";

    private static bool SectionsFit(byte[] data, int sectionTable, int numberOfSections)
    {
        for (int i = 0; i < numberOfSections; i++)
        {
            int row = sectionTable + (i * 40);
            int sizeOfRaw = ReadInt32(data, row + 16);
            int pointerToRaw = ReadInt32(data, row + 20);
            if (pointerToRaw < 0 || sizeOfRaw < 0) return false;
            if (pointerToRaw == 0 || sizeOfRaw == 0) continue;
            long end = (long)pointerToRaw + sizeOfRaw;
            if (end > data.Length) return false;
        }
        return true;
    }

    private static bool TryImageOffsetToFileOffset(
        byte[] data,
        int sectionTable,
        int numberOfSections,
        int imageOffset,
        int size,
        out int offset)
    {
        offset = 0;
        if (imageOffset <= 0 || size <= 0) return false;
        for (int i = 0; i < numberOfSections; i++)
        {
            int row = sectionTable + (i * 40);
            int virtualSize = ReadInt32(data, row + 8);
            int virtualAddress = ReadInt32(data, row + 12);
            int sizeOfRaw = ReadInt32(data, row + 16);
            int pointerToRaw = ReadInt32(data, row + 20);
            int span = virtualSize > sizeOfRaw ? virtualSize : sizeOfRaw;
            if (span <= 0) continue;
            if (imageOffset < virtualAddress) continue;
            long delta = (long)imageOffset - virtualAddress;
            if (delta >= span) continue;
            if (pointerToRaw <= 0) return false;
            long file = (long)pointerToRaw + delta;
            if (file < 0 || file + size > data.Length) return false;
            if (delta + size > sizeOfRaw && sizeOfRaw > 0) return false;
            offset = (int)file;
            return true;
        }
        return false;
    }

    private static int ReadInt32(byte[] bytes, int offset)
    {
        return bytes[offset]
            | (bytes[offset + 1] << 8)
            | (bytes[offset + 2] << 16)
            | (bytes[offset + 3] << 24);
    }

    private static int ReadUInt16(byte[] bytes, int offset) =>
        bytes[offset] | (bytes[offset + 1] << 8);
}
