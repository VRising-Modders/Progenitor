using System;
using System.Security.Cryptography;
using System.Text;

namespace Progenitor.Networking;

/// <summary>
/// ECDSA P-256 + SHA-256 over canonical JSON. Ed25519 is BCL only from net8 and NSec
/// would be a second runtime dependency, so this is the algorithm the BepInEx 6 / net6
/// host actually ships. Signatures are IEEE P1363 raw <c>r||s</c> (64 bytes) as lowercase
/// hex; public keys are SPKI DER as base64; the fingerprint is the first 16 hex chars of
/// SHA-256(SPKI).
/// </summary>
internal sealed class ModManifestKey : IDisposable
{
    private ECDsa? _ecdsa;

    internal ModManifestKey(ECDsa ecdsa, byte[] pkcs8, byte[] spki, string fp)
    {
        _ecdsa = ecdsa;
        Pkcs8 = pkcs8;
        Spki = spki;
        SpkiBase64 = Convert.ToBase64String(spki);
        Fingerprint = fp;
    }

    public byte[] Pkcs8 { get; }
    public byte[] Spki { get; }
    public string SpkiBase64 { get; }
    public string Fingerprint { get; }

    public string Sign(ReadOnlySpan<byte> canonicalUtf8)
    {
        var ecdsa = _ecdsa ?? throw new ObjectDisposedException(nameof(ModManifestKey));
        byte[] sig = ecdsa.SignData(canonicalUtf8.ToArray(), HashAlgorithmName.SHA256);
        return ModManifestCrypto.ToHex(sig);
    }

    public ManifestOperator ToOperator(string name) =>
        new(name ?? "", SpkiBase64, Fingerprint);

    public void Dispose()
    {
        _ecdsa?.Dispose();
        _ecdsa = null;
    }
}

/// <summary>Key generation, PEM-less file bytes, sign and verify for <see cref="ModManifestKey"/>.</summary>
internal static class ModManifestCrypto
{
    public const string AlgorithmName = "ECDSA P-256 + SHA-256";
    public const int SignatureHexLength = 128;
    public const int FingerprintHexLength = 16;

    public static ModManifestKey Generate()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            return FromEcdsa(ecdsa);
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }

    public static bool TryImportPkcs8(byte[] pkcs8, out ModManifestKey key, out string? error)
    {
        key = null!;
        error = null;
        if (pkcs8 == null || pkcs8.Length == 0)
        {
            error = "private key is empty";
            return false;
        }
        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportPkcs8PrivateKey(pkcs8, out int read);
            if (read <= 0)
            {
                error = "PKCS#8 import read 0 bytes";
                ecdsa.Dispose();
                return false;
            }
            key = FromEcdsa(ecdsa);
            return true;
        }
        catch (Exception e)
        {
            ecdsa.Dispose();
            error = e.Message;
            return false;
        }
    }

    public static bool TryImportSpki(string spkiBase64, out byte[] spki, out string fp, out string? error)
    {
        spki = Array.Empty<byte>();
        fp = "";
        error = null;
        if (string.IsNullOrWhiteSpace(spkiBase64))
        {
            error = "SPKI is empty";
            return false;
        }
        try
        {
            spki = Convert.FromBase64String(spkiBase64.Trim());
        }
        catch (FormatException e)
        {
            error = e.Message;
            return false;
        }
        if (spki.Length == 0)
        {
            error = "SPKI is empty";
            return false;
        }
        fp = Fingerprint(spki);
        return true;
    }

    public static string Fingerprint(ReadOnlySpan<byte> spki)
    {
        byte[] hash = SHA256.HashData(spki.ToArray());
        return ToHex(hash.AsSpan(0, 8));
    }

    public static bool Verify(string spkiBase64, ReadOnlySpan<byte> canonicalUtf8, string signatureHex)
    {
        if (!TryImportSpki(spkiBase64, out var spki, out _, out _)) return false;
        return Verify(spki, canonicalUtf8, signatureHex);
    }

    public static bool Verify(ReadOnlySpan<byte> spki, ReadOnlySpan<byte> canonicalUtf8, string signatureHex)
    {
        if (!TryParseHex(signatureHex, out var sig) || sig.Length != 64) return false;
        if (spki.Length == 0) return false;
        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            return ecdsa.VerifyData(canonicalUtf8.ToArray(), sig, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryParseHex(string? hex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.Trim();
        if ((hex.Length & 1) != 0) return false;
        try
        {
            bytes = Convert.FromHexString(hex);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ToHex(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length * 2);
        for (int i = 0; i < data.Length; i++)
            sb.Append(data[i].ToString("x2"));
        return sb.ToString();
    }

    public static byte[] Utf8(string canonical) => Encoding.UTF8.GetBytes(canonical ?? "");

    private static ModManifestKey FromEcdsa(ECDsa ecdsa)
    {
        byte[] pkcs8 = ecdsa.ExportPkcs8PrivateKey();
        byte[] spki = ecdsa.ExportSubjectPublicKeyInfo();
        return new ModManifestKey(ecdsa, pkcs8, spki, Fingerprint(spki));
    }
}
