using System;
using System.Collections.Generic;

namespace Progenitor.Content;

/// <summary>
/// The game-typed steps of applying one received <c>PrfE</c> record, behind an interface so the
/// decision above them is game-free (job FEDW3, R51 finding 6). One instance per packet, bound to
/// one prefab entity.
/// </summary>
internal interface IPrefabEditApplySite
{
    /// <summary>
    /// Resolve the wire name to a local type and classify its shape. False refuses the record with
    /// <paramref name="reason"/> — unresolvable, not a value type, generic, an identity component,
    /// carrying a managed reference, or entirely reference-shaped.
    /// </summary>
    bool TryAcceptType(string? typeFullName, out PrefabEditTypeShape shape, out string reason);

    /// <summary>Whether the bound entity already carries this component. Never added.</summary>
    bool HasComponent(PrefabEditTypeShape shape, out string reason);

    /// <summary>
    /// The receiver's own current bytes for this component in the marshaled layout, or null.
    /// Only called when something must be preserved.
    /// </summary>
    byte[]? CurrentBytes(PrefabEditTypeShape shape);

    /// <summary>Capture the pre-write value as a baseline (idempotent; the store decides).</summary>
    void CaptureBaseline(PrefabEditTypeShape shape, string shortName);

    /// <summary>Write the final bytes. Throwing refuses the record and nothing else.</summary>
    void Write(PrefabEditTypeShape shape, byte[] bytes);
}

/// <summary>
/// The security boundary for <c>PrfE</c>, in one place the suite drives with fakes (job FEDW3,
/// R51 finding 6 — before it, the 52 tests drove a hand copy of <c>ApplyRecords</c> that had none
/// of these decisions in it, and the copy was written in the same commit by the same author as the
/// original).
///
/// <para><b>The order is the contract.</b> resolve → shape → size → <c>HasComponent</c> → mask →
/// <b>value domain</b> → baseline → write, per record, each step refusing on its own with its own
/// line, and the whole record wrapped so one bad type cannot take the rest of the packet with it.
/// A reordering is the regression this class exists to make visible.</para>
///
/// <para>The value domain is checked <em>after</em> the mask (job FEDW5, R53 finding 2) because the
/// mask decides which bytes are the sender's at all: a preserved field carries the receiver's own
/// value, and asking whether the sender's was in domain would be asking about a number nobody is
/// going to write.</para>
/// </summary>
internal static class PrefabEditApplyDecision
{
    /// <summary>
    /// Apply one already-parsed, already-build-checked layer. Returns the short names actually
    /// written. <paramref name="onRefused"/> is (typeName, reason); <paramref name="onPreserved"/>
    /// is called for every record whose mask is non-empty and is expected to do its own
    /// once-per-type-per-generation guard; <paramref name="onValueRefused"/> is
    /// (typeName, field, reason) for a value out of domain and is expected to guard itself once
    /// per type and field, because the value is attacker-chosen and the reason text carries it.
    /// When it is null the refusal goes to <paramref name="onRefused"/> like any other.
    /// </summary>
    public static string[] Apply(
        IReadOnlyList<PrefabEditComponentBytes>? records,
        IPrefabEditApplySite site,
        Action<string, string>? onRefused = null,
        Action<PrefabEditTypeShape>? onPreserved = null,
        Action<string, string, string>? onValueRefused = null)
    {
        var applied = new List<string>(records?.Count ?? 0);
        if (records == null || records.Count == 0 || site == null) return Array.Empty<string>();

        for (int i = 0; i < records.Count; i++)
        {
            var rec = records[i];
            string name = rec.TypeFullName ?? "";
            try
            {
                // 1 + 2: resolve and classify. One call because a resolved type with no shape is
                // never accepted, so there is no state between the two worth naming.
                if (!site.TryAcceptType(name, out var shape, out string reason) || shape == null)
                {
                    onRefused?.Invoke(name, reason);
                    continue;
                }

                // 3: the length the sender declared must be this receiver's own Marshal.SizeOf.
                int length = rec.Bytes?.Length ?? 0;
                if (!PrefabEditWire.SizeMatches(length, shape.Size))
                {
                    onRefused?.Invoke(name, PrefabEditWire.SizeMismatchReason(length, shape.Size, shape.TypeFullName));
                    continue;
                }

                // 4: never add a component; a server may only change one the prefab already has.
                if (!site.HasComponent(shape, out string missing))
                {
                    onRefused?.Invoke(name, missing);
                    continue;
                }

                // 5: the mask. Reference-shaped fields keep the receiver's own bytes, so a pointer
                // or an Entity from the wire is never written (R51 finding 1).
                byte[]? current = shape.WholeValue ? null : site.CurrentBytes(shape);
                if (!PrefabEditShape.TryPreserve(rec.Bytes, current, shape, out var final, out string maskReason))
                {
                    onRefused?.Invoke(name, maskReason);
                    continue;
                }
                if (!shape.WholeValue) onPreserved?.Invoke(shape);

                // 6: the value domain (job FEDW5, R53 finding 2). Being on the list says the
                // field may travel; this says what may be in it — a declared enum member, a
                // declared bit, an explicit range, a finite float. Refuses this record only, and
                // the field is named so the log says which one.
                var domain = shape.ValueDomain;
                if (domain != null && !domain.TryAccept(final, out string badField, out string valueReason))
                {
                    if (onValueRefused != null) onValueRefused(name, badField, valueReason);
                    else onRefused?.Invoke(name, badField.Length == 0 ? valueReason : badField + " " + valueReason);
                    continue;
                }

                // 7 + 8: capture vanilla once, then write.
                site.CaptureBaseline(shape, rec.ShortName);
                site.Write(shape, final);
                applied.Add(rec.ShortName);
            }
            catch (Exception e)
            {
                onRefused?.Invoke(name, "write threw: " + e.Message);
            }
        }
        return applied.ToArray();
    }
}
