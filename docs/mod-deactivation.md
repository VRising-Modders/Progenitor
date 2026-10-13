# Coordinated mod deactivation

**Status: planned; not implemented or live-verified.** Extension of M4.4, [mod manifest and client sync](mod-manifest.md). This is the authoritative design for disabling a server mod and its corresponding client functionality without intentionally disconnecting players or the testing agents. Existing [hot reload](hot-reload.md) does not implement this coordination.

**Scope note: per-server mod pause is not this operation.** The client-side
[per-server mod pause](mods-menu.md#per-server-mod-pause) (implemented, not live-verified) only
unloads *this client's own* hot-loaded `ServerMustHave` plugins that the joined server does not run,
decided locally before the handshake reply, and loads them again when the client leaves. It takes
no instruction from the server, never touches a server mod, never deactivates a mod the server
runs, and never pauses a `NoNeedForSync` or `ClientOnly` plugin. Everything below, including
server-requested and in-session deactivation, remains planned.

## User-facing behavior

An operator selects a mod and chooses **Disable safely**. Progenitor shows the affected mods, connected clients and blockers before making changes. It checks all participants, pauses only the selected feature, drains its active work, deactivates its client and server parts, and confirms the result. The game, unrelated mods and the Dev bridge continue running.

If safe deactivation is unavailable, show **Cannot disable while players are connected**, with a specific reason. Do not automatically kick, disconnect, restart, force unload or delete a client DLL. A separate maintenance decision is outside this operation.

The client receives one unobtrusive status notice, not an installation prompt. Its Server Mods panel distinguishes **Active**, **Pausing**, **Disabled for this server**, **Blocked**, and **Recovery needed**. Installed files and saved settings remain available. Repeated status packets must not reopen UI or steal focus.

This is a qualification target, not a promise that arbitrary native mod code cannot crash. Phase 1 admits only a tested, explicitly cooperative subset. Unknown cleanup or a blocking/native callback is a reason to refuse, not to run it and hope a timeout can interrupt it.

Route supported server disable controls through this coordinator before removing registrations or changing the active manifest. An arbitrary plugin's internal config toggle is not automatically a coordinated disable. External DLL deletion or a plugin crash bypasses preparation: report an uncoordinated loss and preserve diagnostics; do not pretend a catalog update can retroactively make it safe or order clients to unload blindly.

## Scope and authority

| Case | Required behavior |
|---|---|
| Package installed for this server session | May deactivate under the client permission granted for session management. Preserve the package; this operation is not uninstall. |
| Permanently installed or independently installed mod | Do not globally disable it because a server asks. A separately consented, server-scoped feature lease may pause only this server's use; otherwise refuse. |
| Several plugins share one DLL, or dependents share its services | Include the full affected dependency/assembly group in the preview and authorization. Refuse if it cannot be safely separated or the expanded scope was not authorized. |
| Client lacks the corresponding mod | Treat as already inactive only if its capability/state report establishes that fact and the server feature supports that client. |
| Legacy client or unknown state | Block live coordinated deactivation. Silence is never approval or readiness. |
| Bootstrap, Core, Dev, coordinator, transport, or their required dependencies | Protected; cannot be targets or indirect cascade victims. Keep control/status/cancellation reachable. |

Authenticate operator requests through the existing local/admin boundary. Client instructions must come from the current authenticated game connection and match the locally accepted server identity and scope. A signed catalog is not permission to disable arbitrary software. Never accept server-supplied filesystem paths, cleanup code, reflection targets or lists of unrelated client plugins.

Use explicit plugin IDs, loaded image hashes and locally recorded ownership; do not identify a mod by display name or filename alone. Session policy is scoped by server identity and connection incarnation. A change of server must not inherit another server's disabling authority.

## Eligibility contract

Hot-reload support and `UnsafeHarmony` checks are necessary inputs, not a deactivation guarantee. A qualified mod must declare a versioned cooperative lifecycle and pass these gates:

- Stop admitting new casts, tasks, commands and mod messages while allowing owned in-flight work to finish or cancel safely. Keep completion handlers installed until the work has drained.
- Enumerate active callbacks, tasks, owned entities/effects/UI and dependencies. Prove no native work or external registrations can call the retiring code after detachment. A zero managed task count alone is insufficient.
- Provide a bounded, tested resume path for preparation failures. Do not call arbitrary `Unload` during a read-only plan or assume replaying `OnLoad` reverses partial teardown.
- Identify any persisted content or outstanding gameplay state requiring the mod. Phase 1 refuses such state unless its safe lifetime has already been demonstrated; later support needs a specific migration/retention contract.
- Resolve local ownership and a validated method of disabling only the intended feature. Shared client-wide services cannot be unloaded merely because one server no longer uses them.

Initial supported example: one server feature with a client UI/visual subscription, no custom persistent prefab, possession, native patch, active cast or asynchronous native job. Qualify reusable framework-owned effects separately after the basic protocol passes. NPC possession and arbitrary spell teardown are excluded from the first release.

## Transaction and failure behavior

This is a coordinated state machine, not instantaneous distributed atomicity. Other gameplay must remain usable during preparation and recovery. The server retains its compatible runtime until all participants have established a safe cutover boundary.

| State | Required action and observable completion |
|---|---|
| Plan | Read-only snapshot of targets, image hashes, dependencies, ownership, capabilities, connection/world generations and blockers. No unload, disk move or policy mutation. |
| Prepare | Lock the target group against reload/deploy/enable changes. Obtain explicit readiness from every affected connected client and the server. Freeze the participant set; joins/rejoins invalidate preparation and require a new plan before gameplay admission. |
| Quiesce | Gate new target work on all participants. Drain or cancel outstanding work cooperatively, including queued mod messages. Acknowledge the transaction's resource/traffic barrier, not merely receipt of its packet. The server code and cleanup handlers remain installed. |
| Commit decision | Only after every participant is quiescent and all identities still match, persist the decision and intended disabled policy in framework-owned storage. Cancellation after this point is not a promise to restore the old state. |
| Deactivate clients | Apply the qualified detach/cleanup path on each client; retain the server's compatible, quiescent runtime while waiting. Return explicit resource and activation-state readbacks. Repeated requests are idempotent. |
| Deactivate server | After all client deactivation acknowledgments, apply server cleanup and read it back. Publish the new active-mod revision and handshake expectations only at the coordinated boundary. |
| Complete | All participants report the intended revision inactive, no target callbacks/resources remain, the disabled policy prevents watcher reload, and player connections plus the Dev bridge still function. Report assembly collection separately. |

Before the commit decision, a refusal, timeout, identity change or cancellation aborts preparation. Release reservations and resume only through the qualified resume path; verify all participants are active again. If resume fails, report **Recovery needed**, keep the affected feature gated where necessary and preserve diagnostics. Do not label that case an ordinary successful abort.

After the commit decision, a lost acknowledgment or cleanup exception is **Recovery needed**. Query durable participant state and retry the same operation ID; do not infer success, roll back half the clients blindly, unload the server early, or kick to make the participant count smaller. An unreachable client may be removed from the participant set only after the server establishes that its game connection actually ended. Reconnection creates a new incarnation that must reconcile before target gameplay resumes.

A timeout bounds waiting, not arbitrary native execution. No synchronous wait for network replies, task completion or forced GC on the game thread. Apply lifecycle steps at known safe frame boundaries and keep status/cancellation handling outside the target mod.

Readiness must be revalidated at each mutation boundary. Serialize Core swaps, target DLL deployments, enables/disables and world/save transitions with this operation. Phase 1 refuses when such a transition is already active and queues conflicting operations once preparation begins. Do not pause the entire game or stall autosave indefinitely: first-phase targets must not require save mutation; future save-affecting support needs bounded coordination with the save system.

## Reload, content and connection integration

**Disable without deleting.** Add a framework-owned activation policy checked by both reload paths before discovery/reload, plus an explicit suspended state. Do not use file removal or the hash allowlist as a substitute. Track policy by plugin identity and allowed image revision; a changed DLL must not silently reactivate it. Server policy persists until explicit enable. Client session overrides expire or reconcile on leaving, subject to local policy; ending a session must not silently enable a client-wide plugin.

**Unload reasons.** Add an explicit deactivation reason/contract with matching Bootstrap/Core protocol handling. Do not map this to `Removal`, whose consumers may erase persistent world state, or `Swap`, which expects replacement. Existing mods without the new lifecycle remain ineligible. Framework-owned registrations still require complete cleanup.

**Content.** Existing prefab unregister/edit withdrawal packets are data synchronization, not DLL deactivation acknowledgments. Locally owned client registrations require their own agreed cleanup. Preserve item-key positions/tombstones and save identities; do not compact shared indices, destroy in-use prefabs or erase mod save rows. If a live item, buff, unit or dependency cannot safely survive without the mod, refuse before commit. Temporary effects must finish or be released through their verified ownership path.

**Messages and handshake.** Negotiate a new capability and explicit activation revision on both sides. Use separate transaction messages with bounded schemas; catalog omission must never mean an unload command. Preserve normal handshake enforcement for unrelated mods. Target-specific handshake changes must understand the pending transaction so an intermediate mismatch does not trigger a kick. Stamp target traffic with activation revision or establish an equivalent proven drain barrier; late packets cannot invoke retired handlers. After completion, new joins use the disabled revision. A joining client must reconcile its mod state before admission without being forcibly disconnected; if compatibility cannot be maintained, hold the join before gameplay and report why.

**Recovery.** The coordinator belongs to framework infrastructure, not either target mod. Keep a bounded journal of operation ID, decision, participant incarnations, hashes, policy revision and observed results; store plain data, never collectible delegates or entity pointers. Persist the local decision before each irreversible step. Recover after Core replacement/process restart by reconciling actual state and policy; never replay a stale request against a new world. Define journal expiry and compaction only after all affected participants are reconciled. A running Core swap is serialized, not used as a deactivation mechanism.

**Collection.** Report `inactive` and `assemblyCollected` separately. A retained assembly is not permission to load another copy. A known unsafe pin blocks preparation. A pin discovered afterward leaves the feature gated with diagnostics and prevents re-enable/reload until the qualified recovery path can run. Do not restart automatically to obtain a green result.

All activation entry points must honor disabled policy, including manual/reload-all scans, redeployment, pending-package promotion and Edit-and-Continue. Process the affected dependency group with dependents before providers. Reject unknown/cyclic ordering unless the group has an explicitly tested joint lifecycle.

## Proposed API and examples

**Admission qualification is a prerequisite.** The bounded join/rejoin hold must be proven against both Progenitor's handshake timers and the game's transport/admission deadlines. Do not assume postponing a handshake result keeps the connection alive. If that path is unavailable, refuse or defer live deactivation before preparation. Any held join that times out or disconnects during the acceptance scenario is a failure, even if the original players stayed connected. Set finite phase/admission budgets from that qualification and report expiry without disguising it as a completed operation.

**Proposed names only; no callable implementation exists.** UI, Dev bridge and MCP must share one coordinator and the same checks. Agents call Progenitor; they must not script client DLL deletion or independent client/server unloads.

| Proposed bridge operation | Purpose |
|---|---|
| `mod_deactivation_plan` | Return plan ID/revision, affected identities, client count, blockers and expiry without mutation. |
| `mod_deactivation_begin` | Execute an unchanged, unexpired authorized plan with an idempotency key. Return operation ID and initial state, never premature success. |
| `mod_deactivation_status` | Read per-participant state, blockers, resource counts, policy revision, last errors and collection status. |
| `mod_deactivation_cancel` | Cancel before commit; afterward report the durable decision and recovery state. |

Provide matching MCP wrappers at implementation time. Do not advertise them before they exist. Participant messages carry protocol version, operation ID, server/connection incarnation, plan/activation revision, target image identity and phase sequence. Authenticate, size-limit, rate-limit and reject stale, duplicate-conflicting or wrong-phase messages. Accept repeated identical requests without repeating cleanup. Use bounded retry/backoff and a finite per-connection operation budget; one active mutation per affected group.

Example acceptance flow, **not run**:

1. Load the qualified sample on an isolated server and two real clients. Record versions, hashes, active counters, player connections and bridge responsiveness.
2. Plan its deactivation. Expect only the sample and its owned client feature; both clients must be ready. Begin that exact plan through Progenitor.
3. Poll status. Expect quiescence before client cleanup, both client acknowledgments before server cleanup, and `Complete` only after state readback.
4. Confirm the sample command/UI/effect is inactive, new work is rejected cleanly, owned counters return to baseline, both players can move/chat and the testing agents can still query the bridge.
5. Let normal watcher scans run and repeat a byte-identical deploy. Expect the feature to remain disabled. Test changed-image behavior separately; it must not bypass the policy.
6. Restore through a separately specified explicit enable workflow or reset the isolated fixture after recording evidence. The first release must include that reversible fixture/setup procedure; never improvise recovery on a live server.

## Delivery and acceptance gates

1. **Policy and protocol tests:** transaction ordering, idempotency, stale identity/epoch rejection, capability negotiation, authorization, dependency closure, protected targets and bounded buffers. Fault-inject every transition, including unknown cleanup results and rollback/resume failures.
2. **Loader integration:** both reload paths honor disabled policy; default deployment cannot reactivate targets; resource tracking and quiescence are observable. Test loaded-but-inactive and pinned states independently.
3. **Two-client qualification:** sample deactivation and repeated cycles with explicit enable; zero unintended disconnects/crashes, responsive bridge, no duplicates, no changed unrelated mods. One refusing/legacy client aborts before teardown. Independently installed client mods remain untouched.
4. **Failure qualification:** drop/duplicate/reorder transaction messages, lose acknowledgments, delay a client, reconnect during preparation/commit, throw during cleanup, fail resume, change a DLL/dependency, attempt a concurrent Core swap and reload the world. Verify accurate partial/recovery status and no timeout-driven kicks or forced unloads.
5. **Persistence and restart qualification:** restart at each durable boundary using isolated backups; reconcile policies/journals without stale-world actions or corrupted saves. This tests external failures; the deactivation operation itself never requests a restart.
6. **Product completion:** concise programmer sample, agent-tool example and UI use the same service. Every step has expected observations and dated external evidence. Mark only the tested mod/resource classes supported; content-heavy mods remain blocked until separately qualified.

Source observations and design risks are recorded in the internal research notes. A successful implementation of ordinary hot reload or catalog resend does not close these gates.
