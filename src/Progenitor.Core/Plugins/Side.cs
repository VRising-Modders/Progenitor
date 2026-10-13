using System;

namespace Progenitor.Plugins;

/// <summary>
/// Which process a plugin runs in. One DLL can ship for every install: the client process, the
/// dedicated server, and the bundled server a client launches for a private game. The attribute
/// is what makes that safe — a server-only plugin still loads on the client, but stays dormant
/// there instead of touching client-only types.
/// </summary>
[Flags]
public enum Side
{
    /// <summary>Server process (<c>VRisingServer.exe</c>), dedicated or hosted.</summary>
    Server = 1,

    /// <summary>Game client process.</summary>
    Client = 2,

    /// <summary>Run in both processes. The default when <see cref="ProgenitorSideAttribute"/> is omitted.</summary>
    Both = Server | Client,
}

/// <summary>
/// Declares which process a <see cref="ProgenitorPlugin{TContext}"/> is active in. Read from the
/// concrete plugin type during <c>Load</c>. Missing attribute means <see cref="Side.Both"/>.
/// </summary>
/// <example>
/// <code>
/// [BepInPlugin("example.mod", "Example", "1.0.0")]
/// [BepInDependency("progenitor")]
/// [ProgenitorSide(Side.Server)]
/// public class Plugin : ProgenitorPlugin&lt;PluginContext&gt; { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class ProgenitorSideAttribute : Attribute
{
    /// <param name="side">Process this plugin should run in. Use <see cref="Side.Both"/> for a DLL that has work on each side.</param>
    public ProgenitorSideAttribute(Side side) => Side = side;

    /// <summary>Declared process role. Combined with the current executable to decide dormant vs active.</summary>
    public Side Side { get; }
}
