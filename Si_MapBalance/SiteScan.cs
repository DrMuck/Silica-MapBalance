/*
 Si_MapBalance 3.6.0 - site scan mode + runtime base-site warning.

 Scan mode (UserData/cfg/MapBalance/site_scan.json, "enabled": true): on every map load the mod waits for the
 navmesh, scans the map if it is in "maps" and not done yet, then loads the next map of the list
 (NetworkGameServer.LoadLevel with the running game mode) until every map is done. No round, no players needed.
 Per map it writes to <output_dir> (default UserData/MapBalance/site_scan):
   site_scan_<Map>_<ts>.txt        grid scan: header + CSV x,z,sy,s000..s270,c000..c270,n000..n270,o,vy,ay
   site_startpoints_<Map>_<ts>.csv every vanilla start point tested with its own yaw and the 4 grid yaws
   site_layouts_<Map>_<ts>.csv     every base of every layout in <layouts_dir>/<Map>/ (+ subfolders) tested
 and optionally runs Si_StarterUnits' dry-run audit for the same bases (test points written to
 UserData/cfg/StarterUnits/audit/<Map>/ for the duration of the audit, removed afterwards).
 Progress: <output_dir>/state.json ({"done": [...]}) so a restart resumes.

 Runtime: SpawnAtBestStartPoint calls SiteCheckBase for every configured base (warning only, nothing moves).
*/

using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using GraphMask = Pathfinding.GraphMask;
using UnityEngine;

namespace Si_MapBalance
{
    public class SiteScanConfig
    {
        [JsonProperty("enabled")] public bool Enabled = false;
        [JsonProperty("maps")] public List<string> Maps = new List<string>();
        [JsonProperty("layouts_dir")] public string LayoutsDir = "";
        [JsonProperty("grid")] public bool Grid = true;
        [JsonProperty("layout_check")] public bool LayoutCheck = true;
        [JsonProperty("start_points")] public bool StartPoints = true;
        [JsonProperty("starter_audit")] public bool StarterAudit = false;   // optional Si_StarterUnits dry run (not needed)
        [JsonProperty("grid_step")] public float GridStep = 15f;
        [JsonProperty("frame_budget_ms")] public float FrameBudgetMs = 30f;
        [JsonProperty("output_dir")] public string OutputDir = "";
        [JsonProperty("vehicle_ref")] public List<string> VehicleRef = new List<string> { "Platoon Hauler", "Squad Transport" };
        [JsonProperty("alien_ref")] public List<string> AlienRef = new List<string> { "Shocker", "Squid" };
        [JsonProperty("settle_seconds")] public float SettleSeconds = 8f;
        // calibration overrides of the site-test thresholds (null = the built-in values the runtime check uses)
        [JsonProperty("ground_tol_m")] public float? GroundTol;
        [JsonProperty("overlap_depth_m")] public float? OverlapDepth;
        [JsonProperty("on_static_max_m")] public float? OnStaticMax;
        [JsonProperty("on_static_fails")] public bool? OnStaticFails;
    }

    public partial class MapBalance
    {
        internal const string ModVersion = "3.6.0";
        private const string SITE_SCAN_VERSION = "2";   // 2: Nest ground = info (v1 files are normalized by preprocess.py)
        private static int _siteScanSceneToken;
        private static bool _siteScanBusy;
        private static bool _siteLevelsLogged;

        private static string SiteScanConfigPath => Path.Combine("UserData", "cfg", "MapBalance", "site_scan.json");

        private static SiteScanConfig? LoadSiteScanConfig()
        {
            try
            {
                if (!File.Exists(SiteScanConfigPath)) return null;
                return JsonConvert.DeserializeObject<SiteScanConfig>(File.ReadAllText(SiteScanConfigPath));
            }
            catch (Exception ex)
            {
                Melon<MapBalance>.Logger.Warning($"[SITE] {SiteScanConfigPath} unreadable: {ex.Message}");
                return null;
            }
        }

        private static string SiteOutDir(SiteScanConfig cfg)
        {
            string d = string.IsNullOrWhiteSpace(cfg.OutputDir) ? Path.Combine("UserData", "MapBalance", "site_scan") : cfg.OutputDir;
            Directory.CreateDirectory(d);
            return d;
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            try
            {
                var cfg = LoadSiteScanConfig();
                if (cfg == null || !cfg.Enabled) return;
                int token = ++_siteScanSceneToken;
                MelonCoroutines.Start(SiteScanSceneRoutine(cfg, sceneName, token));
            }
            catch (Exception ex) { LoggerInstance.Warning("[SITE] scene hook: " + ex.Message); }
        }

        // ------------------------------------------------------------------ scan mode driver

        private static IEnumerator SiteScanSceneRoutine(SiteScanConfig cfg, string sceneName, int token)
        {
            var log = Melon<MapBalance>.Logger;
            // wait for a gameplay map: terrain + MP_Strategy + navmesh ready
            float waited = 0f;
            while (waited < 180f)
            {
                if (token != _siteScanSceneToken) yield break;
                bool ok = false;
                try { ok = Terrain.activeTerrain != null && GameMode.CurrentGameMode is MP_Strategy && SiteTest.NavReady(out _) && NetworkGameServer.GetServerFullyReady(); } catch { }
                if (ok) break;
                yield return new WaitForSeconds(1f);
                waited += 1f;
            }
            if (token != _siteScanSceneToken) yield break;
            if (waited >= 180f) { log.Warning($"[SITE] scan mode: scene '{sceneName}' never became a ready gameplay map"); yield break; }
            yield return new WaitForSeconds(Mathf.Max(0f, cfg.SettleSeconds));
            if (token != _siteScanSceneToken || _siteScanBusy) yield break;

            if (!_siteLevelsLogged)
            {
                _siteLevelsLogged = true;
                try
                {
                    var levels = GameDatabase.Database.AllLevels.Where(l => l != null).Select(l => $"{l.FileName}{(l.IsMultiplayer ? "" : " (SP)")}");
                    log.Msg("[SITE] levels in the game database: " + string.Join(", ", levels));
                }
                catch (Exception ex) { log.Warning("[SITE] level listing: " + ex.Message); }
            }

            string map = GetCurrentMapName() ?? sceneName;
            string outDir = SiteOutDir(cfg);
            var done = LoadSiteState(outDir);
            var todo = cfg.Maps.Where(m => !done.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
            bool here = todo.Any(m => string.Equals(m, map, StringComparison.OrdinalIgnoreCase));
            if (here)
            {
                _siteScanBusy = true;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                log.Msg($"[SITE] ===== site scan {map} start ({todo.Count} maps to go) =====");
                IEnumerator job = SiteScanMap(cfg, map, outDir);
                while (true)
                {
                    bool more;
                    try { more = job.MoveNext(); }
                    catch (Exception ex) { log.Error($"[SITE] scan {map} aborted: {ex}"); break; }
                    if (!more) break;
                    yield return job.Current;
                    if (token != _siteScanSceneToken) { log.Warning($"[SITE] scan {map} interrupted by a scene change"); _siteScanBusy = false; yield break; }
                }
                _siteScanBusy = false;
                done.Add(map);
                SaveSiteState(outDir, done);
                log.Msg($"[SITE] ===== site scan {map} done in {sw.Elapsed.TotalSeconds:F0} s =====");
                todo = cfg.Maps.Where(m => !done.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
            }
            if (todo.Count == 0) { log.Msg("[SITE] ===== SITE SCAN COMPLETE: all maps done ====="); yield break; }

            string next = todo[0];
            log.Msg($"[SITE] scan mode: loading next map {next}");
            yield return new WaitForSeconds(2f);
            try
            {
                var gm = GameMode.CurrentGameMode;
                bool okLoad = NetworkGameServer.LoadLevel(next, gm != null ? gm.GameModeInfo : null!);
                if (!okLoad) log.Warning($"[SITE] LoadLevel({next}) returned false (server not fully ready?)");
            }
            catch (Exception ex) { log.Error($"[SITE] LoadLevel({next}) failed: {ex.Message}"); }
        }

        private static List<string> LoadSiteState(string outDir)
        {
            try
            {
                string p = Path.Combine(outDir, "state.json");
                if (!File.Exists(p)) return new List<string>();
                var j = JObject.Parse(File.ReadAllText(p));
                return (j["done"] as JArray)?.Select(t => t.ToString()).ToList() ?? new List<string>();
            }
            catch { return new List<string>(); }
        }

        private static void SaveSiteState(string outDir, List<string> done)
        {
            try { File.WriteAllText(Path.Combine(outDir, "state.json"), new JObject { ["done"] = new JArray(done) }.ToString()); }
            catch (Exception ex) { Melon<MapBalance>.Logger.Warning("[SITE] state save: " + ex.Message); }
        }

        private static readonly string[] SiteKeys = { "Sol", "Cent", "Alien" };
        private static readonly int[] SiteYaws = { 0, 90, 180, 270 };

        private static IEnumerator SiteScanMap(SiteScanConfig cfg, string map, string outDir)
        {
            var log = Melon<MapBalance>.Logger;
            SiteTest.ResetPerMap();
            SiteTest.ClearNavCache();
            if (cfg.GroundTol.HasValue) SiteTest.GroundTol = cfg.GroundTol.Value;
            if (cfg.OverlapDepth.HasValue) SiteTest.OverlapDepth = cfg.OverlapDepth.Value;
            if (cfg.OnStaticMax.HasValue) SiteTest.OnStaticMax = cfg.OnStaticMax.Value;
            if (cfg.OnStaticFails.HasValue) SiteTest.OnStaticFails = cfg.OnStaticFails.Value;
            SiteTest.ReplicaChecks = 0; SiteTest.ReplicaMismatch = 0;
            log.Msg($"[SITE] thresholds: ground_tol {SiteTest.GroundTol} m, overlap_depth {SiteTest.OverlapDepth} m, on_static_max {SiteTest.OnStaticMax} m (fails: {SiteTest.OnStaticFails})");
            SiteTest.LogGraphs(log);
            try { log.Msg("[SITE] named graphs: " + string.Join(", ", GameAI.NamedGraphMasks.Select(kv => $"{kv.Key}={kv.Value.value}"))); } catch { }

            var structs = new Dictionary<string, SiteStructure>();
            foreach (var k in SiteKeys)
            {
                SiteStructure? s = null;
                try { s = SiteTest.Resolve(k, log); } catch (Exception ex) { log.Error($"[SITE] resolve {k}: {ex}"); }
                if (s != null) structs[k] = s;
            }
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            if (cfg.StartPoints)
            {
                IEnumerator sp = SiteStartPoints(map, structs, Path.Combine(outDir, $"site_startpoints_{map}_{ts}.csv"));
                while (sp.MoveNext()) yield return sp.Current;
            }
            if (cfg.LayoutCheck && !string.IsNullOrWhiteSpace(cfg.LayoutsDir))
            {
                IEnumerator lc = SiteLayoutCheck(cfg, map, structs, Path.Combine(outDir, $"site_layouts_{map}_{ts}.csv"));
                while (lc.MoveNext()) yield return lc.Current;
            }
            if (cfg.Grid)
            {
                IEnumerator g = SiteGrid(cfg, map, structs, Path.Combine(outDir, $"site_scan_{map}_{ts}.txt"));
                while (g.MoveNext()) yield return g.Current;
            }
            if (cfg.StarterAudit && !string.IsNullOrWhiteSpace(cfg.LayoutsDir))
            {
                IEnumerator a = SiteStarterAudit(cfg, map);
                while (a.MoveNext()) yield return a.Current;
            }
            log.Msg($"[SITE] game-rule replica check {map}: {SiteTest.ReplicaMismatch} mismatches in {SiteTest.ReplicaChecks} GetGroundSlopeClear calls");
        }

        private static string F(float v, int d = 1) => v.ToString("F" + d, CultureInfo.InvariantCulture);
        private static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "'") + "\"";

        private static float Yaw(Quaternion q) { float y = q.eulerAngles.y; y %= 360f; if (y < 0) y += 360f; return y; }

        // ------------------------------------------------------------------ start points (calibration)

        private static List<(string key, Transform t, int idx)> StartPointsOf(string key)
        {
            var list = new List<(string, Transform, int)>();
            if (!(GameMode.CurrentGameMode is MP_Strategy mp) || mp.TeamSetups == null) return list;
            foreach (var setup in mp.TeamSetups)
            {
                if (setup == null || setup.Team == null || setup.BaseStructureStartPoints == null) continue;
                string n = setup.Team.TeamName ?? "";
                string k = n.Contains("Sol") ? "Sol" : n.Contains("Cent") ? "Cent" : (n.Contains("Alien") && !n.Contains("Worm")) ? "Alien" : "";
                if (k != key) continue;
                for (int i = 0; i < setup.BaseStructureStartPoints.Count; i++)
                    if (setup.BaseStructureStartPoints[i] != null) list.Add((k, setup.BaseStructureStartPoints[i], i));
            }
            return list;
        }

        private static IEnumerator SiteStartPoints(string map, Dictionary<string, SiteStructure> structs, string path)
        {
            var log = Melon<MapBalance>.Logger;
            var sb = new StringBuilder();
            sb.AppendLine("map,team,idx,name,x,y,z,yaw,yaw_tested,code,valid,reason,blocker,detail,adjustY,finalY,surfaceY,nav_ok,gap,terrain_gap,overlap_depth,on_static");
            var seen = new Dictionary<int, string>();
            foreach (var key in SiteKeys)
            {
                if (!structs.TryGetValue(key, out var s)) continue;
                foreach (var (k, t, idx) in StartPointsOf(key))
                {
                    int id = t.GetInstanceID();
                    if (seen.TryGetValue(id, out string other)) log.Msg($"[SITE] start point {t.name} shared by {other} and {key}");
                    else seen[id] = key;
                    float yaw = Yaw(t.rotation);
                    var yaws = new List<float> { yaw };
                    yaws.AddRange(SiteYaws.Select(v => (float)v));
                    foreach (float y in yaws)
                    {
                        var r = SiteTest.Test(s, t.position.x, t.position.z, y, true);
                        sb.AppendLine($"{map},{key},{idx},{Csv(t.name)},{F(t.position.x)},{F(t.position.y)},{F(t.position.z)},{F(yaw)},{F(y)},{r.Code},{(r.Valid ? 1 : 0)},{r.ReasonName},{Csv(r.Blocker)},{Csv(r.Detail)},{F(r.AdjustY, 2)},{F(r.FinalY)},{F(r.SurfaceY)},{r.NavProbesOk},{F(r.GroundGap, 2)},{F(r.TerrainGap, 2)},{F(r.OverlapDepth, 2)},{Csv(r.OnStaticInfo)}");
                        if (y == yaw)
                            log.Msg($"[SITE] start point {map} {key} #{idx} '{t.name}' ({F(t.position.x, 0)},{F(t.position.z, 0)}) yaw {F(yaw, 0)}: {(r.Valid ? "VALID" : "INVALID " + r.ReasonName)} {r.Blocker} {r.Detail} (code {r.Code}, adjust {F(r.AdjustY, 2)}, nav {r.NavProbesOk}/8)");
                    }
                    yield return null;
                }
            }
            File.WriteAllText(path, sb.ToString());
            log.Msg($"[SITE] start points -> {path}");
        }

        // ------------------------------------------------------------------ layout check

        private static (Transform? t, float d) NearestStartPoint(string key, float x, float z)
        {
            Transform? best = null; float bd = float.MaxValue;
            foreach (var (_, t, _) in StartPointsOf(key))
            {
                float dx = t.position.x - x, dz = t.position.z - z, d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = t; }
            }
            return (best, best != null ? Mathf.Sqrt(bd) : -1f);
        }

        private static IEnumerator SiteLayoutCheck(SiteScanConfig cfg, string map, Dictionary<string, SiteStructure> structs, string path)
        {
            var log = Melon<MapBalance>.Logger;
            string dir = Path.Combine(cfg.LayoutsDir, map);
            if (!Directory.Exists(dir)) { log.Msg($"[SITE] layout check: no folder {dir}"); yield break; }
            var sb = new StringBuilder();
            sb.AppendLine("map,file,folder,entry,structure,x,z,rot_field,sp_name,sp_dist,sp_yaw,variant,yaw,code,valid,reason,blocker,detail,adjustY,finalY,surfaceY,nav_ok,gap,terrain_gap,overlap_depth,on_static");
            int nFiles = 0, nBad = 0;
            foreach (string file in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                JObject root;
                try { root = JObject.Parse(File.ReadAllText(file)); } catch (Exception ex) { log.Warning($"[SITE] layout {file}: {ex.Message}"); continue; }
                nFiles++;
                string folder = Path.GetDirectoryName(file)!.Length > dir.Length ? Path.GetDirectoryName(file)!.Substring(dir.Length).TrimStart('\\', '/') : "";
                string fname = Path.GetFileName(file);
                if (!(root["spawns"] is JObject spawns)) continue;
                bool swap = root["swap_spawns"]?.Type != JTokenType.Boolean || root["swap_spawns"]!.Value<bool>();
                var gm = root["game_modes"] as JObject;
                bool swapMode = gm == null || (gm["hvh"]?.Type == JTokenType.Boolean && gm["hvh"]!.Value<bool>()) || (gm["hvhva"]?.Type == JTokenType.Boolean && gm["hvhva"]!.Value<bool>());
                foreach (var entryKey in SiteKeys)
                {
                    if (!(spawns[entryKey] is JArray arr) || arr.Count == 0 || !(arr[0] is JObject e)) continue;
                    float x = e["x"]?.Value<float>() ?? 0f, z = e["z"]?.Value<float>() ?? 0f;
                    float? rotField = e["rot"]?.Type == JTokenType.Integer || e["rot"]?.Type == JTokenType.Float ? e["rot"]!.Value<float>() : (float?)null;
                    var tested = new List<string> { entryKey };
                    if (entryKey != "Alien" && swap && swapMode) tested.Add(entryKey == "Sol" ? "Cent" : "Sol");
                    foreach (var sk in tested)
                    {
                        if (!structs.TryGetValue(sk, out var s)) continue;
                        var (sp, spd) = NearestStartPoint(sk, x, z);
                        float spYaw = sp != null ? Yaw(sp.rotation) : 0f;
                        float cur = rotField ?? spYaw;
                        var variants = new List<(string v, float y)> { ("current", cur) };
                        variants.AddRange(SiteYaws.Select(v => ("r" + v.ToString("000"), (float)v)));
                        foreach (var (v, y) in variants)
                        {
                            var r = SiteTest.Test(s, x, z, y, true);
                            sb.AppendLine($"{map},{Csv(fname)},{Csv(folder)},{entryKey},{sk},{F(x)},{F(z)},{(rotField.HasValue ? F(rotField.Value) : "")},{Csv(sp?.name ?? "")},{F(spd)},{F(spYaw)},{v},{F(y)},{r.Code},{(r.Valid ? 1 : 0)},{r.ReasonName},{Csv(r.Blocker)},{Csv(r.Detail)},{F(r.AdjustY, 2)},{F(r.FinalY)},{F(r.SurfaceY)},{r.NavProbesOk},{F(r.GroundGap, 2)},{F(r.TerrainGap, 2)},{F(r.OverlapDepth, 2)},{Csv(r.OnStaticInfo)}");
                            if (v == "current" && !r.Valid)
                            {
                                nBad++;
                                log.Msg($"[SITE] layout {map} {(folder.Length > 0 ? folder + "/" : "")}{fname} {entryKey}->{sk} ({F(x, 0)},{F(z, 0)}) yaw {F(y, 0)}: INVALID {r.ReasonName} {r.Blocker} {r.Detail}");
                            }
                        }
                    }
                }
                yield return null;
            }
            File.WriteAllText(path, sb.ToString());
            log.Msg($"[SITE] layout check {map}: {nFiles} files, {nBad} invalid bases at the current yaw -> {path}");
        }

        // ------------------------------------------------------------------ grid scan

        private static IEnumerator SiteGrid(SiteScanConfig cfg, string map, Dictionary<string, SiteStructure> structs, string path)
        {
            var log = Melon<MapBalance>.Logger;
            var terrain = GetMainTerrain() ?? Terrain.activeTerrain;
            if (terrain == null) { log.Error("[SITE] grid: no terrain"); yield break; }
            float step = Mathf.Max(1f, cfg.GridStep);
            Vector3 tp = terrain.transform.position, tsz = terrain.terrainData.size;
            float minX = Mathf.Ceil(tp.x / step) * step, minZ = Mathf.Ceil(tp.z / step) * step;
            float maxX = Mathf.Floor((tp.x + tsz.x) / step) * step, maxZ = Mathf.Floor((tp.z + tsz.z) / step) * step;
            int nx = Mathf.RoundToInt((maxX - minX) / step) + 1, nz = Mathf.RoundToInt((maxZ - minZ) / step) + 1;

            var tiles = Terrain.activeTerrains.Where(t => t != null && t.terrainData != null).ToList();
            float ux0 = tiles.Min(t => t.transform.position.x), uz0 = tiles.Min(t => t.transform.position.z);
            float ux1 = tiles.Max(t => t.transform.position.x + t.terrainData.size.x), uz1 = tiles.Max(t => t.transform.position.z + t.terrainData.size.z);
            if (tiles.Count > 1 || ux0 < tp.x - 1 || uz0 < tp.z - 1 || ux1 > tp.x + tsz.x + 1 || uz1 > tp.z + tsz.z + 1)
                log.Warning($"[SITE] {map}: {tiles.Count} terrain tiles, union x[{ux0:F0},{ux1:F0}] z[{uz0:F0},{uz1:F0}] vs main terrain x[{tp.x:F0},{tp.x + tsz.x:F0}] z[{tp.z:F0},{tp.z + tsz.z:F0}] (grid = main terrain, as hq_scan)");

            // open-ground probes
            GraphMask vMask = GraphMask.everything; string vGraph = "all graphs";
            if (SiteTest.TryNamedGraph("Small Vehicle", out var vm)) { vMask = vm; vGraph = "Small Vehicle"; }
            var ag = SiteTest.AlienGroundGraph(log);
            var vProbe = SiteTest.MakeProbe("vehicle", cfg.VehicleRef.ToArray(), new Vector3(4f, 1.5f, 6f), vMask, vGraph, log);
            var aProbe = SiteTest.MakeProbe("alien", cfg.AlienRef.ToArray(), new Vector3(3f, 1.5f, 3f), ag.mask, ag.name, log);

            var head = new StringBuilder();
            head.AppendLine("=== Base Site Scan v" + SITE_SCAN_VERSION + " ===");
            head.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            head.AppendLine($"Map: {map}");
            head.AppendLine($"Mod: Si_MapBalance {ModVersion}");
            try { head.AppendLine($"Game: {Application.version}"); } catch { }
            head.AppendLine($"Terrain: ({tp.x:F0},{tp.y:F0},{tp.z:F0}) size ({tsz.x:F0},{tsz.y:F0},{tsz.z:F0}) tiles {tiles.Count}");
            head.AppendLine($"Scan: X=[{minX:F0},{maxX:F0}] Z=[{minZ:F0},{maxZ:F0}] step={step:F0}");
            head.AppendLine($"Grid: {nx}x{nz}");
            try { var fb = FogOfWar.Instance != null ? FogOfWar.Instance.Bounds : default; head.AppendLine($"FogOfWarBounds: min({fb.min.x:F0},{fb.min.z:F0}) max({fb.max.x:F0},{fb.max.z:F0})"); } catch { }
            foreach (var kv in structs) head.AppendLine("Structure " + kv.Value.Describe);
            head.AppendLine($"Probe vehicle: half({vProbe.Half.x:F2},{vProbe.Half.y:F2},{vProbe.Half.z:F2}) {vProbe.Source}; graph '{vProbe.Graph}' main {vProbe.MainArea?.ToString() ?? "-"} slack {vProbe.Slack:F1}");
            head.AppendLine($"Probe alien: half({aProbe.Half.x:F2},{aProbe.Half.y:F2},{aProbe.Half.z:F2}) {aProbe.Source}; graph '{aProbe.Graph}' main {aProbe.MainArea?.ToString() ?? "-"} slack {aProbe.Slack:F1}");
            head.AppendLine($"Params: ground_tol {SiteTest.GroundTol} overlap_depth {SiteTest.OverlapDepth} on_static_max {SiteTest.OnStaticMax} on_static_fails {SiteTest.OnStaticFails} clip_above_lowest_point {SiteTest.CLIP_ABOVE_LOWEST_POINT_M} nav_margin {SiteTest.NAV_MARGIN_M} nav_min_probes {SiteTest.NAV_MIN_PROBES} nav_max_h {SiteTest.NAV_MAX_H_M} nav_max_v {SiteTest.NAV_MAX_V_M}");
            head.AppendLine("Columns: x,z = cell centre; sy = surface y (IGNOREDYNAMIC ray); s/c/n<yaw> = site code Sol HQ / Cent HQ / Alien Nest; o = open-ground bits; vy/ay = landing y of the vehicle/alien probe (empty = none)");

            log.Msg($"[SITE] grid {map}: {nx}x{nz} cells x {structs.Count} structures x 4 yaws, step {step:F0}, budget {cfg.FrameBudgetMs:F0} ms/frame");
            var rows = new StringBuilder(nx * nz * 64);
            rows.AppendLine("x,z,sy,s000,s090,s180,s270,c000,c090,c180,c270,n000,n090,n180,n270,o,vy,ay");
            var counts = new Dictionary<string, int[]>();
            foreach (var k in SiteKeys) counts[k] = new int[4];
            int vOk = 0, aOk = 0, cells = 0;
            SiteTest.VerifyGameRule = false;   // grid: bit 5 from the verified replica (half the raycasts)
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var frame = System.Diagnostics.Stopwatch.StartNew();
            var total = System.Diagnostics.Stopwatch.StartNew();
            for (int iz = 0; iz < nz; iz++)
            {
                float z = minZ + iz * step;
                for (int ix = 0; ix < nx; ix++)
                {
                    float x = minX + ix * step;
                    cells++;
                    float sy = float.NaN;
                    var line = new StringBuilder(80);
                    var codes = new int[12];
                    for (int si = 0; si < SiteKeys.Length; si++)
                    {
                        if (!structs.TryGetValue(SiteKeys[si], out var s))
                        {
                            for (int ri = 0; ri < 4; ri++) codes[si * 4 + ri] = SiteCode.R_NO_DATA << SiteCode.REASON_SHIFT;
                            continue;
                        }
                        for (int ri = 0; ri < 4; ri++)
                        {
                            var r = SiteTest.Test(s, x, z, SiteYaws[ri], true);
                            codes[si * 4 + ri] = r.Code;
                            if (r.Valid) counts[SiteKeys[si]][ri]++;
                            if (float.IsNaN(sy) && !r.OffMap) sy = r.SurfaceY;
                        }
                    }
                    int o = SiteTest.OpenCell(vProbe, x, z, true, out float vy);
                    int oa = SiteTest.OpenCell(aProbe, x, z, false, out float ay);
                    o |= oa & (OpenCode.A_OK | OpenCode.A_NAV | OpenCode.A_OVERLAP);
                    if ((o & OpenCode.V_OK) != 0) vOk++;
                    if ((o & OpenCode.A_OK) != 0) aOk++;
                    line.Append(F(x, 0)).Append(',').Append(F(z, 0)).Append(',').Append(float.IsNaN(sy) ? "" : F(sy));
                    foreach (int c in codes) line.Append(',').Append(c);
                    line.Append(',').Append(o).Append(',').Append(float.IsNaN(vy) ? "" : F(vy)).Append(',').Append(float.IsNaN(ay) ? "" : F(ay));
                    rows.AppendLine(line.ToString());

                    if (frame.Elapsed.TotalMilliseconds > cfg.FrameBudgetMs)
                    {
                        yield return null;
                        frame.Restart();
                    }
                }
                if (sw.Elapsed.TotalSeconds > 30)
                {
                    sw.Restart();
                    log.Msg($"[SITE] grid {map}: row {iz + 1}/{nz} ({100f * (iz + 1) / nz:F0} %), {total.Elapsed.TotalSeconds:F0} s");
                }
            }
            SiteTest.VerifyGameRule = true;
            head.AppendLine("=== Scan Results ===");
            head.AppendLine($"Cells: {cells}");
            foreach (var k in SiteKeys) head.AppendLine($"Valid {k}: r000 {counts[k][0]} r090 {counts[k][1]} r180 {counts[k][2]} r270 {counts[k][3]}");
            head.AppendLine($"Open vehicle: {vOk}  Open alien: {aOk}");
            head.AppendLine($"Scan time: {total.Elapsed.TotalSeconds:F0} s");
            head.AppendLine();
            head.AppendLine("=== CSV DATA ===");
            File.WriteAllText(path, head.ToString() + rows.ToString());
            log.Msg($"[SITE] grid {map} done in {total.Elapsed.TotalSeconds:F0} s: " + string.Join(", ", SiteKeys.Select(k => $"{k} valid {string.Join("/", counts[k])}")) + $", open vehicle {vOk} alien {aOk} of {cells} -> {path}");
        }

        // ------------------------------------------------------------------ Si_StarterUnits dry run

        private static IEnumerator SiteStarterAudit(SiteScanConfig cfg, string map)
        {
            var log = Melon<MapBalance>.Logger;
            Type? su = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                su = asm.GetType("Si_StarterUnits.StarterUnits");
                if (su != null) break;
            }
            var start = su?.GetMethod("StartAudit", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
            var running = su?.GetField("_auditRunning", BindingFlags.NonPublic | BindingFlags.Static);
            if (start == null || running == null) { log.Warning("[SITE] starter audit: Si_StarterUnits.StarterUnits.StartAudit not found, skipped"); yield break; }

            string src = Path.Combine(cfg.LayoutsDir, map);
            if (!Directory.Exists(src)) yield break;
            string auditRoot = Path.Combine("UserData", "cfg", "StarterUnits", "audit");
            bool rootExisted = Directory.Exists(auditRoot);
            string dst = Path.Combine(auditRoot, map);
            bool dstExisted = Directory.Exists(dst);
            if (dstExisted) { log.Warning($"[SITE] starter audit: {dst} already exists, not touching it; skipped"); yield break; }
            var written = new List<string>();
            try
            {
                Directory.CreateDirectory(dst);
                foreach (string file in Directory.GetFiles(src, "*.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        var root = JObject.Parse(File.ReadAllText(file));
                        if (!(root["spawns"] is JObject spawns)) continue;
                        var outSp = new JObject();
                        foreach (var k in SiteKeys)
                            if (spawns[k] is JArray arr && arr.Count > 0 && arr[0] is JObject e)
                                outSp[k] = new JArray(new JObject { ["x"] = e["x"], ["z"] = e["z"] });
                        string rel = Path.GetDirectoryName(file)!.Length > src.Length ? Path.GetDirectoryName(file)!.Substring(src.Length).TrimStart('\\', '/').Replace('\\', '_').Replace('/', '_') + "__" : "";
                        string name = rel + Path.GetFileName(file);
                        string p = Path.Combine(dst, name);
                        File.WriteAllText(p, new JObject { ["spawns"] = outSp }.ToString());
                        written.Add(p);
                    }
                    catch (Exception ex) { log.Warning($"[SITE] starter audit point {file}: {ex.Message}"); }
                }
                log.Msg($"[SITE] starter audit {map}: {written.Count} test-point files in {dst}");
                string reply = "";
                start.Invoke(null, new object[] { true, new Action<string>(s => reply = s) });
            }
            catch (Exception ex) { log.Error("[SITE] starter audit start: " + ex.Message); }

            float t = 0f;
            while (t < 900f)
            {
                bool run = false;
                try { run = (bool)running.GetValue(null); } catch { }
                if (!run) break;
                yield return new WaitForSeconds(0.5f);
                t += 0.5f;
            }
            log.Msg($"[SITE] starter audit {map} finished after {t:F0} s");
            // remove the test points again
            try
            {
                foreach (var p in written) { try { File.Delete(p); } catch { } }
                if (Directory.Exists(dst) && !Directory.EnumerateFileSystemEntries(dst).Any()) Directory.Delete(dst);
                if (!rootExisted && Directory.Exists(auditRoot) && !Directory.EnumerateFileSystemEntries(auditRoot).Any()) Directory.Delete(auditRoot);
            }
            catch (Exception ex) { log.Warning("[SITE] starter audit cleanup: " + ex.Message); }
        }

        // ------------------------------------------------------------------ runtime warning

        private static readonly Dictionary<string, SiteStructure?> _siteRuntime = new Dictionary<string, SiteStructure?>();
        private static string _siteRuntimeMap = "";
        private static readonly List<string> _siteRoundBad = new List<string>();
        private static int _siteRoundChecked;

        /// <summary>Called before the configured bases of a round are spawned.</summary>
        private static void SiteCheckBegin()
        {
            _siteRoundBad.Clear();
            _siteRoundChecked = 0;
        }

        /// <summary>Site test of one configured base at round start. Warning only: the base is spawned where the
        /// layout says. Never throws.</summary>
        private static void SiteCheckBase(object team, string teamName, string layoutEntry, float x, float z, Quaternion rotation, string rotSource, MelonLogger.Instance log)
        {
            try
            {
                string map = GetCurrentMapName() ?? "?";
                if (map != _siteRuntimeMap) { _siteRuntime.Clear(); _siteRuntimeMap = map; SiteTest.ResetPerMap(); SiteTest.ClearNavCache(); }
                string key = teamName.Contains("Sol") ? "Sol" : teamName.Contains("Cent") ? "Cent" : (teamName.Contains("Alien") && !teamName.Contains("Worm")) ? "Alien" : "";
                if (key.Length == 0) return;
                if (!_siteRuntime.TryGetValue(key, out var s)) { s = SiteTest.Resolve(key, log); _siteRuntime[key] = s; }
                if (s == null) return;
                float yaw = Yaw(rotation);
                var r = SiteTest.Test(s, x, z, yaw, true);
                _siteRoundChecked++;
                if (!r.Valid)
                {
                    string line = $"[SITE] BAD BASE map={map} layout={_activeConfigName} team={key}" +
                                  (layoutEntry != key ? $" (on the layout's {layoutEntry} spot, swapped)" : "") +
                                  $" pos=({x:F0},{z:F0}) rot={yaw:F0} ({rotSource}) reason={r.ReasonName}" +
                                  (r.Blocker.Length > 0 ? $" blocker={r.Blocker}" : "") +
                                  (r.Detail.Length > 0 ? $" detail={r.Detail}" : "") +
                                  $" [code {r.Code}, terrainRule {(r.TerrainRule ? 1 : 0)} gameRule {(r.GameRule ? 1 : 0)} nav {r.NavProbesOk}/8]";
                    _siteRoundBad.Add(line);
                    log.Warning(line);
                }
            }
            catch (Exception ex) { log.Warning("[SITE] base site check failed: " + ex.Message); }
        }

        private static void SiteCheckEnd(MelonLogger.Instance log)
        {
            if (_siteRoundChecked == 0) return;
            string map = GetCurrentMapName() ?? "?";
            if (_siteRoundBad.Count == 0)
                log.Msg($"[SITE] Base site check {map} {_activeConfigName}: all {_siteRoundChecked} bases clear");
            else
                log.Warning($"[SITE] Base site check {map} {_activeConfigName}: {_siteRoundBad.Count} of {_siteRoundChecked} bases flagged (see BAD BASE lines; bases are NOT moved)");
        }
    }
}
