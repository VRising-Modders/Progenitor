using System;

namespace Progenitor.Commands;

/// <summary>
/// Marks a method as a chat command. The first parameter must be <see cref="ICommandContext"/>;
/// remaining parameters are parsed from the message after the command name. Overloads of the same
/// name are allowed; the dispatcher picks the best match so mods can offer both a simple and a
/// typed form without inventing extra names.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class CommandAttribute : Attribute
{
    /// <param name="name">Primary invocation name, without the prefix. Case-insensitive.</param>
    /// <param name="shortHand">Optional alias (also without the prefix).</param>
    /// <param name="adminOnly">When true, non-admin callers receive <see cref="CommandResult.Denied"/>.</param>
    /// <param name="usage">Override for generated usage. Shown on parse failure and by <c>.help</c>.</param>
    /// <param name="description">One-line summary for <c>.help</c>.</param>
    public CommandAttribute(
        string name,
        string? shortHand = null,
        bool adminOnly = false,
        string? usage = null,
        string? description = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ShortHand = string.IsNullOrWhiteSpace(shortHand) ? null : shortHand.Trim();
        AdminOnly = adminOnly;
        Usage = string.IsNullOrWhiteSpace(usage) ? null : usage.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    /// <summary>Primary name, without the command prefix.</summary>
    public string Name { get; }

    /// <summary>Optional alias, without the command prefix.</summary>
    public string? ShortHand { get; }

    /// <summary>When true, only <see cref="ICommandContext.IsAdmin"/> callers may run this overload.</summary>
    public bool AdminOnly { get; }

    /// <summary>Optional usage override. When null, usage is generated from the method signature.</summary>
    public string? Usage { get; }

    /// <summary>Optional one-line help text.</summary>
    public string? Description { get; }
}

/// <summary>
/// Core only: the command authorizes with the server's admin-list rule
/// (<c>ServerAdminList.AuthorizeShared</c>: on the admin list, else authenticated with
/// <c>adminauth</c>, else the host of this private game) instead of <see cref="CommandAttribute.AdminOnly"/>,
/// which follows only the game's admin flag (or the host). Put it on a command that is not
/// <c>adminOnly</c>: the dispatcher refuses everyone else with its own admin-only reply before any
/// argument is bound, and <c>.help</c>, group listings and the chat hints hide the overload from
/// them. The command body still re-checks the rule.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
internal sealed class AdminListGateAttribute : Attribute
{
}

/// <summary>
/// Groups every <see cref="CommandAttribute"/> method on the class under a shared first token
/// (<c>.group name</c>). The group exists so related commands share a namespace without each
/// method encoding the prefix in its own name.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class CommandGroupAttribute : Attribute
{
    /// <param name="name">Group token, without the prefix. Case-insensitive.</param>
    /// <param name="shortHand">Optional alias for the group token.</param>
    public CommandGroupAttribute(string name, string? shortHand = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ShortHand = string.IsNullOrWhiteSpace(shortHand) ? null : shortHand.Trim();
    }

    /// <summary>Group token, without the command prefix.</summary>
    public string Name { get; }

    /// <summary>Optional alias for the group token.</summary>
    public string? ShortHand { get; }
}

/// <summary>
/// Captures the rest of the input after previous parameters, verbatim (quotes and spaces kept).
/// Only valid on the last parameter, which must be a <see cref="string"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false, AllowMultiple = false)]
public sealed class RemainderAttribute : Attribute
{
}
