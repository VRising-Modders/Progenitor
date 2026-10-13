using System;
using System.Collections.Generic;

namespace Progenitor.Models;

/// <summary>
/// Name matching behind <c>Puppet.Dress</c>: which of an equipment mesh's bones the puppet has.
/// V Rising equipment is skinned to its own copy of the character rig with the same joint names
/// (<c>Spine_JNT</c>, <c>Left_Elbow_JNT</c>…), which is how the game's <c>HybridEquipmentUser</c>
/// moves it onto the wearer; a dressed puppet does the same by name.
/// </summary>
internal static class PuppetBinding
{
    /// <summary>
    /// For each source bone, the same name if the target has it, else null. Missing names are
    /// listed once each, in order. A null or empty source name counts as missing.
    /// </summary>
    internal static (string?[] Mapped, List<string> Missing) Map(IReadOnlyList<string?> sourceBones, Func<string, bool> targetHas)
    {
        if (sourceBones == null) throw new ArgumentNullException(nameof(sourceBones));
        if (targetHas == null) throw new ArgumentNullException(nameof(targetHas));
        var mapped = new string?[sourceBones.Count];
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < sourceBones.Count; i++)
        {
            string? name = sourceBones[i];
            if (!string.IsNullOrEmpty(name) && targetHas(name!))
            {
                mapped[i] = name;
                continue;
            }
            string label = string.IsNullOrEmpty(name) ? "<unnamed #" + i + ">" : name!;
            if (seen.Add(label)) missing.Add(label);
        }
        return (mapped, missing);
    }
}
