/*
 Layout pins (3.5.0): the pure, Unity-free part of "!layout pin".
 - LayoutPinStore: per-map pins (map -> layout file name) + the master switch, persisted
   as UserData/cfg/MapBalance/layout_pins.json.
 - LayoutPinLogic: matching "<name|#>" against a map's layout files, mode tags, and the
   "can this layout run in this round" check that mirrors the random pick's mode filter.
 Depends on System + Newtonsoft.Json only, so it can be compiled into a plain test program.
*/

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Si_MapBalance
{
    public enum LayoutMatch { Ok, NotFound, Ambiguous, Empty }

    public static class LayoutPinLogic
    {
        public const string Ext = ".json";

        public static string StripJson(string name) =>
            name.EndsWith(Ext, StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - Ext.Length) : name;

        /// <summary>Display / numbering order of a map's layout files: case-insensitive name order.</summary>
        public static List<string> SortLayouts(IEnumerable<string> fileNames) =>
            fileNames.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ThenBy(f => f, StringComparer.Ordinal).ToList();

        /// <summary>
        /// Resolve "&lt;name|#&gt;" against the map's layout files (file names incl. .json, in list order).
        /// Order: exact name (with or without .json, case-insensitive), then 1-based list number,
        /// then a unique case-insensitive substring of the name. More than one substring hit = Ambiguous.
        /// </summary>
        public static LayoutMatch MatchLayout(IReadOnlyList<string> fileNames, string query, out string? match, out List<string> candidates)
        {
            match = null;
            candidates = new List<string>();
            string q = StripJson((query ?? "").Trim().Trim('"'));
            if (q.Length == 0) return LayoutMatch.Empty;

            foreach (var f in fileNames)
                if (string.Equals(StripJson(f), q, StringComparison.OrdinalIgnoreCase)) { match = f; return LayoutMatch.Ok; }

            string num = q.StartsWith("#") ? q.Substring(1) : q;
            if (num.Length > 0 && num.All(char.IsDigit))
            {
                if (int.TryParse(num, out int n) && n >= 1 && n <= fileNames.Count) { match = fileNames[n - 1]; return LayoutMatch.Ok; }
                return LayoutMatch.NotFound;
            }

            foreach (var f in fileNames)
                if (StripJson(f).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) candidates.Add(f);
            if (candidates.Count == 1) { match = candidates[0]; candidates.Clear(); return LayoutMatch.Ok; }
            return candidates.Count == 0 ? LayoutMatch.NotFound : LayoutMatch.Ambiguous;
        }

        /// <summary>Case-insensitive exact map lookup; returns the canonical spelling or null.</summary>
        public static string? ResolveMap(IEnumerable<string> maps, string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            foreach (var m in maps)
                if (string.Equals(m, token, StringComparison.OrdinalIgnoreCase)) return m;
            return null;
        }

        /// <summary>
        /// Arguments after "pin": "&lt;name|#&gt;" (current map) or "&lt;map&gt; &lt;name|#&gt;". The first token is a
        /// map only when more tokens follow and it names a known map. The rest is joined with
        /// spaces (layout file names may contain spaces, e.g. "... (1)").
        /// </summary>
        public static void SplitPinArgs(IReadOnlyList<string> args, Func<string, string?> resolveMap, string? currentMap,
                                        out string? map, out string query)
        {
            if (args.Count >= 2)
            {
                string? m = resolveMap(args[0]);
                if (m != null) { map = m; query = string.Join(" ", args.Skip(1)); return; }
            }
            map = currentMap;
            query = string.Join(" ", args);
        }

        /// <summary>"game_modes": { ..., "2v1": true } (Si_VersusVote 2v1 rounds).</summary>
        public static bool IsTwoVsOne(JObject obj) =>
            obj["game_modes"] is JObject gm
            && gm["2v1"] is JToken v && v.Type == JTokenType.Boolean && v.Value<bool>();

        public static bool HasKoh(JObject obj)
        {
            var koh = obj["koh"];
            return koh != null && koh.Type == JTokenType.Object;
        }

        private static bool ModeFlag(JToken modes, string mode)
        {
            try { return modes[mode]?.Value<bool>() == true; }
            catch { return false; }
        }

        /// <summary>The modes a layout lists as true, in a fixed order; empty = mode-agnostic (no game_modes).</summary>
        public static List<string> Modes(JObject obj)
        {
            var list = new List<string>();
            if (!(obj["game_modes"] is JObject gm)) return list;
            foreach (var m in new[] { "hvh", "hva", "hvhva", "4way" })
                if (ModeFlag(gm, m)) list.Add(m);
            foreach (var p in gm.Properties())
                if (p.Name != "2v1" && !list.Contains(p.Name) && ModeFlag(gm, p.Name)) list.Add(p.Name);
            return list;
        }

        /// <summary>Short tag for lists: "hvh, hva", "any mode", plus "2v1" / "KoH".</summary>
        public static string ModeTags(JObject obj)
        {
            var parts = new List<string>();
            if (obj["game_modes"] == null) parts.Add("any mode");
            else
            {
                var modes = Modes(obj);
                if (modes.Count > 0) parts.Add(string.Join(", ", modes));
                else if (!IsTwoVsOne(obj)) parts.Add("no mode");
            }
            if (IsTwoVsOne(obj)) parts.Add("2v1");
            if (HasKoh(obj)) parts.Add("KoH");
            return string.Join(", ", parts);
        }

        /// <summary>
        /// Can this layout run in the round? Mirrors the random pick's pools:
        /// 2v1 round: a "2v1" layout; if the map has no 2v1 layout at all, the random pick falls
        /// back to the normal 3-way pool, so a plain hvhva layout is accepted then.
        /// Normal round: no game_modes (mode-agnostic) or game_modes[mode] = true; a 2v1 layout
        /// never runs in a normal hvhva round.
        /// </summary>
        public static bool SupportsRound(JObject layout, string gameMode, bool twoVsOne, bool mapHasTwoVsOne, out string reason)
        {
            reason = "";
            if (twoVsOne)
            {
                if (IsTwoVsOne(layout)) return true;
                if (mapHasTwoVsOne)
                {
                    reason = "not a 2v1 layout, round is 2v1";
                    return false;
                }
                // no 2v1 layout on the map: same as the random fallback (normal 3-way pool)
            }

            var modes = layout["game_modes"];
            if (modes != null)
            {
                if (!(modes is JObject))
                {
                    reason = "unreadable game_modes";
                    return false;
                }
                if (!ModeFlag(modes, gameMode))
                {
                    var listed = Modes(layout);
                    reason = $"layout has no mode {gameMode} (has {(listed.Count > 0 ? string.Join(", ", listed) : "none")})";
                    return false;
                }
            }
            if (!twoVsOne && gameMode == "hvhva" && IsTwoVsOne(layout))
            {
                reason = "2v1 layout, round is a normal 3-way";
                return false;
            }
            return true;
        }
    }

    /// <summary>Per-map layout pins + master switch, persisted as a small JSON file.</summary>
    public sealed class LayoutPinStore
    {
        /// <summary>Master switch: off keeps the pins but the random pick ignores them.</summary>
        public bool Pinning = true;

        /// <summary>map -> layout file name (incl. .json). Sorted, so saves and exports are stable.</summary>
        public readonly SortedDictionary<string, string> Pins = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string? GetPin(string? map) =>
            !string.IsNullOrEmpty(map) && Pins.TryGetValue(map!, out var f) ? f : null;

        public void SetPin(string map, string fileName)
        {
            Pins.Remove(map); // keep the new key's spelling
            Pins[map] = fileName;
        }

        public bool RemovePin(string map) => Pins.Remove(map);

        public int Clear()
        {
            int n = Pins.Count;
            Pins.Clear();
            return n;
        }

        public string ToJson()
        {
            var pins = new JObject();
            foreach (var kv in Pins) pins[kv.Key] = kv.Value;
            var root = new JObject
            {
                ["pinning"] = Pinning,
                ["pins"] = pins,
            };
            return JsonConvert.SerializeObject(root, Formatting.Indented);
        }

        public static LayoutPinStore FromJson(string json)
        {
            var store = new LayoutPinStore();
            var root = JObject.Parse(json);
            if (root["pinning"] is JToken p && p.Type == JTokenType.Boolean) store.Pinning = p.Value<bool>();
            if (root["pins"] is JObject pins)
                foreach (var prop in pins.Properties())
                    if (prop.Value.Type == JTokenType.String && !string.IsNullOrWhiteSpace(prop.Value.Value<string>()))
                        store.Pins[prop.Name] = prop.Value.Value<string>()!.Trim();
            return store;
        }

        /// <summary>Missing file = defaults (pinning on, no pins). Unreadable file = defaults + error text.</summary>
        public static LayoutPinStore Load(string path, out string? error)
        {
            error = null;
            try
            {
                if (!File.Exists(path)) return new LayoutPinStore();
                return FromJson(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return new LayoutPinStore();
            }
        }

        /// <summary>Write via a temp file so a crash never leaves a half-written pins file.</summary>
        public void Save(string path)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson());
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
