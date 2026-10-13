using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Progenitor.Persistence;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Job FEX3 (RD6 J4). The persistence half the Sample never showed: <see cref="IModStore.Remove"/>,
/// <see cref="IModStore.Keys"/>, and the <b>pending-store merge</b> — the one rule about
/// <c>ctx.Save</c> that a mod author will otherwise learn from a lost file.
///
/// <b>What <c>.joins</c> already showed and what this adds.</b> <see cref="SampleJoins"/>
/// (<c>Plugin.cs</c>) reads and writes one key with <c>Get</c>/<c>Set</c>. That is two of the four
/// members. <c>.store keys</c> lists what is actually in the bag, <c>.store reset</c> drops the
/// join book with <c>Remove</c> and flushes, and <c>.store</c> reports both alongside the save
/// directory the file lives in.
///
/// <b>The pending store, and the one operation the merge does not make safe.</b> <c>OnLoad</c> on
/// a server start happens <em>before</em> the world save folder is known. A store in that state is
/// pending: writes are held in memory, no file is touched, and when the folder arrives Core loads
/// the disk JSON as the base and lays the held keys over it (<c>Persistence/StoreMerge.cs:20</c>,
/// <c>Persistence/ModStore.cs:14-17</c>). Without that merge an <c>OnLoad</c> <c>Set</c> would mark
/// the store dirty, skip the disk load, and the next save would write only the in-memory keys —
/// silently deleting every other key in the file. So a <b>blind</b> write in <c>OnLoad</c> is safe.
///
/// A <b>read-modify-write</b> is not, and this counter is where the Sample says so (R58 finding 2).
/// On a pending store <c>Get</c> reads an empty document, so <see cref="RecordLoad"/> from
/// <c>OnLoad</c> would compute <c>0 + 1</c> and the overlay would lay that <c>1</c> over the real
/// <c>17</c> in the file — permanently, on every start, with the log line cheerfully explaining the
/// merge that ate it. The fix is the framework's own answer: <see cref="IModStore.WhenReady"/>,
/// which runs the increment once the store is backed by the save folder and the disk value is
/// visible. <see cref="ArmLoadCounter"/> is the whole pattern in five lines.
///
/// <b>Why <c>.store</c> and not <c>.joins reset</c>.</b> RD6 J4 proposed hanging both on
/// <c>.joins</c>, which is an ungated read every player may run. <c>reset</c> deletes persisted
/// state belonging to everyone on the save, so it needs <c>adminOnly: true</c> — and that is an
/// attribute on the whole command, never a hand-rolled check inside one branch
/// (<see cref="SampleCommands"/>'s gating rule). A second, gated command is the shape that rule
/// forces; <c>.joins</c> keeps working unchanged.
///
/// Reload-safe: the one field is the <c>WhenReady</c> registration, disposed and replaced by the
/// next <see cref="Attach"/> so a reload cannot queue the increment twice (Core drops it as well,
/// on plugin unload and on Core uninstall). The counter itself lives in the store, which belongs
/// to the save and not to the plugin, so unload deletes nothing and the next load continues the
/// count. Every line and every branch is game-free, so the test project compiles this file and
/// exercises it against the real <c>ModStore</c> over a temp folder.
/// </summary>
internal static class SampleStore
{
    /// <summary>Log/grep area: <c>Sample store: …</c>.</summary>
    internal const string Area = "store";

    /// <summary>
    /// Key of the join book, owned here rather than in <see cref="SampleJoins"/> so both the
    /// game-free tests and <c>Plugin.cs</c> name the same string. <c>.store reset</c> removes it.
    /// </summary>
    internal const string JoinsKey = "joins";

    /// <summary>
    /// Key of the load counter written from <c>OnLoad</c>. Its whole point is being written while
    /// the store may still be pending.
    /// </summary>
    internal const string LoadsKey = "loads";

    /// <summary>How many keys <c>.store keys</c> will list before it stops and says how many are left.</summary>
    internal const int MaxKeysListed = 20;

    /// <summary>What the caller asked for.</summary>
    internal enum Kind
    {
        /// <summary>No argument: the load counter, the key count, and the save directory.</summary>
        Status = 0,

        /// <summary>List <see cref="IModStore.Keys"/>.</summary>
        Keys = 1,

        /// <summary>Drop <see cref="JoinsKey"/> with <see cref="IModStore.Remove"/>.</summary>
        Reset = 2,

        /// <summary>
        /// <c>ModSave.CurrentSaveDirectory</c> and the JSON path under it (job J19). Its own
        /// branch rather than a field of <c>status</c> because the answer needs a paragraph:
        /// empty is a state, not a failure.
        /// </summary>
        Dir = 3,

        /// <summary>
        /// <c>ModSave.SaveAll()</c> on demand, then read the file back off disk and report what
        /// is actually there (job J19).
        /// </summary>
        Flush = 4,

        /// <summary>
        /// <c>ModSave.Store(guid)</c>, the static route to the same bag <c>ctx.Save</c> hands out
        /// (job J19).
        /// </summary>
        Static = 5,

        /// <summary>
        /// The server-level store, <c>ctx.ServerSave</c>: its keys, the all-time join book that
        /// survives wipes and world restores, and its folder.
        /// </summary>
        Server = 6,
    }

    /// <summary>Parse the one token <c>.store</c> takes.</summary>
    internal static bool TryParse(string? token, out Kind kind)
    {
        kind = Kind.Status;
        if (string.IsNullOrWhiteSpace(token)) return true;

        switch (token!.Trim().ToLowerInvariant())
        {
            case "status":
                return true;
            case "keys":
                kind = Kind.Keys;
                return true;
            case "reset":
                kind = Kind.Reset;
                return true;
            case "dir":
                kind = Kind.Dir;
                return true;
            case "flush":
                kind = Kind.Flush;
                return true;
            case "static":
                kind = Kind.Static;
                return true;
            case "server":
                kind = Kind.Server;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Usage line, replied on anything <see cref="TryParse"/> refuses.</summary>
    internal const string UsageReply =
        "Usage: .store [status] | .store keys | .store dir | .store flush | .store static | .store server | .store reset";

    /// <summary>
    /// Increment the load counter: read, add one, write. Never call this from <c>OnLoad</c> —
    /// on a pending store the read is the fallback <c>0</c> and the overlay writes <c>1</c> over
    /// whatever the file holds. <see cref="ArmLoadCounter"/> is the supported way to reach it.
    /// Returns the new count.
    /// </summary>
    internal static int RecordLoad(IModStore store)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));
        int loads = store.Get(LoadsKey, 0) + 1;
        store.Set(LoadsKey, loads);
        return loads;
    }

    /// <summary>
    /// The pattern this file exists to show: a read-modify-write deferred to
    /// <see cref="IModStore.WhenReady"/>, so it runs once the store is backed by the save folder
    /// and the disk value is visible. Registered from <c>OnLoad</c>; the callback runs at the
    /// pending merge on a server start, or in line when the folder was already known (a plugin
    /// hot-reloaded mid-session). <paramref name="log"/> takes <see cref="LoadLine"/>.
    ///
    /// <para><b>How the line knows which branch it is on</b> (R59 finding 9). Nothing on
    /// <see cref="IModStore"/> tells a callback that — <c>ModStore.IsReady</c> is internal to Core
    /// and adding a public flag for one Sample log line is not worth a Core API — so the Sample
    /// derives it from the one thing it can observe for free: the in-line branch fires
    /// <em>during</em> the <c>WhenReady</c> call (<c>Persistence/ModStore.cs:316</c>,
    /// <c>allowInline: true</c>), while the pending branch always goes through the main-thread
    /// queue (<c>:403</c>, <c>allowInline: false</c>) and so cannot run before the call returns.
    /// A local set after the call is therefore an exact test of which one happened. It is read
    /// under no lock because both branches that read it run on the game main thread, the same
    /// thread this method is called from.</para>
    /// </summary>
    /// <returns>The registration handle; dispose it to cancel a still-pending increment.</returns>
    internal static IDisposable ArmLoadCounter(IModStore store, Action<string>? log)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));

        bool registered = false;
        var registration = store.WhenReady(ready =>
        {
            bool inLine = !registered;
            int loads = RecordLoad(ready);
            log?.Invoke(LoadLine(loads, inLine));
        });
        registered = true;
        return registration;
    }

    /// <summary>
    /// The line the counter writes. It names <c>WhenReady</c> because the interesting fact is not
    /// the number: it is that the same three lines run from <c>OnLoad</c> would have reset it —
    /// and it names <em>which</em> of <c>WhenReady</c>'s two branches ran, because the tutorial
    /// sends the reader here to tell them apart and one string for both taught nothing (R59
    /// finding 9).
    /// </summary>
    /// <param name="inLine">
    /// True when the callback ran inside the <c>WhenReady</c> call because the store was already
    /// backed by the save folder; false when it was posted and ran after the pending merge. The
    /// Sample registers from <c>OnLoad</c>, which is on the main thread, so those are the only two
    /// cases it can be in.
    /// </param>
    internal static string LoadLine(int loads, bool inLine) =>
        "loads=" + N(loads) + " ("
        + (inLine
            ? "WhenReady ran in line: the store was already real"
            : "WhenReady ran after the pending merge")
        + "; a Set from OnLoad would have reset it - see tutorial 3a)";

    /// <summary>Reply for the bare <c>.store</c>.</summary>
    internal static string StatusReply(int loads, int keyCount, string? saveDirectory) =>
        "loads=" + N(loads) + " keys=" + N(keyCount) + " dir=" + Directory(saveDirectory);

    /// <summary>
    /// How the save directory is reported. Empty means the folder is not known yet, which is the
    /// pending state, and saying so is more useful than printing nothing.
    /// </summary>
    internal static string Directory(string? saveDirectory) =>
        string.IsNullOrEmpty(saveDirectory)
            ? "<not known yet: the store is pending>"
            : saveDirectory!;

    /// <summary>
    /// Reply for <c>.store keys</c>. <see cref="IModStore.Keys"/> is a snapshot, not a live view,
    /// so this is safe to walk; long bags are cut at <see cref="MaxKeysListed"/>.
    /// </summary>
    internal static string KeysReply(IReadOnlyList<string>? keys)
    {
        int count = keys == null ? 0 : keys.Count;
        if (count == 0) return "The store has no keys yet.";

        var sb = new StringBuilder();
        sb.Append("Store keys (").Append(N(count)).Append("): ");
        int shown = count < MaxKeysListed ? count : MaxKeysListed;
        for (int i = 0; i < shown; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(keys![i]);
        }

        if (shown < count) sb.Append(", … and ").Append(N(count - shown)).Append(" more");
        return sb.ToString();
    }

    /// <summary>Reply for <c>.store reset</c>.</summary>
    internal static string ResetReply(bool removed) =>
        removed
            ? "Removed '" + JoinsKey + "'. Every player's join count on this save is gone; the next "
                + "join starts them at 1."
            : "Nothing to remove: '" + JoinsKey + "' was not in the store.";

    // ---- the ModSave statics (job J19) --------------------------------------------------------
    //
    // `ctx.Save` is the route a plugin should take: it is the store for *this* plugin's guid, and
    // Core hands it out already resolved. The three statics below exist for the cases that route
    // cannot reach, and each has one right moment:
    //
    //   ModSave.Store(guid)        - code that is not in a plugin's OnLoad and has no context: a
    //                                static helper class, a command class registered by Type, a
    //                                background task. Same instance ctx.Save returned; `.store
    //                                static` proves that with a reference compare rather than
    //                                asserting it.
    //   ModSave.CurrentSaveDirectory - diagnostics only. A mod must not build paths from it (the
    //                                folder moves: Core relocates stores when the resolved save
    //                                directory changes). `.store dir` prints it and the path Core
    //                                itself derives, which is the honest way to show both.
    //   ModSave.SaveAll()          - force a write now. The game's own save trigger and a player
    //                                disconnect already flush; call this when an operator has just
    //                                changed something and wants it on disk before a crash could
    //                                eat it. `.store flush` calls it and then reads the file back.

    /// <summary>
    /// The JSON path Core derives for a mod guid, through <c>SavePaths.StoreFile</c> — the same
    /// public helper <c>ModSave</c> itself uses, rather than a second <c>Path.Combine</c> that
    /// would go stale the day the layout moves. Empty when the folder or the guid is unknown.
    /// </summary>
    internal static string StoreFileOf(string? saveDirectory, string? modGuid)
    {
        if (string.IsNullOrEmpty(saveDirectory) || string.IsNullOrEmpty(modGuid)) return "";
        try { return SavePaths.StoreFile(saveDirectory!, modGuid!); }
        catch { return ""; }
    }

    /// <summary>Reply for <c>.store dir</c>: the directory, and the file Core derives from it.</summary>
    internal static string DirReply(string? saveDirectory, string? storeFile) =>
        string.IsNullOrEmpty(saveDirectory)
            ? "ModSave.CurrentSaveDirectory is empty: the world save folder is not resolved yet, so "
                + "every store is pending and nothing has been written. This is normal during OnLoad "
                + "on a server start - see .store status and IModStore.WhenReady."
            : "ModSave.CurrentSaveDirectory = " + saveDirectory
                + " | this plugin's JSON: " + (string.IsNullOrEmpty(storeFile) ? "<unresolved>" : storeFile!)
                + " (diagnostic only - do not build paths from it, Core relocates stores when the "
                + "resolved save folder changes)";

    /// <summary>
    /// Reply for <c>.store flush</c>. The numbers come from reading the file back after
    /// <c>ModSave.SaveAll()</c> returned, because <c>SaveAll</c> is <c>void</c> and "it did not
    /// throw" is not the same claim as "the bytes are there".
    ///
    /// <para>The missing-file branch names the two things that actually produce it.
    /// <c>ModStore.SaveToDisk</c> refuses only when the store is pending or has no path
    /// (<c>Persistence/ModStore.cs:639-669</c>); otherwise it serializes and writes whatever it
    /// holds, so an <em>empty</em> store writes <c>{}</c> rather than nothing (R65 finding 4 — the
    /// earlier reply told the operator to add a key, which would not have changed the outcome).
    /// What is left is a store still pending after <c>SaveAll</c>'s relocate pass, which logs its
    /// own one-shot skip warning, and a write that threw, which logs
    /// <c>ModSave: save '…' threw:</c>. Both are in the server log, so the reply points there.</para>
    /// </summary>
    internal static string FlushReply(string? storeFile, bool exists, long bytes, int keyCount)
    {
        if (string.IsNullOrEmpty(storeFile))
            return "ModSave.SaveAll() ran, but the save folder is not resolved yet, so there was "
                + "nothing to write: every store is still pending and its keys are held in memory.";

        return exists
            ? "ModSave.SaveAll() wrote " + storeFile + " (" + N64(bytes) + " byte(s), " + N(keyCount)
                + " key(s) in memory). SaveAll writes every mod's store, not only this one; the game's "
                + "own save trigger and a player disconnect call it too."
            : "ModSave.SaveAll() ran but " + storeFile + " does not exist. An empty store is not "
                + "the reason: a store that is not pending is written unconditionally, and with no "
                + "keys that is the two bytes {}. So this store was either still pending after "
                + "SaveAll relocated the stores, or its write threw - either way the server log "
                + "has the line.";
    }

    /// <summary>
    /// Reply for <c>.store static</c>. <paramref name="sameInstance"/> is the whole point: the
    /// static route is not a second copy of the bag, so a helper class that takes it cannot drift
    /// away from what the plugin wrote through <c>ctx.Save</c>.
    /// </summary>
    internal static string StaticReply(string guid, bool sameInstance, int keyCount) =>
        "ModSave.Store(\"" + guid + "\") returned "
        + (sameInstance
            ? "the SAME instance ctx.Save gave this plugin"
            : "a DIFFERENT instance from ctx.Save - that should not happen; the store is cached per guid")
        + " (" + N(keyCount) + " key(s)). Use ctx.Save inside a plugin; use ModSave.Store(guid) from "
        + "code that has no context - a static helper, a command class registered by Type, a "
        + "background task. On the client both are a no-op store: nothing is read or written there.";

    // ---- the server-level store -----------------------------------------------------------------
    //
    // `ctx.Save` is this world's: a wipe archives it with the world and a restore brings back the
    // copy from that point. `ctx.ServerSave` is the server's: <persistentDataPath>\progenitor-server,
    // outside every world folder, so neither touches it. The Sample keeps the same join book in
    // both (Plugin.OnPlayerConnected): `.joins` is "joins in this world", the server book is "joins
    // on this server, ever".

    /// <summary>
    /// Reply for <c>.store server</c>: what the server-level store holds, how many players its
    /// all-time join book knows, and where it lives. Empty folder means the persistent data root
    /// is not known yet (the store is pending, as a world store is before its folder is known).
    /// </summary>
    internal static string ServerReply(int keyCount, int players, int joins, string? directory) =>
        "Server store (survives wipes and world restores): keys=" + N(keyCount)
        + " allTimePlayers=" + N(players) + " allTimeJoins=" + N(joins)
        + " dir=" + (string.IsNullOrEmpty(directory) ? "<not known yet: the store is pending>" : directory!);

    /// <summary>
    /// The <c>Detach</c> line for <c>ModSave.RemoveAll(owner)</c>. The count is callbacks that had
    /// been registered with <c>WhenReady</c> and had not run yet — normally zero, because the store
    /// becomes real seconds after a server start and this plugin registers exactly one.
    /// </summary>
    internal static string RemoveAllLine(int dropped) =>
        dropped == 0
            ? "ModSave.RemoveAll(owner) dropped 0 pending WhenReady callback(s): the store was "
                + "already real, so the increment had run."
            : "ModSave.RemoveAll(owner) dropped " + N(dropped) + " pending WhenReady callback(s): "
                + "this plugin is unloading before the save folder was resolved, so Core will not "
                + "call code that is about to go away.";

    /// <summary>The line that says the removal reached the file rather than only memory.</summary>
    internal static string FlushLine(string? saveDirectory) =>
        "flushed with ModSave.SaveAll(); the file under " + Directory(saveDirectory)
        + " no longer has '" + JoinsKey + "'";

    /// <summary>
    /// Game-free runner: everything <c>.store</c> does to the store, and the lines it replies.
    /// The live handler adds the flush, which is the only part that needs the game process.
    /// </summary>
    internal static IReadOnlyList<string> Run(
        IModStore store,
        Kind kind,
        string? saveDirectory,
        out bool removed,
        string? modGuid = null)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));
        removed = false;

        switch (kind)
        {
            case Kind.Keys:
                return new[] { KeysReply(store.Keys) };

            case Kind.Reset:
                removed = store.Remove(JoinsKey);
                return new[] { ResetReply(removed) };

            case Kind.Dir:
                return new[] { DirReply(saveDirectory, StoreFileOf(saveDirectory, modGuid)) };

            default:
                return new[] { StatusReply(store.Get(LoadsKey, 0), store.Keys.Count, saveDirectory) };
        }
    }

#if !PROGENITOR_TESTS
    /// <summary>
    /// The <c>WhenReady</c> registration from the last <see cref="Attach"/>. Kept only so a
    /// second Attach (a hot reload while the store is still pending) replaces rather than stacks;
    /// Core drops it anyway when the plugin unloads.
    /// </summary>
    private static IDisposable? _loadCounter;

    /// <summary>
    /// Called from <c>OnLoad</c> on the server. Registers the increment for the moment the store
    /// becomes real; nothing is read or written here, because here the store may still be pending.
    /// </summary>
    internal static void Attach(IModStore store)
    {
        try
        {
            _loadCounter?.Dispose();
            _loadCounter = ArmLoadCounter(store, line => SampleLog.Info(Area, line));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample store attach threw: " + e);
        }
    }

    /// <summary>
    /// Unload (job J19). Two steps, and the second is the one a mod author forgets.
    ///
    /// <para>Disposing <see cref="_loadCounter"/> cancels <em>this file's</em> pending
    /// <c>WhenReady</c> callback. <c>ModSave.RemoveAll(owner)</c> then drops every <b>other</b>
    /// callback written anywhere in this plugin's assembly — the ones a helper class registered
    /// and did not keep a handle to. Core's reloader sweep calls it as well, so this is belt and
    /// braces; it is here because the count is worth seeing in the log, and because a plugin that
    /// does not use Core's plugin base class does not get the sweep for free.</para>
    ///
    /// <para>Nothing here deletes data. The store belongs to the world save, not to the plugin:
    /// the keys stay on disk and the next load continues the count.</para>
    /// </summary>
    internal static void Detach()
    {
        try
        {
            _loadCounter?.Dispose();
            _loadCounter = null;

            var owner = (object?)Plugin.Active?.Owner;
            if (owner == null) return;
            SampleLog.Info(Area, RemoveAllLine(ModSave.RemoveAll(owner)));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample store detach threw: " + e);
        }
    }

    /// <summary>Server entry point for <c>.store</c>.</summary>
    internal static void HandleCommand(ICommandContext ctx, Kind kind)
    {
        var store = Plugin.Store;
        if (store == null)
        {
            ctx.Reply("Save store is not available.");
            return;
        }

        string guid = Plugin.Active?.Guid ?? "";
        string? dir = ModSave.CurrentSaveDirectory;

        if (kind == Kind.Flush)
        {
            HandleFlush(ctx, store, dir, guid);
            return;
        }

        if (kind == Kind.Server)
        {
            var server = Plugin.ServerStore;
            if (server == null)
            {
                ctx.Reply("Server store is not available.");
                return;
            }
            var book = SampleJoins.Load(server);
            int joins = 0;
            for (int i = 0; i < book.Players.Count; i++) joins += book.Players[i].Count;
            string line = ServerReply(server.Keys.Count, book.Players.Count, joins, ServerSave.CurrentDirectory);
            ctx.Reply(line);
            SampleLog.Info(Area, line);
            return;
        }

        if (kind == Kind.Static)
        {
            // The static route. Same guid, so Core returns the cached instance ctx.Save handed
            // this plugin in OnLoad - a reference compare says so, which is stronger than a
            // sentence claiming it.
            var viaStatic = ModSave.Store(guid);
            ctx.Reply(StaticReply(guid, ReferenceEquals(viaStatic, store), viaStatic.Keys.Count));
            return;
        }

        var lines = Run(store, kind, dir, out bool removed, guid);
        for (int i = 0; i < lines.Count; i++)
        {
            ctx.Reply(lines[i]);
            SampleLog.Info(Area, lines[i]);
        }

        if (kind != Kind.Reset || !removed) return;

        // A Remove only changes memory. The file catches up on the game's own autosave, on a
        // disconnect, or here — so the operator who just deleted something can see it gone
        // instead of taking it on trust.
        ModSave.SaveAll();
        string flushed = FlushLine(ModSave.CurrentSaveDirectory);
        ctx.Reply(flushed);
        SampleLog.Info(Area, flushed);
    }

    /// <summary>
    /// <c>.store flush</c>. <c>ModSave.SaveAll()</c> is <c>void</c>, so the reply is built from a
    /// read of the file <em>after</em> it returns rather than from the fact that it returned.
    /// </summary>
    private static void HandleFlush(ICommandContext ctx, IModStore store, string? dir, string guid)
    {
        ModSave.SaveAll();

        // Re-read the directory: SaveAll resolves and relocates, so it may have become known.
        string? after = ModSave.CurrentSaveDirectory;
        string file = StoreFileOf(after, guid);
        bool exists = false;
        long bytes = 0;
        if (file.Length != 0)
        {
            try
            {
                var info = new System.IO.FileInfo(file);
                exists = info.Exists;
                if (exists) bytes = info.Length;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError("Sample store flush stat threw: " + e);
            }
        }

        string line = FlushReply(file.Length == 0 ? null : file, exists, bytes, store.Keys.Count);
        ctx.Reply(line);
        SampleLog.Info(Area, line);
    }
#endif

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string N64(long value) => value.ToString(CultureInfo.InvariantCulture);
}
