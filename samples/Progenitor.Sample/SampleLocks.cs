using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Progenitor.Inventory;

#if !PROGENITOR_TESTS
using Progenitor.Plugins;
#endif

namespace Progenitor.Sample;

/// <summary>
/// The <c>.items lock</c> / <c>unlock</c> / <c>locks</c> verbs: a password lock on one container through
/// <c>ctx.Inventory.OnOpenRequest</c> (the container open gate). A locked container asks for its password before it
/// opens, at the chest and in the remote preview; a right answer is remembered until the player disconnects. Only a
/// salted hash is kept, in memory (a real mod keeps it in its own save). Server half only; admin only.
///
/// <para>The password is typed into a chat command here, so it shows in the chat line and the command log: use a
/// throwaway test word. The player's answer goes through the masked prompt and is never logged.</para>
/// </summary>
internal static class SampleLocks
{
    /// <summary>A salted SHA-256 of a password; the salt is 16 random bytes.</summary>
    internal readonly struct LockHash
    {
        internal LockHash(byte[] salt, byte[] hash) { Salt = salt; Hash = hash; }
        internal byte[] Salt { get; }
        internal byte[] Hash { get; }
    }

    /// <summary>Hash <paramref name="password"/> with a fresh salt.</summary>
    internal static LockHash Make(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return new LockHash(salt, Digest(salt, password));
    }

    /// <summary>True when <paramref name="password"/> matches <paramref name="hash"/> (a fixed-time compare).</summary>
    internal static bool Matches(LockHash hash, string password) =>
        hash.Salt != null && hash.Hash != null && CryptographicOperations.FixedTimeEquals(Digest(hash.Salt, password), hash.Hash);

    private static byte[] Digest(byte[] salt, string password)
    {
        var text = Encoding.UTF8.GetBytes(password ?? "");
        var all = new byte[salt.Length + text.Length];
        Buffer.BlockCopy(salt, 0, all, 0, salt.Length);
        Buffer.BlockCopy(text, 0, all, salt.Length, text.Length);
        try { return SHA256.HashData(all); }
        finally { CryptographicOperations.ZeroMemory(all); CryptographicOperations.ZeroMemory(text); }
    }

    private static readonly Dictionary<ContainerId, LockHash> Locks = new();

    // The players who ticked Remember password, per container: this plugin's own grant (a real mod saves it). The gate
    // keeps nothing of it.
    private static readonly HashSet<(ulong Player, ContainerId Container)> Remembered = new();

    internal static int Count => Locks.Count;

    internal static int RememberedCount => Remembered.Count;

    /// <summary>
    /// The handler's decision: a locked container is challenged (with the Remember password box), unless this player
    /// ticked Remember for it; everything else is allowed.
    /// </summary>
    internal static ContainerOpenDecision Decide(ulong player, ContainerId container)
    {
        if (!Locks.ContainsKey(container) || Remembered.Contains((player, container))) return ContainerOpenDecision.Allow;
        return ContainerOpenDecision.Challenge(container.ToString(), "Locked", "Enter this container's password.", 32, offerRemember: true);
    }

    /// <summary>The answer handler's verdict for a typed <paramref name="password"/>; a right one with <paramref name="remember"/> is kept.</summary>
    internal static ContainerAnswerVerdict Answer(ulong player, ContainerId container, string password, bool remember)
    {
        if (!Locks.TryGetValue(container, out var hash) || !Matches(hash, password)) return ContainerAnswerVerdict.Reject("Wrong password.");
        if (remember) Remembered.Add((player, container));
        return ContainerAnswerVerdict.Accept(rememberForSession: true);
    }

    /// <summary>Lock (or re-lock with a new password): every remembered unlock of that container is forgotten.</summary>
    internal static void Lock(ContainerId container, string password)
    {
        Locks[container] = Make(password);
        Remembered.RemoveWhere(r => r.Container == container);
    }

    internal static bool Unlock(ContainerId container)
    {
        Remembered.RemoveWhere(r => r.Container == container);
        return Locks.Remove(container);
    }

    internal static IEnumerable<ContainerId> Locked => Locks.Keys;

#if !PROGENITOR_TESTS
    private static IDisposable? _gate;

    /// <summary>Register the open handler (server).</summary>
    internal static void Attach(PluginContext ctx)
    {
        if (!ctx.IsServer) return;
        _gate = ctx.Inventory.OnOpenRequest(
            request => Decide(request.PlatformId, request.Container),
            answer => Answer(answer.Request.PlatformId, answer.Request.Container, answer.Secret.Reveal(), answer.Remember));
    }

    internal static void Detach()
    {
        _gate?.Dispose();
        _gate = null;
        Locks.Clear();
        Remembered.Clear();
    }
#endif
}
