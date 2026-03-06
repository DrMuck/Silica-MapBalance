"""
GreatErg — Resource Layout Designer + Expansion Tree (v5.0)

Designs resource positions based on border-spawn expansion chain:
  - 16 spawn points along 4 edges (5 per edge, 1200m spacing, 600m from border)
  - 3 Balterium patches per spawn within BUILD_R (starter economy)
  - 16 center/expansion patches (contested zone, expansion incentive)
  - All 64 positions validated against refinery scan (ramp facing resource)
  - Outputs C# position array for the mod
"""

import numpy as np
import pandas as pd
import matplotlib.pyplot as plt
from matplotlib.patches import Circle, Rectangle
from matplotlib.colors import LinearSegmentedColormap
import matplotlib.patches as mpatches
from PIL import Image
import os, sys

MAP_IMAGE = r"E:\Steam\steamapps\common\Silica Dedicated Server\Mod MapReplay\GreatErg_extracted.png"
REFINERY_SCAN = r"E:\Steam\steamapps\common\Silica Dedicated Server\UserData\MapBalance\refinery_scan_GreatErg_20260227_153441.txt"
OUTPUT_DIR = r"C:\Users\schwe\Projects\Si_MapBalance\data"

WORLD_MIN, WORLD_MAX = -3000, 3000
BUILD_R = 600
HQ_CHAIN_R = 1500
SPAWN_EDGE = 2400
SPACING = 1200
REFINERY_REACH = 300
RAMP_ALIGN_THRESH = 0.5

EDGE_POS = [-2400, -1200, 0, 1200, 2400]

SIDES = {
    'south': {'spawns': [(x, -SPAWN_EDGE) for x in EDGE_POS], 'angle': np.pi/2},
    'north': {'spawns': [(x,  SPAWN_EDGE) for x in EDGE_POS], 'angle': -np.pi/2},
    'west':  {'spawns': [(-SPAWN_EDGE, z) for z in EDGE_POS], 'angle': 0},
    'east':  {'spawns': [( SPAWN_EDGE, z) for z in EDGE_POS], 'angle': np.pi},
}
SIDE_COLORS = {
    'south': '#FF4444', 'north': '#4488FF',
    'west': '#44BB44', 'east': '#FF8800',
}


# ── Refinery Scan ───────────────────────────────────────────────────────────
def load_refinery_scan(path):
    with open(path, 'r') as f:
        for i, line in enumerate(f):
            if line.startswith('x,z,rot,'):
                break
        else:
            sys.exit(f"ERROR: no CSV header in {path}")
    df = pd.read_csv(path, skiprows=i)
    print(f"  Loaded {len(df)} refinery placements")
    return df


def check_refinery(x, z, ref_df, reach=REFINERY_REACH):
    """Find closest valid refinery placement with ramp facing (x,z)."""
    rx = ref_df['x'].values
    rz = ref_df['z'].values
    mask = (rx >= x-reach) & (rx <= x+reach) & (rz >= z-reach) & (rz <= z+reach)
    if not mask.any():
        return 0, np.inf, None, None
    sx, sz_ = rx[mask], rz[mask]
    dx = (x - sx).astype(np.float64)
    dz = (z - sz_).astype(np.float64)
    dist = np.sqrt(dx*dx + dz*dz)
    dm = dist <= reach
    if not dm.any():
        return 0, np.inf, None, None
    idx = np.where(mask)[0][dm]
    sd, sdx, sdz = dist[dm], dx[dm], dz[dm]
    slen = np.maximum(np.sqrt(sdx**2 + sdz**2), 0.01)
    trdx, trdz = sdx/slen, sdz/slen

    # Check both ramps
    best_n = 0
    best_dist = np.inf
    best_ref_pos = None
    best_ramp_dir = None

    for ramp in ['A', 'B']:
        rok = ref_df[f'ramp{ramp}_ok'].values[idx]
        rdx = ref_df[f'ramp{ramp}_dirX'].values[idx]
        rdz = ref_df[f'ramp{ramp}_dirZ'].values[idx]
        align = rdx * trdx + rdz * trdz
        valid = (rok == 1) & (align > RAMP_ALIGN_THRESH)
        n = int(valid.sum())
        best_n += n
        if n > 0:
            vi = np.where(valid)[0]
            closest = vi[np.argmin(sd[valid])]
            d = sd[closest]
            if d < best_dist:
                best_dist = d
                ref_idx = idx[closest]
                best_ref_pos = (float(rx[ref_idx]), float(rz[ref_idx]))
                best_ramp_dir = (float(rdx[closest]), float(rdz[closest]))

    return best_n, best_dist, best_ref_pos, best_ramp_dir


# ── Layout Design ───────────────────────────────────────────────────────────
def design_layout():
    """Design all 64 resource positions: 48 starter + 16 center/expansion."""
    positions = []
    seen_spawns = set()  # dedup corners (shared by 2 edges)

    # --- STARTER: 3 patches per unique spawn (16 unique × 3 = 48) ---
    # Forward 350m, and two at 250m fwd + 250m sideways
    for side_name, side_info in SIDES.items():
        angle = side_info['angle']

        for sx, sz in side_info['spawns']:
            key = (sx, sz)
            if key in seen_spawns:
                continue
            seen_spawns.add(key)

            # Corners: use diagonal inward direction instead of edge direction
            is_corner = abs(sx) == SPAWN_EDGE and abs(sz) == SPAWN_EDGE
            if is_corner:
                a = np.arctan2(-sz, -sx)  # toward center
            else:
                a = angle
            idx, idz = np.cos(a), np.sin(a)
            px, pz = -idz, idx  # perpendicular left

            offsets = [
                (350 * idx,             350 * idz),                 # forward
                (250 * idx + 250 * px,  250 * idz + 250 * pz),     # left-fwd
                (250 * idx - 250 * px,  250 * idz - 250 * pz),     # right-fwd
            ]
            for i, (ox, oz) in enumerate(offsets):
                positions.append({
                    'x': round(sx + ox), 'z': round(sz + oz),
                    'zone': 'starter', 'side': side_name,
                    'spawn': (sx, sz), 'patch_idx': i
                })

    # --- CENTER / EXPANSION: 16 patches ---
    center = [
        # Inner contested (r~400m, cross) — 1st expansion reaches these
        (400, 0), (0, 400), (-400, 0), (0, -400),
        # Mid contested (r~850m, diagonal) — expansion frontier
        (600, 600), (-600, 600), (-600, -600), (600, -600),
        # Expansion incentive (r~1100m, cross) — deep expansion target
        (1100, 0), (0, 1100), (-1100, 0), (0, -1100),
        # Deep contested (r~1400m, diagonal) — between starter zones
        (1000, 1000), (-1000, 1000), (-1000, -1000), (1000, -1000),
    ]
    for x, z in center:
        positions.append({'x': x, 'z': z, 'zone': 'center'})

    print(f"  Designed {len(positions)} positions "
          f"({sum(1 for p in positions if p['zone']=='starter')} starter + "
          f"{sum(1 for p in positions if p['zone']=='center')} center)")
    return positions


def validate_layout(positions, ref_df):
    """Validate all positions against refinery scan."""
    ok, bad = 0, 0
    for p in positions:
        n, d, rpos, rdir = check_refinery(p['x'], p['z'], ref_df)
        p['ref_n'] = n
        p['ref_dist'] = d
        p['ref_pos'] = rpos
        p['ref_dir'] = rdir
        if n > 0:
            ok += 1
        else:
            bad += 1
    print(f"  Validated: {ok} OK, {bad} BAD")
    return positions


# ── Expansion Tree (for visualization) ──────────────────────────────────────
def gen_tree(sx, sz, fwd_angle, stages=2, dedup_dist=600):
    offsets = [(HQ_CHAIN_R * np.cos(fwd_angle + da),
                HQ_CHAIN_R * np.sin(fwd_angle + da))
               for da in [0, np.pi/3, -np.pi/3]]
    nodes = [(sx, sz, 0)]
    edges = []
    for stage in range(1, stages + 1):
        parents = [(i, n) for i, n in enumerate(nodes) if n[2] == stage - 1]
        for pi, (ppx, ppz, _) in parents:
            for odx, odz in offsets:
                cx, cz = ppx + odx, ppz + odz
                if abs(cx) > SPAWN_EDGE or abs(cz) > SPAWN_EDGE:
                    continue
                if any(np.hypot(cx - nx, cz - nz) < dedup_dist
                       for nx, nz, _ in nodes):
                    continue
                ci = len(nodes)
                nodes.append((cx, cz, stage))
                edges.append((pi, ci))
    return nodes, edges


# ── Drawing ─────────────────────────────────────────────────────────────────
STAGE_VIS = {
    0: dict(alpha=0.15, lw=2.0, ls='-',  ms=10),
    1: dict(alpha=0.06, lw=0.8, ls='--', ms=6),
    2: dict(alpha=0.03, lw=0.5, ls=':',  ms=4),
}

def _style(ax):
    ax.set_xlim(WORLD_MIN - 100, WORLD_MAX + 100)
    ax.set_ylim(WORLD_MIN - 100, WORLD_MAX + 100)
    ax.set_aspect('equal')
    ax.grid(True, alpha=0.08, color='white')
    ax.tick_params(colors='#888', labelsize=6)
    for s in ax.spines.values():
        s.set_edgecolor('#444')

def _bg(ax, img):
    ax.imshow(img, extent=[WORLD_MIN, WORLD_MAX, WORLD_MIN, WORLD_MAX],
              origin='upper', alpha=0.4)
    ax.add_patch(Rectangle((-SPAWN_EDGE, -SPAWN_EDGE), 2*SPAWN_EDGE, 2*SPAWN_EDGE,
                           fill=False, edgecolor='#FF4444', lw=0.7, ls=':', alpha=0.2))


def draw_expansion_and_resources(ax, img, positions, sides_to_draw, stages=1):
    """Draw expansion trees + resource positions."""
    _bg(ax, img)

    # Expansion trees (background)
    for side in sides_to_draw:
        info = SIDES[side]
        color = SIDE_COLORS[side]
        for sx, sz in info['spawns']:
            nodes, edges = gen_tree(sx, sz, info['angle'], stages)
            for nx, nz, s in nodes:
                v = STAGE_VIS.get(s, STAGE_VIS[2])
                ax.add_patch(Circle((nx, nz), BUILD_R, fill=True,
                    facecolor=color, alpha=v['alpha'],
                    edgecolor=color, lw=v['lw'], ls=v['ls'], zorder=2))
            for pi, ci in edges:
                ax.annotate('', xy=(nodes[ci][0], nodes[ci][1]),
                    xytext=(nodes[pi][0], nodes[pi][1]),
                    arrowprops=dict(arrowstyle='->', color=color,
                                    lw=1.0, alpha=0.25), zorder=4)
            for nx, nz, s in nodes:
                v = STAGE_VIS.get(s, STAGE_VIS[2])
                fc = color if s == 0 else 'none'
                ax.plot(nx, nz, 's', ms=v['ms'], color=color,
                    mfc=fc, mec=color, mew=1.5 if s==0 else 0.8,
                    zorder=7, alpha=0.7 if s==0 else 0.4)

    # Resource positions
    for p in positions:
        x, z = p['x'], p['z']
        ok = p.get('ref_n', -1) > 0
        if p['zone'] == 'starter':
            color = SIDE_COLORS.get(p.get('side', 'south'), '#888')
            sz = 7
        else:
            color = '#FFD700'
            sz = 9
        edge = '#00FF00' if ok else '#FF0000'
        ax.plot(x, z, 'D', ms=sz, color=color,
                mec=edge, mew=1.5, zorder=9, alpha=0.9)

        # Arrow to closest refinery position
        if p.get('ref_pos') and ok:
            rx, rz = p['ref_pos']
            ax.annotate('', xy=(rx, rz), xytext=(x, z),
                arrowprops=dict(arrowstyle='->', color='#00FF00',
                                lw=0.8, alpha=0.4), zorder=5)


def draw_detail_panel(ax, img, positions, side, spawn_idx=2):
    """Detail view of one spawn's resources with refinery arrows."""
    info = SIDES[side]
    sx, sz = info['spawns'][spawn_idx]
    _bg(ax, img)

    # Draw this spawn's build radius
    color = SIDE_COLORS[side]
    ax.add_patch(Circle((sx, sz), BUILD_R, fill=True,
        facecolor=color, alpha=0.15, edgecolor=color, lw=2.5, zorder=2))
    ax.plot(sx, sz, 's', ms=14, color=color, mec='white', mew=2.5, zorder=10)

    # Draw this spawn's patches with detail
    spawn_patches = [p for p in positions
                     if p['zone'] == 'starter' and p.get('spawn') == (sx, sz)]
    for p in spawn_patches:
        x, z = p['x'], p['z']
        ok = p.get('ref_n', -1) > 0
        ax.plot(x, z, 'D', ms=12, color='#FF8800',
                mec='#00FF00' if ok else '#FF0000', mew=2.0, zorder=9)
        d = np.hypot(x - sx, z - sz)
        ax.text(x + 60, z + 60, f'{d:.0f}m\n{p.get("ref_n",0)} ref\n{p.get("ref_dist",999):.0f}m',
                fontsize=7, color='white', zorder=12,
                bbox=dict(facecolor='black', alpha=0.7, boxstyle='round,pad=0.2'))

        # Refinery position + ramp direction
        if p.get('ref_pos') and ok:
            rx, rz = p['ref_pos']
            ax.plot(rx, rz, 'v', ms=8, color='#00FF00', mec='white', mew=1.0, zorder=9)
            if p.get('ref_dir'):
                rdx, rdz = p['ref_dir']
                ax.annotate('', xy=(rx + rdx*80, rz + rdz*80), xytext=(rx, rz),
                    arrowprops=dict(arrowstyle='->', color='#00FF00', lw=1.5), zorder=8)

    # Zoom to spawn area
    margin = 800
    ax.set_xlim(sx - margin, sx + margin)
    ax.set_ylim(sz - margin, sz + margin)
    ax.set_aspect('equal')
    ax.set_title(f'Detail: {side.capitalize()} spawn ({sx},{sz})',
                 fontsize=11, fontweight='bold', color=color, pad=8)
    ax.tick_params(colors='#888', labelsize=7)
    for s in ax.spines.values():
        s.set_edgecolor('#444')


# ── C# Output ──────────────────────────────────────────────────────────────
def output_csharp(positions):
    """Generate C# array for the mod."""
    lines = ['        // Auto-generated resource positions (v5.0)',
             f'        // {len(positions)} positions: '
             f'{sum(1 for p in positions if p["zone"]=="starter")} starter + '
             f'{sum(1 for p in positions if p["zone"]=="center")} center',
             '        private static readonly float[][] DesignedPositions = new float[][] {']

    for i, p in enumerate(positions):
        zone = p['zone']
        extra = f' // {zone}'
        if zone == 'starter':
            extra = f' // {p["side"]} starter ({p["spawn"][0]},{p["spawn"][1]}) #{p["patch_idx"]}'
        lines.append(f'            new float[] {{ {p["x"]:7.0f}f, {p["z"]:7.0f}f }},{extra}')

    lines.append('        };')

    out = os.path.join(OUTPUT_DIR, 'designed_positions_v5.cs')
    with open(out, 'w') as f:
        f.write('\n'.join(lines))
    print(f"  C# output: {out}")
    return out


# ── Main ────────────────────────────────────────────────────────────────────
def main():
    print("=== GreatErg Layout Designer v5.0 ===\n")

    # Design
    print("Designing layout...")
    positions = design_layout()

    # Validate
    print("\nLoading refinery scan...")
    ref_df = load_refinery_scan(REFINERY_SCAN)
    print("\nValidating positions...")
    validate_layout(positions, ref_df)

    # Print summary
    starters = [p for p in positions if p['zone'] == 'starter']
    centers = [p for p in positions if p['zone'] == 'center']
    print(f"\n--- Summary ---")
    for side in SIDES:
        sp = [p for p in starters if p.get('side') == side]
        ok = sum(1 for p in sp if p['ref_n'] > 0)
        min_d = min((p['ref_dist'] for p in sp if p['ref_n'] > 0), default=999)
        print(f"  {side:6s}: {len(sp)} patches, {ok} valid, "
              f"closest refinery: {min_d:.0f}m")
    ok_c = sum(1 for p in centers if p['ref_n'] > 0)
    print(f"  center: {len(centers)} patches, {ok_c} valid")

    # C# output
    print("\nGenerating C# output...")
    output_csharp(positions)

    # Visualization
    print("\nRendering...")
    img = Image.open(MAP_IMAGE)
    fig, axes = plt.subplots(2, 2, figsize=(24, 24), dpi=130)
    fig.patch.set_facecolor('#111111')

    # Panel 1: All sides, 1 expansion stage + all resources
    ax = axes[0, 0]
    draw_expansion_and_resources(ax, img, positions, SIDES.keys(), stages=1)
    ax.set_title('All Resources + 1st Expansion Trees',
                 fontsize=12, fontweight='bold', color='white', pad=8)
    _style(ax)

    # Panel 2: All sides, 2 stages + resources
    ax = axes[0, 1]
    draw_expansion_and_resources(ax, img, positions, SIDES.keys(), stages=2)
    ax.set_title('All Resources + 2 Expansion Stages',
                 fontsize=12, fontweight='bold', color='white', pad=8)
    _style(ax)

    # Panel 3: Detail view — south center spawn
    draw_detail_panel(axes[1, 0], img, positions, 'south', spawn_idx=2)

    # Panel 4: Detail view — corner spawn
    draw_detail_panel(axes[1, 1], img, positions, 'south', spawn_idx=0)

    # Legend
    legend_els = [
        *[plt.Line2D([0],[0], marker='s', color='w', mfc=c, ms=10,
                     label=f'{s.capitalize()} HQ', ls='')
          for s, c in SIDE_COLORS.items()],
        plt.Line2D([0],[0], marker='D', color='w', mfc='#FF8800',
                   mec='#00FF00', ms=9, label='Starter patch (valid)', ls=''),
        plt.Line2D([0],[0], marker='D', color='w', mfc='#FFD700',
                   mec='#00FF00', ms=9, label='Center patch (valid)', ls=''),
        plt.Line2D([0],[0], marker='D', color='w', mfc='#888',
                   mec='#FF0000', ms=9, label='Invalid (no refinery)', ls=''),
        plt.Line2D([0],[0], marker='v', color='w', mfc='#00FF00',
                   ms=8, label='Closest refinery', ls=''),
    ]
    fig.legend(handles=legend_els, loc='lower center', ncol=5,
               fontsize=9, facecolor='#1a1a1a', edgecolor='gray',
               labelcolor='white', framealpha=0.9, bbox_to_anchor=(0.5, 0.003))

    fig.suptitle('GreatErg \u2014 Designed Resource Layout (v5.0)\n'
                 f'48 starter (3/spawn) + 16 center | '
                 f'Build: {BUILD_R}m | Chain: {HQ_CHAIN_R}m',
                 fontsize=14, fontweight='bold', color='white', y=0.99)
    plt.tight_layout(rect=[0, 0.04, 1, 0.96])
    out = os.path.join(OUTPUT_DIR, 'greaterg_layout_v5.0.png')
    fig.savefig(out, dpi=130, bbox_inches='tight', facecolor=fig.get_facecolor())
    print(f"Saved: {out}")
    plt.close(fig)


if __name__ == '__main__':
    main()
