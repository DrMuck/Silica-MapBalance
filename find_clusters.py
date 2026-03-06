"""
Find all natural Balterium clusters on GreatErg.
A "cluster" = a point where placing an HQ (500m build radius) covers >=3 patches.

Scans at the position of each Balterium patch and also at centroids of nearby groups.
Outputs the best HQ positions for spawns and expansions.
"""

import numpy as np
from itertools import combinations

BUILD_R = 500

# All 64 Balterium (idx, x, z, resources, default_active)
BALT = np.array([
    [ 0, 2338.2, 2557.4, 35600, 0], [ 1, 2679.4, 2496.6, 39200, 0],
    [ 2,-1594.0,  721.1, 40800, 1], [ 3,-2499.6,-2651.2, 22400, 0],
    [ 4, 1826.5, 2156.9, 36800, 1], [ 5, -640.9, 2541.9, 40400, 0],
    [ 6, -363.6, -262.8, 41200, 0], [ 7,-1827.4, 1103.0, 40800, 1],
    [ 8,  591.2, -241.2, 38000, 0], [ 9,  708.6,-1481.2, 34400, 1],
    [10,  386.3, 1025.2, 69600, 1], [11, -137.7, -614.4, 50800, 1],
    [12, -244.7,-1833.8, 32400, 1], [13, 1358.2,  830.2, 35200, 0],
    [14,  944.9, 1575.4, 35200, 1], [15, 1404.0, -293.0, 42800, 1],
    [16,-1854.1, 2618.1, 46400, 1], [17, 1974.1,-1057.1, 42400, 1],
    [18,-1835.3,-1880.5, 35200, 0], [19,  379.8, 2635.6, 35200, 1],
    [20, 2325.8, 1746.3, 37600, 1], [21, 2597.1, 1155.6, 36800, 0],
    [22,  914.8, -799.8, 40000, 1], [23, 1052.2,  452.8, 48800, 1],
    [24, 1329.5, -936.6, 33600, 1], [25, 2136.2,-1927.8, 32000, 1],
    [26, 2632.4,-2500.8, 46400, 1], [27,  308.9,  218.5, 34000, 0],
    [28, 1183.3, 1135.2, 37600, 0], [29,-1271.2, 2221.2, 40000, 1],
    [30,-2310.9, 1684.1, 46400, 1], [31, 2344.9, -332.6, 40400, 1],
    [32, 1431.4, 2711.1, 32400, 1], [33,  308.6,-2081.2, 39600, 0],
    [34, -862.2,-1649.4, 37200, 1], [35, -986.0,  495.1, 46000, 0],
    [36,-1577.7,-2443.2, 31600, 0], [37,-2214.5, -861.1, 40800, 1],
    [38, -922.5, -833.4, 46800, 1], [39,   13.4, 1058.9, 37600, 1],
    [40, 1369.3,-1548.2, 54400, 1], [41, 2023.1,-1383.1, 50000, 1],
    [42, -744.2, 1774.5, 36400, 1], [43,-1863.8,   37.2, 50000, 1],
    [44, -324.1,-1039.2, 56800, 1], [45,  -20.9,-1212.5, 44800, 1],
    [46,-2284.0, -189.2, 42400, 1], [47, -366.2,  709.4, 42400, 1],
    [48, 1673.6,-2113.7, 41600, 0], [49,-1221.9,-1460.4, 53600, 0],
    [50, -529.9,  251.3, 41600, 1], [51,  863.3, 2241.2, 35600, 1],
    [52,-2625.8, -499.6, 40800, 1], [53,-2439.7,  512.9, 55200, 1],
    [54,-2502.5,-2137.8, 27600, 1], [55,-2705.5, 2167.9, 47600, 1],
    [56, -641.9, -468.6, 43200, 1], [57, -944.8, 1140.2, 44800, 1],
    [58, 1900.5,-2674.1, 40000, 1], [59, 1692.2,-1282.7, 32800, 0],
    [60, 1459.0, 1773.7, 35200, 0], [61,-1667.6, -607.1, 42000, 1],
    [62, 1165.3,  134.9, 41200, 1], [63,-1659.2, 1766.8, 46800, 0],
])

positions = BALT[:, 1:3]  # (x, z)
resources = BALT[:, 3]
active = BALT[:, 4].astype(bool)


def patches_at(cx, cz, mask=None, build_r=BUILD_R):
    """Which patch indices are within build_r of (cx, cz)."""
    dx = positions[:, 0] - cx
    dz = positions[:, 1] - cz
    dist = np.sqrt(dx*dx + dz*dz)
    within = dist <= build_r
    if mask is not None:
        within = within & mask
    return np.where(within)[0]


def find_clusters(mask=None, build_r=BUILD_R, min_patches=3):
    """
    Find all good HQ positions where >= min_patches are within build_r.
    Tests at each patch position + centroids of nearby groups.
    """
    candidates = []

    # Test at each patch position
    for i in range(len(BALT)):
        if mask is not None and not mask[i]:
            continue
        cx, cz = positions[i]
        p = patches_at(cx, cz, mask, build_r)
        if len(p) >= min_patches:
            total_res = resources[p].sum()
            candidates.append({
                'x': cx, 'z': cz,
                'patches': p.tolist(),
                'n': len(p),
                'res': total_res,
                'center_patch': i,
            })

    # Test at centroids of each 3-patch combination within 2*build_r
    # (find better center positions between patches)
    tested = set()
    for i in range(len(BALT)):
        if mask is not None and not mask[i]:
            continue
        nearby = patches_at(positions[i, 0], positions[i, 1], mask, build_r * 2.5)
        if len(nearby) < min_patches:
            continue
        for combo in combinations(nearby, min(min_patches, len(nearby))):
            key = tuple(sorted(combo))
            if key in tested:
                continue
            tested.add(key)
            cx = np.mean(positions[list(combo), 0])
            cz = np.mean(positions[list(combo), 1])
            p = patches_at(cx, cz, mask, build_r)
            if len(p) >= min_patches:
                total_res = resources[p].sum()
                candidates.append({
                    'x': cx, 'z': cz,
                    'patches': p.tolist(),
                    'n': len(p),
                    'res': total_res,
                })

    # Deduplicate: merge candidates within 200m of each other, keep best
    clusters = []
    used = set()
    # Sort by n desc, then res desc
    candidates.sort(key=lambda c: (-c['n'], -c['res']))
    for c in candidates:
        if any(np.hypot(c['x'] - cl['x'], c['z'] - cl['z']) < 200
               for cl in clusters):
            continue
        clusters.append(c)

    return clusters


def main():
    print("=== Balterium Cluster Analysis ===\n")

    for label, mask in [("All 64", None), ("Vanilla (48)", active)]:
        print(f"\n--- {label} ---")
        clusters = find_clusters(mask, BUILD_R, min_patches=3)
        print(f"  Found {len(clusters)} cluster positions with >=3 patches within {BUILD_R}m")

        # Sort by distance from center
        for c in clusters:
            c['dist'] = np.hypot(c['x'], c['z'])

        clusters.sort(key=lambda c: -c['n'])

        print(f"\n  {'N':>2} {'Res':>7} {'X':>7} {'Z':>7} {'Dist':>6}  Patch indices")
        print(f"  {'-'*65}")
        for c in clusters:
            idx_str = ','.join(str(i) for i in c['patches'])
            print(f"  {c['n']:>2} {c['res']:>7.0f} {c['x']:>7.0f} {c['z']:>7.0f} "
                  f"{c['dist']:>6.0f}  [{idx_str}]")

        # Find opposing pairs for HvH spawns
        print(f"\n  --- Best opposing pairs (spawn distance 1000-2000m from center) ---")
        pairs = []
        for i, a in enumerate(clusters):
            for j, b in enumerate(clusters):
                if j <= i:
                    continue
                # Must be on opposite sides of center
                dot = a['x'] * b['x'] + a['z'] * b['z']
                if dot >= 0:
                    continue  # same side

                # Both should be 1000-2500m from center
                if not (800 <= a['dist'] <= 2500 and 800 <= b['dist'] <= 2500):
                    continue

                # Distance between them
                sep = np.hypot(a['x'] - b['x'], a['z'] - b['z'])
                if sep < 2000:
                    continue  # too close

                # Spawn distance balance
                dist_ratio = min(a['dist'], b['dist']) / max(a['dist'], b['dist'])

                # Resource balance
                res_fair = 1 - abs(a['res'] - b['res']) / max(a['res'], b['res'])

                # Patch count balance
                n_fair = 1 - abs(a['n'] - b['n']) / max(a['n'], b['n'])

                score = dist_ratio * 0.3 + res_fair * 0.4 + n_fair * 0.3

                pairs.append({
                    'a': a, 'b': b,
                    'sep': sep,
                    'dist_ratio': dist_ratio,
                    'res_fair': res_fair,
                    'n_fair': n_fair,
                    'score': score,
                })

        pairs.sort(key=lambda p: -p['score'])
        print(f"  {'Score':>5} {'Sep':>5} {'N_A':>3}{'N_B':>4} "
              f"{'Res_A':>7}{'Res_B':>8} {'X_A':>7}{'Z_A':>7} {'X_B':>7}{'Z_B':>7} "
              f"{'dA':>5}{'dB':>6}")
        for p in pairs[:15]:
            a, b = p['a'], p['b']
            print(f"  {p['score']:.3f} {p['sep']:>5.0f} {a['n']:>3}{b['n']:>4} "
                  f"{a['res']:>7.0f}{b['res']:>8.0f} "
                  f"{a['x']:>7.0f}{a['z']:>7.0f} {b['x']:>7.0f}{b['z']:>7.0f} "
                  f"{a['dist']:>5.0f}{b['dist']:>6.0f}")


if __name__ == '__main__':
    main()
