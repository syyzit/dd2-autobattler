using System;
using System.Collections.Generic;
using System.IO;

namespace Dd2Autobattler.Replay
{
    /// <summary>
    /// Skill tags / conditions / effect ids from the game's Excel CSV exports
    /// (StreamingAssets\Excel). Used only to enrich schema-1 replays, whose
    /// logs never carried skill definitions. Read-only, no game process.
    /// </summary>
    internal sealed class CsvSkillData
    {
        internal sealed class Skill
        {
            public readonly List<string> Tags = new List<string>();
            public readonly List<string> Conditions = new List<string>();
            public readonly List<string> Effects = new List<string>();
        }

        private readonly Dictionary<string, Skill> _skills = new Dictionary<string, Skill>(StringComparer.Ordinal);

        public int Count
        {
            get { return _skills.Count; }
        }

        public Skill Find(string id)
        {
            Skill s;
            return !string.IsNullOrEmpty(id) && _skills.TryGetValue(id, out s) ? s : null;
        }

        public static CsvSkillData Load(string excelDir)
        {
            var data = new CsvSkillData();
            if (string.IsNullOrEmpty(excelDir) || !Directory.Exists(excelDir))
                return data;
            var files = new List<string>(Directory.GetFiles(excelDir, "*_data_export*.csv"));
            foreach (var dlc in Directory.GetDirectories(excelDir, "dlc_*"))
                files.AddRange(Directory.GetFiles(dlc, "*_data_export*.csv"));
            files.Sort(StringComparer.Ordinal);
            foreach (var f in files)
                data.Read(f);
            return data;
        }

        private void Read(string path)
        {
            Skill cur = null;
            var kind = "";
            foreach (var raw in File.ReadLines(path))
            {
                var parts = raw.Split(',');
                if (parts.Length == 0)
                    continue;
                var key = parts[0].Trim();
                if (key == "element_start" && parts.Length >= 3)
                {
                    var id = parts[1].Trim();
                    kind = parts[2].Trim();
                    cur = null;
                    if (kind == "ActorDataSkill" || kind == "ActorDataEffects")
                    {
                        if (!_skills.TryGetValue(id, out cur))
                        {
                            cur = new Skill();
                            _skills[id] = cur;
                        }
                    }
                    continue;
                }
                if (key == "element_end")
                {
                    cur = null;
                    continue;
                }
                if (cur == null)
                    continue;
                List<string> dest = null;
                if (kind == "ActorDataSkill")
                {
                    if (key == "m_Tags")
                        dest = cur.Tags;
                    else if (key == "m_AllConditionIds" || key == "m_AnyConditionIds")
                        dest = cur.Conditions;
                }
                else if (kind == "ActorDataEffects" && key.EndsWith("_effects", StringComparison.Ordinal))
                {
                    dest = cur.Effects;
                }
                if (dest == null)
                    continue;
                for (var i = 1; i < parts.Length; i++)
                {
                    var v = parts[i].Trim();
                    if (v.Length > 0 && !dest.Contains(v))
                        dest.Add(v);
                }
            }
        }
    }
}
