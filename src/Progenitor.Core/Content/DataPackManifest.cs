using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Progenitor.Content.Json;

namespace Progenitor.Content;

/// <summary>A bounded, local, data-only item pack. Reading never registers content.</summary>
internal sealed class DataPackManifest
{
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxItems = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private DataPackManifest(string directory, string id, string version, string sha256, ContentFileDocument document)
    {
        Directory = directory;
        Id = id;
        Version = version;
        Sha256 = sha256;
        Document = document;
    }

    public string Directory { get; }
    public string Id { get; }
    public string Version { get; }
    public string Sha256 { get; }
    public int Schema => 1;
    public string Content => "items.json";
    public ContentFileDocument Document { get; }

    /// <summary>
    /// Validate exactly pack.json and items.json beneath a real local directory. Reject links,
    /// extra files, unrecognized fields, malformed definitions and mismatched content bytes.
    /// The caller decides registration, collision policy and lifetime after this succeeds.
    /// </summary>
    public static DataPackManifest Read(string directory)
    {
        if (directory == null) throw new ArgumentNullException(nameof(directory));
        string fullPath = Path.GetFullPath(directory);
        EnsureNoLinks(fullPath);
        if (!System.IO.Directory.Exists(fullPath)) throw Refuse("pack directory does not exist.");

        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (string entry in System.IO.Directory.EnumerateFileSystemEntries(fullPath))
        {
            var attributes = File.GetAttributes(entry);
            string name = Path.GetFileName(entry);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw Refuse("directories and reparse points are not allowed: " + name);
            if ((name != "pack.json" && name != "items.json") || !files.Add(name))
                throw Refuse("only pack.json and items.json are allowed; found " + name);
        }
        if (files.Count != 2) throw Refuse("pack.json and items.json are both required.");

        byte[] manifestBytes = ReadBounded(Path.Combine(fullPath, "pack.json"));
        string manifestText = Decode(manifestBytes, "pack.json");
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(manifestText); }
        catch (JsonException e) { throw Refuse("pack.json is invalid JSON: " + e.Message); }

        string id, version, sha256;
        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Refuse("pack.json must be an object.");
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name != "schema" && property.Name != "id" && property.Name != "version"
                    && property.Name != "content" && property.Name != "sha256")
                    throw Refuse("pack.json has unknown field '" + property.Name + "'.");
                if (!fields.TryAdd(property.Name, property.Value))
                    throw Refuse("pack.json field '" + property.Name + "' is defined twice.");
            }
            if (!fields.TryGetValue("schema", out var schema) || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out int number) || number != 1)
                throw Refuse("pack.json schema must be 1.");
            id = Text(fields, "id");
            version = Text(fields, "version");
            sha256 = Text(fields, "sha256");
            if (!IsSafeId(id)) throw Refuse("pack id must be 1-48 lowercase ASCII letters, digits, dots or hyphens, with nonempty segments beginning and ending in a letter or digit.");
            if (!IsSafeVersion(version)) throw Refuse("version must be 1-32 ASCII letters, digits, dots, hyphens or plus signs.");
            if (Text(fields, "content") != "items.json") throw Refuse("content must be exactly 'items.json'.");
            if (sha256.Length != 64) throw Refuse("sha256 must contain 64 hexadecimal characters.");
            foreach (char c in sha256)
                if (!IsHex(c)) throw Refuse("sha256 must contain 64 hexadecimal characters.");
            sha256 = sha256.ToLowerInvariant();
        }

        byte[] contentBytes = ReadBounded(Path.Combine(fullPath, "items.json"));
        using (var hasher = SHA256.Create())
        {
            string actual = BitConverter.ToString(hasher.ComputeHash(contentBytes)).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(actual, sha256, StringComparison.Ordinal)) throw Refuse("items.json sha256 does not match pack.json.");
        }
        string contentText = Decode(contentBytes, "items.json");
        // Pack files have stricter duplicate-key handling than legacy plugin content files.
        try
        {
            using var contentJson = JsonDocument.Parse(contentText, new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            EnsureUniqueFields(contentJson.RootElement);
        }
        catch (JsonException e) { throw Refuse("items.json is invalid JSON: " + e.Message); }
        if (!ContentFileParser.TryParse(id + "/items.json", contentText, out var document, out var errors))
            throw Refuse(string.Join(" ", errors));
        if (document!.Items.Count > MaxItems) throw Refuse("items.json must define at most 128 items.");
        foreach (var item in document.Items)
            if (!item.Id.StartsWith(id + ".", StringComparison.Ordinal) || item.Id.Length == id.Length + 1)
                throw Refuse("item '" + item.Id + "' must begin with pack id '" + id + ".'.");

        return new DataPackManifest(fullPath, id, version, sha256, document);
    }

    private static byte[] ReadBounded(string path)
    {
        EnsureNoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes) throw Refuse(Path.GetFileName(path) + " exceeds 256 KiB.");
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length + count > MaxFileBytes) throw Refuse(Path.GetFileName(path) + " exceeds 256 KiB.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static void EnsureNoLinks(string path)
    {
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current!) & FileAttributes.ReparsePoint) != 0)
                throw Refuse("reparse points are not allowed in pack paths.");
    }

    private static string Decode(byte[] bytes, string file)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw Refuse(file + " must contain valid UTF-8."); }
    }

    private static void EnsureUniqueFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!fields.Add(property.Name)) throw Refuse("items.json field '" + property.Name + "' is defined twice.");
                EnsureUniqueFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) EnsureUniqueFields(item);
    }

    private static string Text(Dictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw Refuse("pack.json field '" + name + "' must be a string.");
        return value.GetString()!;
    }

    private static bool AlphaNumeric(char c) => c >= 'a' && c <= 'z' || c >= '0' && c <= '9';
    private static bool IsSafeId(string id)
    {
        if (id.Length < 1 || id.Length > 48 || !AlphaNumeric(id[0]) || !AlphaNumeric(id[id.Length - 1])) return false;
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            if (!AlphaNumeric(c) && c != '.' && c != '-') return false;
            if ((c == '.' || c == '-') && (i == 0 || !AlphaNumeric(id[i - 1]) || i + 1 == id.Length || !AlphaNumeric(id[i + 1]))) return false;
        }
        return true;
    }

    private static bool IsSafeVersion(string version)
    {
        if (version.Length < 1 || version.Length > 32) return false;
        foreach (char c in version)
            if (!AlphaNumeric(c) && !(c >= 'A' && c <= 'Z') && c != '.' && c != '-' && c != '+') return false;
        return true;
    }

    private static bool IsHex(char c) => c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F';
    private static InvalidDataException Refuse(string reason) => new("Data pack refused: " + reason);
}
