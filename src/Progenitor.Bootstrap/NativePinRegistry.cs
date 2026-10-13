using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Progenitor.Bootstrap;

/// <summary>
/// Process-lifetime table of GCHandles and native function pointers, keyed by
/// (name, sequence) inside a Core generation (ALC name). Collectible Core must never
/// <c>GCHandle.Alloc</c> or <c>Marshal.GetFunctionPointerForDelegate</c> itself: those
/// handles pin the ALC until process exit. Bootstrap owns the handle;
/// <see cref="ReleaseGeneration"/> frees only the generation being unloaded. A failed
/// collect must not <see cref="ReleaseAll"/>. A same-name Alloc / GetFunctionPointer /
/// KeepDelegate never frees the previous handle: a native trampoline or freeze-buffer
/// wrapper can still dereference it (R14 finding 3). The previous pin stays until its
/// generation is released or an explicit <see cref="DropByName"/>. Pins recorded with
/// an empty generation (Keep before the ALC name was published) are treated as belonging
/// to the Core generation being released — that was the client <c>dropped=0</c> miss
/// (evidence 53). A pin whose owner is the Core ALC name is in that generation even when
/// Keep stored a different key (stale <c>CurrentPinGeneration</c>); Bootstrap-owned slot
/// bodies (<see cref="BootstrapGeneration"/> / assembly <c>Progenitor.Bootstrap</c>) are
/// process-lifetime and never drop with a Core generation. Uninstall must not
/// <c>DropByName</c> Core pins: that hid <c>lifetime.frame</c> and
/// <c>lifetime.quitting</c> from pin-release while pin-diag still listed them (evidence 57).
/// </summary>
public static class NativePinRegistry
{
    public const string FreezeGeneration = "receive-freeze";
    public const string BootstrapGeneration = "bootstrap";

    /// <summary>Default-ALC assembly name of this plugin; OwnerOf returns it for Bootstrap methods.</summary>
    public const string BootstrapOwner = "Progenitor.Bootstrap";

    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();
    private static int NextSequence;
    private static string? LastReplace;

    private struct Entry
    {
        public string Kind;
        public string Name;
        public string Owner;
        public string Generation;
        public int Sequence;
        public GCHandle Handle;
        public bool HasHandle;
        public Delegate? KeepAlive;
    }

    /// <summary>
    /// Allocate a GCHandle that Bootstrap will free on <see cref="ReleaseGeneration"/> of
    /// <paramref name="generation"/> (or an explicit <see cref="DropByName"/>). A second
    /// Alloc of the same <paramref name="name"/> does not free this handle.
    /// </summary>
    public static GCHandle Alloc(object target, GCHandleType type, string name, string? generation = null)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        var handle = GCHandle.Alloc(target, type);
        string owner = OwnerOf(target);
        string gen = ResolveGeneration(generation, owner);
        string? replaceLine;
        lock (Gate)
        {
            replaceLine = AddUnlocked(new Entry
            {
                Kind = "GCHandle." + type,
                Name = name ?? "",
                Owner = owner,
                Generation = gen,
                Handle = handle,
                HasHandle = true,
            });
        }
        EmitReplace(replaceLine);
        return handle;
    }

    /// <summary>
    /// Keep <paramref name="d"/> alive and return <c>Marshal.GetFunctionPointerForDelegate</c>.
    /// The pointer is only valid until <see cref="ReleaseGeneration"/> of <paramref name="generation"/>
    /// (or an explicit <see cref="DropByName"/>). A same-name re-bind does not invalidate an
    /// earlier pointer: whatever stored the <c>IntPtr</c> (a JMP, a native field) may still
    /// call it.
    /// </summary>
    public static IntPtr GetFunctionPointer(Delegate d, string name, string? generation = null)
    {
        if (d == null) throw new ArgumentNullException(nameof(d));
        var handle = GCHandle.Alloc(d, GCHandleType.Normal);
        IntPtr ptr;
        try { ptr = Marshal.GetFunctionPointerForDelegate(d); }
        catch
        {
            handle.Free();
            throw;
        }
        string owner = OwnerOf(d);
        string gen = ResolveGeneration(generation, owner);
        string? replaceLine;
        lock (Gate)
        {
            replaceLine = AddUnlocked(new Entry
            {
                Kind = "FunctionPointer",
                Name = name ?? d.Method?.Name ?? "",
                Owner = owner,
                Generation = gen,
                Handle = handle,
                HasHandle = true,
                KeepAlive = d,
            });
        }
        EmitReplace(replaceLine);
        return ptr;
    }

    /// <summary>
    /// Hold a managed delegate that was ConvertDelegate'd (or handed to native) so Core can
    /// drop the slot on Uninstall without the default-ALC cache being the only root.
    /// A re-bind of the same name keeps the previous pin until that generation is released
    /// or <see cref="DropByName"/>; the table is keyed by (name, sequence) so two generations
    /// can both stay rooted. Empty <paramref name="generation"/> is filled from the
    /// delegate's collectible ALC name when that name is a Core generation (the Keep-time
    /// id, not the unload-time id).
    /// </summary>
    public static void KeepDelegate(Delegate d, string name, string? generation = null)
    {
        if (d == null) throw new ArgumentNullException(nameof(d));
        string owner = OwnerOf(d);
        string gen = ResolveGeneration(generation, owner);
        string? replaceLine;
        lock (Gate)
        {
            replaceLine = AddUnlocked(new Entry
            {
                Kind = "Delegate",
                Name = name ?? d.Method?.Name ?? "",
                Owner = owner,
                Generation = gen,
                KeepAlive = d,
            });
        }
        EmitReplace(replaceLine);
    }

    /// <summary>Drop every pin with this name (any kind). Used when a slot is nulled.</summary>
    public static int DropByName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        var snapshot = new List<Entry>();
        lock (Gate)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(Entries[i].Name, name, StringComparison.Ordinal))
                    continue;
                snapshot.Add(Entries[i]);
                Entries.RemoveAt(i);
            }
        }
        return FreeSnapshot(snapshot);
    }

    public static int Count
    {
        get { lock (Gate) return Entries.Count; }
    }

    /// <summary>How many pins are tagged with <paramref name="generation"/> (exact match).</summary>
    public static int CountFor(string? generation)
    {
        string gen = generation ?? "";
        lock (Gate)
        {
            int n = 0;
            for (int i = 0; i < Entries.Count; i++)
            {
                if (MatchesGeneration(Entries[i], gen))
                    n++;
            }
            return n;
        }
    }

    /// <summary>
    /// Free every handle registered for <paramref name="generation"/>. Other generations
    /// stay. Empty-generation pins (recorded outside the ALC-name scope) drop with a Core
    /// generation so a Keep-time mismatch cannot leave KeepAlive roots. A pin whose
    /// <c>owner</c> is this Core ALC name also drops, even if Keep stored a different
    /// generation key. Bootstrap-owned pins never drop with a Core generation. Safe to
    /// call twice. Never use this for a generation that is being restored.
    /// Returns the number of entries dropped (KeepDelegate and GCHandle both count).
    /// </summary>
    public static int ReleaseGeneration(string? generation) =>
        ReleaseGeneration(generation, out _, out _);

    /// <summary>
    /// Same as <see cref="ReleaseGeneration(string)"/> and returns the dropped pin names
    /// plus whatever remains in the table (freeze / Bootstrap process-lifetime pins).
    /// </summary>
    public static int ReleaseGeneration(
        string? generation,
        out List<string> droppedNames,
        out List<string> remainingNames)
    {
        string gen = generation ?? "";
        var snapshot = new List<Entry>();
        lock (Gate)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                if (!MatchesGeneration(Entries[i], gen))
                    continue;
                snapshot.Add(Entries[i]);
                Entries.RemoveAt(i);
            }
            remainingNames = NamesUnlocked();
        }
        droppedNames = NamesOf(snapshot);
        droppedNames.Sort(StringComparer.Ordinal);
        remainingNames.Sort(StringComparer.Ordinal);
        return FreeSnapshot(snapshot);
    }

    /// <summary>Grep line after a generation release: names dropped and names still pinned.</summary>
    public static string FormatReleaseLine(
        string? generation,
        int before,
        int after,
        IReadOnlyList<string>? dropped,
        IReadOnlyList<string>? remaining) =>
        "pin-release NativePinRegistry gen=" + (generation ?? "")
        + " before=" + before.ToString(CultureInfo.InvariantCulture)
        + " after=" + after.ToString(CultureInfo.InvariantCulture)
        + " dropped=[" + JoinNames(dropped) + "]"
        + " remaining=[" + JoinNames(remaining) + "]";

    /// <summary>
    /// Grep line when a same-name pin is recorded without freeing the previous handle.
    /// <paramref name="kept"/> is the number of live pins with that name after the add.
    /// </summary>
    public static string FormatReplaceLine(
        string? kind,
        string? name,
        int seq,
        int previousSeq,
        string? previousGen,
        string? generation,
        int kept) =>
        "pin-replace NativePinRegistry name=" + (name ?? "")
        + " kind=" + (kind ?? "")
        + " seq=" + seq.ToString(CultureInfo.InvariantCulture)
        + " previousSeq=" + previousSeq.ToString(CultureInfo.InvariantCulture)
        + " previousGen=" + (previousGen ?? "")
        + " gen=" + (generation ?? "")
        + " kept=" + kept.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Freeze-buffer pin name unique per packet. Two in-flight connections (or two packets
    /// from the same connection) must not share a handle: collect pulses during a Core swap
    /// can otherwise collect an earlier wrapper (R14 finding 3).
    /// </summary>
    public static string FreezeConnectionPinName(long connectionId, int packetSequence) =>
        "freeze.conn." + connectionId.ToString(CultureInfo.InvariantCulture)
        + "." + packetSequence.ToString(CultureInfo.InvariantCulture);

    /// <summary>Most recent <see cref="FormatReplaceLine"/>, or null if no same-name add has run.</summary>
    public static string? LastReplaceLine
    {
        get { lock (Gate) return LastReplace; }
    }

    /// <summary>
    /// Free every handle. Process-exit only: a Core collect-fail restore still needs
    /// generation N's pins. Prefer <see cref="ReleaseGeneration"/>.
    /// </summary>
    public static int ReleaseAll()
    {
        Entry[] snapshot;
        lock (Gate)
        {
            snapshot = Entries.ToArray();
            Entries.Clear();
        }
        return FreeSnapshot(snapshot);
    }

#if PROGENITOR_TESTS
    /// <summary>Drop every entry without regard to generation so tests do not leak pins.</summary>
    public static void ResetForTests()
    {
        ReleaseAll();
        lock (Gate)
        {
            NextSequence = 0;
            LastReplace = null;
        }
    }

    /// <summary>Record a pin with an explicit owner so tests can cover Keep-time key mismatches.</summary>
    public static void RecordForTests(string name, string owner, string generation)
    {
        lock (Gate)
        {
            AddUnlocked(new Entry
            {
                Kind = "Delegate",
                Name = name ?? "",
                Owner = owner ?? "",
                Generation = generation ?? "",
                KeepAlive = (Action)(() => { }),
            });
        }
    }

    /// <summary>
    /// How many table entries still hold a live GCHandle. A same-name re-bind must not
    /// drop this: the previous handle stays until generation release or DropByName.
    /// </summary>
    public static int AllocatedHandleCountForTests()
    {
        lock (Gate)
        {
            int n = 0;
            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i].HasHandle && Entries[i].Handle.IsAllocated)
                    n++;
            }
            return n;
        }
    }
#endif

    /// <summary>One line per pin for the Core-reload diagnostic dump (kind, name, owner, generation).</summary>
    public static List<string> Describe()
    {
        lock (Gate)
        {
            var lines = new List<string>(Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                lines.Add(CoreReloadPolicy.FormatNativePinLine(e.Kind, e.Name, e.Owner, e.Generation));
            }
            return lines;
        }
    }

    /// <summary>
    /// Keep-time generation id → live pin count, sorted by generation name.
    /// Empty generation is listed as <c>""</c> so a Keep that ran before
    /// <c>PinGeneration</c> advanced is visible next to the real Core gens.
    /// </summary>
    public static List<KeyValuePair<string, int>> CountByGeneration()
    {
        lock (Gate)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < Entries.Count; i++)
            {
                string g = Entries[i].Generation ?? "";
                map.TryGetValue(g, out int n);
                map[g] = n + 1;
            }
            var list = new List<KeyValuePair<string, int>>(map.Count);
            foreach (var kv in map)
                list.Add(kv);
            list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return list;
        }
    }

    /// <summary>
    /// Generation id recorded at Keep time. Prefers the caller-supplied id; if that is
    /// empty, uses the delegate/target's collectible Core ALC name so a pin created
    /// while <c>CoreLoader.PinGeneration</c> was still null still matches Unload.
    /// </summary>
    public static string ResolveGeneration(string? generation, string? owner)
    {
        if (!string.IsNullOrEmpty(generation)) return generation;
        if (CoreReloadPolicy.IsCoreAlcName(owner)) return owner ?? "";
        return "";
    }

    private static bool MatchesGeneration(Entry e, string gen)
    {
        if (string.Equals(e.Generation, FreezeGeneration, StringComparison.Ordinal))
            return string.Equals(gen, FreezeGeneration, StringComparison.Ordinal);
        // Process-lifetime Bootstrap trampolines: never drop with a Core generation,
        // even if Keep was passed CurrentPinGeneration (evidence 57 must not free them).
        if (string.Equals(e.Generation, BootstrapGeneration, StringComparison.Ordinal)
            || IsBootstrapOwner(e.Owner))
            return string.Equals(gen, BootstrapGeneration, StringComparison.Ordinal);
        if (string.Equals(e.Generation, gen, StringComparison.Ordinal))
            return true;
        if (!CoreReloadPolicy.IsCoreAlcName(gen))
            return false;
        // pin-diag attributes by owner= as well as gen=; a stale Keep-time key must not hide the pin.
        if (string.Equals(e.Owner, gen, StringComparison.Ordinal))
            return true;
        // Keep before the ALC name was published: empty generation, Core-looking owner or name.
        return string.IsNullOrEmpty(e.Generation);
    }

    private static bool IsBootstrapOwner(string? owner) =>
        string.Equals(owner, BootstrapOwner, StringComparison.Ordinal);

    private static List<string> NamesUnlocked()
    {
        var names = new List<string>(Entries.Count);
        for (int i = 0; i < Entries.Count; i++)
            names.Add(Entries[i].Name ?? "");
        return names;
    }

    private static List<string> NamesOf(IReadOnlyList<Entry> snapshot)
    {
        var names = new List<string>(snapshot.Count);
        for (int i = 0; i < snapshot.Count; i++)
            names.Add(snapshot[i].Name ?? "");
        return names;
    }

    private static string JoinNames(IReadOnlyList<string>? names)
    {
        if (names == null || names.Count == 0) return "";
        return string.Join(",", names);
    }

    /// <summary>
    /// Append <paramref name="e"/> under (name, sequence). Does not free a previous pin of
    /// the same name. Returns a replace line when at least one same-name pin was already live.
    /// Caller holds <see cref="Gate"/>.
    /// </summary>
    private static string? AddUnlocked(Entry e)
    {
        e.Sequence = ++NextSequence;
        int previousSeq = 0;
        string previousGen = "";
        int sameName = 0;
        string pinName = e.Name ?? "";
        for (int i = 0; i < Entries.Count; i++)
        {
            if (!string.Equals(Entries[i].Name, pinName, StringComparison.Ordinal))
                continue;
            sameName++;
            if (Entries[i].Sequence >= previousSeq)
            {
                previousSeq = Entries[i].Sequence;
                previousGen = Entries[i].Generation ?? "";
            }
        }
        Entries.Add(e);
        if (sameName == 0) return null;
        string line = FormatReplaceLine(
            e.Kind, pinName, e.Sequence, previousSeq, previousGen, e.Generation, sameName + 1);
        LastReplace = line;
        return line;
    }

    private static void EmitReplace(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
#if !PROGENITOR_TESTS
        try { ProcessHost.Log?.LogInfo(line); }
        catch { /* never throw from the pin table */ }
#endif
    }

    private static string OwnerOf(object target)
    {
        try
        {
            if (target is Delegate d)
            {
                var asm = d.Method?.DeclaringType?.Assembly;
                if (asm != null)
                {
                    var alc = AssemblyLoadContext.GetLoadContext(asm);
                    if (!string.IsNullOrEmpty(alc?.Name)) return alc.Name;
                    return asm.GetName().Name ?? "";
                }
            }
            var t = target.GetType();
            var typeAsm = t.Assembly;
            var typeAlc = AssemblyLoadContext.GetLoadContext(typeAsm);
            if (!string.IsNullOrEmpty(typeAlc?.Name)) return typeAlc.Name;
            return typeAsm.GetName().Name ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static int FreeSnapshot(IReadOnlyList<Entry> snapshot)
    {
        int n = snapshot.Count;
        for (int i = 0; i < snapshot.Count; i++)
        {
            if (snapshot[i].HasHandle && snapshot[i].Handle.IsAllocated)
            {
                try { snapshot[i].Handle.Free(); }
                catch { /* already freed */ }
            }
        }
        return n;
    }
}
