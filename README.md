# Si_MapBalance

A server-side [Silica](https://store.steampowered.com/app/504900/Silica/) mod that overrides map spawn positions and resource distribution using layout configurations.

> 🛠️ **Author layouts in the browser** with the interactive **[Map Balance Tool](https://drmuck.github.io/Silica-MapBalanceTool/)** — no install required, runs entirely client-side. Pick spawn positions on the map image, place resource patches, tag layouts by game mode, then export ready-to-deploy JSONs straight into `UserData/cfg/Spawns/`. Tool source: [github.com/DrMuck/Silica-MapBalanceTool](https://github.com/DrMuck/Silica-MapBalanceTool).

![Map Balance Tool — Badlands with placed HQs and build-radius rings](docs/screenshots/map_balance_tool_badlands.jpg)

*Screenshot from the [interactive tool](https://drmuck.github.io/Silica-MapBalanceTool/): Badlands map with Sol (blue), Centauri (red), and Alien (green) bases placed; dashed rings show each faction's **build radius**. The build radius value can be tuned per-faction in [Si_UnitBalance](https://github.com/DrMuck/Silica-UnitBalance) (via the `build_radius` parameter under each HQ entry in `Si_UnitBalance_Config.json`, or live through the `!b` editor).*

Maps without a configuration run fully vanilla — no changes to spawns or resources.

## Features

- **Custom spawn positions** for Sol, Centauri, and Alien per map
- **Resource control**: per-type amounts (Balterium, Biotics), per-patch overrides, patch removal
- **Game mode filtering**: layouts are tagged as HvH, HvA, or HvHvA — the mod auto-detects the active game mode and only picks from matching configs
- **Multiple layouts per map**: place several JSON files in a map folder and the mod picks one randomly each round
- **Sol/Cent swap**: in HvH and HvHvA, spawn positions are randomly swapped (50% chance) for extra variation; a layout can turn it off with `"swap_spawns": false`
- **2v1 layouts** (3.4.0): when [Si_VersusVote](#2v1-rounds-si_versusvote) plays a voted HvHvA as a 2v1 (Sol + Centauri allied against the Aliens), only layouts flagged `"2v1": true` in `game_modes` are used; normal HvHvA rounds never use them
- **Allchat announcement**: the selected layout name is shown to all players at game start
- **Per-faction settings**: build radius and chain range per faction

## How It Works

### 1. Create layouts with the Map Balance Tool

Open **[Map Balance Tool](https://drmuck.github.io/Silica-MapBalanceTool/)** in your browser. It's a single-page web app — no install, no account, all editing happens locally — that lets you:

- Pick a Silica map background image
- Click-place HQ / Nest spawn positions for each faction
- Define resource patches with per-type amounts (Balterium / Biotics)
- Remove specific in-map resource patches
- Tag the layout with game-mode flags (`hvh`, `hva`, `hvhva`, optionally `4way`, or `2v1` for allied-humans-vs-Aliens rounds)
- Set per-faction settings (build radius, chain range, etc.)
- Export ready-to-deploy JSON files

Tool source: [github.com/DrMuck/Silica-MapBalanceTool](https://github.com/DrMuck/Silica-MapBalanceTool).

Output filenames the tool typically produces:

```
layout_Badlands_HvH.json
layout_Badlands 3way.json
layout_CrimsonPeak_3way_north.json
```

Each JSON contains:
- **Spawn positions** for each faction
- **Resource amounts** (uniform per type + per-patch overrides)
- **Removed patches** (resource areas to disable)
- **Game mode flags** (`hvh`, `hva`, `hvhva`)
- **Per-faction settings** (build radius, chain range)

### 2. Deploy to the server

Copy the JSON files into the server's `UserData/cfg/Spawns/` directory, organized by map name:

```
Silica Dedicated Server/
  UserData/
    Spawns/
      Badlands/
        layout_Badlands_HvH.json
        layout_Badlands 3way.json
        layout_Badlands HvA_Middle_bottom_Top.json
      CrimsonPeak/
        layout_CrimsonPeak_3way_north.json
        layout_CrimsonPeak_HvH_diagonal_1.json
      ...
```

The folder name must match the map name exactly (e.g., `Badlands`, `CrimsonPeak`, `NorthPolarCap`).

### 3. The mod handles the rest

When a round starts:
1. The mod detects the **game mode** (HvH / HvA / HvHvA) from active teams, and asks Si_VersusVote (if loaded) whether this HvHvA round is a **2v1**
2. It scans `UserData/cfg/Spawns/{MapName}/` for JSON files matching that game mode (2v1 round: only `"2v1": true` layouts; normal HvHvA round: `"2v1"` layouts excluded)
3. One layout is **picked randomly**
4. In HvH and HvHvA, Sol and Cent positions may be **swapped** (50% chance), unless the layout sets `"swap_spawns": false`
5. HQ spawn positions and resource areas are overridden accordingly
6. The selected layout name is **announced in allchat** (with `[2v1: Sol + Centauri vs Aliens]` in a 2v1 round)

## Installation

1. Requires [MelonLoader](https://github.com/LavaGang/MelonLoader) on the dedicated server
2. Copy `Si_MapBalance.dll` to the server's `Mods/` folder
3. Place layout JSONs in `UserData/cfg/Spawns/{MapName}/`
4. Restart the server

## JSON Format

Layout files exported from the Map Balance Tool look like this:

```json
{
  "map": "Badlands",
  "spawns": {
    "Sol": [{ "x": -2160, "z": -120, "isSpawn": true, "isBiocache": false }],
    "Cent": [{ "x": 2250, "z": 2160, "isSpawn": true, "isBiocache": false }],
    "Alien": [{ "x": -675, "z": -2475, "isSpawn": true, "isBiocache": false }]
  },
  "resources": {
    "balterium_amount": 40000,
    "biotics_amount": 50000,
    "removed_patches": [7, 13, 19],
    "patch_overrides": { "2": 60000, "10": 60000 }
  },
  "settings": {
    "solBuildRadius": 620,
    "centBuildRadius": 600,
    "solChainRange": 1520,
    "centChainRange": 1500
  },
  "game_modes": {
    "hvh": true,
    "hva": false,
    "hvhva": false
  },
  "swap_spawns": true
}
```

| Field | Description |
|-------|-------------|
| `spawns` | Faction HQ positions (world X/Z coordinates) |
| `resources.balterium_amount` | Uniform Balterium amount per patch |
| `resources.biotics_amount` | Uniform Biotics amount per patch |
| `resources.removed_patches` | Patch indices to disable (no resources) |
| `resources.patch_overrides` | Per-patch amount overrides (key = patch index) |
| `settings.*BuildRadius` | Build radius per faction |
| `settings.*ChainRange` | Chain range per faction |
| `game_modes` | Which game modes this layout is valid for (`hvh`, `hva`, `hvhva`, `4way`) |
| `game_modes."2v1"` | `true` = 2v1 layout (3.4.0): used only in a Si_VersusVote 2v1 round, never in a normal HvHvA round; absent or `false` = normal layout |
| `swap_spawns` | `false` = never swap Sol and Cent positions for this layout; absent or `true` = 50 % swap in HvH / HvHvA |

## 2v1 rounds (Si_VersusVote)

Si_VersusVote can play a voted HvHvA on some maps (e.g. IndustrialQuarter) as a **2v1**: the vanilla 3-team setup, but Sol and Centauri are allied for the whole round against the Aliens. A symmetric 3-way triangle is the wrong layout for that, so such rounds get their own layouts: both human HQs on one side as a bloc, the Nest opposite, resources balanced for two allied humans against one alien team.

**Flag a layout** in the Map Balance Tool with the Game Modes checkbox *2v1 (allied humans vs Aliens)*, or by hand:

```json
"game_modes": { "hvh": false, "hva": false, "hvhva": false, "4way": false, "2v1": true }
```

**Selection rule**

| Round | Candidate layouts |
|-------|-------------------|
| 2v1 (Si_VersusVote `TwoVsOneActive`, 3 active teams) | layouts with `"2v1": true` (their `hvhva` value does not matter). None on the map: Warning once per map, then the normal HvHvA pool |
| normal HvHvA | layouts with `"hvhva": true` **and not** `"2v1": true` |
| HvH / HvA / 4way | unchanged (their own flag) |

Log lines (MelonLoader, `[Map_Balance]`): `2v1 round on IndustrialQuarter: 3 candidate layout(s) [a, b, c] — picked b`, `Layout pick: b (mode hvhva/2v1, by prefix)`; in a normal HvHvA round `..., 1 2v1 layout(s) excluded (not a 2v1 round)`. The allchat announcement ends with `[2v1: Sol + Centauri vs Aliens]` (or `[2v1 round, no 2v1 layout: 3-way layout]` on the fallback). The `map_layout` game-log line is unchanged.

**How the 2v1 state is read.** No compile-time reference: `VersusVoteBridge` looks up `Si_VersusVote.VersusApi` by reflection (`ApiVersion >= 1`, static `TwoVsOneActive`). Without Si_VersusVote the mod behaves exactly as 3.3.2 (2v1 layouts are then never picked; normal HvHvA rounds still exclude them).

**Timing.** Si_VersusVote sets the flag in its `HarmonyPriority.First` prefix on `MP_Strategy.SetTeamVersusMode` (cleared on `MP_Strategy.Restart`). Si_MapBalance never picks before that call has returned: the pick is made in the `ResourceArea.DistributeAllResources` prefix, which vanilla calls right after `SetTeamVersusMode` at the vote end (`MP_Strategy.cs` 1.0.15 ~2590), or, if the game skips that call (headless path), in the `MP_Strategy.SpawnBaseStructures` prefix, which runs later on the first team join (`QueueCoopSpawnBaseStructures`) and is the method that places the HQs/Nest. `SpawnBaseStructures` re-checks the flag; if it changed after the first pick (HQs are still unplaced at that point), it re-picks and re-applies the resources, as for a mode mismatch.

**Older mod builds** ignore the `"2v1"` key and use the other `game_modes` flags, which is why the tool unticks HvH / HvA / HvHvA / 4-Way when the 2v1 box is ticked.

## Build

```bash
cd Si_MapBalance
dotnet build -c Release
```

Output: `bin/Release/netstandard2.1/Si_MapBalance.dll`

Requires game DLLs in `include/netstandard2.1/` (not included — copy from `Silica_Data/Managed/`).
