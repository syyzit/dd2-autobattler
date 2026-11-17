using System;
using System.Collections.Generic;
using ScoredAction = Dd2Autobattler.Combat.TurnDecider.ScoredAction;

namespace Dd2Autobattler.Combat
{
    /// <summary>
    /// Shackles of Denial (mountain_boss_brain*, notes/cited-bosses.md): which lock attack to take,
    /// free DoT items on a lock, and walking a hero whose rank launches no attack back onto one.
    /// Pure, snapshot only.
    /// </summary>
    internal static class DenialPlay
    {
        // ---------------- which attack (denial_ev) ----------------
        //
        // CSV: four locks x 55 HP, no self-heal (Health's death heal aside), no
        // cleanse, one action each per round; dots_data_export DoTs last 3 of the
        // lock's turns (skill_very_large_blight_dot 5 x 3, skill_medium_bleed_dot
        // 3 x 3) and stack (logs: a Health lock ticking 8 = 3 + 5). Every lock has
        // to die, so all damage counts; a kill takes that lock's attacks away
        // (logs, Nov losses: 32.1 incoming a round with 4 locks, 27.4 with 3,
        // 20.5 with 2). One damage point on the lock in place p of the kill order
        // (n living) brings the deaths of locks p..n forward, so it is worth
        // n - p: the head of the order 4, the last lock 1.
        // The default scorer pays the per-hero must-kill +120 and a deferred
        // lock -75, vetoes a hit on a deferred lock while the must-kill is
        // reachable, and pays a DoT 1.5 ticks (docked when stacking). Replaying
        // logged Denial fights: the same 211 lock attacks
        // are worth 1600 expected damage (direct + DoT x 3 ticks) with that code
        // and 1659 with this pass (Duelist's Advance -> Wicked Slice, Incision ->
        // Noxious Blast, Hand of Light -> Judgement, Thrown <-> Flashing).
        // Ticks of a skill DoT on a lock when the preview does not say (CSV 3).
        // Per-hit previews use PreviewScore.Turns() and each lock's own resist.
        internal const float DotTicks = 3f;
        internal const float EvMargin = 3f;
        internal const float KillBonus = 6f;
        // CSV health_lock_death_heal: m_HealthHealPercent 0.33, conditions
        // brain_health_lock_is_dead + performer_has_0_count. The buff manager
        // that runs it sits on the three other locks (their spawn_effects);
        // count_plus_1 lands on those holders, never on Health, and the
        // heal fires on EVERY Health death, not only the first lock death.
        // In the logs every Health death that left a wounded lock behind healed
        // it (Health 2nd: brain_2 r11 Ranged 8 -> 26; Health 3rd: brain_2 r12
        // Stress 38 -> 55, brain_3 / brain_4 / brain_5 Stress 50/51 -> 55,
        // brain_5 r17 45 -> 55).
        internal const float HealthHealPct = 0.33f;
        // While a lock the order kills before Health still lives, damage that
        // would take Health below this floor (pending DoT included) is worth
        // nothing here (a splash or DoT would finish it early).
        internal const float HealthFloor = 15f;
        // A free DoT item worth at least this much lock HP (DoT x 3 ticks,
        // clamped to the bar) is used on its own.
        internal const float MinItemDot = 5f;
        // A hard veto (kit safety, a waste of a DoT on an immune target) leaves
        // the click this far below 0 once the focus pay is taken out.
        internal const float VetoFloor = -40f;

        private static readonly HashSet<string> EvGates = new HashSet<string>(StringComparer.Ordinal)
        {
            "attack", "attack_over_idle", "open_add"
        };

        // KitSafety reasons that TurnDecider.ApplyKitSafety copies into
        // NoteReason when the delta is 12 or more. They are score terms, not a
        // cited pick: DotHostDelta pays a DoT's ticks (denial_ev values the same
        // ticks per lock, with the kill-order weights and the heal band) and
        // BlindDelta docks every non-kill swing of a blinded hero alike.
        // In logged Denial fights every Plague Doctor round-1 pick with
        // Magnesium Rain legal was a dot_host Plague Grenade (weighted 44
        // against Rain's 66), so the rain the notes call the round-1 click was
        // never weighed.
        internal static readonly HashSet<string> ScoreOnlyNotes = new HashSet<string>(StringComparer.Ordinal)
        {
            "dot_host", "kit_blind"
        };

        internal static bool IsLock(TargetInfo t)
        {
            return t != null && t.Living && !t.Corpse && KitSafety.IdHas(t.ClassId, "boss_brain_lock");
        }

        internal static bool IsLockAttack(ScoredAction c)
        {
            return c != null && c.Kind == SkillKind.Attack && c.EnemyTarget && !c.IsItem && IsLock(c.Target)
                   && c.Preview != null && c.Preview.Ok;
        }

        // The living locks as the kill order sees them.
        internal sealed class LockPlan
        {
            // Weight of one damage point on each living lock: n - place in the kill order.
            public readonly Dictionary<uint, float> Weights = new Dictionary<uint, float>();
            // The living Health lock, 0 when it is dead.
            public uint Health;
            // Health while a lock the order kills before it still lives (HealthFloor).
            public uint HealthGuard;
            // Living locks the order kills after Health: Health's death heal
            // undoes the first 33% of the bar missing on them.
            public readonly HashSet<uint> HealedLater = new HashSet<uint>();
        }

        internal static LockPlan Plan(EnemyFocus focus, TurnSnapshot snap)
        {
            var plan = new LockPlan();
            if (focus == null)
                return plan;
            var locks = new List<EnemyThreat>();
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var e = focus.Enemies[i];
                if (e == null || !KitSafety.IdHas(e.ClassId, "boss_brain_lock"))
                    continue;
                var body = snap != null ? snap.Find(e.Guid) : null;
                if (body != null && (!body.Living || body.Corpse))
                    continue;
                locks.Add(e);
            }
            var order = EnemyFocus.DenialKillOrder(locks, snap);
            var healthAt = -1;
            for (var i = 0; i < order.Count; i++)
            {
                plan.Weights[order[i].Guid] = order.Count - i;
                if (KitSafety.IdHas(order[i].ClassId, "lock_health"))
                {
                    healthAt = i;
                    plan.Health = order[i].Guid;
                }
            }
            if (healthAt > 0)
                plan.HealthGuard = plan.Health;
            if (healthAt >= 0)
            {
                for (var i = healthAt + 1; i < order.Count; i++)
                    plan.HealedLater.Add(order[i].Guid);
            }
            return plan;
        }

        // Weight of one damage point on each living lock: n - place in the kill order.
        internal static Dictionary<uint, float> LockWeights(EnemyFocus focus, TurnSnapshot snap, out uint healthGuard)
        {
            var plan = Plan(focus, snap);
            healthGuard = plan.HealthGuard;
            return plan.Weights;
        }

        // DoT x land x ticks on the clicked target (the "does it DoT at all" test).
        internal static float DotPerTarget(PreviewScore p)
        {
            if (p == null)
                return 0f;
            return (p.ApplyBleed * p.Land("bleed") + p.ApplyBlight * p.Land("blight") + p.ApplyBurn * p.Land("burn")) * p.Turns();
        }

        // DoT x land x ticks on one lock: that lock's own DoT and resist when the
        // preview carries them per hit (Magnesium Rain burns all four locks,
        // CSV burn resist 0.1 / 0.1 / 0.5 / 0.2), else the click's (older logs).
        // Ticks: the DoT's duration (CSV m_DurationAmount, 3) - locks never
        // cleanse, and a lock with less HP left is clamped in LockHitValue.
        internal static float DotOnLock(PreviewScore p, uint guid)
        {
            if (p == null)
                return 0f;
            return p.DotLandedOn(guid) * p.Turns();
        }

        // HP Health's death heal gives a lock with `missing` HP off its bar.
        internal static float HealBack(TargetInfo lockBody, float missing)
        {
            if (lockBody == null || missing <= 0f)
                return 0f;
            var max = lockBody.HpMax > 0f ? lockBody.HpMax : Math.Max(lockBody.Hp, 55f);
            return Math.Min(missing, HealthHealPct * max);
        }

        // Weighted value of a lock attack: direct damage (clamped to the bar) plus
        // DoT x 3 ticks (clamped to what is left after the hit and the pending
        // tick), per lock hit, times that lock's weight; times hit chance.
        // Health's death heal: damage on a lock killed after Health counts only
        // past the 33% the heal gives back; a click that kills Health pays the
        // weighted HP the heal gives every other living lock (this click's own
        // hits included).
        internal static float Value(ScoredAction c, TurnSnapshot snap, LockPlan plan)
        {
            if (!IsLockAttack(c) || snap == null)
                return float.NegativeInfinity;
            var v = HitsValue(c.Preview, c.Target, snap, plan, false);
            var hit = c.Preview.HitChance > 0f ? Math.Min(1f, c.Preview.HitChance) : 0f;
            return v * hit;
        }

        // NegativeInfinity: the click hits no lock.
        private static float HitsValue(PreviewScore p, TargetInfo clicked, TurnSnapshot snap, LockPlan plan, bool unweighted)
        {
            var hits = new List<KeyValuePair<TargetInfo, float>>();
            var dots = new List<float>();
            if (p.Hits != null)
            {
                for (var i = 0; i < p.Hits.Count; i++)
                {
                    var h = p.Hits[i];
                    if (h == null)
                        continue;
                    var body = snap.Find(h.Guid);
                    if (IsLock(body))
                    {
                        hits.Add(new KeyValuePair<TargetInfo, float>(body, h.Damage));
                        dots.Add(DotOnLock(p, h.Guid));
                    }
                }
            }
            if (hits.Count == 0)
            {
                if (!IsLock(clicked))
                    return float.NegativeInfinity;
                hits.Add(new KeyValuePair<TargetInfo, float>(clicked, p.Damage));
                dots.Add(DotOnLock(p, clicked.Guid));
            }
            var killsHealth = false;
            for (var i = 0; i < hits.Count; i++)
            {
                var b = hits[i].Key;
                if (b.Guid == plan.Health && plan.Health != 0 && hits[i].Value > 0f && hits[i].Value + 0.05f >= Math.Max(0f, b.Hp))
                    killsHealth = true;
            }
            var total = 0f;
            for (var i = 0; i < hits.Count; i++)
                total += LockHitValue(hits[i].Key, hits[i].Value, dots[i], plan, killsHealth, unweighted);
            if (killsHealth)
                total -= HealthKillCost(snap, plan, hits, unweighted);
            return total;
        }

        private static float LockHitValue(TargetInfo lockBody, float damage, float dot, LockPlan plan, bool killsHealth, bool unweighted)
        {
            float w;
            if (unweighted || !plan.Weights.TryGetValue(lockBody.Guid, out w))
                w = 1f;
            var hp = Math.Max(0f, lockBody.Hp);
            var direct = Math.Max(0f, Math.Min(damage, hp));
            var left = Math.Max(0f, hp - direct - Math.Max(0f, lockBody.NextDot));
            var usable = direct + Math.Min(Math.Max(0f, dot), left);
            var kills = damage > 0f && damage + 0.05f >= hp;
            if (plan.HealthGuard != 0 && lockBody.Guid == plan.HealthGuard && !kills)
                usable = Math.Min(usable, Math.Max(0f, hp - HealthFloor - Math.Max(0f, lockBody.NextDot)));
            // Health still lives (this click does not kill it) and dies before
            // this lock: the heal gives back the first 33% missing.
            if (!kills && !killsHealth && plan.HealedLater.Contains(lockBody.Guid))
            {
                var max = lockBody.HpMax > 0f ? lockBody.HpMax : Math.Max(hp, 55f);
                var band = HealthHealPct * max;
                var before = Math.Max(0f, max - hp);
                usable = Math.Max(0f, before + usable - band) - Math.Max(0f, before - band);
            }
            return w * usable + (kills ? w * KillBonus : 0f);
        }

        // Weighted HP Health's death heal gives back to every other living lock,
        // after this click's direct hits on them.
        private static float HealthKillCost(TurnSnapshot snap, LockPlan plan, List<KeyValuePair<TargetInfo, float>> hits, bool unweighted)
        {
            var cost = 0f;
            for (var i = 0; i < snap.Enemies.Count; i++)
            {
                var e = snap.Enemies[i];
                if (!IsLock(e) || e.Guid == plan.Health)
                    continue;
                var hp = Math.Max(0f, e.Hp);
                for (var k = 0; k < hits.Count; k++)
                {
                    if (hits[k].Key.Guid == e.Guid)
                        hp = Math.Max(0f, hp - Math.Max(0f, hits[k].Value));
                }
                if (hp <= 0f)
                    continue;
                var max = e.HpMax > 0f ? e.HpMax : Math.Max(e.Hp, 55f);
                float w;
                if (unweighted || !plan.Weights.TryGetValue(e.Guid, out w))
                    w = 1f;
                cost += w * HealBack(e, max - hp);
            }
            return cost;
        }

        internal static bool KillsHealthAtACost(ScoredAction c, TurnSnapshot snap, LockPlan plan)
        {
            if (c == null || c.Preview == null || snap == null || plan == null || plan.Health == 0)
                return false;
            var p = c.Preview;
            var hits = new List<KeyValuePair<TargetInfo, float>>();
            var killsHealth = false;
            if (p.Hits != null)
            {
                for (var i = 0; i < p.Hits.Count; i++)
                {
                    var h = p.Hits[i];
                    var body = h != null ? snap.Find(h.Guid) : null;
                    if (!IsLock(body))
                        continue;
                    hits.Add(new KeyValuePair<TargetInfo, float>(body, h.Damage));
                    if (body.Guid == plan.Health && h.Damage > 0f && h.Damage + 0.05f >= Math.Max(0f, body.Hp))
                        killsHealth = true;
                }
            }
            if (hits.Count == 0 && IsLock(c.Target))
            {
                hits.Add(new KeyValuePair<TargetInfo, float>(c.Target, p.Damage));
                if (c.Target.Guid == plan.Health && p.Damage > 0f && p.Damage + 0.05f >= Math.Max(0f, c.Target.Hp))
                    killsHealth = true;
            }
            return killsHealth && HealthKillCost(snap, plan, hits, true) > 0.5f;
        }

        // ---------------- free DoT items on a lock (denial_dot_item) ----------------
        //
        // CSV items_combat skill_data_export: every combat item is
        // m_IsFreeAction. spiked_ball skill_dot_very_large_bleed (5 x 3),
        // bear_trap skill_dot_medium_bleed (3 x 3), crows_feet
        // skill_dot_very_small_bleed (1 x 3) on every enemy; locks resist
        // bleed 10% and never cleanse. ItemPolicy scores an attack item on its
        // direct damage only (8 + 0 + 4 < UseThreshold 18), so in 30 logged
        // Denial fights not one of these was used although Crow's Feet was legal in 6 of them, Spiked
        // Ball in 2 and Bear Trap in 1. Here a free DoT item worth at least
        // MinItemDot lock HP is used, on the lock where it is worth the most.
        internal static void ScoreDotItems(List<ScoredAction> candidates, TurnSnapshot snap, EnemyFocus focus)
        {
            if (candidates == null || snap == null || focus == null || !focus.DenialLocks)
                return;
            LockPlan plan = null;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c == null || !c.IsItem || !c.FreeAction || !c.EnemyTarget || c.Item == null || c.Preview == null || !c.Preview.Ok)
                    continue;
                if (DotPerTarget(c.Preview) <= 0f || !IsLock(c.Target))
                    continue;
                if (plan == null)
                    plan = Plan(focus, snap);
                var raw = HitsValue(c.Preview, c.Target, snap, plan, true);
                if (float.IsNegativeInfinity(raw) || raw < MinItemDot)
                    continue;
                var weighted = HitsValue(c.Preview, c.Target, snap, plan, false);
                var top = Math.Max(1f, plan.Weights.Count);
                c.Item.Score = ItemPolicy.UseThreshold + 2f + Math.Max(0f, weighted) / top;
                c.Item.UseNow = true;
                c.Item.Reason = "denial_dot_item";
                c.Score = c.Item.Score;
            }
        }

        // Denial only: the gates that already picked a lock attack ("attack",
        // "attack_over_idle", "open_add"); a kill stands; never onto a click that
        // moves the performer, an item, a cursed skill, a self-hit or a click a
        // cited veto sank.
        internal static ScoredAction BestLockAttack(ScoredAction picked, List<ScoredAction> candidates, TurnSnapshot snap, EnemyFocus focus, TargetInfo performer, string gate)
        {
            if (picked == null || candidates == null || snap == null || focus == null || !focus.DenialLocks)
                return picked;
            if (gate == null || !EvGates.Contains(gate))
                return picked;
            if (!IsLockAttack(picked))
                return picked;
            // A cited pick (rank occupancy walk, open_add aside) stands. A kit
            // score note (dot_host, kit_blind) is not a citation: it only moved
            // the default score, which this pass replaces with lock value.
            if (picked.MustRankWalk || picked.ReachReposition || picked.HealReposition
                || (!string.IsNullOrEmpty(picked.NoteReason) && picked.NoteReason != "open_add"
                    && !ScoreOnlyNotes.Contains(picked.NoteReason)))
                return picked;
            var plan = Plan(focus, snap);
            var pickedValue = Value(picked, snap, plan);
            // A pick that finishes Health while another lock is wounded gives
            // that lock 33% back: it is weighed like any other click.
            var healthAtACost = KillsHealthAtACost(picked, snap, plan);
            // Any other kill stands: that lock's attacks are gone this round.
            if (picked.Preview.Kills && !healthAtACost)
                return picked;
            ScoredAction best = null;
            var bestValue = pickedValue + EvMargin;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c == null || c == picked || !IsLockAttack(c))
                    continue;
                // pd_magnesium_rain carries target_team_effects clear_corpse
                // (ClearsCorpse) but burns every lock: only a corpse click is out.
                if (c.Stealthed || c.Cursed || (c.ClearsCorpse && (c.Target == null || c.Target.Corpse)))
                    continue;
                if (TurnDecider.MovesPerformer(c.SkillId, performer))
                    continue;
                if (TurnDecider.SelfHitPenalty(c.Preview, performer) < 0f)
                    continue;
                if (c.Score - TurnDecider.FocusPay(c.SkillId, c.Preview, c.Focus) < VetoFloor)
                    continue;
                var v = Value(c, snap, plan);
                // Equal value (Magnesium Rain burns every lock whichever one
                // is clicked): keep the click the scorer likes best.
                if (v > bestValue || (best != null && v > bestValue - 0.01f && c.Score > best.Score))
                {
                    best = c;
                    bestValue = v;
                }
            }
            if (best == null)
                return picked;
            best.NoteReason = "denial_ev";
            return best;
        }

        // ---------------- walk back onto a launch rank (denial_walk) ----------------
        //
        // CSV quirk_cowardice_neg: turn_start move_to_back_if_in_rank_1_15pct.
        // In two logged Denial fights (round 1 and round 3) the MAA went from
        // rank 1 to rank 4 between turns with no skill moving him, and every
        // equipped MAA attack launches from ranks 1-3 at best (maa_crush 1,2,
        // maa_rampart 1-3). In the first he then spent six turns on Bolster /
        // Retribution / Defender / Rest / pass while the locks took nothing from
        // him. A hero with no damaging click on any lock, whose rank launches
        // none of his equipped attacks, swaps one rank toward the nearest launch
        // rank instead of an uncited support or a pass (Rampart then walks him
        // forward on its own). The ally he swaps with must launch something
        // from his rank. Crisis heals, cited gates and cover_guard stand.
        private static readonly HashSet<string> WalkGates = new HashSet<string>(StringComparer.Ordinal)
        {
            "any", "any_over_negative_attack", "attack_over_idle", "heal_beats_attack", "setup", "over_pass"
        };

        internal static ScoredAction WalkToLaunch(ScoredAction picked, List<ScoredAction> candidates, TurnSnapshot snap, EnemyFocus focus, TargetInfo performer, string gate)
        {
            if (picked == null || candidates == null || snap == null || performer == null || focus == null || !focus.DenialLocks)
                return picked;
            if (gate == null || !WalkGates.Contains(gate))
                return picked;
            if (picked.IsItem || picked.Kind == SkillKind.Heal || picked.Kind == SkillKind.Move)
                return picked;
            if (picked.Kind == SkillKind.Attack && picked.EnemyTarget && picked.Preview != null
                && (picked.Preview.Damage > 0f || DotPerTarget(picked.Preview) > 0f))
                return picked;
            if (!string.IsNullOrEmpty(picked.NoteReason) && picked.NoteReason != "over_pass_mark" && picked.NoteReason != "over_pass_support")
                return picked;
            if (TurnDecider.IsPassHeal(picked.SkillId))
                return picked;
            // Heals (Rest included) come through crisis_heal; on Death's Door the
            // walk is not worth a turn.
            if (performer.DeathsDoor)
                return picked;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (IsLockAttack(c) && (c.Preview.Damage > 0f || DotPerTarget(c.Preview) > 0f))
                    return picked;
            }
            var here = LaunchDistance(performer, performer.Rank, snap);
            if (here <= 0 || here == int.MaxValue)
                return picked;
            ScoredAction best = null;
            var bestDist = here;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c == null || c.Kind != SkillKind.Move || c.IsItem || c.Target == null || c.Target.Guid == 0)
                    continue;
                if (EnemyFocus.IsTangleWasteSkill(c.SkillId))
                    continue;
                var dest = c.Target.Rank;
                if (Math.Abs(dest - performer.Rank) != 1)
                    continue;
                var d = LaunchDistance(performer, dest, snap);
                if (d >= bestDist)
                    continue;
                if (!PartnerKeepsAClick(c.Target, performer.Rank, snap))
                    continue;
                best = c;
                bestDist = d;
            }
            if (best == null)
                return picked;
            best.NoteReason = "denial_walk";
            return best;
        }

        // Ranks from `rank` to the nearest launch rank of an equipped damaging
        // attack (not a 0-damage mark, heal, move or item). MaxValue: none known.
        internal static int LaunchDistance(TargetInfo hero, int rank, TurnSnapshot snap)
        {
            var best = int.MaxValue;
            if (hero == null || hero.SkillIds == null || snap == null)
                return best;
            for (var i = 0; i < hero.SkillIds.Count; i++)
            {
                var id = hero.SkillIds[i];
                var def = snap.Skill(id);
                if (def == null || def.IsMove || def.IsFriendly || def.IsItemDef || def.MasksUnknown)
                    continue;
                if (TurnDecider.LooksLikeHeal(id, def, null) || TurnDecider.IsPassHeal(id) || !TurnDecider.ReachWalkSkill(id))
                    continue;
                if (id.StartsWith("pass", StringComparison.OrdinalIgnoreCase))
                    continue;
                for (var r = 0; r < 4; r++)
                {
                    if (!def.Launches(r))
                        continue;
                    var d = Math.Abs(r - rank);
                    if (d < best)
                        best = d;
                }
            }
            return best;
        }

        // The swap partner lands on `rank`: a corpse does not care; a hero must
        // still launch one of its equipped non-move skills from there.
        private static bool PartnerKeepsAClick(TargetInfo partner, int rank, TurnSnapshot snap)
        {
            if (partner == null)
                return false;
            if (partner.Corpse || !partner.Living)
                return true;
            if (partner.SkillIds == null)
                return false;
            for (var i = 0; i < partner.SkillIds.Count; i++)
            {
                var def = snap.Skill(partner.SkillIds[i]);
                if (def == null || def.IsMove || def.IsItemDef || def.MasksUnknown)
                    continue;
                if (partner.SkillIds[i].StartsWith("pass", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (def.Launches(rank))
                    return true;
            }
            return false;
        }
    }
}
