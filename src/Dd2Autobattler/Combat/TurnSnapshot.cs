using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Dd2Autobattler.Combat
{
    /// <summary>
    /// Everything TurnDecider.DecideSnapshot reads, as plain data: no Unity or
    /// game types. LiveTurnReader fills it from the running game; the replay
    /// tool fills it from a logged turn. A search / lookahead layer can clone
    /// and edit it (move ranks, drop HP, advance memory) and re-decide.
    /// </summary>
    internal sealed class TurnSnapshot
    {
        // 2: first replayable schema (log field "snap").
        public const int CurrentSchema = 2;

        public int Schema;
        // Battle configuration id as logged in fight_start ("fight"), e.g.
        // cultist_mountain_1_5. Null when unknown: snapshots logged before this
        // field (the replay engine fills it from the surrounding fight_start),
        // or a fight that started before the plugin saw it. Rules gated on it
        // must treat null as "not that fight".
        public string FightId;
        public uint PerformerGuid;
        // Same values as the performer's entry in Heroes (kept separately in
        // case the performer is not on a team list).
        public TargetInfo Performer;
        // Every actor on each team in game order, corpses included.
        public List<TargetInfo> Heroes = new List<TargetInfo>();
        public List<TargetInfo> Enemies = new List<TargetInfo>();
        // Legal skill/target pairs in the order the game listed them.
        public List<LegalSnap> Legal = new List<LegalSnap>();
        // Skill definitions for every legal skill and every hero's equipped skill.
        public Dictionary<string, SkillSnap> Skills = new Dictionary<string, SkillSnap>();
        // Remaining turn order this round (actor guids, next first).
        public List<uint> RemainingTurns = new List<uint>();
        // Combat memory before this decision.
        public MemoryState Memory = new MemoryState();
        // Enemy threat facts read from class / skill data, before cited notes.
        public List<EnemyThreat> Threats = new List<EnemyThreat>();
        public List<string> SummonedClasses = new List<string>();
        // Performer-only skill facts.
        public Dictionary<string, int> ItemQty = new Dictionary<string, int>();
        public List<string> CursedSkills = new List<string>();
        // Equipped skills the performer is at the use limit for.
        public List<string> LimitedSkills = new List<string>();
        // Empty when built from the live game / a schema-2 log. Otherwise the
        // inputs a legacy log could not supply (see replay docs).
        public List<string> Degraded = new List<string>();

        [JsonIgnore]
        private Dictionary<uint, TargetInfo> _byGuid;
        [JsonIgnore]
        private HashSet<uint> _enemyGuids;

        [JsonIgnore]
        public int Round
        {
            get { return Memory != null ? Memory.Round : 0; }
        }

        [JsonIgnore]
        public bool FullFidelity
        {
            get { return Degraded == null || Degraded.Count == 0; }
        }

        // DecisionLog placeholders ("none" before any fight_start, "fight" for
        // a fight_start without an id) are not fight ids.
        public static string NormalizeFightId(string fight)
        {
            if (string.IsNullOrEmpty(fight) || fight == "none" || fight == "fight")
                return null;
            return fight;
        }

        // Call after editing Heroes / Enemies so lookups see the change.
        public void Reindex()
        {
            _byGuid = null;
            _enemyGuids = null;
        }

        public TargetInfo Find(uint guid)
        {
            if (guid == 0)
                return null;
            EnsureIndex();
            TargetInfo info;
            return _byGuid.TryGetValue(guid, out info) ? info : null;
        }

        public bool IsEnemy(uint guid)
        {
            EnsureIndex();
            return _enemyGuids.Contains(guid);
        }

        public SkillSnap Skill(string id)
        {
            if (string.IsNullOrEmpty(id) || Skills == null)
                return null;
            SkillSnap def;
            return Skills.TryGetValue(id, out def) ? def : null;
        }

        public bool IsCursed(string skillId)
        {
            return CursedSkills != null && CursedSkills.Contains(skillId);
        }

        public bool UnderSkillLimit(string skillId)
        {
            return LimitedSkills == null || !LimitedSkills.Contains(skillId);
        }

        public int RemainingQty(string skillId)
        {
            int n;
            return ItemQty != null && !string.IsNullOrEmpty(skillId) && ItemQty.TryGetValue(skillId, out n) ? n : 0;
        }

        private void EnsureIndex()
        {
            if (_byGuid != null)
                return;
            var by = new Dictionary<uint, TargetInfo>();
            var enemies = new HashSet<uint>();
            // Team lookup order: heroes then enemies (BattleTeams resolves a
            // guid to exactly one team).
            if (Heroes != null)
            {
                foreach (var a in Heroes)
                {
                    if (a != null && a.Guid != 0 && !by.ContainsKey(a.Guid))
                        by[a.Guid] = a;
                }
            }
            if (Enemies != null)
            {
                foreach (var a in Enemies)
                {
                    if (a == null || a.Guid == 0)
                        continue;
                    enemies.Add(a.Guid);
                    if (!by.ContainsKey(a.Guid))
                        by[a.Guid] = a;
                }
            }
            _byGuid = by;
            _enemyGuids = enemies;
        }
    }

    internal sealed class LegalSnap
    {
        public string Skill;
        public uint Target;
        public bool Stealthed;
        // Preview as the game reported it, plus CSV token adds and
        // performer-buff DoT (data extraction). Living-hit clamp, Combo bleed
        // double and kill-from-HP run in the decider on a copy.
        public PreviewScore Preview;
    }

    /// <summary>Static skill definition facts (CSV / library), owner-size masks.</summary>
    internal sealed class SkillSnap
    {
        public const int MaxEnemySize = 4;

        public string Id;
        public bool IsMove;
        public bool IsFriendly;
        public bool IsItemSkill;
        public bool IsFree;
        public bool ClearCorpseEffect;
        public List<string> Tags = new List<string>();
        // m_AllConditionIds + m_AnyConditionIds.
        public List<string> Conditions = new List<string>();
        public SkillRole Role = new SkillRole();
        // Size of the actor the masks were computed for.
        public int OwnerSize;
        // Bit r: GetHasLaunchRank(r, OwnerSize).
        public int LaunchMask;
        // [launch * MaxEnemySize + (enemySize - 1)] bit e: GetHasTargetRank(launch, OwnerSize, e, enemySize).
        public int[] TargetMasks;
        // Same layout as TargetMasks: bit e set when GetHasTargetRank threw
        // (a throw reads as "no"). Null with MasksUnknown = never read.
        public int[] TargetThrew;
        // True when the game threw while reading any mask, or masks were
        // never read (legacy replay).
        public bool MasksUnknown;

        public bool TargetThrows(int launchRank, int enemyRank, int enemySize)
        {
            if (TargetThrew == null || launchRank < 0 || launchRank >= 4 || enemyRank < 0 || enemyRank >= 4)
                return false;
            var es = enemySize < 1 ? 1 : enemySize > MaxEnemySize ? MaxEnemySize : enemySize;
            var idx = launchRank * MaxEnemySize + (es - 1);
            return idx < TargetThrew.Length && (TargetThrew[idx] & (1 << enemyRank)) != 0;
        }

        public bool Launches(int rank)
        {
            return rank >= 0 && rank < 4 && (LaunchMask & (1 << rank)) != 0;
        }

        public bool Targets(int launchRank, int enemyRank, int enemySize)
        {
            if (TargetMasks == null || launchRank < 0 || launchRank >= 4 || enemyRank < 0 || enemyRank >= 4)
                return false;
            var es = enemySize < 1 ? 1 : enemySize > MaxEnemySize ? MaxEnemySize : enemySize;
            var idx = launchRank * MaxEnemySize + (es - 1);
            return idx < TargetMasks.Length && (TargetMasks[idx] & (1 << enemyRank)) != 0;
        }

        // Substring match, like the old m_Tags walk.
        public bool HasTag(string key)
        {
            return ListHas(Tags, key);
        }

        public bool HasCondition(string key)
        {
            return ListHas(Conditions, key);
        }

        // def.IsItemSkill || tag combat_item (ItemPolicy.IsCombatItem def half).
        [JsonIgnore]
        public bool IsItemDef
        {
            get { return IsItemSkill || HasTag("combat_item"); }
        }

        private static bool ListHas(List<string> list, string key)
        {
            if (list == null || string.IsNullOrEmpty(key))
                return false;
            for (var i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (!string.IsNullOrEmpty(s) && s.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }
    }
}
