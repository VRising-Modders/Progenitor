using System;
using System.Collections.Generic;

namespace Progenitor.Persistence;

/// <summary>
/// Per-mod key-value bag that is written next to the world save. Values are JSON, so a reloadable
/// plugin can round-trip its own types without a schema in Core. The store lives with the save,
/// not with the plugin: disposing the plugin does not delete the file.
/// </summary>
public interface IModStore
{
    /// <summary>Deserialize <paramref name="key"/> as <typeparamref name="T"/>. Missing or invalid data is <c>default</c>.</summary>
    T? Get<T>(string key);

    /// <summary>Deserialize <paramref name="key"/>, or <paramref name="fallback"/> when the key is missing or cannot be read.</summary>
    T Get<T>(string key, T fallback);

    /// <summary>Replace <paramref name="key"/> with the JSON of <paramref name="value"/>.</summary>
    void Set<T>(string key, T value);

    /// <summary>Drop a key. Returns false when it was not present.</summary>
    bool Remove(string key);

    /// <summary>Snapshot of keys currently in memory. Safe to iterate; not live.</summary>
    IReadOnlyList<string> Keys { get; }

    /// <summary>
    /// Run <paramref name="callback"/> once this store is <b>backed by the world save folder</b>,
    /// which is the only state in which a read sees what is on disk.
    ///
    /// <para><b>Why this exists (R58 finding 2).</b> A store handed out before the save folder is
    /// known is <i>pending</i>: <see cref="Get{T}(string, T)"/> reads an empty document, because
    /// the file has not been loaded yet, and a <see cref="Set{T}"/> is held as a dirty overlay
    /// that is laid <i>over</i> the file when the folder arrives. That merge is what makes a blind
    /// write from <c>OnLoad</c> safe — it cannot delete the rest of the file — but it is exactly
    /// what makes a <b>read-modify-write</b> from <c>OnLoad</c> wrong: the read returns the
    /// fallback, and the overlay then writes that answer over the real value, permanently. A
    /// counter incremented that way reads 1 on a save that has been loaded twenty times.</para>
    ///
    /// <para><b>The rule.</b> A read-modify-write belongs in <c>WhenReady</c>. A blind
    /// <see cref="Set{T}"/> from <c>OnLoad</c> is only safe for keys the file does not already
    /// hold (a boot timestamp, a session id); for anything the file may already hold, register
    /// here.</para>
    ///
    /// <para><b>When it runs.</b> Three cases, and they are three (job FEX8, R59 finding 9; the
    /// old sentence folded the last two into one "otherwise" and described both as running after
    /// a merge, which is not true of the second).</para>
    /// <list type="bullet">
    /// <item><description><b>Already backed by the folder, caller on the main thread</b> — which
    /// is every <c>OnLoad</c> once a world is up: <b>in line</b>, on this call, before
    /// <c>WhenReady</c> returns. The answer is available, so the caller is not made to wait a
    /// frame for a value it could have read on that line.</description></item>
    /// <item><description><b>Already backed by the folder, caller off the main thread</b> (a
    /// background task): posted, and run on the <b>next</b> main-thread drain. There is no merge
    /// to be after.</description></item>
    /// <item><description><b>Still pending</b>: posted when the merge lands, and run on the drain
    /// after it — with the disk value visible.</description></item>
    /// </list>
    /// <para>Once per registration, never twice, and never before the merge. On the client the
    /// store is a no-op and the callback never runs.</para>
    ///
    /// <para><b>Lifetime.</b> The registration is tracked against the assembly that wrote the
    /// callback, so a plugin that unloads while the store is still pending does not have its code
    /// called afterwards. Three things drop it: disposing the returned handle,
    /// <c>ModSave.RemoveAll(owner)</c> (the reloader's sweep calls it on every unload, like
    /// <c>Units.RemoveAll</c>, and passes the plugin's whole set of owned assemblies so a callback
    /// declared in a helper library it ships is dropped too), and <c>ModSave.Uninstall</c>. All
    /// three work right up to the moment the callback runs, the posted-but-not-yet-drained frame
    /// included: canceling then means the post is skipped (job FEX8, R59 finding 3 — they used to
    /// stop working the instant the work was queued). Dispose is idempotent.</para>
    ///
    /// <para>The main-thread queue adds a second dropper rather than a backstop for the first:
    /// when the callback has a target object — a lambda or an instance method, which is nearly
    /// all of them — the queued work is tagged with that object's assembly and the reloader's
    /// <c>DropOwnedBy</c> discards it. A <b>static method group</b> has no target for Core to tag
    /// with, so for that shape the queue is not the mechanism and the sweep above is; the old
    /// paragraph offered the queue's retired-<em>context</em> check as the backstop, and that
    /// check reads the posting closure's context, which is Core's.</para>
    /// </summary>
    /// <returns>A handle that cancels the registration when disposed.</returns>
    IDisposable WhenReady(Action<IModStore> callback);
}
