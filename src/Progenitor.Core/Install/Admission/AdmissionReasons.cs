namespace Progenitor.Install.Admission;

/// <summary>Every reason an <see cref="AdmissionResult"/> can carry. Text lives in <see cref="AdmissionReasonText"/>.</summary>
internal enum AdmissionReasonCode
{
    /// <summary>An exact qualification record matched every key and the runtime snapshot agreed.</summary>
    Qualified = 0,

    /// <summary>The bytes are not a readable PE image or the metadata is truncated.</summary>
    MalformedImage = 1,

    /// <summary>A PE image with no managed metadata (native DLL, mixed image without CLI header).</summary>
    NotManagedImage = 2,

    /// <summary>No type carries a directly declared <c>[BepInPlugin]</c>.</summary>
    NoPluginEntryPoint = 3,

    /// <summary>More than one directly attributed plugin type; the first increment admits one.</summary>
    MultiplePluginEntryPoints = 4,

    /// <summary>The <c>progenitor.json</c> id or version differs from the <c>[BepInPlugin]</c> declaration.</summary>
    IdentityMismatch = 5,

    /// <summary>A base type or referenced assembly is neither a known framework identity nor in the approved closure.</summary>
    UnresolvedDependency = 6,

    /// <summary>The plugin's base chain could not be walked to a known framework terminal.</summary>
    UnresolvedBaseChain = 7,

    /// <summary>The <c>progenitor.json</c> declaration schema is not one this policy understands.</summary>
    UnknownDeclarationSchema = 8,

    /// <summary>The package does not declare hot load for this operation and side.</summary>
    HotLoadNotDeclared = 9,

    /// <summary>Replace / Remove are not admitted in this increment.</summary>
    UnsupportedOperation = 10,

    /// <summary>Direct or helper-mediated use of <c>PluginContext.UnsafeHarmony</c> in the image or a bundled dependency.</summary>
    UnsafeHarmonyReference = 11,

    /// <summary>A direct reference to HarmonyLib outside the tracked facade.</summary>
    HarmonyReference = 12,

    /// <summary>A reference to <c>Il2CppInterop.Runtime.Injection.ClassInjector</c>.</summary>
    ClassInjectorReference = 13,

    /// <summary>P/Invoke (<c>DllImport</c>), a module import or a <c>calli</c> site.</summary>
    NativeImport = 14,

    /// <summary><c>MakeGenericMethod</c>, <c>Assembly.Load*</c>, <c>Type.GetType</c>, <c>Activator</c> or <c>System.Reflection.Emit</c>: Unknown for this profile.</summary>
    ReflectionEscape = 15,

    /// <summary>No local reviewed qualification record for this exact image.</summary>
    NoQualificationRecord = 16,

    /// <summary>A record exists for the plugin but one or more keys differ (named in the text).</summary>
    QualificationMismatch = 17,

    /// <summary>The package embedded its own qualification record; it was ignored.</summary>
    EmbeddedQualificationIgnored = 18,

    /// <summary>The Bootstrap admission snapshot is not available in this process.</summary>
    SnapshotUnavailable = 19,

    /// <summary>The loader is loading, reloading or retiring a generation right now.</summary>
    LoaderBusy = 20,

    /// <summary>The reload allowlist refuses this plugin id or image.</summary>
    AllowlistDenied = 21,

    /// <summary>A prior same-name generation is still pinned or retiring.</summary>
    PriorGenerationPinned = 22,

    /// <summary>The running plugin holds the process-lifetime UnsafeHarmony mark; replacement or removal needs a restart.</summary>
    UnsafeHarmonyMarked = 23,

    /// <summary>The package side does not include the process side of the target.</summary>
    SideMismatch = 24,

    /// <summary>A local server target has no supported runtime lifecycle connection.</summary>
    LocalServerRuntimeUnavailable = 25,

    /// <summary>The artifact bytes differ from the ones the plan or approval was bound to.</summary>
    ImageChanged = 26,

    /// <summary>The runtime generation differs from the one the approval was bound to.</summary>
    GenerationChanged = 27,

    /// <summary>The target side differs from the one the approval was bound to.</summary>
    SideChanged = 28,

    /// <summary>The dependency closure or a compatibility identity differs from the approved one.</summary>
    DependencyRewritten = 29,

    /// <summary>A plugin with this id is already loaded, so the requested initial install is really a replacement.</summary>
    AlreadyLoaded = 30,

    /// <summary>The admission policy version differs from the one the approval was bound to.</summary>
    PolicyChanged = 31,

    /// <summary>
    /// HOT2: the loader state moved between the plan and the write — generation, the busy flag or
    /// the pinned/retiring state. The recheck runs immediately before the executor's write, which
    /// is the handoff's "recheck under the same serialized loader operation that performs the
    /// change" (item 4), and nothing is written when it fires.
    /// </summary>
    SnapshotChanged = 32,

    /// <summary>
    /// HOTDEMO1 (user policy 2026-10-03): no reviewed record matched, but the package declares
    /// hotLoadable and its image is a single clean Progenitor plugin, so the flag is trusted.
    /// </summary>
    TrustedHotLoadable = 33,

    /// <summary>HOTDEMO1: no record matched and the plugin does not derive from ProgenitorPlugin, so the flag alone is not trusted.</summary>
    NotProgenitorPlugin = 34,

    /// <summary>
    /// FINJ1: the running plugin registered its own IL2CPP types (ClassInjector), which last for the life of
    /// the process; replacement or removal needs a restart.
    /// </summary>
    ProcessLifetimeMarked = 35,
}

/// <summary>Player-facing sentence for each <see cref="AdmissionReasonCode"/>.</summary>
internal static class AdmissionReasonText
{
    public static string For(AdmissionReasonCode code) => code switch
    {
        AdmissionReasonCode.Qualified => "this exact image is qualified for same-session activation on this side",
        AdmissionReasonCode.MalformedImage => "the plugin file is not a readable PE image",
        AdmissionReasonCode.NotManagedImage => "the plugin file has no managed metadata",
        AdmissionReasonCode.NoPluginEntryPoint => "no type declares [BepInPlugin] directly",
        AdmissionReasonCode.MultiplePluginEntryPoints => "more than one type declares [BepInPlugin]; this increment admits exactly one",
        AdmissionReasonCode.IdentityMismatch => "progenitor.json and the [BepInPlugin] declaration disagree on id or version",
        AdmissionReasonCode.UnresolvedDependency => "a referenced assembly is neither a known framework identity nor in the approved dependency closure",
        AdmissionReasonCode.UnresolvedBaseChain => "the plugin base chain could not be walked to a known framework base",
        AdmissionReasonCode.UnknownDeclarationSchema => "the package declaration schema is not one this policy understands",
        AdmissionReasonCode.HotLoadNotDeclared => "the package does not declare hot load for this operation and side",
        AdmissionReasonCode.UnsupportedOperation => "only initial installation is admitted in this increment; replacement and removal need their own acceptance",
        AdmissionReasonCode.UnsafeHarmonyReference => "the image or a bundled dependency uses PluginContext.UnsafeHarmony; hot activation is refused",
        AdmissionReasonCode.HarmonyReference => "the image or a bundled dependency references HarmonyLib directly; hot activation is refused",
        AdmissionReasonCode.ClassInjectorReference => "the image or a bundled dependency references Il2CppInterop ClassInjector; hot activation is refused",
        AdmissionReasonCode.NativeImport => "the image or a bundled dependency declares P/Invoke or a calli site; hot activation is refused",
        AdmissionReasonCode.ReflectionEscape => "the image uses reflection or dynamic loading (MakeGenericMethod, Assembly.Load, Type.GetType, Activator, Emit); unknown for this profile",
        AdmissionReasonCode.NoQualificationRecord => "no reviewed qualification record covers this exact image, closure, build, contract, side and operation",
        AdmissionReasonCode.QualificationMismatch => "a qualification record exists for this plugin but does not match",
        AdmissionReasonCode.EmbeddedQualificationIgnored => "the package embedded a qualification record; packages cannot self-certify and it was ignored",
        AdmissionReasonCode.SnapshotUnavailable => "the Bootstrap admission snapshot is not available in this process",
        AdmissionReasonCode.LoaderBusy => "the reloader is busy with another generation",
        AdmissionReasonCode.AllowlistDenied => "the reload allowlist refuses this plugin",
        AdmissionReasonCode.PriorGenerationPinned => "a prior generation of this plugin is still pinned or retiring",
        AdmissionReasonCode.UnsafeHarmonyMarked => "the running plugin holds the process-lifetime UnsafeHarmony mark",
        AdmissionReasonCode.SideMismatch => "the package side does not include the target process side",
        AdmissionReasonCode.LocalServerRuntimeUnavailable => "no supported runtime connection to the local server; its available path is install for next start",
        AdmissionReasonCode.ImageChanged => "the artifact bytes changed after the plan",
        AdmissionReasonCode.GenerationChanged => "the runtime generation changed after approval",
        AdmissionReasonCode.SideChanged => "the target side changed after approval",
        AdmissionReasonCode.DependencyRewritten => "the dependency closure or compatibility identity changed after approval",
        AdmissionReasonCode.AlreadyLoaded => "a plugin with this id is already loaded; an initial install would be a replacement",
        AdmissionReasonCode.PolicyChanged => "the admission policy version changed after approval",
        AdmissionReasonCode.SnapshotChanged => "the loader state changed between the plan and the write; nothing was written",
        AdmissionReasonCode.TrustedHotLoadable => "the package declares hotLoadable and its plugin image passed every check; it loads now without a review record",
        AdmissionReasonCode.NotProgenitorPlugin => "the plugin does not derive from ProgenitorPlugin; without a review record only Progenitor plugins load hot",
        AdmissionReasonCode.ProcessLifetimeMarked => "the running plugin registered its own IL2CPP types, which last for the life of the process",
        _ => code.ToString(),
    };
}
