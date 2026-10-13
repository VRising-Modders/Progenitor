using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace Progenitor.Commands;

/// <summary>
/// Holds the live set of chat commands. Every registration records a stable owner id and a
/// qualified route under <see cref="FallbackRoot"/>. Root shortcuts are optional and disabled
/// for every competing owner when they collide. Registrations are keyed by an owner object so
/// a reloadable plugin can drop every command it added; an older handle cannot remove a
/// replacement from a later generation.
/// </summary>
public static class CommandRegistry
{
    private static readonly object Sync = new();
    private static readonly List<CommandBundle> Bundles = new();
    private static readonly object BuiltInOwner = new BuiltInOwnerTag();
    private static IDisposable? _builtIns;
    private static bool _addingBuiltIns;
    private static string _prefix = ".";
    private static CommandRoutingSettings _settings = CommandRoutingSettings.Default;
    private static CommandRouteTable _table = CommandRouteTable.Empty(CommandRoutingSettings.DefaultFallbackRoot);
    private static int _generation;

    /// <summary>
    /// Bumped whenever the routing table, the settings or the prefix change, so a reader (the chat command hints) can
    /// tell its copy of the command list is stale without comparing it.
    /// </summary>
    internal static int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// Character(s) that mark a chat line as a command. Default is <c>.</c>, matching what V Rising
    /// players already type. Changing this at runtime affects both the chat hook and
    /// <see cref="CommandDispatcher.TryExecute"/>.
    /// </summary>
    public static string Prefix
    {
        get => _prefix;
        set
        {
            _prefix = string.IsNullOrEmpty(value) ? "." : value;
            Interlocked.Increment(ref _generation);
        }
    }

    /// <summary>
    /// Deployment-wide qualified-route root (default <c>p</c>). Reserved in the registry.
    /// </summary>
    public static string FallbackRoot
    {
        get { lock (Sync) return _settings.FallbackRoot; }
    }

    /// <summary>Current administrator routing settings (fallback root, assign/rename/disable).</summary>
    public static CommandRoutingSettings Settings
    {
        get { lock (Sync) return _settings; }
    }

    /// <summary>
    /// Discovers every <see cref="CommandAttribute"/> method in <paramref name="assembly"/> and
    /// registers them under <paramref name="owner"/>. Dispose the result (or call
    /// <see cref="UnregisterOwner"/>) to drop them all.
    /// </summary>
    public static IDisposable RegisterAssembly(Assembly assembly, object owner, string? ownerId = null)
    {
        if (assembly == null) throw new ArgumentNullException(nameof(assembly));
        if (owner == null) throw new ArgumentNullException(nameof(owner));

        EnsureBuiltIns();

        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types ?? Array.Empty<Type>();
            Plugin.Logger?.LogWarning($"Commands: {assembly.GetName().Name} had type-load errors; registering the types that did load.");
        }

        var handles = new List<IDisposable>();
        foreach (var type in types)
        {
            if (type == null || type.IsInterface) continue;
            if (!MightHaveCommands(type)) continue;
            handles.Add(RegisterType(type, owner, ownerId));
        }
        return new CompositeHandle(handles);
    }

    /// <summary>
    /// Discovers <see cref="CommandAttribute"/> methods on <paramref name="type"/> (including
    /// non-public) and registers them under <paramref name="owner"/>. Accepts instance classes and
    /// C# static classes. Static methods are invoked with a null target; instance methods share
    /// one instance created with a parameterless constructor. For an already-constructed object
    /// (so the class can hold a <c>PluginContext</c>), use <see cref="RegisterInstance"/>.
    /// </summary>
    public static IDisposable RegisterType(Type type, object owner, string? ownerId = null)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        if (owner == null) throw new ArgumentNullException(nameof(owner));

        EnsureBuiltIns();
        return RegisterTypeCore(type, owner, suppliedInstance: null, ownerId);
    }

    /// <summary>
    /// Discovers <see cref="CommandAttribute"/> methods on <paramref name="instance"/>'s runtime
    /// type. Instance methods are invoked on this object; static methods are invoked with a null
    /// target. Dispose the result (or call <see cref="UnregisterOwner"/>) to drop them.
    /// </summary>
    public static IDisposable RegisterInstance(object instance, object owner, string? ownerId = null)
    {
        if (instance == null) throw new ArgumentNullException(nameof(instance));
        if (instance is Type)
            throw new ArgumentException("A System.Type is not a command class instance. Use RegisterType to register by type.", nameof(instance));
        if (owner == null) throw new ArgumentNullException(nameof(owner));

        EnsureBuiltIns();
        return RegisterTypeCore(instance.GetType(), owner, instance, ownerId);
    }

    /// <summary>
    /// Removes every command registered under <paramref name="owner"/>. Use this from a reloadable
    /// plugin's <c>Unload</c> when you did not keep the <see cref="IDisposable"/> handles.
    /// </summary>
    public static void UnregisterOwner(object owner)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        lock (Sync)
        {
            int removed = Bundles.RemoveAll(b => ReferenceEquals(b.Owner, owner));
            if (removed > 0) RebuildOrKeep_NoLock();
        }
    }

    /// <summary>Live command bundles, for the Core-reload pin dump.</summary>
    internal static int LiveCount
    {
        get { lock (Sync) return Bundles.Count; }
    }

    internal static void ResetForTests()
    {
        lock (Sync)
        {
            Bundles.Clear();
            _builtIns = null;
            _addingBuiltIns = false;
            _prefix = ".";
            _settings = CommandRoutingSettings.Default;
            _table = CommandRouteTable.Empty(CommandRoutingSettings.DefaultFallbackRoot);
            Interlocked.Increment(ref _generation);
        }
    }

    internal static void EnsureBuiltIns()
    {
        if (_builtIns != null || _addingBuiltIns) return;
        _addingBuiltIns = true;
        try
        {
            // Looked up by name so this file compiles in the game-free test assembly, which does
            // not link BuiltInCommands (that type pulls in the reloader and game chat types).
            var builtInType = typeof(CommandRegistry).Assembly.GetType("Progenitor.Commands.BuiltInCommands", throwOnError: false);
            if (builtInType == null) return;
            _builtIns = RegisterTypeCore(builtInType, BuiltInOwner, suppliedInstance: null, CommandRoutingSettings.BuiltInOwnerId);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Commands: failed to register built-in commands: {e}");
        }
        finally
        {
            _addingBuiltIns = false;
        }
    }

    private static bool MightHaveCommands(Type type)
    {
        try
        {
            if (type.GetCustomAttribute<CommandGroupAttribute>() != null) return true;
            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttribute<CommandAttribute>() != null) return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Commands: scanning {type.FullName} failed: {e.Message}");
        }
        return false;
    }

    /// <summary>
    /// Replace routing settings as a whole. On failure the previous valid table is kept.
    /// </summary>
    public static bool TryApplySettings(CommandRoutingSettings settings, out string error)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        lock (Sync)
        {
            if (!CommandRoutingSettings.IsSingleToken(settings.FallbackRoot))
            {
                error = $"FallbackRoot '{settings.FallbackRoot}' must be a single token (no whitespace).";
                return false;
            }
            if (!CommandRouteTable.TryBuild(Bundles, settings, out var table, out error))
                return false;
            _settings = settings;
            _table = table;
            Interlocked.Increment(ref _generation);
            LogDiagnostics_NoLock();
            return true;
        }
    }

    private static IDisposable RegisterTypeCore(Type type, object owner, object? suppliedInstance, string? ownerId)
    {
        var handle = new RegistrationHandle();
        string resolved = ResolveOwnerId(owner, ownerId);
        if (!CommandRoutingSettings.IsOwnerId(resolved))
        {
            Plugin.Logger?.LogWarning($"Commands: owner id '{resolved}' for {type.FullName} is not a single token; registration skipped.");
            return handle;
        }

        List<CommandBundle> discovered;
        try
        {
            discovered = Discover(type, owner, resolved, handle, suppliedInstance);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Commands: discovering {type.FullName} failed: {e}");
            return handle;
        }

        lock (Sync)
        {
            int start = Bundles.Count;
            foreach (var bundle in discovered)
                Bundles.Add(bundle);
            if (discovered.Count > 0 && !Rebuild_NoLock(out var error))
            {
                Bundles.RemoveRange(start, Bundles.Count - start);
                Plugin.Logger?.LogError($"Commands: registration of {type.FullName} rejected ({error}). Previous routing table kept.");
            }
        }

        return handle;
    }

    private static void Unregister(RegistrationHandle handle)
    {
        lock (Sync)
        {
            int removed = Bundles.RemoveAll(b => ReferenceEquals(b.Registration, handle));
            if (removed > 0) RebuildOrKeep_NoLock();
        }
    }

    private static bool Rebuild_NoLock(out string error)
    {
        if (!CommandRouteTable.TryBuild(Bundles, _settings, out var table, out error))
            return false;
        _table = table;
        Interlocked.Increment(ref _generation);
        LogDiagnostics_NoLock();
        return true;
    }

    private static void RebuildOrKeep_NoLock()
    {
        if (!Rebuild_NoLock(out var error))
            Plugin.Logger?.LogError($"Commands: rebuild rejected ({error}). Previous routing table kept.");
    }

    private static void LogDiagnostics_NoLock()
    {
        foreach (var line in _table.Diagnostics)
            Plugin.Logger?.LogWarning("Commands: " + line);
    }

    internal static bool TryGetRoot(string name, out CommandBundle bundle)
    {
        bundle = null!;
        return _table.TryGetRoot(name, out var slot) && slot.Bundle != null && (bundle = slot.Bundle) != null;
    }

    internal static bool TryGetRootSlot(string name, out RouteSlot slot) =>
        _table.TryGetRoot(name, out slot!);

    internal static bool TryGetGrouped(string group, string name, out CommandBundle bundle)
    {
        bundle = null!;
        return _table.TryGetGrouped(group, name, out var slot) && slot.Bundle != null && (bundle = slot.Bundle) != null;
    }

    internal static bool TryGetGroupedSlot(string group, string name, out RouteSlot slot) =>
        _table.TryGetGrouped(group, name, out slot!);

    internal static bool TryGetGroup(string group, out Dictionary<string, RouteSlot> commands) =>
        _table.TryGetGroup(group, out commands!);

    internal static bool TryGetQualified(string ownerId, string path, out CommandBundle bundle) =>
        _table.TryGetQualified(ownerId, path, out bundle!);

    internal static bool TryGetQualifiedOwner(string ownerId, out Dictionary<string, CommandBundle> byPath) =>
        _table.TryGetQualifiedOwner(ownerId, out byPath!);

    internal static IReadOnlyList<string> Diagnostics
    {
        get { lock (Sync) return _table.Diagnostics; }
    }

    /// <summary>
    /// SETTINGS1 stage 3: every route two or more owners registered (its short form is disabled for
    /// all of them), as (route, owner ids, qualified paths). Read by the Mods menu's problem record.
    /// </summary>
    internal static List<(string Route, IReadOnlyList<string> Owners, IReadOnlyList<string> Qualified)> Conflicts()
    {
        var list = new List<(string Route, IReadOnlyList<string> Owners, IReadOnlyList<string> Qualified)>();
        lock (Sync)
        {
            foreach (var pair in _table.Root)
                if (pair.Value.Ambiguous) list.Add((pair.Key, pair.Value.OwnerIds, pair.Value.QualifiedPaths));
            foreach (var group in _table.Groups)
                foreach (var pair in group.Value)
                    if (pair.Value.Ambiguous) list.Add((group.Key + " " + pair.Key, pair.Value.OwnerIds, pair.Value.QualifiedPaths));
        }

        list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Route, b.Route));
        return list;
    }

    internal static List<CommandBundle> ListActive()
    {
        lock (Sync) return new List<CommandBundle>(_table.Live);
    }

    internal static List<CommandBundle> ListGroup(string group)
    {
        var list = new List<CommandBundle>();
        lock (Sync)
        {
            foreach (var bundle in _table.Live)
            {
                if (string.Equals(bundle.GroupName, group, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(bundle.GroupShortHand)
                        && string.Equals(bundle.GroupShortHand, group, StringComparison.OrdinalIgnoreCase)))
                    list.Add(bundle);
            }
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    internal static string FormatQualified(CommandBundle bundle) =>
        CommandRouteTable.FormatQualified(FallbackRoot, bundle);

    internal static string FormatConflictReply(string route, RouteSlot slot)
    {
        string owners = string.Join(", ", slot.OwnerIds);
        string line = "Ambiguous command '" + Prefix + route + "' is disabled. Owners: " + owners + ".";
        if (slot.QualifiedPaths.Count > 0)
            line += " Use " + string.Join(" or ", slot.QualifiedPaths) + ".";
        return line;
    }

    internal static string FormatHelpLine(CommandBundle bundle)
    {
        string usage = FormatUsage(bundle, bundle.Overloads[0]);
        string fallback = FormatQualified(bundle);
        string? desc = bundle.Description;
        if (bundle.ShortcutsEnabled)
        {
            string line = usage + "  (fallback " + fallback + ")";
            if (string.IsNullOrEmpty(desc)) return line;
            return line + " — " + desc;
        }
        if (string.IsNullOrEmpty(desc)) return fallback;
        return fallback + " — " + desc;
    }

    internal static string FormatUsage(CommandBundle bundle, CommandOverload overload)
    {
        if (!string.IsNullOrEmpty(overload.Attribute.Usage)) return overload.Attribute.Usage!;

        var sb = new StringBuilder();
        sb.Append(Prefix);
        if (bundle.ShortcutsEnabled)
        {
            if (!string.IsNullOrEmpty(bundle.GroupName))
            {
                sb.Append(bundle.GroupName);
                sb.Append(' ');
            }
            sb.Append(bundle.EffectiveShortcut);
        }
        else
        {
            sb.Append(FallbackRoot);
            sb.Append(' ');
            sb.Append(bundle.QualifiedPath);
        }

        var parameters = overload.Parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            bool optional = p.HasDefaultValue;
            bool remainder = overload.HasRemainder && i == parameters.Length - 1;
            string name = p.Name ?? "arg";
            sb.Append(' ');
            if (remainder) sb.Append(optional ? $"[{name}...]" : $"<{name}...>");
            else sb.Append(optional ? $"[{name}]" : $"<{name}>");
        }

        return sb.ToString();
    }

    private static List<CommandBundle> Discover(Type type, object owner, string ownerId, RegistrationHandle handle, object? suppliedInstance)
    {
        var groupAttr = type.GetCustomAttribute<CommandGroupAttribute>();
        string groupName = groupAttr == null ? "" : groupAttr.Name.Trim();
        string? groupShort = groupAttr == null ? null : groupAttr.ShortHand;
        if (string.IsNullOrEmpty(groupName)) groupName = "";

        object? instance = suppliedInstance;
        bool instanceFailed = false;
        bool triedInstance = suppliedInstance != null;

        object? Instance()
        {
            if (triedInstance) return instanceFailed ? null : instance;
            triedInstance = true;

            // C# static classes are abstract and sealed. They cannot be constructed; instance
            // command methods cannot exist on them, so a null target is the correct outcome.
            if (type.IsAbstract && type.IsSealed)
            {
                instanceFailed = true;
                return null;
            }

            if (type.IsAbstract)
            {
                instanceFailed = true;
                Plugin.Logger?.LogWarning($"Commands: {type.FullName} is abstract; instance command methods are skipped.");
                return null;
            }

            try
            {
                instance = Activator.CreateInstance(type, nonPublic: true);
                if (instance == null)
                {
                    instanceFailed = true;
                    Plugin.Logger?.LogWarning($"Commands: {type.FullName} produced a null instance; instance command methods are skipped.");
                }
            }
            catch (Exception e)
            {
                instanceFailed = true;
                Plugin.Logger?.LogWarning($"Commands: could not construct {type.FullName} for instance command methods: {e.Message}");
            }
            return instanceFailed ? null : instance;
        }

        var methods = type.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        var byName = new Dictionary<string, List<CommandOverload>>(StringComparer.OrdinalIgnoreCase);
        foreach (var method in methods)
        {
            CommandAttribute? attr;
            try { attr = method.GetCustomAttribute<CommandAttribute>(); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning($"Commands: reading attributes on {type.Name}.{method.Name} failed: {e.Message}");
                continue;
            }
            if (attr == null) continue;

            if (!TryCreateOverload(type, method, attr, Instance, out var overload)) continue;

            string key = attr.Name.Trim();
            if (string.IsNullOrEmpty(key))
            {
                Plugin.Logger?.LogWarning($"Commands: {type.Name}.{method.Name} has an empty command name; skipped.");
                continue;
            }

            if (!byName.TryGetValue(key, out var list))
            {
                list = new List<CommandOverload>();
                byName[key] = list;
            }
            list.Add(overload);
        }

        var bundles = new List<CommandBundle>(byName.Count);
        foreach (var kv in byName)
        {
            var overloads = kv.Value;
            var aliases = new List<string>();
            string? description = null;
            foreach (var o in overloads)
            {
                if (o.Attribute.ShortHand != null && !aliases.Exists(a => a.Equals(o.Attribute.ShortHand, StringComparison.OrdinalIgnoreCase)))
                    aliases.Add(o.Attribute.ShortHand);
                if (description == null && o.Attribute.Description != null)
                    description = o.Attribute.Description;
            }

            bundles.Add(new CommandBundle(
                owner,
                ownerId,
                handle,
                groupName,
                groupShort,
                overloads[0].Attribute.Name.Trim(),
                aliases,
                description,
                overloads));
        }

        return bundles;
    }

    private static bool TryCreateOverload(
        Type type,
        MethodInfo method,
        CommandAttribute attr,
        Func<object?> instanceFactory,
        out CommandOverload overload)
    {
        overload = null!;
        var parameters = method.GetParameters();
        if (parameters.Length < 1 || parameters[0].ParameterType != typeof(ICommandContext))
        {
            Plugin.Logger?.LogWarning($"Commands: {type.Name}.{method.Name} must take {nameof(ICommandContext)} as its first parameter; skipped.");
            return false;
        }

        var args = new ParameterInfo[parameters.Length - 1];
        bool hasRemainder = false;
        for (int i = 1; i < parameters.Length; i++)
        {
            var p = parameters[i];
            bool remainder = p.GetCustomAttribute<RemainderAttribute>() != null;
            if (remainder)
            {
                if (i != parameters.Length - 1 || p.ParameterType != typeof(string))
                {
                    Plugin.Logger?.LogWarning($"Commands: {type.Name}.{method.Name} [{nameof(RemainderAttribute)}] must be the last parameter and of type string; skipped.");
                    return false;
                }
                hasRemainder = true;
            }
            else if (!CommandParser.IsSupportedType(p.ParameterType))
            {
                Plugin.Logger?.LogWarning($"Commands: {type.Name}.{method.Name} parameter '{p.Name}' has unsupported type {p.ParameterType.Name}; skipped.");
                return false;
            }
            args[i - 1] = p;
        }

        object? instance = null;
        if (!method.IsStatic)
        {
            if (type.IsAbstract)
            {
                Plugin.Logger?.LogWarning($"Commands: {type.Name}.{method.Name} is an instance method on an abstract type; skipped.");
                return false;
            }
            instance = instanceFactory();
            if (instance == null) return false;
        }

        overload = new CommandOverload(method, instance, attr, args, hasRemainder);
        return true;
    }

    internal static string ResolveOwnerId(object owner, string? ownerId)
    {
        if (!string.IsNullOrWhiteSpace(ownerId)) return ownerId.Trim();
        if (owner is BuiltInOwnerTag) return CommandRoutingSettings.BuiltInOwnerId;
        string? fromPlugin = TryReadPluginGuid(owner);
        if (!string.IsNullOrWhiteSpace(fromPlugin)) return fromPlugin!.Trim();
        return "anon-" + RuntimeHelpers.GetHashCode(owner).ToString("x8");
    }

    private static string? TryReadPluginGuid(object owner)
    {
        try
        {
            foreach (var attr in owner.GetType().GetCustomAttributes(inherit: true))
            {
                var t = attr.GetType();
                if (!string.Equals(t.Name, "BepInPlugin", StringComparison.Ordinal)
                    && !string.Equals(t.FullName, "BepInEx.BepInPlugin", StringComparison.Ordinal))
                    continue;
                var guid = t.GetProperty("GUID")?.GetValue(attr) as string;
                if (!string.IsNullOrWhiteSpace(guid)) return guid;
            }
        }
        catch
        {
            // Inference is best-effort; callers can pass ownerId.
        }
        return null;
    }

    internal static string DescribeOwner(object owner)
    {
        try
        {
            if (owner is BuiltInOwnerTag) return "Progenitor (built-in)";
            var text = owner.ToString();
            if (!string.IsNullOrEmpty(text) && text != owner.GetType().ToString()) return text!;
            return owner.GetType().FullName ?? owner.GetType().Name;
        }
        catch
        {
            return owner.GetType().Name;
        }
    }

    private sealed class BuiltInOwnerTag
    {
        public override string ToString() => "Progenitor (built-in)";
    }

    private sealed class RegistrationHandle : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Unregister(this);
        }
    }

    private sealed class CompositeHandle : IDisposable
    {
        private readonly List<IDisposable> _inner;
        private int _disposed;

        public CompositeHandle(List<IDisposable> inner) => _inner = inner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            for (int i = _inner.Count - 1; i >= 0; i--)
            {
                try { _inner[i].Dispose(); }
                catch (Exception e) { Plugin.Logger?.LogWarning($"Commands: registration dispose threw: {e.Message}"); }
            }
        }
    }
}

internal sealed class CommandBundle
{
    public readonly object Owner;
    public readonly string OwnerId;
    public readonly object Registration;
    public readonly string GroupName;
    public readonly string? GroupShortHand;
    public readonly string Name;
    public readonly List<string> Aliases;
    public readonly string? Description;
    public readonly List<CommandOverload> Overloads;
    public bool ShortcutsEnabled;
    public string EffectiveShortcut;

    public CommandBundle(
        object owner,
        string ownerId,
        object registration,
        string groupName,
        string? groupShortHand,
        string name,
        List<string> aliases,
        string? description,
        List<CommandOverload> overloads)
    {
        Owner = owner;
        OwnerId = ownerId;
        Registration = registration;
        GroupName = groupName;
        GroupShortHand = groupShortHand;
        Name = name;
        Aliases = aliases;
        Description = description;
        Overloads = overloads;
        EffectiveShortcut = name;
    }

    public string DisplayPath => string.IsNullOrEmpty(GroupName) ? Name : $"{GroupName} {Name}";

    public string QualifiedPath => string.IsNullOrEmpty(GroupName) ? $"{OwnerId} {Name}" : $"{OwnerId} {GroupName} {Name}";

    /// <summary>
    /// Whether <c>.help</c>, a group listing and the chat hints show this command: some overload
    /// the viewer may run (<see cref="CommandOverload.UsableBy"/>). <paramref name="listedAdmin"/>
    /// is the server's admin-list rule (<see cref="CommandAccess.ListedAdmin"/>); left false, an
    /// overload gated by that rule shows only to <paramref name="isAdmin"/>.
    /// </summary>
    public bool VisibleTo(bool isAdmin, bool listedAdmin = false)
    {
        foreach (var o in Overloads)
            if (o.UsableBy(isAdmin, listedAdmin)) return true;
        return false;
    }
}

internal sealed class CommandOverload
{
    public readonly MethodInfo Method;
    public readonly object? Instance;
    public readonly CommandAttribute Attribute;
    public readonly ParameterInfo[] Parameters;
    public readonly bool HasRemainder;

    public CommandOverload(
        MethodInfo method,
        object? instance,
        CommandAttribute attribute,
        ParameterInfo[] parameters,
        bool hasRemainder)
    {
        Method = method;
        Instance = instance;
        Attribute = attribute;
        Parameters = parameters;
        HasRemainder = hasRemainder;
        AdminListGate = method.GetCustomAttribute<AdminListGateAttribute>(inherit: false) != null;
    }

    public bool AdminOnly => Attribute.AdminOnly;

    /// <summary>Core only: the overload authorizes with the server's admin-list rule (<see cref="AdminListGateAttribute"/>).</summary>
    public readonly bool AdminListGate;

    /// <summary>
    /// May this viewer run the overload? An <see cref="AdminListGate"/> overload needs the server's
    /// admin-list rule (<paramref name="listedAdmin"/>; the game's admin flag and the host pass that
    /// rule too, so <paramref name="isAdmin"/> counts); an <see cref="AdminOnly"/> overload needs
    /// <paramref name="isAdmin"/>; anything else is open.
    /// </summary>
    public bool UsableBy(bool isAdmin, bool listedAdmin) =>
        AdminListGate ? isAdmin || listedAdmin
        : !AdminOnly || isAdmin;
}

/// <summary>
/// The server's admin-list rule for a command caller, the one <see cref="AdminListGateAttribute"/>
/// overloads use: <c>ServerAdminList.AuthorizeShared</c> (on the admin list, else authenticated with
/// <c>adminauth</c>, else the host of this private game).
/// </summary>
internal static class CommandAccess
{
    /// <summary>True when the caller passes the server's admin-list rule.</summary>
    internal static bool ListedAdmin(ICommandContext ctx) =>
        Progenitor.Install.ServerAdminList.AuthorizeShared(SteamIdOf(ctx), GameAdminOf(ctx)) != Progenitor.Install.RemoteAuthorizedBy.None;

    /// <summary><see cref="ListedAdmin"/> when one of <paramref name="bundle"/>'s overloads is gated by it; else false without reading the list.</summary>
    internal static bool ListedAdminFor(ICommandContext ctx, CommandBundle bundle)
    {
        foreach (var o in bundle.Overloads)
            if (o.AdminListGate) return ListedAdmin(ctx);
        return false;
    }

#if PROGENITOR_TESTS
    private static ulong SteamIdOf(ICommandContext ctx) => ctx.PlatformId;
    private static bool GameAdminOf(ICommandContext ctx) => ctx.IsAdmin;
#else
    private static ulong SteamIdOf(ICommandContext ctx) => ctx.User.PlatformId;
    private static bool GameAdminOf(ICommandContext ctx) => ctx.User.IsAdmin;
#endif
}
