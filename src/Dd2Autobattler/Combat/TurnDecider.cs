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

        // Live entry point: read the game into a snapshot, decide on plain
        // data, then write memory back and log. Picks are the same as
        // DecideSnapshot on the logged "snap".
        public static ChosenAction Decide(ActorControllerBase controller, bool commit)
        {
            var performer = controller != null ? GetPerformer(controller) : null;
            if (performer == null)
                return null;

            var teams = GetTeams(controller);
            var snap = LiveTurnReader.Capture(controller, performer, teams);
            var result = DecideSnapshot(snap);
            // Optional round lookahead (Search.Mode; default Off).
            Search.SearchResult search = null;
            try { search = Search.SearchHook.ApplyLive(snap, result); }
            catch (Exception ex) { DecisionLog.Warn("Search failed: " + ex.Message); }
            var performerGuid = snap.PerformerGuid;
            var best = result.Chosen;
            var rows = result.Rows;

            if (best == null)
            {
                CombatMemory.Restore(result.Memory);
                if (!commit)
                    CombatMemory.NoteShadowPick(performerGuid, null, rows);
                LogTurn(controller, performer, snap, rows, null, "no_legal_action", result.Party, result.Focus, commit);
                return null;
            }

            if (!commit)
            {
                CombatMemory.Restore(result.Memory);
                CombatMemory.NoteShadowPick(performerGuid, best, rows);
                LogTurn(controller, performer, snap, rows, best, best.Reason, result.Party, result.Focus, false, search);
                return best;
            }

            ApplyCommit(result.Memory, result);
            CombatMemory.Restore(result.Memory);
            _pendingTarget = best.TargetGuid;
            LogTurn(controller, performer, snap, rows, best, best.Reason, result.Party, result.Focus, true, search);
            return best;
        }

        // Pure decision over plain data. No game calls, no static state: the
        // snapshot's memory is cloned, probed (setup / item budgets) and
        // returned in result.Memory. Same picks and log rows as the live path.
        internal static DecisionResult DecideSnapshot(TurnSnapshot snap)
        {
            var result = new DecisionResult();
            if (snap == null)
            {
                result.Reason = "no_legal_action";
                result.Candidates = new List<ScoredAction>();
                result.Rows = new List<JObject>();
                result.Memory = new MemoryState();
                return result;
            }

            var memory = (snap.Memory ?? new MemoryState()).Clone();
            var performerGuid = snap.PerformerGuid;
            var performerBody = snap.Performer ?? snap.Find(performerGuid) ?? new TargetInfo { Guid = performerGuid };
            var livingEnemies = CountLivingEnemies(snap);
            var killableEnemies = CountKillableEnemies(snap);
            var party = PartyKit.Scan(snap);
            var focus = EnemyFocus.Scan(snap);
            var remaining = snap.RemainingTurns ?? new List<uint>();
            var nextEnemyGuid = FirstEnemyInOrder(remaining, snap);
            var candidates = new List<ScoredAction>();

            if (snap.Legal != null)
            {
                for (var i = 0; i < snap.Legal.Count; i++)
                {
                    var legal = snap.Legal[i];
                    if (legal == null || string.IsNullOrEmpty(legal.Skill) || legal.Target == 0)
                        continue;

                    var skillId = legal.Skill;
                    var targetGuid = legal.Target;
                    var skillDef = snap.Skill(skillId);
                    var stealthed = legal.Stealthed;

                    var preview = legal.Preview != null
                        ? legal.Preview.Clone()
                        : new PreviewScore { Error = "no_preview" };
                    if (preview.Ok)
                        ClampPreviewToLivingHits(preview, snap, performerGuid);
                    var target = snap.Find(targetGuid) ?? new TargetInfo();
                    if (preview.Ok)
                        SkillPreviewReader.ApplyComboBleedDouble(skillId, target.Combo, preview, targetGuid);
                    var enemyTarget = snap.IsEnemy(targetGuid);
                    var kind = Classify(skillId, skillDef, preview, enemyTarget);
                    NoteKillFromHp(preview, target, snap);
                    if (target.DiesToDot && livingEnemies > 1)
                        preview.Kills = false;
                    var role = skillDef != null && skillDef.Role != null ? skillDef.Role : new SkillRole();
                    var tokens = TokenPrices.Evaluate(kind, enemyTarget, preview, target, livingEnemies, party, performerGuid, role, performerBody, nextEnemyGuid, memory);
                    var isItem = ItemPolicy.IsCombatItem(skillDef, skillId, performerBody);
                    var clearsCorpse = ItemPolicy.ClearsCorpse(skillId, skillDef);
                    var freeItem = isItem && ItemPolicy.IsFreeAction(skillDef);
                    var qty = isItem ? snap.RemainingQty(skillId) : 0;
                    var item = isItem
                        ? ItemPolicy.Evaluate(skillId, skillDef, kind, enemyTarget, preview, target, tokens, livingEnemies, qty)
                        : null;
                    var setup = party != null ? party.SetupBonus(role, target, enemyTarget, preview) : 0f;
                    if (TokenPrices.IsEarlySetup(memory.Round, livingEnemies))
                        setup *= 1.5f;
                    // Corpse-clear skills skip token/setup on a corpse click — Lye scoring.
                    var score = ScoreAction(skillId, kind, enemyTarget, stealthed || target.Stealth, preview, target, livingEnemies, party, focus, clearsCorpse);
                    if (item != null)
                        score = item.Score;
                    else if (!(clearsCorpse && target.Corpse))
                        score += tokens.Score + setup;
                    if (enemyTarget && kind == SkillKind.Attack && !isItem)
                        score += SelfHitPenalty(preview, performerBody);
                    var focusGuid = enemyTarget ? FocusPayGuid(preview, target) : 0u;
                    candidates.Add(new ScoredAction
                    {
                        SkillId = skillId,
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
                        Cursed = snap.IsCursed(skillId)
                    });
                }
            }

            ApplyCursePenalty(candidates);
            if (AllyInCrisis(snap) && !HasLegalAllyHeal(candidates))
                ApplyHealReposition(candidates, performerBody, snap);
            ApplyHarvestHungerGuard(candidates, performerBody, snap, focus);
            ApplyLibrarianBookVeto(candidates, focus);
            ApplyCorpseSplash(candidates, snap);
            ApplySighLungVeto(candidates, focus);
            ApplyFocusedFaultNotes(candidates, focus);
            ApplyImplicationBlindNotes(candidates, focus);
            ApplyLaterActNotes(candidates, focus, performerBody);
            ApplyChirurgeonDotWaste(candidates, focus, snap);
            ApplyRankWalks(candidates, performerBody, snap, focus, livingEnemies, performerGuid, party, memory);
            ApplyCorpseReach(candidates, focus, livingEnemies);
            ApplyOnePly(candidates, livingEnemies, performerGuid, remaining, snap, party);
            ApplyComboChipVeto(candidates);
            var partyDoor = PartyHasDeathsDoor(snap);
            ApplyKitSafety(candidates, performerBody, livingEnemies, partyDoor, party, performerGuid, snap);
            // Again after the DoT pay above (KitSafety dot_host): a live Poison
            // Dart blight must not climb back over the direct hit on him.
            ApplyChirurgeonDotWaste(candidates, focus, snap);
            ApplyCrisisStabilize(candidates, snap, focus);
            ApplyStressBeforeRest(candidates);
            ApplyRestOverSelfMedicine(candidates, performerBody);
            ApplyMountainCultistTaunt(candidates, snap, performerBody, focus);
            // Librarian: Duelist's Advance / Rampart that leave fewer heroes on
            // him rank below a hit that does not (LibrarianPlay).
            LibrarianPlay.DemoteReachLosingSelfMoves(candidates, performerBody, snap);
            // Shackles of Denial: a free DoT item on a lock is worth its ticks
            // (DenialPlay; ItemPolicy counts direct damage only).
            DenialPlay.ScoreDotItems(candidates, snap, focus);

            var lastEnemy = FindLastLivingEnemy(candidates);
            var lastGuid = lastEnemy != null ? lastEnemy.Guid : 0u;
            var awkward = lastEnemy != null && (lastEnemy.Riposte || lastEnemy.Dodge);
            var allowSetup = livingEnemies <= 1 && awkward && memory.CanSpendSetup(lastGuid);

            var performerCrisis = performerBody.DeathsDoor || performerBody.HpPct <= 0.20f
                                  || performerBody.DiesToDot
                                  || (performerBody.Hp > 0f && performerBody.NextDot + 0.05f >= performerBody.Hp);
            var partyCrisis = partyDoor || AllyInCrisis(snap);
            var allyRiposte = AllyHasRiposte(snap, performerGuid);
            var allyLow = AllyNeedsCover(snap);
            var trace = new PickTrace();
            var picked = PickAction(candidates, livingEnemies, allowSetup, performerGuid, focus, killableEnemies, performerCrisis, partyCrisis, performerBody.Riposte, allyRiposte, allyLow, memory, trace);
            var opened = OpenAddOverIdle(picked, candidates, focus);
            // A cited replacement, not the scorer's pick among equals: the
            // round search must not swap the filler back in.
            if (opened != picked)
                trace.Gate = "open_add";
            picked = opened;
            // Shackles of Denial: the lock attack with the most weighted damage
            // (DenialPlay). Cited like open_add: the search must not swap it back.
            var denialHit = DenialPlay.BestLockAttack(picked, candidates, snap, focus, performerBody, trace.Gate);
            if (denialHit != picked)
                trace.Gate = "denial_ev";
            picked = denialHit;
            picked = RecoverOverIdle(picked, candidates, focus, snap, performerBody, livingEnemies, killableEnemies, LastEnemyRecovery);
            var cover = CoverGuardOverSwing(picked, candidates, snap, performerBody, trace.Gate);
            // Cited like open_add: the search must not swap the swing back in.
            if (cover != picked)
                trace.Gate = "cover_guard";
            picked = cover;
            var bolster = MountainBolsterOverSwing(picked, candidates, snap, performerBody, focus, trace.Gate, killableEnemies);
            // Cited like cover_guard: the search must not swap the swing back in.
            if (bolster != picked)
                trace.Gate = "mountain_bolster";
            picked = bolster;
            var free = FreeOverPass(picked, candidates, focus, snap, performerBody, livingEnemies);
            // Cited like open_add: the search must not swap the pass back in.
            if (free != picked)
                trace.Gate = "over_pass";
            picked = free;
            // Shackles of Denial: a hero whose rank launches no attack walks back
            // onto one instead of an uncited support or a pass (DenialPlay).
            var walk = DenialPlay.WalkToLaunch(picked, candidates, snap, focus, performerBody, trace.Gate);
            if (walk != picked)
                trace.Gate = "denial_walk";
            picked = walk;
            // Ignited Librarian: a damaging hit on him over any heal / Rest /
            // support (LibrarianPlay). Cited like over_pass.
            var race = LibrarianPlay.IgnitedRace(picked, candidates, snap);
            if (race != picked)
                trace.Gate = "librarian_race";
            picked = race;
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

            result.Chosen = best;
            result.Reason = best != null ? best.Reason : "no_legal_action";
            result.Picked = picked;
            result.Candidates = candidates;
            result.Rows = ToLogRows(candidates, focus);
            result.Party = party;
            result.Focus = focus;
            result.Memory = memory;
            result.PerformerGuid = performerGuid;
            result.LastEnemyGuid = lastGuid;
            result.AllowSetup = allowSetup;
            result.Trace = trace;
            return result;
        }

        // Commit side effects of a pick onto memory (live: after an AUTO
        // click; replay: the logged pick, so later turns see the same memory).
        internal static void ApplyCommit(MemoryState memory, DecisionResult result, ScoredAction picked = null)
        {
            if (memory == null || result == null)
                return;
            picked = picked ?? result.Picked;
            if (picked == null)
                return;
            var performerGuid = result.PerformerGuid;
            var party = result.Party;
            var focus = result.Focus;
            var wasSetup = picked.Kind == SkillKind.Support || picked.Kind == SkillKind.Pass;
            memory.NoteChosen(result.LastEnemyGuid, wasSetup && result.AllowSetup && !picked.IsItem);
            if (picked.ReachReposition || picked.MustRankWalk)
                memory.NoteReachWalk(performerGuid);
            if (picked.IsItem)
                memory.NoteItemUsed(performerGuid);
            if (!picked.IsItem && party != null && party.HeroSpendsCombo(performerGuid))
                memory.NoteComboSpenderActed(performerGuid);
            // Items (bandage) must not burn the once-per-round skill-heal gate.
            if (!picked.IsItem && !picked.EnemyTarget && picked.Target != null
                && CountsAsCrisisHealSpend(picked.SkillId, picked.Kind)
                && (picked.Target.DeathsDoor || picked.Target.HpPct <= 0.35f))
                memory.NoteCrisisHeal(picked.TargetGuid);
            if (!picked.IsItem && picked.EnemyTarget && focus != null && focus.IsTaproot(picked.TargetGuid))
                memory.NoteTaprootHit();
        }

        internal sealed class DecisionResult
        {
            public ChosenAction Chosen;
            // Chosen reason, or "no_legal_action".
            public string Reason;
            public ScoredAction Picked;
            // Legal order (the log's "legal" rows).
            public List<ScoredAction> Candidates;
            public List<JObject> Rows;
            public PartyKit Party;
            public EnemyFocus Focus;
            // Snapshot memory after this decision's probes, before commit.
            public MemoryState Memory;
            public uint PerformerGuid;
            public uint LastEnemyGuid;
            public bool AllowSetup;
            public PickTrace Trace;

            // Candidates best first (stable on legal order for ties).
            public List<ScoredAction> Ranked()
            {
                var list = new List<ScoredAction>();
                if (Candidates == null)
                    return list;
                var idx = new Dictionary<ScoredAction, int>();
                for (var i = 0; i < Candidates.Count; i++)
                {
                    list.Add(Candidates[i]);
                    idx[Candidates[i]] = i;
                }
                list.Sort((a, b) =>
                {
                    var c = b.Score.CompareTo(a.Score);
                    return c != 0 ? c : idx[a].CompareTo(idx[b]);
                });
                return list;
            }
        }

        internal sealed class ScoredAction
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
            // cultist_mountain_*: a self-taunt that would only pile stress on the
            // tank (ApplyMountainCultistTaunt). Never the tank_riposte open.
            public bool TauntBlocked;
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
        internal static void NoteKillFromHp(PreviewScore preview, TargetInfo clickTarget, TurnSnapshot snap)
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
                barGuid = GuardBarGuid(preview, clickTarget, HeroGuidTest(snap));
                if (barGuid == 0 || barGuid == clickTarget.Guid)
                    return;
                var described = snap != null ? snap.Find(barGuid) : null;
                if (described == null)
                {
                    if (HitKills(preview, barGuid))
                        preview.Kills = true;
                    return;
                }
                if (described.Corpse || described.Healthless)
                    return;
                bar = described;
            }

            // Death's Door armor: a lethal hit only drops the bar to 0 HP, and at
            // 0 HP each token absorbs one hit. Logs 2026-11-16: 18 of 19 flagged
            // kills on an armored bar did not die; 10 of 10 at 0 HP unarmored did.
            if (bar.DeathArmor)
            {
                preview.Kills = false;
                return;
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
        // skip: guids that cannot be the guardian (our own heroes). The preview
        // lists the Riposte return hit on the performer first (logged turn:
        // Hits [MAA 6.0, guardian mongrel 1.3]); taking the performer's
        // 0 HP Death's Door bar as the guardian scored a "kill" and MAA crushed
        // into the Riposte on Death's Door and died.
        internal static uint GuardBarGuid(PreviewScore preview, TargetInfo clickTarget, Func<uint, bool> skip = null)
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
                    if (g != 0 && g != clickTarget.Guid && (skip == null || !skip(g)))
                        return g;
                }
            }
            if (preview.HitGuids != null)
            {
                for (var i = 0; i < preview.HitGuids.Count; i++)
                {
                    var g = preview.HitGuids[i];
                    if (g != 0 && g != clickTarget.Guid && (skip == null || !skip(g)))
                        return g;
                }
            }
            return 0;
        }

        // A guid the snapshot knows as one of our heroes (never a guardian).
        internal static Func<uint, bool> HeroGuidTest(TurnSnapshot snap)
        {
            if (snap == null)
                return null;
            return g => snap.Find(g) != null && !snap.IsEnemy(g);
        }

        // Not given the hero skip (unlike NoteKillFromHp): when the preview lists
        // the performer first, the pay still goes to that hero guid (0 focus).
        // Pointing it at the guardian puts the deferred-add penalty on guarded
        // clicks (replay 2026-11-17: Highwayman Take Aim at +2 over a damaging
        // hit) — a separate change, not made here.
        internal static uint FocusPayGuid(PreviewScore preview, TargetInfo target)
        {
            if (target == null)
                return 0;
            var click = target.Guid;
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

        internal static SkillKind Classify(string skillId, SkillSnap def, PreviewScore preview, bool enemyTarget)
        {
            if (def != null && def.IsMove)
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
            if (def != null && def.IsFriendly)
                return SkillKind.Support;
            return SkillKind.Attack;
        }

        internal static bool LooksLikeHeal(string skillId, SkillSnap def, PreviewScore preview)
        {
            if (preview != null && (preview.HealValid || preview.Heal > 0f))
                return true;
            if (IsPassHeal(skillId) || (!string.IsNullOrEmpty(skillId) && skillId.StartsWith("pass_", StringComparison.OrdinalIgnoreCase)))
                return false;
            // CSV tag "heal" plus an actual HP heal in the effects. Tag alone
            // is not enough: More MORE! is tagged heal and is a taunt.
            if (def != null)
            {
                if (def.HasTag("heal") && def.Role != null && def.Role.Heals)
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

        private static int CountLivingEnemies(TurnSnapshot snap)
        {
            return CountEnemies(snap, includeHealthless: true);
        }

        // Taproot is living but has no HP bar. Last-kill / finish checks
        // ignore it so a General kill can end the fight.
        private static int CountKillableEnemies(TurnSnapshot snap)
        {
            return CountEnemies(snap, includeHealthless: false);
        }

        private static int CountEnemies(TurnSnapshot snap, bool includeHealthless)
        {
            var n = 0;
            foreach (var info in snap.Enemies)
            {
                if (info == null || !info.Living)
                    continue;
                if (info.Corpse)
                    continue;
                if (!includeHealthless && info.Healthless)
                    continue;
                // Book stacks (m_IsBattleComplete) do not keep the fight going:
                // a Librarian kill is the last-bar finish.
                if (!includeHealthless && !LibrarianPlay.HoldsBattleOpen(info))
                    continue;
                n++;
            }
            return n;
        }

        // The preview lists the Riposte return (or a self-cost) as a Hit on the
        // performer. On Death's Door every damage instance rolls deathblow (CSV
        // hero resistance death 0.6, min 0.33; deathblow_chance_shaping_heroes
        // -0.1 per survived roll until combat end), so that click is a death roll
        // the hero chooses. In one logged turn the MAA on Death's Door
        // crushed a guarded pillager, the guardian mongrel's Riposte killed him.
        // A self-hit that only drops a living hero onto Death's Door costs less.
        internal const float SelfHitOnDeathsDoorPenalty = 200f;
        internal const float SelfHitIntoDeathsDoorPenalty = 60f;

        internal static float SelfHitPenalty(PreviewScore preview, TargetInfo performer)
        {
            if (preview == null || !preview.Ok || performer == null || performer.Guid == 0 || performer.Corpse)
                return 0f;
            float self;
            if (!TryHitDamageOn(preview, performer.Guid, out self) || self <= 0f)
                return 0f;
            if (performer.DeathsDoor)
                return -SelfHitOnDeathsDoorPenalty;
            if (performer.Hp > 0f && self + 0.05f >= performer.Hp)
                return -SelfHitIntoDeathsDoorPenalty;
            return 0f;
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
                    // CSV pass_stress: performer_effects stress_damage_2. At 8+ that is
                    // Overstress at 10 (80% Meltdown: HP down to 10%). Logs: 28 such
                    // passes; all 14 checked overstressed. Any other click, a swap included, is better.
                    if (PassOverstresses(skillId, target))
                        score -= 1100f;
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
                    // Rest at 8+ stress is a Meltdown, not a heal (RestIntoMeltdown).
                    if (RestIntoMeltdown(skillId, target))
                    {
                        score -= RestIntoMeltdownPenalty;
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
                        if (MeltdownImminent(target, focus))
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
                    if (enemyTarget && focus != null && target != null && target.Guid != 0)
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

        private static TargetInfo FindLastLivingEnemy(List<ScoredAction> candidates)
        {
            if (candidates == null)
                return null;
            TargetInfo found = null;
            foreach (var c in candidates)
            {
                if (c.Target == null || !c.EnemyTarget || c.Target.Corpse || c.Target.Guid == 0 || !c.Target.Living)
                    continue;
                if (found == null)
                    found = c.Target;
                else if (c.Target.Guid != found.Guid)
                    return null;
            }
            return found;
        }

        private const float HealBeatsAttackGap = 40f;

        // Which PickAction gate returned the pick, and the candidates that
        // survived the cited vetoes (the only picks a lookahead may make).
        internal sealed class PickTrace
        {
            public string Gate;
            public readonly List<ScoredAction> Eligible = new List<ScoredAction>();
        }

        private static ScoredAction Gate(PickTrace trace, string gate, ScoredAction pick)
        {
            if (trace != null)
                trace.Gate = gate;
            return pick;
        }

        private static ScoredAction PickAction(List<ScoredAction> candidates, int livingEnemies, bool allowSetup, uint performerGuid, EnemyFocus focus, int killableEnemies, bool performerCrisis, bool partyCrisis, bool performerRiposte, bool allyRiposte, bool allyLow, MemoryState memory, PickTrace trace = null)
        {
            if (candidates == null || candidates.Count == 0)
                return null;

            // wiki.gg/Harvest_Child: rank 1 + harvest_hunger is forced Feed the
            // Hunger (CSV harvest_hunger, target_is_meat). Meats are deferred
            // adds, so the crisis/add vetoes would skip the only legal click
            // (t15 HWM 16% HP → no_legal_action).
            ScoredAction bestHungerEat = null;
            foreach (var c in candidates)
            {
                if (!IsForcedHarvestEat(c.SkillId))
                    continue;
                if (bestHungerEat == null || c.Score > bestHungerEat.Score)
                    bestHungerEat = c;
            }
            if (bestHungerEat != null)
                return Gate(trace, "hunger_eat", bestHungerEat);

            ScoredAction bestItem = null;
            foreach (var c in candidates)
            {
                if (!c.IsItem || !c.FreeAction || c.Item == null || !c.Item.UseNow)
                    continue;
                if (!memory.CanSpendItem(performerGuid, c.Item.Crisis))
                    continue;
                if (bestItem == null || c.Score > bestItem.Score)
                    bestItem = c;
            }
            if (bestItem != null)
                return Gate(trace, "free_item", bestItem);

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
                    if (c.Kind != SkillKind.Attack || !c.EnemyTarget || c.Target == null || c.Target.Guid == 0)
                        continue;
                    // A click on a stealthed must-kill scores -200 and is never
                    // taken; it must not veto the hits this hero can make.
                    if (c.Stealthed)
                        continue;
                    if (ComboMarkWaste(c))
                        continue;
                    // Blind Gas / Tracking Shot is not a hit. It must not arm
                    // the deferred-boss veto or steal Incision / Firefly.
                    var focusHit = CountsAsDamagingFocusClick(c.SkillId, c.Preview, c.LeaveChip)
                                   || IsTaprootTap(c);
                    if (!focusHit)
                        continue;
                    if (focus.HasPriorityTarget && focus.IsPriority(c.Target.Guid))
                        priorityLegal = true;
                    if (focus.HasMustKillFirst && focus.IsMustKillFirst(c.Target.Guid))
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
            var stalksUp = focus != null && focus.HasLivingStalkMustKill();
            var hasStalkHit = HasStalkRealHit(candidates, stalksUp);
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
                              && c.Target.Guid != 0 && c.Target.Living;
                var hungerGuard = IsHarvestHungerGuard(c.SkillId);
                var hungerEat = IsForcedHarvestEat(c.SkillId);
                var splashFocus = HitsFocusTarget(c, focus);
                // wiki.gg/Librarian: do not punch stacks even when he is out of reach.
                if (realHit && focus != null && focus.IsLibrarianStack(c.Target.Guid))
                    continue;
                if (realHit && !hungerGuard && !hungerEat && !splashFocus
                    && ShouldSkipDeferredPunch(
                        mustKillLegal,
                        focus != null && focus.IsDeferred(c.Target.Guid),
                        LastKillableFinish(c.Preview, c.Target, killableEnemies)))
                    continue;
                // Taunt on the deferred target is forced; a leftover 0-dmg
                // click on the must-kill must not veto it.
                if (realHit && !hungerGuard && !hungerEat && !splashFocus && priorityLegal
                    && focus.IsDeferred(c.Target.Guid) && !c.Target.Taunt)
                    continue;
                if (realHit && !hungerGuard && !hungerEat && !splashFocus && priorityLegal
                    && focus.IsAdd(c.Target.Guid)
                    && !focus.IsPriority(c.Target.Guid))
                    continue;
                if (realHit && !hungerEat && !splashFocus && performerCrisis && livingEnemies > 1 && focus != null
                    && focus.IsAdd(c.Target.Guid)
                    && !focus.IsPriority(c.Target.Guid))
                    continue;
                if (realHit && peelLegal && NeedsReachPeel(c, focus) && !StripsReachDefense(c)
                    && (c.Preview == null || !c.Preview.Kills))
                    continue;
                if (realHit && ComboChipWaste(c))
                    continue;
                if (realHit && ComboMarkWaste(c))
                    continue;
                // wiki.gg/Focused_Fault: AoE is a kill or a DoT finish. Blind /
                // Tracking Shot on a Cluster is neither when blight is legal.
                if (realHit && StalkTapWaste(stalksUp, hasStalkHit, c.SkillId, c.Preview))
                    continue;
                // Tracking Shot on a deferred foot while a Drummer is up is not
                // setup. Wiki: kill the controller; Combo on an add is a wasted turn.
                if (realHit && ComboMarkOnDeferredAdd(
                        focus != null && focus.HasPriorityTarget,
                        focus != null && c.Target.Guid != 0
                            && (focus.IsDeferred(c.Target.Guid)
                                || focus.IsAdd(c.Target.Guid)),
                        c.SkillId,
                        c.Preview))
                    continue;
                // 0-damage Tracking Shot on the Librarian while Wicked Slice is
                // legal on him (t18: 170 vs 169.5). A mark is not a hit.
                if (realHit && IsZeroDamageMark(c.SkillId, c.Preview)
                    && c.Target.Guid != 0
                    && HasDamagingHitOn(candidates, c.Target.Guid)
                    && focus != null
                    && (focus.IsMustKillFirst(c.Target.Guid)
                        || focus.IsPriority(c.Target.Guid)))
                    continue;
                // A blight cone whose click target is a corpse still tags a
                // living Cluster. That is the wiki DoT finish, not skip_corpse.
                var stalkDotFinish = IsStalkDotFinish(
                    stalksUp,
                    c.EnemyTarget,
                    c.Target != null && c.Target.Corpse,
                    c.Preview);
                if ((realHit || stalkDotFinish) && (bestAttack == null || BetterAttack(c, bestAttack, focus)))
                    bestAttack = c;
                // A pass sets nothing up (CSV pass_stress: +2 stress only;
                // Riposte / Dodge last 3 of their own turns, so waiting does
                // not strip them).
                if (allowSetup && c.Kind == SkillKind.Support
                    && (bestSetup == null || c.Score > bestSetup.Score))
                    bestSetup = c;
                // After the vetoes so a skipped deferred punch cannot leak
                // through as bestAny when bestAttack is null or negative.
                if (bestAny == null || c.Score > bestAny.Score)
                    bestAny = c;
                // Survived every cited veto above (search may only pick these).
                if (trace != null)
                    trace.Eligible.Add(c);
            }

            if (bestCrisisHeal != null
                && !ChirurgeonKillBeatsHeal(bestAttack, bestCrisisHeal, focus)
                && !MustKillFinishBeatsHeal(bestAttack, bestCrisisHeal, focus)
                && ShouldTakeCrisisHeal(
                    bestCrisisHeal.Target,
                    bestAttack != null ? bestAttack.Preview : null,
                    bestAttack != null ? bestAttack.Target : null,
                    killableEnemies,
                    memory.CrisisHealed(bestCrisisHeal.TargetGuid)))
                return Gate(trace, "crisis_heal", bestCrisisHeal);

            // Shackles of Denial: Doubt lands every round, so a hero at 8+ stress
            // Meltdowns (HP to 10%) unless stress-healed. Bolster before a non-kill
            // swing or Retribution (logs: Bolster on a 9-stress ally lost to both).
            if (focus != null && focus.DenialLocks && ShouldOpenUtility(bestAttack, killableEnemies, focus))
            {
                var meltdownGuard = BestMeltdownGuard(candidates, focus);
                if (meltdownGuard != null)
                {
                    meltdownGuard.NoteReason = "meltdown_guard";
                    return Gate(trace, "meltdown_guard", meltdownGuard);
                }
            }

            // Strip Combo / Fall Taunt-Guard before punching Altar or Reach.
            // Same utility gate as Implication Blind — not over a kill/crisis.
            var cultistDefense = PreferCultistDefense(bestStripCombo, bestFallDefense, bestAttack, killableEnemies, partyCrisis);
            if (cultistDefense != null)
                return Gate(trace, "cultist_defense", cultistDefense);

            // Blind loaded Implication before BOOOOOOOM! — not over a kill/crisis/evolve pack.
            if (bestArtilleryBlind != null && !partyCrisis
                && ShouldOpenUtility(bestAttack, killableEnemies, focus))
                return Gate(trace, "artillery_blind", bestArtilleryBlind);

            // MAA Retribution (taunt+riposte) is the team's one Riposte. Take Aim
            // is not this gate — Duelist's Advance plants Riposte on the attack.
            // Librarian: the party reach walk goes first (logs: Retribution in
            // round 1-2 put the MAA's rank 3 -> 1 walk off by a round, with GR
            // on rank 1 unable to hit him meanwhile).
            if (!(LibrarianReachWalkLegal(candidates) && !partyCrisis)
                && ShouldOpenTankRiposte(
                    performerRiposte,
                    allyRiposte,
                    allyLow,
                    livingEnemies,
                    bestRiposteSetup != null,
                    bestAttack != null ? bestAttack.Preview : null,
                    bestAttack != null ? bestAttack.Target : null,
                    killableEnemies,
                    focus))
                return Gate(trace, "tank_riposte", bestRiposteSetup);

            ScoredAction bestCorpseReach = null;
            foreach (var c in candidates)
            {
                if (c.Target == null || !c.Target.Corpse || c.NoteReason != "corpse_reach")
                    continue;
                if (c.IsItem)
                {
                    if (c.Item == null || !c.Item.UseNow)
                        continue;
                    if (!memory.CanSpendItem(performerGuid, true))
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
                return Gate(trace, "corpse_reach", bestCorpseReach);

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
                        return Gate(trace, "reposition", bestReposition);
                    if (!PlaysFocusClick(bestAttack, focus))
                        return Gate(trace, "reposition", bestReposition);
                }
            }

            // Vetoes emptied bestAttack while a connecting controller hit was
            // still in the list. Restore it so setup/pass cannot win.
            if (bestAttack == null)
                bestAttack = BestFocusAttack(candidates, focus);

            // One setup while the last enemy is awkward, then we must swing.
            // Not while an ally is on Death's Door / <=35%: the crisis docks every
            // support (ApplyCrisisStabilize) and this gate must not take it back
            // (a logged turn: Ounce of Prevention at -120 over a
            // 175 Noxious Blast on the Deacon with MAA at 6/55).
            if (allowSetup && bestSetup != null && bestAttack != null && !partyCrisis)
                return Gate(trace, "setup", bestSetup);
            // A stress heal on an ally one point from Meltdown (MeltdownImminent:
            // 9+, overstress at 10 is meltdown_health = HP to 10%) that already
            // outscores the best swing is not idle. In one logged round a
            // blinded Rampart (1.25 damage, 41.7) beat Bolster on the 9-stress PD
            // (57) through attack_over_idle; she melted 16 -> 3 HP next round.
            if (bestAttack != null && bestAny != null && !ReferenceEquals(bestAny, bestAttack)
                && bestAny.Score > bestAttack.Score
                && IsMeltdownGuardClick(bestAny, focus)
                && ShouldOpenUtility(bestAttack.Preview, bestAttack.Target, killableEnemies, focus))
            {
                bestAny.NoteReason = "meltdown_guard";
                return Gate(trace, "meltdown_guard", bestAny);
            }
            // Blind swing at −36 or worse: Reflection / Withstand already outscore it.
            // A connecting hit that already scored ≥ 0 is not that case.
            if (bestAttack != null && bestAny != null
                && ReplacesAttackWithIdle(bestAny.Kind, bestAny.EnemyTarget, false)
                && !ReplacesAttackWithIdle(bestAttack.Kind, bestAttack.EnemyTarget, false)
                && bestAttack.Score >= 0f)
                return Gate(trace, "attack_over_idle", bestAttack);
            if (bestAttack != null && bestAttack.Score < 0f && bestAny != null && bestAny.Score > bestAttack.Score)
                return Gate(trace, "any_over_negative_attack", bestAny);
            if (bestAttack != null)
            {
                if (HealBeatsAttack(bestAny.Kind, bestAny.SkillId, bestAny.EnemyTarget, bestAny.Target, bestAny.Preview, bestAny.Score, bestAttack.Score))
                    return Gate(trace, "heal_beats_attack", bestAny);
                return Gate(trace, "attack", bestAttack);
            }
            return Gate(trace, "any", bestAny);
        }

        // Setup / Rest / Absinthe must not beat a connecting hit that already
        // scored. Crisis heals are a different gate.
        internal static bool ReplacesAttackWithIdle(SkillKind kind, bool enemyTarget, bool crisisHeal)
        {
            if (crisisHeal)
                return false;
            if (kind == SkillKind.Attack && enemyTarget)
                return false;
            return kind == SkillKind.Support || kind == SkillKind.Pass || kind == SkillKind.Move
                   || (kind == SkillKind.Heal && !enemyTarget);
        }

        private static ScoredAction BestFocusAttack(List<ScoredAction> candidates, EnemyFocus focus)
        {
            if (candidates == null || focus == null)
                return null;
            ScoredAction best = null;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.IsItem && c.FreeAction)
                    continue;
                if (c.Kind != SkillKind.Attack || !c.EnemyTarget || c.Target == null || c.Target.Guid == 0)
                    continue;
                if (ComboMarkWaste(c) || IsZeroDamageMark(c.SkillId, c.Preview))
                    continue;
                if (!CountsAsDamagingFocusClick(c.SkillId, c.Preview, c.LeaveChip) && !IsTaprootTap(c))
                    continue;
                var guid = c.Target.Guid;
                if (!focus.IsPriority(guid) && !focus.IsMustKillFirst(guid))
                    continue;
                if (best == null || BetterAttack(c, best, focus))
                    best = c;
            }
            return best;
        }

        // Do not open Retribution / artillery Blind over a real kill, last bar,
        // a Cabin Boy burst window, or a damaging hit on a living Altar / stalk
        // must-kill (wiki: kill them; Crush on a Cluster is not setup).
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
            if (focus != null && focus.HasLivingStalkMustKill()
                && attackTarget != null && IdHasClass(attackTarget.ClassId, "eyes_stalk")
                && StalkClickHurts(attackPreview))
                return false;
            if (attackPreview != null && attackPreview.Kills)
                return false;
            return !LastKillableFinish(attackPreview, attackTarget, killableEnemies);
        }

        internal static bool StalkClickHurts(PreviewScore preview)
        {
            if (preview == null)
                return false;
            if (preview.Damage > 0f)
                return true;
            return preview.ApplyBleed + preview.ApplyBlight + preview.ApplyBurn > 0.05f;
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
            return c != null && !c.IsItem && !c.TauntBlocked
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
            if (target.Guid != 0)
            {
                count = target.CountToken("count");
                forcedMiss = target.CountToken("forced_miss");
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
            if (IsForcedHarvestEat(picked.SkillId))
                return "forced_hunger";
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

        // CSV harvest_hunger: performer_has_harvest_hunger + target_is_meat.
        // The game replaces the skill bar; this is not Hold the Line.
        internal static bool IsForcedHarvestEat(string skillId)
        {
            return !string.IsNullOrEmpty(skillId)
                   && skillId.IndexOf("harvest_hunger", StringComparison.OrdinalIgnoreCase) >= 0;
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
        private static void ApplyOnePly(List<ScoredAction> candidates, int livingEnemies, uint performerGuid, List<uint> remaining, TurnSnapshot snap, PartyKit party)
        {
            if (candidates == null)
                return;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.IsItem)
                    continue;
                var ply = BoardDelta(c);
                var leave = ShouldLeaveChip(c, livingEnemies, performerGuid, remaining, snap, party);
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

        // Blind Gas / Tracking Shot while a HP or DoT click on a Cluster is legal.
        internal static bool StalkTapWaste(bool stalksUp, bool hasStalkRealHit, string skillId, PreviewScore preview)
        {
            if (!stalksUp || !hasStalkRealHit)
                return false;
            return IsZeroDamageMark(skillId, preview);
        }

        // HP or blight/bleed/burn on a living Cluster, or a DoT cone whose click
        // target is a corpse but HitGuids still include another body (the Cluster).
        internal static bool IsStalkRealHit(
            bool stalksUp,
            bool enemyTarget,
            bool targetCorpse,
            string classId,
            string skillId,
            PreviewScore preview)
        {
            if (!stalksUp || !enemyTarget || preview == null)
                return false;
            if (IsZeroDamageMark(skillId, preview))
                return false;
            var dot = preview.ApplyBleed + preview.ApplyBlight + preview.ApplyBurn;
            if (!targetCorpse && IdHasClass(classId, "eyes_stalk")
                && (preview.Damage > 0f || dot > 0.05f))
                return true;
            return IsStalkDotFinish(true, true, targetCorpse, preview);
        }

        // Corpse-targeted blight still lands on a living stalk in the cone.
        // ScoreAction would skip_corpse (−250); undo that so Rest cannot win.
        internal static bool IsStalkDotFinish(bool stalksUp, bool enemyTarget, bool targetCorpse, PreviewScore preview)
        {
            if (!stalksUp || !enemyTarget || !targetCorpse || preview == null)
                return false;
            var dot = preview.ApplyBleed + preview.ApplyBlight + preview.ApplyBurn;
            if (dot <= 0.05f)
                return false;
            return preview.HitGuids != null && preview.HitGuids.Count >= 2;
        }

        internal static float StalkDotFinishUndo(bool stalksUp, bool enemyTarget, bool targetCorpse, float applyDot, int hitN)
        {
            if (!stalksUp || !enemyTarget || !targetCorpse || applyDot <= 0.05f || hitN < 2)
                return 0f;
            return 250f;
        }

        private static bool HasStalkRealHit(List<ScoredAction> candidates, bool stalksUp)
        {
            if (!stalksUp || candidates == null)
                return false;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.IsItem && c.FreeAction)
                    continue;
                if (c.Kind != SkillKind.Attack || !c.EnemyTarget || c.Target == null)
                    continue;
                if (IsStalkRealHit(true, true, c.Target.Corpse, c.Target.ClassId, c.SkillId, c.Preview))
                    return true;
            }
            return false;
        }

        private static bool ShouldLeaveChip(ScoredAction c, int livingEnemies, uint performerGuid, List<uint> remaining, TurnSnapshot snap, PartyKit party)
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
            var enemyGuid = c.Target.Guid;
            return LaterAllyBeforeEnemy(remaining, performerGuid, enemyGuid, snap, party);
        }

        internal static uint FirstEnemyInOrder(List<uint> remaining, TurnSnapshot snap)
        {
            if (remaining == null || snap == null)
                return 0;
            for (var i = 0; i < remaining.Count; i++)
            {
                var g = remaining[i];
                if (g == 0 || !snap.IsEnemy(g))
                    continue;
                var actor = snap.Find(g);
                if (actor != null && actor.Living && !actor.Corpse)
                    return g;
            }
            return 0;
        }

        private static bool LaterAllyBeforeEnemy(List<uint> remaining, uint performerGuid, uint enemyGuid, TurnSnapshot snap, PartyKit party)
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
                var actor = snap.Find(g);
                if (actor != null && actor.Living)
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
            if (c == null || c.Target == null || c.Target.Guid == 0 || focus == null)
                return false;
            var guid = c.Target.Guid;
            if (!focus.IsMustKillFirst(guid) && !focus.IsPriority(guid))
                return false;
            if (ComboMarkWaste(c) || IsZeroDamageMark(c.SkillId, c.Preview))
                return false;
            if (c.Preview != null && c.Preview.Damage > 0f)
                return true;
            return IsTaprootTap(c);
        }

        // Crush self-heals on Combo. That is not a skill heal; do not walk for it.
        internal static bool IsAllyHealSkill(string skillId, SkillSnap def)
        {
            if (string.IsNullOrEmpty(skillId) || IsPassHeal(skillId))
                return false;
            if (def != null)
            {
                if (def.IsMove || def.IsItemSkill)
                    return false;
                if (!def.IsFriendly)
                    return false;
                var role = def.Role;
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

        // Last HP bar only. Healthless Taproot is ignored. A bar still holding
        // Death's Door armor is never the finish.
        internal static bool LastKillableFinish(PreviewScore preview, TargetInfo target, int killableEnemies)
        {
            if (preview == null || target == null || target.Corpse || target.Healthless)
                return false;
            if (killableEnemies > 1)
                return false;
            if (target.DeathArmor)
                return false; // armor absorbs this hit; the fight does not end
            if (preview.Kills)
                return true;
            return preview.Damage > 0f && (target.Hp <= 0.05f || target.DeathsDoor);
        }

        private static void ApplyKitSafety(List<ScoredAction> candidates, TargetInfo performer, int livingEnemies, bool partyDoor, PartyKit party, uint performerGuid, TurnSnapshot snap = null)
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
                if (party != null && c.Target != null && c.Target.Guid != 0)
                {
                    var guid = c.Target.Guid;
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
                    Party = party,
                    Snap = snap
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

        private static void ClampPreviewToLivingHits(PreviewScore preview, TurnSnapshot snap, uint performerGuid)
        {
            if (preview == null || !preview.Ok)
                return;
            int livingHits;
            bool kills;
            var live = SumLivingHitDamage(preview, guid => IsLivingEnemyHit(snap, guid, performerGuid),
                out livingHits, out kills);
            if (livingHits <= 0)
                return;
            preview.Damage = live;
            if (!kills && preview.Hits != null)
            {
                for (var i = 0; i < preview.Hits.Count; i++)
                {
                    var hit = preview.Hits[i];
                    if (hit == null || hit.Damage <= 0f || !IsLivingEnemyHit(snap, hit.Guid, performerGuid))
                        continue;
                    var bar = snap.Find(hit.Guid);
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

        private static bool IsLivingEnemyHit(TurnSnapshot snap, uint guid, uint performerGuid)
        {
            if (guid == 0 || guid == performerGuid)
                return false;
            if (!snap.IsEnemy(guid))
                return false;
            var actor = snap.Find(guid);
            if (actor == null)
                return false;
            if (actor.Corpse)
                return false;
            return actor.Living;
        }

        // Flashing Daggers (m_IsMultiHit) can HitGuids a corpse + a live enemy
        // while another click of the same skill, or Pick/Thrown, hits only living.
        private static void ApplyCorpseSplash(List<ScoredAction> candidates, TurnSnapshot snap)
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
                CountSplashHits(c, snap, out live, out dead);
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
                CountSplashHits(c, snap, out live, out dead);
                var bestLive = live;
                for (var j = 0; j < candidates.Count; j++)
                {
                    var o = candidates[j];
                    if (o.Kind != SkillKind.Attack
                        || !string.Equals(o.SkillId, c.SkillId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    int ol;
                    int od;
                    CountSplashHits(o, snap, out ol, out od);
                    if (ol > bestLive)
                        bestLive = ol;
                }
                var dot = 0f;
                if (c.Preview != null)
                    dot = c.Preview.ApplyBleed + c.Preview.ApplyBlight + c.Preview.ApplyBurn;
                var delta = CorpseSplashDelta(live, dead, bestLive, cleanLiving, dot);
                if (delta == 0f)
                    continue;
                c.Score += delta;
                if (string.IsNullOrEmpty(c.NoteReason))
                    c.NoteReason = "corpse_splash";
            }
        }

        internal static float CorpseSplashDelta(
            int livingHits,
            int corpseHits,
            int bestLiveSameSkill,
            bool cleanLivingHitExists,
            float applyDot = 0f)
        {
            if (corpseHits <= 0)
                return 0f;
            if (bestLiveSameSkill > livingHits)
                return -80f;
            // Two living hits are the reward. Do not veto that cone because a
            // corpse is also in it — Pick would then beat a real AoE.
            if (livingHits >= 2)
                return 0f;
            // wiki DoT finish: blight through a corpse still tags the Cluster.
            if (applyDot > 0.05f && livingHits >= 1)
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

        private static void CountSplashHits(ScoredAction c, TurnSnapshot snap, out int living, out int corpses)
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
                    CountOneHit(snap.Find(guid), ref living, ref corpses);
                }
            }
            if (c.Target != null && c.Target.Guid != 0 && seen.Add(c.Target.Guid))
            {
                if (c.Target.Corpse)
                    corpses++;
                else if (c.EnemyTarget && !c.Target.Healthless)
                    living++;
            }
        }

        private static void CountOneHit(TargetInfo actor, ref int living, ref int corpses)
        {
            if (actor == null)
                return;
            if (actor.Corpse)
            {
                corpses++;
                return;
            }
            if (actor.Living)
                living++;
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
                var clickStack = c.Target != null && c.Target.Guid != 0
                                 && focus.IsLibrarianStack(c.Target.Guid);
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
            var clickGuid = c.Target != null ? c.Target.Guid : 0u;
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
                    && c.Target != null && c.Target.Guid != 0)
                {
                    var classId = c.Target.ClassId;
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
                    var undo = StalkDotFinishUndo(
                        true,
                        true,
                        c.Target != null && c.Target.Corpse,
                        dot,
                        hits);
                    if (undo != 0f)
                    {
                        c.Score += undo;
                        if (string.IsNullOrEmpty(c.NoteReason) || c.NoteReason == "corpse_splash")
                            c.NoteReason = "stalk_dot_finish";
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
        private static void ApplyLaterActNotes(List<ScoredAction> candidates, EnemyFocus focus, TargetInfo performer)
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

            var performerBody = performer;
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
                if (bodyP2 && c.EnemyTarget && c.Target != null && c.Target.Guid != 0)
                {
                    var classId = c.Target.ClassId;
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
            if (c.Target.Guid != 0 && focus.IsMustKillFirst(c.Target.Guid)
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

        private static void ApplyRankWalks(List<ScoredAction> candidates, TargetInfo performer, TurnSnapshot snap, EnemyFocus focus, int livingEnemies, uint performerGuid, PartyKit party, MemoryState memory)
        {
            if (candidates == null || performer == null)
                return;
            if (memory.ReachWalked(performerGuid))
                return;

            var body = performer;
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
                    FallIsLive(snap),
                    RankIsBack(rank),
                    HasTauntLaunchFromBack(performer, snap),
                    HasComboStripLegal(candidates),
                    focus != null && focus.HasLivingAltarMustKill()))
            {
                ApplyWalkBack(candidates, performer, "fall_rank");
                if (HasRankWalk(candidates))
                    return;
            }

            if (memory.PartyReachWalked())
                return;

            var crisis = PartyHasDeathsDoor(snap) || AllyInCrisis(snap);
            var reachTarget = ReachWalkTarget(candidates, performer, snap, focus, livingEnemies, performerGuid, party, memory);
            if (reachTarget != null)
                ApplyReachReposition(candidates, performer, reachTarget, party, crisis, snap);
            // Librarian: a hero who cannot hit him walks when that puts more of
            // the party on him (Categorize; LibrarianPlay).
            LibrarianPlay.ApplyPartyReachWalk(candidates, performer, snap, crisis);
        }

        private static void ApplyWalkBack(List<ScoredAction> candidates, TargetInfo performer, string reason)
        {
            if (candidates == null || performer == null)
                return;
            var current = performer.Rank;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Kind != SkillKind.Move || c.Target == null || c.Target.Guid == 0)
                    continue;
                var dest = c.Target.Rank;
                if (dest <= current)
                    continue;
                c.ReachReposition = true;
                c.MustRankWalk = true;
                c.NoteReason = reason;
                c.Score = dest >= 3 ? 195f : dest >= 2 ? 188f : 170f;
            }
        }

        private static bool LibrarianReachWalkLegal(List<ScoredAction> candidates)
        {
            if (candidates == null)
                return false;
            for (var i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].ReachReposition && candidates[i].NoteReason == "librarian_reach")
                    return true;
            }
            return false;
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
                if (c.Target.Guid == 0 || c.Preview == null || !c.Preview.Kills)
                    continue;
                if (focus.IsMustKillFirst(c.Target.Guid))
                    return true;
            }
            return false;
        }

        private static bool FallIsLive(TurnSnapshot snap)
        {
            if (snap == null)
                return false;
            foreach (var hero in snap.Heroes)
            {
                if (hero == null || !hero.Living || hero.Corpse)
                    continue;
                var info = hero;
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

        private static bool HasTauntLaunchFromBack(TargetInfo performer, TurnSnapshot snap)
        {
            if (performer == null || snap == null)
                return false;
            var skillIds = performer.SkillIds;
            if (skillIds == null)
                return false;
            for (var i = 0; i < skillIds.Count; i++)
            {
                var id = skillIds[i];
                if (IsHarvestHungerGuard(id))
                    continue;
                var def = snap.Skill(id);
                if (def == null)
                    continue;
                if (def.IsItemSkill || def.IsMove)
                    continue;
                var role = def.Role;
                if (role == null || !role.AppliesTaunt)
                    continue;
                if (def.Launches(3))
                    return true;
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
                if (guid == 0 || (c.Target != null && c.Target.Guid != 0 && guid == c.Target.Guid))
                    continue;
                if (focus.IsMustKillFirst(guid) || focus.IsPriority(guid))
                    return true;
            }
            return false;
        }

        private static bool IsSighLungTarget(ScoredAction c)
        {
            if (c == null || !c.EnemyTarget || c.Target == null || c.Target.Guid == 0)
                return false;
            var classId = c.Target.ClassId;
            return IdHasClass(classId, "lungs_front") || IdHasClass(classId, "lungs_back");
        }

        private static bool IdHasClass(string id, string key)
        {
            return !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(key)
                   && id.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // CSV overstress `meltdown` (80%): meltdown_health drops HP to 10%,
        // stress to 1. One more stress point from 9 is that. Shackles of Denial:
        // every lock's Crippling Doubt is stress_damage_1 + 25% one more on two
        // heroes (Overwhelming Anguish: 3), so 8 is one Doubt away.
        internal static bool MeltdownImminent(TargetInfo target, EnemyFocus focus)
        {
            if (target == null || !target.Living || target.Corpse)
                return false;
            if (target.Stress >= 9f)
                return true;
            return focus != null && focus.DenialLocks && target.Stress >= 8f;
        }

        // CSV shared_lost_soul_chirurgeon trinkets: chirurgeon_appalling_apron_01
        // turn_start hot_heal_small_if_dmgdot, _02 turn_end remove_all_bleed/blight/burn.
        // A DoT on him ticks once, then turns into a HoT that lifts him off Death's
        // Door (deaths_door_exit_effects re-adds the armor). Log surgeon_gaunt_102a:
        // two Poison Darts, HoT 2-4, he left Death's Door twice. When a hit on him
        // with no DoT does at least as much direct damage, it ranks above the DoT.
        private static void ApplyChirurgeonDotWaste(List<ScoredAction> candidates, EnemyFocus focus, TurnSnapshot snap)
        {
            if (candidates == null || focus == null || focus.ChirurgeonGuid == 0)
                return;
            ScoredAction direct = null;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!IsChirurgeonHit(c, focus) || AppliesDamageDot(c, snap))
                    continue;
                if (direct == null || c.Score > direct.Score)
                    direct = c;
            }
            if (direct == null)
                return;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!IsChirurgeonHit(c, focus) || c.Preview.Kills || !AppliesDamageDot(c, snap))
                    continue;
                if (direct.Preview.Damage + 0.05f < c.Preview.Damage || c.Score < direct.Score)
                    continue;
                c.Score = direct.Score - 1f;
            }
        }

        private static bool IsChirurgeonHit(ScoredAction c, EnemyFocus focus)
        {
            return c != null && !c.IsItem && c.EnemyTarget && c.Kind == SkillKind.Attack
                   && c.TargetGuid == focus.ChirurgeonGuid
                   && c.Preview != null && c.Preview.Damage > 0f;
        }

        // He leaves Death's Door on any heal (his own Leucotomy round, an Apron HoT)
        // and gets the armor back. Log surgeon_gaunt_102a r13: Artemisia on a
        // 17% Grave Robber over a legal kill on him at 0 HP; his Bone Saw then put
        // the Plague Doctor on Death's Door. Kill him unless the heal is urgent.
        internal static bool ChirurgeonKillBeatsHeal(ScoredAction attack, ScoredAction heal, EnemyFocus focus)
        {
            if (attack == null || heal == null || focus == null || focus.ChirurgeonGuid == 0)
                return false;
            if (attack.TargetGuid != focus.ChirurgeonGuid || attack.Preview == null || !attack.Preview.Kills)
                return false;
            return !TargetNeedsUrgentHeal(heal.Target);
        }

        // A sure kill on the must-kill beats a heal that is not urgent (no Death's
        // Door, no DoT death, above 15% HP). Logged fight
        // lost_battalion_mash_316: Bishop (must_kill, Serve Once More) at 1 HP from
        // round 2; r5 Grave Robber at 10/35 took Artemisia (+12) over a 100% Poison
        // Dart kill on him. He lived to round 12, raised the Arbalist twice and kept
        // Smiting; the party wiped.
        internal const float MustKillFinishHitMin = 0.9f;

        internal static bool MustKillFinishBeatsHeal(ScoredAction attack, ScoredAction heal, EnemyFocus focus)
        {
            if (attack == null || heal == null || focus == null || !focus.HasMustKillFirst)
                return false;
            if (attack.IsItem || attack.Kind != SkillKind.Attack || !attack.EnemyTarget || attack.Target == null)
                return false;
            var p = attack.Preview;
            if (p == null || !p.Kills || p.HitChance < MustKillFinishHitMin)
                return false;
            var t = attack.Target;
            if (t.Corpse || t.Healthless || t.DeathArmor || attack.Stealthed || !focus.IsMustKillFirst(t.Guid))
                return false;
            return !TargetNeedsUrgentHeal(heal.Target);
        }

        private static bool AppliesDamageDot(ScoredAction c, TurnSnapshot snap)
        {
            var p = c.Preview;
            if (p != null && p.ApplyBleed + p.ApplyBlight + p.ApplyBurn > 0.05f)
                return true;
            var def = snap != null ? snap.Skill(c.SkillId) : null;
            var role = def != null ? def.Role : null;
            return role != null && (role.Bleed || role.Blight || role.Burn);
        }

        // Highest-stress ally a legal skill stress-heal reaches (items keep their own policy).
        private static ScoredAction BestMeltdownGuard(List<ScoredAction> candidates, EnemyFocus focus)
        {
            ScoredAction best = null;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.IsItem || c.EnemyTarget || c.Kind != SkillKind.Support || !LooksLikeStressSupport(c.SkillId))
                    continue;
                if (!MeltdownImminent(c.Target, focus))
                    continue;
                if (best == null || c.Target.Stress > best.Target.Stress
                    || (c.Target.Stress == best.Target.Stress && c.Score > best.Score))
                    best = c;
            }
            return best;
        }

        internal static bool IsMeltdownGuardClick(ScoredAction c, EnemyFocus focus)
        {
            return c != null && !c.IsItem && !c.EnemyTarget && c.Kind == SkillKind.Support
                   && LooksLikeStressSupport(c.SkillId) && MeltdownImminent(c.Target, focus);
        }

        internal static bool PassOverstresses(string skillId, TargetInfo performer)
        {
            return performer != null
                   && string.Equals(skillId, "pass_stress", StringComparison.OrdinalIgnoreCase)
                   && performer.Stress + 2f >= 10f;
        }

        // CSV pass_heal: performer_effects heal_20pct, stress_damage_2 (legal
        // under 25% HP, cooldown 3). At 8+ stress the +2 is Overstress at 10:
        // overstress_data_export meltdown (m_Chance 0.8) is meltdown_health
        // (m_HealthDamageDownToPercent 0.1) plus meltdown_db_resist_debuff
        // (death resist -0.15 until the next inn) and a 40% meltdown quirk;
        // resolute (0.2) heals 67%. Off Death's Door the Rest heals 20% and the
        // Meltdown then drops the hero to 10% of max HP, below where they began
        // unless they were already under 10%. In the logs Rest was the last
        // stress hit of 39 of 175
        // Overstresses, more than any enemy skill; 23 were off Death's Door
        // (17 Meltdowns, e.g. MAA 11/55 -> 4, GR 7/29 -> 3, PD 6/30 -> 3; 5
        // resolute). On Death's Door the Rest still lifts the hero off it
        // (Meltdown lands at 10%), so it stays a heal there.
        internal const float RestIntoMeltdownPenalty = 300f;
        internal const float RestStress = 2f;

        internal static bool RestIntoMeltdown(string skillId, TargetInfo performer)
        {
            if (performer == null || !IsPassHeal(skillId) || performer.DeathsDoor)
                return false;
            if (performer.Stress + RestStress < 10f)
                return false;
            return performer.HpPct >= 0.10f;
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

        private static void ApplyHarvestHungerGuard(List<ScoredAction> candidates, TargetInfo performer, TurnSnapshot snap, EnemyFocus focus)
        {
            if (candidates == null || performer == null || snap == null || focus == null)
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

            var selfHungry = performer.CountToken("harvest_hunger") > 0;
            var allyHungry = false;
            foreach (var hero in snap.Heroes)
            {
                if (hero == null || hero.Guid == performer.Guid)
                    continue;
                if (!hero.Living || hero.Corpse)
                    continue;
                if (hero.CountToken("harvest_hunger") > 0)
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
                    if (performer.Blind)
                        c.Score -= 40f;
                }
                else if (allyHungry)
                    c.Score += 80f;
            }
        }

        private const float CoverHpPct = 0.45f;

        private static bool AllyHasRiposte(TurnSnapshot snap, uint performerGuid)
        {
            if (snap == null)
                return false;
            foreach (var hero in snap.Heroes)
            {
                if (hero == null || !hero.Living || hero.Corpse)
                    continue;
                if (hero.Guid == performerGuid)
                    continue;
                if (hero.Riposte)
                    return true;
            }
            return false;
        }

        private static bool AllyNeedsCover(TurnSnapshot snap)
        {
            if (snap == null)
                return false;
            foreach (var hero in snap.Heroes)
            {
                if (hero == null || !hero.Living || hero.Corpse)
                    continue;
                var body = hero;
                if (body.DeathsDoor)
                    return true;
                if (body.HpPct <= CoverHpPct && !KitSafety.WantsToStayLow(body))
                    return true;
            }
            return false;
        }

        private static bool AllyInCrisis(TurnSnapshot snap)
        {
            if (snap == null)
                return false;
            foreach (var hero in snap.Heroes)
            {
                if (hero == null || !hero.Living || hero.Corpse)
                    continue;
                var body = hero;
                if (body.DeathsDoor)
                    return true;
                if (body.HpPct <= 0.35f && !KitSafety.WantsToStayLow(body))
                    return true;
            }
            return false;
        }

        private static bool PartyHasDeathsDoor(TurnSnapshot snap)
        {
            if (snap == null)
                return false;
            foreach (var hero in snap.Heroes)
            {
                if (hero == null || !hero.Living || hero.Corpse)
                    continue;
                if (hero.DeathsDoor)
                    return true;
            }
            return false;
        }

        internal static bool IsPassHeal(string skillId)
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
            if (RestIntoMeltdown(skillId, target))
                return false;
            if (TargetNeedsUrgentHeal(target))
                return true;
            return target.HpPct <= 0.35f && !KitSafety.WantsToStayLow(target);
        }

        // Skill heal spent (BM limit 3). Food/bandage/antivenom that still heal must
        // fire on Death's Door. Do not buff (Ounce of Prevention) while someone is dying.
        private static void ApplyCrisisStabilize(List<ScoredAction> candidates, TurnSnapshot snap, EnemyFocus focus = null)
        {
            if (candidates == null || snap == null)
                return;
            var door = PartyHasDeathsDoor(snap);
            var crisis = AllyInCrisis(snap);
            if (!door && !crisis)
                return;
            var skillHeal = HasLegalCrisisHeal(candidates);
            var passHeal = HasLegalPassHeal(candidates);
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                // Bolster on a hero about to Meltdown is not a buff: Meltdown
                // drops HP to 10%, which is the crisis we are stabilizing.
                if (c.Kind == SkillKind.Support && !c.IsItem && (door || crisis)
                    && !(!c.EnemyTarget && LooksLikeStressSupport(c.SkillId) && MeltdownImminent(c.Target, focus)))
                    c.Score -= 80f;
                // Laudanum while Rest / BM is legal — stabilize HP first. Only for an item that
                // spends the turn: every CSV combat item is m_IsFreeAction, so a free stress item
                // goes first and the heal still follows in the same turn (logs: laudanum
                // / spring water vetoed this way on 36 hero-turns at 8-9 stress, mostly while
                // the holder Rested instead).
                if (c.IsItem && !c.FreeAction && c.Item != null && (door || crisis)
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

        // CSV pass_heal: heal_20pct, stress_damage_2. Rest at 8 stress overstresses (10: Meltdown 0.8,
        // HP to 10%, death resist -0.15, or Resolute). A free stress item (laudanum stress_heal_1,
        // m_IsFreeAction) on that hero first takes him to 7 and the Rest lands at 9. Item-use scoring
        // only: the Rest itself is scored elsewhere.
        internal static void ApplyStressBeforeRest(List<ScoredAction> candidates)
        {
            if (candidates == null)
                return;
            foreach (var rest in candidates)
            {
                if (rest.IsItem || rest.EnemyTarget || rest.Target == null || rest.Target.Corpse || !IsPassHeal(rest.SkillId))
                    continue;
                if (!RestoresHp(rest.Preview))
                    continue;
                var t = rest.Target;
                if (t.Stress + RestStress < 10f || t.Stress - 1f + RestStress >= 10f)
                    continue;
                foreach (var c in candidates)
                {
                    if (!c.IsItem || !c.FreeAction || c.Item == null || c.EnemyTarget || c.Target == null || c.Target.Guid != t.Guid)
                        continue;
                    if (c.Item.Reason == null || c.Item.Reason.IndexOf("stress", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    c.Item.UseNow = true;
                    c.Item.Crisis = true;
                    c.Item.Score = Math.Max(c.Item.Score, 60f);
                    c.Item.Reason = "item_stress_before_rest";
                    c.Score = c.Item.Score;
                }
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
                if (RestIntoMeltdown(c.SkillId, c.Target))
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
                // Rest heals only the performer: it does not stop the healer
                // walking back to a rank where the ally heal launches (a
                // logged round: PD on rank index 1, off the Medicine
                // launch ranks 3-4, Rested at 6/30 while the MAA sat on Death's
                // Door; the swap onto index 2 was legal and scored -1000).
                if (IsPassHeal(c.SkillId))
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

        // Battlefield Medicine on the Plague Doctor herself while Rest heals her
        // too. CSV pd_battlefield_medicine(_u): m_Limit 3 per fight, friendly,
        // target ranks 1-4, heal 20% (_u 25%) under 50% HP, remove_all_dots.
        // pass_heal: heal_20pct + stress_damage_2, under 25% HP, cooldown 3,
        // no use limit. Logs: 74 of 346
        // Medicine uses were on the PD herself; 48 of them had Rest on her
        // legal and within 2 points (e.g. 293 vs 291 on Death's Door), and the
        // charges then ran out in 29 of those fights. Replaying with Rest
        // instead (stress <= 5, so Rest leaves her at 7 or less): 27 charges
        // kept, 14 of them reach an ally on Death's Door / lethal DoT / <=20%
        // after the real third charge was gone (one logged fight: PD
        // Medicined herself over the Vestal on Death's Door, who died to her
        // bleed). Not when her own DoT tick would put her back on 0 HP after
        // the Rest (Medicine clears it), nor when the Rest overstresses.
        internal const float RestOverMedicineMaxStress = 5f;
        internal const float RestOverMedicineGap = 10f;

        internal static void ApplyRestOverSelfMedicine(List<ScoredAction> candidates, TargetInfo performer)
        {
            if (candidates == null || performer == null || performer.Guid == 0)
                return;
            if (performer.Stress > RestOverMedicineMaxStress)
                return;
            ScoredAction rest = null;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c == null || c.IsItem || c.EnemyTarget || !IsPassHeal(c.SkillId))
                    continue;
                if (c.Target == null || c.Target.Guid != performer.Guid || !RestoresHp(c.Preview))
                    continue;
                if (RestIntoMeltdown(c.SkillId, performer))
                    continue;
                rest = c;
                break;
            }
            if (rest == null)
                return;
            // Her DoT ticks before her next action; Medicine strips it, Rest does not.
            var afterRest = Math.Max(0f, performer.Hp) + rest.Preview.Heal;
            if (performer.NextDot > 0.05f && performer.NextDot + 0.05f >= afterRest)
                return;
            var docked = false;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c == null || c.IsItem || c.EnemyTarget || c.Kind != SkillKind.Heal)
                    continue;
                if (!IsUseLimitedAllyHeal(c.SkillId))
                    continue;
                if (c.Target == null || c.Target.Guid != performer.Guid)
                    continue;
                if (c.Score > rest.Score - RestOverMedicineGap)
                {
                    c.Score = rest.Score - RestOverMedicineGap;
                    docked = true;
                }
            }
            if (docked)
                rest.NoteReason = "rest_keeps_medicine";
        }

        // CSV m_Limit heals that can target allies (not self-only like Artemisia).
        internal static bool IsUseLimitedAllyHeal(string skillId)
        {
            return KitSafety.IdHas(skillId, "battlefield_medicine");
        }

        private static void ApplyHealReposition(List<ScoredAction> candidates, TargetInfo performer, TurnSnapshot snap)
        {
            if (candidates == null || performer == null)
                return;
            var heals = BlockedHealSkills(performer, snap);
            if (heals.Count == 0)
                return;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Kind != SkillKind.Move || c.Target == null || c.Target.Guid == 0)
                    continue;
                var dest = c.Target.Rank;
                var helps = false;
                for (var h = 0; h < heals.Count; h++)
                {
                    if (heals[h].Launches(dest))
                    {
                        helps = true;
                        break;
                    }
                }
                if (!helps)
                    continue;
                c.HealReposition = true;
                c.Score = 200f;
            }
        }

        // Last living enemy behind corpses, or a must-kill (Librarian) this
        // hero cannot damage from the current rank. 0-damage Combo marks do not count.
        internal static bool HasDamagingHitOn(List<ScoredAction> candidates, uint guid)
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
                if (c.Target.Guid == 0 || !c.Target.Living)
                    continue;
                if (guid != 0 && c.Target.Guid != guid)
                    continue;
                if (c.Preview != null && c.Preview.Damage > 0f)
                    return true;
                if (IsTaprootTap(c))
                    return true;
            }
            return false;
        }

        // Chirurgeon wave (logged fight surgeon_gaunt_104a): MAA and
        // Jester could not reach him and passed five turns while the Urchin
        // and Yeoman killed the party. A deferred add that is only deferred
        // for priority beats an idle turn when this hero has no damaging
        // click on the must-kill.
        internal static ScoredAction OpenAddOverIdle(ScoredAction picked, List<ScoredAction> candidates, EnemyFocus focus)
        {
            if (picked == null || candidates == null || focus == null)
                return picked;
            var cultist = focus.CultistOpenOverIdle;
            if (!IsIdlePick(picked) && !(cultist && IsCultistFillerPick(picked)))
                return picked;
            // Altar of Denial Stealth: a click on the stealthed Altar is not a hit.
            if (HasDamagingMustKillHit(candidates, focus, cultist))
                return picked;
            ScoredAction best = null;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.IsItem || c.Kind != SkillKind.Attack || !c.EnemyTarget || c.Target == null)
                    continue;
                if (c.Target.Corpse || c.Target.Healthless || !c.Target.Living || c.Target.Riposte || c.Stealthed)
                    continue;
                // Cultists: a DoT open (Noxious Blast blight) is a hit too.
                if (c.Preview == null || !(c.Preview.Damage > 0f || (cultist && StalkClickHurts(c.Preview))))
                    continue;
                // Cultists: a Herald is never deferred, only add-scored; he is
                // as open as a deferred Evangelist / Deacon.
                if (!focus.IsOpenWhenUnreached(c.Target.Guid)
                    && !(cultist && !focus.IsDeferred(c.Target.Guid) && !focus.IsMustKillFirst(c.Target.Guid)))
                    continue;
                if (best == null || c.Score > best.Score)
                    best = c;
            }
            if (best == null)
                return picked;
            if (string.IsNullOrEmpty(best.NoteReason))
                best.NoteReason = "open_add";
            return best;
        }

        // Config [Combat] LastEnemyRecovery (default on). Plain field so the
        // decider stays pure; Plugin syncs it from the config.
        internal static bool LastEnemyRecovery = true;

        // Stress points weighed against HP when two recovery clicks compete.
        internal const float RecoverStressHp = 4f;

        // End-of-fight recovery, smallest safe version (notes/recovery.md).
        // One safe enemy left and this hero's pick is idle (pass, a support the
        // scorer rates as a loss, or a 0-damage click on the enemy): take a heal,
        // DoT removal or stress heal instead. Never replaces a damaging hit or a
        // kill, so the fight does not last a round longer. In the logs the
        // MAA passed (pass_stress, +2 stress to himself) or
        // guarded at -35 while Bolster on a 5+ stress ally was legal.
        internal static ScoredAction RecoverOverIdle(ScoredAction picked, List<ScoredAction> candidates, EnemyFocus focus, TurnSnapshot snap, TargetInfo performer, int livingEnemies, int killableEnemies, bool enabled)
        {
            if (!enabled || picked == null || candidates == null || snap == null)
                return picked;
            if (livingEnemies != 1 || killableEnemies != 1)
                return picked;
            if (!IsRecoveryIdle(picked))
                return picked;
            var last = LastLivingEnemy(snap);
            if (!IsSafeLastEnemy(last, focus)
                && !(IsMountainCultistLastEnemy(last, focus) && IsMountainRecoveryIdle(picked)))
                return picked;
            // Death's Door belongs to the crisis gates (heal_deaths_door, items).
            if (PartyHasDeathsDoor(snap))
                return picked;
            // A guard covers a low ally; a pass (pass_stress: +2 stress to the
            // passer, CSV pass_data) covers nobody, so a stress heal beats it.
            var holdStress = AllyInCrisis(snap) && picked.Kind != SkillKind.Pass;
            var best = BestRecovery(candidates, performer, holdStress);
            if (best == null || best == picked)
                return picked;
            if (string.IsNullOrEmpty(best.NoteReason))
                best.NoteReason = "recover";
            return best;
        }

        // Idle for recovery: a pass that is not pass_heal, a support the scorer
        // rates below 0 (IsIdlePick), or a 0-damage click on an enemy (Tracking
        // Shot / Blinding Gas). Cited picks (meltdown_guard, blind_artillery,
        // fall_taunt ...) carry a NoteReason and are left alone.
        internal static bool IsRecoveryIdle(ScoredAction picked)
        {
            if (picked == null || picked.IsItem || !string.IsNullOrEmpty(picked.NoteReason))
                return false;
            if (picked.HealReposition || picked.ReachReposition || picked.MustRankWalk)
                return false;
            if (picked.Kind == SkillKind.Pass)
                return !IsPassHeal(picked.SkillId);
            if (picked.Kind == SkillKind.Support)
                return picked.Score < 0f;
            if (picked.Kind == SkillKind.Attack && picked.EnemyTarget)
                return picked.Preview != null && picked.Preview.Damage <= 0f && !picked.Preview.Kills;
            return false;
        }

        private static TargetInfo LastLivingEnemy(TurnSnapshot snap)
        {
            TargetInfo found = null;
            foreach (var e in snap.Enemies)
            {
                if (e == null || !e.Living || e.Corpse)
                    continue;
                if (found != null)
                    return null;
                found = e;
            }
            return found;
        }

        // Not a boss / summoner / reviver / must-kill / commander, no scripted
        // boss mode up, and not an ignited fanatic (logs: whipper_ignited 32,
        // blind_shaman_ignited 41 damage a round; a guard or blind matters there).
        internal static bool IsSafeLastEnemy(TargetInfo enemy, EnemyFocus focus)
        {
            if (enemy == null || !enemy.Living || enemy.Corpse || enemy.Healthless)
                return false;
            if (KitSafety.IdHas(enemy.ClassId, "ignited"))
                return false;
            if (focus == null)
                return true;
            if (focus.DenialLocks || focus.ExemplarUp || focus.LeviathanHandUp || focus.ReachPhase2 || focus.ReachPhase3
                || focus.BurstBeforeEvolve || focus.ChirurgeonGuid != 0 || focus.HasMustKillFirst)
                return false;
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var t = focus.Enemies[i];
                if (t == null || t.Guid != enemy.Guid)
                    continue;
                if (t.Boss || t.Summons || t.Resurrects || t.MustKillFirst || t.Commander)
                    return false;
            }
            return true;
        }

        // Best legal recovery click by value: HP the preview restores, DoTs a
        // Battlefield Medicine strips, stress a Bolster-type skill heals. CSV
        // (hero_maa / effect / condition exports): Bolster heals the target only
        // at stress >= 5 (target_at_stress_heal_threshold) and the MAA himself
        // only at >= 50% stress (performer_at_stress_heal_threshold); maa_bolster_u
        // heals 2. holdStress: no stress heals (an ally at 35% HP or lower keeps
        // the guard the idle pick gave).
        internal static ScoredAction BestRecovery(List<ScoredAction> candidates, TargetInfo performer, bool holdStress)
        {
            ScoredAction best = null;
            var bestValue = 0f;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                var value = RecoveryValue(c, performer, holdStress);
                if (value <= 0f)
                    continue;
                if (best == null || value > bestValue || (value == bestValue && c.Score > best.Score))
                {
                    best = c;
                    bestValue = value;
                }
            }
            return best;
        }

        internal static float RecoveryValue(ScoredAction c, TargetInfo performer, bool holdStress)
        {
            if (c == null || c.IsItem || c.EnemyTarget || c.Kind == SkillKind.Pass || c.Kind == SkillKind.Move)
                return 0f;
            var t = c.Target;
            if (t == null || !t.Living || t.Corpse)
                return 0f;
            var value = 0f;
            if (c.Kind == SkillKind.Heal && RestoresHp(c.Preview) && !KitSafety.WantsToStayLow(t))
                value += Math.Min(c.Preview.Heal, t.HpMax > 0f ? Math.Max(0f, t.HpMax - t.Hp) : c.Preview.Heal);
            // CSV pd_battlefield_medicine: target_apply_limit_effects remove_all_dots.
            if (KitSafety.IdHas(c.SkillId, "battlefield_medicine") && t.BleedDot + t.BlightDot + t.BurnDot + t.NextDot > 0.05f)
                value += 2f * Math.Max(1f, t.NextDot);
            if (!holdStress && c.Kind == SkillKind.Support && LooksLikeStressSupport(c.SkillId))
            {
                var per = c.SkillId.EndsWith("_u", StringComparison.OrdinalIgnoreCase) ? 2f : 1f;
                var stress = 0f;
                if (t.Stress >= 5f)
                    stress += Math.Min(per, t.Stress);
                if (KitSafety.IdHas(c.SkillId, "bolster") && performer != null && performer.Guid != t.Guid && performer.Stress >= 5f)
                    stress += Math.Min(per, performer.Stress);
                value += stress * RecoverStressHp;
            }
            return value;
        }

        // The plain pass is never free: CSV pass_data pass_stress is
        // performer_effects stress_damage_2 (m_StressDamage 2), no condition,
        // no cooldown. In the logs the starter party passed ~9
        // times a run (scored -50) while a guard / Bolster / Ounce (-54 to
        // -145), a mark, a chip on a deferred add or a hit on a clogging
        // corpse cost nothing. When the pick is that pass, take the first
        // free click by tier, best score within a tier:
        //   1 guard on an ally at 35% HP or less (or Death's Door)
        //   2 a damaging / DoT hit on a living enemy
        //   3 a hit that destroys a corpse in front of a living enemy now
        //     (corpse class: health_max, m_DeathRound 3; Team removes the dead
        //     actor and closes the ranks behind it)
        //   4 a support: stress heal, guard, buff
        //   5 a 0-damage mark (Blind / Combo), a hit on a target dying to DoT
        //   6 a chip on a corpse in front of a living enemy
        // Never: moves or attacks that move the performer (formation), items,
        // limited-use skills (m_Limit: Battlefield Medicine, Artemisia ...),
        // cursed skills (skill_modifier curse riders: Vulnerable, Weak, Blind,
        // stress_damage_1), Riposte / stealthed / healthless (Taproot) targets,
        // Librarian stacks, a lung kill without inflate, a kill on a deferred
        // boss part (Denial locks), a self-taunt while the performer is low.
        // If nothing is left the pass stands.
        internal static ScoredAction FreeOverPass(ScoredAction picked, List<ScoredAction> candidates, EnemyFocus focus, TurnSnapshot snap, TargetInfo performer, int livingEnemies)
        {
            if (picked == null || candidates == null || !IsStressPass(picked))
                return picked;
            var allyLow = snap != null && AllyInCrisis(snap);
            var livingRanks = LivingEnemyRanks(snap);
            ScoredAction best = null;
            var bestTier = int.MaxValue;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c == picked)
                    continue;
                var tier = FreeTier(c, focus, snap, performer, livingEnemies, allyLow, livingRanks);
                if (tier <= 0)
                    continue;
                if (best == null || tier < bestTier || (tier == bestTier && c.Score > best.Score))
                {
                    best = c;
                    bestTier = tier;
                }
            }
            if (best == null)
                return picked;
            best.NoteReason = FreeTierReasons[bestTier];
            return best;
        }

        // Guard a hero on Death's Door (or at <=25% HP) instead of a non-kill swing.
        // CSV maa_defender: target_effects add_2_guard, performer_effects add_2_block
        // (_u: add_3_guard, add_2_block_plus), cooldown 1. Token guard
        // (token_data_export): consumed per redirected attack, lasts 3 turns,
        // removed when the guardian dies. Logs: 33 enemy hits on a guarded hero
        // landed on the MAA instead (single-target and AoE). On Death's Door every
        // hit rolls deathblow (hero resistance death 0.6, -0.1 per survived roll,
        // deathblow_chance_shaping_heroes), so a redirected hit is a death roll
        // not taken. ApplyCrisisStabilize docks every support -80 in a crisis,
        // which left Defender at -66 under every swing: logged non-lair
        // fights had 30 turns with a
        // healthy MAA, Defender legal on an ally at <=25% or on Death's Door, and
        // a swing picked; that ally then took 21 hits, 6 Death's Door entries and
        // 2 deaths before the MAA's next turn (spitting_distance r10:
        // Highwayman 9/39 died to two carrion bites while MAA at 29/60 crushed).
        // The guardian must not be in crisis himself; kills, heals, items, cited
        // boss gates, Taunt (it already pulls the hits) and rank walks stand.
        internal const float CoverGuardAllyHpPct = 0.25f;
        internal const float CoverGuardSelfHpPct = 0.35f;

        private static readonly HashSet<string> CoverGuardGates = new HashSet<string>(StringComparer.Ordinal)
        {
            "attack", "attack_over_idle", "any_over_negative_attack", "any", "setup", "open_add"
        };

        private static readonly HashSet<string> CoverGuardNotes = new HashSet<string>(StringComparer.Ordinal)
        {
            "peel", "open_add", "recover"
        };

        internal static ScoredAction CoverGuardOverSwing(ScoredAction picked, List<ScoredAction> candidates, TurnSnapshot snap, TargetInfo performer, string gate)
        {
            if (picked == null || candidates == null || snap == null || performer == null)
                return picked;
            if (!CoverGuardMayReplace(picked, gate))
                return picked;
            if (!CanCoverAlly(performer))
                return picked;
            ScoredAction best = null;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c == null || c.IsItem || c.EnemyTarget || c.Cursed || c.Kind != SkillKind.Support)
                    continue;
                if (!AppliesGuardTo(c))
                    continue;
                var t = c.Target;
                if (t == null || t.Guid == 0 || t.Guid == performer.Guid || !t.Living || t.Corpse)
                    continue;
                if (!NeedsCoverGuard(t))
                    continue;
                if (best == null || CoverBefore(t, best.Target))
                    best = c;
            }
            if (best == null)
                return picked;
            best.NoteReason = "cover_guard";
            return best;
        }

        // ---------- cultist_mountain_* (notes/cited-bosses.md, Cultists) ----------
        //
        // The no-retreat Deacon fight right before the mountain boss, no inn in
        // between: what matters is the stress (then HP) the party walks in with.
        // Logs (11 fights): 5 arrivals with a
        // hero at 9 stress; the Deacon dealt 103 of the 121 logged enemy stress
        // (Weight of Worlds 50, Sundering Steel 48, Exultation 5) and was the last
        // enemy alive in every fight. Kill order stays minions first (wiki; CSV
        // minion Worship heals him 30% and feeds Exultation).
        //
        // Taunt. CSV token taunt: m_ConsumeTypes forced_target; maa_retribution
        // add_2_taunt_nr, so it drags the next two single-target hits onto the
        // tank. The single-target stress in this fight is Sundering Steel
        // (stress_damage_2), Exultation (4 + 50% 1), Blood Commune (75% 1),
        // Enfeebling Miasma (25% 1), all target_ranks 1,2, and Clarion Call
        // (stress_damage_2, target_ranks 1-4). Weight of Worlds is multi-hit on
        // every rank (no taunt effect). So Taunt moves stress, it does not cut it:
        // 29 single-target hits (22 stress) landed on a taunting MAA, who arrived
        // at 9 twice (once from Sundering Steel 8 + Exultation 5 while
        // taunted). Riposte returns ~5.5 damage a Retribution (MAA ripostes, 10
        // single-target returns for 31), about a Crush, so blocking it does not
        // slow the kill. A taunt is only worth it when it shields a hero the
        // redirected hits would otherwise reach who is at least
        // MountainTauntStressMargin stress above the tank (two taunted hits are
        // ~3 stress: Sundering Steel 2, Exultation 4+).
        internal const float MountainTauntStressMargin = 3f;
        internal const float MountainTauntPenalty = 60f;

        internal static void ApplyMountainCultistTaunt(List<ScoredAction> candidates, TurnSnapshot snap, TargetInfo performer, EnemyFocus focus)
        {
            if (candidates == null || focus == null || !focus.MountainCultist)
                return;
            if (MountainTauntShields(snap, performer, focus))
                return;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c == null || c.IsItem || c.EnemyTarget || !KitSafety.IsSelfTaunt(c.SkillId))
                    continue;
                c.TauntBlocked = true;
                c.Score -= MountainTauntPenalty;
            }
        }

        // True when a hero the redirected single-target hits could reach is at
        // least MountainTauntStressMargin above the taunter: ranks 1-2 (game
        // ranks 0-1) always, any rank while a Herald (Clarion Call 1-4) lives.
        internal static bool MountainTauntShields(TurnSnapshot snap, TargetInfo performer, EnemyFocus focus)
        {
            if (snap == null || performer == null)
                return false;
            var heraldUp = false;
            if (snap.Enemies != null)
            {
                foreach (var e in snap.Enemies)
                {
                    if (e != null && e.Living && !e.Corpse && KitSafety.IdHas(e.ClassId, "cultist_herald"))
                        heraldUp = true;
                }
            }
            var covered = -1f;
            foreach (var h in snap.Heroes)
            {
                if (h == null || !h.Living || h.Corpse || h.Guid == performer.Guid)
                    continue;
                if (!heraldUp && (h.Rank < 0 || h.Rank > 1))
                    continue;
                if (h.Stress > covered)
                    covered = h.Stress;
            }
            return covered >= 0f && covered >= performer.Stress + MountainTauntStressMargin;
        }

        // Bolster. CSV maa_bolster: target stress_heal_1_target_at_threshold (at
        // stress >= 5, target_at_stress_heal_threshold) and performer
        // stress_heal_1_performer_at_threshold (MAA at >= 5); _u heals 2 each;
        // cooldown 1. Heroes arriving at the boss at 8-9 melted by round 3
        // (Denial note). Logs: 35 MAA turns here had Bolster legal and healing
        // 1+ (23 on a hero at 7+) and took a swing; meltdown_guard only fires at
        // 9 when Bolster already outscores the swing (one logged fight: PD sat at 9 for
        // seven MAA turns). One MAA swing is ~4 damage here, about a fifth of a
        // round of the Deacon (~3 party stress a round): a 2-point Bolster, or 1
        // point off a 7+ hero, is the better arrival.
        internal const float MountainBolsterTargetStress = 7f;
        internal const float MountainBolsterMinHeal = 2f;

        private static readonly HashSet<string> MountainBolsterGates = new HashSet<string>(StringComparer.Ordinal)
        {
            "attack", "attack_over_idle", "any_over_negative_attack", "any", "setup", "open_add"
        };

        internal static ScoredAction MountainBolsterOverSwing(ScoredAction picked, List<ScoredAction> candidates, TurnSnapshot snap, TargetInfo performer, EnemyFocus focus, string gate, int killableEnemies)
        {
            if (picked == null || candidates == null || snap == null || performer == null || focus == null || !focus.MountainCultist)
                return picked;
            if (gate == null || !MountainBolsterGates.Contains(gate))
                return picked;
            if (!MountainBolsterMayReplace(picked, killableEnemies, focus))
                return picked;
            // Death's Door / a hero at 25% or less: the crisis and cover gates own it.
            foreach (var h in snap.Heroes)
            {
                if (h == null || !h.Living || h.Corpse)
                    continue;
                if (h.DeathsDoor)
                    return picked;
                if (h.Guid != performer.Guid && h.HpPct > 0f && h.HpPct <= CoverGuardAllyHpPct && !KitSafety.WantsToStayLow(h))
                    return picked;
            }
            ScoredAction best = null;
            var bestHeal = 0f;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c == null || c.IsItem || c.EnemyTarget || c.Cursed || c.Kind != SkillKind.Support)
                    continue;
                if (!KitSafety.IdHas(c.SkillId, "bolster"))
                    continue;
                var t = c.Target;
                if (t == null || !t.Living || t.Corpse)
                    continue;
                var heal = BolsterStressHealed(c.SkillId, t, performer);
                if (heal < MountainBolsterMinHeal && !(heal > 0f && t.Stress >= MountainBolsterTargetStress))
                    continue;
                if (best == null || heal > bestHeal
                    || (heal == bestHeal && (t.Stress > best.Target.Stress || (t.Stress == best.Target.Stress && c.Score > best.Score))))
                {
                    best = c;
                    bestHeal = heal;
                }
            }
            if (best == null)
                return picked;
            best.NoteReason = "mountain_bolster";
            return best;
        }

        // Stress Bolster removes this click (CSV thresholds above).
        internal static float BolsterStressHealed(string skillId, TargetInfo target, TargetInfo performer)
        {
            if (target == null)
                return 0f;
            var per = !string.IsNullOrEmpty(skillId) && skillId.EndsWith("_u", StringComparison.OrdinalIgnoreCase) ? 2f : 1f;
            var heal = 0f;
            if (target.Stress >= 5f)
                heal += Math.Min(per, target.Stress);
            if (performer != null && performer.Guid != target.Guid && performer.Stress >= 5f)
                heal += Math.Min(per, performer.Stress);
            return heal;
        }

        // A non-kill swing, a stress pass or a filler support; not a kill, a
        // last-bar finish or a damaging Altar hit (ShouldOpenUtility), not a heal,
        // guard, item, walk or an allowed taunt.
        private static bool MountainBolsterMayReplace(ScoredAction picked, int killableEnemies, EnemyFocus focus)
        {
            if (!string.IsNullOrEmpty(picked.NoteReason) && picked.NoteReason != "peel" && picked.NoteReason != "open_add")
                return false;
            if (picked.IsItem || picked.HealReposition || picked.ReachReposition || picked.MustRankWalk)
                return false;
            if (picked.Kind == SkillKind.Attack && picked.EnemyTarget)
                return ShouldOpenUtility(picked.Preview, picked.Target, killableEnemies, focus);
            if (picked.Kind == SkillKind.Pass)
                return IsStressPass(picked);
            if (picked.Kind == SkillKind.Support && !picked.EnemyTarget)
                return picked.Score < 0f && !AppliesGuardTo(picked) && !KitSafety.IsSelfTaunt(picked.SkillId)
                       && !LooksLikeStressSupport(picked.SkillId);
            return false;
        }

        // Last-enemy recovery (RecoverOverIdle) normally skips a boss / must-kill
        // (a guard or a Blind matters against a big hitter). Here the Deacon is
        // the last enemy in 9 of 11 logged fights (a must-kill Cherub in the
        // others), so the rule never fired. A pass or a filler support costs the
        // same round either way: it becomes recovery. A 0-damage Blind / mark on
        // the enemy, a guard, a DoT and every damaging hit stand.
        internal static bool IsMountainCultistLastEnemy(TargetInfo enemy, EnemyFocus focus)
        {
            return focus != null && focus.MountainCultist && enemy != null && enemy.Living && !enemy.Corpse && !enemy.Healthless;
        }

        internal static bool IsMountainRecoveryIdle(ScoredAction picked)
        {
            if (!IsRecoveryIdle(picked))
                return false;
            if (picked.Kind == SkillKind.Pass)
                return true;
            return picked.Kind == SkillKind.Support && !picked.EnemyTarget && !AppliesGuardTo(picked);
        }

        internal static bool NeedsCoverGuard(TargetInfo t)
        {
            if (t == null || !t.Living || t.Corpse)
                return false;
            // Already guarded: Defender would only refresh the tokens.
            if (HasTokenId(t, "guard"))
                return false;
            if (t.DeathsDoor)
                return true;
            return t.HpPct > 0f && t.HpPct <= CoverGuardAllyHpPct && !KitSafety.WantsToStayLow(t);
        }

        internal static bool CanCoverAlly(TargetInfo performer)
        {
            if (performer == null || performer.DeathsDoor || performer.DiesToDot)
                return false;
            if (performer.Hp > 0f && performer.NextDot + 0.05f >= performer.Hp)
                return false;
            return performer.HpPct > CoverGuardSelfHpPct;
        }

        private static bool CoverBefore(TargetInfo a, TargetInfo b)
        {
            if (b == null)
                return true;
            if (a.DeathsDoor != b.DeathsDoor)
                return a.DeathsDoor;
            return a.HpPct < b.HpPct;
        }

        private static bool HasTokenId(TargetInfo t, string id)
        {
            if (t == null || t.Tokens == null)
                return false;
            for (var i = 0; i < t.Tokens.Count; i++)
            {
                if (string.Equals(t.Tokens[i], id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool CoverGuardMayReplace(ScoredAction picked, string gate)
        {
            if (gate == null || !CoverGuardGates.Contains(gate))
                return false;
            if (!string.IsNullOrEmpty(picked.NoteReason) && !CoverGuardNotes.Contains(picked.NoteReason))
                return false;
            if (picked.IsItem || picked.HealReposition || picked.ReachReposition || picked.MustRankWalk)
                return false;
            if (picked.Kind == SkillKind.Heal || picked.Kind == SkillKind.Move || IsPassHeal(picked.SkillId))
                return false;
            if (!picked.EnemyTarget && AppliesGuardTo(picked))
                return false;
            if (KitSafety.IsSelfTaunt(picked.SkillId) || IsHarvestHungerGuard(picked.SkillId) || IsForcedHarvestEat(picked.SkillId))
                return false;
            if (RestoresHp(picked.Preview) && !picked.EnemyTarget)
                return false;
            if (picked.EnemyTarget && picked.Preview != null && picked.Preview.Kills)
                return false;
            return true;
        }

        private static readonly string[] FreeTierReasons =
        {
            null, "over_pass_guard", "over_pass_hit", "over_pass_corpse", "over_pass_support", "over_pass_mark", "over_pass_corpse_chip"
        };

        // pass_stress (and any non-heal pass that costs stress); a Rest that
        // restores HP is a heal, not this.
        internal static bool IsStressPass(ScoredAction c)
        {
            return c != null && !c.IsItem && c.Kind == SkillKind.Pass
                   && string.Equals(c.SkillId, "pass_stress", StringComparison.OrdinalIgnoreCase);
        }

        // 0 = not free. See FreeOverPass for the tiers.
        internal static int FreeTier(ScoredAction c, EnemyFocus focus, TurnSnapshot snap, TargetInfo performer, int livingEnemies, bool allyLow, int[] livingRanks)
        {
            // Rest (pass_heal) costs the same 2 stress.
            if (c == null || c.IsItem || c.Cursed || c.Kind == SkillKind.Move || c.Kind == SkillKind.Pass || IsPassHeal(c.SkillId))
                return 0;
            if (c.HealReposition || c.ReachReposition || c.MustRankWalk)
                return 0;
            if (HasUseLimit(c.SkillId) || MovesPerformer(c.SkillId, performer))
                return 0;
            var t = c.Target;
            if (t == null)
                return 0;
            if (!c.EnemyTarget)
            {
                if (t.Corpse || !t.Living)
                    return 0;
                if (c.Kind != SkillKind.Support && c.Kind != SkillKind.Heal)
                    return 0;
                // Taunt pulls the hits onto a performer who is already low.
                if (KitSafety.IsSelfTaunt(c.SkillId) && performer != null
                    && (performer.DeathsDoor || performer.HpPct <= 0.35f))
                    return 0;
                // Guard redirects the ally's hits onto the guardian: on Death's
                // Door each one is a deathblow roll for him instead.
                if (AppliesGuardTo(c) && performer != null && performer.DeathsDoor)
                    return 0;
                if (allyLow && AppliesGuardTo(c) && performer != null && t.Guid != performer.Guid
                    && (t.DeathsDoor || (t.HpPct <= 0.35f && !KitSafety.WantsToStayLow(t))))
                    return 1;
                return 4;
            }
            if (c.Kind != SkillKind.Attack || c.Stealthed || t.Stealth || t.Healthless)
                return 0;
            var p = c.Preview;
            if (p == null)
                return 0;
            if (t.Corpse)
            {
                if (!CorpseBlocks(t.Rank, livingRanks) || p.Damage <= 0f || HitsLibrarianStackAny(c, focus))
                    return 0;
                return c.ClearsCorpse || p.Damage + 0.05f >= t.Hp ? 3 : 6;
            }
            if (!t.Living)
                return 0;
            if (t.Riposte && !p.Kills)
                return 0;
            // The preview's Riposte return on the performer (a guardian's Riposte
            // too: the click target itself has none). Not over a +2 stress pass
            // on Death's Door or when it drops him onto it (the Riposte death in
            // the SelfHitOnDeathsDoorPenalty note).
            if (SelfHitPenalty(p, performer) < 0f)
                return 0;
            if (HitsLibrarianStackAny(c, focus))
                return 0;
            if (p.Kills && IsSighLungTarget(c) && !t.LungInflate)
                return 0;
            if (p.Kills && focus != null && focus.IsDeferred(t.Guid) && IsBossPart(focus, t.Guid))
                return 0;
            if (t.DiesToDot && livingEnemies > 1)
                return 5;
            if (p.Damage > 0f || StalkClickHurts(p))
                return 2;
            return 5;
        }

        private static bool IsBossPart(EnemyFocus focus, uint guid)
        {
            for (var i = 0; i < focus.Enemies.Count; i++)
            {
                var e = focus.Enemies[i];
                if (e != null && e.Guid == guid)
                    return e.Boss;
            }
            return false;
        }

        private static bool HitsLibrarianStackAny(ScoredAction c, EnemyFocus focus)
        {
            if (focus == null || c.Target == null)
                return false;
            var click = c.Target.Guid != 0 && focus.IsLibrarianStack(c.Target.Guid);
            return click || HitsLibrarianStack(c, focus, false);
        }

        private static bool AppliesGuardTo(ScoredAction c)
        {
            if (c.Preview != null && TokenPrices.HasId(c.Preview.ApplyTarget, "guard"))
                return true;
            if (c.Tokens != null && TokenPrices.HasId(c.Tokens.Apply, "guard"))
                return true;
            return KitSafety.IdHas(c.SkillId, "maa_defender");
        }

        // A corpse with a living enemy behind it: destroying it moves that
        // enemy up a rank.
        internal static bool CorpseBlocks(int corpseRank, int[] livingRanks)
        {
            if (livingRanks == null)
                return false;
            for (var i = 0; i < livingRanks.Length; i++)
            {
                if (livingRanks[i] > corpseRank)
                    return true;
            }
            return false;
        }

        private static int[] LivingEnemyRanks(TurnSnapshot snap)
        {
            var ranks = new List<int>();
            if (snap != null)
            {
                foreach (var e in snap.Enemies)
                {
                    if (e != null && e.Living && !e.Corpse)
                        ranks.Add(e.Rank);
                }
            }
            return ranks.ToArray();
        }

        // CSV hero_*: skills with m_Limit (uses per fight).
        private static readonly string[] UseLimitedSkills =
        {
            "battlefield_medicine", "artemisia", "absinthe", "dead_of_night", "exsanguinate", "sepsis",
            "deathless", "adrenaline_rush", "encore", "finale_p2", "lep_revenge", "lep_ruin", "lep_solemnity",
            "lep_withstand", "cause_of_death_p1", "cauterize"
        };

        internal static bool HasUseLimit(string skillId)
        {
            for (var i = 0; i < UseLimitedSkills.Length; i++)
            {
                if (KitSafety.IdHas(skillId, UseLimitedSkills[i]))
                    return true;
            }
            return false;
        }

        // CSV performer_effects move_forward_* / move_backward_* (starter
        // kits plus Grave Robber): the click walks the performer out of
        // formation unless it is already at that end.
        private static readonly string[] ForwardMovers = { "maa_rampart", "maa_hold_the_line", "hwm_duelists_advance", "gr_lunge" };
        private static readonly string[] BackwardMovers = { "hwm_point_blank_shot", "maa_strategic_withdrawal", "gr_shadow_fade", "gr_pirouette" };

        internal static bool MovesPerformer(string skillId, TargetInfo performer)
        {
            var rank = performer != null ? performer.Rank : -1;
            for (var i = 0; i < ForwardMovers.Length; i++)
            {
                if (KitSafety.IdHas(skillId, ForwardMovers[i]))
                    return rank != 0;
            }
            for (var i = 0; i < BackwardMovers.Length; i++)
            {
                if (KitSafety.IdHas(skillId, BackwardMovers[i]))
                    return rank < 3;
            }
            return false;
        }

        // Pass, or a support / self click the scorer itself rates as a loss.
        // Cultist guardian fights: Tracking Shot / Blinding Gas (0 damage) or a
        // base-score support (Take Aim, Ounce of Prevention at +2) is filler
        // next to a real hit on the Deacon / an Evangelist. Not items, not
        // heals, not a support the scorer already rates (Bolster on 7+ stress).
        internal const float CultistFillerSupportMax = 10f;

        internal static bool IsCultistFillerPick(ScoredAction picked)
        {
            if (picked == null || picked.IsItem)
                return false;
            if (picked.Kind == SkillKind.Support)
                return picked.Score < CultistFillerSupportMax && !RestoresHp(picked.Preview);
            // Artemisia / a heal with 0 HP restore this click (scored -20).
            if (picked.Kind == SkillKind.Heal && !picked.EnemyTarget)
                return !RestoresHp(picked.Preview);
            if (picked.Kind == SkillKind.Attack && picked.EnemyTarget)
                return !StalkClickHurts(picked.Preview);
            return false;
        }

        internal static bool IsIdlePick(ScoredAction picked)
        {
            if (picked == null)
                return false;
            if (picked.Kind == SkillKind.Pass)
                return true;
            return picked.Kind == SkillKind.Support && picked.Score < 0f;
        }

        private static bool HasDamagingMustKillHit(List<ScoredAction> candidates, EnemyFocus focus, bool skipStealthed = false)
        {
            if (candidates == null || focus == null)
                return false;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.IsItem && c.FreeAction)
                    continue;
                if (skipStealthed && c.Stealthed)
                    continue;
                if (c.Kind != SkillKind.Attack || !c.EnemyTarget || c.Target == null || c.Target.Corpse)
                    continue;
                if (c.Target.Guid == 0 || !c.Target.Living)
                    continue;
                if (!focus.IsMustKillFirst(c.Target.Guid))
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

        private static TargetInfo ReachWalkTarget(List<ScoredAction> candidates, TargetInfo performer, TurnSnapshot snap, EnemyFocus focus, int livingEnemies, uint performerGuid, PartyKit party, MemoryState memory)
        {
            if (memory.ReachWalked(performerGuid))
                return null;

            if (livingEnemies <= 1 && !HasDamagingHitOn(candidates, 0))
            {
                var last = FindLastLivingEnemy(candidates);
                if (last == null || last.Guid == 0)
                    return null;
                if (AllyAlreadyReaches(party, performerGuid, last))
                    return null;
                return last;
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
                var actor = snap.Find(e.Guid);
                if (actor == null || !actor.Living)
                    continue;
                if (actor.Corpse)
                    continue;
                // Same ping-pong on Altar: MAA walks onto Dismas's Pistol rank,
                // then Dismas walks back next round. Librarian: the move filter
                // below only lets a swap through when the partner keeps his
                // reach (no ping-pong), so an ally's reach does not stop it.
                if (AllyAlreadyReaches(party, performerGuid, actor) && !PreserveAllyReach(actor.ClassId))
                    continue;
                if (BlockedReachSkills(performer, actor, snap).Count > 0)
                    return actor;
            }
            return null;
        }

        private static bool AllyAlreadyReaches(PartyKit party, uint performerGuid, TargetInfo enemy)
        {
            if (party == null || enemy == null)
                return false;
            return party.AllyHitsEnemyRank(performerGuid, enemy.Rank);
        }

        private static void ApplyReachReposition(List<ScoredAction> candidates, TargetInfo performer, TargetInfo enemy, PartyKit party, bool partyCrisis, TurnSnapshot snap)
        {
            if (candidates == null || performer == null || enemy == null)
                return;
            var skills = BlockedReachSkills(performer, enemy, snap);
            if (skills.Count == 0)
                return;
            var enemyRank = enemy.Rank;
            var enemySize = enemy.Size;
            var enemyClass = enemy.ClassId;

            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Kind != SkillKind.Move || c.Target == null || c.Target.Guid == 0)
                    continue;
                if (EnemyFocus.IsTangleWasteSkill(c.SkillId))
                    continue;
                var dest = c.Target.Rank;
                // wiki Librarian: do not move an ally who already punches him off
                // his reach. A move slides everyone between the two ranks one
                // step (logs: MAA rank 4 -> 2 gave HWM / MAA / GR / PD); a move
                // that leaves every such ally in reach is fine (Categorize sorts
                // by class name: GR / HWM / MAA / PD leaves GR on rank 1 and MAA
                // on rank 3 while the Pistol still lands).
                if (PreserveAllyReach(enemyClass))
                {
                    if (!LibrarianWalkKeepsReach(snap, performer, dest, enemy))
                        continue;
                }
                var helps = false;
                for (var s = 0; s < skills.Count; s++)
                {
                    if (skills[s].Launches(dest)
                        && skills[s].Targets(dest, enemyRank, enemySize))
                    {
                        helps = true;
                        break;
                    }
                }
                if (!helps)
                    continue;
                c.ReachReposition = true;
                // Below typical crisis heals (~160–200) so Endure / BM / Rest win.
                c.Score = partyCrisis ? 90f : 180f;
            }
        }

        // The move puts the performer on dest and slides every hero between the
        // two ranks one step toward his old rank. False when an ally who has a
        // damaging attack on the enemy now would lose it on his new rank, or
        // when the ranks are not one hero per rank (cannot model: keep the old
        // never-move-a-reacher rule).
        internal static bool LibrarianWalkKeepsReach(TurnSnapshot snap, TargetInfo performer, int dest, TargetInfo enemy)
        {
            if (snap == null || performer == null || enemy == null || snap.Heroes == null)
                return false;
            var from = performer.Rank;
            if (dest == from || from < 0 || dest < 0)
                return false;
            var lo = Math.Min(from, dest);
            var hi = Math.Max(from, dest);
            var step = dest > from ? -1 : 1;
            var seen = new HashSet<int>();
            for (var i = 0; i < snap.Heroes.Count; i++)
            {
                var h = snap.Heroes[i];
                if (h == null || h.Rank < 0)
                    continue;
                if (h.Size > 1 || !seen.Add(h.Rank))
                    return false;
            }
            for (var r = lo; r <= hi; r++)
            {
                if (!seen.Contains(r))
                    return false;
            }
            for (var i = 0; i < snap.Heroes.Count; i++)
            {
                var h = snap.Heroes[i];
                if (h == null || h.Guid == performer.Guid || h.Rank < lo || h.Rank > hi)
                    continue;
                if (!h.Living || h.Corpse)
                    continue;
                var newRank = h.Rank + step;
                if (HeroHitsFrom(snap, h, h.Rank, enemy) && !HeroHitsFrom(snap, h, newRank, enemy))
                    return false;
            }
            return true;
        }

        // Would this hero still have a damaging attack on the enemy's rank from
        // fromRank (CSV launch + target ranks, equipped skills)? Unknown masks
        // read as "no", so the old never-swap rule stands when we cannot tell.
        internal static bool HeroHitsFrom(TurnSnapshot snap, TargetInfo hero, int fromRank, TargetInfo enemy)
        {
            if (snap == null || hero == null || enemy == null || hero.SkillIds == null)
                return false;
            for (var i = 0; i < hero.SkillIds.Count; i++)
            {
                var id = hero.SkillIds[i];
                var def = snap.Skill(id);
                if (def == null || def.IsItemSkill || def.IsMove || def.IsFriendly)
                    continue;
                if (LooksLikeHeal(id, def, null) || IsPassHeal(id) || !ReachWalkSkill(id))
                    continue;
                if (def.Role != null && !def.Role.Attacks)
                    continue;
                if (def.Launches(fromRank) && def.Targets(fromRank, enemy.Rank, enemy.Size))
                    return true;
            }
            return false;
        }

        // Performer skills only (UnderSkillLimit is the performer's).
        private static List<SkillSnap> BlockedReachSkills(TargetInfo performer, TargetInfo enemy, TurnSnapshot snap)
        {
            var blocked = new List<SkillSnap>();
            var skillIds = performer != null ? performer.SkillIds : null;
            if (skillIds == null || enemy == null || snap == null)
                return blocked;
            var enemyRank = enemy.Rank;
            var enemySize = enemy.Size;

            for (var i = 0; i < skillIds.Count; i++)
            {
                var id = skillIds[i];
                var def = snap.Skill(id);
                if (def == null)
                    continue;
                if (def.IsItemSkill || def.IsMove)
                    continue;
                if (def.IsFriendly)
                    continue;
                if (LooksLikeHeal(id, def, null) || IsPassHeal(id))
                    continue;
                if (!ReachWalkSkill(id))
                    continue;
                if (!snap.UnderSkillLimit(id))
                    continue;
                var hitsThem = false;
                for (var dest = 0; dest < 4; dest++)
                {
                    if (def.Launches(dest)
                        && def.Targets(dest, enemyRank, enemySize))
                    {
                        hitsThem = true;
                        break;
                    }
                }
                if (!hitsThem)
                    continue;
                blocked.Add(def);
            }
            return blocked;
        }

        private static List<SkillSnap> BlockedHealSkills(TargetInfo performer, TurnSnapshot snap)
        {
            var blocked = new List<SkillSnap>();
            var skillIds = performer != null ? performer.SkillIds : null;
            if (skillIds == null || snap == null)
                return blocked;
            var rank = performer.Rank;

            for (var i = 0; i < skillIds.Count; i++)
            {
                var id = skillIds[i];
                var def = snap.Skill(id);
                if (def == null)
                    continue;
                if (def.IsItemSkill || def.IsMove)
                    continue;
                if (IsPassHeal(id))
                    continue;
                if (!IsAllyHealSkill(id, def))
                    continue;
                if (!snap.UnderSkillLimit(id))
                    continue;
                if (def.Launches(rank))
                    continue;
                blocked.Add(def);
            }
            return blocked;
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

        private static void LogTurn(ActorControllerBase controller, ActorInstance performer, TurnSnapshot snap, List<JObject> candidates, ChosenAction chosen, string reason, PartyKit party, EnemyFocus focus, bool commit, Search.SearchResult search = null)
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

            // Schema 2: "round" always; "snap" (the full decider input, see
            // TurnSnapshot) rides with "legal" under LogPreviews so the turn
            // can be re-decided offline.
            // Search.Mode Compare / On: the lookahead's pick, value and line.
            if (search != null)
                record["search"] = search.ToJson();

            record["schema"] = TurnSnapshot.CurrentSchema;
            record["round"] = snap != null ? snap.Round : 0;
            if (!Plugin.LogPreviews.Value)
            {
                record.Remove("legal");
            }
            else if (snap != null)
            {
                try { record["snap"] = SnapshotJson.Write(snap); }
                catch (Exception ex) { DecisionLog.Warn("Snapshot log failed: " + ex.Message); }
            }

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
