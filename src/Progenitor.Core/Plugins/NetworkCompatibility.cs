using System;

namespace Progenitor.Plugins;

/// <summary>
/// How strictly a plugin must be present on the other side of a join. Combined with
/// <see cref="NetworkVersionRule"/> on <see cref="NetworkCompatibilityAttribute"/>. The
/// handshake evaluates each side's plugins against the peer's list; a failure becomes one
/// readable kick reason naming the plugin guid, the expected version, and what the peer sent.
/// </summary>
public enum NetworkCompatibilityLevel : byte
{
    /// <summary>
    /// The other side must have this plugin. Version matching follows
    /// <see cref="NetworkVersionRule"/>. This is also the in-memory default for a handshake
    /// row recorded without the attribute (see <see cref="NetworkCompatibilityAttribute"/>).
    /// A server plugin at this level (or any level other than
    /// <see cref="NoNeedForSync"/> / <see cref="ClientOnly"/>) makes the handshake
    /// fail-closed: a client that never answers is kicked after
    /// <c>Networking.HandshakeAnswerTimeoutSeconds</c> (default 20 s).
    /// </summary>
    ServerMustHave = 0,

    /// <summary>
    /// Either side may lack this plugin. When both sides have it, version matching still
    /// follows <see cref="NetworkVersionRule"/>. A server whose plugins are only this
    /// level and/or <see cref="ClientOnly"/> does not kick a silent client; the handshake
    /// logs that the client is unverified and continues.
    /// </summary>
    NoNeedForSync = 1,

    /// <summary>
    /// Server ignores this plugin: it is not required on the other side and its version is
    /// never compared. Use for client-only UI / input mods. <see cref="NetworkVersionRule"/>
    /// is ignored. A server-side ClientOnly row does not consume a handshake slot and is
    /// not sent on the request.
    /// </summary>
    ClientOnly = 2,
}

/// <summary>
/// How two version strings are compared when a <see cref="NetworkCompatibilityLevel"/> says
/// the plugin must (or, if both present, should) match.
/// </summary>
public enum NetworkVersionRule : byte
{
    /// <summary>
    /// Whole string, case-insensitive. <c>1.0</c> and <c>1.0.0</c> differ. This is the
    /// handshake's original rule and the default when the attribute is omitted.
    /// </summary>
    Exact = 0,

    /// <summary>
    /// Major and minor numbers must match (<c>1.2.3</c> accepts <c>1.2.9</c>, rejects
    /// <c>1.3.0</c>). A proper <c>major.minor</c> parse is required (a lone <c>1</c> is
    /// unparseable). Patch, further dotted numeric components, and a leading <c>v</c> are
    /// ignored (<c>1.2.3.4</c> is the same minor as <c>1.2.9</c> — a legal four-part
    /// <c>System.Version</c> / BepInEx string). A <c>-</c> pre-release token is part of
    /// the compare (<c>1.0-Beta</c> vs <c>1.0.5</c> is a mismatch) and <c>+</c> build
    /// metadata is ignored. Identical strings always match, even when unparseable; an
    /// unparseable version that is not identical to the peer is a mismatch.
    /// </summary>
    Minor = 1,

    /// <summary>Presence only; the version strings are not compared.</summary>
    None = 2,
}

/// <summary>
/// Per-plugin network compatibility. The handshake already carries guid + version; this
/// attribute tells it <em>how</em> to compare this plugin against the peer, including the
/// reverse direction (a client-declared <see cref="NetworkCompatibilityLevel.ServerMustHave"/>
/// plugin missing on the server is a kick).
/// </summary>
/// <remarks>
/// Read from the concrete plugin type by <c>PluginHandshake.DeclareSelf</c> and, when present,
/// applied automatically after <c>OnLoad</c> so the attribute alone is enough. Plugins without
/// this attribute keep the original handshake rule: <c>Require</c> / <c>DeclareSelf</c> is
/// opt-in exact-match of the server's required list, and extra mods the client reports are
/// ignored.
/// </remarks>
/// <example>
/// <code>
/// [BepInPlugin("example.mod", "Example", "1.2.0")]
/// [BepInDependency("progenitor")]
/// [ProgenitorSide(Side.Both)]
/// [NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor)]
/// public class Plugin : ProgenitorPlugin&lt;PluginContext&gt; { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class NetworkCompatibilityAttribute : Attribute
{
    /// <summary>
    /// Presence rule only. The version rule is left undeclared: the handshake row still carries
    /// <see cref="NetworkVersionRule.Exact"/> for a peer whose Progenitor predates compatibility ranges, and a peer that
    /// understands ranges uses <see cref="Peers"/> when set, else the cautious default, the same minor version
    /// (mod compatibility C3).
    /// </summary>
    public NetworkCompatibilityAttribute(NetworkCompatibilityLevel level)
    {
        Level = level;
        VersionRule = NetworkVersionRule.Exact;
        VersionRuleDeclared = false;
    }

    /// <param name="level">
    /// Whether the peer must have this plugin. <see cref="NetworkCompatibilityLevel.ServerMustHave"/>
    /// is the usual choice for content that both sides apply.
    /// </param>
    /// <param name="versionRule">
    /// How versions are compared when the level requires a match. Ignored when
    /// <paramref name="level"/> is <see cref="NetworkCompatibilityLevel.ClientOnly"/>, and by a peer that understands
    /// ranges when <see cref="Peers"/> is set.
    /// </param>
    /// <remarks>
    /// A plugin compiled before compatibility ranges (C3) always calls this constructor, even when its source omitted
    /// the rule (the old default was <see cref="NetworkVersionRule.Exact"/>), so it keeps Exact until it is rebuilt.
    /// </remarks>
    public NetworkCompatibilityAttribute(NetworkCompatibilityLevel level, NetworkVersionRule versionRule)
    {
        Level = level;
        VersionRule = versionRule;
        VersionRuleDeclared = true;
    }

    /// <summary>Presence rule. See <see cref="NetworkCompatibilityLevel"/>.</summary>
    public NetworkCompatibilityLevel Level { get; }

    /// <summary>
    /// Version rule. See <see cref="NetworkVersionRule"/>. <see cref="NetworkVersionRule.Exact"/> when the rule was not
    /// declared (that is what goes on the handshake row for older peers).
    /// </summary>
    public NetworkVersionRule VersionRule { get; }

    /// <summary>True when the version rule was given explicitly (the two-argument constructor).</summary>
    public bool VersionRuleDeclared { get; }

    /// <summary>
    /// Mod compatibility C3: the peer versions of this mod this build works with, in the
    /// <see cref="CompatRange"/> syntax (<c>"&gt;=1.2.0 &lt;2.0.0"</c>, <c>"1.4.x"</c>, <c>"*"</c>; at most 64
    /// characters). Mirror it in the package's <c>progenitor.json</c> as <c>compatibility.peers</c> (package
    /// inspection refuses a package whose two disagree; <c>tools/package.ps1</c> writes it for you). When the two sides
    /// run different versions, the <b>newer</b> side's range must include the older version. A peer that predates
    /// ranges still gets <see cref="VersionRule"/>. Null or empty: no range declared, so an explicit
    /// <see cref="VersionRule"/> applies, else the same minor version. An invalid range is ignored with one log line.
    /// </summary>
    public string? Peers { get; set; }
}
