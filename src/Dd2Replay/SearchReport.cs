using System;
using System.Collections.Generic;
using System.Linq;
using Dd2Autobattler.Replay;
using Dd2Autobattler.Search;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dd2Replay
{
    /// <summary>Greedy vs round lookahead over replayed turns.</summary>
    internal static class SearchReport
    {
        public static void Print(ReplayEngine engine, bool json, int changes, bool verbose)
        {
            var turns = engine.Turns.Where(t => t.Search != null).ToList();
            var searched = turns.Where(t => t.Search.Abstain == null).ToList();
            var disagree = searched.Where(t => !t.Search.Agrees).ToList();
            var abstain = turns.Where(t => t.Search.Abstain != null)
                .GroupBy(t => Bucket(t.Search.Abstain))
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count());
            var ms = searched.Select(t => t.Search.Ms).OrderBy(x => x).ToList();
            var nodes = searched.Select(t => (double)t.Search.Nodes).ToList();
            var withOrder = turns.Count(t => !t.InferredOrder && t.Snapshot == null && t.Full);

            if (json)
            {
                var shown = changes <= 0 ? disagree : disagree.Take(changes).ToList();
                var obj = new JObject
                {
                    ["summary"] = new JObject
                    {
                        ["turns"] = turns.Count,
                        ["searched"] = searched.Count,
                        ["inferred_turn_order"] = turns.Count(t => t.InferredOrder),
                        ["abstained"] = JObject.FromObject(abstain),
                        ["disagree"] = disagree.Count,
                        ["disagree_pct_of_searched"] = searched.Count > 0 ? Math.Round(100.0 * disagree.Count / searched.Count, 2) : (double?)null,
                        ["ms_mean"] = ms.Count > 0 ? Math.Round(ms.Average(), 3) : (double?)null,
                        ["ms_p95"] = ms.Count > 0 ? Math.Round(ms[(int)Math.Min(ms.Count - 1, Math.Floor(ms.Count * 0.95))], 3) : (double?)null,
                        ["ms_max"] = ms.Count > 0 ? Math.Round(ms.Max(), 3) : (double?)null,
                        ["nodes_mean"] = nodes.Count > 0 ? Math.Round(nodes.Average(), 1) : (double?)null
                    },
                    ["disagreements"] = new JArray(shown.Select(t => Row(t)).ToArray())
                };
                Console.WriteLine(obj.ToString(Formatting.Indented));
                return;
            }

            Console.WriteLine("Round lookahead vs greedy: " + turns.Count + " turns, searched " + searched.Count
                              + " (" + turns.Count(t => t.InferredOrder) + " with turn order inferred from action_result)");
            if (abstain.Count > 0)
                Console.WriteLine("  abstained: " + string.Join(", ", abstain.Select(kv => kv.Key + " " + kv.Value)));
            Console.WriteLine("  disagree: " + disagree.Count + "/" + searched.Count
                              + (searched.Count > 0 ? " (" + (100.0 * disagree.Count / searched.Count).ToString("0.0") + "%)" : ""));
            if (ms.Count > 0)
                Console.WriteLine("  time per searched decision: mean " + ms.Average().ToString("0.000") + " ms, p95 "
                                  + ms[(int)Math.Min(ms.Count - 1, Math.Floor(ms.Count * 0.95))].ToString("0.000") + " ms, max "
                                  + ms.Max().ToString("0.000") + " ms; nodes mean " + nodes.Average().ToString("0.0"));
            var list = changes <= 0 ? disagree : disagree.Take(changes).ToList();
            foreach (var t in list)
            {
                var s = t.Search;
                Console.WriteLine();
                Console.WriteLine("  " + t.Key + " " + t.Fight + " r" + t.Round + " " + t.Hero + (t.InferredOrder ? " [order from action_result]" : "")
                                  + " gate " + s.Gate + "   logged " + (t.Logged != null ? t.Logged.Skill + "->" + t.Logged.Target : "-"));
                Console.WriteLine("      greedy " + Cand(s.Greedy) + "   " + (s.Greedy.Outcome != null ? s.Greedy.Outcome.ToString() : ""));
                Console.WriteLine("      search " + Cand(s.Pick) + " gain " + s.Gain.ToString("0.0") + "   " + (s.Pick.Outcome != null ? s.Pick.Outcome.ToString() : ""));
                if (s.Line != null)
                    foreach (var l in s.Line)
                        Console.WriteLine("        | " + l);
                if (verbose && s.GreedyLine != null)
                    foreach (var l in s.GreedyLine)
                        Console.WriteLine("        g " + l);
            }
            if (list.Count < disagree.Count)
                Console.WriteLine("\n  ... " + (disagree.Count - list.Count) + " more (--changes 0 for all, --json for machine output)");
        }

        private static string Bucket(string abstain)
        {
            return abstain;
        }

        private static string Cand(SearchCandidate c)
        {
            if (c == null)
                return "-";
            return c.Action.SkillId + "->" + c.Action.TargetGuid + " score " + c.Score.ToString("0.0")
                   + " future " + c.Future.ToString("+0.0;-0.0") + " value " + c.Value.ToString("0.0");
        }

        private static JObject Row(ReplayTurn t)
        {
            return new JObject
            {
                ["key"] = t.Key,
                ["fight"] = t.Fight,
                ["round"] = t.Round,
                ["hero"] = t.Hero,
                ["inferred_turn_order"] = t.InferredOrder,
                ["logged"] = t.Logged != null ? t.Logged.Skill + "->" + t.Logged.Target : null,
                ["search"] = t.Search.ToJson()
            };
        }
    }
}
