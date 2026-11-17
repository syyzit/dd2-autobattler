using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Dd2Autobattler.Search
{
    /// <summary>
    /// Static game data the forward model needs for actors other than the
    /// current performer (who has exact game previews): skill damage ranges,
    /// launch / target ranks, heal and DoT effects, class HP / speed / size /
    /// corpse. Read once from the game's own Excel CSV exports
    /// (StreamingAssets\Excel), which is the data the game loads. Pure data,
    /// no game types: the replay tool and the tests read the same files.
    /// </summary>
    internal sealed class GameData
    {
        internal sealed class SkillData
        {
            public string Id;
            public string File;
            public bool Friendly;
            public bool MultiHit;
            public bool SelfTargetValid;
            // Bit r = 0-based rank r (CSV ranks are 1-based).
            public int LaunchMask;
            public int TargetMask;
            public bool HasRanks;
            public float DmgMin;
            public float DmgMax;
            public float Crit;
            public int Limit;
            public readonly List<string> Tags = new List<string>();
            public readonly List<string> Conditions = new List<string>();
            public readonly List<string> TargetEffects = new List<string>();
            public readonly List<string> PerformerEffects = new List<string>();

            public bool Damages
            {
                get { return DmgMax > 0f; }
            }

            public float DmgMean
            {
                get { return (DmgMin + DmgMax) * 0.5f; }
            }

            public bool HasTag(string key)
            {
                for (var i = 0; i < Tags.Count; i++)
                {
                    if (string.Equals(Tags[i], key, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
        }

        internal sealed class EffectData
        {
            public string Id;
            public float Chance = 1f;
            public string TokenAdd;
            public int TokenAmount;
            public string DotAdd;
            public int DotAmount;
            public float HealPct;
            public float HealAmount;
            public float DamageAmount;
            public int Move;
            public string Summon;
            public bool AddTurn;
            public string ChangeClass;
            public bool IgnoreResist;
        }

        internal sealed class DotData
        {
            public string Id;
            public string Type;
            public float PerTick;
            public int Duration;
        }

        internal sealed class ClassData
        {
            public string Id;
            public string File;
            public int Size = 1;
            public float HpMax;
            public float Speed;
            public int Turns = 1;
            public bool Healthless;
            public float DeathResist;
            public float ResistStun;
            public float ResistMove;
            public bool HasCorpse;
            public readonly List<string> Tags = new List<string>();
        }

        private readonly Dictionary<string, SkillData> _skills = new Dictionary<string, SkillData>(StringComparer.Ordinal);
        private readonly Dictionary<string, EffectData> _effects = new Dictionary<string, EffectData>(StringComparer.Ordinal);
        private readonly Dictionary<string, DotData> _dots = new Dictionary<string, DotData>(StringComparer.Ordinal);
        private readonly Dictionary<string, ClassData> _classes = new Dictionary<string, ClassData>(StringComparer.Ordinal);
        // Skills defined in the same CSV file as a class (enemy kit fallback
        // when the snapshot has no equipped skill ids).
        private readonly Dictionary<string, List<string>> _fileSkills = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _kitCache = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        public string Source;

        public int SkillCount
        {
            get { return _skills.Count; }
        }

        public bool Loaded
        {
            get { return _skills.Count > 0; }
        }

        public SkillData Skill(string id)
        {
            SkillData s;
            return !string.IsNullOrEmpty(id) && _skills.TryGetValue(id, out s) ? s : null;
        }

        public EffectData Effect(string id)
        {
            EffectData e;
            return !string.IsNullOrEmpty(id) && _effects.TryGetValue(id, out e) ? e : null;
        }

        public DotData Dot(string id)
        {
            DotData d;
            return !string.IsNullOrEmpty(id) && _dots.TryGetValue(id, out d) ? d : null;
        }

        public ClassData Class(string id)
        {
            ClassData c;
            return !string.IsNullOrEmpty(id) && _classes.TryGetValue(id, out c) ? c : null;
        }

        // Every skill in the class's own CSV file that has rank data (the
        // enemy's kit when the snapshot does not list equipped skills).
        public List<string> FileKit(string classId)
        {
            var cls = Class(classId);
            if (cls == null || string.IsNullOrEmpty(cls.File))
                return null;
            List<string> kit;
            if (_kitCache.TryGetValue(classId, out kit))
                return kit;
            kit = new List<string>();
            List<string> all;
            if (_fileSkills.TryGetValue(cls.File, out all))
            {
                foreach (var id in all)
                {
                    var s = Skill(id);
                    if (s == null || !s.HasRanks)
                        continue;
                    // Path / upgrade variants share the file; the base id is the kit.
                    if (id.EndsWith("_u", StringComparison.Ordinal) || id.Contains("_p1") || id.Contains("_p2") || id.Contains("_p3"))
                        continue;
                    kit.Add(id);
                }
            }
            _kitCache[classId] = kit;
            return kit;
        }

        private static GameData _shared;
        private static string _sharedDir;

        // Lazy shared instance for the live game / replay (first caller wins).
        public static GameData Shared(string excelDir)
        {
            if (_shared != null && (excelDir == null || string.Equals(excelDir, _sharedDir, StringComparison.OrdinalIgnoreCase)))
                return _shared;
            _shared = Load(excelDir);
            _sharedDir = excelDir;
            return _shared;
        }

        public static GameData Load(string excelDir)
        {
            var data = new GameData { Source = excelDir };
            if (string.IsNullOrEmpty(excelDir) || !Directory.Exists(excelDir))
                return data;
            var files = new List<string>(Directory.GetFiles(excelDir, "*.csv"));
            foreach (var sub in Directory.GetDirectories(excelDir))
                files.AddRange(Directory.GetFiles(sub, "*.csv"));
            files.Sort(StringComparer.Ordinal);
            foreach (var f in files)
            {
                try { data.Read(f); }
                catch (IOException) { }
            }
            foreach (var kv in data._classes)
            {
                if (data._classes.ContainsKey(kv.Key + "_corpse"))
                    kv.Value.HasCorpse = true;
            }
            return data;
        }

        private void Read(string path)
        {
            var file = Path.GetFileName(path);
            string id = null;
            string kind = null;
            string[] keyMap = null;
            foreach (var raw in File.ReadLines(path))
            {
                if (raw.Length == 0)
                    continue;
                var parts = raw.Split(',');
                var key = parts[0].Trim();
                if (key == "element_start" && parts.Length >= 3)
                {
                    id = parts[1].Trim();
                    kind = parts[2].Trim();
                    keyMap = null;
                    if (kind == "ActorDataSkill")
                    {
                        var s = SkillFor(id);
                        s.File = file;
                        List<string> list;
                        if (!_fileSkills.TryGetValue(file, out list))
                            _fileSkills[file] = list = new List<string>();
                        if (!list.Contains(id))
                            list.Add(id);
                    }
                    else if (kind == "ActorDataClass")
                    {
                        ClassFor(id).File = file;
                    }
                    else if (kind == "Effect")
                    {
                        _effects[id] = new EffectData { Id = id };
                    }
                    else if (kind == "Dot")
                    {
                        _dots[id] = new DotData { Id = id };
                    }
                    continue;
                }
                if (key == "element_end")
                {
                    id = null;
                    kind = null;
                    continue;
                }
                if (id == null)
                    continue;
                switch (kind)
                {
                    case "ActorDataSkill":
                        ReadSkill(SkillFor(id), key, parts);
                        break;
                    case "ActorDataStats":
                        if (key == "key_map")
                            keyMap = parts;
                        else if (key == "add_stats" && keyMap != null)
                            ReadStats(id, keyMap, parts);
                        else if (key == "sub_stat" && parts.Length >= 4)
                            ReadSubStat(id, parts);
                        break;
                    case "ActorDataEffects":
                        if (key == "target_effects")
                            AddAll(SkillFor(id).TargetEffects, parts);
                        else if (key == "performer_effects")
                            AddAll(SkillFor(id).PerformerEffects, parts);
                        break;
                    case "ActorDataClass":
                        ReadClass(ClassFor(id), key, parts);
                        break;
                    case "Effect":
                        ReadEffect(_effects[id], key, parts);
                        break;
                    case "Dot":
                        ReadDot(_dots[id], key, parts);
                        break;
                }
            }
        }

        private SkillData SkillFor(string id)
        {
            SkillData s;
            if (!_skills.TryGetValue(id, out s))
                _skills[id] = s = new SkillData { Id = id };
            return s;
        }

        private ClassData ClassFor(string id)
        {
            ClassData c;
            if (!_classes.TryGetValue(id, out c))
                _classes[id] = c = new ClassData { Id = id };
            return c;
        }

        private static void ReadSkill(SkillData s, string key, string[] parts)
        {
            switch (key)
            {
                case "m_IsFriendly":
                    s.Friendly = Bool(parts);
                    break;
                case "m_IsMultiHit":
                    s.MultiHit = Bool(parts);
                    break;
                case "m_IsFriendlySelfTargetValid":
                    s.SelfTargetValid = Bool(parts);
                    break;
                case "launch_ranks":
                    s.LaunchMask = Ranks(parts);
                    s.HasRanks = true;
                    break;
                case "target_ranks":
                    s.TargetMask = Ranks(parts);
                    break;
                case "m_Limit":
                    s.Limit = (int)Num(parts, 1);
                    break;
                case "m_Tags":
                    AddAll(s.Tags, parts);
                    break;
                case "m_AllConditionIds":
                case "m_AnyConditionIds":
                    AddAll(s.Conditions, parts);
                    break;
            }
        }

        private void ReadStats(string id, string[] keyMap, string[] values)
        {
            float? dmg = null;
            var range = 0f;
            for (var i = 1; i < keyMap.Length && i < values.Length; i++)
            {
                var k = keyMap[i].Trim();
                if (k.Length == 0)
                    continue;
                var v = Num(values, i);
                switch (k)
                {
                    case "health_damage":
                        dmg = v;
                        break;
                    case "health_damage_range":
                        // Game range: base .. base + range.
                        range = v;
                        break;
                    case "crit_chance":
                        SkillFor(id).Crit = v;
                        break;
                    case "health_max":
                        ClassFor(id).HpMax = v;
                        break;
                    case "speed":
                        ClassFor(id).Speed = v;
                        break;
                    case "speed_number_of_turns":
                        ClassFor(id).Turns = Math.Max(1, (int)v);
                        break;
                }
            }
            if (dmg.HasValue)
            {
                var s = SkillFor(id);
                s.DmgMin = dmg.Value;
                s.DmgMax = dmg.Value + range;
            }
        }

        private void ReadSubStat(string id, string[] parts)
        {
            if (parts[1].Trim() != "resistance")
                return;
            var what = parts[2].Trim();
            var v = Num(parts, 3);
            var c = ClassFor(id);
            if (what == "death")
                c.DeathResist = v;
            else if (what == "stun")
                c.ResistStun = v;
            else if (what == "move")
                c.ResistMove = v;
        }

        private static void ReadClass(ClassData c, string key, string[] parts)
        {
            switch (key)
            {
                case "m_Size":
                    c.Size = Math.Max(1, (int)Num(parts, 1));
                    break;
                case "m_IsHealthless":
                    c.Healthless = Bool(parts);
                    break;
                case "m_Tags":
                    AddAll(c.Tags, parts);
                    break;
            }
        }

        private static void ReadEffect(EffectData e, string key, string[] parts)
        {
            switch (key)
            {
                case "m_Chance": e.Chance = Num(parts, 1); break;
                case "m_TokenAddId": e.TokenAdd = Str(parts); break;
                case "m_TokenAddAmount": e.TokenAmount = (int)Num(parts, 1); break;
                case "m_DotAddId": e.DotAdd = Str(parts); break;
                case "m_DotAddAmount": e.DotAmount = (int)Num(parts, 1); break;
                case "m_HealthHealPercent": e.HealPct = Num(parts, 1); break;
                case "m_HealthHealAmount": e.HealAmount = Num(parts, 1); break;
                case "m_HealthDamageAmount": e.DamageAmount = Num(parts, 1); break;
                case "m_Move": e.Move = (int)Num(parts, 1); break;
                case "m_SummonClassActorId": e.Summon = Str(parts); break;
                case "m_AddTurn": e.AddTurn = Bool(parts); break;
                case "m_ChangeClassActorId": e.ChangeClass = Str(parts); break;
                case "m_IgnoreResist": e.IgnoreResist = Bool(parts); break;
            }
        }

        private static void ReadDot(DotData d, string key, string[] parts)
        {
            switch (key)
            {
                case "m_Type":
                    d.Type = Str(parts);
                    break;
                case "m_DurationAmount":
                    d.Duration = (int)Num(parts, 1);
                    break;
                case "effects":
                    // health_damage_N per tick.
                    for (var i = 1; i < parts.Length; i++)
                    {
                        var p = parts[i].Trim();
                        if (!p.StartsWith("health_damage_", StringComparison.Ordinal))
                            continue;
                        float n;
                        if (float.TryParse(p.Substring("health_damage_".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out n))
                            d.PerTick += n;
                    }
                    break;
            }
        }

        // DoT per tick an effect id adds (effect m_DotAddId x amount).
        public float DotPerTick(EffectData e)
        {
            if (e == null || string.IsNullOrEmpty(e.DotAdd))
                return 0f;
            var dot = Dot(e.DotAdd);
            if (dot == null)
                return 0f;
            return dot.PerTick * Math.Max(1, e.DotAmount);
        }

        private static int Ranks(string[] parts)
        {
            var mask = 0;
            for (var i = 1; i < parts.Length; i++)
            {
                var p = parts[i].Trim().TrimStart('@');
                int r;
                if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) && r >= 1 && r <= 4)
                    mask |= 1 << (r - 1);
            }
            return mask;
        }

        private static bool Bool(string[] parts)
        {
            return parts.Length > 1 && string.Equals(parts[1].Trim(), "True", StringComparison.OrdinalIgnoreCase);
        }

        private static string Str(string[] parts)
        {
            return parts.Length > 1 ? parts[1].Trim() : null;
        }

        private static float Num(string[] parts, int i)
        {
            if (i >= parts.Length)
                return 0f;
            float v;
            return float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0f;
        }

        private static void AddAll(List<string> dest, string[] parts)
        {
            for (var i = 1; i < parts.Length; i++)
            {
                var v = parts[i].Trim();
                if (v.Length > 0 && !dest.Contains(v))
                    dest.Add(v);
            }
        }
    }
}
