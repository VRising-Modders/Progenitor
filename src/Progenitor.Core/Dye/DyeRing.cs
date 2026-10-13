namespace Progenitor.Dye;

/// <summary>
/// Game-free bounds check for the opt-in palette blob read. The charge ring's own numbers (the drawn
/// texture, the completion pulse) are Core's shared <see cref="Progenitor.Ui.HoldRingMath"/>.
/// </summary>
internal static class DyeRing
{
    /// <summary>Most palettes one item is read with; a longer list is treated as a bad read.</summary>
    internal const int MaxPalettes = 64;

    /// <summary>
    /// Whether a <c>BlobArray</c> of <paramref name="count"/> elements of <paramref name="stride"/>
    /// bytes, whose header sits <paramref name="arrayAt"/> bytes into a blob and whose elements start
    /// <paramref name="relativeOffset"/> bytes after that header, fits inside a blob of
    /// <paramref name="blobLength"/> bytes (the <c>BlobAssetHeader.Length</c>). Anything that does not
    /// fit, or a count over <see cref="MaxPalettes"/>, is a bad read and the palettes are not used.
    /// </summary>
    internal static bool PaletteArrayFits(int blobLength, int arrayAt, int relativeOffset, int count, int stride)
    {
        if (blobLength <= 0 || arrayAt < 0 || stride <= 0) return false;
        if (count < 0 || count > MaxPalettes) return false;
        if (count == 0) return true;
        if (relativeOffset < 8) return false; // the elements cannot overlap the {offset, length} header
        long end = (long)arrayAt + relativeOffset + (long)count * stride;
        return end <= blobLength;
    }
}
