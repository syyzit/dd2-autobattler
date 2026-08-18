using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Assets.Code.Effect;
using Assets.Code.Library;
using Assets.Code.Skill;
using Assets.Code.Skill.Queries;
using Assets.Code.Utils;
using Newtonsoft.Json.Linq;

namespace Dd2Autobattler.Combat
{
    public sealed class PreviewScore
    {
        public bool Ok;
        public float Damage;
        public float DamageLow;
        public float DamageHigh;
        public float Heal;
        public float HitChance = 1f;
        public bool Kills;
        public bool HealsDeathsDoor;
        public bool DamageValid;
        public bool HealValid;
        public string Error;
        public JObject Raw;
        public readonly List<string> ConsumeTarget = new List<string>();
        public readonly List<string> ConsumePerformer = new List<string>();
        public readonly List<string> ApplyTarget = new List<string>();
        public readonly List<string> RemoveTarget = new List<string>();
    }

    public static class SkillPreviewReader
    {
        private static bool _loggedShape;

        public static PreviewScore Score(uint performerGuid, string skillId, uint targetGuid)
        {
            var result = new PreviewScore();
            try
            {
                var query = QuerySkillPreview.Trigger(performerGuid, skillId, targetGuid);
                if (query == null || !query.IsValid)
                {
                    result.Error = "preview_invalid";
                    return result;
                }

                result.Ok = true;
                var previews = query.m_SkillPreviews;
                if (previews == null)
                    return result;

                foreach (var item in previews)
                    ReadPreview(item, result);

                if (!_loggedShape && previews.Count > 0)
                {
                    _loggedShape = true;
                    Logging.DecisionLog.Info("Preview item type: " + previews[0].GetType().FullName);
                }
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = ex.GetType().Name + ": " + ex.Message;
            }
            return result;
        }

        private static void ReadPreview(object item, PreviewScore result)
        {
            if (item == null)
                return;

            var damageValid = AsBool(GetMember(item, "m_IsDamageValid"));
            var healValid = AsBool(GetMember(item, "m_IsHealValid"));
            var low = AsFloat(GetMember(item, "m_DamageLow"));
            var high = AsFloat(GetMember(item, "m_DamageHigh"));
            var hit = AsFloat(GetMember(item, "m_ToHitChance"));
            var critChance = AsFloat(GetMember(item, "m_CritChance"));
            var critDmg = AsFloat(GetMember(item, "m_CritDamage"));
            var healBase = AsFloat(GetMember(item, "m_TargetHealthHealBase"));
            var healRange = AsFloat(GetMember(item, "m_TargetHealthHealRange"));

            // Nested ActorResult uses HealthDamage / HealthHeal instead of the SkillPreview names.
            if (low <= 0f && high <= 0f)
            {
                low = AsFloat(GetMember(item, "HealthDamage"));
                high = low;
                if (!damageValid)
                    damageValid = AsBool(GetMember(item, "IsDamaging")) || low > 0f;
            }
            if (healBase <= 0f)
            {
                healBase = AsFloat(GetMember(item, "HealthHeal"));
                if (!healValid)
                    healValid = AsBool(GetMember(item, "IsHealthHeal")) || healBase > 0f;
            }

            var toHitValid = AsBool(GetMember(item, "m_IsToHitValid"));
            if (hit < 0f || hit > 1f)
                hit = 1f;
            else if (hit == 0f && !toHitValid)
                hit = 1f;

            var mid = (low + high) * 0.5f;
            if (critChance > 0f && critDmg > mid)
                mid += critChance * (critDmg - mid);

            if (damageValid || mid > 0f)
            {
                result.DamageValid = true;
                result.DamageLow += low;
                result.DamageHigh += high;
                result.Damage += mid * hit;
            }
            if (healValid || healBase > 0f)
            {
                result.HealValid = true;
                result.Heal += healBase + healRange * 0.5f;
            }
            result.HitChance = hit;
            result.Kills |= AsBool(GetMember(item, "IsKill")) || AsBool(GetMember(item, "IsDamageKill"));
            result.HealsDeathsDoor |= result.Heal > 0f && AsBool(GetMember(item, "TargetIsAtDeathsDoor"));

            AddStrings(GetMember(item, "m_TargetAttemptedTokenConsumeIds"), result.ConsumeTarget);
            AddStrings(GetMember(item, "m_PerformerAttemptedTokenConsumeIds"), result.ConsumePerformer);
            AddStrings(GetMember(item, "m_TargetTokenRemoveIds"), result.RemoveTarget);
            AddTokenAddsFromEffects(GetMember(item, "m_TargetConditionValidEffectIds"), result);

            if (result.Raw == null)
                result.Raw = new JObject();
        }

        private static void AddTokenAddsFromEffects(object effectIds, PreviewScore result)
        {
            if (!(effectIds is IEnumerable ids))
                return;
            foreach (var item in ids)
            {
                var id = item as string;
                if (string.IsNullOrEmpty(id))
                    continue;
                var def = GetEffect(id);
                if (def == null)
                    continue;
                try
                {
                    if (!string.IsNullOrEmpty(def.m_TokenAddId))
                        AddUnique(result.ApplyTarget, def.m_TokenAddId);
                    else if (!string.IsNullOrEmpty(def.m_TokenAddTag))
                        AddUnique(result.ApplyTarget, def.m_TokenAddTag);
                }
                catch
                {
                    // effect shape is best-effort
                }
            }
        }

        public static void AddSkillTokenAdds(ActorDataSkill skill, PreviewScore result)
        {
            if (skill == null || result == null || result.ApplyTarget.Count > 0)
                return;
            try
            {
                var effects = skill.ActorDataEffects;
                if (effects == null)
                    return;
                var groups = effects.EffectGroups;
                if (groups == null)
                    return;
                for (var i = 0; i < groups.Count; i++)
                    ReadGroupTokenAdds(groups[i], result);
            }
            catch
            {
                // skill-data walk is a fallback only
            }
        }

        private static void ReadGroupTokenAdds(object group, PreviewScore result)
        {
            if (group == null)
                return;
            var sources = GetMember(group, "m_SourceEffects") ?? GetMember(group, "SourceEffects");
            if (!(sources is IEnumerable items))
                return;
            foreach (var src in items)
            {
                if (src == null)
                    continue;
                var def = src as EffectDefinition;
                if (def == null)
                {
                    var inner = GetMember(src, "m_Definition") ?? GetMember(src, "Definition") ?? GetMember(src, "m_SourceId");
                    def = inner as EffectDefinition;
                    if (def == null && inner is string sid)
                        def = GetEffect(sid);
                }
                if (def == null)
                    continue;
                var tokenId = !string.IsNullOrEmpty(def.m_TokenAddId) ? def.m_TokenAddId : def.m_TokenAddTag;
                if (string.IsNullOrEmpty(tokenId))
                    continue;
                // Stun is resisted; only trust preview-validated effects for it.
                if (tokenId.IndexOf("stun", StringComparison.OrdinalIgnoreCase) >= 0
                    || tokenId.IndexOf("daze", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                AddUnique(result.ApplyTarget, tokenId);
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

        private static void AddStrings(object list, List<string> dest)
        {
            if (!(list is IEnumerable items))
                return;
            foreach (var item in items)
            {
                var s = item as string;
                if (!string.IsNullOrEmpty(s))
                    AddUnique(dest, s);
            }
        }

        private static void AddUnique(List<string> dest, string value)
        {
            if (dest == null || string.IsNullOrEmpty(value))
                return;
            for (var i = 0; i < dest.Count; i++)
            {
                if (string.Equals(dest[i], value, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            dest.Add(value);
        }

        private static object GetMember(object obj, string name)
        {
            var type = obj.GetType();
            var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
                return prop.GetValue(obj, null);
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field != null ? field.GetValue(obj) : null;
        }

        private static float AsFloat(object value)
        {
            if (value == null) return 0f;
            try { return Convert.ToSingle(value); } catch { return 0f; }
        }

        private static bool AsBool(object value)
        {
            if (value == null) return false;
            try { return Convert.ToBoolean(value); } catch { return false; }
        }
    }
}
