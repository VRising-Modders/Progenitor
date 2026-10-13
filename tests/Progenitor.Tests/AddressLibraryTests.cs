using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Progenitor.Native;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of address-library id assignment, build ids, JSON round trip, and
/// <c>moduleBase + relative address</c> resolve. Does not load GameAssembly.dll.
/// </summary>
public class AddressLibraryTests : IDisposable
{
    public AddressLibraryTests() => AddressLibrary.Reset();

    public void Dispose()
    {
        AddressLibrary.LogSink = null;
        AddressLibrary.Reset();
    }

    [Fact]
    public void ToImageOffset_keeps_a_value_below_image_base()
    {
        Assert.Equal(0x8129EF0, AddressId.ToImageOffset(0x8129EF0, AddressId.DefaultImageBase));
        Assert.Equal(0, AddressId.ToImageOffset(0, AddressId.DefaultImageBase));
    }

    [Fact]
    public void ToImageOffset_subtracts_image_base_from_a_va()
    {
        Assert.Equal(0x8129EF0, AddressId.ToImageOffset(AddressId.DefaultImageBase + 0x8129EF0, AddressId.DefaultImageBase));
    }

    [Fact]
    public void Assign_output_index_matches_input_index()
    {
        // Lockstep with tools/addresslib/build.mjs assignIds: grouping by Name must not
        // reorder. A Map-iteration implementation would put A$$First before Z$$Last.
        var methods = new[]
        {
            new DumpMethod("Z$$Last", "i", 3),
            new DumpMethod("A$$First", "i", 1),
            new DumpMethod("A$$First", "v", 2),
        };
        var assigned = AddressId.Assign(methods);
        Assert.Equal(3, assigned.Count);
        Assert.Equal("Z$$Last", assigned[0].Id);
        Assert.Equal(3, assigned[0].ImageOffset);
        Assert.Equal("A$$First#i", assigned[1].Id);
        Assert.Equal(1, assigned[1].ImageOffset);
        Assert.Equal("A$$First#v", assigned[2].Id);
        Assert.Equal(2, assigned[2].ImageOffset);
    }

    [Fact]
    public void Assign_keeps_a_unique_dumper_name()
    {
        var methods = new[]
        {
            new DumpMethod("ProjectM.ClientBootstrapSystem$$OnReliableEventsReceived", "viii", 0x8137AB0),
        };
        var assigned = AddressId.Assign(methods);
        Assert.Single(assigned);
        Assert.Equal("ProjectM.ClientBootstrapSystem$$OnReliableEventsReceived", assigned[0].Id);
        Assert.Equal(0x8137AB0, assigned[0].ImageOffset);
    }

    [Fact]
    public void Assign_keeps_generic_and_nested_punctuation()
    {
        var methods = new[]
        {
            new DumpMethod("NetworkEvents$$SendEvent<ValidateItemKeyLookupEvent>", "iiiii", 100),
            new DumpMethod("ProjectM.BuildItemKeyLookupSystem.IFE_1.ResolvedChunk$$Get", "iiii", 200),
        };
        var assigned = AddressId.Assign(methods);
        Assert.Equal("NetworkEvents$$SendEvent<ValidateItemKeyLookupEvent>", assigned[0].Id);
        Assert.Equal("ProjectM.BuildItemKeyLookupSystem.IFE_1.ResolvedChunk$$Get", assigned[1].Id);
    }

    [Fact]
    public void Assign_suffixes_overloads_with_type_signature()
    {
        var methods = new[]
        {
            new DumpMethod("ProjectM.ServerBootstrapSystem$$TryAuthenticate", "viiijiiiiiiii", 10),
            new DumpMethod("ProjectM.ServerBootstrapSystem$$TryAuthenticate", "iijiiiiiiiiii", 20),
        };
        var assigned = AddressId.Assign(methods);
        Assert.Equal(2, assigned.Count);
        Assert.Equal("ProjectM.ServerBootstrapSystem$$TryAuthenticate#viiijiiiiiiii", assigned[0].Id);
        Assert.Equal("ProjectM.ServerBootstrapSystem$$TryAuthenticate#iijiiiiiiiiii", assigned[1].Id);
        Assert.Equal(10, assigned[0].ImageOffset);
        Assert.Equal(20, assigned[1].ImageOffset);
    }

    [Fact]
    public void Assign_numbers_leftover_collisions_after_type_signature()
    {
        var methods = new[]
        {
            new DumpMethod("Type$$Overload", "iii", 1),
            new DumpMethod("Type$$Overload", "iii", 2),
            new DumpMethod("Type$$Overload", "vii", 3),
        };
        var assigned = AddressId.Assign(methods);
        Assert.Equal("Type$$Overload#iii", assigned[0].Id);
        Assert.Equal("Type$$Overload#iii#2", assigned[1].Id);
        Assert.Equal("Type$$Overload#vii", assigned[2].Id);
    }

    [Fact]
    public void AssignWanted_keeps_the_overload_suffix_from_the_full_dump()
    {
        var all = new[]
        {
            new DumpMethod("ProjectM.ServerBootstrapSystem$$TryAuthenticate", "viiijiiiiiiii", 10),
            new DumpMethod("ProjectM.ServerBootstrapSystem$$TryAuthenticate", "iijiiiiiiiiii", 20),
            new DumpMethod("ProjectM.GameBootstrap$$Start", "vii", 30),
        };
        var wanted = new[] { all[0], all[2] };
        var assigned = AddressId.AssignWanted(all, wanted);
        Assert.Equal(2, assigned.Count);
        Assert.Equal("ProjectM.ServerBootstrapSystem$$TryAuthenticate#viiijiiiiiiii", assigned[0].Id);
        Assert.Equal("ProjectM.GameBootstrap$$Start", assigned[1].Id);
        Assert.NotEqual("ProjectM.ServerBootstrapSystem$$TryAuthenticate", assigned[0].Id);
    }

    [Fact]
    public void Assign_of_a_wanted_subset_alone_would_have_dropped_the_suffix()
    {
        var onlyFirstOverload = new[]
        {
            new DumpMethod("ProjectM.ServerBootstrapSystem$$TryAuthenticate", "viiijiiiiiiii", 10),
        };
        var naive = AddressId.Assign(onlyFirstOverload);
        Assert.Equal("ProjectM.ServerBootstrapSystem$$TryAuthenticate", naive[0].Id);
    }

    [Fact]
    public void TryParseBuildId_and_BuildIdMatchesModule_use_size_and_checksum()
    {
        var sha = new byte[32];
        for (var i = 0; i < sha.Length; i++) sha[i] = (byte)(i + 1);
        string id = AddressId.ComputeBuildId(4096, sha, 0x11111111u, 0xAAAAu);
        Assert.True(AddressId.TryParseBuildId(id, out var size, out var stamp, out var sum, out var hex));
        Assert.Equal(4096, size);
        Assert.Equal(0x11111111u, stamp);
        Assert.Equal(0xAAAAu, sum);
        Assert.Equal(AddressId.Sha256PrefixHexChars, hex.Length);
        Assert.True(AddressId.BuildIdMatchesModule(id, 4096, 0xAAAAu));
        Assert.False(AddressId.BuildIdMatchesModule(id, 4097, 0xAAAAu));
        Assert.False(AddressId.BuildIdMatchesModule(id, 4096, 0xBBBBu));
        Assert.False(AddressId.TryParseBuildId("not-an-id", out _, out _, out _, out _));
        Assert.Equal(
            "source=disk path=C:\\core\\addresslib\\client-1.json build=1-0-0-abcd",
            AddressId.FormatTableSource("disk", @"C:\core\addresslib\client-1.json", "1-0-0-abcd"));
        Assert.Equal(
            "site=receive.client id=ProjectM.ClientBootstrapSystem$$OnReliableEventsReceived offset=0x2000 is not in an executable section; falling back to prefix",
            AddressId.FormatImageOffsetNotExecutable(
                AddressId.SiteReceiveClient,
                AddressId.ClientOnReliableEventsReceived,
                0x2000));
        Assert.Equal("StunProcess_MONO$$Start", AddressId.StunProcessMonoStart);
    }

    [Fact]
    public void Offset_outside_executable_sections_falls_back_to_prefix()
    {
        var text = new PeSection(0x1000, 0x500, 0x500, AddressId.ImageScnMemExecute);
        var data = new PeSection(0x2000, 0x500, 0x500, 0);
        Assert.True(AddressId.ImageOffsetLiesInExecutableSection(0x1100, new[] { text, data }));
        Assert.False(AddressId.ImageOffsetLiesInExecutableSection(0x2000, new[] { text, data }));
        Assert.False(AddressId.ImageOffsetLiesInExecutableSection(0x1100, Array.Empty<PeSection>()));

        var table = new AddressTable(
            "client",
            "1-aaaaaaaaaaaaaaaa",
            AddressId.DefaultImageBase,
            "2026-09-03T00:00:00Z",
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [AddressId.ClientOnReliableEventsReceived] = 0x2000,
            });
        AddressLibrary.Attach(
            table,
            (IntPtr)0x1000,
            moduleMemorySize: 0x10000,
            executableSections: new[] { text, data });
        var logs = new List<string>();
        AddressLibrary.LogSink = logs.Add;
        bool prefixCalled = false;

        var result = DetourSite.Resolve(
            AddressId.SiteReceiveClient,
            AddressId.ClientOnReliableEventsReceived,
            () =>
            {
                prefixCalled = true;
                return (IntPtr)0xBEEF;
            });

        Assert.True(result.Ok);
        Assert.True(prefixCalled);
        Assert.Equal(AddressId.PathFallback, result.Path);
        Assert.Equal((IntPtr)0xBEEF, result.Address);
        Assert.Contains(logs, line => line.Contains("falling back to prefix", StringComparison.Ordinal));
        Assert.Contains(AddressLibrary.LoadedSource ?? "", "attached");
    }

    [Fact]
    public void AcceptBuildFingerprint_refuses_a_size_or_checksum_mismatch()
    {
        var table = new AddressTable(
            "client",
            "4096-11111111-aaaa-aaaaaaaaaaaaaaaa",
            AddressId.DefaultImageBase,
            "2026-09-03T00:00:00Z",
            new Dictionary<string, long>(StringComparer.Ordinal) { ["x"] = 1 });
        Assert.True(AddressLibrary.AcceptBuildFingerprint(table, 4096, 0xAAAAu, "disk.json", out _));
        Assert.False(AddressLibrary.AcceptBuildFingerprint(table, 1, 0xAAAAu, "disk.json", out var whySize));
        Assert.Contains("length=1", whySize, StringComparison.Ordinal);
        Assert.False(AddressLibrary.AcceptBuildFingerprint(table, 4096, 0xBBBBu, "disk.json", out var whySum));
        Assert.Contains("checksum=bbbb", whySum, StringComparison.Ordinal);
        Assert.True(AddressLibrary.AcceptBuildFingerprint(table, moduleFileLength: -1, moduleCheckSum: 0, "skip", out _));
    }

    [Fact]
    public void ComputeBuildId_is_size_stamp_checksum_and_lowercase_sha_prefix()
    {
        var sha = new byte[32];
        for (var i = 0; i < sha.Length; i++) sha[i] = (byte)(i + 1);
        var hex = Convert.ToHexString(sha).ToLowerInvariant();
        var id = AddressId.ComputeBuildId(247473480, sha);
        Assert.Equal("247473480-0-0-" + hex.Substring(0, AddressId.Sha256PrefixHexChars), id);
        var withPe = AddressId.ComputeBuildId(247473480, sha, 0x44332211u, 0xDDCCBBAAu);
        Assert.Equal("247473480-44332211-ddccbbaa-" + hex.Substring(0, AddressId.Sha256PrefixHexChars), withPe);
        Assert.Equal(id, id.ToLowerInvariant());
        Assert.Equal(withPe, withPe.ToLowerInvariant());
    }

    [Fact]
    public void ComputeBuildIdFromFile_matches_size_and_sha256_of_the_prefix()
    {
        var dir = Path.Combine(Path.GetTempPath(), "progenitor-addresslib", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "fake.dll");
        try
        {
            var bytes = Encoding.UTF8.GetBytes("address-library-fixture");
            File.WriteAllBytes(path, bytes);
            var expectedHash = SHA256.HashData(bytes);
            var expected = AddressId.ComputeBuildId(bytes.Length, expectedHash, 0, 0);
            Assert.Equal(expected, AddressId.ComputeBuildIdFromFile(path));
            Assert.Equal(expected, AddressId.ComputeBuildIdFromPrefix(bytes, bytes.Length));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ComputeBuildIdFromFile_hashes_only_the_first_64kb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "progenitor-addresslib", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var a = Path.Combine(dir, "a.dll");
        var b = Path.Combine(dir, "b.dll");
        try
        {
            var prefix = new byte[AddressId.BuildIdPrefixBytes];
            for (var i = 0; i < prefix.Length; i++) prefix[i] = (byte)(i * 3);
            var fileA = new byte[AddressId.BuildIdPrefixBytes + 16];
            var fileB = new byte[AddressId.BuildIdPrefixBytes + 16];
            Buffer.BlockCopy(prefix, 0, fileA, 0, prefix.Length);
            Buffer.BlockCopy(prefix, 0, fileB, 0, prefix.Length);
            for (var i = 0; i < 16; i++)
            {
                fileA[prefix.Length + i] = 1;
                fileB[prefix.Length + i] = 2;
            }
            File.WriteAllBytes(a, fileA);
            File.WriteAllBytes(b, fileB);
            var idA = AddressId.ComputeBuildIdFromFile(a);
            var idB = AddressId.ComputeBuildIdFromFile(b);
            Assert.Equal(idA, idB);
            Assert.Equal(AddressId.ComputeBuildIdFromPrefix(prefix, fileA.Length), idA);
            Assert.StartsWith(fileA.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-", idA);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ComputeBuildIdFromFile_same_size_different_pe_timestamp_differs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "progenitor-addresslib", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var a = Path.Combine(dir, "a.dll");
        var b = Path.Combine(dir, "b.dll");
        try
        {
            var fileA = MakePeFixture(size: 4096, timeDateStamp: 0x11111111u, checkSum: 0xAAAAu);
            var fileB = MakePeFixture(size: 4096, timeDateStamp: 0x22222222u, checkSum: 0xAAAAu);
            File.WriteAllBytes(a, fileA);
            File.WriteAllBytes(b, fileB);
            var idA = AddressId.ComputeBuildIdFromFile(a);
            var idB = AddressId.ComputeBuildIdFromFile(b);
            Assert.NotEqual(idA, idB);
            Assert.Contains("-11111111-", idA);
            Assert.Contains("-22222222-", idB);
            Assert.StartsWith("4096-", idA);
            Assert.StartsWith("4096-", idB);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void TryReadPeIdentifiers_reads_timestamp_and_checksum()
    {
        var pe = new byte[0x100];
        pe[0] = (byte)'M';
        pe[1] = (byte)'Z';
        pe[0x3C] = 0x80;
        pe[0x80] = (byte)'P';
        pe[0x81] = (byte)'E';
        // TimeDateStamp at PE+8
        pe[0x88] = 0x11;
        pe[0x89] = 0x22;
        pe[0x8A] = 0x33;
        pe[0x8B] = 0x44;
        // CheckSum at optional-header + 64 = PE+24+64 = 0x80+88 = 0xD8
        pe[0xD8] = 0xAA;
        pe[0xD9] = 0xBB;
        pe[0xDA] = 0xCC;
        pe[0xDB] = 0xDD;
        Assert.True(AddressId.TryReadPeIdentifiers(pe, out var stamp, out var sum));
        Assert.Equal(0x44332211u, stamp);
        Assert.Equal(0xDDCCBBAAu, sum);
        Assert.False(AddressId.TryReadPeIdentifiers(Encoding.ASCII.GetBytes("not-a-pe"), out _, out _));
    }

    [Fact]
    public void TryReadPeSections_finds_the_executable_section()
    {
        var pe = MakePeWithSections();
        Assert.True(AddressId.TryReadPeSections(pe, out var sections));
        Assert.Equal(2, sections.Length);
        Assert.True(sections[0].IsExecutable);
        Assert.False(sections[1].IsExecutable);
        Assert.True(AddressId.ImageOffsetLiesInExecutableSection(0x1000, sections));
        Assert.False(AddressId.ImageOffsetLiesInExecutableSection(0x2000, sections));
    }

    [Fact]
    public void Table_round_trips_header_and_entries()
    {
        var entries = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["ProjectM.GameBootstrap$$Start"] = 0x1111,
            ["ProjectM.ServerBootstrapSystem$$TryAuthenticate#viiijiiiiiiii"] = 0x2222,
        };
        var original = new AddressTable("client", "123-abcdabcdabcdabcd", AddressId.DefaultImageBase, "2026-09-03T00:00:00Z", entries);

        Assert.True(AddressTable.TryParse(original.ToJson(), out var parsed, out var error));
        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal(original.Side, parsed!.Side);
        Assert.Equal(original.Build, parsed.Build);
        Assert.Equal(original.ImageBase, parsed.ImageBase);
        Assert.Equal(original.GeneratedUtc, parsed.GeneratedUtc);
        Assert.Equal(2, parsed.Count);
        Assert.Equal(0x1111, parsed.Entries["ProjectM.GameBootstrap$$Start"]);
        Assert.Equal(0x2222, parsed.Entries["ProjectM.ServerBootstrapSystem$$TryAuthenticate#viiijiiiiiiii"]);
    }

    [Fact]
    public void TryParse_reads_the_generator_fixture_shape()
    {
        const string json = """
            {
              "side": "server",
              "build": "248598344-41ba4eb27c53dd9d",
              "imageBase": 6442450944,
              "generatedUtc": "2026-09-03T12:00:00Z",
              "count": 2,
              "entries": {
                "ProjectM.ChatMessageSystem$$OnUpdate": 35339,
                "ProjectM.ItemKeyLookup$$Dispose": 47219
              }
            }
            """;
        Assert.True(AddressTable.TryParse(json, out var table, out var error));
        Assert.Null(error);
        Assert.Equal("server", table!.Side);
        Assert.Equal("248598344-41ba4eb27c53dd9d", table.Build);
        Assert.Equal(AddressId.DefaultImageBase, table.ImageBase);
        Assert.Equal(2, table.Count);
        Assert.True(table.TryGetImageOffset("ProjectM.ChatMessageSystem$$OnUpdate", out var imageOffset));
        Assert.Equal(35339, imageOffset);
        Assert.False(table.TryGetImageOffset("missing", out _));
    }

    [Fact]
    public void TryParse_empty_or_broken_json_is_false_not_thrown()
    {
        Assert.False(AddressTable.TryParse("", out var empty, out var emptyError));
        Assert.Null(empty);
        Assert.Contains("empty", emptyError, StringComparison.OrdinalIgnoreCase);

        Assert.False(AddressTable.TryParse("{not-json", out var broken, out var brokenError));
        Assert.Null(broken);
        Assert.False(string.IsNullOrEmpty(brokenError));
    }

    [Fact]
    public void TryResolve_rejects_an_offset_outside_the_module()
    {
        var table = new AddressTable(
            "client",
            "1-aaaaaaaaaaaaaaaa",
            AddressId.DefaultImageBase,
            "2026-09-03T00:00:00Z",
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["in.range"] = 0x10,
                ["at.end"] = 0x100,
                ["too.big"] = long.MaxValue,
            });
        AddressLibrary.Attach(table, (IntPtr)0x1000, moduleMemorySize: 0x100);

        Assert.True(AddressLibrary.TryResolve("in.range", out var ok));
        Assert.Equal(AddressLibrary.AddImageOffset((IntPtr)0x1000, 0x10, 0x100), ok);
        Assert.False(AddressLibrary.TryResolve("at.end", out var atEnd));
        Assert.Equal(IntPtr.Zero, atEnd);
        Assert.False(AddressLibrary.TryResolve("too.big", out var tooBig));
        Assert.Equal(IntPtr.Zero, tooBig);
        Assert.Equal(IntPtr.Zero, AddressLibrary.AddImageOffset((IntPtr)0x1000, 0x100, 0x100));
    }

    [Fact]
    public void Failed_table_lookup_does_not_latch_LoadAttempted()
    {
        AddressLibrary.Reset();
        Assert.False(AddressLibrary.TryFindTable("client", "no-such-build", out var found, out var why));
        Assert.Null(found);
        Assert.False(string.IsNullOrEmpty(why));
        Assert.False(AddressLibrary.LoadAttempted);
        Assert.False(AddressLibrary.TryFindTable("client", "no-such-build", out _, out _));
        Assert.False(AddressLibrary.LoadAttempted);
    }

    [Fact]
    public void TryResolve_adds_the_offset_to_the_attached_module_base()
    {
        var table = new AddressTable(
            "client",
            "1-aaaaaaaaaaaaaaaa",
            AddressId.DefaultImageBase,
            "2026-09-03T00:00:00Z",
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["ProjectM.ClientBootstrapSystem$$OnReliableEventsReceived"] = 0x10,
            });
        var moduleBase = unchecked((IntPtr)AddressId.DefaultImageBase);
        AddressLibrary.Attach(table, moduleBase);

        Assert.True(AddressLibrary.TryResolve(
            "ProjectM.ClientBootstrapSystem$$OnReliableEventsReceived", out var address));
        Assert.Equal(AddressLibrary.AddImageOffset(moduleBase, 0x10), address);
        Assert.Equal(unchecked((IntPtr)(AddressId.DefaultImageBase + 0x10)), address);
    }

    [Fact]
    public void TryResolve_missing_id_is_false_and_does_not_throw()
    {
        var table = new AddressTable(
            "client",
            "1-aaaaaaaaaaaaaaaa",
            AddressId.DefaultImageBase,
            "2026-09-03T00:00:00Z",
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["known"] = 4,
            });
        AddressLibrary.Attach(table, (IntPtr)0x1000);
        var logs = new List<string>();
        AddressLibrary.LogSink = logs.Add;

        Assert.False(AddressLibrary.TryResolve("missing", out var address));
        Assert.Equal(IntPtr.Zero, address);
        Assert.False(AddressLibrary.TryResolve("", out _));
        Assert.False(AddressLibrary.TryResolve(null!, out _));
        Assert.Contains(logs, line => line.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public void TryResolve_without_a_table_is_false()
    {
        AddressLibrary.Reset();
        Assert.False(AddressLibrary.IsLoaded);
        Assert.False(AddressLibrary.TryResolve("ProjectM.GameBootstrap$$Start", out var address));
        Assert.Equal(IntPtr.Zero, address);
    }

    [Fact]
    public void Load_logs_when_no_shipped_file_matches_the_build()
    {
        var logs = new List<string>();
        AddressLibrary.LogSink = logs.Add;
        AddressLibrary.Reset();

        // No GameAssembly.dll in the test process: EnsureLoaded records that and must not throw.
        Assert.False(AddressLibrary.TryResolve("anything", out var address));
        Assert.Equal(IntPtr.Zero, address);
        Assert.False(string.IsNullOrEmpty(AddressLibrary.Status));
        Assert.NotEmpty(logs);
        Assert.False(AddressLibrary.LoadAttempted);
        Assert.False(AddressLibrary.TryResolve("anything", out _));
        Assert.False(AddressLibrary.LoadAttempted);
    }

    [Fact]
    public void TableMatchesProcess_requires_side_and_build_not_just_the_filename()
    {
        var table = new AddressTable("client", "1-aaaaaaaaaaaaaaaa", AddressId.DefaultImageBase, "2026-09-03T00:00:00Z",
            new Dictionary<string, long>(StringComparer.Ordinal) { ["x"] = 1 });
        Assert.True(AddressLibrary.TableMatchesProcess(table, "client", "1-aaaaaaaaaaaaaaaa"));
        Assert.True(AddressLibrary.TableMatchesProcess(table, "CLIENT", "1-aaaaaaaaaaaaaaaa"));
        Assert.False(AddressLibrary.TableMatchesProcess(table, "server", "1-aaaaaaaaaaaaaaaa"));
        Assert.False(AddressLibrary.TableMatchesProcess(table, "client", "2-bbbbbbbbbbbbbbbb"));
        Assert.False(AddressLibrary.TableMatchesProcess(table, "", "1-aaaaaaaaaaaaaaaa"));
    }

    [Fact]
    public void TryFindTable_refuses_a_json_whose_fields_do_not_match_the_requested_side_and_build()
    {
        AddressLibrary.Reset();
        const string json = """
            {
              "side": "client",
              "build": "planted-id",
              "imageBase": 6442450944,
              "entries": { "ProjectM.ClientBootstrapSystem$$OnReliableEventsReceived": 1 }
            }
            """;
        Assert.True(AddressTable.TryParse(json, out var table, out _));
        Assert.False(AddressLibrary.TableMatchesProcess(table!, "client", "real-build"));
        Assert.False(AddressLibrary.TryFindTable("client", "real-build", out var found, out var why));
        Assert.Null(found);
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    public void Candidate_files_are_next_to_core_and_ignore_the_environment_override()
    {
        var previous = Environment.GetEnvironmentVariable(AddressLibrary.IgnoredOverrideEnvironmentVariable);
        try
        {
            var decoy = Path.Combine(Path.GetTempPath(), "progenitor-addresslib-decoy", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(decoy);
            Environment.SetEnvironmentVariable(AddressLibrary.IgnoredOverrideEnvironmentVariable, decoy);
            var files = AddressLibrary.ListCandidateFiles("client", "1-abcdabcdabcdabcd");
            Assert.NotEmpty(files);
            foreach (var path in files)
            {
                Assert.False(path.StartsWith(decoy, StringComparison.OrdinalIgnoreCase), path);
                var name = Path.GetFileName(path);
                Assert.Equal("client-1-abcdabcdabcdabcd.json", name, StringComparer.OrdinalIgnoreCase);
                Assert.False(IsPluginsDirectory(path), path);
            }
            var asmDir = Path.GetDirectoryName(typeof(AddressLibrary).Assembly.Location);
            Assert.Contains(files, p => string.Equals(Path.GetDirectoryName(p), asmDir, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(files, p =>
            {
                var dir = Path.GetDirectoryName(p);
                return dir != null && string.Equals(Path.GetFileName(dir), "addresslib", StringComparison.OrdinalIgnoreCase);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(AddressLibrary.IgnoredOverrideEnvironmentVariable, previous);
        }
    }

    private static bool IsPluginsDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            if (string.Equals(Path.GetFileName(dir), "plugins", StringComparison.OrdinalIgnoreCase))
                return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    [Fact]
    public void DetourSites_are_the_stable_dumper_names()
    {
        Assert.Equal("ProjectM.ClientBootstrapSystem$$OnReliableEventsReceived", AddressId.ClientOnReliableEventsReceived);
        Assert.Equal("ProjectM.ServerBootstrapSystem$$OnDataReceivedServer", AddressId.ServerOnDataReceivedServer);
        Assert.Equal("System.Collections.Generic.Dictionary<PrefabLookupKey, object>$$FindEntry", AddressId.ManagedDataFindEntry);
        Assert.Equal(3, AddressId.DetourSites.Length);
        Assert.Contains(AddressId.DetourSites, s => s.Site == AddressId.SiteReceiveClient && s.Id == AddressId.ClientOnReliableEventsReceived);
        Assert.Contains(AddressId.DetourSites, s => s.Site == AddressId.SiteReceiveServer && s.Id == AddressId.ServerOnDataReceivedServer);
        Assert.Contains(AddressId.DetourSites, s => s.Site == AddressId.SiteKeyProbeFindEntry && s.Id == AddressId.ManagedDataFindEntry);
    }

    [Fact]
    public void Resolve_uses_the_fake_table_before_the_prefix_fallback()
    {
        var table = new AddressTable(
            "client",
            "1-aaaaaaaaaaaaaaaa",
            AddressId.DefaultImageBase,
            "2026-09-03T00:00:00Z",
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [AddressId.ClientOnReliableEventsReceived] = 0x10,
            });
        AddressLibrary.Attach(table, (IntPtr)0x1000);
        var logs = new List<string>();
        AddressLibrary.LogSink = logs.Add;
        bool prefixCalled = false;

        var result = DetourSite.Resolve(
            AddressId.SiteReceiveClient,
            AddressId.ClientOnReliableEventsReceived,
            () =>
            {
                prefixCalled = true;
                return (IntPtr)0xDEAD;
            });

        Assert.True(result.Ok);
        Assert.Equal(AddressId.PathTable, result.Path);
        Assert.Equal(0x10, result.ImageOffset);
        Assert.Equal((IntPtr)0x1010, result.Address);
        Assert.False(prefixCalled);
        Assert.Contains(logs, line => line.Contains("path=table", StringComparison.Ordinal) && line.Contains("offset=0x10", StringComparison.Ordinal));
        var ping = AddressLibrary.DescribeForPing();
        Assert.True(ping.TablePresent);
        Assert.Equal("client", ping.Side);
        Assert.Equal("1-aaaaaaaaaaaaaaaa", ping.Build);
        Assert.Contains(ping.Sites, s => s.Site == AddressId.SiteReceiveClient && s.Path == AddressId.PathTable);
        Assert.Contains("table=yes", ping.Line, StringComparison.Ordinal);
        Assert.Contains("receive.client:table@0x10", ping.Line, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_falls_back_to_prefix_only_when_the_table_is_missing()
    {
        AddressLibrary.Reset();
        AddressLibrary.SetDetectedProcess("client", "9-deadbeefdeadbeef");
        var logs = new List<string>();
        AddressLibrary.LogSink = logs.Add;
        bool prefixCalled = false;

        var result = DetourSite.Resolve(
            AddressId.SiteReceiveClient,
            AddressId.ClientOnReliableEventsReceived,
            () =>
            {
                prefixCalled = true;
                return (IntPtr)0xBEEF;
            });

        Assert.True(result.Ok);
        Assert.True(prefixCalled);
        Assert.Equal(AddressId.PathFallback, result.Path);
        Assert.Equal((IntPtr)0xBEEF, result.Address);
        Assert.False(result.TablePresent);
        Assert.Contains(logs, line => line.Contains("path=fallback", StringComparison.Ordinal));
        Assert.Contains("fallback", AddressLibrary.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_does_not_fallback_when_the_table_is_present_but_the_id_is_missing()
    {
        var table = new AddressTable(
            "server",
            "2-bbbbbbbbbbbbbbbb",
            AddressId.DefaultImageBase,
            "2026-09-03T00:00:00Z",
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["other"] = 4,
            });
        AddressLibrary.Attach(table, (IntPtr)0x1000);
        var logs = new List<string>();
        AddressLibrary.LogSink = logs.Add;
        bool prefixCalled = false;

        var result = DetourSite.Resolve(
            AddressId.SiteReceiveServer,
            AddressId.ServerOnDataReceivedServer,
            () =>
            {
                prefixCalled = true;
                return (IntPtr)0xBEEF;
            });

        Assert.False(result.Ok);
        Assert.False(prefixCalled);
        Assert.Equal(AddressId.PathMissing, result.Path);
        Assert.Equal(IntPtr.Zero, result.Address);
        Assert.True(result.TablePresent);
        Assert.Contains("receive.server", result.Message, StringComparison.Ordinal);
        Assert.Contains("2-bbbbbbbbbbbbbbbb", result.Message, StringComparison.Ordinal);
        Assert.Contains("node tools/addresslib/build.mjs --side server", result.Message, StringComparison.Ordinal);
        Assert.Contains(logs, line => line.Contains("node tools/addresslib/build.mjs --side server", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_missing_table_and_failed_prefix_names_the_site_build_and_rebuild_command()
    {
        AddressLibrary.Reset();
        AddressLibrary.SetDetectedProcess("client", "123-abcdabcdabcdabcd");
        var logs = new List<string>();
        AddressLibrary.LogSink = logs.Add;

        var result = DetourSite.Resolve(
            AddressId.SiteReceiveClient,
            AddressId.ClientOnReliableEventsReceived,
            () => IntPtr.Zero);

        Assert.False(result.Ok);
        Assert.Equal(AddressId.PathMissing, result.Path);
        Assert.Contains("receive.client", result.Message, StringComparison.Ordinal);
        Assert.Contains(AddressId.ClientOnReliableEventsReceived, result.Message, StringComparison.Ordinal);
        Assert.Contains("123-abcdabcdabcdabcd", result.Message, StringComparison.Ordinal);
        Assert.Contains("node tools/addresslib/build.mjs --side client", result.Message, StringComparison.Ordinal);
        Assert.Equal(
            AddressId.FormatNotInstalled(
                AddressId.SiteReceiveClient,
                AddressId.ClientOnReliableEventsReceived,
                "client",
                "123-abcdabcdabcdabcd",
                tablePresent: false),
            result.Message);
        Assert.Contains(logs, line => line.Contains("node tools/addresslib/build.mjs --side client", StringComparison.Ordinal));
        Assert.Contains("table=no", AddressLibrary.Describe(), StringComparison.Ordinal);
        var ping = AddressLibrary.DescribeForPing();
        Assert.False(ping.TablePresent);
        Assert.Equal("client", ping.Side);
        Assert.Equal("123-abcdabcdabcdabcd", ping.Build);
        Assert.Contains(ping.Reports, r => r.Site == AddressId.SiteReceiveClient && r.Path == AddressId.PathMissing);
        var status = ping.ToStatus();
        Assert.NotNull(status);
    }

    [Fact]
    public void FormatPingLine_lists_table_fallback_and_missing()
    {
        var sites = new[]
        {
            new DetourSiteOutcome(AddressId.SiteReceiveClient, AddressId.ClientOnReliableEventsReceived, AddressId.PathTable, 0x10, (IntPtr)0x1010),
            new DetourSiteOutcome(AddressId.SiteReceiveServer, AddressId.ServerOnDataReceivedServer, AddressId.PathFallback, 0x20, (IntPtr)0x1020),
            new DetourSiteOutcome(AddressId.SiteKeyProbeFindEntry, AddressId.ManagedDataFindEntry, AddressId.PathMissing, 0, IntPtr.Zero),
        };
        string line = AddressId.FormatPingLine("SERVER", "9-deadbeefdeadbeef", tablePresent: false, sites);
        Assert.Equal(
            "native side=server build=9-deadbeefdeadbeef table=no sites=receive.client:table@0x10,receive.server:fallback@0x20,keyprobe.findentry:missing",
            line);
        var snap = new NativePingSnapshot("server", "9-deadbeefdeadbeef", tablePresent: false, "no table", sites);
        Assert.Equal(line, snap.Line);
        Assert.Equal(3, snap.Reports.Count);
        Assert.Equal(AddressId.PathMissing, snap.Reports[2].Path);
        Assert.Equal(0L, snap.Reports[2].Offset);
    }

    [Fact]
    public void FormatNotInstalled_is_the_readable_missing_table_message()
    {
        string text = AddressId.FormatNotInstalled(
            AddressId.SiteKeyProbeFindEntry,
            AddressId.ManagedDataFindEntry,
            "server",
            "248598344-0-0-aaaaaaaaaaaaaaaa",
            tablePresent: false);
        Assert.Contains("keyprobe.findentry", text, StringComparison.Ordinal);
        Assert.Contains(AddressId.ManagedDataFindEntry, text, StringComparison.Ordinal);
        Assert.Contains("248598344-0-0-aaaaaaaaaaaaaaaa", text, StringComparison.Ordinal);
        Assert.Contains(AddressId.FormatRebuildCommand("server"), text, StringComparison.Ordinal);
        Assert.Equal("node tools/addresslib/build.mjs --side server", AddressId.FormatRebuildCommand("SERVER"));
    }

    [Fact]
    public void TryParsePingLine_reads_bootstrap_pipe_format()
    {
        var line = AddressLibrary.FormatPingPipe(new DetourSiteOutcome(
            AddressId.SiteReceiveClient,
            AddressId.ClientOnReliableEventsReceived,
            AddressId.PathTable,
            0x8137AB0,
            unchecked((IntPtr)0x1808137AB0)));
        Assert.True(AddressLibrary.TryParsePingLine(line, out var parsed));
        Assert.Equal(AddressId.SiteReceiveClient, parsed.Site);
        Assert.Equal(AddressId.ClientOnReliableEventsReceived, parsed.Id);
        Assert.Equal(AddressId.PathTable, parsed.Path);
        Assert.Equal(0x8137AB0, parsed.ImageOffset);
        Assert.False(AddressLibrary.TryParsePingLine("", out _));
        Assert.False(AddressLibrary.TryParsePingLine("no-pipes", out _));
    }

    private static byte[] MakePeWithSections()
    {
        // DOS + PE at 0x80, COFF, tiny optional header, two 40-byte sections.
        const int pe = 0x80;
        const int optionalSize = 0x60;
        int sectionTable = pe + 24 + optionalSize;
        var bytes = new byte[sectionTable + 80];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        bytes[0x3C] = 0x80;
        bytes[pe] = (byte)'P';
        bytes[pe + 1] = (byte)'E';
        int coff = pe + 4;
        bytes[coff + 2] = 2; // NumberOfSections
        bytes[coff + 16] = optionalSize; // SizeOfOptionalHeader
        int text = sectionTable;
        WriteUInt32(bytes, text + 8, 0x500);
        WriteUInt32(bytes, text + 12, 0x1000);
        WriteUInt32(bytes, text + 16, 0x500);
        WriteUInt32(bytes, text + 36, AddressId.ImageScnMemExecute);
        int data = sectionTable + 40;
        WriteUInt32(bytes, data + 8, 0x500);
        WriteUInt32(bytes, data + 12, 0x2000);
        WriteUInt32(bytes, data + 16, 0x500);
        return bytes;
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }

    private static byte[] MakePeFixture(int size, uint timeDateStamp, uint checkSum)
    {
        var pe = new byte[size];
        pe[0] = (byte)'M';
        pe[1] = (byte)'Z';
        pe[0x3C] = 0x80;
        pe[0x80] = (byte)'P';
        pe[0x81] = (byte)'E';
        pe[0x88] = (byte)timeDateStamp;
        pe[0x89] = (byte)(timeDateStamp >> 8);
        pe[0x8A] = (byte)(timeDateStamp >> 16);
        pe[0x8B] = (byte)(timeDateStamp >> 24);
        pe[0xD8] = (byte)checkSum;
        pe[0xD9] = (byte)(checkSum >> 8);
        pe[0xDA] = (byte)(checkSum >> 16);
        pe[0xDB] = (byte)(checkSum >> 24);
        return pe;
    }
}
