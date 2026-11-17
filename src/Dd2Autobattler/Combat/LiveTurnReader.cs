using System.Collections.Generic;
using Assets.Code.Actor;
using Assets.Code.Actor.ActorController;
using Assets.Code.Combat;
using Assets.Code.Combat.Queries;
using Assets.Code.Library;
using Assets.Code.Skill;
using Assets.Code.Utils;

namespace Dd2Autobattler.Combat
{
    /// <summary>
    /// Adapter: reads the live game into a TurnSnapshot. Every game call the
    /// decider used to make mid-scoring happens here, once, up front. The
    /// decider itself (TurnDecider.DecideSnapshot) only sees plain data.
    /// </summary>
    internal static class LiveTurnReader
    {
        public static TurnSnapshot Capture(ActorControllerBase controller, ActorInstance performer, BattleTeams teams)
        {
            var snap = new TurnSnapshot
            {
                Schema = TurnSnapshot.CurrentSchema,
                FightId = TurnSnapshot.NormalizeFightId(Logging.DecisionLog.FightId),
                PerformerGuid = performer.ActorGuid,
                Memory = CombatMemory.Capture()
            };

            var live = new Dictionary<uint, ActorInstance>();
            foreach (var actor in GameSnapshot.TeamActors(teams, BattleTeams.HERO_TEAM_INDEX))
                snap.Heroes.Add(Describe(actor, live));
            foreach (var actor in GameSnapshot.TeamActors(teams, BattleTeams.ENEMY_TEAM_INDEX))
                snap.Enemies.Add(Describe(actor, live));
            snap.Reindex();
            snap.Performer = snap.Find(snap.PerformerGuid) ?? Describe(performer, live);

            // Performer skills first so their masks use the performer's size.
            AddSkills(snap, snap.Performer);

            var entries = controller.GetValidSkillTargetEntries();
            if (entries != null)
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (entry == null || !entry.IsValid || string.IsNullOrEmpty(entry.m_SkillId))
                        continue;

                    var skillId = entry.m_SkillId;
                    var skillDef = GetSkill(skillId);
                    var targets = entry.m_ValidTargetActorGuids;
                    if (targets == null || targets.Count == 0)
                        continue;
                    AddSkill(snap, skillId, skillDef, snap.Performer.Size);

                    for (var t = 0; t < targets.Count; t++)
                    {
                        var targetGuid = targets[t];
                        if (targetGuid == 0)
                            continue;

                        var stealthed = false;
                        try { stealthed = controller.GetIsStealthedTarget(skillId, targetGuid); } catch { }

                        var preview = SkillPreviewReader.Score(performer.ActorGuid, skillId, targetGuid);
                        if (preview.Ok)
                        {
                            // CSV / buff reads. Order vs the decider's clamp is
                            // safe: these only touch token lists and Apply* DoT.
                            SkillPreviewReader.AddSkillTokenAdds(skillDef, preview);
                            SkillPreviewReader.AddConditionalDotDealt(performer, skillId, preview);
                        }
                        snap.Legal.Add(new LegalSnap
                        {
                            Skill = skillId,
                            Target = targetGuid,
                            Stealthed = stealthed,
                            Preview = preview
                        });
                    }

                    if (IsCursedSkill(performer, skillId) && !snap.CursedSkills.Contains(skillId))
                        snap.CursedSkills.Add(skillId);
                    if (ItemPolicy.HoldsCombatItemLive(performer, skillId))
                        AddUnique(Held(snap.Performer), skillId);
                    if (!snap.ItemQty.ContainsKey(skillId)
                        && ItemPolicy.IsCombatItem(snap.Skill(skillId), skillId, snap.Performer))
                        snap.ItemQty[skillId] = ItemPolicy.RemainingQtyLive(performer, skillId);
                }
            }

            foreach (var hero in snap.Heroes)
            {
                if (hero != null && hero != snap.Performer)
                    AddSkills(snap, hero);
            }

            // Held combat items per hero (PartyKit skips them as kit).
            foreach (var info in snap.Heroes)
            {
                ActorInstance actor;
                if (info == null || info.SkillIds == null || !live.TryGetValue(info.Guid, out actor))
                    continue;
                foreach (var id in info.SkillIds)
                {
                    if (ItemPolicy.HoldsCombatItemLive(actor, id))
                        AddUnique(Held(info), id);
                }
            }

            if (snap.Performer.SkillIds != null)
            {
                foreach (var id in snap.Performer.SkillIds)
                {
                    var def = GetSkill(id);
                    if (def == null)
                        continue;
                    var under = true;
                    try { under = performer.GetIsUnderSkillLimit(def); } catch { }
                    if (!under && !snap.LimitedSkills.Contains(id))
                        snap.LimitedSkills.Add(id);
                }
            }

            snap.RemainingTurns = ReadRemainingTurns();
            EnemyFocus.ReadThreats(teams, snap.Threats, snap.SummonedClasses);
            return snap;
        }

        private static TargetInfo Describe(ActorInstance actor, Dictionary<uint, ActorInstance> live)
        {
            TargetInfo info;
            try
            {
                info = GameSnapshot.Describe(actor);
            }
            catch
            {
                // The old decider only described living actors outside a
                // try; keep that failure, but a dead actor's unreadable HP
                // must not cost the turn.
                var living = false;
                try { living = actor != null && actor.IsLiving; } catch { }
                if (living)
                    throw;
                info = new TargetInfo { Size = 1 };
                try { info.Guid = actor.ActorGuid; } catch { }
                try { info.Team = actor.TeamIndex; } catch { }
                try { info.Rank = actor.TeamPosition; } catch { }
                try { info.Corpse = GameSnapshot.IsCorpse(actor); } catch { }
            }
            if (actor != null && info.Guid != 0 && !live.ContainsKey(info.Guid))
                live[info.Guid] = actor;
            return info;
        }

        private static List<string> Held(TargetInfo info)
        {
            if (info.HeldItems == null)
                info.HeldItems = new List<string>();
            return info.HeldItems;
        }

        private static void AddUnique(List<string> list, string id)
        {
            if (!string.IsNullOrEmpty(id) && !list.Contains(id))
                list.Add(id);
        }

        private static void AddSkills(TurnSnapshot snap, TargetInfo owner)
        {
            if (owner == null || owner.SkillIds == null)
                return;
            foreach (var id in owner.SkillIds)
                AddSkill(snap, id, GetSkill(id), owner.Size);
        }

        private static void AddSkill(TurnSnapshot snap, string id, ActorDataSkill def, int ownerSize)
        {
            if (string.IsNullOrEmpty(id) || def == null || snap.Skills.ContainsKey(id))
                return;
            snap.Skills[id] = ReadSkill(id, def, ownerSize);
        }

        internal static SkillSnap ReadSkill(string id, ActorDataSkill def, int ownerSize)
        {
            var s = new SkillSnap { Id = id, OwnerSize = ownerSize };
            try { s.IsMove = def.IsMoveSkill; } catch { }
            try { s.IsFriendly = def.m_IsFriendly; } catch { }
            try { s.IsItemSkill = def.IsItemSkill; } catch { }
            try { s.IsFree = def.m_IsFreeAction; } catch { }
            s.ClearCorpseEffect = ItemPolicy.HasClearCorpseEffect(def);
            try
            {
                var tags = def.m_Tags;
                if (tags != null)
                {
                    for (var i = 0; i < tags.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(tags[i]))
                            s.Tags.Add(tags[i]);
                    }
                }
            }
            catch { }
            s.Conditions = ItemPolicy.ConditionIdsLive(def);
            s.Role = PartyKit.DescribeSkill(def);

            s.TargetMasks = new int[4 * SkillSnap.MaxEnemySize];
            s.TargetThrew = new int[4 * SkillSnap.MaxEnemySize];
            for (var launch = 0; launch < 4; launch++)
            {
                try
                {
                    if (def.GetHasLaunchRank(launch, ownerSize))
                        s.LaunchMask |= 1 << launch;
                }
                catch { s.MasksUnknown = true; }
                for (var es = 1; es <= SkillSnap.MaxEnemySize; es++)
                {
                    for (var er = 0; er < 4; er++)
                    {
                        try
                        {
                            if (def.GetHasTargetRank(launch, ownerSize, er, es))
                                s.TargetMasks[launch * SkillSnap.MaxEnemySize + (es - 1)] |= 1 << er;
                        }
                        catch
                        {
                            s.MasksUnknown = true;
                            s.TargetThrew[launch * SkillSnap.MaxEnemySize + (es - 1)] |= 1 << er;
                        }
                    }
                }
            }
            return s;
        }

        private static ActorDataSkill GetSkill(string skillId)
        {
            if (string.IsNullOrEmpty(skillId))
                return null;
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
    }
}
