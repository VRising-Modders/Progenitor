using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using BepInEx;
using BepInEx.Unity.IL2CPP.Hook;
using ProjectM;
using Stunlock.Network;

namespace Progenitor.Bootstrap;

/// <summary>
/// Process-lifetime native receive detours. Applied once per process; Core fills
/// <see cref="TrySetClient"/> / <see cref="TrySetServer"/> slots and must never
/// <c>Dispose</c> these detours. The trampoline is allocation-free on the success path and
/// never throws across the native boundary.
/// </summary>
internal static unsafe class ReceiveDetours
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OnReliableEventsReceivedFn(IntPtr self, NetBufferIn* netBuffer, IntPtr methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OnDataReceivedServerFn(IntPtr self, NetBufferIn* netBuffer, IntPtr netConnectionId, IntPtr methodInfo);

    /// <summary>Core's client dispatcher. Null means Core is down; fourccs are dropped.</summary>
    public delegate bool ClientDispatch(IntPtr self, IntPtr netBuffer, IntPtr methodInfo);

    /// <summary>Core's server dispatcher. Null means Core is down; fourccs are dropped.</summary>
    public delegate bool ServerDispatch(IntPtr self, IntPtr netBuffer, IntPtr netConnectionId, IntPtr methodInfo);

    private static ClientDispatch? _clientSlot;
    private static ServerDispatch? _serverSlot;

    private static INativeDetour? _clientDetour;
    private static OnReliableEventsReceivedFn? _clientOriginal;
    private static OnReliableEventsReceivedFn? _clientHook;

    private static INativeDetour? _serverDetour;
    private static OnDataReceivedServerFn? _serverOriginal;
    private static OnDataReceivedServerFn? _serverHook;

    private static int _clientThrowLogged;
    private static int _serverThrowLogged;
    private static int _emptyDropLogged;
    private static int _frozenOverflowLogged;
    private static int _frozenOverflowCount;
    private static int _frozen;
    private static int _inFlight;
    private static int _freezePinSeq;

    private static readonly object BufferGate = new();
    private static readonly List<BufferedPacket> FrozenBuffer = new();
    private static readonly object PingGate = new();
    private static readonly List<string> PingLines = new();

    /// <summary>
    /// Lockstep with <c>Progenitor.Native.AddressId</c>. Bootstrap cannot compile-reference Core.
    /// </summary>
    private const string ClientReceiveId = "ProjectM.ClientBootstrapSystem$$OnReliableEventsReceived";
    private const string ServerReceiveId = "ProjectM.ServerBootstrapSystem$$OnDataReceivedServer";
    private const string SiteClient = "receive.client";
    private const string SiteServer = "receive.server";
    private const string PathTable = "table";
    private const string PathFallback = "fallback";
    private const string PathMissing = "missing";
    private const int BuildIdPrefixBytes = 65536;
    private const int Sha256PrefixHexChars = 16;

    /// <summary>
    /// Pipe lines Core's ping snapshot merges: <c>site|id|path|offsetHex|addressHex</c>.
    /// Exposed publicly through <see cref="ReceiveDetourPing"/> because this type is
    /// internal and <c>Assembly.GetType</c> from collectible Core would miss it.
    /// </summary>
    internal static string[] NativePingLines
    {
        get { lock (PingGate) return PingLines.ToArray(); }
    }

    public static bool OwnsClient { get; private set; }
    public static bool OwnsServer { get; private set; }

    /// <summary>
    /// Core-swap freeze: the trampoline does not invoke the collectible dispatcher (so
    /// UnloadAlc can collect) and does not drop owned fourccs. Payloads are copied into
    /// Bootstrap and replayed after Unfreeze. Vanilla still hits original. Aborting a swap
    /// before Uninstall can then keep the old generation running: Unfreeze replays into
    /// the still-live slot.
    /// </summary>
    public static bool Frozen => Volatile.Read(ref _frozen) != 0;

    /// <summary>In-flight slot invokes. UnloadAlc must not run while this is nonzero.</summary>
    public static int InFlightCount => Volatile.Read(ref _inFlight);

    public static void Freeze() => Volatile.Write(ref _frozen, 1);

    /// <summary>
    /// Replay buffered owned packets into the live slot, then clear Frozen. Frozen stays
    /// set across the replay so a packet arriving on the net thread cannot dispatch live
    /// and reorder against the buffer. After the last replay, Frozen is cleared and one
    /// more drain catches packets that landed in the gap.
    /// </summary>
    public static void Unfreeze()
    {
        try
        {
            for (int i = 0; i < 8; i++)
            {
                if (!ReplayBuffered()) break;
            }
        }
        finally
        {
            Volatile.Write(ref _frozen, 0);
        }
        try { ReplayBuffered(); }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning($"ReceiveDetours: freeze replay after Unfreeze: {e.Message}");
        }
        NativePinRegistry.ReleaseGeneration(NativePinRegistry.FreezeGeneration);
    }

    /// <summary>
    /// Spin until in-flight slot invokes return. Call after <see cref="Freeze"/> and
    /// before Uninstall/UnloadAlc so a hook that copied the slot pointer cannot enter
    /// a dying ALC.
    /// </summary>
    public static void WaitForIdle()
    {
        for (int i = 0; i < 1000; i++)
        {
            if (Volatile.Read(ref _inFlight) == 0) return;
            Thread.SpinWait(64);
        }
        ProcessHost.Log?.LogWarning("ReceiveDetours: in-flight dispatch still running after Freeze.");
    }

    internal static void Install()
    {
        if (Plugin.IsServerProcess)
            TryInstallServer();
        else
            TryInstallClient();
    }

    /// <summary>
    /// Unload order (R10 finding 10): (1) null the Core dispatcher slots so an in-flight
    /// packet cannot enter a Core that is going away; (2) Dispose the <c>INativeDetour</c>
    /// only if Core is gone <em>and</em> the process is exiting. Bootstrap is
    /// process-lifetime: Unload of this plugin is not process-exit. Clearing
    /// <see cref="OwnsClient"/> / <see cref="OwnsServer"/> would let a later Core
    /// <c>CreateAndApply</c> on the same address. Leave the JMP and the empty-slot
    /// fourcc policy in place.
    /// </summary>
    internal static void Uninstall()
    {
        bool clientSlotLive = Volatile.Read(ref _clientSlot) != null;
        bool serverSlotLive = Volatile.Read(ref _serverSlot) != null;
        bool corePresent = ProcessHost.CoreAttached || CoreLoader.Assembly != null;

        Volatile.Write(ref _clientSlot, null);
        Volatile.Write(ref _serverSlot, null);

        // Plugin.Unload is not process-exit. MayDispose is kept so the rule is testable
        // and so a future process-exit path can Dispose without re-deriving the order.
        bool processExiting = false;
        if (!ReceiveSlotPolicy.MayDisposeReceiveDetour(clientSlotLive || serverSlotLive, corePresent, processExiting))
            return;

        OwnsClient = false;
        OwnsServer = false;
        _clientDetour?.Dispose();
        _clientDetour = null;
        _clientOriginal = null;
        _clientHook = null;
        _serverDetour?.Dispose();
        _serverDetour = null;
        _serverOriginal = null;
        _serverHook = null;
    }

    /// <summary>
    /// Point the client trampoline at Core's dispatcher, or null it. Returns false when this
    /// process did not apply the client detour (so Core may own it as a fallback).
    ///
    /// <para>Binding a dispatcher re-arms every once-per-generation report about that dispatcher
    /// (job FEVT1, R44 finding 4). These latches are Bootstrap statics — process lifetime — but
    /// what they report on is the <b>Core generation's</b> dispatcher, which is installed here and
    /// nulled on every Core Uninstall. Left un-armed, one benign throw in generation 1 silences
    /// <see cref="LogThrowOnce"/> for every later generation, and the failure it hides is a silent
    /// packet drop: a throwing dispatcher yields <c>consumed = false</c>, but
    /// <c>ReceiveSlotPolicy.Decide</c> still returns <c>Consumed</c> for an owned fourcc and
    /// <c>Consume</c> eats the packet, while <see cref="LogEmptyDropOnce"/> does not cover that
    /// path (it needs <c>!slotPresent</c>). The symptom is mod networking dying after a
    /// <c>.corereload</c> with a clean log.</para>
    /// </summary>
    public static bool TrySetClient(Delegate? dispatcher)
    {
        if (!OwnsClient) return false;
        Volatile.Write(ref _clientSlot, BindClient(dispatcher));
        if (dispatcher != null)
        {
            Interlocked.Exchange(ref _emptyDropLogged, 0);
            Interlocked.Exchange(ref _clientThrowLogged, 0);
        }
        return true;
    }

    /// <summary>
    /// Point the server trampoline at Core's dispatcher, or null it. Returns false when this
    /// process did not apply the server detour. Re-arms the server report latches on a bind for
    /// the reasons given on <see cref="TrySetClient"/> (job FEVT1, R44 finding 4).
    /// </summary>
    public static bool TrySetServer(Delegate? dispatcher)
    {
        if (!OwnsServer) return false;
        Volatile.Write(ref _serverSlot, BindServer(dispatcher));
        if (dispatcher != null)
        {
            Interlocked.Exchange(ref _emptyDropLogged, 0);
            Interlocked.Exchange(ref _serverThrowLogged, 0);
        }
        return true;
    }

    private static ClientDispatch? BindClient(Delegate? dispatcher)
    {
        if (dispatcher == null) return null;
        ClientDispatch bound = dispatcher is ClientDispatch typed
            ? typed
            : (ClientDispatch)Delegate.CreateDelegate(typeof(ClientDispatch), dispatcher.Target, dispatcher.Method);
        NativePinRegistry.KeepDelegate(bound, "receive.client", CoreLoader.CurrentPinGeneration);
        return bound;
    }

    private static ServerDispatch? BindServer(Delegate? dispatcher)
    {
        if (dispatcher == null) return null;
        ServerDispatch bound = dispatcher is ServerDispatch typed
            ? typed
            : (ServerDispatch)Delegate.CreateDelegate(typeof(ServerDispatch), dispatcher.Target, dispatcher.Method);
        NativePinRegistry.KeepDelegate(bound, "receive.server", CoreLoader.CurrentPinGeneration);
        return bound;
    }

    private static void TryInstallClient()
    {
        if (OwnsClient || _clientDetour != null) return;
        try
        {
            if (!TryResolveTarget(
                    SiteClient,
                    ClientReceiveId,
                    typeof(ClientBootstrapSystem),
                    nameof(ClientBootstrapSystem.OnReliableEventsReceived),
                    out var target))
                return;
            _clientHook = ClientHook;
            _clientDetour = INativeDetour.CreateAndApply(target, _clientHook, out _clientOriginal);
            OwnsClient = true;
            ProcessHost.Log?.LogInfo($"ReceiveDetours: client receive detour installed at 0x{target.ToInt64():X}.");
        }
        catch (Exception e)
        {
            OwnsClient = false;
            _clientDetour = null;
            _clientOriginal = null;
            _clientHook = null;
            ProcessHost.Log?.LogError($"ReceiveDetours: client install failed; Core will own the detour if it loads: {e}");
        }
    }

    private static void TryInstallServer()
    {
        if (OwnsServer || _serverDetour != null) return;
        try
        {
            if (!TryResolveTarget(
                    SiteServer,
                    ServerReceiveId,
                    typeof(ServerBootstrapSystem),
                    nameof(ServerBootstrapSystem.OnDataReceivedServer),
                    out var target))
                return;
            _serverHook = ServerHook;
            _serverDetour = INativeDetour.CreateAndApply(target, _serverHook, out _serverOriginal);
            OwnsServer = true;
            ProcessHost.Log?.LogInfo($"ReceiveDetours: server receive detour installed at 0x{target.ToInt64():X}.");
        }
        catch (Exception e)
        {
            OwnsServer = false;
            _serverDetour = null;
            _serverOriginal = null;
            _serverHook = null;
            ProcessHost.Log?.LogError($"ReceiveDetours: server install failed; Core will own the detour if it loads: {e}");
        }
    }

    /// <summary>
    /// Address-library table first (disk JSON next to Core, same fingerprint as
    /// <c>AddressId.ComputeBuildIdFromFile</c>). Prefix fallback only when that table is
    /// missing. A loaded table that lacks the id does not fall back. Lockstep with
    /// <c>Progenitor.Native.DetourSite.Resolve</c>.
    /// </summary>
    private static bool TryResolveTarget(string site, string addressId, Type interopType, string methodName, out IntPtr target)
    {
        target = IntPtr.Zero;
        string side = Plugin.IsServerProcess ? "server" : "client";
        string build = "";
        bool tablePresent = false;
        try
        {
            if (TryResolveFromTable(
                    addressId,
                    side,
                    out target,
                    out var imageOffset,
                    out build,
                    out tablePresent,
                    out var tablePath,
                    out var offsetNotExecutable)
                && target != IntPtr.Zero
                && !offsetNotExecutable)
            {
                RecordPing(site, addressId, PathTable, imageOffset, target);
                if (!string.IsNullOrEmpty(tablePath))
                    ProcessHost.Log?.LogInfo(
                        "ReceiveDetours: " + FormatTableSource("disk", tablePath, build));
                ProcessHost.Log?.LogInfo("ReceiveDetours: " + FormatResolvedLine(site, addressId, PathTable, imageOffset, target));
                return true;
            }

            if (offsetNotExecutable)
                ProcessHost.Log?.LogWarning("ReceiveDetours: " + FormatImageOffsetNotExecutable(site, addressId, imageOffset));

            // Missing id in a present table does not fall back. An in-module relative address that is
            // not in an executable section does: a leftover colliding JSON must not JMP .rdata.
            if (!tablePresent || offsetNotExecutable)
            {
                try
                {
                    target = NativeMethod.PointerByPrefix(interopType, methodName);
                }
                catch (Exception e)
                {
                    LogNotInstalled(site, addressId, side, build, tablePresent: false, e.Message);
                    return false;
                }

                if (target != IntPtr.Zero)
                {
                    long prefixImageOffset = ImageOffsetFromModule(target);
                    RecordPing(site, addressId, PathFallback, prefixImageOffset, target);
                    ProcessHost.Log?.LogInfo("ReceiveDetours: " + FormatResolvedLine(site, addressId, PathFallback, prefixImageOffset, target));
                    return true;
                }
            }

            LogNotInstalled(site, addressId, side, build, tablePresent, prefixError: null);
            return false;
        }
        catch (Exception e)
        {
            LogNotInstalled(site, addressId, side, build, tablePresent, e.Message);
            target = IntPtr.Zero;
            return false;
        }
    }

    private static void LogNotInstalled(string site, string addressId, string side, string build, bool tablePresent, string? prefixError)
    {
        RecordPing(site, addressId, PathMissing, 0, IntPtr.Zero);
        string message = FormatNotInstalled(site, addressId, side, build, tablePresent);
        if (!string.IsNullOrEmpty(prefixError))
            message = message + " Prefix fallback: " + prefixError;
        ProcessHost.Log?.LogError("ReceiveDetours: " + message);
    }

    private static void RecordPing(string site, string id, string path, long imageOffset, IntPtr address)
    {
        string line = site + "|" + id + "|" + path + "|"
            + imageOffset.ToString("X", CultureInfo.InvariantCulture) + "|"
            + address.ToInt64().ToString("X", CultureInfo.InvariantCulture);
        lock (PingGate)
        {
            for (int i = PingLines.Count - 1; i >= 0; i--)
            {
                if (!PingLines[i].StartsWith(site + "|", StringComparison.Ordinal)) continue;
                PingLines.RemoveAt(i);
            }
            PingLines.Add(line);
        }
    }

    // Lockstep with AddressId.FormatResolvedLine / FormatNotInstalled / FormatRebuildCommand.
    private static string FormatResolvedLine(string site, string id, string path, long imageOffset, IntPtr address) =>
        "site=" + site
        + " id=" + id
        + " path=" + path
        + " offset=0x" + imageOffset.ToString("X", CultureInfo.InvariantCulture)
        + " address=0x" + address.ToInt64().ToString("X", CultureInfo.InvariantCulture);

    private static string FormatNotInstalled(string site, string id, string side, string build, bool tablePresent)
    {
        if (string.IsNullOrEmpty(build)) build = "(unknown)";
        string cmd = "node tools/addresslib/build.mjs --side " + side;
        if (tablePresent)
        {
            return "Address library table for " + side + " build " + build
                + " has no relative address for site '" + site + "' (" + id
                + "). The detour was not installed. Rebuild with: " + cmd;
        }

        return "Address library has no table for " + side + " build " + build
            + ". Site '" + site + "' (" + id + ") was not installed. Rebuild with: " + cmd;
    }

    private static bool TryResolveFromTable(
        string addressId,
        string side,
        out IntPtr address,
        out long imageOffset,
        out string build,
        out bool tablePresent,
        out string? tablePath,
        out bool offsetNotExecutable)
    {
        address = IntPtr.Zero;
        imageOffset = 0;
        build = "";
        tablePresent = false;
        tablePath = null;
        offsetNotExecutable = false;
        if (!TryGetGameAssembly(out var moduleBase, out var modulePath, out var moduleSize))
            return false;
        long fileLength;
        uint peCheckSum;
        PeSectionRow[]? sections;
        try
        {
            build = ComputeBuildIdFromFile(modulePath, out fileLength, out peCheckSum, out sections);
        }
        catch { return false; }

        string? jsonPath = FindTablePath(side, build);
        if (jsonPath == null) return false;
        if (!TryReadTableImageOffset(jsonPath, side, build, addressId, out imageOffset, out var headerMatches))
        {
            // Filename hit is not enough: a planted JSON whose fields do not match this
            // process is treated as no table (prefix fallback). A matching header with
            // a missing id is a present table (no fallback).
            tablePresent = headerMatches;
            return false;
        }

        tablePresent = true;
        tablePath = jsonPath;
        if (!BuildIdMatchesModule(build, fileLength, peCheckSum))
        {
            tablePresent = false;
            tablePath = null;
            return false;
        }
        if (imageOffset < 0) return false;
        if (moduleSize > 0 && (ulong)imageOffset >= (ulong)moduleSize) return false;
        if (sections != null && sections.Length > 0 && !ImageOffsetInExecutableSection(imageOffset, sections))
        {
            offsetNotExecutable = true;
            return false;
        }
        var abs = unchecked((ulong)(long)moduleBase + (ulong)imageOffset);
        address = (IntPtr)(long)abs;
        return address != IntPtr.Zero;
    }

    private static string FormatTableSource(string source, string? path, string build) =>
        "source=" + source
        + (string.IsNullOrEmpty(path) ? "" : " path=" + path)
        + " build=" + (build ?? "");

    private static string FormatImageOffsetNotExecutable(string site, string id, long imageOffset) =>
        "site=" + site
        + " id=" + id
        + " offset=0x" + imageOffset.ToString("X", CultureInfo.InvariantCulture)
        + " is not in an executable section; falling back to prefix";

    private readonly struct PeSectionRow
    {
        public PeSectionRow(uint virtualAddress, uint virtualSize, uint sizeOfRaw, uint characteristics)
        {
            VirtualAddress = virtualAddress;
            VirtualSize = virtualSize;
            SizeOfRaw = sizeOfRaw;
            Characteristics = characteristics;
        }

        public uint VirtualAddress { get; }
        public uint VirtualSize { get; }
        public uint SizeOfRaw { get; }
        public uint Characteristics { get; }
    }

    private const uint ImageScnMemExecute = 0x20000000;

    private static bool ImageOffsetInExecutableSection(long imageOffset, PeSectionRow[] sections)
    {
        if (imageOffset < 0 || sections == null || sections.Length == 0) return false;
        ulong u = (ulong)imageOffset;
        for (int i = 0; i < sections.Length; i++)
        {
            var s = sections[i];
            if ((s.Characteristics & ImageScnMemExecute) == 0) continue;
            uint span = s.VirtualSize > s.SizeOfRaw ? s.VirtualSize : s.SizeOfRaw;
            if (span == 0) continue;
            if (u < s.VirtualAddress) continue;
            if (u - s.VirtualAddress >= span) continue;
            return true;
        }
        return false;
    }

    private static bool BuildIdMatchesModule(string build, long fileLength, uint peCheckSum)
    {
        if (string.IsNullOrEmpty(build)) return false;
        var parts = build.Split('-');
        if (parts.Length != 4) return false;
        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            return false;
        if (size != fileLength) return false;
        if (!uint.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var sum))
            return false;
        return sum == peCheckSum;
    }

    private static string? FindTablePath(string side, string build)
    {
        string fileName = side + "-" + build + ".json";
        try
        {
            string root = Paths.BepInExRootPath;
            if (string.IsNullOrEmpty(root)) return null;
            string core = CoreLayout.Folder(root);
            string nested = Path.Combine(core, "addresslib", fileName);
            if (File.Exists(nested)) return nested;
            string beside = Path.Combine(core, fileName);
            if (File.Exists(beside)) return beside;
        }
        catch
        {
            // Paths can throw during early load; treat as no table.
        }
        return null;
    }

    private static bool TryReadTableImageOffset(
        string path,
        string side,
        string build,
        string addressId,
        out long imageOffset,
        out bool headerMatches)
    {
        imageOffset = 0;
        headerMatches = false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!TryGetString(root, "side", out var tableSide)
                || !string.Equals(tableSide, side, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!TryGetString(root, "build", out var tableBuild)
                || !string.Equals(tableBuild, build, StringComparison.Ordinal))
                return false;
            headerMatches = true;
            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Object)
                return false;
            if (!entries.TryGetProperty(addressId, out var value)) return false;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out imageOffset)) return true;
            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out imageOffset))
                return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind != JsonValueKind.String) return false;
        value = el.GetString() ?? "";
        return !string.IsNullOrEmpty(value);
    }

    private static bool TryGetGameAssembly(out IntPtr moduleBase, out string modulePath, out long moduleSize)
    {
        moduleBase = IntPtr.Zero;
        modulePath = "";
        moduleSize = 0;
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            foreach (System.Diagnostics.ProcessModule module in process.Modules)
            {
                var name = module.ModuleName ?? "";
                if (!name.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
                    continue;
                moduleBase = module.BaseAddress;
                modulePath = module.FileName ?? "";
                try { moduleSize = module.ModuleMemorySize; }
                catch { moduleSize = 0; }
                return moduleBase != IntPtr.Zero && File.Exists(modulePath);
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    /// <summary>Lockstep with <c>AddressId.ComputeBuildIdFromFile</c>.</summary>
    private static string ComputeBuildIdFromFile(string path) =>
        ComputeBuildIdFromFile(path, out _, out _, out _);

    private static string ComputeBuildIdFromFile(
        string path,
        out long size,
        out uint checkSum,
        out PeSectionRow[]? sections)
    {
        size = new FileInfo(path).Length;
        checkSum = 0;
        sections = null;
        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: Math.Min(BuildIdPrefixBytes, 64 * 1024),
            FileOptions.SequentialScan);
        var n = size < 0 ? 0 : size;
        if (n > BuildIdPrefixBytes) n = BuildIdPrefixBytes;
        var buffer = new byte[(int)n];
        var read = 0;
        while (read < buffer.Length)
        {
            var got = fs.Read(buffer, read, buffer.Length - read);
            if (got <= 0) break;
            read += got;
        }
        byte[] prefix = buffer;
        if (read != buffer.Length)
        {
            prefix = new byte[read];
            Buffer.BlockCopy(buffer, 0, prefix, 0, read);
        }

        uint stamp = 0, sum = 0;
        TryReadPeIdentifiers(prefix, out stamp, out sum);
        checkSum = sum;
        if (TryReadPeSections(prefix, out var parsed) && parsed.Length > 0)
            sections = parsed;
        var hash = SHA256.HashData(prefix);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        if (hex.Length > Sha256PrefixHexChars)
            hex = hex.Substring(0, Sha256PrefixHexChars);
        return size.ToString(CultureInfo.InvariantCulture)
            + "-" + stamp.ToString("x", CultureInfo.InvariantCulture)
            + "-" + sum.ToString("x", CultureInfo.InvariantCulture)
            + "-" + hex;
    }

    private static bool TryReadPeSections(byte[] prefix, out PeSectionRow[] sections)
    {
        sections = Array.Empty<PeSectionRow>();
        if (prefix.Length < 0x40) return false;
        if (prefix[0] != (byte)'M' || prefix[1] != (byte)'Z') return false;
        int pe = prefix[0x3C] | (prefix[0x3D] << 8) | (prefix[0x3E] << 16) | (prefix[0x3F] << 24);
        if (pe < 0 || pe + 24 > prefix.Length) return false;
        if (prefix[pe] != (byte)'P' || prefix[pe + 1] != (byte)'E' || prefix[pe + 2] != 0 || prefix[pe + 3] != 0)
            return false;
        int coff = pe + 4;
        if (coff + 20 > prefix.Length) return false;
        int numberOfSections = prefix[coff + 2] | (prefix[coff + 3] << 8);
        int sizeOfOptional = prefix[coff + 16] | (prefix[coff + 17] << 8);
        if (numberOfSections <= 0 || numberOfSections > 96) return false;
        int optional = coff + 20;
        int sectionTable = optional + sizeOfOptional;
        int sectionBytes = numberOfSections * 40;
        if (sectionTable < 0 || sectionTable + sectionBytes > prefix.Length) return false;
        var list = new PeSectionRow[numberOfSections];
        for (int i = 0; i < numberOfSections; i++)
        {
            int row = sectionTable + (i * 40);
            uint virtualSize = (uint)(prefix[row + 8] | (prefix[row + 9] << 8) | (prefix[row + 10] << 16) | (prefix[row + 11] << 24));
            uint virtualAddress = (uint)(prefix[row + 12] | (prefix[row + 13] << 8) | (prefix[row + 14] << 16) | (prefix[row + 15] << 24));
            uint sizeOfRaw = (uint)(prefix[row + 16] | (prefix[row + 17] << 8) | (prefix[row + 18] << 16) | (prefix[row + 19] << 24));
            uint characteristics = (uint)(prefix[row + 36] | (prefix[row + 37] << 8) | (prefix[row + 38] << 16) | (prefix[row + 39] << 24));
            list[i] = new PeSectionRow(virtualAddress, virtualSize, sizeOfRaw, characteristics);
        }
        sections = list;
        return true;
    }

    private static bool TryReadPeIdentifiers(byte[] prefix, out uint timeDateStamp, out uint checkSum)
    {
        timeDateStamp = 0;
        checkSum = 0;
        if (prefix.Length < 0x40) return false;
        if (prefix[0] != (byte)'M' || prefix[1] != (byte)'Z') return false;
        int pe = prefix[0x3C] | (prefix[0x3D] << 8) | (prefix[0x3E] << 16) | (prefix[0x3F] << 24);
        if (pe < 0 || pe + 88 + 4 > prefix.Length) return false;
        if (prefix[pe] != (byte)'P' || prefix[pe + 1] != (byte)'E' || prefix[pe + 2] != 0 || prefix[pe + 3] != 0)
            return false;
        timeDateStamp = (uint)(prefix[pe + 8] | (prefix[pe + 9] << 8) | (prefix[pe + 10] << 16) | (prefix[pe + 11] << 24));
        int optional = pe + 24;
        if (optional + 66 > prefix.Length) return false;
        checkSum = (uint)(prefix[optional + 64] | (prefix[optional + 65] << 8) | (prefix[optional + 66] << 16) | (prefix[optional + 67] << 24));
        return true;
    }

    private static long ImageOffsetFromModule(IntPtr address)
    {
        if (!TryGetGameAssembly(out var moduleBase, out _, out _)) return 0;
        if (moduleBase == IntPtr.Zero || address == IntPtr.Zero) return 0;
        return (long)((ulong)address.ToInt64() - (ulong)(long)moduleBase);
    }

    private static void ClientHook(IntPtr self, NetBufferIn* netBuffer, IntPtr methodInfo)
    {
        try
        {
            bool frozen = Frozen;
            bool peeked = TryPeekClientEventId(netBuffer, out int eventId);
            if (frozen)
            {
                var frozenAction = ReceiveSlotPolicy.Decide(
                    slotPresent: false, slotConsumed: null, isEventsPacket: true, eventId, frozen: true);
                if (frozenAction == ReceiveAction.Buffered)
                {
                    bool enqueued = EnqueueFrozen(server: false, self, netBuffer, connectionId: IntPtr.Zero, methodInfo);
                    if (ReceiveSlotPolicy.ConsumeAfterFrozenEnqueue(enqueued))
                    {
                        Consume(netBuffer);
                        return;
                    }
                    // Overflow: do not Consume. Returning without original NAKs the
                    // reliable fourcc so the peer can resend after Unfreeze.
                    return;
                }
                CallClientOriginal(self, netBuffer, methodInfo);
                return;
            }

            var slot = Volatile.Read(ref _clientSlot);
            bool slotPresent = slot != null;
            bool? consumed = null;
            if (slotPresent)
            {
                Interlocked.Increment(ref _inFlight);
                try
                {
                    consumed = slot!(self, (IntPtr)netBuffer, methodInfo);
                }
                catch (Exception e)
                {
                    LogThrowOnce(ref _clientThrowLogged, "client", e);
                    consumed = false;
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }
            else if (!peeked)
            {
                CallClientOriginal(self, netBuffer, methodInfo);
                return;
            }

            var action = ReceiveSlotPolicy.Decide(slotPresent, consumed, isEventsPacket: true, eventId);
            if (action == ReceiveAction.Consumed)
            {
                if (consumed != true)
                {
                    Consume(netBuffer);
                    if (!slotPresent) LogEmptyDropOnce(eventId);
                }
                return;
            }
        }
        catch (Exception e)
        {
            LogThrowOnce(ref _clientThrowLogged, "client", e);
        }
        CallClientOriginal(self, netBuffer, methodInfo);
    }

    private static void ServerHook(IntPtr self, NetBufferIn* netBuffer, IntPtr netConnectionId, IntPtr methodInfo)
    {
        try
        {
            bool frozen = Frozen;
            bool peeked = TryPeekServerEvent(netBuffer, out bool isEvents, out int eventId);
            if (frozen)
            {
                var frozenAction = ReceiveSlotPolicy.Decide(
                    slotPresent: false, slotConsumed: null, isEvents, eventId, frozen: true);
                if (frozenAction == ReceiveAction.Buffered)
                {
                    bool enqueued = EnqueueFrozen(server: true, self, netBuffer, netConnectionId, methodInfo);
                    if (ReceiveSlotPolicy.ConsumeAfterFrozenEnqueue(enqueued))
                    {
                        Consume(netBuffer);
                        return;
                    }
                    return;
                }
                CallServerOriginal(self, netBuffer, netConnectionId, methodInfo);
                return;
            }

            var slot = Volatile.Read(ref _serverSlot);
            bool slotPresent = slot != null;
            bool? consumed = null;
            if (slotPresent)
            {
                Interlocked.Increment(ref _inFlight);
                try
                {
                    consumed = slot!(self, (IntPtr)netBuffer, netConnectionId, methodInfo);
                }
                catch (Exception e)
                {
                    LogThrowOnce(ref _serverThrowLogged, "server", e);
                    consumed = false;
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }
            else if (!peeked)
            {
                CallServerOriginal(self, netBuffer, netConnectionId, methodInfo);
                return;
            }

            var action = ReceiveSlotPolicy.Decide(slotPresent, consumed, isEvents, eventId);
            if (action == ReceiveAction.Consumed)
            {
                if (consumed != true)
                {
                    Consume(netBuffer);
                    if (!slotPresent) LogEmptyDropOnce(eventId);
                }
                return;
            }
        }
        catch (Exception e)
        {
            LogThrowOnce(ref _serverThrowLogged, "server", e);
        }
        CallServerOriginal(self, netBuffer, netConnectionId, methodInfo);
    }

    private static bool TryPeekClientEventId(NetBufferIn* netBuffer, out int eventId)
    {
        eventId = 0;
        int pos = netBuffer->_ReadPosition;
        int bits = netBuffer->_BitLength;
        int totalBytes = (bits + 7) >> 3;
        if ((pos & 7) != 0) return false;
        int bytePos = pos >> 3;
        if (bytePos + sizeof(int) > totalBytes) return false;
        byte* data = netBuffer->UnsafeDataPtr;
        if (data == null) return false;
        eventId = *(int*)(data + bytePos);
        return true;
    }

    private static bool TryPeekServerEvent(NetBufferIn* netBuffer, out bool isEvents, out int eventId)
    {
        isEvents = false;
        eventId = 0;
        int pos = netBuffer->_ReadPosition;
        int bits = netBuffer->_BitLength;
        int totalBytes = (bits + 7) >> 3;
        if ((pos & 7) != 0) return false;
        int bytePos = pos >> 3;
        if (bytePos + 1 + sizeof(int) > totalBytes) return false;
        byte* data = netBuffer->UnsafeDataPtr;
        if (data == null) return false;
        if (data[bytePos] != ReceiveSlotPolicy.EventsPacketType) return true;
        isEvents = true;
        eventId = *(int*)(data + bytePos + 1);
        return true;
    }

    private static void Consume(NetBufferIn* netBuffer) => netBuffer->_ReadPosition = netBuffer->_BitLength;

    private static void CallClientOriginal(IntPtr self, NetBufferIn* netBuffer, IntPtr methodInfo)
    {
        var original = _clientOriginal;
        if (original == null) return;
        original(self, netBuffer, methodInfo);
    }

    private static void CallServerOriginal(IntPtr self, NetBufferIn* netBuffer, IntPtr netConnectionId, IntPtr methodInfo)
    {
        var original = _serverOriginal;
        if (original == null) return;
        original(self, netBuffer, netConnectionId, methodInfo);
    }

    /// <summary>
    /// One line per <b>Core generation</b>, not per process: <see cref="TrySetClient"/> /
    /// <see cref="TrySetServer"/> re-arm the latch whenever a new dispatcher binds (job FEVT1,
    /// R44 finding 4). The throw it reports is not cosmetic — the packet is consumed and dropped
    /// anyway — so a generation that starts throwing must say so even if an earlier one already
    /// did.
    /// </summary>
    private static void LogThrowOnce(ref int flag, string side, Exception e)
    {
        if (Interlocked.Exchange(ref flag, 1) != 0) return;
        ProcessHost.Log?.LogError($"ReceiveDetours: {side} slot threw; owned fourccs still drop: {e}");
    }

    private static void LogEmptyDropOnce(int eventId)
    {
        if (Interlocked.Exchange(ref _emptyDropLogged, 1) != 0) return;
        ProcessHost.Log?.LogInfo($"ReceiveDetours: slot empty; dropping progenitor event 0x{eventId:X8}.");
    }

    private static bool EnqueueFrozen(
        bool server,
        IntPtr self,
        NetBufferIn* netBuffer,
        IntPtr connectionId,
        IntPtr methodInfo)
    {
        byte[] bytes = CopyPayload(netBuffer);
        if (bytes.Length == 0) return false;
        int allocator = ReadAllocatorLabel(netBuffer);
        if (!ReceiveSlotPolicy.AllocatorIsValid(allocator))
            allocator = ReceiveSlotPolicy.AllocatorPersistent;
        IntPtr connCopy = connectionId;
        if (server && connCopy != IntPtr.Zero)
            PinConnection(connCopy);
        lock (BufferGate)
        {
            if (!ReceiveSlotPolicy.CanEnqueueFrozen(FrozenBuffer.Count))
            {
                int n = Interlocked.Increment(ref _frozenOverflowCount);
                if (Interlocked.Exchange(ref _frozenOverflowLogged, 1) == 0)
                    ProcessHost.Log?.LogWarning("ReceiveDetours: freeze buffer full; not consuming newest owned packet (count=" + n + ").");
                else
                    ProcessHost.Log?.LogWarning("ReceiveDetours: freeze buffer overflow count=" + n + ".");
                return false;
            }
            FrozenBuffer.Add(new BufferedPacket
            {
                Server = server,
                Bytes = bytes,
                BitLength = netBuffer->_BitLength,
                ReadPosition = netBuffer->_ReadPosition,
                Self = self,
                ConnectionId = connCopy,
                MethodInfo = methodInfo,
                AllocatorLabel = allocator,
            });
        }
        return true;
    }

    private static int ReadAllocatorLabel(NetBufferIn* netBuffer)
    {
        try
        {
            var header = *(ReplayNativeArray*)netBuffer;
            return header.Pad;
        }
        catch
        {
            return ReceiveSlotPolicy.AllocatorInvalid;
        }
    }

    private static void PinConnection(IntPtr connectionId)
    {
        try
        {
            var wrapper = new NetConnectionId(connectionId);
            int seq = Interlocked.Increment(ref _freezePinSeq);
            string name = NativePinRegistry.FreezeConnectionPinName(connectionId.ToInt64(), seq);
            NativePinRegistry.Alloc(wrapper, GCHandleType.Normal, name, NativePinRegistry.FreezeGeneration);
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("ReceiveDetours: freeze connection pin: " + e.Message);
        }
    }

    private static byte[] CopyPayload(NetBufferIn* netBuffer)
    {
        try
        {
            int bits = netBuffer->_BitLength;
            int totalBytes = (bits + 7) >> 3;
            if (totalBytes <= 0 || totalBytes > 65536) return Array.Empty<byte>();
            byte* data = netBuffer->UnsafeDataPtr;
            if (data == null) return Array.Empty<byte>();
            var copy = new byte[totalBytes];
            Marshal.Copy((IntPtr)data, copy, 0, totalBytes);
            return copy;
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>Drain the freeze buffer once. Returns true when at least one packet was replayed.</summary>
    private static bool ReplayBuffered()
    {
        BufferedPacket[] packets;
        lock (BufferGate)
        {
            if (FrozenBuffer.Count == 0) return false;
            packets = FrozenBuffer.ToArray();
            FrozenBuffer.Clear();
        }
        Interlocked.Exchange(ref _frozenOverflowLogged, 0);
        Interlocked.Exchange(ref _frozenOverflowCount, 0);
        for (int i = 0; i < packets.Length; i++)
        {
            try { ReplayOne(in packets[i]); }
            catch (Exception e)
            {
                ProcessHost.Log?.LogWarning($"ReceiveDetours: freeze replay threw: {e.Message}");
            }
        }
        return packets.Length > 0;
    }

    private static void ReplayOne(in BufferedPacket packet)
    {
        var bytes = packet.Bytes;
        if (bytes == null || bytes.Length == 0) return;
        if (packet.Server && !ConnectionStillExists(packet.ConnectionId))
        {
            ProcessHost.Log?.LogWarning("ReceiveDetours: skip freeze replay; connection no longer exists.");
            return;
        }
        int allocator = packet.AllocatorLabel;
        if (!ReceiveSlotPolicy.AllocatorIsValid(allocator))
            allocator = ReceiveSlotPolicy.AllocatorPersistent;
        fixed (byte* p = bytes)
        {
            var overlay = new ReplayNativeArray { Ptr = p, Length = bytes.Length, Pad = allocator };
            NetBufferIn nb = default;
            *(ReplayNativeArray*)&nb = overlay;
            nb._BitLength = packet.BitLength;
            nb._ReadPosition = packet.ReadPosition;
            if (packet.Server)
            {
                var slot = Volatile.Read(ref _serverSlot);
                if (slot == null) return;
                Interlocked.Increment(ref _inFlight);
                try { slot(packet.Self, (IntPtr)(&nb), packet.ConnectionId, packet.MethodInfo); }
                finally { Interlocked.Decrement(ref _inFlight); }
            }
            else
            {
                var slot = Volatile.Read(ref _clientSlot);
                if (slot == null) return;
                Interlocked.Increment(ref _inFlight);
                try { slot(packet.Self, (IntPtr)(&nb), packet.MethodInfo); }
                finally { Interlocked.Decrement(ref _inFlight); }
            }
        }
    }

    private static bool ConnectionStillExists(IntPtr connectionId)
    {
        if (connectionId == IntPtr.Zero) return false;
        try
        {
            var wrap = new NetConnectionId(connectionId);
            return wrap.Pointer != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReplayNativeArray
    {
        public byte* Ptr;
        public int Length;
        public int Pad;
    }

    private struct BufferedPacket
    {
        public bool Server;
        public byte[] Bytes;
        public int BitLength;
        public int ReadPosition;
        public IntPtr Self;
        /// <summary>
        /// Copied Il2Cpp object pointer, not a managed <c>NetConnectionId</c> that GC
        /// pulses during collect can collect. Validated on replay.
        /// </summary>
        public IntPtr ConnectionId;
        public IntPtr MethodInfo;
        public int AllocatorLabel;
    }
}

/// <summary>
/// Public reflection surface for receive-detour resolve outcomes. Bootstrap's receive
/// trampoline type is internal; collectible Core merges these pipe lines into
/// <c>AddressLibrary.DescribeForPing</c> via <c>Assembly.GetType</c>, which only returns
/// public types from another assembly. Format: <c>site|id|path|offsetHex|addressHex</c>.
/// </summary>
public static class ReceiveDetourPing
{
    /// <summary>Last resolve per site. Empty until the process-lifetime receive JMP is applied.</summary>
    public static string[] NativePingLines => ReceiveDetours.NativePingLines;
}
