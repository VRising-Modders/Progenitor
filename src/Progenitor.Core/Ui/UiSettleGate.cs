using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// A panel that owes itself a re-measure. The one thing the frame pass needs from a panel, so the
/// pass can be written without a <c>Canvas</c>, a <c>RectTransform</c> or a game (job FUI33).
/// </summary>
internal interface ISettlePanel
{
    /// <summary>
    /// One frame of the re-measure. <c>false</c> means nothing further is owed — the panel was
    /// laid out against the real canvas, the budget ran out, or the panel is gone.
    /// </summary>
    bool SettleTick();
}

/// <summary>
/// The owed list behind <c>UnityUiRuntime</c>'s deferred re-measure — which panels are waiting for
/// their canvas, who holds the frame pass, and when it is given back. Job FUI33, closing R48
/// finding 2 and the first two bullets of finding 6.
///
/// <para><b>Why it is a type of its own.</b> Job FUI32 wrote this machinery inline in
/// <c>UnityUiRuntime.cs</c>, which the test project does not compile, so <c>ArmSettle</c>,
/// <c>DropSettle</c> and the pump were covered by source pins over text and by nothing else — and
/// a pin that says <c>Plugin.Frame += SettlePump;</c> is on its own line cannot notice that the
/// line after it deletes what it added. Nothing in here is a Unity type; only
/// <see cref="ISettlePanel.SettleTick"/>'s implementation is. So the list lives here with tests and
/// the reads stay there without, which is the split this module applies everywhere else.</para>
///
/// <para><b>The re-arm race, which is the defect R48 found.</b> A tick that re-lays the panel out
/// can arm it again from inside itself: the re-layout measures, finds the canvas still unreadable,
/// logs <c>settle=armed</c> and calls <c>ArmSettle</c> — and the old pump then dropped the entry
/// anyway, on the strength of the <c>false</c> the same tick had returned. The panel kept the
/// fallback size for the rest of the session and the last thing the log said about it was that a
/// re-measure was coming: a latch recording done for work that did not happen, which is a shape
/// this project has a standing rule about. <see cref="Arm"/> records a re-arm taken during a pass
/// and <see cref="Pump"/> refuses to drop it. The wait is still bounded, because the budget lives
/// in the panel's own frame count and a re-arm does not reset it.</para>
///
/// <para><b>Reset, and why it exists.</b> <see cref="Reset"/> is on <c>Ui.Uninstall</c>'s list
/// (finding 6). <c>Plugin.Frame</c> is a Core static that <c>Plugin.Uninstall</c> nulls, so a
/// leftover subscription is not a cross-ALC pin — but the pump-armed latch is process state that
/// outlives an uninstall in the same ALC, and a latch left standing over a subscription that was
/// nulled means no later <c>ArmSettle</c> ever subscribes again and every panel prints
/// <c>settle=armed</c> over a pump that is not running. The disarm the owner handed in at
/// <see cref="Arm"/> is invoked here, so the list and the subscription go together.</para>
/// </summary>
internal static class UiSettleGate
{
    private static readonly object Gate = new();
    private static readonly List<ISettlePanel> Owed = new();

    /// <summary>Panels armed while <see cref="Pump"/> was walking the pass — see the type doc.</summary>
    private static readonly List<ISettlePanel> ArmedDuringPass = new();

    private static bool _pumpArmed;
    private static bool _passRunning;
    private static Action? _disarm;

    /// <summary>How many panels are waiting for their canvas.</summary>
    internal static int Count
    {
        get { lock (Gate) return Owed.Count; }
    }

    /// <summary>Whether the frame pass is held. Read by the pin diagnostic and by tests.</summary>
    internal static bool PumpArmed
    {
        get { lock (Gate) return _pumpArmed; }
    }

    /// <summary>One grep line for <c>Ui.DescribePins</c>.</summary>
    internal static string Describe()
    {
        lock (Gate) return "settleOwed=" + Owed.Count + " pump=" + _pumpArmed;
    }

    /// <summary>
    /// Take the frame pass for <paramref name="panel"/> until its canvas answers. Idempotent: a
    /// panel already owed is not added twice. Returns <c>true</c> when the caller must subscribe
    /// the pump, which is once for all owed panels; <paramref name="disarm"/> is how
    /// <see cref="Reset"/> gives that subscription back, and is remembered from whichever arm
    /// supplied it.
    /// </summary>
    internal static bool Arm(ISettlePanel? panel, Action? disarm)
    {
        if (panel == null) return false;
        lock (Gate)
        {
            if (disarm != null) _disarm = disarm;
            if (IndexOf(Owed, panel) < 0) Owed.Add(panel);

            // A panel re-armed by the very tick whose answer is about to be read must survive that
            // answer. Recorded rather than inferred: the tick returns one bool and cannot say "I
            // am finished with the pass I was asked about, and I have started another".
            if (_passRunning && IndexOf(ArmedDuringPass, panel) < 0) ArmedDuringPass.Add(panel);

            if (_pumpArmed) return false;
            _pumpArmed = true;
            return true;
        }
    }

    /// <summary>
    /// The caller's subscribe threw, so the pass is not held after all. Without this the latch
    /// would say it is and nothing would ever subscribe again.
    /// </summary>
    internal static void SubscribeFailed()
    {
        lock (Gate) _pumpArmed = false;
    }

    /// <summary>Forget a panel that was hidden or released before its canvas ever answered.</summary>
    internal static void Drop(ISettlePanel? panel)
    {
        if (panel == null) return;
        lock (Gate)
        {
            int i = IndexOf(Owed, panel);
            if (i >= 0) Owed.RemoveAt(i);
        }
    }

    /// <summary>
    /// One frame over every owed panel. Returns <c>true</c> when nothing is owed any more and the
    /// caller must give the frame pass back, so the common case — no panel open — costs nothing.
    ///
    /// <para>The pass is walked over a snapshot: a tick re-lays its panel out, which can arm or
    /// drop panels, and a list being mutated under the loop that reads it is the other half of the
    /// defect this method was written to fix.</para>
    /// </summary>
    internal static bool Pump()
    {
        ISettlePanel[] pass;
        lock (Gate)
        {
            pass = Owed.ToArray();
            ArmedDuringPass.Clear();
            _passRunning = true;
        }

        try
        {
            for (int i = 0; i < pass.Length; i++)
            {
                bool keep;
                try { keep = pass[i].SettleTick(); }
                catch (Exception e)
                {
                    keep = false;
                    Plugin.Logger?.LogWarning("Ui: panel re-measure threw: " + e.Message);
                }

                Finish(pass[i], keep);
            }
        }
        finally
        {
            lock (Gate)
            {
                _passRunning = false;
                ArmedDuringPass.Clear();
            }
        }

        lock (Gate)
        {
            bool unsubscribe = Owed.Count == 0 && _pumpArmed;
            if (unsubscribe) _pumpArmed = false;
            return unsubscribe;
        }
    }

    /// <summary>
    /// Drop the list and give the frame pass back. On <c>Ui.Uninstall</c>'s list: see the type doc
    /// for why the latch, not the subscription, is the thing that had to be cleared.
    /// </summary>
    internal static void Reset()
    {
        Action? disarm;
        lock (Gate)
        {
            Owed.Clear();
            ArmedDuringPass.Clear();
            _passRunning = false;
            disarm = _pumpArmed ? _disarm : null;
            _pumpArmed = false;
            _disarm = null;
        }

        if (disarm == null) return;
        try { disarm(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: panel re-measure disarm threw: " + e.Message);
        }
    }

    /// <summary>
    /// Apply one tick's answer. A <c>false</c> from a panel that armed itself again during this
    /// pass is not a drop — that is R48 finding 2.
    /// </summary>
    private static void Finish(ISettlePanel panel, bool keep)
    {
        lock (Gate)
        {
            if (keep) return;
            if (IndexOf(ArmedDuringPass, panel) >= 0) return;
            int i = IndexOf(Owed, panel);
            if (i >= 0) Owed.RemoveAt(i);
        }
    }

    /// <summary>Reference identity, never <c>Equals</c>: a panel is itself and nothing else.</summary>
    private static int IndexOf(List<ISettlePanel> list, ISettlePanel panel)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], panel)) return i;
        }

        return -1;
    }
}
