using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Progenitor.Access;

/// <summary>
/// The one runtime behind every generated accessor: it turns a declared member name into the
/// interop's own metadata pointer, once, and caches it for this Core generation.
///
/// **No offset is ever written down.** A field's location comes from
/// <c>il2cpp_field_get_offset</c> on the FieldInfo the interop resolved, and the read and write
/// themselves go through <c>il2cpp_field_get_value</c> / <c>il2cpp_field_set_value</c>, which do
/// that arithmetic in the runtime. Nothing here multiplies a stride or adds a literal, so the
/// dump-stride rule (CLAUDE.md) and the raw-write policy have no surface to bite: this is not a
/// chunk write, it is the IL2CPP runtime's own field accessor.
///
/// **Resolution order** for a field:
/// 1. the interop stub's generated <c>NativeFieldInfoPtr_&lt;name&gt;</c> static, which is what
///    Il2CppInterop itself uses for class fields (its static constructor is what fills it in);
/// 2. failing that, <c>il2cpp_class_get_field_from_name</c> on the type's native class — the path
///    that still works for a field the generator did not emit a stub for.
///
/// A method resolves through the same shape: the generated <c>NativeMethodInfoPtr_&lt;name&gt;_*</c>
/// static (see <c>Interop/Il2CppNative.cs</c>, which this reuses) and, for a Harmony target, the
/// interop stub's own <see cref="MethodBase"/>. When the name is overloaded the right static is
/// picked by the interop's own deterministic naming (<see cref="AccessNaming"/>) and never by
/// position in a reflection enumeration whose order nothing promises.
///
/// **The caller does not choose the width.** <c>il2cpp_field_get_value</c> takes no size: it
/// copies the field's own width into the buffer it is handed. So every blittable accessor asks
/// <see cref="FieldWidth"/> once per member and refuses when <c>sizeof(T)</c> disagrees, rather
/// than letting a mistyped <c>T</c> run past the local (R39 finding 3).
///
/// **And the width is not the whole question.** A reference-typed field is <c>IntPtr.Size</c>
/// bytes wide, which is the honest size of its pointer slot and therefore lets <c>sizeof(long)</c>
/// through — so the width check alone would <em>authorize</em> writing an arbitrary integer into a
/// slot the IL2CPP GC dereferences as an object (R40 finding 3). Every field is classified once,
/// from <c>il2cpp_class_is_valuetype</c> on its own class (<see cref="FieldIsReference"/>), and
/// the classification is checked before the width: a reference field refuses every unmanaged
/// accessor outright and is reachable only through the raw pointer pair and the offset, which the
/// generator emits only for a declaration that says <c>ref</c>. The mirror holds too — the pointer
/// pair refuses a value-typed field.
///
/// **And the classification fails closed.** A field the runtime would not classify — a build where
/// <c>il2cpp_field_get_type</c> or <c>il2cpp_class_from_type</c> hands back zero, or where either
/// throws — refuses <em>every</em> accessor, blittable and pointer pair alike, with a message
/// naming the step that did not answer, and the failure is cached as a failure. Passing it would
/// make the two gates fail together on exactly the condition they exist for: the single answer
/// "the runtime would not say" would open both the width check and the classification, and
/// <c>SetValue&lt;long&gt;</c> would reach <c>il2cpp_field_set_value</c> on a GC-visible slot
/// (R41 finding 1). The width's own unknown stays permissive, which is the right call for a
/// missing number; the classification's is not a number.
///
/// Every failure is an <see cref="AccessException"/> carrying one <see cref="AccessMessages"/>
/// sentence. The cache is static state of the Core assembly, so it lives exactly one Core
/// generation; <see cref="Reset"/> empties it early.
///
/// **A miss is cached exactly like a hit, at every entry.** The policy R41 finding 1 settled for
/// <see cref="Shape"/> — a failure is *cached as that failure*, because the refusal has to be the
/// same on the thousandth call as on the first and re-asking a native call that already threw is a
/// per-call risk of throwing again on the game's own thread — is the policy of the whole resolver:
/// <see cref="FieldInfo"/>, <see cref="FieldOffset"/> (which is <c>FieldInfo</c>),
/// <see cref="MethodPointer"/> and <see cref="Method"/> all record the
/// <see cref="AccessException"/>'s sentence against the same key their answer would have used and
/// rethrow it verbatim on every later call, for this Core generation only. Before R65 finding 7
/// only the shape read followed it, so a member that resolves on no build re-ran
/// <c>RunClassConstructor</c>, a reflection lookup, a native class-pointer lookup and
/// <c>il2cpp_class_get_field_from_name</c> on the main thread every single call — the file argued
/// for the rule twelve lines above the site where the failures actually happen and then did not
/// follow it. The member still throws every call, with the identical sentence; what stops is the
/// native work behind it. A member that appears later in the process — a late-loaded image — needs
/// <see cref="Reset"/> (or the next Core generation), which is the deliberate cost of the rule and
/// the reason <see cref="Reset"/> clears the misses too.
///
/// **One refused member is one refusal.** <see cref="Shape"/> sits on top of
/// <see cref="FieldInfo"/>, so a member that is on no build and is asked through
/// <see cref="FieldWidth"/> used to write a refusal under each of their two keys and count twice in
/// <see cref="MissCount"/> (R66 finding 8). <see cref="Shape"/> now asks <see cref="FieldInfo"/>
/// before it memoizes, so the refusal belongs to the entry that produced it and the count is a
/// count of members.
/// </summary>
public static unsafe class AccessResolver
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, IntPtr> Fields = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, AccessFieldShape> FieldShapes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IntPtr> MethodPointers = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, MethodBase> MethodInfos = new(StringComparer.Ordinal);

    /// <summary>
    /// The refusal sentence for every key that has already failed, in the same key space as the
    /// three caches above (the <c>|f|</c>, <c>|p|</c> and <c>|m|</c> markers keep them apart), so a
    /// key is either resolved, refused, or not yet asked.
    /// </summary>
    private static readonly Dictionary<string, string> Misses = new(StringComparer.Ordinal);

    /// <summary>Drop every cached pointer, and every cached refusal. Idempotent; safe on any thread.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Fields.Clear();
            FieldShapes.Clear();
            MethodPointers.Clear();
            MethodInfos.Clear();
            Misses.Clear();
        }
    }

    /// <summary>
    /// Cached member count, for the <c>Access.Describe()</c> line, which prints it as
    /// <c>resolved=</c> — so it counts members that resolved and not the refusals beside them.
    /// </summary>
    public static int CachedCount
    {
        get { lock (Gate) return Fields.Count + MethodPointers.Count + MethodInfos.Count; }
    }

    /// <summary>
    /// Refused member count for this Core generation. Diagnostic; not part of the facade line.
    ///
    /// One refused member is one entry. A member that is on no build is refused by
    /// <see cref="FieldInfo"/>, under <see cref="FieldInfo"/>'s key, and the shape read above it
    /// adds nothing of its own — it asks <see cref="FieldInfo"/> before it memoizes, precisely so
    /// that one missing field asked through <see cref="FieldWidth"/> is not counted twice
    /// (R66 finding 8).
    /// </summary>
    public static int MissCount
    {
        get { lock (Gate) return Misses.Count; }
    }

    /// <summary>
    /// The one memoization every entry goes through: answer from the positive cache, else from the
    /// negative one, else resolve — and record whichever of the two happened.
    ///
    /// Only an <see cref="AccessException"/> is cached as a miss, and in practice that is
    /// <em>every</em> failure this resolver can observe: <see cref="ResolveField"/> wraps the class
    /// constructor's throw and the native class lookup's throw into one before either can escape,
    /// so the filter is not a way of telling a fact about the build ("not found", "ambiguous",
    /// "resolved but the pointer is null") apart from a transient fault of the call — a wrapped
    /// transient fault is cached the same way, and <see cref="Reset"/> (or the next Core
    /// generation) is the only escape from it. The typed catch is still the right shape, because it
    /// keeps anything a future entry throws <em>unwrapped</em> out of the table; it just is not the
    /// discriminator the first draft of this comment claimed (R66 finding 8). The concrete case is
    /// an interop type whose static constructor has already failed once: the runtime rethrows its
    /// cached <c>TypeInitializationException</c> for the life of the process, so caching "its
    /// interop type would not initialize" is right — arrived at by a filter that would have cached
    /// a genuinely transient fault identically.
    ///
    /// The resolve itself runs outside the lock, as it always has: it calls into the interop, and
    /// two threads racing the same member cost one duplicate lookup, not a deadlock.
    /// </summary>
    private static T Memoised<T>(Dictionary<string, T> cache, string key, Func<T> resolve)
    {
        lock (Gate)
        {
            if (cache.TryGetValue(key, out var cached)) return cached;
            if (Misses.TryGetValue(key, out var refused)) throw new AccessException(refused);
        }

        T resolved;
        try
        {
            resolved = resolve();
        }
        catch (AccessException e)
        {
            lock (Gate) Misses[key] = e.Message;
            throw;
        }

        lock (Gate) cache[key] = resolved;
        return resolved;
    }

    // ---- fields ---------------------------------------------------------------------------

    /// <summary>
    /// The IL2CPP <c>FieldInfo*</c> for a declared field. Throws
    /// <see cref="AccessException"/> with <see cref="AccessMessages.NotFound"/> when this build
    /// has no such field — every call, with the same sentence, but only the first one does the
    /// native work: the refusal is cached for this Core generation exactly as the pointer is
    /// (R65 finding 7; see the type's remarks for why, and for what <see cref="Reset"/> is for).
    /// </summary>
    public static IntPtr FieldInfo(Type interopType, string declaredType, string fieldName)
    {
        if (interopType == null) throw new AccessException(AccessMessages.NotFound(declaredType, fieldName));
        var key = interopType.FullName + "|f|" + fieldName;
        return Memoised(Fields, key, () => ResolveField(interopType, declaredType, fieldName));
    }

    /// <summary>
    /// The field's offset inside the object, from <c>il2cpp_field_get_offset</c>. Diagnostic only
    /// — the accessors below never do the arithmetic themselves. It is <see cref="FieldInfo"/>
    /// plus one native call, so it inherits that entry's miss policy: a member that is on no build
    /// throws the same sentence on every call and re-resolves nothing.
    /// </summary>
    public static uint FieldOffset(Type interopType, string declaredType, string fieldName) =>
        IL2CPP.il2cpp_field_get_offset(FieldInfo(interopType, declaredType, fieldName));

    /// <summary>
    /// The field's own width in bytes on this build, from <c>il2cpp_field_get_type</c> — the size
    /// <c>il2cpp_field_get_value</c> will copy, whatever the caller's <c>T</c> is. Zero when the
    /// runtime would not say; resolved once per member per Core generation and cached beside the
    /// <c>FieldInfo*</c>.
    /// </summary>
    public static int FieldWidth(Type interopType, string declaredType, string fieldName) =>
        Shape(interopType, declaredType, fieldName).Width;

    /// <summary>
    /// Whether the field holds an object pointer on this build, from
    /// <c>il2cpp_class_is_valuetype</c> on the field's own class — the question the width cannot
    /// answer (R40 finding 3). False when the runtime would not classify it; ask
    /// <see cref="FieldIsClassified"/> to tell that apart from a genuine value type.
    /// </summary>
    public static bool FieldIsReference(Type interopType, string declaredType, string fieldName) =>
        Shape(interopType, declaredType, fieldName).IsReference;

    /// <summary>Whether the runtime classified the field at all.</summary>
    public static bool FieldIsClassified(Type interopType, string declaredType, string fieldName) =>
        Shape(interopType, declaredType, fieldName).Classified;

    /// <summary>
    /// What the runtime says about one field: how wide it is, and — the part the width cannot
    /// speak to — whether it is a value or a reference. Both come from the same
    /// <c>il2cpp_field_get_type</c> / <c>il2cpp_class_from_type</c> pair, so they are read and
    /// cached together.
    ///
    /// The decision itself is <see cref="AccessFieldShape.Classify"/>, which is game-free and
    /// therefore testable; everything below is the four native calls it asks. A failure — a zero
    /// from either pointer call, or a throw from any of them — comes back unclassified with the
    /// step named, and is <em>cached as that failure</em>: the refusal has to be the same on the
    /// thousandth call as on the first, and re-asking a native call that already threw is a
    /// per-call risk of throwing again on the game's own thread (R41 finding 1).
    ///
    /// That is the same sentence <see cref="Memoised"/> now applies to every other entry, so this
    /// one goes through it too: an unclassified shape is a cached <em>value</em> (the shape says
    /// what did not answer).
    ///
    /// The other failure reachable here — the member being on no build at all — is
    /// <see cref="FieldInfo"/>'s, and is asked <em>before</em> this entry memoizes anything, not
    /// from inside its resolve lambda. That is R66 finding 8: a refusal thrown through the lambda
    /// was recorded twice, once under <see cref="FieldInfo"/>'s <c>|f|</c> key and again under this
    /// entry's <c>|w|</c> key, so one missing field asked through <see cref="FieldWidth"/> counted
    /// as two refused members — and, when <paramref name="interopType"/> was null, under a
    /// <c>"?"</c>-prefixed key that every declared type shared. The refusal is
    /// <see cref="FieldInfo"/>'s to own; this entry owns only the shape. The cost is one extra
    /// dictionary read per shape call on the hot path, which is the same lock this entry was
    /// already taking.
    /// </summary>
    private static AccessFieldShape Shape(Type interopType, string declaredType, string fieldName)
    {
        // Outside Classify, and outside the memoization: an unresolvable member is an
        // AccessException with its own sentence, cached under its own key, not an unclassified
        // shape and not a second refusal.
        var field = FieldInfo(interopType, declaredType, fieldName);

        var key = interopType.FullName + "|w|" + fieldName;
        return Memoised(FieldShapes, key, () => AccessFieldShape.Classify(
            () => IL2CPP.il2cpp_field_get_type(field),
            type => IL2CPP.il2cpp_class_from_type(type),
            klass => IL2CPP.il2cpp_class_is_valuetype(klass),
            klass => { uint align = 0; return IL2CPP.il2cpp_class_value_size(klass, ref align); },
            IntPtr.Size));
    }

    /// <summary>
    /// The gate every unmanaged accessor passes: the field must be value-typed on this build, and
    /// then <c>sizeof(T)</c> must be its width.
    ///
    /// The order is the fix for R40 finding 3. <c>il2cpp_field_get_value</c> takes no size, so a
    /// width mismatch is a silent stack write in one direction and a silent stack read in the
    /// other (R39 finding 3) — but a <em>reference</em> field is <c>IntPtr.Size</c> wide, so the
    /// width check alone passes <c>SetValue&lt;long&gt;</c> and writes an arbitrary integer into a
    /// GC-tracked slot. The classification is therefore asked first and refuses outright,
    /// whatever the width says.
    /// </summary>
    private static void RequireBlittable<T>(Type interopType, string declaredType, string fieldName)
        where T : unmanaged
    {
        var shape = Shape(interopType, declaredType, fieldName);
        AccessNaming.RequireValueTyped(
            shape.Classified, shape.IsReference, declaredType, fieldName, shape.UnclassifiedStep);
        AccessNaming.RequireWidth(shape.Width, sizeof(T), declaredType, fieldName);
    }

    /// <summary>
    /// The mirror gate for the raw object-pointer accessors: the field must actually hold an
    /// object pointer on this build, so <c>il2cpp_field_set_value_object</c> is never handed a
    /// value-typed field (a <c>float3</c> written over with a pointer).
    /// </summary>
    private static void RequireReference(Type interopType, string declaredType, string fieldName)
    {
        var shape = Shape(interopType, declaredType, fieldName);
        AccessNaming.RequireReferenceTyped(
            shape.Classified, shape.IsReference, declaredType, fieldName, shape.UnclassifiedStep);
    }

    /// <summary>Read a blittable instance field.</summary>
    public static T GetValue<T>(Type interopType, string declaredType, string fieldName, Il2CppObjectBase instance)
        where T : unmanaged
    {
        var field = FieldInfo(interopType, declaredType, fieldName);
        RequireBlittable<T>(interopType, declaredType, fieldName);
        var pointer = InstancePointer(declaredType, fieldName, instance);
        T value = default;
        IL2CPP.il2cpp_field_get_value(pointer, field, &value);
        return value;
    }

    /// <summary>Write a blittable instance field.</summary>
    public static void SetValue<T>(Type interopType, string declaredType, string fieldName, Il2CppObjectBase instance, T value)
        where T : unmanaged
    {
        var field = FieldInfo(interopType, declaredType, fieldName);
        RequireBlittable<T>(interopType, declaredType, fieldName);
        var pointer = InstancePointer(declaredType, fieldName, instance);
        IL2CPP.il2cpp_field_set_value(pointer, field, &value);
    }

    /// <summary>
    /// Read a reference-typed instance field as a raw object pointer (may be zero).
    ///
    /// **The pointer is not rooted.** Nothing here pins the object, so it is valid for as long as
    /// the field still holds it and the IL2CPP GC has not moved on: use it in the frame you read
    /// it and null-check it there. Storing one across frames is the hazard this accessor cannot
    /// close; wrap it in the interop type (`new Il2CppObjectBase`-derived stub) if you need to
    /// keep it.
    /// </summary>
    public static IntPtr GetObject(Type interopType, string declaredType, string fieldName, Il2CppObjectBase instance)
    {
        var field = FieldInfo(interopType, declaredType, fieldName);
        RequireReference(interopType, declaredType, fieldName);
        var pointer = InstancePointer(declaredType, fieldName, instance);
        return IL2CPP.il2cpp_field_get_value_object(field, pointer);
    }

    /// <summary>Write a reference-typed instance field from a raw object pointer.</summary>
    public static void SetObject(Type interopType, string declaredType, string fieldName, Il2CppObjectBase instance, IntPtr value)
    {
        var field = FieldInfo(interopType, declaredType, fieldName);
        RequireReference(interopType, declaredType, fieldName);
        var pointer = InstancePointer(declaredType, fieldName, instance);
        IL2CPP.il2cpp_field_set_value_object(pointer, field, value);
    }

    /// <summary>
    /// Read a blittable static field. Il2CppInterop emits no property for an IL2CPP static field
    /// it did not model, which is one of the cases the access layer exists for.
    /// </summary>
    public static T GetStaticValue<T>(Type interopType, string declaredType, string fieldName)
        where T : unmanaged
    {
        var field = FieldInfo(interopType, declaredType, fieldName);
        RequireBlittable<T>(interopType, declaredType, fieldName);
        T value = default;
        IL2CPP.il2cpp_field_static_get_value(field, &value);
        return value;
    }

    /// <summary>Write a blittable static field.</summary>
    public static void SetStaticValue<T>(Type interopType, string declaredType, string fieldName, T value)
        where T : unmanaged
    {
        var field = FieldInfo(interopType, declaredType, fieldName);
        RequireBlittable<T>(interopType, declaredType, fieldName);
        IL2CPP.il2cpp_field_static_set_value(field, &value);
    }

    // ---- methods --------------------------------------------------------------------------

    /// <summary>
    /// The compiled function pointer for a declared method, through the interop's own
    /// <c>NativeMethodInfoPtr_*</c> static. <paramref name="signature"/> is the declaration's
    /// parameter list (<c>""</c> for "the only overload"); an ambiguous name with no list is an
    /// <see cref="AccessException"/> rather than a guess — and, like every refusal here, the same
    /// exception sentence on every later call without re-running the lookup (R65 finding 7).
    /// </summary>
    public static IntPtr MethodPointer(Type interopType, string declaredType, string methodName, string signature)
    {
        if (interopType == null) throw new AccessException(AccessMessages.NotFound(declaredType, methodName));
        var declared = signature ?? "";
        var key = interopType.FullName + "|p|" + methodName + "|" + declared;
        return Memoised(MethodPointers, key, () => ResolveMethodPointer(interopType, declaredType, methodName, declared));
    }

    private static IntPtr ResolveMethodPointer(Type interopType, string declaredType, string methodName, string signature)
    {
        IntPtr resolved;
        try
        {
            resolved = Progenitor.Interop.Il2CppNative.MethodPointerByPrefix(interopType, methodName);
        }
        catch (AmbiguousMatchException)
        {
            var info = MethodInfoOf(interopType, declaredType, methodName, signature);
            resolved = Progenitor.Interop.Il2CppNative.MethodPointer(
                interopType, NativeFieldNameFor(interopType, info!, declaredType));
        }
        catch (MissingMethodException e)
        {
            throw new AccessException(AccessMessages.NotFound(declaredType, methodName), e);
        }
        catch (MissingFieldException e)
        {
            throw new AccessException(AccessMessages.NotFound(declaredType, methodName), e);
        }
        catch (InvalidOperationException e)
        {
            throw new AccessException(AccessMessages.Unresolved(declaredType, methodName, e.Message), e);
        }

        if (resolved == IntPtr.Zero)
            throw new AccessException(AccessMessages.Unresolved(declaredType, methodName, "the pointer is null"));

        return resolved;
    }

    /// <summary>
    /// The interop stub's <see cref="MethodBase"/> for a declared method — what Harmony and
    /// <c>PatchSites.Register</c> want. Never null: a missing method is an
    /// <see cref="AccessException"/>, and — like every other entry — the same one on every call,
    /// resolved once (R65 finding 7). The cache holds resolved methods only, so there is no null
    /// entry to read a "cached miss" out of by accident: a refusal lives in its own table with the
    /// sentence to rethrow.
    /// </summary>
    public static MethodBase Method(Type interopType, string declaredType, string methodName, string signature)
    {
        if (interopType == null) throw new AccessException(AccessMessages.NotFound(declaredType, methodName));
        var declared = signature ?? "";
        var key = interopType.FullName + "|m|" + methodName + "|" + declared;
        return Memoised(MethodInfos, key, () => MethodInfoOf(interopType, declaredType, methodName, declared));
    }

    // ---- resolution -----------------------------------------------------------------------

    private static IntPtr ResolveField(Type interopType, string declaredType, string fieldName)
    {
        try
        {
            RuntimeHelpers.RunClassConstructor(interopType.TypeHandle);
        }
        catch (Exception e)
        {
            throw new AccessException(
                AccessMessages.Unresolved(declaredType, fieldName, "its interop type would not initialize: " + e.Message), e);
        }

        // 1. The interop generator's own field-info pointer.
        var stub = interopType.GetField(
            "NativeFieldInfoPtr_" + fieldName,
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        if (stub != null && stub.FieldType == typeof(IntPtr))
        {
            var value = (IntPtr)(stub.GetValue(null) ?? IntPtr.Zero);
            if (value != IntPtr.Zero) return value;
        }

        // 2. The native class, by name. Covers a field the interop emitted no stub for.
        IntPtr klass;
        try { klass = Il2CppClassPointerStore.GetNativeClassPointer(interopType); }
        catch (Exception e)
        {
            throw new AccessException(
                AccessMessages.Unresolved(declaredType, fieldName, "its native class did not resolve: " + e.Message), e);
        }
        if (klass != IntPtr.Zero)
        {
            var byName = IL2CPP.il2cpp_class_get_field_from_name(klass, fieldName);
            if (byName != IntPtr.Zero) return byName;
        }

        throw new AccessException(AccessMessages.NotFound(declaredType, fieldName));
    }

    private static MethodBase MethodInfoOf(Type interopType, string declaredType, string methodName, string signature)
    {
        MethodBase? single = null;
        int matches = 0;
        var wanted = ParameterList(signature);
        foreach (var candidate in interopType.GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            if (!string.Equals(candidate.Name, methodName, StringComparison.Ordinal)) continue;
            if (wanted != null && !Matches(candidate, wanted)) continue;
            matches++;
            single ??= candidate;
        }

        if (matches == 0) throw new AccessException(AccessMessages.NotFound(declaredType, methodName));
        if (matches > 1) throw new AccessException(AccessMessages.Ambiguous(declaredType, methodName, matches));
        return single!;
    }

    private static string[]? ParameterList(string signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        var pieces = signature!.Split(',');
        for (int i = 0; i < pieces.Length; i++) pieces[i] = pieces[i].Trim();
        return pieces;
    }

    private static bool Matches(MethodInfo candidate, string[] wanted)
    {
        var parameters = candidate.GetParameters();
        if (parameters.Length != wanted.Length) return false;
        for (int i = 0; i < parameters.Length; i++)
        {
            var type = parameters[i].ParameterType;
            var name = type.FullName ?? type.Name;
            if (string.Equals(name, wanted[i], StringComparison.Ordinal)) continue;
            if (string.Equals(type.Name, wanted[i], StringComparison.Ordinal)) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// The generated <c>NativeMethodInfoPtr_*</c> field name for one resolved overload, by the
    /// interop's own deterministic naming — see <see cref="AccessNaming.FieldNameFor"/>. Nothing
    /// here counts positions: <see cref="Type.GetMethods()"/> and <see cref="Type.GetFields()"/>
    /// both leave their order unspecified, and an inherited same-named method shifts one of them
    /// and not the other (R39 finding 2).
    /// </summary>
    private static string NativeFieldNameFor(Type interopType, MethodBase method, string declaredType)
    {
        // The interop type the declaration named, not the method's declaring type: an inherited
        // overload is declared on a base class, and the statics live on the stub that has them.
        var names = new List<string>();
        foreach (var f in interopType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
            if (f.FieldType == typeof(IntPtr)) names.Add(f.Name);
        return AccessNaming.FieldNameFor(method, names, declaredType);
    }

    private static IntPtr InstancePointer(string declaredType, string fieldName, Il2CppObjectBase instance)
    {
        if (instance == null)
            throw new AccessException(AccessMessages.Unresolved(declaredType, fieldName, "the instance is null"));
        var pointer = instance.Pointer;
        if (pointer == IntPtr.Zero)
            throw new AccessException(AccessMessages.Unresolved(declaredType, fieldName, "the instance pointer is null"));
        return pointer;
    }
}
