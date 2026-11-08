using MelonLoader;
using System;
using System.Reflection;

namespace Si_MapBalance
{
    /// <summary>
    /// Reads Si_VersusVote's public API by reflection (no compile-time reference):
    ///   Si_VersusVote.VersusApi { const int ApiVersion; static bool TwoVsOneActive;
    ///                             static string CurrentMap; static string LayoutShort; }
    /// VersusVote sets TwoVsOneActive in its HarmonyPriority.First PREFIX on
    /// MP_Strategy.SetTeamVersusMode, i.e. before the original runs, and clears it on
    /// MP_Strategy.Restart. Si_MapBalance picks its layout AFTER SetTeamVersusMode
    /// returned (DistributeAllResources prefix, or SpawnBaseStructures on the first team
    /// join), so the flag is always settled when it is read.
    ///
    /// Missing mod / older or unknown API = "not a 2v1 round" (behaviour as before 3.4.0).
    /// The type is looked up lazily; a miss is retried once per round (ResetForNewRound),
    /// so a VersusVote assembly that loads late is still found.
    /// </summary>
    internal static class VersusVoteBridge
    {
        private const string TypeName = "Si_VersusVote.VersusApi";
        private const int MinApiVersion = 1;

        private static Type? _apiType;
        private static MemberInfo? _twoVsOneMember;
        private static MemberInfo? _currentMapMember;
        private static MemberInfo? _layoutShortMember;
        private static bool _searchedThisRound;
        private static string _lastState = "";

        /// <summary>Allow a new lookup if the previous one found nothing.</summary>
        internal static void ResetForNewRound() => _searchedThisRound = false;

        internal static bool IsAvailable => Resolve();

        /// <summary>True only when Si_VersusVote is loaded and says this round is a 2v1.</summary>
        internal static bool TwoVsOneActive
        {
            get
            {
                if (!Resolve()) return false;
                try { return ReadStatic(_twoVsOneMember) is bool b && b; }
                catch (Exception ex)
                {
                    Melon<MapBalance>.Logger.Warning($"VersusVote bridge: reading TwoVsOneActive failed: {ex.Message}");
                    return false;
                }
            }
        }

        internal static string CurrentMap => SafeString(_currentMapMember);
        internal static string LayoutShort => SafeString(_layoutShortMember);

        private static string SafeString(MemberInfo? m)
        {
            if (!Resolve()) return "";
            try { return ReadStatic(m) as string ?? ""; }
            catch { return ""; }
        }

        private static bool Resolve()
        {
            if (_apiType != null) return true;
            if (_searchedThisRound) return false;
            _searchedThisRound = true;

            Type? t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType(TypeName, false); }
                catch { t = null; }
                if (t != null) break;
            }

            if (t == null)
            {
                LogStateOnce("absent", "VersusVote bridge: Si_VersusVote not loaded — 2v1 layouts never picked, 3-way rounds as before");
                return false;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            int version = 0;
            try { version = Convert.ToInt32(t.GetField("ApiVersion", flags)?.GetValue(null) ?? 0); }
            catch { version = 0; }

            var twoVsOne = (MemberInfo?)t.GetProperty("TwoVsOneActive", flags) ?? t.GetField("TwoVsOneActive", flags);
            if (version < MinApiVersion || twoVsOne == null)
            {
                LogStateOnce($"bad:{version}", $"VersusVote bridge: {TypeName} found but unusable (ApiVersion {version}, TwoVsOneActive {(twoVsOne != null ? "found" : "missing")}) — ignoring it");
                return false;
            }

            _apiType = t;
            _twoVsOneMember = twoVsOne;
            _currentMapMember = (MemberInfo?)t.GetProperty("CurrentMap", flags) ?? t.GetField("CurrentMap", flags);
            _layoutShortMember = (MemberInfo?)t.GetProperty("LayoutShort", flags) ?? t.GetField("LayoutShort", flags);
            LogStateOnce($"ok:{version}", $"VersusVote bridge: found {TypeName} (ApiVersion {version}) — 2v1 rounds pick 2v1-flagged layouts");
            return true;
        }

        private static object? ReadStatic(MemberInfo? m) => m switch
        {
            PropertyInfo p => p.GetValue(null),
            FieldInfo f => f.GetValue(null),
            _ => null,
        };

        private static void LogStateOnce(string state, string msg)
        {
            if (state == _lastState) return;
            _lastState = state;
            Melon<MapBalance>.Logger.Msg(msg);
        }
    }
}
