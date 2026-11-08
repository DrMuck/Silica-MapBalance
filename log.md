# Si_MapBalance — Change Log

## v3.4.0 — 2026-11-08

### Feature: 2v1 layouts for Si_VersusVote rounds
- Si_VersusVote turns a voted HvHvA on some maps (IndustrialQuarter) into a 2v1: Sol and
  Centauri allied for the whole round against the Aliens. New layout flag
  `"game_modes": { ..., "2v1": true }` (an extra key in the existing mode map, like `4way`).
- 2v1 round (`VersusApi.TwoVsOneActive` and 3 active teams): only `"2v1"` layouts are
  candidates; none on the map -> Warning once per map, normal HvHvA pool. Normal HvHvA
  round: `"2v1"` layouts are excluded even if they also say `"hvhva": true`.
  HvH / HvA / 4way unchanged. Missing key = normal layout.
- `VersusVoteBridge` reads `Si_VersusVote.VersusApi` by reflection (ApiVersion >= 1);
  lookup retried once per round while missing. Without Si_VersusVote: as 3.3.2.
- Timing: the pick happens in the DistributeAllResources prefix (vanilla calls it right after
  SetTeamVersusMode returns, MP_Strategy 1.0.15 ~2590) or the SpawnBaseStructures prefix
  (first team join, headless path) — both after VersusVote's Priority.First
  SetTeamVersusMode prefix set the flag. The 2v1 state is part of the per-round reuse key:
  if it differs at SpawnBaseStructures (HQs not yet placed), re-pick + re-apply resources.
- Logs: `2v1 round on <Map>: N candidate layout(s) [...] — picked X`, mode shown as
  `hvhva/2v1` in `Layout pick/reuse`, `N 2v1 layout(s) excluded (not a 2v1 round)`.
  Allchat: `Map Balance: X [2v1: Sol + Centauri vs Aliens]`. `map_layout` log line unchanged.
- Tool: checkbox "2v1 (allied humans vs Aliens)" under Game Modes (Si_MapBalanceTool b44c1a7).

## v3.3.2 — 2026-11-01

### Feature: per-layout switch for the Sol ↔ Cent swap
- New top-level layout field `"swap_spawns"`: `false` keeps Sol and Cent on their own
  positions for that layout (asymmetric hand-made layouts); absent or `true` = the old
  50 % swap in HvH / HvHvA, so existing layouts behave as before.
- Log line `<mode>: Sol ↔ Cent swap disabled by the layout (swap_spawns: false)`; no
  "(swapped)" suffix in the announcement then.
- The MapBalance web tool writes the field (checkbox "Random Sol/Cent swap" under Game Modes).

## v3.3.1 — 2026-10-30

### Bug Fix: resources and HQs came from two different layouts
- **Problem**: On configured maps the Balterium/Biotics totals on the ground matched some
  OTHER layout of the map than the one announced in allchat and logged as `map_layout`.
  HvHvA rounds on IndustrialQuarter got the 60k/42k patch amounts that only the hvh-only
  layouts configure ("larger patches" complaints). Seen in the HL logs 2026-10-23..26.
  Headless rounds (no game call to DistributeAllResources) were correct.
- **Root cause**: two independent random picks per round. The game calls
  `DistributeAllResources` first (MP_Strategy vote end: `SetTeamVersusMode` then
  `DistributeAllResources`; `SpawnBaseStructures` only later, on the first team join).
  The prefix called `LoadMapConfig()` with the default mode `"hvh"` and applied that
  layout's resources. `SpawnBaseStructures` then detected the real mode, called
  `LoadMapConfig(gameMode)` again, got a different layout, overwrote `_activeConfig`,
  placed HQs and announced that one. Its resource fallback was a no-op because the
  round was already marked distributed.
- **Fix**: exactly one layout pick per round (`PickLayout`), used for both spawns and
  resources, made by whichever hook runs first:
  - Game-mode detection moved into `DetectGameMode(...)`, shared by both hooks. The prefix
    reads the current `MP_Strategy` through `GameMode.CurrentGameMode`. Teams are
    already active at that point (`GetTeamSetupActive` = Enabled + `TeamsVersus`, which
    is set right before the call), so the prefix now picks for the REAL mode.
  - `SpawnBaseStructures` reuses the pick if it was made for the same mode
    (`_activeConfigMode`). If the modes disagree it logs a Warning, picks again for the
    mode the HQs are placed for and applies the resources again, so both stay on one layout.
  - If the mode cannot be known in the prefix (fewer than 2 active teams, not 4way), the
    prefix does not pick. It skips vanilla and leaves distribution to SpawnBaseStructures,
    which replays vanilla `DistributeAllResources` if the map/mode has no layout.
  - `EnsureResourcesDistributed` never loads a config itself any more. A config without
    spawns now also gets its resources applied at spawn time. If no ResourceAreas exist
    at spawn time, the pick is kept for a later game call or the OnGameStarted safety net.
  - A pick left over from another map (OnGameEnded missed) is thrown away, not reused.
    All per-round pick state is reset in `OnGameEnded`.
  - Non-strategy modes (Siege, TowerDefense: no SpawnBaseStructures) keep the old hvh-pool
    pick in the prefix. Maps without a config still run vanilla unchanged. The 50 % Sol/Cent
    swap is unchanged.
- **Logging**: one `Layout pick: <file> (mode <m>, by <prefix|spawn>)` line per round,
  `Layout reuse: ...` from the other hook, and `Layout pick mismatch: ...` as a Warning if
  the hooks ever disagree.

### Bug Fix: no amounts at all after a map change without a round end
- **Problem**: live test 2026-10-30 with 3.2.0. GreatErg ended without OnGameEnded and the
  server loaded IndustrialQuarter, then Badlands. On both, the game call logged "Already
  distributed this round — skipping original only". The mod applied nothing, and because
  the original was skipped vanilla did not apply its 0.8x amounts or hide 50 % of patches
  either. Every patch stayed at the raw scene amount (IndustrialQuarter 30 Balterium at
  60-96k, 172 Biotics at 22k; Badlands 96 patches, 4,173,600 = full vanilla total). The
  spawn-time fallback was a no-op for the same reason. Hits every admin map change, map
  vote and same-map restart not preceded by a proper round end (373 `restarted_same_map`
  rounds in the archive). This is the bigger cause of the "larger patches" reports.
- **Root cause**: `_distributedThisRound` / `_activeConfig` were only reset in
  `OnGameEnded`, so a mark from the previous map survived the level load.
- **Fix**: a round epoch. `BeginNewRound` bumps it and resets all pick, distribution,
  swap, specials and spawned-area state. It runs on
  - every `SceneManager.sceneLoaded` with `LoadSceneMode.Single` (Silica loads Loading
    and every map that way, see `GameLevelLoader`),
  - `MusicJukeboxHandler.OnGameRestart` (MP_Strategy.Restart: the start of every round,
    including an in-place `Restart_Synced` without a scene load),
  - `OnGameEnded` (as before).

  A layout pick and the "amounts applied" mark carry the epoch and the map name they were
  made in. `AmountsAppliedThisRound()` (same epoch, same map, config present) is the only
  gate for "Already distributed", so a game call in a new scene or round always distributes.
  A pick from another epoch or map is discarded with a Warning.

---

## v3.1.0 — 2026-03-06

### Game Mode Filtering
- Detects active game mode from team composition: **HvH** (2 human teams), **HvA** (human+alien), **HvHvA** (3 teams)
- Only picks layout configs whose `game_modes` field matches the current mode
- Falls back to any available config if no mode-specific match exists

### Sol ↔ Cent Swap
- In HvH mode, randomly swaps Sol and Centauri spawn positions (50% chance)
- Doubles effective layout variation without needing extra config files

### Allchat Announcement
- Announces the selected layout config name in allchat at game start
- Shows "(swapped)" suffix when Sol↔Cent positions were swapped
- Uses `Player.SendServerChatMessage` (server chat, visible to all players)

### Config Deployment
- All 20 layout JSONs deployed from `Desktop\Spawns\` to `UserData\Spawns\{MapName}\`
- Maps with configs: Badlands (5), CrimsonPeak (6), GreatErg (2), NarakaCity (1), NorthPolarCap (1), RiftBasin (2), WhisperingPlains (3)

---

## v3.0.0 — 2026-03-04

### Web Tool Integration
- **New config directory**: `UserData/Spawns/{MapName}/*.json` — directly consumes web tool layout exports
- Multiple layout files per map are supported; one is picked randomly each round
- **Backward compatible**: still falls back to `UserData/MapBalance/configs/{MapName}.json` (legacy format)

### New features
- **`patch_overrides`**: per-patch resource amount overrides (e.g., `"2": 60000` sets patch index 2 to 60K)
- **Per-faction settings**: `solBuildRadius`, `centBuildRadius`, `solChainRange`, `centChainRange`, `alienNodeChainRange`, `alienBiocacheChainRange`
- **`game_modes`**: field parsed but not yet enforced (hvh, hva, hvhva)
- Spawn format handles both web tool arrays (`"Sol": [{"x","z","isSpawn","isBiocache"}]`) and legacy variants (`"variant_A": {"Sol": {"x","z"}}`)

### Internal
- Removed `SpawnVariant` class and `SelectSpawnVariant()` — replaced by `MapConfig.GetFactionSpawn()` which handles both formats
- Removed legacy hardcoded `DesignedPositions` fallback path (dead code after v2.1.0 fix)
- Config model uses `JToken` for spawns to flexibly parse both schemas

---

## v2.1.0 — 2026-03-04

### Bug Fix: Vanilla fallback for unconfigured maps
- **Problem**: Maps without a JSON config in `UserData/MapBalance/configs/` had all their
  resources zeroed out and replaced with hardcoded positions designed for a specific map layout.
  This caused resources to be deleted or placed incorrectly on any map that didn't have a config.
- **Root cause**: `Prefix_DistributeAllResources` always returned `false` (skip original game logic),
  and `CustomDistribution()` zeroed all existing ResourceAreas before checking whether a config
  existed. The "legacy" fallback spawned resources at 64 hardcoded coordinates that only made sense
  for large 4800x4800 maps.
- **Fix**: Check for a map config *before* doing anything destructive. If no config exists,
  return `true` from the Harmony prefix so the game's vanilla resource distribution runs untouched.
  Removed the legacy hardcoded-position fallback path (no longer needed).
- Spawn handling (`Prefix_SpawnBaseStructures`) already had correct vanilla passthrough — no change needed.

### Configured maps
Only maps with a JSON config are affected by this mod:
- `NorthPolarCap` — custom resource amounts + spawn positions

All other maps run fully vanilla.

---

## v2.0.0 — Initial release

- Per-map JSON config system (`UserData/MapBalance/configs/{MapName}.json`)
- Custom resource amounts (Balterium, Biotics) per map
- Spawn variant selection (A/B swappable HQ positions)
- `removed_patches` to disable specific resource area indices
- Refinery placement scanning tools
- Web-based map balance planner (`web_tool/`)
