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

            if (focus.HasPriorityTarget)
            {
                for (var i = 0; i < focus.Enemies.Count; i++)
                {
                    var e = focus.Enemies[i];
                    if (!e.Boss && !e.Summons && !e.Resurrects && !e.MustKillFirst)
                        e.Add = true;
                }
            }

            for (var i = 0; i < focus.Enemies.Count; i++)
                ScoreThreat(focus.Enemies[i], focus.HasPriorityTarget);

            return focus;
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
            var t = Find(guid);
            return t != null && !t.Defer
                   && (t.MustKillFirst || t.Commander || t.Boss || t.Summons || t.Resurrects);
        }

        public bool IsMustKillFirst(uint guid)
        {
            var t = Find(guid);
            return t != null && t.MustKillFirst;
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

        private static void ScoreThreat(EnemyThreat e, bool hasController)
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

        private static void ApplyTangleNotes(EnemyFocus focus)
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
