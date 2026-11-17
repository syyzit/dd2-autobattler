using System;
using System.Collections.Generic;
using System.IO;
using Dd2Autobattler.Combat;
using Dd2Autobattler.Search;
using Newtonsoft.Json.Linq;

namespace Dd2Autobattler.Replay
{
    /// <summary>
    /// Accuracy evidence for the forward model. For each logged hero turn N
    /// with the next hero turn N+1 in the same fight, predict board N+1 from
    /// board N + the logged pick + the actors that acted in between, and
    /// compare field by field. Needs "action_result" records (who acted, with
    /// which skill, on whom, for how much); schema-2 "snap" adds the game's
    /// turn order. Also scores the CSV damage model against every logged
    /// hero preview (the ally model in search).
    /// </summary>
    internal sealed class ModelValidation
    {
        internal sealed class Counter
        {
            public int N;
            public int Hit;
            public double Sum;
            public double SumAbs;
            public double SumPred;
            public double SumActual;

            public void Add(double pred, double actual)
            {
                N++;
                SumPred += pred;
                SumActual += actual;
                SumAbs += Math.Abs(pred - actual);
                Sum += pred - actual;
            }

            public void Count(bool ok)
            {
                N++;
                if (ok)
                    Hit++;
            }

            public JObject ToJson(bool rate)
            {
                if (rate)
                    return new JObject { ["n"] = N, ["ok"] = Hit, ["rate"] = N > 0 ? Math.Round((double)Hit / N, 4) : (double?)null };
                return new JObject
                {
                    ["n"] = N,
                    ["mae"] = N > 0 ? Math.Round(SumAbs / N, 3) : (double?)null,
                    ["bias"] = N > 0 ? Math.Round(Sum / N, 3) : (double?)null,
                    ["mean_pred"] = N > 0 ? Math.Round(SumPred / N, 3) : (double?)null,
                    ["mean_actual"] = N > 0 ? Math.Round(SumActual / N, 3) : (double?)null
                };
            }

            public string Text(bool rate)
            {
                if (N == 0)
                    return "n=0";
                if (rate)
                    return Hit + "/" + N + " (" + (100.0 * Hit / N).ToString("0.0") + "%)";
                return "n=" + N + " MAE " + (SumAbs / N).ToString("0.00") + " bias " + (Sum / N).ToString("+0.00;-0.00")
                       + " (pred " + (SumPred / N).ToString("0.00") + " vs actual " + (SumActual / N).ToString("0.00") + ")";
            }
        }

        public GameData Data;
        public int Files;
        public int Fights;
        public int Pairs;
        public int PairsWithOrder;
        public readonly SortedDictionary<string, Counter> Rates = new SortedDictionary<string, Counter>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, Counter> Errors = new SortedDictionary<string, Counter>(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Unmodeled = new Dictionary<string, int>();

        private Counter R(string k)
        {
            Counter c;
            if (!Rates.TryGetValue(k, out c))
                Rates[k] = c = new Counter();
            return c;
        }

        private Counter E(string k)
        {
            Counter c;
            if (!Errors.TryGetValue(k, out c))
                Errors[k] = c = new Counter();
            return c;
        }

        private sealed class Rec
        {
            public JObject Turn;
            public readonly List<JObject> After = new List<JObject>();
        }

        public void RunFile(string path)
        {
            Files++;
            var fights = new List<List<Rec>>();
            List<Rec> fight = null;
            Rec cur = null;
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                JObject o;
                try { o = JObject.Parse(line); }
                catch { continue; }
                var type = o.Value<string>("type");
                if (type == "fight_start")
                {
                    fight = new List<Rec>();
                    fights.Add(fight);
                    cur = null;
                    continue;
                }
                if (type == "fight_end")
                {
                    fight = null;
                    cur = null;
                    continue;
                }
                if (fight == null)
                    continue;
                if (type == "turn" && o["note"] == null)
                {
                    cur = new Rec { Turn = o };
                    fight.Add(cur);
                }
                else if (type == "action_result" && cur != null)
                {
                    cur.After.Add(o);
                }
            }
            foreach (var f in fights)
            {
                if (f.Count == 0)
                    continue;
                Fights++;
                RunFight(f);
            }
        }

        private void RunFight(List<Rec> fight)
        {
            var memory = new MemoryState();
            memory.NoteRound(1);
            for (var i = 0; i < fight.Count; i++)
            {
                var rec = fight[i];
                var snap = Snap(rec.Turn, memory);
                if (snap == null)
                    continue;
                ScoreAllyModel(snap);
                if (rec.After.Count == 0)
                    continue;
                ScoreOwnAction(snap, rec);
                ScoreEnemyActions(snap, rec);
                if (i + 1 < fight.Count)
                {
                    var next = Snap(fight[i + 1].Turn, memory);
                    if (next != null)
                        ScoreBoard(snap, rec, next);
                }
            }
        }

        private TurnSnapshot Snap(JObject turn, MemoryState memory)
        {
            var snap = turn["snap"] is JObject s
                ? SnapshotJson.Read(s)
                : LegacyTurn.Build(turn, memory, null);
            // Records inside a fight carry the fight_start's id as "fight".
            ReplayEngine.FillFightId(snap, turn.Value<string>("fight"), null);
            return snap;
        }

        // ---------- A. own action: preview vs action_result ----------

        private void ScoreOwnAction(TurnSnapshot snap, Rec rec)
        {
            var chosen = rec.Turn["chosen"] as JObject;
            var own = OwnResult(rec);
            if (chosen == null || own == null)
                return;
            var skill = chosen.Value<string>("skill");
            var target = chosen.Value<uint?>("target") ?? 0u;
            LegalSnap legal = null;
            foreach (var l in snap.Legal)
            {
                if (l != null && l.Target == target && string.Equals(l.Skill, skill, StringComparison.OrdinalIgnoreCase))
                {
                    legal = l;
                    break;
                }
            }
            if (legal == null || legal.Preview == null || !legal.Preview.Ok)
                return;
            var p = legal.Preview.Clone();
            var t = snap.Find(target);
            TurnDecider.NoteKillFromHp(p, t, snap);
            var modelBoard = ForwardModel.Build(snap, Data, null);
            var pi = modelBoard.IndexOf(snap.PerformerGuid);
            if (pi >= 0)
                ForwardModel.ApplyPreview(modelBoard, Data, pi, skill, target, legal.Preview, snap.Skill(skill));
            foreach (var tr in Arr(own["targets"]))
            {
                var to = tr as JObject;
                if (to == null || to.Value<string>("result") != "TARGET")
                    continue;
                var guid = (to["target"] as JObject)?.Value<uint?>("guid") ?? 0u;
                var victim = snap.Find(guid);
                if (victim == null || !snap.IsEnemy(guid))
                    continue;
                float pred;
                if (!TurnDecider.TryHitDamageOn(p, guid, out pred))
                    pred = guid == target ? p.Damage : (p.HitGuids.Contains(guid) ? p.Damage : 0f);
                var hit = to.Value<bool?>("hit") ?? false;
                var dmg = to.Value<float?>("dmg") ?? 0f;
                var kill = to.Value<bool?>("kill") ?? false;
                R("own.hit_vs_hitchance>=0.9").Count(hit || p.HitChance < 0.9f);
                if (pred > 0f && hit)
                {
                    E("own.damage_on_hit").Add(pred, dmg);
                    var lo = p.DamageLow > 0f ? p.DamageLow : pred;
                    var hi = p.DamageHigh > 0f ? p.DamageHigh : pred;
                    R("own.damage_in_preview_range_or_crit").Count(dmg + 0.5f >= lo && (dmg <= hi + 0.5f || (to.Value<bool?>("crit") ?? false)));
                }
                if (guid == target || p.Hits.Count > 0)
                {
                    // The decider's kill flag (NoteKillFromHp) ...
                    var predKill = guid == target ? p.Kills : HitKillsGuid(p, guid);
                    R("decider.kill_flag_correct").Count(predKill == kill);
                    if (predKill)
                        R("decider.kill_flag_when_set_killed").Count(kill);
                    // ... and the forward model (damage range, Death's Door armor).
                    var m = modelBoard.Find(guid);
                    if (m != null)
                    {
                        var modelKill = !m.Up && !(m.Corpse && !kill);
                        var pk = 1f - m.AliveP;
                        R("own.model_kill_correct").Count(modelKill == kill);
                        if (modelKill)
                            R("own.model_kill_when_predicted").Count(kill);
                        if (kill)
                            R("own.model_kill_was_predicted").Count(modelKill);
                        E("own.model_kill_probability_brier").Add(pk, kill ? 1f : 0f);
                        if (Verbose && modelKill != kill)
                            Console.WriteLine("  [own kill] " + Key(rec) + " " + skill + "->" + guid + " hp " + (victim.Hp) + " armor "
                                              + victim.CountToken("deaths_door_armor") + " dd " + victim.DeathsDoor + " prev " + pred.ToString("0.0")
                                              + " [" + p.DamageLow + "-" + p.DamageHigh + "] actual " + dmg + " kill " + kill + " model aliveP " + m.AliveP.ToString("0.00"));
                    }
                }
            }
        }

        public bool Verbose;

        private static string Key(Rec rec)
        {
            return rec.Turn.Value<string>("fight") + "/t" + rec.Turn.Value<int?>("turn_index");
        }

        private static bool HitKillsGuid(PreviewScore p, uint guid)
        {
            foreach (var h in p.Hits)
            {
                if (h != null && h.Guid == guid)
                    return h.Kills;
            }
            return false;
        }

        private static JObject OwnResult(Rec rec)
        {
            var actor = (rec.Turn["actor"] as JObject)?.Value<uint?>("guid") ?? 0u;
            var skill = (rec.Turn["chosen"] as JObject)?.Value<string>("skill");
            foreach (var a in rec.After)
            {
                var perf = (a["performer"] as JObject)?.Value<uint?>("guid") ?? 0u;
                if (perf == actor && string.Equals(a.Value<string>("skill"), skill, StringComparison.OrdinalIgnoreCase))
                    return a;
            }
            return null;
        }

        // ---------- B / C. enemy actions: CSV mechanics and choice model ----------

        private void ScoreEnemyActions(TurnSnapshot snap, Rec rec)
        {
            var focus = EnemyFocus.Scan(snap);
            var board = ForwardModel.Build(snap, Data, focus);
            var own = OwnResult(rec);
            foreach (var a in rec.After)
            {
                if (a == own)
                    continue;
                var perf = a["performer"] as JObject;
                if (perf == null || (perf.Value<int?>("team") ?? 0) != 1)
                    continue;
                var guid = perf.Value<uint?>("guid") ?? 0u;
                var idx = board.IndexOf(guid);
                var skillId = a.Value<string>("skill");
                var skill = Data.Skill(skillId);
                R("enemy.skill_in_csv").Count(skill != null && skill.HasRanks);
                if (idx < 0)
                    continue;
                var actor = board.Actors[idx];
                R("enemy.skill_in_kit").Count(actor.Skills != null && actor.Skills.Contains(skillId));
                uint firstTarget = 0;
                foreach (var tr in Arr(a["targets"]))
                {
                    var to = tr as JObject;
                    if (to == null || to.Value<string>("result") != "TARGET")
                        continue;
                    var tg = (to["target"] as JObject)?.Value<uint?>("guid") ?? 0u;
                    if (firstTarget == 0)
                        firstTarget = tg;
                    var victim = board.Find(tg);
                    if (victim == null || skill == null || !victim.Hero)
                        continue;
                    var model = ForwardModel.ModelDamage(actor, skill, victim);
                    var hit = to.Value<bool?>("hit") ?? false;
                    var dmg = to.Value<float?>("dmg") ?? 0f;
                    var hitP = ForwardModel.HitChance(actor, victim);
                    R("enemy.hit_when_model_hit>=0.9").Count(hit || hitP < 0.9f);
                    if (hit && skill.Damages)
                    {
                        E("enemy.damage_on_hit (oracle skill+target)").Add(model, dmg);
                        var mult = ForwardModel.OffenseMult(actor) * ForwardModel.DefenseMult(victim);
                        var crit = to.Value<bool?>("crit") ?? false;
                        var hi = skill.DmgMax * mult * (crit ? 1.5f : 1f) + 0.6f;
                        R("enemy.damage_in_csv_range").Count(dmg >= Math.Floor(skill.DmgMin * mult * 0.999f) - 0.6f && dmg <= hi);
                    }
                }
                float pSkill, pTarget;
                bool modelled;
                ForwardModel.EnemyChoiceProb(board, Data, idx, skillId, firstTarget, out pSkill, out pTarget, out modelled);
                R("enemy.choice_modelled").Count(modelled);
                if (modelled)
                {
                    R("enemy.actual_skill_in_model_set").Count(pSkill > 0f);
                    if (pSkill > 0f && firstTarget != 0)
                        R("enemy.actual_target_in_model_set").Count(pTarget > 0f);
                    if (pSkill > 0f)
                        E("enemy.p_actual_skill").Add(pSkill, 0);
                    if (pSkill > 0f && pTarget > 0f)
                        E("enemy.p_actual_target").Add(pTarget, 0);
                }
            }
        }

        // ---------- D. board N+1 ----------

        private string _key;

        private void ScoreBoard(TurnSnapshot snap, Rec rec, TurnSnapshot next)
        {
            Pairs++;
            _key = Key(rec);
            var focus = EnemyFocus.Scan(snap);
            var result = TurnDecider.DecideSnapshot(snap);
            var chosen = rec.Turn["chosen"] as JObject;
            if (chosen == null)
                return;
            var picked = ReplayEngine.FindCandidate(result, chosen.Value<string>("skill"), chosen.Value<uint?>("target") ?? 0u);
            if (picked == null)
                return;
            var nextActor = next.PerformerGuid;
            var own = OwnResult(rec);

            // Actors who acted in between (from action_result), excluding the own action.
            var actedSeq = new List<KeyValuePair<uint, JObject>>();
            foreach (var a in rec.After)
            {
                if (a == own)
                    continue;
                var g = (a["performer"] as JObject)?.Value<uint?>("guid") ?? 0u;
                if (g != 0)
                    actedSeq.Add(new KeyValuePair<uint, JObject>(g, a));
            }

            var baseBoard = ForwardModel.Build(snap, Data, focus);
            var performer = baseBoard.IndexOf(snap.PerformerGuid);
            if (performer < 0)
                return;

            // Baseline: nothing changes.
            CompareBoard("baseline_nochange", baseBoard, next, snap);

            // Own action only (preview), then a no-op for everyone else.
            var ownOnly = new SimBoard();
            ownOnly.CopyFrom(baseBoard);
            ForwardModel.ApplyPreview(ownOnly, Data, performer, picked.SkillId, picked.TargetGuid, picked.Preview, snap.Skill(picked.SkillId));

            // D1: oracle actors and skills/targets, model numbers.
            var oracle = new SimBoard();
            oracle.CopyFrom(ownOnly);
            foreach (var kv in actedSeq)
            {
                var idx = oracle.IndexOf(kv.Key);
                if (idx < 0)
                    continue;
                var act = ForwardModel.BeginTurn(oracle, idx);
                var skill = Data.Skill(kv.Value.Value<string>("skill"));
                var tg = FirstTarget(kv.Value);
                var t = tg != 0 ? oracle.Find(tg) : null;
                if (skill == null || t == null)
                    continue;
                ForwardModel.ApplyModelSkill(oracle, Data, oracle.Actors[idx], skill, null, t, act > 0f ? 1f : 0f);
                if (Verbose && _key == Environment.GetEnvironmentVariable("DD2_DEBUG_KEY"))
                    Console.WriteLine("    after " + skill.Id + ": " + oracle);
            }
            if (Verbose && _key == Environment.GetEnvironmentVariable("DD2_DEBUG_KEY"))
                Console.WriteLine("    base: " + baseBoard + "\n    own: " + ownOnly);
            TickNext(oracle, nextActor, snap.PerformerGuid);
            CompareBoard("oracle_actions", oracle, next, snap);

            // D2: expected-value enemy model over the actual acting sequence.
            var ev = new SimBoard();
            ev.CopyFrom(ownOnly);
            foreach (var kv in actedSeq)
            {
                var idx = ev.IndexOf(kv.Key);
                if (idx < 0)
                    continue;
                var act = ForwardModel.BeginTurn(ev, idx);
                if (ev.Actors[idx].Hero)
                {
                    var skill = Data.Skill(kv.Value.Value<string>("skill"));
                    var t = ev.Find(FirstTarget(kv.Value));
                    if (skill != null && t != null)
                        ForwardModel.ApplyModelSkill(ev, Data, ev.Actors[idx], skill, null, t, act);
                }
                else
                {
                    ForwardModel.EnemyTurn(ev, Data, idx, act);
                }
            }
            TickNext(ev, nextActor, snap.PerformerGuid);
            CompareBoard("ev_model_actual_order", ev, next, snap);
            NoteUnmodeled(ev.Unmodeled);

            // D3 (schema 2): the game's turn order instead of the observed one.
            if (snap.RemainingTurns != null && snap.RemainingTurns.Count > 0)
            {
                PairsWithOrder++;
                var order = new List<uint>();
                var startedAt = snap.RemainingTurns[0] == snap.PerformerGuid ? 1 : 0;
                for (var i = startedAt; i < snap.RemainingTurns.Count; i++)
                {
                    if (snap.RemainingTurns[i] == nextActor)
                        break;
                    order.Add(snap.RemainingTurns[i]);
                }
                // Turn order agreement with what actually acted.
                var observed = new List<uint>();
                foreach (var kv in actedSeq)
                    observed.Add(kv.Key);
                var subseq = IsSubsequence(observed, order);
                R("order.observed_actors_within_game_order").Count(subseq);
                R("order.next_actor_in_remaining_or_new_round").Count(snap.RemainingTurns.Contains(nextActor) || next.Round != snap.Round);
                var full = new SimBoard();
                full.CopyFrom(ownOnly);
                foreach (var g in order)
                {
                    var idx = full.IndexOf(g);
                    if (idx < 0 || !full.Actors[idx].Up)
                        continue;
                    var act = ForwardModel.BeginTurn(full, idx);
                    if (!full.Actors[idx].Hero)
                        ForwardModel.EnemyTurn(full, Data, idx, act);
                }
                TickNext(full, nextActor, snap.PerformerGuid);
                CompareBoard("ev_model_game_order", full, next, snap);
            }
        }

        private static bool IsSubsequence(List<uint> sub, List<uint> seq)
        {
            var j = 0;
            for (var i = 0; i < seq.Count && j < sub.Count; i++)
            {
                if (seq[i] == sub[j])
                    j++;
            }
            return j == sub.Count;
        }

        private static void TickNext(SimBoard b, uint nextActor, uint performer)
        {
            if (nextActor == performer)
                return;
            var idx = b.IndexOf(nextActor);
            if (idx >= 0)
                ForwardModel.TickDot(b, b.Actors[idx]);
        }

        private static uint FirstTarget(JObject action)
        {
            foreach (var tr in Arr(action["targets"]))
            {
                var to = tr as JObject;
                if (to != null && to.Value<string>("result") == "TARGET")
                    return (to["target"] as JObject)?.Value<uint?>("guid") ?? 0u;
            }
            return 0;
        }

        private void CompareBoard(string mode, SimBoard model, TurnSnapshot actual, TurnSnapshot before)
        {
            foreach (var list in new[] { actual.Heroes, actual.Enemies })
            {
                if (list == null)
                    continue;
                foreach (var t in list)
                {
                    if (t == null || t.Guid == 0)
                        continue;
                    var side = list == actual.Heroes ? "hero" : "enemy";
                    var m = model.Find(t.Guid);
                    if (m == null)
                    {
                        R(mode + "." + side + ".actor_known").Count(false);
                        continue;
                    }
                    R(mode + "." + side + ".actor_known").Count(true);
                    var aliveActual = t.Living && !t.Corpse;
                    var aliveModel = m.Up;
                    R(mode + "." + side + ".alive").Count(aliveActual == aliveModel);
                    if (Verbose && mode == "oracle_actions" && aliveActual != aliveModel)
                        Console.WriteLine("  [alive] " + _key + " " + side + " " + t.Guid + " " + t.ClassId + " actual living " + t.Living
                                          + " corpse " + t.Corpse + " hp " + t.Hp + " | model " + m);
                    if (Verbose && mode == "oracle_actions" && aliveActual && aliveModel && m.Rank != t.Rank)
                        Console.WriteLine("  [rank] " + _key + " " + side + " " + t.Guid + " " + t.ClassId + " actual r" + t.Rank + " | model " + m);
                    var prev = before.Find(t.Guid);
                    var prevAlive = prev != null && prev.Living && !prev.Corpse;
                    if (prevAlive && !aliveActual)
                        R(mode + "." + side + ".death_predicted").Count(!aliveModel);
                    if (prevAlive && !aliveModel)
                        R(mode + "." + side + ".predicted_death_happened").Count(!aliveActual);
                    if (side == "enemy")
                        R(mode + ".enemy.corpse").Count(t.Corpse == (m.Corpse && m.Rank >= 0));
                    if (aliveActual && aliveModel)
                    {
                        E(mode + "." + side + ".hp").Add(m.Hp, t.Hp);
                        R(mode + "." + side + ".rank").Count(m.Rank == t.Rank);
                        R(mode + "." + side + ".stun").Count((m.StunP >= 0.5f) == t.Stun);
                        if (side == "hero")
                            R(mode + ".hero.deaths_door").Count((m.DoorP >= 0.5f) == t.DeathsDoor);
                        if (prev != null && Math.Abs(prev.Hp - t.Hp) > 0.5f)
                            E(mode + "." + side + ".hp_changed_only").Add(m.Hp, t.Hp);
                    }
                }
            }
        }

        private void NoteUnmodeled(int flags)
        {
            if ((flags & ForwardModel.UnmodeledSummon) != 0) Bump("summon");
            if ((flags & ForwardModel.UnmodeledClassChange) != 0) Bump("class_change");
            if ((flags & ForwardModel.UnmodeledExtraTurn) != 0) Bump("extra_turn");
            if ((flags & ForwardModel.UnmodeledUnknownSkill) != 0) Bump("enemy_no_usable_skill");
            if ((flags & ForwardModel.UnmodeledRiposte) != 0) Bump("riposte");
        }

        private void Bump(string k)
        {
            Unmodeled[k] = Unmodeled.TryGetValue(k, out var n) ? n + 1 : 1;
        }

        // ---------- E. CSV hero damage model vs the game's preview ----------

        private void ScoreAllyModel(TurnSnapshot snap)
        {
            var board = ForwardModel.Build(snap, Data, null);
            var p = board.Find(snap.PerformerGuid);
            if (p == null)
                return;
            foreach (var l in snap.Legal)
            {
                if (l == null || l.Preview == null || !l.Preview.Ok || l.Preview.Damage <= 0f)
                    continue;
                var t = board.Find(l.Target);
                if (t == null || t.Hero || t.Corpse)
                    continue;
                var skill = Data.Skill(l.Skill);
                R("ally.skill_in_csv").Count(skill != null && skill.Damages);
                if (skill == null || !skill.Damages)
                    continue;
                float pred;
                if (!TurnDecider.TryHitDamageOn(l.Preview, l.Target, out pred))
                    pred = l.Preview.Damage;
                var model = ForwardModel.ModelDamage(p, skill, t);
                E("ally.csv_damage_vs_preview").Add(model, pred);
                if (Verbose)
                    E("ally.by_skill." + l.Skill).Add(model, pred);
                R("ally.csv_within_25pct_of_preview").Count(Math.Abs(model - pred) <= 0.25f * Math.Max(1f, pred));
                R("ally.csv_within_2hp_of_preview").Count(Math.Abs(model - pred) <= 2f);
            }
        }

        private static IEnumerable<JToken> Arr(JToken t)
        {
            if (t is JArray a)
            {
                foreach (var x in a)
                    yield return x;
            }
        }

        public JObject ToJson()
        {
            var rates = new JObject();
            foreach (var kv in Rates)
                rates[kv.Key] = kv.Value.ToJson(true);
            var errors = new JObject();
            foreach (var kv in Errors)
                errors[kv.Key] = kv.Value.ToJson(false);
            var un = new JObject();
            foreach (var kv in Unmodeled)
                un[kv.Key] = kv.Value;
            return new JObject
            {
                ["files"] = Files,
                ["fights"] = Fights,
                ["pairs"] = Pairs,
                ["pairs_with_turn_order"] = PairsWithOrder,
                ["rates"] = rates,
                ["errors"] = errors,
                ["unmodeled"] = un
            };
        }

        public IEnumerable<string> TextLines()
        {
            yield return "Forward-model validation: " + Files + " file(s), " + Fights + " fight(s), " + Pairs + " turn pairs ("
                         + PairsWithOrder + " with game turn order)";
            foreach (var kv in Errors)
                yield return "  " + kv.Key.PadRight(52) + kv.Value.Text(false);
            foreach (var kv in Rates)
                yield return "  " + kv.Key.PadRight(52) + kv.Value.Text(true);
            if (Unmodeled.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kv in Unmodeled)
                    parts.Add(kv.Key + " x" + kv.Value);
                yield return "  unmodeled effects met: " + string.Join(", ", parts);
            }
        }
    }
}
