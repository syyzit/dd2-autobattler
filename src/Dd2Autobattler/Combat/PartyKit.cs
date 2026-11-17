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
        public string ClassId;
        public int Rank;
        public bool Living;
        public bool Attacks;
        public bool Heals;
        public bool Stuns;
        public bool SpendsCombo;
        public bool AppliesCombo;
        public float ComboSpendValue;
        public int ComboHitRanks = PartySynergy.ComboRanksUnknown;
        public int AttackHitRanks = PartySynergy.ComboRanksUnknown;
        public bool Bleed;
        public bool Blight;
        public bool Burn;
        public bool AcidRain;
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
        public bool AppliesTaunt;
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
        // Round memory for "spender already acted"; null = live CombatMemory.
        public MemoryState Memory;

        public static PartyKit Scan(TurnSnapshot snap)
        {
            var kit = new PartyKit();
            if (snap == null)
                return kit;
            kit.Memory = snap.Memory;

            foreach (var actor in snap.Heroes)
            {
                if (actor == null)
                    continue;
                var hero = ReadHero(actor, snap);
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
                    var body = actor;
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

        public bool FollowUpSpendsCombo(uint performerGuid)
        {
            if (!PartySpendsCombo)
                return false;
            for (var i = 0; i < Heroes.Count; i++)
            {
                var hero = Heroes[i];
                if (!hero.Living || !hero.SpendsCombo || hero.Guid == performerGuid)
                    continue;
                if (!ComboSpenderActed(hero.Guid))
                    return true;
            }
            return false;
        }

        internal bool ComboSpenderActed(uint guid)
        {
            return (Memory ?? CombatMemory.Live).ComboSpenderActed(guid);
        }

        public bool HeroSpendsCombo(uint guid)
        {
            return Hero(guid)?.SpendsCombo == true;
        }

        public bool HeroAttacks(uint guid)
        {
            return Hero(guid)?.Attacks == true;
        }

        public bool HitsEnemyRank(uint guid, int enemyRank)
        {
            var hero = Hero(guid);
            if (hero == null || !hero.Living || !hero.Attacks)
                return false;
            return PartySynergy.HitsRankMask(hero.AttackHitRanks, enemyRank);
        }

        // Rank-walk ping-pong: MAA shoves Dismas off the Pistol rank, then
        // Dismas walks back. If any other attacker already hits this tile, sit.
        public bool AllyHitsEnemyRank(uint performerGuid, int enemyRank)
        {
            for (var i = 0; i < Heroes.Count; i++)
            {
                var hero = Heroes[i];
                if (hero == null || hero.Guid == performerGuid)
                    continue;
                if (HitsEnemyRank(hero.Guid, enemyRank))
                    return true;
            }
            return false;
        }

        public bool HeroHeals(uint guid)
        {
            return Hero(guid)?.Heals == true;
        }

        public bool IsUniqueHealer(uint guid)
        {
            return UniqueHealerGuid != 0 && UniqueHealerGuid == guid;
        }

        public float ProtectBonus(TargetInfo target)
        {
            if (target == null || target.Guid == 0 || target.Corpse)
                return 0f;
            var guid = target.Guid;
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
            // wiki.gg/Focused_Fault: support the Seen hero with heals.
            if (target.EyesFocus > 0)
            {
                if (target.DeathsDoor)
                    score += 35f;
                else if (target.HpPct <= 0.55f)
                    score += 20f;
                else
                    score += 10f;
            }
            return score;
        }

        public float SetupBonus(SkillRole role, TargetInfo target, bool enemyTarget, PreviewScore preview = null)
        {
            if (role == null)
                return 0f;
            var score = 0f;
            if (enemyTarget && target != null && !target.Corpse)
            {
                if (role.Bleed && PartyBleed)
                    score += 4f * (preview != null ? preview.Land("bleed") : 1f);
                if (role.Blight && PartyBlight)
                    score += 4f * (preview != null ? preview.Land("blight") : 1f);
                if (role.Burn && PartyBurn)
                    score += 4f * (preview != null ? preview.Land("burn") : 1f);
                if (role.AppliesVulnerable && PartyAttacks)
                    score += (AttackerCount >= 2 ? 4f : 2f) * (preview != null ? preview.Land("debuff") : 1f);
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

        private static HeroKit ReadHero(TargetInfo actor, TurnSnapshot snap)
        {
            var hero = new HeroKit
            {
                Guid = actor.Guid,
                Name = string.IsNullOrEmpty(actor.Name) ? actor.Guid.ToString() : actor.Name,
                Rank = actor.Rank,
                Living = actor.Living && !actor.Corpse
            };
            hero.ClassId = actor.ClassId;
            if (!hero.Living)
                return hero;

            var skillIds = actor.SkillIds;
            if (skillIds == null)
                return hero;

            for (var i = 0; i < skillIds.Count; i++)
            {
                var id = skillIds[i];
                var def = snap.Skill(id);
                if (def == null || ItemPolicy.IsCombatItem(def, id, actor))
                    continue;
                if (IsUtilitySkill(def, id))
                    continue;

                var intel = def.Role ?? new SkillRole();
                if (intel.Attacks)
                {
                    hero.Attacks = true;
                    var hit = ComboHitMask(def, actor);
                    if (hit != PartySynergy.ComboRanksUnknown)
                    {
                        if (hero.AttackHitRanks == PartySynergy.ComboRanksUnknown)
                            hero.AttackHitRanks = 0;
                        hero.AttackHitRanks |= hit;
                    }
                }
                if (intel.Heals)
                    hero.Heals = true;
                if (intel.Stuns)
                    hero.Stuns = true;
                if (intel.SpendsCombo)
                {
                    hero.SpendsCombo = true;
                    var mask = ComboHitMask(def, actor);
                    if (mask != PartySynergy.ComboRanksUnknown)
                    {
                        if (hero.ComboHitRanks == PartySynergy.ComboRanksUnknown)
                            hero.ComboHitRanks = 0;
                        hero.ComboHitRanks |= mask;
                    }
                }
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
                if (id != null && id.IndexOf("acid_rain", StringComparison.OrdinalIgnoreCase) >= 0)
                    hero.AcidRain = true;
            }

            return hero;
        }

        // Enemy ranks (size 1 or 2) this skill hits from the hero's rank.
        // Masks come from the skill snapshot (owner size). Same per-rank
        // "queried" rule as the old try { size1 || size2 } loop: a rank whose
        // query threw is skipped; all skipped = unknown.
        private static int ComboHitMask(SkillSnap def, TargetInfo actor)
        {
            if (def == null || actor == null || def.TargetMasks == null)
                return PartySynergy.ComboRanksUnknown;
            var rank = actor.Rank;
            var mask = 0;
            var queried = false;
            for (var enemyRank = 0; enemyRank < 4; enemyRank++)
            {
                if (def.TargetThrows(rank, enemyRank, 1))
                    continue;
                if (def.Targets(rank, enemyRank, 1))
                {
                    mask |= 1 << enemyRank;
                    queried = true;
                    continue;
                }
                if (def.TargetThrows(rank, enemyRank, 2))
                    continue;
                if (def.Targets(rank, enemyRank, 2))
                    mask |= 1 << enemyRank;
                queried = true;
            }
            return queried ? mask : PartySynergy.ComboRanksUnknown;
        }

        // Game read (effect walk). Called by LiveTurnReader into SkillSnap.Role.
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

            // CSV hwm_open_vein: skill_dot_*_bleed, end_combo, combo_increase_bleed_dealt_100pct.
            // Preview often omits Consume combo, so mark it as a spend here.
            try
            {
                var key = def.GetKey();
                if (KitSafety.IdHas(key, "open_vein"))
                {
                    intel.Bleed = true;
                    intel.SpendsCombo = true;
                    if (intel.ComboSpendValue < 12f)
                        intel.ComboSpendValue = 12f;
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
                if (IdHas(effect.m_TokenAddId, "taunt"))
                    intel.AppliesTaunt = true;
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

        private static bool IsUtilitySkill(SkillSnap def, string skillId)
        {
            if (def.IsMove)
                return true;
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
