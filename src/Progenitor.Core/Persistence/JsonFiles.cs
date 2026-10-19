using System;
using System.IO;
using System.Text.Json;

namespace Progenitor.Persistence;

/// <summary>
/// JSON files read as bytes. <see cref="JsonDocument.Parse(ReadOnlyMemory{byte}, JsonDocumentOptions)"/> rejects a
/// leading UTF-8 byte order mark (EF BB BF), which hand-edited files often carry (Notepad, Windows PowerShell's
/// <c>Out-File</c> and <c>Set-Content -Encoding UTF8</c>, some Thunderstore <c>manifest.json</c> files). These helpers
/// skip it for parsing only: the file is never rewritten, and a hash or signature over the file still uses its exact
/// bytes. Progenitor's own JSON writers keep writing without a BOM.
/// Game-free, so the test projects compile it.
/// </summary>
internal static class JsonFiles
{
    /// <summary>True when <paramref name="bytes"/> starts with a UTF-8 byte order mark.</summary>
    internal static bool HasBom(byte[]? bytes) =>
        bytes != null && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    /// <summary><paramref name="bytes"/> without a leading UTF-8 byte order mark; no copy is made.</summary>
    internal static ReadOnlyMemory<byte> WithoutBom(byte[] bytes) =>
        HasBom(bytes) ? new ReadOnlyMemory<byte>(bytes, 3, bytes.Length - 3) : new ReadOnlyMemory<byte>(bytes);

    /// <summary>The JSON in <paramref name="bytes"/>, a leading byte order mark skipped.</summary>
    internal static JsonDocument Parse(byte[] bytes, JsonDocumentOptions options = default) =>
        JsonDocument.Parse(WithoutBom(bytes), options);

    /// <summary>The JSON file at <paramref name="path"/>, a leading byte order mark skipped.</summary>
    internal static JsonDocument ParseFile(string path, JsonDocumentOptions options = default) =>
        Parse(File.ReadAllBytes(path), options);
}
