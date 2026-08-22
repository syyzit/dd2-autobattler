using System;
using System.Collections.Generic;
using Assets.Code.Actor;
using Assets.Code.Actor.ActorController;
using Assets.Code.Combat;
using Assets.Code.Library;
using Assets.Code.Skill;
using Assets.Code.Utils;
using Dd2Autobattler.Logging;
using Newtonsoft.Json.Linq;

namespace Dd2Autobattler.Combat
{
    public sealed class ChosenAction
    {
        public string SkillId;
        public uint TargetGuid;
        public string Reason;
        public bool IsItem;
    }

    public static class TurnDecider
    {
        [ThreadStatic]
        private static uint _pendingTarget;

        public static uint ConsumePendingTarget()
        {
            var t = _pendingTarget;
            _pendingTarget = 0;
            return t;
        }

        public static ChosenAction Decide(ActorControllerBase controller)
        {
            var performer = controller != null ? GetPerformer(controller) : null;
            if (performer == null)
                return null;

            var entries = controller.GetValidSkillTargetEntries();
            var teams = GetTeams(controller);
            var livingEnemies = CountLivingEnemies(teams);
            var performerGuid = performer.ActorGuid;
            var party = PartyKit.Scan(teams, performerGuid);
            var focus = EnemyFocus.Scan(teams);
            var candidates = new List<ScoredAction>();

            if (entries != null)
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (entry == null || !entry.IsValid || string.IsNullOrEmpty(entry.m_SkillId))
                        continue;

                    var skillDef = GetSkill(entry.m_SkillId);
                    var targets = entry.m_ValidTargetActorGuids;
                    if (targets == null || targets.Count == 0)
                        continue;

                    for (var t = 0; t < targets.Count; t++)
                    {
                        var targetGuid = targets[t];
                        if (targetGuid == 0)
                            continue;

                        var stealthed = false;
                        try { stealthed = controller.GetIsStealthedTarget(entry.m_SkillId, targetGuid); } catch { }

                        var preview = SkillPreviewReader.Score(performer.ActorGuid, entry.m_SkillId, targetGuid);
                        if (preview.Ok)
                            SkillPreviewReader.AddSkillTokenAdds(skillDef, preview);
                        var target = GameSnapshot.Describe(GetActor(teams, targetGuid));
                        var enemyTarget = IsEnemy(teams, targetGuid);
                        var kind = Classify(entry.m_SkillId, skillDef, preview, enemyTarget);
                        if (!preview.Kills && preview.Damage > 0f && target.Hp > 0f && preview.Damage >= target.Hp)
                            preview.Kills = true;
                        if (target.DiesToDot && livingEnemies > 1)
                            preview.Kills = false;
                        var role = PartyKit.DescribeSkill(skillDef);
                        var tokens = TokenPrices.Evaluate(kind, enemyTarget, preview, target, livingEnemies, party, performerGuid, role);
                        var isItem = ItemPolicy.IsCombatItem(skillDef, entry.m_SkillId, performer);
                        var freeItem = isItem && ItemPolicy.IsFreeAction(skillDef);
                        var qty = isItem ? ItemPolicy.RemainingQty(performer, entry.m_SkillId) : 0;
                        var item = isItem
                            ? ItemPolicy.Evaluate(entry.m_SkillId, skillDef, kind, enemyTarget, preview, target, tokens, livingEnemies, qty)
                            : null;
                        var score = ScoreAction(entry.m_SkillId, kind, enemyTarget, stealthed || target.Stealth, preview, target, livingEnemies, party, focus)
                                    + tokens.Score
                                    + (party != null ? party.SetupBonus(role, target, enemyTarget) : 0f);
                        if (item != null)
                            score = item.Score;
                        candidates.Add(new ScoredAction
                        {
                            SkillId = entry.m_SkillId,
                            TargetGuid = targetGuid,
                            Kind = kind,
                            EnemyTarget = enemyTarget,
                            Target = target,
                            Preview = preview,
                            Tokens = tokens,
                            Item = item,
                            IsItem = isItem,
                            FreeAction = freeItem,
                            ItemQty = qty,
                            Score = score,
                            Stealthed = stealthed || target.Stealth,
                            Focus = enemyTarget && target != null && target.Actor != null ? focus.ScoreOf(target.Actor.ActorGuid) : 0f,
                            FocusWhy = enemyTarget && target != null && target.Actor != null ? focus.Why(target.Actor.ActorGuid) : null,
                            Cursed = IsCursedSkill(performer, entry.m_SkillId)
                        });
                    }
                }
            }

            ApplyCursePenalty(candidates);
            if (AllyInCrisis(teams) && !HasLegalAllyHeal(candidates))
                ApplyHealReposition(candidates, performer);
            var reachTarget = ReachWalkTarget(candidates, performer, teams, focus, livingEnemies);
            if (reachTarget != null)
                ApplyReachReposition(candidates, performer, reachTarget);
            ApplyHarvestHungerGuard(candidates, performer, teams, focus);
            ApplyLibrarianBookVeto(candidates, focus);
            ApplySighLungVeto(candidates, focus);
            ApplyFocusedFaultNotes(candidates, focus);
            ApplyOnePly(candidates);
            ApplyCrisisStabilize(candidates, teams);

            var lastEnemy = FindLastLivingEnemy(candidates);
            var lastGuid = lastEnemy != null && lastEnemy.Actor != null ? lastEnemy.Actor.ActorGuid : 0u;
            var awkward = lastEnemy != null && (lastEnemy.Riposte || lastEnemy.Dodge);
            var allowSetup = livingEnemies <= 1 && awkward && CombatMemory.CanSpendSetup(lastGuid);

            var performerBody = GameSnapshot.Describe(performer);
            var performerCrisis = performerBody.DeathsDoor || performerBody.HpPct <= 0.20f;
            var partyDoor = PartyHasDeathsDoor(teams);
            var picked = PickAction(candidates, livingEnemies, allowSetup, performerGuid, focus, partyDoor, performerCrisis);
            var best = picked == null
                ? null
                : new ChosenAction
                {
                    SkillId = picked.SkillId,
                    TargetGuid = picked.TargetGuid,
                    Reason = ReasonFor(picked, livingEnemies, allowSetup),
                    IsItem = picked.IsItem
                };
            var rows = ToLogRows(candidates, focus);

            if (best == null)
            {
                LogTurn(controller, performer, rows, null, "no_legal_action", party, focus);
                return null;
            }

            var wasSetup = picked.Kind == SkillKind.Support || picked.Kind == SkillKind.Pass;
            CombatMemory.NoteChosen(lastGuid, wasSetup && allowSetup && !picked.IsItem);
            if (picked.IsItem)
                CombatMemory.NoteItemUsed(performerGuid);
            if (!picked.IsItem && party != null && party.HeroSpendsCombo(performerGuid))
                CombatMemory.NoteComboSpenderActed(performerGuid);
            if (!picked.EnemyTarget && picked.Target != null
                && (picked.Kind == SkillKind.Heal || IsPassHeal(picked.SkillId))
                && (picked.Target.DeathsDoor || picked.Target.HpPct <= 0.35f))
                CombatMemory.NoteCrisisHeal(picked.TargetGuid);
            if (!picked.IsItem && picked.EnemyTarget && focus != null && focus.IsTaproot(picked.TargetGuid))
                CombatMemory.NoteTaprootHit();
            _pendingTarget = best.TargetGuid;
            LogTurn(controller, performer, rows, best, best.Reason, party, focus);
            return best;
        }

        private sealed class ScoredAction
        {
            public string SkillId;
            public uint TargetGuid;
            public SkillKind Kind;
            public bool EnemyTarget;
            public TargetInfo Target;
            public PreviewScore Preview;
            public TokenEval Tokens;
            public ItemEval Item;
            public bool IsItem;
            public bool FreeAction;
            public int ItemQty;
            public float Score;
            public bool Stealthed;
            public float Focus;
            public string FocusWhy;
            public bool Cursed;
            public bool HealReposition;
            public bool ReachReposition;
            public float Ply;
        }

        // Crush on Combo heals the performer. That is still an attack: do not
        // classify an enemy click as Heal just because the preview has a self-heal.
        internal static SkillKind Classify(string skillId, ActorDataSkill def, PreviewScore preview, bool enemyTarget)
        {
            if (def != null && def.IsMoveSkill)
                return SkillKind.Move;
            if (EnemyFocus.IsTangleWasteSkill(skillId))
                return SkillKind.Move;
            if (!string.IsNullOrEmpty(skillId) &&
                (skillId.EndsWith("_move", StringComparison.OrdinalIgnoreCase) ||
                 skillId.IndexOf("_move", StringComparison.OrdinalIgnoreCase) >= 0))
                return SkillKind.Move;
            if (!enemyTarget && LooksLikeHeal(skillId, preview))
                return SkillKind.Heal;
            if (!string.IsNullOrEmpty(skillId) && skillId.StartsWith("pass_", StringComparison.OrdinalIgnoreCase))
                return SkillKind.Pass;
            if (def != null && def.m_IsFriendly)
                return SkillKind.Support;
            return SkillKind.Attack;
        }

        private static bool LooksLikeHeal(string skillId, PreviewScore preview)
        {
            if (preview != null && (preview.HealValid || preview.Heal > 0f))
                return true;
            if (string.IsNullOrEmpty(skillId))
                return false;
            // pass_heal is Rest. Do not treat it as a skill heal that needs a launch rank.
            if (IsPassHeal(skillId) || skillId.StartsWith("pass_", StringComparison.OrdinalIgnoreCase))
                return false;
            return skillId.IndexOf("heal", StringComparison.OrdinalIgnoreCase) >= 0
                   || skillId.IndexOf("battlefield_medicine", StringComparison.OrdinalIgnoreCase) >= 0
                   || skillId.IndexOf("divine_grace", StringComparison.OrdinalIgnoreCase) >= 0
                   || skillId.IndexOf("gods_comfort", StringComparison.OrdinalIgnoreCase) >= 0
                   || skillId.IndexOf("wyrd", StringComparison.OrdinalIgnoreCase) >= 0
                   || skillId.IndexOf("reconstruction", StringComparison.OrdinalIgnoreCase) >= 0
                   || skillId.IndexOf("ministration", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsEnemy(BattleTeams teams, uint targetGuid)
        {
            if (teams == null)
                return false;
            try { return teams.GetTeamIndexFromActorGuid(targetGuid) == BattleTeams.ENEMY_TEAM_INDEX; }
            catch { return false; }
        }

        private static ActorDataSkill GetSkill(string skillId)
        {
            try
            {
                var lib = SingletonMonoBehaviour<Library<string, ActorDataSkill>>.Instance;
                return lib != null ? lib.GetLibraryElement(skillId) : null;
            }
            catch
            {
                return null;
            }
        }

        private static int CountLivingEnemies(BattleTeams teams)
        {
            var n = 0;
            foreach (var actor in GameSnapshot.TeamActors(teams, BattleTeams.ENEMY_TEAM_INDEX))
            {
                if (actor == null || !actor.IsLiving)
                    continue;
                var info = GameSnapshot.Describe(actor);
                if (!info.Corpse)
                    n++;
            }
            return n;
        }

        private static float ScoreAction(string skillId, SkillKind kind, bool enemyTarget, bool stealthed, PreviewScore preview, TargetInfo target, int livingEnemies, PartyKit party, EnemyFocus focus)
        {
            var score = 0f;
            var hpPct = target != null ? target.HpPct : 1f;
            var lastEnemy = livingEnemies <= 1 && enemyTarget && target != null && !target.Corpse;

            switch (kind)
            {
                case SkillKind.Move:
                    score -= 1000f;
                    break;
                case SkillKind.Pass:
                    score -= 50f;
                    break;
                case SkillKind.Heal:
                    if (enemyTarget)
                    {
                        score -= 40f;
                        break;
                    }
                    if (target != null && target.DeathsDoor)
                        score += 220f;
                    else if (hpPct <= 0.35f)
                        score += 90f + (1f - hpPct) * 80f;
                    else if (hpPct <= 0.55f)
                        score += 25f + (1f - hpPct) * 40f;
                    else
                        score -= 25f;
                    score += preview.Heal;
                    if (party != null)
                        score += party.ProtectBonus(target);
                    break;
                case SkillKind.Support:
                    score += livingEnemies <= 1 ? -40f : 2f;
                    score += preview.Heal * 0.3f;
                    if (!enemyTarget && target != null && LooksLikeStressSupport(skillId))
                    {
                        if (target.Stress >= 9f)
                            score += 55f;
                        else if (target.Stress >= 7f)
                            score += 24f;
                        else
                            score -= 25f;
                    }
                    break;
                default:
                    if (target != null && target.Corpse)
                        score -= 250f;
                    else if (target != null && target.DiesToDot && livingEnemies > 1)
                        score -= 120f;
                    else
                    {
                        score += enemyTarget ? 10f : -30f;
                        score += preview.Damage;
                        if (enemyTarget && hpPct > 0f)
                            score += (1f - hpPct) * 6f;
                        if (lastEnemy && preview.Damage > 0f)
                            score += 30f;
                    }
                    if (enemyTarget && focus != null && target != null && target.Actor != null)
                    {
                        score += focus.ScoreOf(target.Actor.ActorGuid);
                        if (focus.HasPriorityTarget && focus.IsAdd(target.Actor.ActorGuid) && preview.Kills)
                            score -= 25f;
                    }
                    break;
            }

            if (kind == SkillKind.Attack)
            {
                if (stealthed || (target != null && target.Stealth))
                    score -= lastEnemy ? 20f : 200f;
                if (target != null && target.Riposte && !preview.Kills && !lastEnemy)
                    score -= 90f;
                if (preview.HitChance > 0f && preview.HitChance < 0.99f)
                    score -= (1f - preview.HitChance) * (lastEnemy ? 15f : 55f);
            }

            if (preview.Kills && (target == null || (!target.Corpse && !target.DiesToDot)))
                score += 40f;
            if (preview.HealsDeathsDoor || (kind == SkillKind.Heal && target != null && target.DeathsDoor))
                score += 20f;

            return score;
        }

        private static ActorInstance GetActor(BattleTeams teams, uint targetGuid)
        {
            if (teams == null)
                return null;
            try
            {
                var team = teams.GetTeamFromActorGuid(targetGuid);
                return team != null ? team.GetActorFromGuid(targetGuid) : null;
            }
            catch
            {
                return null;
            }
        }

        private static TargetInfo FindLastLivingEnemy(List<ScoredAction> candidates)
        {
            if (candidates == null)
                return null;
            TargetInfo found = null;
            foreach (var c in candidates)
            {
                if (c.Target == null || !c.EnemyTarget || c.Target.Corpse || c.Target.Actor == null || !c.Target.Actor.IsLiving)
                    continue;
                if (found == null)
                    found = c.Target;
                else if (c.Target.Actor.ActorGuid != found.Actor.ActorGuid)
                    return null;
            }
            return found;
        }

        private static ScoredAction PickAction(List<ScoredAction> candidates, int livingEnemies, bool allowSetup, uint performerGuid, EnemyFocus focus, bool partyDoor, bool performerCrisis)
        {
            if (candidates == null || candidates.Count == 0)
                return null;

            ScoredAction bestItem = null;
            foreach (var c in candidates)
            {
                if (!c.IsItem || !c.FreeAction || c.Item == null || !c.Item.UseNow)
                    continue;
                if (!CombatMemory.CanSpendItem(performerGuid, c.Item.Crisis))
                    continue;
                if (bestItem == null || c.Score > bestItem.Score)
                    bestItem = c;
            }
            if (bestItem != null)
                return bestItem;

            var priorityLegal = false;
            var mustKillLegal = false;
            if (focus != null)
            {
                foreach (var c in candidates)
                {
                    if (c.IsItem && c.FreeAction)
                        continue;
                    if (c.Kind != SkillKind.Attack || !c.EnemyTarget || c.Target == null || c.Target.Actor == null)
                        continue;
                    if (focus.HasPriorityTarget && focus.IsPriority(c.Target.Actor.ActorGuid))
                        priorityLegal = true;
                    if (focus.HasMustKillFirst && focus.IsMustKillFirst(c.Target.Actor.ActorGuid))
                        mustKillLegal = true;
                }
            }

            ScoredAction bestCrisisHeal = null;
            ScoredAction bestAttack = null;
            ScoredAction bestSetup = null;
            ScoredAction bestAny = null;
            foreach (var c in candidates)
            {
                if (c.IsItem && c.FreeAction)
                    continue;
                if (bestAny == null || c.Score > bestAny.Score)
                    bestAny = c;
                var crisis = !c.EnemyTarget && c.Target != null && !c.Target.Corpse
                             && (c.Kind == SkillKind.Heal || IsPassHeal(c.SkillId))
                             && (c.Target.DeathsDoor || c.Target.HpPct <= 0.35f);
                if (crisis && (bestCrisisHeal == null || c.Score > bestCrisisHeal.Score))
                    bestCrisisHeal = c;
                var realHit = c.Kind == SkillKind.Attack && c.EnemyTarget && c.Target != null && !c.Target.Corpse
                              && c.Target.Actor != null && c.Target.Actor.IsLiving;
                var hungerGuard = IsHarvestHungerGuard(c.SkillId);
                var splashFocus = HitsFocusTarget(c, focus);
                // wiki.gg/Librarian: do not punch stacks even when he is out of reach.
                // Crush-as-Heal used to make mustKillLegal false, which skipped this gate.
                if (realHit && focus != null && focus.IsLibrarianStack(c.Target.Actor.ActorGuid))
                    continue;
                if (realHit && !hungerGuard && !splashFocus && mustKillLegal && focus.IsDeferred(c.Target.Actor.ActorGuid))
                    continue;
                if (realHit && !hungerGuard && !splashFocus && priorityLegal && focus.IsDeferred(c.Target.Actor.ActorGuid))
                    continue;
                if (realHit && !hungerGuard && !splashFocus && priorityLegal && focus.IsAdd(c.Target.Actor.ActorGuid))
                    continue;
                if (realHit && !splashFocus && performerCrisis && livingEnemies > 1 && focus != null
                    && focus.IsAdd(c.Target.Actor.ActorGuid))
                    continue;
                if (realHit && (bestAttack == null || c.Score > bestAttack.Score))
                    bestAttack = c;
                if (allowSetup && (c.Kind == SkillKind.Support || c.Kind == SkillKind.Pass)
                    && (bestSetup == null || c.Score > bestSetup.Score))
                    bestSetup = c;
            }

            if (bestCrisisHeal != null)
            {
                var alreadyHealed = CombatMemory.CrisisHealThisRound(bestCrisisHeal.TargetGuid);
                var stillDoor = bestCrisisHeal.Target != null && bestCrisisHeal.Target.DeathsDoor;
                // Finish the fight only if nobody is on Death's Door. A last-enemy
                // kill ends combat; a miss while someone is on Death's Door does not.
                var lastKill = bestAttack != null && bestAttack.Preview != null && bestAttack.Preview.Kills
                               && livingEnemies <= 1 && !partyDoor;
                if (!lastKill && (!alreadyHealed || stillDoor))
                    return bestCrisisHeal;
            }

            ScoredAction bestReposition = null;
            foreach (var c in candidates)
            {
                if (!c.HealReposition && !c.ReachReposition)
                    continue;
                if (bestReposition == null || c.Score > bestReposition.Score)
                    bestReposition = c;
            }
            if (bestReposition != null)
                return bestReposition;

            // One setup while the last enemy is awkward, then we must swing.
            if (allowSetup && bestSetup != null && bestAttack != null)
                return bestSetup;
            if (bestAttack != null)
                return bestAttack;
            return bestAny;
        }

        private static List<JObject> ToLogRows(List<ScoredAction> candidates, EnemyFocus focus)
        {
            var rows = new List<JObject>();
            if (candidates == null)
                return rows;
            foreach (var c in candidates)
            {
                var target = c.Target ?? new TargetInfo();
                rows.Add(new JObject
                {
                    ["skill"] = c.SkillId,
                    ["kind"] = c.Kind.ToString(),
                    ["target"] = c.TargetGuid,
                    ["enemy"] = c.EnemyTarget,
                    ["target_hp"] = target.Hp,
                    ["corpse"] = target.Corpse,
                    ["next_dot"] = target.NextDot,
                    ["dies_to_dot"] = target.DiesToDot,
                    ["deaths_door"] = target.DeathsDoor,
                    ["stealthed"] = c.Stealthed,
                    ["riposte"] = target.Riposte,
                    ["dodge"] = target.Dodge,
                    ["combo"] = target.Combo,
                    ["stun"] = target.Stun,
                    ["hit"] = c.Preview != null ? c.Preview.HitChance : 1f,
                    ["apply"] = ToArray(c.Tokens != null ? c.Tokens.Apply : null),
                    ["consume"] = ToArray(c.Tokens != null ? c.Tokens.Consume : null),
                    ["token_price"] = c.Tokens != null ? c.Tokens.Score : 0f,
                    ["item"] = c.IsItem,
                    ["item_free"] = c.FreeAction,
                    ["item_qty"] = c.ItemQty,
                    ["item_use"] = c.Item != null && c.Item.UseNow,
                    ["focus"] = c.Focus,
                    ["focus_why"] = c.FocusWhy,
                    ["score"] = c.Score,
                    ["preview_ok"] = c.Preview != null && c.Preview.Ok,
                    ["dmg"] = c.Preview != null ? c.Preview.Damage : 0f,
                    ["dmg_lo"] = c.Preview != null ? c.Preview.DamageLow : 0f,
                    ["dmg_hi"] = c.Preview != null ? c.Preview.DamageHigh : 0f,
                    ["heal"] = c.Preview != null ? c.Preview.Heal : 0f,
                    ["kills"] = c.Preview != null && c.Preview.Kills,
                    ["curse"] = c.Cursed,
                    ["reposition"] = c.HealReposition,
                    ["ply"] = c.Ply,
                    ["error"] = c.Preview != null ? c.Preview.Error : null
                });
            }
            return rows;
        }

        private static string ReasonFor(ScoredAction picked, int livingEnemies, bool allowSetup)
        {
            if (picked == null)
                return "fallback";
            if (picked.IsItem && picked.Item != null && !string.IsNullOrEmpty(picked.Item.Reason))
                return picked.Item.Reason;

            var kind = picked.Kind;
            var enemyTarget = picked.EnemyTarget;
            var preview = picked.Preview;
            var target = picked.Target;
            var tokens = picked.Tokens;
            if (picked.HealReposition) return "heal_reposition";
            if (picked.ReachReposition) return "reach_reposition";
            if (IsHarvestHungerGuard(picked.SkillId) && picked.Score >= 80f)
                return "hunger_guard";
            if (kind == SkillKind.Heal && target != null && target.DeathsDoor) return "heal_deaths_door";
            if (kind == SkillKind.Attack && picked.FocusWhy != null
                && picked.FocusWhy.IndexOf("librarian", StringComparison.OrdinalIgnoreCase) >= 0
                && picked.FocusWhy.IndexOf("stack", StringComparison.OrdinalIgnoreCase) < 0)
                return "focus_librarian";
            if (kind == SkillKind.Attack && picked.FocusWhy != null
                && picked.FocusWhy.IndexOf("sigh_lung", StringComparison.OrdinalIgnoreCase) >= 0)
                return "focus_sigh_lung";
            if (kind == SkillKind.Attack && picked.FocusWhy != null
                && picked.FocusWhy.IndexOf("sigh_core", StringComparison.OrdinalIgnoreCase) >= 0)
                return "focus_sigh_core";
            if (kind == SkillKind.Attack && picked.FocusWhy != null
                && picked.FocusWhy.IndexOf("eyes", StringComparison.OrdinalIgnoreCase) >= 0)
                return "focus_eyes";
            if (kind == SkillKind.Attack && picked.FocusWhy != null
                && picked.FocusWhy.IndexOf("harvest_table", StringComparison.OrdinalIgnoreCase) >= 0)
                return "focus_harvest";
            if (kind == SkillKind.Attack && picked.FocusWhy != null
                && picked.FocusWhy.IndexOf("chirurgeon", StringComparison.OrdinalIgnoreCase) >= 0)
                return "focus_chirurgeon";
            if (kind == SkillKind.Heal && target != null && target.HpPct <= 0.55f) return "heal_low_ally";
            if (allowSetup && (kind == SkillKind.Support || kind == SkillKind.Pass))
                return "setup_once";
            if (kind == SkillKind.Attack && picked.FocusWhy != null
                && picked.FocusWhy.IndexOf("taproot", StringComparison.OrdinalIgnoreCase) >= 0)
                return "focus_taproot";
            if (kind == SkillKind.Attack && picked.FocusWhy != null
                && picked.FocusWhy.IndexOf("lock_stress", StringComparison.OrdinalIgnoreCase) >= 0)
                return "focus_lock_stress";
            if (kind == SkillKind.Attack && picked.FocusWhy != null
                && picked.FocusWhy.IndexOf("lock_melee", StringComparison.OrdinalIgnoreCase) >= 0)
                return "focus_lock_melee";
            if (kind == SkillKind.Attack && picked.FocusWhy != null
                && picked.FocusWhy.IndexOf("lock_ranged", StringComparison.OrdinalIgnoreCase) >= 0)
                return "focus_lock_ranged";
            if (kind == SkillKind.Attack && picked.FocusWhy != null
                && picked.FocusWhy.IndexOf("lock_health", StringComparison.OrdinalIgnoreCase) >= 0)
                return "focus_lock_health";
            if (kind == SkillKind.Attack && picked.FocusWhy != null
                && picked.FocusWhy.IndexOf("bishop", StringComparison.OrdinalIgnoreCase) >= 0)
                return "focus_bishop";
            if (kind == SkillKind.Attack && picked.FocusWhy != null
                && picked.FocusWhy.IndexOf("drummer", StringComparison.OrdinalIgnoreCase) >= 0)
                return "focus_drummer";
            if (kind == SkillKind.Attack && picked.Focus >= 30f && !string.IsNullOrEmpty(picked.FocusWhy)
                && picked.FocusWhy.IndexOf("add", StringComparison.OrdinalIgnoreCase) < 0
                && picked.FocusWhy.IndexOf("defer", StringComparison.OrdinalIgnoreCase) < 0)
                return "focus_" + (picked.FocusWhy.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "boss"
                    : picked.FocusWhy.IndexOf("summon", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "summoner"
                        : picked.FocusWhy.IndexOf("rez", StringComparison.OrdinalIgnoreCase) >= 0
                            ? "rezzer"
                            : "priority");
            if (kind == SkillKind.Attack && livingEnemies <= 1 && enemyTarget && target != null && !target.Corpse)
                return "finish_last";
            if (kind == SkillKind.Attack && target != null && target.Corpse) return "skip_corpse";
            if (kind == SkillKind.Attack && target != null && target.DiesToDot) return "let_dot_kill";
            if (kind == SkillKind.Attack && (target != null && target.Stealth)) return "skip_stealth";
            if (kind == SkillKind.Attack && target != null && target.Riposte && !preview.Kills) return "skip_riposte";
            if (kind == SkillKind.Attack && preview.HitChance > 0f && preview.HitChance < 0.6f) return "low_hit";
            if (tokens != null && tokens.Reason == "save_combo")
                return "save_combo";
            if (tokens != null && !string.IsNullOrEmpty(tokens.Reason))
                return tokens.Reason;
            if (picked.Ply >= 8f && kind == SkillKind.Attack && enemyTarget
                && preview != null && preview.Damage > 0f && !preview.Kills)
                return "one_ply";
            if (preview.Kills) return "preview_kill";
            if (preview.Ok && preview.Damage > 0) return "preview_damage";
            if (kind == SkillKind.Attack && enemyTarget) return "attack_enemy";
            if (kind == SkillKind.Heal) return "heal";
            if (kind == SkillKind.Support) return "support";
            if (kind == SkillKind.Pass) return "pass";
            if (kind == SkillKind.Move) return "move_last_resort";
            return "fallback";
        }

        private static bool IsHarvestHungerGuard(string skillId)
        {
            return !string.IsNullOrEmpty(skillId)
                   && (skillId.IndexOf("hold_the_line", StringComparison.OrdinalIgnoreCase) >= 0
                       || skillId.IndexOf("toe_to_toe", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Paper-apply this click's preview onto the board. Remaining enemy HP and
        // kills beat a 0-damage Combo mark. Does not clone legal skills for the next hero.
        private static void ApplyOnePly(List<ScoredAction> candidates)
        {
            if (candidates == null)
                return;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.IsItem)
                    continue;
                var ply = BoardDelta(c);
                c.Ply = ply;
                c.Score += ply;
            }
        }

        private static float BoardDelta(ScoredAction c)
        {
            if (c == null || c.Kind != SkillKind.Attack || !c.EnemyTarget || c.Target == null || c.Target.Corpse)
                return 0f;
            var preview = c.Preview;
            var dmg = preview != null ? preview.Damage : 0f;
            var kills = preview != null && preview.Kills;
            var ply = 0f;
            if (kills)
                ply += 28f;
            else
                ply += dmg * 1.2f;
            var applyCombo = c.Tokens != null && TokenPrices.HasId(c.Tokens.Apply, "combo");
            if (applyCombo && !kills && dmg < 1f)
                ply -= 10f;
            else if (applyCombo && !kills)
                ply += 3f;
            return ply;
        }

        // wiki.gg/Librarian: destroying a stack grants Burning Bright and speeds Ignite.
        private static void ApplyLibrarianBookVeto(List<ScoredAction> candidates, EnemyFocus focus)
        {
            if (candidates == null || focus == null)
                return;
            var librarianUp = false;
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var id = focus.Enemies[i].ClassId;
                if (string.IsNullOrEmpty(id))
                    continue;
                if (id.IndexOf("librarian_stack", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (id.IndexOf("librarian", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    librarianUp = true;
                    break;
                }
            }
            if (!librarianUp)
                return;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!c.EnemyTarget)
                    continue;
                var clickStack = c.Target != null && c.Target.Actor != null
                                 && focus.IsLibrarianStack(c.Target.Actor.ActorGuid);
                var splashStack = HitsLibrarianStack(c, focus, clickStack);
                if (!clickStack && !splashStack)
                    continue;
                if (clickStack && c.Preview != null && c.Preview.Kills)
                    c.Score -= 200f;
                else
                    c.Score -= 40f;
            }
        }

        private static bool HitsLibrarianStack(ScoredAction c, EnemyFocus focus, bool clickIsStack)
        {
            if (c == null || c.Preview == null || focus == null)
                return false;
            var hits = c.Preview.HitGuids;
            if (hits == null || hits.Count == 0)
                return false;
            var clickGuid = c.Target != null && c.Target.Actor != null ? c.Target.Actor.ActorGuid : 0u;
            for (var i = 0; i < hits.Count; i++)
            {
                var guid = hits[i];
                if (guid == 0 || (clickIsStack && guid == clickGuid))
                    continue;
                if (focus.IsLibrarianStack(guid))
                    return true;
            }
            return false;
        }

        // wiki.gg/Seething_Sigh: rarely kill lungs. Pop inflate; finishing a lung
        // without inflate (or when a non-kill pop exists) is wasted core damage.
        private static void ApplySighLungVeto(List<ScoredAction> candidates, EnemyFocus focus)
        {
            if (candidates == null || focus == null)
                return;
            var coreUp = false;
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                if (IdHasClass(focus.Enemies[i].ClassId, "lungs_core"))
                {
                    coreUp = true;
                    break;
                }
            }
            if (!coreUp)
                return;

            var safePop = false;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!IsSighLungTarget(c) || c.Preview == null || c.Preview.Kills)
                    continue;
                if (c.Target != null && c.Target.LungInflate && c.Preview.Damage > 0f)
                    safePop = true;
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!IsSighLungTarget(c) || c.Preview == null || !c.Preview.Kills)
                    continue;
                if (c.Target == null || !c.Target.LungInflate)
                    c.Score -= 200f;
                else if (safePop)
                    c.Score -= 120f;
            }
        }

        // wiki.gg/Focused_Fault Phase 2: Weak/Block blunt Limerence. ≥3 positive
        // tokens on a hero without Seen invites Suppress.
        private static void ApplyFocusedFaultNotes(List<ScoredAction> candidates, EnemyFocus focus)
        {
            if (candidates == null || focus == null)
                return;
            var massUp = false;
            var stalksUp = false;
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var id = focus.Enemies[i].ClassId;
                if (IdHasClass(id, "eyes_stalk"))
                    stalksUp = true;
                else if (IdHasClass(id, "boss_eyes"))
                    massUp = true;
            }
            if (!massUp && !stalksUp)
                return;

            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.EnemyTarget && massUp && c.Tokens != null && TokenPrices.HasId(c.Tokens.Apply, "weak")
                    && c.Target != null && c.Target.Actor != null)
                {
                    string classId = null;
                    try { classId = c.Target.Actor.ActorDataClass != null ? c.Target.Actor.ActorDataClass.GetKey() : null; } catch { }
                    if (IdHasClass(classId, "boss_eyes") && !IdHasClass(classId, "stalk"))
                        c.Score += 12f;
                }
                if (massUp && !c.EnemyTarget && c.Target != null && c.Target.EyesFocus <= 0
                    && c.Target.PositiveTokens >= 2 && c.Tokens != null
                    && (TokenPrices.HasId(c.Tokens.Apply, "strength")
                        || TokenPrices.HasId(c.Tokens.Apply, "block")
                        || TokenPrices.HasId(c.Tokens.Apply, "dodge")))
                    c.Score -= 25f;
            }
        }

        private static bool HitsFocusTarget(ScoredAction c, EnemyFocus focus)
        {
            if (c == null || c.Preview == null || focus == null)
                return false;
            var hits = c.Preview.HitGuids;
            if (hits == null || hits.Count == 0)
                return false;
            for (var i = 0; i < hits.Count; i++)
            {
                var guid = hits[i];
                if (guid == 0 || (c.Target != null && c.Target.Actor != null && guid == c.Target.Actor.ActorGuid))
                    continue;
                if (focus.IsMustKillFirst(guid) || focus.IsPriority(guid))
                    return true;
            }
            return false;
        }

        private static bool IsSighLungTarget(ScoredAction c)
        {
            if (c == null || !c.EnemyTarget || c.Target == null || c.Target.Actor == null)
                return false;
            string classId = null;
            try { classId = c.Target.Actor.ActorDataClass != null ? c.Target.Actor.ActorDataClass.GetKey() : null; } catch { }
            return IdHasClass(classId, "lungs_front") || IdHasClass(classId, "lungs_back");
        }

        private static bool IdHasClass(string id, string key)
        {
            return !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(key)
                   && id.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLikeStressSupport(string skillId)
        {
            if (string.IsNullOrEmpty(skillId))
                return false;
            return skillId.IndexOf("inspiring", StringComparison.OrdinalIgnoreCase) >= 0
                   || skillId.IndexOf("bolster", StringComparison.OrdinalIgnoreCase) >= 0
                   || skillId.IndexOf("play_out", StringComparison.OrdinalIgnoreCase) >= 0
                   || skillId.IndexOf("consolation", StringComparison.OrdinalIgnoreCase) >= 0
                   || skillId.IndexOf("soothing", StringComparison.OrdinalIgnoreCase) >= 0
                   || skillId.IndexOf("stress_heal", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ApplyHarvestHungerGuard(List<ScoredAction> candidates, ActorInstance performer, BattleTeams teams, EnemyFocus focus)
        {
            if (candidates == null || performer == null || teams == null || focus == null)
                return;
            var tableUp = false;
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                if (focus.Enemies[i].ClassId != null
                    && focus.Enemies[i].ClassId.IndexOf("harvest_table", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    tableUp = true;
                    break;
                }
            }
            if (!tableUp)
                return;

            var selfHungry = GameSnapshot.CountToken(performer, "harvest_hunger") > 0;
            var allyHungry = false;
            foreach (var hero in GameSnapshot.TeamActors(teams, BattleTeams.HERO_TEAM_INDEX))
            {
                if (hero == null || hero.ActorGuid == performer.ActorGuid)
                    continue;
                if (!hero.IsLiving || GameSnapshot.IsCorpse(hero))
                    continue;
                if (GameSnapshot.CountToken(hero, "harvest_hunger") > 0)
                {
                    allyHungry = true;
                    break;
                }
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!IsHarvestHungerGuard(c.SkillId))
                    continue;
                if (selfHungry)
                    c.Score -= 50f;
                else if (allyHungry)
                    c.Score += 80f;
            }
        }

        private static bool AllyInCrisis(BattleTeams teams)
        {
            if (teams == null)
                return false;
            foreach (var hero in GameSnapshot.TeamActors(teams, BattleTeams.HERO_TEAM_INDEX))
            {
                if (hero == null || !hero.IsLiving || GameSnapshot.IsCorpse(hero))
                    continue;
                var body = GameSnapshot.Describe(hero);
                if (body.DeathsDoor || body.HpPct <= 0.35f)
                    return true;
            }
            return false;
        }

        private static bool PartyHasDeathsDoor(BattleTeams teams)
        {
            if (teams == null)
                return false;
            foreach (var hero in GameSnapshot.TeamActors(teams, BattleTeams.HERO_TEAM_INDEX))
            {
                if (hero == null || !hero.IsLiving || GameSnapshot.IsCorpse(hero))
                    continue;
                if (GameSnapshot.Describe(hero).DeathsDoor)
                    return true;
            }
            return false;
        }

        private static bool IsPassHeal(string skillId)
        {
            return !string.IsNullOrEmpty(skillId)
                   && skillId.IndexOf("pass_heal", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Skill heal spent (BM limit 3). Food/bandage/antivenom that still heal must
        // fire on Death's Door. Do not buff (Ounce of Prevention) while someone is dying.
        private static void ApplyCrisisStabilize(List<ScoredAction> candidates, BattleTeams teams)
        {
            if (candidates == null || teams == null)
                return;
            var door = PartyHasDeathsDoor(teams);
            var crisis = AllyInCrisis(teams);
            if (!door && !crisis)
                return;
            var skillHeal = HasLegalCrisisHeal(candidates);
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Kind == SkillKind.Support && !c.IsItem && (door || crisis))
                    c.Score -= 80f;
                if (!c.IsItem || c.Target == null || c.EnemyTarget || c.Target.Corpse)
                    continue;
                if (c.Preview == null || c.Preview.Heal <= 0f)
                    continue;
                if (!c.Target.DeathsDoor && c.Target.HpPct > 0.30f)
                    continue;
                if (skillHeal && !c.Target.DeathsDoor)
                    continue;
                if (c.Item == null)
                    continue;
                c.Item.UseNow = true;
                c.Item.Crisis = true;
                if (c.Target.DeathsDoor)
                {
                    c.Item.Score = Math.Max(c.Item.Score, 90f);
                    c.Item.Reason = "item_heal_dd";
                }
                else
                {
                    c.Item.Score = Math.Max(c.Item.Score, 55f);
                    c.Item.Reason = "item_heal_prevent";
                }
                c.Score = c.Item.Score;
            }
        }

        private static bool HasLegalCrisisHeal(List<ScoredAction> candidates)
        {
            if (candidates == null)
                return false;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.IsItem)
                    continue;
                if (c.EnemyTarget || c.Target == null || c.Target.Corpse)
                    continue;
                if (c.Kind != SkillKind.Heal && !IsPassHeal(c.SkillId))
                    continue;
                if (c.Target.DeathsDoor || c.Target.HpPct <= 0.35f)
                    return true;
            }
            return false;
        }

        private static bool HasLegalAllyHeal(List<ScoredAction> candidates)
        {
            if (candidates == null)
                return false;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.IsItem)
                    continue;
                if (c.Kind == SkillKind.Heal && !c.EnemyTarget && c.Target != null && !c.Target.Corpse)
                    return true;
            }
            return false;
        }

        private static void ApplyCursePenalty(List<ScoredAction> candidates)
        {
            if (candidates == null)
                return;
            var cleanAttack = false;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!c.Cursed && c.Kind == SkillKind.Attack && c.EnemyTarget && c.Target != null && !c.Target.Corpse)
                    cleanAttack = true;
            }
            if (!cleanAttack)
                return;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!c.Cursed)
                    continue;
                if (c.Kind == SkillKind.Heal)
                    continue;
                c.Score -= 40f;
            }
        }

        private static void ApplyHealReposition(List<ScoredAction> candidates, ActorInstance performer)
        {
            if (candidates == null || performer == null)
                return;
            var heals = BlockedHealSkills(performer);
            if (heals.Count == 0)
                return;
            var size = 1;
            try { size = performer.Size; } catch { }
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Kind != SkillKind.Move || c.Target == null || c.Target.Actor == null)
                    continue;
                var dest = 0;
                try { dest = c.Target.Actor.TeamPosition; } catch { continue; }
                var helps = false;
                for (var h = 0; h < heals.Count; h++)
                {
                    try
                    {
                        if (heals[h].GetHasLaunchRank(dest, size))
                        {
                            helps = true;
                            break;
                        }
                    }
                    catch { }
                }
                if (!helps)
                    continue;
                c.HealReposition = true;
                c.Score = 200f;
            }
        }

        // Last living enemy behind corpses, or a must-kill (Librarian) this
        // hero cannot damage from the current rank. 0-damage Combo marks do not count.
        private static bool HasDamagingHitOn(List<ScoredAction> candidates, uint guid)
        {
            if (candidates == null)
                return false;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.IsItem && c.FreeAction)
                    continue;
                if (c.Kind != SkillKind.Attack || !c.EnemyTarget || c.Target == null || c.Target.Corpse)
                    continue;
                if (c.Target.Actor == null || !c.Target.Actor.IsLiving)
                    continue;
                if (guid != 0 && c.Target.Actor.ActorGuid != guid)
                    continue;
                if (c.Preview != null && c.Preview.Damage > 0f)
                    return true;
            }
            return false;
        }

        private static bool HasDamagingMustKillHit(List<ScoredAction> candidates, EnemyFocus focus)
        {
            if (candidates == null || focus == null)
                return false;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.IsItem && c.FreeAction)
                    continue;
                if (c.Kind != SkillKind.Attack || !c.EnemyTarget || c.Target == null || c.Target.Corpse)
                    continue;
                if (c.Target.Actor == null || !c.Target.Actor.IsLiving)
                    continue;
                if (c.Preview == null || c.Preview.Damage <= 0f)
                    continue;
                if (focus.IsMustKillFirst(c.Target.Actor.ActorGuid))
                    return true;
            }
            return false;
        }

        private static ActorInstance ReachWalkTarget(List<ScoredAction> candidates, ActorInstance performer, BattleTeams teams, EnemyFocus focus, int livingEnemies)
        {
            if (livingEnemies <= 1 && !HasDamagingHitOn(candidates, 0))
            {
                var last = FindLastLivingEnemy(candidates);
                return last != null ? last.Actor : null;
            }

            if (focus == null || !focus.HasMustKillFirst || HasDamagingMustKillHit(candidates, focus))
                return null;

            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var e = focus.Enemies[i];
                if (e == null || !e.MustKillFirst)
                    continue;
                var actor = GetActor(teams, e.Guid);
                if (actor == null || !actor.IsLiving)
                    continue;
                try { if (GameSnapshot.IsCorpse(actor)) continue; } catch { }
                if (BlockedReachSkills(performer, actor).Count > 0)
                    return actor;
            }
            return null;
        }

        private static void ApplyReachReposition(List<ScoredAction> candidates, ActorInstance performer, ActorInstance enemy)
        {
            if (candidates == null || performer == null || enemy == null)
                return;
            var skills = BlockedReachSkills(performer, enemy);
            if (skills.Count == 0)
                return;
            var size = 1;
            try { size = performer.Size; } catch { }
            var enemyRank = 0;
            var enemySize = 1;
            try { enemyRank = enemy.TeamPosition; } catch { return; }
            try { enemySize = enemy.Size; } catch { }

            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Kind != SkillKind.Move || c.Target == null || c.Target.Actor == null)
                    continue;
                var dest = 0;
                try { dest = c.Target.Actor.TeamPosition; } catch { continue; }
                var helps = false;
                for (var s = 0; s < skills.Count; s++)
                {
                    try
                    {
                        if (skills[s].GetHasLaunchRank(dest, size)
                            && skills[s].GetHasTargetRank(dest, size, enemyRank, enemySize))
                        {
                            helps = true;
                            break;
                        }
                    }
                    catch { }
                }
                if (!helps)
                    continue;
                c.ReachReposition = true;
                c.Score = 180f;
            }
        }

        private static List<ActorDataSkill> BlockedReachSkills(ActorInstance performer, ActorInstance enemy)
        {
            var blocked = new List<ActorDataSkill>();
            IReadOnlyList<string> skillIds = null;
            try { skillIds = performer.GetEquippedCombatSkillIds(); } catch { }
            if (skillIds == null || enemy == null)
                return blocked;
            var size = 1;
            try { size = performer.Size; } catch { }
            var enemyRank = 0;
            var enemySize = 1;
            try { enemyRank = enemy.TeamPosition; } catch { return blocked; }
            try { enemySize = enemy.Size; } catch { }

            for (var i = 0; i < skillIds.Count; i++)
            {
                var id = skillIds[i];
                var def = GetSkill(id);
                if (def == null)
                    continue;
                try { if (def.IsItemSkill || def.IsMoveSkill) continue; } catch { }
                try { if (def.m_IsFriendly) continue; } catch { }
                if (LooksLikeHeal(id, null) || IsPassHeal(id))
                    continue;
                try
                {
                    if (!performer.GetIsUnderSkillLimit(def))
                        continue;
                }
                catch { }
                var hitsThem = false;
                for (var dest = 0; dest < 4; dest++)
                {
                    try
                    {
                        if (def.GetHasLaunchRank(dest, size)
                            && def.GetHasTargetRank(dest, size, enemyRank, enemySize))
                        {
                            hitsThem = true;
                            break;
                        }
                    }
                    catch { }
                }
                if (!hitsThem)
                    continue;
                blocked.Add(def);
            }
            return blocked;
        }

        private static List<ActorDataSkill> BlockedHealSkills(ActorInstance performer)
        {
            var blocked = new List<ActorDataSkill>();
            IReadOnlyList<string> skillIds = null;
            try { skillIds = performer.GetEquippedCombatSkillIds(); } catch { }
            if (skillIds == null)
                return blocked;
            var rank = 0;
            try { rank = performer.TeamPosition; } catch { }
            var size = 1;
            try { size = performer.Size; } catch { }

            for (var i = 0; i < skillIds.Count; i++)
            {
                var id = skillIds[i];
                var def = GetSkill(id);
                if (def == null)
                    continue;
                try { if (def.IsItemSkill || def.IsMoveSkill) continue; } catch { }
                if (IsPassHeal(id))
                    continue;
                if (!LooksLikeHeal(id, null) && !PartyKit.DescribeSkill(def).Heals)
                    continue;
                try
                {
                    if (!performer.GetIsUnderSkillLimit(def))
                        continue;
                }
                catch { }
                var fromHere = false;
                try { fromHere = def.GetHasLaunchRank(rank, size); } catch { }
                if (fromHere)
                    continue;
                blocked.Add(def);
            }
            return blocked;
        }

        private static bool IsCursedSkill(ActorInstance performer, string skillId)
        {
            if (performer == null || string.IsNullOrEmpty(skillId))
                return false;
            try
            {
                var inst = performer.GetCombatSkillInstance(skillId);
                if (inst == null)
                    return false;
                var mod = inst.GetActiveSkillModifier(performer.ActorGuid);
                return mod != null && mod.GetIsForceEquip();
            }
            catch
            {
                return false;
            }
        }

        private static JArray ToArray(List<string> values)
        {
            var arr = new JArray();
            if (values == null)
                return arr;
            for (var i = 0; i < values.Count; i++)
                arr.Add(values[i]);
            return arr;
        }

        private static void LogTurn(ActorControllerBase controller, ActorInstance performer, List<JObject> candidates, ChosenAction chosen, string reason, PartyKit party, EnemyFocus focus)
        {
            JArray heroes = new JArray();
            JArray enemies = new JArray();
            try
            {
                var teams = GetTeams(controller);
                if (teams != null)
                {
                    heroes = GameSnapshot.Side(GameSnapshot.TeamActors(teams, BattleTeams.HERO_TEAM_INDEX));
                    enemies = GameSnapshot.Side(GameSnapshot.TeamActors(teams, BattleTeams.ENEMY_TEAM_INDEX));
                }
            }
            catch (Exception ex)
            {
                DecisionLog.Warn("Snapshot failed: " + ex.Message);
            }

            var record = new JObject
            {
                ["actor"] = GameSnapshot.Actor(performer),
                ["heroes"] = heroes,
                ["enemies"] = enemies,
                ["legal"] = new JArray(candidates.ToArray()),
                ["chosen"] = chosen == null ? JValue.CreateNull() : new JObject
                {
                    ["skill"] = chosen.SkillId,
                    ["target"] = chosen.TargetGuid,
                    ["reason"] = chosen.Reason,
                    ["item"] = chosen.IsItem
                },
                ["reason"] = reason,
                ["synergy"] = party != null ? party.ToJson() : null,
                ["focus"] = focus != null ? focus.ToJson() : null
            };

            var summary = chosen == null
                ? $"R? {GameSnapshot.OneLine(performer)}: NO LEGAL ACTION"
                : $"{GameSnapshot.OneLine(performer)}: {chosen.SkillId} -> {chosen.TargetGuid} ({reason})";

            if (!Plugin.LogPreviews.Value)
                record.Remove("legal");

            DecisionLog.Turn(record, summary);
        }

        private static ActorInstance GetPerformer(ActorControllerBase controller)
        {
            try { return controller != null ? GetField<ActorInstance>(controller, "m_PerformerActor") : null; }
            catch { return null; }
        }

        private static BattleTeams GetTeams(ActorControllerBase controller)
        {
            try { return GetField<BattleTeams>(controller, "m_BattleTeams"); }
            catch { return null; }
        }

        private static T GetField<T>(object obj, string name) where T : class
        {
            var field = obj.GetType().GetField(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy);
            if (field == null)
                field = typeof(ActorControllerBase).GetField(name,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            return field != null ? field.GetValue(obj) as T : null;
        }
    }
}
