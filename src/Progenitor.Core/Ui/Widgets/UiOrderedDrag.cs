using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>What one frame of a row drag asks its widget to do (<see cref="UiOrderedDrag.Step"/>).</summary>
internal enum UiDragStep
{
    /// <summary>The widget cannot draw or take input any more: drop the drag without moving anything.</summary>
    Cancel = 0,

    /// <summary>The primary button is up (a release the grip did not see): drop at the current target.</summary>
    Drop = 1,

    /// <summary>The pointer is over the slots: <c>slot</c> is the nearest slot (an index into the centers).</summary>
    Target = 2,

    /// <summary>Nothing readable this frame (the pointer left the window): keep the drag as it is.</summary>
    Wait = 3,
}

/// <summary>
/// P7b: one frame of a grip drag, shared by the ordered list (<see cref="UiOrderedListHandle"/>) and the master-detail
/// member list (<see cref="UiMasterDetailHandle"/>). The held button and the pointer come from the framework's input
/// layer (<see cref="IWidgetRuntime"/>); the slot under the pointer is <see cref="UiOrderedListModel.DropIndex"/> over the
/// slots' fixed centers, so the target cannot oscillate. Game-free.
/// </summary>
internal static class UiOrderedDrag
{
    /// <summary>
    /// Decide this frame. <paramref name="slotCenters"/> is asked only when the pointer is readable and the button held.
    /// </summary>
    internal static UiDragStep Step(IWidgetRuntime? runtime, bool live, Func<IReadOnlyList<float>> slotCenters, out int slot)
    {
        slot = -1;
        if (runtime == null || !live) return UiDragStep.Cancel;
        bool held;
        try { held = runtime.PrimaryHeld(); }
        catch { held = false; }
        if (!held) return UiDragStep.Drop;

        PointerSample pointer;
        try { pointer = runtime.ReadPointer(); }
        catch { return UiDragStep.Wait; }
        if (pointer.Kind != PointerKind.Inside) return UiDragStep.Wait;
        slot = UiOrderedListModel.DropIndex(pointer.Y, slotCenters());
        return UiDragStep.Target;
    }
}
