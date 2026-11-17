using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dd2Autobattler.Combat;
using Newtonsoft.Json.Linq;

namespace Dd2Autobattler.Search
{
    internal sealed class SearchOutcome
    {
        // Expectations at the end of the round, relative to the root board.
        public float EnemyKills;
        public float EnemyHpLost;
        public float HeroHpLost;
        public float HeroDoor;
        public float HeroDeath;

        public JObject ToJson()
        {
            return new JObject
            {
                ["enemy_kills"] = R(EnemyKills),
                ["enemy_hp_lost"] = R(EnemyHpLost),
                ["hero_hp_lost"] = R(HeroHpLost),
                ["hero_door"] = R(HeroDoor),
                ["hero_death"] = R(HeroDeath)
            };
        }

        public override string ToString()
        {
            return "kills " + EnemyKills.ToString("0.00") + ", enemy hp -" + EnemyHpLost.ToString("0.0")
                   + ", hero hp -" + HeroHpLost.ToString("0.0") + ", door " + HeroDoor.ToString("0.00")
                   + ", death " + HeroDeath.ToString("0.00");
        }

        internal static double R(float v)
        {
            return Math.Round(v, 2);
        }
    }

    internal sealed class SearchCandidate
    {
        public TurnDecider.ScoredAction Action;
        public float Score;
        public float Future;
        public float Value;
        public SearchOutcome Outcome;

        public JObject ToJson()
        {
            return new JObject
            {
                ["skill"] = Action.SkillId,
                ["target"] = Action.TargetGuid,
                ["score"] = SearchOutcome.R(Score),
                ["future"] = SearchOutcome.R(Future),
                ["value"] = SearchOutcome.R(Value)
            };
        }
    }

    internal sealed class SearchResult
    {
        public SearchMode Mode;
        // Null when the search ran; otherwise why it did not.
        public string Abstain;
        public string Gate;
        public SearchCandidate Pick;
        public SearchCandidate Greedy;
        public float Gain;
        // True when On mode replaced the greedy pick.
        public bool Overrode;
        public readonly List<SearchCandidate> Evaluated = new List<SearchCandidate>();
        public List<string> Line;
        public List<string> GreedyLine;
        public int Nodes;
        public double Ms;
        public int Unmodeled;

        public bool Agrees
        {
            get
            {
                return Pick == null || Greedy == null
                       || (Pick.Action.SkillId == Greedy.Action.SkillId && Pick.Action.TargetGuid == Greedy.Action.TargetGuid);
            }
        }

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["mode"] = Mode.ToString().ToLowerInvariant(),
                ["gate"] = Gate
            };
            if (Abstain != null)
            {
                o["abstain"] = Abstain;
                o["ms"] = Math.Round(Ms, 3);
                return o;
            }
            o["agree"] = Agrees;
            o["overrode"] = Overrode;
            if (Pick != null)
            {
                o["pick"] = Pick.ToJson();
                o["predicted"] = Pick.Outcome != null ? Pick.Outcome.ToJson() : null;
            }
            if (Greedy != null)
            {
                o["greedy"] = Greedy.ToJson();
                o["greedy_predicted"] = Greedy.Outcome != null ? Greedy.Outcome.ToJson() : null;
            }
            o["gain"] = SearchOutcome.R(Gain);
            var evals = new JArray();
            foreach (var c in Evaluated)
                evals.Add(c.ToJson());
            o["evaluated"] = evals;
            if (Line != null)
                o["line"] = new JArray(Line.ToArray());
            if (GreedyLine != null && !Agrees)
                o["greedy_line"] = new JArray(GreedyLine.ToArray());
            o["nodes"] = Nodes;
            o["ms"] = Math.Round(Ms, 3);
            if (Unmodeled != 0)
                o["unmodeled"] = Unmodeled;
            return o;
        }
    }

    /// <summary>
    /// Lookahead to the end of the current round on top of the greedy pick.
    ///
    /// Root: the performer's legal actions that survived every cited veto
    /// (PickAction trace) and score within Margin of the greedy pick, applied
    /// with the game's exact preview. Then the game's remaining turn order:
    /// enemies act as an expectation over their usable skills and targets,
    /// later heroes follow a one-step model policy (top AllyBranch actions
    /// branched at the first ally turn), DoT ticks and stuns apply, kills
    /// remove turns. Value = greedy score + FutureWeight x (board value at
    /// round end - board value right after the root action), so the greedy
    /// scorer stays the leaf heuristic for the action itself and the search
    /// only adds what happens later in the round.
    ///
    /// Never searched: picks forced by a cited gate (crisis heal, cultist
    /// defense, corpse reach, rank walks, setup, free items, hunger eat).
    /// Deterministic (no randomness; wall-clock cap only abandons).
    /// </summary>
    internal static class RoundSearch
    {
        // Gates whose pick is the scorer's choice among equals, not a cited force.
        private static readonly HashSet<string> SearchableGates = new HashSet<string>
        {
            "attack", "attack_over_idle", "any", "any_over_negative_attack", "heal_beats_attack"
        };

        private sealed class Ctx
        {
            public TurnSnapshot Snap;
            public GameData Data;
            public SearchSettings S;
            public float[] Threat = new float[SimBoard.Capacity];
            public SimBoard Root;
            public int Nodes;
            public Stopwatch Clock;
            public bool TimedOut;
            public readonly List<SimBoard> Pool = new List<SimBoard>();
            public int PoolUsed;

            public SimBoard Rent()
            {
                if (PoolUsed >= Pool.Count)
                    Pool.Add(new SimBoard());
                return Pool[PoolUsed++];
            }
        }

        [ThreadStatic]
        private static Ctx _ctx;

        public static SearchResult Run(TurnSnapshot snap, TurnDecider.DecisionResult greedy, SearchSettings s, GameData data)
        {
            var clock = Stopwatch.StartNew();
            var res = new SearchResult { Mode = s != null ? s.Mode : SearchMode.Off };
            try
            {
                Search(snap, greedy, s, data, res, clock);
            }
            catch (Exception ex)
            {
                res.Abstain = "error:" + ex.GetType().Name;
                res.Pick = null;
            }
            res.Ms = clock.Elapsed.TotalMilliseconds;
            return res;
        }

        private static void Search(TurnSnapshot snap, TurnDecider.DecisionResult greedy, SearchSettings s, GameData data, SearchResult res, Stopwatch clock)
        {
            if (s == null || s.Mode == SearchMode.Off)
            {
                res.Abstain = "off";
                return;
            }
            var picked = greedy != null ? greedy.Picked : null;
            res.Gate = greedy != null && greedy.Trace != null ? greedy.Trace.Gate : null;
            if (picked == null)
            {
                res.Abstain = "no_pick";
                return;
            }
            if (data == null || !data.Loaded)
            {
                res.Abstain = "no_game_data";
                return;
            }
            if (res.Gate == null || !SearchableGates.Contains(res.Gate))
            {
                res.Abstain = "gate:" + (res.Gate ?? "none");
                return;
            }
            if (snap.RemainingTurns == null || snap.RemainingTurns.Count == 0)
            {
                res.Abstain = "no_turn_order";
                return;
            }

            if (ActionClass(picked) == 0)
            {
                res.Abstain = "greedy_pick_not_attack_or_heal";
                return;
            }
            var alts = Alternatives(greedy, picked, s);
            if (alts.Count < 2)
            {
                res.Abstain = "single_option";
                return;
            }

            var ctx = _ctx ?? (_ctx = new Ctx());
            ctx.Snap = snap;
            ctx.Data = data;
            ctx.S = s;
            ctx.Nodes = 0;
            ctx.Clock = clock;
            ctx.TimedOut = false;
            ctx.PoolUsed = 0;
            ctx.Root = ForwardModel.Build(snap, data, greedy.Focus, ctx.Rent());
            var performer = ctx.Root.IndexOf(snap.PerformerGuid);
            if (performer < 0)
            {
                res.Abstain = "performer_not_on_board";
                return;
            }
            for (var i = 0; i < ctx.Root.Count; i++)
                ctx.Threat[i] = ctx.Root.Actors[i].Hero ? 0f : EnemyThreat(ctx.Root, data, i);

            var mark = ctx.PoolUsed;
            for (var i = 0; i < alts.Count; i++)
            {
                if (ctx.Nodes >= s.NodeBudget && i > 0)
                    break;
                ctx.PoolUsed = mark;
                var c = alts[i];
                var board = ctx.Rent();
                board.CopyFrom(ctx.Root);
                ForwardModel.ApplyPreview(board, data, performer, c.SkillId, c.TargetGuid, c.Preview, snap.Skill(c.SkillId));
                var after = Eval(ctx, board);
                var end = Rollout(ctx, board, false, null);
                if (ctx.TimedOut)
                {
                    res.Abstain = "timeout";
                    res.Nodes = ctx.Nodes;
                    return;
                }
                var cand = new SearchCandidate
                {
                    Action = c,
                    Score = c.Score,
                    Future = end - after,
                    Outcome = Outcome(ctx.Root, board)
                };
                cand.Value = c.Score + s.FutureWeight * cand.Future;
                res.Evaluated.Add(cand);
                res.Unmodeled |= board.Unmodeled;
            }

            SearchCandidate best = null;
            SearchCandidate greedyEval = null;
            foreach (var c in res.Evaluated)
            {
                if (c.Action == picked)
                    greedyEval = c;
                if (best == null || c.Value > best.Value + 0.0001f)
                    best = c;
            }
            if (greedyEval == null)
            {
                res.Abstain = "budget";
                res.Nodes = ctx.Nodes;
                return;
            }
            res.Greedy = greedyEval;
            res.Gain = best.Value - greedyEval.Value;
            res.Pick = res.Gain >= s.MinGain ? best : greedyEval;
            res.Nodes = ctx.Nodes;

            // Principal lines (diagnostics; outside the budget).
            res.Line = Line(ctx, performer, res.Pick.Action);
            if (!res.Agrees)
                res.GreedyLine = Line(ctx, performer, picked);
        }

        private static List<TurnDecider.ScoredAction> Alternatives(TurnDecider.DecisionResult greedy, TurnDecider.ScoredAction picked, SearchSettings s)
        {
            var list = new List<TurnDecider.ScoredAction> { picked };
            var eligible = greedy.Trace != null ? greedy.Trace.Eligible : null;
            if (eligible == null)
                return list;
            var pool = new List<TurnDecider.ScoredAction>();
            foreach (var c in eligible)
            {
                if (c == picked || c.IsItem || c.Preview == null || !c.Preview.Ok)
                    continue;
                if (c.Score < picked.Score - s.Margin)
                    continue;
                // Same action class as the greedy pick: attack vs attack (target,
                // kill sequencing, overkill) or heal vs heal (whom). Heal-vs-attack
                // is decided by cited gates (crisis heal, Pain, idle vs a
                // connecting hit) and stays with greedy.
                if (ActionClass(c) == 0 || ActionClass(c) != ActionClass(picked))
                    continue;
                pool.Add(c);
            }
            // Best greedy score first; legal order breaks ties (stable).
            for (var i = 1; i < pool.Count; i++)
            {
                var x = pool[i];
                var j = i - 1;
                while (j >= 0 && pool[j].Score < x.Score)
                {
                    pool[j + 1] = pool[j];
                    j--;
                }
                pool[j + 1] = x;
            }
            for (var i = 0; i < pool.Count && list.Count < Math.Max(2, s.MaxRoot); i++)
                list.Add(pool[i]);
            return list;
        }

        // 1 = attack on a living enemy, 2 = HP heal on an ally, 0 = other.
        internal static int ActionClass(TurnDecider.ScoredAction c)
        {
            if (c == null || c.IsItem)
                return 0;
            if (c.Kind == SkillKind.Attack && c.EnemyTarget && c.Target != null && c.Target.Living && !c.Target.Corpse)
                return 1;
            if (c.Kind == SkillKind.Heal && !c.EnemyTarget && TurnDecider.RestoresHp(c.Preview))
                return 2;
            return 0;
        }

        // ---------- rollout ----------

        private static float Rollout(Ctx ctx, SimBoard b, bool branched, Recorder rec)
        {
            while (true)
            {
                if (ctx.Clock.Elapsed.TotalMilliseconds > ctx.S.HardCapMs)
                {
                    ctx.TimedOut = true;
                    return Eval(ctx, b);
                }
                var save = b.QueuePos;
                var idx = ForwardModel.NextActor(b);
                if (idx < 0)
                    break;
                var a = b.Actors[idx];
                if (a.Hero && !branched && ctx.S.AllyBranch > 1)
                {
                    // Branch the first ally turn over its top-K model actions.
                    b.QueuePos = save;
                    return Branch(ctx, b, rec);
                }
                ctx.Nodes++;
                var hpBefore = a.Hp;
                var aliveBefore = a.AliveP;
                var stunBefore = a.StunP;
                var act = ForwardModel.BeginTurn(b, idx);
                if (rec != null && (Math.Abs(a.Hp - hpBefore) > 0.05f || aliveBefore - a.AliveP > 0.01f))
                    rec.Step(a, "(turn start dot/regen)", a, "");
                if (rec != null && stunBefore > 0.01f)
                    rec.Step(a, "(stunned p" + stunBefore.ToString("0.00") + ")", a, "");
                if (act <= 0f)
                    continue;
                if (!a.Hero)
                {
                    ForwardModel.EnemyTurn(b, ctx.Data, idx, act, rec);
                    continue;
                }
                HeroOption best;
                if (BestHeroOptions(ctx, b, idx, 1, out best, null) > 0)
                    ForwardModel.ApplyModelSkill(b, ctx.Data, a, best.Skill, best.Def, b.Actors[best.Target], act * ctx.S.AllyTrust, rec);
                else if (rec != null)
                    rec.Step(a, "(no modelled action)", null, "");
            }
            return Eval(ctx, b);
        }

        private struct HeroOption
        {
            public GameData.SkillData Skill;
            public SkillSnap Def;
            public int Target;
            public float Value;
        }

        [ThreadStatic]
        private static HeroOption[] _opts;

        // The first ally turn of the rollout: try its top-K model actions,
        // keep the best (allies cooperate). Without a recorder the best
        // child board is copied back so the caller sees the line's end
        // state; with one, the chosen option is replayed and recorded.
        private static float Branch(Ctx ctx, SimBoard b, Recorder rec)
        {
            var idx = ForwardModel.NextActor(b);
            var a = b.Actors[idx];
            ctx.Nodes++;
            var act = ForwardModel.BeginTurn(b, idx);
            if (act <= 0f)
                return Rollout(ctx, b, true, rec);
            var opts = _opts ?? (_opts = new HeroOption[8]);
            HeroOption first;
            var n = BestHeroOptions(ctx, b, idx, Math.Min(ctx.S.AllyBranch, opts.Length), out first, opts);
            if (n == 0)
            {
                if (rec != null)
                    rec.Step(a, "(no modelled action)", null, "");
                return Rollout(ctx, b, true, rec);
            }
            // Copy the options: the recursive rollout reuses _opts.
            var local = new HeroOption[n];
            Array.Copy(opts, local, n);
            var mark = ctx.PoolUsed;
            var bestValue = float.NegativeInfinity;
            var bestIdx = 0;
            SimBoard bestBoard = null;
            for (var i = 0; i < n; i++)
            {
                var child = ctx.Rent();
                child.CopyFrom(b);
                var o = local[i];
                ForwardModel.ApplyModelSkill(child, ctx.Data, child.Actors[idx], o.Skill, o.Def, child.Actors[o.Target], act * ctx.S.AllyTrust);
                var v = Rollout(ctx, child, true, null);
                if (v > bestValue)
                {
                    bestValue = v;
                    bestIdx = i;
                    bestBoard = child;
                }
                if (ctx.TimedOut)
                    break;
            }
            if (rec == null)
            {
                if (bestBoard != null)
                    b.CopyFrom(bestBoard);
                ctx.PoolUsed = mark;
                return bestValue;
            }
            ctx.PoolUsed = mark;
            var best = local[bestIdx];
            rec.Note("(ally branch: best of " + n + ")");
            ForwardModel.ApplyModelSkill(b, ctx.Data, a, best.Skill, best.Def, b.Actors[best.Target], act * ctx.S.AllyTrust, rec);
            return Rollout(ctx, b, true, rec);
        }
        // Top-k model actions for hero idx by one-step value; returns how many.
        private static int BestHeroOptions(Ctx ctx, SimBoard b, int idx, int k, out HeroOption best, HeroOption[] top)
        {
            best = default(HeroOption);
            var a = b.Actors[idx];
            var count = 0;
            var haveBest = false;
            if (a.Skills == null)
                return 0;
            for (var si = 0; si < a.Skills.Count; si++)
            {
                var id = a.Skills[si];
                if (string.IsNullOrEmpty(id) || id.StartsWith("pass_", StringComparison.OrdinalIgnoreCase)
                    || id.IndexOf("_move", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                var skill = ctx.Data.Skill(id);
                if (skill == null || !skill.HasRanks || !HeroCanEvaluate(skill))
                    continue;
                var def = ctx.Snap.Skill(id);
                if (def != null && (def.IsMove || def.IsItemDef))
                    continue;
                if (!ForwardModel.Launches(b, a, skill, def))
                    continue;
                for (var ti = 0; ti < b.Count; ti++)
                {
                    var t = b.Actors[ti];
                    float v;
                    if (skill.Friendly)
                    {
                        if (!t.Hero || t.Dead || t.Corpse || t.Rank < 0)
                            continue;
                        if (t == a && !skill.SelfTargetValid && !SelfOnly(skill))
                            continue;
                        if (t != a && SelfOnly(skill))
                            continue;
                        if (t != a && !ForwardModel.Reaches(b, a, skill, def, t))
                            continue;
                        v = HealValue(ctx, skill, t);
                    }
                    else
                    {
                        if (t.Hero || !t.Up || t.Healthless || (t.Stealth && !skill.MultiHit))
                            continue;
                        if (!ForwardModel.Reaches(b, a, skill, def, t))
                            continue;
                        v = AttackValue(ctx, b, a, skill, def, t);
                    }
                    if (v <= 0.01f)
                        continue;
                    var opt = new HeroOption { Skill = skill, Def = def, Target = ti, Value = v };
                    if (!haveBest || v > best.Value)
                    {
                        best = opt;
                        haveBest = true;
                    }
                    if (top != null)
                        Insert(top, ref count, k, opt);
                }
            }
            return top != null ? count : (haveBest ? 1 : 0);
        }

        private static void Insert(HeroOption[] top, ref int count, int k, HeroOption opt)
        {
            var pos = count;
            while (pos > 0 && top[pos - 1].Value < opt.Value)
                pos--;
            if (pos >= k)
                return;
            var end = Math.Min(count, k - 1);
            for (var i = end; i > pos; i--)
                top[i] = top[i - 1];
            top[pos] = opt;
            if (count < k)
                count++;
        }

        // Skills whose use conditions the model cannot evaluate are skipped
        // (conservative: an ally that needs Combo / tokens may do less).
        private static bool HeroCanEvaluate(GameData.SkillData skill)
        {
            for (var i = 0; i < skill.Conditions.Count; i++)
            {
                var c = skill.Conditions[i];
                if (c.IndexOf("hidden", StringComparison.OrdinalIgnoreCase) >= 0 && skill.Friendly)
                    continue;
                if (c.StartsWith("performer_", StringComparison.OrdinalIgnoreCase)
                    || c.StartsWith("target_", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private static bool SelfOnly(GameData.SkillData skill)
        {
            return skill.TargetMask == 0;
        }

        private static float HealValue(Ctx ctx, GameData.SkillData skill, SimActor t)
        {
            var amount = 0f;
            for (var i = 0; i < skill.TargetEffects.Count; i++)
            {
                var e = ctx.Data.Effect(skill.TargetEffects[i]);
                if (e == null)
                    continue;
                amount += e.HealPct * t.HpMax + e.HealAmount;
            }
            if (amount <= 0f || t.HpMax <= 0f)
                return 0f;
            // Heal skills carry hidden HP-threshold conditions; model them as
            // "only at or below half HP". Flagellant / Pain heroes are not
            // healed above the kit floor (cited-classes Pain loop).
            var hpPct = (1f - t.DoorP) * t.Hp / t.HpMax;
            if (t.DoorP < 0.5f && hpPct > 0.5f)
                return 0f;
            if (t.DoorP < 0.5f && KitSafety.IsFlagellant(t.ClassId) && hpPct > 0.25f)
                return 0f;
            var gain = Math.Min(amount, Math.Max(0f, t.HpMax - t.Hp));
            var low = hpPct < 0.35f;
            return gain * (low ? ctx.S.LowHpWeight : 0.5f) + t.DoorP * ctx.S.DoorPenalty * 0.5f;
        }

        private static float AttackValue(Ctx ctx, SimBoard b, SimActor p, GameData.SkillData skill, SkillSnap def, SimActor target)
        {
            if (!skill.Damages)
                return 0f;
            var total = 0f;
            for (var i = 0; i < b.Count; i++)
            {
                var u = b.Actors[i];
                if (u != target && !(skill.MultiHit && !u.Hero && u.Up && ForwardModel.Reaches(b, p, skill, def, u)))
                    continue;
                if (u.Hero || !u.Up || u.Healthless)
                    continue;
                var mult = ForwardModel.OffenseMult(p) * ForwardModel.DefenseMult(u);
                var dmg = ForwardModel.ModelDamage(p, skill, u);
                var q = ForwardModel.HitChance(p, u);
                float killP;
                if (ForwardModel.OnDoor(u))
                    killP = u.Armor > 0 ? 0f : q * (1f - ForwardModel.Clamp01(u.DeathResist));
                else
                    killP = u.Armor > 0 ? 0f : q * ForwardModel.LethalP(u.Hp, u.HpVar, skill.DmgMin * mult, skill.DmgMax * mult);
                var fw = FocusWeight(u);
                total += fw * ((q - killP) * Math.Min(dmg, u.Hp)
                               + killP * (u.Hp + ctx.S.KillBonus + ctx.S.ThreatRounds * ctx.Threat[i]));
            }
            return total;
        }

        // ---------- evaluation ----------

        internal static float FocusWeight(SimActor e)
        {
            if (e.Deferred)
                return 0.4f;
            var w = 1f + Math.Max(0f, e.Focus) / 100f;
            return w > 3f ? 3f : w;
        }

        private static float Eval(Ctx ctx, SimBoard b)
        {
            var s = ctx.S;
            var v = 0f;
            for (var i = 0; i < b.Count; i++)
            {
                var a = b.Actors[i];
                var root = ctx.Root.Actors[i];
                if (a.Hero)
                {
                    // Hp is the HP of the branch not on Death's Door.
                    var hp = (1f - a.DoorP) * a.Hp;
                    var lost = a.HpMax - hp;
                    var low = a.HpMax > 0f && hp / a.HpMax < 0.35f;
                    v -= lost * (low ? s.LowHpWeight : 1f) + a.DoorP * s.DoorPenalty + a.DeathP * s.DeathPenalty;
                    continue;
                }
                if (a.Healthless || (root.Corpse && root.AliveP >= 0.5f) || root.Dead)
                    continue;
                var fw = FocusWeight(a);
                var killed = 1f - a.AliveP;
                v += fw * (killed * (a.HpMax + s.KillBonus + s.ThreatRounds * ctx.Threat[i])
                           + a.AliveP * (a.HpMax - a.Hp));
            }
            return v;
        }

        // Expected HP damage per turn this enemy deals at the root board
        // (uniform over usable damaging skills; MultiHit counts every target).
        internal static float EnemyThreat(SimBoard b, GameData data, int idx)
        {
            var e = b.Actors[idx];
            if (!e.Up || e.Skills == null)
                return 0f;
            var sum = 0f;
            var usable = 0;
            for (var i = 0; i < e.Skills.Count; i++)
            {
                var skill = data.Skill(e.Skills[i]);
                if (!ForwardModel.EnemyCanUse(b, e, skill))
                    continue;
                usable++;
                if (!skill.Damages)
                    continue;
                var targets = 0;
                for (var t = 0; t < b.Count; t++)
                {
                    var h = b.Actors[t];
                    if (h.Hero && h.Up && ForwardModel.Reaches(b, e, skill, null, h))
                        targets++;
                }
                sum += skill.DmgMean * ForwardModel.OffenseMult(e) * (skill.MultiHit ? Math.Max(1, targets) : 1);
            }
            var cls = data.Class(e.ClassId);
            var turns = cls != null ? Math.Max(1, cls.Turns) : 1;
            return usable > 0 ? sum / usable * turns : 0f;
        }

        private static SearchOutcome Outcome(SimBoard root, SimBoard end)
        {
            var o = new SearchOutcome();
            for (var i = 0; i < end.Count; i++)
            {
                var a = end.Actors[i];
                var r = root.Actors[i];
                if (a.Hero)
                {
                    o.HeroHpLost += Math.Max(0f, (1f - r.DoorP) * r.Hp - (1f - a.DoorP) * a.Hp);
                    o.HeroDoor += Math.Max(0f, a.DoorP - r.DoorP);
                    o.HeroDeath += Math.Max(0f, a.DeathP - r.DeathP);
                    continue;
                }
                if (r.Dead || r.Corpse || a.Healthless)
                    continue;
                o.EnemyKills += r.AliveP - a.AliveP;
                o.EnemyHpLost += a.AliveP * Math.Max(0f, r.Hp - a.Hp) + (1f - a.AliveP) * r.Hp;
            }
            return o;
        }

        private static List<string> Line(Ctx ctx, int performer, TurnDecider.ScoredAction c)
        {
            var rec = new Recorder();
            ctx.PoolUsed = 1;
            var board = ctx.Rent();
            board.CopyFrom(ctx.Root);
            var save = ctx.S.HardCapMs;
            ForwardModel.ApplyPreview(board, ctx.Data, performer, c.SkillId, c.TargetGuid, c.Preview, ctx.Snap.Skill(c.SkillId), rec);
            // Same rollout as the value, recorded and uncapped.
            var s = ctx.S;
            ctx.S = s.Clone();
            ctx.S.HardCapMs = float.MaxValue;
            Rollout(ctx, board, false, rec);
            ctx.S = s;
            ctx.S.HardCapMs = save;
            rec.Note("end: " + Outcome(ctx.Root, board));
            rec.Note("board: " + board);
            return rec.Lines;
        }
    }
}
