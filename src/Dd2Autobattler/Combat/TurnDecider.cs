using System;
using System.Collections.Generic;
using Assets.Code.Actor;
using Assets.Code.Actor.ActorController;
using Assets.Code.Combat;
using Assets.Code.Combat.Queries;
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
        public float Score;
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

        public static void EnsureShadow(ActorControllerBase controller)
        {
            var performer = GetPerformer(controller);
            if (performer == null)
                return;
            if (CombatMemory.ShadowActor == performer.ActorGuid)
                return;
            Decide(controller, false);
        }

        public static ChosenAction Decide(ActorControllerBase controller, bool commit)
        {
            var performer = controller != null ? GetPerformer(controller) : null;
            if (performer == null)
                return null;

            var entries = controller.GetValidSkillTargetEntries();
            var teams = GetTeams(controller);
            var livingEnemies = CountLivingEnemies(teams);
            var killableEnemies = CountKillableEnemies(teams);
            var performerGuid = performer.ActorGuid;
            var party = PartyKit.Scan(teams, performerGuid);
            var focus = EnemyFocus.Scan(teams);
            var remaining = ReadRemainingTurns();
            var nextEnemyGuid = FirstEnemyInOrder(remaining, teams);
            var performerBody = GameSnapshot.Describe(performer);
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
                        {
                            SkillPreviewReader.AddSkillTokenAdds(skillDef, preview);
                            ClampPreviewToLivingHits(preview, teams, performer.ActorGuid);
                        }
                        var target = GameSnapshot.Describe(GetActor(teams, targetGuid));
                        if (preview.Ok)
                        {
                            SkillPreviewReader.AddConditionalDotDealt(performer, entry.m_SkillId, preview);
                            SkillPreviewReader.ApplyComboBleedDouble(entry.m_SkillId, target.Combo, preview);
                        }
                        var enemyTarget = IsEnemy(teams, targetGuid);
                        var kind = Classify(entry.m_SkillId, skillDef, preview, enemyTarget);
                        NoteKillFromHp(preview, target, teams);
                        if (target.DiesToDot && livingEnemies > 1)
                            preview.Kills = false;
                        var role = PartyKit.DescribeSkill(skillDef);
                        var tokens = TokenPrices.Evaluate(kind, enemyTarget, preview, target, livingEnemies, party, performerGuid, role, performerBody, nextEnemyGuid);
                        var isItem = ItemPolicy.IsCombatItem(skillDef, entry.m_SkillId, performer);
                        var clearsCorpse = ItemPolicy.ClearsCorpse(entry.m_SkillId, skillDef);
                        var freeItem = isItem && ItemPolicy.IsFreeAction(skillDef);
                        var qty = isItem ? ItemPolicy.RemainingQty(performer, entry.m_SkillId) : 0;
                        var item = isItem
                            ? ItemPolicy.Evaluate(entry.m_SkillId, skillDef, kind, enemyTarget, preview, target, tokens, livingEnemies, qty)
                            : null;
                        var setup = party != null ? party.SetupBonus(role, target, enemyTarget, preview) : 0f;
                        if (TokenPrices.IsEarlySetup(CombatMemory.Round, livingEnemies))
                            setup *= 1.5f;
                        // Corpse-clear skills skip token/setup on a corpse click — Lye scoring.
                        var score = ScoreAction(entry.m_SkillId, kind, enemyTarget, stealthed || target.Stealth, preview, target, livingEnemies, party, focus, clearsCorpse);
                        if (item != null)
                            score = item.Score;
                        else if (!(clearsCorpse && target.Corpse))
                            score += tokens.Score + setup;
                        var focusGuid = enemyTarget ? FocusPayGuid(preview, target) : 0u;
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
                            ClearsCorpse = clearsCorpse,
                            FreeAction = freeItem,
                            ItemQty = qty,
                            Score = score,
                            Stealthed = stealthed || target.Stealth,
                            Focus = focusGuid != 0 ? focus.ScoreOf(focusGuid) : 0f,
                            FocusWhy = focusGuid != 0 ? focus.Why(focusGuid) : (GuardRedirects(preview, target) ? "guard" : null),
                            Cursed = IsCursedSkill(performer, entry.m_SkillId)
                        });
                    }
                }
            }

            ApplyCursePenalty(candidates);
            if (AllyInCrisis(teams) && !HasLegalAllyHeal(candidates))
                ApplyHealReposition(candidates, performer);
            ApplyHarvestHungerGuard(candidates, performer, teams, focus);
            ApplyLibrarianBookVeto(candidates, focus);
            ApplyCorpseSplash(candidates, teams);
            ApplySighLungVeto(candidates, focus);
            ApplyFocusedFaultNotes(candidates, focus);
            ApplyImplicationBlindNotes(candidates, focus);
            ApplyLaterActNotes(candidates, focus, performer);
            ApplyRankWalks(candidates, performer, teams, focus, livingEnemies, performerGuid, party);
            ApplyCorpseReach(candidates, focus, livingEnemies);
            ApplyOnePly(candidates, livingEnemies, performerGuid, remaining, teams, party);
            ApplyComboChipVeto(candidates);
            var partyDoor = PartyHasDeathsDoor(teams);
            ApplyKitSafety(candidates, performerBody, livingEnemies, partyDoor, party, performerGuid);
            ApplyCrisisStabilize(candidates, teams);

            var lastEnemy = FindLastLivingEnemy(candidates);
            var lastGuid = lastEnemy != null && lastEnemy.Actor != null ? lastEnemy.Actor.ActorGuid : 0u;
            var awkward = lastEnemy != null && (lastEnemy.Riposte || lastEnemy.Dodge);
            var allowSetup = livingEnemies <= 1 && awkward && CombatMemory.CanSpendSetup(lastGuid);

            var performerCrisis = performerBody.DeathsDoor || performerBody.HpPct <= 0.20f
                                  || performerBody.DiesToDot
                                  || (performerBody.Hp > 0f && performerBody.NextDot + 0.05f >= performerBody.Hp);
            var partyCrisis = partyDoor || AllyInCrisis(teams);
            var allyRiposte = AllyHasRiposte(teams, performerGuid);
            var allyLow = AllyNeedsCover(teams);
            var picked = PickAction(candidates, livingEnemies, allowSetup, performerGuid, focus, killableEnemies, performerCrisis, partyCrisis, performerBody.Riposte, allyRiposte, allyLow);
            var best = picked == null
                ? null
                : new ChosenAction
                {
                    SkillId = picked.SkillId,
                    TargetGuid = picked.TargetGuid,
                    Reason = ReasonFor(picked, livingEnemies, allowSetup),
                    IsItem = picked.IsItem,
                    Score = picked.Score
                };
            var rows = ToLogRows(candidates, focus);

            if (best == null)
            {
                if (!commit)
                    CombatMemory.NoteShadowPick(performerGuid, null, rows);
                LogTurn(controller, performer, rows, null, "no_legal_action", party, focus, commit);
                return null;
            }

            if (!commit)
            {
                CombatMemory.NoteShadowPick(performerGuid, best, rows);
                LogTurn(controller, performer, rows, best, best.Reason, party, focus, false);
                return best;
            }

            var wasSetup = picked.Kind == SkillKind.Support || picked.Kind == SkillKind.Pass;
            CombatMemory.NoteChosen(lastGuid, wasSetup && allowSetup && !picked.IsItem);
            if (picked.ReachReposition || picked.MustRankWalk)
                CombatMemory.NoteReachWalk(performerGuid);
            if (picked.IsItem)
                CombatMemory.NoteItemUsed(performerGuid);
            if (!picked.IsItem && party != null && party.HeroSpendsCombo(performerGuid))
                CombatMemory.NoteComboSpenderActed(performerGuid);
            // Items (bandage) must not burn the once-per-round skill-heal gate.
            if (!picked.IsItem && !picked.EnemyTarget && picked.Target != null
                && CountsAsCrisisHealSpend(picked.SkillId, picked.Kind)
                && (picked.Target.DeathsDoor || picked.Target.HpPct <= 0.35f))
                CombatMemory.NoteCrisisHeal(picked.TargetGuid);
            if (!picked.IsItem && picked.EnemyTarget && focus != null && focus.IsTaproot(picked.TargetGuid))
                CombatMemory.NoteTaprootHit();
            _pendingTarget = best.TargetGuid;
            LogTurn(controller, performer, rows, best, best.Reason, party, focus, true);
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
            public bool ClearsCorpse;
            public bool FreeAction;
            public int ItemQty;
            public float Score;
            public bool Stealthed;
            public float Focus;
            public string FocusWhy;
            public bool Cursed;
            public bool HealReposition;
            public bool ReachReposition;
            public bool MustRankWalk;
            public string NoteReason;
            public float Ply;
            public bool LeaveChip;
        }

        // Crush on Combo heals the performer. That is still an attack: do not
        // classify an enemy click as Heal just because the preview has a self-heal.
        // Guard redirects the hit. Kill/HP overlay must use the guardian's bar,
        // not the click target's. CSV/preview: m_GuardingActorGuid is the
        // protected actor when you click them (same guid); the guardian is the
        // other HitGuids entry. Living 0 HP (Death Armor) is a kill.
        internal static void NoteKillFromHp(PreviewScore preview, TargetInfo clickTarget, BattleTeams teams)
        {
            if (preview == null || clickTarget == null)
                return;
            if (clickTarget.Corpse || clickTarget.Healthless)
                return;

            var bar = clickTarget;
            var barGuid = clickTarget.Guid;
            if (GuardRedirects(preview, clickTarget))
            {
                // IsKill is the guardian dying, not the protected click target.
                preview.Kills = false;
                barGuid = GuardBarGuid(preview, clickTarget);
                if (barGuid == 0 || barGuid == clickTarget.Guid)
                    return;
                var actor = GetActor(teams, barGuid);
                if (actor == null)
                {
                    if (HitKills(preview, barGuid))
                        preview.Kills = true;
                    return;
                }
                var described = GameSnapshot.Describe(actor);
                if (described == null || described.Corpse || described.Healthless)
                    return;
                bar = described;
            }

            float dmg;
            var guid = bar.Guid != 0 ? bar.Guid : barGuid;
            if (!TryHitDamageOn(preview, guid, out dmg))
                dmg = preview.Damage;
            if (preview.Kills || dmg <= 0f || bar.Corpse || bar.Healthless)
                return;
            if (bar.Hp <= 0.05f || dmg >= bar.Hp)
                preview.Kills = true;
        }

        internal static bool GuardRedirects(PreviewScore preview, TargetInfo clickTarget)
        {
            return preview != null && preview.GuardGuid != 0 && clickTarget != null;
        }

        // Other actor if the preview names them; otherwise the first Hit that
        // is not the protected click target. 0 = redirect with no bar yet.
        internal static uint GuardBarGuid(PreviewScore preview, TargetInfo clickTarget)
        {
            if (preview == null || clickTarget == null || preview.GuardGuid == 0)
                return 0;
            if (preview.GuardGuid != clickTarget.Guid)
                return preview.GuardGuid;
            if (preview.Hits != null)
            {
                for (var i = 0; i < preview.Hits.Count; i++)
                {
                    var g = preview.Hits[i] != null ? preview.Hits[i].Guid : 0u;
                    if (g != 0 && g != clickTarget.Guid)
                        return g;
                }
            }
            if (preview.HitGuids != null)
            {
                for (var i = 0; i < preview.HitGuids.Count; i++)
                {
                    var g = preview.HitGuids[i];
                    if (g != 0 && g != clickTarget.Guid)
                        return g;
                }
            }
            return 0;
        }

        internal static uint FocusPayGuid(PreviewScore preview, TargetInfo target)
        {
            if (target == null)
                return 0;
            var click = target.Actor != null && target.Actor.ActorGuid != 0
                ? target.Actor.ActorGuid
                : target.Guid;
            if (!GuardRedirects(preview, target))
                return click;
            var bar = GuardBarGuid(preview, target);
            if (bar != 0 && bar != click)
                return bar;
            return 0;
        }

        private static bool HitKills(PreviewScore preview, uint guid)
        {
            if (preview == null || preview.Hits == null || guid == 0)
                return false;
            for (var i = 0; i < preview.Hits.Count; i++)
            {
                var hit = preview.Hits[i];
                if (hit != null && hit.Guid == guid && hit.Kills)
                    return true;
            }
            return false;
        }

        internal static bool TryHitDamageOn(PreviewScore preview, uint guid, out float damage)
        {
            damage = 0f;
            if (preview == null || preview.Hits == null || guid == 0)
                return false;
            for (var i = 0; i < preview.Hits.Count; i++)
            {
                var hit = preview.Hits[i];
                if (hit == null || hit.Guid != guid)
                    continue;
                damage = hit.Damage;
                return true;
            }
            return false;
        }

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
            if (!enemyTarget && LooksLikeHeal(skillId, def, preview))
                return SkillKind.Heal;
            if (!string.IsNullOrEmpty(skillId) && skillId.StartsWith("pass_", StringComparison.OrdinalIgnoreCase))
                return SkillKind.Pass;
            if (def != null && def.m_IsFriendly)
                return SkillKind.Support;
            return SkillKind.Attack;
        }

        internal static bool LooksLikeHeal(string skillId, ActorDataSkill def, PreviewScore preview)
        {
            if (preview != null && (preview.HealValid || preview.Heal > 0f))
                return true;
            if (IsPassHeal(skillId) || (!string.IsNullOrEmpty(skillId) && skillId.StartsWith("pass_", StringComparison.OrdinalIgnoreCase)))
                return false;
            // CSV tag "heal" plus an actual HP heal in the effects. Tag alone
            // is not enough: More MORE! is tagged heal and is a taunt.
            if (def != null)
            {
                if (HasSkillTag(def, "heal") && PartyKit.DescribeSkill(def).Heals)
                    return true;
                return false;
            }
            if (string.IsNullOrEmpty(skillId))
                return false;
            return skillId.IndexOf("heal", StringComparison.OrdinalIgnoreCase) >= 0
                   || skillId.IndexOf("medicine", StringComparison.OrdinalIgnoreCase) >= 0
                   || skillId.IndexOf("wyrd", StringComparison.OrdinalIgnoreCase) >= 0
                   || skillId.IndexOf("reconstruction", StringComparison.OrdinalIgnoreCase) >= 0
                   || skillId.IndexOf("cauterize", StringComparison.OrdinalIgnoreCase) >= 0
                   || skillId.IndexOf("absolution", StringComparison.OrdinalIgnoreCase) >= 0
                   || skillId.IndexOf("comfort", StringComparison.OrdinalIgnoreCase) >= 0
                   || skillId.IndexOf("grace", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasSkillTag(ActorDataSkill def, string key)
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
            return CountEnemies(teams, includeHealthless: true);
        }

        // Taproot is living but has no HP bar. Last-kill / finish checks
        // ignore it so a General kill can end the fight.
        private static int CountKillableEnemies(BattleTeams teams)
        {
            return CountEnemies(teams, includeHealthless: false);
        }

        private static int CountEnemies(BattleTeams teams, bool includeHealthless)
        {
            var n = 0;
            foreach (var actor in GameSnapshot.TeamActors(teams, BattleTeams.ENEMY_TEAM_INDEX))
            {
                if (actor == null || !actor.IsLiving)
                    continue;
                var info = GameSnapshot.Describe(actor);
                if (info.Corpse)
                    continue;
                if (!includeHealthless && info.Healthless)
                    continue;
                n++;
            }
            return n;
        }

        private static float ScoreAction(string skillId, SkillKind kind, bool enemyTarget, bool stealthed, PreviewScore preview, TargetInfo target, int livingEnemies, PartyKit party, EnemyFocus focus, bool clearsCorpse = false)
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
                    // Tagged Heal with 0 HP restore this click (Absinthe only
                    // heals below 33%). Do not crisis-score a Dodge drink.
                    if (!RestoresHp(preview))
                    {
                        score -= 20f;
                        break;
                    }
                    if (target != null && target.DeathsDoor)
                        score += 220f;
                    else if (target != null && (target.DiesToDot
                             || (target.Hp > 0f && target.NextDot + 0.05f >= target.Hp)))
                        score += 90f + (1f - hpPct) * 80f;
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
                    {
                        // lep_purge / Lye: clear is the point. Other attacks waste the turn.
                        if (clearsCorpse)
                            score += ItemPolicy.CorpseClearBaseScore(livingEnemies);
                        else
                            score -= 250f;
                    }
                    else if (target != null && target.DiesToDot && livingEnemies > 1)
                        score -= 120f;
                    else
                    {
                        score += enemyTarget ? 10f : -30f;
                        score += preview.Damage;
                        // Finish bonus is for a real hit, not a 0-damage Combo mark.
                        // 0 HP Death Armor still gets the last-bar bump.
                        if (enemyTarget && hpPct >= 0f && preview.Damage > 0f && (target == null || !target.Healthless))
                            score += (1f - hpPct) * 6f;
                        if (lastEnemy && preview.Damage > 0f)
                            score += 30f;
                    }
                    if (enemyTarget && focus != null && target != null && target.Actor != null)
                    {
                        var payGuid = FocusPayGuid(preview, target);
                        var pay = payGuid != 0 ? focus.ScoreOf(payGuid) : 0f;
                        score += FocusPay(skillId, preview, pay);
                        if (focus.HasPriorityTarget && payGuid != 0 && focus.IsAdd(payGuid) && preview.Kills)
                            score -= 25f;
                    }
                    break;
            }

            if (kind == SkillKind.Attack)
            {
                if (stealthed || (target != null && target.Stealth))
                    score -= lastEnemy ? 20f : 200f;
                if (target != null && target.Riposte && !preview.Kills)
                    score -= lastEnemy ? 40f : 90f;
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

        private const float HealBeatsAttackGap = 40f;

        private static ScoredAction PickAction(List<ScoredAction> candidates, int livingEnemies, bool allowSetup, uint performerGuid, EnemyFocus focus, int killableEnemies, bool performerCrisis, bool partyCrisis, bool performerRiposte, bool allyRiposte, bool allyLow)
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
            var peelLegal = false;
            if (focus != null)
            {
                foreach (var c in candidates)
                {
                    if (c.IsItem && c.FreeAction)
                        continue;
                    if (NeedsReachPeel(c, focus) && StripsReachDefense(c))
                        peelLegal = true;
                    if (c.Kind != SkillKind.Attack || !c.EnemyTarget || c.Target == null || c.Target.Actor == null)
                        continue;
                    if (ComboMarkWaste(c))
                        continue;
                    // Blind Gas / Tracking Shot is not a hit. It must not arm
                    // the deferred-boss veto or steal Incision / Firefly.
                    var focusHit = CountsAsDamagingFocusClick(c.SkillId, c.Preview, c.LeaveChip)
                                   || IsTaprootTap(c);
                    if (!focusHit)
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
            ScoredAction bestRiposteSetup = null;
            ScoredAction bestArtilleryBlind = null;
            ScoredAction bestStripCombo = null;
            ScoredAction bestFallDefense = null;
            foreach (var c in candidates)
            {
                if (c.IsItem && c.FreeAction)
                    continue;
                if (IsCrisisHealClick(c.Kind, c.SkillId, c.EnemyTarget, c.Target, c.Preview)
                    && (bestCrisisHeal == null || c.Score > bestCrisisHeal.Score))
                    bestCrisisHeal = c;
                if (IsTankRiposteSetup(c) && c.Score > 0f
                    && (bestRiposteSetup == null || c.Score > bestRiposteSetup.Score))
                    bestRiposteSetup = c;
                if (IsArtilleryBlindClick(c) && c.Score > 0f
                    && (bestArtilleryBlind == null || c.Score > bestArtilleryBlind.Score))
                    bestArtilleryBlind = c;
                if (IsStripComboClick(c) && c.Score > 0f
                    && (bestStripCombo == null || c.Score > bestStripCombo.Score))
                    bestStripCombo = c;
                if (IsFallDefenseClick(c) && c.Score > 0f
                    && (bestFallDefense == null || c.Score > bestFallDefense.Score))
                    bestFallDefense = c;
                var realHit = c.Kind == SkillKind.Attack && c.EnemyTarget && c.Target != null && !c.Target.Corpse
                              && c.Target.Actor != null && c.Target.Actor.IsLiving;
                var hungerGuard = IsHarvestHungerGuard(c.SkillId);
                var splashFocus = HitsFocusTarget(c, focus);
                // wiki.gg/Librarian: do not punch stacks even when he is out of reach.
                if (realHit && focus != null && focus.IsLibrarianStack(c.Target.Actor.ActorGuid))
                    continue;
                if (realHit && !hungerGuard && !splashFocus
                    && ShouldSkipDeferredPunch(
                        mustKillLegal,
                        focus != null && focus.IsDeferred(c.Target.Actor.ActorGuid),
                        LastKillableFinish(c.Preview, c.Target, killableEnemies)))
                    continue;
                // Taunt on the deferred target is forced; a leftover 0-dmg
                // click on the must-kill must not veto it.
                if (realHit && !hungerGuard && !splashFocus && priorityLegal
                    && focus.IsDeferred(c.Target.Actor.ActorGuid) && !c.Target.Taunt)
                    continue;
                if (realHit && !hungerGuard && !splashFocus && priorityLegal
                    && focus.IsAdd(c.Target.Actor.ActorGuid)
                    && !focus.IsPriority(c.Target.Actor.ActorGuid))
                    continue;
                if (realHit && !splashFocus && performerCrisis && livingEnemies > 1 && focus != null
                    && focus.IsAdd(c.Target.Actor.ActorGuid)
                    && !focus.IsPriority(c.Target.Actor.ActorGuid))
                    continue;
                if (realHit && peelLegal && NeedsReachPeel(c, focus) && !StripsReachDefense(c)
                    && (c.Preview == null || !c.Preview.Kills))
                    continue;
                if (realHit && ComboChipWaste(c))
                    continue;
                if (realHit && ComboMarkWaste(c))
                    continue;
                // Tracking Shot on a deferred foot while a Drummer is up is not
                // setup. Wiki: kill the controller; Combo on an add is a wasted turn.
                if (realHit && ComboMarkOnDeferredAdd(
                        focus != null && focus.HasPriorityTarget,
                        focus != null && c.Target.Actor != null
                            && (focus.IsDeferred(c.Target.Actor.ActorGuid)
                                || focus.IsAdd(c.Target.Actor.ActorGuid)),
                        c.SkillId,
                        c.Preview))
                    continue;
                // 0-damage Tracking Shot on the Librarian while Wicked Slice is
                // legal on him (t18: 170 vs 169.5). A mark is not a hit.
                if (realHit && IsZeroDamageMark(c.SkillId, c.Preview)
                    && c.Target.Actor != null
                    && HasDamagingHitOn(candidates, c.Target.Actor.ActorGuid)
                    && focus != null
                    && (focus.IsMustKillFirst(c.Target.Actor.ActorGuid)
                        || focus.IsPriority(c.Target.Actor.ActorGuid)))
                    continue;
                if (realHit && (bestAttack == null || BetterAttack(c, bestAttack, focus)))
                    bestAttack = c;
                if (allowSetup && (c.Kind == SkillKind.Support || c.Kind == SkillKind.Pass)
                    && (bestSetup == null || c.Score > bestSetup.Score))
                    bestSetup = c;
                // After the vetoes so a skipped deferred punch cannot leak
                // through as bestAny when bestAttack is null or negative.
                if (bestAny == null || c.Score > bestAny.Score)
                    bestAny = c;
            }

            if (bestCrisisHeal != null
                && ShouldTakeCrisisHeal(
                    bestCrisisHeal.Target,
                    bestAttack != null ? bestAttack.Preview : null,
                    bestAttack != null ? bestAttack.Target : null,
                    killableEnemies,
                    CombatMemory.CrisisHealThisRound(bestCrisisHeal.TargetGuid)))
                return bestCrisisHeal;

            // Strip Combo / Fall Taunt-Guard before punching Altar or Reach.
            // Same utility gate as Implication Blind — not over a kill/crisis.
            var cultistDefense = PreferCultistDefense(bestStripCombo, bestFallDefense, bestAttack, killableEnemies, partyCrisis);
            if (cultistDefense != null)
                return cultistDefense;

            // Blind loaded Implication before BOOOOOOOM! — not over a kill/crisis/evolve pack.
            if (bestArtilleryBlind != null && !partyCrisis
                && ShouldOpenUtility(bestAttack, killableEnemies, focus))
                return bestArtilleryBlind;

            // MAA Retribution (taunt+riposte) is the team's one Riposte. Take Aim
            // is not this gate — Duelist's Advance plants Riposte on the attack.
            if (ShouldOpenTankRiposte(
                    performerRiposte,
                    allyRiposte,
                    allyLow,
                    livingEnemies,
                    bestRiposteSetup != null,
                    bestAttack != null ? bestAttack.Preview : null,
                    bestAttack != null ? bestAttack.Target : null,
                    killableEnemies,
                    focus))
                return bestRiposteSetup;

            ScoredAction bestCorpseReach = null;
            foreach (var c in candidates)
            {
                if (c.Target == null || !c.Target.Corpse || c.NoteReason != "corpse_reach")
                    continue;
                if (c.IsItem)
                {
                    if (c.Item == null || !c.Item.UseNow)
                        continue;
                    if (!CombatMemory.CanSpendItem(performerGuid, true))
                        continue;
                }
                else if (!c.ClearsCorpse)
                {
                    continue;
                }
                if (bestCorpseReach == null || c.Score > bestCorpseReach.Score)
                    bestCorpseReach = c;
            }
            if (bestCorpseReach != null)
                return bestCorpseReach;

            ScoredAction bestReposition = null;
            foreach (var c in candidates)
            {
                if (!c.HealReposition && !c.ReachReposition)
                    continue;
                if (bestReposition == null || c.Score > bestReposition.Score)
                    bestReposition = c;
            }
            // A legal damaging click (or a real Taproot tap) is playing.
            // A 0-damage Combo mark is not — walk onto a Pistol/Chop rank.
            // MustRankWalk is Undertow / Exemplar rank-4 (not while Altar lives).
            // Reach walks lose to stabilize when the party is in crisis.
            if (bestReposition != null)
            {
                var crisisWalk = partyCrisis && bestReposition.ReachReposition && !bestReposition.MustRankWalk;
                if (!crisisWalk)
                {
                    if (!bestReposition.ReachReposition || bestReposition.MustRankWalk)
                        return bestReposition;
                    if (!PlaysFocusClick(bestAttack, focus))
                        return bestReposition;
                }
            }

            // Vetoes ate the Drummer shot (tagged add, etc.). Restore it:
            // a connecting controller hit always beats Take Aim / Absinthe / Pass.
            if (bestAttack == null)
                bestAttack = BestFocusAttack(candidates, focus);

            // One setup while the last enemy is awkward, then we must swing.
            if (allowSetup && bestSetup != null && bestAttack != null)
                return bestSetup;
            // Blind swing at −36 or worse: Reflection / Withstand already outscore it.
            if (bestAttack != null && bestAttack.Score < 0f && bestAny != null && bestAny.Score > bestAttack.Score)
                return bestAny;
            if (bestAttack != null)
            {
                if (HealBeatsAttack(bestAny.Kind, bestAny.SkillId, bestAny.EnemyTarget, bestAny.Target, bestAny.Preview, bestAny.Score, bestAttack.Score))
                    return bestAny;
                return bestAttack;
            }
            return bestAny;
        }

        // Do not open Retribution / artillery Blind over a real kill, last bar,
        // a Cabin Boy burst window, or a damaging hit on a living Altar must-kill.
        internal static bool ShouldOpenUtility(PreviewScore attackPreview, TargetInfo attackTarget, int killableEnemies)
        {
            return ShouldOpenUtility(attackPreview, attackTarget, killableEnemies, null);
        }

        internal static bool ShouldOpenUtility(
            PreviewScore attackPreview,
            TargetInfo attackTarget,
            int killableEnemies,
            EnemyFocus focus)
        {
            if (focus != null && focus.BurstBeforeEvolve)
                return false;
            if (focus != null && focus.HasLivingAltarMustKill()
                && attackPreview != null && attackPreview.Damage > 0f
                && attackTarget != null && IdHasClass(attackTarget.ClassId, "cultist_altar"))
                return false;
            if (attackPreview != null && attackPreview.Kills)
                return false;
            return !LastKillableFinish(attackPreview, attackTarget, killableEnemies);
        }

        private static bool ShouldOpenUtility(ScoredAction bestAttack, int killableEnemies, EnemyFocus focus)
        {
            return ShouldOpenUtility(
                bestAttack != null ? bestAttack.Preview : null,
                bestAttack != null ? bestAttack.Target : null,
                killableEnemies,
                focus);
        }

        // Exemplar / Reach p1: Combo-strip and Fall Taunt/Guard beat non-kill swings.
        internal static bool ShouldPreferCultistDefense(
            bool hasStripOrFall,
            bool partyCrisis,
            PreviewScore attackPreview,
            TargetInfo attackTarget,
            int killableEnemies)
        {
            if (partyCrisis || !hasStripOrFall)
                return false;
            return ShouldOpenUtility(attackPreview, attackTarget, killableEnemies);
        }

        internal static bool IsStripComboClick(string noteReason)
        {
            return noteReason == "strip_combo";
        }

        internal static bool IsFallDefenseClick(string noteReason)
        {
            return noteReason == "fall_taunt" || noteReason == "fall_guard";
        }

        private static bool IsStripComboClick(ScoredAction c)
        {
            return c != null && IsStripComboClick(c.NoteReason);
        }

        private static bool IsFallDefenseClick(ScoredAction c)
        {
            return c != null && IsFallDefenseClick(c.NoteReason);
        }

        private static ScoredAction PreferCultistDefense(
            ScoredAction bestStripCombo,
            ScoredAction bestFallDefense,
            ScoredAction bestAttack,
            int killableEnemies,
            bool partyCrisis)
        {
            if (!ShouldPreferCultistDefense(
                bestStripCombo != null || bestFallDefense != null,
                partyCrisis,
                bestAttack != null ? bestAttack.Preview : null,
                bestAttack != null ? bestAttack.Target : null,
                killableEnemies))
                return null;
            if (bestStripCombo != null)
                return bestStripCombo;
            return bestFallDefense;
        }

        internal static bool IsSelfRiposteSetup(string skillId, bool enemyTarget, PreviewScore preview, TokenEval tokens)
        {
            if (enemyTarget || string.IsNullOrEmpty(skillId))
                return false;
            if (KitSafety.IdHas(skillId, "retribution"))
                return true;
            if (preview != null && TokenPrices.HasId(preview.ApplyPerformer, "riposte"))
                return true;
            if (preview != null && TokenPrices.HasId(preview.ApplyTarget, "riposte"))
                return true;
            if (tokens != null && TokenPrices.HasId(tokens.Apply, "riposte"))
                return true;
            return false;
        }

        // Retribution / self-taunt that also plants Riposte. Take Aim is self
        // Riposte without Taunt — it must not steal the utility-open slot.
        internal static bool IsTankRiposteSetup(string skillId, bool enemyTarget, PreviewScore preview, TokenEval tokens)
        {
            if (!IsSelfRiposteSetup(skillId, enemyTarget, preview, tokens))
                return false;
            return KitSafety.IsSelfTaunt(skillId);
        }

        private static bool IsTankRiposteSetup(ScoredAction c)
        {
            return c != null && !c.IsItem
                   && IsTankRiposteSetup(c.SkillId, c.EnemyTarget, c.Preview, c.Tokens);
        }

        // One team Riposte. MAA opens it while nobody has the token. A second
        // copy is only for Taunt cover when someone is already low.
        internal static bool ShouldOpenTankRiposte(
            bool performerRiposte,
            bool allyRiposte,
            bool allyLow,
            int livingEnemies,
            bool hasTankSetup,
            PreviewScore attackPreview,
            TargetInfo attackTarget,
            int killableEnemies,
            EnemyFocus focus = null)
        {
            if (performerRiposte || !hasTankSetup || livingEnemies < 2)
                return false;
            if (allyRiposte && !allyLow)
                return false;
            return ShouldOpenUtility(attackPreview, attackTarget, killableEnemies, focus);
        }

        // CSV shared_pillager_artillery: count token after Load Shot enables BOOM.
        internal static bool IsLoadedArtillery(string classId, bool blind, int countTokens, int forcedMissTokens)
        {
            if (blind || countTokens <= 0 || forcedMissTokens > 0)
                return false;
            return KitSafety.IdHas(classId, "pillager_artillery");
        }

        internal static bool IsLoadedArtillery(TargetInfo target)
        {
            if (target == null || target.Corpse)
                return false;
            var count = 0;
            var forcedMiss = 0;
            if (target.Actor != null)
            {
                count = GameSnapshot.CountToken(target.Actor, "count");
                forcedMiss = GameSnapshot.CountToken(target.Actor, "forced_miss");
            }
            return IsLoadedArtillery(target.ClassId, target.Blind, count, forcedMiss);
        }

        internal static bool AppliesBlind(PreviewScore preview, TokenEval tokens)
        {
            if (preview != null && TokenPrices.HasId(preview.ApplyTarget, "blind"))
                return true;
            if (tokens != null && TokenPrices.HasId(tokens.Apply, "blind"))
                return true;
            return false;
        }

        private static bool IsArtilleryBlindClick(ScoredAction c)
        {
            return c != null && c.EnemyTarget && !c.IsItem
                   && AppliesBlind(c.Preview, c.Tokens)
                   && IsLoadedArtillery(c.Target);
        }

        // wiki.gg/Implication: Blind the cannon while loaded so BOOOOOOOM! whiffs.
        // CSV: pillager_artillery_loading → count_plus_1; boom needs performer_has_1_count.
        private static void ApplyImplicationBlindNotes(List<ScoredAction> candidates, EnemyFocus focus)
        {
            if (candidates == null)
                return;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!c.EnemyTarget || c.Target == null || c.IsItem)
                    continue;
                if (!AppliesBlind(c.Preview, c.Tokens) || !IsLoadedArtillery(c.Target))
                    continue;
                c.Score += 55f;
                if (string.IsNullOrEmpty(c.NoteReason))
                    c.NoteReason = "blind_artillery";
            }
        }

        // DD / DoT lethal / ≤15%: never skip for lastKill or a prior bandage.
        internal static bool TargetNeedsUrgentHeal(TargetInfo target)
        {
            if (target == null || target.Corpse)
                return false;
            if (target.DeathsDoor || target.DiesToDot)
                return true;
            if (target.Hp > 0f && target.NextDot + 0.05f >= target.Hp)
                return true;
            return target.HpPct > 0f && target.HpPct <= 0.15f;
        }

        internal static bool ShouldTakeCrisisHeal(TargetInfo healTarget, PreviewScore attackPreview, TargetInfo attackTarget, int killableEnemies, bool alreadyHealed)
        {
            if (healTarget == null || healTarget.Corpse)
                return false;
            var urgent = TargetNeedsUrgentHeal(healTarget);
            var stillDoor = healTarget.DeathsDoor;
            var lastKill = LastKillableFinish(attackPreview, attackTarget, killableEnemies);
            // Finish the last bar only when nobody is about to die to DoT/DD.
            if (lastKill && !urgent)
                return false;
            if (alreadyHealed && !stillDoor && !urgent)
                return false;
            return true;
        }

        internal static bool HealBeatsAttack(SkillKind kind, string skillId, bool enemyTarget, TargetInfo target, PreviewScore preview, float healScore, float attackScore)
        {
            if (enemyTarget || target == null || target.Corpse)
                return false;
            if (kind != SkillKind.Heal && !IsPassHeal(skillId))
                return false;
            if (!RestoresHp(preview))
                return false;
            return healScore >= attackScore + HealBeatsAttackGap;
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
                    ["healthless"] = target.Healthless,
                    ["next_dot"] = target.NextDot,
                    ["next_hot"] = target.NextHot,
                    ["dies_to_dot"] = target.DiesToDot,
                    ["death_armor"] = target.DeathArmor,
                    ["deaths_door"] = target.DeathsDoor,
                    ["stealthed"] = c.Stealthed,
                    ["riposte"] = target.Riposte,
                    ["dodge"] = target.Dodge,
                    ["combo"] = target.Combo,
                    ["stun"] = target.Stun,
                    ["hit"] = c.Preview != null ? c.Preview.HitChance : 1f,
                    ["blocked"] = c.Preview != null && c.Preview.Blocked,
                    ["guard"] = c.Preview != null ? c.Preview.GuardGuid : 0,
                    ["res_bleed"] = c.Preview != null ? c.Preview.ResistBleed : 0f,
                    ["res_blight"] = c.Preview != null ? c.Preview.ResistBlight : 0f,
                    ["res_burn"] = c.Preview != null ? c.Preview.ResistBurn : 0f,
                    ["res_stun"] = c.Preview != null ? c.Preview.ResistStun : 0f,
                    ["apply"] = ToArray(c.Tokens != null ? c.Tokens.Apply : null),
                    ["apply_self"] = ToArray(c.Preview != null ? c.Preview.ApplyPerformer : null),
                    ["remove_self"] = ToArray(c.Preview != null ? c.Preview.RemovePerformer : null),
                    ["consume"] = ToArray(c.Tokens != null ? c.Tokens.Consume : null),
                    ["consume_self"] = ToArray(c.Preview != null ? c.Preview.ConsumePerformer : null),
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
                    ["hit_n"] = c.Preview != null ? c.Preview.HitGuids.Count : 0,
                    ["apply_bleed"] = c.Preview != null ? c.Preview.ApplyBleed : 0f,
                    ["apply_blight"] = c.Preview != null ? c.Preview.ApplyBlight : 0f,
                    ["apply_burn"] = c.Preview != null ? c.Preview.ApplyBurn : 0f,
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
            if (!string.IsNullOrEmpty(picked.NoteReason)) return picked.NoteReason;
            if (picked.MustRankWalk) return "rank_walk";
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
            if (kind == SkillKind.Heal && target != null && target.HpPct <= 0.55f && RestoresHp(preview))
                return "heal_low_ally";
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
            if (kind == SkillKind.Attack && target != null && target.Corpse)
                return picked.ClearsCorpse ? "clear_corpse" : "skip_corpse";
            if (kind == SkillKind.Attack && target != null && target.DiesToDot) return "let_dot_kill";
            if (kind == SkillKind.Attack && (target != null && target.Stealth)) return "skip_stealth";
            if (kind == SkillKind.Attack && target != null && target.Riposte && !preview.Kills) return "skip_riposte";
            if (kind == SkillKind.Attack && preview.HitChance > 0f && preview.HitChance < 0.6f) return "low_hit";
            if (tokens != null && tokens.Reason == "save_combo")
                return "save_combo";
            if (tokens != null && tokens.Reason == "stun_next")
                return "stun_next";
            if (tokens != null && tokens.Reason == "self_riposte")
                return "self_riposte";
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
        // kills beat a 0-damage Combo mark. If a later ally acts before this
        // enemy, do not dump a 15 dmg swing into a 1 HP chip.
        private static void ApplyOnePly(List<ScoredAction> candidates, int livingEnemies, uint performerGuid, List<uint> remaining, BattleTeams teams, PartyKit party)
        {
            if (candidates == null)
                return;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.IsItem)
                    continue;
                var ply = BoardDelta(c);
                var leave = ShouldLeaveChip(c, livingEnemies, performerGuid, remaining, teams, party);
                if (leave)
                {
                    var dmg = c.Preview != null ? c.Preview.Damage : 0f;
                    var hp = c.Target != null ? c.Target.Hp : 0f;
                    var leftover = dmg - hp;
                    ply = LeaveChipPly(leftover);
                    c.LeaveChip = true;
                    c.Score -= 40f;
                }
                c.Ply = ply;
                c.Score += ply;
            }
        }

        internal static float LeaveChipPly(float leftover)
        {
            return 8f - leftover * 1.2f;
        }

        // wiki.gg/Focused_Fault: kill the stalks. A 1 HP Cluster still Gazes.
        internal static bool CanLeaveChip(string classId)
        {
            return !IdHasClass(classId, "eyes_stalk");
        }

        // Death Armor / 0 HP is the execute, not overkill to leave for an ally
        // who will click the guarded Drummer instead.
        internal static bool CanLeaveChip(TargetInfo target)
        {
            if (target != null && (target.DeathArmor || target.DeathsDoor || target.Hp <= 0.05f))
                return false;
            return CanLeaveChip(target != null ? target.ClassId : null);
        }

        // wiki.gg/Focused_Fault killing plan: AoE is for a kill or a DoT that
        // finishes. Chipping two full Clusters splits both (t2 Flashing 159 vs Thrown 152).
        internal static float StalkChipAoEDelta(bool stalksUp, bool enemyTarget, bool kills, int livingHits, float applyDot)
        {
            if (!stalksUp || !enemyTarget || kills)
                return 0f;
            if (livingHits < 2)
                return 0f;
            if (applyDot > 0.05f)
                return 0f;
            return -16f;
        }

        private static bool ShouldLeaveChip(ScoredAction c, int livingEnemies, uint performerGuid, List<uint> remaining, BattleTeams teams, PartyKit party)
        {
            if (c == null || c.Kind != SkillKind.Attack || !c.EnemyTarget || c.Target == null || c.Target.Corpse)
                return false;
            if (!CanLeaveChip(c.Target))
                return false;
            if (livingEnemies <= 1)
                return false;
            if (c.Preview == null || !c.Preview.Kills)
                return false;
            var hp = c.Target.Hp;
            var leftover = c.Preview.Damage - hp;
            if (!TokenPrices.IsChipHp(hp) && leftover < 6f)
                return false;
            var enemyGuid = c.Target.Actor != null ? c.Target.Actor.ActorGuid : 0u;
            return LaterAllyBeforeEnemy(remaining, performerGuid, enemyGuid, teams, party);
        }

        private static List<uint> ReadRemainingTurns()
        {
            var list = new List<uint>();
            try
            {
                var q = QueryTurnOrder.Trigger(false);
                if (q == null || q.m_RemainingTurnOrder == null)
                    return list;
                for (var i = 0; i < q.m_RemainingTurnOrder.Count; i++)
                {
                    var g = q.m_RemainingTurnOrder[i];
                    if (g != 0)
                        list.Add(g);
                }
            }
            catch { }
            return list;
        }

        internal static uint FirstEnemyInOrder(List<uint> remaining, BattleTeams teams)
        {
            if (remaining == null)
                return 0;
            for (var i = 0; i < remaining.Count; i++)
            {
                var g = remaining[i];
                if (g == 0 || !IsEnemy(teams, g))
                    continue;
                var actor = GetActor(teams, g);
                if (actor != null && actor.IsLiving && !GameSnapshot.IsCorpse(actor))
                    return g;
            }
            return 0;
        }

        private static bool LaterAllyBeforeEnemy(List<uint> remaining, uint performerGuid, uint enemyGuid, BattleTeams teams, PartyKit party)
        {
            if (remaining == null || party == null || enemyGuid == 0)
                return false;
            for (var i = 0; i < remaining.Count; i++)
            {
                var g = remaining[i];
                if (g == 0 || g == performerGuid)
                    continue;
                if (g == enemyGuid)
                    return false;
                if (!party.HeroAttacks(g))
                    continue;
                var actor = GetActor(teams, g);
                if (actor != null && actor.IsLiving)
                    return true;
            }
            return false;
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
            if (applyCombo && !kills && TokenPrices.IsChipHp(c.Target.Hp))
                ply -= 24f;
            return ply;
        }

        private static bool ComboChipWaste(ScoredAction c)
        {
            if (c == null || !c.EnemyTarget || c.Target == null || c.Target.Corpse)
                return false;
            if (c.Preview != null && c.Preview.Kills)
                return false;
            if (!TokenPrices.IsChipHp(c.Target.Hp))
                return false;
            return c.Tokens != null && TokenPrices.HasId(c.Tokens.Apply, "combo");
        }

        private static bool ComboMarkWaste(ScoredAction c)
        {
            if (c == null || !c.EnemyTarget)
                return false;
            return ComboMarkWaste(c.SkillId, c.Preview, c.Target);
        }

        // Tracking Shot / Blinding Gas with no damage: wasted on Taproot, on a
        // target that already has Combo, and on 0 HP / Death's Door (the next
        // real hit finishes Death Armor; Combo does nothing).
        internal static bool ComboMarkWaste(string skillId, PreviewScore preview, TargetInfo target)
        {
            if (target == null || target.Corpse)
                return false;
            if (!IsComboOnlyTap(skillId, preview))
                return false;
            var dmg = preview != null ? preview.Damage : 0f;
            if (dmg >= 1f)
                return false;
            if (target.Healthless
                || (target.ClassId != null
                    && target.ClassId.IndexOf("taproot", StringComparison.OrdinalIgnoreCase) >= 0))
                return true;
            if (target.DeathsDoor || target.Hp <= 0.05f)
                return true;
            return target.Combo;
        }

        internal static bool IsZeroDamageMark(string skillId, PreviewScore preview)
        {
            if (!IsComboOnlyTap(skillId, preview))
                return false;
            return preview == null || preview.Damage < 1f;
        }

        // Must-kill / boss focus is for a real hit. Tracking Shot inheriting
        // +138 on a blank Combo mark beat Wicked Slice by 130.
        internal static float FocusPay(string skillId, PreviewScore preview, float focusScore)
        {
            if (focusScore == 0f || IsZeroDamageMark(skillId, preview))
                return 0f;
            return focusScore;
        }

        // Rest is pass_heal. Once-per-round is the skill heal, not a bandage tick.
        internal static bool CountsAsCrisisHealSpend(string skillId, SkillKind kind)
        {
            if (IsPassHeal(skillId))
                return false;
            return kind == SkillKind.Heal;
        }

        // wiki Librarian: do not displace the hero who already punches him.
        // Other must-kills still walk this hero onto a damaging launch rank.
        internal static bool PreserveAllyReach(string enemyClassId)
        {
            return IdHasClass(enemyClassId, "librarian") && !IdHasClass(enemyClassId, "stack");
        }

        // 0-damage Combo marks are not a skill to walk for.
        internal static bool ReachWalkSkill(string skillId)
        {
            return !IsComboOnlyTap(skillId, null);
        }

        // Tracking Shot from rank 0 on the Librarian is not "already playing."
        private static bool PlaysFocusClick(ScoredAction c, EnemyFocus focus)
        {
            if (c == null || c.Target == null || c.Target.Actor == null || focus == null)
                return false;
            var guid = c.Target.Actor.ActorGuid;
            if (!focus.IsMustKillFirst(guid) && !focus.IsPriority(guid))
                return false;
            if (ComboMarkWaste(c) || IsZeroDamageMark(c.SkillId, c.Preview))
                return false;
            if (c.Preview != null && c.Preview.Damage > 0f)
                return true;
            return IsTaprootTap(c);
        }

        // Crush self-heals on Combo. That is not a skill heal; do not walk for it.
        internal static bool IsAllyHealSkill(string skillId, ActorDataSkill def)
        {
            if (string.IsNullOrEmpty(skillId) || IsPassHeal(skillId))
                return false;
            if (def != null)
            {
                try
                {
                    if (def.IsMoveSkill || def.IsItemSkill)
                        return false;
                }
                catch { }
                try
                {
                    if (!def.m_IsFriendly)
                        return false;
                }
                catch { return false; }
                var role = PartyKit.DescribeSkill(def);
                if (role != null && role.Attacks)
                    return false;
                return (role != null && role.Heals) || LooksLikeHeal(skillId, def, null);
            }
            if (skillId.IndexOf("crush", StringComparison.OrdinalIgnoreCase) >= 0
                || skillId.IndexOf("rampart", StringComparison.OrdinalIgnoreCase) >= 0
                || skillId.IndexOf("hold_the_line", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return LooksLikeHeal(skillId, null, null);
        }

        // Last HP bar only. Healthless Taproot is ignored. Death Armor at 0 HP
        // is a kill once NoteKillFromHp has run (hp already 0 still connects).
        internal static bool LastKillableFinish(PreviewScore preview, TargetInfo target, int killableEnemies)
        {
            if (preview == null || target == null || target.Corpse || target.Healthless)
                return false;
            if (killableEnemies > 1)
                return false;
            if (preview.Kills)
                return true;
            return preview.Damage > 0f && (target.Hp <= 0.05f || target.DeathsDoor);
        }

        private static void ApplyKitSafety(List<ScoredAction> candidates, TargetInfo performer, int livingEnemies, bool partyDoor, PartyKit party, uint performerGuid)
        {
            if (candidates == null)
                return;
            var frontOccupied = PartySynergy.FrontOccupiedByOther(party, performerGuid);
            var anyStealth = false;
            for (var i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Stealthed && candidates[i].EnemyTarget)
                    anyStealth = true;
            }
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.IsItem)
                    continue;
                var targetAttacks = false;
                var targetHeals = false;
                if (party != null && c.Target != null && c.Target.Actor != null)
                {
                    var guid = c.Target.Actor.ActorGuid;
                    targetAttacks = party.HeroAttacks(guid);
                    targetHeals = party.HeroHeals(guid);
                }
                string why;
                var ctx = new KitContext
                {
                    Kind = c.Kind,
                    EnemyTarget = c.EnemyTarget,
                    Preview = c.Preview,
                    Target = c.Target,
                    Performer = performer,
                    LivingEnemies = livingEnemies,
                    PartyDoor = partyDoor,
                    FrontOccupied = frontOccupied,
                    AnyEnemyStealth = anyStealth,
                    TargetAttacks = targetAttacks,
                    TargetHeals = targetHeals,
                    Party = party
                };
                var delta = KitSafety.Score(c.SkillId, ctx, out why);
                if (delta == 0f)
                    continue;
                c.Score += delta;
                if (!string.IsNullOrEmpty(why) && string.IsNullOrEmpty(c.NoteReason) && Math.Abs(delta) >= 12f)
                    c.NoteReason = why;
            }
        }

        // Multi-hit previews include every ActorResult, corpses included.
        // Score the HP that actually comes off living enemies.
        internal static float SumLivingHitDamage(PreviewScore preview, Predicate<uint> isLivingEnemy, out int livingHits, out bool kills)
        {
            livingHits = 0;
            kills = false;
            if (preview == null)
                return 0f;
            if (isLivingEnemy == null || preview.Hits == null || preview.Hits.Count == 0)
                return preview.Damage;

            var sum = 0f;
            var paid = 0;
            var max = 0f;
            for (var i = 0; i < preview.Hits.Count; i++)
            {
                var hit = preview.Hits[i];
                if (hit == null || hit.Guid == 0 || !isLivingEnemy(hit.Guid))
                    continue;
                livingHits++;
                kills |= hit.Kills;
                if (hit.Damage <= 0f)
                    continue;
                paid++;
                sum += hit.Damage;
                if (hit.Damage > max)
                    max = hit.Damage;
            }

            if (preview.HitGuids != null)
            {
                for (var i = 0; i < preview.HitGuids.Count; i++)
                {
                    var guid = preview.HitGuids[i];
                    if (guid == 0 || !isLivingEnemy(guid))
                        continue;
                    var known = false;
                    for (var h = 0; h < preview.Hits.Count; h++)
                    {
                        if (preview.Hits[h] != null && preview.Hits[h].Guid == guid)
                        {
                            known = true;
                            break;
                        }
                    }
                    if (!known)
                        livingHits++;
                }
            }

            if (livingHits == 0)
                return preview.Damage;
            if (paid == 0)
                return preview.Damage;
            if (livingHits > paid && max > 0f)
                sum += max * (livingHits - paid);
            return sum;
        }

        private static void ClampPreviewToLivingHits(PreviewScore preview, BattleTeams teams, uint performerGuid)
        {
            if (preview == null || !preview.Ok)
                return;
            int livingHits;
            bool kills;
            var live = SumLivingHitDamage(preview, guid => IsLivingEnemyHit(teams, guid, performerGuid),
                out livingHits, out kills);
            if (livingHits <= 0)
                return;
            preview.Damage = live;
            if (!kills && preview.Hits != null)
            {
                for (var i = 0; i < preview.Hits.Count; i++)
                {
                    var hit = preview.Hits[i];
                    if (hit == null || hit.Damage <= 0f || !IsLivingEnemyHit(teams, hit.Guid, performerGuid))
                        continue;
                    var bar = GameSnapshot.Describe(GetActor(teams, hit.Guid));
                    if (bar == null || bar.Corpse || bar.Healthless || bar.Hp <= 0f)
                        continue;
                    if (hit.Damage >= bar.Hp)
                    {
                        kills = true;
                        break;
                    }
                }
            }
            preview.Kills = kills;
        }

        private static bool IsLivingEnemyHit(BattleTeams teams, uint guid, uint performerGuid)
        {
            if (guid == 0 || guid == performerGuid)
                return false;
            if (!IsEnemy(teams, guid))
                return false;
            var actor = GetActor(teams, guid);
            if (actor == null)
                return false;
            try
            {
                if (GameSnapshot.IsCorpse(actor))
                    return false;
            }
            catch { }
            try { return actor.IsLiving; }
            catch { return false; }
        }

        // Flashing Daggers (m_IsMultiHit) can HitGuids a corpse + a live enemy
        // while another click of the same skill, or Pick/Thrown, hits only living.
        private static void ApplyCorpseSplash(List<ScoredAction> candidates, BattleTeams teams)
        {
            if (candidates == null)
                return;
            var cleanLiving = false;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Kind != SkillKind.Attack || !c.EnemyTarget || c.IsItem)
                    continue;
                int live;
                int dead;
                CountSplashHits(c, teams, out live, out dead);
                if (live > 0 && dead == 0)
                    cleanLiving = true;
            }
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Kind != SkillKind.Attack || !c.EnemyTarget)
                    continue;
                int live;
                int dead;
                CountSplashHits(c, teams, out live, out dead);
                var bestLive = live;
                for (var j = 0; j < candidates.Count; j++)
                {
                    var o = candidates[j];
                    if (o.Kind != SkillKind.Attack
                        || !string.Equals(o.SkillId, c.SkillId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    int ol;
                    int od;
                    CountSplashHits(o, teams, out ol, out od);
                    if (ol > bestLive)
                        bestLive = ol;
                }
                var delta = CorpseSplashDelta(live, dead, bestLive, cleanLiving);
                if (delta == 0f)
                    continue;
                c.Score += delta;
                if (string.IsNullOrEmpty(c.NoteReason))
                    c.NoteReason = "corpse_splash";
            }
        }

        internal static float CorpseSplashDelta(int livingHits, int corpseHits, int bestLiveSameSkill, bool cleanLivingHitExists)
        {
            if (corpseHits <= 0)
                return 0f;
            if (bestLiveSameSkill > livingHits)
                return -80f;
            // Two living hits are the reward. Do not veto that cone because a
            // corpse is also in it — Pick would then beat a real AoE.
            if (livingHits >= 2)
                return 0f;
            if (cleanLivingHitExists)
                return -50f;
            return 0f;
        }

        // Attack into a corpse is −250 unless the skill clears corpses (Purge / Lye).
        internal static float CorpseTargetScore(bool clearsCorpse, int livingEnemies)
        {
            return clearsCorpse
                ? ItemPolicy.CorpseClearBaseScore(livingEnemies)
                : -250f;
        }

        private static void CountSplashHits(ScoredAction c, BattleTeams teams, out int living, out int corpses)
        {
            living = 0;
            corpses = 0;
            if (c == null)
                return;
            var seen = new HashSet<uint>();
            var hits = c.Preview != null ? c.Preview.HitGuids : null;
            if (hits != null)
            {
                for (var i = 0; i < hits.Count; i++)
                {
                    var guid = hits[i];
                    if (guid == 0 || !seen.Add(guid))
                        continue;
                    CountOneHit(GetActor(teams, guid), ref living, ref corpses);
                }
            }
            if (c.Target != null && c.Target.Actor != null && seen.Add(c.Target.Guid != 0 ? c.Target.Guid : c.Target.Actor.ActorGuid))
            {
                if (c.Target.Corpse)
                    corpses++;
                else if (c.EnemyTarget && !c.Target.Healthless)
                    living++;
            }
        }

        private static void CountOneHit(ActorInstance actor, ref int living, ref int corpses)
        {
            if (actor == null)
                return;
            try
            {
                if (GameSnapshot.IsCorpse(actor))
                {
                    corpses++;
                    return;
                }
            }
            catch { }
            try
            {
                if (actor.IsLiving)
                    living++;
            }
            catch { }
        }

        // Front corpse in a lower rank than every living enemy clogs reach.
        // Lye / Purge then, especially if this hero has no damaging hit on the must-kill / last.
        private static void ApplyCorpseReach(List<ScoredAction> candidates, EnemyFocus focus, int livingEnemies)
        {
            if (candidates == null)
                return;
            var living = new int[4];
            var livingN = 0;
            var clog = false;
            var noHit = livingEnemies <= 1 && !HasDamagingHitOn(candidates, 0);
            if (!noHit && focus != null && focus.HasMustKillFirst)
                noHit = !HasDamagingMustKillHit(candidates, focus);
            for (var i = 0; i < candidates.Count; i++)
            {
                var t = candidates[i].Target;
                if (t == null || !candidates[i].EnemyTarget)
                    continue;
                if (t.Corpse)
                    continue;
                if (livingN < living.Length)
                    living[livingN++] = t.Rank;
            }
            var livingRanks = new int[livingN];
            for (var i = 0; i < livingN; i++)
                livingRanks[i] = living[i];
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Target == null || !c.Target.Corpse)
                    continue;
                if (PartySynergy.CorpseClogsRanks(c.Target.Rank, livingRanks))
                    clog = true;
            }
            if (!clog && !noHit)
                return;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Target == null || !c.Target.Corpse)
                    continue;
                if (!clog && !PartySynergy.CorpseClogsRanks(c.Target.Rank, livingRanks) && !noHit)
                    continue;
                var clearItem = c.IsItem && c.Item != null
                    && c.Item.Reason != null
                    && c.Item.Reason.IndexOf("clear_corpse", StringComparison.OrdinalIgnoreCase) >= 0;
                var clearSkill = !c.IsItem && c.ClearsCorpse;
                if (!clearItem && !clearSkill)
                    continue;
                if (clearItem)
                {
                    c.Item.UseNow = true;
                    c.Item.Crisis = true;
                    if (c.Item.Score < 40f)
                        c.Item.Score = 40f;
                    c.Score = c.Item.Score;
                }
                else if (c.Score < 40f)
                {
                    c.Score = 40f;
                }
                c.NoteReason = "corpse_reach";
            }
        }

        // Tracking Shot (and any 0-kill Combo apply) on a 1 HP chip is a wasted turn.
        private static void ApplyComboChipVeto(List<ScoredAction> candidates)
        {
            if (candidates == null)
                return;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!ComboChipWaste(c))
                    continue;
                c.Score -= 80f;
                if (string.IsNullOrEmpty(c.NoteReason))
                    c.NoteReason = "wasted_combo";
            }
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
                if (stalksUp && c.EnemyTarget)
                {
                    var hits = 0;
                    var dot = 0f;
                    var kills = false;
                    if (c.Preview != null)
                    {
                        kills = c.Preview.Kills;
                        if (c.Preview.HitGuids != null)
                            hits = c.Preview.HitGuids.Count;
                        if (hits == 0 && c.Preview.Damage > 0f)
                            hits = 1;
                        dot = c.Preview.ApplyBleed + c.Preview.ApplyBlight + c.Preview.ApplyBurn;
                    }
                    var aoe = StalkChipAoEDelta(true, true, kills, hits, dot);
                    if (aoe != 0f)
                    {
                        c.Score += aoe;
                        if (string.IsNullOrEmpty(c.NoteReason))
                            c.NoteReason = "stalk_chip_aoe";
                    }
                }
            }
        }

        // wiki.gg/Exemplar: The Fall needs Combo on a hero; Holy Water / strip
        // Combo to stop Worship. Rank-4 Taunt skips The Fall. Guard a Combo
        // ally so Fall hits the tank (Worship only if that tank also has Combo).
        // wiki.gg/Ravenous_Reach p1 Setback Combo-strip; p2 Dodge / p3 Riposte
        // peel before you swing. wiki.gg/Body_of_Work p2 Covetous Glance steals
        // 4+ positives; Guard the Contempt mark (Haymaker); Weak/Block blunt it.
        private static void ApplyLaterActNotes(List<ScoredAction> candidates, EnemyFocus focus, ActorInstance performer)
        {
            if (candidates == null || focus == null)
                return;
            var exemplar = focus.ExemplarUp;
            var reachP1 = false;
            var bodyP2 = false;
            var bodyP3 = false;
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var id = focus.Enemies[i].ClassId;
                if (IdHasClass(id, "boss_arms_phase1"))
                    reachP1 = true;
                else if (IdHasClass(id, "boss_body_phase2"))
                    bodyP2 = true;
                else if (IdHasClass(id, "boss_body_phase3") || IdHasClass(id, "boss_body_cherub")
                         || IdHasClass(id, "boss_body_failure"))
                    bodyP3 = true;
            }

            var performerBody = GameSnapshot.Describe(performer);
            var performerRank = performerBody != null ? performerBody.Rank : 0;
            var performerCombo = performerBody != null && performerBody.Combo;

            var punishCombo = exemplar || reachP1;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (punishCombo && !c.EnemyTarget && c.Target != null && c.Target.Combo
                    && StripsCombo(c))
                {
                    c.Score += 45f;
                    c.NoteReason = "strip_combo";
                    if (c.Item != null)
                    {
                        c.Item.UseNow = true;
                        if (c.Item.Score < 28f)
                            c.Item.Score = 28f;
                    }
                }
                if (exemplar && !c.EnemyTarget)
                {
                    var taunt = AppliesTaunt(c);
                    var skip = ExemplarTauntSkip(true, taunt, performerRank);
                    if (skip > 0f)
                    {
                        c.Score += skip;
                        c.NoteReason = "fall_taunt";
                    }
                    var guard = AppliesGuard(c);
                    var redirect = ExemplarGuardCombo(true, guard,
                        c.Target != null && c.Target.Combo, performerCombo);
                    if (redirect > 0f)
                    {
                        c.Score += redirect;
                        if (string.IsNullOrEmpty(c.NoteReason))
                            c.NoteReason = "fall_guard";
                    }
                }
                if ((bodyP2 || bodyP3) && !c.EnemyTarget && c.Target != null
                    && c.Target.PositiveTokens >= (bodyP2 ? 3 : 2) && c.Tokens != null
                    && (TokenPrices.HasId(c.Tokens.Apply, "strength")
                        || TokenPrices.HasId(c.Tokens.Apply, "block")
                        || TokenPrices.HasId(c.Tokens.Apply, "dodge")
                        || TokenPrices.HasId(c.Tokens.Apply, "riposte")
                        || TokenPrices.HasId(c.Tokens.Apply, "crit")))
                    c.Score -= 25f;
                if (bodyP2 && !c.EnemyTarget && c.Target != null && c.Target.TorsoTarget)
                {
                    var guard = HaymakerGuardBonus(true, true, AppliesGuard(c));
                    var heal = HaymakerHealBonus(true, true, c.Kind == SkillKind.Heal);
                    if (guard > 0f)
                    {
                        c.Score += guard;
                        c.NoteReason = "haymaker_guard";
                    }
                    else if (heal > 0f)
                        c.Score += heal;
                }
                if (bodyP2 && c.EnemyTarget && c.Target != null && c.Target.Actor != null)
                {
                    string classId = null;
                    try { classId = c.Target.Actor.ActorDataClass != null ? c.Target.Actor.ActorDataClass.GetKey() : null; } catch { }
                    var onBody = IdHasClass(classId, "boss_body_phase2");
                    var blunt = HaymakerBluntBonus(true, onBody,
                        TokenPrices.HasId(c.Tokens != null ? c.Tokens.Apply : null, "weak"),
                        TokenPrices.HasId(c.Tokens != null ? c.Tokens.Apply : null, "block"));
                    if (blunt > 0f)
                    {
                        c.Score += blunt;
                        if (string.IsNullOrEmpty(c.NoteReason))
                            c.NoteReason = "haymaker_blunt";
                    }
                }
                if (c.EnemyTarget && c.Target != null && !c.Target.Corpse && StripsDefense(c, c.Target))
                {
                    c.Score += PeelBonus(c.Target);
                    if (NeedsReachPeel(c, focus) && StripsReachDefense(c))
                    {
                        c.Score += 20f;
                        c.NoteReason = "reach_peel";
                    }
                    else if (string.IsNullOrEmpty(c.NoteReason))
                        c.NoteReason = "peel";
                }
            }
        }

        private static bool StripsCombo(ScoredAction c)
        {
            if (c == null)
                return false;
            if (c.Tokens != null
                && (TokenPrices.HasId(c.Tokens.Remove, "combo")
                    || TokenPrices.HasId(c.Tokens.Consume, "combo")))
                return true;
            return c.Item != null && !string.IsNullOrEmpty(c.Item.Reason)
                   && c.Item.Reason.IndexOf("combo", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // CSV launch/target ranks are 1-4; ActorInstance.TeamPosition is 0-3.
        internal static bool RankIsFrontTwo(int rank)
        {
            return rank <= 1;
        }

        internal static bool RankIsBack(int rank)
        {
            return rank >= 3;
        }

        internal static bool ShouldWalkOffUndertow(bool handUp, bool callOfTheDeep, int rank, bool killsHand)
        {
            return handUp && callOfTheDeep && RankIsFrontTwo(rank) && !killsHand;
        }

        // wiki.gg/Exemplar: kill Altar first (denies Pillar). Walking to rank 4
        // for The Fall spends the party's reach-walk and leaves a 20% Altar up.
        internal static bool ShouldFallWalk(
            bool exemplarUp,
            bool fallLive,
            bool alreadyBack,
            bool hasTauntFromBack,
            bool comboStripLegal,
            bool altarMustKill)
        {
            if (!exemplarUp || !fallLive || alreadyBack || !hasTauntFromBack || comboStripLegal)
                return false;
            return !altarMustKill;
        }

        // Skip a deferred punch only when this hero already has a damaging
        // must-kill click. Blind Gas / a mark is not that click — walk or
        // take the real swing.
        internal static bool ShouldSkipDeferredPunch(
            bool damagingMustKillLegal,
            bool targetDeferred,
            bool lastKillableFinish)
        {
            return damagingMustKillLegal && targetDeferred && !lastKillableFinish;
        }

        // HP (or a Taproot tap). Combo-only taps do not count.
        internal static bool CountsAsDamagingFocusClick(string skillId, PreviewScore preview, bool leaveChip)
        {
            if (leaveChip)
                return false;
            if (IsComboOnlyTap(skillId, preview) || IsZeroDamageMark(skillId, preview))
                return false;
            if (preview != null && preview.GuardGuid != 0)
                return false;
            return preview != null && preview.Damage > 0f;
        }

        // Blind Gas / Tracking Shot on a deferred add while a controller is up.
        internal static bool ComboMarkOnDeferredAdd(
            bool hasPriorityTarget,
            bool targetDeferredOrAdd,
            string skillId,
            PreviewScore preview)
        {
            if (!hasPriorityTarget || !targetDeferredOrAdd)
                return false;
            return IsZeroDamageMark(skillId, preview);
        }

        // t1 Ingress: Poison Dart 183 / 1.5 dmg vs Thrown Dagger 147 / 4 dmg.
        // A DoT open is not finishing Pillar bait.
        internal static bool AltarBurstBeats(
            float damage,
            float score,
            float otherDamage,
            float otherScore,
            bool diesToDot)
        {
            if (diesToDot)
                return score > otherScore;
            if (Math.Abs(damage - otherDamage) > 0.5f)
                return damage > otherDamage;
            return score > otherScore;
        }

        private static bool BetterAttack(ScoredAction c, ScoredAction best, EnemyFocus focus)
        {
            if (best == null)
                return true;
            if (focus != null && focus.HasLivingAltarMustKill()
                && IsAltarMustKillClick(c, focus) && IsAltarMustKillClick(best, focus))
            {
                var dies = focus.AltarMustKillDiesToDot();
                var cKills = c.Preview != null && c.Preview.Kills;
                var bKills = best.Preview != null && best.Preview.Kills;
                if (cKills != bKills)
                    return cKills;
                var cDmg = c.Preview != null ? c.Preview.Damage : 0f;
                var bDmg = best.Preview != null ? best.Preview.Damage : 0f;
                if (Math.Abs(cDmg - bDmg) > 0.5f)
                    return AltarBurstBeats(cDmg, c.Score, bDmg, best.Score, dies);
            }
            return c.Score > best.Score;
        }

        private static bool IsAltarMustKillClick(ScoredAction c, EnemyFocus focus)
        {
            if (c == null || c.Target == null || focus == null)
                return false;
            if (c.Target.Actor != null && focus.IsMustKillFirst(c.Target.Actor.ActorGuid)
                && IdHasClass(c.Target.ClassId, "cultist_altar"))
                return true;
            return IdHasClass(c.Target.ClassId, "cultist_altar") && focus.HasLivingAltarMustKill();
        }

        internal static float ExemplarTauntSkip(bool exemplar, bool appliesTaunt, int performerRank)
        {
            if (!exemplar || !appliesTaunt || !RankIsBack(performerRank))
                return 0f;
            return 50f;
        }

        internal static float ExemplarGuardCombo(bool exemplar, bool appliesGuard, bool targetCombo, bool performerCombo)
        {
            if (!exemplar || !appliesGuard || !targetCombo || performerCombo)
                return 0f;
            return 40f;
        }

        internal static float HaymakerGuardBonus(bool bodyP2, bool torsoTarget, bool appliesGuard)
        {
            if (!bodyP2 || !torsoTarget || !appliesGuard)
                return 0f;
            return 50f;
        }

        internal static float HaymakerHealBonus(bool bodyP2, bool torsoTarget, bool isHeal)
        {
            if (!bodyP2 || !torsoTarget || !isHeal)
                return 0f;
            return 18f;
        }

        internal static float HaymakerBluntBonus(bool bodyP2, bool enemyBody, bool appliesWeak, bool appliesBlock)
        {
            if (!bodyP2 || !enemyBody || (!appliesWeak && !appliesBlock))
                return 0f;
            return 22f;
        }

        internal static float PeelBonus(TargetInfo target)
        {
            if (target == null)
                return 0f;
            var n = 0f;
            if (target.Riposte)
                n += 16f;
            if (target.Dodge)
                n += 12f;
            if (target.Stealth)
                n += 12f;
            if (target.BlockCount >= 2)
                n += 10f;
            else if (target.BlockCount > 0)
                n += 6f;
            return n;
        }

        private static bool AppliesTaunt(ScoredAction c)
        {
            return c != null && c.Tokens != null && TokenPrices.HasId(c.Tokens.Apply, "taunt");
        }

        private static bool AppliesGuard(ScoredAction c)
        {
            return c != null && c.Tokens != null && TokenPrices.HasId(c.Tokens.Apply, "guard");
        }

        private static bool NeedsReachPeel(ScoredAction c, EnemyFocus focus)
        {
            if (c == null || c.Target == null || focus == null)
                return false;
            return (focus.ReachPhase2 && c.Target.Dodge) || (focus.ReachPhase3 && c.Target.Riposte);
        }

        private static bool StripsReachDefense(ScoredAction c)
        {
            if (c == null || c.Target == null)
                return false;
            if (c.Target.Dodge && TokenHas(c, "dodge"))
                return true;
            if (c.Target.Riposte && TokenHas(c, "riposte"))
                return true;
            return false;
        }

        private static bool StripsDefense(ScoredAction c, TargetInfo target)
        {
            if (c == null || target == null)
                return false;
            if (target.Dodge && TokenHas(c, "dodge"))
                return true;
            if (target.Riposte && TokenHas(c, "riposte"))
                return true;
            if (target.Stealth && TokenHas(c, "stealth"))
                return true;
            if (target.BlockCount > 0 && TokenHas(c, "block"))
                return true;
            return false;
        }

        private static bool TokenHas(ScoredAction c, string key)
        {
            if (c == null || c.Tokens == null)
                return false;
            return TokenPrices.HasId(c.Tokens.Remove, key) || TokenPrices.HasId(c.Tokens.Consume, key);
        }

        private static void ApplyRankWalks(List<ScoredAction> candidates, ActorInstance performer, BattleTeams teams, EnemyFocus focus, int livingEnemies, uint performerGuid, PartyKit party)
        {
            if (candidates == null || performer == null)
                return;
            if (CombatMemory.ReachWalkedThisRound(performerGuid))
                return;

            var body = GameSnapshot.Describe(performer);
            var rank = body != null ? body.Rank : 0;
            var marked = body != null && body.CallOfTheDeep;
            var killsHand = HasKillOnMustKill(candidates, focus);

            if (ShouldWalkOffUndertow(focus != null && focus.LeviathanHandUp, marked, rank, killsHand))
            {
                ApplyWalkBack(candidates, performer, "undertow_walk");
                if (HasRankWalk(candidates))
                    return;
            }

            if (ShouldFallWalk(
                    focus != null && focus.ExemplarUp,
                    FallIsLive(teams),
                    RankIsBack(rank),
                    HasTauntLaunchFromBack(performer),
                    HasComboStripLegal(candidates),
                    focus != null && focus.HasLivingAltarMustKill()))
            {
                ApplyWalkBack(candidates, performer, "fall_rank");
                if (HasRankWalk(candidates))
                    return;
            }

            if (CombatMemory.PartyReachWalkedThisRound())
                return;

            var reachTarget = ReachWalkTarget(candidates, performer, teams, focus, livingEnemies, performerGuid, party);
            if (reachTarget != null)
            {
                var crisis = PartyHasDeathsDoor(teams) || AllyInCrisis(teams);
                ApplyReachReposition(candidates, performer, reachTarget, party, crisis);
            }
        }

        private static void ApplyWalkBack(List<ScoredAction> candidates, ActorInstance performer, string reason)
        {
            if (candidates == null || performer == null)
                return;
            var current = 0;
            try { current = performer.TeamPosition; } catch { return; }
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Kind != SkillKind.Move || c.Target == null || c.Target.Actor == null)
                    continue;
                var dest = 0;
                try { dest = c.Target.Actor.TeamPosition; } catch { continue; }
                if (dest <= current)
                    continue;
                c.ReachReposition = true;
                c.MustRankWalk = true;
                c.NoteReason = reason;
                c.Score = dest >= 3 ? 195f : dest >= 2 ? 188f : 170f;
            }
        }

        private static bool HasRankWalk(List<ScoredAction> candidates)
        {
            if (candidates == null)
                return false;
            for (var i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].MustRankWalk)
                    return true;
            }
            return false;
        }

        private static bool HasKillOnMustKill(List<ScoredAction> candidates, EnemyFocus focus)
        {
            if (candidates == null || focus == null)
                return false;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Kind != SkillKind.Attack || !c.EnemyTarget || c.Target == null || c.Target.Corpse)
                    continue;
                if (c.Target.Actor == null || c.Preview == null || !c.Preview.Kills)
                    continue;
                if (focus.IsMustKillFirst(c.Target.Actor.ActorGuid))
                    return true;
            }
            return false;
        }

        private static bool FallIsLive(BattleTeams teams)
        {
            if (teams == null)
                return false;
            foreach (var hero in GameSnapshot.TeamActors(teams, BattleTeams.HERO_TEAM_INDEX))
            {
                if (hero == null || !hero.IsLiving || GameSnapshot.IsCorpse(hero))
                    continue;
                var info = GameSnapshot.Describe(hero);
                if (info.Combo && info.Rank <= 2)
                    return true;
            }
            return false;
        }

        private static bool HasComboStripLegal(List<ScoredAction> candidates)
        {
            if (candidates == null)
                return false;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!c.EnemyTarget && c.Target != null && c.Target.Combo && StripsCombo(c))
                    return true;
            }
            return false;
        }

        private static bool HasTauntLaunchFromBack(ActorInstance performer)
        {
            if (performer == null)
                return false;
            IReadOnlyList<string> skillIds = null;
            try { skillIds = performer.GetEquippedCombatSkillIds(); } catch { }
            if (skillIds == null)
                return false;
            var size = 1;
            try { size = performer.Size; } catch { }
            for (var i = 0; i < skillIds.Count; i++)
            {
                var id = skillIds[i];
                if (IsHarvestHungerGuard(id))
                    continue;
                var def = GetSkill(id);
                if (def == null)
                    continue;
                try { if (def.IsItemSkill || def.IsMoveSkill) continue; } catch { }
                var role = PartyKit.DescribeSkill(def);
                if (role == null || !role.AppliesTaunt)
                    continue;
                try
                {
                    if (def.GetHasLaunchRank(3, size))
                        return true;
                }
                catch { }
            }
            return false;
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
                {
                    c.Score -= 50f;
                    // Blind hunger eats are forced legally but must not beat Solemnity.
                    if (GameSnapshot.Describe(performer).Blind)
                        c.Score -= 40f;
                }
                else if (allyHungry)
                    c.Score += 80f;
            }
        }

        private const float CoverHpPct = 0.45f;

        private static bool AllyHasRiposte(BattleTeams teams, uint performerGuid)
        {
            if (teams == null)
                return false;
            foreach (var hero in GameSnapshot.TeamActors(teams, BattleTeams.HERO_TEAM_INDEX))
            {
                if (hero == null || !hero.IsLiving || GameSnapshot.IsCorpse(hero))
                    continue;
                if (hero.ActorGuid == performerGuid)
                    continue;
                if (GameSnapshot.Describe(hero).Riposte)
                    return true;
            }
            return false;
        }

        private static bool AllyNeedsCover(BattleTeams teams)
        {
            if (teams == null)
                return false;
            foreach (var hero in GameSnapshot.TeamActors(teams, BattleTeams.HERO_TEAM_INDEX))
            {
                if (hero == null || !hero.IsLiving || GameSnapshot.IsCorpse(hero))
                    continue;
                var body = GameSnapshot.Describe(hero);
                if (body.DeathsDoor)
                    return true;
                if (body.HpPct <= CoverHpPct && !KitSafety.WantsToStayLow(body))
                    return true;
            }
            return false;
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
                if (body.DeathsDoor)
                    return true;
                if (body.HpPct <= 0.35f && !KitSafety.WantsToStayLow(body))
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

        // Preview Heal is the HP restore this click, not the skill tag.
        // Absinthe is tagged Heal but only restores when HP < 33%.
        internal static bool RestoresHp(PreviewScore preview)
        {
            return preview != null && preview.Heal > 0.05f;
        }

        internal static bool IsCrisisHealClick(SkillKind kind, string skillId, bool enemyTarget, TargetInfo target, PreviewScore preview)
        {
            if (enemyTarget || target == null || target.Corpse)
                return false;
            if (kind != SkillKind.Heal && !IsPassHeal(skillId))
                return false;
            if (!RestoresHp(preview))
                return false;
            if (TargetNeedsUrgentHeal(target))
                return true;
            return target.HpPct <= 0.35f && !KitSafety.WantsToStayLow(target);
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
            var passHeal = HasLegalPassHeal(candidates);
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Kind == SkillKind.Support && !c.IsItem && (door || crisis))
                    c.Score -= 80f;
                // Laudanum while Rest / BM is legal — stabilize HP first.
                if (c.IsItem && c.Item != null && (door || crisis)
                    && c.Item.Reason != null
                    && c.Item.Reason.IndexOf("stress", StringComparison.OrdinalIgnoreCase) >= 0
                    && (skillHeal || passHeal))
                {
                    c.Item.UseNow = false;
                    c.Score -= 100f;
                    continue;
                }
                if (!c.IsItem || c.Target == null || c.EnemyTarget || c.Target.Corpse)
                    continue;
                if (c.Preview == null || c.Preview.Heal <= 0f)
                    continue;
                if (!c.Target.DeathsDoor && (c.Target.HpPct > 0.30f || KitSafety.WantsToStayLow(c.Target)))
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

        private static bool HasLegalPassHeal(List<ScoredAction> candidates)
        {
            if (candidates == null)
                return false;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.IsItem)
                    continue;
                if (!IsPassHeal(c.SkillId) || c.EnemyTarget || c.Target == null || c.Target.Corpse)
                    continue;
                if (RestoresHp(c.Preview))
                    return true;
            }
            return false;
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
                if (IsCrisisHealClick(c.Kind, c.SkillId, c.EnemyTarget, c.Target, c.Preview))
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
                if (c.Kind == SkillKind.Heal && !c.EnemyTarget && c.Target != null && !c.Target.Corpse
                    && RestoresHp(c.Preview))
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
                if (IsTaprootTap(c))
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
                if (!focus.IsMustKillFirst(c.Target.Actor.ActorGuid))
                    continue;
                if (c.Preview != null && c.Preview.Damage > 0f)
                    return true;
                if (IsTaprootTap(c))
                    return true;
            }
            return false;
        }

        // Taproot is healthless so QuerySkillPreview reports 0 damage. A connecting
        // attack still retracts a vine (wiki: "Used when Hit").
        private static bool IsTaprootTap(ScoredAction c)
        {
            if (c == null || c.Kind != SkillKind.Attack || !c.EnemyTarget || c.Target == null)
                return false;
            if (!c.Target.Healthless && (c.Target.ClassId == null
                || c.Target.ClassId.IndexOf("taproot", StringComparison.OrdinalIgnoreCase) < 0))
                return false;
            if (IsComboOnlyTap(c.SkillId, c.Preview))
                return false;
            return c.Preview == null || c.Preview.HitChance > 0f;
        }

        internal static bool IsComboOnlyTap(string skillId, PreviewScore preview)
        {
            if (KitSafety.IdHas(skillId, "tracking_shot") || KitSafety.IdHas(skillId, "blinding_gas"))
                return true;
            var dmg = preview != null ? preview.Damage : 0f;
            return dmg < 1f && preview != null && TokenPrices.HasId(preview.ApplyTarget, "combo");
        }

        private static ActorInstance ReachWalkTarget(List<ScoredAction> candidates, ActorInstance performer, BattleTeams teams, EnemyFocus focus, int livingEnemies, uint performerGuid, PartyKit party)
        {
            if (CombatMemory.ReachWalkedThisRound(performerGuid))
                return null;

            if (livingEnemies <= 1 && !HasDamagingHitOn(candidates, 0))
            {
                var last = FindLastLivingEnemy(candidates);
                if (last == null || last.Actor == null)
                    return null;
                if (AllyAlreadyReaches(party, performerGuid, last.Actor))
                    return null;
                return last.Actor;
            }

            if (focus == null || !focus.HasMustKillFirst || HasDamagingMustKillHit(candidates, focus))
                return null;

            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var e = focus.Enemies[i];
                if (e == null || !e.MustKillFirst)
                    continue;
                // Taproot is healthless rank-4. If this hero cannot tap it, hit
                // the General. Walking ping-pongs the last two heroes forever.
                if (e.ClassId != null && e.ClassId.IndexOf("taproot", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                var actor = GetActor(teams, e.Guid);
                if (actor == null || !actor.IsLiving)
                    continue;
                try { if (GameSnapshot.IsCorpse(actor)) continue; } catch { }
                // Same ping-pong on Altar: MAA walks onto Dismas's Pistol rank,
                // then Dismas walks back next round.
                if (AllyAlreadyReaches(party, performerGuid, actor))
                    continue;
                if (BlockedReachSkills(performer, actor).Count > 0)
                    return actor;
            }
            return null;
        }

        private static bool AllyAlreadyReaches(PartyKit party, uint performerGuid, ActorInstance enemy)
        {
            if (party == null || enemy == null)
                return false;
            var rank = 0;
            try { rank = enemy.TeamPosition; }
            catch { return false; }
            return party.AllyHitsEnemyRank(performerGuid, rank);
        }

        private static void ApplyReachReposition(List<ScoredAction> candidates, ActorInstance performer, ActorInstance enemy, PartyKit party, bool partyCrisis)
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
            string enemyClass = null;
            try { enemyClass = enemy.ActorDataClass != null ? enemy.ActorDataClass.GetKey() : null; } catch { }

            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Kind != SkillKind.Move || c.Target == null || c.Target.Actor == null)
                    continue;
                if (EnemyFocus.IsTangleWasteSkill(c.SkillId))
                    continue;
                var dest = 0;
                try { dest = c.Target.Actor.TeamPosition; } catch { continue; }
                // wiki Librarian: do not swap the ally who already punches him.
                if (party != null && party.HitsEnemyRank(c.Target.Actor.ActorGuid, enemyRank)
                    && PreserveAllyReach(enemyClass))
                    continue;
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
                // Below typical crisis heals (~160–200) so Endure / BM / Rest win.
                c.Score = partyCrisis ? 90f : 180f;
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
                if (LooksLikeHeal(id, def, null) || IsPassHeal(id))
                    continue;
                if (!ReachWalkSkill(id))
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
                if (!IsAllyHealSkill(id, def))
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

        internal static JObject ShadowCompare(ChosenAction bot, List<JObject> legal, string humanSkill, uint humanTarget)
        {
            var match = bot != null
                        && string.Equals(bot.SkillId, humanSkill, StringComparison.OrdinalIgnoreCase)
                        && bot.TargetGuid == humanTarget;
            var human = FindLegal(legal, humanSkill, humanTarget);
            var botScore = bot != null ? bot.Score : 0f;
            var humanScore = human != null ? human.Value<float>("score") : 0f;
            var rank = RankInLegal(legal, humanSkill, humanTarget);
            return new JObject
            {
                ["match"] = match,
                ["bot"] = bot == null ? JValue.CreateNull() : new JObject
                {
                    ["skill"] = bot.SkillId,
                    ["target"] = bot.TargetGuid,
                    ["reason"] = bot.Reason,
                    ["score"] = bot.Score,
                    ["item"] = bot.IsItem
                },
                ["human"] = new JObject
                {
                    ["skill"] = humanSkill,
                    ["target"] = humanTarget,
                    ["score"] = humanScore,
                    ["rank"] = rank
                },
                ["gap"] = botScore - humanScore
            };
        }

        internal static int RankInLegal(List<JObject> legal, string skill, uint target)
        {
            if (legal == null || string.IsNullOrEmpty(skill))
                return -1;
            var scored = new List<JObject>(legal);
            scored.Sort((a, b) => b.Value<float>("score").CompareTo(a.Value<float>("score")));
            for (var i = 0; i < scored.Count; i++)
            {
                var row = scored[i];
                if (row == null)
                    continue;
                if (!string.Equals(row.Value<string>("skill"), skill, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (row.Value<uint>("target") != target)
                    continue;
                return i + 1;
            }
            return -1;
        }

        private static JObject FindLegal(List<JObject> legal, string skill, uint target)
        {
            if (legal == null)
                return null;
            for (var i = 0; i < legal.Count; i++)
            {
                var row = legal[i];
                if (row == null)
                    continue;
                if (!string.Equals(row.Value<string>("skill"), skill, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (row.Value<uint>("target") != target)
                    continue;
                return row;
            }
            return null;
        }

        private static void LogTurn(ActorControllerBase controller, ActorInstance performer, List<JObject> candidates, ChosenAction chosen, string reason, PartyKit party, EnemyFocus focus, bool commit)
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

            var mode = commit ? "auto" : "shadow";
            var record = new JObject
            {
                ["mode"] = mode,
                ["actor"] = GameSnapshot.Actor(performer),
                ["heroes"] = heroes,
                ["enemies"] = enemies,
                ["legal"] = new JArray(candidates.ToArray()),
                ["chosen"] = chosen == null ? JValue.CreateNull() : new JObject
                {
                    ["skill"] = chosen.SkillId,
                    ["target"] = chosen.TargetGuid,
                    ["reason"] = chosen.Reason,
                    ["item"] = chosen.IsItem,
                    ["score"] = chosen.Score
                },
                ["reason"] = reason,
                ["synergy"] = party != null ? party.ToJson() : null,
                ["focus"] = focus != null ? focus.ToJson() : null
            };

            var line = chosen == null
                ? $"{GameSnapshot.OneLine(performer)}: NO LEGAL ACTION"
                : $"{GameSnapshot.OneLine(performer)}: {chosen.SkillId} -> {chosen.TargetGuid} ({reason})";
            var summary = commit ? line : "SHADOW would " + line;
            string runnerSkill = null;
            var runnerScore = 0f;
            var runnerLine = RunnerUpLine(candidates, chosen, out runnerSkill, out runnerScore);
            if (!string.IsNullOrEmpty(runnerSkill))
            {
                record["runner"] = new JObject
                {
                    ["skill"] = runnerSkill,
                    ["score"] = runnerScore
                };
            }

            if (!Plugin.LogPreviews.Value)
                record.Remove("legal");

            DecisionLog.Turn(record, summary, runnerLine);
        }

        private static string RunnerUpLine(List<JObject> candidates, ChosenAction chosen, out string skill, out float score)
        {
            skill = null;
            score = 0f;
            if (candidates == null || candidates.Count == 0)
                return "";
            JObject bestOther = null;
            for (var i = 0; i < candidates.Count; i++)
            {
                var row = candidates[i];
                if (row == null)
                    continue;
                if (chosen != null
                    && row.Value<string>("skill") == chosen.SkillId
                    && row.Value<uint>("target") == chosen.TargetGuid)
                    continue;
                if (bestOther == null || row.Value<float>("score") > bestOther.Value<float>("score"))
                    bestOther = row;
            }
            if (bestOther == null)
                return "";
            skill = bestOther.Value<string>("skill");
            score = bestOther.Value<float>("score");
            var kills = bestOther.Value<bool>("kills") ? " kill" : "";
            return "next " + skill + " " + score.ToString("0") + kills;
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
