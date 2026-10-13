using System;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using Progenitor.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Dye;

/// <summary>
/// The charge ring over the held swatch. The ring itself is Core's shared <see cref="HoldRing"/>
/// (one filled radial <c>Image</c>, raycasts off, re-parented per hold, destroyed on unload); this
/// class keeps the one thing that is Dye all's own — which sprite it wears.
///
/// <para><b>Sprite.</b> The game's own art first: the swatch's selected-state frame
/// (<c>TransmogColorGridSelectionEntry.SelectedStateNode</c>) when its sprite is named like a ring or
/// circle — borrowed, never copied, and safe to borrow because the ring is the swatch's child and
/// cannot outlive it. Otherwise <see cref="HoldRing"/> draws its own ring. Which one was used is
/// logged once.</para>
/// </summary>
internal sealed class ChargeRing : IDisposable
{
    private const string ObjectName = "progenitor-dyeall-ring";
    private const float Outset = 6f;
    private static readonly Regex RoundName = new("ring|circle|round|radial", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ManualLogSource _log;
    private readonly HoldRing _ring = new(ObjectName);
    private bool _loggedSource;

    internal ChargeRing(ManualLogSource log) => _log = log;

    /// <summary>Put an empty ring over <paramref name="cell"/>.</summary>
    internal void Show(TransmogColorGridSelectionEntry cell)
    {
        if (cell == null || cell.Pointer == IntPtr.Zero) return;
        try
        {
            _ring.ShowOver(cell.transform, Outset, SpriteFor(cell));
        }
        catch (Exception e)
        {
            _log.LogWarning("Dye all: charge ring could not be shown: " + e.Message);
        }
    }

    /// <summary>Fill to <paramref name="progress"/> (0 to 1).</summary>
    internal void SetProgress(float progress) => _ring.SetProgress(progress);

    /// <summary>The completion pulse: a full ring that grows and fades.</summary>
    internal void SetPulse(float scale, float alpha) => _ring.SetPulse(scale, alpha);

    /// <summary>Hide the ring (it stays built for the next hold).</summary>
    internal void Hide() => _ring.Hide();

    /// <summary>Destroy the ring and the drawn sprite.</summary>
    public void Dispose() => _ring.Dispose();

    private Sprite? SpriteFor(TransmogColorGridSelectionEntry cell)
    {
        Sprite? game = null;
        string gameName = "none";
        try
        {
            var node = cell.SelectedStateNode;
            var image = node != null ? node.GetComponentInChildren<Image>(true) : null;
            if (image != null && image.sprite != null)
            {
                gameName = image.sprite.name ?? "";
                if (RoundName.IsMatch(gameName)) game = image.sprite;
            }
        }
        catch { game = null; }

        if (!_loggedSource)
        {
            _loggedSource = true;
            _log.LogInfo(game != null
                ? "Dye all: charge ring uses the game sprite '" + gameName + "'."
                : "Dye all: charge ring uses its own drawn ring (the selected-state sprite is '" + gameName + "').");
        }

        return game;
    }
}
