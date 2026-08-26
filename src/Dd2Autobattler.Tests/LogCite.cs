using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Dd2Autobattler.Tests
{
    /// <summary>
    /// Cited-note checks on a logged turn (legal + chosen). Mirrors _tools/dd2logs.py cite_turn.
    /// Replay without going through PickAction / the live game.
    /// </summary>
    internal static class LogCite
    {
        public static List<string> Check(JObject turn)
        {
            var hits = new List<string>();
            if (turn == null)
                return hits;
            var chosen = turn["chosen"] as JObject;
            if (chosen == null)
                return hits;
            var skill = chosen.Value<string>("skill") ?? "";
            var reason = turn.Value<string>("reason") ?? chosen.Value<string>("reason") ?? "";
            var legal = turn["legal"] as JArray ?? new JArray();
            var picked = Match(legal, chosen);
            var kind = picked != null ? picked.Value<string>("kind") ?? "" : "";
            var stalks = StalksUp(turn);

            JObject bestKill = null;
            foreach (var token in legal)
            {
                var row = token as JObject;
                if (CitedKill(row) && (bestKill == null || row.Value<float>("score") > bestKill.Value<float>("score")))
                    bestKill = row;
            }

            if (IsSetup(skill, kind, reason) && bestKill != null)
                hits.Add("setup_over_kill");
            else if (IsSetup(skill, kind, reason))
            {
                JObject bestAtk = null;
                foreach (var token in legal)
                {
                    var row = token as JObject;
                    if (row == null || row.Value<string>("kind") != "Attack" || !row.Value<bool>("enemy"))
                        continue;
                    if (bestAtk == null || row.Value<float>("score") > bestAtk.Value<float>("score"))
                        bestAtk = row;
                }
                var chosenScore = chosen.Value<float>("score");
                if (bestAtk != null && bestAtk.Value<float>("score") >= chosenScore + 30f)
                    hits.Add("setup_over_swing");
            }

            if (stalks)
            {
                var aoe = IsAoe(skill);
                var kills = picked != null && picked.Value<bool>("kills");
                var hitN = picked != null ? picked.Value<int>("hit_n") : 0;
                var dot = 0f;
                if (picked != null)
                    dot = picked.Value<float>("apply_bleed") + picked.Value<float>("apply_blight") + picked.Value<float>("apply_burn");
                if (aoe && !kills && hitN >= 2 && dot <= 0.05f)
                    hits.Add("stalk_aoe_chip");
                if (picked != null && !picked.Value<bool>("kills") && bestKill != null && !IsSetup(skill, kind, reason))
                {
                    var gap = System.Math.Abs(picked.Value<float>("score") - bestKill.Value<float>("score"));
                    if (gap < 8f)
                        hits.Add("stalk_skip_kill");
                }
            }

            return hits;
        }

        private static bool StalksUp(JObject turn)
        {
            var enemies = turn["enemies"] as JArray ?? new JArray();
            foreach (var token in enemies)
            {
                var row = token as JObject;
                var cls = row != null ? row.Value<string>("class") ?? "" : "";
                if (cls.IndexOf("eyes_stalk") >= 0)
                    return true;
            }
            return false;
        }

        private static JObject Match(JArray legal, JObject chosen)
        {
            var skill = chosen.Value<string>("skill");
            var target = chosen.Value<uint>("target");
            foreach (var token in legal)
            {
                var row = token as JObject;
                if (row != null && row.Value<string>("skill") == skill && row.Value<uint>("target") == target)
                    return row;
            }
            foreach (var token in legal)
            {
                var row = token as JObject;
                if (row != null && row.Value<string>("skill") == skill)
                    return row;
            }
            return null;
        }

        private static bool CitedKill(JObject row)
        {
            if (row == null || !row.Value<bool>("kills"))
                return false;
            var why = (row.Value<string>("focus_why") ?? "").ToLowerInvariant();
            return why.IndexOf("eyes") >= 0 || why.IndexOf("altar") >= 0 || why.IndexOf("librarian") >= 0;
        }

        private static bool IsAoe(string skill)
        {
            var s = (skill ?? "").ToLowerInvariant();
            return s.IndexOf("flashing") >= 0 || s.IndexOf("blinding_gas") >= 0;
        }

        private static bool IsSetup(string skill, string kind, string reason)
        {
            var s = (skill ?? "").ToLowerInvariant();
            var r = (reason ?? "").ToLowerInvariant();
            if (r.StartsWith("item_stress") || r == "pass_stress" || r == "setup_once")
                return true;
            if (s == "laudanum" || s == "pass_stress")
                return true;
            return false;
        }
    }
}
