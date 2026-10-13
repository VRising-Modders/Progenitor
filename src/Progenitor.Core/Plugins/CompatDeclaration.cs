using System;
using System.Reflection;

namespace Progenitor.Plugins;

/// <summary>
/// Mod compatibility C3: what one build's <see cref="NetworkCompatibilityAttribute"/> says about peer versions of
/// itself, as plain values (it pins no type, so the mod registry can keep it across a Core swap). Read by attribute
/// <em>name</em> through <see cref="CustomAttributeData"/>, because after a Core swap a plugin loaded at game start
/// carries the previous generation's attribute type. Game-free.
/// </summary>
internal sealed class CompatDeclaration
{
    internal const string AttributeName = "NetworkCompatibilityAttribute";

    internal CompatDeclaration(string version, NetworkCompatibilityLevel level, NetworkVersionRule rule, bool ruleDeclared, string? peers)
    {
        Version = (version ?? "").Trim();
        Level = level;
        Rule = rule;
        RuleDeclared = ruleDeclared;
        string p = (peers ?? "").Trim();
        PeersInvalid = p.Length > 0 && !CompatRange.TryParse(p, out _, out _);
        DeclaredPeers = p.Length > 0 && !PeersInvalid ? p : "";
        InvalidPeers = PeersInvalid ? p : "";
        EffectiveRange = CompatRanges.EffectiveText(Version, DeclaredPeers, RuleDeclared, Rule) ?? "";
    }

    internal string Version { get; }
    internal NetworkCompatibilityLevel Level { get; }

    /// <summary>The rule on the handshake row (Exact when undeclared).</summary>
    internal NetworkVersionRule Rule { get; }

    internal bool RuleDeclared { get; }

    /// <summary>The declared <c>Peers</c> range when valid, else empty.</summary>
    internal string DeclaredPeers { get; }

    /// <summary>A <c>Peers</c> value that is not a valid range (ignored), else empty.</summary>
    internal string InvalidPeers { get; }

    internal bool PeersInvalid { get; }

    /// <summary>
    /// The range the join evaluation uses for this build (<see cref="CompatRanges.EffectiveText"/>): the declared
    /// range, else the explicit rule, else the same minor version. Empty only when the version cannot be carried.
    /// </summary>
    internal string EffectiveRange { get; }

    /// <summary>The declaration of a plugin type, or null when it has no <c>[NetworkCompatibility]</c>.</summary>
    internal static CompatDeclaration? From(Type? pluginType, string? version)
    {
        if (pluginType == null) return null;
        try
        {
            for (var t = pluginType; t != null; t = t.BaseType)
            {
                foreach (var data in CustomAttributeData.GetCustomAttributes(t))
                {
                    if (!string.Equals(data.AttributeType.Name, AttributeName, StringComparison.Ordinal)) continue;
                    var args = data.ConstructorArguments;
                    var level = args.Count > 0 && args[0].Value is IConvertible l
                        ? (NetworkCompatibilityLevel)Convert.ToByte(l, System.Globalization.CultureInfo.InvariantCulture)
                        : NetworkCompatibilityLevel.ServerMustHave;
                    bool ruleDeclared = args.Count > 1;
                    var rule = ruleDeclared && args[1].Value is IConvertible r
                        ? (NetworkVersionRule)Convert.ToByte(r, System.Globalization.CultureInfo.InvariantCulture)
                        : NetworkVersionRule.Exact;
                    string? peers = null;
                    foreach (var named in data.NamedArguments)
                        if (string.Equals(named.MemberName, nameof(NetworkCompatibilityAttribute.Peers), StringComparison.Ordinal))
                            peers = named.TypedValue.Value as string;
                    return new CompatDeclaration(version ?? "", level, rule, ruleDeclared, peers);
                }
            }
        }
        catch
        {
            // A declaration that cannot be read is treated as none: the cautious default applies.
        }

        return null;
    }

    /// <summary>
    /// The effective range for a build that has no declaration at all (no attribute): the same minor version.
    /// </summary>
    internal static string DefaultRange(string? version) => CompatRanges.EffectiveText(version, null, false, NetworkVersionRule.Exact) ?? "";
}
