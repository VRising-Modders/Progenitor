using System;
using Progenitor;
using Progenitor.Bootstrap;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of step D: Bootstrap-owned Harmony slots, the diagnostic skip, and
/// Core's fallback-vs-fill decision. No HarmonyLib — the trampolines live in Bootstrap.
/// </summary>
[Collection("HarmonySlotHandoff")]
public class HarmonySlotHandoffTests : IDisposable
{
    private static readonly object Gate = new();

    public HarmonySlotHandoffTests()
    {
        HarmonySlotHandoff.Use(null);
    }

    public void Dispose()
    {
        HarmonySlotHandoff.Use(null);
    }

    private static void Nop() { }

    [Fact]
    public void Site_names_match_policy_and_handoff()
    {
        Assert.Equal(HarmonySlotPolicy.SiteCount, HarmonySlotHandoff.SiteCount);
        Assert.Equal(HarmonySlotPolicy.SiteCount, HarmonySlotPolicy.AllSites.Length);
        Assert.Equal(HarmonySlotPolicy.SiteCount, HarmonySlotHandoff.AllSites.Length);
        Assert.Equal(HarmonySlotPolicy.SystemBaseUpdate, HarmonySlotHandoff.SystemBaseUpdate);
        Assert.Equal(HarmonySlotPolicy.UnmanagedUpdate, HarmonySlotHandoff.UnmanagedUpdate);
        Assert.Equal(HarmonySlotPolicy.ChatOnUpdate, HarmonySlotHandoff.ChatOnUpdate);
        Assert.Equal(HarmonySlotPolicy.UserConnected, HarmonySlotHandoff.UserConnected);
        Assert.Equal(HarmonySlotPolicy.UserDisconnected, HarmonySlotHandoff.UserDisconnected);
        Assert.Equal(HarmonySlotPolicy.DeserializeLoadFile, HarmonySlotHandoff.DeserializeLoadFile);
        Assert.Equal(HarmonySlotPolicy.TriggerSave, HarmonySlotHandoff.TriggerSave);
        Assert.Equal(HarmonySlotPolicy.BeginCreateServerWorld, HarmonySlotHandoff.BeginCreateServerWorld);
        Assert.Equal(HarmonySlotPolicy.StunProcessStart, HarmonySlotHandoff.StunProcessStart);
        Assert.Equal(HarmonySlotPolicy.SetLanguage, HarmonySlotHandoff.SetLanguage);
        Assert.Equal(HarmonySlotPolicy.BootstrapSystemOnUpdate, HarmonySlotHandoff.BootstrapSystemOnUpdate);
        Assert.Equal(HarmonySlotPolicy.SystemBaseUpdatePrefix, HarmonySlotHandoff.SystemBaseUpdatePrefix);
        Assert.Equal(HarmonySlotPolicy.StunProcessStartFinalizer, HarmonySlotHandoff.StunProcessStartFinalizer);
        Assert.Equal(HarmonySlotHandoff.FallbackLog, HarmonySlotPolicy.FallbackLog);
        Assert.Equal(HarmonySlotPolicy.SkipLog("X"), HarmonySlotHandoff.SkipLog("X"));
        Assert.True(HarmonySlotPolicy.IsKnownSlot(HarmonySlotPolicy.SystemBaseUpdatePrefix));
        Assert.False(HarmonySlotPolicy.IsKnownSlot("not-a-site"));
        Assert.False(HarmonySlotPolicy.IsKnownSlot(null));
    }

    [Fact]
    public void Skip_switch_blocks_fill_and_self_patch()
    {
        Assert.False(HarmonySlotPolicy.CoreMayApplyHarmony(hostOwnsSite: true, skipCoreHarmony: true));
        Assert.False(HarmonySlotPolicy.CoreMayFillSlot(hostOwnsSite: true, skipCoreHarmony: true));
        Assert.False(HarmonySlotPolicy.CoreMayApplyHarmony(hostOwnsSite: false, skipCoreHarmony: true));
        Assert.False(HarmonySlotPolicy.CoreMayFillSlot(hostOwnsSite: false, skipCoreHarmony: true));
        Assert.Equal(
            HarmonySlotPolicy.CoreMayApplyHarmony(true, true),
            HarmonySlotHandoff.CoreMayApplyHarmony(true, true));
        Assert.Equal(
            HarmonySlotPolicy.CoreMayFillSlot(true, true),
            HarmonySlotHandoff.CoreMayFillSlot(true, true));
        Assert.Equal("Diagnostics.SkipCoreHarmony: skipped Harmony site SystemBase.Update", HarmonySlotPolicy.SkipLog(HarmonySlotPolicy.SystemBaseUpdate));
        Assert.Equal(
            "Diagnostics.SkipCoreHarmony=true: Core will install none of its Harmony patches. Tick stays (Bootstrap).",
            HarmonySlotPolicy.SkipEnabledLog);
        Assert.Equal(HarmonySlotPolicy.SkipEnabledLog, HarmonySlotHandoff.SkipEnabledLog);
        Assert.Equal(HarmonySlotPolicy.SkipTickStaysLog, HarmonySlotHandoff.SkipTickStaysLog);
        Assert.Equal(HarmonySlotPolicy.SkipRefusedLiveWorldLog, HarmonySlotHandoff.SkipRefusedLiveWorldLog);
        Assert.Equal(HarmonySlotPolicy.ContentSites, HarmonySlotHandoff.ContentSites);
        Assert.DoesNotContain(HarmonySlotPolicy.BootstrapSystemOnUpdate, HarmonySlotPolicy.ContentSites);
        Assert.Contains("Commands never see", HarmonySlotPolicy.SkipSiteEffect(HarmonySlotPolicy.ChatOnUpdate));
        Assert.Contains("Tick / reload / bridge", HarmonySlotPolicy.SkipSiteEffect(HarmonySlotPolicy.BootstrapSystemOnUpdate));
        Assert.Contains("DOORSTOP", HarmonySlotPolicy.SkipSiteEffect(HarmonySlotPolicy.StunProcessStart));
    }

    [Fact]
    public void Host_owns_patches_Core_fills_does_not_self_patch()
    {
        Assert.True(HarmonySlotPolicy.CoreMayFillSlot(hostOwnsSite: true, skipCoreHarmony: false));
        Assert.False(HarmonySlotPolicy.CoreMayApplyHarmony(hostOwnsSite: true, skipCoreHarmony: false));
    }

    [Fact]
    public void Absent_host_Core_self_patches()
    {
        Assert.False(HarmonySlotPolicy.CoreMayFillSlot(hostOwnsSite: false, skipCoreHarmony: false));
        Assert.True(HarmonySlotPolicy.CoreMayApplyHarmony(hostOwnsSite: false, skipCoreHarmony: false));
        Assert.Equal(
            "Bootstrap Harmony slots absent: Core applying Harmony patches itself.",
            HarmonySlotPolicy.FallbackLog);
        Assert.Equal(
            "Bootstrap Harmony slots absent: Core applying Harmony patch on ChatMessageSystem.OnUpdate",
            HarmonySlotPolicy.FallbackSiteLog(HarmonySlotPolicy.ChatOnUpdate));
    }

    [Fact]
    public void Prefix_empty_or_throw_runs_original()
    {
        Assert.True(HarmonySlotPolicy.PrefixRunOriginal(slotPresent: false, slotReturned: null, slotThrew: false));
        Assert.True(HarmonySlotPolicy.PrefixRunOriginal(slotPresent: true, slotReturned: null, slotThrew: true));
        Assert.True(HarmonySlotPolicy.PrefixRunOriginal(slotPresent: true, slotReturned: true, slotThrew: false));
        Assert.False(HarmonySlotPolicy.PrefixRunOriginal(slotPresent: true, slotReturned: false, slotThrew: false));
        Assert.True(HarmonySlotPolicy.PrefixRunOriginal(slotPresent: true, slotReturned: false, slotThrew: true));
    }

    [Fact]
    public void Absent_host_TrySet_returns_false()
    {
        lock (Gate)
        {
            HarmonySlotHandoff.Use(null);
            Assert.False(HarmonySlotHandoff.HostOwnsPatches);
            Assert.False(HarmonySlotHandoff.SkipCoreHarmony);
            Assert.False(HarmonySlotHandoff.TrySet(HarmonySlotHandoff.ChatOnUpdate, new Action(Nop)));
            Assert.True(HarmonySlotHandoff.CoreMayApplyHarmony(HarmonySlotHandoff.HostOwnsPatches, HarmonySlotHandoff.SkipCoreHarmony));
        }
    }

    [Fact]
    public void Skip_on_fake_refuses_TrySet()
    {
        lock (Gate)
        {
            var fake = new HarmonySlotHandoff.FakeSlot { SkipCoreHarmony = true };
            HarmonySlotHandoff.Use(fake);
            try
            {
                Assert.True(HarmonySlotHandoff.HostOwnsPatches);
                Assert.True(HarmonySlotHandoff.SkipCoreHarmony);
                Assert.False(HarmonySlotHandoff.TrySet(HarmonySlotHandoff.ChatOnUpdate, new Action(Nop)));
                Assert.Null(fake.Get(HarmonySlotHandoff.ChatOnUpdate));
                Assert.False(HarmonySlotHandoff.CoreMayApplyHarmony(true, true));
                Assert.False(HarmonySlotHandoff.CoreMayFillSlot(true, true));
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void Slot_install_clear_reinstall()
    {
        lock (Gate)
        {
            var fake = new HarmonySlotHandoff.FakeSlot();
            HarmonySlotHandoff.Use(fake);
            try
            {
                var first = new Action(Nop);
                Assert.True(HarmonySlotHandoff.HostOwnsPatches);
                Assert.True(HarmonySlotHandoff.TrySet(HarmonySlotHandoff.ChatOnUpdate, first));
                Assert.Same(first, fake.Get(HarmonySlotHandoff.ChatOnUpdate));

                HarmonySlotHandoff.Clear(HarmonySlotHandoff.ChatOnUpdate);
                Assert.Null(fake.Get(HarmonySlotHandoff.ChatOnUpdate));

                var second = new Action(Nop);
                Assert.True(HarmonySlotHandoff.TrySet(HarmonySlotHandoff.ChatOnUpdate, second));
                Assert.Same(second, fake.Get(HarmonySlotHandoff.ChatOnUpdate));

                var language = new Action<string>(_ => { });
                Assert.True(HarmonySlotHandoff.TrySet(HarmonySlotHandoff.SetLanguage, language));
                Assert.Same(language, fake.Get(HarmonySlotHandoff.SetLanguage));

                HarmonySlotHandoff.ClearAll();
                Assert.Null(fake.Get(HarmonySlotHandoff.ChatOnUpdate));
                Assert.Null(fake.Get(HarmonySlotHandoff.SetLanguage));
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void Host_owns_but_write_refused_stays_inert()
    {
        lock (Gate)
        {
            var fake = new HarmonySlotHandoff.FakeSlot { AcceptWrites = false };
            HarmonySlotHandoff.Use(fake);
            try
            {
                Assert.True(HarmonySlotHandoff.HostOwnsPatches);
                Assert.False(HarmonySlotHandoff.TrySet(HarmonySlotHandoff.TriggerSave, new Action<bool>(_ => { })));
                Assert.False(HarmonySlotHandoff.CoreMayApplyHarmony(true, false));
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void CanonicalSite_maps_slots_onto_originals()
    {
        Assert.Equal(HarmonySlotPolicy.SystemBaseUpdate, HarmonySlotPolicy.CanonicalSite(HarmonySlotPolicy.SystemBaseUpdatePrefix));
        Assert.Equal(HarmonySlotPolicy.SystemBaseUpdate, HarmonySlotHandoff.CanonicalSite(HarmonySlotHandoff.SystemBaseUpdatePostfix));
        Assert.Equal(HarmonySlotPolicy.StunProcessStart, HarmonySlotPolicy.CanonicalSite(HarmonySlotPolicy.StunProcessStartFinalizer));
        Assert.Equal(HarmonySlotPolicy.StunProcessStart, HarmonySlotPolicy.CanonicalSite(HarmonySlotPolicy.StunProcessMonoStart));
        Assert.Equal("StunProcess_MONO$$Start", HarmonySlotPolicy.StunProcessMonoStart);
        Assert.True(HarmonySlotPolicy.IsKnownSlot(HarmonySlotPolicy.StunProcessMonoStart));
        Assert.Equal(HarmonySlotPolicy.ChatOnUpdate, HarmonySlotPolicy.CanonicalSite(HarmonySlotPolicy.ChatOnUpdate));
        Assert.Equal(HarmonySlotPolicy.ChatOnUpdate, HarmonySlotPolicy.FillGroup(HarmonySlotPolicy.ChatOnUpdate));
        Assert.Equal(HarmonySlotHandoff.UserDisconnected, HarmonySlotHandoff.FillGroup(HarmonySlotHandoff.UserDisconnected));
        Assert.Equal(HarmonySlotHandoff.ChatOnUpdate, HarmonySlotHandoff.FillGroup(HarmonySlotHandoff.ChatOnUpdate));
        Assert.Equal(HarmonySlotHandoff.TriggerSave, HarmonySlotHandoff.FillGroup(HarmonySlotHandoff.TriggerSave));
        Assert.Equal(HarmonySlotHandoff.BeginCreateServerWorld, HarmonySlotHandoff.FillGroup(HarmonySlotHandoff.BeginCreateServerWorld));
        Assert.Equal(HarmonySlotPolicy.TriggerSave, HarmonySlotPolicy.FillGroup(HarmonySlotPolicy.TriggerSave));
        Assert.Equal(HarmonySlotPolicy.SystemBaseUpdatePrefix, HarmonySlotPolicy.FillGroup(HarmonySlotPolicy.SystemBaseUpdatePrefix));
        Assert.Equal(HarmonySlotHandoff.SystemBaseUpdatePrefix, HarmonySlotHandoff.FillGroup(HarmonySlotHandoff.SystemBaseUpdatePrefix));
        Assert.Equal(
            HarmonySlotPolicy.FillGroup(HarmonySlotPolicy.ChatOnUpdate),
            HarmonySlotHandoff.FillGroup(HarmonySlotHandoff.ChatOnUpdate));
        Assert.Equal(
            HarmonySlotPolicy.FillGroup(HarmonySlotPolicy.SystemBaseUpdatePrefix),
            HarmonySlotHandoff.FillGroup(HarmonySlotHandoff.SystemBaseUpdatePrefix));
        Assert.Equal("", HarmonySlotPolicy.FillGroup(null));
        Assert.Equal("", HarmonySlotHandoff.FillGroup(null));
    }

    [Fact]
    public void Per_site_ownership_refuses_unpatched_site_and_allows_fallback()
    {
        lock (Gate)
        {
            var fake = new HarmonySlotHandoff.FakeSlot
            {
                OwnedSites = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                {
                    HarmonySlotHandoff.ChatOnUpdate,
                },
            };
            HarmonySlotHandoff.Use(fake);
            try
            {
                Assert.True(HarmonySlotHandoff.HostOwnsSite(HarmonySlotHandoff.ChatOnUpdate));
                Assert.False(HarmonySlotHandoff.HostOwnsSite(HarmonySlotHandoff.TriggerSave));
                Assert.True(HarmonySlotHandoff.TrySet(HarmonySlotHandoff.ChatOnUpdate, new Action(Nop)));
                Assert.False(HarmonySlotHandoff.TrySet(HarmonySlotHandoff.TriggerSave, new Action<bool>(_ => { })));
                Assert.Null(fake.Get(HarmonySlotHandoff.TriggerSave));
                Assert.True(HarmonySlotHandoff.CoreMayApplyHarmony(
                    HarmonySlotHandoff.HostOwnsSite(HarmonySlotHandoff.TriggerSave),
                    skipCoreHarmony: false));
                Assert.False(HarmonySlotHandoff.CoreMayApplyHarmony(
                    HarmonySlotHandoff.HostOwnsSite(HarmonySlotHandoff.ChatOnUpdate),
                    skipCoreHarmony: false));
                Assert.Equal(HarmonySlotPolicy.VanillaSiteLog(HarmonySlotPolicy.TriggerSave), HarmonySlotPolicy.VanillaSiteLog("TriggerPersistenceSaveSystem.TriggerSave"));
                Assert.Contains("OwnsPatches=false", HarmonySlotPolicy.OwnsPatchesNoneLog);
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void TryFill_does_not_roll_back_a_sibling_site()
    {
        lock (Gate)
        {
            var fake = new HarmonySlotHandoff.FakeSlot();
            HarmonySlotHandoff.Use(fake);
            try
            {
                var chat = new Action(Nop);
                Assert.True(HarmonySlotHandoff.TryFill(HarmonySlotHandoff.ChatOnUpdate, chat));
                Assert.Same(chat, fake.Get(HarmonySlotHandoff.ChatOnUpdate));

                fake.RemainingWrites = 0;
                Assert.False(HarmonySlotHandoff.TryFill(
                    HarmonySlotHandoff.UserConnected,
                    new Action<object, object>((_, _) => { })));
                Assert.Same(chat, fake.Get(HarmonySlotHandoff.ChatOnUpdate));
                Assert.Null(fake.Get(HarmonySlotHandoff.UserConnected));
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void Bind_fills_owned_site_and_fallbacks_unowned()
    {
        lock (Gate)
        {
            var fake = new HarmonySlotHandoff.FakeSlot
            {
                OwnedSites = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                {
                    HarmonySlotHandoff.ChatOnUpdate,
                },
            };
            HarmonySlotHandoff.Use(fake);
            try
            {
                var chat = new Action(Nop);
                Assert.Equal(
                    HarmonySlotHandoff.SiteBind.Filled,
                    HarmonySlotHandoff.Bind(HarmonySlotHandoff.ChatOnUpdate, chat));
                Assert.Same(chat, fake.Get(HarmonySlotHandoff.ChatOnUpdate));
                Assert.Equal(
                    HarmonySlotHandoff.SiteBind.Fallback,
                    HarmonySlotHandoff.Bind(HarmonySlotHandoff.UserConnected, new Action<object, object>((_, _) => { })));
                Assert.Null(fake.Get(HarmonySlotHandoff.UserConnected));
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void Bind_skip_does_not_fill()
    {
        lock (Gate)
        {
            var fake = new HarmonySlotHandoff.FakeSlot { SkipCoreHarmony = true };
            HarmonySlotHandoff.Use(fake);
            try
            {
                Assert.Equal(
                    HarmonySlotHandoff.SiteBind.Skip,
                    HarmonySlotHandoff.Bind(HarmonySlotHandoff.ChatOnUpdate, new Action(Nop)));
                Assert.Null(fake.Get(HarmonySlotHandoff.ChatOnUpdate));
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void Bind_owned_write_failure_is_inert_not_fallback()
    {
        lock (Gate)
        {
            var fake = new HarmonySlotHandoff.FakeSlot { AcceptWrites = false };
            HarmonySlotHandoff.Use(fake);
            try
            {
                Assert.Equal(
                    HarmonySlotHandoff.SiteBind.Inert,
                    HarmonySlotHandoff.Bind(HarmonySlotHandoff.TriggerSave, new Action<bool>(_ => { })));
                Assert.Contains("staying inert for this site", HarmonySlotHandoff.InertLog("ModSave", HarmonySlotHandoff.TriggerSave));
                Assert.Contains("is vanilla", HarmonySlotHandoff.VanillaLog("ModSave", HarmonySlotHandoff.TriggerSave));
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void TryFillAll_is_all_or_nothing()
    {
        lock (Gate)
        {
            var fake = new HarmonySlotHandoff.FakeSlot();
            HarmonySlotHandoff.Use(fake);
            try
            {
                fake.RemainingWrites = 1;
                Assert.False(HarmonySlotHandoff.TryFillAll(
                    new[] { HarmonySlotHandoff.SystemBaseUpdatePrefix, HarmonySlotHandoff.SystemBaseUpdatePostfix },
                    new Delegate[] { new Action(Nop), new Action(Nop) }));
                Assert.Null(fake.Get(HarmonySlotHandoff.SystemBaseUpdatePrefix));
                Assert.Null(fake.Get(HarmonySlotHandoff.SystemBaseUpdatePostfix));
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void Skip_refused_on_live_world_turns_skip_off()
    {
        lock (Gate)
        {
            var fake = new HarmonySlotHandoff.FakeSlot { SkipCoreHarmony = true };
            HarmonySlotHandoff.Use(fake);
            try
            {
                Assert.True(HarmonySlotHandoff.SkipCoreHarmony);
                HarmonySlotHandoff.SkipRefusedLiveWorld = true;
                Assert.False(HarmonySlotHandoff.SkipCoreHarmony);
                Assert.True(HarmonySlotHandoff.TrySet(HarmonySlotHandoff.ChatOnUpdate, new Action(Nop)));
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void Plugin_sites_match_policy_and_are_lazy()
    {
        Assert.Equal(HarmonySlotPolicy.PluginSiteCount, HarmonySlotPolicy.PluginSites.Length);
        Assert.Equal(HarmonySlotPolicy.PluginSites, HarmonySlotHandoff.PluginSites);
        Assert.Equal(HarmonySlotPolicy.PluginSlotNames, HarmonySlotHandoff.PluginSlotNames);
        Assert.Equal(HarmonySlotPolicy.LaunchGame, HarmonySlotHandoff.LaunchGame);
        Assert.Equal(HarmonySlotPolicy.Authenticate, HarmonySlotHandoff.Authenticate);
        Assert.Equal(HarmonySlotPolicy.ClientBootstrapConnect, HarmonySlotPolicy.CanonicalSite(HarmonySlotPolicy.ClientBootstrapConnectPrefix));
        Assert.Equal(HarmonySlotHandoff.ClientBootstrapConnect, HarmonySlotHandoff.CanonicalSite(HarmonySlotHandoff.ClientBootstrapConnectPostfix));
        Assert.True(HarmonySlotPolicy.IsLazySite(HarmonySlotPolicy.LaunchGame));
        Assert.True(HarmonySlotPolicy.IsPluginSlot(HarmonySlotPolicy.SendPacketToUser));
        Assert.True(HarmonySlotPolicy.IsKnownSlot(HarmonySlotPolicy.GetAuthSessionTicket));
        Assert.False(HarmonySlotPolicy.IsLazySite(HarmonySlotPolicy.ChatOnUpdate));
        Assert.True(HarmonySlotPolicy.PluginMayFillSlot(hostOwnsSite: true));
        Assert.False(HarmonySlotPolicy.PluginMayFillSlot(hostOwnsSite: false));
        Assert.True(HarmonySlotPolicy.PrefixRunOriginal(slotPresent: false, slotReturned: null, slotThrew: false));
    }

    [Fact]
    public void Plugin_bind_clear_and_empty_slot_are_vanilla_and_ignore_skip()
    {
        lock (Gate)
        {
            var fake = new HarmonySlotHandoff.FakeSlot { SkipCoreHarmony = true };
            HarmonySlotHandoff.Use(fake);
            try
            {
                Assert.True(HarmonySlotHandoff.SkipCoreHarmony);
                Assert.False(HarmonySlotHandoff.TrySet(HarmonySlotHandoff.ChatOnUpdate, new Action(Nop)));
                Assert.Null(fake.Get(HarmonySlotHandoff.LaunchGame));

                var first = new Action(Nop);
                Assert.True(HarmonySlotHandoff.TryBindPlugin(HarmonySlotHandoff.LaunchGame, first));
                Assert.Same(first, fake.Get(HarmonySlotHandoff.LaunchGame));

                HarmonySlotHandoff.ClearPlugin(HarmonySlotHandoff.LaunchGame);
                Assert.Null(fake.Get(HarmonySlotHandoff.LaunchGame));

                var second = new Action(Nop);
                using (var lease = HarmonySlotHandoff.BindPlugin(HarmonySlotHandoff.SendPacketToUser, second))
                {
                    Assert.NotNull(lease);
                    Assert.Same(second, fake.Get(HarmonySlotHandoff.SendPacketToUser));
                }
                Assert.Null(fake.Get(HarmonySlotHandoff.SendPacketToUser));

                Assert.True(HarmonySlotHandoff.TryBindPlugin(HarmonySlotHandoff.Authenticate, first));
                HarmonySlotHandoff.ClearAll();
                Assert.Null(fake.Get(HarmonySlotHandoff.Authenticate));
                Assert.Null(fake.Get(HarmonySlotHandoff.LaunchGame));
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void Plugin_bind_refuses_unowned_or_inert_site()
    {
        lock (Gate)
        {
            var fake = new HarmonySlotHandoff.FakeSlot
            {
                OwnedSites = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                {
                    HarmonySlotHandoff.LaunchGame,
                },
            };
            HarmonySlotHandoff.Use(fake);
            try
            {
                Assert.True(HarmonySlotHandoff.TryBindPlugin(HarmonySlotHandoff.LaunchGame, new Action(Nop)));
                Assert.False(HarmonySlotHandoff.TryBindPlugin(HarmonySlotHandoff.Authenticate, new Action(Nop)));
                Assert.Null(fake.Get(HarmonySlotHandoff.Authenticate));
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }

            var inert = new HarmonySlotHandoff.FakeSlot { AcceptWrites = false };
            HarmonySlotHandoff.Use(inert);
            try
            {
                Assert.False(HarmonySlotHandoff.TryBindPlugin(HarmonySlotHandoff.LaunchGame, new Action(Nop)));
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void Plugin_bind_refuses_a_second_owner_and_ClearPlugin_requires_the_same_owner()
    {
        lock (Gate)
        {
            var fake = new HarmonySlotHandoff.FakeSlot();
            HarmonySlotHandoff.Use(fake);
            try
            {
                var first = new Action(Nop);
                Assert.True(HarmonySlotHandoff.TryBindPlugin(HarmonySlotHandoff.LaunchGame, first, "plugin-a"));
                Assert.Same(first, fake.Get(HarmonySlotHandoff.LaunchGame));
                Assert.Equal("plugin-a", fake.Owners[HarmonySlotHandoff.LaunchGame]);

                Assert.False(HarmonySlotHandoff.TryBindPlugin(HarmonySlotHandoff.LaunchGame, new Action(Nop), "plugin-b"));
                Assert.Same(first, fake.Get(HarmonySlotHandoff.LaunchGame));
                Assert.Equal(
                    "HarmonySlots: plugin slot 'GameHelper.LaunchGame' is bound by plugin-a; refused bind by plugin-b.",
                    HarmonySlotPolicy.FormatOwnerRefused(HarmonySlotHandoff.LaunchGame, "plugin-a", "plugin-b"));

                HarmonySlotHandoff.ClearPlugin(HarmonySlotHandoff.LaunchGame, "plugin-b");
                Assert.Same(first, fake.Get(HarmonySlotHandoff.LaunchGame));
                Assert.Equal(
                    "HarmonySlots: plugin slot 'GameHelper.LaunchGame' is bound by plugin-a; refused clear by plugin-b.",
                    HarmonySlotPolicy.FormatClearOwnerRefused(HarmonySlotHandoff.LaunchGame, "plugin-a", "plugin-b"));

                HarmonySlotHandoff.ClearPlugin(HarmonySlotHandoff.LaunchGame, "plugin-a");
                Assert.Null(fake.Get(HarmonySlotHandoff.LaunchGame));
                Assert.False(fake.Owners.ContainsKey(HarmonySlotHandoff.LaunchGame));

                Assert.True(HarmonySlotPolicy.MayBindPlugin(null, "plugin-a"));
                Assert.False(HarmonySlotPolicy.MayBindPlugin("plugin-a", "plugin-b"));
                Assert.True(HarmonySlotPolicy.MayBindPlugin("plugin-a", "plugin-a"));
                Assert.False(HarmonySlotPolicy.MayBindPlugin(null, ""));
                Assert.False(HarmonySlotPolicy.MayBindPlugin(null, null));
                Assert.False(HarmonySlotPolicy.MayBindPlugin("plugin-a", ""));
                Assert.Equal(
                    "HarmonySlots: plugin slot 'GameHelper.LaunchGame' refused bind with empty owner.",
                    HarmonySlotPolicy.FormatEmptyOwnerRefused(HarmonySlotHandoff.LaunchGame));
                Assert.False(HarmonySlotPolicy.MayClearPlugin("plugin-a", "plugin-b"));
                Assert.True(HarmonySlotPolicy.MayClearPlugin("plugin-a", "plugin-a"));
                Assert.True(HarmonySlotPolicy.MayClearPlugin("plugin-a", null));
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void ReloaderHost_ClearOwned_drops_every_slot_for_that_plugin()
    {
        lock (Gate)
        {
            var fake = new HarmonySlotHandoff.FakeSlot();
            HarmonySlotHandoff.Use(fake);
            try
            {
                Assert.True(HarmonySlotHandoff.TryBindPlugin(HarmonySlotHandoff.LaunchGame, new Action(Nop), "progenitor-reload:Dev:1"));
                Assert.True(HarmonySlotHandoff.TryBindPlugin(HarmonySlotHandoff.SendPacketToUser, new Action(Nop), "progenitor-reload:Dev:1"));
                Assert.True(HarmonySlotHandoff.TryBindPlugin(HarmonySlotHandoff.Authenticate, new Action(Nop), "progenitor-reload:Sample:1"));

                HarmonySlotHandoff.ClearOwned("progenitor-reload:Dev:1");
                Assert.Null(fake.Get(HarmonySlotHandoff.LaunchGame));
                Assert.Null(fake.Get(HarmonySlotHandoff.SendPacketToUser));
                Assert.NotNull(fake.Get(HarmonySlotHandoff.Authenticate));

                HarmonySlotHandoff.ClearPluginContext(assembly: null, alcName: "progenitor-reload:Sample:1");
                Assert.Null(fake.Get(HarmonySlotHandoff.Authenticate));
                Assert.True(HarmonySlotPolicy.OwnerMatchesUnload("progenitor-reload:Dev:1", "progenitor-reload:Dev:1", "Progenitor.Dev"));
                Assert.False(HarmonySlotPolicy.OwnerMatchesUnload("progenitor-reload:Dev:1", "progenitor-reload:Sample:1", "Progenitor.Sample"));
                Assert.Equal(
                    "HarmonySlots: cleared 2 plugin slot(s) owned by progenitor-reload:Dev:1.",
                    HarmonySlotPolicy.FormatClearedOwned("progenitor-reload:Dev:1", 2));
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void Collectible_path_sources_do_not_mention_HarmonyLib()
    {
        string root = FindRepoRoot();
        string[] files =
        {
            System.IO.Path.Combine(root, "src", "Progenitor.Core", "HarmonySlotHandoff.cs"),
            System.IO.Path.Combine(root, "src", "Progenitor.Core", "Runtime.cs"),
            System.IO.Path.Combine(root, "src", "Progenitor.Core", "BootstrapLink.cs"),
            System.IO.Path.Combine(root, "src", "Progenitor.Core", "Plugin.cs"),
            System.IO.Path.Combine(root, "src", "Progenitor.Core", "Hooks", "SystemHooks.cs"),
            System.IO.Path.Combine(root, "src", "Progenitor.Dev", "Ops", "HostCapture.cs"),
            System.IO.Path.Combine(root, "src", "Progenitor.Dev", "Ops", "ChatObserve.cs"),
            System.IO.Path.Combine(root, "src", "Progenitor.Dev", "Ops", "ExtraInstanceAuth.cs"),
            System.IO.Path.Combine(root, "src", "Progenitor.Dev", "Ops", "ExtraInstanceServerAuth.cs"),
        };
        foreach (var file in files)
        {
            Assert.True(System.IO.File.Exists(file), file);
            string text = System.IO.File.ReadAllText(file);
            Assert.DoesNotContain("using HarmonyLib", text);
            Assert.DoesNotContain("new Harmony(", text);
        }
        string runtime = System.IO.Path.Combine(root, "src", "Progenitor.Core", "Runtime.cs");
        Assert.DoesNotContain("HarmonyLib", System.IO.File.ReadAllText(runtime));
    }

    private static string FindRepoRoot()
    {
        string? dir = System.IO.Path.GetFullPath(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, "Progenitor.sln")))
                return dir;
            dir = System.IO.Directory.GetParent(dir)?.FullName;
        }
        throw new System.IO.DirectoryNotFoundException("Progenitor.sln not found from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void CoreNeedsFallbackHarmony_is_per_site_not_global_owns()
    {
        Assert.False(HarmonySlotPolicy.CoreNeedsFallbackHarmony(skipCoreHarmony: true, hostOwnsSite: _ => false));
        Assert.True(HarmonySlotPolicy.CoreNeedsFallbackHarmony(skipCoreHarmony: false, hostOwnsSite: null));
        Assert.False(HarmonySlotPolicy.CoreNeedsFallbackHarmony(skipCoreHarmony: false, hostOwnsSite: _ => true));
        Assert.True(HarmonySlotPolicy.CoreNeedsFallbackHarmony(skipCoreHarmony: false, hostOwnsSite: site => site == HarmonySlotPolicy.ChatOnUpdate));

        lock (Gate)
        {
            var someOwned = new HarmonySlotHandoff.FakeSlot
            {
                OwnedSites = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                {
                    HarmonySlotHandoff.ChatOnUpdate,
                },
            };
            HarmonySlotHandoff.Use(someOwned);
            try
            {
                Assert.True(HarmonySlotHandoff.HostOwnsPatches);
                Assert.False(HarmonySlotHandoff.CoreMayApplyHarmony(HarmonySlotHandoff.HostOwnsPatches, skipCoreHarmony: false));
                Assert.True(HarmonySlotHandoff.CoreNeedsFallbackHarmony());
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }

            var allOwned = new HarmonySlotHandoff.FakeSlot();
            HarmonySlotHandoff.Use(allOwned);
            try
            {
                Assert.False(HarmonySlotHandoff.CoreNeedsFallbackHarmony());
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void SkipCoreModules_parse_skip_refuse_and_pin_format()
    {
        Assert.Equal(17, HarmonySlotPolicy.ModuleNames.Length);
        Assert.Equal(17, HarmonySlotHandoff.ModuleNames.Length);
        Assert.Equal(HarmonySlotPolicy.ModuleNames, HarmonySlotHandoff.ModuleNames);
        Assert.Contains("Effects", HarmonySlotPolicy.ModuleNames);
        Assert.Contains("Effects", HarmonySlotHandoff.ModuleNames);
        Assert.Contains("Ui", HarmonySlotPolicy.ModuleNames);
        Assert.Contains("Ui", HarmonySlotHandoff.ModuleNames);
        Assert.Contains("Units", HarmonySlotPolicy.ModuleNames);
        Assert.Contains("Units", HarmonySlotHandoff.ModuleNames);
        Assert.Equal(HarmonySlotHandoff.EffectsModule, HarmonySlotHandoff.ModuleNames[14]);
        Assert.Equal(HarmonySlotHandoff.UiModule, HarmonySlotHandoff.ModuleNames[15]);
        Assert.Equal(HarmonySlotHandoff.UnitsModule, HarmonySlotHandoff.ModuleNames[16]);
        Assert.Equal("Effects", HarmonySlotHandoff.CanonicalModuleName("effects"));
        Assert.Equal("Effects", HarmonySlotPolicy.CanonicalModuleName("effects"));
        foreach (var name in HarmonySlotPolicy.ModuleNames)
            Assert.Contains(name, HarmonySlotHandoff.ModuleNames);
        Assert.Contains("Events", HarmonySlotPolicy.ModuleNames);
        Assert.Contains("Ecs", HarmonySlotPolicy.ModuleNames);
        Assert.Equal("Events", HarmonySlotPolicy.CanonicalModuleName("events"));
        Assert.Null(HarmonySlotPolicy.CanonicalModuleName("not-a-module"));
        Assert.Empty(HarmonySlotPolicy.ParseSkipModules(""));
        Assert.Empty(HarmonySlotPolicy.ParseSkipModules("  , , "));
        Assert.Equal(new[] { "Events", "Content" }, HarmonySlotPolicy.ParseSkipModules("Events, content"));
        Assert.Equal(new[] { "Events" }, HarmonySlotPolicy.ParseSkipModules("Events, Events"));
        Assert.Equal(new[] { "Nope" }, HarmonySlotPolicy.UnknownSkipModules("Nope, Events"));
        Assert.True(HarmonySlotPolicy.ShouldSkipModule("Events", "events, Hooks", refusedLiveWorld: false));
        Assert.False(HarmonySlotPolicy.ShouldSkipModule("Events", "events", refusedLiveWorld: true));
        Assert.False(HarmonySlotPolicy.ShouldSkipModule("Chat", "Events", refusedLiveWorld: false));
        Assert.True(HarmonySlotPolicy.ShouldSkipModule("Networking", "Content", refusedLiveWorld: false));
        Assert.True(HarmonySlotPolicy.ShouldSkipModule("Commands", "Events", refusedLiveWorld: false));
        Assert.True(HarmonySlotPolicy.ShouldSkipModule("Persistence", "Events", refusedLiveWorld: false));
        Assert.True(HarmonySlotPolicy.ShouldSkipModule("Content", "Events", refusedLiveWorld: false));
        Assert.Equal(new[] { "Networking" }, HarmonySlotPolicy.ImpliedSkips("Content"));
        Assert.Empty(HarmonySlotPolicy.ImpliedSkips("Hooks"));
        Assert.Empty(HarmonySlotHandoff.ImpliedSkips("Hooks"));
        Assert.False(HarmonySlotPolicy.ShouldSkipModule("Systems", "Hooks", refusedLiveWorld: false));
        Assert.Equal(
            HarmonySlotPolicy.SkippedModuleExceptionMessage("Config"),
            HarmonySlotHandoff.SkippedModuleExceptionMessage("Config"));
        Assert.Contains("Config", HarmonySlotPolicy.SkippedModuleException("Config").Message);
        Assert.Equal(
            new[] { "Events", "Commands", "Content", "Networking", "Persistence" },
            HarmonySlotPolicy.ExpandSkipModules("Events"));
        Assert.Equal(
            HarmonySlotPolicy.ExpandSkipModules("content"),
            HarmonySlotHandoff.ExpandSkipModules("content"));
        Assert.Equal(
            HarmonySlotPolicy.SkipCouplingLog("Content", new[] { "Networking" }),
            HarmonySlotHandoff.SkipCouplingLog("Content", new[] { "Networking" }));
        Assert.Contains("Hermit's Shawl", HarmonySlotPolicy.SkipCouplingReason("Content"));
        Assert.Contains("event bus", HarmonySlotPolicy.SkipCouplingReason("Events"));
        Assert.Equal("pin-diag module=Content skipped", HarmonySlotPolicy.FormatModulePinsSkipped("Content"));
        Assert.Equal(
            HarmonySlotPolicy.FormatModulePinsSkipped("Hooks"),
            HarmonySlotHandoff.FormatModulePinsSkipped("Hooks"));
        Assert.True(HarmonySlotPolicy.IsZeroCountHold("before=0 after=0 unresolved=0 waiters=0 installed=False"));
        Assert.False(HarmonySlotPolicy.IsZeroCountHold("before=2 after=0 unresolved=0 waiters=0 installed=True"));
        Assert.Equal(
            "pin-diag module=Hooks holds=[installed]",
            HarmonySlotPolicy.DropZeroCountPinHolds(
                "pin-diag module=Hooks holds=[installed, before=0 after=0 unresolved=0 waiters=0 installed=False]"));
        Assert.Equal(
            "pin-diag module=Networking holds=[installed, clientHandlers=2]",
            HarmonySlotPolicy.ReplaceOrDropHold(
                "pin-diag module=Networking holds=[installed, clientHandlers=5]",
                "clientHandlers=",
                "clientHandlers=2"));
        Assert.Equal(
            "pin-diag module=Networking holds=[installed]",
            HarmonySlotPolicy.ReplaceOrDropHold(
                "pin-diag module=Networking holds=[installed, clientHandlers=5]",
                "clientHandlers=",
                null));
        Assert.Contains("No-op (no process-lifetime install): Chat, Castles, Players", HarmonySlotPolicy.SkipModulesEnabledLog("Hosted"));
        Assert.Equal(HarmonySlotPolicy.NoopModuleNames, HarmonySlotHandoff.NoopModuleNames);
        Assert.Equal(
            HarmonySlotPolicy.ParseSkipModules("Hosted, Localization"),
            HarmonySlotHandoff.ParseSkipModules("Hosted, Localization"));
        Assert.Equal(
            HarmonySlotPolicy.SkipModuleLog("Hosted"),
            HarmonySlotHandoff.SkipModuleLog("Hosted"));
        Assert.Equal(
            HarmonySlotPolicy.SkipModulesRefusedLiveWorldLog,
            HarmonySlotHandoff.SkipModulesRefusedLiveWorldLog);
        Assert.Contains("Known names: Events, Hooks", HarmonySlotPolicy.SkipModulesEnabledLog("Hosted"));
        Assert.Contains("Effects", HarmonySlotPolicy.SkipModulesEnabledLog("Hosted"));
        Assert.Contains("Ui", HarmonySlotPolicy.SkipModulesEnabledLog("Hosted"));
        Assert.Contains("Units", HarmonySlotPolicy.SkipModulesEnabledLog("Hosted"));
        Assert.Equal("pin-diag module=Events holds=[]", HarmonySlotPolicy.FormatModulePins("Events"));
        Assert.Equal(
            "pin-diag module=Hosted holds=[BootstrapLifetimeSlots, Application.quitting]",
            HarmonySlotPolicy.FormatModulePins("Hosted", "BootstrapLifetimeSlots", "Application.quitting"));
        Assert.Equal(
            HarmonySlotPolicy.FormatModulePins("Networking", "clientReceiveSlot"),
            HarmonySlotHandoff.FormatModulePins("Networking", "clientReceiveSlot"));

        HarmonySlotHandoff.SetSkipModules("Events, content, Nope", refusedLiveWorld: false);
        try
        {
            Assert.True(HarmonySlotHandoff.ShouldSkipModule("Events"));
            Assert.True(HarmonySlotHandoff.ShouldSkipModule("Content"));
            Assert.False(HarmonySlotHandoff.ShouldSkipModule("Hooks"));
            Assert.False(HarmonySlotHandoff.ShouldSkipModule("Nope"));
            HarmonySlotHandoff.ThrowIfModuleSkipped("Hooks");
            var eventsSkipped = Assert.Throws<InvalidOperationException>(
                () => HarmonySlotHandoff.ThrowIfModuleSkipped("Events"));
            Assert.Equal(HarmonySlotHandoff.SkippedModuleExceptionMessage("Events"), eventsSkipped.Message);
        }
        finally
        {
            HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        }

        HarmonySlotHandoff.SetSkipModules("Events", refusedLiveWorld: true);
        try
        {
            Assert.False(HarmonySlotHandoff.ShouldSkipModule("Events"));
        }
        finally
        {
            HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        }

        Assert.Equal("SkipCoreModules", HarmonySlotPolicy.SkipModulesKey);
        Assert.Equal("SkipCoreModulesAllowLiveWorld", HarmonySlotPolicy.SkipModulesAllowLiveWorldKey);
        Assert.Equal("", HarmonySlotPolicy.CurrentSkipModules());
        HarmonySlotPolicy.SkipModulesValue = "Hosted";
        HarmonySlotPolicy.SkipModulesReader = () => "Localization";
        try
        {
            Assert.Equal("Localization", HarmonySlotPolicy.CurrentSkipModules());
        }
        finally
        {
            HarmonySlotPolicy.SkipModulesReader = null;
            HarmonySlotPolicy.SkipModulesValue = "";
            HarmonySlotPolicy.SkipModulesAllowLiveWorld = false;
            HarmonySlotPolicy.SkipModulesAllowLiveWorldReader = null;
        }
    }

    [Fact]
    public void TrySet_null_dispatcher_throws()
    {
        lock (Gate)
        {
            HarmonySlotHandoff.Use(new HarmonySlotHandoff.FakeSlot());
            try
            {
                Assert.Throws<ArgumentNullException>(() => HarmonySlotHandoff.TrySet(HarmonySlotHandoff.ChatOnUpdate, null!));
                Assert.Throws<ArgumentException>(() => HarmonySlotHandoff.TrySet("", new Action(Nop)));
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void LateFrame_absent_host_TrySet_returns_false()
    {
        lock (Gate)
        {
            HarmonySlotHandoff.Use(null);
            Assert.False(HarmonySlotHandoff.TrySetLateFrame(Nop));
        }
    }

    [Fact]
    public void LateFrame_slot_install_clear_reinstall()
    {
        lock (Gate)
        {
            var fake = new HarmonySlotHandoff.FakeSlot();
            HarmonySlotHandoff.Use(fake);
            try
            {
                var first = new Action(Nop);
                Assert.True(HarmonySlotHandoff.TrySetLateFrame(first));
                Assert.Same(first, fake.Get(HarmonySlotHandoff.LateFrame));

                HarmonySlotHandoff.ClearLateFrame();
                Assert.Null(fake.Get(HarmonySlotHandoff.LateFrame));

                var second = new Action(Nop);
                Assert.True(HarmonySlotHandoff.TrySetLateFrame(second));
                Assert.Same(second, fake.Get(HarmonySlotHandoff.LateFrame));

                HarmonySlotHandoff.ClearAll();
                Assert.Null(fake.Get(HarmonySlotHandoff.LateFrame));
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void LateFrame_refused_write_stays_empty()
    {
        lock (Gate)
        {
            var fake = new HarmonySlotHandoff.FakeSlot { AcceptWrites = false };
            HarmonySlotHandoff.Use(fake);
            try
            {
                Assert.False(HarmonySlotHandoff.TrySetLateFrame(Nop));
                Assert.Null(fake.Get(HarmonySlotHandoff.LateFrame));
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }
        }
    }

    [Fact]
    public void LateFrame_null_dispatcher_throws()
    {
        lock (Gate)
        {
            HarmonySlotHandoff.Use(new HarmonySlotHandoff.FakeSlot());
            try
            {
                Assert.Throws<ArgumentNullException>(() => HarmonySlotHandoff.TrySetLateFrame(null!));
            }
            finally
            {
                HarmonySlotHandoff.Use(null);
            }
        }
    }

    // ---- job FLATCH1: latches scoped wider than the thing they describe ------------------------

    /// <summary>
    /// R44 finding 6. Only two bind outcomes mean a handler will actually run. A module that
    /// latches "hooked" on <c>Inert</c> (Bootstrap owns the site and the slot write FAILED) or
    /// <c>Vanilla</c> (nothing is patched) has recorded done for a seam that was never installed
    /// and will not retry for the rest of the generation. <c>Skip</c> is not permanent either:
    /// <c>SkipRefusedLiveWorld</c> turns <c>SkipCoreHarmony</c> back off when a live world
    /// refuses the opt-out.
    /// </summary>
    [Fact]
    public void Only_a_filled_slot_or_a_cleared_fallback_may_latch_a_module_as_hooked()
    {
        Assert.True(HarmonySlotHandoff.BindMayLatch(HarmonySlotHandoff.SiteBind.Filled));
        Assert.True(HarmonySlotHandoff.BindMayLatch(HarmonySlotHandoff.SiteBind.Fallback));
        Assert.False(HarmonySlotHandoff.BindMayLatch(HarmonySlotHandoff.SiteBind.Inert));
        Assert.False(HarmonySlotHandoff.BindMayLatch(HarmonySlotHandoff.SiteBind.Vanilla));
        Assert.False(HarmonySlotHandoff.BindMayLatch(HarmonySlotHandoff.SiteBind.Skip));
    }

    /// <summary>
    /// R44 finding 6, the production shape. <c>Localization</c> takes game types
    /// (<c>Stunlock.Localization</c>, <c>ProjectM.ManagedItemData</c>) so it cannot compile into
    /// this assembly; this is a source pin and says so. It is worth having because the property
    /// it names has no other guard: <c>_hooked</c> means "the SetLanguage postfix is installed",
    /// and there must be exactly ONE place that sets it, gated on the bind outcome AND on a
    /// confirmation that the patch landed. Before the fix it was set on four paths, two of which
    /// (Inert, Vanilla) install nothing at all, so a failed slot write silently cost every mod
    /// string in the process the next time the player changed language.
    /// </summary>
    [Fact]
    public void Localization_latches_hooked_only_where_the_postfix_was_installed()
    {
        string text = System.IO.File.ReadAllText(System.IO.Path.Combine(
            FindRepoRoot(), "src", "Progenitor.Core", "Localization", "Localization.cs"));

        Assert.Equal(1, CountOccurrences(text, "_hooked = true"));
        SourcePin.AssertStatement(text, "if (installed && HarmonySlotHandoff.BindMayLatch(outcome)) _hooked = true;");
        // The Fallback route only clears Core to patch; the patch itself may still be refused.
        SourcePin.AssertUncommented(text, "installed = FallbackSetLanguage();");
        SourcePin.AssertStatement(text, "private static bool FallbackSetLanguage()");
    }

    /// <summary>
    /// R44 finding 6, second half. <c>Register</c> returned a no-op handle from inside the
    /// duplicate-row check, ahead of <c>EnsureHooked</c> and <c>PushKeyToGame</c> — reading our
    /// own <c>Entries</c> list as evidence about the GAME's <c>LocalizedStrings</c>, which a
    /// language change wipes without touching our list. The re-registration a mod does to recover
    /// was therefore the one call guaranteed to do nothing. Both calls must now be on every path.
    /// </summary>
    [Fact]
    public void Localization_register_pushes_to_the_game_table_even_for_a_duplicate_row()
    {
        string text = System.IO.File.ReadAllText(System.IO.Path.Combine(
            FindRepoRoot(), "src", "Progenitor.Core", "Localization", "Localization.cs"));

        int at = text.IndexOf("public static IDisposable Register(", StringComparison.Ordinal);
        Assert.True(at > 0);
        int push = text.IndexOf("PushKeyToGame(key);", at, StringComparison.Ordinal);
        Assert.True(push > at, "Register must end in a PushKeyToGame on every path.");

        string body = text.Substring(at, push - at);
        Assert.DoesNotContain("return NoOpHandle.Instance;", body, StringComparison.Ordinal);
        SourcePin.AssertStatement(body, "EnsureHooked();");
        // The duplicate check may only decide whether a ROW is added.
        SourcePin.AssertStatement(body, "if (!duplicate) Entries.Add(entry);");
        SourcePin.AssertStatement(text.Substring(push), "return duplicate ? NoOpHandle.Instance : handle;");
    }

    /// <summary>
    /// R44 finding 7. <c>HarmonyPatches.LogThrow</c> is reached from twenty-six typed slot sites
    /// and used to share one process-lifetime <c>int</c>: the first throw anywhere spent the only
    /// stack trace the process would ever print, and every later throw at every other site, in
    /// every later Core generation, was message-only. The dispatchers behind those slots are
    /// Core-generation objects, so the latch must be per site and must re-arm with the generation.
    /// <c>HarmonyPatches</c> is <c>unsafe</c> and takes game types, so this is a source pin.
    /// </summary>
    [Fact]
    public void The_slot_throw_stack_trace_is_per_site_and_re_arms_with_the_core_generation()
    {
        string text = System.IO.File.ReadAllText(System.IO.Path.Combine(
            FindRepoRoot(), "src", "Progenitor.Bootstrap", "HarmonyPatches.cs"));

        // No shared process-lifetime flag left.
        Assert.DoesNotContain("_throwLogged", text, StringComparison.Ordinal);

        int at = text.IndexOf("private static void LogThrow(string site, Exception e)", StringComparison.Ordinal);
        Assert.True(at > 0);
        string body = text.Substring(at, Math.Min(600, text.Length - at));
        SourcePin.AssertUncommented(body, "ThrowLogged.Add(site");
        SourcePin.AssertUncommented(body, "first ? e.ToString() : e.Message");

        // Re-armed where CoreLoader already drops the rest of this generation's slot state.
        int clear = text.IndexOf("internal static void ClearAfterSnapshots()", StringComparison.Ordinal);
        Assert.True(clear > 0);
        string clearBody = text.Substring(clear, Math.Min(500, text.Length - clear));
        SourcePin.AssertUncommented(clearBody, "ThrowLogged.Clear();");
    }

    private static int CountOccurrences(string text, string needle)
    {
        int n = 0;
        int at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            n++;
            at += needle.Length;
        }
        return n;
    }
}

[CollectionDefinition("HarmonySlotHandoff")]
public sealed class HarmonySlotHandoffCollection { }
