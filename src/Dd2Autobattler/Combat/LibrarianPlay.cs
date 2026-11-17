using System;
using System.Collections.Generic;
using ScoredAction = Dd2Autobattler.Combat.TurnDecider.ScoredAction;

namespace Dd2Autobattler.Combat
{
    /// <summary>
    /// The Librarian (Sprawl lair, city_dungeon_3_*; notes/cited-bosses.md). Pure, snapshot only.
    /// Reach is counted per hero from CSV launch / target ranks of the equipped attacks
    /// (TurnDecider.HeroHitsFrom), on the formation a move or self-moving attack leaves.
    /// </summary>
    internal static class LibrarianPlay
    {
        // ---------------- identity ----------------

        internal static bool IsLibrarianClass(string classId)
        {
            return Has(classId, "librarian") && !Has(classId, "stack");
        }

        // CSV librarian_ignite_self: m_ChangeClassActorId fanatic_librarian_ignited.
        internal static bool IsIgnitedClass(string classId)
        {
            return IsLibrarianClass(classId) && Has(classId, "ignited");
        }

        internal static bool IsBookStackClass(string classId)
        {
            return Has(classId, "librarian_stack");
        }

        // CSV fanatic_librarian_stack_* m_IsBattleComplete True: the game ends
        // the battle with stacks still up (IL NoTeamBattleCondition). Snapshots
        // logged before TargetInfo.BattleComplete read it from the class id.
        internal static bool HoldsBattleOpen(TargetInfo enemy)
        {
            if (enemy == null)
                return false;
            return !enemy.BattleComplete && !IsBookStackClass(enemy.ClassId);
        }

        internal static TargetInfo FindLibrarian(TurnSnapshot snap)
        {
            if (snap == null || snap.Enemies == null)
                return null;
            for (var i = 0; i < snap.Enemies.Count; i++)
            {
                var e = snap.Enemies[i];
                if (e == null || !e.Living || e.Corpse || e.Rank < 0)
                    continue;
                if (IsLibrarianClass(e.ClassId))
                    return e;
            }
            return null;
        }

        // ---------------- formation model ----------------

        // A `*_move` (m_IsMoveToTarget, relative -2..2) or a one-rank self-move
        // (move_forward_1 / move_backward_1) puts the performer on dest and
        // slides every hero between one step toward his old rank (logs: MAA
        // rank 3 -> 1 over GR / HWM gave MAA, GR, HWM, PD). Null when the ranks
        // are not one hero per rank (cannot model).
        internal static Dictionary<uint, int> RanksAfterMove(TurnSnapshot snap, TargetInfo performer, int dest)
        {
            if (snap == null || performer == null || snap.Heroes == null)
                return null;
            var from = performer.Rank;
            if (from < 0 || dest < 0 || dest > 3 || dest == from)
                return null;
            var seen = new HashSet<int>();
            var ranks = new Dictionary<uint, int>();
            for (var i = 0; i < snap.Heroes.Count; i++)
            {
                var h = snap.Heroes[i];
                if (h == null || h.Rank < 0)
                    continue;
                if (h.Size > 1 || !seen.Add(h.Rank))
                    return null;
                ranks[h.Guid] = h.Rank;
            }
            var lo = Math.Min(from, dest);
            var hi = Math.Max(from, dest);
            for (var r = lo; r <= hi; r++)
            {
                if (!seen.Contains(r))
                    return null;
            }
            var step = dest > from ? -1 : 1;
            for (var i = 0; i < snap.Heroes.Count; i++)
            {
                var h = snap.Heroes[i];
                if (h == null || h.Rank < lo || h.Rank > hi)
                    continue;
                ranks[h.Guid] = h.Guid == performer.Guid ? dest : h.Rank + step;
            }
            return ranks;
        }

        // Living heroes with an equipped damaging attack that launches from
        // their rank and lands on the Librarian's rank.
        internal static int ReachCount(TurnSnapshot snap, Dictionary<uint, int> ranks, TargetInfo librarian)
        {
            if (snap == null || snap.Heroes == null || librarian == null)
                return 0;
            var n = 0;
            for (var i = 0; i < snap.Heroes.Count; i++)
            {
                var h = snap.Heroes[i];
                if (h == null || !h.Living || h.Corpse || h.Rank < 0)
                    continue;
                int rank;
                if (ranks == null || !ranks.TryGetValue(h.Guid, out rank))
                    rank = h.Rank;
                if (TurnDecider.HeroHitsFrom(snap, h, rank, librarian))
                    n++;
            }
            return n;
        }

        // ---------------- party reach walk (librarian_reach) ----------------
        //
        // wiki Strategy: "Your damage dealers need to reach every rank";
        // Categorize sorts the party by English class name (logs: GR, HWM, MAA,
        // PD), "bring repositioning skills". CSV: he starts on rank 4 behind
        // three stacks and only moves up as Page Burner eats the stack in front
        // (m_TargetRelativeRanks -1); from rank 4 only Pistol Shot (HWM, ranks
        // 2-4) and Thrown Dagger (GR, 2-4) reach him. After Categorize only the
        // HWM does; the MAA (rank 3) launches nothing on rank 4 from anywhere,
        // so the reach walk (which asks whether the walker himself gains a
        // hit) never fired for him until the stack in front burned (logged
        // Librarian fights: one walk in round 4, three in round 2, after a
        // Retribution turn). His rank 3 -> 1 move puts GR back
        // on Thrown Dagger and keeps HWM on Pistol: two heroes on him instead
        // of one. A hero with no damaging click on him takes a move that
        // strictly raises the number of heroes who reach him (same score as
        // other reach walks: 180, 90 in a party crisis, so crisis heals win).
        internal const float WalkScore = 180f;
        internal const float WalkCrisisScore = 90f;

        internal static bool ApplyPartyReachWalk(List<ScoredAction> candidates, TargetInfo performer, TurnSnapshot snap, bool partyCrisis)
        {
            if (candidates == null || performer == null || snap == null)
                return false;
            var lib = FindLibrarian(snap);
            if (lib == null)
                return false;
            if (TurnDecider.HasDamagingHitOn(candidates, lib.Guid))
                return false;
            for (var i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].ReachReposition || candidates[i].MustRankWalk)
                    return false;
            }
            var before = ReachCount(snap, null, lib);
            var any = false;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Kind != SkillKind.Move || c.EnemyTarget || c.Target == null || c.Target.Guid == 0)
                    continue;
                if (EnemyFocus.IsTangleWasteSkill(c.SkillId))
                    continue;
                var ranks = RanksAfterMove(snap, performer, c.Target.Rank);
                if (ranks == null)
                    continue;
                var after = ReachCount(snap, ranks, lib);
                if (after <= before)
                    continue;
                c.ReachReposition = true;
                c.NoteReason = "librarian_reach";
                // More heroes on him first.
                c.Score = (partyCrisis ? WalkCrisisScore : WalkScore) + (after - before);
                any = true;
            }
            return any;
        }

        // ---------------- self-moving attacks (librarian_keep_reach) ----------------
        //
        // CSV hwm_duelists_advance(_u) performer_effects move_forward_1 (launch
        // 2-4), maa_rampart move_forward_1. Duelist's Advance from rank 2 puts
        // the HWM on rank 1, where Pistol Shot does not launch and Wicked Slice
        // only lands on ranks 1-2: while the Librarian is on rank 3-4 his next
        // turn is a walk or nothing (one logged fight: r3 and r5
        // Duelist's Advance, r4 hwm_move back; 26 Duelist's Advances in 16
        // Librarian fights, picked over Pistol on the riposte token value). A
        // damaging attack on him that leaves fewer heroes reaching him than
        // now ranks just below the best legal damaging attack on him that does
        // not.
        internal static void DemoteReachLosingSelfMoves(List<ScoredAction> candidates, TargetInfo performer, TurnSnapshot snap)
        {
            if (candidates == null || performer == null || snap == null)
                return;
            var lib = FindLibrarian(snap);
            if (lib == null)
                return;
            var before = -1;
            List<ScoredAction> losing = null;
            ScoredAction bestKeep = null;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!DamagingOn(c, lib))
                    continue;
                var dest = SelfMoveDest(c.SkillId, performer);
                var loses = false;
                if (dest >= 0)
                {
                    var ranks = RanksAfterMove(snap, performer, dest);
                    if (ranks != null)
                    {
                        if (before < 0)
                            before = ReachCount(snap, null, lib);
                        loses = ReachCount(snap, ranks, lib) < before;
                    }
                }
                if (loses)
                {
                    if (losing == null)
                        losing = new List<ScoredAction>();
                    losing.Add(c);
                }
                else if (!c.IsItem && (bestKeep == null || c.Score > bestKeep.Score))
                    bestKeep = c;
            }
            if (losing == null || bestKeep == null)
                return;
            for (var i = 0; i < losing.Count; i++)
            {
                if (losing[i].Score >= bestKeep.Score)
                    losing[i].Score = bestKeep.Score - 1f;
            }
        }

        // Rank the click leaves the performer on, or -1 when it does not move him.
        internal static int SelfMoveDest(string skillId, TargetInfo performer)
        {
            if (performer == null || !TurnDecider.MovesPerformer(skillId, performer))
                return -1;
            var forward = Has(skillId, "maa_rampart") || Has(skillId, "maa_hold_the_line")
                          || Has(skillId, "hwm_duelists_advance") || Has(skillId, "gr_lunge");
            var dest = forward ? performer.Rank - 1 : performer.Rank + 1;
            return dest >= 0 && dest <= 3 ? dest : -1;
        }

        private static bool DamagingOn(ScoredAction c, TargetInfo lib)
        {
            if (c == null || lib == null || c.Kind != SkillKind.Attack || !c.EnemyTarget)
                return false;
            if (c.Target == null || c.Target.Guid != lib.Guid)
                return false;
            if (c.IsItem && c.FreeAction)
                return false;
            return c.Preview != null && c.Preview.Damage > 0f && c.Preview.HitChance > 0f;
        }

        // ---------------- after Ignite (librarian_race) ----------------
        //
        // CSV fanatic_librarian_ignited: speed 8, 2 turns a round, every turn
        // Conflagration (m_IsMultiHit, ranks 1-4: 3-6 + skill_dot_small_burn on
        // every hero), 2 deaths_door_armor again, HP = HP at Ignite + 24
        // (m_ActorChangeClassHealType MAX_HP_DELTA, 116 -> 140), no retreat
        // (m_RunDataStatsId no_retreat). Logs (12 ignited phases):
        // 26-46 party HP lost a round with four heroes,
        // hero heals 0-34 a round; 38 of 96 hero turns healed, Rested or
        // supported; both wins came from 9 and 0 HP at Ignite. No heal keeps
        // up and nothing ends the fight but his death, so every turn spent off
        // him only adds Conflagrations. A legal damaging attack on the ignited
        // Librarian replaces any other pick but a free-action item.
        internal const float RaceVetoFloor = -100f;

        internal static ScoredAction IgnitedRace(ScoredAction picked, List<ScoredAction> candidates, TurnSnapshot snap)
        {
            if (candidates == null || snap == null)
                return picked;
            var lib = FindLibrarian(snap);
            if (lib == null || !IsIgnitedClass(lib.ClassId))
                return picked;
            if (picked != null && (DamagingOn(picked, lib) || (picked.IsItem && picked.FreeAction)))
                return picked;
            ScoredAction best = null;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!DamagingOn(c, lib) || c.Score <= RaceVetoFloor)
                    continue;
                if (best == null || c.Score > best.Score)
                    best = c;
            }
            if (best == null)
                return picked;
            if (string.IsNullOrEmpty(best.NoteReason))
                best.NoteReason = "librarian_race";
            return best;
        }

        private static bool Has(string id, string key)
        {
            return !string.IsNullOrEmpty(id) && id.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
