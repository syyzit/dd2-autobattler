using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Assets.Code.Actor;
using Assets.Code.Combat;
using Assets.Code.Effect;
using Assets.Code.Skill;
using Newtonsoft.Json.Linq;

namespace Dd2Autobattler.Combat
{
    internal sealed class EnemyThreat
    {
        public uint Guid;
        public string Name;
        public string ClassId;
        public bool Boss;
        public bool Summons;
        public bool Resurrects;
        public bool Supports;
        public bool Add;
        public bool MustKillFirst;
        public bool Defer;
        public bool Commander;
        public bool LungInflate;
        public bool DiesToDot;
        public bool Dodge;
        public bool Riposte;
        public float HpPct;
        public int Worship;
        public int Size;
        public float Score;
        public string Why;
    }

    internal sealed class EnemyFocus
    {
        public readonly List<EnemyThreat> Enemies = new List<EnemyThreat>();
        public bool HasController;
        public bool HasPriorityTarget;
        public bool HasMustKillFirst;
        public int DgRound;
        public int DgTaprootBudget;
        public int DgTaprootHits;
        public bool LeviathanHandUp;
        public bool ExemplarUp;
        public bool ReachPhase2;
        public bool ReachPhase3;
        // Cabin Boy (and similar): burst before Spawning Ground; riposte does not proc.
        public bool BurstBeforeEvolve;
        // Max Worship on Deacon / Cardinal / Exemplar (cap 2 → Exultation).
        public int CultistWorship;

        // wiki.gg/Cultists: especially Altars while Deacon/Cardinal is up.
        internal const float AltarMustKillBias = 20f;
        // Per Worship stack on the boss — escalate Altar / Herald focus.
        internal const float WorshipStackBias = 30f;

        public static EnemyFocus Scan(BattleTeams teams)
        {
            var focus = new EnemyFocus();
            if (teams == null)
                return focus;

            var summonedClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var actor in GameSnapshot.TeamActors(teams, BattleTeams.ENEMY_TEAM_INDEX))
            {
                if (actor == null || !actor.IsLiving || GameSnapshot.IsCorpse(actor))
                    continue;
                var threat = ReadEnemy(actor, summonedClasses);
                focus.Enemies.Add(threat);
            }

            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var e = focus.Enemies[i];
                if (!string.IsNullOrEmpty(e.ClassId) && summonedClasses.Contains(e.ClassId))
                    e.Add = true;
                if (LooksLikeAdd(e.ClassId, e.Name) && !e.Boss && !e.Summons && !e.Resurrects)
                    e.Add = true;
                if (e.Boss || e.Summons || e.Resurrects)
                    focus.HasPriorityTarget = true;
                if (e.Boss || e.Summons || e.Resurrects || e.Supports)
                    focus.HasController = true;
            }

            ApplyDreamingGeneralNote(focus, teams);
            ApplyTangleNotes(focus);
            ApplyDenialLocksNote(focus, teams);
            ApplyHarvestChildNote(focus);
            ApplyLibrarianNote(focus);
            ApplyChirurgeonNote(focus);
            ApplyLeviathanNote(focus);
            ApplyCabinBoyNote(focus);
            ApplyCultistNote(focus);
            ApplyRavenousReachNote(focus);
            ApplyBodyOfWorkNote(focus);
            ApplySeethingSighNote(focus, teams);
            ApplyFocusedFaultNote(focus);

            if (focus.HasPriorityTarget)
                MarkNonPriorityAdds(focus);

            ScoreEnemies(focus);

            return focus;
        }

        // Who IsPriority cares about. MarkNonPriorityAdds uses the same list —
        // duplicating flags here is how a mash Drummer got tagged add.
        internal static bool IsController(EnemyThreat t)
        {
            return t != null && !t.Defer
                   && (t.MustKillFirst || t.Commander || t.Boss || t.Summons || t.Resurrects);
        }

        internal static void MarkNonPriorityAdds(EnemyFocus focus)
        {
            if (focus == null)
                return;
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var e = focus.Enemies[i];
                if (!IsController(e))
                    e.Add = true;
            }
        }

        internal static void ScoreEnemies(EnemyFocus focus)
        {
            if (focus == null)
                return;
            for (var i = 0; i < focus.Enemies.Count; i++)
                ScoreThreat(focus.Enemies[i], focus.HasPriorityTarget, focus.CultistWorship);
        }

        public float ScoreOf(uint guid)
        {
            var t = Find(guid);
            return t != null ? t.Score : 0f;
        }

        public bool IsAdd(uint guid)
        {
            var t = Find(guid);
            return t != null && t.Add;
        }

        public bool IsPriority(uint guid)
        {
            return IsController(Find(guid));
        }

        public bool IsMustKillFirst(uint guid)
        {
            var t = Find(guid);
            return t != null && t.MustKillFirst;
        }

        // Living Altar that is the exclusive must-kill (Pillar bait). Corpses
        // never enter Scan, so MustKillFirst here means it is still up.
        public bool HasLivingAltarMustKill()
        {
            for (var i = 0; i < Enemies.Count; i++)
            {
                var e = Enemies[i];
                if (e != null && e.MustKillFirst && IdHas(e.ClassId, "cultist_altar"))
                    return true;
            }
            return false;
        }

        // Focused Fault p1: every stalk is must_kill. Same Scan rule as Altar —
        // if it is still in the list, it is still up.
        public bool HasLivingStalkMustKill()
        {
            for (var i = 0; i < Enemies.Count; i++)
            {
                var e = Enemies[i];
                if (e != null && e.MustKillFirst && IdHas(e.ClassId, "eyes_stalk"))
                    return true;
            }
            return false;
        }

        public bool AltarMustKillDiesToDot()
        {
            for (var i = 0; i < Enemies.Count; i++)
            {
                var e = Enemies[i];
                if (e != null && e.MustKillFirst && e.DiesToDot && IdHas(e.ClassId, "cultist_altar"))
                    return true;
            }
            return false;
        }

        public bool IsTaproot(uint guid)
        {
            var t = Find(guid);
            return t != null && IdHas(t.ClassId, "taproot");
        }

        public bool IsDeferred(uint guid)
        {
            var t = Find(guid);
            return t != null && t.Defer;
        }

        public bool IsLibrarianStack(uint guid)
        {
            var t = Find(guid);
            return t != null && IdHas(t.ClassId, "librarian_stack");
        }

        public static bool IsTangleWasteSkill(string skillId)
        {
            return !string.IsNullOrEmpty(skillId)
                   && skillId.IndexOf("taproot_tangle", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public string Why(uint guid)
        {
            var t = Find(guid);
            return t != null ? t.Why : null;
        }

        public JObject ToJson()
        {
            var arr = new JArray();
            for (var i = 0; i < Enemies.Count; i++)
            {
                var e = Enemies[i];
                arr.Add(new JObject
                {
                    ["name"] = e.Name,
                    ["class"] = e.ClassId,
                    ["boss"] = e.Boss,
                    ["summons"] = e.Summons,
                    ["resurrects"] = e.Resurrects,
                    ["supports"] = e.Supports,
                    ["add"] = e.Add,
                    ["must_kill"] = e.MustKillFirst,
                    ["defer"] = e.Defer,
                    ["commander"] = e.Commander,
                    ["focus"] = e.Score,
                    ["why"] = e.Why
                });
            }
            var json = new JObject
            {
                ["controller"] = HasController,
                ["priority"] = HasPriorityTarget,
                ["must_kill"] = HasMustKillFirst,
                ["burst_before_evolve"] = BurstBeforeEvolve,
                ["enemies"] = arr
            };
            if (DgRound > 0)
            {
                json["dg_round"] = DgRound;
                json["dg_taproot_budget"] = DgTaprootBudget;
                json["dg_taproot_hits"] = DgTaprootHits;
            }
            return json;
        }

        private EnemyThreat Find(uint guid)
        {
            for (var i = 0; i < Enemies.Count; i++)
            {
                if (Enemies[i].Guid == guid)
                    return Enemies[i];
            }
            return null;
        }

        private static void ScoreThreat(EnemyThreat e, bool hasController, int cultistWorship)
        {
            var score = 0f;
            var why = "";
            if (e.Boss)
            {
                score += 40f;
                why += "boss+";
            }
            if (e.Summons)
            {
                score += 32f;
                why += "summon+";
            }
            if (e.Resurrects)
            {
                score += 32f;
                why += "rez+";
            }
            if (e.Supports)
            {
                score += 18f;
                why += "support+";
            }
            if (e.Size >= 2)
            {
                score += 12f;
                why += "large+";
            }
            if (e.MustKillFirst)
            {
                score += 80f;
                if (IdHas(e.ClassId, "taproot"))
                    why += "taproot+";
                else if (IdHas(e.ClassId, "lock_stress"))
                    why += "lock_stress+";
                else if (IdHas(e.ClassId, "lock_melee"))
                    why += "lock_melee+";
                else if (IdHas(e.ClassId, "lock_ranged"))
                    why += "lock_ranged+";
                else if (IdHas(e.ClassId, "lock_health"))
                    why += "lock_health+";
                else if (IdHas(e.ClassId, "harvest_table"))
                    why += "harvest_table+";
                else if (IdHas(e.ClassId, "librarian") && !IdHas(e.ClassId, "stack"))
                    why += "librarian+";
                else if (IdHas(e.ClassId, "chirurgeon"))
                    why += "chirurgeon+";
                else if (IdHas(e.ClassId, "leviathan_hand"))
                    why += "leviathan_hand+";
                else if (IdHas(e.ClassId, "cultist_exemplar"))
                    why += "exemplar+";
                else if (IdHas(e.ClassId, "cultist_altar"))
                {
                    why += "altar+";
                    score += AltarMustKillBias;
                    if (cultistWorship > 0)
                    {
                        score += WorshipStackBias * cultistWorship;
                        why += "worship+";
                    }
                }
                else if (IdHas(e.ClassId, "cultist_herald"))
                {
                    why += "herald+";
                    if (cultistWorship > 0)
                    {
                        score += WorshipStackBias * cultistWorship;
                        why += "worship+";
                    }
                }
                else if (IdHas(e.ClassId, "cultist_"))
                    why += "cultist+";
                else if (IdHas(e.ClassId, "boss_arms"))
                    why += "reach+";
                else if (IdHas(e.ClassId, "boss_body_failure"))
                    why += "spectre+";
                else if (IdHas(e.ClassId, "boss_body_cherub"))
                    why += "proclaimer+";
                else if (IdHas(e.ClassId, "boss_body"))
                    why += "body_work+";
                else if (IdHas(e.ClassId, "lungs_core") || IdHas(e.ClassId, "lungs_front") || IdHas(e.ClassId, "lungs_back"))
                    why += e.LungInflate ? "sigh_lung+" : "sigh_core+";
                else if (IdHas(e.ClassId, "eyes"))
                    why += "eyes+";
                else
                    why += "bishop+";
            }
            if (e.Commander)
            {
                score += 30f;
                why += "drummer+";
            }
            if (e.Defer)
            {
                score -= 70f;
                why += "defer-";
            }
            if (e.Add && hasController)
            {
                score -= 45f;
                why += "add-";
            }
            e.Score = score;
            e.Why = why.Length > 0 ? why.TrimEnd('+') : "trash";
        }

        // wiki.gg/Dreaming_General "Strategy & Advice" 5-round routine.
        // Taproot is healthless; extra hits arm Soil Stirs / Waking Dead.
        private static void ApplyDreamingGeneralNote(EnemyFocus focus, BattleTeams teams)
        {
            var taprootAlive = false;
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                if (IdHas(focus.Enemies[i].ClassId, "taproot"))
                    taprootAlive = true;
            }
            if (!taprootAlive)
                return;

            var extraVineHeroes = 0;
            var anyLocked = false;
            foreach (var hero in GameSnapshot.TeamActors(teams, BattleTeams.HERO_TEAM_INDEX))
            {
                if (hero == null || !hero.IsLiving || GameSnapshot.IsCorpse(hero))
                    continue;
                if (GameSnapshot.CountToken(hero, "taproot_tangle_c") > 0)
                {
                    anyLocked = true;
                    extraVineHeroes++;
                }
                else if (GameSnapshot.CountToken(hero, "taproot_tangle_b") > 0)
                {
                    extraVineHeroes++;
                }
            }

            var round = CombatMemory.Round;
            if (round < 1)
                round = 1;
            var budget = DreamingTaprootBudget(round, extraVineHeroes, anyLocked);
            var remaining = budget - CombatMemory.TaprootHitsThisRound;
            focus.DgRound = round;
            focus.DgTaprootBudget = budget;
            focus.DgTaprootHits = CombatMemory.TaprootHitsThisRound;

            focus.HasPriorityTarget = true;
            if (remaining <= 0)
            {
                for (var i = 0; i < focus.Enemies.Count; i++)
                {
                    if (IdHas(focus.Enemies[i].ClassId, "taproot"))
                        focus.Enemies[i].Defer = true;
                }
                return;
            }

            focus.HasMustKillFirst = true;
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var e = focus.Enemies[i];
                if (IdHas(e.ClassId, "taproot"))
                    e.MustKillFirst = true;
                else if (IdHas(e.ClassId, "dreaming_general"))
                    e.Defer = true;
            }
        }

        // R1 ignore Taproot. Even rounds: once. Odd rounds after 1: one hit per
        // hero with more than one vine. tangle_c (Nightmare lock) forces one hit
        // so the root still retracts - wiki goal is "Nightmare is almost never seen".
        private static int DreamingTaprootBudget(int round, int extraVineHeroes, bool anyLocked)
        {
            int budget;
            if (round <= 1)
                budget = 0;
            else if (round % 2 == 0)
                budget = 1;
            else
                budget = extraVineHeroes;
            if (anyLocked && budget < 1)
                budget = 1;
            return budget;
        }

        // wiki.gg/Seething_Sigh Strategy: pop lung_inflate (6% max HP) so
        // Sundering Exhalation does not fire; hit the core otherwise. Rarely
        // finish a lung - dead lungs make the core multi-target.
        private static void ApplySeethingSighNote(EnemyFocus focus, BattleTeams teams)
        {
            EnemyThreat core = null;
            var lungs = new List<EnemyThreat>();
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var e = focus.Enemies[i];
                if (IdHas(e.ClassId, "lungs_core"))
                    core = e;
                else if (IdHas(e.ClassId, "lungs_front") || IdHas(e.ClassId, "lungs_back"))
                    lungs.Add(e);
            }
            if (core == null)
                return;

            for (var i = 0; i < lungs.Count; i++)
            {
                var lung = lungs[i];
                foreach (var actor in GameSnapshot.TeamActors(teams, BattleTeams.ENEMY_TEAM_INDEX))
                {
                    if (actor == null || actor.ActorGuid != lung.Guid)
                        continue;
                    lung.LungInflate = GameSnapshot.CountToken(actor, "lung_inflate") > 0;
                    break;
                }
            }

            focus.HasPriorityTarget = true;
            focus.HasMustKillFirst = true;
            core.Boss = true;

            var inflated = 0;
            for (var i = 0; i < lungs.Count; i++)
            {
                if (lungs[i].LungInflate)
                    inflated++;
            }

            if (inflated > 0)
            {
                core.MustKillFirst = false;
                core.Defer = true;
                for (var i = 0; i < lungs.Count; i++)
                {
                    var lung = lungs[i];
                    if (lung.LungInflate)
                    {
                        lung.MustKillFirst = true;
                        lung.Defer = false;
                    }
                    else
                    {
                        lung.MustKillFirst = false;
                        lung.Defer = true;
                        lung.Add = true;
                    }
                }
                return;
            }

            core.MustKillFirst = true;
            core.Defer = false;
            for (var i = 0; i < lungs.Count; i++)
            {
                lungs[i].MustKillFirst = false;
                lungs[i].Defer = true;
                lungs[i].Add = true;
            }
        }

        // wiki.gg/Focused_Fault: kill the stalks (phase 1), then the mass (phase 2).
        private static void ApplyFocusedFaultNote(EnemyFocus focus)
        {
            var stalks = new List<EnemyThreat>();
            EnemyThreat mass = null;
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var e = focus.Enemies[i];
                if (IdHas(e.ClassId, "eyes_stalk"))
                    stalks.Add(e);
                else if (IdHas(e.ClassId, "boss_eyes") && !IdHas(e.ClassId, "stalk"))
                    mass = e;
            }
            if (stalks.Count == 0 && mass == null)
                return;

            focus.HasPriorityTarget = true;
            focus.HasMustKillFirst = true;
            if (stalks.Count > 0)
            {
                for (var i = 0; i < stalks.Count; i++)
                {
                    stalks[i].MustKillFirst = true;
                    stalks[i].Defer = false;
                    stalks[i].Boss = true;
                }
                return;
            }

            mass.MustKillFirst = true;
            mass.Defer = false;
            mass.Boss = true;
        }

        // wiki.gg/Cultists Strategy: kill regulars (especially Altars) before they
        // Worship Deacon/Cardinal into Exultation.
        // wiki.gg/Exemplar + TheGamer: kill the Altar / soft add before Pillar of
        // Sacrifice (denies Regen+Worship). Obsession Altar round-start Taunt is
        // especially bad. Herald is worth a kill. Finish Exemplar when low.
        // Cherub / Evangelist stay deferred while Exemplar is the race.
        // Worship stacks on the boss escalate Altar/Herald urgency (cap 2).
        internal const float ExemplarFinishHpPct = 0.25f;

        internal static void ApplyCultistNote(EnemyFocus focus)
        {
            if (focus == null)
                return;
            EnemyThreat exemplar = null;
            var bosses = new List<EnemyThreat>();
            var regulars = new List<EnemyThreat>();
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var e = focus.Enemies[i];
                if (IdHas(e.ClassId, "cultist_exemplar"))
                    exemplar = e;
                else if (IdHas(e.ClassId, "cultist_deacon") || IdHas(e.ClassId, "cultist_cardinal"))
                    bosses.Add(e);
                else if (IsRegularCultist(e.ClassId))
                    regulars.Add(e);
            }

            var worship = 0;
            if (exemplar != null && exemplar.Worship > worship)
                worship = exemplar.Worship;
            for (var i = 0; i < bosses.Count; i++)
            {
                if (bosses[i].Worship > worship)
                    worship = bosses[i].Worship;
            }
            focus.CultistWorship = worship;

            if (exemplar != null)
            {
                focus.ExemplarUp = true;
                focus.HasPriorityTarget = true;
                focus.HasMustKillFirst = true;

                EnemyThreat altar = null;
                EnemyThreat herald = null;
                for (var i = 0; i < regulars.Count; i++)
                {
                    var e = regulars[i];
                    if (IdHas(e.ClassId, "cultist_altar"))
                        altar = e;
                    else if (IdHas(e.ClassId, "cultist_herald"))
                        herald = e;
                }

                // Do not race Exemplar while Worship is live and Altar can Pillar
                // the second stack into Exultation.
                var finish = exemplar.HpPct > 0f && exemplar.HpPct <= ExemplarFinishHpPct
                             && !(worship >= 1 && altar != null);
                EnemyThreat killFirst = null;
                if (!finish && altar != null)
                    killFirst = altar;
                else if (!finish && herald != null)
                    killFirst = herald;

                if (killFirst != null)
                {
                    killFirst.MustKillFirst = true;
                    killFirst.Defer = false;
                    killFirst.Add = false;
                    exemplar.MustKillFirst = false;
                    exemplar.Defer = true;
                    exemplar.Add = false;
                    exemplar.Boss = true;
                    for (var i = 0; i < regulars.Count; i++)
                    {
                        var e = regulars[i];
                        if (e == killFirst)
                            continue;
                        // Herald stays legal while Altar is the must-kill so a
                        // splash / leftover click can still chip him.
                        if (IdHas(e.ClassId, "cultist_herald"))
                        {
                            e.MustKillFirst = false;
                            e.Defer = false;
                            e.Add = false;
                        }
                        else
                        {
                            e.MustKillFirst = false;
                            e.Defer = true;
                            e.Add = true;
                        }
                    }
                    return;
                }

                exemplar.MustKillFirst = true;
                exemplar.Defer = false;
                exemplar.Add = false;
                exemplar.Boss = true;
                for (var i = 0; i < regulars.Count; i++)
                {
                    var e = regulars[i];
                    if (IdHas(e.ClassId, "cultist_herald"))
                    {
                        e.Defer = false;
                        e.Add = false;
                        e.MustKillFirst = false;
                    }
                    else
                    {
                        e.MustKillFirst = false;
                        e.Defer = true;
                        e.Add = true;
                    }
                }
                return;
            }

            if (bosses.Count == 0)
                return;

            focus.HasPriorityTarget = true;
            if (regulars.Count == 0)
            {
                focus.HasMustKillFirst = true;
                for (var i = 0; i < bosses.Count; i++)
                {
                    bosses[i].MustKillFirst = true;
                    bosses[i].Defer = false;
                    bosses[i].Add = false;
                    bosses[i].Boss = true;
                }
                return;
            }

            focus.HasMustKillFirst = true;
            EnemyThreat deaconAltar = null;
            for (var i = 0; i < regulars.Count; i++)
            {
                if (IdHas(regulars[i].ClassId, "cultist_altar"))
                {
                    deaconAltar = regulars[i];
                    break;
                }
            }

            if (deaconAltar != null)
            {
                // Exclusive Altar must-kill — Cherub/Evangelist used to share
                // MustKillFirst and a low-HP Cherub could outscore a full Altar.
                deaconAltar.MustKillFirst = true;
                deaconAltar.Defer = false;
                deaconAltar.Add = false;
                for (var i = 0; i < regulars.Count; i++)
                {
                    var e = regulars[i];
                    if (e == deaconAltar)
                        continue;
                    if (IdHas(e.ClassId, "cultist_herald"))
                    {
                        e.MustKillFirst = false;
                        e.Defer = false;
                        e.Add = false;
                    }
                    else
                    {
                        e.MustKillFirst = false;
                        e.Defer = true;
                        e.Add = true;
                    }
                }
            }
            else
            {
                for (var i = 0; i < regulars.Count; i++)
                {
                    regulars[i].MustKillFirst = true;
                    regulars[i].Defer = false;
                    regulars[i].Add = false;
                }
            }
            for (var i = 0; i < bosses.Count; i++)
            {
                bosses[i].MustKillFirst = false;
                bosses[i].Defer = true;
            }
        }

        // wiki.gg/Ravenous_Reach Strategy: one target, three phases. Token-strip
        // and bleed cleanse are click-level (TurnDecider). Here just mark the arms.
        internal static void ApplyRavenousReachNote(EnemyFocus focus)
        {
            if (focus == null)
                return;
            var arms = false;
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var e = focus.Enemies[i];
                if (!IdHas(e.ClassId, "boss_arms_phase"))
                    continue;
                arms = true;
                if (IdHas(e.ClassId, "boss_arms_phase2"))
                    focus.ReachPhase2 = true;
                else if (IdHas(e.ClassId, "boss_arms_phase3"))
                    focus.ReachPhase3 = true;
                e.MustKillFirst = true;
                e.Defer = false;
                e.Add = false;
                e.Boss = true;
            }
            if (!arms)
                return;
            focus.HasPriorityTarget = true;
            focus.HasMustKillFirst = true;
        }

        // wiki.gg/Body_of_Work: p1/p2 are the body. p3 God is 999 HP; Proclaimers
        // unlock Face Your Failure, then the Spectre pays 200. Kill those first.
        internal static void ApplyBodyOfWorkNote(EnemyFocus focus)
        {
            if (focus == null)
                return;
            var proclaimers = new List<EnemyThreat>();
            var spectres = new List<EnemyThreat>();
            EnemyThreat god = null;
            EnemyThreat body = null;
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var e = focus.Enemies[i];
                if (IdHas(e.ClassId, "spacer"))
                    continue;
                if (IdHas(e.ClassId, "boss_body_cherub"))
                    proclaimers.Add(e);
                else if (IdHas(e.ClassId, "boss_body_failure"))
                    spectres.Add(e);
                else if (IdHas(e.ClassId, "boss_body_phase3"))
                    god = e;
                else if (IdHas(e.ClassId, "boss_body_phase"))
                    body = e;
            }
            if (proclaimers.Count == 0 && spectres.Count == 0 && god == null && body == null)
                return;

            focus.HasPriorityTarget = true;
            focus.HasMustKillFirst = true;

            if (proclaimers.Count > 0)
            {
                for (var i = 0; i < proclaimers.Count; i++)
                {
                    proclaimers[i].MustKillFirst = true;
                    proclaimers[i].Defer = false;
                    proclaimers[i].Add = false;
                }
                if (god != null)
                {
                    god.MustKillFirst = false;
                    god.Defer = true;
                    god.Add = true;
                }
                return;
            }

            if (spectres.Count > 0)
            {
                for (var i = 0; i < spectres.Count; i++)
                {
                    spectres[i].MustKillFirst = true;
                    spectres[i].Defer = false;
                    spectres[i].Add = false;
                }
                if (god != null)
                {
                    god.MustKillFirst = false;
                    god.Defer = true;
                    god.Add = true;
                }
                return;
            }

            if (god != null)
            {
                god.MustKillFirst = true;
                god.Defer = false;
                god.Add = false;
                god.Boss = true;
                return;
            }

            if (body != null)
            {
                body.MustKillFirst = true;
                body.Defer = false;
                body.Add = false;
                body.Boss = true;
            }
        }

        private static bool IsRegularCultist(string classId)
        {
            return IdHas(classId, "cultist_altar")
                   || IdHas(classId, "cultist_cherub")
                   || IdHas(classId, "cultist_herald")
                   || IdHas(classId, "cultist_evangelist");
        }

        // wiki.gg/Leviathan Strategy: the Hand is the most important target.
        // Undertow drowns a Call of the Deep mark until the Hand dies; recast
        // next round is expected. Hit the body only once the Hand is dead or
        // dying from DoT. CSV: coastal_boss_leviathan / _hand. Deep Rising
        // summons the Hand, so the generic add tag would skip it.
        internal static void ApplyLeviathanNote(EnemyFocus focus)
        {
            if (focus == null)
                return;
            EnemyThreat hand = null;
            EnemyThreat body = null;
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var e = focus.Enemies[i];
                if (IdHas(e.ClassId, "leviathan_hand"))
                    hand = e;
                else if (IdHas(e.ClassId, "coastal_boss_leviathan"))
                    body = e;
            }
            if (hand == null)
                return;

            focus.LeviathanHandUp = true;
            focus.HasPriorityTarget = true;
            if (hand.DiesToDot)
            {
                hand.MustKillFirst = false;
                hand.Defer = true;
                hand.Add = true;
                if (body != null)
                {
                    body.MustKillFirst = false;
                    body.Defer = false;
                    body.Add = false;
                    body.Boss = true;
                }
                return;
            }

            focus.HasMustKillFirst = true;
            hand.MustKillFirst = true;
            hand.Defer = false;
            hand.Add = false;
            hand.Boss = true;
            if (body != null)
            {
                body.MustKillFirst = false;
                body.Defer = true;
                body.Add = true;
            }
        }

        // wiki.gg/Cabin_Boy: fragile incubators that Spawning Ground into a
        // fully healed Fisherfolk with Newborn Mutation. They do not attack
        // while incubating — riposte/taunt setup is a wasted turn. Burst them.
        internal static void ApplyCabinBoyNote(EnemyFocus focus)
        {
            if (focus == null)
                return;
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                if (IsCabinBoy(focus.Enemies[i].ClassId))
                {
                    focus.BurstBeforeEvolve = true;
                    return;
                }
            }
        }

        internal static bool IsCabinBoy(string classId)
        {
            return IdHas(classId, "cabin_boy");
        }

        // wiki.gg/Chirurgeon Strategy: he is a support boss. Leucotomy heals
        // 33% and buffs the patients each round. Kill him; the rest are adds.
        // Boss-node modifier otherwise marks every gaunt as a boss.
        internal static void ApplyChirurgeonNote(EnemyFocus focus)
        {
            if (focus == null)
                return;
            EnemyThreat chirurgeon = null;
            var adds = new List<EnemyThreat>();
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var e = focus.Enemies[i];
                if (IdHas(e.ClassId, "chirurgeon"))
                    chirurgeon = e;
                else if (IdHas(e.ClassId, "lost_soul") || IdHas(e.ClassId, "patient")
                         || IdHas(e.ClassId, "widow") || IdHas(e.ClassId, "yeoman")
                         || IdHas(e.ClassId, "woodsman") || IdHas(e.ClassId, "urchin"))
                    adds.Add(e);
            }
            if (chirurgeon == null)
                return;

            focus.HasPriorityTarget = true;
            focus.HasMustKillFirst = true;
            chirurgeon.MustKillFirst = true;
            chirurgeon.Defer = false;
            chirurgeon.Boss = true;
            chirurgeon.Add = false;
            for (var i = 0; i < adds.Count; i++)
            {
                adds[i].MustKillFirst = false;
                adds[i].Defer = true;
                adds[i].Add = true;
                adds[i].Boss = false;
            }
        }

        // wiki.gg/Librarian Strategy: do not destroy the book stacks. Killing a
        // stack gives a free Burning Bright and speeds Ignite. Hit the Librarian.
        private static void ApplyLibrarianNote(EnemyFocus focus)
        {
            EnemyThreat librarian = null;
            var books = new List<EnemyThreat>();
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var e = focus.Enemies[i];
                if (IdHas(e.ClassId, "librarian_stack"))
                    books.Add(e);
                else if (IdHas(e.ClassId, "librarian"))
                    librarian = e;
            }
            if (librarian == null)
                return;

            focus.HasPriorityTarget = true;
            focus.HasMustKillFirst = true;
            librarian.MustKillFirst = true;
            librarian.Defer = false;
            librarian.Boss = true;
            for (var i = 0; i < books.Count; i++)
            {
                books[i].Defer = true;
                books[i].MustKillFirst = false;
                books[i].Add = true;
            }
        }

        // wiki.gg/Harvest_Child: do not kill the meats. They block Maws of Life.
        // Focus the table (Child). Meats are deferred while the table is alive.
        private static void ApplyHarvestChildNote(EnemyFocus focus)
        {
            EnemyThreat table = null;
            var meats = new List<EnemyThreat>();
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var e = focus.Enemies[i];
                if (IdHas(e.ClassId, "harvest_table"))
                    table = e;
                else if (IdHas(e.ClassId, "fetid_meat") || IdHas(e.ClassId, "putrid_meat"))
                    meats.Add(e);
            }
            if (table == null)
                return;

            focus.HasPriorityTarget = true;
            focus.HasMustKillFirst = true;
            table.MustKillFirst = true;
            table.Defer = false;
            for (var i = 0; i < meats.Count; i++)
            {
                meats[i].Defer = true;
                meats[i].MustKillFirst = false;
                meats[i].Add = true;
            }
        }

        // wiki.gg/The_Shackles_of_Denial Strategy + r/darkestdungeon 19348th.
        // Health first (wiki option A; 33% death-heal wasted at full HP),
        // then the lock that denies this party's damage, Stress last.
        // Not encoded: pass when Health is unreachable (not in those sources).
        private static void ApplyDenialLocksNote(EnemyFocus focus, BattleTeams teams)
        {
            var locks = new List<EnemyThreat>();
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                if (IdHas(focus.Enemies[i].ClassId, "boss_brain_lock"))
                    locks.Add(focus.Enemies[i]);
            }
            if (locks.Count == 0)
                return;

            focus.HasPriorityTarget = true;
            focus.HasMustKillFirst = true;

            var pick = PickDenialLock(locks, teams);
            if (pick == null)
                pick = locks[0];

            for (var i = 0; i < locks.Count; i++)
            {
                var e = locks[i];
                if (e == pick)
                {
                    e.MustKillFirst = true;
                    e.Defer = false;
                }
                else
                {
                    e.Defer = true;
                    e.MustKillFirst = false;
                }
            }
        }

        private static EnemyThreat PickDenialLock(List<EnemyThreat> locks, BattleTeams teams)
        {
            var health = FindLock(locks, "lock_health");
            if (health != null)
                return health;

            var melee = FindLock(locks, "lock_melee");
            var ranged = FindLock(locks, "lock_ranged");
            if (melee != null && ranged != null)
            {
                int meleeSkills;
                int rangedSkills;
                CountPartyAttackTags(teams, out meleeSkills, out rangedSkills);
                return rangedSkills > meleeSkills ? ranged : melee;
            }
            if (melee != null)
                return melee;
            if (ranged != null)
                return ranged;

            return FindLock(locks, "lock_stress") ?? locks[0];
        }

        private static EnemyThreat FindLock(List<EnemyThreat> locks, string key)
        {
            for (var i = 0; i < locks.Count; i++)
            {
                if (IdHas(locks[i].ClassId, key))
                    return locks[i];
            }
            return null;
        }

        private static void CountPartyAttackTags(BattleTeams teams, out int melee, out int ranged)
        {
            melee = 0;
            ranged = 0;
            if (teams == null)
                return;
            foreach (var hero in GameSnapshot.TeamActors(teams, BattleTeams.HERO_TEAM_INDEX))
            {
                if (hero == null || !hero.IsLiving || GameSnapshot.IsCorpse(hero))
                    continue;
                IReadOnlyList<string> skillIds = null;
                try { skillIds = hero.GetEquippedCombatSkillIds(); } catch { }
                if (skillIds == null)
                    continue;
                for (var i = 0; i < skillIds.Count; i++)
                {
                    var id = skillIds[i];
                    if (string.IsNullOrEmpty(id))
                        continue;
                    ActorDataSkill def = null;
                    try
                    {
                        var lib = Assets.Code.Utils.SingletonMonoBehaviour<Assets.Code.Library.Library<string, ActorDataSkill>>.Instance;
                        def = lib != null ? lib.GetLibraryElement(id) : null;
                    }
                    catch { }
                    if (def == null)
                        continue;
                    try
                    {
                        if (def.IsItemSkill)
                            continue;
                    }
                    catch { }
                    if (SkillHasTag(def, "melee"))
                        melee++;
                    if (SkillHasTag(def, "ranged"))
                        ranged++;
                }
            }
        }

        private static bool SkillHasTag(ActorDataSkill def, string key)
        {
            if (def == null || string.IsNullOrEmpty(key))
                return false;
            try
            {
                var tags = def.m_Tags;
                if (tags == null)
                    return false;
                for (var i = 0; i < tags.Count; i++)
                {
                    var tag = tags[i];
                    if (!string.IsNullOrEmpty(tag) && tag.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch { }
            return false;
        }

        internal static void ApplyTangleNotes(EnemyFocus focus)
        {
            var bishop = false;
            var drummer = false;
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var id = focus.Enemies[i].ClassId;
                if (IdHas(id, "lost_battalion_bishop"))
                    bishop = true;
                if (IdHas(id, "lost_battalion_drummer"))
                    drummer = true;
            }
            if (!bishop && !drummer)
                return;

            focus.HasPriorityTarget = true;
            if (bishop)
                focus.HasMustKillFirst = true;

            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var e = focus.Enemies[i];
                if (IdHas(e.ClassId, "lost_battalion_bishop"))
                {
                    e.Resurrects = true;
                    e.MustKillFirst = true;
                }
                else if (IdHas(e.ClassId, "lost_battalion_drummer"))
                {
                    e.Commander = true;
                    e.Add = false;
                }
                else if (bishop || drummer)
                {
                    if (IdHas(e.ClassId, "lost_battalion_knight")
                        || IdHas(e.ClassId, "foot_soldier")
                        || IdHas(e.ClassId, "arbalist"))
                    {
                        e.Add = true;
                        e.Defer = true;
                    }
                }
            }
        }

        private static EnemyThreat ReadEnemy(ActorInstance actor, HashSet<string> summonedClasses)
        {
            var threat = new EnemyThreat
            {
                Guid = actor.ActorGuid,
                Name = string.IsNullOrEmpty(actor.ActorName) ? actor.ActorGuid.ToString() : actor.ActorName
            };

            ActorDataClass cls = null;
            try { cls = actor.ActorDataClass; } catch { }
            if (cls != null)
            {
                try { threat.ClassId = cls.GetKey(); } catch { }
                try { threat.Size = cls.m_Size; } catch { }
                try
                {
                    threat.Boss = cls.IsBiomeBoss || cls.IsExpeditionBoss || cls.IsGangBoss;
                }
                catch { }
                try { if (cls.m_IsSummonReplacable) threat.Add = true; } catch { }
                if (HasTag(cls, "boss") || IdHas(threat.ClassId, "boss"))
                    threat.Boss = true;
            }

            try
            {
                if (actor.Size > threat.Size)
                    threat.Size = actor.Size;
            }
            catch { }

            try
            {
                if (actor.BossModifier != null)
                    threat.Boss = true;
            }
            catch { }

            if (HasTag(actor, "boss") || IdHas(threat.Name, "boss"))
                threat.Boss = true;

            try
            {
                var info = GameSnapshot.Describe(actor);
                if (info != null)
                {
                    threat.DiesToDot = info.DiesToDot;
                    threat.Dodge = info.Dodge;
                    threat.Riposte = info.Riposte;
                    threat.HpPct = info.HpPct;
                }
                threat.Worship = GameSnapshot.CountToken(actor, "worship");
            }
            catch { }

            IReadOnlyList<string> skillIds = null;
            try { skillIds = actor.GetEquippedCombatSkillIds(); } catch { }
            if (skillIds != null)
            {
                for (var i = 0; i < skillIds.Count; i++)
                    ReadSkill(skillIds[i], threat, summonedClasses);
            }

            if (cls != null)
            {
                try
                {
                    var classEffects = GetMember(cls, "ActorDataEffects") ?? GetMember(cls, "m_ActorDataEffects");
                    WalkEffects(classEffects as ActorDataEffects, threat, summonedClasses);
                }
                catch { }
            }

            return threat;
        }

        private static void ReadSkill(string skillId, EnemyThreat threat, HashSet<string> summonedClasses)
        {
            if (string.IsNullOrEmpty(skillId))
                return;
            if (IdHas(skillId, "summon") || IdHas(skillId, "spawn"))
                threat.Summons = true;
            if (IdHas(skillId, "resurrect") || IdHas(skillId, "revive") || IdHas(skillId, "raise")
                || IdHas(skillId, "unholy") || IdHas(skillId, "rally"))
                threat.Resurrects = true;

            ActorDataSkill def = null;
            try
            {
                var lib = Assets.Code.Utils.SingletonMonoBehaviour<Assets.Code.Library.Library<string, ActorDataSkill>>.Instance;
                def = lib != null ? lib.GetLibraryElement(skillId) : null;
            }
            catch { }
            if (def == null)
                return;

            try
            {
                if (def.m_IsFriendly)
                    threat.Supports = true;
            }
            catch { }

            try { WalkEffects(def.ActorDataEffects, threat, summonedClasses); }
            catch { }
        }

        private static void WalkEffects(ActorDataEffects effects, EnemyThreat threat, HashSet<string> summonedClasses)
        {
            if (effects == null)
                return;
            var groups = effects.EffectGroups;
            if (groups == null)
                return;
            for (var g = 0; g < groups.Count; g++)
            {
                var sources = GetMember(groups[g], "SourceEffects") ?? GetMember(groups[g], "m_SourceEffects");
                if (!(sources is IEnumerable items))
                    continue;
                foreach (var src in items)
                {
                    var effect = AsEffect(src);
                    if (effect == null)
                        continue;
                    try
                    {
                        if (!string.IsNullOrEmpty(effect.m_SummonClassActorId))
                        {
                            threat.Summons = true;
                            summonedClasses.Add(effect.m_SummonClassActorId);
                        }
                    }
                    catch { }
                    string effectId = null;
                    try { effectId = effect.GetKey(); } catch { }
                    if (IdHas(effectId, "summon") || IdHas(effectId, "spawn"))
                        threat.Summons = true;
                    if (IdHas(effectId, "resurrect") || IdHas(effectId, "revive") || IdHas(effectId, "raise"))
                        threat.Resurrects = true;
                    try
                    {
                        if (effect.m_HealthHealAmount > 0f || effect.m_HealthHealPercent > 0f)
                            threat.Supports = true;
                    }
                    catch { }
                }
            }
        }

        private static bool LooksLikeAdd(string classId, string name)
        {
            return IdHas(classId, "tentacle") || IdHas(classId, "minion") || IdHas(classId, "spawn")
                   || IdHas(classId, "_add") || IdHas(classId, "summon")
                   || IdHas(name, "tentacle") || IdHas(name, "minion");
        }

        private static bool HasTag(object obj, string tag)
        {
            if (obj == null || string.IsNullOrEmpty(tag))
                return false;
            try
            {
                var method = obj.GetType().GetMethod("ContainsTag", new[] { typeof(string) });
                if (method != null)
                    return (bool)method.Invoke(obj, new object[] { tag });
            }
            catch { }
            try
            {
                var tags = GetMember(obj, "AllTags") ?? GetMember(obj, "m_Tags") ?? GetMember(obj, "m_AllTags");
                if (tags is IEnumerable list)
                {
                    foreach (var item in list)
                    {
                        var s = item as string;
                        if (!string.IsNullOrEmpty(s) && s.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool IdHas(string id, string key)
        {
            return !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(key)
                   && id.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static EffectDefinition AsEffect(object src)
        {
            if (src == null)
                return null;
            var direct = src as EffectDefinition;
            if (direct != null)
                return direct;
            var inner = GetMember(src, "Definition") ?? GetMember(src, "m_Definition");
            return inner as EffectDefinition;
        }

        private static object GetMember(object obj, string name)
        {
            if (obj == null)
                return null;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var prop = obj.GetType().GetProperty(name, flags);
            if (prop != null)
                return prop.GetValue(obj, null);
            var field = obj.GetType().GetField(name, flags);
            return field != null ? field.GetValue(obj) : null;
        }
    }
}
