using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Assets.Code.Actor;
using Assets.Code.Combat;
using Assets.Code.Condition;
using Assets.Code.Effect;
using Assets.Code.Library;
using Assets.Code.Skill;
using Assets.Code.Utils;
using Newtonsoft.Json.Linq;

namespace Dd2Autobattler.Combat
{
    internal sealed class HeroKit
    {
        public uint Guid;
        public string Name;
        public int Rank;
        public bool Living;
        public bool Attacks;
        public bool Heals;
        public bool Stuns;
        public bool SpendsCombo;
        public bool AppliesCombo;
        public float ComboSpendValue;
        public bool Bleed;
        public bool Blight;
        public bool Burn;
    }

    internal sealed class SkillRole
    {
        public bool Attacks;
        public bool Heals;
        public bool Stuns;
        public bool SpendsCombo;
        public bool AppliesCombo;
        public float ComboSpendValue;
        public bool Bleed;
        public bool Blight;
        public bool Burn;
        public bool AppliesVulnerable;
        public bool AppliesStrength;
        public bool AppliesBlock;
        public bool AppliesGuard;
    }

    internal sealed class PartyKit
    {
        public readonly List<HeroKit> Heroes = new List<HeroKit>();
        public bool PartySpendsCombo;
        public bool PartyAttacks;
        public bool PartyHeals;
        public bool PartyStuns;
        public bool PartyBleed;
        public bool PartyBlight;
        public bool PartyBurn;
        public int AttackerCount;
        public int HealerCount;
        public float BestComboSpend;
        public uint UniqueHealerGuid;
        public bool HealerThreatened;

        public static PartyKit Scan(BattleTeams teams, uint performerGuid)
        {
            var kit = new PartyKit();
            if (teams == null)
                return kit;

            foreach (var actor in GameSnapshot.TeamActors(teams, BattleTeams.HERO_TEAM_INDEX))
            {
                if (actor == null)
                    continue;
                var hero = ReadHero(actor);
                kit.Heroes.Add(hero);
                if (!hero.Living)
                    continue;
                if (hero.SpendsCombo)
                {
                    kit.PartySpendsCombo = true;
                    if (hero.ComboSpendValue > kit.BestComboSpend)
                        kit.BestComboSpend = hero.ComboSpendValue;
                }
                if (hero.Attacks)
                {
                    kit.PartyAttacks = true;
                    kit.AttackerCount++;
                }
                if (hero.Heals)
                {
                    kit.PartyHeals = true;
                    kit.HealerCount++;
                    kit.UniqueHealerGuid = hero.Guid;
                    var body = GameSnapshot.Describe(actor);
                    if (body.DeathsDoor || body.HpPct <= 0.40f)
                        kit.HealerThreatened = true;
                }
                if (hero.Stuns)
                    kit.PartyStuns = true;
                if (hero.Bleed)
                    kit.PartyBleed = true;
                if (hero.Blight)
                    kit.PartyBlight = true;
                if (hero.Burn)
                    kit.PartyBurn = true;
            }

            if (kit.HealerCount != 1)
                kit.UniqueHealerGuid = 0;

            return kit;
        }

        public bool FollowUpSpendsCombo()
        {
            return PartySpendsCombo;
        }

        public bool HeroAttacks(uint guid)
        {
            return Hero(guid)?.Attacks == true;
        }

        public bool IsUniqueHealer(uint guid)
        {
            return UniqueHealerGuid != 0 && UniqueHealerGuid == guid;
        }

        public float ProtectBonus(TargetInfo target)
        {
            if (target == null || target.Actor == null || target.Corpse)
                return 0f;
            var guid = target.Actor.ActorGuid;
            var score = 0f;
            if (IsUniqueHealer(guid))
            {
                if (target.DeathsDoor)
                    score += 35f;
                else if (target.HpPct <= 0.35f)
                    score += 20f;
                else if (target.HpPct <= 0.55f)
                    score += 8f;
            }
            else if (HeroAttacks(guid) && (target.DeathsDoor || target.HpPct <= 0.35f))
                score += 6f;
            return score;
        }

        public float SetupBonus(SkillRole role, TargetInfo target, bool enemyTarget)
        {
            if (role == null)
                return 0f;
            var score = 0f;
            if (enemyTarget && target != null && !target.Corpse)
            {
                if (role.Bleed && PartyBleed)
                    score += 4f;
                if (role.Blight && PartyBlight)
                    score += 4f;
                if (role.Burn && PartyBurn)
                    score += 4f;
                if (role.AppliesVulnerable && PartyAttacks)
                    score += AttackerCount >= 2 ? 4f : 2f;
            }
            return score;
        }

        public JObject ToJson()
        {
            var spenders = new JArray();
            var attackers = new JArray();
            var healers = new JArray();
            var stunners = new JArray();
            for (var i = 0; i < Heroes.Count; i++)
            {
                var hero = Heroes[i];
                if (!hero.Living)
                    continue;
                if (hero.SpendsCombo)
                    spenders.Add(hero.Name);
                if (hero.Attacks)
                    attackers.Add(hero.Name);
                if (hero.Heals)
                    healers.Add(hero.Name);
                if (hero.Stuns)
                    stunners.Add(hero.Name);
            }
            return new JObject
            {
                ["combo_spenders"] = spenders,
                ["combo_best"] = BestComboSpend,
                ["attackers"] = attackers,
                ["healers"] = healers,
                ["stunners"] = stunners,
                ["bleed"] = PartyBleed,
                ["blight"] = PartyBlight,
                ["burn"] = PartyBurn
            };
        }

        private HeroKit Hero(uint guid)
        {
            for (var i = 0; i < Heroes.Count; i++)
            {
                if (Heroes[i].Guid == guid)
                    return Heroes[i];
            }
            return null;
        }

        private static HeroKit ReadHero(ActorInstance actor)
        {
            var hero = new HeroKit
            {
                Guid = actor.ActorGuid,
                Name = string.IsNullOrEmpty(actor.ActorName) ? actor.ActorGuid.ToString() : actor.ActorName,
                Rank = actor.TeamPosition,
                Living = actor.IsLiving && !GameSnapshot.IsCorpse(actor)
            };
            if (!hero.Living)
                return hero;

            IReadOnlyList<string> skillIds = null;
            try { skillIds = actor.GetEquippedCombatSkillIds(); }
            catch { }

            if (skillIds == null)
                return hero;

            for (var i = 0; i < skillIds.Count; i++)
            {
                var id = skillIds[i];
                var def = GetSkill(id);
                if (def == null || ItemPolicy.IsCombatItem(def, id, actor))
                    continue;
                if (IsUtilitySkill(def, id))
                    continue;

                var intel = DescribeSkill(def);
                if (intel.Attacks)
                    hero.Attacks = true;
                if (intel.Heals)
                    hero.Heals = true;
                if (intel.Stuns)
                    hero.Stuns = true;
                if (intel.SpendsCombo)
                    hero.SpendsCombo = true;
                if (intel.AppliesCombo)
                    hero.AppliesCombo = true;
                if (intel.ComboSpendValue > hero.ComboSpendValue)
                    hero.ComboSpendValue = intel.ComboSpendValue;
                if (intel.Bleed)
                    hero.Bleed = true;
                if (intel.Blight)
                    hero.Blight = true;
                if (intel.Burn)
                    hero.Burn = true;
            }

            return hero;
        }

        public static SkillRole DescribeSkill(ActorDataSkill def)
        {
            var intel = new SkillRole();
            if (def == null)
                return intel;
            try
            {
                if (!def.m_IsFriendly && !def.IsMoveSkill)
                    intel.Attacks = true;
            }
            catch { }

            try
            {
                var effects = def.ActorDataEffects;
                var groups = effects != null ? effects.EffectGroups : null;
                if (groups != null)
                {
                    for (var g = 0; g < groups.Count; g++)
                        ReadGroup(groups[g], intel);
                }
            }
            catch { }

            return intel;
        }

        private static void ReadGroup(object group, SkillRole intel)
        {
            if (group == null)
                return;
            var sources = GetMember(group, "SourceEffects") ?? GetMember(group, "m_SourceEffects");
            if (!(sources is IEnumerable items))
                return;
            foreach (var src in items)
            {
                var effect = AsEffect(src);
                if (effect == null)
                    continue;
                ReadEffect(effect, intel);
            }
        }

        private static void ReadEffect(EffectDefinition effect, SkillRole intel)
        {
            try
            {
                if (IsComboId(effect.m_TokenAddId) || IsComboId(effect.m_TokenAddTag))
                    intel.AppliesCombo = true;
                if (IdHas(effect.m_TokenAddId, "stun") || IdHas(effect.m_TokenAddId, "daze"))
                    intel.Stuns = true;
                if (IdHas(effect.m_TokenAddId, "vulnerable"))
                    intel.AppliesVulnerable = true;
                if (IdHas(effect.m_TokenAddId, "strength"))
                    intel.AppliesStrength = true;
                if (IdHas(effect.m_TokenAddId, "block"))
                    intel.AppliesBlock = true;
                if (IdHas(effect.m_TokenAddId, "guard"))
                    intel.AppliesGuard = true;
                if (IdHas(effect.m_DotAddId, "bleed"))
                    intel.Bleed = true;
                if (IdHas(effect.m_DotAddId, "blight"))
                    intel.Blight = true;
                if (IdHas(effect.m_DotAddId, "burn"))
                    intel.Burn = true;
                if (effect.m_HealthHealAmount > 0f || effect.m_HealthHealPercent > 0f)
                    intel.Heals = true;
                if (effect.m_HealthDamageAmount > 0f || effect.m_HealthDamagePercent > 0f)
                    intel.Attacks = true;
            }
            catch { }

            var comboCond = EffectNeedsTargetCombo(effect);
            var comboFlag = false;
            try { comboFlag = effect.m_IsCombo; } catch { }

            if (!comboCond && !comboFlag)
                return;

            if (IsMeaningfulComboSpend(effect))
            {
                intel.SpendsCombo = true;
                var value = 6f;
                try
                {
                    if (effect.m_AddTurn != 0 || IdHas(effect.m_TokenAddId, "stun"))
                        value += 10f;
                    value += Math.Max(effect.m_HealthDamageAmount, 0f);
                    if (effect.m_HealthHealAmount > 0f || effect.m_HealthHealPercent > 0f)
                        value += 8f;
                    if (!string.IsNullOrEmpty(effect.m_DotAddId))
                        value += 4f;
                }
                catch { }
                if (value > intel.ComboSpendValue)
                    intel.ComboSpendValue = value;
            }
        }

        private static bool IsMeaningfulComboSpend(EffectDefinition effect)
        {
            try
            {
                if (effect.m_IsCombo)
                    return true;
                if (effect.m_HealthDamageAmount > 0f || effect.m_HealthDamagePercent > 0f)
                    return true;
                if (effect.m_HealthHealAmount > 0f || effect.m_HealthHealPercent > 0f)
                    return true;
                if (effect.m_AddTurn != 0)
                    return true;
                if (!string.IsNullOrEmpty(effect.m_DotAddId))
                    return true;
                if (!string.IsNullOrEmpty(effect.m_TokenAddId) && !IsComboId(effect.m_TokenAddId))
                    return true;
            }
            catch { }

            try
            {
                if (IsComboId(effect.m_TokenRemoveId) || IsComboId(effect.m_TokenRemoveTag))
                    return false;
            }
            catch { }

            return false;
        }

        private static bool EffectNeedsTargetCombo(EffectDefinition effect)
        {
            if (effect == null)
                return false;
            if (IdLooksLikeTargetCombo(GetString(effect, "m_ConditionId")))
                return true;
            return ListHasTargetCombo(effect.AllConditions) || ListHasTargetCombo(effect.AnyConditions);
        }

        private static bool ListHasTargetCombo(IEnumerable list)
        {
            if (list == null)
                return false;
            foreach (var item in list)
            {
                var cond = item as ConditionDefinition;
                if (cond != null)
                {
                    if (cond.IsInverse)
                        continue;
                    if (IdLooksLikeTargetCombo(cond.GetKey()) || IdLooksLikeTargetCombo(cond.ConditionString))
                        return true;
                    continue;
                }
                if (IdLooksLikeTargetCombo(item as string))
                    return true;
            }
            return false;
        }

        private static bool IdLooksLikeTargetCombo(string id)
        {
            if (string.IsNullOrEmpty(id))
                return false;
            if (id.IndexOf("not_combo", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (id.IndexOf("skill_is_combo", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return id.IndexOf("target_is_combo", StringComparison.OrdinalIgnoreCase) >= 0
                   || string.Equals(id, "combo", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsComboId(string id)
        {
            return IdHas(id, "combo");
        }

        private static bool IdHas(string id, string key)
        {
            return !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(key)
                   && id.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsUtilitySkill(ActorDataSkill def, string skillId)
        {
            try
            {
                if (def.IsMoveSkill)
                    return true;
            }
            catch { }
            return !string.IsNullOrEmpty(skillId) &&
                   (skillId.IndexOf("_move", StringComparison.OrdinalIgnoreCase) >= 0
                    || skillId.StartsWith("pass_", StringComparison.OrdinalIgnoreCase));
        }

        private static EffectDefinition AsEffect(object src)
        {
            if (src == null)
                return null;
            var direct = src as EffectDefinition;
            if (direct != null)
                return direct;
            var inner = GetMember(src, "Definition") ?? GetMember(src, "m_Definition");
            direct = inner as EffectDefinition;
            if (direct != null)
                return direct;
            var id = inner as string ?? GetMember(src, "m_SourceId") as string;
            return GetEffect(id);
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

        private static EffectDefinition GetEffect(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            try
            {
                var typed = LibraryEffect.LibraryEffectInstance;
                if (typed != null)
                    return typed.GetLibraryElement(id);
            }
            catch { }
            try
            {
                var lib = SingletonMonoBehaviour<Library<string, EffectDefinition>>.Instance;
                return lib != null ? lib.GetLibraryElement(id) : null;
            }
            catch
            {
                return null;
            }
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

        private static string GetString(object obj, string name)
        {
            return GetMember(obj, name) as string;
        }
    }
}
