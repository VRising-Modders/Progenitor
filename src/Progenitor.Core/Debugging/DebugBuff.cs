using System;
using System.Collections.Generic;
// Game-free: the unload reason (job FBUFF2) is compiled into the test assembly too.
// IModStore (job FBUFF7) likewise: the own-grant ledger is plain data over the save store.
using Progenitor.Persistence;
using Progenitor.Plugins;

#if !PROGENITOR_TESTS
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Progenitor.Commands;
using Progenitor.Content;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;
#endif

namespace Progenitor.Debugging;

/// <summary>
/// Core's admin debug buff (<c>.debug on|off|status</c>). <c>DefineBuff</c> clones a visible
/// vanilla buff, a prefab editor sets drain to zero with an infinite lifetime and adds the
/// protections, and <c>.debug on</c> applies it through the same debug path as the Dev
/// <c>buff</c> op, then writes <c>Immortal</c> and takes the kill-path guard on the wearer. Moved
/// here from the Sample's <c>.nodrain</c> (requested 2026-10-06: "The debug buff should just be
/// part of Progenitor, not a plugin"). Parsing, the grant ledger and every verdict are game-free
/// so the test project compiles this file.
/// </summary>
internal static class DebugBuff
{
    /// <summary>
    /// Spawnable id hashed into the clone GUID. Must be identical on every peer. Neutral on
    /// purpose: it does not carry the framework's plugin id, which may be renamed.
    /// </summary>
    internal const string Id = "progenitor.debug";

    /// <summary>
    /// The Sample's id for this buff before it moved into Core. Nothing registers it any more;
    /// <c>.debug off</c> also removes a leftover instance of it (there is no automatic migration).
    /// </summary>
    internal const string LegacySampleId = "sample.nodrain";

    /// <summary>
    /// Vanilla clone source. A visible <c>Buff_General_*</c> that already carries
    /// <c>ShowOnBuffBar</c> (and client <c>ShowOnBuffBarData</c> with an icon). Immaterial
    /// is hidden. Resolved at apply time so this file does not hard-code a PrefabGUID hash.
    /// </summary>
    internal const string SourceSpawnableName = "Buff_General_Haste";

    /// <summary>
    /// HUD icon path. Core has no assets folder: the PNG is embedded in Core under the logical
    /// name <c>progenitor.content.icons.debug.png</c>, which the client icon applicator reads for
    /// a Core-owned definition (<c>BuffIconApply.TryReadPng</c>).
    /// </summary>
    internal const string IconRelativePath = "icons/debug.png";

    /// <summary>Localization key written onto <c>ShowOnBuffBarData.Entry.Header</c>.</summary>
    internal const string NameLocalizationKey = "progenitor.debug.name";

    /// <summary>Localization key written onto <c>ShowOnBuffBarData.Entry.Description.Key</c>.</summary>
    internal const string DescriptionLocalizationKey = "progenitor.debug.desc";

    /// <summary>
    /// English HUD title. <c>Named</c> registers this under <see cref="NameLocalizationKey"/>.
    ///
    /// <para>
    /// Named for what it <b>is</b> rather than for the first thing it did. The buff carries five
    /// protections — no blood drain, the three <c>BuffModificationTypes</c> bits, no aggro,
    /// survives death, plus <c>Immortal</c> and Core's kill-path guard on the character — and a
    /// player looking at one icon on their bar needs to know whose it is and that it is not
    /// something they earned. "No Drain" named the least of it and nothing at all about where it
    /// came from.
    /// </para>
    ///
    /// <para>
    /// The <see cref="Id"/> must not change once shipped: it is hashed into the clone's PrefabGUID
    /// and must be identical on every peer, so renaming it would orphan every buff instance
    /// already applied and every grant already recorded.
    /// </para>
    /// </summary>
    internal const string LocalizedName = "Debug";

    /// <summary>English HUD tooltip body. Fits the 1–63 ASCII wire slot (58 bytes).</summary>
    internal const string LocalizedDescription = "No blood drain; invulnerable, sun-immune, ignored by foes.";

    /// <summary>
    /// Source-prefab visual sequence components stripped so the clone keeps the icon and
    /// tooltip without haste's glow. Prefab carries <c>BuffSequence</c> (consumed by
    /// <c>BuffSequenceSystem_Spawn</c>); the others are stripped if present.
    /// Not on <c>DefineBuff</c>: this module's prefab editor owns this list.
    /// </summary>
    internal static readonly string[] SourceVisualComponentNames =
    {
        "BuffSequence",
        "BuffSequenceSpawned",
        "SpawnSequenceForEntity",
        "SequenceGUID",
    };

    // ------------------------------------------------------------------
    // The edit passes, as data. PatchPrefab / PatchInstance dispatch from these arrays, so what
    // a test asserts about them is what the live edit does — no source grep in between
    // (R30 "Test quality"). The split is load-bearing: StripSourceVisuals, WriteModificationFlags
    // and WriteDisableAggro must never run on a live instance.
    // ------------------------------------------------------------------

    internal const string StepLifeTime = "WriteLifeTime";
    internal const string StepDrainModifier = "WriteDrainModifier";
    internal const string StepPersistTag = "AddPersistTag";
    internal const string StepModificationFlags = "WriteModificationFlags";
    internal const string StepDisableAggro = "WriteDisableAggro";
    internal const string StepStripSpawnEvents = "StripSpawnEvents";
    internal const string StepStripSourceVisuals = "StripSourceVisuals";

    /// <summary>Character pass (job FBUFF5): add <c>ProjectM.Immortal</c> with <c>IsImmortal</c> true.</summary>
    internal const string StepImmortal = "WriteImmortal";

    /// <summary>Character pass (job FBUFF5): undo <see cref="StepImmortal"/>.</summary>
    internal const string StepClearImmortal = "ClearImmortal";

    /// <summary>Every step name <c>RunStep</c> knows. A name outside this set is a bug.</summary>
    internal static readonly string[] KnownPatchSteps =
    {
        StepLifeTime,
        StepDrainModifier,
        StepPersistTag,
        StepModificationFlags,
        StepDisableAggro,
        StepStripSpawnEvents,
        StepStripSourceVisuals,
        StepImmortal,
        StepClearImmortal,
    };

    /// <summary>Prefab-only pass, in order. Run once on the clone.</summary>
    internal static readonly string[] PrefabPatchSteps =
    {
        StepLifeTime,
        StepDrainModifier,
        StepPersistTag,
        StepModificationFlags,
        StepDisableAggro,
        StepStripSpawnEvents,
        StepStripSourceVisuals,
    };

    /// <summary>
    /// Post-<c>ApplyBuff</c> instance pass, in order. Prefab-safe components only: removing
    /// <c>BuffSequenceSpawned</c> from a live instance leaves the sequence playing (evidence
    /// 109), and rewriting the two modification components there races the <c>ModificationId</c>s
    /// the apply system assigned.
    /// </summary>
    internal static readonly string[] InstancePatchSteps =
    {
        StepLifeTime,
        StepDrainModifier,
        StepPersistTag,
        StepStripSpawnEvents,
    };

    /// <summary>
    /// Character pass, run once after <c>ApplyBuff</c>. These steps write the <b>wearer</b>, not
    /// the buff entity, so they must never appear in <see cref="PrefabPatchSteps"/> or
    /// <see cref="InstancePatchSteps"/>.
    /// </summary>
    internal static readonly string[] CharacterApplySteps =
    {
        StepImmortal,
    };

    /// <summary>
    /// Character pass, run on <b>every</b> removal path: <c>.debug off</c>, the
    /// <see cref="PluginUnloadReason.Removal"/> sweep, and the watchdog that catches a buff that
    /// ended some other way (Dev bridge <c>buff apply:false</c>, a game-side destroy).
    /// </summary>
    internal static readonly string[] CharacterRemoveSteps =
    {
        StepClearImmortal,
    };

    /// <summary>
    /// The prefab-tag gate on <see cref="StepStripSourceVisuals"/>. False on a live instance,
    /// where the strip would orphan <c>BuffSequenceSpawned</c>.
    /// </summary>
    internal static bool MayStripSourceVisuals(bool isPrefabEntity) => isPrefabEntity;

    /// <summary>The line the gate writes when it refuses.</summary>
    internal const string StripSkipLog =
        "edit skip StripSourceVisuals (not a prefab; would orphan BuffSequenceSpawned)";

    /// <summary>True when the strip pass removes this component off the source prefab.</summary>
    internal static bool StripsSourceVisual(string? componentName) =>
        componentName != null && Array.IndexOf(SourceVisualComponentNames, componentName) >= 0;

    /// <summary><see cref="ProjectM.ModifyBloodDrainBuff.BloodValue"/> written by the editor (Set to 0).</summary>
    internal const float DrainBloodValue = 0f;

    /// <summary><see cref="ProjectM.LifeTime.Duration"/> for an infinite buff (observed in game).</summary>
    internal const float InfiniteDuration = -1f;

    /// <summary><c>ModificationType.Set</c> as a byte. Game-free so tests can pin the intent.</summary>
    internal const byte ModificationTypeSet = 0;

    /// <summary><c>LifeTimeEndAction.None</c>. Game-free so tests can pin the intent.</summary>
    internal const int LifeTimeEndActionNone = 0;

    /// <summary>High so this clone wins over other <c>ModifyBloodDrainBuff</c> modifiers.</summary>
    internal const int DrainPriority = 1000;

    /// <summary>
    /// Component that carries the debug buff's invulnerability / immunity bits
    /// (<c>ProjectM.BuffModificationFlagData</c>, server metadata:
    /// <c>long ModificationTypes @0x0</c>, <c>ModificationId ModificationId @0x8</c>).
    /// The buff prefab holds the bits; <c>Apply_BuffModificationsSystem_Server</c>
    /// registers them into the wearer's networked
    /// <c>BuffableFlagState.Value</c> and fills <c>ModificationId</c>
    /// on the <b>instance</b> so <c>Destroy_BuffModificationsSystem_Server</c>
    /// can reverse them. Name kept game-free for the tests.
    /// </summary>
    internal const string ModificationFlagComponentName = "BuffModificationFlagData";

    /// <summary>
    /// <c>BuffModificationTypes.ImmuneToHazards</c> (server metadata,
    /// "immune to Hazards (Garlic, Holy Area, Curse)").
    /// </summary>
    internal const long ImmuneToHazardsFlag = 1L;

    /// <summary><c>BuffModificationTypes.ImmuneToSun</c> (server metadata).</summary>
    internal const long ImmuneToSunFlag = 2L;

    /// <summary><c>BuffModificationTypes.Invulnerable</c> (server metadata).</summary>
    internal const long InvulnerableFlag = 4L;

    /// <summary>
    /// Value written to <c>BuffModificationFlagData.ModificationTypes</c> on the clone.
    /// 7 is exactly what the game's own <c>Admin_Invulnerable_Buff</c> carries (read live
    /// through the Dev bridge <c>prefab</c> op: guid -480024072, ModificationTypes 7,
    /// and that vanilla buff also carries <c>ModifyBloodDrainBuff</c>).
    /// </summary>
    internal const long ModificationFlags = ImmuneToHazardsFlag | ImmuneToSunFlag | InvulnerableFlag;

    /// <summary>Human-readable flag list for the log line and the docs.</summary>
    internal const string ModificationFlagNames = "ImmuneToHazards|ImmuneToSun|Invulnerable";

    /// <summary>Vanilla buff whose <c>ModificationTypes</c> equals <see cref="ModificationFlags"/>.</summary>
    internal const string ModificationFlagVanillaSource = "Admin_Invulnerable_Buff";

    /// <summary>
    /// Component that makes hostile units ignore the wearer
    /// (<c>ProjectM.DisableAggroBuff</c>, server metadata: a single
    /// <c>DisableAggroBuffMode Mode</c> at 0x0). It belongs on the buff <b>prefab</b>;
    /// <c>Apply_BuffModificationsSystem_Server</c> (its LambdaJob_4 job struct, with
    /// <c>ComponentLookup&lt;Aggroable&gt;</c> and <c>ComponentLookup&lt;AggroConsumer&gt;</c>)
    /// registers the modification through <c>ModificationsRegistry</c>
    /// and adds <c>DisableAggroBuffModifications</c> (the two owning
    /// <c>ModificationId</c>s) to the <b>instance</b> so
    /// <c>Destroy_BuffModificationsSystem_Server</c> (its job struct and lambda)
    /// can reverse it.
    /// Name kept game-free for the tests.
    /// </summary>
    internal const string AggroComponentName = "DisableAggroBuff";

    /// <summary>
    /// <c>DisableAggroBuffMode.OthersDontAttackTarget</c> (server metadata). Clears the
    /// wearer's <c>Aggroable.Value</c> (<c>ModifiableBool</c>) so hostile
    /// units stop gaining aggro on it and drop it as a target.
    /// </summary>
    internal const int OthersDontAttackTargetMode = 1;

    /// <summary>
    /// <c>DisableAggroBuffMode.TargetDontAttackOthers</c> (server metadata). Clears the
    /// wearer's <c>AggroConsumer.Active</c> — the AI half. A player character
    /// has <c>Aggroable</c> but no <c>AggroConsumer</c> (read live off a test character),
    /// so this bit would be a no-op here; the debug buff does not set it.
    /// </summary>
    internal const int TargetDontAttackOthersMode = 2;

    /// <summary>Value written to <c>DisableAggroBuff.Mode</c> on the clone.</summary>
    internal const int AggroMode = OthersDontAttackTargetMode;

    /// <summary>Human-readable mode list for the log line and the docs.</summary>
    internal const string AggroModeNames = "OthersDontAttackTarget";

    /// <summary>
    /// Vanilla player-facing buff carrying the same <c>Mode</c>. Read live through the Dev
    /// bridge <c>prefab</c> op: guid 1462259032, <c>Mode</c> 1. The same value is on
    /// <c>Admin_Observe_Ghost_Buff</c> (77473184), <c>AB_Vampire_Slashers_Camouflage_Main_Buff</c>
    /// (252363754) and <c>AB_Shapeshift_Rat_Burrow_Buff</c>.
    /// </summary>
    internal const string AggroVanillaSource = "AB_Blood_BloodRite_SpellMod_Stealth";

    // ------------------------------------------------------------------
    // God mode (job FBUFF5).
    //
    // BuffModificationTypes.Invulnerable guards exactly one thing: the stat-change pipeline.
    // StatChangeMutationSystem.ApplyInvulnerableJob (server metadata) sets a negative
    // Health change to 0 when BuffableFlagState.Value has bit 4. The five direct kill utilities
    // never read BuffableFlagState at all, and the only *component* any
    // of them honors is ProjectM.Immortal (one field, `bool IsImmortal @0x0`),
    // read once, in StatChangeUtility.KillEntity(..., ref Health, in HealthConstants, ...,
    // bool killImmortals):
    //     if (TryGetComponentData<Immortal>(entity, out im) && im.IsImmortal && !killImmortals) return;
    // (the server's native ProjectM.StatChangeUtility.KillEntity, the 11-argument overload).
    // See docs/dev-loop.md for the three
    // kill paths no component can block (the 2-arg KillEntity, KillOrDestroyEntity without
    // Health, DeathUtilities.Kill) — those need a Harmony prefix, which Core does not offer yet.
    //
    // There is no data-driven "while this buff is on the target, the target has component X"
    // mechanism on this build: a search of the whole server metadata for Buff_ApplyComponent,
    // ModifyTargetComponent, AddComponentOnBuff, ApplyComponentOnBuff, AddComponentsOnBuff and
    // BuffAddComponent returns zero hits. Vanilla does it with a *compiled gameplay script*:
    // Script_RemoveImmortality_Buff.Server, whose [ServerScriptFunction(0)]
    // Spawn reads Buff.Target off the buff and calls
    // ServerGameManager.SetComponentImmediate<Immortal>(target, 0), and whose
    // [ServerScriptFunction(2)] Destroy calls the same with 1 — a "while the buff is on, the
    // target's Immortal is off" pair, the exact inverse of what we want (the server's native
    // ProjectM.Gameplay.Scripting.Script_RemoveImmortality_Buff.Server Spawn and Destroy).
    // A GameplayScriptBehaviour body is IL2CPP-compiled, so a mod cannot author one; the debug buff
    // does the add/set itself through the typed entity toolkit and undoes it on every removal
    // path. Note what vanilla does *not* do: it only ever Sets, guarded by TryGetComponent —
    // it never adds Immortal. A player character does not carry the component, so the debug buff
    // adds it, and is therefore the only thing that can take it away again.
    // ------------------------------------------------------------------

    /// <summary>
    /// The one component a direct kill honors (<c>ProjectM.Immortal</c>, server metadata:
    /// a single <c>bool IsImmortal @0x0</c>). Name kept game-free for the tests.
    /// </summary>
    internal const string ImmortalComponentName = "Immortal";

    /// <summary>
    /// This buff's god-mode guard owner label, distinct from
    /// the Sample's <c>.godmode</c> owner label (R40 finding 5). Guards are reference-counted per
    /// (entity, owner), so <c>.debug</c>'s clear drops only this label's hold: a character the
    /// <c>.godmode</c> debug command also guards stays guarded, and vice versa. Before this, one
    /// owner per entity plus an unconditional <c>Remove</c> meant either command's <c>off</c>
    /// silently unguarded a character the other still reported as protected.
    /// </summary>
    internal const string GuardOwnerLabel = "progenitor.debug";

    /// <summary>
    /// Value written to <c>Immortal.IsImmortal</c> while the buff is on. This is the same value
    /// the vanilla <see cref="ImmortalVanillaSource"/> script writes back in its
    /// <c>Destroy</c> (<c>SetComponentImmediate&lt;Immortal&gt;(Buff.Target, 1)</c>).
    /// </summary>
    internal const bool ImmortalValue = true;

    /// <summary>
    /// Vanilla source for the field value and for the buff-lifetime pattern:
    /// <c>ProjectM.Gameplay.Scripting.Script_RemoveImmortality_Buff.Server</c>
    /// (server metadata), <c>Spawn</c> writes <c>0</c> and <c>Destroy</c> writes <c>1</c>
    /// to <c>Immortal</c> on <c>Buff.Target</c>. There is no prefab-data equivalent.
    /// </summary>
    internal const string ImmortalVanillaSource = "Script_RemoveImmortality_Buff";

    /// <summary>Virtual system that catches a buff which ended without <c>.debug off</c>.</summary>
    internal const string ImmortalWatchdogName = "progenitor.debug.immortal";

    /// <summary>
    /// Watchdog period in simulation frames.
    ///
    /// Job FBUFF6 (R31 finding 10): the watchdog does not gate on a generation-scoped statics
    /// table, so it works after a swap, a reload, a restart and a crash, where
    /// <see cref="ImmortalHolders"/> is empty.
    ///
    /// Job FBUFF7 (R32 finding 2): its candidate set is the debug buff's <b>own grant ledger</b>
    /// (<see cref="GrantStoreKey"/> in ModSave), never a world-wide <c>Immortal</c> query. The
    /// old <c>All(Immortal, PlayerCharacter)</c> sweep deleted vanilla's and other mods'
    /// <c>Immortal</c> from any character that was not wearing <c>progenitor.debug</c>.
    /// The per-tick cost is one <c>PlayerCharacter</c> query used only to resolve the ledger's
    /// platform ids back to entities (bounded by the server's player slots) plus one
    /// <c>BuffBuffer</c> walk per <b>granted</b> character — normally zero.
    /// </summary>
    internal const int ImmortalWatchdogPeriodFrames = 60;

    /// <summary>
    /// How many consecutive reconcile passes may end with a read-back that <b>threw</b>
    /// <b>for one grant</b> before the reconcile stops retrying <b>that grant</b> (job FBUFF18,
    /// R46 finding 1; scope corrected by job FBUFF19, R47 finding 1).
    ///
    /// <para><b>Why an unreadable read-back is bounded and a wrong one is not.</b> The reconcile's
    /// retry exists because of evidence 166: at WorldReady nobody is connected, every granted
    /// character is unresolvable, and the pass must run again when the players arrive. That retry
    /// is unbounded on purpose and stays unbounded for the two cases where the next pass can
    /// genuinely differ — a grant whose character has not streamed in (no mutating pass runs at
    /// all for it), and an apply whose guard read back as <see cref="ReadBack.No"/> (the apply
    /// failed, and the next one may not).</para>
    ///
    /// <para>A read-back that threw is a different thing. Re-running
    /// <c>RunCharacterSteps(CharacterApplySteps)</c> — a mutating pass over the character, not a
    /// probe — because the result could not be <em>observed</em> is not evidence-gathering; it is
    /// the same write repeated at watchdog cadence forever, with the log lines to match, and
    /// nothing about the next pass makes the observation any more likely to work. So the budget
    /// is spent on it, and past the bound the reconcile gives up <b>out loud</b>
    /// (<see cref="FormatReconcileGaveUp"/>) rather than either retrying silently or latching as
    /// if it had succeeded.</para>
    ///
    /// <para><b>Why the counter is keyed by grant (job FBUFF19, R47 finding 1).</b> FBUFF18
    /// counted this per <em>pass</em> and stopped the reconcile per <em>module</em>, while the
    /// retries the paragraph above is careful not to bound are per <em>grant</em>. One character
    /// whose read-back threw every pass therefore ended the reconcile for everybody after about
    /// three seconds — including the grants whose retry is unbounded by design — and because this
    /// reconcile is the <b>only</b> automatic re-arm of the god-mode guard after a restart
    /// (evidence 162; the other two <c>CharacterApplySteps</c> call sites are both on
    /// <c>.debug on</c>, a player command), a player who connected after that was left with no
    /// kill-path guard and nothing said so. The budget now lives beside the evidence: keyed by the
    /// ledger's own key, spent by the grant whose read-back threw, and refilled for that grant by
    /// any pass it was readable on.</para>
    ///
    /// <para>Three is the same shape as <c>UiInputCapture.MaxChainExhaustions</c>, and for the
    /// same reason: enough passes that a one-off transient does not end the retry, few enough that
    /// a genuinely broken read-back is named within a handful of seconds instead of never.</para>
    /// </summary>
    internal const int MaxUnreadableReconcilePasses = 3;

    /// <summary>
    /// How many consecutive reconcile passes may <b>throw before finishing</b> before the
    /// reconcile stops running at all (job FBUFF19, R47 finding 1 — FBUFF18's declared residual).
    ///
    /// <para><b>Why this is a second constant and not the one above.</b> FBUFF18 recorded the
    /// outer <c>catch</c> of <see cref="ReconcileImmortalOnWorldReady"/> as the same species one
    /// level up — a pass that throws mid-loop has already run mutating steps for the rows it got
    /// through, and it repeats every watchdog period forever — and declined to fold it into the
    /// read-back budget because that would have made the give-up line's claim (<i>"the guard
    /// read-back threw"</i>) false for a pass that threw somewhere else entirely. That reasoning
    /// was right, and it is exactly an argument for a <b>separate</b> state with a separate
    /// sentence, which is what this is: <see cref="FormatReconcilePassGaveUp"/> states what this
    /// bound actually observed — the pass threw, some grants may have been mutated, and which
    /// ones is not recorded — and states none of the read-back line's claims.</para>
    ///
    /// <para>The two bounds are deliberately not shared even though they hold the same number. A
    /// per-grant read-back budget and a whole-pass throw budget are measurements of different
    /// things, and a future job that wants a throwing pass to be tolerated for longer must be able
    /// to move one without silently moving the other.</para>
    ///
    /// <para>Refilled by any pass that completes, including the two early returns (no world yet,
    /// nothing in the ledger) — those are ordinary outcomes, not throws.</para>
    /// </summary>
    internal const int MaxThrowingReconcilePasses = 3;

    // ------------------------------------------------------------------
    // The own-grant ledger (job FBUFF7, R32 finding 2).
    //
    // ProjectM.Immortal is a *shared vanilla component*: vanilla writes it from
    // Script_RemoveImmortality_Buff (see the god-mode block above), and it is the obvious
    // component for any other V Rising mod's god mode or an admin's invulnerability. The FBUFF6
    // code queried All(Immortal, PlayerCharacter) across the whole world and removed the
    // component from every row that was not a progenitor.debug wearer — at load, on every unload,
    // and once a second forever. That deletes other people's Immortal.
    //
    // The rule now: the debug buff removes only Immortal it granted itself. Each `.debug on`
    // records a grant keyed by the player's platform id AND the character's name (job FBUFF8,
    // R33 finding 6) — durable, unlike an Entity index, so a restart still knows, and specific to
    // one character, so a deleted-and-recreated character does not inherit the previous one's
    // grant — together with whether Immortal was ALREADY on the character when the grant was
    // made. A pre-existing component is somebody else's: the debug buff never writes it, never
    // removes it, and on clear drops only its own GodMode guard. A row that cannot prove it is
    // about the character in front of us is dropped, and that character is left alone.
    // ------------------------------------------------------------------

    /// <summary>
    /// ModSave key holding the <see cref="ImmortalGrantBook"/>. The file lives with the world
    /// save (docs/modules.md "Persistence"), so the ledger survives a swap, a reload and a
    /// restart — which is exactly when <see cref="ImmortalHolders"/> is empty and the old code
    /// fell back to "anything with Immortal must be mine".
    /// </summary>
    internal const string GrantStoreKey = "debug.immortal";

    /// <summary>
    /// The key the Sample's <c>.nodrain</c> kept its ledger under, in the Sample's own store. The
    /// rows have the same shape as <see cref="ImmortalGrantBook"/>.
    /// </summary>
    internal const string LegacyGrantStoreKey = "nodrain.immortal";

    /// <summary>
    /// Version of the prefab pass this module runs on the clone (<see cref="PrefabPatchSteps"/>
    /// and their values). It is part of the definition's live-refresh fingerprint, so bump it
    /// whenever the pass changes, and live instances are re-applied from the new clone.
    /// </summary>
    internal const string PrefabPassVersion = "1";

    /// <summary>
    /// Move the Sample's grant rows into this ledger (review of 2026-10-06). An <c>Immortal</c>
    /// the Sample granted is still on a character if the server stopped before the Sample's
    /// unload cleared it, and only a ledger row lets this module take it back; the row's
    /// <see cref="ImmortalGrant.PreExisting"/> flag is kept, so a component that pre-dated the
    /// Sample's grant is still never removed. A row for an account this ledger already has is
    /// left out (this ledger's row is newer). A row with no character identity can never be
    /// matched and is not copied. The legacy key is removed only after this ledger was written.
    /// Returns how many rows were copied.
    /// </summary>
    internal static int MigrateLegacyGrants(IModStore? legacy, IModStore? store)
    {
        if (legacy == null || store == null) return 0;
        try
        {
            var old = legacy.Get(LegacyGrantStoreKey, new ImmortalGrantBook()) ?? new ImmortalGrantBook();
            old.Grants ??= new List<ImmortalGrant>();
            var book = LoadGrants(store);
            int copied = 0, skipped = 0;
            foreach (var row in old.Grants)
            {
                if (row == null || string.IsNullOrEmpty(row.CharacterId)) { skipped++; continue; }
                if (string.IsNullOrEmpty(row.Identity)) { skipped++; continue; }
                bool present = false;
                foreach (var mine in book.Grants)
                    if (string.Equals(mine.CharacterId, row.CharacterId, StringComparison.Ordinal)) { present = true; break; }
                if (present) { skipped++; continue; }
                book.Grants.Add(new ImmortalGrant
                {
                    CharacterId = row.CharacterId,
                    Identity = row.Identity,
                    PreExisting = row.PreExisting,
                });
                copied++;
            }
            if (copied > 0) store.Set(GrantStoreKey, book);
            legacy.Remove(LegacyGrantStoreKey);
            Log(FormatLegacyMigrated(copied, skipped));
            return copied;
        }
        catch (Exception e)
        {
            Log("legacy grant migration threw: " + e.Message);
            return 0;
        }
    }

    /// <summary>Grep <c>migrated the Sample's grant ledger</c>.</summary>
    internal static string FormatLegacyMigrated(int copied, int skipped) =>
        "migrated the Sample's grant ledger (" + LegacyGrantStoreKey + "): copied " + copied
        + " row(s), skipped " + skipped + " (already here or no character identity)";

    /// <summary>Every outstanding <c>Immortal</c> grant this plugin has made.</summary>
    internal sealed class ImmortalGrantBook
    {
        public List<ImmortalGrant> Grants { get; set; } = new();
    }

    /// <summary>
    /// One outstanding grant. <see cref="CharacterId"/> is the wearer's platform id (Steam id)
    /// rather than an <c>Entity</c>, because entity indices are re-issued across a save reload
    /// and the point of the ledger is to survive one. <see cref="PreExisting"/> records that
    /// <c>ProjectM.Immortal</c> was already on the character when <c>.debug on</c> ran — a
    /// component the debug buff did not write and must never remove.
    ///
    /// <see cref="Identity"/> is the second half of the key (job FBUFF8, R33 finding 6). The
    /// platform id is the <b>account</b>, and a V Rising character can be deleted and recreated
    /// on the same account (an admin wipe, a hardcore death, a character reset). Keyed on the
    /// account alone, a stale row makes the reconcile answer "granted" for a brand-new character
    /// and delete an <c>Immortal</c> the debug buff never wrote — R32 finding 2's exact failure,
    /// reached without any world query at all. The recorded identity is the character's
    /// <b>name</b>:
    /// <list type="bullet">
    /// <item><c>PlayerCharacter.Name</c> (<c>FixedString64Bytes</c> @0x0, server metadata)
    /// on the character entity itself, mirrored by <c>User.CharacterName</c>
    /// (<c>FixedString64Bytes</c> @0x40). Both are ordinary persisted
    /// component fields, so they survive a save reload, and a recreated character goes through
    /// character creation and gets a new one.</item>
    /// <item><b>Not</b> <c>ProjectM.Network.NetworkId</c>: its
    /// <c>Normal_Index</c> / <c>Normal_Generation</c> pair is handed out at runtime by
    /// <c>NetworkIdSystem</c> and resolved through <c>NetworkIdLookupMap</c>
    /// — a session-local allocation that does not survive a reload, which is the
    /// half of the test the ledger exists to pass.</item>
    /// <item><b>Not</b> a creation stamp: <c>PlayerCharacter</c> has exactly three fields
    /// (<c>Name</c>, <c>SmartClanName</c>, <c>UserEntity</c>) and this build carries no
    /// per-character creation timestamp or persistent GUID at all (the only persisted GUIDs are
    /// <c>PrefabGUID</c> and authoring-time <c>EntityGuid</c>).</item>
    /// </list>
    /// Residual: a name is freed when the character is deleted, so a player who recreates under
    /// the <b>same</b> name still inherits the row. That is the best identity this build exposes;
    /// it narrows the finding from "every recreation" to "a recreation that reuses the name", and
    /// every other mismatch falls through to "leave it alone". A row with an empty identity (one
    /// written by FBUFF7, before this field existed) cannot prove it is about this character, so
    /// it is treated as a mismatch and dropped rather than trusted.
    /// </summary>
    internal sealed class ImmortalGrant
    {
        public string CharacterId { get; set; } = "";
        public bool PreExisting { get; set; }
        public string Identity { get; set; } = "";
    }

    /// <summary>The ledger, never null. A missing key, a null store or a corrupt value is empty.</summary>
    internal static ImmortalGrantBook LoadGrants(IModStore? store)
    {
        if (store == null) return new ImmortalGrantBook();
        try
        {
            var book = store.Get(GrantStoreKey, new ImmortalGrantBook()) ?? new ImmortalGrantBook();
            book.Grants ??= new List<ImmortalGrant>();
            for (int i = book.Grants.Count - 1; i >= 0; i--)
                if (book.Grants[i] == null || string.IsNullOrEmpty(book.Grants[i].CharacterId))
                    book.Grants.RemoveAt(i);
            return book;
        }
        catch (Exception e)
        {
            Log("grant book read threw: " + e.Message);
            return new ImmortalGrantBook();
        }
    }

    /// <summary>
    /// Record a grant. An id already in the book keeps its original
    /// <see cref="ImmortalGrant.PreExisting"/> flag: re-running the apply steps on a character
    /// the debug buff already made immortal (the world-ready adopt) must not re-read the component's
    /// presence and conclude its own write was somebody else's.
    /// </summary>
    internal static bool RecordGrant(IModStore? store, string? characterId, string? identity, bool preExisting)
    {
        if (store == null || string.IsNullOrEmpty(characterId)) return false;
        // Job FBUFF9 (R34 finding 6): a row with no identity can never match anything, so it can
        // never be recognized as its own again — and the component written next to it
        // could never be removed. Refuse the row instead of writing an unmatchable one.
        if (!CanRecordGrant(characterId, identity))
        {
            Log(FormatGrantRefused(characterId!));
            return false;
        }
        try
        {
            var book = LoadGrants(store);
            for (int i = 0; i < book.Grants.Count; i++)
            {
                if (book.Grants[i].CharacterId != characterId) continue;
                if (!IdentityMatches(book.Grants[i].Identity, identity))
                {
                    // Job FBUFF9 (R34 finding 6): same account, but the row is about a character
                    // it no longer has (a recreation), or it is an identity-less FBUFF7 row.
                    // Replace it whole — identity AND the pre-existing flag — because the caller
                    // has just read that flag off the character in front of it. Keeping the old
                    // flag is what latched a foreign Immortal as "ours, pre-existing" forever.
                    string stale = book.Grants[i].Identity ?? "";
                    book.Grants[i].Identity = identity!;
                    book.Grants[i].PreExisting = preExisting;
                    store.Set(GrantStoreKey, book);
                    Log(FormatGrantReplaced(characterId!, stale, identity!, preExisting));
                    return true;
                }

                // The same character: keep the row exactly as it is. Never re-stamp the
                // PreExisting flag — re-running the apply steps on a character the debug buff
                // already made immortal (the world-ready adopt) must not re-read the
                // component's presence and conclude its own write was somebody else's.
                return false;
            }
            book.Grants.Add(new ImmortalGrant
            {
                CharacterId = characterId!,
                PreExisting = preExisting,
                Identity = identity!,
            });
            store.Set(GrantStoreKey, book);
            Log(FormatGrantRecorded(characterId!, identity ?? "", preExisting));
            return true;
        }
        catch (Exception e)
        {
            Log("grant record threw: " + e.Message);
            return false;
        }
    }

    /// <summary>Drop a grant once the component (or the guard) is gone. False when it was not there.</summary>
    internal static bool ForgetGrant(IModStore? store, string? characterId)
    {
        if (store == null || string.IsNullOrEmpty(characterId)) return false;
        try
        {
            var book = LoadGrants(store);
            for (int i = 0; i < book.Grants.Count; i++)
            {
                if (book.Grants[i].CharacterId != characterId) continue;
                book.Grants.RemoveAt(i);
                store.Set(GrantStoreKey, book);
                Log(FormatGrantForgotten(characterId!));
                return true;
            }
            return false;
        }
        catch (Exception e)
        {
            Log("grant forget threw: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Keep or forget one grant according to <see cref="ShouldKeepGrant"/>. Production
    /// <c>ClearImmortal</c> calls this with the OnUnload reason; <c>.debug off</c> and the
    /// watchdog pass null and forget. True when the row was kept (or forgotten successfully).
    /// </summary>
    internal static bool KeepOrForgetGrant(IModStore? store, string? characterId, PluginUnloadReason? reason)
    {
        if (string.IsNullOrEmpty(characterId)) return false;
        if (ShouldKeepGrant(reason))
        {
            Log(FormatGrantKept(characterId, reason));
            return true;
        }
        return ForgetGrant(store, characterId);
    }

    /// <summary>
    /// The raw row lookup: is there a row for this account, and what does it say? Callers want
    /// <see cref="TryGetGrant(IModStore?, string?, string?, out bool)"/>, which also checks the
    /// row is about the character in front of it.
    /// </summary>
    internal static bool TryGetGrantRow(
        IModStore? store, string? characterId, out bool preExisting, out string identity)
    {
        preExisting = false;
        identity = "";
        if (store == null || string.IsNullOrEmpty(characterId)) return false;
        var book = LoadGrants(store);
        for (int i = 0; i < book.Grants.Count; i++)
        {
            if (book.Grants[i].CharacterId != characterId) continue;
            preExisting = book.Grants[i].PreExisting;
            identity = book.Grants[i].Identity ?? "";
            return true;
        }
        return false;
    }

    /// <summary>
    /// Can a grant for this character be written down at all (job FBUFF9, R34 finding 6)? The
    /// account id alone is not enough: a row with no character identity can never match anything
    /// afterwards, so the <c>Immortal</c> written beside it could never be recognized as the
    /// debug buff's and never removed. <c>.debug on</c> and <c>WriteImmortal</c> both refuse rather
    /// than write a component they could not take back.
    /// </summary>
    internal static bool CanRecordGrant(string? characterId, string? identity) =>
        !string.IsNullOrEmpty(characterId) && !string.IsNullOrEmpty(identity);

    /// <summary>
    /// Do a recorded and a live character identity name the same character? Ordinal, and an
    /// empty one on either side is never a match: a row that cannot prove which character it is
    /// about is not evidence of a grant (job FBUFF8, R33 finding 6). It is not evidence of a
    /// <i>mismatch</i> either — that distinction is <see cref="LookupGrant"/>'s (job FBUFF9).
    /// </summary>
    internal static bool IdentityMatches(string? recorded, string? live) =>
        !string.IsNullOrEmpty(recorded)
        && !string.IsNullOrEmpty(live)
        && string.Equals(recorded, live, StringComparison.Ordinal);

    /// <summary>
    /// What the ledger can say about the character in front of the caller. Three answers, not
    /// two (job FBUFF9, R34 finding 6): "no row", "this row is about a different character" and
    /// "I cannot tell" are different facts and lead to different actions.
    /// </summary>
    internal enum GrantLookup
    {
        /// <summary>No row for this account. Nothing was granted; nothing to drop.</summary>
        None = 0,

        /// <summary>A row, and its identity is provably this character's. The grant is ours.</summary>
        Ours = 1,

        /// <summary>
        /// A row, both identities readable, and they name different characters: the account
        /// recreated its character. Not a grant for this one, and the row is droppable — by the
        /// reconcile, through <see cref="DropGrant"/>, never by a read.
        /// </summary>
        Recreated = 2,

        /// <summary>
        /// One of the two identities could not be read (an empty live name, or an FBUFF7 row
        /// written before the field existed). Identity unknown: <b>never a match and never a
        /// mismatch</b>. Nothing is claimed, nothing is dropped, and the next pass — which may
        /// read the name fine — decides instead.
        /// </summary>
        Unknown = 3,
    }

    /// <summary>
    /// The ledger's answer for one character. A <b>pure read</b>: it never writes the store, so
    /// calling it twice returns the same row (job FBUFF9, R34 finding 6 — the FBUFF8 version
    /// deleted the row as a side effect of reading it, which turned one unreadable-name frame
    /// into a permanently unremovable <c>Immortal</c>).
    /// </summary>
    internal static GrantLookup LookupGrant(
        IModStore? store,
        string? characterId,
        string? liveIdentity,
        out bool preExisting,
        out string recorded)
    {
        if (!TryGetGrantRow(store, characterId, out preExisting, out recorded))
        {
            preExisting = false;
            return GrantLookup.None;
        }
        if (IdentityMatches(recorded, liveIdentity)) return GrantLookup.Ours;
        preExisting = false;
        return string.IsNullOrEmpty(recorded) || string.IsNullOrEmpty(liveIdentity)
            ? GrantLookup.Unknown
            : GrantLookup.Recreated;
    }

    /// <summary>
    /// Is this character one of ours, and was its <c>Immortal</c> already there? A pure read:
    /// only <see cref="GrantLookup.Ours"/> is true, and no other answer changes the ledger.
    /// Callers that have to <b>act</b> on a stale row go through
    /// <see cref="DecideForCharacter"/> and <see cref="DropGrant"/> instead.
    /// </summary>
    internal static bool TryGetGrant(
        IModStore? store, string? characterId, string? liveIdentity, out bool preExisting) =>
        LookupGrant(store, characterId, liveIdentity, out preExisting, out _) == GrantLookup.Ours;

    /// <summary>
    /// Drop a ledger row the reconcile has decided is stale — the one explicit, logged deletion
    /// (job FBUFF9). Only the world-ready reconcile calls it, and only after
    /// <see cref="DecideForCharacter"/> answered <see cref="ImmortalPlan.Foreign"/> with
    /// <c>dropRow</c> set: the account resolved to a live character the row demonstrably is not
    /// about. An unresolved row, and a row whose identity could not be read, both survive.
    /// </summary>
    internal static bool DropGrant(IModStore? store, string? characterId, string? live)
    {
        if (store == null || string.IsNullOrEmpty(characterId)) return false;
        if (!TryGetGrantRow(store, characterId, out _, out var recorded)) return false;
        Log(FormatGrantDropped(characterId!, recorded, live));
        return ForgetGrant(store, characterId);
    }

    /// <summary>
    /// The whole ownership question for one character, game-free: the ledger row, the identity
    /// check and <see cref="PlanImmortal"/> in one call. The world-ready reconcile, the watchdog
    /// and <c>ClearImmortal</c> all go through it, so the recreated-character case is decided in
    /// exactly one place and the tests can drive it without a world.
    ///
    /// Job FBUFF9: deciding does not delete. <paramref name="dropRow"/> is the decision's
    /// recommendation — true only for a row that is provably about a different character — and
    /// the reconcile is the one caller that acts on it, by calling <see cref="DropGrant"/>. An
    /// identity that could not be read is <see cref="GrantLookup.Unknown"/>: logged, planned as
    /// <see cref="ImmortalPlan.Foreign"/> so nothing is touched, and <b>not</b> dropped.
    /// </summary>
    internal static ImmortalPlan DecideForCharacter(
        IModStore? store,
        string? characterId,
        string? liveIdentity,
        bool wearsBuff,
        out bool preExisting,
        out bool dropRow)
    {
        dropRow = false;
        var lookup = LookupGrant(store, characterId, liveIdentity, out preExisting, out var recorded);
        switch (lookup)
        {
            case GrantLookup.Recreated:
                Log(FormatGrantIdentityMismatch(characterId ?? "", recorded, liveIdentity));
                dropRow = true;
                break;
            case GrantLookup.Unknown:
                Log(FormatGrantIdentityUnknown(characterId ?? "", recorded, liveIdentity));
                break;
        }

        bool granted = lookup == GrantLookup.Ours;
        if (!granted) preExisting = false;
        return PlanImmortal(granted, preExisting, wearsBuff);
    }

    /// <summary>Every id in the ledger, in insertion order. The reconcile's whole candidate set.</summary>
    internal static List<string> GrantIds(IModStore? store)
    {
        var ids = new List<string>();
        var book = LoadGrants(store);
        for (int i = 0; i < book.Grants.Count; i++)
            ids.Add(book.Grants[i].CharacterId);
        return ids;
    }

    /// <summary>What the reconcile / watchdog / clear does with one character's <c>Immortal</c>.</summary>
    internal enum ImmortalPlan
    {
        /// <summary>Not in the ledger. Vanilla's or another mod's — never touched.</summary>
        Foreign = 0,

        /// <summary>Ours and the buff is still on: leave it, re-assert the guard.</summary>
        Keep = 1,

        /// <summary>Ours, we added the component, the buff is gone: remove it.</summary>
        Remove = 2,

        /// <summary>Ours but the component pre-dated the grant: drop only the guard.</summary>
        DropGuardOnly = 3,
    }

    /// <summary>
    /// The one rule (job FBUFF7). <paramref name="granted"/> is "this id is in its own
    /// ledger"; it is the only thing that can make the debug buff touch a character's
    /// <c>Immortal</c>. Game-free so the reconcile, the watchdog and <c>ClearImmortal</c> all
    /// decide the same way and the tests pin it once.
    /// </summary>
    internal static ImmortalPlan PlanImmortal(bool granted, bool preExisting, bool wearsBuff)
    {
        if (!granted) return ImmortalPlan.Foreign;
        if (wearsBuff) return ImmortalPlan.Keep;
        return preExisting ? ImmortalPlan.DropGuardOnly : ImmortalPlan.Remove;
    }

    /// <summary>Grep <c>grant recorded</c>.</summary>
    internal static string FormatGrantRecorded(string characterId, string identity, bool preExisting) =>
        "grant recorded character=" + characterId + " identity=" + identity
        + " preExisting=" + preExisting
        + (preExisting
            ? " (foreign " + ImmortalComponentName + "; never removed, guard only)"
            : " (ours; removed on clear)");

    /// <summary>Grep <c>grant forgotten</c>.</summary>
    internal static string FormatGrantForgotten(string characterId) =>
        "grant forgotten character=" + characterId;

    /// <summary>Grep <c>grant kept</c> on the unload keep branch.</summary>
    internal static string FormatGrantKept(string characterId, PluginUnloadReason? reason) =>
        "grant kept character=" + characterId
        + " (unload reason=" + reason
        + "; next generation reconciles from the ledger)";

    /// <summary>
    /// The line the recreated-character case writes (job FBUFF8, R33 finding 6). Grep
    /// <c>grant identity mismatch</c>: the account matched, the character did not, so this
    /// character is treated as having no grant at all. Job FBUFF9: the observation is all this
    /// line is — the deletion is <see cref="FormatGrantDropped"/>, one line further on, and only
    /// from the reconcile.
    /// </summary>
    internal static string FormatGrantIdentityMismatch(
        string characterId, string? recorded, string? live) =>
        "grant identity mismatch character=" + characterId
        + " recorded=" + (string.IsNullOrEmpty(recorded) ? "(none)" : recorded)
        + " live=" + (string.IsNullOrEmpty(live) ? "(none)" : live)
        + " (character recreated; no grant for this one, "
        + ImmortalComponentName + " left alone)";

    /// <summary>
    /// The identity-unknown line (job FBUFF9, R34 finding 6). Grep <c>grant identity unknown</c>:
    /// one of the two names could not be read, so this pass can prove neither a match nor a
    /// mismatch. The row is <b>kept</b> and nothing is touched; a later pass that can read the
    /// name decides. A read that deleted here stranded an <c>Immortal</c> forever.
    /// </summary>
    internal static string FormatGrantIdentityUnknown(
        string characterId, string? recorded, string? live) =>
        "grant identity unknown character=" + characterId
        + " recorded=" + (string.IsNullOrEmpty(recorded) ? "(none)" : recorded)
        + " live=" + (string.IsNullOrEmpty(live) ? "(none)" : live)
        + " (identity unreadable this pass; row kept, no match and no mismatch, "
        + ImmortalComponentName + " left alone)";

    /// <summary>
    /// The one line a deletion writes (job FBUFF9). Grep <c>grant dropped</c>: the reconcile
    /// decided <c>Foreign</c> for a row it resolved to a character the row is not about, and
    /// <see cref="DropGrant"/> removed it. No read path writes this line.
    /// </summary>
    internal static string FormatGrantDropped(string characterId, string? recorded, string? live) =>
        "grant dropped character=" + characterId
        + " recorded=" + (string.IsNullOrEmpty(recorded) ? "(none)" : recorded)
        + " live=" + (string.IsNullOrEmpty(live) ? "(none)" : live)
        + " (reconcile decided Foreign for a recreated character)";

    /// <summary>
    /// The refusal line (job FBUFF9). Grep <c>grant refused</c>: no readable character identity,
    /// so no row can be written — and therefore no <c>Immortal</c> is written either.
    /// </summary>
    internal static string FormatGrantRefused(string characterId) =>
        "grant refused character=" + characterId
        + " (no readable character identity; a row that cannot be matched could never be removed)";

    /// <summary>
    /// The replacement line (job FBUFF9). Grep <c>grant replaced</c>: the same account granted
    /// again for a different character, so the stale row's identity <b>and</b> its pre-existing
    /// flag are both re-read from the character in front of the code.
    /// </summary>
    internal static string FormatGrantReplaced(
        string characterId, string? stale, string identity, bool preExisting) =>
        "grant replaced character=" + characterId
        + " stale=" + (string.IsNullOrEmpty(stale) ? "(none)" : stale)
        + " identity=" + identity
        + " preExisting=" + preExisting;

    /// <summary>
    /// The line <c>.debug on</c> and <c>WriteImmortal</c> write instead of granting
    /// (job FBUFF9, R34 finding 6). Grep <c>refusing to write Immortal</c>.
    /// </summary>
    internal static string FormatImmortalWriteRefused(string who) =>
        "refusing to write " + ImmortalComponentName + " character=" + who
        + " (identity unknown; a grant that cannot be recorded could never be removed)";

    /// <summary>
    /// Reply <c>.debug on</c> sends when the character's name cannot be read, so the grant
    /// cannot be recorded (job FBUFF9). Nothing was applied and nothing was changed.
    /// </summary>
    internal const string OnIdentityUnknownReply =
        "Could not read your character's name, so the debug buff was not applied. "
        + "Nothing was changed - try again in a moment.";

    /// <summary>Per-decision line. Grep <c>decision=</c>.</summary>
    internal static string FormatImmortalDecision(string characterId, ImmortalPlan plan) =>
        "character=" + characterId + " decision=" + plan;

    /// <summary>
    /// The line every path writes instead of removing something it did not grant. Grep
    /// <c>leaving foreign Immortal</c>.
    /// </summary>
    internal static string FormatForeignImmortal(string characterId) =>
        "leaving foreign " + ImmortalComponentName + " alone character=" + characterId
        + " (no " + Id + " grant recorded)";

    /// <summary>
    /// The candidates that must lose <c>Immortal</c>: everyone in <paramref name="candidates"/>
    /// who is not wearing the buff. Game-free so the watchdog's and the load reconcile's rule is
    /// the one the tests pin. <paramref name="wearsBuff"/> null means "nobody wears it".
    ///
    /// Job FBUFF7: the caller must hand in <b>only characters in its own ledger</b>
    /// (<see cref="GrantIds"/>). This function decides "the buff ended", not "this is mine";
    /// <see cref="PlanImmortal"/> owns the ownership half.
    /// </summary>
    internal static System.Collections.Generic.List<T> PlanImmortalClears<T>(
        System.Collections.Generic.IReadOnlyList<T>? candidates,
        Func<T, bool>? wearsBuff)
    {
        var stale = new System.Collections.Generic.List<T>();
        if (candidates == null) return stale;
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            bool wears = false;
            if (wearsBuff != null)
            {
                try { wears = wearsBuff(c); }
                catch (Exception e) { Log("plan clears predicate threw: " + e.Message); }
            }
            if (!wears) stale.Add(c);
        }
        return stale;
    }

    /// <summary>
    /// Exact line the unconditional unload clear writes (job FBUFF6, R31 finding 3). Grep
    /// <c>unload cleared Immortal</c>. Zero is logged: the operator needs to see the clear ran on
    /// a Swap and a Shutdown too.
    /// </summary>
    internal static string FormatUnloadCleared(PluginUnloadReason reason, int cleared) =>
        "unload reason=" + reason + ", cleared Immortal + god-mode guard from "
        + cleared + " character(s)";

    // ------------------------------------------------------------------
    // FBUFF17 — the one rule every verdict in this file obeys
    // ------------------------------------------------------------------

    /// <summary>
    /// <b>A success verdict is derived from a read-back, never from the absence of a thrown
    /// exception.</b>
    ///
    /// <para>Every mutating path in this module is a <c>void</c> call that swallows everything.
    /// <see cref="RunCharacterSteps"/> returns nothing; <c>WriteImmortal</c> has three paths
    /// that never reach <c>GodMode.Add</c> (no live character, the FBUFF9 refusal when the
    /// account+character key cannot be read, and its own outer catch) and <c>ClearImmortal</c>
    /// has three that never reach the component. "It did not throw" is therefore evidence of
    /// nothing, and evidence 162 is what that costs: a wearer with the buff icon, the flags and
    /// <c>Immortal</c>, no direct-kill protection at all, and a line asserting the apply
    /// succeeded.</para>
    ///
    /// <para><b>Three states, not two.</b> "I could not tell" is not "it is off". Code that only
    /// has a bool must pick one of them as the failure default and is wrong half the time — R43
    /// finding 7 and R45 finding 18 are both that choice made wrongly.</para>
    ///
    /// <para><b>Two consequences, both enforced here rather than restated at each call site.</b>
    /// A verdict may claim an outcome only for the matching <see cref="Yes"/>/<see cref="No"/>
    /// (<see cref="Verified"/>), and <b>a count that gates a latch may only be incremented for
    /// work that was read back</b> (<see cref="TallyReconcileRow"/>). Where a path cannot read
    /// back at all, the line the operator reads says so — <c>?</c>, <c>UNREADABLE</c>, "could
    /// not read" — and never "ok".</para>
    /// </summary>
    internal enum ReadBack
    {
        /// <summary>The read threw. Says nothing about the world, and verifies nothing.</summary>
        Unreadable = 0,

        /// <summary>Read, and the thing is not there. A genuine answer.</summary>
        No = 1,

        /// <summary>Read, and the thing is there. A genuine answer.</summary>
        Yes = 2,
    }

    /// <summary>
    /// Fold this file's existing <c>xReadable</c>/<c>x</c> pairs into <see cref="ReadBack"/>, so
    /// the pair shape stays where it reads well (<see cref="FormatStatusLines"/>) without a
    /// second copy of the three-state rule living beside it.
    /// </summary>
    internal static ReadBack ToReadBack(bool readable, bool value) =>
        !readable ? ReadBack.Unreadable : value ? ReadBack.Yes : ReadBack.No;

    /// <summary>
    /// Whether a read-back <b>establishes</b> <paramref name="expected"/>. The load-bearing row
    /// of the table is the third one: <see cref="ReadBack.Unreadable"/> verifies neither
    /// direction, so it can never be folded into "well, it probably worked" — which is the
    /// single move every finding this rule answers actually made.
    /// </summary>
    internal static bool Verified(ReadBack readBack, bool expected) =>
        readBack == (expected ? ReadBack.Yes : ReadBack.No);

    /// <summary>Render a read-back for an operator line: <c>True</c>, <c>False</c> or <c>?</c>.</summary>
    internal static string Show(ReadBack readBack) => readBack switch
    {
        ReadBack.Yes => "True",
        ReadBack.No => "False",
        _ => "?",
    };

    /// <summary>Which counter of <see cref="FormatReconcile"/> a reconcile row lands in.</summary>
    internal enum ReconcileTally
    {
        /// <summary>The guard was read back as held after the apply pass.</summary>
        Adopted = 0,

        /// <summary>The guard was read back as dropped after the remove pass.</summary>
        Removed = 1,

        /// <summary>Same read-back as <see cref="Removed"/>; a pre-existing component was kept.</summary>
        ForeignKept = 2,

        /// <summary>Nothing was established. The latch stays open and the watchdog retries.</summary>
        Unresolved = 3,

        /// <summary>The row was proved to be about a character this account no longer has.</summary>
        Dropped = 4,

        /// <summary>
        /// The pass ran and the read-back <b>threw</b> (job FBUFF18, R46 finding 1). Counted
        /// unresolved like <see cref="Unresolved"/> — an unreadable row may never close the latch,
        /// which is FBUFF17's whole rule — but kept apart from it because the two deserve
        /// different retries. <see cref="Unresolved"/> means "the world said no", and the next
        /// pass may say yes. This means "we could not see what the world said", and the next pass
        /// has no more chance of seeing it; retrying a <em>mutating</em> pass on that basis is
        /// what <see cref="MaxUnreadableReconcilePasses"/> bounds.
        /// </summary>
        Unreadable = 5,
    }

    /// <summary>
    /// R45 finding 3 — the reason this function exists at all.
    ///
    /// <para><c>69d528e</c> moved the reconcile latch from "the pass ran" to "nothing was left
    /// unresolved", which was right, and left <c>adopted++</c> sitting unconditionally after a
    /// <c>void</c>, everything-swallowing <see cref="RunCharacterSteps"/>. So the count the
    /// latch is computed from was still incremented for work nobody read back: on a Core swap
    /// where <c>TryCharacterId</c> cannot read the character name, <c>WriteImmortal</c> takes
    /// the FBUFF9 refusal, <c>GodMode.Add</c> never runs, no guard is held —
    /// <c>unresolved</c> stays 0, the latch closes for the rest of the generation, and the
    /// summary line says <c>adopted 1</c>. That is evidence 162's state with a success message
    /// printed over it.</para>
    ///
    /// <para>The read-back is the same one FBUFF16 (<c>77e74b7</c>) added to <c>.debug on</c>'s
    /// already-on re-arm three lines away — <c>GodMode.IsGuardedBy(character,
    /// DebugGuardOwner)</c> — because the adopt <em>is</em> that re-arm, from the same steps
    /// array. It is a complete proof for the apply direction: <c>GodMode.Add</c> is the last
    /// effectful statement of <c>WriteImmortal</c>, so a held guard means every earlier step ran
    /// too. For the remove direction it is a proof of the half that matters and no more:
    /// <c>GodMode.Remove</c> is the <em>first</em> statement of <c>ClearImmortal</c>, so a
    /// dropped guard proves the character is killable again but not that the component was
    /// taken back — which is why the summary line is a count and <c>.debug status</c> is the
    /// per-protection readout.</para>
    ///
    /// <para><see cref="ImmortalPlan.Foreign"/> touches nothing on the character, so it needs no
    /// read-back and ignores one: its evidence is <paramref name="dropRow"/>, the identity that
    /// proved the row is about somebody else.</para>
    /// </summary>
    /// <remarks>
    /// Job FBUFF18 (R46 finding 1) split the failure column in two without moving the boundary
    /// FBUFF17 drew: <see cref="ReconcileTally.Unreadable"/> is still counted unresolved by every
    /// caller and still holds the latch open, so nothing here can close a latch over work that was
    /// not read back. What it adds is which <em>kind</em> of failure the row was, because a
    /// read-back that threw is the one failure whose retry cannot improve its own evidence.
    /// </remarks>
    internal static ReconcileTally TallyReconcileRow(
        ImmortalPlan plan, ReadBack guardAfterSteps, bool dropRow) => plan switch
    {
        ImmortalPlan.Keep =>
            Verified(guardAfterSteps, expected: true) ? ReconcileTally.Adopted : UnresolvedKind(guardAfterSteps),
        ImmortalPlan.Remove =>
            Verified(guardAfterSteps, expected: false) ? ReconcileTally.Removed : UnresolvedKind(guardAfterSteps),
        ImmortalPlan.DropGuardOnly =>
            Verified(guardAfterSteps, expected: false) ? ReconcileTally.ForeignKept : UnresolvedKind(guardAfterSteps),
        // Foreign touches nothing on the character and takes no read-back, so it can never be
        // Unreadable: its evidence is dropRow, and a row that did not establish an identity is an
        // ordinary unresolved row that the next pass may well resolve.
        ImmortalPlan.Foreign =>
            dropRow ? ReconcileTally.Dropped : ReconcileTally.Unresolved,
        _ => ReconcileTally.Unresolved,
    };

    /// <summary>
    /// Which of the two failure tallies an unverified row lands in: the read-back threw
    /// (<see cref="ReconcileTally.Unreadable"/>) or the read-back answered the other way
    /// (<see cref="ReconcileTally.Unresolved"/>).
    /// </summary>
    private static ReconcileTally UnresolvedKind(ReadBack guardAfterSteps) =>
        guardAfterSteps == ReadBack.Unreadable ? ReconcileTally.Unreadable : ReconcileTally.Unresolved;

    /// <summary>
    /// Whether a tally holds the reconcile latch open. Both failure kinds do — FBUFF17's rule is
    /// that a count gating a latch may only be incremented for work that was read back, and an
    /// unreadable row was not read back. Keeping this here rather than at the counting switch is
    /// what stops the split introduced by FBUFF18 from quietly becoming a way to latch over one of
    /// the two.
    /// </summary>
    internal static bool HoldsLatchOpen(ReconcileTally tally) =>
        tally == ReconcileTally.Unresolved || tally == ReconcileTally.Unreadable;

    /// <summary>
    /// Exact line a reconcile row writes when the pass ran but its read-back did not establish
    /// the outcome, so the row is counted unresolved and the latch stays open. Grep
    /// <c>reconcile unverified</c>.
    /// </summary>
    internal static string FormatReconcileUnverified(
        string characterId, ImmortalPlan plan, ReadBack guardAfterSteps) =>
        "reconcile unverified character=" + characterId + " decision=" + plan
        + " guard=" + Show(guardAfterSteps)
        + " (counted unresolved; the latch stays open and the watchdog retries)";

    /// <summary>
    /// Exact line <c>.debug off</c> writes when the buff row is already gone but the character
    /// may still be immortal (job FBUFF6, R31 finding 9). Grep <c>off: buff not present</c>.
    ///
    /// <para>R45 finding 18: the two booleans are read inside one <c>try</c> over three IL2CPP
    /// reads with no inner guard, so <c>False</c> used to mean either "nothing was there" or
    /// "the first read threw". <paramref name="stateReadable"/> separates them and both print
    /// <c>?</c> when it is false — the line may only state what the code checked.</para>
    /// </summary>
    internal static string FormatOffNotPresent(bool stateReadable, bool hadImmortal, bool hadGuard) =>
        "off: buff not present, immortal=" + Show(ToReadBack(stateReadable, hadImmortal))
        + " guard=" + Show(ToReadBack(stateReadable, hadGuard))
        + " (clearing anyway)";

    /// <summary>Reply <c>.debug off</c> sends when it found nothing at all to clear.</summary>
    internal const string OffNotPresentReply = "The debug buff is not on.";

    /// <summary>
    /// Reply <c>.debug off</c> sends when it also removed a leftover <see cref="LegacySampleId"/>
    /// instance (the buff before it moved into Core).
    /// </summary>
    internal const string LegacyRemovedReply =
        "Removed a leftover sample.nodrain buff (the Sample's old debug buff).";

    /// <summary>
    /// Reply <c>.debug off</c> sends when the buff was gone but the character was still
    /// immortal — the manual recovery path R31 finding 9 asked for.
    /// </summary>
    internal const string OffRecoveredReply =
        "The debug buff is not on, but you were still immortal. Cleared it.";

    /// <summary>
    /// Reply <c>.debug off</c> sends on the recovery path when the three state reads threw, so
    /// neither "nothing was there" nor "you were still immortal" was established (R45 finding
    /// 18). The clear runs regardless; the status readout that follows is the answer.
    /// </summary>
    internal const string OffStateUnreadableReply =
        "The debug buff row is gone. I could not read whether you were still immortal, "
        + "so I cleared anyway - read the status below.";

    /// <summary>
    /// The <c>.debug off</c> recovery-path reply, worded from what the reads established
    /// rather than from the fact that the clear was called (R45 finding 18).
    /// </summary>
    internal static string FormatOffNotPresentReply(bool stateReadable, bool hadImmortal, bool hadGuard)
    {
        if (!stateReadable) return OffStateUnreadableReply;
        return hadImmortal || hadGuard ? OffRecoveredReply : OffNotPresentReply;
    }

    /// <summary>
    /// The <c>.debug off</c> reply on the ordinary path, worded from a read-back of the
    /// kill-path guard (R45 finding 29).
    ///
    /// <para>The old reply was a bare <c>"Debug off."</c> asserted after
    /// <c>RunCharacterSteps(CharacterRemoveSteps)</c>, and <c>ClearImmortal</c> has three paths
    /// that leave the character exactly as it was and say so only in the operator log — plus an
    /// outer catch that means <c>GodMode.Remove</c> may not have run at all, if
    /// <c>ImmortalHolders.TryGetValue</c> threw ahead of it. A player told the buff is off while
    /// still holding the guard is the mirror of evidence 162 and costs the same debugging time.
    /// <c>Status</c> follows this line in every case, so it never has to carry the detail.</para>
    /// </summary>
    internal static string FormatOffReply(ReadBack guardAfterClear) => guardAfterClear switch
    {
        ReadBack.No => "Debug off.",
        ReadBack.Yes => "Debug off, but the kill-path guard is still held - read the status below.",
        _ => "Debug off. I could not read the kill-path guard back - read the status below.",
    };

    /// <summary>
    /// Whether a reconcile pass may stop retrying. Evidence 166: "the pass ran" and "every grant
    /// was resolved" are different facts, and latching on the first is what left the god-mode
    /// guard un-taken after a server restart — WorldReady fires before any player connects, so
    /// every granted character is unresolvable at exactly the moment the old code latched.
    /// </summary>
    internal static bool ReconcileMayLatch(int unresolved) => unresolved <= 0;

    /// <summary>
    /// The consecutive-unreadable-pass counter <b>for one grant</b> after a pass in which that
    /// grant produced <paramref name="unreadableRows"/> rows whose read-back threw (job FBUFF18,
    /// R46 finding 1; keyed per grant by job FBUFF19, R47 finding 1 — a grant contributes one row
    /// per pass, so the argument is 0 or 1, and the parameter stays a count because the counter's
    /// rule is "any unreadable row at all", not "exactly one").
    ///
    /// <para>A single readable pass refills it: the bound is on a read-back that is
    /// <em>persistently</em> broken for that character, not on one that hiccupped once while the
    /// world was still coming up.</para>
    /// </summary>
    internal static int NextUnreadablePasses(int priorPasses, int unreadableRows) =>
        unreadableRows > 0 ? priorPasses + 1 : 0;

    /// <summary>
    /// Whether the reconcile must stop retrying <b>one grant</b> because <b>its own read-back</b>,
    /// rather than the world, is the failing part (job FBUFF18, R46 finding 1; per grant since
    /// job FBUFF19, R47 finding 1).
    ///
    /// <para>This is not <see cref="ReconcileMayLatch"/> and must never be folded into it. Latching
    /// means "everything is resolved, there is nothing left to do"; this means "we stopped on this
    /// grant, and what is on that character is unknown". They are different facts and the operator
    /// is told which one happened — <see cref="FormatReconcileGaveUp"/> says the second in as many
    /// words, because a give-up reported as a success is exactly the class of defect this module's
    /// whole read-back rule exists to prevent (evidence 162).</para>
    ///
    /// <para>It is also not <see cref="ReconcileEveryGrantSettled"/>. This answers "has <em>this
    /// grant</em> stopped"; that answers "is there anything left for a pass to do at all", which
    /// is the only question the periodic caller may be gated on.</para>
    /// </summary>
    internal static bool ReconcileReadBackGaveUp(int consecutiveUnreadablePasses) =>
        consecutiveUnreadablePasses >= MaxUnreadableReconcilePasses;

    /// <summary>
    /// Whether a whole reconcile pass is now pointless because every grant in the ledger has
    /// either been resolved or given up (job FBUFF19, R47 finding 1).
    ///
    /// <para><b>This is the defect R47 found, stated as a function.</b> FBUFF18 stopped the
    /// periodic caller when <em>some</em> grant gave up. The stop condition has to be that
    /// <em>every</em> grant has, because a pass that still has one retryable grant in it is doing
    /// the work evidence 162 needs — re-taking the god-mode guard for a player who connected after
    /// the boot — and a give-up over somebody else's grant is no evidence at all about that
    /// player's.</para>
    ///
    /// <para>Both arguments are counted over the same pass: <paramref name="unresolved"/> is every
    /// row that held the latch open (<see cref="HoldsLatchOpen"/>) and <paramref name="gaveUp"/>
    /// is the subset of those rows belonging to grants that have given up. A gave-up grant is
    /// always counted unresolved too — giving up resolves nothing — so <c>unresolved</c> can never
    /// be the smaller of the two; the comparison is written <c>&lt;=</c> rather than <c>==</c> so
    /// that a future miscount stops the pass instead of wedging it into an unbounded loop, and
    /// <c>gaveUp &gt; 0</c> keeps the ordinary all-resolved case in <see cref="ReconcileMayLatch"/>
    /// where it belongs.</para>
    /// </summary>
    internal static bool ReconcileEveryGrantSettled(int unresolved, int gaveUp) =>
        gaveUp > 0 && unresolved <= gaveUp;

    /// <summary>
    /// The consecutive-throwing-pass counter after a pass that either threw before finishing
    /// (<paramref name="passThrew"/>) or did not (job FBUFF19, R47 finding 1). Any completing pass
    /// refills it, including the two early returns, which are ordinary outcomes rather than
    /// throws.
    /// </summary>
    internal static int NextThrowingPasses(int priorPasses, bool passThrew) =>
        passThrew ? priorPasses + 1 : 0;

    /// <summary>
    /// Whether the reconcile must stop because <b>the pass itself</b> keeps throwing before it
    /// finishes (job FBUFF19, R47 finding 1 — the residual FBUFF18 declared and carried).
    ///
    /// <para>Kept apart from <see cref="ReconcileReadBackGaveUp"/> on purpose, and this is the
    /// whole reason the residual could not be folded into that budget: the two know different
    /// things. A read-back give-up knows which character it is about and knows the failing read
    /// was the guard. A pass that threw knows neither — the throw may come from the store, the
    /// resolve, a buffer walk or a decision, and the rows it got through before throwing are not
    /// recorded. <see cref="FormatReconcilePassGaveUp"/> therefore claims neither.</para>
    /// </summary>
    internal static bool ReconcilePassGaveUp(int consecutiveThrowingPasses) =>
        consecutiveThrowingPasses >= MaxThrowingReconcilePasses;

    /// <summary>
    /// Whether the watchdog may run another reconcile pass. The pass mutates
    /// (<c>RunCharacterSteps</c> per granted character), so every way it stops is derived here:
    /// it resolved everything (<paramref name="reconciled"/>), every grant left is one that gave
    /// up (<paramref name="everyGrantSettled"/>), or the pass itself keeps throwing before it
    /// finishes (<paramref name="passGaveUp"/>). Deriving it in one place is what stops a later
    /// caller from re-arming one of them without the others.
    ///
    /// <para>Job FBUFF19 (R47 finding 1) replaced FBUFF18's second argument. It used to be "some
    /// grant gave up", which stopped the pass for every other grant as well; it is now "there is
    /// nothing a further pass could do", which is the only thing that may gate a re-arm the rest
    /// of the ledger is still waiting for.</para>
    /// </summary>
    internal static bool ReconcileMayRun(bool reconciled, bool everyGrantSettled, bool passGaveUp) =>
        !reconciled && !everyGrantSettled && !passGaveUp;

    /// <summary>
    /// The per-grant give-up line (job FBUFF18, R46 finding 1; named per character by job
    /// FBUFF19, R47 finding 1). Grep <c>reconcile STOPPED for character=</c>.
    ///
    /// <para>The standing rule is that a verdict may only state what was established, so this line
    /// states that the retry stopped for <b>this</b> grant and that the state of <b>that</b>
    /// character is unknown. It is deliberately not phrased as a completion: nothing about giving
    /// up makes the god-mode guard any more likely to be held. It also names the one thing that is
    /// known — the read-back itself threw, repeatedly, on this character — because "the read-back
    /// is broken" is a more actionable fact for an operator than another thousand copies of "still
    /// unresolved".</para>
    ///
    /// <para>It names the character because, since FBUFF19, "the reconcile stopped" is a fact
    /// about a row rather than about the module: other grants are still being retried, and a line
    /// that did not say which character it was about would leave the operator unable to tell which
    /// of those two things had happened to any given player.</para>
    /// </summary>
    internal static string FormatReconcileGaveUp(string characterId, int passes) =>
        "reconcile STOPPED for character=" + characterId + " after " + passes
        + " consecutive pass(es) whose god-mode guard read-back threw. The retry re-runs a "
        + "MUTATING pass over that character, and re-running it because the result cannot be "
        + "observed gathers no evidence - so it is stopped for this grant. Other grants are "
        + "unaffected and keep retrying. This is NOT a success: whether this character holds the "
        + "kill-path guard is UNKNOWN. The buff-gone sweep keeps running; swap Core or "
        + "restart the server to reconcile again, and '.debug on' re-arms this character by "
        + "hand in the meantime.";

    /// <summary>
    /// The line a pass writes for a grant it is skipping because that grant already gave up (job
    /// FBUFF19, R47 finding 1). Grep <c>reconcile skipping character=</c>.
    ///
    /// <para>The row is skipped <b>before</b> any mutating step, which is the whole point of the
    /// budget, and it is still counted unresolved, because giving up resolved nothing. Written
    /// every pass rather than once, so the report the operator reads describes the pass that
    /// actually ran; the report's own change-comparison
    /// (<c>LogReconcileReport</c>) is what keeps it to one line in the log.</para>
    /// </summary>
    internal static string FormatReconcileGrantSkipped(string characterId) =>
        "reconcile skipping character=" + characterId
        + " (gave up on its guard read-back; no pass runs for it and its state is UNKNOWN)";

    /// <summary>
    /// The line the reconcile writes when the last retryable grant is gone and no further pass
    /// will run (job FBUFF19, R47 finding 1). Grep <c>reconcile STOPPED entirely</c>.
    ///
    /// <para>Distinct from <see cref="FormatReconcileGaveUp"/>, which is about one grant. This is
    /// the module-level fact the periodic caller is gated on, and it states exactly what
    /// <see cref="ReconcileEveryGrantSettled"/> checked: every grant in the ledger is resolved or
    /// has given up. It names the characters it gave up on for the same reason the per-grant line
    /// does — an operator reading only this line must still be able to tell which players are
    /// unknown.</para>
    /// </summary>
    internal static string FormatReconcileAllStopped(IReadOnlyList<string>? gaveUpCharacters)
    {
        int count = gaveUpCharacters == null ? 0 : gaveUpCharacters.Count;
        return "reconcile STOPPED entirely: every grant in the ledger is now either resolved or "
            + "given up, so no further pass will run. Gave up on " + count + " character(s): "
            + JoinCharacterIds(gaveUpCharacters) + ". This is NOT a success: whether those "
            + "characters hold the kill-path guard is UNKNOWN. The buff-gone sweep keeps running; "
            + "swap Core or restart the server to reconcile again, and '.debug on' "
            + "re-arms one character by hand in the meantime.";
    }

    /// <summary>
    /// The give-up line for the pass itself (job FBUFF19, R47 finding 1). Grep
    /// <c>reconcile STOPPED: the pass threw</c>.
    ///
    /// <para>FBUFF18's reason for carrying this residual was that folding it into the read-back
    /// budget would make that line's claim false. So this line makes none of those claims. It does
    /// not say the guard read-back threw, it does not name a character, and it says out loud the
    /// thing that is genuinely not known here and is known in the per-grant case: which grants had
    /// mutating steps run for them before the throw.</para>
    /// </summary>
    internal static string FormatReconcilePassGaveUp(int passes) =>
        "reconcile STOPPED: the pass threw before finishing on " + passes
        + " consecutive pass(es), so it is no longer repeated. A pass that throws mid-loop has "
        + "already run MUTATING steps for the grants it got through and read none of them back, "
        + "and which grants those were is not recorded. This is NOT a success: whether any granted "
        + "character holds the kill-path guard is UNKNOWN, and the throw was not necessarily the "
        + "guard read-back. The buff-gone sweep keeps running; swap Core or restart the "
        + "server to reconcile again, and '.debug on' re-arms one character by hand in the "
        + "meantime.";

    /// <summary>
    /// Character ids for a line that has to name them (job FBUFF19). Empty renders as
    /// <c>(none)</c> rather than as nothing, so a line can never read as if it had named a set it
    /// did not have.
    /// </summary>
    internal static string JoinCharacterIds(IReadOnlyList<string>? characterIds)
    {
        if (characterIds == null || characterIds.Count == 0) return "(none)";
        return string.Join(", ", characterIds);
    }

    /// <summary>
    /// Exact summary the world-ready reconcile writes (job FBUFF7, R32 finding 10). Grep
    /// <c>world-ready reconcile</c>. Always logged, including the all-zero case, so the operator
    /// can see the pass ran. (Its doc comment had drifted two hundred lines up the file onto
    /// <see cref="ReconcileMayLatch"/>; job FBUFF19 put it back.)
    ///
    /// <para><paramref name="unreadable"/> and <paramref name="gaveUp"/> are both subsets of
    /// <paramref name="unresolved"/> and the line is worded so an operator can see that rather
    /// than adding them together. They do not overlap with each other: a grant that has given up
    /// is skipped before any read-back, so it produces no unreadable row (job FBUFF19, R47
    /// finding 1).</para>
    /// </summary>
    internal static string FormatReconcile(
        int grants, int adopted, int removed, int foreignKept,
        int unresolved, int unreadable, int gaveUp, int dropped) =>
        "world-ready reconcile over " + grants + " own grant(s): adopted " + adopted
        + ", removed " + removed + ", pre-existing kept " + foreignKept
        + ", unresolved " + unresolved
        + " (unreadable " + unreadable + ", gave up " + gaveUp + ")"
        + ", dropped " + dropped;

    /// <summary>
    /// The no-world branch R32 finding 10 found silent. The reconcile is retried from the first
    /// watchdog tick that does see a world, so this is a deferral, not a loss. Grep
    /// <c>world-ready reconcile</c>.
    /// </summary>
    internal const string ReconcileNoWorldLog =
        "world-ready reconcile: no server world yet, deferred to the watchdog";

    /// <summary>
    /// The pending-ledger branch: the world exists but the grant ledger is not backed by the save
    /// folder yet, so it reads empty. A deferral, never a latch. Grep <c>world-ready reconcile</c>.
    /// </summary>
    internal const string ReconcileLedgerPendingLog =
        "world-ready reconcile: grant ledger not loaded from the save folder yet, deferred to the watchdog";

    /// <summary>
    /// Reply <c>.debug on</c> sends when the grant ledger is not backed by the save folder yet.
    /// Nothing was applied and nothing was changed.
    /// </summary>
    internal const string LedgerNotReadyReply =
        "The world save folder is not known yet, so the debug buff was not applied. Nothing was changed - try again in a moment.";

    /// <summary>The nothing-to-do line. An empty ledger is the normal case and must still log.</summary>
    internal const string ReconcileNothingToDoLog =
        "world-ready reconcile: 0 own grant(s) recorded, nothing to do";

    /// <summary>
    /// Whether a clear <b>removes</b> <c>Immortal</c> from the character.
    ///
    /// Job FBUFF7 (R32 finding 2) replaced the old <c>!tracked || addedByUs</c> rule, whose
    /// <c>!tracked</c> half is exactly what deleted vanilla's and other mods' component: an
    /// untracked character was treated as ours. The debug buff now removes the component only when
    /// its own durable ledger says it granted it (<paramref name="granted"/>) <b>and</b> the
    /// component was not already there when the grant was made
    /// (<paramref name="preExisting"/>). Everything else keeps its component; the debug buff still
    /// drops its own GodMode guard on every path, so no character it touched stays unkillable
    /// through Core's kill-path prefixes (R27 finding 2).
    /// </summary>
    internal static bool ImmortalClearRemoves(bool granted, bool preExisting) => granted && !preExisting;

    /// <summary>
    /// Whether the clear removes <c>Immortal</c>. The same ledger rule on every path, explicit
    /// <c>.debug off</c> included: <see cref="ImmortalClearRemoves(bool, bool)"/>.
    ///
    /// <para>Never "whoever added it" (reverted 2026-10-11): the vanilla player prefab
    /// <c>CHAR_VampireMale</c> carries <c>Immortal { IsImmortal = true }</c>, which appears to be how
    /// the game makes the downed state possible (not established for every live player: a respawn
    /// reuses the character entity and did not bring it back): <c>KillEntity</c> does not kill an immortal character, so a
    /// player stays alive at 0 health, the next hit makes
    /// <c>Script_ApplyBuffUnderHealthThreshold</c> apply <c>Buff_General_Vampire_Wounded_Buff</c>,
    /// and the downed state ends the player through a kill that ignores <c>Immortal</c>. Removing
    /// the vanilla component made players die outright at 0 health and never go downed.</para>
    /// </summary>
    internal static bool ImmortalClearRemoves(bool granted, bool preExisting, bool explicitOff, bool protectionHolds) =>
        ImmortalClearRemoves(granted, preExisting);

    /// <summary>Reply line when <c>.debug off</c> took <c>Admin_Invulnerable_Buff</c> off too.</summary>
    internal const string OffInvulnerableRemovedReply = "Admin invulnerability removed too.";

    /// <summary>
    /// Whether an explicit <c>.debug off</c> removes <c>Admin_Invulnerable_Buff</c>: yes, unless a
    /// debug protection grant (<c>protect</c>, KeepAlive) still holds the character, which applies
    /// and relies on that buff. Debug off means the player can be hurt again (project rule, 2026-10-11).
    /// </summary>
    internal static bool OffRemovesInvulnerable(bool protectionHolds) => !protectionHolds;

    /// <summary>Set only while <see cref="Off"/> runs the character remove steps.</summary>
    [ThreadStatic] private static bool _explicitOff;

    /// <summary>Reply line when an explicit off kept <c>Immortal</c> because a protection grant holds it.</summary>
    internal const string OffImmortalKeptForProtectionReply =
        "Immortal stays on: a debug protection grant (protect / KeepAlive) still holds you. Release it to become mortal.";

    /// <summary>Set by the last explicit off when it kept Immortal for a protection grant.</summary>
    [ThreadStatic] private static bool _offKeptForProtection;

    /// <summary>
    /// Exact log line the apply step writes, before the <c>character=</c> suffix. Grep
    /// <c>edit Immortal IsImmortal=</c>.
    /// </summary>
    internal static string FormatImmortalSet(bool added, bool was) =>
        "edit " + ImmortalComponentName + " IsImmortal=" + ImmortalValue
        + " added=" + added + " was=" + was;

    /// <summary>
    /// Exact log line a clear writes, before the <c>character=</c> suffix. Grep
    /// <c>edit Immortal cleared</c>.
    /// </summary>
    internal static string FormatImmortalCleared(bool removed, bool present) =>
        "edit " + ImmortalComponentName + " cleared removed=" + removed + " present=" + present;

    internal enum Kind
    {
        On = 1,
        Off = 2,

        /// <summary>
        /// Read back what is <b>actually</b> on the character right now. The buff shows as one
        /// HUD icon carrying one 58-byte tooltip, which cannot enumerate five protections, and
        /// two of them (<c>Immortal</c> and Core's kill-path guard) are not buff components at
        /// all and can never have an icon. Without this the only way to answer "is all of it
        /// really on?" was to read the server log.
        /// </summary>
        Status = 3,
    }

    /// <summary>
    /// <c>on</c> / <c>off</c> / <c>status</c>, with a bare <c>.debug</c> meaning
    /// <see cref="Kind.Status"/> — asking for the state is the safe default, and it is what an
    /// operator who mistypes the argument most likely wanted.
    /// </summary>
    internal static bool TryParse(string? token, out Kind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            kind = Kind.Status;
            return true;
        }

        switch (token.Trim().ToLowerInvariant())
        {
            case "on": kind = Kind.On; return true;
            case "off": kind = Kind.Off; return true;
            case "status": kind = Kind.Status; return true;
            default: return false;
        }
    }

    /// <summary>Header of the <see cref="Kind.Status"/> reply.</summary>
    internal const string StatusHeader = "Debug buff - what is actually applied:";

    /// <summary>
    /// The <see cref="Kind.Status"/> reply, built from facts read off the live entities. Every
    /// line is one protection and says on or off; nothing here is a restatement of what the
    /// command *intended* to do, which is the whole point — the buff's HUD tooltip is a constant
    /// string and cannot go out of date, so it cannot be the answer to "did it all apply?".
    ///
    /// <para>
    /// The five protections are deliberately listed as separate lines even though one buff
    /// carries them, because they are enforced by four different mechanisms and can fail
    /// independently: <c>ModifyBloodDrainBuff</c> and <c>DisableAggroBuff</c> live on the buff
    /// entity, the three <c>BuffModificationTypes</c> bits aggregate onto the character's
    /// <c>BuffableFlagState</c>, <c>Immortal</c> is written directly onto the character, and the
    /// kill-path guard is a Core Harmony prefix that is not entity state at all.
    /// </para>
    ///
    /// <para>
    /// All three character-side read groups — the flags, <c>Immortal</c>, and the god-mode
    /// guard — carry an explicit <c>*Readable</c> flag, and every one of them is <b>required</b>
    /// by the all-clear line (R43 findings 7 and 8). The rule is one rule and it has no
    /// exceptions: a group whose read failed can never contribute to "all five protections are
    /// live", because a caller's zero-initialized locals must not be indistinguishable from a
    /// successful read of zero. The guard group used to have no such flag, so an exception on
    /// the way to <c>PatchCount</c> left <c>0 == 0</c> satisfying the strictest term of the
    /// all-clear over god-mode state that was never read — the exact <c>prefixes=0</c> state
    /// evidence 162 found, under the exact sentence an admin died trusting.
    /// </para>
    /// </summary>
    /// <param name="buffPresent">A live buff instance was found on the character.</param>
    /// <param name="buffLabel">Its <c>index:version</c>, or the empty string.</param>
    /// <param name="flagsReadable">
    /// The <c>BuffableFlagState</c> read completed without throwing. False means "I could not
    /// tell", which is never OFF and never contributes to the all-clear.
    /// </param>
    /// <param name="flagsPresent">
    /// The character actually carries a <c>BuffableFlagState</c> component. False is a genuine
    /// answer, not a failure: no component means no modification has aggregated onto this
    /// character, so all three bits are definitively off (R43 finding 8). Ignored when
    /// <paramref name="flagsReadable"/> is false.
    /// </param>
    /// <param name="flags">
    /// The character's aggregated <c>BuffableFlagState.Value</c>. Ignored unless
    /// <paramref name="flagsReadable"/> and <paramref name="flagsPresent"/> are both true; a
    /// real zero is a genuine answer meaning every bit is off.
    /// </param>
    /// <param name="immortalReadable">The <c>Immortal</c> read succeeded.</param>
    /// <param name="immortal"><c>Immortal.IsImmortal</c> on the character.</param>
    /// <param name="guardReadable">
    /// The god-mode group — the guard, <c>PatchCount</c> and <c>RequiredPrefixes</c> — was read
    /// without throwing. False means <paramref name="guarded"/>,
    /// <paramref name="killPathPatches"/> and <paramref name="requiredPatches"/> are the
    /// caller's defaults and say nothing about the process.
    /// </param>
    /// <param name="guarded">This buff's own Core god-mode guard is held on the character.</param>
    /// <param name="killPathPatches">Kill-path prefixes actually installed.</param>
    /// <param name="requiredPatches">
    /// How many there should be. Zero is not a pass: a required count of zero means the policy
    /// itself did not read, so the all-clear demands a positive requirement as well as equality.
    /// </param>
    /// <param name="noDrain">The buff entity carries <c>ModifyBloodDrainBuff</c>.</param>
    /// <param name="noAggro">The buff entity carries <c>DisableAggroBuff</c>.</param>
    /// <param name="persists">The buff entity carries <c>Buff_Persists_Through_Death</c>.</param>
    /// <param name="reconcileGaveUpCharacters">
    /// The grants the background reconcile has stopped retrying because their guard read-back
    /// threw (job FBUFF18, R46 finding 1; a set of character ids rather than a pass count since
    /// job FBUFF19, R47 finding 1). A retry that was given up on must not be invisible here,
    /// because "nothing is complaining any more" would otherwise read as "it is fine" — and since
    /// the give-up is per grant, the readout has to say <b>which</b> characters, or a player
    /// reading it cannot tell whether it is about them.
    ///
    /// <para>It does <b>not</b> enter the all-clear. The all-clear is about the five protections
    /// on <em>this</em> character, every one of them read fresh a few lines above; the reconcile
    /// is a background pass over the whole ledger and a give-up there says nothing about the
    /// reads that just succeeded here. Mixing the two would make this line state something it did
    /// not establish, in either direction.</para>
    /// </param>
    /// <param name="reconcileThrowingPasses">
    /// Consecutive background-reconcile passes that threw before finishing (job FBUFF19, R47
    /// finding 1). Past <see cref="MaxThrowingReconcilePasses"/> — asked of
    /// <see cref="ReconcilePassGaveUp"/> here, so the threshold has one owner — the reconcile has
    /// stopped, and this readout says so on its own line, never merged with the one above: that
    /// one knows which characters and knows the guard read-back was the failing read, and this one
    /// knows neither.
    /// </param>
    internal static IReadOnlyList<string> FormatStatusLines(
        bool buffPresent,
        string? buffLabel,
        bool flagsReadable,
        bool flagsPresent,
        long flags,
        bool immortalReadable,
        bool immortal,
        bool guardReadable,
        bool guarded,
        int killPathPatches,
        int requiredPatches,
        bool noDrain,
        bool noAggro,
        bool persists,
        IReadOnlyList<string>? reconcileGaveUpCharacters,
        int reconcileThrowingPasses)
    {
        var lines = new List<string> { StatusHeader };

        lines.Add(buffPresent
            ? "  buff instance: on (" + (string.IsNullOrEmpty(buffLabel) ? "unnamed" : buffLabel) + ")"
            : "  buff instance: OFF - nothing is applied, run .debug on");

        lines.Add("  no blood drain: " + OnOff(buffPresent && noDrain) + " (ModifyBloodDrainBuff)");
        lines.Add("  ignored by foes: " + OnOff(buffPresent && noAggro) + " (DisableAggroBuff)");
        lines.Add("  survives death: " + OnOff(buffPresent && persists) + " (Buff_Persists_Through_Death)");

        if (!flagsReadable)
        {
            lines.Add("  invulnerable / sun / hazards: UNREADABLE - BuffableFlagState did not read");
        }
        else if (!flagsPresent)
        {
            // R43 finding 8. An absent component is not an unreadable one. "Did not read" sends
            // the operator to the interop; "no component" sends them to the buff, which is where
            // the fault actually is — the modification never aggregated onto this character.
            lines.Add("  invulnerable / sun / hazards: OFF - no BuffableFlagState component on the character");
        }
        else
        {
            lines.Add(
                "  invulnerable: " + OnOff((flags & InvulnerableFlag) != 0)
                + ", sun-immune: " + OnOff((flags & ImmuneToSunFlag) != 0)
                + ", hazard-immune: " + OnOff((flags & ImmuneToHazardsFlag) != 0)
                + " (BuffableFlagState=" + flags + ")");
        }

        // The two that have no icon and never can. Immortal is the only thing a direct kill
        // honors, and the prefixes cover the kill calls that read neither it nor the flags.
        lines.Add(immortalReadable
            ? "  immortal (direct kills): " + OnOff(immortal) + " (Immortal on the character)"
            : "  immortal (direct kills): UNREADABLE - Immortal did not read");

        lines.Add(guardReadable
            ? "  kill-path guard: " + OnOff(guarded)
              + " (Core prefixes " + killPathPatches + "/" + requiredPatches + " installed)"
            : "  kill-path guard: UNREADABLE - Core god-mode state did not read");

        // Every term is stated positively and every read group must have succeeded. Nothing that
        // was not read can reach this line: an unreadable group fails its own `*Readable` term
        // long before its zero-valued companions get to satisfy an equality by accident.
        // `requiredPatches > 0` is the other half of that — 0/0 is what a failed read of the
        // policy looks like, and it must never read as "all installed".
        bool allLive =
            buffPresent && noDrain && noAggro && persists
            && flagsReadable && flagsPresent && (flags & ModificationFlags) == ModificationFlags
            && immortalReadable && immortal
            && guardReadable && guarded
            && requiredPatches > 0 && killPathPatches == requiredPatches;

        lines.Add(allLive
            ? "  all five protections are live on one buff. One icon is correct."
            : "  SOMETHING ABOVE IS OFF - do not stand in the sun.");

        // Job FBUFF18 (R46 finding 1), per grant since FBUFF19 (R47 finding 1). Printed after the
        // verdict rather than folded into it: the verdict is about this character's own fresh
        // reads and stays exactly as true as it was, while these are facts about the background
        // pass over the ledger. Only shown once something has actually given up, because a pass
        // that is still retrying is working as designed and has nothing to report to a player.
        if (reconcileGaveUpCharacters != null && reconcileGaveUpCharacters.Count > 0)
            lines.Add(FormatStatusReconcileStopped(reconcileGaveUpCharacters));

        // Two lines, not one, because they are two measurements: the one above knows which
        // characters and knows the guard read-back was the read that threw, and this one knows
        // neither. Both can be true at once, and a pass that gave up mid-loop is exactly when a
        // reader most needs to see that the named set is not the whole story.
        if (ReconcilePassGaveUp(reconcileThrowingPasses))
            lines.Add(FormatStatusReconcilePassStopped(reconcileThrowingPasses));

        return lines;
    }

    /// <summary>
    /// The <c>.debug status</c> line for the grants a reconcile gave up on (job FBUFF18, R46
    /// finding 1; naming them since job FBUFF19, R47 finding 1). Grep
    /// <c>background reconcile: STOPPED for</c>. It reports the state as unknown, never as fine:
    /// the retry stopping is not evidence that anything was applied.
    ///
    /// <para>It says nothing about the grants it does not name. Whether the rest of the ledger is
    /// still being retried, has resolved, or has stopped for the other reason is three different
    /// facts this line did not check, and the second line
    /// (<see cref="StatusReconcilePassStopped"/>) carries the only one of them a player can act
    /// on.</para>
    /// </summary>
    internal static string FormatStatusReconcileStopped(IReadOnlyList<string>? characterIds) =>
        "  background reconcile: STOPPED for "
        + (characterIds == null ? 0 : characterIds.Count)
        + " grant(s) whose god-mode guard read-back threw - " + JoinCharacterIds(characterIds)
        + ". Those characters are UNKNOWN, not fine. Swap Core or restart the server to "
        + "reconcile them again.";

    /// <summary>
    /// The <c>.debug status</c> line for a reconcile that stopped because the pass itself kept
    /// throwing (job FBUFF19, R47 finding 1). Grep <c>background reconcile: STOPPED - the pass</c>.
    /// It names no character because the code did not establish one.
    /// </summary>
    internal static string FormatStatusReconcilePassStopped(int passes) =>
        "  background reconcile: STOPPED - the pass threw before finishing on " + passes
        + " consecutive tries, so it gave up. Which grants it had already touched is not "
        + "recorded, so every grant other than the reads above is UNKNOWN, not fine. Swap Core "
        + "or restart the server to reconcile again.";

    private static string OnOff(bool value) => value ? "on" : "OFF";

    /// <summary>
    /// The <c>.debug on</c> reply for a character that already carries the buff entity, worded
    /// from a read-back of the god-mode guard rather than from the fact that the character pass
    /// was called (R43 finding 9).
    ///
    /// <para>
    /// The pass this reply follows returns <c>void</c> and swallows every failure, and three of
    /// its paths never reach <c>GodMode.Add</c> at all. Evidence 162 closes with "a reply that
    /// says what a command <i>intended</i> is not evidence" — so this one says what the guard
    /// actually reads afterwards, and an unreadable guard is reported as unreadable rather than
    /// as either outcome. The full status readout follows on the next line in every case; this
    /// line must not contradict it.
    /// </para>
    /// </summary>
    internal static string FormatAlreadyOnReply(string label, bool guardReadable, bool guarded)
    {
        string head = "Debug was already on (" + label + "); ";
        if (!guardReadable)
            return head + "could not read the process-lifetime guard back - read the status below.";
        return guarded
            ? head + "re-armed the process-lifetime guard."
            : head + "the process-lifetime guard is still OFF - read the status below.";
    }

    /// <summary>
    /// <see cref="FormatAlreadyOnReply(string, bool, bool)"/> over the shared
    /// <see cref="ReadBack"/>, so the one read-back helper feeds every verdict in the module.
    /// </summary>
    internal static string FormatAlreadyOnReply(string label, ReadBack guard) =>
        FormatAlreadyOnReply(label, guard != ReadBack.Unreadable, guard == ReadBack.Yes);

    /// <summary>
    /// Exact log line <see cref="Reset"/> writes on plugin unload. Grep
    /// <c>reset removed</c>. Zero is logged too: the operator needs to see that the sweep ran.
    /// </summary>
    internal static string FormatResetRemoved(int count) =>
        "reset removed " + count + " live instance(s) via TryRemoveBuff";

    /// <summary>
    /// Destroy every live instance handed in. Game-free so the unload sweep can be tested:
    /// <paramref name="alive"/> is <c>EntityManager.Exists</c> and <paramref name="destroy"/>
    /// is the same <c>DestroyUtility.Destroy(..., DestroyDebugReason.TryRemoveBuff)</c> call
    /// <c>.debug off</c> makes. Returns how many were destroyed.
    /// </summary>
    internal static int SweepInstances<T>(
        System.Collections.Generic.IReadOnlyList<T>? instances,
        Func<T, bool>? alive,
        Action<T>? destroy)
    {
        if (instances == null || instances.Count == 0 || destroy == null) return 0;
        int removed = 0;
        for (int i = 0; i < instances.Count; i++)
        {
            var e = instances[i];
            if (alive != null && !alive(e)) continue;
            try
            {
                destroy(e);
                removed++;
            }
            catch (Exception ex)
            {
                Log("reset destroy threw: " + ex.Message);
            }
        }
        return removed;
    }

    /// <summary>
    /// Plugin unload: remove the debug buff from every holder, then log the count.
    /// <paramref name="sweep"/> is the server-side query + destroy; the parameterless
    /// <c>Reset()</c> supplies the real one.
    ///
    /// Before FZ this method had an empty body. The buff is infinite-duration
    /// (<see cref="InfiniteDuration"/>), persists through death, and after FW/FX carries
    /// <c>BuffModificationFlagData</c> 7 (invulnerable, sun-immune, hazard-immune) and
    /// <c>DisableAggroBuff</c> mode 1. Hot-swapping or deleting the debug buff took
    /// <c>.debug off</c> away with the command registration and left every holder
    /// permanently invulnerable and un-aggroable with nothing in the game able to remove it
    /// (R27 finding 2). A plugin whose buff can outlive its own commands must sweep it here.
    /// </summary>
    internal static int Reset(Func<int>? sweep)
    {
        int removed = 0;
        try
        {
            if (sweep != null) removed = sweep();
        }
        catch (Exception e)
        {
            Log("reset sweep threw: " + e.Message);
        }
        Log(FormatResetRemoved(removed));
        return removed;
    }

    /// <summary>
    /// True only for <see cref="PluginUnloadReason.Removal"/>. Job FBUFF2: this buff is
    /// infinite-duration and persists through death, so it is written into the world save and
    /// restored on load — and the debug buff was destroying the restored instance on the unload half
    /// of every Core hot swap, eleven log lines after the save had put it back
    /// (job FBUFF). A <see cref="PluginUnloadReason.Swap"/> brings the
    /// same plugin back in the next generation, which finds the instance again by GUID query
    /// (<c>.debug off</c> works on it, and re-registration re-applies the edits), so the buff
    /// must survive. <see cref="PluginUnloadReason.Shutdown"/> is the same: the save carries it.
    /// Only a real removal has to sweep, because after it nothing can take the buff off a player
    /// (R27 finding 2). <see cref="PluginUnloadReason.Unknown"/> (job FBUFF4) keeps too: nothing
    /// established that the plugin is gone, and a kept buff is recoverable where a swept one is
    /// not. The rule is <c>PluginUnloadReasonPolicy.KeepsState</c> — a sweep needs a positive
    /// Removal, never the absence of evidence.
    /// </summary>
    internal static bool ShouldSweep(PluginUnloadReason reason) =>
        !PluginUnloadReasonPolicy.KeepsState(reason);

    /// <summary>
    /// Keep the grant ledger when the plugin is coming back or the reason was not
    /// established. Forget only on <see cref="PluginUnloadReason.Removal"/>. A null reason is
    /// not an unload (<c>.debug off</c>, the stale-buff watchdog) and forgets.
    ///
    /// Keys off the reason <c>OnUnload</c> received — Bootstrap's ReloaderHost supplies it
    /// through <c>PluginUnloadSlot</c> — never <see cref="PluginUnloadScope.Current"/> alone.
    /// Production never pushes that scope, so reading it on Swap forgot every grant (R75
    /// finding 1) and the next generation's world-ready reconcile had nothing to adopt.
    /// Do not call <see cref="PluginUnloadReasonPolicy.Resolve"/> from off/watchdog: with no
    /// slot and no scope that answers Unknown, which would keep the grant on off.
    /// </summary>
    internal static bool ShouldKeepGrant(PluginUnloadReason? reason) =>
        reason is PluginUnloadReason r && PluginUnloadReasonPolicy.KeepsState(r);

    /// <summary>
    /// Exact log line the keep branch writes:
    /// <c>Debug: unload reason=Swap, keeping 1 live instance(s)</c>. Grep
    /// <c>Debug: unload reason=</c>.
    /// </summary>
    internal static string FormatKept(PluginUnloadReason reason, int live) =>
        "unload reason=" + reason + ", keeping " + live + " live instance(s)";

    /// <summary>
    /// Exact grant keep-or-forget line:
    /// <c>Debug: unload reason=Swap grants=kept count=1</c>. Grep
    /// <c>grants=kept</c> / <c>grants=forgotten</c>.
    /// </summary>
    internal static string FormatGrantUnloadDecision(PluginUnloadReason? reason, int count) =>
        "unload reason=" + (reason is PluginUnloadReason r ? r.ToString() : "none")
        + " grants=" + (ShouldKeepGrant(reason) ? "kept" : "forgotten")
        + " count=" + count;

    /// <summary>
    /// Reason-aware unload. On <see cref="PluginUnloadReason.Removal"/> this is exactly the old
    /// <see cref="Reset(Func{int})"/> sweep; otherwise nothing is destroyed and the count from
    /// <paramref name="count"/> is logged so the operator can see what was kept. Returns the
    /// number destroyed (0 whenever the buff was kept).
    /// </summary>
    internal static int Reset(PluginUnloadReason reason, Func<int>? sweep, Func<int>? count) =>
        Reset(reason, sweep, count, clearGuards: null);

    /// <summary>
    /// Reason-aware unload with the unconditional character clear (job FBUFF6, R31 finding 3).
    ///
    /// <paramref name="clearGuards"/> runs on <b>every</b> reason — Swap, Shutdown, Unknown and
    /// Removal alike — and returns how many characters lost <c>ProjectM.Immortal</c> and the
    /// god-mode guard. The buff keep rule (<see cref="ShouldSweep"/>) is unchanged and still owns
    /// the buff <em>entity</em>; this owns the <em>character</em>.
    ///
    /// Why it cannot be reason-aware like the buff: <c>Immortal</c> carries no
    /// <c>[ExcludeComponent]</c> / <c>[ComponentExcludesEntity]</c> attribute (server
    /// metadata), and PersistenceV2 is opt-out
    /// (<c>PersistenceV2Utility.GetExcludedComponentTypes</c>), so the component
    /// goes into the character's saved archetype. A buff kept across a Swap is recoverable — the
    /// next generation finds it by GUID query and re-adopts the wearer
    /// (<see cref="ReconcileImmortalOnWorldReady"/>) — but an <c>Immortal</c> written into the save with
    /// no plugin generation left to take it off is not: nothing in the game ever removes it (the
    /// god-mode block above), so the player is permanently unkillable. A swap re-adding the
    /// component costs one write; a persisted one costs the character.
    ///
    /// <b>Save-window caveat.</b> Core exposes no before-save event to plugins — the only save
    /// seam is <c>ModSave</c>'s internal <c>TriggerPersistenceSaveSystem.TriggerSave</c>
    /// <em>postfix</em> (<c>src/Progenitor.Core/Persistence/ModSave.cs</c>), which runs after the
    /// world write and is not plugin-facing. So the debug buff cannot clear "just before a save"; it
    /// clears on every unload instead, and the remaining risk window is the interval between an
    /// unload and the next autosave — during which no plugin generation is live to have written
    /// <c>Immortal</c> in the first place. A process killed mid-window saves nothing.
    /// Server stop is covered by the same clear: <c>Shutdown</c> arrives from <c>ProcessExit</c>
    /// (job FBUFF4) and now takes this branch too.
    /// </summary>
    internal static int Reset(
        PluginUnloadReason reason,
        Func<int>? sweep,
        Func<int>? count,
        Func<int>? clearGuards)
    {
        int destroyed;
        if (ShouldSweep(reason))
        {
            destroyed = Reset(sweep);
        }
        else
        {
            int live = 0;
            try
            {
                if (count != null) live = count();
            }
            catch (Exception e)
            {
                Log("reset count threw: " + e.Message);
            }
            Log(FormatKept(reason, live));
            destroyed = 0;
        }

        int cleared = 0;
        try
        {
            if (clearGuards != null) cleared = clearGuards();
        }
        catch (Exception e)
        {
            Log("reset clear threw: " + e.Message);
        }
        Log(FormatUnloadCleared(reason, cleared));
        return destroyed;
    }

    /// <summary>
    /// One-line registration intent the tests pin and the live log greps.
    /// Not a game call.
    /// </summary>
    internal static string DescribeRegistration() =>
        $"id={Id} source={SourceSpawnableName} drain={DrainBloodValue} type=Set/{ModificationTypeSet} " +
        $"lifetime={InfiniteDuration} endAction=None/{LifeTimeEndActionNone} priority={DrainPriority} " +
        $"icon={IconRelativePath} name={NameLocalizationKey} desc={DescriptionLocalizationKey}";

#if !PROGENITOR_TESTS
    /// <summary>Deterministic GUID for the cloned buff. Same value the registry assigns.</summary>
    internal static PrefabGUID Guid => new(PrefabGUID.CalculateGuidHash(Id));

    /// <summary>The Sample's old clone GUID (<see cref="LegacySampleId"/>), for the leftover clear.</summary>
    internal static PrefabGUID LegacyGuid => new(PrefabGUID.CalculateGuidHash(LegacySampleId));

    /// <summary>
    /// This buff's guard owner token: Core plus <see cref="GuardOwnerLabel"/>. Built per call
    /// because tokens compare by (plugin, label). A token must carry a plugin identity (R41
    /// finding 7); this one is Core's own (<c>GodModeGuard.CorePlugin</c>), which Core's
    /// <c>Uninstall</c> drops after <see cref="Reset(PluginUnloadReason)"/> has already dropped
    /// this label's holds. Guards are process state, so a new Core generation re-takes them from
    /// the grant ledger (the world-ready reconcile).
    /// </summary>
    private static Progenitor.GodMode.GodModeGuard.GodModeOwner DebugGuardOwner =>
        new(Progenitor.GodMode.GodModeGuard.CorePlugin, GuardOwnerLabel);

    /// <summary>
    /// The one read-back every verdict in this module is derived from (job FBUFF17). It is the
    /// call FBUFF16 introduced for <c>.debug on</c>'s already-on re-arm, lifted out so the
    /// reconcile, both <c>.debug off</c> paths and that re-arm share it rather than growing a
    /// second copy each with its own idea of what a throw means.
    ///
    /// <para>Why the guard is the right thing to read: it is the only part of this buff with
    /// process lifetime, so it is the part a Core swap or a server restart silently drops
    /// (evidence 162), and it is the last effectful statement of <c>WriteImmortal</c> and the
    /// first of <c>ClearImmortal</c> — see <see cref="TallyReconcileRow"/> for exactly how much
    /// each direction proves.</para>
    ///
    /// <para>A throw is <see cref="ReadBack.Unreadable"/>, never <see cref="ReadBack.No"/>: the
    /// caller must be told it does not know, not handed the failure default dressed as an
    /// answer.</para>
    ///
    /// <para><paramref name="sink"/> (job FBUFF18, R46 finding 1) is where the throw line goes for
    /// a caller that repeats on a timer. <c>.debug on</c> and <c>.debug off</c> are driven by
    /// a player and log straight out; the reconcile runs once per
    /// <see cref="ImmortalWatchdogPeriodFrames"/> frames for as long as it retries, so its lines
    /// join the pass report and go through the same de-duplication the summary line has always
    /// had. One mechanism, not two.</para>
    /// </summary>
    private static ReadBack ReadGuardBack(Entity character, List<string>? sink = null)
    {
        try
        {
            return Progenitor.GodMode.GodMode.IsGuardedBy(character, DebugGuardOwner)
                ? ReadBack.Yes
                : ReadBack.No;
        }
        catch (Exception e)
        {
            string line = "guard read-back threw: " + e.Message;
            if (sink != null) sink.Add(line);
            else Log(line);
            return ReadBack.Unreadable;
        }
    }

    /// <summary>
    /// Unload hook, called from <see cref="DebugTools.Uninstall"/> (Core's own uninstall) with
    /// <see cref="PluginUnloadReason.Swap"/>: Core is only ever uninstalled by a Core swap or at
    /// process exit, and both bring it back with the save. On
    /// <see cref="PluginUnloadReason.Removal"/> it would sweep the debug buff off every holder on
    /// the server through the same <c>DestroyUtility.Destroy(..., TryRemoveBuff)</c> path
    /// <c>.debug off</c> uses and log <c>Debug: reset removed N live instance(s) via
    /// TryRemoveBuff</c>; on a swap it destroys nothing and logs <c>Debug: unload reason=Swap,
    /// keeping N live instance(s)</c>. <c>Immortal</c> and the guard it granted are cleared on
    /// every reason. No-op on a client (the server owns buff instances).
    /// </summary>
    internal static void Reset(PluginUnloadReason reason)
    {
        // A new generation must reconcile again from its own WorldReady (job FBUFF7), with a
        // fresh retry budget and no inherited give-up (job FBUFF18) - a Core swap or a redeploy
        // is exactly the kind of change that can fix a read-back which was throwing.
        _reconciled = false;
        _lastReconcileReport = "";
        ResetReconcileRetryState();
        // Grant keep-or-forget keys off this OnUnload reason, not PluginUnloadScope.Current.
        // ReloaderHost pushes PluginUnloadSlot; Current is null on the production path.
        _grantUnloadReason = reason;
        try
        {
            Log(FormatGrantUnloadDecision(reason, GrantIds(DebugTools.Store).Count));
            Reset(reason, ServerSweep, ServerCount, ServerClearImmortalGuards);
        }
        finally
        {
            _grantUnloadReason = null;
        }
    }

    /// <summary>
    /// The unconditional character clear (job FBUFF6, R31 finding 3). Runs on every unload
    /// reason. Candidates are the union of three sources so nothing is missed when the statics
    /// are empty or the component is already gone but the guard is not:
    /// <list type="bullet">
    /// <item>every character in its own grant ledger
    /// (<see cref="ResolveGrantedCharacters"/>) — the only source that survives a swap or a
    /// restart, and the only one that is provably <b>ours</b> (job FBUFF7, R32 finding 2: the
    /// world-wide <c>All(Immortal, PlayerCharacter)</c> query this used to open with cleared
    /// vanilla's and other mods' component too);</item>
    /// <item>the wearer of every live buff instance (<c>Buff.Target</c>);</item>
    /// <item>this generation's <see cref="ImmortalHolders"/> keys — a character whose
    /// <c>Immortal</c> something else removed may still hold the god-mode guard.</item>
    /// </list>
    /// A candidate with no grant loses only the debug buff's GodMode guard; its <c>Immortal</c> is
    /// left alone (<see cref="ClearImmortal"/>). Returns how many characters were visited. 0 with
    /// no server world (the client owns no buff instances).
    /// </summary>
    private static int ServerClearImmortalGuards()
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return 0;
        var em = world.EntityManager;

        var candidates = new System.Collections.Generic.List<Entity>();
        var seen = new System.Collections.Generic.HashSet<Entity>();

        void AddCandidate(Entity e)
        {
            if (e == Entity.Null) return;
            if (seen.Add(e)) candidates.Add(e);
        }

        // FBUFF8: a row whose recorded identity is not this character's is dropped here rather
        // than visited — the account recreated its character and the row is about the old one.
        foreach (var kv in ResolveGrantedCharacters(em))
            if (TryGetGrant(DebugTools.Store, kv.Key, kv.Value.Identity, out _))
                AddCandidate(kv.Value.Entity);

        try
        {
            Entity clone = Entity.Null;
            try { PrefabLookup.TryGetEntity(world, Guid, out clone); }
            catch { /* the wearers still resolve off the instances */ }

            var instances = CollectInstances(em, Guid, clone);
            for (int i = 0; i < instances.Count; i++)
            {
                var instance = instances[i];
                if (instance == Entity.Null || !em.Exists(instance)) continue;
                if (!instance.TryRead(em, out Buff buff)) continue;
                AddCandidate(buff.Target);
            }
        }
        catch (Exception e)
        {
            Log("unload wearer walk threw: " + e.Message);
        }

        // Snapshot: ClearImmortal mutates ImmortalHolders.
        var tracked = new System.Collections.Generic.List<Entity>(ImmortalHolders.Keys);
        for (int i = 0; i < tracked.Count; i++)
            AddCandidate(tracked[i]);

        for (int i = 0; i < candidates.Count; i++)
            RunCharacterSteps(em, candidates[i], CharacterRemoveSteps);

        return candidates.Count;
    }

    /// <summary>
    /// The debug buff's own grants, resolved to live entities (job FBUFF7, R32 finding 2).
    ///
    /// This replaces the FBUFF6 <c>All(ProjectM.Immortal, ProjectM.PlayerCharacter)</c> query,
    /// which enumerated <b>every</b> immortal player — vanilla's, an admin's, another mod's —
    /// and handed them to a clear. The candidate set is now the ledger
    /// (<see cref="GrantIds"/>); the world is read only to turn a durable platform id back into
    /// the entity it currently names. The one query here is on <c>PlayerCharacter</c> alone and
    /// its rows are used as a lookup, never as a work list. Disabled entities are included
    /// because an offline player's character <em>is</em> disabled (measured on the test server:
    /// <c>Disabled</c> present, no <c>DisabledDueToInactivity</c>, so by a path other than
    /// <c>KillAndDisableInactivePlayerAfterDuration</c>; which one is the open question in the
    /// research note user-entity-lifecycle.md). The <c>User</c> entity is not.
    ///
    /// Job FBUFF8: the value carries the live character's identity next to the entity, because
    /// the account the ledger row is keyed by may now name a <b>different</b> character than the
    /// one the grant was made for. The caller decides that with
    /// <see cref="DecideForCharacter"/>; resolving is only a lookup.
    /// </summary>
    private static System.Collections.Generic.Dictionary<string, (Entity Entity, string Identity)>
        ResolveGrantedCharacters(EntityManager em)
    {
        var result = new System.Collections.Generic.Dictionary<string, (Entity Entity, string Identity)>(
            StringComparer.Ordinal);
        var ids = GrantIds(DebugTools.Store);
        if (ids.Count == 0) return result;

        var wanted = new System.Collections.Generic.HashSet<string>(ids, StringComparer.Ordinal);
        try
        {
            var playerType = ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly);
            var entities = Query.Entities(
                em, includeDisabled: true, includeSystems: false, playerType);
            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                if (e == Entity.Null || !em.Exists(e)) continue;
                if (!TryCharacterId(em, e, out var id, out var identity)) continue;
                if (!wanted.Contains(id)) continue;
                result[id] = (e, identity);
            }
        }
        catch (Exception e)
        {
            Log("granted character resolve threw: " + e.Message);
        }
        return result;
    }

    /// <summary>
    /// Is this character's <c>Immortal</c> one the debug buff granted? The durable ledger first, the
    /// in-generation cache second (a character with no readable user row).
    /// </summary>
    private static bool HasOwnGrant(EntityManager em, Entity character)
    {
        if (TryCharacterId(em, character, out var id, out var identity)
            && TryGetGrant(DebugTools.Store, id, identity, out _))
            return true;
        return ImmortalHolders.ContainsKey(character);
    }

    /// <summary>
    /// The full ledger key (job FBUFF8, R33 finding 6): the account
    /// (<paramref name="id"/>, <c>User.PlatformId</c>) <b>and</b> the character
    /// (<paramref name="identity"/>, <c>PlayerCharacter.Name</c> with <c>User.CharacterName</c>
    /// as the fallback). The account survives everything and identifies nothing on its own; the
    /// name is persisted with the character and is re-chosen when a character is recreated, so
    /// the pair survives a save reload and changes on a recreation. See
    /// <see cref="ImmortalGrant"/> for why <c>NetworkId</c> is not usable here.
    ///
    /// Job FBUFF9 (R34 finding 6): a readable account with an <b>unreadable name</b> is not a
    /// usable key, so this returns <b>false</b> — the same answer as "no user row at all". Half
    /// a key used to come back as <c>true</c> with <c>identity = ""</c>, which every downstream
    /// read then treated as a mismatch: the write half recorded an unmatchable row and the read
    /// half deleted a good one. "Identity unknown" now stops at the door instead.
    /// </summary>
    private static bool TryCharacterId(
        EntityManager em, Entity character, out string id, out string identity)
    {
        id = "";
        identity = "";
        try
        {
            if (character == Entity.Null || !em.Exists(character)) return false;
            if (!character.Has<PlayerCharacter>(em)) return false;
            // GetComponentData, not TryRead: PlayerCharacter / User carry bools and
            // NetworkedEntity, so neither is C# unmanaged (same reason CastleReader reads them
            // this way).
            var pc = em.GetComponentData<PlayerCharacter>(character);
            try { identity = pc.Name.ToString() ?? ""; }
            catch { identity = ""; }
            var userEntity = pc.UserEntity;
            if (userEntity == Entity.Null || !em.Exists(userEntity)) return false;
            if (!userEntity.Has<User>(em)) return false;
            var user = em.GetComponentData<User>(userEntity);
            if (user.PlatformId == 0) return false;
            if (identity.Length == 0)
            {
                // PlayerCharacter.Name @0x0 and User.CharacterName @0x40 carry the same string;
                // read the second one when the first came back empty.
                try { identity = user.CharacterName.ToString() ?? ""; }
                catch { identity = ""; }
            }
            if (identity.Length == 0)
            {
                // FBUFF9: neither name read anything. Refuse the whole key rather than hand out
                // an account id with no character behind it.
                Log(FormatGrantRefused(user.PlatformId.ToString()));
                return false;
            }
            id = user.PlatformId.ToString();
            return true;
        }
        catch (Exception e)
        {
            Log("character id read threw: " + e.Message);
            return false;
        }
    }

    /// <summary>Live instances without touching them, for the keep branch. 0 with no server world.</summary>
    private static int ServerCount()
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return 0;
        var em = world.EntityManager;
        var guid = Guid;

        Entity clone = Entity.Null;
        try { PrefabLookup.TryGetEntity(world, guid, out clone); }
        catch { /* count the instances anyway */ }

        var instances = CollectInstances(em, guid, clone);
        int live = 0;
        for (int i = 0; i < instances.Count; i++)
        {
            var e = instances[i];
            if (e != Entity.Null && em.Exists(e)) live++;
        }
        return live;
    }

    /// <summary>Server query + destroy for <see cref="Reset(Func{int})"/>. 0 when there is no server world.</summary>
    private static int ServerSweep()
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return 0;
        var em = world.EntityManager;
        var guid = Guid;

        Entity clone = Entity.Null;
        try { PrefabLookup.TryGetEntity(world, guid, out clone); }
        catch { /* sweep the instances anyway */ }

        var instances = CollectInstances(em, guid, clone);
        return SweepInstances(
            instances,
            e => e != Entity.Null && em.Exists(e),
            e =>
            {
                // FBUFF5: the wearer's Immortal is the debug buff's, not the game's. Take it back
                // before the instance goes, while Buff.Target still resolves.
                ClearImmortalForInstance(em, e);
                DestroyUtility.Destroy(em, e, DestroyDebugReason.TryRemoveBuff);
            });
    }

    /// <summary>
    /// Run <see cref="CharacterRemoveSteps"/> on the wearer of <paramref name="instance"/>.
    /// <c>ProjectM.Buff.Target</c> is the wearer (server metadata, <c>Entity Target @0x8</c>)
    /// — the same field the vanilla <see cref="ImmortalVanillaSource"/> script reads.
    /// </summary>
    private static void ClearImmortalForInstance(EntityManager em, Entity instance)
    {
        try
        {
            if (instance == Entity.Null || !em.Exists(instance)) return;
            if (!instance.TryRead(em, out Buff buff)) return;
            if (buff.Target == Entity.Null) return;
            RunCharacterSteps(em, buff.Target, CharacterRemoveSteps);
        }
        catch (Exception e)
        {
            Log("clear " + ImmortalComponentName + " for instance threw: " + e.Message);
        }
    }

    /// <summary>
    /// Live <c>Buff</c> + <c>PrefabGUID</c> entities carrying this clone's GUID, excluding the
    /// prefab clone itself. Same rules Core's live-refresh query uses: no <c>Prefab</c> tag,
    /// disabled entities included (an offline player's character is disabled, by a path that is
    /// not the inactivity timer, and world entities out of player range are disabled too).
    /// </summary>
    private static System.Collections.Generic.List<Entity> CollectInstances(
        EntityManager em,
        PrefabGUID guid,
        Entity prefabClone)
    {
        var result = new System.Collections.Generic.List<Entity>();
        try
        {
            var buffType = ComponentIndex.Of<Buff>(ComponentType.AccessMode.ReadOnly);
            var guidType = ComponentIndex.Of<PrefabGUID>(ComponentType.AccessMode.ReadOnly);
            var prefabType = ComponentIndex.Of<Unity.Entities.Prefab>(ComponentType.AccessMode.ReadOnly);
            var entities = Query.Entities(em, includeDisabled: true, includeSystems: false, buffType, guidType);
            int hash = guid._Value;
            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                if (e == prefabClone) continue;
                if (!em.Exists(e)) continue;
                if (em.HasComponent(e, prefabType)) continue;
                if (em.GetComponentData<PrefabGUID>(e)._Value != hash) continue;
                result.Add(e);
            }
        }
        catch (Exception e)
        {
            Log("reset query threw: " + e.Message);
        }
        return result;
    }

    /// <summary>
    /// Register <c>progenitor.debug</c> through <c>DefineBuff</c> under Core's owner so both
    /// sides clone a visible haste buff (icon on the buff bar) and the server sends <c>PrfD</c>.
    /// The prefab pass (drain / lifetime / flags / no aggro / strips) runs from
    /// <see cref="PrefabRegistry.AfterApply"/> on each side, the frame the clone is built, and
    /// once more here for a clone that already exists (a Core swap into a live world). A
    /// registry <c>Content.Edit</c> is not used: it requires a plugin owner with a handshake
    /// declaration, and Core is neither. Every handle goes into <paramref name="registrations"/>.
    /// </summary>
    internal static void Register(List<IDisposable> registrations)
    {
        registrations.Add(PrefabRegistry.DefineBuff(Id)
            .CloneOf(SourceSpawnableName)
            .Named(LocalizedName)
            .Described(LocalizedDescription)
            .Icon(IconRelativePath)
            .RefreshLiveInstances()
            .PassVersion(PrefabPassVersion)
            .Register(DebugTools.Owner));

        Action<World, PrefabDefinition> afterApply = OnCloneApplied;
        PrefabRegistry.AfterApply += afterApply;
        registrations.Add(new ActionDisposable(() => PrefabRegistry.AfterApply -= afterApply));
        PatchExistingClone(Plugin.IsServerProcess ? Worlds.Server : Worlds.Client);

        // FBUFF5: nothing in the game removes the Immortal this buff adds, so this module owns
        // the whole lifetime. `.debug off` covers the path an admin drives; this watchdog covers
        // every other end of the buff (Dev bridge `buff apply:false`, a game-side destroy) by
        // reconciling tracked wearers against the BuffBuffer. Server only — the server owns buff
        // instances.
        if (Plugin.IsServerProcess)
        {
            registrations.Add(Progenitor.Systems.VirtualSystems.Add(ImmortalWatchdogName, ImmortalWatchdog, DebugTools.Owner));
            // FBUFF7 (R32 finding 10): the reconcile runs on WorldReady, and the first watchdog
            // tick that sees a world retries it, so a generation that loads *after* WorldReady
            // fired still reconciles. Both paths log.
            registrations.Add(Progenitor.Events.EventBus.On<Progenitor.Events.WorldReady>(
                OnWorldReadyReconcile, owner: DebugTools.Owner));
            // Mid-session Core swap: WorldReady already fired, so the handler above is a no-op
            // until the next world. If the server world is up now, reconcile from the persisted
            // grant ledger at load and log the readiness line a swap wait greps. The watchdog
            // still retries unresolved grants. A missing world stays deferred, as before.
            try
            {
                var world = Worlds.Server;
                if (world != null && world.IsCreated)
                    ReconcileImmortalOnWorldReady();
            }
            catch (Exception e)
            {
                Log("load reconcile threw: " + e.Message);
            }
        }

        Log("DefineBuff " + DescribeRegistration());
    }

    /// <summary>
    /// Set once <see cref="ReconcileImmortalOnWorldReady"/> has run against a live world <b>and
    /// left nothing unresolved</b>. The second half is load-bearing: a grant whose character has
    /// not streamed in yet is the normal state at WorldReady on a server boot, and latching
    /// there meant the guard was never re-taken for it (evidence 166).
    /// </summary>
    private static bool _reconciled;

    /// <summary>Set once this generation looked for the Sample's legacy ledger (<see cref="MigrateLegacyGrants"/>).</summary>
    private static bool _legacyMigrated;

    /// <summary>
    /// The <c>OnUnload</c> reason for this unload's grant keep-or-forget. Set around
    /// <see cref="Reset(PluginUnloadReason)"/>; null on <c>.debug off</c> and the watchdog,
    /// which forget. Never read <see cref="PluginUnloadScope.Current"/> for this.
    /// </summary>
    [ThreadStatic]
    private static PluginUnloadReason? _grantUnloadReason;

    /// <summary>
    /// The last reconcile pass's <b>whole</b> report — every per-row line plus the summary, joined
    /// — so a retrying pass does not repeat itself.
    ///
    /// <para>Job FBUFF18 (R46 finding 1) widened this from the summary alone. The comment beside
    /// the old comparison already had the right conclusion — <i>"a grant that stays unresolved …
    /// would otherwise write this line once a second forever"</i> — and FBUFF17 then added two
    /// more per-row lines and a new way to stay unresolved into the very loop that comment is
    /// about, without applying it to them. Rather than grow a second de-duplication mechanism
    /// keyed by id, the pass now composes its lines into one report and the existing comparison
    /// covers all of them: an unchanged situation logs nothing, and a changed one logs in
    /// full.</para>
    /// </summary>
    private static string _lastReconcileReport = "";

    /// <summary>
    /// Consecutive reconcile passes in which <b>one grant</b>'s read-back threw, keyed by the
    /// ledger's own key — the character id (job FBUFF18, R46 finding 1; keyed per grant by job
    /// FBUFF19, R47 finding 1).
    ///
    /// <para>Keyed this way because that is where the evidence is. FBUFF18 kept one counter for
    /// the whole pass, so one character with a broken read-back spent everybody's budget; the
    /// retries the budget is careful not to bound — a grant whose character has not streamed in,
    /// an apply whose guard read back as <c>No</c> — are per grant, and a bound that is not keyed
    /// the same way cannot leave them alone.</para>
    ///
    /// <para>An entry is removed when the grant's pass is readable again (the refill) and when the
    /// grant leaves the ledger (<see cref="PruneReconcileRetryState"/>), so this cannot outgrow
    /// the ledger.</para>
    /// </summary>
    private static readonly Dictionary<string, int> UnreadablePassesByGrant =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>
    /// The grants the reconcile has stopped retrying because their guard read-back kept throwing
    /// (<see cref="ReconcileReadBackGaveUp"/>), in the order they gave up (job FBUFF19, R47
    /// finding 1).
    ///
    /// <para>A list rather than a set: the give-up line and <c>.debug status</c> both name these
    /// characters, and a stable order is what lets the pass report's own change-comparison
    /// (<see cref="LogReconcileReport"/>) recognize an unchanged situation. The membership test is
    /// linear, over a collection bounded by the number of players who have ever run
    /// <c>.debug on</c> on this world.</para>
    ///
    /// <para>Distinct from <see cref="_reconciled"/> on purpose: that one means the pass resolved
    /// everything, this one means it stopped on those grants without knowing. Nothing reads them
    /// as the same thing — <c>.debug status</c> reports these as UNKNOWN.</para>
    /// </summary>
    private static readonly List<string> GaveUpGrants = new List<string>();

    /// <summary>
    /// Set once <see cref="ReconcileEveryGrantSettled"/> held — every grant in the ledger is
    /// resolved or has given up, so no further pass could do anything (job FBUFF19, R47
    /// finding 1). This, and not "some grant gave up", is what stops the periodic caller.
    /// </summary>
    private static bool _everyGrantSettled;

    /// <summary>
    /// Consecutive reconcile passes that threw before finishing (job FBUFF19, R47 finding 1 —
    /// FBUFF18's declared residual). Refilled by any pass that completes.
    /// </summary>
    private static int _throwingPasses;

    /// <summary>
    /// Set once <see cref="ReconcilePassGaveUp"/> held. Kept apart from
    /// <see cref="_everyGrantSettled"/> because they are different measurements with different
    /// sentences; <see cref="ReconcileMayRun"/> is the only place all three stop conditions meet.
    /// </summary>
    private static bool _passGaveUp;

    /// <summary>Read by <c>.debug status</c> so the readout can name the grants that gave up.</summary>
    internal static IReadOnlyList<string> ReconcileGaveUpCharacters => GaveUpGrants;

    /// <summary>Read by <c>.debug status</c> so the readout can say the pass itself gave up.</summary>
    internal static int ReconcileThrowingPasses => _throwingPasses;

    /// <summary>
    /// Clear every piece of reconcile retry state (job FBUFF19, R47 finding 1). One function
    /// rather than a repeated block, so a state added later cannot be cleared on one of the two
    /// paths and forgotten on the other — which is the shape of the defect this job is fixing.
    ///
    /// <para>Both callers are events, not timers: <see cref="Reset(PluginUnloadReason)"/> (a new
    /// generation — a Core swap or a redeploy is exactly the kind of change that can fix a
    /// read-back which was throwing) and the <c>WorldReady</c> handler (a new world). Neither is
    /// periodic, so neither can reopen the loop the budget closed.</para>
    /// </summary>
    private static void ResetReconcileRetryState()
    {
        UnreadablePassesByGrant.Clear();
        GaveUpGrants.Clear();
        _everyGrantSettled = false;
        _throwingPasses = 0;
        _passGaveUp = false;
    }

    /// <summary>
    /// Drop retry state for grants that are no longer in the ledger (job FBUFF19, R47 finding 1).
    /// A row the reconcile dropped, or one a later <c>.debug off</c> removed, must not keep a
    /// give-up alive: a give-up is a claim about a character whose state is unknown, and a grant
    /// that no longer exists has nothing left to be unknown about.
    /// </summary>
    private static void PruneReconcileRetryState(List<string> ledgerIds)
    {
        var live = new HashSet<string>(ledgerIds, StringComparer.Ordinal);
        GaveUpGrants.RemoveAll(id => !live.Contains(id));

        var stale = new List<string>();
        foreach (var kv in UnreadablePassesByGrant)
            if (!live.Contains(kv.Key)) stale.Add(kv.Key);
        for (int i = 0; i < stale.Count; i++) UnreadablePassesByGrant.Remove(stale[i]);
    }

    /// <summary>
    /// Log a reconcile pass's report only if it differs from the last one, then remember it. Every
    /// line the reconcile writes on a path that repeats at watchdog cadence goes through here, so
    /// there is exactly one de-duplication rule for the pass and no line that escapes it.
    /// </summary>
    private static void LogReconcileReport(List<string> lines)
    {
        string composed = string.Join("\n", lines);
        if (string.Equals(composed, _lastReconcileReport, StringComparison.Ordinal)) return;
        _lastReconcileReport = composed;
        for (int i = 0; i < lines.Count; i++) Log(lines[i]);
    }

    /// <summary><c>WorldReady</c> handler. Server worlds only; the client owns no buff instances.</summary>
    private static void OnWorldReadyReconcile(Progenitor.Events.WorldReady ev)
    {
        if (!ev.IsServer) return;
        // FBUFF18: a new server world is a new situation, not another turn of the old retry, so
        // it gets a fresh budget. This cannot reopen the unbounded loop the budget closed -
        // WorldReady fires per world creation, not on a timer, and the watchdog (which is the
        // timer) honors every give-up. FBUFF19: through the one clear, so a state added later
        // cannot be reset here and forgotten in Reset.
        ResetReconcileRetryState();
        _legacyMigrated = false; // a new world may be a different save with its own Sample ledger
        ReconcileImmortalOnWorldReady();
    }

    /// <summary>
    /// World-ready reconcile (job FBUFF7, R32 findings 2 and 10).
    ///
    /// R32 finding 10: this used to be a one-shot call from <see cref="Register"/> that returned
    /// silently when <c>Worlds.Server</c> was not up — the restart case it exists for. It now
    /// runs from the <c>WorldReady</c> event, is retried from the first watchdog tick that sees a
    /// world, and logs on every branch, including "nothing to do".
    ///
    /// R32 finding 2: the candidate set is the debug buff's <b>own grant ledger</b>, not a
    /// world-wide <c>Immortal</c> query. For each granted id, resolved back to a live entity:
    /// <list type="bullet">
    /// <item><b>Keep</b> — still wearing <c>progenitor.debug</c>: re-run
    /// <see cref="CharacterApplySteps"/> so the god-mode guard exists again in this generation
    /// (the grant, and with it the "did we add the component" answer, comes from the ledger, so
    /// the adopt cannot mistake its own write for somebody else's);</item>
    /// <item><b>Remove</b> — the buff is gone and the ledger says the debug buff added the
    /// component: take it back. This is the R31 finding 3 recovery — a server killed while the
    /// buff was on saved <c>Immortal</c> onto the character and nothing in the game removes
    /// it;</item>
    /// <item><b>DropGuardOnly</b> — the buff is gone but <c>Immortal</c> pre-dated the grant:
    /// drop the debug buff's guard and leave the component exactly as vanilla (or the other mod) had
    /// it;</item>
    /// <item><b>unresolved</b> — the id names no live player character (a deleted character, or
    /// a player whose entity has not streamed in). The grant is kept, so a later pass can still
    /// undo it.</item>
    /// <item><b>Foreign</b> (job FBUFF8, R33 finding 6) — the account resolves, but to a
    /// character whose identity is not the one the row recorded: the account deleted and
    /// recreated its character. The row is about a character that no longer exists, so it is
    /// logged, dropped, and this character is left completely alone — including an
    /// <c>Immortal</c> something else gave it, which the account-keyed version deleted.</item>
    /// </list>
    /// A character that is <em>not</em> in the ledger is never visited at all.
    /// </summary>
    private static void ReconcileImmortalOnWorldReady()
    {
        // FBUFF18 (R46 finding 1): every line this pass writes on a path that repeats at watchdog
        // cadence is composed here and flushed through LogReconcileReport, which is the summary
        // line's own change-comparison widened to cover the whole pass.
        var report = new List<string>();
        // FBUFF19 (R47 finding 1): the outer catch's own state, set on the one path that reaches
        // it and read in the finally, so the two early returns and every completing pass refill
        // the budget without saying so twice.
        bool passThrew = false;
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated)
            {
                // Repeats every watchdog period for as long as no world appears, so it is a
                // report like any other rather than a bare Log.
                report.Add(ReconcileNoWorldLog);
                return;
            }

            // A pending ledger reads empty, and "0 own grants" would latch the reconcile for the
            // rest of the generation with every guard still down. Defer instead, like no world.
            if (!DebugTools.StoreReady)
            {
                report.Add(ReconcileLedgerPendingLog);
                return;
            }

            var em = world.EntityManager;
            var store = DebugTools.Store;
            if (!_legacyMigrated)
            {
                _legacyMigrated = true;
                foreach (var legacy in Progenitor.Persistence.ModSave.FindStoresHolding(LegacyGrantStoreKey, DebugTools.StoreId))
                    MigrateLegacyGrants(legacy, store);
            }
            var ids = GrantIds(store);
            // FBUFF19: retry state is keyed by the ledger's key, so it is trimmed to the ledger
            // before anything reads it. A grant dropped last pass must not keep a give-up alive.
            PruneReconcileRetryState(ids);
            if (ids.Count == 0)
            {
                // Nothing to resolve, so nothing can stay unresolved: this pass is final.
                _reconciled = true;
                report.Add(ReconcileNothingToDoLog);
                return;
            }

            var resolved = ResolveGrantedCharacters(em);
            var guid = Guid;
            int adopted = 0, removed = 0, foreignKept = 0, unresolved = 0, unreadable = 0, dropped = 0;
            int gaveUp = 0;

            for (int i = 0; i < ids.Count; i++)
            {
                var id = ids[i];

                // FBUFF19 (R47 finding 1). A grant that gave up is skipped BEFORE any mutating
                // step - that is what the budget buys - and is still counted unresolved, because
                // giving up resolved nothing. Only this row is skipped: every other grant in the
                // ledger runs exactly as it did, including the two unbounded retries.
                if (GaveUpGrants.Contains(id))
                {
                    gaveUp++;
                    unresolved++;
                    report.Add(FormatReconcileGrantSkipped(id));
                    continue;
                }

                if (!resolved.TryGetValue(id, out var row) || !em.Exists(row.Entity))
                {
                    unresolved++;
                    // Evidence 166's retry, and it stays unbounded: no mutating pass runs for this
                    // grant at all. FBUFF19: the budget is refilled rather than left standing,
                    // because a pass this grant produced no unreadable row on is exactly what the
                    // refill is for - a character that streams out between two throwing passes
                    // must not carry the earlier ones into a later one.
                    AdvanceGrantReadBackBudget(id, rowUnreadable: false, report);
                    report.Add(FormatImmortalDecision(id, ImmortalPlan.Keep) + " (unresolved; grant kept)");
                    continue;
                }

                var character = row.Entity;
                bool wears = TryFindBuff(em, character, guid, out var instance) && em.Exists(instance);
                // FBUFF8: the identity check lives inside DecideForCharacter, which logs a row
                // that belongs to a character this account no longer has. FBUFF9 (R34 finding
                // 6): deciding no longer deletes — this loop is the only place that does, below,
                // and only for a row it has just resolved to a live character it is not about.
                var plan = DecideForCharacter(
                    store, id, row.Identity, wears, out bool preExisting, out bool dropRow);
                report.Add(FormatImmortalDecision(id, plan) + " character=" + FormatEntity(character)
                    + " wears=" + wears + " preExisting=" + preExisting);

                // FBUFF17 (R45 finding 3). Every one of these passes is a void call that
                // swallows everything, so the counter a row lands in is decided by a read-back
                // of the god-mode guard afterwards — never by the pass having returned. The
                // Foreign case touches nothing on the character and so takes no read-back;
                // TallyReconcileRow says which is which and why.
                var guardAfter = ReadBack.Unreadable;
                switch (plan)
                {
                    case ImmortalPlan.Keep:
                        RunCharacterSteps(em, character, CharacterApplySteps);
                        guardAfter = ReadGuardBack(character, report);
                        break;
                    case ImmortalPlan.Remove:
                    case ImmortalPlan.DropGuardOnly:
                        RunCharacterSteps(em, character, CharacterRemoveSteps);
                        guardAfter = ReadGuardBack(character, report);
                        break;
                    case ImmortalPlan.Foreign:
                        // The account's current character is not the one the row was about.
                        // Nothing on the character is touched — not even the god-mode guard,
                        // which this character was never given. FBUFF9: the row itself goes only
                        // when the identity proved it is about somebody else. An identity that
                        // could not be read (dropRow false) keeps its row for the next pass.
                        break;
                }

                var tally = TallyReconcileRow(plan, guardAfter, dropRow);
                switch (tally)
                {
                    case ReconcileTally.Adopted: adopted++; break;
                    case ReconcileTally.Removed: removed++; break;
                    case ReconcileTally.ForeignKept: foreignKept++; break;
                    case ReconcileTally.Dropped:
                        DropGrant(store, id, row.Identity);
                        dropped++;
                        break;
                    case ReconcileTally.Unreadable:
                        // FBUFF18: the row is counted unresolved as well, one statement below.
                        // This counter is only what decides whether the retry is worth repeating.
                        unreadable++;
                        break;
                }

                // FBUFF17's rule in one statement: both failure tallies hold the latch open, so
                // the number the latch is computed from is incremented here, from the tally, and
                // never beside the branch that ran the pass. FBUFF18 splitting the failure column
                // in two must not become a way for one of the halves to latch.
                if (HoldsLatchOpen(tally))
                {
                    unresolved++;
                    if (plan != ImmortalPlan.Foreign)
                        report.Add(FormatReconcileUnverified(id, plan, guardAfter));
                }

                // FBUFF19 (R47 finding 1). The budget is spent by the grant whose read-back threw
                // and refilled by every other outcome, so a grant that keeps throwing stops itself
                // and stops nothing else. This is the whole scope correction: FBUFF18 spent one
                // counter per pass and stopped the module, over retries that are per grant.
                if (AdvanceGrantReadBackBudget(id, tally == ReconcileTally.Unreadable, report))
                    gaveUp++;
            }

            // Evidence 166. The latch used to be set on entry, so the pass ran exactly once -
            // at WorldReady, which on a server boot is precisely the moment when no player is
            // connected and every granted character is therefore unresolvable. The grant was
            // logged 'unresolved', the latch closed, and the god-mode guard this pass exists
            // to re-take in the new generation was never taken. Latching on 'nothing left
            // unresolved' instead lets the watchdog retry until the players actually arrive.
            // The retry is close to free: the watchdog already calls ResolveGrantedCharacters
            // on the same tick for its own sweep.
            //
            // FBUFF17 (R45 finding 3) is the other half of the same latch: moving it onto a
            // count is worth nothing while the count is incremented for work nobody read back.
            // Every row above now lands in its counter through TallyReconcileRow, so a
            // swallowed apply failure lands in `unresolved` and this stays open.
            if (ReconcileMayLatch(unresolved)) _reconciled = true;

            report.Add(FormatReconcile(
                ids.Count, adopted, removed, foreignKept, unresolved, unreadable, gaveUp, dropped));

            // FBUFF19 (R47 finding 1). Giving up is a third state, not a latch: `_reconciled`
            // stays false, because the pass did not resolve anything, and `.debug status`
            // reports those grants as UNKNOWN. "We stopped retrying" is never reported as "it is
            // fine". What stops the periodic caller is that EVERY grant is settled - resolved or
            // given up - never that one of them gave up; the retry the rest of the ledger is
            // waiting for is the only automatic re-arm of the god-mode guard there is.
            if (ReconcileEveryGrantSettled(unresolved, gaveUp) && !_everyGrantSettled)
            {
                _everyGrantSettled = true;
                report.Add(FormatReconcileAllStopped(GaveUpGrants));
            }
        }
        catch (Exception e)
        {
            passThrew = true;
            report.Add("world-ready reconcile threw: " + e.Message);
        }
        finally
        {
            // FBUFF19 (R47 finding 1), FBUFF18's declared residual. A pass that throws mid-loop
            // has already run mutating steps for the rows it got through, so repeating it forever
            // is the same defect one level up - and it gets its own counter, its own bound and its
            // own sentence rather than being folded into the read-back budget, because it knows
            // neither which character nor which read failed.
            _throwingPasses = NextThrowingPasses(_throwingPasses, passThrew);
            if (ReconcilePassGaveUp(_throwingPasses) && !_passGaveUp)
            {
                _passGaveUp = true;
                report.Add(FormatReconcilePassGaveUp(_throwingPasses));
            }

            // Logged on change only, whole pass at a time. A grant that stays unresolved - a
            // character deleted and never recreated - would otherwise write these lines once a
            // second forever, and FBUFF17 added two more of them to this loop.
            LogReconcileReport(report);
        }
    }

    /// <summary>
    /// Spend or refill one grant's read-back budget, and give up on that grant past the bound
    /// (job FBUFF19, R47 finding 1). Returns whether <b>this call</b> is what gave up, so the
    /// pass can count the row without a second membership test.
    ///
    /// <para>Every branch of the row loop goes through here exactly once, including the branch
    /// that runs no pass at all, because "this grant produced no unreadable row" is the refill and
    /// a branch that skipped the call would leave a stale count standing.</para>
    /// </summary>
    private static bool AdvanceGrantReadBackBudget(string id, bool rowUnreadable, List<string> report)
    {
        int prior = UnreadablePassesByGrant.TryGetValue(id, out int recorded) ? recorded : 0;
        int next = NextUnreadablePasses(prior, rowUnreadable ? 1 : 0);
        if (next == 0) UnreadablePassesByGrant.Remove(id);
        else UnreadablePassesByGrant[id] = next;

        if (!ReconcileReadBackGaveUp(next)) return false;
        GaveUpGrants.Add(id);
        report.Add(FormatReconcileGaveUp(id, next));
        return true;
    }

    /// <summary>
    /// Characters this generation wrote <c>Immortal</c> onto; value = the debug buff owns the
    /// component (it was absent before the grant). The durable answer lives in the ModSave
    /// ledger (<see cref="GrantStoreKey"/>); this is the in-generation cache and the one place
    /// that still knows about a character whose id could not be resolved.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<Entity, bool> ImmortalHolders = new();

    /// <summary>
    /// Every character <b>the debug buff granted</b> whose <c>progenitor.debug</c> buff is gone loses
    /// what the debug buff gave it.
    ///
    /// Job FBUFF6 (R31 finding 10): the candidate set is not gated on
    /// <see cref="ImmortalHolders"/>, so the watchdog is not blind after a swap, a plugin
    /// redeploy, a save reload or a restart — all of which leave the statics empty while the buff
    /// and the component survive. Job FBUFF7 (R32 finding 2): what replaces the statics is the
    /// durable ledger, <b>not</b> a world-wide <c>Immortal</c> query. A character with
    /// <c>Immortal</c> and no grant is vanilla's or another mod's and is never visited.
    /// <see cref="ImmortalHolders"/> is still unioned in, because a character whose grant id
    /// could not be resolved this tick may still be holding the god-mode guard.
    ///
    /// Cost: throttled to one pass per <see cref="ImmortalWatchdogPeriodFrames"/> frames; see the
    /// note on that constant. Normally zero grants, zero rows and zero <c>BuffBuffer</c> walks.
    /// </summary>
    private static void ImmortalWatchdog(Progenitor.Systems.VirtualSystemContext vs)
    {
        if (vs.Frame % ImmortalWatchdogPeriodFrames != 0) return;
        try
        {
            var em = vs.EntityManager;
            var guid = Guid;

            // R32 finding 10: a generation that loaded after WorldReady fired never gets the
            // event. The first tick that sees a world runs the reconcile instead.
            //
            // FBUFF18 (R46 finding 1): this is the periodic caller of a MUTATING pass, so every
            // stop condition is consulted through one derivation. FBUFF19 (R47 finding 1) fixed
            // which conditions those are - the pass resolved everything, every grant left is one
            // that gave up, or the pass itself keeps throwing. It is deliberately NOT "some grant
            // gave up": this pass is the only automatic re-arm of the god-mode guard after a
            // restart (evidence 162), so one broken read-back must not end it for a player who
            // has not connected yet. The stale-buff sweep below is not gated on any of them: it is
            // driven by the BuffBuffer, converges (a clear forgets its grant), and is not what the
            // bounds are about.
            if (ReconcileMayRun(_reconciled, _everyGrantSettled, _passGaveUp)) ReconcileImmortalOnWorldReady();

            var candidates = new System.Collections.Generic.List<Entity>();
            var seen = new System.Collections.Generic.HashSet<Entity>();
            // FBUFF8: same identity gate as the reconcile and the unload sweep — an account whose
            // character was recreated is not a candidate for anything.
            foreach (var kv in ResolveGrantedCharacters(em))
                if (TryGetGrant(DebugTools.Store, kv.Key, kv.Value.Identity, out _)
                    && seen.Add(kv.Value.Entity))
                    candidates.Add(kv.Value.Entity);
            if (ImmortalHolders.Count > 0)
            {
                // Snapshot the keys: RunCharacterSteps below mutates ImmortalHolders.
                foreach (var character in new System.Collections.Generic.List<Entity>(ImmortalHolders.Keys))
                    if (seen.Add(character)) candidates.Add(character);
            }

            if (candidates.Count == 0) return;

            var stale = PlanImmortalClears(
                candidates,
                character => em.Exists(character)
                    && TryFindBuff(em, character, guid, out var instance)
                    && em.Exists(instance));

            for (int i = 0; i < stale.Count; i++)
            {
                Log("watchdog buff gone character=" + FormatEntity(stale[i]));
                RunCharacterSteps(em, stale[i], CharacterRemoveSteps);
            }
        }
        catch (Exception e)
        {
            Log("watchdog threw: " + e.Message);
        }
    }

    /// <summary>
    /// Who a <c>.debug on|off|status</c> acts on, and where its replies go. The caller's own
    /// character, or another online player's that <see cref="DebugCommands"/> resolved through the
    /// player directory in the current world epoch. Replies always go to the admin who typed it.
    /// </summary>
    internal readonly struct Target
    {
        internal Target(World world, Entity user, Entity character, Action<string> reply)
        {
            World = world;
            User = user;
            Character = character;
            Reply = reply ?? (_ => { });
        }

        internal World World { get; }

        internal Entity User { get; }

        internal Entity Character { get; }

        internal Action<string> Reply { get; }
    }

    internal static void Run(Target t, Kind kind)
    {
        if (kind == Kind.On) On(t);
        else if (kind == Kind.Status) Status(t);
        else Off(t);
    }

    /// <summary>
    /// <c>.debug off</c>'s cleanup of a leftover <see cref="LegacySampleId"/> instance on the
    /// target. The Sample used to own this buff; an instance it applied and kept across its own
    /// unload is not registered by anything now, so nothing else removes it. Destroyed through
    /// the game's own removal path. True when one was found and destroyed.
    /// </summary>

    /// <summary>
    /// <c>.debug off</c>: take <c>Admin_Invulnerable_Buff</c> off as well (test harnesses and the
    /// bridge apply it directly, and it blocks all damage), unless a protection grant holds it.
    /// </summary>
    private static bool RemoveAdminInvulnerable(World world, EntityManager em, Entity character)
    {
        try
        {
            if (!OffRemovesInvulnerable(Progenitor.Buffs.DebugProtection.HoldsImmortal(character)))
            {
                Log("off: keeping " + Progenitor.Buffs.DebugProtection.InvulnerableBuffName + " (a debug protection grant holds the character)");
                return false;
            }
            if (!PrefabLookup.TryGetGuidByName(world, Progenitor.Buffs.DebugProtection.InvulnerableBuffName, out var guid)) return false;
            if (!TryFindBuff(em, character, guid, out var instance) || !em.Exists(instance)) return false;
            Log("off: removing " + Progenitor.Buffs.DebugProtection.InvulnerableBuffName + " instance=" + FormatEntity(instance));
            DestroyUtility.Destroy(em, instance, DestroyDebugReason.TryRemoveBuff);
            return true;
        }
        catch (Exception e)
        {
            Log("off: " + Progenitor.Buffs.DebugProtection.InvulnerableBuffName + " removal threw: " + e.Message);
            return false;
        }
    }

    private static bool RemoveLegacyInstance(EntityManager em, Entity character)
    {
        try
        {
            if (!TryFindBuff(em, character, LegacyGuid, out var legacy) || !em.Exists(legacy)) return false;
            Log("off: removing leftover " + LegacySampleId + " instance=" + FormatEntity(legacy));
            DestroyUtility.Destroy(em, legacy, DestroyDebugReason.TryRemoveBuff);
            return true;
        }
        catch (Exception e)
        {
            Log("off: leftover " + LegacySampleId + " removal threw: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Read every protection off the live entities and reply with what is really there.
    ///
    /// <para>
    /// Nothing in here is cached and nothing is inferred from what <see cref="On"/> tried to do:
    /// each fact is a fresh read, so a protection that was applied and later lost — a game-side
    /// destroy, a crash, a Core swap between the buff and the guard — shows as OFF. Every read
    /// is guarded on its own, and a read that throws reports UNREADABLE rather than OFF, because
    /// "I could not tell" and "it is not there" are different answers and only one of them means
    /// stand in the sun at your peril.
    /// </para>
    /// </summary>
    private static void Status(Target t)
    {
        if (t.Character == Entity.Null)
        {
            t.Reply("No character entity on this user; nothing to report.");
            return;
        }

        var em = t.World.EntityManager;
        var character = t.Character;

        bool buffPresent = false;
        string buffLabel = "";
        bool noDrain = false, noAggro = false, persists = false;
        try
        {
            if (TryFindBuff(em, character, Guid, out var instance) && em.Exists(instance))
            {
                buffPresent = true;
                buffLabel = FormatEntity(instance);
                try { noDrain = instance.Has<ModifyBloodDrainBuff>(em); } catch { /* stays false */ }
                try { noAggro = instance.Has<DisableAggroBuff>(em); } catch { /* stays false */ }
                try { persists = instance.Has<Buff_Persists_Through_Death>(em); } catch { /* stays false */ }
            }
        }
        catch (Exception e)
        {
            Log("status: buff read threw: " + e.Message);
        }

        // R43 finding 8. Three outcomes, three answers: the read threw (unreadable), the
        // component is not there (definitively off), or it is there and its value — zero
        // included — is a genuine answer. `flagsReadable` is set on the last line of the try so
        // that nothing before it can be mistaken for a completed read.
        bool flagsReadable = false, flagsPresent = false;
        long flags = 0L;
        try
        {
            flagsPresent = character.Has<BuffableFlagState>(em);
            if (flagsPresent)
                flags = character.Read<BuffableFlagState>(em).Value.Value;
            flagsReadable = true;
        }
        catch (Exception e)
        {
            Log("status: BuffableFlagState read threw: " + e.Message);
        }

        bool immortalReadable = false, immortal = false;
        try
        {
            immortal = character.Has<Immortal>(em) && character.Read<Immortal>(em).IsImmortal;
            immortalReadable = true;
        }
        catch (Exception e)
        {
            Log("status: Immortal read threw: " + e.Message);
        }

        // R43 finding 7. `guardReadable` is set on the LAST line of the try, so a throw anywhere
        // in the group leaves it false and the zero defaults below can never satisfy the
        // all-clear's `patches == required` term by being equally unset. The two sibling groups
        // above already worked this way; this one did not, which is the whole finding.
        bool guardReadable = false, guarded = false;
        int patches = 0, required = 0;
        try
        {
            guarded = Progenitor.GodMode.GodMode.IsGuardedBy(character, DebugGuardOwner);
            patches = Progenitor.GodMode.GodMode.PatchCount;
            required = Progenitor.GodMode.GodModePolicy.RequiredPrefixes;
            guardReadable = true;
        }
        catch (Exception e)
        {
            Log("status: god-mode read threw: " + e.Message);
        }

        // FBUFF18 (R46 finding 1): the readout carries the background reconcile's give-ups too, so
        // a retry that was stopped is visible to the player rather than only in the server log.
        // FBUFF19 (R47 finding 1): which characters, because a give-up is now a per-grant fact.
        var lines = FormatStatusLines(
            buffPresent, buffLabel, flagsReadable, flagsPresent, flags,
            immortalReadable, immortal, guardReadable, guarded,
            patches, required, noDrain, noAggro, persists,
            ReconcileGaveUpCharacters, ReconcileThrowingPasses);

        for (int i = 0; i < lines.Count; i++)
            t.Reply(lines[i]);

        // MAPTP1: the map powers the buff gives (DebugMap): what the client was sent, read fresh.
        var mapLines = DebugMap.StatusFor(t.World, t.User, t.Character);
        for (int i = 0; i < mapLines.Count; i++)
            t.Reply(mapLines[i]);

        // The same verdict goes to the server log, so a status taken before a death is in the
        // record next to the death itself.
        Log("status character=" + FormatEntity(character)
            + " buff=" + (buffPresent ? buffLabel : "none")
            + " flags=" + (flagsReadable ? (flagsPresent ? flags.ToString() : "absent") : "?")
            + " immortal=" + (immortalReadable ? immortal.ToString() : "?")
            + " guard=" + (guardReadable ? guarded.ToString() : "?")
            + " prefixes=" + (guardReadable ? patches + "/" + required : "?/?")
            + " drain=" + noDrain + " aggro=" + noAggro + " persists=" + persists
            // FBUFF19 (R47 finding 1): the operator line carries the give-up as a decided fact
            // rather than as a threshold the reader has to compare against a constant, and it
            // names the grants, because "the reconcile stopped" is now a per-row fact. R47 called
            // this the surface most likely to be read by a machine, and debug_buffs asserts these
            // fields - so the field names changed with the meaning rather than keeping a name
            // whose value no longer measures what it says.
            + " reconcileGaveUpGrants=" + ReconcileGaveUpCharacters.Count
            + " reconcileGaveUpCharacters=" + JoinCharacterIds(ReconcileGaveUpCharacters)
            + " reconcilePassGaveUp=" + ReconcilePassGaveUp(ReconcileThrowingPasses)
            + " reconcileThrowingPasses=" + ReconcileThrowingPasses);
    }

    private static void On(Target t)
    {
        if (t.Character == Entity.Null)
        {
            t.Reply("No character entity on this user; cannot apply the debug buff.");
            return;
        }

        var guid = Guid;
        Log("on begin " + DescribeRegistration());

        if (!PrefabLookup.TryGetEntity(t.World, guid, out var prefab) || prefab == Entity.Null)
        {
            t.Reply("progenitor.debug is not in the prefab collection yet (wait for DefineBuff apply).");
            Log("not in collection yet guid=" + guid._Value);
            return;
        }

        Log("collection entity=" + FormatEntity(prefab) + " guid=" + guid._Value);

        var em = t.World.EntityManager;

        // The grant is recorded with a read-modify-write of the ledger; on a store that is not
        // backed by the save folder yet that write would replace the ledger on disk when it merges.
        if (!DebugTools.StoreReady)
        {
            t.Reply(LedgerNotReadyReply);
            Log("on refused: grant ledger not backed by the save folder yet");
            return;
        }

        // FBUFF9 (R34 finding 6): the grant is only removable if it can be written down, and it
        // can only be written down with a readable character identity. Refuse before applying
        // anything rather than leave a player with an Immortal the debug buff can never take back.
        if (!TryCharacterId(em, t.Character, out var grantId, out var grantIdentity)
            || !CanRecordGrant(grantId, grantIdentity))
        {
            t.Reply(OnIdentityUnknownReply);
            Log(FormatImmortalWriteRefused(FormatEntity(t.Character)));
            return;
        }

        if (TryFindBuff(em, t.Character, guid, out var existing) && em.Exists(existing))
        {
            // Evidence 162. This used to reply "already on" and return, which is wrong in the
            // one case that matters: the buff entity is *world* state and survives a server
            // restart and a Core swap, while the god-mode guard and the three kill-path
            // prefixes are *process* state and do not. So after any restart `.debug on` found
            // the buff, answered "already on", and left PatchCount at 0 with no guard held —
            // the wearer keeps the flags and Immortal and loses the direct-kill protection,
            // silently, which is the same shape of failure as the two deaths.
            //
            // The world-ready reconcile is supposed to re-adopt, but it walks its own
            // grant ledger and does nothing for a character with no row in it — which is
            // precisely the character whose Immortal predates the ledger.
            //
            // The character pass is idempotent (a pre-existing Immortal is left alone and the
            // guard is reference-counted), so re-running it is the fix: it re-takes the guard,
            // which installs the prefixes on the first Add of the generation.
            Log("already on instance=" + FormatEntity(existing) + "; re-running the character pass");
            RunCharacterSteps(em, t.Character, CharacterApplySteps);

            // R43 finding 9. RunCharacterSteps returns void and swallows everything, and
            // WriteImmortal has three paths that reach neither GodMode.Add nor any component
            // write — no live character, the FBUFF9 refusal when there is no readable
            // account+character key, and its own outer catch. Saying "re-armed" after a call
            // that cannot report failure is exactly the reply evidence 162 forbids: a reply
            // that states what the command intended. Read the guard back and word it from that.
            //
            // FBUFF17: this read is now ReadGuardBack, the module's single read-back helper —
            // the reconcile's adopt is the same re-arm from the same steps array and had no
            // read-back at all (R45 finding 3), which is what one copy per call site buys you.
            var reArm = ReadGuardBack(t.Character);
            Log("already on re-arm read-back guard=" + Show(reArm));
            t.Reply(FormatAlreadyOnReply(FormatEntity(existing), reArm));
            Status(t);
            return;
        }

        if (!TryApply(t.World, t.User, t.Character, guid, out var instance, out var applyWhy))
        {
            t.Reply("Applying progenitor.debug failed: " + applyWhy);
            Log("apply failed: " + applyWhy);
            return;
        }

        t.Reply($"Debug on. buff={FormatEntity(instance)} guid={guid._Value}");
        // MAPTP1: reveal the map and arm the map teleport now rather than on the next half-second push.
        DebugMap.PushNow(t.World, t.User, t.Character);
        Log("applied instance=" + FormatEntity(instance) + " guid=" + guid._Value);

        // Read it all back rather than declaring victory. A reply of "Debug on." over a
        // half-applied buff is what an admin trusted before dying to the first hit (R32 finding
        // 5), and the apply touches four different mechanisms that can fail independently.
        Status(t);
    }

    /// <summary>
    /// The character remove steps for an explicit <c>.debug off</c>: <see cref="ClearImmortal"/>
    /// then removes <c>Immortal</c> whoever added it, unless a protection grant holds it.
    /// </summary>
    private static void RunExplicitOffSteps(EntityManager em, Entity character)
    {
        _offKeptForProtection = false;
        _explicitOff = true;
        try { RunCharacterSteps(em, character, CharacterRemoveSteps); }
        finally { _explicitOff = false; }
    }

    private static void Off(Target t)
    {
        if (t.Character == Entity.Null)
        {
            t.Reply("No character entity on this user; cannot remove the debug buff.");
            return;
        }

        var guid = Guid;
        Log("off guid=" + guid._Value);
        var em = t.World.EntityManager;
        if (RemoveLegacyInstance(em, t.Character))
            t.Reply(LegacyRemovedReply);
        if (RemoveAdminInvulnerable(t.World, em, t.Character))
            t.Reply(OffInvulnerableRemovedReply);
        if (!TryFindBuff(em, t.Character, guid, out var instance) || !em.Exists(instance))
        {
            // FBUFF6 (R31 finding 9): the buff row being gone is exactly the state a crash, a
            // swap or a game-side destroy leaves behind, and it is the state in which the
            // character is stuck immortal. Clear regardless — ClearImmortal is idempotent and
            // handles the no-component and no-character cases — and say what was found, so this
            // stays the manual recovery path.
            // FBUFF17 (R45 finding 18). Three IL2CPP reads with no inner guard under one try,
            // and both booleans initialized to the "nothing here" answer: `False` used to mean
            // either "nothing was there" or "the first read threw", and the player-facing
            // sentence and the operator line both asserted the former. `stateReadable` is set
            // on the genuine last line of the try — the shape the three Status groups use — so
            // an unread state prints `?` and gets its own reply rather than an all-clear.
            bool stateReadable = false;
            bool hadImmortal = false;
            bool hadGuard = false;
            try
            {
                // FBUFF7: only an Immortal the debug buff granted counts as "stuck immortal" here.
                // A foreign one is not the debug buff's to clear, so it must not be reported as
                // cleared either.
                hadImmortal = em.Exists(t.Character)
                    && t.Character.Has<Immortal>(em)
                    && HasOwnGrant(em, t.Character);
                // This buff's own hold only: a guard `.godmode` took is not stuck `.debug`
                // state, and `.debug off` neither clears it nor may claim it did.
                hadGuard = Progenitor.GodMode.GodMode.IsGuardedBy(
                    t.Character, DebugGuardOwner);
                stateReadable = true;
            }
            catch (Exception e)
            {
                Log("off: state read threw: " + e.Message);
            }
            Log(FormatOffNotPresent(stateReadable, hadImmortal, hadGuard));
            RunExplicitOffSteps(em, t.Character);
            t.Reply(FormatOffNotPresentReply(stateReadable, hadImmortal, hadGuard));
            if (_offKeptForProtection) t.Reply(OffImmortalKeptForProtectionReply);
            DebugMap.PushOff(t.World, t.User);
            // R45 finding 29: every other path of this command ends in a live readout, and this
            // is the manual recovery path — the one where the state was least certain going in.
            Status(t);
            return;
        }

        Log("Destroy instance=" + FormatEntity(instance) + " reason=TryRemoveBuff");
        DestroyUtility.Destroy(em, instance, DestroyDebugReason.TryRemoveBuff);
        // FBUFF5: nothing in the game takes Immortal off again — `.debug off` must.
        RunExplicitOffSteps(em, t.Character);

        // FBUFF17 (R45 finding 29). The reply used to be a flat "Debug off." after a void call
        // with three silent-failure paths and an outer catch that can skip GodMode.Remove
        // entirely. Word it from a read-back of the guard, and end in Status like both On paths
        // do, so this command stops being the one that asserts an outcome it did not check.
        var guardAfterClear = ReadGuardBack(t.Character);
        Log("off clear read-back guard=" + Show(guardAfterClear));
        t.Reply(FormatOffReply(guardAfterClear));
        if (_offKeptForProtection) t.Reply(OffImmortalKeptForProtectionReply);
        Log("removed");
        // MAPTP1: take the map reveal and teleport away now (the half-second push would catch it too).
        DebugMap.PushOff(t.World, t.User);
        Status(t);
    }

    /// <summary>
    /// <see cref="PrefabRegistry.AfterApply"/>: the registry just built or updated this clone in
    /// <paramref name="world"/>, before anything was instantiated from it on a client. Every step
    /// is idempotent (add when missing, OR the bits), so a re-apply writes the same values again.
    /// </summary>
    private static void OnCloneApplied(World world, PrefabDefinition def)
    {
        if (def == null || def.Guid.GuidHash != Guid.GuidHash) return;
        PatchExistingClone(world);
    }

    /// <summary>Run the prefab pass on this side's clone of the debug buff, when it exists.</summary>
    private static void PatchExistingClone(World? world)
    {
        try
        {
            if (world == null || !world.IsCreated) return;
            if (!PrefabLookup.TryGetEntity(world, Guid, out var prefab) || prefab == Entity.Null) return;
            var em = world.EntityManager;
            Log("edit begin guid=" + Guid._Value + " entity=" + FormatEntity(prefab) + " world=" + world.Name);
            PatchPrefab(em, prefab);
            Log("edit done guid=" + Guid._Value);
        }
        catch (Exception e)
        {
            Log("edit threw: " + e);
        }
    }

    /// <summary>
    /// Prefab-only pass: drain / lifetime / persist / modification flags / no-aggro /
    /// spawn-event buffers / source visuals. <c>BuffSequence</c> is safe to strip on the
    /// clone so Instantiate does not copy the haste glow. Never run
    /// <see cref="StripSourceVisuals"/>, <see cref="WriteModificationFlags"/> or
    /// <see cref="WriteDisableAggro"/> on a live instance — removing
    /// <c>BuffSequenceSpawned</c> there leaves the sequence playing (evidence 109) and
    /// rewriting <c>BuffModificationFlagData</c> / <c>DisableAggroBuff</c> there races the
    /// <c>ModificationId</c>s the apply system assigned; destroy through
    /// <c>TryRemoveBuff</c> instead.
    /// </summary>
    private static void PatchPrefab(EntityManager em, Entity entity, PrefabEditHandle? handle = null) =>
        RunSteps(em, entity, handle, PrefabPatchSteps);

    /// <summary>
    /// The one place a step name becomes a call. Both passes dispatch through here from
    /// <see cref="PrefabPatchSteps"/> / <see cref="InstancePatchSteps"/>, so those arrays are the
    /// live edit order rather than a description of it.
    /// </summary>
    private static void RunSteps(EntityManager em, Entity entity, PrefabEditHandle? handle, string[] steps)
    {
        for (int i = 0; i < steps.Length; i++)
        {
            switch (steps[i])
            {
                case StepLifeTime: WriteLifeTime(em, entity, handle); break;
                case StepDrainModifier: WriteDrainModifier(em, entity); break;
                case StepPersistTag: AddPersistTag(em, entity); break;
                case StepModificationFlags: WriteModificationFlags(em, entity, handle); break;
                case StepDisableAggro: WriteDisableAggro(em, entity, handle); break;
                case StepStripSpawnEvents: StripSpawnEvents(em, entity); break;
                case StepStripSourceVisuals: StripSourceVisuals(em, entity); break;
                default: Log("edit step unknown: " + steps[i]); break;
            }
        }
    }

    /// <summary>
    /// Post-<c>ApplyBuff</c> instance pass. Prefab-safe components only: <c>LifeTime</c>,
    /// <c>ModifyBloodDrainBuff</c>, <c>Buff_Persists_Through_Death</c>, and the spawn-event
    /// buffers a cloned buff must not carry. Never <c>BuffSequenceSpawned</c> / source visuals, and
    /// never the two modification components: <c>Instantiate</c> already copied them off the
    /// clone, and <c>Apply_BuffModificationsSystem_Server</c> writes owning
    /// <c>ModificationId</c>s onto the instance that <c>Destroy_BuffModificationsSystem_Server</c>
    /// needs to reverse the flags off the wearer's <c>BuffableFlagState</c> and the no-aggro
    /// modification off its <c>Aggroable</c>.
    /// </summary>
    private static void PatchInstance(EntityManager em, Entity entity) =>
        RunSteps(em, entity, handle: null, InstancePatchSteps);

    /// <summary>
    /// The character pass (job FBUFF5). Separate dispatcher because these steps write the
    /// <b>wearer</b>, never the buff entity: feeding a character step to <see cref="RunSteps"/>
    /// (or a buff step to this one) would silently write the wrong entity.
    /// </summary>
    private static void RunCharacterSteps(EntityManager em, Entity character, string[] steps)
    {
        for (int i = 0; i < steps.Length; i++)
        {
            switch (steps[i])
            {
                case StepImmortal: WriteImmortal(em, character); break;
                case StepClearImmortal: ClearImmortal(em, character); break;
                default: Log("edit character step unknown: " + steps[i]); break;
            }
        }
    }

    /// <summary>
    /// Give the wearer <c>ProjectM.Immortal</c> with <see cref="ImmortalValue"/>. Typed toolkit
    /// only (<c>Has</c>/<c>Add</c>/<c>TryRead</c>/<c>Write</c>) — no raw pointer write, the
    /// struct is one <c>bool</c> (server metadata). Idempotent: the component is
    /// added only when missing, and re-running writes the same value. The pre-state is recorded
    /// in <see cref="ImmortalHolders"/> so <see cref="ClearImmortal"/> can put it back exactly.
    /// </summary>
    private static void WriteImmortal(EntityManager em, Entity character)
    {
        try
        {
            if (character == Entity.Null || !em.Exists(character))
            {
                Log("edit " + ImmortalComponentName + " skip (no character)");
                return;
            }

            bool had = character.Has<Immortal>(em);
            bool was = false;
            if (had && character.TryRead(em, out Immortal current))
                was = current.IsImmortal;

            // FBUFF7 (R32 finding 2): record the grant durably, and record whether Immortal was
            // ALREADY there. An id already in the ledger keeps its original flag, so the
            // world-ready adopt re-running these steps cannot mistake its own component
            // for a pre-existing one. No id (no user row) means no durable record: the grant is
            // then in-generation only, and a restart will leave the component alone rather than
            // guess.
            bool recorded = false;
            bool hasId = TryCharacterId(em, character, out var id, out var identity);
            if (!hasId || !CanRecordGrant(id, identity))
            {
                // FBUFF9 (R34 finding 6): no readable account+character key, so no ledger row —
                // and a component written now could never be recognized as the debug buff's again,
                // which is exactly the state that leaves a player permanently unkillable. Write
                // nothing at all: no Immortal, no god-mode guard, no holder entry. The buff
                // itself stays on; the player can re-run `.debug on`.
                Log(FormatImmortalWriteRefused(FormatEntity(character)));
                return;
            }

            // FBUFF8: an existing row for this account that names a different character is
            // replaced by RecordGrant, so a recreated character starts from a fresh, honest
            // reading of "was Immortal already there".
            bool granted = TryGetGrant(DebugTools.Store, id, identity, out recorded);
            bool preExisting = granted ? recorded : had;
            RecordGrant(DebugTools.Store, id, identity, preExisting);

            if (preExisting)
            {
                // Somebody else's component, and the wearer is already immortal: the buff's
                // intent is satisfied without the debug buff writing a component it will never be
                // allowed to take back.
                Log("edit " + ImmortalComponentName + " skip write (pre-existing, not ours)"
                    + " character=" + FormatEntity(character));
            }
            else
            {
                if (!had)
                    character.Add<Immortal>(em);
                var immortal = new Immortal { IsImmortal = ImmortalValue };
                character.Write(em, in immortal);
            }

            ImmortalHolders[character] = !preExisting;

            // FHARM: Immortal only covers FDR §4 rows 3, 5, 6 — the paths that pass
            // killImmortals: false. Rows 4, 7 and 8 (the 2-arg KillEntity, the no-Health
            // KillOrDestroyEntity branch, and DeathUtilities.Kill itself) read no component at
            // all, so Core's kill-path prefixes take those. The plugin-facing form is
            // ctx.GodMode.Add(entity), which also tags the guard with the plugin for the unload
            // sweep. The guard is taken under this buff's own owner label so that .godmode's
            // hold on the same character is a separate reference (R40 finding 5); every removal
            // path this buff has (.debug off, the Removal sweep, the watchdog) funnels through
            // ClearImmortal, which drops the same label and nothing else.
            Progenitor.GodMode.GodMode.Add(character, DebugGuardOwner);

            Log(FormatImmortalSet(added: !had, was: was) + " character=" + FormatEntity(character));
        }
        catch (Exception e)
        {
            Log("edit " + ImmortalComponentName + " threw: " + e.Message);
        }
    }

    /// <summary>
    /// Undo <see cref="WriteImmortal"/> — and nothing else (job FBUFF7, R32 finding 2).
    ///
    /// The debug buff's GodMode guard is always dropped: it is unambiguously the debug buff's, and a
    /// character left guarded is one Core's kill prefixes keep alive (R27 finding 2).
    /// <c>ProjectM.Immortal</c> is removed only when the durable ledger says the debug buff granted
    /// it and the component was absent before that grant (<see cref="ImmortalClearRemoves"/>).
    /// A pre-existing component, or one on a character with no grant at all — vanilla's, an
    /// admin's, another mod's — is left exactly as it is and the decision is logged. The old rule
    /// removed the component from any character it did not have a record for, which is what R32
    /// finding 2 is about.
    /// </summary>
    private static void ClearImmortal(EntityManager em, Entity character)
    {
        try
        {
            bool cached = ImmortalHolders.TryGetValue(character, out bool ownedInGeneration);
            ImmortalHolders.Remove(character);

            // FHARM: drop the kill-path guard on every removal path, and do it before the
            // early returns below — a character the debug buff can no longer find (destroyed, or
            // never had the component) must not stay unkillable. Owner-scoped (R40 finding 5):
            // this drops this buff's hold, never a hold `.godmode` or another plugin took.
            Progenitor.GodMode.GodMode.Remove(character, DebugGuardOwner);

            if (character == Entity.Null || !em.Exists(character))
            {
                Log("edit " + ImmortalComponentName + " clear skip (no character)");
                return;
            }

            bool recorded = false;
            bool hasId = TryCharacterId(em, character, out var id, out var identity);
            // FBUFF8: the identity half of the key. A row about a character this account no
            // longer has does not answer for this one: granted stays false and the branch below
            // leaves the component alone — the R33 finding 6 case. FBUFF9: this is a pure read
            // now; the row survives a clear it was never about.
            bool granted = hasId && TryGetGrant(DebugTools.Store, id, identity, out recorded);
            bool preExisting = granted ? recorded : !ownedInGeneration;
            if (!granted && cached)
            {
                // No durable record (no user row, or the store is not attached yet) but this
                // generation made the grant: honor the in-memory one.
                granted = true;
            }
            // FBUFF9: only a row this clear is actually about is forgotten. A row that belongs to
            // a character this account no longer has is the reconcile's to drop (DropGrant), not
            // a side effect of clearing somebody else's Immortal.
            //
            // Swap / Shutdown / Unknown must keep the ledger. ReconcileImmortalOnWorldReady walks
            // GrantIds, not a world-wide buff query; forgetting here (qualification-5 swap seq
            // 4284) left the next generation with nothing to re-adopt, so the watchdog had no
            // grant and the guard stayed down while the buff survived. Removal, `.debug off`
            // and the stale-buff watchdog still forget: those paths are done with the grant.
            //
            // The reason is the one OnUnload received (Reset stores it in _grantUnloadReason),
            // never PluginUnloadScope.Current alone. Production ReloaderHost never pushes that
            // scope; Current is null on Swap and the old keepGrant branch forgot every grant.
            if (hasId && granted)
                KeepOrForgetGrant(DebugTools.Store, id, _grantUnloadReason);

            string who = hasId ? id : FormatEntity(character);
            var plan = PlanImmortal(granted, preExisting, wearsBuff: false);
            if (!granted)
            {
                Log(FormatForeignImmortal(who));
                Log(FormatImmortalDecision(who, plan));
                return;
            }

            if (!character.Has<Immortal>(em))
            {
                Log(FormatImmortalCleared(removed: false, present: false)
                    + " character=" + FormatEntity(character));
                return;
            }

            bool remove = ImmortalClearRemoves(granted, preExisting);
            if (remove)
                character.Remove<Immortal>(em);
            else
                Log(FormatImmortalDecision(who, ImmortalPlan.DropGuardOnly)
                    + " (pre-existing " + ImmortalComponentName + " left in place)");

            Log(FormatImmortalCleared(removed: remove, present: true)
                + " character=" + FormatEntity(character));
        }
        catch (Exception e)
        {
            Log("edit " + ImmortalComponentName + " clear threw: " + e.Message);
        }
    }

    private static void WriteLifeTime(EntityManager em, Entity entity, PrefabEditHandle? handle)
    {
        var life = new LifeTime
        {
            Duration = InfiniteDuration,
            EndAction = (LifeTimeEndAction)LifeTimeEndActionNone,
        };
        if (!entity.Has<LifeTime>(em))
            entity.Add<LifeTime>(em);
        if (handle != null)
            handle.Write(in life);
        else
            entity.Write(em, in life);
        Log("edit LifeTime duration=" + InfiniteDuration + " endAction=None");
    }

    private static unsafe void WriteDrainModifier(EntityManager em, Entity entity)
    {
        var type = ComponentIndex.Of<ModifyBloodDrainBuff>(ComponentType.AccessMode.ReadWrite);
        if (!em.HasComponent(entity, type))
            em.AddComponent(entity, type);

        var typeIndex = ComponentIndex.IndexOf<ModifyBloodDrainBuff>();
        byte* ptr = (byte*)em.GetComponentDataRawRW(entity, typeIndex);
        if (ptr == null)
        {
            Log("edit ModifyBloodDrainBuff GetComponentDataRawRW returned null");
            return;
        }

        // FieldOffsets from the internal research notes (inferred from stubs).
        // ModificationId fields stay 0: ModifyBloodDrainSystem_Spawn fills them.
        *(float*)(ptr + 0) = DrainBloodValue;
        *(float*)(ptr + 4) = DrainBloodValue;
        *(int*)(ptr + 8) = 0;
        *(int*)(ptr + 12) = 0;
        *(int*)(ptr + 16) = 0;
        ptr[20] = ModificationTypeSet;
        ptr[21] = ModificationTypeSet;
        *(int*)(ptr + 24) = DrainPriority;
        *(int*)(ptr + 28) = DrainPriority;
        ptr[32] = 1;
        ptr[33] = 1;
        ptr[34] = 1;
        Log("edit ModifyBloodDrainBuff BloodValue=" + DrainBloodValue + " type=Set affect=1 idle=1 ignoreIdle=1 priority=" + DrainPriority);
    }

    /// <summary>
    /// Prefab-only: give the clone <c>ProjectM.BuffModificationFlagData</c> with
    /// <see cref="ModificationFlags"/> (7 = ImmuneToHazards | ImmuneToSun | Invulnerable),
    /// the same value the vanilla <see cref="ModificationFlagVanillaSource"/> carries.
    /// <c>Buff_General_Haste</c> has no such component, so the first pass adds it.
    /// Idempotent: the bits are OR-ed into whatever is already there and
    /// <c>AddComponent</c> is skipped when the component exists, so a re-applied clone
    /// (Core swap, plugin reload, <c>Invalidate</c>) writes the same value once more
    /// instead of stacking anything. Prefab-tag gated for the same reason as
    /// <see cref="StripSourceVisuals"/>: on a live instance this component's
    /// <c>ModificationId</c> belongs to the game.
    /// </summary>
    private static void WriteModificationFlags(EntityManager em, Entity entity, PrefabEditHandle? handle)
    {
        try
        {
            if (!entity.Has<Unity.Entities.Prefab>(em))
            {
                Log("edit skip " + ModificationFlagComponentName
                    + " (not a prefab; the live instance owns ModificationId)");
                return;
            }

            bool had = entity.Has<BuffModificationFlagData>(em);
            if (!had)
                entity.Add<BuffModificationFlagData>(em);

            if (!entity.TryRead(em, out BuffModificationFlagData flags))
            {
                Log("edit " + ModificationFlagComponentName + " read failed after add");
                return;
            }

            long was = flags.ModificationTypes;
            flags.ModificationTypes = was | ModificationFlags;

            if (handle != null)
                handle.Write(in flags);
            else
                entity.Write(em, in flags);

            Log("edit " + ModificationFlagComponentName
                + " ModificationTypes=" + flags.ModificationTypes
                + " (" + ModificationFlagNames + ") was=" + was + " added=" + (!had));
        }
        catch (Exception e)
        {
            Log("edit " + ModificationFlagComponentName + " threw: " + e.Message);
        }
    }

    /// <summary>
    /// Prefab-only: give the clone <c>ProjectM.DisableAggroBuff</c> with
    /// <see cref="AggroMode"/> (1 = <c>OthersDontAttackTarget</c>), the same value the vanilla
    /// <see cref="AggroVanillaSource"/> carries, so hostile units stop aggroing on the wearer
    /// and drop it as a target. <c>Buff_General_Haste</c> has no such component, so the first
    /// pass adds it. Idempotent and OR-safe exactly like <see cref="WriteModificationFlags"/>:
    /// <c>AddComponent</c> only when missing, then the mode bits are OR-ed into whatever is
    /// already there. Prefab-tag gated for the same reason: on a live instance
    /// <c>Apply_BuffModificationsSystem_Server</c> owns the paired
    /// <c>DisableAggroBuffModifications</c> ids, and it is the one that adds that component —
    /// the debug buff never writes it.
    /// </summary>
    private static void WriteDisableAggro(EntityManager em, Entity entity, PrefabEditHandle? handle)
    {
        try
        {
            if (!entity.Has<Unity.Entities.Prefab>(em))
            {
                Log("edit skip " + AggroComponentName
                    + " (not a prefab; the live instance owns DisableAggroBuffModifications)");
                return;
            }

            bool had = entity.Has<DisableAggroBuff>(em);
            if (!had)
                entity.Add<DisableAggroBuff>(em);

            if (!entity.TryRead(em, out DisableAggroBuff aggro))
            {
                Log("edit " + AggroComponentName + " read failed after add");
                return;
            }

            int was = (int)aggro.Mode;
            aggro.Mode = (DisableAggroBuffMode)(was | AggroMode);

            if (handle != null)
                handle.Write(in aggro);
            else
                entity.Write(em, in aggro);

            Log("edit " + AggroComponentName
                + " Mode=" + (int)aggro.Mode
                + " (" + AggroModeNames + ") was=" + was + " added=" + (!had));
        }
        catch (Exception e)
        {
            Log("edit " + AggroComponentName + " threw: " + e.Message);
        }
    }

    private static void AddPersistTag(EntityManager em, Entity entity)
    {
        if (!entity.Has<Buff_Persists_Through_Death>(em))
            entity.Add<Buff_Persists_Through_Death>(em);
        Log("edit Buff_Persists_Through_Death");
    }

    private static void StripSpawnEvents(EntityManager em, Entity entity)
    {
        TryRemove<CreateGameplayEventsOnSpawn>(entity, em, "CreateGameplayEventsOnSpawn");
        TryRemove<GameplayEventListeners>(entity, em, "GameplayEventListeners");
        TryRemove<RemoveBuffOnGameplayEvent>(entity, em, "RemoveBuffOnGameplayEvent");
        TryRemove<RemoveBuffOnGameplayEventEntry>(entity, em, "RemoveBuffOnGameplayEventEntry");
        // Source is Buff_General_Haste: keep ShowOnBuffBar / ShowOnBuffBarData (the icon),
        // drop the haste speed so .debug is drain-only.
        TryRemove<ModifyMovementSpeedBuff>(entity, em, "ModifyMovementSpeedBuff");
        TryRemove<ModifyMovementSpeedBuffModification>(entity, em, "ModifyMovementSpeedBuffModification");
    }

    /// <summary>
    /// Drop the source haste glow. <c>BuffSequence</c> is the prefab buffer
    /// <c>BuffSequenceSystem_Spawn.CheckAndApplyBuffSequences</c> consumes; the rest are
    /// stripped if the clone inherited them. Do not remove ShowOnBuffBar / ShowOnBuffBarData /
    /// Buff / LifeTime / network components. Prefab-tag gated: a live instance must never
    /// lose <c>BuffSequenceSpawned</c> (evidence 109).
    /// </summary>
    private static void StripSourceVisuals(EntityManager em, Entity entity)
    {
        if (!MayStripSourceVisuals(entity.Has<Unity.Entities.Prefab>(em)))
        {
            Log(StripSkipLog);
            return;
        }
        TryRemove<BuffSequence>(entity, em, "BuffSequence");
        TryRemove<ProjectM.Sequencer.BuffSequenceSpawned>(entity, em, "BuffSequenceSpawned");
        TryRemove<ProjectM.Sequencer.SpawnSequenceForEntity>(entity, em, "SpawnSequenceForEntity");
        TryRemove<SequenceGUID>(entity, em, "SequenceGUID");
    }

    private static void TryRemove<T>(Entity entity, EntityManager em, string name)
    {
        try
        {
            if (entity.Has<T>(em))
            {
                entity.Remove<T>(em);
                Log("edit removed " + name);
            }
        }
        catch (Exception e)
        {
            Log("edit remove " + name + " threw: " + e.Message);
        }
    }

    private static bool TryApply(
        World world,
        Entity userEntity,
        Entity characterEntity,
        PrefabGUID guid,
        out Entity instance,
        out string why)
    {
        instance = Entity.Null;
        why = "";
        try
        {
            var des = world.GetExistingSystemManaged<DebugEventsSystem>();
            if (des == null)
            {
                why = "DebugEventsSystem missing";
                return false;
            }

            var from = new FromCharacter { User = userEntity, Character = characterEntity };
            var evt = new ApplyBuffDebugEvent { BuffPrefabGUID = guid, Stacks = 1 };
            Log("ApplyBuff guid=" + guid._Value + " character=" + FormatEntity(characterEntity) + " user=" + FormatEntity(userEntity));
            des.ApplyBuff(from, evt);

            var em = world.EntityManager;
            if (!TryFindBuff(em, characterEntity, guid, out instance) || !em.Exists(instance))
            {
                why = "ApplyBuff ran but BuffBuffer has no progenitor.debug row yet";
                return false;
            }

            // Prefab-safe instance edits only (lifetime / persist / spawn-event buffers /
            // drain). Never strip BuffSequenceSpawned here — evidence 109.
            PatchInstance(em, instance);

            // FBUFF5: the character pass. Immortal lives on the wearer, not on the buff — no
            // prefab component adds it (see the god-mode block above), so the debug buff does.
            RunCharacterSteps(em, characterEntity, CharacterApplySteps);

            why = "applied";
            return true;
        }
        catch (Exception e)
        {
            why = e.GetType().Name + ": " + e.Message;
            Log("ApplyBuff threw: " + e);
            return false;
        }
    }

    /// <summary>
    /// MAPTP1: whether <paramref name="character"/> wears <c>progenitor.debug</c> now (a live buff entity in its
    /// <c>BuffBuffer</c>). <see cref="DebugMap"/> gates the map reveal and teleport on it.
    /// </summary>
    internal static bool HoldsBuff(EntityManager em, Entity character) =>
        TryFindBuff(em, character, Guid, out var instance) && em.Exists(instance);

    /// <summary>
    /// Whether Core's <c>.debug</c> holds a grant on <paramref name="character"/> (the durable
    /// ledger or this generation's holder cache). <c>ctx.Debug</c>'s
    /// <see cref="Progenitor.Buffs.DebugProtection"/> asks this before it removes a
    /// <c>progenitor.debug</c> buff or clears <c>Immortal</c>: while it is true, both are Core's.
    /// Never throws.
    /// </summary>
    internal static bool CoreHoldsGrant(EntityManager em, Entity character)
    {
        try { return HasOwnGrant(em, character); }
        catch (Exception e)
        {
            Log("grant lookup threw: " + e.Message);
            // Unknown is treated as Core's: leaving a buff on is recoverable, stripping it is not.
            return true;
        }
    }

    private static bool TryFindBuff(EntityManager em, Entity target, PrefabGUID guid, out Entity buffEntity)
    {
        buffEntity = Entity.Null;
        try
        {
            var bufferType = ComponentIndex.Of<BuffBuffer>(ComponentType.AccessMode.ReadOnly);
            if (!em.Exists(target) || !em.HasComponent(target, bufferType))
                return false;
            var buffer = em.GetBuffer<BuffBuffer>(target, true);
            int hash = guid._Value;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].PrefabGuid._Value != hash) continue;
                buffEntity = buffer[i].Entity;
                return buffEntity != Entity.Null;
            }
            return false;
        }
        catch (Exception e)
        {
            Log("BuffBuffer walk threw: " + e.Message);
            return false;
        }
    }

    private static string FormatEntity(Entity entity) => entity.Index + ":" + entity.Version;
#endif

    internal static void Log(string step)
    {
        string line = "Debug: " + step;
        try
        {
#if PROGENITOR_TESTS
            global::Progenitor.Plugin.Logger?.LogInfo(line);
#else
            Plugin.Logger?.LogInfo(line);
#endif
        }
        catch { }
    }
}
