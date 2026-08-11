# LaunchInventoryTidy v3+ Permanent P2P Admission Gates

Source: `D:\Agent-工作目录\.audit\phase3-p2p-static-audit\Codex-P2P-StaticAudit-Suite-v4.0-20260801.md`, sections 3.2 and 4.2.

These gates are release blockers for every LIT v3.0+ change that claims, enables, or can affect SteamP2PFriends / P2P Listen Host behavior. A clean build, a single-player PASS, or a U3DS PASS cannot waive either gate.

## P0-LIT-02: Persistent fault-circuit scope isolation

- [ ] `TidyFaultCircuitPersistence` exposes and uses `InitializeForScope(...)`; it must not permanently select one global `persistent_faults.json` in `Awake()`.
- [ ] Scope initialization occurs only after mode and world/save-slot identity are stable, and `InitializeForScope(...)` and `Load()` execute at the same stable session boundary.
- [ ] The scope distinguishes at minimum single-player versus P2P Listen Host. It also includes a sanitized world/map identity and validated save-slot identity.
- [ ] Scope changes clear in-memory fault-circuit state before loading the new scope. No implicit copy, migration, or fallback from another scope is allowed.
- [ ] SteamID remains an in-file record key; it must not be used in the filename and is not a substitute for mode/world/slot isolation.
- [ ] Dynamic regression: same SteamID executes `single-player -> P2P -> single-player`; a P2P persistent fault must not appear in either single-player scope, and each scope file must be independently evidenced.

## P0-LIT-01: Listen Host local-session identity sequencing

- [ ] `OnServerHosted()` must not call `ServerSessionRegistry.BeginSession(...)` using a potentially pre-override `Provider.server` value for the local Listen Host.
- [ ] `OnServerHosted()` may set a pending flag only. The main-thread `Update()` path must call `TryBeginLocalListenHostSession()` after dispatcher work.
- [ ] `TryBeginLocalListenHostSession()` requires `Provider.isServer && Provider.isClient`, non-Nil IDs, `Provider.server == Provider.client`, and `Player.LocalPlayer != null` before `BeginSession`.
- [ ] Until those predicates hold, the method leaves the session pending and performs no registry write. RNG/setup failure must fail closed, clear pending, and must not retry with a substitute nonce or identity.
- [ ] Disconnect, unload, and a later host session reset pending/started flags and clear the matching `ServerSessionRegistry` entry.
- [ ] Dynamic regression: after P2P host creation, record that `BeginSession` SteamID, `Provider.server`, `Provider.client`, and LMN `LoopbackToServer` sender are the same ID. The host's first tidy request must commit without nonce rejection or `NotSupportedException`.

## Required evidence for either gate

- [ ] Source diff identifies every initialization, reset, load, and persistence write path.
- [ ] Release and TestHarness builds compile with zero errors and zero warnings; TestHarness-only code remains excluded from Release.
- [ ] New DLL version/identity/hash and LMN v4 ABI evidence are supplied.
- [ ] P2P minimum matrix T1, T2, and T3 from the source audit passes before P2P Alpha release is considered.

## Explicit non-waiver boundary

P2P remains **not approved** until P0-LIT-01 and P0-LIT-02 are independently closed by static review and dynamic dual-machine evidence. Existing single-player and U3DS approvals do not cover this transport/mode.
