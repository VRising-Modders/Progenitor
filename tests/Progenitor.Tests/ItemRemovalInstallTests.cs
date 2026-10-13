using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Progenitor.Content;
using Progenitor.Install;
using Xunit;

namespace Progenitor.Tests;

public sealed class ItemRemovalInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-removal-install-" + Guid.NewGuid().ToString("N"));
    private string Bep => Path.Combine(_root, "BepInEx");
    private string OwnerFile => Path.Combine(Bep, ItemOwnerLedger.RelativeFileName);
    private ServerInstallLedgerEntry Add(string path, string owner, int guid, string category, bool wrongHash = false)
    {
        var absolute = Path.Combine(Bep, path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        var bytes = Encoding.UTF8.GetBytes("plugin:" + owner); File.WriteAllBytes(absolute, bytes);
        var ledger = ItemOwnerLedger.Load(OwnerFile);
        Assert.True(ledger.Register(owner + "/item", guid, owner, path)); Assert.True(ledger.Save(OwnerFile));
        return new ServerInstallLedgerEntry("BepInEx/" + path, wrongHash ? "bad" : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.Length, category, "package:Author-Pack@1", DateTime.UtcNow);
    }
    private void InstallRows(params ServerInstallLedgerEntry[] rows)
        => new ServerInstallLedger("1", "", "", DateTime.UtcNow, rows).Write(_root);
    private bool Removed(string owner) => ItemOwnerLedger.Load(OwnerFile).Eligible(owner, false, false, false, false).Count != 0;

    [Fact]
    public void Successfully_deleted_mod_records_item_cleanup_intent()
    {
        InstallRows(Add("progenitor-reload/mod.dll", "mod", 1, ServerInstallLedger.CategoryMod));
        var result = ServerInstall.RemovePackage(Bep, "package:Author-Pack@1");
        Assert.True(result.Ok); Assert.True(Removed("mod"));
        Assert.False(File.Exists(Path.Combine(Bep, "progenitor-reload/mod.dll")));
    }

    [Fact]
    public void Pending_cancel_does_not_record_item_cleanup()
    {
        InstallRows(Add("progenitor-pending/mod.dll", "mod", 1, ServerInstallLedger.CategoryPending));
        ServerInstall.RemovePackage(Bep, "package:Author-Pack@1");
        Assert.False(Removed("mod"));
    }

    [Fact]
    public void Hash_mismatch_preserves_file_and_item_cleanup_state()
    {
        InstallRows(Add("progenitor-reload/mod.dll", "mod", 1, ServerInstallLedger.CategoryMod, wrongHash: true));
        ServerInstall.RemovePackage(Bep, "package:Author-Pack@1");
        Assert.True(File.Exists(Path.Combine(Bep, "progenitor-reload/mod.dll"))); Assert.False(Removed("mod"));
    }

    [Fact]
    public void Mixed_package_does_not_convert_pending_cancel_to_permanent_item_removal()
    {
        InstallRows(Add("progenitor-pending/pending.dll", "pending", 1, ServerInstallLedger.CategoryPending),
            Add("progenitor-reload/mod.dll", "mod", 2, ServerInstallLedger.CategoryMod));
        ServerInstall.RemovePackage(Bep, "package:Author-Pack@1");
        Assert.False(Removed("pending")); Assert.True(Removed("mod"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
