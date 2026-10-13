using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Content;

/// <summary>
/// One serialized component of a server edit pass: the component's full type name and the exact
/// bytes the reader closure copied off the prefab entity. Game-free so the packet builder, the
/// dedupe hash and the unit tests all handle the same shape.
/// </summary>
internal readonly struct PrefabEditComponentBytes
{
    public PrefabEditComponentBytes(string typeFullName, byte[] bytes)
    {
        TypeFullName = typeFullName ?? "";
        Bytes = bytes ?? Array.Empty<byte>();
    }

    /// <summary>Assembly-qualified-free full name, e.g. <c>ProjectM.ItemData</c>.</summary>
    public string TypeFullName { get; }

    /// <summary>Component bytes exactly as the receiver will write them.</summary>
    public byte[] Bytes { get; }

    /// <summary>Short type name (after the last dot), the key the baselines store uses.</summary>
    public string ShortName => PrefabEditWire.ShortTypeName(TypeFullName);
}

/// <summary>
/// The <c>PrfE</c> wire format (job FEDW1, design <c>docs/edits-on-the-wire.md</c>): the server is
/// authoritative for edited component values and ships the results, so a client without the
/// editing plugin — or with a different captured input — sees the server's numbers.
///
/// <para><b>Layout</b> (little-endian, no padding):</para>
/// <code>
/// int32  guidHash
/// int32  buildHash        // FNV-1a of PrefabEditBuildIdentity.Compose(...); 0 when unknown
/// int32  editIdentityHash // FNV-1a of PrefabEditTable.Stamp(guid)
/// uint8  count            // 0 = withdraw the layer; 1..MaxEditComponents = the layer
/// count x
///   uint8  typeNameLength // 1..MaxTypeNameBytes
///   bytes  typeFullName   // ASCII
///   uint16 payloadLength  // 1..MaxComponentBytes
///   bytes  payload
/// </code>
///
/// <para>Nothing here is measured at runtime and multiplied: the receiver checks the declared
/// length against its own <c>Marshal.SizeOf(type)</c> and refuses the record on any disagreement
/// (native array strides rule, CLAUDE.md).</para>
/// </summary>
internal static class PrefabEditWire
{
    /// <summary>'PrfE' — next free id after <see cref="PrefabDefinitionPacket.EventId"/> ('PrfD').</summary>
    public const int EventId = 0x50726645;

    /// <summary>Most components one prefab may ship in a single packet.</summary>
    public const int MaxEditComponents = 16;

    /// <summary>Longest ASCII type full name on the wire (the length byte is 1..127).</summary>
    public const int MaxTypeNameBytes = 127;

    /// <summary>Largest single component payload. Comfortably above every edited game component.</summary>
    public const int MaxComponentBytes = 1024;

    /// <summary>
    /// Largest whole packet. Matches <c>CustomEvents.MaxPayloadBytes</c> (15 KiB): the caps above
    /// multiply out past it, so the builder trims and reports rather than throwing at the send.
    /// </summary>
    public const int MaxPacketBytes = 15 * 1024;

    /// <summary>Bytes before the first record: guid, build hash, identity hash, count.</summary>
    public const int HeaderBytes = (3 * sizeof(int)) + 1;

    /// <summary>Fixed bytes one record costs on top of its name and payload.</summary>
    public const int RecordOverheadBytes = 1 + 2;

    /// <summary>
    /// Longest type name a log line prints. A hostile server chooses these names, so the log
    /// gets a bounded, printable version of them (job FEDW3, R51 finding 2).
    /// </summary>
    public const int MaxLoggedTypeNameChars = 64;

    // ---- hashing ------------------------------------------------------------------------

    /// <summary>
    /// FNV-1a over the UTF-8 bytes of <paramref name="text"/>, as an int. Null and empty both
    /// hash to 0, which is the "unknown build" value: a packet that says 0 matches only a
    /// receiver that also knows nothing, never a receiver with a real build id.
    /// </summary>
    public static int Fnv1a(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        unchecked
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;
            uint hash = offset;
            var bytes = Encoding.UTF8.GetBytes(text!);
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= prime;
            }
            return (int)hash;
        }
    }

    /// <summary>
    /// Dedupe key for one prefab's serialized layer: order-sensitive FNV-1a over every type name
    /// and every payload byte. A re-run that writes the same values produces the same hash and
    /// sends nothing.
    /// </summary>
    public static int PayloadHash(IReadOnlyList<PrefabEditComponentBytes>? records)
    {
        unchecked
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;
            uint hash = offset;
            int n = records?.Count ?? 0;
            hash ^= (uint)n;
            hash *= prime;
            for (int i = 0; i < n; i++)
            {
                var rec = records![i];
                var name = Encoding.UTF8.GetBytes(rec.TypeFullName ?? "");
                for (int b = 0; b < name.Length; b++)
                {
                    hash ^= name[b];
                    hash *= prime;
                }
                hash ^= 0xFF;
                hash *= prime;
                var bytes = rec.Bytes ?? Array.Empty<byte>();
                for (int b = 0; b < bytes.Length; b++)
                {
                    hash ^= bytes[b];
                    hash *= prime;
                }
            }
            return (int)hash;
        }
    }

    /// <summary>Everything after the last dot; the whole name when there is none.</summary>
    public static string ShortTypeName(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return "";
        int dot = fullName!.LastIndexOf('.');
        return dot < 0 || dot == fullName.Length - 1 ? fullName : fullName.Substring(dot + 1);
    }

    // ---- caps ---------------------------------------------------------------------------

    /// <summary>
    /// Apply every cap to a candidate record set, in order, and report each refusal. Pure so the
    /// suite locks the same rule the send path runs: a record that is refused here never reaches
    /// the wire, and the records that survive always fit one packet.
    /// </summary>
    public static List<PrefabEditComponentBytes> SelectRecords(
        IReadOnlyList<PrefabEditComponentBytes>? candidates,
        out List<string> refusals)
    {
        refusals = new List<string>();
        var accepted = new List<PrefabEditComponentBytes>();
        if (candidates == null || candidates.Count == 0) return accepted;

        int size = HeaderBytes;
        for (int i = 0; i < candidates.Count; i++)
        {
            var rec = candidates[i];
            if (accepted.Count >= MaxEditComponents)
            {
                refusals.Add(RefusalCountCap(rec.TypeFullName));
                continue;
            }
            if (!IsValidTypeName(rec.TypeFullName, out int nameBytes, out string nameError))
            {
                refusals.Add(RefusalRecord(rec.TypeFullName, nameError));
                continue;
            }
            int payload = rec.Bytes?.Length ?? 0;
            if (payload <= 0)
            {
                refusals.Add(RefusalRecord(rec.TypeFullName, "payload is empty"));
                continue;
            }
            if (payload > MaxComponentBytes)
            {
                refusals.Add(RefusalRecord(
                    rec.TypeFullName,
                    "payload of " + payload.ToString(CultureInfo.InvariantCulture)
                    + " bytes exceeds " + MaxComponentBytes.ToString(CultureInfo.InvariantCulture)));
                continue;
            }
            int cost = RecordOverheadBytes + nameBytes + payload;
            if (size + cost > MaxPacketBytes)
            {
                refusals.Add(RefusalPacketCap(rec.TypeFullName));
                continue;
            }
            size += cost;
            accepted.Add(rec);
        }
        return accepted;
    }

    /// <summary>1..127 ASCII bytes. <paramref name="byteCount"/> is meaningful only when true.</summary>
    public static bool IsValidTypeName(string? name, out int byteCount, out string error)
    {
        byteCount = 0;
        if (string.IsNullOrEmpty(name))
        {
            error = "type name is empty";
            return false;
        }
        for (int i = 0; i < name!.Length; i++)
        {
            if (name[i] > 0x7F)
            {
                error = "type name is not ASCII";
                return false;
            }
        }
        byteCount = Encoding.ASCII.GetByteCount(name);
        if (byteCount > MaxTypeNameBytes)
        {
            error = "type name exceeds " + MaxTypeNameBytes.ToString(CultureInfo.InvariantCulture)
                + " ASCII bytes";
            byteCount = 0;
            return false;
        }
        error = "";
        return true;
    }

    // ---- build ---------------------------------------------------------------------------

    /// <summary>
    /// Encode one prefab's layer. <paramref name="records"/> must already have been through
    /// <see cref="SelectRecords"/>; anything that would break a cap fails the whole build rather
    /// than truncating silently.
    /// </summary>
    public static bool TryWrite(
        int guidHash,
        int buildHash,
        int editIdentityHash,
        IReadOnlyList<PrefabEditComponentBytes>? records,
        out byte[] packet,
        out string error)
    {
        packet = Array.Empty<byte>();
        int count = records?.Count ?? 0;
        if (count == 0)
        {
            // count 0 on the wire means "withdraw"; it is spent deliberately by
            // TryWriteWithdraw, never produced by an empty layer that meant to say something.
            error = "no components to send";
            return false;
        }
        if (count > MaxEditComponents)
        {
            error = "count " + count.ToString(CultureInfo.InvariantCulture) + " exceeds "
                + MaxEditComponents.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        int size = HeaderBytes;
        for (int i = 0; i < count; i++)
        {
            var rec = records![i];
            if (!IsValidTypeName(rec.TypeFullName, out int nameBytes, out error)) return false;
            int payload = rec.Bytes?.Length ?? 0;
            if (payload <= 0)
            {
                error = "payload for " + rec.TypeFullName + " is empty";
                return false;
            }
            if (payload > MaxComponentBytes)
            {
                error = "payload for " + rec.TypeFullName + " is "
                    + payload.ToString(CultureInfo.InvariantCulture) + " bytes; max is "
                    + MaxComponentBytes.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            size += RecordOverheadBytes + nameBytes + payload;
        }
        if (size > MaxPacketBytes)
        {
            error = "packet of " + size.ToString(CultureInfo.InvariantCulture) + " bytes exceeds "
                + MaxPacketBytes.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        var buffer = new byte[size];
        int o = 0;
        WriteInt32(buffer, ref o, guidHash);
        WriteInt32(buffer, ref o, buildHash);
        WriteInt32(buffer, ref o, editIdentityHash);
        buffer[o++] = (byte)count;
        for (int i = 0; i < count; i++)
        {
            var rec = records![i];
            var name = Encoding.ASCII.GetBytes(rec.TypeFullName);
            buffer[o++] = (byte)name.Length;
            Buffer.BlockCopy(name, 0, buffer, o, name.Length);
            o += name.Length;
            var bytes = rec.Bytes!;
            buffer[o++] = (byte)bytes.Length;
            buffer[o++] = (byte)(bytes.Length >> 8);
            Buffer.BlockCopy(bytes, 0, buffer, o, bytes.Length);
            o += bytes.Length;
        }

        packet = buffer;
        error = "";
        return true;
    }

    /// <summary>
    /// The withdraw packet (job FEDW3, R51 finding 7): a header with <c>count == 0</c>, thirteen
    /// bytes, meaning <em>drop the layer for this guid and put its baseline back</em>. Before this
    /// encoding was spent a server that had pushed a layer onto a <em>vanilla</em> prefab could
    /// correct the value but never take it back: <c>PrfU</c> only reaches prefabs with a registry
    /// entry, and the client carried the values until it changed worlds.
    /// </summary>
    public static byte[] WriteWithdraw(int guidHash, int buildHash, int editIdentityHash)
    {
        var buffer = new byte[HeaderBytes];
        int o = 0;
        WriteInt32(buffer, ref o, guidHash);
        WriteInt32(buffer, ref o, buildHash);
        WriteInt32(buffer, ref o, editIdentityHash);
        buffer[o] = 0;
        return buffer;
    }

    /// <summary>
    /// Decode a packet written by <see cref="TryWrite"/>. Every length is checked against the
    /// caps <em>and</em> against what is left of the buffer, so a hostile or corrupt packet
    /// cannot walk past the end. A packet with trailing bytes is refused.
    /// </summary>
    public static bool TryRead(
        ReadOnlySpan<byte> src,
        out int guidHash,
        out int buildHash,
        out int editIdentityHash,
        out List<PrefabEditComponentBytes> records,
        out string error)
    {
        guidHash = 0;
        buildHash = 0;
        editIdentityHash = 0;
        records = new List<PrefabEditComponentBytes>();
        if (src.Length < HeaderBytes)
        {
            error = "packet of " + src.Length.ToString(CultureInfo.InvariantCulture)
                + " bytes is shorter than the " + HeaderBytes.ToString(CultureInfo.InvariantCulture)
                + "-byte header";
            return false;
        }
        if (src.Length > MaxPacketBytes)
        {
            error = "packet of " + src.Length.ToString(CultureInfo.InvariantCulture)
                + " bytes exceeds " + MaxPacketBytes.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        int o = 0;
        guidHash = ReadInt32(src, ref o);
        buildHash = ReadInt32(src, ref o);
        editIdentityHash = ReadInt32(src, ref o);
        int count = src[o++];
        if (count == 0)
        {
            // Withdraw. Nothing may follow it: a header plus payload is not a shape this
            // protocol has, so trailing bytes refuse the packet rather than being ignored.
            if (o != src.Length)
            {
                error = (src.Length - o).ToString(CultureInfo.InvariantCulture)
                    + " trailing byte(s) after a withdraw";
                return false;
            }
            error = "";
            return true;
        }
        if (count > MaxEditComponents)
        {
            error = "count " + count.ToString(CultureInfo.InvariantCulture) + " exceeds "
                + MaxEditComponents.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (o >= src.Length)
            {
                error = "truncated before record " + (i + 1).ToString(CultureInfo.InvariantCulture);
                return false;
            }
            int nameLen = src[o++];
            if (nameLen == 0)
            {
                error = "record " + (i + 1).ToString(CultureInfo.InvariantCulture) + " has an empty type name";
                return false;
            }
            if (nameLen > MaxTypeNameBytes || o + nameLen + 2 > src.Length)
            {
                error = "record " + (i + 1).ToString(CultureInfo.InvariantCulture)
                    + " type name length " + nameLen.ToString(CultureInfo.InvariantCulture) + " does not fit";
                return false;
            }
            if (!TryAsciiString(src.Slice(o, nameLen), out string name))
            {
                error = "record " + (i + 1).ToString(CultureInfo.InvariantCulture)
                    + " type name has a byte outside printable ASCII";
                records.Clear();
                return false;
            }
            o += nameLen;
            int payload = src[o] | (src[o + 1] << 8);
            o += 2;
            if (payload <= 0)
            {
                error = "record '" + SanitiseTypeName(name, out _) + "' has an empty payload";
                return false;
            }
            if (payload > MaxComponentBytes || o + payload > src.Length)
            {
                error = "record '" + SanitiseTypeName(name, out _) + "' payload length "
                    + payload.ToString(CultureInfo.InvariantCulture) + " does not fit";
                return false;
            }
            var bytes = src.Slice(o, payload).ToArray();
            o += payload;
            records.Add(new PrefabEditComponentBytes(name, bytes));
        }

        if (o != src.Length)
        {
            error = (src.Length - o).ToString(CultureInfo.InvariantCulture) + " trailing byte(s)";
            records.Clear();
            return false;
        }

        error = "";
        return true;
    }

    // ---- receiver-side type rules ---------------------------------------------------------

    /// <summary>
    /// Assemblies a <c>PrfE</c> type name may be resolved from. The client never loads an
    /// assembly to satisfy a packet: it walks what is already loaded, and only these three
    /// families. A name that resolves nowhere is refused, not searched for.
    /// </summary>
    public static readonly string[] AllowedAssemblyPrefixes = { "ProjectM", "Stunlock", "Unity" };

    /// <summary>True when an already-loaded assembly with this simple name may define a wire type.</summary>
    public static bool IsAllowedAssemblyName(string? assemblySimpleName)
    {
        if (string.IsNullOrEmpty(assemblySimpleName)) return false;
        for (int i = 0; i < AllowedAssemblyPrefixes.Length; i++)
        {
            if (assemblySimpleName!.StartsWith(AllowedAssemblyPrefixes[i], StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Name-only refusals, before any reflection: an identity component
    /// (<see cref="PrefabEditIdentity"/>, plus <c>PrefabGUID</c> and the <c>Prefab</c> tag) can
    /// never be written from a packet, whatever assembly claims it.
    /// </summary>
    public static bool IsRefusedTypeName(string? typeFullName, out string reason)
    {
        string shortName = ShortTypeName(typeFullName);
        if (PrefabEditIdentity.IsIdentityType(shortName))
        {
            reason = "identity component";
            return true;
        }
        reason = "";
        return false;
    }

    /// <summary>
    /// The build rule. A packet is applied only when the sender's identity hash equals the
    /// receiver's. 0 means "no game identity detected", so 0 matches only 0: an unknown build
    /// never silently accepts bytes laid out for a known one. What the hash is taken over is
    /// <see cref="PrefabEditBuildIdentity"/>, which is deliberately <em>not</em> a per-binary
    /// fingerprint (R50 finding 10).
    /// </summary>
    public static bool BuildMatches(int packetBuildHash, int localBuildHash) =>
        packetBuildHash == localBuildHash;

    /// <summary>
    /// Packet-level origin gate. <c>PrfE</c> is server-to-client only: a server that is handed
    /// a received layer (a client-originated packet, or a handler registered on the wrong side)
    /// refuses it before any store or entity write. The live receive path is client-only; this
    /// is the same decision that path runs so a mis-wired handler cannot apply bytes.
    /// </summary>
    public static bool TryAdmitReceivedPacket(bool receiverIsServer, out string reason)
    {
        if (receiverIsServer)
        {
            reason = ReceivedOnServerReason;
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>Operator-facing reason <see cref="TryAdmitReceivedPacket"/> returns on a server.</summary>
    public const string ReceivedOnServerReason =
        "a server does not apply a received edit layer (client-originated)";

    /// <summary>
    /// Record-level allowlist gate, before any entity write: a type Core does not vouch for,
    /// or an identity component, is refused. The live apply ladder asks the same questions
    /// through <c>TryResolveType</c>; this form is game-free so the suite can refuse without
    /// a world.
    /// </summary>
    public static bool TryAdmitReceivedRecord(string? typeFullName, out string reason)
    {
        if (IsRefusedTypeName(typeFullName, out reason)) return false;
        if (!PrefabEditWireTypes.IsVouched(typeFullName))
        {
            reason = PrefabEditWireTypes.NotVouchedReason(typeFullName);
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// Whether <paramref name="type"/> may be memcpy'd into an ECS chunk. "Value type" is
    /// strictly weaker than <c>unmanaged</c>, and <see cref="System.Reflection.MethodInfo.MakeGenericMethod"/>
    /// does <b>not</b> enforce the <c>unmanaged</c> constraint on
    /// <c>EntityExtensions.Write&lt;T&gt;</c>: the constraint is a modreq on the type parameter
    /// that reflection cannot see, so a struct carrying a managed field closes the method fine
    /// and the body's <c>sizeof(T)</c> then reports the pointer-inclusive size (R50 finding 10,
    /// measured; <c>MakeGenericMethodEnforcesOnlyValueType</c> in the suite measures it again in
    /// this runtime). So the wire path asks the question the constraint was supposed to answer.
    /// </summary>
    public static bool ContainsManagedReferences(Type? type, out string reason)
    {
        reason = "";
        if (type == null)
        {
            reason = "no type";
            return true;
        }
        try
        {
            var probe = typeof(System.Runtime.CompilerServices.RuntimeHelpers)
                .GetMethod(
                    nameof(System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences),
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (probe == null)
            {
                reason = "IsReferenceOrContainsReferences is unavailable, so a managed field cannot be ruled out";
                return true;
            }
            if (probe.MakeGenericMethod(type).Invoke(null, null) is bool contains && contains)
            {
                reason = "carries a managed reference (Write<T> takes unmanaged only)";
                return true;
            }
            return false;
        }
        catch (Exception e)
        {
            reason = "managed-reference probe threw: " + e.Message;
            return true;
        }
    }

    /// <summary>Size rule: the declared length must equal the receiver's own marshaled size.</summary>
    public static bool SizeMatches(int packetLength, int marshalSize) =>
        marshalSize > 0 && packetLength == marshalSize;

    /// <summary>
    /// The one refusal text for a length disagreement, shared by the sender's pre-check and the
    /// receiver's check so the two cannot describe the same fact differently. Since FEDW2 both
    /// ends take the same measure, so this can only be a hand-built packet or a real layout
    /// surprise — never the normal path (R51 finding 4).
    /// </summary>
    public static string SizeMismatchReason(int packetLength, int marshalSize, string? typeName) =>
        "size mismatch: packet has " + packetLength.ToString(CultureInfo.InvariantCulture)
        + " byte(s), Marshal.SizeOf(" + SanitiseTypeName(typeName, out _) + ") is "
        + marshalSize.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Everything about a resolved type that can be decided without its layout: it must be a
    /// non-generic value type that is not an identity component and carries no managed reference.
    /// The shape walk (<see cref="PrefabEditShape"/>) is the step after this one and needs the
    /// layout; this one only needs the type.
    ///
    /// <para>Game-free and driven by the suite against real fixture types, which is what R51
    /// finding 6 asked for: before FEDW3 every one of these decisions was reachable only from a
    /// file the test project does not compile.</para>
    /// </summary>
    public static bool TryAcceptType(Type? type, out string reason)
    {
        if (type == null)
        {
            reason = "no loaded ProjectM/Stunlock/Unity assembly defines it";
            return false;
        }
        if (IsRefusedTypeName(type.FullName ?? type.Name, out reason)) return false;
        if (!type.IsValueType)
        {
            reason = "not a value type";
            return false;
        }
        if (type.IsGenericTypeDefinition || type.ContainsGenericParameters)
        {
            reason = "generic type";
            return false;
        }
        if (ContainsManagedReferences(type, out string refReason))
        {
            reason = refReason;
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>
    /// The one measure on this wire: <b>the marshaled layout</b>. Every reader produces bytes
    /// with <c>Marshal.StructureToPtr</c> over <c>Marshal.SizeOf(type)</c> and every receiver
    /// checks the declared length against its own <c>Marshal.SizeOf(type)</c> and rebuilds the
    /// value with <c>Marshal.PtrToStructure</c>.
    ///
    /// <para><b>Why not <c>sizeof</c>.</b> FEDW1's typed reader produced
    /// <c>MemoryMarshal.AsBytes</c> of the managed layout while the receiver validated and parsed
    /// the marshaled one. The two disagree for exactly the structs this game is full of — default
    /// marshaling widens <c>bool</c> to four bytes, and CLAUDE.md's EV17 rule records
    /// <c>sizeof</c> reading an unpadded extent (73 for a 76-byte <c>SequenceInputElement</c>) —
    /// so where they differed every record was refused as a size mismatch, indistinguishable from
    /// the documented negative control, and where they happened to agree on size but not on field
    /// offsets the receiver would have parsed the bytes wrong (R50 finding 10). One measure, taken
    /// from the type on each side; nothing is measured at runtime and multiplied.</para>
    /// </summary>
    public static bool TryMarshalBytes(object? value, Type? type, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        if (value == null || type == null)
        {
            error = "no value";
            return false;
        }
        int size;
        try { size = System.Runtime.InteropServices.Marshal.SizeOf(type); }
        catch (Exception e)
        {
            error = "Marshal.SizeOf(" + (type.FullName ?? type.Name) + ") threw: " + e.Message;
            return false;
        }
        if (size <= 0)
        {
            error = "Marshal.SizeOf(" + (type.FullName ?? type.Name) + ") is " + size;
            return false;
        }

        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            var copy = new byte[size];
            System.Runtime.InteropServices.Marshal.StructureToPtr(value, buffer, false);
            System.Runtime.InteropServices.Marshal.Copy(buffer, copy, 0, size);
            bytes = copy;
            error = "";
            return true;
        }
        catch (Exception e)
        {
            error = "StructureToPtr(" + (type.FullName ?? type.Name) + ") threw: " + e.Message;
            return false;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    // ---- log fragments --------------------------------------------------------------------

    private static string RefusalRecord(string? typeName, string reason) =>
        "component " + (string.IsNullOrEmpty(typeName) ? "(unnamed)" : typeName) + ": " + reason;

    private static string RefusalCountCap(string? typeName) =>
        RefusalRecord(typeName, "more than " + MaxEditComponents.ToString(CultureInfo.InvariantCulture)
            + " component(s) touched; not sent");

    private static string RefusalPacketCap(string? typeName) =>
        RefusalRecord(typeName, "packet would exceed "
            + MaxPacketBytes.ToString(CultureInfo.InvariantCulture) + " bytes; not sent");

    private static void WriteInt32(byte[] dest, ref int offset, int value)
    {
        dest[offset] = (byte)value;
        dest[offset + 1] = (byte)(value >> 8);
        dest[offset + 2] = (byte)(value >> 16);
        dest[offset + 3] = (byte)(value >> 24);
        offset += 4;
    }

    private static int ReadInt32(ReadOnlySpan<byte> src, ref int offset)
    {
        int v = src[offset]
            | (src[offset + 1] << 8)
            | (src[offset + 2] << 16)
            | (src[offset + 3] << 24);
        offset += 4;
        return v;
    }

    /// <summary>
    /// Read a wire type name. Every byte must be printable ASCII (0x20..0x7E). The old version
    /// masked with <c>&amp; 0x7F</c> and kept the result, so <c>TryRead</c> was not the inverse of
    /// <c>TryWrite</c> and a name could carry <c>\r</c>, <c>\n</c> or <c>\x1b</c> into sixteen log
    /// sites per packet (job FEDW3, R51 finding 2). The send side has always promised ASCII, so
    /// refusing here rejects only packets no honest sender produces.
    /// </summary>
    private static bool TryAsciiString(ReadOnlySpan<byte> bytes, out string text)
    {
        text = "";
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (b < 0x20 || b > 0x7E) return false;
            chars[i] = (char)b;
        }
        text = new string(chars);
        return true;
    }

    /// <summary>
    /// The version of a type name that may reach a log line. Non-printable characters become
    /// <c>?</c> and the name is cut to <see cref="MaxLoggedTypeNameChars"/>;
    /// <paramref name="altered"/> tells the caller to say so on the line, so an operator never
    /// reads a sanitized name as the literal one.
    ///
    /// <para>The parser already refuses a non-printable name, so this is the second gate rather
    /// than the only one: log formatting must not depend on every future caller having parsed its
    /// input first.</para>
    /// </summary>
    public static string SanitiseTypeName(string? name, out bool altered)
    {
        altered = false;
        if (string.IsNullOrEmpty(name)) return "";
        var sb = new StringBuilder(Math.Min(name!.Length, MaxLoggedTypeNameChars));
        int n = name.Length;
        if (n > MaxLoggedTypeNameChars)
        {
            n = MaxLoggedTypeNameChars;
            altered = true;
        }
        for (int i = 0; i < n; i++)
        {
            char c = name[i];
            if (c < 0x20 || c > 0x7E)
            {
                sb.Append('?');
                altered = true;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>
/// The game facts the <c>PrfE</c> identity is built from, as the process can read them:
/// <c>GameVersionUtils.GetVersionData()</c> plus <c>GameVersionUtils.GetNetworkVersion()</c>.
/// A plain carrier so <see cref="PrefabEditBuildIdentity"/> is game-free and the suite can put
/// a client's and a server's numbers side by side.
/// </summary>
internal readonly struct PrefabGameVersionFacts
{
    public PrefabGameVersionFacts(
        string? name,
        int major,
        int minor,
        int patch,
        int revision,
        int vcsRevision,
        int build,
        long unixTimestamp,
        int networkVersion)
    {
        Name = name ?? "";
        Major = major;
        Minor = minor;
        Patch = patch;
        Revision = revision;
        VcsRevision = vcsRevision;
        Build = build;
        UnixTimestamp = unixTimestamp;
        NetworkVersion = networkVersion;
    }

    /// <summary>"VRising" on the client, "VRisingServer" on the server. Deliberately unused.</summary>
    public string Name { get; }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public int Revision { get; }
    public int VcsRevision { get; }
    public int Build { get; }

    /// <summary>The build machine's clock, minutes apart between the two binaries. Unused.</summary>
    public long UnixTimestamp { get; }

    /// <summary>
    /// <c>GameVersionUtils.GetNetworkVersion()</c>: the number the server compares the client's
    /// hail against in <c>OnConnectionApproval</c> before anything else happens.
    /// </summary>
    public int NetworkVersion { get; }
}

/// <summary>
/// What <c>PrfE</c> means by "the same game" (job FEDW2, R50 finding 10).
///
/// <para><b>Why not the address library.</b> The first design and the first implementation hashed
/// <c>AddressLibrary.DetectedBuild</c>, which is a fingerprint of the local <c>GameAssembly.dll</c>
/// (<c>{fileSize}-{timeDateStamp}-{checkSum}-{sha}</c>) and is explicitly side-scoped — the tables
/// are keyed by <c>(side, build)</c>. The client's and the server's <c>GameAssembly.dll</c> are
/// different files, so the equality gate could never hold across a real join: it would have dropped
/// 100 % of the packets the design exists to send. The live server's own id,
/// <c>248598344-6a148fd0-ed22e34-3dcd792996235366</c> (evidence 189), is not a number the client
/// can ever produce.</para>
///
/// <para><b>What both sides genuinely share.</b> The version constants compiled into each binary.
/// From the client and server metadata of this build the two agree on
/// <c>Major=1, Minor=1, Patch=13, Revision=0,
/// VcsRevision=99712, Build=17</c> and disagree on exactly three fields: <c>Name</c> ("VRising" vs
/// "VRisingServer"), <c>UnixTimestamp</c> and <c>Date</c> (the two binaries were built ~1 h apart).
/// So the identity uses the six that agree and drops the three that cannot. On top of them it puts
/// <c>GameVersionUtils.GetNetworkVersion()</c>, which is the game's own compatibility number: the
/// server compares the client's hail against its local value in <c>OnConnectionApproval</c> and
/// disconnects with reason <c>0xd</c> on a mismatch (research <c>server-auth-path.md</c>), so two
/// processes that are talking at all necessarily agree on it.</para>
///
/// <para><b>What a mismatch means.</b> The two processes are running different game builds — which
/// is normally impossible, because the same check that gates the connection gates the identity — or
/// one of them could not read its version data at all. Either way the component layouts behind the
/// bytes are not known to match, so the layer is dropped rather than written. It is not a Progenitor
/// version check: the handshake already kicks on that.</para>
/// </summary>
internal static class PrefabEditBuildIdentity
{
    /// <summary>The identity of a process that could not read its own version. Hashes to 0.</summary>
    public const string Unknown = "";

    /// <summary>
    /// The shared identity string, or <see cref="Unknown"/> when the facts are not usable. Pure:
    /// the same facts always produce the same string, and no side-specific field reaches it.
    /// </summary>
    public static string Compose(PrefabGameVersionFacts facts)
    {
        if (facts.Major < 0 || facts.Minor < 0 || facts.Patch < 0 || facts.Revision < 0
            || facts.VcsRevision < 0 || facts.Build < 0)
            return Unknown;
        if (facts.Major == 0 && facts.Minor == 0 && facts.Patch == 0 && facts.Revision == 0)
            return Unknown;

        var sb = new StringBuilder(48);
        sb.Append("vrising/");
        sb.Append(facts.Major.ToString(CultureInfo.InvariantCulture)).Append('.');
        sb.Append(facts.Minor.ToString(CultureInfo.InvariantCulture)).Append('.');
        sb.Append(facts.Patch.ToString(CultureInfo.InvariantCulture)).Append('.');
        sb.Append(facts.Revision.ToString(CultureInfo.InvariantCulture));
        sb.Append("+r").Append(facts.VcsRevision.ToString(CultureInfo.InvariantCulture));
        sb.Append("+b").Append(facts.Build.ToString(CultureInfo.InvariantCulture));
        sb.Append("/net").Append(facts.NetworkVersion.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>FNV-1a of <see cref="Compose"/>, which is what travels in the packet header.</summary>
    public static int Hash(PrefabGameVersionFacts facts) => PrefabEditWire.Fnv1a(Compose(facts));
}
