/*
 Si_MapBalance 3.6.0 - physics-aware base site test (shared by the site scan, the layout check and the
 runtime warning). See docs/SITE_SCAN.md in Si_MapBalanceTool for the data contract.

 A base site (structure S of team T at x/z with a yaw) is VALID when all of these pass:
   1. On the map: FogOfWar.Instance.Bounds contains the point (the game's OutsideMap test in
      ConstructionPreview.GetWithinBaseStructuresDistanceAndFogOfWar) and the point is on a terrain tile.
   2. Ground: the structure's own ConstructionPreview.GetGroundSlopeClear(position, rotation, out adjustY)
      at the grid-snapped position (ConstructionData.SnapToGrid of the surface hit) and the rotation from
      ConstructionPreview.GetSnappedRotation - what StrategyMode (commander placement preview) and
      ConstructionPlacement call. It raycasts every AboveGround/BelowGround point from +100 m down against
      GamePhysics.TRACELAYERMASK_IGNOREDYNAMIC, so rocks, roofs and overhangs under/over the points count.
      Ground() replicates it to measure the foundation gap (how far the worst BelowGround point hangs above
      the ground); valid while gap <= GroundTol (calibrated on the vanilla start points; the exact rule is
      flag bit 5). A structure without ConstructionData passes.
   3. On top of static geometry (> OnStaticMax above the heightmap): recorded, NOT a failure unless
      OnStaticFails (Citadel plateaus and IndustrialQuarter platforms are meshes and playable ground).
   4. No static overlap: the structure prefab's solid colliders (non-trigger, movement layers; one box per
      collider in the structure's frame) at the final height, bottom clipped at the lowest AboveGround point
      + 0.5 m: no collider on the static layers (TRACELAYERMASK_IGNOREDYNAMIC minus Terrain/Water/No Collide/
      Gizmos/UI/AIVehicleObstacle) or "Invisible Walls" penetrates one deeper than OverlapDepth
      (Physics.ComputePenetration). Colliders owned by a BaseGameObject (structures, units) are ignored.
   5. Navmesh: at least NAV_MIN_PROBES of the 8 probes on the footprint rectangle grown by NAV_MARGIN_M
      (4 corners + 4 edge midpoints) snap to a walkable node of the site graph (humans: "Small Vehicle",
      Nest: the alien ground graph) within NAV_MAX_H_M / NAV_MAX_V_M (+ voxel slack) that lies in the
      graph's MAIN component (the component with the most walkable nodes, tracked by a reference node
      because A* renumbers GraphNode.Area after graph updates).
 Reason code = first failing test in the order above; the flag bits keep every test's own result.
*/

using MelonLoader;
using Pathfinding;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Si_MapBalance
{
    /// <summary>Site code byte (also the pixel value of data/sites/site_*.png).</summary>
    public static class SiteCode
    {
        public const int VALID = 1 << 0;
        public const int REASON_SHIFT = 1, REASON_MASK = 0x7 << 1;
        public const int F_TERRAIN_RULE = 1 << 4;   // legacy terrain-only rule (hq_scan) passes
        public const int F_GAME_RULE = 1 << 5;      // GetGroundSlopeClear passes (physics)
        public const int F_NO_OVERLAP = 1 << 6;     // no static overlap (and, only with OnStaticFails, not on static)
        public const int F_NAV = 1 << 7;            // navmesh test passes

        public const int R_NONE = 0, R_OFFMAP = 1, R_GROUND_TERRAIN = 2, R_GROUND_STATIC = 3,
                         R_ON_STATIC = 4, R_OVERLAP = 5, R_NAV = 6, R_NO_DATA = 7;

        public static readonly string[] ReasonNames =
            { "ok", "offmap", "ground_terrain", "ground_static", "on_static", "overlap", "navmesh", "no_data" };

        public static int Reason(int code) => (code & REASON_MASK) >> REASON_SHIFT;
    }

    /// <summary>Open-ground cell byte (data/sites/open_*.png).</summary>
    public static class OpenCode
    {
        public const int V_OK = 1 << 0;        // ground vehicle can stand + drive away
        public const int A_OK = 1 << 1;        // alien ground unit can stand + move away
        public const int GROUND = 1 << 2;      // the vehicle cast found ground at all
        public const int ON_STATIC = 1 << 3;   // landing surface is non-terrain > 4 m above the heightmap
        public const int V_OVERLAP = 1 << 4;   // vehicle box overlaps static geometry (both yaws)
        public const int V_NAV = 1 << 5;       // vehicle navmesh failed (off mesh or not main component)
        public const int A_NAV = 1 << 6;       // alien navmesh failed
        public const int A_OVERLAP = 1 << 7;   // alien box overlaps static geometry
    }

    public sealed class SiteResult
    {
        public int Code;
        public float SurfaceY, FinalY, AdjustY;
        public Quaternion Rotation;
        public string Blocker = "";
        public string Detail = "";
        public int NavProbesOk;
        public float GroundGap, TerrainGap, OverlapDepth;
        public string OnStaticInfo = "";
        public string GroundInfo = "";
        public bool TerrainRule, GameRule, GroundFail, OnStatic, Overlap, Nav, OffMap;
        public bool Valid => (Code & SiteCode.VALID) != 0;
        public string ReasonName => SiteCode.ReasonNames[SiteCode.Reason(Code)];
    }

    /// <summary>Everything about one base structure type that the site test needs, resolved once per map.</summary>
    public sealed class SiteStructure
    {
        public string Key = "";             // Sol | Cent | Alien
        public Team? Team;
        public ObjectInfo? Info;
        public GameObject? Prefab;
        public ConstructionData? Data;      // null = no game rule
        public ConstructionPreview? Preview;
        public float SnapY = 1f;
        public readonly List<Vector3> Above = new List<Vector3>();
        public readonly List<Vector3> Below = new List<Vector3>();
        public readonly List<(List<Vector3> a, List<Vector3> b, int mustA, int mustB)> Entries =
            new List<(List<Vector3>, List<Vector3>, int, int)>();
        public readonly List<(Vector3 c, Vector3 h)> Boxes = new List<(Vector3, Vector3)>();   // structure frame
        public readonly List<string> BoxNames = new List<string>();
        public float ClipLocalY = -1000f;
        public float FootMinX, FootMaxX, FootMinZ, FootMaxZ;   // footprint rectangle, structure frame
        public GraphMask NavMask = GraphMask.everything;
        public string NavGraph = "";
        public GraphNode? MainRef;      // a node of the main component (areas get renumbered; see SiteTest.MainRef)
        public uint? MainArea { get { try { return MainRef != null ? MainRef.Area : (uint?)null; } catch { return null; } } }
        public float NavSlack;
        public string Describe = "";
        public Bounds Union; public bool UnionAny, UnionReady;
        /// <summary>false for the Nest: 11 of 266 vanilla Nest placements break the build rule (tilt from a 1 m
        /// ray triangle on rock, ray misses, foundation gaps 8-35 m) and vanilla spawns them upright anyway; the
        /// ground result is then recorded (flags, GroundInfo) but is not a failure.</summary>
        public bool GroundFails = true;
    }

    public sealed class GroundProbe
    {
        public string Name = "";
        public Vector3 Half;        // half extents incl. margin
        public GraphMask Mask = GraphMask.everything;
        public string Graph = "";
        public GraphNode? MainRef;      // a node of the main component (areas get renumbered; see SiteTest.MainRef)
        public uint? MainArea { get { try { return MainRef != null ? MainRef.Area : (uint?)null; } catch { return null; } } }
        public float Slack;
        public string Source = "";
    }

    internal static class SiteTest
    {
        public const float ON_STATIC_MAX_M = 4f;
        public const float CLIP_ABOVE_LOWEST_POINT_M = 0.5f;
        public const float NAV_MARGIN_M = 12f;
        public const int NAV_MIN_PROBES = 2;
        public const float NAV_MAX_H_M = 6f;
        public const float NAV_MAX_V_M = 4f;
        public const float NAV_SEARCH_M = 20f;
        public const float SURFACE_RAY_UP_M = 500f;

        static bool s_Masks;
        public static int CastMask, StaticMask, WallMask, TerrainLayer = -1;
        static readonly Collider[] s_Buf = new Collider[32];
        static readonly Dictionary<int, bool> s_IsGameObjectCol = new Dictionary<int, bool>();

        public static void EnsureMasks()
        {
            if (s_Masks) return;
            CastMask = GamePhysics.TRACELAYERMASK_IGNOREDYNAMIC;
            int terrain = GamePhysics.TRACELAYERMASK_TERRAIN;
            int nonBlocking = LayerMask.GetMask("Water", "No Collide", "Gizmos", "UI", "AIVehicleObstacle");
            StaticMask = CastMask & ~terrain & ~nonBlocking;
            WallMask = LayerMask.GetMask("Invisible Walls");
            TerrainLayer = GamePhysics.LAYER_TERRAIN;
            s_Masks = true;
        }

        public static void ResetPerMap() { s_IsGameObjectCol.Clear(); }

        // ------------------------------------------------------------------ helpers

        public static float? TerrainHeight(float x, float z)
        {
            foreach (var t in Terrain.activeTerrains)
            {
                if (t == null || t.terrainData == null) continue;
                var p = t.transform.position; var s = t.terrainData.size;
                if (x >= p.x && x <= p.x + s.x && z >= p.z && z <= p.z + s.z)
                    return t.SampleHeight(new Vector3(x, 0f, z)) + p.y;
            }
            return null;
        }

        /// <summary>Top surface at x/z (first hit of a ray from far above, IGNOREDYNAMIC, triggers ignored).</summary>
        public static bool Surface(float x, float z, float refY, out RaycastHit hit)
        {
            EnsureMasks();
            Vector3 o = new Vector3(x, refY + SURFACE_RAY_UP_M, z);
            return Physics.Raycast(o, Vector3.down, out hit, SURFACE_RAY_UP_M * 2f, CastMask, QueryTriggerInteraction.Ignore);
        }

        public static bool IsGameObjectCollider(Collider c)
        {
            int id = c.GetInstanceID();
            if (s_IsGameObjectCol.TryGetValue(id, out bool v)) return v;
            try { v = c.GetComponentInParent<BaseGameObject>() != null; } catch { v = false; }
            s_IsGameObjectCol[id] = v;
            return v;
        }

        public static string DescribeCollider(Collider? c)
        {
            if (c == null) return "?";
            string layer = "";
            try { layer = LayerMask.LayerToName(c.gameObject.layer); } catch { }
            if (c.gameObject.layer == TerrainLayer) return "terrain";
            string path = c.name;
            try { var p = c.transform.parent; if (p != null) path = p.name + "/" + c.name; } catch { }
            return $"'{path}' [{layer}]";
        }

        static int s_SolidDeny = -1;
        public static bool IsSolidLayer(int layer)
        {
            if (s_SolidDeny == -1)
                s_SolidDeny = LayerMask.GetMask("Ignore Raycast", "Ignore Raycast Avoidance", "Single Object Raycast", "Trigger",
                    "AIObstacle", "Zone", "CharacterHitZone", "Debris", "CollideWithStatic", "Damage Bounds", "Water", "SelfOnly",
                    "SelfOnly Avoidance", "Vehicle Avoidance", "Invisible Walls Avoidance", "No Collide", "UI", "Gizmos");
            return ((1 << layer) & s_SolidDeny) == 0;
        }

        /// <summary>One box per solid collider of the prefab, as an axis-aligned box in the prefab root's frame
        /// (from the collider shape: Collider.bounds is not valid on a prefab asset).</summary>
        public static List<(Vector3 c, Vector3 h, string name)> PrefabColliderBoxes(GameObject prefab)
        {
            var list = new List<(Vector3, Vector3, string)>();
            Transform root = prefab.transform;
            foreach (var col in prefab.GetComponentsInChildren<Collider>(true))
            {
                if (col == null || col.isTrigger || !IsSolidLayer(col.gameObject.layer)) continue;
                Vector3 lc, le;
                if (col is BoxCollider bc) { lc = bc.center; le = bc.size * 0.5f; }
                else if (col is SphereCollider sc) { lc = sc.center; le = Vector3.one * sc.radius; }
                else if (col is CapsuleCollider cc)
                {
                    lc = cc.center; le = Vector3.one * cc.radius;
                    float hh = Mathf.Max(cc.height * 0.5f, cc.radius);
                    if (cc.direction == 0) le.x = hh; else if (cc.direction == 1) le.y = hh; else le.z = hh;
                }
                else if (col is MeshCollider mc && mc.sharedMesh != null) { lc = mc.sharedMesh.bounds.center; le = mc.sharedMesh.bounds.extents; }
                else continue;
                Transform t = col.transform;
                bool any = false; Bounds b = default;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = lc + new Vector3((i & 1) == 0 ? -le.x : le.x, (i & 2) == 0 ? -le.y : le.y, (i & 4) == 0 ? -le.z : le.z);
                    Vector3 p = root.InverseTransformPoint(t.TransformPoint(corner));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
                if (any) list.Add((b.center, b.extents, col.GetType().Name + ":" + col.name));
            }
            return list;
        }

        // ------------------------------------------------------------------ navmesh

        public static bool NavReady(out string state)
        {
            try
            {
                if (AstarPath.active == null) { state = "no AstarPath"; return false; }
                var rs = GameAI.ReloadState;
                state = rs.ToString();
                return rs == ENavmeshReloadState.Ready;
            }
            catch (Exception ex) { state = ex.GetType().Name; return false; }
        }

        public static NNInfo Nearest(Vector3 p, GraphMask mask, float maxDist)
        {
            var c = new NearestNodeConstraint { graphMask = mask, area = -1, tags = -1, maxDistance = maxDist };
            return AstarPath.active.GetNearest(p, c);
        }

        static readonly Dictionary<int, (GraphNode? node, string info)> s_MainRef = new Dictionary<int, (GraphNode?, string)>();

        public static void ClearNavCache() => s_MainRef.Clear();

        /// <summary>Main connected component of the graphs in the mask (the GraphNode.Area with the most walkable
        /// nodes), returned as one REFERENCE NODE of it. A* renumbers areas after graph updates (seen on
        /// CrystalChasm: the same component as area 13955, 13957, 13959 within a minute), so callers compare a
        /// probe node's Area with the reference node's Area read at the same moment, never with a stored id.</summary>
        public static GraphNode? MainRef(GraphMask mask, out string info)
        {
            if (s_MainRef.TryGetValue(mask.value, out var cached)) { info = cached.info; return cached.node; }
            var counts = new Dictionary<uint, int>();
            var sample = new Dictionary<uint, GraphNode>();
            int total = 0;
            try
            {
                var graphs = AstarPath.active.data.graphs;
                for (int i = 0; i < graphs.Length && i < 32; i++)
                {
                    var g = graphs[i];
                    if (g == null || ((mask.value >> i) & 1) == 0) continue;
                    g.GetNodes(n =>
                    {
                        if (n == null || !n.Walkable) return;
                        uint a = n.Area;
                        counts.TryGetValue(a, out int c);
                        counts[a] = c + 1;
                        if (!sample.ContainsKey(a)) sample[a] = n;
                        total++;
                    });
                }
            }
            catch (Exception ex) { info = "node walk failed: " + ex.Message; s_MainRef[mask.value] = (null, info); return null; }
            var top = counts.OrderByDescending(kv => kv.Value).Take(4).ToList();
            GraphNode? main = top.Count > 0 ? sample[top[0].Key] : null;
            info = $"{total} walkable nodes, {counts.Count} components, top: " +
                   string.Join(", ", top.Select(kv => $"area {kv.Key}={kv.Value}"));
            s_MainRef[mask.value] = (main, info);
            return main;
        }

        public static string GraphName(GraphMask mask)
        {
            try
            {
                var named = GameAI.NamedGraphMasks;
                if (named != null)
                    foreach (var kv in named)
                        if (kv.Value.value == mask.value) return kv.Key;
            }
            catch { }
            return mask.containsAllGraphs ? "all graphs" : "mask " + mask.value;
        }

        public static bool TryNamedGraph(string name, out GraphMask mask)
        {
            mask = GraphMask.everything;
            try
            {
                var named = GameAI.NamedGraphMasks;
                if (named != null && named.TryGetValue(name, out var m)) { mask = m; return true; }
            }
            catch { }
            return false;
        }

        /// <summary>Snap slack of point graphs (alien VoxelGraph: nodes are voxel centres): VoxelSize + |SurfaceOffset|,
        /// read by reflection as in Si_StarterUnits. 0 for navmesh graphs.</summary>
        public static float GraphSlack(GraphMask mask)
        {
            float slack = 0f;
            try
            {
                var graphs = AstarPath.active != null ? AstarPath.active.data.graphs : null;
                if (graphs == null) return 0f;
                for (int i = 0; i < graphs.Length && i < 32; i++)
                {
                    var g = graphs[i];
                    if (g == null || ((mask.value >> i) & 1) == 0) continue;
                    if (g.GetType().Name != "VoxelGraph") continue;
                    object? cf = g.GetType().GetField("Config")?.GetValue(g);
                    if (cf == null) continue;
                    float vs = Convert.ToSingle(cf.GetType().GetField("VoxelSize")?.GetValue(cf) ?? 0f);
                    float so = Convert.ToSingle(cf.GetType().GetField("SurfaceOffset")?.GetValue(cf) ?? 0f);
                    if (vs > 0f && vs < 50f) slack = Mathf.Max(slack, vs + Mathf.Abs(so));
                }
            }
            catch { }
            return slack;
        }

        /// <summary>Probe point on the navmesh in the main component? groundY = surface height at the probe.</summary>
        public static bool NavProbe(Vector3 groundPt, GraphMask mask, uint? mainArea, float slack, out string why)
        {
            why = "";
            NNInfo nn = Nearest(groundPt, mask, NAV_SEARCH_M);
            if (nn.node == null) { why = "no node"; return false; }
            float dx = nn.position.x - groundPt.x, dz = nn.position.z - groundPt.z;
            float dxz = Mathf.Sqrt(dx * dx + dz * dz), dy = Mathf.Abs(nn.position.y - groundPt.y);
            if (dxz > NAV_MAX_H_M + slack || dy > NAV_MAX_V_M + slack) { why = $"node {dxz:F0}m/{dy:F0}m away"; return false; }
            if (mainArea.HasValue)
            {
                uint a = mainArea.Value;
                try { a = nn.node.Area; } catch { }
                if (a != mainArea.Value) { why = $"area {a}"; return false; }
            }
            return true;
        }

        // ------------------------------------------------------------------ structure setup

        /// <summary>Resolves the base structure of the team whose name matches <paramref name="key"/> (Sol, Cent, Alien).</summary>
        public static SiteStructure? Resolve(string key, MelonLogger.Instance log)
        {
            EnsureMasks();
            Team? team = null;
            foreach (var t in Team.Teams)
            {
                if (t == null) continue;
                string n = t.TeamName ?? "";
                if (key == "Sol" && n.Contains("Sol")) { team = t; break; }
                if (key == "Cent" && n.Contains("Cent")) { team = t; break; }
                if (key == "Alien" && n.Contains("Alien") && !n.Contains("Worm")) { team = t; break; }
            }
            if (team == null) { log.Warning($"[SITE] {key}: team not found"); return null; }
            ObjectInfo? info = null;
            try { info = team.BaseStructure; } catch { }
            if (info == null || info.Prefab == null) { log.Warning($"[SITE] {key}: team {team.TeamName} has no BaseStructure prefab"); return null; }

            var s = new SiteStructure { Key = key, Team = team, Info = info, Prefab = info.Prefab, GroundFails = key != "Alien" };

            // ConstructionData whose ObjectInfo is the base structure = the game's placement rule for it
            foreach (var cd in Resources.FindObjectsOfTypeAll<ConstructionData>())
            {
                if (cd == null || cd.ObjectInfo != info) continue;
                ConstructionPreview? pv = null;
                try { pv = cd.ObjectPreviewSetup; } catch (Exception ex) { log.Warning($"[SITE] {key}: {cd.name}.ObjectPreviewSetup threw {ex.Message}"); }
                if (pv == null) continue;
                s.Data = cd; s.Preview = pv;
                break;
            }
            if (s.Preview != null)
            {
                s.SnapY = s.Data!.GridSnapY > 0f ? s.Data.GridSnapY : 1f;
                foreach (var t in s.Preview.AboveGroundPoints) if (t != null) s.Above.Add(t.localPosition);
                foreach (var t in s.Preview.BelowGroundPoints) if (t != null) s.Below.Add(t.localPosition);
                foreach (var e in s.Preview.PreviewEntries)
                {
                    if (e == null) continue;
                    s.Entries.Add((e.AboveGroundPoints.Where(t => t != null).Select(t => t.localPosition).ToList(),
                                   e.BelowGroundPoints.Where(t => t != null).Select(t => t.localPosition).ToList(),
                                   e.MustBeAboveGroundCount, e.MustBeBelowGroundCount));
                }
            }

            // collision boxes
            var boxes = PrefabColliderBoxes(s.Prefab);
            Bounds union = default; bool any = false;
            foreach (var b in boxes)
            {
                s.Boxes.Add((b.c, b.h)); s.BoxNames.Add(b.name);
                var bb = new Bounds(b.c, b.h * 2f);
                if (!any) { union = bb; any = true; } else union.Encapsulate(bb);
            }
            float minAboveY = s.Above.Count > 0 ? s.Above.Min(p => p.y) : (any ? union.min.y : 0f);
            s.ClipLocalY = minAboveY + CLIP_ABOVE_LOWEST_POINT_M;

            // footprint: AboveGround points extent united with the collider boxes (x/z)
            float fx0 = float.MaxValue, fx1 = float.MinValue, fz0 = float.MaxValue, fz1 = float.MinValue;
            foreach (var p in s.Above) { fx0 = Mathf.Min(fx0, p.x); fx1 = Mathf.Max(fx1, p.x); fz0 = Mathf.Min(fz0, p.z); fz1 = Mathf.Max(fz1, p.z); }
            if (any) { fx0 = Mathf.Min(fx0, union.min.x); fx1 = Mathf.Max(fx1, union.max.x); fz0 = Mathf.Min(fz0, union.min.z); fz1 = Mathf.Max(fz1, union.max.z); }
            if (fx0 > fx1) { var e = info.PhysicalBounds; fx0 = e.min.x; fx1 = e.max.x; fz0 = e.min.z; fz1 = e.max.z; }
            s.FootMinX = fx0; s.FootMaxX = fx1; s.FootMinZ = fz0; s.FootMaxZ = fz1;

            // navmesh graph of the site test
            string want = key == "Alien" ? "" : "Small Vehicle";
            if (want.Length > 0 && TryNamedGraph(want, out var m)) { s.NavMask = m; s.NavGraph = want; }
            else if (key == "Alien")
            {
                var g = AlienGroundGraph(log);
                s.NavMask = g.mask; s.NavGraph = g.name;
            }
            s.MainRef = MainRef(s.NavMask, out string navInfo);
            s.NavSlack = GraphSlack(s.NavMask);

            var pb = info.PhysicalBounds;
            s.Describe = $"{key}: '{info.DisplayName}' prefab {s.Prefab.name}, " +
                         (s.Data != null ? $"rule {s.Data.name} (snap XZ {s.Data.GridSnapXZ} Y {s.Data.GridSnapY}, rotateToSurface {s.Preview!.StructureRotateToSurface} max {s.Preview.StructureRotateMaxAngle}, above {s.Above.Count}, below {s.Below.Count}, entries {s.Entries.Count})"
                                         : "NO ConstructionData (no game rule)") +
                         $", {s.Boxes.Count} collider boxes, union {(any ? $"{union.size.x:F1}x{union.size.y:F1}x{union.size.z:F1} c({union.center.x:F1},{union.center.y:F1},{union.center.z:F1})" : "-")}" +
                         $", PhysicalBounds {pb.size.x:F1}x{pb.size.y:F1}x{pb.size.z:F1} c({pb.center.x:F1},{pb.center.y:F1},{pb.center.z:F1})" +
                         $", footprint x[{fx0:F1},{fx1:F1}] z[{fz0:F1},{fz1:F1}], clip y>{s.ClipLocalY:F2}" +
                         $", nav graph '{s.NavGraph}' slack {s.NavSlack:F1} main area {(s.MainArea?.ToString() ?? "-")} ({navInfo})";
            log.Msg("[SITE] " + s.Describe);
            if (boxes.Count > 0)
                log.Msg($"[SITE] {key} boxes: " + string.Join("; ", boxes.Select(b => $"{b.name} c({b.c.x:F1},{b.c.y:F1},{b.c.z:F1}) h({b.h.x:F1},{b.h.y:F1},{b.h.z:F1})")));
            return s;
        }

        /// <summary>The alien ground graph: a VoxelGraph-type graph or a name with "Voxel"/"Alien"/"Creature".</summary>
        public static (GraphMask mask, string name) AlienGroundGraph(MelonLogger.Instance log)
        {
            try
            {
                var graphs = AstarPath.active.data.graphs;
                for (int i = 0; i < graphs.Length && i < 32; i++)
                {
                    var g = graphs[i];
                    if (g == null) continue;
                    string tn = g.GetType().Name, n = g.name ?? "";
                    if (tn == "VoxelGraph" || n.IndexOf("Voxel", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("Alien", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("Creature", StringComparison.OrdinalIgnoreCase) >= 0)
                        return (GraphMask.FromGraphIndex((uint)i), n);
                }
            }
            catch (Exception ex) { log.Warning("[SITE] alien graph lookup: " + ex.Message); }
            return (GraphMask.everything, "all graphs");
        }

        public static void LogGraphs(MelonLogger.Instance log)
        {
            try
            {
                var graphs = AstarPath.active.data.graphs;
                for (int i = 0; i < graphs.Length; i++)
                {
                    var g = graphs[i];
                    if (g == null) { log.Msg($"[SITE] graph {i}: null"); continue; }
                    int n = 0; g.GetNodes(_ => n++);
                    log.Msg($"[SITE] graph {i}: '{g.name}' type {g.GetType().FullName}, {n} nodes, slack {GraphSlack(GraphMask.FromGraphIndex((uint)i)):F1}");
                }
            }
            catch (Exception ex) { log.Warning("[SITE] graph listing failed: " + ex.Message); }
        }

        // ------------------------------------------------------------------ the site test

        static Vector3 L2W(Vector3 pos, Quaternion rot, Vector3 local) => pos + rot * local;

        static bool OnTerrainBounds(float x, float z) => TerrainHeight(x, z).HasValue;

        public struct GroundEval
        {
            public bool Miss, Tilt, EntriesOk;
            public float Adjust, Gap;          // Gap = how far the worst BelowGround point sticks out above the ground
            public Collider? HighStatic; public float HighStaticH;
            public bool GameOk => !Miss && !Tilt && EntriesOk && Gap <= 0f;
            public bool OkWithin(float tol) => !Miss && !Tilt && EntriesOk && Gap <= tol;
        }

        /// <summary>Replica of ConstructionPreview.GetGroundSlopeClear that also returns the gap: AboveGround points
        /// raycast from +100 m down 200 m (IGNOREDYNAMIC), adjust = max(hit.y - point.y) (start -30), then every
        /// BelowGround point must have ground at or above it; Gap = max(point.y - hit.y) over them. physics=false
        /// uses the heightmap instead of rays (the legacy hq_scan rule; entries counted as in hq_scan).</summary>
        public static GroundEval Ground(SiteStructure s, Vector3 pos, Quaternion rot, bool physics)
        {
            var g = new GroundEval { Adjust = -30f, Gap = float.NegativeInfinity, EntriesOk = true };
            if (s.Preview == null) { g.Adjust = 0f; g.Gap = 0f; return g; }
            if (physics && s.Preview.StructureRotateToSurface && s.Preview.StructureRotateMaxAngle > 0f)
            {
                Vector3 e = rot.eulerAngles;
                float ex = e.x > 180f ? e.x - 360f : e.x, ez = e.z > 180f ? e.z - 360f : e.z;
                if (Mathf.Abs(ex) > s.Preview.StructureRotateMaxAngle || Mathf.Abs(ez) > s.Preview.StructureRotateMaxAngle) g.Tilt = true;
            }
            foreach (var lp in s.Above)
            {
                Vector3 w = L2W(pos, rot, lp);
                float? gy = null;
                if (physics)
                {
                    if (Physics.Raycast(w + Vector3.up * 100f, Vector3.down, out RaycastHit h, 200f, CastMask, QueryTriggerInteraction.Ignore))
                    {
                        gy = h.point.y;
                        if (h.collider != null && h.collider.gameObject.layer != TerrainLayer)
                        {
                            float? th = TerrainHeight(w.x, w.z);
                            float above = h.point.y - (th ?? h.point.y);
                            if (above > g.HighStaticH || g.HighStatic == null) { g.HighStaticH = above; g.HighStatic = h.collider; }
                        }
                    }
                }
                else gy = TerrainHeight(w.x, w.z);
                if (!gy.HasValue) { g.Miss = true; continue; }
                g.Adjust = Mathf.Max(g.Adjust, gy.Value - w.y);
            }
            Vector3 fin = pos + Vector3.up * g.Adjust;
            foreach (var lp in s.Below)
            {
                Vector3 w = L2W(fin, rot, lp);
                float? gy = null;
                if (physics)
                {
                    if (Physics.Raycast(w + Vector3.up * 100f, Vector3.down, out RaycastHit h, 200f, CastMask, QueryTriggerInteraction.Ignore))
                        gy = h.point.y;
                }
                else gy = TerrainHeight(w.x, w.z);
                if (!gy.HasValue) { g.Miss = true; continue; }
                g.Gap = Mathf.Max(g.Gap, w.y - gy.Value);
            }
            if (s.Below.Count == 0) g.Gap = 0f;
            if (physics)
            {
                try { foreach (var e in s.Preview.PreviewEntries) if (e != null && !e.Evaluate(fin, rot)) { g.EntriesOk = false; break; } }
                catch { }
            }
            else
            {
                foreach (var e in s.Entries)
                {
                    int a = 0, b = 0;
                    foreach (var lp in e.a) { Vector3 w = L2W(fin, rot, lp); if (OnTerrainBounds(w.x, w.z)) a++; }
                    foreach (var lp in e.b) { Vector3 w = L2W(fin, rot, lp); float? th = TerrainHeight(w.x, w.z); if (th.HasValue && th.Value >= w.y) b++; }
                    if (a < e.mustA || b < e.mustB) { g.EntriesOk = false; break; }
                }
            }
            return g;
        }

        // Thresholds (site_scan.json may override them for calibration runs; the runtime check uses these values)
        public static float GroundTol = 8f;        // m a BelowGround point may stick out above the ground (vanilla p95)
        public static float OverlapDepth = 3f;     // m of penetration into a static collider that counts (vanilla p97)
        public static float OnStaticMax = ON_STATIC_MAX_M;
        public static bool OnStaticFails = false;   // standing on static geometry is a failure (off: info only)
        public static int ReplicaMismatch, ReplicaChecks;
        /// <summary>true = bit 5 from the real GetGroundSlopeClear call (and the replica is checked against it);
        /// false (grid scan) = bit 5 from the replica, half the raycasts.</summary>
        public static bool VerifyGameRule = true;

        /// <summary>
        /// Full site test at world x/z with the given yaw (degrees, any value), x/z used as given (the scan grid is
        /// on the HQ GridSnapXZ already). <paramref name="full"/> = evaluate every test even after a failure (flag
        /// bits complete); false = stop at the first failure.
        /// </summary>
        public static SiteResult Test(SiteStructure s, float x, float z, float yawDeg, bool full = true)
        {
            EnsureMasks();
            var r = new SiteResult();
            int reason = SiteCode.R_NONE;
            void Fail(int code) { if (reason == SiteCode.R_NONE) reason = code; }

            float? th0 = TerrainHeight(x, z);
            bool offMap = !th0.HasValue;
            try { var fow = FogOfWar.Instance; if (fow != null && !fow.Bounds.Contains(new Vector3(x, fow.Bounds.center.y, z))) offMap = true; } catch { }
            r.OffMap = offMap;
            if (offMap) { r.Code = (SiteCode.R_OFFMAP << SiteCode.REASON_SHIFT); r.Detail = "outside the map"; return r; }

            float refY = th0!.Value;
            float surfY = refY;
            if (Surface(x, z, refY, out RaycastHit sh)) surfY = sh.point.y;
            r.SurfaceY = surfY;

            // Normalize: start points report yaws like 359.99; Quaternion.Euler(0, 359.99, 0) ~ (0,0,0,-1) gave a
            // 0 m penetration where yaw 0 (same rotation) gives 3.6 m (Citadel, root cause not found). Snap to a
            // multiple of 90 within 0.05 deg and keep w >= 0 so the runtime check matches the scan grid.
            yawDeg %= 360f; if (yawDeg < 0f) yawDeg += 360f;
            float snap90 = Mathf.Round(yawDeg / 90f) * 90f;
            if (Mathf.Abs(yawDeg - snap90) < 0.05f) yawDeg = snap90 % 360f;
            Quaternion yaw = Quaternion.Euler(0f, yawDeg, 0f);
            if (yaw.w < 0f) yaw = new Quaternion(-yaw.x, -yaw.y, -yaw.z, -yaw.w);
            Vector3 snapped = new Vector3(x, surfY, z);
            Quaternion rot = yaw;
            if (s.Data != null)
            {
                try { snapped = s.Data.SnapToGrid(snapped); snapped.x = x; snapped.z = z; } catch { snapped.y = Mathf.Round(surfY / s.SnapY) * s.SnapY; }
                try { rot = s.Preview!.GetSnappedRotation(snapped, yaw, s.SnapY); } catch { rot = yaw; }
            }
            r.Rotation = rot;

            // 2. ground: the game rule (exact call, F_GAME_RULE) and its replica with the gap (validity uses GroundTol)
            var gp = Ground(s, snapped, rot, true);
            var gt = Ground(s, snapped, yaw, false);
            r.TerrainRule = gt.GameOk;
            if (s.Preview != null && !VerifyGameRule) r.GameRule = gp.GameOk;
            else if (s.Preview != null)
            {
                try { r.GameRule = s.Preview.GetGroundSlopeClear(snapped, rot, out float adjustGame); }
                catch (Exception ex) { r.GameRule = gp.GameOk; r.Detail = "GetGroundSlopeClear threw " + ex.Message; }
                ReplicaChecks++;
                if (r.GameRule != gp.GameOk && ++ReplicaMismatch <= 5)
                    Melon<MapBalance>.Logger.Msg($"[SITE] replica mismatch {s.Key} ({x:F0},{z:F0}) yaw {yawDeg:F0}: game {r.GameRule}, replica miss {gp.Miss} tilt {gp.Tilt} entries {gp.EntriesOk} gap {gp.Gap:F3} adjust {gp.Adjust:F2}");
            }
            else r.GameRule = true;
            float adjustY = s.Preview != null ? gp.Adjust : 0f;
            if (adjustY < -30f || adjustY > 200f) adjustY = 0f;
            r.AdjustY = adjustY;
            r.GroundGap = s.Preview != null && !float.IsNegativeInfinity(gp.Gap) ? gp.Gap : 0f;
            r.TerrainGap = s.Preview != null && !float.IsNegativeInfinity(gt.Gap) ? gt.Gap : 0f;
            Vector3 pos = snapped + Vector3.up * adjustY;
            r.FinalY = pos.y;
            if (!gp.OkWithin(GroundTol))
            {
                r.GroundFail = true;
                bool terrainToo = !gt.OkWithin(GroundTol);
                string why = gp.Miss ? "ground ray missed" : gp.Tilt ? "slope above the max tilt" : !gp.EntriesOk ? "preview entry failed" : $"foundation gap {r.GroundGap:F1} m (terrain-only {r.TerrainGap:F1} m)";
                if (s.GroundFails)
                {
                    Fail(terrainToo ? SiteCode.R_GROUND_TERRAIN : SiteCode.R_GROUND_STATIC);
                    if (!terrainToo && gp.HighStatic != null && r.Blocker.Length == 0) r.Blocker = DescribeCollider(gp.HighStatic) + $" ({gp.HighStaticH:F0} m above terrain)";
                    if (r.Detail.Length == 0) r.Detail = why;
                    if (!full) return Finish(r, reason);
                }
                else r.GroundInfo = why;   // Nest: the build rule does not apply to a spawned base (see SiteStructure.GroundFails)
            }

            // 3. on top of static geometry (roof / rock / cliff top)
            Collider? onCol = null; float onH = 0f;
            foreach (var lp in s.Above.Count > 0 ? s.Above : Corners(s))
            {
                Vector3 w = L2W(pos, rot, lp);
                if (!Physics.Raycast(w + Vector3.up * 100f, Vector3.down, out RaycastHit h, 200f, CastMask, QueryTriggerInteraction.Ignore)) continue;
                if (h.collider == null || h.collider.gameObject.layer == TerrainLayer || IsGameObjectCollider(h.collider)) continue;
                float? th = TerrainHeight(w.x, w.z);
                if (!th.HasValue) continue;
                float above = h.point.y - th.Value;
                if (above > OnStaticMax && above > onH) { onH = above; onCol = h.collider; }
            }
            if (onCol != null)
            {
                r.OnStatic = true;
                r.OnStaticInfo = DescribeCollider(onCol) + $" ({onH:F0} m above terrain)";
                // informational since calibration: Citadel plays on rock-mesh plateaus, IndustrialQuarter on floor
                // meshes; the navmesh main-component test decides. OnStaticFails restores the strict rule.
                if (OnStaticFails)
                {
                    Fail(SiteCode.R_ON_STATIC);
                    if (r.Blocker.Length == 0) r.Blocker = r.OnStaticInfo;
                    if (!full) return Finish(r, reason);
                }
            }

            // 4. static overlap of the collision boxes (penetration depth > OverlapDepth)
            Collider? ov = OverlapStatic(s, pos, rot, out int nHits, out float depth);
            r.OverlapDepth = depth;
            if (ov != null)
            {
                r.Overlap = true;
                Fail(SiteCode.R_OVERLAP);
                if (r.Blocker.Length == 0 || reason == SiteCode.R_OVERLAP) r.Blocker = DescribeCollider(ov) + $" ({depth:F1} m deep into {LastOverlapBox}" + (nHits > 1 ? $", {nHits} hits)" : ")");
                if (!full) return Finish(r, reason);
            }

            // 5. navmesh
            r.Nav = NavTest(s, pos, rot, out int ok, out string navWhy);
            r.NavProbesOk = ok;
            if (!r.Nav)
            {
                Fail(SiteCode.R_NAV);
                if (r.Detail.Length == 0) r.Detail = $"navmesh: {ok}/8 probes on main '{s.NavGraph}' ({navWhy})";
            }
            return Finish(r, reason);
        }

        static SiteResult Finish(SiteResult r, int reason)
        {
            int code = reason << SiteCode.REASON_SHIFT;
            if (reason == SiteCode.R_NONE) code |= SiteCode.VALID;
            if (r.TerrainRule) code |= SiteCode.F_TERRAIN_RULE;
            if (r.GameRule) code |= SiteCode.F_GAME_RULE;
            if (!r.Overlap && !(OnStaticFails && r.OnStatic)) code |= SiteCode.F_NO_OVERLAP;
            if (r.Nav) code |= SiteCode.F_NAV;
            r.Code = code;
            return r;
        }

        static IEnumerable<Vector3> Corners(SiteStructure s)
        {
            yield return new Vector3(s.FootMinX, 0f, s.FootMinZ);
            yield return new Vector3(s.FootMinX, 0f, s.FootMaxZ);
            yield return new Vector3(s.FootMaxX, 0f, s.FootMinZ);
            yield return new Vector3(s.FootMaxX, 0f, s.FootMaxZ);
            yield return Vector3.zero;
        }

        static GameObject? s_ProbeGo;
        static BoxCollider? s_Probe;

        /// <summary>Hidden trigger box (layer Ignore Raycast, 10 km below the map) used only as a shape for
        /// Physics.ComputePenetration with an explicit pose; recreated after a scene change.</summary>
        static BoxCollider? Probe()
        {
            if (s_Probe != null) return s_Probe;
            try
            {
                s_ProbeGo = new GameObject("Si_MapBalance_SiteProbe");
                s_ProbeGo.layer = LayerMask.NameToLayer("Ignore Raycast");
                s_ProbeGo.transform.position = new Vector3(0f, -10000f, 0f);
                s_Probe = s_ProbeGo.AddComponent<BoxCollider>();
                s_Probe.isTrigger = true;
            }
            catch { s_Probe = null; }
            return s_Probe;
        }

        /// <summary>Deepest static collider penetrating one of the structure's collision boxes (bottom clipped at the
        /// lowest AboveGround point + 0.5 m). Only penetrations deeper than OverlapDepth count. A first OverlapBox of
        /// the clipped union of all boxes skips the per-box queries where nothing static is near.</summary>
        public static string LastOverlapBox = "";
        public static Collider? OverlapStatic(SiteStructure s, Vector3 pos, Quaternion rot, out int hits, out float depth)
        {
            hits = 0; depth = 0f;
            Collider? worst = null;
            int mask = StaticMask | WallMask;
            if (!s.UnionReady)
            {
                bool any = false; Bounds u = default;
                foreach (var (c, h) in s.Boxes)
                {
                    float y0 = Mathf.Max(c.y - h.y, s.ClipLocalY), y1 = c.y + h.y;
                    if (y1 - y0 < 0.2f) continue;
                    var b = new Bounds(new Vector3(c.x, (y0 + y1) * 0.5f, c.z), new Vector3(h.x * 2f, y1 - y0, h.z * 2f));
                    if (!any) { u = b; any = true; } else u.Encapsulate(b);
                }
                s.Union = u; s.UnionAny = any; s.UnionReady = true;
            }
            if (!s.UnionAny) return null;
            int nu = Physics.OverlapBoxNonAlloc(pos + rot * s.Union.center, s.Union.extents, s_Buf, rot, mask, QueryTriggerInteraction.Ignore);
            bool near = false;
            for (int i = 0; i < nu && !near; i++)
            {
                var col = s_Buf[i];
                if (col != null && col.gameObject.layer != TerrainLayer && !IsGameObjectCollider(col)) near = true;
            }
            if (!near) return null;

            var probe = Probe();
            for (int bi = 0; bi < s.Boxes.Count; bi++)
            {
                var (c, h) = s.Boxes[bi];
                float y0 = Mathf.Max(c.y - h.y, s.ClipLocalY), y1 = c.y + h.y;
                if (y1 - y0 < 0.2f) continue;
                Vector3 lc = new Vector3(c.x, (y0 + y1) * 0.5f, c.z);
                Vector3 half = new Vector3(h.x, (y1 - y0) * 0.5f, h.z);
                Vector3 wc = pos + rot * lc;
                int n = Physics.OverlapBoxNonAlloc(wc, half, s_Buf, rot, mask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    var col = s_Buf[i];
                    if (col == null || col.gameObject.layer == TerrainLayer || IsGameObjectCollider(col)) continue;
                    float d = OverlapDepth + 1f;   // no probe: count the overlap
                    if (probe != null)
                    {
                        probe.size = half * 2f;
                        probe.center = Vector3.zero;
                        try
                        {
                            Transform ct = col.transform;
                            d = Physics.ComputePenetration(probe, wc, rot, col, ct.position, ct.rotation, out _, out float dist) ? dist : 0f;
                        }
                        catch { }
                    }
                    if (d <= OverlapDepth) continue;
                    hits++;
                    if (d > depth) { depth = d; worst = col; LastOverlapBox = bi < s.BoxNames.Count ? s.BoxNames[bi] : "?"; }
                    if (!VerifyGameRule) return worst;   // grid mode: the first deep penetration decides, depth is a lower bound
                }
            }
            return worst;
        }

        static readonly Vector3[] s_ProbeDirs = new Vector3[8];

        /// <summary>8 probes on the footprint rectangle grown by NAV_MARGIN_M; at least NAV_MIN_PROBES must be on
        /// the main component of the site graph.</summary>
        public static bool NavTest(SiteStructure s, Vector3 pos, Quaternion rot, out int ok, out string why)
        {
            ok = 0; why = "";
            if (!NavReady(out string st)) { why = "navmesh not ready: " + st; ok = -1; return true; }
            float x0 = s.FootMinX - NAV_MARGIN_M, x1 = s.FootMaxX + NAV_MARGIN_M, z0 = s.FootMinZ - NAV_MARGIN_M, z1 = s.FootMaxZ + NAV_MARGIN_M;
            float xm = (x0 + x1) * 0.5f, zm = (z0 + z1) * 0.5f;
            s_ProbeDirs[0] = new Vector3(x0, 0, z0); s_ProbeDirs[1] = new Vector3(xm, 0, z0); s_ProbeDirs[2] = new Vector3(x1, 0, z0); s_ProbeDirs[3] = new Vector3(x1, 0, zm);
            s_ProbeDirs[4] = new Vector3(x1, 0, z1); s_ProbeDirs[5] = new Vector3(xm, 0, z1); s_ProbeDirs[6] = new Vector3(x0, 0, z1); s_ProbeDirs[7] = new Vector3(x0, 0, zm);
            var whys = new List<string>(8);
            foreach (var lp in s_ProbeDirs)
            {
                Vector3 w = pos + rot * lp;
                float? th = TerrainHeight(w.x, w.z);
                if (!th.HasValue) { whys.Add("offmap"); continue; }
                float gy = th.Value;
                if (Surface(w.x, w.z, Mathf.Max(gy, pos.y), out RaycastHit h)) gy = h.point.y;
                if (NavProbe(new Vector3(w.x, gy, w.z), s.NavMask, s.MainArea, s.NavSlack, out string pw)) ok++;
                else whys.Add(pw);
            }
            why = string.Join(",", whys.Distinct().Take(4));
            return ok >= NAV_MIN_PROBES;
        }

        // ------------------------------------------------------------------ open ground

        /// <summary>Can a unit with box <paramref name="p"/> stand at x/z and move away? Returns the OpenCode bits for
        /// this unit class (ok / overlap / nav), plus ground + on-static.</summary>
        public static int OpenCell(GroundProbe p, float x, float z, bool vehicle, out float groundY)
        {
            groundY = float.NaN;
            float? th = TerrainHeight(x, z);
            if (!th.HasValue) return 0;
            int bits = 0;
            Vector3 half = p.Half;
            float top = th.Value + SURFACE_RAY_UP_M;
            Vector3 origin = new Vector3(x, top, z);
            if (Physics.CheckBox(origin, half, Quaternion.identity, CastMask, QueryTriggerInteraction.Ignore)) origin.y += SURFACE_RAY_UP_M;
            if (!Physics.BoxCast(origin, half, Vector3.down, out RaycastHit hit, Quaternion.identity, origin.y - th.Value + 30f, CastMask, QueryTriggerInteraction.Ignore))
                return bits;
            bits |= OpenCode.GROUND;
            Vector3 center = origin + Vector3.down * hit.distance;
            float gy = center.y - half.y;
            groundY = gy;
            bool onStatic = hit.collider != null && hit.collider.gameObject.layer != TerrainLayer && gy - th.Value > OnStaticMax;
            if (onStatic) bits |= OpenCode.ON_STATIC;

            // static overlap: axis-aligned and turned 90 degrees, either one clear is enough
            bool clear = false;
            foreach (var q in new[] { Quaternion.identity, Quaternion.Euler(0f, 90f, 0f) })
            {
                int n = Physics.OverlapBoxNonAlloc(center + Vector3.up * 0.15f, half, s_Buf, q, StaticMask | WallMask, QueryTriggerInteraction.Ignore);
                bool blocked = false;
                for (int i = 0; i < n && !blocked; i++)
                {
                    var c = s_Buf[i];
                    if (c == null || c.gameObject.layer == TerrainLayer || IsGameObjectCollider(c)) continue;
                    blocked = true;
                }
                if (!blocked) { clear = true; break; }
            }
            if (!clear) bits |= vehicle ? OpenCode.V_OVERLAP : OpenCode.A_OVERLAP;

            bool nav = true;
            if (NavReady(out _))
                nav = NavProbe(new Vector3(x, gy, z), p.Mask, p.MainArea, p.Slack, out _);
            if (!nav) bits |= vehicle ? OpenCode.V_NAV : OpenCode.A_NAV;

            if (clear && nav && !(OnStaticFails && onStatic)) bits |= vehicle ? OpenCode.V_OK : OpenCode.A_OK;
            return bits;
        }

        /// <summary>Reference unit box for the open-ground layer: the solid collider box of the first ObjectInfo with
        /// one of <paramref name="names"/> (DisplayName), + 1 m margin per side, else the fallback.</summary>
        public static GroundProbe MakeProbe(string label, string[] names, Vector3 fallbackHalf, GraphMask mask, string graph, MelonLogger.Instance log)
        {
            var p = new GroundProbe { Name = label, Mask = mask, Graph = graph, Half = fallbackHalf, Source = "fallback" };
            try
            {
                var infos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
                foreach (string want in names)
                {
                    var info = infos.FirstOrDefault(i => i != null && i.Prefab != null && string.Equals(i.DisplayName, want, StringComparison.OrdinalIgnoreCase));
                    if (info == null) continue;
                    var boxes = PrefabColliderBoxes(info.Prefab);
                    if (boxes.Count == 0) continue;
                    Bounds u = new Bounds(boxes[0].c, boxes[0].h * 2f);
                    foreach (var b in boxes) u.Encapsulate(new Bounds(b.c, b.h * 2f));
                    if (u.extents.x < 0.2f || u.extents.x > 30f || u.extents.z < 0.2f || u.extents.z > 30f) continue;
                    p.Half = new Vector3(u.extents.x + 1f, Mathf.Clamp(u.extents.y, 0.5f, 4f), u.extents.z + 1f);
                    p.Source = $"'{info.DisplayName}' {boxes.Count} colliders {u.size.x:F1}x{u.size.y:F1}x{u.size.z:F1} (+1 m per side)";
                    break;
                }
            }
            catch (Exception ex) { log.Warning("[SITE] probe " + label + ": " + ex.Message); }
            p.MainRef = MainRef(p.Mask, out string info2);
            p.Slack = GraphSlack(p.Mask);
            log.Msg($"[SITE] open-ground probe {label}: box {p.Half.x * 2f:F1}x{p.Half.y * 2f:F1}x{p.Half.z * 2f:F1} ({p.Source}), graph '{p.Graph}' slack {p.Slack:F1}, main area {p.MainArea?.ToString() ?? "-"} ({info2})");
            return p;
        }
    }
}
