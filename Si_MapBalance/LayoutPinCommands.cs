/*
 Layout pins (3.5.0): admins force one layout JSON per map instead of the random pick.
 - Pins + master switch live in UserData/cfg/MapBalance/layout_pins.json (LayoutPinStore).
 - The single pick point (LoadMapConfig, via PickLayout) asks TryPinnedLayout first.
 - !layout [status | list [map] | pin [map] name|# | unpin [map|all] | pinning on|off]
 - ModUIData(): the same as Si_ModUI rows (admin only).
*/

using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Si_MapBalance
{
    public partial class MapBalance
    {
        private const string Tag = "[MapBalance] ";

        // ---- pin store ----

        private static readonly string PinsPath = Path.Combine(
            MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "cfg", "MapBalance", "layout_pins.json");
        private static LayoutPinStore _pins = new LayoutPinStore();
        private static DateTime _pinsStamp = DateTime.MinValue;

        private static DateTime PinsFileStamp()
        {
            try { return File.Exists(PinsPath) ? File.GetLastWriteTimeUtc(PinsPath) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        private static void LoadPinStore(string why)
        {
            var log = Melon<MapBalance>.Logger;
            _pins = LayoutPinStore.Load(PinsPath, out string? error);
            _pinsStamp = PinsFileStamp();
            if (error != null)
                log.Warning($"Layout pins file {PinsPath} unreadable ({error}) — no pins, pinning on (the next change overwrites the file)");
            else
                log.Msg($"Layout pins ({why}): pinning {(_pins.Pinning ? "ON" : "OFF")}, {_pins.Pins.Count} pin(s)" +
                        (_pins.Pins.Count > 0 ? ": " + string.Join(", ", _pins.Pins.Select(kv => $"{kv.Key}={kv.Value}")) : ""));
        }

        /// <summary>The store, reloaded when the file was edited (or deleted) by hand.</summary>
        private static LayoutPinStore Pins()
        {
            if (PinsFileStamp() != _pinsStamp) LoadPinStore("file changed on disk");
            return _pins;
        }

        private static string? SavePins()
        {
            try
            {
                _pins.Save(PinsPath);
                _pinsStamp = PinsFileStamp();
                return null;
            }
            catch (Exception ex)
            {
                Melon<MapBalance>.Logger.Warning($"Could not save {PinsPath}: {ex.Message}");
                return ex.Message;
            }
        }

        // ---- the pick hook ----

        // Warn once per round (and pin/reason) — PickLayout can run twice in a round (mode mismatch re-pick).
        private static int _pinWarnEpoch = -1;
        private static string _pinWarnKey = "";

        private static void PinSkipped(string map, string pin, string reason, MelonLogger.Instance log)
        {
            _pinSkipReason = reason;
            string key = $"{map}|{pin}|{reason}";
            string line = $"Layout pin for {map} ({pin}) NOT used: {reason} — random pick instead";
            if (_pinWarnEpoch == _roundEpoch && _pinWarnKey == key) log.Msg(line);
            else
            {
                log.Warning(line);
                _pinWarnEpoch = _roundEpoch;
                _pinWarnKey = key;
            }
        }

        /// <summary>
        /// Path of the pinned layout if pinning is on, the map has a pin, and the file exists,
        /// parses and supports this round's mode. Null = use the random pick (a skipped pin is
        /// logged as a Warning once per round and shown in the announce).
        /// </summary>
        private static string? TryPinnedLayout(string mapName, string mapDir, string[] jsonFiles,
                                               string gameMode, bool twoVsOne, MelonLogger.Instance log)
        {
            var store = Pins();
            string? pin = store.GetPin(mapName);
            if (pin == null) return null;
            if (!store.Pinning)
            {
                log.Msg($"Layout pin for {mapName} ({pin}) ignored: pinning is switched off");
                return null;
            }

            string path = Path.Combine(mapDir, pin);
            if (!File.Exists(path)) { PinSkipped(mapName, pin, "file missing", log); return null; }

            JObject obj;
            try
            {
                string json = File.ReadAllText(path);
                obj = JObject.Parse(json);
                if (JsonConvert.DeserializeObject<MapConfig>(json) == null) throw new Exception("empty document");
            }
            catch (Exception ex)
            {
                log.Msg($"  {pin}: {ex.Message}");
                PinSkipped(mapName, pin, "unreadable JSON", log);
                return null;
            }

            bool mapHasTwoVsOne = false;
            if (twoVsOne)
                foreach (var f in jsonFiles)
                {
                    try { if (LayoutPinLogic.IsTwoVsOne(JObject.Parse(File.ReadAllText(f)))) { mapHasTwoVsOne = true; break; } }
                    catch { }
                }

            if (!LayoutPinLogic.SupportsRound(obj, gameMode, twoVsOne, mapHasTwoVsOne, out string reason))
            {
                PinSkipped(mapName, pin, reason, log);
                return null;
            }

            if (twoVsOne) _activeConfigTwoVsOneFallback = !LayoutPinLogic.IsTwoVsOne(obj);
            _activeConfigPinned = true;
            log.Msg($"Layout pin for {mapName}: {pin} [{LayoutPinLogic.ModeTags(obj)}] fits mode {ModeTag(gameMode, twoVsOne)} — used instead of the random pick");
            if (LayoutPinLogic.HasKoh(obj) && !IsKohModeActive())
                log.Msg("  Pinned layout has a 'koh' block but KGT KoH mode is not active — the KoH tower will not be spawned");
            return path;
        }

        // ---- layout files ----

        private static List<string> MapFolders()
        {
            try
            {
                if (!Directory.Exists(SpawnsDir)) return new List<string>();
                return Directory.GetDirectories(SpawnsDir).Select(d => Path.GetFileName(d))
                    .Where(n => !string.IsNullOrEmpty(n) && !n.StartsWith("~"))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch { return new List<string>(); }
        }

        /// <summary>Map folders plus maps that only have a pin (folder renamed or removed).</summary>
        private static List<string> KnownMaps() =>
            MapFolders().Concat(Pins().Pins.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>The map's layout file names (with .json), in list order; null = no folder.</summary>
        private static List<string>? LayoutFiles(string map)
        {
            try
            {
                string dir = Path.Combine(SpawnsDir, map);
                if (!Directory.Exists(dir)) return null;
                return LayoutPinLogic.SortLayouts(Directory.GetFiles(dir, "*.json").Select(f => Path.GetFileName(f)));
            }
            catch { return null; }
        }

        // Mode tags per file, re-read only when the file changes.
        private static readonly Dictionary<string, (DateTime stamp, long size, string tags, bool ok)> _tagCache =
            new Dictionary<string, (DateTime, long, string, bool)>(StringComparer.OrdinalIgnoreCase);

        private static string LayoutTags(string map, string file, out bool readable)
        {
            string path = Path.Combine(SpawnsDir, map, file);
            readable = false;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return "file missing";
                if (_tagCache.TryGetValue(path, out var c) && c.stamp == fi.LastWriteTimeUtc && c.size == fi.Length)
                {
                    readable = c.ok;
                    return c.tags;
                }
                string tags; bool ok;
                try { tags = LayoutPinLogic.ModeTags(JObject.Parse(File.ReadAllText(path))); ok = true; }
                catch { tags = "unreadable JSON"; ok = false; }
                _tagCache[path] = (fi.LastWriteTimeUtc, fi.Length, tags, ok);
                readable = ok;
                return tags;
            }
            catch { return "unreadable"; }
        }

        private static bool PickIsForThisRound(string map) =>
            _layoutResolved && _pickEpoch == _roundEpoch && _activeConfigMap == map;

        private static string ActiveLayoutText(string map)
        {
            if (!PickIsForThisRound(map)) return "not picked yet";
            if (_activeConfig == null) return $"none, vanilla (mode {ModeTag(_activeConfigMode, _activeConfigTwoVsOne)})";
            return $"{_activeConfigName}{(_activeConfigPinned ? " (PINNED)" : " (random)")} (mode {ModeTag(_activeConfigMode, _activeConfigTwoVsOne)})";
        }

        // ---- !layout ----

        private static void RegisterLayoutCommand(MelonLogger.Instance log)
        {
            try
            {
                var handler = typeof(MapBalance).GetMethod(nameof(OnLayoutCommand), BindingFlags.NonPublic | BindingFlags.Static);
                var callback = Delegate.CreateDelegate(_adminCallbackType!, handler!);
                object powerMap = Enum.ToObject(_adminPowerType!, 1 << 6); // SilicaAdminMod.Power.Map
                _registerAdminCmdMethod!.Invoke(null, new object[] {
                    "layout", callback, powerMap,
                    "Pin a spawn layout per map: !layout [status | list | pin | unpin | pinning on/off]" });
                log.Msg("Registered !layout admin command via AdminMod");
            }
            catch (Exception ex)
            {
                log.Warning($"Failed to register !layout: {ex.GetBaseException().Message}");
            }
        }

        private static void Reply(Player? player, string msg)
        {
            string line = Tag + msg;
            try
            {
                if (_sendChatToPlayerMethod != null)
                    _sendChatToPlayerMethod.Invoke(null, new object?[] { player, new[] { line } });
                else
                    Melon<MapBalance>.Logger.Msg(line);
            }
            catch (Exception ex) { Melon<MapBalance>.Logger.Msg($"{line} (chat failed: {ex.GetBaseException().Message})"); }
        }

        private const string NextRound = "Takes effect at the next round start (the layout is chosen once per round).";

        private static void OnLayoutCommand(Player? player, string args)
        {
            var log = Melon<MapBalance>.Logger;
            try
            {
                var tokens = (args ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                string sub = tokens.Count > 1 ? tokens[1].ToLowerInvariant() : "status";
                var rest = tokens.Skip(2).ToList();
                string who = player != null ? player.PlayerName : "CONSOLE";
                switch (sub)
                {
                    case "status": LayoutStatus(player); break;
                    case "list": LayoutList(player, rest); break;
                    case "pin": LayoutPin(player, rest, who, log); break;
                    case "unpin": LayoutUnpin(player, rest, who, log); break;
                    case "pinning": LayoutPinning(player, rest, who, log); break;
                    default:
                        Reply(player, "!layout | !layout list [map] | !layout pin [map] name-or-# | !layout unpin [map|all] | !layout pinning on|off");
                        break;
                }
            }
            catch (Exception ex)
            {
                log.Error($"!layout failed: {ex}");
                Reply(player, $"Error: {ex.GetBaseException().Message}");
            }
        }

        private static void LayoutStatus(Player? player)
        {
            var store = Pins();
            string map = GetCurrentMapName() ?? "";
            if (map.Length == 0) Reply(player, "No map loaded.");
            else
            {
                Reply(player, $"Map {map}: this round {ActiveLayoutText(map)}");
                string? pin = store.GetPin(map);
                if (pin == null) Reply(player, $"No pin for {map}.");
                else
                {
                    string state = !File.Exists(Path.Combine(SpawnsDir, map, pin)) ? " (FILE MISSING)"
                                 : !store.Pinning ? " (pinning OFF: ignored)" : "";
                    Reply(player, $"Pin for {map}: {LayoutPinLogic.StripJson(pin)} [{LayoutTags(map, pin, out _)}]{state}");
                }
                if (PickIsForThisRound(map) && _pinSkipReason.Length > 0)
                    Reply(player, $"Pin skipped this round: {_pinSkipReason} (random pick used)");
            }
            Reply(player, $"Pinning {(store.Pinning ? "ON" : "OFF: pins are kept but ignored")}; {store.Pins.Count} pin(s)" +
                          (store.Pins.Count > 0 ? ": " + string.Join(", ", store.Pins.Select(kv => $"{kv.Key}={LayoutPinLogic.StripJson(kv.Value)}")) : ""));
        }

        private static void LayoutList(Player? player, List<string> rest)
        {
            string? map = rest.Count > 0 ? LayoutPinLogic.ResolveMap(KnownMaps(), string.Join(" ", rest)) : GetCurrentMapName();
            if (string.IsNullOrEmpty(map))
            {
                Reply(player, rest.Count > 0 ? $"Unknown map '{string.Join(" ", rest)}'. Maps: {string.Join(", ", MapFolders())}"
                                             : "No map loaded. Use !layout list MAP");
                return;
            }
            var files = LayoutFiles(map!);
            if (files == null || files.Count == 0)
            {
                Reply(player, $"No layout files for {map} in cfg/Spawns/{map}.");
                return;
            }
            var store = Pins();
            string? pin = store.GetPin(map);
            bool current = string.Equals(map, GetCurrentMapName(), StringComparison.OrdinalIgnoreCase);
            Reply(player, $"Layouts of {map} ({files.Count}), pin: {(pin != null ? LayoutPinLogic.StripJson(pin) : "none")}" +
                          (pin != null && !store.Pinning ? " (pinning OFF)" : ""));
            for (int i = 0; i < files.Count; i++)
            {
                string f = files[i];
                string mark = "";
                if (pin != null && string.Equals(pin, f, StringComparison.OrdinalIgnoreCase)) mark += " [PINNED]";
                if (current && PickIsForThisRound(map!) && _activeConfig != null
                    && string.Equals(_activeConfigName, LayoutPinLogic.StripJson(f), StringComparison.OrdinalIgnoreCase))
                    mark += " [this round]";
                Reply(player, $"#{i + 1}  {LayoutPinLogic.StripJson(f)}  ({LayoutTags(map!, f, out _)}){mark}");
            }
            Reply(player, $"Pin with !layout pin # (number) or a unique part of the name{(current ? "" : $", e.g. !layout pin {map} 1")}.");
        }

        private static void LayoutPin(Player? player, List<string> rest, string who, MelonLogger.Instance log)
        {
            if (rest.Count == 0)
            {
                Reply(player, "!layout pin name-or-# (this map) | !layout pin MAP name-or-#. See !layout list.");
                return;
            }
            var maps = KnownMaps();
            LayoutPinLogic.SplitPinArgs(rest, t => LayoutPinLogic.ResolveMap(maps, t), GetCurrentMapName(), out string? map, out string query);
            if (string.IsNullOrEmpty(map))
            {
                Reply(player, "No map loaded: use !layout pin MAP name-or-#.");
                return;
            }
            var files = LayoutFiles(map!);
            if (files == null || files.Count == 0)
            {
                Reply(player, $"No layout files for {map} in cfg/Spawns/{map}.");
                return;
            }
            switch (LayoutPinLogic.MatchLayout(files, query, out string? match, out var candidates))
            {
                case LayoutMatch.Empty:
                    Reply(player, "Give a layout name or number. See !layout list.");
                    return;
                case LayoutMatch.NotFound:
                    Reply(player, $"No layout '{query}' on {map} ({files.Count} files). See !layout list {map}.");
                    return;
                case LayoutMatch.Ambiguous:
                    Reply(player, $"'{query}' matches {candidates.Count} layouts on {map}, be more specific:");
                    foreach (var c in candidates.Take(10))
                        Reply(player, $"#{files.IndexOf(c) + 1}  {LayoutPinLogic.StripJson(c)}");
                    if (candidates.Count > 10) Reply(player, $"... and {candidates.Count - 10} more.");
                    return;
            }

            string tags = LayoutTags(map!, match!, out bool readable);
            if (!readable)
            {
                Reply(player, $"{LayoutPinLogic.StripJson(match!)} is not readable ({tags}) — not pinned.");
                return;
            }
            var store = Pins();
            store.SetPin(map!, match!);
            string? err = SavePins();
            log.Msg($"!layout pin by {who}: {map} -> {match} [{tags}]");
            Reply(player, $"Pinned {LayoutPinLogic.StripJson(match!)} ({tags}) for {map}. {NextRound}");
            Reply(player, "A round whose mode the layout does not support uses the random pick.");
            if (!store.Pinning) Reply(player, "Pinning is OFF: the pin is stored but ignored until !layout pinning on.");
            if (err != null) Reply(player, $"WARNING: pins file not saved ({err}); the pin is lost on restart.");
            if (string.Equals(map, GetCurrentMapName(), StringComparison.OrdinalIgnoreCase) && PickIsForThisRound(map!))
                Reply(player, $"This round keeps {PickDisplayName()}.");
        }

        private static void LayoutUnpin(Player? player, List<string> rest, string who, MelonLogger.Instance log)
        {
            var store = Pins();
            string arg = string.Join(" ", rest);
            if (arg.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                int n = store.Clear();
                string? e = SavePins();
                log.Msg($"!layout unpin all by {who}: {n} pin(s) removed");
                Reply(player, $"Removed {n} pin(s). {NextRound}" + (e != null ? $" WARNING: not saved ({e})." : ""));
                return;
            }
            string? map = arg.Length > 0 ? LayoutPinLogic.ResolveMap(KnownMaps(), arg) : GetCurrentMapName();
            if (string.IsNullOrEmpty(map))
            {
                Reply(player, arg.Length > 0 ? $"Unknown map '{arg}'. Pinned maps: {(store.Pins.Count > 0 ? string.Join(", ", store.Pins.Keys) : "none")}"
                                             : "No map loaded: use !layout unpin MAP or !layout unpin all.");
                return;
            }
            string? old = store.GetPin(map);
            if (old == null)
            {
                Reply(player, $"No pin for {map}.");
                return;
            }
            store.RemovePin(map!);
            string? err = SavePins();
            log.Msg($"!layout unpin by {who}: {map} (was {old})");
            Reply(player, $"Unpinned {map} (was {LayoutPinLogic.StripJson(old)}). {NextRound}" + (err != null ? $" WARNING: not saved ({err})." : ""));
        }

        private static void LayoutPinning(Player? player, List<string> rest, string who, MelonLogger.Instance log)
        {
            var store = Pins();
            string v = rest.Count > 0 ? rest[0].ToLowerInvariant() : "";
            bool? on = v == "on" || v == "true" || v == "1" || v == "enable" ? true
                     : v == "off" || v == "false" || v == "0" || v == "disable" ? false : (bool?)null;
            if (on == null)
            {
                Reply(player, $"Pinning is {(store.Pinning ? "ON" : "OFF")} ({store.Pins.Count} pin(s)). !layout pinning on|off");
                return;
            }
            store.Pinning = on.Value;
            string? err = SavePins();
            log.Msg($"!layout pinning {(on.Value ? "on" : "off")} by {who}");
            Reply(player, (on.Value ? $"Pinning ON: {store.Pins.Count} pin(s) apply. "
                                    : "Pinning OFF: pins are kept but ignored, every round uses the random pick. ") + NextRound +
                          (err != null ? $" WARNING: not saved ({err})." : ""));
        }

        // ---- Si_ModUI export (admin only: the mod has no player command, so its frame entry
        //      and this document never reach non-admins) ----

        private static string? _uiCache;
        private static string _uiKey = "";
        private static string _uiFilesMap = "";
        private static int _uiFilesTick;
        private static List<(string file, string tags)> _uiFiles = new List<(string, string)>();
        private static bool _uiWarned;

        private static List<(string file, string tags)> ExportLayouts(string map)
        {
            // Directory listing at most every 10 s; tags come from the per-file cache.
            if (map == _uiFilesMap && unchecked(Environment.TickCount - _uiFilesTick) < 10000) return _uiFiles;
            var list = new List<(string, string)>();
            if (map.Length > 0)
                foreach (var f in LayoutFiles(map) ?? new List<string>())
                    list.Add((f, LayoutTags(map, f, out _)));
            _uiFiles = list;
            _uiFilesMap = map;
            _uiFilesTick = Environment.TickCount;
            return list;
        }

        public static string? ModUIData()
        {
            try
            {
                var store = Pins();
                string map = GetCurrentMapName() ?? "";
                var layouts = ExportLayouts(map);
                string? pin = map.Length > 0 ? store.GetPin(map) : null;
                bool pinListed = pin != null && layouts.Any(l => string.Equals(l.file, pin, StringComparison.OrdinalIgnoreCase));
                string skip = PickIsForThisRound(map) ? _pinSkipReason : "";
                string others = string.Join(", ", store.Pins.Keys.Where(k => !string.Equals(k, map, StringComparison.OrdinalIgnoreCase)));

                string key = string.Join("|", map, ActiveLayoutText(map), pin ?? "", pinListed, store.Pinning, skip, others,
                                         string.Join(",", layouts.Select(l => l.file + ":" + l.tags)));
                if (_uiCache != null && key == _uiKey) return _uiCache;

                var x = ModUI.ModUIExport.New();
                if (map.Length == 0) x.Info("No map loaded.");
                else
                {
                    x.Info($"Map {map}: this round {ActiveLayoutText(map)}");
                    x.Info(pin == null ? $"No pin for {map}."
                         : $"Pin for {map}: {LayoutPinLogic.StripJson(pin)}" +
                           (!pinListed ? " (FILE MISSING)" : !store.Pinning ? " (pinning OFF: ignored)" : "") + " — applies from the next round start");
                    if (skip.Length > 0) x.Info($"Pin skipped this round: {skip} (random pick used)");
                }
                if (others.Length > 0) x.Info($"Pins on other maps: {others}");

                x.Section("Layout pin");
                x.OnOff("!layout pinning", "Pinning (master switch)",
                        "Off keeps the pins but every round uses the random pick. Saved in cfg/MapBalance.", true, store.Pinning);
                if (layouts.Count > 0)
                    x.Picker("!layout pin", "Pin layout (this map)",
                             "Forces this layout on this map from the next round start until unpinned. A round of another mode uses the random pick.",
                             true, "choice",
                             layouts.Select((l, i) => (LayoutPinLogic.StripJson(l.file),
                                 $"#{i + 1} {LayoutPinLogic.StripJson(l.file)} ({l.tags})" +
                                 (pin != null && string.Equals(pin, l.file, StringComparison.OrdinalIgnoreCase) ? " PINNED" : ""))).ToArray());
                x.Action("!layout unpin", "Unpin this map", "Removes the pin of the current map; the random pick is back from the next round start.", true);
                x.Action("!layout unpin all", "Clear all pins", "Removes the pins of every map; takes effect at the next round start.", true);
                x.Report("!layout list", "List layouts", "Lists the layout files of this map with their modes; the pinned one is marked.", true);
                x.Report("!layout status", "Status", "Shows the map, this round's layout, the pin and the master switch in chat.", true);

                _uiCache = x.Build();
                _uiKey = key;
                return _uiCache;
            }
            catch (Exception ex)
            {
                if (!_uiWarned) { _uiWarned = true; Melon<MapBalance>.Logger.Warning($"ModUIData failed: {ex.Message}"); }
                return null;
            }
        }
    }
}
