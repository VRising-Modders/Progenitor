using System;
using Il2CppInterop.Runtime.Injection;
using Progenitor.Bootstrap;
using UnityEngine;

namespace Progenitor.Ticks;

/// <summary>
/// The one MonoBehaviour Progenitor injects. FullName is <c>Progenitor.Ticks.FrameTicker</c> and
/// must stay that forever: <c>ClassInjector.RegisterTypeInIl2Cpp</c> has no unregister and keys
/// by FullName, so a second register (Core's fallback copy, or a rename) throws. This assembly
/// is the process registrar; Core attaches to this component (or stays inert) instead of
/// injecting a second CLR type. Update calls the Bootstrap host only; Core's frame subscribers
/// run from <c>ProcessHost.CoreTick</c>. LateUpdate raises the LateFrame slot when
/// <c>Camera.onPreCull</c> / <c>onPreRender</c> could not be subscribed or never
/// fire (HDRP skips <c>FireOnPreCull</c>; evidence EV5). That LateUpdate runs after
/// PresentationSystemGroup (PreLateUpdate). <c>HybridLineRendererComponent</c> has
/// no LateUpdate of its own. The interop stub of
/// <c>DefaultExecutionOrder</c> is not an attribute class, so order among other
/// LateUpdates is default 0.
/// </summary>
public sealed class FrameTicker : MonoBehaviour
{
    public FrameTicker(IntPtr ptr) : base(ptr) { }

    private static bool _registered;

    internal static void Install(BepInEx.Unity.IL2CPP.BasePlugin owner)
    {
        if (!_registered && !ClassInjector.IsTypeRegisteredInIl2Cpp<FrameTicker>())
            ClassInjector.RegisterTypeInIl2Cpp<FrameTicker>();
        _registered = true;
        ProcessHost.OwnsFrameTicker = true;
        owner.AddComponent<FrameTicker>();
        LateFrameHost.Install();
    }

    private void Update()
    {
        ProcessHost.Tick();
    }

    private void LateUpdate()
    {
        LateFrameHost.TickFromLateUpdate();
    }
}
