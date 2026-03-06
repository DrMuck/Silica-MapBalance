"""
GreatErg — HvH Spawn Analysis (v5.0)

Uses REAL Balterium positions. Can only enable/disable areas + change richness.
Spawn HQs placed at natural cluster positions (>=3 patches within BUILD_R).

Game mechanics:
  - HQ build radius: ~500m (realistic refinery placement range)
  - HQ chain distance: 1400m (realistic max HQ-to-HQ)
  - Spawn HQ: placed at natural Balterium cluster center (>=3 patches in BUILD_R)
  - Commander only expands toward a spot with >=3 Balterium within BUILD_R

Tier model (based on expansion tree, not radial distance):
  - T0: starter patches within BUILD_R of spawn HQ (must be >=3)
  - T1: 1 chain HQ (stays on own side of map)
  - T2: 2-3 chain HQs (stays on own side — T2 NEVER crosses midline)
  - T3: from T2 positions, crosses into enemy side (interception zone)

Expansion tree rules:
  - Only expand where >=3 NEW patches fall within BUILD_R of candidate HQ
  - T0-T2 (chains 0-3): candidate must be on own side of the midline
  - T3 (chain 4): may cross midline — this is where interception happens
"""

import numpy as np
import pandas as pd
import matplotlib.pyplot as plt
from matplotlib.patches import Circle
from matplotlib.lines import Line2D
from PIL import Image
from itertools import combinations
import os, sys

MAP_IMAGE = r"E:\Steam\steamapps\common\Silica Dedicated Server\Mod MapReplay\GreatErg_extracted.png"
REFINERY_SCAN = r"E:\Steam\steamapps\common\Silica Dedicated Server\UserData\MapBalance\refinery_scan_GreatErg_20260227_153441.txt"
OUTPUT_DIR = r"C:\Users\schwe\Projects\Si_MapBalance\data"

WORLD_MIN, WORLD_MAX = -3000, 3000
BUILD_R = 500        # realistic refinery placement range from HQ
HQ_CHAIN_R = 1400    # realistic max HQ-to-HQ distance
SPAWN_DIST = 1500    # medium spawn distance from center
MIN_PATCHES_FOR_EXPANSION = 3  # commander won't expand for fewer

# ── All 64 vanilla Balterium positions ───────────────────────────────────────
# (idx, x, z, resources, default_active)
BALTERIUM = np.array([
    [ 0, 2338.2, 2557.4, 35600, 0],
    [ 1, 2679.4, 2496.6, 39200, 0],
    [ 2,-1594.0,  721.1, 40800, 1],
    [ 3,-2499.6,-2651.2, 22400, 0],
    [ 4, 1826.5, 2156.9, 36800, 1],
    [ 5, -640.9, 2541.9, 40400, 0],
    [ 6, -363.6, -262.8, 41200, 0],
    [ 7,-1827.4, 1103.0, 40800, 1],
    [ 8,  591.2, -241.2, 38000, 0],
    [ 9,  708.6,-1481.2, 34400, 1],
    [10,  386.3, 1025.2, 69600, 1],
    [11, -137.7, -614.4, 50800, 1],
    [12, -244.7,-1833.8, 32400, 1],
    [13, 1358.2,  830.2, 35200, 0],
    [14,  944.9, 1575.4, 35200, 1],
    [15, 1404.0, -293.0, 42800, 1],
    [16,-1854.1, 2618.1, 46400, 1],
    [17, 1974.1,-1057.1, 42400, 1],
    [18,-1835.3,-1880.5, 35200, 0],
    [19,  379.8, 2635.6, 35200, 1],
    [20, 2325.8, 1746.3, 37600, 1],
    [21, 2597.1, 1155.6, 36800, 0],
    [22,  914.8, -799.8, 40000, 1],
    [23, 1052.2,  452.8, 48800, 1],
    [24, 1329.5, -936.6, 33600, 1],
    [25, 2136.2,-1927.8, 32000, 1],
    [26, 2632.4,-2500.8, 46400, 1],
    [27,  308.9,  218.5, 34000, 0],
    [28, 1183.3, 1135.2, 37600, 0],
    [29,-1271.2, 2221.2, 40000, 1],
    [30,-2310.9, 1684.1, 46400, 1],
    [31, 2344.9, -332.6, 40400, 1],
    [32, 1431.4, 2711.1, 32400, 1],
    [33,  308.6,-2081.2, 39600, 0],
    [34, -862.2,-1649.4, 37200, 1],
    [35, -986.0,  495.1, 46000, 0],
    [36,-1577.7,-2443.2, 31600, 0],
    [37,-2214.5, -861.1, 40800, 1],
    [38, -922.5, -833.4, 46800, 1],
    [39,   13.4, 1058.9, 37600, 1],
    [40, 1369.3,-1548.2, 54400, 1],
    [41, 2023.1,-1383.1, 50000, 1],
    [42, -744.2, 1774.5, 36400, 1],
    [43,-1863.8,   37.2, 50000, 1],
    [44, -324.1,-1039.2, 56800, 1],
    [45,  -20.9,-1212.5, 44800, 1],
    [46,-2284.0, -189.2, 42400, 1],
    [47, -366.2,  709.4, 42400, 1],
    [48, 1673.6,-2113.7, 41600, 0],
    [49,-1221.9,-1460.4, 53600, 0],
    [50, -529.9,  251.3, 41600, 1],
    [51,  863.3, 2241.2, 35600, 1],
    [52,-2625.8, -499.6, 40800, 1],
    [53,-2439.7,  512.9, 55200, 1],
    [54,-2502.5,-2137.8, 27600, 1],
    [55,-2705.5, 2167.9, 47600, 1],
    [56, -641.9, -468.6, 43200, 1],
    [57, -944.8, 1140.2, 44800, 1],
    [58, 1900.5,-2674.1, 40000, 1],
    [59, 1692.2,-1282.7, 32800, 0],
    [60, 1459.0, 1773.7, 35200, 0],
    [61,-1667.6, -607.1, 42000, 1],
    [62, 1165.3,  134.9, 41200, 1],
    [63,-1659.2, 1766.8, 46800, 0],
])


# ── Refinery Scan ────────────────────────────────────────────────────────────

def load_refinery_scan(path):
    with open(path, 'r') as f:
        for i, line in enumerate(f):
            if line.startswith('x,z,rot,'):
                break
        else:
            sys.exit(f"ERROR: no CSV header in {path}")
    df = pd.read_csv(path, skiprows=i)
    print(f"  Loaded {len(df):,} refinery placements")
    return df


def check_refinery_access(ref_df, reach=350, align_thresh=0.3):
    rx = ref_df['x'].values.astype(np.float64)
    rz = ref_df['z'].values.astype(np.float64)
    ra_ok = ref_df['rampA_ok'].values
    ra_dx = ref_df['rampA_dirX'].values.astype(np.float64)
    ra_dz = ref_df['rampA_dirZ'].values.astype(np.float64)
    rb_ok = ref_df['rampB_ok'].values
    rb_dx = ref_df['rampB_dirX'].values.astype(np.float64)
    rb_dz = ref_df['rampB_dirZ'].values.astype(np.float64)

    results = {}
    for row in BALTERIUM:
        idx = int(row[0])
        bx, bz = row[1], row[2]
        mask = ((rx >= bx - reach) & (rx <= bx + reach) &
                (rz >= bz - reach) & (rz <= bz + reach))
        if not mask.any():
            results[idx] = {'n_valid': 0, 'accessible': False,
                            'best_pos': None, 'best_ramp_dir': None}
            continue
        sx, sz = rx[mask], rz[mask]
        dx, dz = bx - sx, bz - sz
        dist = np.sqrt(dx*dx + dz*dz)
        near = dist <= reach
        if not near.any():
            results[idx] = {'n_valid': 0, 'accessible': False,
                            'best_pos': None, 'best_ramp_dir': None}
            continue
        ni = np.where(mask)[0][near]
        nd = dist[near]
        ndx, ndz = dx[near], dz[near]
        nlen = np.maximum(np.sqrt(ndx**2 + ndz**2), 0.01)
        trx, trz = ndx/nlen, ndz/nlen

        best_n, best_d, best_pos, best_rd = 0, np.inf, None, None
        for rok, rdx, rdz in [(ra_ok, ra_dx, ra_dz), (rb_ok, rb_dx, rb_dz)]:
            ok = rok[ni]
            rx2, rz2 = rdx[ni], rdz[ni]
            align = rx2*trx + rz2*trz
            valid = (ok == 1) & (align > align_thresh)
            n = int(valid.sum())
            best_n += n
            if n > 0:
                vi = np.where(valid)[0]
                c = vi[np.argmin(nd[valid])]
                if nd[c] < best_d:
                    best_d = nd[c]
                    gi = ni[c]
                    best_pos = (float(rx[gi]), float(rz[gi]))
                    best_rd = (float(rx2[c]), float(rz2[c]))

        results[idx] = {'n_valid': best_n, 'accessible': best_n > 0,
                         'best_pos': best_pos, 'best_ramp_dir': best_rd}

    n_ok = sum(1 for v in results.values() if v['accessible'])
    print(f"  Refinery access: {n_ok}/64 accessible")
    return results


# ── Expansion Tree (realistic) ───────────────────────────────────────────────

def _patches_in_radius(cx, cz, resources, build_r=BUILD_R):
    """Count how many resource positions fall within build_r of (cx,cz)."""
    count = 0
    for rx, rz in resources:
        if np.hypot(rx - cx, rz - cz) <= build_r:
            count += 1
    return count


def _new_patches_in_radius(cx, cz, resources, already_captured, build_r=BUILD_R):
    """Which resource indices are newly captured by placing HQ at (cx,cz)."""
    new = set()
    for i, (rx, rz) in enumerate(resources):
        if i not in already_captured and np.hypot(rx - cx, rz - cz) <= build_r:
            new.add(i)
    return new


def _is_on_own_side(cx, cz, hq_pos, midline_pos, midline_normal):
    """Check if (cx,cz) is on the same side as hq_pos relative to midline."""
    # midline passes through midline_pos with normal pointing from B→A
    # "own side" for A: dot > 0, for B: dot < 0
    # We check: is (cx,cz) on the same side as hq_pos?
    dot_hq = (hq_pos[0] - midline_pos[0]) * midline_normal[0] + \
             (hq_pos[1] - midline_pos[1]) * midline_normal[1]
    dot_c = (cx - midline_pos[0]) * midline_normal[0] + \
            (cz - midline_pos[1]) * midline_normal[1]
    return dot_c * dot_hq >= 0  # same sign = same side


def build_expansion_tree(hq_pos, enemy_pos, resources, max_chains=4,
                         min_patches=MIN_PATCHES_FOR_EXPANSION):
    """
    Build realistic expansion tree.

    Rules:
    - Only expand where >= min_patches NEW resources within BUILD_R
    - Chains 1-3 (T0-T2): stay on own side of midline
    - Chain 4 (T3): may cross midline (interception)
    - Greedy: pick candidate that captures most new patches

    Returns: list of (x, z, chain_stage, parent_idx, patches_captured)
    """
    # Midline: perpendicular bisector between hq_pos and enemy_pos
    mid = (np.array(hq_pos) + np.array(enemy_pos)) / 2
    axis = np.array(hq_pos) - np.array(enemy_pos)
    axis_len = np.linalg.norm(axis)
    if axis_len > 0:
        midline_normal = axis / axis_len  # points from enemy toward us
    else:
        midline_normal = np.array([1.0, 0.0])

    nodes = [(hq_pos[0], hq_pos[1], 0, -1, [])]  # (x, z, chain, parent, patch_indices)

    def captured_so_far():
        cap = set()
        for nx, nz, _, _, pi in nodes:
            cap.update(pi)
            # Also add patches in BUILD_R of this node
            for i, (rx, rz) in enumerate(resources):
                if np.hypot(rx - nx, rz - nz) <= BUILD_R:
                    cap.add(i)
        return cap

    # Find T0 patches (already in range of spawn HQ)
    t0_patches = set()
    for i, (rx, rz) in enumerate(resources):
        if np.hypot(rx - hq_pos[0], rz - hq_pos[1]) <= BUILD_R:
            t0_patches.add(i)
    nodes[0] = (hq_pos[0], hq_pos[1], 0, -1, list(t0_patches))

    for chain in range(1, max_chains + 1):
        already = captured_so_far()
        must_stay_own_side = chain <= 3  # T2 = chains 1-3, T3 = chain 4

        best_score = 0
        best_candidate = None
        best_parent = -1
        best_new = set()

        # Try placing from each existing node
        for pi, (px, pz, _, _, _) in enumerate(nodes):
            # Generate candidate positions: toward each uncaptured resource
            uncaptured = [(i, resources[i]) for i in range(len(resources))
                          if i not in already]

            targets = []
            # Individual resources
            for ri, (rx, rz) in uncaptured:
                targets.append((rx, rz))

            # Cluster centroids of nearby uncaptured resources
            if len(uncaptured) >= 3:
                ucoords = np.array([r for _, r in uncaptured])
                # Find clusters: resources within 800m of each other
                for ri, (rx, rz) in uncaptured:
                    nearby = ucoords[np.linalg.norm(ucoords - [rx, rz], axis=1) < 800]
                    if len(nearby) >= 3:
                        targets.append((np.mean(nearby[:, 0]), np.mean(nearby[:, 1])))

            for tx, tz in targets:
                dx, dz = tx - px, tz - pz
                d = np.hypot(dx, dz)
                if d < 100:
                    continue

                # Place HQ at chain distance toward target
                place_d = min(HQ_CHAIN_R, d)
                cx = px + dx / d * place_d
                cz = pz + dz / d * place_d
                cx = np.clip(cx, WORLD_MIN + 100, WORLD_MAX - 100)
                cz = np.clip(cz, WORLD_MIN + 100, WORLD_MAX - 100)

                # Must be within CHAIN_R of parent
                if np.hypot(cx - px, cz - pz) > HQ_CHAIN_R:
                    continue

                # Min separation from existing HQs
                if any(np.hypot(cx - nx, cz - nz) < 350
                       for nx, nz, _, _, _ in nodes):
                    continue

                # Side check
                if must_stay_own_side:
                    if not _is_on_own_side(cx, cz, hq_pos, mid, midline_normal):
                        continue

                # How many new patches?
                new = _new_patches_in_radius(cx, cz, resources, already)
                if len(new) < min_patches:
                    continue

                if len(new) > best_score:
                    best_score = len(new)
                    best_candidate = (cx, cz)
                    best_parent = pi
                    best_new = new

        if best_candidate is None:
            # No viable expansion left at this chain level
            # If we haven't crossed midline yet and this is T3 stage, that's expected
            break

        nodes.append((best_candidate[0], best_candidate[1], chain,
                       best_parent, list(best_new)))

    return nodes


# ── Resource Classification (tree-based) ─────────────────────────────────────

def classify_from_trees(tree_a, tree_b, enabled_mask, refinery_access=None):
    """
    Classify resources based on actual expansion trees.
    A resource belongs to whichever team's tree captures it.
    If both trees capture it (T3 overlap), it's contested.
    """
    # Build capture maps: resource_idx -> (team, chain_stage)
    cap_a = {}  # idx -> chain_stage
    cap_b = {}

    for nx, nz, chain, parent, patches in tree_a:
        for i in patches:
            if i not in cap_a or chain < cap_a[i]:
                cap_a[i] = chain
    # Also check BUILD_R of each tree node
    for nx, nz, chain, _, _ in tree_a:
        for i in range(len(BALTERIUM)):
            rx, rz = BALTERIUM[i, 1], BALTERIUM[i, 2]
            if np.hypot(rx - nx, rz - nz) <= BUILD_R:
                if i not in cap_a or chain < cap_a[i]:
                    cap_a[i] = chain

    for nx, nz, chain, parent, patches in tree_b:
        for i in patches:
            if i not in cap_b or chain < cap_b[i]:
                cap_b[i] = chain
    for nx, nz, chain, _, _ in tree_b:
        for i in range(len(BALTERIUM)):
            rx, rz = BALTERIUM[i, 1], BALTERIUM[i, 2]
            if np.hypot(rx - nx, rz - nz) <= BUILD_R:
                if i not in cap_b or chain < cap_b[i]:
                    cap_b[i] = chain

    results = []
    for row in BALTERIUM:
        idx = int(row[0])
        x, z, res = row[1], row[2], row[3]
        active = bool(row[4])
        if enabled_mask is not None:
            active = enabled_mask[idx]

        in_a = idx in cap_a
        in_b = idx in cap_b
        chain_a = cap_a.get(idx, 99)
        chain_b = cap_b.get(idx, 99)

        if in_a and in_b:
            # Both teams reach it
            if chain_a < chain_b:
                owner = 'A'
            elif chain_b < chain_a:
                owner = 'B'
            else:
                owner = 'contested'  # same chain stage = true fight
        elif in_a:
            owner = 'A'
        elif in_b:
            owner = 'B'
        else:
            owner = 'none'

        ref_ok = True
        ref_info = None
        if refinery_access is not None:
            ref_info = refinery_access.get(idx, {})
            ref_ok = ref_info.get('accessible', False)

        results.append({
            'idx': idx, 'x': x, 'z': z, 'res': res,
            'active': active,
            'chain_a': chain_a, 'chain_b': chain_b,
            'owner': owner,
            'ref_ok': ref_ok, 'ref_info': ref_info,
        })

    return results


def compute_fairness(classification):
    active = [r for r in classification if r['active']]
    team_a = [r for r in active if r['owner'] == 'A']
    team_b = [r for r in active if r['owner'] == 'B']
    contested = [r for r in active if r['owner'] == 'contested']
    unreached = [r for r in active if r['owner'] == 'none']

    res_a = sum(r['res'] for r in team_a)
    res_b = sum(r['res'] for r in team_b)
    res_c = sum(r['res'] for r in contested)

    # Starter = chain 0
    starter_a = sum(r['res'] for r in team_a if r['chain_a'] == 0)
    starter_b = sum(r['res'] for r in team_b if r['chain_b'] == 0)
    n_starter_a = sum(1 for r in team_a if r['chain_a'] == 0)
    n_starter_b = sum(1 for r in team_b if r['chain_b'] == 0)

    # T1 (chain 1)
    t1_a = sum(r['res'] for r in team_a if r['chain_a'] == 1)
    t1_b = sum(r['res'] for r in team_b if r['chain_b'] == 1)

    # T2 (chain 2-3)
    t2_a = sum(r['res'] for r in team_a if r['chain_a'] in (2, 3))
    t2_b = sum(r['res'] for r in team_b if r['chain_b'] in (2, 3))

    total_a = res_a + res_c / 2
    total_b = res_b + res_c / 2

    def _fair(a, b):
        return 1 - abs(a - b) / max(a, b) if max(a, b) > 0 else 1.0

    return {
        'n_a': len(team_a), 'n_b': len(team_b),
        'n_contested': len(contested), 'n_unreached': len(unreached),
        'res_a': res_a, 'res_b': res_b, 'res_contested': res_c,
        'starter_a': starter_a, 'starter_b': starter_b,
        'n_starter_a': n_starter_a, 'n_starter_b': n_starter_b,
        't1_a': t1_a, 't1_b': t1_b,
        't2_a': t2_a, 't2_b': t2_b,
        'total_a': total_a, 'total_b': total_b,
        'fairness': _fair(total_a, total_b),
        'starter_fairness': _fair(starter_a, starter_b),
        't1_fairness': _fair(t1_a, t1_b),
        't2_fairness': _fair(t2_a, t2_b),
    }


# ── Sweep ────────────────────────────────────────────────────────────────────

def sweep_angles(enabled_mask=None):
    """Sweep all angles, building real expansion trees at each."""
    em = enabled_mask if enabled_mask is not None else \
         np.array([bool(row[4]) for row in BALTERIUM])
    resources = [(BALTERIUM[i, 1], BALTERIUM[i, 2])
                 for i in range(len(BALTERIUM)) if em[i]]

    results = []
    for angle in range(360):
        rad = np.radians(angle)
        hq_a = np.array([SPAWN_DIST * np.cos(rad), SPAWN_DIST * np.sin(rad)])
        hq_b = -hq_a

        tree_a = build_expansion_tree(hq_a, hq_b, resources, max_chains=4)
        tree_b = build_expansion_tree(hq_b, hq_a, resources, max_chains=4)

        cls = classify_from_trees(tree_a, tree_b, em)
        f = compute_fairness(cls)
        f['angle'] = angle
        results.append(f)

    return results


def print_best_angles(sweep_results, top_n=12):
    by_combined = sorted(sweep_results,
                         key=lambda r: (r['fairness'] * 0.25 +
                                        r['starter_fairness'] * 0.35 +
                                        r['t1_fairness'] * 0.20 +
                                        r['t2_fairness'] * 0.20),
                         reverse=True)
    print(f"\n{'='*105}")
    print(f"  TOP {top_n}  (25% overall + 35% starter + 20% T1 + 20% T2)")
    print(f"  Build={BUILD_R}m  Chain={HQ_CHAIN_R}m  MinPatches={MIN_PATCHES_FOR_EXPANSION}")
    print(f"{'='*105}")
    print(f"  {'Ang':>4}  {'All':>5} {'St':>5} {'T1':>5} {'T2':>5} {'Comb':>5}  "
          f"{'A':>2} {'B':>2} {'C':>2} {'?':>2}  "
          f"{'St A':>6} {'St B':>6}  {'T1 A':>6} {'T1 B':>6}  "
          f"{'T2 A':>6} {'T2 B':>6}")
    print(f"  {'-'*105}")
    for r in by_combined[:top_n]:
        c = (r['fairness'] * 0.25 + r['starter_fairness'] * 0.35 +
             r['t1_fairness'] * 0.20 + r['t2_fairness'] * 0.20)
        print(f"  {r['angle']:>3}°  {r['fairness']:.3f} {r['starter_fairness']:.3f} "
              f"{r['t1_fairness']:.3f} {r['t2_fairness']:.3f} {c:.3f}  "
              f"{r['n_a']:>2} {r['n_b']:>2} {r['n_contested']:>2} {r['n_unreached']:>2}  "
              f"{r['starter_a']:>6.0f} {r['starter_b']:>6.0f}  "
              f"{r['t1_a']:>6.0f} {r['t1_b']:>6.0f}  "
              f"{r['t2_a']:>6.0f} {r['t2_b']:>6.0f}")


# ── Natural Cluster Finding ──────────────────────────────────────────────────

def _patches_at(cx, cz, mask=None, build_r=BUILD_R):
    """Which patch indices are within build_r of (cx, cz)."""
    positions = BALTERIUM[:, 1:3]
    dx = positions[:, 0] - cx
    dz = positions[:, 1] - cz
    dist = np.sqrt(dx*dx + dz*dz)
    within = dist <= build_r
    if mask is not None:
        within = within & mask
    return np.where(within)[0]


def find_clusters(mask=None, build_r=BUILD_R, min_patches=3):
    """Find all positions where >= min_patches Balterium are within build_r."""
    positions = BALTERIUM[:, 1:3]
    resources = BALTERIUM[:, 3]
    candidates = []

    # Test at each patch position
    for i in range(len(BALTERIUM)):
        if mask is not None and not mask[i]:
            continue
        cx, cz = positions[i]
        p = _patches_at(cx, cz, mask, build_r)
        if len(p) >= min_patches:
            total_res = resources[p].sum()
            candidates.append({
                'x': cx, 'z': cz,
                'patches': p.tolist(), 'n': len(p), 'res': total_res,
            })

    # Test at centroids of nearby groups
    tested = set()
    for i in range(len(BALTERIUM)):
        if mask is not None and not mask[i]:
            continue
        nearby = _patches_at(positions[i, 0], positions[i, 1], mask, build_r * 2.5)
        if len(nearby) < min_patches:
            continue
        for combo in combinations(nearby, min(min_patches, len(nearby))):
            key = tuple(sorted(combo))
            if key in tested:
                continue
            tested.add(key)
            cx = np.mean(positions[list(combo), 0])
            cz = np.mean(positions[list(combo), 1])
            p = _patches_at(cx, cz, mask, build_r)
            if len(p) >= min_patches:
                total_res = resources[p].sum()
                candidates.append({
                    'x': cx, 'z': cz,
                    'patches': p.tolist(), 'n': len(p), 'res': total_res,
                })

    # Deduplicate: merge within 200m, keep best
    clusters = []
    candidates.sort(key=lambda c: (-c['n'], -c['res']))
    for c in candidates:
        if any(np.hypot(c['x'] - cl['x'], c['z'] - cl['z']) < 200
               for cl in clusters):
            continue
        clusters.append(c)

    return clusters


def find_opposing_pairs(clusters, min_sep=2000, min_dist=800, max_dist=2500):
    """Find opposing cluster pairs suitable for HvH spawns."""
    pairs = []
    for i, a in enumerate(clusters):
        for j, b in enumerate(clusters):
            if j <= i:
                continue
            # Opposite sides of center
            dot = a['x'] * b['x'] + a['z'] * b['z']
            if dot >= 0:
                continue

            a_dist = np.hypot(a['x'], a['z'])
            b_dist = np.hypot(b['x'], b['z'])
            if not (min_dist <= a_dist <= max_dist and min_dist <= b_dist <= max_dist):
                continue

            sep = np.hypot(a['x'] - b['x'], a['z'] - b['z'])
            if sep < min_sep:
                continue

            dist_ratio = min(a_dist, b_dist) / max(a_dist, b_dist)
            res_fair = 1 - abs(a['res'] - b['res']) / max(a['res'], b['res'])
            n_fair = 1 - abs(a['n'] - b['n']) / max(a['n'], b['n'])
            score = dist_ratio * 0.3 + res_fair * 0.4 + n_fair * 0.3

            pairs.append({
                'a': a, 'b': b,
                'sep': sep, 'score': score,
                'dist_ratio': dist_ratio, 'res_fair': res_fair, 'n_fair': n_fair,
            })

    pairs.sort(key=lambda p: -p['score'])
    return pairs


def evaluate_cluster_pair(pair, enabled_mask, ref_access=None):
    """Run full expansion tree analysis on a cluster pair."""
    em = enabled_mask
    resources = [(BALTERIUM[i, 1], BALTERIUM[i, 2])
                 for i in range(len(BALTERIUM)) if em[i]]

    hq_a = np.array([pair['a']['x'], pair['a']['z']])
    hq_b = np.array([pair['b']['x'], pair['b']['z']])

    tree_a = build_expansion_tree(hq_a, hq_b, resources, max_chains=4)
    tree_b = build_expansion_tree(hq_b, hq_a, resources, max_chains=4)

    cls = classify_from_trees(tree_a, tree_b, em, ref_access)
    f = compute_fairness(cls)

    return hq_a, hq_b, tree_a, tree_b, cls, f


# ── Visualization ────────────────────────────────────────────────────────────

def plot_setup(angle_deg, tree_a, tree_b, classification, fairness,
               hq_a, hq_b, refinery_access, img,
               title_suffix="", filename_suffix="", pair_label=None):

    fig, ax = plt.subplots(1, 1, figsize=(18, 18), dpi=130)
    fig.patch.set_facecolor('#111111')
    ax.set_facecolor('#1a1a1a')
    ax.imshow(img, extent=[WORLD_MIN, WORLD_MAX, WORLD_MIN, WORLD_MAX],
              origin='upper', alpha=0.30)

    TEAM_CLR = {'A': '#FF4444', 'B': '#4488FF'}
    CHAIN_STYLE = {
        0: dict(fa=0.10, ea=0.45, lw=2.5, ls='-',  ms=16, mew=3, lbl='HQ'),
        1: dict(fa=0.06, ea=0.30, lw=1.8, ls='--', ms=11, mew=2, lbl='T1'),
        2: dict(fa=0.04, ea=0.20, lw=1.3, ls='--', ms=9,  mew=1.5, lbl='T2'),
        3: dict(fa=0.03, ea=0.14, lw=1.0, ls=':',  ms=7,  mew=1.2, lbl='T2'),
        4: dict(fa=0.02, ea=0.10, lw=0.7, ls=':',  ms=6,  mew=1, lbl='T3'),
    }

    # ── Draw expansion trees ──
    for team, tree, color in [('A', tree_a, TEAM_CLR['A']),
                               ('B', tree_b, TEAM_CLR['B'])]:
        for i, (nx, nz, chain, parent, patches) in enumerate(tree):
            st = CHAIN_STYLE.get(chain, CHAIN_STYLE[4])

            # Build radius: fill + edge
            ax.add_patch(Circle((nx, nz), BUILD_R,
                                fill=True, facecolor=color, alpha=st['fa'],
                                edgecolor='none', zorder=2))
            ax.add_patch(Circle((nx, nz), BUILD_R,
                                fill=False, edgecolor=color,
                                lw=st['lw'], ls=st['ls'], alpha=st['ea'],
                                zorder=3))

            # Chain range circle (where NEXT HQ could go)
            if chain < 4:
                ax.add_patch(Circle((nx, nz), HQ_CHAIN_R,
                                    fill=False, edgecolor=color,
                                    lw=0.3, ls=':', alpha=0.05, zorder=1))

            # HQ marker
            mk = 's' if chain == 0 else 'o'
            fc = color if chain == 0 else 'none'
            ec = 'white' if chain == 0 else color
            ax.plot(nx, nz, mk, ms=st['ms'], color=color,
                    mfc=fc, mec=ec, mew=st['mew'],
                    zorder=14 - chain, alpha=0.9)

            # Arrow from parent
            if parent >= 0:
                px, pz = tree[parent][0], tree[parent][1]
                ax.annotate('', xy=(nx, nz), xytext=(px, pz),
                            arrowprops=dict(arrowstyle='->', color=color,
                                            lw=1.5, alpha=0.35), zorder=5)

            # Label with patch count
            n_cap = len(patches)
            lbl = f"{st['lbl']}"
            if chain > 0:
                lbl += f" ({n_cap})"
            ax.text(nx, nz - BUILD_R - 45, lbl,
                    ha='center', fontsize=6, color=color, alpha=0.6,
                    fontweight='bold', zorder=12)

    # Team labels
    ax.text(hq_a[0], hq_a[1] + BUILD_R + 100, 'Team A',
            ha='center', fontsize=13, fontweight='bold', color='#FF4444', zorder=16)
    ax.text(hq_b[0], hq_b[1] + BUILD_R + 100, 'Team B',
            ha='center', fontsize=13, fontweight='bold', color='#4488FF', zorder=16)

    # ── Resource patches ──
    owner_colors = {'A': '#FF4444', 'B': '#4488FF', 'contested': '#FFD700', 'none': '#555555'}

    for r in classification:
        x, z = r['x'], r['z']
        color = owner_colors[r['owner']]
        active = r['active']
        ref_ok = r.get('ref_ok', True)
        ms = max(6, min(14, r['res'] / 4500))
        alpha = 0.9 if active else 0.2
        edge = 'white' if active else '#444'

        ax.plot(x, z, 'D', ms=ms, color=color,
                mec=edge, mew=1.3, alpha=alpha, zorder=10)

        if active:
            # Refinery indicator
            if ref_ok:
                ax.plot(x + 60, z + 60, 'o', ms=4, color='#00CC00',
                        mec='#00CC00', mew=0, alpha=0.7, zorder=11)
            else:
                ax.plot(x + 60, z + 60, 'x', ms=6, color='#FF0000',
                        mew=2, alpha=0.8, zorder=11)

            # Chain label
            if r['owner'] == 'A' and r['chain_a'] < 99:
                cl = r['chain_a']
                tl = 'T0' if cl == 0 else f'T1' if cl == 1 else f'T2' if cl <= 3 else 'T3'
                ax.text(x + 70, z - 70, tl, fontsize=5, color=color,
                        alpha=0.65, zorder=11)
            elif r['owner'] == 'B' and r['chain_b'] < 99:
                cl = r['chain_b']
                tl = 'T0' if cl == 0 else f'T1' if cl == 1 else f'T2' if cl <= 3 else 'T3'
                ax.text(x + 70, z - 70, tl, fontsize=5, color=color,
                        alpha=0.65, zorder=11)
            elif r['owner'] == 'contested':
                ax.text(x + 70, z - 70, 'T3', fontsize=5, color='#FFD700',
                        alpha=0.65, zorder=11)

    # ── Midline ──
    mid = (hq_a + hq_b) / 2
    axis = hq_a - hq_b
    alen = np.linalg.norm(axis)
    if alen > 0:
        perp = np.array([-axis[1], axis[0]]) / alen
        half = 3500
        ax.plot([mid[0] - perp[0]*half, mid[0] + perp[0]*half],
                [mid[1] - perp[1]*half, mid[1] + perp[1]*half],
                '-', color='#FFD700', lw=1.5, alpha=0.25, zorder=3)
        ax.text(mid[0] + perp[0]*200, mid[1] + perp[1]*200 + 80,
                'midline', fontsize=8, color='#FFD700', alpha=0.35,
                ha='center', zorder=12)

    # HQ axis
    ax.plot([hq_a[0], hq_b[0]], [hq_a[1], hq_b[1]],
            '--', color='white', lw=0.7, alpha=0.15, zorder=2)

    # ── Info box ──
    f = fairness
    n_ref_ok = sum(1 for r in classification if r['active'] and r.get('ref_ok', True))
    n_active = sum(1 for r in classification if r['active'])
    comb = (f['fairness'] * 0.25 + f['starter_fairness'] * 0.35 +
            f['t1_fairness'] * 0.20 + f['t2_fairness'] * 0.20)

    pos_label = pair_label if pair_label else f"Angle: {angle_deg}°"
    info = (
        f"{pos_label}  Chain: {HQ_CHAIN_R}m  Build: {BUILD_R}m\n"
        f"Min patches/expansion: {MIN_PATCHES_FOR_EXPANSION}\n"
        f"{'─'*40}\n"
        f"Overall:  {f['fairness']:.3f}   Starter: {f['starter_fairness']:.3f}\n"
        f"T1:       {f['t1_fairness']:.3f}   T2:      {f['t2_fairness']:.3f}\n"
        f"Combined: {comb:.3f}   Refinery: {n_ref_ok}/{n_active}\n"
        f"{'─'*40}\n"
        f"Team A: {f['n_a']} patches ({f['res_a']:,.0f})\n"
        f"  T0: {f['n_starter_a']}p {f['starter_a']:,.0f}\n"
        f"  T1: {f['t1_a']:,.0f}  T2: {f['t2_a']:,.0f}\n"
        f"Team B: {f['n_b']} patches ({f['res_b']:,.0f})\n"
        f"  T0: {f['n_starter_b']}p {f['starter_b']:,.0f}\n"
        f"  T1: {f['t1_b']:,.0f}  T2: {f['t2_b']:,.0f}\n"
        f"Contested: {f['n_contested']} ({f['res_contested']:,.0f})\n"
        f"Unreached: {f['n_unreached']}"
    )
    ax.text(0.01, 0.99, info, transform=ax.transAxes, fontsize=8,
            verticalalignment='top', fontfamily='monospace',
            color='white', zorder=20,
            bbox=dict(facecolor='black', alpha=0.85, boxstyle='round,pad=0.5'))

    # ── Legend ──
    legend_els = [
        Line2D([0], [0], marker='s', color='w', mfc='#FF4444', ms=12,
               label='Team A HQ', ls=''),
        Line2D([0], [0], marker='s', color='w', mfc='#4488FF', ms=12,
               label='Team B HQ', ls=''),
        Line2D([0], [0], marker='o', color='w', mfc='none', mec='#FF4444',
               ms=9, label='Expansion HQ (A)', ls=''),
        Line2D([0], [0], marker='o', color='w', mfc='none', mec='#4488FF',
               ms=9, label='Expansion HQ (B)', ls=''),
        Line2D([0], [0], marker='D', color='w', mfc='#FF4444', ms=8,
               label='A-owned', ls=''),
        Line2D([0], [0], marker='D', color='w', mfc='#4488FF', ms=8,
               label='B-owned', ls=''),
        Line2D([0], [0], marker='D', color='w', mfc='#FFD700', ms=8,
               label='Contested (T3)', ls=''),
        Line2D([0], [0], marker='D', color='w', mfc='#555', ms=8,
               label='Unreached / Disabled', ls=''),
        Line2D([0], [0], marker='o', color='w', mfc='#00CC00', ms=5,
               label='Refinery OK', ls=''),
    ]
    ax.legend(handles=legend_els, loc='lower right', fontsize=7,
              facecolor='#1a1a1a', edgecolor='#555', labelcolor='white',
              ncol=2)

    ax.set_xlim(WORLD_MIN - 100, WORLD_MAX + 100)
    ax.set_ylim(WORLD_MIN - 100, WORLD_MAX + 100)
    ax.set_aspect('equal')
    ax.grid(True, alpha=0.06, color='white')
    ax.tick_params(colors='#888', labelsize=7)
    for s in ax.spines.values():
        s.set_edgecolor('#444')

    pos_title = pair_label if pair_label else f'{angle_deg}°'
    title = (f'GreatErg HvH — {pos_title} | '
             f'Chain {HQ_CHAIN_R}m | Build {BUILD_R}m | '
             f'MinPatch {MIN_PATCHES_FOR_EXPANSION}{title_suffix}')
    ax.set_title(title, fontsize=13, fontweight='bold', color='white', pad=10)

    out = os.path.join(OUTPUT_DIR, f'hvh_v5_{angle_deg}deg{filename_suffix}.png')
    fig.savefig(out, dpi=130, bbox_inches='tight', facecolor=fig.get_facecolor())
    print(f"  Saved: {out}")
    plt.close(fig)


def plot_sweep(sweep_results, title_suffix="", filename_suffix=""):
    angles = [np.radians(r['angle']) for r in sweep_results]
    fairness = [r['fairness'] for r in sweep_results]
    starter = [r['starter_fairness'] for r in sweep_results]
    t1 = [r['t1_fairness'] for r in sweep_results]
    t2 = [r['t2_fairness'] for r in sweep_results]

    fig, ax = plt.subplots(1, 1, figsize=(10, 10), dpi=100,
                           subplot_kw={'projection': 'polar'})
    fig.patch.set_facecolor('#111111')
    ax.set_facecolor('#1a1a1a')

    ax.plot(angles + [angles[0]], fairness + [fairness[0]],
            color='#00FF88', lw=2, label='Overall', alpha=0.9)
    ax.plot(angles + [angles[0]], starter + [starter[0]],
            color='#FF8800', lw=2, label='Starter', alpha=0.9)
    ax.plot(angles + [angles[0]], t1 + [t1[0]],
            color='#88FF88', lw=1.5, label='T1', alpha=0.7)
    ax.plot(angles + [angles[0]], t2 + [t2[0]],
            color='#8888FF', lw=1.5, label='T2', alpha=0.7)

    ax.set_ylim(0, 1.05)
    ax.set_theta_zero_location('E')
    ax.set_theta_direction(1)
    ax.grid(True, alpha=0.3, color='#555')
    ax.tick_params(colors='#888', labelsize=8)
    ax.set_title(f'HvH Fairness vs Angle{title_suffix}\n'
                 f'Build={BUILD_R}m Chain={HQ_CHAIN_R}m MinPatch={MIN_PATCHES_FOR_EXPANSION}',
                 fontsize=11, fontweight='bold', color='white', pad=20)
    ax.legend(loc='lower right', fontsize=9, facecolor='#222', labelcolor='white')

    out = os.path.join(OUTPUT_DIR, f'fairness_sweep_v4{filename_suffix}.png')
    fig.savefig(out, dpi=100, bbox_inches='tight', facecolor=fig.get_facecolor())
    print(f"  Saved: {out}")
    plt.close(fig)


# ── Main ─────────────────────────────────────────────────────────────────────

def main():
    print(f"=== GreatErg HvH Analysis v5.0 (Cluster-Based Spawns) ===")
    print(f"  Build: {BUILD_R}m  Chain: {HQ_CHAIN_R}m")
    print(f"  Min patches per expansion: {MIN_PATCHES_FOR_EXPANSION}")
    print(f"  Spawn HQs at natural cluster positions (>=3 patches in BUILD_R)")
    print(f"  T0=starter  T1=1chain  T2=2-3chains(own side)  T3=4chains(crosses)\n")

    print("Loading refinery scan...")
    ref_df = load_refinery_scan(REFINERY_SCAN)
    print("Checking refinery access...")
    ref_access = check_refinery_access(ref_df)

    img = Image.open(MAP_IMAGE)

    for mask_label, enabled_mask in [("all64", np.ones(64, dtype=bool)),
                                      ("vanilla", np.array([bool(row[4]) for row in BALTERIUM]))]:
        em = enabled_mask
        n_active = int(em.sum())
        print(f"\n{'='*80}")
        print(f"  {mask_label.upper()} ({n_active} patches)")
        print(f"{'='*80}")

        # Find natural clusters
        print(f"\n  Finding natural clusters (>={MIN_PATCHES_FOR_EXPANSION} patches within {BUILD_R}m)...")
        clusters = find_clusters(em, BUILD_R, MIN_PATCHES_FOR_EXPANSION)
        print(f"  Found {len(clusters)} cluster positions")

        # Find opposing pairs
        print(f"  Finding opposing pairs (sep>=2000m, dist 800-2500m from center)...")
        pairs = find_opposing_pairs(clusters, min_sep=2000, min_dist=800, max_dist=2500)
        print(f"  Found {len(pairs)} opposing pairs")

        if not pairs:
            print("  No viable opposing pairs found!")
            continue

        # Evaluate top pairs with full expansion trees
        print(f"\n  Evaluating top {min(10, len(pairs))} pairs with expansion trees...")
        print(f"\n  {'#':>2} {'Score':>5} {'Sep':>5} {'nA':>3}{'nB':>3} "
              f"{'StA':>7}{'StB':>7} {'T1f':>5} {'T2f':>5} {'AllF':>5} {'Comb':>5} "
              f"{'XA':>7}{'ZA':>7} {'XB':>7}{'ZB':>7}")
        print(f"  {'-'*110}")

        evaluated = []
        for pi, pair in enumerate(pairs[:10]):
            hq_a, hq_b, tree_a, tree_b, cls, f = evaluate_cluster_pair(pair, em, ref_access)

            comb = (f['fairness'] * 0.25 + f['starter_fairness'] * 0.35 +
                    f['t1_fairness'] * 0.20 + f['t2_fairness'] * 0.20)

            evaluated.append({
                'pair': pair, 'hq_a': hq_a, 'hq_b': hq_b,
                'tree_a': tree_a, 'tree_b': tree_b,
                'cls': cls, 'fairness': f, 'combined': comb,
            })

            print(f"  {pi+1:>2} {pair['score']:.3f} {pair['sep']:>5.0f} "
                  f"{pair['a']['n']:>3}{pair['b']['n']:>3} "
                  f"{f['starter_a']:>7.0f}{f['starter_b']:>7.0f} "
                  f"{f['t1_fairness']:.3f} {f['t2_fairness']:.3f} "
                  f"{f['fairness']:.3f} {comb:.3f} "
                  f"{hq_a[0]:>7.0f}{hq_a[1]:>7.0f} "
                  f"{hq_b[0]:>7.0f}{hq_b[1]:>7.0f}")

        # Sort by combined fairness for visualization
        evaluated.sort(key=lambda e: -e['combined'])

        # Visualize top 4 most fair setups (deduplicate near positions)
        print(f"\n  Visualizing top setups...")
        vis_count = 0
        seen_positions = []
        for ev in evaluated:
            # Skip if too close to an already-visualized pair
            too_close = False
            for sp in seen_positions:
                if (np.hypot(ev['hq_a'][0] - sp[0], ev['hq_a'][1] - sp[1]) < 400 or
                    np.hypot(ev['hq_b'][0] - sp[0], ev['hq_b'][1] - sp[1]) < 400):
                    too_close = True
                    break
            if too_close:
                continue

            seen_positions.append(ev['hq_a'])
            seen_positions.append(ev['hq_b'])

            p = ev['pair']
            f = ev['fairness']
            label = (f"A({p['a']['n']}p@{ev['hq_a'][0]:.0f},{ev['hq_a'][1]:.0f}) "
                     f"vs B({p['b']['n']}p@{ev['hq_b'][0]:.0f},{ev['hq_b'][1]:.0f})")

            # Compute approximate angle for filename
            angle_deg = int(np.degrees(np.arctan2(ev['hq_a'][1], ev['hq_a'][0]))) % 360

            print(f"\n    [{mask_label}] Pair #{vis_count+1}: {label}")
            print(f"      Combined: {ev['combined']:.3f}  Overall: {f['fairness']:.3f}  "
                  f"Starter: {f['starter_fairness']:.3f}  T1: {f['t1_fairness']:.3f}  "
                  f"T2: {f['t2_fairness']:.3f}")
            print(f"      Tree A: {len(ev['tree_a'])} nodes  Tree B: {len(ev['tree_b'])} nodes  "
                  f"Contested: {f['n_contested']}  Unreached: {f['n_unreached']}")
            print(f"      Starter A: {f['n_starter_a']}p ({f['starter_a']:,.0f})  "
                  f"Starter B: {f['n_starter_b']}p ({f['starter_b']:,.0f})")

            plot_setup(angle_deg, ev['tree_a'], ev['tree_b'], ev['cls'], f,
                       ev['hq_a'], ev['hq_b'], ref_access, img,
                       f" ({mask_label})", f"_cluster{vis_count+1}_{mask_label}",
                       pair_label=label)

            vis_count += 1
            if vis_count >= 4:
                break

    print("\nDone!")


if __name__ == '__main__':
    main()
