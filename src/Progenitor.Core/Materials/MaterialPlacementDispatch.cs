#if !PROGENITOR_TESTS
using System;
using Il2CppInterop.Runtime;
using Unity.Entities.Internal;
using PlacementJob = ProjectM.CastleBuilding.Systems.BuildModeSystem.BuildModeSystem_5FA9898B_LambdaJob_10_Job;

namespace Progenitor.Materials;

// The optimized placement job embeds its own inventory count. While a shared view
// is active, use the game's equivalent synchronous dispatch that reaches our count
// seam. Both delegates belong to the game; no collectible delegate is installed.
internal static class MaterialPlacementDispatch
{
    private static InternalCompilerInterface.JobRunWithoutJobSystemDelegate? _original, _replacement;
    private static bool _logged, _unavailable;
    private static InternalCompilerInterface.JobRunWithoutJobSystemDelegate? _gameDispatch;

    internal static void Enter(bool enabled)
    {
        Restore();
        if (!enabled || _unavailable) return;
        var original = PlacementJob.FunctionPtrFieldBurst;
        // The generated NoBurst field itself points at a Burst direct-call wrapper.
        // Bind the game's preserved managed entry, using native reflection so the
        // delegate contains only a game MethodInfo and never a Core callback.
        var replacement = _gameDispatch;
        if (replacement == null)
        {
            var method = Il2CppType.Of<PlacementJob>().GetMethod("RunWithoutJobSystem$BurstManaged",
                Il2CppSystem.Reflection.BindingFlags.Public | Il2CppSystem.Reflection.BindingFlags.NonPublic | Il2CppSystem.Reflection.BindingFlags.Static);
            if (method == null)
            {
                // Logged once; placement then keeps the game's own dispatch (its embedded count).
                _unavailable = true;
                Plugin.Logger?.LogWarning("materials: the game's synchronous placement entry was not found; build placement keeps the game's own count");
                return;
            }
            try
            {
                replacement = Il2CppSystem.Delegate.CreateDelegate(
                    Il2CppType.Of<InternalCompilerInterface.JobRunWithoutJobSystemDelegate>(), method)
                    .Cast<InternalCompilerInterface.JobRunWithoutJobSystemDelegate>();
            }
            catch (Exception e)
            {
                _unavailable = true;
                Plugin.Logger?.LogWarning("materials: binding the game's synchronous placement entry failed; build placement keeps the game's own count: " + e.Message);
                return;
            }
            _gameDispatch = replacement;
        }
        if (original == null || replacement == null || original.Pointer == replacement.Pointer) return;
        _original = original;
        _replacement = replacement;
        PlacementJob.FunctionPtrFieldBurst = replacement;
        if (!_logged)
        {
            _logged = true;
            Plugin.Logger?.LogInfo("materials: placement uses game inventory-aware dispatch while shared totals are active");
        }
    }

    internal static void Restore()
    {
        if (_original == null) return;
        var current = PlacementJob.FunctionPtrFieldBurst;
        if (current != null && _replacement != null && current.Pointer == _replacement.Pointer)
            PlacementJob.FunctionPtrFieldBurst = _original;
        _original = null;
        _replacement = null;
    }
}
#endif
