# Si_MapBalance — Change Log

## v3.6.0 — 2026-11-12

### Feature: base site test (scan, layout check, runtime warning) and the layout field `"rot"`
- **Why**: players saw starter units spawn inside map geometry when a layout put the HQ next to a
  cliff (Citadel) or the Nest between skyscrapers (IndustrialQuarter). The layouts come from the web
  tool, whose HQ masks were terrain-only (`!mapdump` hq_scan): no rocks, cliff meshes, buildings,
  navmesh, and nothing for the Nest.
- **Site test** (`SiteTest.cs`, shared by everything below): for Sol HQ, Centauri HQ and Alien Nest
  (the team's `BaseStructure`, sized from its prefab's solid colliders) at x/z and a yaw:
  on the map (FogOfWar bounds); ground = the structure's own `ConstructionPreview.GetGroundSlopeClear`
  (called, and replicated to measure how far the foundation sticks out; valid up to `GroundTol` 8 m);
  no static collider (rocks, cliffs, buildings, props, invisible walls; not terrain, not game objects,
  not the palms' `AIVehicleObstacle` layer) penetrating the collision boxes deeper than `OverlapDepth`
  3 m (`Physics.ComputePenetration`); at least 2 of 8 probes around the
  footprint on the main connected component of the site graph (`Small Vehicle` for HQs, `Voxel Graph`
  for the Nest; the main component is tracked by a reference node because A* renumbers
  `GraphNode.Area` after graph updates). Result = one code byte (valid bit, reason 0-7, four test
  flags). Thresholds calibrated on all 578 vanilla start-point placements of the 13 maps: 94.9 % of
  the HQ and 95.9 % of the Nest placements pass (see Si_MapBalanceTool `docs/SITE_SCAN.md` §9).
  Standing on a non-terrain surface (Citadel rock plateaus, IndustrialQuarter platforms) is logged,
  not failed: those are playable meshes; roofs and pillar tops fail the navmesh test instead.
  For the Nest the ground rule is recorded but not a failure (11 of 266 vanilla Nest placements break
  it: tilt from a 1 m ray triangle on rock, ray misses, gaps of 8-35 m; vanilla spawns them anyway).
  Yaws within 0.05 deg of a multiple of 90 are snapped and the quaternion kept at w >= 0 (a start point
  yaw of 359.99 gave 0 m penetration where yaw 0 gave 3.6 m on Citadel).
- **Runtime warning**: when a round applies a layout, every configured base is tested before it is
  spawned. Bad base: one Warning `[SITE] BAD BASE map=… layout=… team=… pos=(x,z) rot=… (source)
  reason=… blocker='<collider>' [layer] (depth) …`; otherwise `[SITE] Base site check <map> <layout>:
  all N bases clear`. Nothing is moved.
- **`"rot"`** (optional, per spawn entry, degrees; the tool writes 0/90/180/270): the base at that
  entry spawns with exactly this yaw. Absent = as before (the nearest vanilla start point's rotation).
  The yaw belongs to the position, so after the 50 % Sol/Cent swap the other faction's HQ uses it: in
  swappable layouts it has to suit both HQs. The relocated start point keeps its own rotation (not
  rotated, so no leak into later rounds on the same scene). Older builds ignore the field.
- **Site scan mode** (`SiteScan.cs`, `UserData/cfg/MapBalance/site_scan.json` `"enabled": true`): on
  each map load it waits for the navmesh, scans, loads the next listed map (`NetworkGameServer.LoadLevel`
  with the running game mode) and resumes from `state.json` after a restart. Output in
  `UserData/MapBalance/site_scan/`: grid `site_scan_<Map>_<ts>.txt` (15 m hq_scan grid, 3 structures
  x 4 yaws + open-ground bits for a vehicle and an alien probe), `site_startpoints_*.csv`,
  `site_layouts_*.csv` (every base of every layout in `layouts_dir`), optional Si_StarterUnits dry run
  (`starter_audit`, off by default; test points in `cfg/StarterUnits/audit/<Map>/`, removed after).
  Yields every `frame_budget_ms`. Needed once per map and game version: layout checks afterwards are
  offline (Si_MapBalanceTool `tools/site_audit.py` on `data/sites/`).
- Build: needs `AstarPathfindingProject.dll`, `PackageTools.dll`, `Drawing.dll` in
  `include/netstandard2.1/` (same files in 0.9.47 and 1.0.x). refcheck clean against both.
- Small edits in `Si_MapBalance.cs` only: `SpawnPos.Rot` + `ReadRot`, two lines in `GetFactionSpawn`,
  `SiteCheckBegin/End` around the spawn loop, `yawOverride`/`layoutEntry` parameters and the
  `SiteCheckBase` call in `SpawnAtBestStartPoint`, version strings.

## v3.5.0 — 2026-11-12

### Feature: pin a layout per map (`!layout`)
- Admins can force one layout JSON per map instead of the random pick (show matches,
  trying a specific JSON). Pins are per map, sticky until removed, and stored with a master
  switch in `UserData/cfg/MapBalance/layout_pins.json` (`{"pinning": true, "pins": {"<map>": "<file>.json"}}`),
  re-read when edited by hand. `pinning off` keeps the pins but ignores them.
- Admin command (AdminMod, power `Map`): `!layout` / `!layout status`, `!layout list [map]`,
  `!layout pin [map] <name|#>`, `!layout unpin [map|all]`, `!layout pinning on|off`. Names match
  case-insensitively with or without `.json`: exact name, then list number, then a unique
  substring; ambiguous or unknown -> the candidates are listed, nothing changes. Replies say a
  pin takes effect at the next round start (one pick per round).
- Pick: still exactly one pick per round (`PickLayout` -> `LoadMapConfig`); the pin is checked
  first there. Used only if the file exists, parses and supports the round's mode by the same
  rules as the random pool (mode flag or mode-agnostic; never a 2v1 layout in a normal HvHvA
  round; in a 2v1 round a `"2v1"` layout, or a plain `hvhva` one when the map has no 2v1
  layout). Otherwise: Warning once per round + normal random pick, never a broken round.
  The KoH preference does not apply to a pin; `swap_spawns` behaves as before.
- Logs: `Layout pin for <map>: <file> [...] fits mode X — used instead of the random pick`,
  `Layout pick: <name> (PINNED) (mode X, by Y)`, `Layout pin for <map> (<file>) NOT used: <reason>`.
  Allchat: `Map Balance: <name> [PINNED]` or `... [pinned layout skipped: <reason>]`.
  `map_layout` game-log line unchanged.
- Si_ModUI export `ModUIData()` (admin only): status lines, pinning ON/OFF switch, "Pin layout
  (this map)" picker with mode tags, "Unpin this map", "Clear all pins", list and status
  reports. `ModUI_Export.cs` copied verbatim from Si_ModUI. `!layout` is registered with a
  real static handler (not a DynamicMethod shim) so Si_ModUI attributes it to this assembly.
- Code: pure logic (matching, pin store, mode check) in `LayoutPins.cs` (no Unity), command +
  export in `LayoutPinCommands.cs`; `MapBalance` is now `partial`.

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
