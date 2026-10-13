using Progenitor.Ticks;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of R9 finding 7: one IL2CPP FullName for FrameTicker per process.
/// Core registers a fallback copy only when Bootstrap is absent and the name is free;
/// a registered name after a failed attach is inert, never a second component.
/// </summary>
public class TickOwnershipTests
{
    [Fact]
    public void Core_does_not_install_when_attach_succeeded()
    {
        Assert.False(TickOwnership.CoreMayInstallTicker(attachSucceeded: true, typeAlreadyRegistered: false));
        Assert.False(TickOwnership.CoreMayInstallTicker(attachSucceeded: true, typeAlreadyRegistered: true));
    }

    [Fact]
    public void Core_does_not_install_when_Bootstrap_already_registered_the_FullName()
    {
        Assert.False(TickOwnership.CoreMayInstallTicker(attachSucceeded: false, typeAlreadyRegistered: true));
    }

    [Fact]
    public void Core_fallback_installs_only_when_Bootstrap_is_absent_and_the_name_is_free()
    {
        Assert.True(TickOwnership.CoreMayInstallTicker(attachSucceeded: false, typeAlreadyRegistered: false));
    }

    [Fact]
    public void Failed_attach_after_the_type_is_registered_is_inert()
    {
        Assert.True(TickOwnership.StayInertForTicks(attachSucceeded: false, typeAlreadyRegistered: true));
        Assert.False(TickOwnership.StayInertForTicks(attachSucceeded: false, typeAlreadyRegistered: false));
        Assert.False(TickOwnership.StayInertForTicks(attachSucceeded: true, typeAlreadyRegistered: true));
        Assert.Equal("attach failed, Core stays inert for ticks", TickOwnership.AttachFailedInertLog);
    }

    [Fact]
    public void Core_does_not_install_a_second_log_ring()
    {
        Assert.False(TickOwnership.CoreMayInstallLogRing(backendAlreadySet: true, bootstrapRingListening: false));
        Assert.False(TickOwnership.CoreMayInstallLogRing(backendAlreadySet: false, bootstrapRingListening: true));
        Assert.False(TickOwnership.CoreMayInstallLogRing(backendAlreadySet: true, bootstrapRingListening: true));
        Assert.True(TickOwnership.CoreMayInstallLogRing(backendAlreadySet: false, bootstrapRingListening: false));
    }

    [Fact]
    public void Bootstrap_present_must_not_add_a_second_tick_source()
    {
        Assert.False(TickOwnership.CoreAddsSecondTickSource(bootstrapPresent: true, corePatchesOnUpdate: false, coreInjectsTicker: false));
        Assert.True(TickOwnership.CoreAddsSecondTickSource(bootstrapPresent: true, corePatchesOnUpdate: true, coreInjectsTicker: false));
        Assert.True(TickOwnership.CoreAddsSecondTickSource(bootstrapPresent: true, corePatchesOnUpdate: false, coreInjectsTicker: true));
        Assert.False(TickOwnership.CoreAddsSecondTickSource(bootstrapPresent: false, corePatchesOnUpdate: true, coreInjectsTicker: false));
    }

    [Fact]
    public void InstallDecision_consults_the_matrix()
    {
        Assert.Equal(
            TickOwnership.AttachFailedInertLog,
            TickOwnership.InstallDecision(attachSucceeded: false, typeAlreadyRegistered: true));
        Assert.Contains(
            "refusing ClassInjector",
            TickOwnership.InstallDecision(attachSucceeded: true, typeAlreadyRegistered: true));
        Assert.Contains(
            "Harmony OnUpdate postfix only",
            TickOwnership.InstallDecision(attachSucceeded: false, typeAlreadyRegistered: false));
    }
}
