using System;
using System.Collections.Generic;
using System.IO;
using Dd2Autobattler.Combat;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dd2Autobattler.Replay
{
    /// <summary>
    /// A pinned turn: a decider snapshot plus the pick it must make (expect)
    /// and/or picks it must not make (forbid). Checked in under
    /// src/Dd2Autobattler.Tests/Golden and run by dotnet test.
    /// Target 0 in a forbid entry means "this skill on any target".
    /// </summary>
    internal sealed class GoldenCase
    {
        public string Name;
        public string Note;
        public string Source;
        public GoldenPick Expect;
        // The pick with search On (Dd2Replay pin option with expect-search); null = Expect.
        public GoldenPick ExpectSearch;
        public List<GoldenPick> Forbid = new List<GoldenPick>();
        public TurnSnapshot Snap;

        public static GoldenCase Load(string path)
        {
            var obj = JObject.Parse(File.ReadAllText(path));
            var g = new GoldenCase
            {
                Name = obj.Value<string>("name") ?? Path.GetFileNameWithoutExtension(path),
                Note = obj.Value<string>("note"),
                Source = obj.Value<string>("source"),
                Expect = obj["expect"] is JObject e ? e.ToObject<GoldenPick>() : null,
                ExpectSearch = obj["expect_search"] is JObject es ? es.ToObject<GoldenPick>() : null,
                Snap = SnapshotJson.Read(obj["snap"])
            };
            if (obj["forbid"] is JArray f)
            {
                foreach (var t in f)
                {
                    if (t is JObject fo)
                        g.Forbid.Add(fo.ToObject<GoldenPick>());
                }
            }
            return g;
        }

        public void Save(string path)
        {
            var obj = new JObject
            {
                ["name"] = Name,
                ["note"] = Note,
                ["source"] = Source,
                ["expect"] = Expect != null ? JObject.FromObject(Expect) : null,
                ["expect_search"] = ExpectSearch != null ? JObject.FromObject(ExpectSearch) : null,
                ["forbid"] = JArray.FromObject(Forbid ?? new List<GoldenPick>()),
                ["snap"] = SnapshotJson.Write(Snap)
            };
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, obj.ToString(Formatting.Indented));
        }

        // Null when the case passes, otherwise why it failed.
        public string Check(out ChosenAction chosen)
        {
            return Check(out chosen, null, null);
        }

        // With search settings (Mode On): the pick after the round lookahead.
        public string Check(out ChosenAction chosen, Search.SearchSettings search, Search.GameData data)
        {
            chosen = null;
            if (Snap == null)
                return "no snapshot";
            var result = TurnDecider.DecideSnapshot(Snap);
            if (search != null)
                Search.SearchHook.Apply(Snap, result, search, data);
            chosen = result.Chosen;
            var got = chosen != null ? chosen.SkillId + "->" + chosen.TargetGuid + " (" + chosen.Reason + ", " + chosen.Score.ToString("0.0") + ")" : "(no action)";
            var expect = search != null && ExpectSearch != null ? ExpectSearch : Expect;
            if (expect != null && !expect.Matches(chosen))
                return "expected " + expect + (expect == ExpectSearch ? " (search on)" : "") + ", got " + got;
            if (Forbid != null)
            {
                foreach (var f in Forbid)
                {
                    if (f != null && f.Matches(chosen))
                        return "forbidden " + f + ", got " + got;
                }
            }
            return null;
        }
    }

    internal sealed class GoldenPick
    {
        public string Skill;
        public uint Target;

        public bool Matches(ChosenAction chosen)
        {
            if (chosen == null || string.IsNullOrEmpty(Skill))
                return false;
            if (!string.Equals(chosen.SkillId, Skill, StringComparison.OrdinalIgnoreCase))
                return false;
            return Target == 0 || chosen.TargetGuid == Target;
        }

        public override string ToString()
        {
            return Skill + (Target != 0 ? "->" + Target : "->any");
        }
    }
}
