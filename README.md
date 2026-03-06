# Si_MapBalance

A server-side [Silica](https://store.steampowered.com/app/504900/Silica/) mod that overrides map spawn positions and resource distribution using layout configurations created with the [Map Balance Tool](https://github.com/DrMuck/Silica-MapBalanceTool).

Maps without a configuration run fully vanilla — no changes to spawns or resources.

## Features

- **Custom spawn positions** for Sol, Centauri, and Alien per map
- **Resource control**: per-type amounts (Balterium, Biotics), per-patch overrides, patch removal
- **Game mode filtering**: layouts are tagged as HvH, HvA, or HvHvA — the mod auto-detects the active game mode and only picks from matching configs
- **Multiple layouts per map**: place several JSON files in a map folder and the mod picks one randomly each round
- **Sol/Cent swap**: in HvH mode, spawn positions are randomly swapped (50% chance) for extra variation
- **Allchat announcement**: the selected layout name is shown to all players at game start
- **Per-faction settings**: build radius and chain range per faction

## How It Works

### 1. Create layouts with the Map Balance Tool

Use the interactive [Map Balance Tool](https://github.com/DrMuck/Silica-MapBalanceTool) to plan spawn positions and resource distribution for each map. The tool outputs JSON files like:

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

Copy the JSON files into the server's `UserData/Spawns/` directory, organized by map name:

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
1. The mod detects the **game mode** (HvH / HvA / HvHvA) from active teams
2. It scans `UserData/Spawns/{MapName}/` for JSON files matching that game mode
3. One layout is **picked randomly**
4. In HvH mode, Sol and Cent positions may be **swapped** (50% chance)
5. HQ spawn positions and resource areas are overridden accordingly
6. The selected layout name is **announced in allchat**

## Installation

1. Requires [MelonLoader](https://github.com/LavaGang/MelonLoader) on the dedicated server
2. Copy `Si_MapBalance.dll` to the server's `Mods/` folder
3. Place layout JSONs in `UserData/Spawns/{MapName}/`
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
  }
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
| `game_modes` | Which game modes this layout is valid for |

## Build

```bash
cd Si_MapBalance
dotnet build -c Release
```

Output: `bin/Release/netstandard2.1/Si_MapBalance.dll`

Requires game DLLs in `include/netstandard2.1/` (not included — copy from `Silica_Data/Managed/`).
