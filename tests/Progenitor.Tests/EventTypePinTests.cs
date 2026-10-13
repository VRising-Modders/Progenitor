using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Progenitor.Config;
using Progenitor.Events;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// A Core static keyed by <see cref="Type"/> roots the assembly that declared the type. When that
/// assembly is a reloadable plugin (a collectible ALC), the plugin stays pinned after unload.
/// <c>EventBus</c> kept a <c>ByType</c> entry (and a nested-publish log note) for an event type
/// after its last subscription went, so a plugin that declared its own event type and unsubscribed
/// cleanly was still pinned. The settings bounds caches had the same shape for a plugin's
/// <c>AcceptableValueList&lt;PluginEnum&gt;</c>.
/// </summary>
[Collection("PluginLogger")]
public sealed class EventTypePinTests
{
    [Theory]
    [InlineData("dispose", false)]
    [InlineData("owner", false)]
    [InlineData("assembly", false)]
    [InlineData("dispose", true)]
    [InlineData("owner", true)]
    [InlineData("assembly", true)]
    [InlineData("self-dispose", true)]
    public void Removing_the_last_subscription_releases_a_collectible_event_type(string cleanup, bool nested)
    {
        WeakReference reference = SubscribeAndRelease(cleanup, nested);
        Collect(reference);
        Assert.False(reference.IsAlive, "EventBus retained the collectible event type after " + cleanup);
    }

    [Fact]
    public void A_remaining_subscription_keeps_its_event_type_registered()
    {
        var first = EventBus.On<SurvivingEvent>(_ => { });
        int calls = 0;
        var second = EventBus.On<SurvivingEvent>(_ => calls++);
        try
        {
            first.Dispose();
            Assert.Equal(1, EventBus.SubscriberCount<SurvivingEvent>());
            EventBus.Publish(new SurvivingEvent());
            Assert.Equal(1, calls);
        }
        finally
        {
            second.Dispose();
        }
        Assert.Equal(0, EventBus.SubscriberCount<SurvivingEvent>());
    }

    [Fact]
    public void A_generic_closed_over_a_collectible_type_is_itself_collectible()
    {
        // The bounds caches rely on Type.IsCollectible for AcceptableValueList<PluginEnum>.
        var (dynamicType, _) = DefineCollectibleType();
        Assert.True(dynamicType.IsCollectible);
        Assert.True(typeof(List<>).MakeGenericType(dynamicType).IsCollectible);
        Assert.False(typeof(List<int>).IsCollectible);
    }

    [Fact]
    public void Settings_bounds_caches_never_hold_a_collectible_type()
    {
        WeakReference reference = ResolveBoundsOfCollectibleType();
        Collect(reference);
        Assert.False(reference.IsAlive, "a bounds cache retained a collectible type");

        int synced = SyncedBounds.CachedTypeCount;
        int ui = SettingsBounds.CachedTypeCount;
        SyncedBounds.ResolveMembersForTests(typeof(List<string>));
        SettingsBounds.ResolveMembersForTests(typeof(List<string>));
        Assert.True(SyncedBounds.CachedTypeCount >= synced);
        Assert.True(SyncedBounds.CachedTypeCount <= synced + 1);
        Assert.True(SettingsBounds.CachedTypeCount >= ui);
        Assert.True(SettingsBounds.CachedTypeCount <= ui + 1);
    }

    /// <summary>
    /// Guard: every static <see cref="Type"/>-keyed collection in Core is reviewed for how its
    /// entries leave. A new one must either drop plugin types when they go, never hold one, or
    /// only ever hold game types; add it here with that reason.
    /// </summary>
    private static readonly Dictionary<string, string> ReviewedTypeKeyedStatics = new(StringComparer.Ordinal)
    {
        [Key("Events", "EventBus.cs", "ByType")] = "the entry goes with the type's last subscription",
        [Key("Events", "EventBus.cs", "NestedLogged")] = "cleared with the type's ByType entry and after the outer publish",
        [Key("Events", "EventBus.cs", "Publishing")] = "each publish removes its type in finally",
        [Key("Config", "SyncedBounds.cs", "Cache")] = "collectible types are not cached",
        [Key("Ui", "SettingsRow.cs", "Cache")] = "collectible types are not cached",
        [Key("Content", "PrefabEditShapeReflect.cs", "Cache")] = "ECS component types only; cleared each Core generation",
        [Key("Ecs", "ComponentIndex.cs", "ByManagedType")] = "IL2CPP interop component types only",
        [Key("Hooks", "SystemHooks.cs", "IndexByType")] = "game system types only",
        [Key("Hooks", "SystemHooks.cs", "Reported")] = "game system types only",
        [Key("Networking", "ModMessageCodec.cs", "Customs")] = "Core registers game interop scalars only (PrefabGUID); collectible types are refused; cleared at Core Uninstall",
    };

    private static readonly Regex TypeKeyedStatic = new(
        @"\bstatic\s+(?:readonly\s+)?(?:Dictionary|HashSet|ConcurrentDictionary|List|ConditionalWeakTable|SortedDictionary)<\s*Type\b[^(;=]*?\??\s+(\w+)\s*[;=]",
        RegexOptions.Compiled);

    [Fact]
    public void Core_static_Type_keyed_collections_are_reviewed_for_plugin_pins()
    {
        string root = Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Core");
        var found = new HashSet<string>(StringComparer.Ordinal);
        var unreviewed = new List<string>();
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = path.Substring(root.Length + 1);
            string rooted = Path.DirectorySeparatorChar + relative;
            if (rooted.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || rooted.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                var match = TypeKeyedStatic.Match(trimmed);
                if (!match.Success) continue;
                string key = relative + "#" + match.Groups[1].Value;
                found.Add(key);
                if (!ReviewedTypeKeyedStatics.ContainsKey(key))
                    unreviewed.Add(relative + ":" + (i + 1) + ": " + trimmed);
            }
        }

        Assert.True(unreviewed.Count == 0,
            "A static collection keyed by Type roots a plugin's collectible ALC unless its entries leave "
            + "with the plugin. Review it and add it to ReviewedTypeKeyedStatics:\n" + string.Join("\n", unreviewed));
        // The scan must still see the fields it was written against.
        Assert.Contains(Key("Events", "EventBus.cs", "ByType"), found);
        Assert.Contains(Key("Config", "SyncedBounds.cs", "Cache"), found);
    }

    private static string Key(string folder, string file, string field) =>
        Path.Combine(folder, file) + "#" + field;

    private sealed class SurvivingEvent { }

    private static void Collect(WeakReference reference)
    {
        for (int attempt = 0; attempt < 12 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static (Type type, ModuleBuilder module) DefineCollectibleType()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("EventTypePin_" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect);
        var module = assembly.DefineDynamicModule("Pins");
        Type type = module.DefineType("OwnedEvent", TypeAttributes.Public | TypeAttributes.Class).CreateType()!;
        return (type, module);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ResolveBoundsOfCollectibleType()
    {
        var (type, _) = DefineCollectibleType();
        var closed = typeof(List<>).MakeGenericType(type);
        SyncedBounds.ResolveMembersForTests(closed);
        SettingsBounds.ResolveMembersForTests(closed);
        return new WeakReference(type);
    }

    /// <summary>
    /// A plugin-declared event type in a collectible assembly, with a handler declared there too,
    /// optionally republishing itself (the nested-publish log note) or disposing its own
    /// subscription mid-publish.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SubscribeAndRelease(string cleanup, bool nested)
    {
        var (eventType, module) = DefineCollectibleType();
        var owner = new object();
        var handlerBuilder = module.DefineType("OwnedHandler", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var subscriptionField = handlerBuilder.DefineField("Subscription", typeof(IDisposable), FieldAttributes.Public | FieldAttributes.Static);
        var handleMethod = handlerBuilder.DefineMethod("Handle", MethodAttributes.Public | MethodAttributes.Static, typeof(void), new[] { eventType });
        var il = handleMethod.GetILGenerator();
        if (cleanup == "self-dispose")
        {
            il.Emit(OpCodes.Ldsfld, subscriptionField);
            il.Emit(OpCodes.Callvirt, typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose))!);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Stsfld, subscriptionField);
        }
        if (nested)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(EventBus).GetMethod(nameof(EventBus.Publish))!.MakeGenericMethod(eventType));
            il.Emit(OpCodes.Pop);
        }
        il.Emit(OpCodes.Ret);
        var handlerType = handlerBuilder.CreateType()!;
        var handler = handlerType.GetMethod("Handle")!.CreateDelegate(typeof(Action<>).MakeGenericType(eventType));
        var on = typeof(EventBus).GetMethod(nameof(EventBus.On))!.MakeGenericMethod(eventType);
        var subscription = (IDisposable)on.Invoke(null, new object?[] { handler, 0, owner, null, null, "test.collectible-event" })!;
        handlerType.GetField("Subscription")!.SetValue(null, subscription);
        if (nested)
            typeof(EventBus).GetMethod(nameof(EventBus.Publish))!.MakeGenericMethod(eventType)
                .Invoke(null, new[] { Activator.CreateInstance(eventType) });
        if (cleanup == "owner") EventBus.RemoveAll(owner);
        else if (cleanup == "assembly") EventBus.RemoveDeclaredIn(eventType.Assembly);
        else if (cleanup != "self-dispose") subscription.Dispose();
        handlerType.GetField("Subscription")!.SetValue(null, null);
        return new WeakReference(eventType);
    }
}
