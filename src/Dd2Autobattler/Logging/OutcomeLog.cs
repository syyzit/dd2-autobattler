using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Assets.Code.Actor;
using Assets.Code.Actor.Events;
using Assets.Code.Combat;
using Assets.Code.Events;
using Assets.Code.Library;
using Assets.Code.Game.Events;
using Assets.Code.Skill;
using Assets.Code.Skill.Events;
using Assets.Code.Utils;
using Dd2Autobattler.Combat;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace Dd2Autobattler.Logging
{
    /// <summary>
    /// What actually happened: one action_result line per resolved skill (hit/miss/crit/dodge,
    /// damage, heals, effects, kills) and the party/enemy state that goes into fight_end.
    /// Read-only: never changes the battle.
    /// </summary>
    internal static class OutcomeLog
    {
        private static bool _subscribed;
        private static Battle _battle;
        private static readonly HashSet<uint> _enemiesSeen = new HashSet<uint>();
        private static readonly HashSet<uint> _heroesSeen = new HashSet<uint>();
        private static int _deathsDoorEntered;
        private static int _heroDeaths;
        private static int _actions;
        private static int _firstRound = -1;
        // Node type of the last node started (fight_end node / guardian boss_kind).
        private static string _lastNode;

        /// <summary>Fired after every fight_end so run tracking can tally it.</summary>
        public static event Action<JObject> FightEnded;

        public static void Patch(Harmony harmony)
        {
            try
            {
                var postCreate = AccessTools.Method(typeof(Battle), "PostCreate");
                if (postCreate != null)
                    harmony.Patch(postCreate, postfix: new HarmonyMethod(typeof(OutcomeLog), nameof(BattlePostCreatePostfix)));
                else
                    DecisionLog.Warn("OutcomeLog: Battle.PostCreate not found; enemies_left will come from action results only.");
            }
            catch (Exception ex)
            {
                DecisionLog.Error("OutcomeLog: could not patch Battle.PostCreate", ex);
            }
        }

        public static void Subscribe()
        {
            if (_subscribed)
                return;
            try
            {
                EventManager.AddListener<EventSkillFinalizeResults>(OnSkillFinalizeResults, false, EventManager.PRIORITY_DEFAULT);
                EventManager.AddListener<EventActorDeath>(OnActorDeath, false, EventManager.PRIORITY_DEFAULT);
                EventManager.AddListener<EventExecuteNodeStarted>(OnNodeStarted, false, EventManager.PRIORITY_DEFAULT);
                _subscribed = true;
            }
            catch (Exception ex)
            {
                DecisionLog.Error("OutcomeLog: could not subscribe", ex);
            }
        }

        public static void Unsubscribe()
        {
            if (!_subscribed)
                return;
            try
            {
                EventManager.RemoveListener<EventSkillFinalizeResults>(OnSkillFinalizeResults);
                EventManager.RemoveListener<EventActorDeath>(OnActorDeath);
                EventManager.RemoveListener<EventExecuteNodeStarted>(OnNodeStarted);
            }
            catch { }
            _subscribed = false;
        }

        private static void OnNodeStarted(EventExecuteNodeStarted evt)
        {
            try { _lastNode = evt != null && evt.m_NodeType != null ? evt.m_NodeType.ToString() : null; } catch { _lastNode = null; }
        }

        private static void BattlePostCreatePostfix(Battle __instance)
        {
            _battle = __instance;
        }

        /// <summary>Call from fight_start.</summary>
        public static void BeginFight()
        {
            _enemiesSeen.Clear();
            _heroesSeen.Clear();
            _deathsDoorEntered = 0;
            _heroDeaths = 0;
            _actions = 0;
            _firstRound = -1;
        }

        private static void OnSkillFinalizeResults(EventSkillFinalizeResults evt)
        {
            if (evt == null)
                return;
            try
            {
                AutobattlerApi.RaiseProgress("action");
                var rec = new JObject();
                uint performer = 0;
                try { performer = evt.PerformerGuid; } catch { }
                var performerActor = GetActor(performer);
                rec["round"] = CombatMemory.Round;
                if (_firstRound < 0)
                    _firstRound = CombatMemory.Round;
                rec["performer"] = ActorRef(performerActor, performer);
                try { rec["skill"] = evt.SkillId; } catch { }
                try { rec["turn_type"] = evt.m_BattleTurnType != null ? evt.m_BattleTurnType.ToString() : null; } catch { }

                var targets = new JArray();
                float dmgTotal = 0, healTotal = 0;
                int kills = 0;
                IReadOnlyList<SkillCalculation.ActorResult> results = null;
                try { results = evt.ActorResults; } catch { }
                if (results != null)
                {
                    for (var i = 0; i < results.Count; i++)
                    {
                        var r = results[i];
                        if (r == null)
                            continue;
                        var t = DescribeResult(r);
                        if (t == null)
                            continue;
                        targets.Add(t);
                        dmgTotal += t.Value<float?>("dmg") ?? 0f;
                        healTotal += t.Value<float?>("heal") ?? 0f;
                        if (t.Value<bool?>("kill") == true)
                            kills++;
                    }
                }
                try
                {
                    var riposte = evt.RiposteResult;
                    if (riposte != null)
                    {
                        var rt = DescribeResult(riposte);
                        if (rt != null)
                            rec["riposte"] = rt;
                    }
                }
                catch { }

                var killed = new JArray();
                try
                {
                    var skillResult = Traverse.Create(evt).Field("m_SkillResult").GetValue();
                    var list = skillResult != null ? Traverse.Create(skillResult).Field("m_KilledActorGuids").GetValue() as IEnumerable<uint> : null;
                    if (list != null)
                        foreach (var g in list)
                            killed.Add(g);
                }
                catch { }

                rec["targets"] = targets;
                rec["dmg_total"] = Round1(dmgTotal);
                rec["heal_total"] = Round1(healTotal);
                rec["kills"] = Math.Max(kills, killed.Count);
                if (killed.Count > 0)
                    rec["killed_guids"] = killed;
                _actions++;
                DecisionLog.Record("action_result", rec);
            }
            catch (Exception ex)
            {
                DecisionLog.Warn("OutcomeLog: action_result failed: " + ex.Message);
            }
        }

        /// <summary>HP once the hit/heal lands: clamped to [0, max] (Death's Door sits at 0).</summary>
        internal static float HpAfter(float before, float max, float dmg, float heal)
        {
            var v = before - dmg + heal;
            if (max > 0 && v > max)
                v = max;
            return v < 0 ? 0 : v;
        }

        private static JObject DescribeResult(SkillCalculation.ActorResult r)
        {
            if (r == null)
                return null;
            var o = new JObject();
            uint target = 0;
            try { target = r.m_TargetActorGuid; } catch { }
            var actor = GetActor(target);
            o["target"] = ActorRef(actor, target);
            int team = -1;
            try { team = r.m_TargetTeamIndex; } catch { }
            NoteSide(target, team);
            try { o["result"] = r.m_Type.ToString(); } catch { }
            o["hit"] = Safe(() => r.IsHit);
            o["miss"] = Safe(() => r.IsMiss);
            o["dodge"] = Safe(() => r.IsDodge);
            o["crit"] = Safe(() => r.IsCrit);
            o["blocked"] = Safe(() => r.IsBlocked);
            o["guarded"] = Safe(() => r.IsGuarding);
            if (Safe(() => r.IsDamaging))
                o["dmg"] = Round1(SafeF(() => r.HealthDamage));
            if (Safe(() => r.IsHealthHeal))
                o["heal"] = Round1(SafeF(() => r.HealthHeal));
            if (Safe(() => r.IsDamagingStress))
                o["stress_dmg"] = Round1(SafeF(() => r.StressDamage));
            if (Safe(() => r.IsStressHeal))
                o["stress_heal"] = Round1(SafeF(() => r.StressHeal));
            var kill = Safe(() => r.IsKill);
            o["kill"] = kill;
            var enterDd = Safe(() => r.IsEnteringDeathsDoor);
            if (enterDd)
            {
                o["enter_deaths_door"] = true;
                if (team == BattleTeams.HERO_TEAM_INDEX)
                    _deathsDoorEntered++;
            }
            if (Safe(() => r.IsDeathsDoorSurvival))
                o["deaths_door_survive"] = true;
            var effects = AppliedEffectIds(r);
            if (effects != null && effects.Count > 0)
                o["effects"] = effects;
            if (actor != null)
            {
                // EventSkillFinalizeResults fires before the result is applied, so the actor still has its old HP.
                try
                {
                    var before = actor.HpRounded;
                    var max = actor.CurrentHpMax;
                    var dmg = o.Value<float?>("dmg") ?? 0f;
                    var heal = o.Value<float?>("heal") ?? 0f;
                    o["hp_before"] = Round1(before);
                    o["hp_after"] = Round1(HpAfter(before, max, dmg, heal));
                }
                catch { }
            }
            return o;
        }

        private static JArray AppliedEffectIds(SkillCalculation.ActorResult r)
        {
            try
            {
                var container = r.m_AppliedEffectsOutputContainer;
                if (container == null)
                    return null;
                var outputs = Traverse.Create(container).Field("m_Outputs").GetValue() as IEnumerable;
                if (outputs == null)
                    return null;
                var ids = new JArray();
                foreach (var output in outputs)
                {
                    if (output == null)
                        continue;
                    var applied = Traverse.Create(output).Field("m_EffectInstancesApplied").GetValue() as IEnumerable;
                    if (applied == null)
                        continue;
                    foreach (var inst in applied)
                    {
                        var id = EffectId(inst);
                        if (!string.IsNullOrEmpty(id))
                            ids.Add(id);
                    }
                }
                return ids;
            }
            catch
            {
                return null;
            }
        }

        private static string EffectId(object inst)
        {
            if (inst == null)
                return null;
            try
            {
                var def = Traverse.Create(inst).Property("EffectDefinition").GetValue();
                if (def == null)
                    return null;
                var getKey = def.GetType().GetMethod("GetKey", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (getKey != null)
                    return Convert.ToString(getKey.Invoke(def, null));
                return def.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static void OnActorDeath(EventActorDeath evt)
        {
            if (evt == null)
                return;
            try
            {
                // A hero dies once as "<class>" and its corpse is removed later as "<class>_corpse": count once.
                var dataId = evt.m_DyingActorDataId ?? "";
                if (evt.m_DyingActorTeamIndex == BattleTeams.HERO_TEAM_INDEX && !dataId.EndsWith("_corpse", StringComparison.OrdinalIgnoreCase))
                {
                    _heroDeaths++;
                    DecisionLog.Record("hero_death", new JObject
                    {
                        ["actor"] = ActorRef(GetActor(evt.m_DyingActorGuid), evt.m_DyingActorGuid),
                        ["actor_data_id"] = evt.m_DyingActorDataId,
                        ["death_type"] = evt.m_DeathType != null ? evt.m_DeathType.ToString() : null,
                        ["source"] = evt.m_SourceType != null ? evt.m_SourceType.ToString() : null,
                        ["in_combat"] = CombatMemory.InCombat
                    });
                }
            }
            catch { }
        }

        /// <summary>Adds party state, outcome, rounds and enemies left to a fight_end record.</summary>
        public static void FillFightEnd(JObject extra, BattleResult result)
        {
            if (extra == null)
                return;
            try
            {
                extra["rounds"] = CombatMemory.Round;
                extra["actions"] = _actions;

                var partyGuids = new List<uint>();
                try
                {
                    if (result != null && result.m_StartingPartyActorGuids != null)
                        partyGuids.AddRange(result.m_StartingPartyActorGuids);
                }
                catch { }
                if (partyGuids.Count == 0)
                {
                    foreach (var a in RunTrackerParty())
                        partyGuids.Add(a.ActorGuid);
                }

                var party = new JArray();
                int alive = 0, dead = 0, onDoor = 0;
                foreach (var g in partyGuids)
                {
                    var a = GetActor(g);
                    var h = HeroState(a, g);
                    party.Add(h);
                    if (h.Value<bool?>("living") == true)
                        alive++;
                    else
                        dead++;
                    if (h.Value<bool?>("deaths_door") == true)
                        onDoor++;
                }
                extra["party"] = party;
                extra["heroes_alive"] = alive;
                extra["heroes_dead"] = dead;
                extra["heroes_on_deaths_door"] = onDoor;
                extra["deaths_door_entered"] = _deathsDoorEntered;
                extra["hero_deaths"] = _heroDeaths;

                int bystanders;
                var enemiesLeft = CountLivingEnemies(out bystanders);
                extra["enemies_left"] = enemiesLeft;
                // Living enemies the game does not need dead (m_IsBattleComplete:
                // Librarian book stacks, mushrooms, barricades). Logs before this
                // field counted them in enemies_left and called a Librarian kill
                // before Ignite "unresolved" (twice).
                if (bystanders > 0)
                    extra["bystanders_left"] = bystanders;

                bool retreat = false, forceEnd = false;
                try { retreat = result != null && result.m_IsRetreat; } catch { }
                try { forceEnd = result != null && result.m_IsForceEnd; } catch { }
                extra["outcome"] = Outcome(retreat, partyGuids.Count, alive, enemiesLeft, forceEnd);
                try { extra["has_next_battle"] = result != null && result.HasNextBattle; } catch { }
                try { extra["node_sub_type"] = result != null ? result.m_NodeSubType : null; } catch { }
                bool expBoss = false, biomeBoss = false;
                try { expBoss = result != null && result.m_IsExpeditionBossBattle; } catch { }
                try { biomeBoss = result != null && result.m_IsBiomeBossBattle; } catch { }
                extra["expedition_boss"] = expBoss;
                extra["biome_boss"] = biomeBoss;
                // Region guardians (e.g. cultist_guardian_*) are not flagged as boss battles by the game.
                var node = _lastNode;
                var fightId = DecisionLog.FightId ?? "";
                var guardian = string.Equals(node, "Guardian", StringComparison.OrdinalIgnoreCase)
                               || fightId.IndexOf("guardian", StringComparison.OrdinalIgnoreCase) >= 0;
                extra["node"] = node;
                extra["boss_kind"] = expBoss ? "expedition" : biomeBoss ? "biome" : guardian ? "guardian"
                    : fightId.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0 ? "named" : null;
                if (extra.Value<string>("boss_kind") != null)
                    extra["boss"] = true;
            }
            catch (Exception ex)
            {
                DecisionLog.Warn("OutcomeLog: fight_end fill failed: " + ex.Message);
            }
        }

        // enemiesLeft counts only enemies the game needs dead (no
        // m_IsBattleComplete); bystanders such as book stacks never block a win.
        internal static string Outcome(bool retreat, int partyCount, int alive, int enemiesLeft, bool forceEnd)
        {
            if (retreat)
                return "retreat";
            if (partyCount > 0 && alive == 0)
                return "loss";
            if (enemiesLeft == 0)
                return "win";
            if (forceEnd)
                return "force_end";
            return "unresolved"; // heroes and enemies both standing: scripted end, flee, or run over
        }

        /// <summary>Call after the fight_end line is written.</summary>
        public static void NotifyFightEnded(JObject fightEnd)
        {
            try { FightEnded?.Invoke(fightEnd); } catch { }
            _battle = null;
        }

        private static int CountLivingEnemies(out int bystanders)
        {
            var living = 0;
            bystanders = 0;
            var counted = new HashSet<uint>();
            try
            {
                var teams = _battle != null ? Traverse.Create(_battle).Field("m_BattleTeams").GetValue() as BattleTeams : null;
                if (teams != null)
                {
                    foreach (var a in GameSnapshot.TeamActors(teams, BattleTeams.ENEMY_TEAM_INDEX))
                    {
                        counted.Add(a.ActorGuid);
                        Tally(a, ref living, ref bystanders);
                    }
                }
            }
            catch { }
            foreach (var g in _enemiesSeen)
            {
                if (counted.Contains(g))
                    continue;
                Tally(GetActor(g), ref living, ref bystanders);
            }
            return living;
        }

        private static void Tally(ActorInstance a, ref int living, ref int bystanders)
        {
            if (!IsLivingFoe(a))
                return;
            if (GameSnapshot.IsBattleComplete(a))
                bystanders++;
            else
                living++;
        }

        // Living, not a corpse, has an HP bar. Whether the game needs it dead
        // is GameSnapshot.IsBattleComplete (counted apart as a bystander).
        private static bool IsLivingFoe(ActorInstance a)
        {
            try
            {
                if (a == null || !a.IsLiving)
                    return false;
                if (GameSnapshot.IsCorpse(a))
                    return false;
                string cls = null;
                try { cls = a.ActorDataClass != null ? a.ActorDataClass.GetKey() : null; } catch { }
                return !GameSnapshot.IsHealthless(a, cls);
            }
            catch
            {
                return false;
            }
        }

        private static void NoteSide(uint guid, int team)
        {
            if (guid == 0)
                return;
            if (team == BattleTeams.ENEMY_TEAM_INDEX)
                _enemiesSeen.Add(guid);
            else if (team == BattleTeams.HERO_TEAM_INDEX)
                _heroesSeen.Add(guid);
        }

        internal static IEnumerable<ActorInstance> RunTrackerParty()
        {
            IReadOnlyList<ActorInstance> party = null;
            try { party = Singleton<Assets.Code.Game.GameTypeMgr>.Instance.RosterManager.GetPartyActors(); } catch { }
            if (party == null)
                yield break;
            for (var i = 0; i < party.Count; i++)
                if (party[i] != null)
                    yield return party[i];
        }

        internal static JObject HeroState(ActorInstance a, uint guid)
        {
            var o = new JObject { ["guid"] = guid };
            if (a == null)
            {
                o["living"] = false;
                o["missing"] = true;
                o["dead"] = true;
                o["hp"] = 0f;
                return o;
            }
            try { o["name"] = a.ActorName; } catch { }
            try { o["class"] = a.ActorDataClass != null ? a.ActorDataClass.GetKey() : null; } catch { }
            try { o["path"] = a.ActorDataPath != null ? a.ActorDataPath.GetKey() : null; } catch { }
            try { o["rank"] = a.TeamPosition; } catch { }
            try { o["hp"] = Round1(a.HpRounded); } catch { }
            try { o["hp_max"] = Round1(a.CurrentHpMax); } catch { }
            try { o["stress"] = Round1(a.Stress); } catch { }
            // A dead hero lingers as a "<class>_corpse" actor that still reports IsLiving.
            bool living = false, corpse = false;
            try { living = a.IsLiving; } catch { }
            try { corpse = GameSnapshot.IsCorpse(a); } catch { }
            var cls = o.Value<string>("class");
            if (cls != null && cls.EndsWith("_corpse", StringComparison.OrdinalIgnoreCase))
                corpse = true;
            o["living"] = living && !corpse;
            if (corpse)
                o["corpse"] = true;
            try { o["deaths_door"] = a.GetIsStatusActive(ActorStatusType.DEATHS_DOOR); } catch { }
            MarkDead(o);
            return o;
        }

        // A dead hero is a "<class>_corpse" actor whose HP is the corpse's own bar
        // (logged fight_end: grave_robber_corpse 16, plague_doctor_corpse -3),
        // not the hero's. Report it as dead with 0 HP; the corpse bar is kept apart.
        internal static void MarkDead(JObject o)
        {
            if (o == null || o.Value<bool?>("living") != false)
                return;
            o["dead"] = true;
            var hp = o["hp"];
            if (hp != null && hp.Type != JTokenType.Null && o.Value<bool?>("corpse") == true)
                o["corpse_hp"] = hp;
            o["hp"] = 0f;
            o["deaths_door"] = false;
            var cls = o.Value<string>("class");
            if (cls != null && cls.EndsWith("_corpse", StringComparison.OrdinalIgnoreCase))
                o["class"] = cls.Substring(0, cls.Length - "_corpse".Length);
        }

        internal static ActorInstance GetActor(uint guid)
        {
            if (guid == 0)
                return null;
            try
            {
                return SingletonMonoBehaviour<Library<uint, ActorInstance>>.Instance.GetLibraryElement(guid);
            }
            catch
            {
                return null;
            }
        }

        private static JObject ActorRef(ActorInstance a, uint guid)
        {
            var o = new JObject { ["guid"] = guid };
            if (a == null)
                return o;
            try { o["class"] = a.ActorDataClass != null ? a.ActorDataClass.GetKey() : null; } catch { }
            try { o["name"] = a.ActorName; } catch { }
            try { o["team"] = a.TeamIndex; } catch { }
            try { o["rank"] = a.TeamPosition; } catch { }
            return o;
        }

        private static bool Safe(Func<bool> f)
        {
            try { return f(); } catch { return false; }
        }

        private static float SafeF(Func<float> f)
        {
            try { return f(); } catch { return 0f; }
        }

        private static float Round1(float v)
        {
            return (float)Math.Round(v, 1);
        }
    }
}
