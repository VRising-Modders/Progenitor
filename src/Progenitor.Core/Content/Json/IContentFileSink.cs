namespace Progenitor.Content.Json;

/// <summary>
/// The one seam between a parsed content file and the registry. The production implementation
/// (<c>ContentFiles.RegistrySink</c>) calls <c>PrefabRegistry.DefineItem(id).CloneOf(...)
/// .MaxAmount(...).Named(...).Described(...).Register(owner)</c> — the same builder a C# plugin
/// uses, so there is no second registration path — and the tests fake it.
/// </summary>
internal interface IContentFileSink
{
    /// <summary>
    /// True when this plugin already registered <paramref name="id"/> from C# (or from an
    /// earlier content file in the same load). A file that redefines one of those is refused:
    /// silently replacing a compiled definition from a data file is the failure mode this
    /// feature must not have.
    /// </summary>
    bool IsDefined(string id);

    /// <summary>
    /// Register one validated item. Throwing is allowed; <c>ContentFileLoad</c> reports it
    /// against the file and item that caused it and keeps going with the next file.
    /// </summary>
    void DefineItem(ContentItemSpec spec);
}
