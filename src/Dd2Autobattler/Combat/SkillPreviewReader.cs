using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Assets.Code.Actor;
using Assets.Code.Dot;
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
        public readonly List<string> ApplyPerformer = new List<string>();
        public readonly List<string> RemoveTarget = new List<string>();
        public readonly List<string> RemovePerformer = new List<string>();
        public readonly List<uint> HitGuids = new List<uint>();
        public bool Blocked;
        public uint GuardGuid;
        public bool ResistOk;
        public float ResistBleed;
        public float ResistBlight;
        public float ResistBurn;
        public float ResistStun;
        public float ResistDebuff;
        public float ResistMove;
        public float ApplyBleed;
        public float ApplyBlight;
        public float ApplyBurn;

        // 1 = always lands. Unknown preview resists fail open (1).
        public float Land(string key)
        {
            if (!ResistOk || string.IsNullOrEmpty(key))
                return 1f;
            var resist = 0f;
            if (key.IndexOf("bleed", StringComparison.OrdinalIgnoreCase) >= 0)
                resist = ResistBleed;
            else if (key.IndexOf("blight", StringComparison.OrdinalIgnoreCase) >= 0)
                resist = ResistBlight;
            else if (key.IndexOf("burn", StringComparison.OrdinalIgnoreCase) >= 0)
                resist = ResistBurn;
            else if (key.IndexOf("move", StringComparison.OrdinalIgnoreCase) >= 0)
                resist = ResistMove;
            else if (key.IndexOf("stun", StringComparison.OrdinalIgnoreCase) >= 0
                     || key.IndexOf("daze", StringComparison.OrdinalIgnoreCase) >= 0)
                resist = ResistStun;
            else if (key.IndexOf("debuff", StringComparison.OrdinalIgnoreCase) >= 0
                     || key.IndexOf("weak", StringComparison.OrdinalIgnoreCase) >= 0
                     || key.IndexOf("blind", StringComparison.OrdinalIgnoreCase) >= 0
                     || key.IndexOf("vulnerable", StringComparison.OrdinalIgnoreCase) >= 0)
                resist = ResistDebuff;
            else
                return 1f;
            return LandFromResist(resist);
        }

        internal static float LandFromResist(float resist)
        {
            if (resist < 0f)
                resist = 0f;
            if (resist > 1f)
                resist = 1f;
            return 1f - resist;
        }

        // Opening a DoT that mostly bounces is worse than a real swing.
        internal static float DotOpenPay(float land)
        {
            if (land <= 0.35f)
                return -12f;
            return 5f * land;
        }

        // Live magnitude (effect IDs + skill-locked trinket stats) times land.
        // Stacking the same DoT is worse than a new host.
        internal static float DotApplyPay(float amount, float alreadyOn, float land, int livingEnemies)
        {
            if (amount < 1f)
                return 0f;
            if (land <= 0.35f)
                return -12f;
            if (alreadyOn > 1f)
                return -8f * land;
            var ticks = livingEnemies <= 1 ? 1f : 1.5f;
            return amount * land * ticks;
        }
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
                AddHitGuid(result, targetGuid);

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
            result.Blocked |= AsBool(GetMember(item, "m_IsBlocked")) || AsBool(GetMember(item, "IsBlocked"));
            var dmgObj = GetMember(item, "m_Damage");
            if (dmgObj != null)
                result.Blocked |= AsBool(GetMember(dmgObj, "m_IsBlocked")) || AsBool(GetMember(dmgObj, "IsBlocked"));
            var guard = AsUInt(GetMember(item, "m_GuardingActorGuid"));
            if (guard != 0)
                result.GuardGuid = guard;
            result.Kills |= AsBool(GetMember(item, "IsKill")) || AsBool(GetMember(item, "IsDamageKill"));
            result.HealsDeathsDoor |= result.Heal > 0f && AsBool(GetMember(item, "TargetIsAtDeathsDoor"));
            AddHitGuid(result, AsUInt(GetMember(item, "m_TargetActorGuid")));

            AddStrings(GetMember(item, "m_TargetAttemptedTokenConsumeIds"), result.ConsumeTarget);
            AddStrings(GetMember(item, "m_PerformerAttemptedTokenConsumeIds"), result.ConsumePerformer);
            AddStrings(GetMember(item, "m_TargetTokenRemoveIds"), result.RemoveTarget);
            AddStrings(GetMember(item, "m_PerformerTokenRemoveIds"), result.RemovePerformer);
            AddTokenAddsFromEffects(GetMember(item, "m_TargetConditionValidEffectIds"), result.ApplyTarget);
            AddTokenAddsFromEffects(GetMember(item, "m_PerformerConditionValidEffectIds"), result.ApplyPerformer);
            AddDotsFromEffectIds(GetMember(item, "m_TargetConditionValidEffectIds"), result);
            ReadResists(GetMember(item, "m_TargetResistanceStatValues"),
                GetMember(item, "m_PerformerResistanceIgnoreStatValues"), result);

            if (result.Raw == null)
                result.Raw = new JObject();
        }

        private static void ReadResists(object targetDict, object ignoreDict, PreviewScore result)
        {
            if (result == null)
                return;
            var any = CopyResistDict(targetDict, result, false);
            CopyResistDict(ignoreDict, result, true);
            if (any)
                result.ResistOk = true;
        }

        private static bool CopyResistDict(object dict, PreviewScore result, bool subtractIgnore)
        {
            if (dict == null)
                return false;
            var any = false;
            if (dict is IDictionary map)
            {
                foreach (DictionaryEntry entry in map)
                {
                    if (ApplyResistKey(result, entry.Key as string, AsFloat(entry.Value), subtractIgnore))
                        any = true;
                }
                return any;
            }
            if (!(dict is IEnumerable items))
                return false;
            foreach (var item in items)
            {
                if (item == null)
                    continue;
                var key = GetMember(item, "Key") as string;
                var val = AsFloat(GetMember(item, "Value"));
                if (ApplyResistKey(result, key, val, subtractIgnore))
                    any = true;
            }
            return any;
        }

        private static bool ApplyResistKey(PreviewScore result, string key, float value, bool subtractIgnore)
        {
            if (string.IsNullOrEmpty(key))
                return false;
            if (key.IndexOf("bleed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.ResistBleed = subtractIgnore ? result.ResistBleed - value : value;
                return true;
            }
            if (key.IndexOf("blight", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.ResistBlight = subtractIgnore ? result.ResistBlight - value : value;
                return true;
            }
            if (key.IndexOf("burn", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.ResistBurn = subtractIgnore ? result.ResistBurn - value : value;
                return true;
            }
            if (key.IndexOf("stun", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.ResistStun = subtractIgnore ? result.ResistStun - value : value;
                return true;
            }
            if (key.IndexOf("debuff", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.ResistDebuff = subtractIgnore ? result.ResistDebuff - value : value;
                return true;
            }
            if (key.IndexOf("move", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.ResistMove = subtractIgnore ? result.ResistMove - value : value;
                return true;
            }
            return false;
        }

        private static void AddTokenAddsFromEffects(object effectIds, List<string> dest)
        {
            if (dest == null || !(effectIds is IEnumerable ids))
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
                        AddUnique(dest, def.m_TokenAddId);
                    else if (!string.IsNullOrEmpty(def.m_TokenAddTag))
                        AddUnique(dest, def.m_TokenAddTag);
                }
                catch
                {
                    // effect shape is best-effort
                }
            }
        }

        // Preview lists target token adds. Performer Winded/Ruin/Blind-strip live
        // on PERFORMER* effect groups — walk those even when ApplyTarget is full.
        public static void AddSkillTokenAdds(ActorDataSkill skill, PreviewScore result)
        {
            if (skill == null || result == null)
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
                    ReadGroupTokens(groups[i], result);
                if (result.ApplyBleed < 1f && result.ApplyBlight < 1f && result.ApplyBurn < 1f)
                {
                    for (var i = 0; i < groups.Count; i++)
                        ReadGroupDots(groups[i], result);
                }
            }
            catch
            {
                // skill-data walk is a fallback only
            }
        }

        private static void ReadGroupDots(object group, PreviewScore result)
        {
            if (group == null || result == null)
                return;
            if (IsPerformerGroup(GetMember(group, "m_EffectType")))
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
                AddDotFromEffect(def, result);
            }
        }

        private static void ReadGroupTokens(object group, PreviewScore result)
        {
            if (group == null)
                return;
            var performer = IsPerformerGroup(GetMember(group, "m_EffectType"));
            var fillTargetAdds = !performer && result.ApplyTarget.Count == 0;
            if (!performer && !fillTargetAdds)
                return;
            var apply = performer ? result.ApplyPerformer : result.ApplyTarget;
            var remove = performer ? result.RemovePerformer : result.RemoveTarget;
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
                try
                {
                    var addId = !string.IsNullOrEmpty(def.m_TokenAddId) ? def.m_TokenAddId : def.m_TokenAddTag;
                    if (!string.IsNullOrEmpty(addId))
                    {
                        if (!performer && (addId.IndexOf("stun", StringComparison.OrdinalIgnoreCase) >= 0
                            || addId.IndexOf("daze", StringComparison.OrdinalIgnoreCase) >= 0))
                            continue;
                        AddUnique(apply, addId);
                    }
                    var removeId = def.m_TokenRemoveId;
                    if (string.IsNullOrEmpty(removeId))
                        removeId = def.m_TokenRemoveTag;
                    if (!string.IsNullOrEmpty(removeId))
                        AddUnique(remove, removeId);
                    AddConvertFrom(def, remove);
                    if (!performer)
                        AddDotFromEffect(def, result);
                }
                catch
                {
                    // effect shape is best-effort
                }
            }
        }

        internal static void AddConditionalDotDealt(ActorInstance performer, string skillId, PreviewScore result)
        {
            if (performer == null || result == null || string.IsNullOrEmpty(skillId))
                return;
            try
            {
                var buffs = performer.BuffContainer;
                if (buffs == null)
                    return;
                var instances = buffs.GetInstances();
                if (instances == null)
                    return;
                for (var i = 0; i < instances.Count; i++)
                {
                    var inst = instances[i];
                    if (inst == null)
                        continue;
                    var def = GetMember(inst, "Definition") ?? GetMember(inst, "m_Definition");
                    if (def == null)
                        try { def = inst.GetType().GetProperty("Definition").GetValue(inst, null); } catch { }
                    if (def == null)
                        continue;
                    var cond = GetMember(def, "m_ConditionId") as string;
                    if (string.IsNullOrEmpty(cond) || cond.IndexOf("skill_is_", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    var key = cond.Substring(cond.IndexOf("skill_is_", StringComparison.OrdinalIgnoreCase) + 9);
                    if (skillId.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    AddDotDealtFromStats(def, result);
                }
            }
            catch
            {
                // buff shape is best-effort
            }
        }

        internal static void ApplyComboBleedDouble(string skillId, bool targetCombo, PreviewScore result)
        {
            if (result == null || !targetCombo || result.ApplyBleed < 1f)
                return;
            if (!KitSafety.IdHas(skillId, "open_vein"))
                return;
            result.ApplyBleed *= 2f;
        }

        private static void AddDotsFromEffectIds(object effectIds, PreviewScore result)
        {
            if (result == null || !(effectIds is IEnumerable ids))
                return;
            foreach (var item in ids)
            {
                var id = item as string;
                if (string.IsNullOrEmpty(id))
                    continue;
                AddDotFromEffect(GetEffect(id), result);
            }
        }

        private static void AddDotFromEffect(EffectDefinition def, PreviewScore result)
        {
            if (def == null || result == null)
                return;
            string dotId = null;
            try { dotId = def.m_DotAddId; } catch { }
            if (string.IsNullOrEmpty(dotId))
                return;
            var mag = 0;
            try
            {
                var dotDef = GetDotDef(dotId);
                if (dotDef != null)
                    mag = DotDescription.GetDotMagnitude(dotDef);
            }
            catch { }
            if (mag <= 0)
                return;
            AddDotKind(result, dotId, mag);
        }

        private static DotDefinition GetDotDef(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            try
            {
                var lib = SingletonMonoBehaviour<Library<string, DotDefinition>>.Instance;
                return lib != null ? lib.GetLibraryElement(id) : null;
            }
            catch
            {
                return null;
            }
        }

        private static void AddDotKind(PreviewScore result, string id, float mag)
        {
            if (result == null || mag < 1f || string.IsNullOrEmpty(id))
                return;
            if (id.IndexOf("bleed", StringComparison.OrdinalIgnoreCase) >= 0)
                result.ApplyBleed += mag;
            else if (id.IndexOf("blight", StringComparison.OrdinalIgnoreCase) >= 0)
                result.ApplyBlight += mag;
            else if (id.IndexOf("burn", StringComparison.OrdinalIgnoreCase) >= 0)
                result.ApplyBurn += mag;
        }

        private static void AddDotDealtFromStats(object def, PreviewScore result)
        {
            if (def == null || result == null)
                return;
            try
            {
                var stats = GetMember(def, "ActorDataStats") ?? GetMember(def, "m_ActorDataStats")
                            ?? def;
                AddDotDealtKey(stats, "bleed", result);
                AddDotDealtKey(stats, "blight", result);
                AddDotDealtKey(stats, "burn", result);
            }
            catch { }
        }

        private static void AddDotDealtKey(object stats, string kind, PreviewScore result)
        {
            if (stats == null)
                return;
            var t = stats.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            object dict = null;
            try
            {
                var p = t.GetProperty("m_SubStats", flags) ?? t.GetProperty("SubStats", flags);
                if (p != null)
                    dict = p.GetValue(stats, null);
            }
            catch { }
            if (dict == null)
            {
                try
                {
                    var f = t.GetField("m_SubStats", flags) ?? t.GetField("SubStats", flags);
                    if (f != null)
                        dict = f.GetValue(stats);
                }
                catch { }
            }
            var add = ReadDealtChange(dict, kind);
            if (add < 0.5f)
                add = ReadDealtChange(stats, kind);
            if (add < 0.5f)
                return;
            AddDotKind(result, kind, add);
        }

        private static float ReadDealtChange(object bag, string kind)
        {
            if (bag == null || string.IsNullOrEmpty(kind))
                return 0f;
            if (bag is IDictionary map)
            {
                foreach (DictionaryEntry entry in map)
                {
                    var key = entry.Key as string ?? Convert.ToString(entry.Key);
                    if (string.IsNullOrEmpty(key))
                        continue;
                    if (key.IndexOf("dot_effect_value_dealt", StringComparison.OrdinalIgnoreCase) < 0
                        && key.IndexOf(kind, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (key.IndexOf(kind, StringComparison.OrdinalIgnoreCase) < 0
                        && Convert.ToString(entry.Value).IndexOf(kind, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        var n = AsFloat(entry.Value);
                        if (n >= 0.5f && key.IndexOf("dealt", StringComparison.OrdinalIgnoreCase) >= 0)
                            return n;
                        continue;
                    }
                    var v = AsFloat(entry.Value);
                    if (v >= 0.5f)
                        return v;
                }
            }
            return 0f;
        }

        private static void AddConvertFrom(EffectDefinition def, List<string> remove)
        {
            if (def == null || remove == null)
                return;
            try
            {
                var ids = GetMember(def, "m_TokenConvertFromTokenIds");
                if (ids is string one)
                    AddUnique(remove, one);
                else
                    AddStrings(ids, remove);
            }
            catch { }
        }

        private static bool IsPerformerGroup(object effectType)
        {
            if (effectType == null)
                return false;
            try
            {
                if (Equals(effectType, ActorDataEffectType.PERFORMER)
                    || Equals(effectType, ActorDataEffectType.PERFORMER_AFTER_TARGET)
                    || Equals(effectType, ActorDataEffectType.PERFORMER_PER_TARGET))
                    return true;
            }
            catch { }
            var name = effectType.ToString();
            if (string.IsNullOrEmpty(name))
                return false;
            if (name.IndexOf("TEAM", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return name.IndexOf("PERFORMER", StringComparison.OrdinalIgnoreCase) >= 0;
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

        private static uint AsUInt(object value)
        {
            if (value == null) return 0;
            try { return Convert.ToUInt32(value); } catch { return 0; }
        }

        private static void AddHitGuid(PreviewScore result, uint guid)
        {
            if (result == null || guid == 0)
                return;
            for (var i = 0; i < result.HitGuids.Count; i++)
            {
                if (result.HitGuids[i] == guid)
                    return;
            }
            result.HitGuids.Add(guid);
        }
    }
}
