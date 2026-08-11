# Changelog

All notable changes to LaunchInventoryTidy are documented here.

## [3.0.1] - 2026-08-05

### Added

- Scoped persistent fault storage for `singleplayer` and `p2p` sessions, keyed by mode, normalized map name, map hash, and save slot.
- Fail-closed scope switching with main-thread assertion, non-mutating validation before state replacement, and global degradation on transition failure.
- Optional SteamP2PFriends bridge: when LIT is installed in a P2P Listen Host, SteamP2PFriends initializes the P2P scope only after Stage6A and host identity are stable.

### Changed

- Updated LIT metadata to 3.0.1.0.
- Updated the P2P bridge contract to `BeginScope(string mode, string mapName, int saveSlot) -> bool`.

### Security and release boundary

- P2P is Alpha-only. It is not a production support claim until two-machine T1-T3 dynamic validation has passed.
- A missing LIT plugin does not block SteamP2PFriends. An installed LIT plugin with an unavailable or failed scope bridge aborts P2P host startup fail-closed.

## [3.0.0] - 2026-08-01

### Changed

- Established the v3 baseline and raised the mandatory LaunchMultiplayerNet dependency to 4.0.0 or later.
- Standardized the distributable filename as `LaunchInventoryTidy.dll` with no version suffix.
- Rebuilt the TestHarness and U3DS harness against the v3 / LMN v4 ABI.

### Verification

- Single-player automated suite: 18/18 passed.
- U3DS controlled dual-end suite: passed, including snapshot comparison and cooldown coverage.

## [2.0.x] - 2026-07

- Introduced server-authoritative manual tidying, transaction journals, post-commit verification, rollback safety, session tokens, replay protection, admission control, fault persistence, and automated test harnesses.
- Passive inventory sorting remained disabled as a safety boundary.

## [1.4.1] - 2026-07-28

- Disabled the passive `Items.tryAddItem` sorting path after a safety review.
