using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dd2Autobattler.Replay
{
    /// <summary>
    /// A saved replay run: turn key -> replayed pick. Diff a later run against
    /// it to see exactly which decisions a code change altered.
    /// </summary>
    internal sealed class Baseline
    {
        public const int Version = 1;
        public string Created;
        public string Note;
        public Dictionary<string, BaselineEntry> Decisions = new Dictionary<string, BaselineEntry>();

        public static Baseline From(IEnumerable<ReplayTurn> turns, string note)
        {
            var b = new Baseline { Created = DateTime.UtcNow.ToString("o"), Note = note };
            foreach (var t in turns)
            {
                if (t == null || string.IsNullOrEmpty(t.Key))
                    continue;
                b.Decisions[t.Key] = new BaselineEntry
                {
                    Fight = t.Fight,
                    Hero = t.Hero,
                    Full = t.Full,
                    Pick = t.Replayed
                };
            }
            return b;
        }

        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var obj = new JObject
            {
                ["version"] = Version,
                ["created"] = Created,
                ["note"] = Note,
                ["decisions"] = JObject.FromObject(Decisions)
            };
            File.WriteAllText(path, obj.ToString(Formatting.Indented));
        }

        public static Baseline Load(string path)
        {
            var obj = JObject.Parse(File.ReadAllText(path));
            var b = new Baseline
            {
                Created = obj.Value<string>("created"),
                Note = obj.Value<string>("note")
            };
            var d = obj["decisions"] as JObject;
            if (d != null)
                b.Decisions = d.ToObject<Dictionary<string, BaselineEntry>>() ?? b.Decisions;
            return b;
        }

        public BaselineDiff Diff(IEnumerable<ReplayTurn> current)
        {
            var diff = new BaselineDiff();
            var seen = new HashSet<string>();
            foreach (var t in current)
            {
                if (t == null || string.IsNullOrEmpty(t.Key))
                    continue;
                seen.Add(t.Key);
                BaselineEntry before;
                if (!Decisions.TryGetValue(t.Key, out before))
                {
                    diff.Added.Add(t.Key);
                    continue;
                }
                diff.Compared++;
                var same = before.Pick == null
                    ? t.Replayed == null
                    : before.Pick.Same(t.Replayed);
                if (same)
                {
                    diff.Unchanged++;
                    continue;
                }
                diff.Changed.Add(new BaselineChange
                {
                    Key = t.Key,
                    Fight = t.Fight,
                    Hero = t.Hero,
                    Full = t.Full,
                    Before = before.Pick,
                    After = t.Replayed,
                    Logged = t.Logged
                });
            }
            foreach (var key in Decisions.Keys)
            {
                if (!seen.Contains(key))
                    diff.Missing.Add(key);
            }
            return diff;
        }
    }

    internal sealed class BaselineEntry
    {
        public string Fight;
        public string Hero;
        public bool Full;
        public ReplayPick Pick;
    }

    internal sealed class BaselineChange
    {
        public string Key;
        public string Fight;
        public string Hero;
        public bool Full;
        public ReplayPick Before;
        public ReplayPick After;
        public ReplayPick Logged;
    }

    internal sealed class BaselineDiff
    {
        public int Compared;
        public int Unchanged;
        public List<BaselineChange> Changed = new List<BaselineChange>();
        // In this run but not in the baseline / the other way round.
        public List<string> Added = new List<string>();
        public List<string> Missing = new List<string>();
    }
}
