using System;
using System.Collections.Generic;
using System.Text;
using Assets.Code.Actor;
using Assets.Code.Combat;
using Assets.Code.Dot;
using Assets.Code.Token;
using Newtonsoft.Json.Linq;

namespace Dd2Autobattler.Combat
{
    public sealed class TargetInfo
    {
        public ActorInstance Actor;
        public float Hp;
        public float HpPct;
        public bool DeathsDoor;
        public bool Corpse;
        public float Stress;
        public float NextDot;
        public float BleedDot;
        public float BlightDot;
        public float BurnDot;
        public bool DiesToDot;
        public bool Stealth;
        public bool Riposte;
        public bool Dodge;
        public bool Combo;
        public bool Stun;
        public bool Vulnerable;
        public bool Weak;
        public bool Blind;
        public int StrengthCount;
        public int BlockCount;
        public int DodgeCount;
        public bool Tangled;
        public bool TangledLock;
        public int EyesFocus;
        public bool LungInflate;
        public int PositiveTokens;
    }

    public static class GameSnapshot
    {
        public static JObject Actor(ActorInstance actor)
        {
            if (actor == null)
                return null;

            var tokens = new JArray();
            try
            {
                var container = actor.TokenContainer;
                if (container != null)
                {
                    var instances = container.GetInstances();
                    if (instances != null)
                    {
                        for (var i = 0; i < instances.Count; i++)
                        {
                            var inst = instances[i];
                            if (inst == null)
                                continue;
                            var def = inst.Definition;
                            tokens.Add(new JObject
                            {
                                ["id"] = def != null ? def.GetKey() : "?",
                                ["dur"] = inst.GetDurationAmount()
                            });
                        }
                    }
                }
            }
            catch
            {
                // token dump is best-effort
            }

            string classId = null;
            string pathId = null;
            try { classId = actor.ActorDataClass != null ? actor.ActorDataClass.GetKey() : null; } catch { }
            try { pathId = actor.ActorDataPath != null ? actor.ActorDataPath.GetKey() : null; } catch { }

            var info = Describe(actor);

            return new JObject
            {
                ["guid"] = actor.ActorGuid,
                ["name"] = actor.ActorName,
                ["class"] = classId,
                ["path"] = pathId,
                ["team"] = actor.TeamIndex,
                ["rank"] = actor.TeamPosition,
                ["hp"] = info.Hp,
                ["hp_max"] = actor.CurrentHpMax,
                ["hp_pct"] = info.HpPct,
                ["stress"] = actor.Stress,
                ["living"] = actor.IsLiving,
                ["deaths_door"] = info.DeathsDoor,
                ["corpse"] = info.Corpse,
                ["next_dot"] = info.NextDot,
                ["dies_to_dot"] = info.DiesToDot,
                ["stealth"] = info.Stealth,
                ["riposte"] = info.Riposte,
                ["dodge"] = info.Dodge,
                ["combo"] = info.Combo,
                ["stun"] = info.Stun,
                ["vulnerable"] = info.Vulnerable,
                ["weak"] = info.Weak,
                ["tangled"] = info.Tangled,
                ["tangled_lock"] = info.TangledLock,
                ["eyes_focus"] = info.EyesFocus,
                ["lung_inflate"] = info.LungInflate,
                ["tokens"] = tokens
            };
        }

        public static TargetInfo Describe(ActorInstance actor)
        {
            var info = new TargetInfo { Actor = actor };
            if (actor == null)
                return info;

            info.Hp = actor.HpRounded;
            info.HpPct = actor.CurrentHpPercent;
            try { info.Stress = actor.Stress; } catch { }
            try { info.DeathsDoor = actor.GetIsStatusActive(ActorStatusType.DEATHS_DOOR); } catch { }
            info.Corpse = IsCorpse(actor);
            info.NextDot = NextDotTick(actor);
            info.BleedDot = DotAmount(actor, "bleed");
            info.BlightDot = DotAmount(actor, "blight");
            info.BurnDot = DotAmount(actor, "burn");
            info.DiesToDot = !info.DeathsDoor && info.NextDot > 0f && info.Hp > 0f && info.NextDot + 0.05f >= info.Hp;
            info.Stealth = HasToken(actor, TokenType.STEALTH, "stealth");
            info.Riposte = HasToken(actor, TokenType.RIPOSTE, "riposte");
            info.Dodge = HasToken(actor, TokenType.EVADE, "dodge");
            info.Combo = CountToken(actor, "combo") > 0;
            info.Stun = CountToken(actor, "stun") > 0;
            info.Vulnerable = CountToken(actor, "vulnerable") > 0;
            info.Weak = CountToken(actor, "weak") > 0;
            info.Blind = CountToken(actor, "blind") > 0;
            info.StrengthCount = CountToken(actor, "strength");
            info.BlockCount = CountToken(actor, "block");
            info.DodgeCount = CountToken(actor, "dodge");
            info.TangledLock = CountToken(actor, "taproot_tangle_c") > 0;
            info.Tangled = info.TangledLock
                           || CountToken(actor, "taproot_tangle_b") > 0
                           || CountToken(actor, "taproot_tangle") > 0;
            info.EyesFocus = CountToken(actor, "eyes_focus");
            info.LungInflate = CountToken(actor, "lung_inflate") > 0;
            info.PositiveTokens = info.StrengthCount + info.BlockCount + info.DodgeCount
                                  + (info.Riposte ? 1 : 0) + CountToken(actor, "crit");
            return info;
        }

        public static bool HasToken(ActorInstance actor, TokenType type, string idContains)
        {
            if (actor == null || actor.TokenContainer == null)
                return false;
            try
            {
                if (actor.TokenContainer.GetHasTokenAsTarget(type))
                    return true;
            }
            catch { }
            try
            {
                var instances = actor.TokenContainer.GetInstances();
                if (instances == null || string.IsNullOrEmpty(idContains))
                    return false;
                for (var i = 0; i < instances.Count; i++)
                {
                    var def = instances[i] != null ? instances[i].Definition : null;
                    var id = def != null ? def.GetKey() : null;
                    if (!string.IsNullOrEmpty(id) && id.IndexOf(idContains, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch { }
            return false;
        }

        public static int CountToken(ActorInstance actor, string idContains)
        {
            if (actor == null || actor.TokenContainer == null || string.IsNullOrEmpty(idContains))
                return 0;
            try
            {
                var instances = actor.TokenContainer.GetInstances();
                if (instances == null)
                    return 0;
                var n = 0;
                for (var i = 0; i < instances.Count; i++)
                {
                    var def = instances[i] != null ? instances[i].Definition : null;
                    var id = def != null ? def.GetKey() : null;
                    if (!string.IsNullOrEmpty(id) && id.IndexOf(idContains, StringComparison.OrdinalIgnoreCase) >= 0)
                        n++;
                }
                return n;
            }
            catch
            {
                return 0;
            }
        }

        public static bool IsCorpse(ActorInstance actor)
        {
            if (actor == null)
                return false;
            try
            {
                if (actor.ContainsTag(CommonActorTags.TAG_CORPSE))
                    return true;
            }
            catch { }
            try
            {
                var id = actor.ActorDataClass != null ? actor.ActorDataClass.GetKey() : "";
                if (string.IsNullOrEmpty(id))
                    return false;
                return id.IndexOf("corpse", StringComparison.OrdinalIgnoreCase) >= 0
                       || id.IndexOf("pile", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        public static float NextDotTick(ActorInstance actor)
        {
            return DotAmount(actor, null);
        }

        public static float DotAmount(ActorInstance actor, string idContains)
        {
            if (actor == null || actor.DotContainer == null)
                return 0f;
            try
            {
                var instances = actor.DotContainer.GetInstances();
                if (instances == null)
                    return 0f;
                var total = 0f;
                for (var i = 0; i < instances.Count; i++)
                {
                    var dot = instances[i];
                    if (dot == null)
                        continue;
                    var def = dot.Definition;
                    if (def != null && def.IsHoT)
                        continue;
                    if (dot.GetDurationAmount() <= 0)
                        continue;
                    if (!string.IsNullOrEmpty(idContains))
                    {
                        var id = def != null ? def.GetKey() : null;
                        if (string.IsNullOrEmpty(id) || id.IndexOf(idContains, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                    }
                    total += Math.Abs(dot.m_EffectValueChange);
                }
                return total;
            }
            catch
            {
                return 0f;
            }
        }

        public static JArray Side(IEnumerable<ActorInstance> actors)
        {
            var arr = new JArray();
            if (actors == null)
                return arr;
            foreach (var actor in actors)
            {
                var obj = Actor(actor);
                if (obj != null)
                    arr.Add(obj);
            }
            return arr;
        }

        public static string OneLine(ActorInstance actor)
        {
            if (actor == null)
                return "?";
            var sb = new StringBuilder();
            sb.Append(string.IsNullOrEmpty(actor.ActorName) ? actor.ActorGuid.ToString() : actor.ActorName);
            sb.Append(" r").Append(actor.TeamPosition);
            sb.Append(" ").Append(actor.HpRounded.ToString("0")).Append("/").Append(actor.CurrentHpMax.ToString("0"));
            try
            {
                if (actor.GetIsStatusActive(ActorStatusType.DEATHS_DOOR))
                    sb.Append(" DD");
            }
            catch { }
            return sb.ToString();
        }

        public static IEnumerable<ActorInstance> TeamActors(BattleTeams teams, int teamIndex)
        {
            if (teams == null)
                yield break;
            Team team;
            try { team = teams.GetTeam(teamIndex); }
            catch { yield break; }
            if (team == null)
                yield break;

            IReadOnlyList<ActorInstance> actors = null;
            try { actors = team.Actors; } catch { }
            if (actors == null)
                yield break;
            for (var i = 0; i < actors.Count; i++)
            {
                if (actors[i] != null)
                    yield return actors[i];
            }
        }
    }
}
