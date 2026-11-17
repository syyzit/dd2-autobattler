using Dd2Autobattler.Combat;

namespace Dd2Autobattler.Search
{
    /// <summary>
    /// One call between the pure greedy decision and its commit / log:
    /// Off does nothing, Compare runs the search and leaves the greedy pick,
    /// On replaces the pick when the search gains at least MinGain. The
    /// replacement is always a candidate that survived the cited vetoes.
    /// </summary>
    internal static class SearchHook
    {
        public const string Reason = "search_round";

        public static SearchResult Apply(TurnSnapshot snap, TurnDecider.DecisionResult result, SearchSettings s, GameData data)
        {
            if (s == null || s.Mode == SearchMode.Off || result == null || snap == null)
                return null;
            var r = RoundSearch.Run(snap, result, s, data);
            if (s.Mode == SearchMode.On && r.Abstain == null && r.Pick != null && !r.Agrees)
            {
                var pick = r.Pick.Action;
                result.Picked = pick;
                result.Chosen = new ChosenAction
                {
                    SkillId = pick.SkillId,
                    TargetGuid = pick.TargetGuid,
                    Reason = Reason,
                    IsItem = pick.IsItem,
                    Score = pick.Score
                };
                result.Reason = Reason;
                r.Overrode = true;
            }
            return r;
        }

        // Live entry: settings and CSV data from the plugin config.
        public static SearchResult ApplyLive(TurnSnapshot snap, TurnDecider.DecisionResult result)
        {
            var s = SearchConfig.Current;
            if (s == null || s.Mode == SearchMode.Off)
                return null;
            return Apply(snap, result, s, SearchConfig.Data());
        }
    }
}
