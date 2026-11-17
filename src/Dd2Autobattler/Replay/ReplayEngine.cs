using System;
using System.Collections.Generic;
using System.IO;
using Dd2Autobattler.Combat;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dd2Autobattler.Replay
{
    internal sealed class ReplayPick
    {
        public string Skill;
        public uint Target;
        public float Score;
        public string Reason;
        public bool Item;

        public bool Same(ReplayPick other)
        {
            if (other == null)
                return false;
            return string.Equals(Skill, other.Skill, StringComparison.OrdinalIgnoreCase) && Target == other.Target;
        }

        public override string ToString()
        {
            if (Skill == null)
                return "(no action)";
            return Skill + "->" + Target + " " + Score.ToString("0.0") + (string.IsNullOrEmpty(Reason) ? "" : " " + Reason);
        }
    }

    internal sealed class ReplayTurn
    {
        // <session>#<fight no>/t<turn_index>: stable across runs of the same logs.
        public string Key;
        public string Session;
        public int FightNo;
        public string Fight;
        public int TurnIndex;
        public int Round;
        public string Mode;
        public string Hero;
        public string HeroClass;
        public int Schema;
        public bool Full;
        public List<string> Degraded = new List<string>();
        public ReplayPick Logged;
        public ReplayPick Replayed;
        // Replayed score of the logged pick (null = logged pick not in replayed rows).
        public float? LoggedPickNow;
        // Logged score of the replayed pick.
        public float? ReplayedPickThen;
        public bool Agree;
        public int Rows;
        public int RowsScoreMatch;
        // With ReplayEngine.CollectRowDiffs: rows whose score moved, and the
        // logged fields that differ from the replayed row.
        public List<RowDiff> RowDiffs;

        [JsonIgnore]
        public TurnSnapshot Snapshot;
        // Search compare (Dd2Replay search option).
        [JsonIgnore]
        public Search.SearchResult Search;
        public bool InferredOrder;
    }

    internal sealed class RowDiff
    {
        public string Skill;
        public uint Target;
        public float LoggedScore;
        public float ReplayedScore;
        public Dictionary<string, JToken[]> Fields = new Dictionary<string, JToken[]>();
    }

    internal sealed class ReplaySummary
    {
        public int Files;
        public int Fights;
        public int Turns;
        public int Skipped;
        public int Full;
        public int Degraded;
        public int Agree;
        public int AgreeFull;
        public int AgreeDegraded;
        public int Rows;
        public int RowsScoreMatch;

        public static string Pct(int n, int d)
        {
            return d <= 0 ? "n/a" : (100.0 * n / d).ToString("0.0") + "%";
        }
    }

    internal sealed class ReplayFilter
    {
        public string Fight;
        public string Hero;
        public string Key;
    }

    /// <summary>
    /// Walks decisions.jsonl files, re-decides every turn with the current
    /// TurnDecider.DecideSnapshot, and compares with the logged pick.
    /// Schema-2 turns use the logged "snap" (full fidelity); schema-1 turns
    /// are rebuilt by LegacyTurn with combat memory simulated across the fight.
    /// </summary>
    internal sealed class ReplayEngine
    {
        public const float ScoreTolerance = 0.05f;

        public readonly List<ReplayTurn> Turns = new List<ReplayTurn>();
        public readonly ReplaySummary Summary = new ReplaySummary();
        public ReplayFilter Filter = new ReplayFilter();
        // Keep TurnSnapshot on each ReplayTurn (pinning).
        public bool KeepSnapshots;
        // Game CSV skill tags for schema-1 turns (null = none).
        public CsvSkillData Csv;
        // Fill ReplayTurn.RowDiffs (changed turns only unless AllRowDiffs).
        public bool CollectRowDiffs;
        public bool AllRowDiffs;

        // Optional round lookahead in compare mode (Dd2Replay search option).
        public Search.SearchSettings SearchSettings;
        public Search.GameData SearchData;

        public void RunFile(string path)
        {
            var session = SessionName(path);
            Summary.Files++;
            var state = new FightState();
            var fights = new FightCounter();
            var records = new List<JObject>();
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try { records.Add(JObject.Parse(line)); }
                catch { }
            }
            for (var ri = 0; ri < records.Count; ri++)
            {
                var o = records[ri];
                _records = records;
                _recordIndex = ri;
                var type = o.Value<string>("type");
                var isTurn = type == "turn" && o["note"] == null;
                if (fights.Next(type, o.Value<string>("fight"), isTurn))
                {
                    state = new FightState();
                    state.Start();
                    if (Matches(fights.FightId, Filter.Fight))
                        Summary.Fights++;
                }
                if (!isTurn)
                    continue;
                if (!state.Started)
                    state.Start();
                RunTurn(session, fights.FightNo, o.Value<string>("fight") ?? fights.FightId, fights.FightId, o, state);
            }
        }

        /// <summary>
        /// Fight numbering for turn keys. A fight_start opens a fight; so does a
        /// turn after a fight_end with no fight_start in between (story fights:
        /// in one log pillager_mash_202 ended, then lost_battalion_mash_204
        /// at a StoryResist node logged turns with no fight_start, and both shared
        /// #35, so baselines matched turn t1 of one fight against the other's).
        /// </summary>
        internal sealed class FightCounter
        {
            public int FightNo;
            public string FightId = "none";
            private bool _open;

            /// <summary>True when this record starts a new fight.</summary>
            public bool Next(string type, string fight, bool isTurn)
            {
                if (type == "fight_start")
                    return Open(fight);
                if (type == "fight_end")
                {
                    _open = false;
                    return false;
                }
                if (!isTurn)
                    return false;
                if (FightNo == 0 || !_open
                    || (!string.IsNullOrEmpty(fight) && fight != "none" && FightId != "fight" && fight != FightId))
                    return Open(fight);
                return false;
            }

            private bool Open(string fight)
            {
                FightNo++;
                FightId = fight ?? "fight";
                _open = true;
                return true;
            }
        }

        /// <summary>
        /// TurnSnapshot.FightId for a logged snapshot that predates the field
        /// (or a schema-1 rebuild): the fight_start that opened this fight,
        /// else the turn record's own "fight". A logged value is kept.
        /// </summary>
        internal static void FillFightId(TurnSnapshot snap, string fightStart, string turnFight)
        {
            if (snap == null || !string.IsNullOrEmpty(snap.FightId))
                return;
            snap.FightId = TurnSnapshot.NormalizeFightId(fightStart) ?? TurnSnapshot.NormalizeFightId(turnFight);
        }

        private void RunTurn(string session, int fightNo, string fight, string fightStart, JObject turn, FightState state)
        {
            var actor = turn["actor"] as JObject;
            var actorGuid = actor != null ? actor.Value<uint?>("guid") ?? 0u : 0u;
            var mode = turn.Value<string>("mode") ?? "auto";
            var commit = mode != "shadow";
            var hasSnap = turn["snap"] is JObject;

            // Round bookkeeping runs for every turn (filters must not change memory).
            if (!hasSnap)
                state.BeforeTurn(actorGuid, turn["focus"] as JObject);

            var key = session + "#" + fightNo + "/t" + (turn.Value<int?>("turn_index") ?? 0);
            var hero = actor != null ? actor.Value<string>("name") : null;
            var heroClass = actor != null ? actor.Value<string>("class") : null;
            var selected = Matches(fight, Filter.Fight)
                           && (Matches(hero, Filter.Hero) || Matches(heroClass, Filter.Hero))
                           && (string.IsNullOrEmpty(Filter.Key) || key == Filter.Key);
            // Schema-2 turns carry their own memory: no need to decide them
            // just to advance the fight state.
            if (hasSnap && !selected)
            {
                state.AfterTurn(actorGuid, false, turn);
                return;
            }

            TurnSnapshot snap;
            if (hasSnap)
                snap = SnapshotJson.Read(turn["snap"]);
            else
                snap = LegacyTurn.Build(turn, state.Memory, Csv);

            if (snap == null)
            {
                if (selected)
                    Summary.Skipped++;
                state.AfterTurn(actorGuid, false, null);
                return;
            }
            FillFightId(snap, fightStart, turn.Value<string>("fight"));

            // Search compare on a log without the game's turn order: use the
            // actors that actually acted later in the same round (action_result).
            var inferredOrder = false;
            if (SearchSettings != null && selected && (snap.RemainingTurns == null || snap.RemainingTurns.Count == 0))
            {
                var order = InferRemainingTurns(turn);
                if (order != null)
                {
                    snap.RemainingTurns = order;
                    snap.Degraded.Add("turn_order_from_action_results");
                    inferredOrder = true;
                }
            }

            var result = TurnDecider.DecideSnapshot(snap);
            Search.SearchResult search = null;
            if (SearchSettings != null && selected)
            {
                // Schema 1 never logged other heroes' equipped skills: after the
                // greedy decision, give the search the skills each hero showed
                // in this fight's own legal rows (kits do not change mid-fight).
                if (!hasSnap && FillHeroKits(snap))
                    snap.Degraded.Add("hero_kits_from_fight_rows");
                search = Search.RoundSearch.Run(snap, result, SearchSettings, SearchData);
            }
            var logged = LoggedPick(turn);
            var picked = logged != null ? FindCandidate(result, logged.Skill, logged.Target) : null;

            if (!hasSnap)
            {
                // Advance simulated memory with what the game actually did.
                var memory = result.Memory;
                if (commit && picked != null)
                    TurnDecider.ApplyCommit(memory, result, picked);
                state.Memory = memory;
            }
            state.AfterTurn(actorGuid, picked != null && picked.IsItem && picked.FreeAction, turn);

            if (!selected)
                return;

            var rt = new ReplayTurn
            {
                Key = key,
                Session = session,
                FightNo = fightNo,
                Fight = fight,
                TurnIndex = turn.Value<int?>("turn_index") ?? 0,
                Round = snap.Round,
                Mode = mode,
                Hero = hero,
                HeroClass = heroClass,
                Schema = snap.Schema,
                Full = snap.FullFidelity,
                Logged = logged,
                Replayed = ToPick(result.Chosen),
                Snapshot = KeepSnapshots ? snap : null,
                Search = search,
                InferredOrder = inferredOrder
            };
            if (snap.Degraded != null)
                rt.Degraded.AddRange(snap.Degraded);
            rt.Agree = logged == null ? result.Chosen == null : logged.Same(rt.Replayed);
            if (picked != null)
                rt.LoggedPickNow = picked.Score;
            var legal = turn["legal"] as JArray;
            if (rt.Replayed != null && rt.Replayed.Skill != null)
            {
                var row = FindRow(legal, rt.Replayed.Skill, rt.Replayed.Target);
                if (row != null)
                    rt.ReplayedPickThen = row.Value<float?>("score");
            }
            CompareRows(legal, result, rt, CollectRowDiffs && (AllRowDiffs || !rt.Agree));

            Turns.Add(rt);
            Summary.Turns++;
            Summary.Rows += rt.Rows;
            Summary.RowsScoreMatch += rt.RowsScoreMatch;
            if (rt.Full)
            {
                Summary.Full++;
                if (rt.Agree)
                    Summary.AgreeFull++;
            }
            else
            {
                Summary.Degraded++;
                if (rt.Agree)
                    Summary.AgreeDegraded++;
            }
            if (rt.Agree)
                Summary.Agree++;
        }

        private List<JObject> _records;
        private int _recordIndex;

        // Hero guid -> skill ids seen in that hero's legal rows in the current fight.
        private bool FillHeroKits(TurnSnapshot snap)
        {
            if (_records == null)
                return false;
            var start = _recordIndex;
            while (start > 0 && _records[start].Value<string>("type") != "fight_start")
                start--;
            var kits = new Dictionary<uint, List<string>>();
            for (var i = start + 1; i < _records.Count; i++)
            {
                var o = _records[i];
                var type = o.Value<string>("type");
                if (type == "fight_start" || type == "fight_end")
                    break;
                if (type != "turn")
                    continue;
                var g = (o["actor"] as JObject)?.Value<uint?>("guid") ?? 0u;
                if (g == 0 || !(o["legal"] is JArray legal))
                    continue;
                if (!kits.TryGetValue(g, out var list))
                    kits[g] = list = new List<string>();
                foreach (var row in legal)
                {
                    var id = (row as JObject)?.Value<string>("skill");
                    if (!string.IsNullOrEmpty(id) && !list.Contains(id))
                        list.Add(id);
                }
            }
            var filled = false;
            foreach (var h in snap.Heroes)
            {
                if (h == null || !kits.TryGetValue(h.Guid, out var list))
                    continue;
                var real = h.SkillIds != null && h.SkillIds.Exists(id => !string.IsNullOrEmpty(id) && !id.StartsWith("~", StringComparison.Ordinal));
                if (real)
                    continue;
                h.SkillIds = new List<string>(list);
                filled = true;
            }
            return filled;
        }

        // Performers of the action_result records after this turn's own action
        // that carry the same round, in order. Null when the log has none.
        // Actors that were stunned or died to DoT before acting are missing
        // (the model would skip them anyway); it is still observed, not the
        // game's planned order, so the turn is marked degraded.
        private List<uint> InferRemainingTurns(JObject turn)
        {
            if (_records == null)
                return null;
            var actor = (turn["actor"] as JObject)?.Value<uint?>("guid") ?? 0u;
            var skill = (turn["chosen"] as JObject)?.Value<string>("skill");
            int? round = null;
            var order = new List<uint>();
            for (var i = _recordIndex + 1; i < _records.Count; i++)
            {
                var o = _records[i];
                var type = o.Value<string>("type");
                if (type == "fight_start" || type == "fight_end")
                    break;
                if (type != "action_result")
                    continue;
                var perf = (o["performer"] as JObject)?.Value<uint?>("guid") ?? 0u;
                var r = o.Value<int?>("round");
                if (round == null)
                {
                    // First result must be this turn's own action.
                    if (perf != actor || !string.Equals(o.Value<string>("skill"), skill, StringComparison.OrdinalIgnoreCase))
                        return null;
                    round = r;
                    continue;
                }
                if (r != round)
                    break;
                if (perf != 0)
                    order.Add(perf);
            }
            return round == null ? null : order;
        }

        private static void CompareRows(JArray legal, TurnDecider.DecisionResult result, ReplayTurn rt, bool diffs)
        {
            if (diffs)
                rt.RowDiffs = new List<RowDiff>();
            if (legal == null || result.Candidates == null)
                return;
            var n = Math.Min(legal.Count, result.Candidates.Count);
            rt.Rows = legal.Count;
            for (var i = 0; i < n; i++)
            {
                var row = legal[i] as JObject;
                var c = result.Candidates[i];
                if (row == null || c == null)
                    continue;
                if (!string.Equals(row.Value<string>("skill"), c.SkillId, StringComparison.OrdinalIgnoreCase)
                    || (row.Value<uint?>("target") ?? 0u) != c.TargetGuid)
                    continue;
                var s = row.Value<float?>("score") ?? 0f;
                if (Math.Abs(s - c.Score) <= ScoreTolerance)
                {
                    rt.RowsScoreMatch++;
                    continue;
                }
                if (!diffs || i >= result.Rows.Count)
                    continue;
                var d = new RowDiff { Skill = c.SkillId, Target = c.TargetGuid, LoggedScore = s, ReplayedScore = c.Score };
                foreach (var prop in result.Rows[i].Properties())
                {
                    if (prop.Name == "score")
                        continue;
                    var then = row[prop.Name];
                    if (then == null || !JToken.DeepEquals(Norm(then), Norm(prop.Value)))
                        d.Fields[prop.Name] = new[] { then, prop.Value };
                }
                rt.RowDiffs.Add(d);
            }
        }

        // Floats compared at log precision.
        private static JToken Norm(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null || (t is JValue v && v.Value == null))
                return JValue.CreateNull();
            if (t.Type == JTokenType.Float)
                return new JValue(Math.Round((double)t, 3));
            if (t != null && t.Type == JTokenType.Integer)
                return new JValue(Math.Round((double)t, 3));
            return t;
        }

        internal static ReplayPick LoggedPick(JObject turn)
        {
            var chosen = turn["chosen"] as JObject;
            if (chosen == null)
                return null;
            return new ReplayPick
            {
                Skill = chosen.Value<string>("skill"),
                Target = chosen.Value<uint?>("target") ?? 0u,
                Score = chosen.Value<float?>("score") ?? 0f,
                Reason = chosen.Value<string>("reason") ?? turn.Value<string>("reason"),
                Item = chosen.Value<bool?>("item") ?? false
            };
        }

        internal static ReplayPick ToPick(ChosenAction chosen)
        {
            if (chosen == null)
                return null;
            return new ReplayPick
            {
                Skill = chosen.SkillId,
                Target = chosen.TargetGuid,
                Score = chosen.Score,
                Reason = chosen.Reason,
                Item = chosen.IsItem
            };
        }

        internal static TurnDecider.ScoredAction FindCandidate(TurnDecider.DecisionResult result, string skill, uint target)
        {
            if (result == null || result.Candidates == null || string.IsNullOrEmpty(skill))
                return null;
            foreach (var c in result.Candidates)
            {
                if (c != null && c.TargetGuid == target
                    && string.Equals(c.SkillId, skill, StringComparison.OrdinalIgnoreCase))
                    return c;
            }
            return null;
        }

        private static JObject FindRow(JArray legal, string skill, uint target)
        {
            if (legal == null)
                return null;
            foreach (var token in legal)
            {
                var row = token as JObject;
                if (row != null && (row.Value<uint?>("target") ?? 0u) == target
                    && string.Equals(row.Value<string>("skill"), skill, StringComparison.OrdinalIgnoreCase))
                    return row;
            }
            return null;
        }

        private static bool Matches(string value, string filter)
        {
            if (string.IsNullOrEmpty(filter))
                return true;
            return !string.IsNullOrEmpty(value) && value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static string SessionName(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                var name = Path.GetFileName(dir);
                return string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(path) : name;
            }
            catch
            {
                return Path.GetFileNameWithoutExtension(path);
            }
        }

        /// <summary>
        /// Simulated CombatMemory for schema-1 fights. Rounds are inferred: a
        /// hero who already acted this round starts the next one, unless the
        /// previous record was the same hero's free item. Dreaming General
        /// logs the real round, which overrides the guess.
        /// </summary>
        private sealed class FightState
        {
            public MemoryState Memory = new MemoryState();
            public bool Started;
            private readonly HashSet<uint> _actedThisRound = new HashSet<uint>();
            private uint _prevActor;
            private bool _prevFreeItem;

            public void Start()
            {
                Started = true;
                Memory = new MemoryState();
                Memory.NoteRound(1);
                _actedThisRound.Clear();
                _prevActor = 0;
                _prevFreeItem = false;
            }

            public void BeforeTurn(uint actor, JObject focus)
            {
                if (focus != null && focus["dg_round"] != null)
                {
                    var dg = focus.Value<int>("dg_round");
                    if (dg != Memory.Round)
                    {
                        Memory.NoteRound(dg);
                        _actedThisRound.Clear();
                    }
                    Memory.TaprootHitsThisRound = focus.Value<int?>("dg_taproot_hits") ?? Memory.TaprootHitsThisRound;
                }
                else if (actor != 0 && _actedThisRound.Contains(actor) && !(actor == _prevActor && _prevFreeItem))
                {
                    Memory.NoteRound(Memory.Round + 1);
                    _actedThisRound.Clear();
                }
                if (actor != 0)
                    _actedThisRound.Add(actor);
            }

            public void AfterTurn(uint actor, bool freeItem, JObject turn)
            {
                _prevActor = actor;
                _prevFreeItem = freeItem;
            }
        }
    }
}
