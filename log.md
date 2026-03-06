# Si_MapBalance — Change Log

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
