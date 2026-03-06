/*
 Si_MapBalance - v3.1.0 (Web Tool Integration)

 Per-map configuration:
 - Primary: reads layout JSONs from UserData/Spawns/{MapName}/*.json (web tool output)
 - Fallback: reads from UserData/MapBalance/configs/{MapName}.json (legacy format)
 - Supports per-resource-type amounts, patch_overrides, removed_patches
 - Supports per-faction settings (build radius, chain range)
 - Multiple layout files per map = random variant selection per round
 - Maps without a config are handled fully vanilla (no resource/spawn changes)

 Resource layout:
 - Re-enables existing ResourceAreas with configured amounts
 - Server-only mod, no client mod needed
*/

using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

[assembly: MelonInfo(typeof(Si_MapBalance.MapBalance), "Map Balance", "3.1.0", "schwe")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]

namespace Si_MapBalance
{
    // === Per-map JSON config model ===
    // Supports both web tool format and legacy variant format

    public class MapConfig
    {
        [JsonProperty("map")]
        public string Map = "";

        [JsonProperty("description")]
        public string Description = "";

        [JsonProperty("resources")]
        public ResourceConfig Resources = new ResourceConfig();

        // Raw spawns token — parsed flexibly to handle both formats
        [JsonProperty("spawns")]
        public JToken? RawSpawns;

        [JsonProperty("positions")]
        public List<PositionEntry>? Positions;

        [JsonProperty("settings")]
        public SettingsConfig Settings = new SettingsConfig();

        [JsonProperty("game_modes")]
        public JToken? GameModes;

        /// <summary>
        /// Extract spawn position for a faction, handling both formats:
        /// Web tool:  "Sol": [{"x":-2070,"z":2295,"isSpawn":false,...}]
        /// Legacy:    "variant_X": {"Sol": {"x":-1305,"z":-1260}, ...}
        /// For legacy format, a random variant is selected.
        /// </summary>
        public SpawnPos? GetFactionSpawn(string faction, MelonLogger.Instance log)
        {
            if (RawSpawns == null || RawSpawns.Type != JTokenType.Object) return null;
            var obj = (JObject)RawSpawns;

            // Check if it's web tool format: faction keys with array values
            var factionToken = obj[faction];
            if (factionToken != null && factionToken.Type == JTokenType.Array)
            {
                var arr = (JArray)factionToken;
                if (arr.Count == 0) return null;
                var entry = arr[0];
                return new SpawnPos
                {
                    X = entry["x"]?.Value<float>() ?? 0f,
                    Z = entry["z"]?.Value<float>() ?? 0f
                };
            }

            // Legacy format: variant keys → pick random variant, extract faction
            var variants = obj.Properties()
                .Where(p => p.Value.Type == JTokenType.Object && p.Value[faction] != null)
                .ToList();
            if (variants.Count == 0) return null;

            var picked = variants[UnityEngine.Random.Range(0, variants.Count)];
            log.Msg($"Selected spawn variant: {picked.Name}");
            var pos = picked.Value[faction];
            if (pos == null) return null;

            return new SpawnPos
            {
                X = pos["x"]?.Value<float>() ?? 0f,
                Z = pos["z"]?.Value<float>() ?? 0f
            };
        }

        /// <summary>Returns true if any faction has spawn data.</summary>
        public bool HasSpawns()
        {
            if (RawSpawns == null || RawSpawns.Type != JTokenType.Object) return false;
            var obj = (JObject)RawSpawns;
            // Web tool: check for faction arrays
            foreach (var faction in new[] { "Sol", "Cent", "Alien" })
            {
                var t = obj[faction];
                if (t != null && t.Type == JTokenType.Array && ((JArray)t).Count > 0)
                    return true;
            }
            // Legacy: check for variant objects
            return obj.Properties().Any(p => p.Value.Type == JTokenType.Object);
        }
    }

    public class ResourceConfig
    {
        [JsonProperty("balterium_amount")]
        public int BalteriumAmount = 0;

        [JsonProperty("biotics_amount")]
        public int BioticsAmount = 0;

        [JsonProperty("removed_patches")]
        public List<int> RemovedPatches = new List<int>();

        [JsonProperty("patch_overrides")]
        public Dictionary<string, int> PatchOverrides = new Dictionary<string, int>();
    }

    public class SpawnPos
    {
        [JsonProperty("x")]
        public float X;

        [JsonProperty("z")]
        public float Z;
    }

    public class PositionEntry
    {
        [JsonProperty("x")]
        public float X;

        [JsonProperty("z")]
        public float Z;

        [JsonProperty("type")]
        public string Type = "balterium";
    }

    public class SettingsConfig
    {
        // Legacy single-value fields
        [JsonProperty("buildRadius")]
        public float BuildRadius = 550f;

        [JsonProperty("chainRange")]
        public float ChainRange = 1300f;

        // Per-faction fields (web tool format)
        [JsonProperty("solBuildRadius")]
        public float SolBuildRadius = 0f;

        [JsonProperty("centBuildRadius")]
        public float CentBuildRadius = 0f;

        [JsonProperty("solChainRange")]
        public float SolChainRange = 0f;

        [JsonProperty("centChainRange")]
        public float CentChainRange = 0f;

        [JsonProperty("alienNodeChainRange")]
        public float AlienNodeChainRange = 0f;

        [JsonProperty("alienBiocacheChainRange")]
        public float AlienBiocacheChainRange = 0f;

        /// <summary>Get effective build radius for a faction.</summary>
        public float GetBuildRadius(string faction)
        {
            if (faction.Contains("Sol") && SolBuildRadius > 0) return SolBuildRadius;
            if (faction.Contains("Cent") && CentBuildRadius > 0) return CentBuildRadius;
            return BuildRadius;
        }

        /// <summary>Get effective chain range for a faction.</summary>
        public float GetChainRange(string faction)
        {
            if (faction.Contains("Sol") && SolChainRange > 0) return SolChainRange;
            if (faction.Contains("Cent") && CentChainRange > 0) return CentChainRange;
            return ChainRange;
        }
    }

    public class MapBalance : MelonMod
    {
        private static bool _dumpedThisRound;
        private static List<GameObject> _spawnedAreas = new List<GameObject>();
        private static MapConfig? _activeConfig;
        private static string _activeConfigName = "";
        private static bool _swappedSolCent;
        private static MethodInfo? _sendServerChatMethod;

        // Resolved via reflection
        private static Type? _resourceAreaType;
        private static Type? _gameType;
        private static Type? _gameDatabaseType;
        private static Type? _resourceType;
        private static Type? _networkComponentType;
        private static Type? _playerType;
        private static Type? _constructionDataType;
        private static Type? _constructionPreviewType;
        private static Type? _enableAboveGroundType;
        private static Type? _constructionBoundsType;
        private static Type? _mpStrategyType;
        private static Type? _strategyTeamSetupType;
        private static Type? _teamType;
        private static bool _typesResolved;

        // Cached reflection
        private static MethodInfo? _distributeResourcesMethod;
        private static MethodInfo? _sendNetInitMethod;
        private static MethodInfo? _spawnPrefabMethod;
        private static MethodInfo? _getPrefabIndexMethod;
        private static MethodInfo? _getPrefabMethod;
        private static FieldInfo? _allAreasField;
        private static PropertyInfo? _allAreasProp;
        private static FieldInfo? _enabledAreasField;
        private static PropertyInfo? _enabledAreasProp;

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("Si_MapBalance v3.1.0 loaded");
        }

        public override void OnLateInitializeMelon()
        {
            ResolveTypes();
            CacheReflection();
            PatchDistributeAllResources();
            PatchSpawnBaseStructures();
        }

        private static void ResolveTypes()
        {
            if (_typesResolved) return;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "SilicaCore") continue;

                _resourceAreaType = asm.GetType("ResourceArea");
                _gameType = asm.GetType("Game");
                _gameDatabaseType = asm.GetType("GameDatabase");
                _resourceType = asm.GetType("Resource");
                _networkComponentType = asm.GetType("NetworkComponent");
                _playerType = asm.GetType("Player");
                _constructionDataType = asm.GetType("ConstructionData");
                _constructionPreviewType = asm.GetType("ConstructionPreview");
                _enableAboveGroundType = asm.GetType("EnableAboveGround");
                _constructionBoundsType = asm.GetType("ConstructionBounds");
                _mpStrategyType = asm.GetType("MP_Strategy");
                _strategyTeamSetupType = asm.GetType("StrategyTeamSetup");
                _teamType = asm.GetType("Team");
                break;
            }

            _typesResolved = true;
        }

        private static void CacheReflection()
        {
            if (_resourceAreaType == null) return;

            var log = Melon<MapBalance>.Logger;

            // DistributeResources(bool editorPreview = false)
            _distributeResourcesMethod = _resourceAreaType.GetMethod("DistributeResources",
                BindingFlags.Public | BindingFlags.Instance);
            log.Msg($"DistributeResources: {_distributeResourcesMethod}");

            // AllResourceAreas / ResourceAreas static lists
            _allAreasField = _resourceAreaType.GetField("AllResourceAreas",
                BindingFlags.Public | BindingFlags.Static);
            if (_allAreasField == null)
                _allAreasProp = _resourceAreaType.GetProperty("AllResourceAreas",
                    BindingFlags.Public | BindingFlags.Static);

            _enabledAreasField = _resourceAreaType.GetField("ResourceAreas",
                BindingFlags.Public | BindingFlags.Static);
            if (_enabledAreasField == null)
                _enabledAreasProp = _resourceAreaType.GetProperty("ResourceAreas",
                    BindingFlags.Public | BindingFlags.Static);

            // NetworkComponent.SendNetInit(Player player) — null = broadcast to all
            if (_networkComponentType != null)
            {
                // Try with Player param
                _sendNetInitMethod = _networkComponentType.GetMethod("SendNetInit",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { _playerType! }, null);

                if (_sendNetInitMethod == null)
                {
                    // Try no-param version
                    _sendNetInitMethod = _networkComponentType.GetMethod("SendNetInit",
                        BindingFlags.Public | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);
                }

                if (_sendNetInitMethod == null)
                {
                    // Try any overload
                    var methods = _networkComponentType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => m.Name == "SendNetInit").ToArray();
                    log.Msg($"SendNetInit overloads found: {methods.Length}");
                    foreach (var m in methods)
                    {
                        var ps = m.GetParameters();
                        log.Msg($"  {m.Name}({string.Join(", ", ps.Select(p => p.ParameterType.Name + " " + p.Name))})");
                    }
                    if (methods.Length > 0)
                        _sendNetInitMethod = methods[0];
                }

                log.Msg($"SendNetInit: {_sendNetInitMethod}");
            }

            // GameDatabase — use GetMethods to avoid AmbiguousMatchException
            try
            {
                if (_gameDatabaseType != null)
                {
                    // GetSpawnablePrefabIndex(string) — find the overload that takes a string
                    var indexMethods = _gameDatabaseType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Where(m => m.Name == "GetSpawnablePrefabIndex").ToArray();
                    log.Msg($"GetSpawnablePrefabIndex overloads: {indexMethods.Length}");
                    foreach (var m in indexMethods)
                    {
                        var ps = m.GetParameters();
                        log.Msg($"  ({string.Join(", ", ps.Select(p => p.ParameterType.Name + " " + p.Name))})");
                    }
                    _getPrefabIndexMethod = indexMethods.FirstOrDefault(m =>
                        m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string))
                        ?? indexMethods.FirstOrDefault();

                    // GetSpawnablePrefab(int) — find the overload that takes an int
                    var prefabMethods = _gameDatabaseType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Where(m => m.Name == "GetSpawnablePrefab").ToArray();
                    log.Msg($"GetSpawnablePrefab overloads: {prefabMethods.Length}");
                    foreach (var m in prefabMethods)
                    {
                        var ps = m.GetParameters();
                        log.Msg($"  ({string.Join(", ", ps.Select(p => p.ParameterType.Name + " " + p.Name))})");
                    }
                    _getPrefabMethod = prefabMethods.FirstOrDefault(m =>
                        m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(int))
                        ?? prefabMethods.FirstOrDefault();

                    log.Msg($"Using GetSpawnablePrefabIndex: {_getPrefabIndexMethod}");
                    log.Msg($"Using GetSpawnablePrefab: {_getPrefabMethod}");
                }

                // Game.SpawnPrefab — find the overload with most params
                if (_gameType != null)
                {
                    var spawnMethods = _gameType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Where(m => m.Name == "SpawnPrefab").ToArray();
                    log.Msg($"SpawnPrefab overloads: {spawnMethods.Length}");
                    foreach (var m in spawnMethods)
                    {
                        var ps = m.GetParameters();
                        log.Msg($"  SpawnPrefab({string.Join(", ", ps.Select(p => p.ParameterType.Name + " " + p.Name))})");
                    }
                    _spawnPrefabMethod = spawnMethods
                        .OrderByDescending(m => m.GetParameters().Length)
                        .FirstOrDefault();
                    log.Msg($"Using SpawnPrefab: {_spawnPrefabMethod} ({_spawnPrefabMethod?.GetParameters().Length ?? 0} params)");
                }
            }
            catch (Exception ex)
            {
                log.Error($"Failed to resolve spawn methods: {ex.Message}");
            }

            // Player.SendServerChatMessage(string)
            if (_playerType != null)
            {
                _sendServerChatMethod = _playerType.GetMethod("SendServerChatMessage",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(string) }, null);
                log.Msg($"SendServerChatMessage: {_sendServerChatMethod}");
            }
        }

        private void PatchDistributeAllResources()
        {
            if (_resourceAreaType == null)
            {
                LoggerInstance.Error("Cannot patch — ResourceArea type not found");
                return;
            }

            var distributeMethod = _resourceAreaType.GetMethod("DistributeAllResources",
                BindingFlags.Public | BindingFlags.Static);

            if (distributeMethod == null)
            {
                LoggerInstance.Error("DistributeAllResources not found");
                return;
            }

            var prefix = typeof(MapBalance).GetMethod(nameof(Prefix_DistributeAllResources),
                BindingFlags.Static | BindingFlags.NonPublic);

            HarmonyInstance.Patch(distributeMethod, prefix: new HarmonyMethod(prefix));
            LoggerInstance.Msg("Patched DistributeAllResources (prefix, skips original)");
        }

        private void PatchSpawnBaseStructures()
        {
            if (_mpStrategyType == null)
            {
                LoggerInstance.Warning("MP_Strategy type not found — cannot patch spawn positions");
                return;
            }

            var spawnMethod = _mpStrategyType.GetMethod("SpawnBaseStructures",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (spawnMethod == null)
            {
                LoggerInstance.Warning("SpawnBaseStructures not found — cannot patch spawn positions");
                return;
            }

            var prefix = typeof(MapBalance).GetMethod(nameof(Prefix_SpawnBaseStructures),
                BindingFlags.Static | BindingFlags.NonPublic);

            HarmonyInstance.Patch(spawnMethod, prefix: new HarmonyMethod(prefix));
            LoggerInstance.Msg("Patched SpawnBaseStructures (prefix, overrides HQ positions)");
        }

        // === Harmony Patches ===

        private static bool Prefix_SpawnBaseStructures(object __instance)
        {
            var log = Melon<MapBalance>.Logger;

            try
            {
                // Get TeamSetups first — we need team count to detect game mode
                var teamSetupsField = _mpStrategyType!.GetField("TeamSetups",
                    BindingFlags.Public | BindingFlags.Instance);
                if (teamSetupsField == null) { log.Error("TeamSetups field not found"); return true; }

                var teamSetups = teamSetupsField.GetValue(__instance);
                if (teamSetups == null) { log.Error("TeamSetups is null"); return true; }

                var getActiveMethod = FindMethod(_mpStrategyType, "GetTeamSetupActive");
                var countProp = teamSetups.GetType().GetProperty("Count");
                int count = (int)(countProp?.GetValue(teamSetups) ?? 0);
                var itemProp = teamSetups.GetType().GetProperty("Item");

                // Detect game mode from active teams
                int activeCount = 0;
                bool hasAlien = false;
                for (int i = 0; i < count; i++)
                {
                    var s = itemProp?.GetValue(teamSetups, new object[] { i });
                    if (s != null && getActiveMethod != null &&
                        (bool)(getActiveMethod.Invoke(__instance, new[] { s }) ?? false))
                    {
                        activeCount++;
                        object? team = GetTeamFromSetup(s);
                        if (team != null && GetTeamName(team).Contains("Alien"))
                            hasAlien = true;
                    }
                }

                string gameMode;
                if (activeCount >= 3)
                    gameMode = "hvhva";
                else if (activeCount == 2 && hasAlien)
                    gameMode = "hva";
                else
                    gameMode = "hvh";
                log.Msg($"Detected game mode: {gameMode} ({activeCount} active teams, alien={hasAlien})");

                // Load config filtered by game mode
                var config = LoadMapConfig(gameMode);
                if (config == null)
                {
                    log.Msg("SpawnBaseStructures: No config — letting game handle spawn positions");
                    return true;
                }

                if (!config.HasSpawns())
                {
                    log.Msg("SpawnBaseStructures: No spawn positions in config — letting game handle");
                    _activeConfig = config; // still use config for resources
                    return true;
                }

                _activeConfig = config;

                // For HvH/HvHvA: randomly swap Sol↔Cent positions for extra variation
                _swappedSolCent = false;
                if ((gameMode == "hvh" || gameMode == "hvhva") && UnityEngine.Random.Range(0, 2) == 1)
                {
                    _swappedSolCent = true;
                    log.Msg($"{gameMode}: Swapping Sol ↔ Cent spawn positions");
                }

                log.Msg("SpawnBaseStructures: Overriding HQ positions from config");

                var usedPoints = new List<Transform>();

                // Reflection helpers cached per call
                var idxField = _strategyTeamSetupType?.GetField("BaseStructureStartPointIndex",
                    BindingFlags.Public | BindingFlags.Instance);
                var startPointsField = _strategyTeamSetupType?.GetField("BaseStructureStartPoints",
                    BindingFlags.Public | BindingFlags.Instance);
                var baseStructureField = _teamType?.GetField("BaseStructure",
                    BindingFlags.Public | BindingFlags.Instance);
                var baseStructureProp = _teamType?.GetProperty("BaseStructure",
                    BindingFlags.Public | BindingFlags.Instance);

                // Compute map center and spawn radius for default placement (same as game)
                Vector3 mapCenter = Vector3.zero;
                float spawnRadius = 1000f;
                var terrain = Terrain.activeTerrain;
                if (terrain != null)
                {
                    var extents = terrain.terrainData.bounds.extents;
                    mapCenter = terrain.transform.position + terrain.terrainData.bounds.center;
                    spawnRadius = (extents.x + extents.z) * 0.5f * 0.5f;
                }
                float randomAngle = UnityEngine.Random.Range(0f, 360f);
                float angleStep = activeCount > 0 ? 360f / activeCount : 0f;
                int activeIdx = 0;

                for (int i = 0; i < count; i++)
                {
                    var setup = itemProp?.GetValue(teamSetups, new object[] { i });
                    if (setup == null) continue;

                    bool isActive = getActiveMethod != null &&
                        (bool)(getActiveMethod.Invoke(__instance, new[] { setup }) ?? false);
                    if (!isActive) continue;

                    // Get Team
                    object? team = GetTeamFromSetup(setup);
                    if (team == null) { activeIdx++; continue; }

                    string teamName = GetTeamName(team);

                    // Match to config spawn position (handles both web tool and legacy formats)
                    // If swapped, Sol reads Cent's position and vice versa
                    string faction = teamName.Contains("Sol") ? "Sol"
                                   : teamName.Contains("Cent") ? "Cent"
                                   : teamName.Contains("Alien") ? "Alien" : "";
                    string lookupFaction = faction;
                    if (_swappedSolCent)
                    {
                        if (faction == "Sol") lookupFaction = "Cent";
                        else if (faction == "Cent") lookupFaction = "Sol";
                    }
                    SpawnPos? spawnPos = !string.IsNullOrEmpty(lookupFaction)
                        ? config.GetFactionSpawn(lookupFaction, log) : null;

                    // Get start points for this team
                    var startPoints = startPointsField?.GetValue(setup) as List<Transform>;

                    if (spawnPos != null)
                    {
                        // === Configured position: spawn at our coords ===
                        float worldX = spawnPos.X;
                        float worldZ = spawnPos.Z;
                        float terrainY = SampleTerrainHeight(worldX, worldZ, log);
                        Vector3 targetPos = new Vector3(worldX, terrainY, worldZ);

                        SpawnAtBestStartPoint(setup, team, startPoints, targetPos, usedPoints,
                            idxField, baseStructureField, baseStructureProp, log, teamName,
                            useExactPosition: true);
                    }
                    else
                    {
                        // === No config for this team: use game's default logic ===
                        // Compute target direction (same algorithm as original SpawnBaseStructures)
                        float yaw = randomAngle + angleStep * activeIdx;
                        float rad = yaw * Mathf.Deg2Rad;
                        float dist = spawnRadius * UnityEngine.Random.Range(0.8f, 1.2f);
                        Vector3 target = mapCenter + new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad)) * dist;

                        SpawnAtBestStartPoint(setup, team, startPoints, target, usedPoints,
                            idxField, baseStructureField, baseStructureProp, log, teamName,
                            useExactPosition: false);
                        log.Msg($"  {teamName}: Using default placement (no config position)");
                    }

                    activeIdx++;
                }

                return false; // skip original
            }
            catch (Exception ex)
            {
                log.Error($"SpawnBaseStructures override failed: {ex}");
                return true;
            }
        }

        private static void SpawnAtBestStartPoint(object setup, object team,
            List<Transform>? startPoints, Vector3 target, List<Transform> usedPoints,
            FieldInfo? idxField, FieldInfo? baseStructureField, PropertyInfo? baseStructureProp,
            MelonLogger.Instance log, string teamName, bool useExactPosition)
        {
            if (startPoints == null || startPoints.Count == 0)
            {
                log.Warning($"  {teamName}: No BaseStructureStartPoints — cannot spawn");
                return;
            }

            // Find closest unused start point to target
            int bestIdx = 0;
            float bestDist = float.MaxValue;
            Transform? bestPoint = null;
            for (int k = 0; k < startPoints.Count; k++)
            {
                var pt = startPoints[k];
                if (usedPoints.Contains(pt)) continue;
                float dx = pt.position.x - target.x;
                float dz = pt.position.z - target.z;
                float dist = dx * dx + dz * dz;
                if (bestPoint == null || dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = k;
                    bestPoint = pt;
                }
            }

            if (bestPoint != null)
                usedPoints.Add(bestPoint);

            // Set BaseStructureStartPointIndex for client sync
            idxField?.SetValue(setup, bestIdx);

            // Get BaseStructure.Prefab and spawn
            object? baseStructure = baseStructureProp?.GetValue(team) ?? baseStructureField?.GetValue(team);
            if (baseStructure == null)
            {
                log.Error($"  {teamName}: BaseStructure is null");
                return;
            }

            var prefabProp = baseStructure.GetType().GetProperty("Prefab", BindingFlags.Public | BindingFlags.Instance);
            var prefabField = baseStructure.GetType().GetField("Prefab", BindingFlags.Public | BindingFlags.Instance);
            var prefab = (prefabProp?.GetValue(baseStructure) ?? prefabField?.GetValue(baseStructure)) as GameObject;

            if (prefab == null)
            {
                log.Error($"  {teamName}: BaseStructure.Prefab is null");
                return;
            }

            var rotation = bestPoint?.rotation ?? Quaternion.identity;
            // For configured positions: spawn at exact target coords
            // For default placement: spawn at the predefined start point's position (like the game does)
            Vector3 spawnPos = useExactPosition ? target : (bestPoint?.position ?? target);

            // Move the start point transform to match spawn position so starter units
            // (which the game spawns relative to the start point) appear near the HQ
            if (useExactPosition && bestPoint != null)
            {
                log.Msg($"  {teamName}: Relocating start point from ({bestPoint.position.x:F0},{bestPoint.position.z:F0}) to ({spawnPos.x:F0},{spawnPos.z:F0})");
                bestPoint.position = spawnPos;
            }

            SpawnHQPrefab(prefab, team, spawnPos, rotation, log);
            log.Msg($"  {teamName}: Spawned HQ at ({spawnPos.x:F0}, {spawnPos.z:F0}) — startPointIdx={bestIdx}");
        }

        private static object? GetTeamFromSetup(object setup)
        {
            // Walk up the type hierarchy to find Team field/property
            var type = setup.GetType();
            while (type != null)
            {
                var prop = type.GetProperty("Team", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (prop != null) return prop.GetValue(setup);
                var field = type.GetField("Team", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(setup);
                type = type.BaseType;
            }
            return null;
        }

        private static string GetTeamName(object team)
        {
            var prop = _teamType?.GetProperty("TeamName", BindingFlags.Public | BindingFlags.Instance);
            var field = _teamType?.GetField("TeamName", BindingFlags.Public | BindingFlags.Instance);
            return (string)(prop?.GetValue(team) ?? field?.GetValue(team) ?? "");
        }

        private static MethodInfo? FindMethod(Type type, string name)
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
            if (method != null) return method;
            var baseType = type.BaseType;
            while (baseType != null)
            {
                method = baseType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
                if (method != null) return method;
                baseType = baseType.BaseType;
            }
            return null;
        }

        private static void SpawnHQPrefab(GameObject prefab, object team, Vector3 position, Quaternion rotation,
            MelonLogger.Instance log)
        {
            if (_spawnPrefabMethod == null)
            {
                log.Error("SpawnPrefab method not resolved");
                return;
            }

            // Game.SpawnPrefab(GameObject, Player, Team, Vector3, Quaternion, bool, bool)
            var paramCount = _spawnPrefabMethod.GetParameters().Length;
            object?[] args;
            if (paramCount >= 7)
                args = new object?[] { prefab, null, team, position, rotation, true, true };
            else if (paramCount >= 5)
                args = new object?[] { prefab, null, team, position, rotation };
            else
                args = new object?[] { prefab, null, team, position, rotation };

            _spawnPrefabMethod.Invoke(null, args);
        }

        private static bool Prefix_DistributeAllResources(float resourceMultiplier, float disableAmount01)
        {
            var log = Melon<MapBalance>.Logger;
            log.Msg($"=== DistributeAllResources intercepted (mult={resourceMultiplier}, hide={disableAmount01}) ===");

            try
            {
                // Load config early — if no config exists for this map, let vanilla handle it
                if (_activeConfig == null)
                    _activeConfig = LoadMapConfig();

                if (_activeConfig == null)
                {
                    log.Msg("No map config — letting game handle resource distribution (vanilla)");
                    return true; // run original game logic
                }

                CustomDistribution(resourceMultiplier);
            }
            catch (Exception ex)
            {
                log.Error($"Custom distribution failed: {ex}");
                return true; // fallback: let original run
            }

            return false; // skip original
        }

        [HarmonyPatch(typeof(MusicJukeboxHandler), "OnGameStarted")]
        private static class Patch_OnGameStarted
        {
            static void Postfix()
            {
                try
                {
                    _dumpedThisRound = false;

                    // Announce selected config in allchat
                    if (_activeConfig != null && _sendServerChatMethod != null)
                    {
                        string swap = _swappedSolCent ? " (swapped)" : "";
                        string msg = $"Map Balance: {_activeConfigName}{swap}";
                        _sendServerChatMethod.Invoke(null, new object[] { msg });
                        Melon<MapBalance>.Logger.Msg($"Allchat: {msg}");
                    }
                }
                catch (Exception ex)
                {
                    Melon<MapBalance>.Logger.Error($"OnGameStarted failed: {ex}");
                }
            }
        }

        [HarmonyPatch(typeof(MusicJukeboxHandler), "OnGameEnded")]
        private static class Patch_OnGameEnded
        {
            static void Postfix()
            {
                _dumpedThisRound = false;
                _activeConfig = null;
                _activeConfigName = "";
                _swappedSolCent = false;
                CleanupSpawnedAreas();
            }
        }

        // === Designed Resource Positions (v5.0) ===
        // 64 positions: 48 starter (3 per spawn) + 16 center/expansion
        // Spawns: 16 points along 4 map edges (1200m spacing, 600m from border)
        // All validated against refinery scan (ramp facing resource)
        private static readonly float[][] DesignedPositions = new float[][] {
            // South edge starters
            new float[] { -2153f, -2153f }, // corner (-2400,-2400) #0
            new float[] { -2400f, -2046f }, // corner (-2400,-2400) #1
            new float[] { -2046f, -2400f }, // corner (-2400,-2400) #2
            new float[] { -1200f, -2050f }, // spawn (-1200,-2400) #0
            new float[] { -1450f, -2150f }, // spawn (-1200,-2400) #1
            new float[] {  -950f, -2150f }, // spawn (-1200,-2400) #2
            new float[] {     0f, -2050f }, // spawn (0,-2400) #0
            new float[] {  -250f, -2150f }, // spawn (0,-2400) #1
            new float[] {   250f, -2150f }, // spawn (0,-2400) #2
            new float[] {  1200f, -2050f }, // spawn (1200,-2400) #0
            new float[] {   950f, -2150f }, // spawn (1200,-2400) #1
            new float[] {  1450f, -2150f }, // spawn (1200,-2400) #2
            new float[] {  2153f, -2153f }, // corner (2400,-2400) #0
            new float[] {  2046f, -2400f }, // corner (2400,-2400) #1
            new float[] {  2400f, -2046f }, // corner (2400,-2400) #2
            // North edge starters
            new float[] { -2153f,  2153f }, // corner (-2400,2400) #0
            new float[] { -2046f,  2400f }, // corner (-2400,2400) #1
            new float[] { -2400f,  2046f }, // corner (-2400,2400) #2
            new float[] { -1200f,  2050f }, // spawn (-1200,2400) #0
            new float[] {  -950f,  2150f }, // spawn (-1200,2400) #1
            new float[] { -1450f,  2150f }, // spawn (-1200,2400) #2
            new float[] {     0f,  2050f }, // spawn (0,2400) #0
            new float[] {   250f,  2150f }, // spawn (0,2400) #1
            new float[] {  -250f,  2150f }, // spawn (0,2400) #2
            new float[] {  1200f,  2050f }, // spawn (1200,2400) #0
            new float[] {  1450f,  2150f }, // spawn (1200,2400) #1
            new float[] {   950f,  2150f }, // spawn (1200,2400) #2
            new float[] {  2153f,  2153f }, // corner (2400,2400) #0
            new float[] {  2400f,  2046f }, // corner (2400,2400) #1
            new float[] {  2046f,  2400f }, // corner (2400,2400) #2
            // West edge starters (non-corner)
            new float[] { -2050f, -1200f }, // spawn (-2400,-1200) #0
            new float[] { -2150f,  -950f }, // spawn (-2400,-1200) #1
            new float[] { -2150f, -1450f }, // spawn (-2400,-1200) #2
            new float[] { -2050f,     0f }, // spawn (-2400,0) #0
            new float[] { -2150f,   250f }, // spawn (-2400,0) #1
            new float[] { -2150f,  -250f }, // spawn (-2400,0) #2
            new float[] { -2050f,  1200f }, // spawn (-2400,1200) #0
            new float[] { -2150f,  1450f }, // spawn (-2400,1200) #1
            new float[] { -2150f,   950f }, // spawn (-2400,1200) #2
            // East edge starters (non-corner)
            new float[] {  2050f, -1200f }, // spawn (2400,-1200) #0
            new float[] {  2150f, -1450f }, // spawn (2400,-1200) #1
            new float[] {  2150f,  -950f }, // spawn (2400,-1200) #2
            new float[] {  2050f,     0f }, // spawn (2400,0) #0
            new float[] {  2150f,  -250f }, // spawn (2400,0) #1
            new float[] {  2150f,   250f }, // spawn (2400,0) #2
            new float[] {  2050f,  1200f }, // spawn (2400,1200) #0
            new float[] {  2150f,   950f }, // spawn (2400,1200) #1
            new float[] {  2150f,  1450f }, // spawn (2400,1200) #2
            // Center / expansion patches
            new float[] {   400f,     0f }, // inner cross
            new float[] {     0f,   400f },
            new float[] {  -400f,     0f },
            new float[] {     0f,  -400f },
            new float[] {   600f,   600f }, // mid diagonal
            new float[] {  -600f,   600f },
            new float[] {  -600f,  -600f },
            new float[] {   600f,  -600f },
            new float[] {  1100f,     0f }, // expansion cross
            new float[] {     0f,  1100f },
            new float[] { -1100f,     0f },
            new float[] {     0f, -1100f },
            new float[] {  1000f,  1000f }, // deep diagonal
            new float[] { -1000f,  1000f },
            new float[] { -1000f, -1000f },
            new float[] {  1000f, -1000f },
        };

        // === Custom Distribution ===

        private static void CustomDistribution(float resourceMultiplier)
        {
            var log = Melon<MapBalance>.Logger;
            if (_resourceAreaType == null) return;

            // _activeConfig is guaranteed non-null here (checked in prefix)

            // --- Step 1: Disable all existing scene ResourceAreas ---
            var allAreas = GetAllResourceAreas();
            if (allAreas.Count == 0)
            {
                log.Warning("No ResourceAreas found");
                return;
            }

            int balCount = 0, bioCount = 0;
            foreach (var area in allAreas)
            {
                string resName = GetResourceName(area);
                if (resName == "Balterium") balCount++;
                else if (resName == "Biotics") bioCount++;
                ZeroOutResourceArea(area);
            }
            log.Msg($"Zeroed {allAreas.Count} existing areas (Balterium: {balCount}, Biotics: {bioCount})");

            // Sync zeroed state to clients
            SyncAllToClients(allAreas);

            // --- Step 2: Clean up any previously spawned areas ---
            CleanupSpawnedAreas();

            // --- Step 3: Configure resource areas from config ---
            SpawnConfiguredResources(_activeConfig!, resourceMultiplier, log);

            // Sync re-enabled areas to clients
            var updatedAreas = GetAllResourceAreas();
            SyncAllToClients(updatedAreas);

            int enabledCount = CountEnabledAreas();
            log.Msg($"=== Distribution complete: {enabledCount} total enabled areas ===");
        }

        private static GameObject? GetResourcePrefab(string prefabName, MelonLogger.Instance log)
        {
            int idx = (int)_getPrefabIndexMethod!.Invoke(null, new object[] { prefabName })!;
            if (idx < 0)
            {
                log.Warning($"Prefab '{prefabName}' not found");
                return null;
            }
            var prefab = _getPrefabMethod!.Invoke(null, new object[] { idx }) as GameObject;
            if (prefab != null)
                log.Msg($"Found prefab: {prefab.name} (index={idx})");
            return prefab;
        }

        private static void SpawnConfiguredResources(
            MapConfig config, float resourceMultiplier,
            MelonLogger.Instance log)
        {
            var removedSet = new HashSet<int>(config.Resources.RemovedPatches);
            var overrides = config.Resources.PatchOverrides;

            // Re-enable existing scene resource areas at their original positions.
            // The AllResourceAreas list ordering matches the web tool's dump indices,
            // so removed_patches indices from the config map directly to list positions.
            var allAreas = GetAllResourceAreas();
            log.Msg($"Found {allAreas.Count} existing resource areas in scene");

            if (removedSet.Count > 0)
                log.Msg($"  Removed patches: [{string.Join(", ", removedSet)}]");
            if (overrides.Count > 0)
                log.Msg($"  Patch overrides: {overrides.Count} entries");

            int baltEnabled = 0, bioEnabled = 0, skipped = 0, overridden = 0;

            for (int i = 0; i < allAreas.Count; i++)
            {
                var area = allAreas[i];
                string resName = GetResourceName(area);
                bool isBalt = resName == "Balterium";
                bool isBio = resName == "Biotics";

                // Skip removed patches — leave them zeroed/disabled
                if (removedSet.Contains(i))
                {
                    skipped++;
                    continue;
                }

                // Determine target amount: check patch_overrides first, then uniform type amount
                string idxKey = i.ToString();
                int targetAmount = 0;
                bool isOverride = false;

                if (overrides.ContainsKey(idxKey))
                {
                    targetAmount = overrides[idxKey];
                    isOverride = true;
                }
                else if (isBalt && config.Resources.BalteriumAmount > 0)
                    targetAmount = config.Resources.BalteriumAmount;
                else if (isBio && config.Resources.BioticsAmount > 0)
                    targetAmount = config.Resources.BioticsAmount;

                if (targetAmount <= 0) continue;

                // Re-enable the area and set configured amount
                SetEnabled(area, true);
                SetMemberValue(area, "ResourceDistributionAmountMult", resourceMultiplier);
                CallDistributeResources(area);
                SetResourceAmount(area, targetAmount, log);

                if (isOverride)
                {
                    overridden++;
                    var pos = (area as Component)?.transform.position ?? Vector3.zero;
                    log.Msg($"OVERRIDE [{i}] {resName}: ({pos.x:F0},{pos.z:F0}) amount={targetAmount}");
                }
                else if (isBalt)
                {
                    baltEnabled++;
                    if (baltEnabled <= 3)
                    {
                        var pos = (area as Component)?.transform.position ?? Vector3.zero;
                        log.Msg($"BALT [{i}]: ({pos.x:F0},{pos.z:F0}) amount={targetAmount}");
                    }
                }
                else if (isBio)
                {
                    bioEnabled++;
                    if (bioEnabled <= 3)
                    {
                        var pos = (area as Component)?.transform.position ?? Vector3.zero;
                        log.Msg($"BIO  [{i}]: ({pos.x:F0},{pos.z:F0}) amount={targetAmount}");
                    }
                }
            }

            log.Msg($"Enabled {baltEnabled} Balterium, {bioEnabled} Biotics areas ({overridden} overridden, {skipped} removed)");
        }

        private static void SpawnLegacyPositions(
            GameObject baltPrefab, Terrain? terrain, float terrainY0,
            float resourceMultiplier, MelonLogger.Instance log)
        {
            int paramCount = _spawnPrefabMethod!.GetParameters().Length;
            int spawned = 0;

            for (int i = 0; i < DesignedPositions.Length; i++)
            {
                float tx = DesignedPositions[i][0];
                float tz = DesignedPositions[i][1];
                var go = SpawnResourceArea(baltPrefab, tx, tz, terrain, terrainY0, paramCount, log);
                if (go == null) continue;

                _spawnedAreas.Add(go);
                var areaComp = go.GetComponent(_resourceAreaType!);
                if (areaComp != null)
                {
                    SetMemberValue(areaComp, "ResourceDistributionAmountMult", resourceMultiplier);
                    CallDistributeResources(areaComp);
                }

                spawned++;
                if (i < 3 || i >= DesignedPositions.Length - 2)
                    log.Msg($"SPAWN [{i}]: ({tx:F0},{tz:F0}) -> {go.name}");
            }

            log.Msg($"Legacy: spawned {spawned}/{DesignedPositions.Length} Balterium areas");
        }

        private static GameObject? SpawnResourceArea(
            GameObject prefab, float tx, float tz,
            Terrain? terrain, float terrainY0, int paramCount,
            MelonLogger.Instance log)
        {
            float ty = terrainY0;
            if (terrain != null)
            {
                try { ty = terrain.SampleHeight(new Vector3(tx, 0f, tz)) + terrainY0; }
                catch { }
            }

            var pos = new Vector3(tx, ty, tz);
            var rot = Quaternion.identity;

            object?[] args;
            if (paramCount >= 7)
                args = new object?[] { prefab, null, null, pos, rot, true, true };
            else
                args = new object?[] { prefab, null, null, pos, rot };

            try
            {
                return _spawnPrefabMethod!.Invoke(null, args) as GameObject;
            }
            catch (Exception ex)
            {
                log.Error($"SpawnPrefab failed at ({tx:F0},{tz:F0}): {ex.Message}");
                return null;
            }
        }

        private static void SetResourceAmount(Component area, int targetAmount, MelonLogger.Instance log)
        {
            // Set ResourceAmountMax and ResourceAmountCurrent to exact value
            // Also scale ResourceDistributionAmountMult to produce correct total
            // after DistributeResources fills the grid cells

            // Read current total after distribution
            int currentMax = GetMemberValueInt(area, "ResourceAmountMax");

            if (currentMax > 0 && currentMax != targetAmount)
            {
                // Scale the multiplier to hit target
                float currentMult = GetMemberValue<float>(area, "ResourceDistributionAmountMult");
                float scaleFactor = (float)targetAmount / (float)currentMax;
                float newMult = currentMult * scaleFactor;

                SetMemberValue(area, "ResourceDistributionAmountMult", newMult);
                CallDistributeResources(area);

                // Verify
                int newMax = GetMemberValueInt(area, "ResourceAmountMax");
                if (Math.Abs(newMax - targetAmount) > targetAmount * 0.05f)
                    log.Warning($"  Amount mismatch: target={targetAmount}, got={newMax} (mult={newMult:F4})");
            }
            else if (currentMax == 0)
            {
                // Area has no cells — set directly
                SetMemberValueInt(area, "ResourceAmountMax", targetAmount);
                SetMemberValueInt(area, "ResourceAmountCurrent", targetAmount);
            }
        }

        private static void CleanupSpawnedAreas()
        {
            var log = Melon<MapBalance>.Logger;
            int count = 0;
            foreach (var go in _spawnedAreas)
            {
                if (go != null)
                {
                    UnityEngine.Object.Destroy(go);
                    count++;
                }
            }
            _spawnedAreas.Clear();
            if (count > 0)
                log.Msg($"Cleaned up {count} previously spawned areas");
        }

        // === Config Loading ===

        private static readonly string SpawnsDir = Path.Combine("UserData", "Spawns");
        private static readonly string LegacyConfigDir = Path.Combine("UserData", "MapBalance", "configs");

        private static MapConfig? LoadMapConfig(string gameMode = "hvh")
        {
            var log = Melon<MapBalance>.Logger;
            string? mapName = GetCurrentMapName();
            if (string.IsNullOrEmpty(mapName))
            {
                log.Warning("Cannot detect map name");
                return null;
            }

            string? configPath = null;

            // Primary: scan UserData/Spawns/{mapName}/ for *.json files, filtered by game mode
            string mapSpawnsDir = Path.Combine(SpawnsDir, mapName);
            if (Directory.Exists(mapSpawnsDir))
            {
                var jsonFiles = Directory.GetFiles(mapSpawnsDir, "*.json");
                // Filter by game_modes if available
                var matching = new List<string>();
                foreach (var file in jsonFiles)
                {
                    try
                    {
                        var obj = JObject.Parse(File.ReadAllText(file));
                        var modes = obj["game_modes"];
                        if (modes == null)
                        {
                            // No game_modes field → accept for any mode
                            matching.Add(file);
                        }
                        else if (modes[gameMode]?.Value<bool>() == true)
                        {
                            matching.Add(file);
                        }
                    }
                    catch { }
                }

                log.Msg($"Found {jsonFiles.Length} layouts for {mapName}, {matching.Count} match game mode '{gameMode}'");

                if (matching.Count > 0)
                {
                    int idx = matching.Count == 1 ? 0 : UnityEngine.Random.Range(0, matching.Count);
                    configPath = matching[idx];
                }
                else if (jsonFiles.Length > 0)
                {
                    // No mode-specific match — fall back to any config
                    log.Warning($"No layouts match game mode '{gameMode}' — picking from all");
                    int idx = jsonFiles.Length == 1 ? 0 : UnityEngine.Random.Range(0, jsonFiles.Length);
                    configPath = jsonFiles[idx];
                }
            }

            // Fallback: legacy UserData/MapBalance/configs/{mapName}.json
            if (configPath == null)
            {
                string legacyPath = Path.Combine(LegacyConfigDir, $"{mapName}.json");
                if (File.Exists(legacyPath))
                    configPath = legacyPath;
            }

            // Fallback: scan legacy dir for any JSON where "map" field matches
            if (configPath == null && Directory.Exists(LegacyConfigDir))
            {
                foreach (var file in Directory.GetFiles(LegacyConfigDir, "*.json"))
                {
                    try
                    {
                        var obj = JObject.Parse(File.ReadAllText(file));
                        if (obj["map"]?.ToString() == mapName)
                        {
                            configPath = file;
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (configPath == null)
            {
                log.Msg($"No config found for map '{mapName}' — vanilla mode");
                return null;
            }

            try
            {
                string json = File.ReadAllText(configPath);
                var config = JsonConvert.DeserializeObject<MapConfig>(json);
                if (config == null)
                {
                    log.Warning($"Failed to parse config: {configPath}");
                    return null;
                }

                _activeConfigName = Path.GetFileNameWithoutExtension(configPath);
                log.Msg($"=== Loaded map config: {configPath} ===");
                log.Msg($"  Map: {config.Map}");
                if (!string.IsNullOrEmpty(config.Description))
                    log.Msg($"  Description: {config.Description}");
                log.Msg($"  Balterium: {config.Resources.BalteriumAmount}, Biotics: {config.Resources.BioticsAmount}");
                if (config.Resources.RemovedPatches.Count > 0)
                    log.Msg($"  Removed patches: [{string.Join(", ", config.Resources.RemovedPatches)}]");
                if (config.Resources.PatchOverrides.Count > 0)
                    log.Msg($"  Patch overrides: {config.Resources.PatchOverrides.Count} entries");
                log.Msg($"  Has spawns: {config.HasSpawns()}");

                return config;
            }
            catch (Exception ex)
            {
                log.Error($"Error loading config {configPath}: {ex.Message}");
                return null;
            }
        }

        // SelectSpawnVariant removed — replaced by MapConfig.GetFactionSpawn()

        private static void ZeroOutResourceArea(Component area)
        {
            SetEnabled(area, false);

            var amountsField = _resourceAreaType!.GetField("ResourceAmounts",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var amountsProp = _resourceAreaType.GetProperty("ResourceAmounts",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            object? amounts = amountsField?.GetValue(area) ?? amountsProp?.GetValue(area);

            if (amounts is int[,] arr)
            {
                int ax = arr.GetLength(0);
                int az = arr.GetLength(1);

                for (int x = 0; x < ax; x++)
                    for (int z = 0; z < az; z++)
                        arr[x, z] = 0;

                var changedField = _resourceAreaType.GetField("Net_ResourceChanged",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var changedProp = _resourceAreaType.GetProperty("Net_ResourceChanged",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                object? changed = changedField?.GetValue(area) ?? changedProp?.GetValue(area);
                if (changed is bool[,] changedArr)
                {
                    for (int x = 0; x < ax; x++)
                        for (int z = 0; z < az; z++)
                            changedArr[x, z] = true;
                }
            }

            SetMemberValueInt(area, "ResourceAmountCurrent", 0);
            SetMemberValueInt(area, "ResourceAmountMax", 0);
        }

        private static void SyncAllToClients(List<Component> allAreas)
        {
            var log = Melon<MapBalance>.Logger;

            if (_sendNetInitMethod == null)
            {
                log.Warning("SendNetInit method not found — cannot sync to clients");
                return;
            }

            int synced = 0;
            int failed = 0;

            foreach (var area in allAreas)
            {
                try
                {
                    var nc = GetNetworkComponent(area);
                    if (nc == null)
                    {
                        failed++;
                        continue;
                    }

                    var paramCount = _sendNetInitMethod.GetParameters().Length;
                    if (paramCount == 0)
                        _sendNetInitMethod.Invoke(nc, null);
                    else if (paramCount == 1)
                        _sendNetInitMethod.Invoke(nc, new object?[] { null });
                    else
                        _sendNetInitMethod.Invoke(nc, new object?[paramCount]);

                    synced++;
                }
                catch (Exception ex)
                {
                    if (failed == 0)
                        log.Warning($"SendNetInit failed: {ex.Message}");
                    failed++;
                }
            }

            log.Msg($"Synced {synced} areas to clients ({failed} failed)");
        }

        private static object? GetNetworkComponent(Component area)
        {
            var ncField = _resourceAreaType!.GetField("NetworkComponent",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (ncField != null) return ncField.GetValue(area);

            var ncProp = _resourceAreaType.GetProperty("NetworkComponent",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (ncProp != null) return ncProp.GetValue(area);

            if (_networkComponentType != null)
            {
                var getComp = typeof(GameObject).GetMethod("GetComponent",
                    new[] { typeof(Type) });
                return getComp?.Invoke(area.gameObject, new object[] { _networkComponentType });
            }

            return null;
        }

        // === Refinery Placement Scan ===

        private class RefineryCheckData
        {
            public string Name = "";
            public float GridSnapXZ = 10f;
            public float GridSnapY = 5f;
            public bool RotateToSurface;
            public float MaxAngle = 30f;
            public List<Vector3> AboveGroundLocal = new List<Vector3>();
            public List<Vector3> BelowGroundLocal = new List<Vector3>();
            public List<EntryCheckData> PreviewEntries = new List<EntryCheckData>();
            public List<RampCheckData> Ramps = new List<RampCheckData>();
        }

        private class EntryCheckData
        {
            public List<Vector3> AboveLocal = new List<Vector3>();
            public List<Vector3> BelowLocal = new List<Vector3>();
            public int MustAboveCount;
            public int MustBelowCount;
        }

        private class RampCheckData
        {
            public Vector3 CheckPointLocal;
            public Vector3 EntryLocal;
            public bool IsAboveType;
            public bool HasDepositPoint;
        }

        /// <summary>
        /// Extract building placement check data from a ConstructionData asset.
        /// Works for any building type (Refinery, Headquarters, etc.)
        /// </summary>
        private static RefineryCheckData? ExtractBuildingData(Func<string, bool> nameFilter, string label)
        {
            var log = Melon<MapBalance>.Logger;

            if (_constructionDataType == null) { log.Error("ConstructionData type not found"); return null; }
            if (_constructionPreviewType == null) { log.Error("ConstructionPreview type not found"); return null; }

            var allCD = Resources.FindObjectsOfTypeAll(_constructionDataType);

            UnityEngine.Object? targetCD = null;
            foreach (var cd in allCD)
            {
                var uo = cd as UnityEngine.Object;
                if (uo != null && nameFilter(uo.name))
                {
                    targetCD = uo;
                    log.Msg($"Found {label}: {uo.name}");
                    break;
                }
            }

            if (targetCD == null)
            {
                log.Warning($"{label} not found in {allCD.Length} ConstructionData assets");
                return null;
            }

            var data = new RefineryCheckData();
            data.Name = targetCD.name;

            // Grid snap values
            data.GridSnapXZ = GetMemberValue<float>(targetCD, "GridSnapXZ");
            data.GridSnapY = GetMemberValue<float>(targetCD, "GridSnapY");
            if (data.GridSnapXZ <= 0f) data.GridSnapXZ = 10f;
            if (data.GridSnapY <= 0f) data.GridSnapY = 5f;
            log.Msg($"[{label}] Grid: XZ={data.GridSnapXZ}, Y={data.GridSnapY}");

            // Get ObjectPreviewPrefab
            var previewPrefab = GetMemberValue<GameObject>(targetCD, "ObjectPreviewPrefab");
            if (previewPrefab == null) { log.Error($"[{label}] ObjectPreviewPrefab is null"); return null; }

            // Get ConstructionPreview component
            var preview = previewPrefab.GetComponent(_constructionPreviewType);
            if (preview == null) { log.Error($"[{label}] ConstructionPreview not found on preview prefab"); return null; }

            // Config
            data.RotateToSurface = GetMemberValue<bool>(preview, "StructureRotateToSurface");
            data.MaxAngle = GetMemberValue<float>(preview, "StructureRotateMaxAngle");
            log.Msg($"[{label}] RotateToSurface={data.RotateToSurface}, MaxAngle={data.MaxAngle}");

            // AboveGroundPoints
            data.AboveGroundLocal = ExtractTransformLocalPositions(preview, "AboveGroundPoints");
            log.Msg($"[{label}] AboveGroundPoints: {data.AboveGroundLocal.Count}");

            // BelowGroundPoints
            data.BelowGroundLocal = ExtractTransformLocalPositions(preview, "BelowGroundPoints");
            log.Msg($"[{label}] BelowGroundPoints: {data.BelowGroundLocal.Count}");

            // PreviewEntries
            var entriesRaw = GetRawMember(preview, "PreviewEntries");
            if (entriesRaw is System.Collections.IList entriesList)
            {
                log.Msg($"[{label}] PreviewEntries: {entriesList.Count}");
                foreach (var entry in entriesList)
                {
                    if (entry == null) continue;
                    var ed = new EntryCheckData();
                    ed.AboveLocal = ExtractTransformLocalPositions(entry, "AboveGroundPoints");
                    ed.BelowLocal = ExtractTransformLocalPositions(entry, "BelowGroundPoints");
                    ed.MustAboveCount = GetMemberValue<int>(entry, "MustBeAboveGroundCount");
                    ed.MustBelowCount = GetMemberValue<int>(entry, "MustBeBelowGroundCount");
                    data.PreviewEntries.Add(ed);
                    log.Msg($"  Entry: above={ed.AboveLocal.Count}(need>={ed.MustAboveCount}), below={ed.BelowLocal.Count}(need>={ed.MustBelowCount})");
                }
            }

            // EnableAboveGround from structure prefab (ramp/terrain checks)
            var structurePrefab = GetMemberValue<GameObject>(targetCD, "ObjectToBuild");
            if (structurePrefab == null)
            {
                var objectInfo = GetRawMember(targetCD, "ObjectInfo");
                if (objectInfo != null)
                    structurePrefab = GetMemberValue<GameObject>(objectInfo, "Prefab");
            }

            if (structurePrefab != null && _enableAboveGroundType != null)
            {
                log.Msg($"[{label}] Structure prefab: {structurePrefab.name}");

                var eag = structurePrefab.GetComponentInChildren(_enableAboveGroundType, true);
                if (eag != null)
                {
                    Transform structRoot = structurePrefab.transform;
                    ExtractRampPoints(eag, "EnableObjectIfPointAboveGround", true, structRoot, data.Ramps, log);
                    ExtractRampPoints(eag, "EnableObjectIfPointUnderGround", false, structRoot, data.Ramps, log);
                    log.Msg($"[{label}] Terrain check points: {data.Ramps.Count}");
                }
                else
                {
                    log.Msg($"[{label}] No EnableAboveGround component (OK for HQ)");
                }
            }

            // ConstructionBounds
            if (_constructionBoundsType != null)
            {
                var boundsComps = previewPrefab.GetComponentsInChildren(_constructionBoundsType, true);
                log.Msg($"[{label}] ConstructionBounds: {boundsComps.Length}");
                foreach (var cb in boundsComps)
                {
                    if (cb == null) continue;
                    var size = GetMemberValue<Vector3>(cb, "Size");
                    var boundsType = GetRawMember(cb, "BoundsType");
                    var localPos = ((Component)cb).transform.localPosition;
                    log.Msg($"  {boundsType} size=({size.x:F1},{size.y:F1},{size.z:F1}) localPos=({localPos.x:F1},{localPos.y:F1},{localPos.z:F1})");
                }
            }

            return data;
        }

        private static RefineryCheckData? ExtractRefineryData()
        {
            return ExtractBuildingData(
                name => name.Contains("Sol") && name.Contains("Refinery")
                        && !name.Contains("Turret") && !name.Contains("Upgrade"),
                "Sol_Refinery");
        }

        private static void ExtractRampPoints(object enableAboveGround, string listName, bool isAboveType,
            Transform structRoot, List<RampCheckData> ramps, MelonLogger.Instance log)
        {
            var raw = GetRawMember(enableAboveGround, listName);
            if (!(raw is System.Collections.IList list)) return;

            log.Msg($"  {listName}: {list.Count} entries");

            foreach (var item in list)
            {
                if (item == null) continue;
                var ramp = new RampCheckData();
                ramp.IsAboveType = isAboveType;

                // Point (GameObject) — get position relative to structure root
                var pointGO = GetMemberValue<GameObject>(item, "Point");
                if (pointGO != null)
                {
                    ramp.CheckPointLocal = structRoot.InverseTransformPoint(pointGO.transform.position);
                    log.Msg($"    CheckPoint: ({ramp.CheckPointLocal.x:F2}, {ramp.CheckPointLocal.y:F2}, {ramp.CheckPointLocal.z:F2})");
                }

                // DepositPointEntryPoint (Transform) — harvester approach point
                var depositPoint = GetRawMember(item, "DepositPoint");
                ramp.HasDepositPoint = depositPoint != null;

                var entryTrans = GetRawMember(item, "DepositPointEntryPoint") as Transform;
                if (entryTrans != null)
                {
                    ramp.EntryLocal = structRoot.InverseTransformPoint(entryTrans.position);
                    log.Msg($"    EntryPoint: ({ramp.EntryLocal.x:F2}, {ramp.EntryLocal.y:F2}, {ramp.EntryLocal.z:F2})");
                }

                // Fallback: get first AIEntryPoint from ResourceDepositPoint
                if (ramp.EntryLocal == Vector3.zero && depositPoint != null)
                {
                    var aiRaw = GetRawMember(depositPoint, "AIEntryPoints");
                    if (aiRaw is System.Collections.IList aiList && aiList.Count > 0)
                    {
                        var firstAI = aiList[0] as Transform;
                        if (firstAI != null)
                        {
                            ramp.EntryLocal = structRoot.InverseTransformPoint(firstAI.position);
                            log.Msg($"    EntryPoint(AI): ({ramp.EntryLocal.x:F2}, {ramp.EntryLocal.y:F2}, {ramp.EntryLocal.z:F2})");
                        }
                    }
                }

                ramps.Add(ramp);
            }
        }

        private static void ScanRefineryPlacements()
        {
            var log = Melon<MapBalance>.Logger;
            log.Msg("=== Starting Refinery Placement Scan ===");

            // Scan Sol refinery (original behavior)
            ScanSingleRefinery("Sol",
                n => n.Contains("Sol") && n.Contains("Refinery")
                     && !n.Contains("Turret") && !n.Contains("Upgrade"));

            // Also scan Cent refinery
            ScanSingleRefinery("Cent",
                n => n.Contains("Cent") && n.Contains("Refinery")
                     && !n.Contains("Turret") && !n.Contains("Upgrade"));
        }

        private static void ScanSingleRefinery(string faction, Func<string, bool> nameFilter)
        {
            var log = Melon<MapBalance>.Logger;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            string label = $"{faction}_Refinery";
            var refData = ExtractBuildingData(nameFilter, label);
            if (refData == null) { log.Error($"Failed to extract {label} data"); return; }

            var terrain = GetMainTerrain();
            if (terrain == null) { log.Error("No terrain found for scan"); return; }

            float terrainY0 = terrain.transform.position.y;
            Vector3 terrainPos = terrain.transform.position;
            Vector3 terrainSize = terrain.terrainData.size;

            float snap = refData.GridSnapXZ;
            float snapY = refData.GridSnapY;

            // Compute scan bounds (snap to grid)
            float minX = Mathf.Ceil(terrainPos.x / snap) * snap;
            float minZ = Mathf.Ceil(terrainPos.z / snap) * snap;
            float maxX = Mathf.Floor((terrainPos.x + terrainSize.x) / snap) * snap;
            float maxZ = Mathf.Floor((terrainPos.z + terrainSize.z) / snap) * snap;

            int stepsX = Mathf.RoundToInt((maxX - minX) / snap) + 1;
            int stepsZ = Mathf.RoundToInt((maxZ - minZ) / snap) + 1;

            log.Msg($"Terrain: pos=({terrainPos.x:F0},{terrainPos.y:F0},{terrainPos.z:F0}) size=({terrainSize.x:F0},{terrainSize.y:F0},{terrainSize.z:F0})");
            log.Msg($"Scan: X=[{minX:F0},{maxX:F0}] Z=[{minZ:F0},{maxZ:F0}] steps={stepsX}x{stepsZ} x4rot = {stepsX * stepsZ * 4} checks");

            var headerSb = new StringBuilder();
            var csvSb = new StringBuilder();

            // Header with prefab data
            headerSb.AppendLine($"=== Refinery Placement Scan v1.0 ===");
            headerSb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            headerSb.AppendLine($"Map: {GetCurrentMapName() ?? "unknown"}");
            headerSb.AppendLine($"Faction: {faction}");
            headerSb.AppendLine($"Asset: {refData.Name}");
            headerSb.AppendLine($"GridSnapXZ: {refData.GridSnapXZ}, GridSnapY: {refData.GridSnapY}");
            headerSb.AppendLine($"RotateToSurface: {refData.RotateToSurface}, MaxAngle: {refData.MaxAngle}");
            headerSb.AppendLine();

            headerSb.AppendLine($"--- AboveGroundPoints ({refData.AboveGroundLocal.Count}) ---");
            foreach (var p in refData.AboveGroundLocal)
                headerSb.AppendLine($"  ({p.x:F2}, {p.y:F2}, {p.z:F2})");

            headerSb.AppendLine($"--- BelowGroundPoints ({refData.BelowGroundLocal.Count}) ---");
            foreach (var p in refData.BelowGroundLocal)
                headerSb.AppendLine($"  ({p.x:F2}, {p.y:F2}, {p.z:F2})");

            headerSb.AppendLine($"--- PreviewEntries ({refData.PreviewEntries.Count}) ---");
            for (int i = 0; i < refData.PreviewEntries.Count; i++)
            {
                var e = refData.PreviewEntries[i];
                headerSb.AppendLine($"  [{i}] above={e.AboveLocal.Count}(need>={e.MustAboveCount}), below={e.BelowLocal.Count}(need>={e.MustBelowCount})");
                foreach (var p in e.AboveLocal) headerSb.AppendLine($"    A: ({p.x:F2}, {p.y:F2}, {p.z:F2})");
                foreach (var p in e.BelowLocal) headerSb.AppendLine($"    B: ({p.x:F2}, {p.y:F2}, {p.z:F2})");
            }

            headerSb.AppendLine($"--- Ramps ({refData.Ramps.Count}) ---");
            for (int i = 0; i < refData.Ramps.Count; i++)
            {
                var r = refData.Ramps[i];
                headerSb.AppendLine($"  [{i}] type={(r.IsAboveType ? "above" : "below")} check=({r.CheckPointLocal.x:F2},{r.CheckPointLocal.y:F2},{r.CheckPointLocal.z:F2}) entry=({r.EntryLocal.x:F2},{r.EntryLocal.y:F2},{r.EntryLocal.z:F2})");
            }

            headerSb.AppendLine();
            headerSb.AppendLine($"Terrain: ({terrainPos.x:F0},{terrainPos.y:F0},{terrainPos.z:F0}) size ({terrainSize.x:F0},{terrainSize.y:F0},{terrainSize.z:F0})");
            headerSb.AppendLine($"Scan: X=[{minX:F0},{maxX:F0}] Z=[{minZ:F0},{maxZ:F0}] step={snap}");
            headerSb.AppendLine();

            // Filter to only harvester ramps (those with DepositPoint)
            var harvesterRamps = new List<int>();
            for (int r = 0; r < refData.Ramps.Count; r++)
            {
                if (refData.Ramps[r].HasDepositPoint)
                    harvesterRamps.Add(r);
            }
            log.Msg($"Harvester ramps (with DepositPoint): {harvesterRamps.Count} of {refData.Ramps.Count} total");

            // Build CSV header — only harvester ramps
            var rampHeaders = new StringBuilder();
            for (int hi = 0; hi < harvesterRamps.Count; hi++)
            {
                char rampLabel = (char)('A' + hi);
                rampHeaders.Append($",ramp{rampLabel}_ok,ramp{rampLabel}_dirX,ramp{rampLabel}_dirZ");
            }
            csvSb.AppendLine($"x,z,rot,terrainY,adjustY,finalY{rampHeaders}");

            // === Main scan loop ===
            int totalChecks = 0;
            int validCount = 0;
            int[] rampCounts = new int[refData.Ramps.Count];

            Quaternion[] rotations = new[]
            {
                Quaternion.Euler(0f, 0f, 0f),
                Quaternion.Euler(0f, 90f, 0f),
                Quaternion.Euler(0f, 180f, 0f),
                Quaternion.Euler(0f, 270f, 0f)
            };
            float[] rotAngles = { 0f, 90f, 180f, 270f };

            for (float gx = minX; gx <= maxX; gx += snap)
            {
                for (float gz = minZ; gz <= maxZ; gz += snap)
                {
                    // Sample terrain height at grid center
                    float rawTerrainY = terrain.SampleHeight(new Vector3(gx, 0f, gz)) + terrainY0;
                    float baseY = Mathf.Round(rawTerrainY / snapY) * snapY;

                    for (int ri = 0; ri < 4; ri++)
                    {
                        Quaternion rot = rotations[ri];
                        totalChecks++;

                        bool valid = true;
                        float maxDelta = -30f;

                        // === AboveGroundPoints check ===
                        foreach (var localPos in refData.AboveGroundLocal)
                        {
                            Vector3 worldPos = new Vector3(gx, baseY, gz) + rot * localPos;

                            // Check terrain bounds
                            if (worldPos.x < terrainPos.x || worldPos.x > terrainPos.x + terrainSize.x ||
                                worldPos.z < terrainPos.z || worldPos.z > terrainPos.z + terrainSize.z)
                            {
                                valid = false;
                                break;
                            }

                            float terrainH = terrain.SampleHeight(worldPos) + terrainY0;
                            float delta = terrainH - worldPos.y;
                            if (delta > maxDelta) maxDelta = delta;
                        }

                        if (!valid) continue;

                        float adjustY = maxDelta;
                        float finalY = baseY + adjustY;

                        // === BelowGroundPoints check (with adjusted Y) ===
                        foreach (var localPos in refData.BelowGroundLocal)
                        {
                            Vector3 worldPos = new Vector3(gx, finalY, gz) + rot * localPos;

                            if (worldPos.x < terrainPos.x || worldPos.x > terrainPos.x + terrainSize.x ||
                                worldPos.z < terrainPos.z || worldPos.z > terrainPos.z + terrainSize.z)
                            {
                                valid = false;
                                break;
                            }

                            float terrainH = terrain.SampleHeight(worldPos) + terrainY0;
                            if (terrainH < worldPos.y) // terrain below point = point sticks out
                            {
                                valid = false;
                                break;
                            }
                        }

                        if (!valid) continue;

                        // === PreviewEntries check ===
                        foreach (var entry in refData.PreviewEntries)
                        {
                            int aboveHits = 0;
                            foreach (var lp in entry.AboveLocal)
                            {
                                Vector3 wp = new Vector3(gx, finalY, gz) + rot * lp;
                                if (wp.x >= terrainPos.x && wp.x <= terrainPos.x + terrainSize.x &&
                                    wp.z >= terrainPos.z && wp.z <= terrainPos.z + terrainSize.z)
                                {
                                    aboveHits++; // terrain exists = raycast would hit
                                }
                            }

                            int belowHits = 0;
                            foreach (var lp in entry.BelowLocal)
                            {
                                Vector3 wp = new Vector3(gx, finalY, gz) + rot * lp;
                                if (wp.x >= terrainPos.x && wp.x <= terrainPos.x + terrainSize.x &&
                                    wp.z >= terrainPos.z && wp.z <= terrainPos.z + terrainSize.z)
                                {
                                    float th = terrain.SampleHeight(wp) + terrainY0;
                                    if (th >= wp.y) belowHits++;
                                }
                            }

                            if (aboveHits < entry.MustAboveCount || belowHits < entry.MustBelowCount)
                            {
                                valid = false;
                                break;
                            }
                        }

                        if (!valid) continue;

                        // === Ramp accessibility check (all ramps, for stats) ===
                        for (int r = 0; r < refData.Ramps.Count; r++)
                        {
                            var ramp = refData.Ramps[r];
                            Vector3 wp = new Vector3(gx, finalY, gz) + rot * ramp.CheckPointLocal;

                            bool accessible;
                            if (wp.x < terrainPos.x || wp.x > terrainPos.x + terrainSize.x ||
                                wp.z < terrainPos.z || wp.z > terrainPos.z + terrainSize.z)
                            {
                                accessible = ramp.IsAboveType;
                            }
                            else
                            {
                                float th = terrain.SampleHeight(wp) + terrainY0;
                                accessible = ramp.IsAboveType
                                    ? (th < wp.y)
                                    : (th >= wp.y);
                            }

                            if (accessible) rampCounts[r]++;
                        }

                        // === CSV output — only harvester ramps ===
                        var rampLine = new StringBuilder();
                        foreach (int r in harvesterRamps)
                        {
                            var ramp = refData.Ramps[r];
                            Vector3 wp = new Vector3(gx, finalY, gz) + rot * ramp.CheckPointLocal;

                            bool accessible;
                            if (wp.x < terrainPos.x || wp.x > terrainPos.x + terrainSize.x ||
                                wp.z < terrainPos.z || wp.z > terrainPos.z + terrainSize.z)
                            {
                                accessible = ramp.IsAboveType;
                            }
                            else
                            {
                                float th = terrain.SampleHeight(wp) + terrainY0;
                                accessible = ramp.IsAboveType
                                    ? (th < wp.y)
                                    : (th >= wp.y);
                            }

                            // Compute ramp direction from structure center
                            Vector3 entryWorld = new Vector3(gx, finalY, gz) + rot * ramp.EntryLocal;
                            float dirX = entryWorld.x - gx;
                            float dirZ = entryWorld.z - gz;
                            float len = Mathf.Sqrt(dirX * dirX + dirZ * dirZ);
                            if (len > 0.01f) { dirX /= len; dirZ /= len; }

                            rampLine.Append($",{(accessible ? 1 : 0)},{dirX:F3},{dirZ:F3}");
                        }

                        validCount++;
                        csvSb.AppendLine($"{gx:F0},{gz:F0},{rotAngles[ri]:F0},{rawTerrainY:F1},{adjustY:F2},{finalY:F1}{rampLine}");
                    }
                }
            }

            sw.Stop();

            // Summary
            headerSb.AppendLine($"=== Scan Results ===");
            headerSb.AppendLine($"Total checks: {totalChecks}");
            headerSb.AppendLine($"Valid placements: {validCount} ({(totalChecks > 0 ? 100.0 * validCount / totalChecks : 0):F1}%)");
            headerSb.AppendLine();
            headerSb.AppendLine("Harvester ramps (with DepositPoint):");
            for (int hi = 0; hi < harvesterRamps.Count; hi++)
            {
                int r = harvesterRamps[hi];
                char rampLabel = (char)('A' + hi);
                var ramp = refData.Ramps[r];
                headerSb.AppendLine($"  Ramp {rampLabel} (idx {r}): {rampCounts[r]} accessible ({(validCount > 0 ? 100.0 * rampCounts[r] / validCount : 0):F1}% of valid), check=({ramp.CheckPointLocal.x:F1},{ramp.CheckPointLocal.y:F1},{ramp.CheckPointLocal.z:F1}), entry=({ramp.EntryLocal.x:F1},{ramp.EntryLocal.y:F1},{ramp.EntryLocal.z:F1})");
            }
            headerSb.AppendLine();
            headerSb.AppendLine("All terrain-dependent elements (for reference):");
            for (int r = 0; r < refData.Ramps.Count; r++)
            {
                string tag = refData.Ramps[r].HasDepositPoint ? " [HARVESTER]" : "";
                headerSb.AppendLine($"  Ramp {r}: {rampCounts[r]} accessible ({(validCount > 0 ? 100.0 * rampCounts[r] / validCount : 0):F1}% of valid){tag}");
            }
            headerSb.AppendLine();
            headerSb.AppendLine($"Scan time: {sw.ElapsedMilliseconds}ms");
            headerSb.AppendLine();
            headerSb.AppendLine($"=== CSV DATA (harvester ramps only) ===");

            // Write output
            string logDir = Path.Combine("UserData", "MapBalance");
            Directory.CreateDirectory(logDir);
            string mapName = GetCurrentMapName() ?? "unknown";
            string fileName = $"refinery_scan_{faction}_{mapName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string filePath = Path.Combine(logDir, fileName);

            using (var writer = new StreamWriter(filePath))
            {
                writer.Write(headerSb.ToString());
                writer.Write(csvSb.ToString());
            }

            log.Msg($"=== Refinery scan complete ===");
            log.Msg($"Valid: {validCount}/{totalChecks} ({(totalChecks > 0 ? 100.0 * validCount / totalChecks : 0):F1}%)");
            log.Msg($"Scan time: {sw.ElapsedMilliseconds}ms");
            log.Msg($"Output: {filePath}");
        }

        // === HQ Placement Scan ===

        private static void ScanHQPlacements()
        {
            var log = Melon<MapBalance>.Logger;
            log.Msg("=== Starting HQ Placement Scan ===");

            // Scan both Sol and Centauri HQs
            var factions = new[] {
                ("Sol", new Func<string, bool>(n => n.Contains("Sol") && n.Contains("Headquarters"))),
                ("Cent", new Func<string, bool>(n => n.Contains("Cent") && n.Contains("Headquarters"))),
            };

            foreach (var (faction, nameFilter) in factions)
            {
                ScanSingleHQ(faction, nameFilter);
            }
        }

        private static void ScanSingleHQ(string faction, Func<string, bool> nameFilter)
        {
            var log = Melon<MapBalance>.Logger;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            string label = $"{faction}_Headquarters";
            var hqData = ExtractBuildingData(nameFilter, label);
            if (hqData == null) { log.Error($"Failed to extract {label} data"); return; }

            var terrain = GetMainTerrain();
            if (terrain == null) { log.Error("No terrain found for HQ scan"); return; }

            float terrainY0 = terrain.transform.position.y;
            Vector3 terrainPos = terrain.transform.position;
            Vector3 terrainSize = terrain.terrainData.size;

            float snap = hqData.GridSnapXZ;
            float snapY = hqData.GridSnapY;

            float minX = Mathf.Ceil(terrainPos.x / snap) * snap;
            float minZ = Mathf.Ceil(terrainPos.z / snap) * snap;
            float maxX = Mathf.Floor((terrainPos.x + terrainSize.x) / snap) * snap;
            float maxZ = Mathf.Floor((terrainPos.z + terrainSize.z) / snap) * snap;

            int stepsX = Mathf.RoundToInt((maxX - minX) / snap) + 1;
            int stepsZ = Mathf.RoundToInt((maxZ - minZ) / snap) + 1;

            log.Msg($"[{label}] Scan: X=[{minX:F0},{maxX:F0}] Z=[{minZ:F0},{maxZ:F0}] steps={stepsX}x{stepsZ} x4rot = {stepsX * stepsZ * 4} checks");

            var headerSb = new StringBuilder();
            var csvSb = new StringBuilder();

            headerSb.AppendLine($"=== HQ Placement Scan v1.0 ===");
            headerSb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            headerSb.AppendLine($"Map: {GetCurrentMapName() ?? "unknown"}");
            headerSb.AppendLine($"Faction: {faction}");
            headerSb.AppendLine($"Asset: {hqData.Name}");
            headerSb.AppendLine($"GridSnapXZ: {hqData.GridSnapXZ}, GridSnapY: {hqData.GridSnapY}");
            headerSb.AppendLine($"RotateToSurface: {hqData.RotateToSurface}, MaxAngle: {hqData.MaxAngle}");
            headerSb.AppendLine();

            headerSb.AppendLine($"--- AboveGroundPoints ({hqData.AboveGroundLocal.Count}) ---");
            foreach (var p in hqData.AboveGroundLocal)
                headerSb.AppendLine($"  ({p.x:F2}, {p.y:F2}, {p.z:F2})");

            headerSb.AppendLine($"--- BelowGroundPoints ({hqData.BelowGroundLocal.Count}) ---");
            foreach (var p in hqData.BelowGroundLocal)
                headerSb.AppendLine($"  ({p.x:F2}, {p.y:F2}, {p.z:F2})");

            headerSb.AppendLine($"--- PreviewEntries ({hqData.PreviewEntries.Count}) ---");
            for (int i = 0; i < hqData.PreviewEntries.Count; i++)
            {
                var e = hqData.PreviewEntries[i];
                headerSb.AppendLine($"  [{i}] above={e.AboveLocal.Count}(need>={e.MustAboveCount}), below={e.BelowLocal.Count}(need>={e.MustBelowCount})");
            }

            headerSb.AppendLine();
            headerSb.AppendLine($"Terrain: ({terrainPos.x:F0},{terrainPos.y:F0},{terrainPos.z:F0}) size ({terrainSize.x:F0},{terrainSize.y:F0},{terrainSize.z:F0})");
            headerSb.AppendLine($"Scan: X=[{minX:F0},{maxX:F0}] Z=[{minZ:F0},{maxZ:F0}] step={snap}");
            headerSb.AppendLine();

            // CSV header — no ramp columns for HQ
            csvSb.AppendLine("x,z,rot,terrainY,adjustY,finalY");

            // === Main scan loop ===
            int totalChecks = 0;
            int validCount = 0;

            Quaternion[] rotations = new[]
            {
                Quaternion.Euler(0f, 0f, 0f),
                Quaternion.Euler(0f, 90f, 0f),
                Quaternion.Euler(0f, 180f, 0f),
                Quaternion.Euler(0f, 270f, 0f)
            };
            float[] rotAngles = { 0f, 90f, 180f, 270f };

            for (float gx = minX; gx <= maxX; gx += snap)
            {
                for (float gz = minZ; gz <= maxZ; gz += snap)
                {
                    float rawTerrainY = terrain.SampleHeight(new Vector3(gx, 0f, gz)) + terrainY0;
                    float baseY = Mathf.Round(rawTerrainY / snapY) * snapY;

                    for (int ri = 0; ri < 4; ri++)
                    {
                        Quaternion rot = rotations[ri];
                        totalChecks++;

                        bool valid = true;
                        float maxDelta = -30f;

                        // AboveGroundPoints check
                        foreach (var localPos in hqData.AboveGroundLocal)
                        {
                            Vector3 worldPos = new Vector3(gx, baseY, gz) + rot * localPos;
                            if (worldPos.x < terrainPos.x || worldPos.x > terrainPos.x + terrainSize.x ||
                                worldPos.z < terrainPos.z || worldPos.z > terrainPos.z + terrainSize.z)
                            {
                                valid = false;
                                break;
                            }
                            float terrainH = terrain.SampleHeight(worldPos) + terrainY0;
                            float delta = terrainH - worldPos.y;
                            if (delta > maxDelta) maxDelta = delta;
                        }
                        if (!valid) continue;

                        float adjustY = maxDelta;
                        float finalY = baseY + adjustY;

                        // BelowGroundPoints check
                        foreach (var localPos in hqData.BelowGroundLocal)
                        {
                            Vector3 worldPos = new Vector3(gx, finalY, gz) + rot * localPos;
                            if (worldPos.x < terrainPos.x || worldPos.x > terrainPos.x + terrainSize.x ||
                                worldPos.z < terrainPos.z || worldPos.z > terrainPos.z + terrainSize.z)
                            {
                                valid = false;
                                break;
                            }
                            float terrainH = terrain.SampleHeight(worldPos) + terrainY0;
                            if (terrainH < worldPos.y)
                            {
                                valid = false;
                                break;
                            }
                        }
                        if (!valid) continue;

                        // PreviewEntries check
                        foreach (var entry in hqData.PreviewEntries)
                        {
                            int aboveHits = 0;
                            foreach (var lp in entry.AboveLocal)
                            {
                                Vector3 wp = new Vector3(gx, finalY, gz) + rot * lp;
                                if (wp.x >= terrainPos.x && wp.x <= terrainPos.x + terrainSize.x &&
                                    wp.z >= terrainPos.z && wp.z <= terrainPos.z + terrainSize.z)
                                {
                                    aboveHits++;
                                }
                            }

                            int belowHits = 0;
                            foreach (var lp in entry.BelowLocal)
                            {
                                Vector3 wp = new Vector3(gx, finalY, gz) + rot * lp;
                                if (wp.x >= terrainPos.x && wp.x <= terrainPos.x + terrainSize.x &&
                                    wp.z >= terrainPos.z && wp.z <= terrainPos.z + terrainSize.z)
                                {
                                    float th = terrain.SampleHeight(wp) + terrainY0;
                                    if (th >= wp.y) belowHits++;
                                }
                            }

                            if (aboveHits < entry.MustAboveCount || belowHits < entry.MustBelowCount)
                            {
                                valid = false;
                                break;
                            }
                        }
                        if (!valid) continue;

                        validCount++;
                        csvSb.AppendLine($"{gx:F0},{gz:F0},{rotAngles[ri]:F0},{rawTerrainY:F1},{adjustY:F2},{finalY:F1}");
                    }
                }
            }

            sw.Stop();

            headerSb.AppendLine($"=== Scan Results ===");
            headerSb.AppendLine($"Total checks: {totalChecks}");
            headerSb.AppendLine($"Valid placements: {validCount} ({(totalChecks > 0 ? 100.0 * validCount / totalChecks : 0):F1}%)");
            headerSb.AppendLine($"Scan time: {sw.ElapsedMilliseconds}ms");
            headerSb.AppendLine();
            headerSb.AppendLine($"=== CSV DATA ===");

            string logDir = Path.Combine("UserData", "MapBalance");
            Directory.CreateDirectory(logDir);
            string mapName = GetCurrentMapName() ?? "unknown";
            string fileName = $"hq_scan_{faction}_{mapName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string filePath = Path.Combine(logDir, fileName);

            using (var writer = new StreamWriter(filePath))
            {
                writer.Write(headerSb.ToString());
                writer.Write(csvSb.ToString());
            }

            log.Msg($"=== {label} scan complete ===");
            log.Msg($"Valid: {validCount}/{totalChecks} ({(totalChecks > 0 ? 100.0 * validCount / totalChecks : 0):F1}%)");
            log.Msg($"Scan time: {sw.ElapsedMilliseconds}ms");
            log.Msg($"Output: {filePath}");
        }

        // === ResourceArea Helpers ===

        private static List<Component> GetAllResourceAreas()
        {
            var list = new List<Component>();
            object? allAreasList = _allAreasField?.GetValue(null) ?? _allAreasProp?.GetValue(null);
            if (allAreasList == null) return list;

            var countProp = allAreasList.GetType().GetProperty("Count");
            int count = (int)(countProp?.GetValue(allAreasList) ?? 0);
            var itemProp = allAreasList.GetType().GetProperty("Item");

            for (int i = 0; i < count; i++)
            {
                var area = itemProp?.GetValue(allAreasList, new object[] { i }) as Component;
                if (area != null) list.Add(area);
            }
            return list;
        }

        private static int CountEnabledAreas()
        {
            object? enabledList = _enabledAreasField?.GetValue(null) ?? _enabledAreasProp?.GetValue(null);
            if (enabledList == null) return 0;
            var cp = enabledList.GetType().GetProperty("Count");
            return (int)(cp?.GetValue(enabledList) ?? 0);
        }

        private static void SetEnabled(Component area, bool enabled)
        {
            if (area is Behaviour b)
                b.enabled = enabled;
        }

        private static void CallDistributeResources(Component area)
        {
            if (_distributeResourcesMethod == null) return;
            try
            {
                var paramCount = _distributeResourcesMethod.GetParameters().Length;
                if (paramCount == 0)
                    _distributeResourcesMethod.Invoke(area, null);
                else
                    _distributeResourcesMethod.Invoke(area, new object[] { false });
            }
            catch (Exception ex)
            {
                Melon<MapBalance>.Logger.Warning($"DistributeResources failed: {ex.Message}");
            }
        }

        private static void SetMemberValue(object obj, string name, float value)
        {
            var type = obj.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) { field.SetValue(obj, value); return; }
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanWrite) prop.SetValue(obj, value);
        }

        private static int GetMemberValueInt(object obj, string name)
        {
            if (obj == null) return 0;
            var type = obj.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) { var v = field.GetValue(obj); if (v is int i) return i; }
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanRead) { var v = prop.GetValue(obj); if (v is int i) return i; }
            return 0;
        }

        private static void SetMemberValueInt(object obj, string name, int value)
        {
            var type = obj.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) { field.SetValue(obj, value); return; }
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanWrite) prop.SetValue(obj, value);
        }

        // === Dump Logic ===

        private static void DumpMapInfo()
        {
            if (_dumpedThisRound) return;
            _dumpedThisRound = true;

            var log = Melon<MapBalance>.Logger;
            var sb = new StringBuilder();
            sb.AppendLine("=== Si_MapBalance Dump v1.0 ===");
            sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            string mapName = GetCurrentMapName() ?? "unknown";
            sb.AppendLine();

            // --- Terrain info ---
            sb.AppendLine("--- TERRAIN ---");
            sb.AppendLine($"Map: {mapName}");
            var terrain = GetMainTerrain();
            if (terrain != null)
            {
                var tPos = terrain.transform.position;
                var tSize = terrain.terrainData.size;
                sb.AppendLine($"Terrain Position: ({tPos.x:F1}, {tPos.y:F1}, {tPos.z:F1})");
                sb.AppendLine($"Terrain Size: ({tSize.x:F1}, {tSize.y:F1}, {tSize.z:F1})");
                sb.AppendLine($"Heightmap Resolution: {terrain.terrainData.heightmapResolution}");

                // Sample elevation range
                float minElev = float.MaxValue, maxElev = float.MinValue;
                int samples = 21;
                for (int sx = 0; sx < samples; sx++)
                {
                    for (int sz = 0; sz < samples; sz++)
                    {
                        float wx = tPos.x + tSize.x * sx / (samples - 1);
                        float wz = tPos.z + tSize.z * sz / (samples - 1);
                        float h = terrain.SampleHeight(new Vector3(wx, 0f, wz)) + tPos.y;
                        if (h < minElev) minElev = h;
                        if (h > maxElev) maxElev = h;
                    }
                }
                sb.AppendLine($"Elevation Range: {minElev:F1} to {maxElev:F1} (sampled {samples * samples} points)");
            }
            else
            {
                sb.AppendLine("Terrain: not found");
            }
            sb.AppendLine();

            // --- Resource Areas (ALL, including disabled) ---
            var allAreas = GetAllResourceAreas();
            int enabledCount = CountEnabledAreas();

            sb.AppendLine("--- RESOURCE AREAS ---");
            sb.AppendLine($"Total ResourceAreas: {allAreas.Count} (Enabled: {enabledCount})");
            sb.AppendLine();

            for (int i = 0; i < allAreas.Count; i++)
            {
                var area = allAreas[i];
                bool compEnabled = area is Behaviour b && b.enabled;
                var pos = area.transform.position;
                string resName = GetResourceName(area);
                string goName = area.gameObject.name;

                int currentAmount = GetMemberValue<int>(area, "ResourceAmountCurrent");
                int maxAmount = GetMemberValue<int>(area, "ResourceAmountMax");
                float amountMult = GetMemberValue<float>(area, "ResourceDistributionAmountMult");
                float distMult = GetMemberValue<float>(area, "ResourceDistributionDistanceMult");
                int maxPerCell = GetMemberValue<int>(area, "MaxResourceAmountPerCell");
                float cellSize = GetMemberValue<float>(area, "CellSize");

                // Grid dimensions from ResourceAmounts array
                int gridX = 0, gridZ = 0;
                var amountsField = _resourceAreaType!.GetField("ResourceAmounts",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var amountsProp = _resourceAreaType.GetProperty("ResourceAmounts",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object? amounts = amountsField?.GetValue(area) ?? amountsProp?.GetValue(area);
                if (amounts is int[,] arr)
                {
                    gridX = arr.GetLength(0);
                    gridZ = arr.GetLength(1);
                }

                sb.AppendLine($"[{i}] \"{goName}\" ({resName})");
                sb.AppendLine($"     Active: {compEnabled}");
                sb.AppendLine($"     Position: ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})");
                if (gridX > 0 && cellSize > 0)
                {
                    sb.AppendLine($"     Grid: {gridX}x{gridZ} cells, CellSize={cellSize:F1}m");
                    sb.AppendLine($"     World extent: {gridX * cellSize:F0}x{gridZ * cellSize:F0}m");
                }
                sb.AppendLine($"     MaxPerCell: {maxPerCell}, AmountMult: {amountMult:F2}, DistMult: {distMult:F2}");
                sb.AppendLine($"     Resources: {currentAmount} / {maxAmount}");
                sb.AppendLine();
            }

            string logDir = Path.Combine("UserData", "MapBalance");
            Directory.CreateDirectory(logDir);
            string logFile = Path.Combine(logDir, $"dump_{mapName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(logFile, sb.ToString());

            log.Msg($"Dump: {logFile} ({allAreas.Count} areas)");
        }

        private static void DumpConstructionData()
        {
            var log = Melon<MapBalance>.Logger;
            if (_constructionDataType == null) return;

            var allCD = Resources.FindObjectsOfTypeAll(_constructionDataType);
            var sb = new StringBuilder();
            sb.AppendLine("=== ConstructionData Dump ===");
            sb.AppendLine($"Total: {allCD.Length}");
            sb.AppendLine();
            sb.AppendLine($"{"Name",-50} {"MaxDist",8} {"MinDist",8} {"Anchor",7} {"Placeable",10} {"Cost",6} {"GridXZ",7}");
            sb.AppendLine(new string('-', 100));

            foreach (var cd in allCD)
            {
                var uo = cd as UnityEngine.Object;
                if (uo == null) continue;

                float maxDist = GetMemberValue<float>(cd, "MaximumBaseStructureDistance");
                float minDist = GetMemberValue<float>(cd, "MinimumBaseStructureDistance");
                bool placeable = GetMemberValue<bool>(cd, "Placeable");
                int cost = GetMemberValue<int>(cd, "ResourceCost");
                float gridXZ = GetMemberValue<float>(cd, "GridSnapXZ");

                // Get anchor status from ObjectInfo
                bool isAnchor = false;
                var objectInfo = GetRawMember(cd, "ObjectInfo");
                if (objectInfo != null)
                    isAnchor = GetMemberValue<bool>(objectInfo, "StructureConstructionAnchor");

                sb.AppendLine($"{uo.name,-50} {maxDist,8:F0} {minDist,8:F0} {isAnchor,7} {placeable,10} {cost,6} {gridXZ,7:F0}");
            }

            string logDir = Path.Combine("UserData", "MapBalance");
            Directory.CreateDirectory(logDir);
            string mapName = GetCurrentMapName() ?? "unknown";
            string filePath = Path.Combine(logDir, $"construction_data_{mapName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(filePath, sb.ToString());

            log.Msg($"ConstructionData dump: {filePath}");
        }

        // === General Helpers ===

        private static string GetResourceName(object area)
        {
            var resField = _resourceAreaType?.GetField("ResourceType",
                BindingFlags.Public | BindingFlags.Instance);
            object? resObj = resField?.GetValue(area);
            if (resObj == null)
            {
                var resProp = _resourceAreaType?.GetProperty("ResourceType",
                    BindingFlags.Public | BindingFlags.Instance);
                resObj = resProp?.GetValue(area);
            }
            if (resObj != null)
                return GetMemberValue<string>(resObj, "ResourceName") ?? "?";
            return "?";
        }

        /// <summary>
        /// Sample terrain height at world (x, z), checking all terrain tiles.
        /// Returns world-space Y suitable for spawning.
        /// </summary>
        private static float SampleTerrainHeight(float worldX, float worldZ, MelonLogger.Instance log)
        {
            // Try all terrains — maps may use multiple tiles
            Terrain? bestTerrain = null;
            foreach (var t in Terrain.activeTerrains)
            {
                var pos = t.transform.position;
                var size = t.terrainData.size;
                // Check if (worldX, worldZ) is within this terrain tile
                if (worldX >= pos.x && worldX <= pos.x + size.x &&
                    worldZ >= pos.z && worldZ <= pos.z + size.z)
                {
                    bestTerrain = t;
                    break;
                }
            }

            // Fallback to activeTerrain if no tile matched
            if (bestTerrain == null)
                bestTerrain = Terrain.activeTerrain;

            if (bestTerrain != null)
            {
                float h = bestTerrain.SampleHeight(new Vector3(worldX, 0f, worldZ))
                          + bestTerrain.transform.position.y;
                log.Msg($"  TerrainHeight at ({worldX:F0},{worldZ:F0}): {h:F1} (terrain: {bestTerrain.name})");
                return h;
            }

            log.Warning($"  No terrain found for ({worldX:F0},{worldZ:F0}) — using Y=0");
            return 0f;
        }

        private static Terrain? GetMainTerrain()
        {
            try
            {
                var prop = _gameType?.GetProperty("MainTerrain", BindingFlags.Public | BindingFlags.Static);
                return prop?.GetValue(null) as Terrain;
            }
            catch { return null; }
        }

        private static string? GetCurrentMapName()
        {
            try
            {
                var prop = _gameType?.GetProperty("ServerMapName", BindingFlags.Public | BindingFlags.Static);
                if (prop != null) return prop.GetValue(null) as string;
                var field = _gameType?.GetField("ServerMapName", BindingFlags.Public | BindingFlags.Static);
                if (field != null) return field.GetValue(null) as string;
                return SceneManager.GetActiveScene().name;
            }
            catch { return null; }
        }

        private static T GetMemberValue<T>(object obj, string name)
        {
            if (obj == null) return default!;
            var type = obj.GetType();

            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                var val = field.GetValue(obj);
                if (val is T t) return t;
            }

            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanRead)
            {
                var val = prop.GetValue(obj);
                if (val is T t) return t;
            }

            return default!;
        }

        private static object? GetRawMember(object obj, string name)
        {
            if (obj == null) return null;
            var type = obj.GetType();

            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) return field.GetValue(obj);

            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanRead) return prop.GetValue(obj);

            return null;
        }

        private static List<Vector3> ExtractTransformLocalPositions(object obj, string fieldName)
        {
            var result = new List<Vector3>();
            var raw = GetRawMember(obj, fieldName);
            if (raw is System.Collections.IList list)
            {
                foreach (var item in list)
                {
                    if (item is Transform t)
                        result.Add(t.localPosition);
                }
            }
            return result;
        }
    }
}
