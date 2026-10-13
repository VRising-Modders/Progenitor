using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Serializes tests that assign the process-wide <see cref="Plugin.Logger"/> shim so one class
/// cannot steal another class's captured log lines.
/// </summary>
[CollectionDefinition("PluginLogger")]
public sealed class PluginLoggerCollection
{
}
