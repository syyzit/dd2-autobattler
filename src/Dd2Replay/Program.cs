using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Dd2Autobattler.Replay;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dd2Replay
{
    internal static class Program
    {
        private const string Usage = @"Dd2Replay - re-decide logged turns with the current decider.

  Dd2Replay [decisions.jsonl | session dir ...] [options]

Sources (default: the latest session under the logs root)
  --logs DIR           logs root (default <GameDir>\BepInEx\Dd2Autobattler\logs)
  --excel DIR          game CSV folder for schema-1 skill tags (default <GameDir>\...\StreamingAssets\Excel)
  --no-csv             do not read game CSVs
  --all                every session
  --since STAMP        sessions whose folder name >= STAMP (e.g. 20260825)
Filters
  --fight TEXT         fight id contains TEXT
  --hero TEXT          hero name or class contains TEXT
  --turn KEY           one turn, KEY = <session>#<fight no>/t<turn_index>
Output
  --json               JSON on stdout (summary, changes, diff)
  --changes N          list at most N changed decisions (default 40, 0 = all)
  --turns              with --json, include every replayed turn
  --rows               show rows whose score moved, with the fields that differ
Baseline / diff
  --save-baseline F    save this run's decisions to F
  --baseline F         compare this run's decisions with F
Forward model / search
  --search             greedy vs round lookahead (compare): disagreements with the search's
                       principal line and predicted outcome (text or --json; --changes N)
  --search-on          same, with On-mode override semantics in the summary
  --search-margin F / --search-gain F / --search-branch N / --search-budget N
  --search-cap MS / --search-weight F / --search-trust F   tuning overrides (see notes\search.md)
  --validate-model     score the search forward model against logged next boards
                       (needs action_result records; schema-2 snap adds turn order)
Golden cases
  --pin KEY            write a golden fixture for turn KEY
  --expect WHAT        replayed (default) | logged | SKILL[->TARGET]
  --expect-search WHAT with --search: the pick with search On (search | SKILL[->TARGET])
  --forbid WHAT        logged | SKILL[->TARGET] (repeatable; TARGET omitted = any)
  --name NAME          fixture name (default: from KEY)
  --note TEXT          why this turn is pinned
  --out FILE           fixture path (default src\Dd2Autobattler.Tests\Golden\<name>.json)";

        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 2;
            }
        }

        private sealed class Options
        {
            public readonly List<string> Paths = new List<string>();
            public string Logs;
            public string Excel;
            public bool NoCsv;
            public bool All;
            public string Since;
            public string Fight;
            public string Hero;
            public string Turn;
            public bool Json;
            public bool AllTurns;
            public bool Rows;
            public int Changes = 40;
            public string SaveBaseline;
            public string Baseline;
            public string Pin;
            public string Expect;
            public string ExpectSearch;
            public readonly List<string> Forbid = new List<string>();
            public string Name;
            public string Note;
            public string Out;
            public bool ValidateModel;
            public bool Verbose;
            public bool Search;
            public readonly Dd2Autobattler.Search.SearchSettings SearchSet = new Dd2Autobattler.Search.SearchSettings { Mode = Dd2Autobattler.Search.SearchMode.Compare, HardCapMs = 1000f };
        }

        private static int Run(string[] args)
        {
            var o = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                string Next()
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException(a + " needs a value");
                    return args[++i];
                }
                switch (a)
                {
                    case "-h":
                    case "--help":
                        Console.WriteLine(Usage);
                        return 0;
                    case "--logs": o.Logs = Next(); break;
                    case "--excel": o.Excel = Next(); break;
                    case "--no-csv": o.NoCsv = true; break;
                    case "--all": o.All = true; break;
                    case "--since": o.Since = Next(); break;
                    case "--fight": o.Fight = Next(); break;
                    case "--hero": o.Hero = Next(); break;
                    case "--turn": o.Turn = Next(); break;
                    case "--json": o.Json = true; break;
                    case "--turns": o.AllTurns = true; break;
                    case "--rows": o.Rows = true; break;
                    case "--changes": o.Changes = int.Parse(Next()); break;
                    case "--save-baseline": o.SaveBaseline = Next(); break;
                    case "--baseline": o.Baseline = Next(); break;
                    case "--pin": o.Pin = Next(); break;
                    case "--expect": o.Expect = Next(); break;
                    case "--expect-search": o.ExpectSearch = Next(); break;
                    case "--forbid": o.Forbid.Add(Next()); break;
                    case "--name": o.Name = Next(); break;
                    case "--note": o.Note = Next(); break;
                    case "--out": o.Out = Next(); break;
                    case "--validate-model": o.ValidateModel = true; break;
                    case "--verbose": o.Verbose = true; break;
                    case "--search": o.Search = true; break;
                    case "--search-on": o.Search = true; o.SearchSet.Mode = Dd2Autobattler.Search.SearchMode.On; break;
                    case "--search-margin": o.SearchSet.Margin = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--search-gain": o.SearchSet.MinGain = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--search-branch": o.SearchSet.AllyBranch = int.Parse(Next()); break;
                    case "--search-budget": o.SearchSet.NodeBudget = int.Parse(Next()); break;
                    case "--search-cap": o.SearchSet.HardCapMs = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--search-trust": o.SearchSet.AllyTrust = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--search-weight": o.SearchSet.FutureWeight = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                    default:
                        if (a.StartsWith("--", StringComparison.Ordinal))
                            throw new ArgumentException("unknown option " + a + " (see --help)");
                        o.Paths.Add(a);
                        break;
                }
            }

            if (!string.IsNullOrEmpty(o.Pin) && string.IsNullOrEmpty(o.Since) && !o.All && o.Paths.Count == 0)
            {
                // A pin key names its session; replay just that one.
                var session = o.Pin.Split('#')[0];
                o.Since = session;
                o.All = true;
            }

            var files = ResolveFiles(o);
            if (files.Count == 0)
                throw new InvalidOperationException("no decisions.jsonl found (use --logs or pass a path)");

            if (o.ValidateModel)
                return RunValidation(o, files);

            var engine = new ReplayEngine
            {
                Filter = new ReplayFilter { Fight = o.Fight, Hero = o.Hero, Key = o.Turn ?? o.Pin },
                KeepSnapshots = !string.IsNullOrEmpty(o.Pin),
                CollectRowDiffs = o.Rows,
                Csv = o.NoCsv ? null : CsvSkillData.Load(o.Excel ?? ExcelDir()),
                AllRowDiffs = o.Rows && !string.IsNullOrEmpty(o.Turn)
            };
            if (o.Search)
            {
                engine.SearchSettings = o.SearchSet;
                engine.SearchData = Dd2Autobattler.Search.GameData.Load(o.Excel ?? ExcelDir());
                if (!engine.SearchData.Loaded)
                    throw new InvalidOperationException("game CSVs not found (use --excel DIR)");
            }
            foreach (var f in files)
            {
                if (!string.IsNullOrEmpty(o.Pin) && ReplayEngine.SessionName(f) != o.Pin.Split('#')[0])
                    continue;
                engine.RunFile(f);
            }

            BaselineDiff diff = null;
            if (!string.IsNullOrEmpty(o.Baseline))
                diff = Dd2Autobattler.Replay.Baseline.Load(o.Baseline).Diff(engine.Turns);
            if (!string.IsNullOrEmpty(o.SaveBaseline))
                Dd2Autobattler.Replay.Baseline.From(engine.Turns, string.Join(" ", args)).Save(o.SaveBaseline);

            string pinned = null;
            if (!string.IsNullOrEmpty(o.Pin))
                pinned = WritePin(o, engine);

            if (o.Search)
                SearchReport.Print(engine, o.Json, o.Changes, o.Verbose);
            else if (o.Json)
                PrintJson(o, engine, diff, pinned);
            else
                PrintText(o, engine, diff, pinned, files.Count);
            return 0;
        }

        private static int RunValidation(Options o, List<string> files)
        {
            var data = Dd2Autobattler.Search.GameData.Load(o.Excel ?? ExcelDir());
            if (!data.Loaded)
                throw new InvalidOperationException("game CSVs not found (use --excel DIR)");
            var v = new ModelValidation { Data = data, Verbose = o.Verbose };
            foreach (var f in files)
                v.RunFile(f);
            if (o.Json)
                Console.WriteLine(v.ToJson().ToString(Formatting.Indented));
            else
                foreach (var line in v.TextLines())
                    Console.WriteLine(line);
            return 0;
        }

        private static string WritePin(Options o, ReplayEngine engine)
        {
            var turn = engine.Turns.FirstOrDefault(t => t.Key == o.Pin);
            if (turn == null || turn.Snapshot == null)
                throw new InvalidOperationException("turn " + o.Pin + " not found (keys look like yyyyMMdd-HHmmss#3/t12)");
            var name = o.Name ?? Regex.Replace(turn.Key, "[^A-Za-z0-9_-]+", "_") + "_" + (turn.Hero ?? "hero");
            var g = new GoldenCase
            {
                Name = name,
                Note = o.Note,
                Source = turn.Key + " " + turn.Fight + " " + turn.Hero + " (logged " + turn.Logged + ")",
                Snap = turn.Snapshot
            };
            var expect = o.Expect ?? (o.Forbid.Count == 0 ? "replayed" : null);
            if (expect != null)
                g.Expect = ParsePick(expect, turn);
            foreach (var f in o.Forbid)
                g.Forbid.Add(ParsePick(f, turn));
            if (o.ExpectSearch != null)
            {
                if (o.ExpectSearch == "search")
                {
                    var sp = turn.Search != null ? turn.Search.Pick : null;
                    if (sp == null)
                        throw new InvalidOperationException("turn has no search pick (add --search)");
                    g.ExpectSearch = new GoldenPick { Skill = sp.Action.SkillId, Target = sp.Action.TargetGuid };
                }
                else
                    g.ExpectSearch = ParsePick(o.ExpectSearch, turn);
            }
            var path = o.Out ?? Path.Combine(FindRepoRoot() ?? ".", "src", "Dd2Autobattler.Tests", "Golden", name + ".json");
            g.Save(path);
            string chosen;
            var fail = Check(g, out chosen);
            if (fail != null)
                Console.Error.WriteLine("warning: pinned case does not pass with the current decider: " + fail);
            return Path.GetFullPath(path);
        }

        private static string Check(GoldenCase g, out string chosen)
        {
            Dd2Autobattler.Combat.ChosenAction c;
            var fail = g.Check(out c);
            chosen = c != null ? c.SkillId + "->" + c.TargetGuid : null;
            return fail;
        }

        private static GoldenPick ParsePick(string what, ReplayTurn turn)
        {
            ReplayPick p = null;
            if (what == "replayed")
                p = turn.Replayed;
            else if (what == "logged")
                p = turn.Logged;
            if (what == "replayed" || what == "logged")
            {
                if (p == null || p.Skill == null)
                    throw new InvalidOperationException("turn has no " + what + " pick");
                return new GoldenPick { Skill = p.Skill, Target = p.Target };
            }
            var parts = what.Split(new[] { "->" }, StringSplitOptions.None);
            return new GoldenPick
            {
                Skill = parts[0],
                Target = parts.Length > 1 ? uint.Parse(parts[1]) : 0u
            };
        }

        private static List<string> DegradedReasons(ReplayEngine engine)
        {
            var counts = new Dictionary<string, int>();
            foreach (var t in engine.Turns)
            {
                foreach (var r in t.Degraded)
                    counts[r] = counts.TryGetValue(r, out var n) ? n + 1 : 1;
            }
            return counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + (kv.Value == engine.Turns.Count ? "" : " x" + kv.Value)).ToList();
        }

        private static List<ReplayTurn> Changed(ReplayEngine engine)
        {
            return engine.Turns.Where(t => !t.Agree).ToList();
        }

        private static void PrintText(Options o, ReplayEngine engine, BaselineDiff diff, string pinned, int fileCount)
        {
            var s = engine.Summary;
            Console.WriteLine("Replayed " + s.Turns + " turns from " + s.Files + " session(s), " + s.Fights + " fight(s)"
                              + (s.Skipped > 0 ? ", skipped " + s.Skipped + " (no legal rows)" : ""));
            Console.WriteLine("  fidelity: full " + s.Full + ", degraded " + s.Degraded
                              + (s.Degraded > 0 ? " (schema-1 logs: rebuilt from post-scoring rows)" : ""));
            if (s.Degraded > 0)
                Console.WriteLine("  degraded inputs: " + string.Join(", ", DegradedReasons(engine)));
            Console.WriteLine("  agreement with logged pick: " + s.Agree + "/" + s.Turns + " (" + ReplaySummary.Pct(s.Agree, s.Turns) + ")"
                              + "   full " + ReplaySummary.Pct(s.AgreeFull, s.Full)
                              + "   degraded " + ReplaySummary.Pct(s.AgreeDegraded, s.Degraded));
            Console.WriteLine("  candidate rows with the logged score (+-" + ReplayEngine.ScoreTolerance + "): "
                              + s.RowsScoreMatch + "/" + s.Rows + " (" + ReplaySummary.Pct(s.RowsScoreMatch, s.Rows) + ")");

            var changed = Changed(engine);
            if (changed.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Changed decisions (" + changed.Count + "), logged -> replayed:");
                var shown = o.Changes <= 0 ? changed : changed.Take(o.Changes).ToList();
                foreach (var t in shown)
                {
                    Console.WriteLine("  " + t.Key + " " + t.Fight + " r" + t.Round + " " + t.Hero + (t.Full ? "" : " [degraded]"));
                    Console.WriteLine("      logged   " + Pick(t.Logged) + Now(t.LoggedPickNow));
                    Console.WriteLine("      replayed " + Pick(t.Replayed) + Then(t.ReplayedPickThen));
                    PrintRowDiffs(t);
                }
                if (shown.Count < changed.Count)
                    Console.WriteLine("  ... " + (changed.Count - shown.Count) + " more (--changes 0 for all, --json for machine output)");
            }

            if (diff != null)
            {
                Console.WriteLine();
                Console.WriteLine("Baseline diff: " + diff.Changed.Count + " changed / " + diff.Compared + " compared"
                                  + (diff.Added.Count > 0 ? ", " + diff.Added.Count + " new turns" : "")
                                  + (diff.Missing.Count > 0 ? ", " + diff.Missing.Count + " missing" : ""));
                var shown = o.Changes <= 0 ? diff.Changed : diff.Changed.Take(o.Changes).ToList();
                foreach (var c in shown)
                {
                    Console.WriteLine("  " + c.Key + " " + c.Fight + " " + c.Hero + (c.Full ? "" : " [degraded]"));
                    Console.WriteLine("      before " + Pick(c.Before));
                    Console.WriteLine("      after  " + Pick(c.After) + "   (logged " + Pick(c.Logged) + ")");
                }
                if (shown.Count < diff.Changed.Count)
                    Console.WriteLine("  ... " + (diff.Changed.Count - shown.Count) + " more");
            }
            if (!string.IsNullOrEmpty(o.SaveBaseline))
                Console.WriteLine("\nSaved baseline: " + Path.GetFullPath(o.SaveBaseline));
            if (pinned != null)
                Console.WriteLine("\nPinned golden case: " + pinned);
            if (o.Rows && !string.IsNullOrEmpty(o.Turn))
            {
                foreach (var t in engine.Turns.Where(x => x.Agree))
                {
                    Console.WriteLine("\n" + t.Key + " (agrees) " + Pick(t.Replayed));
                    PrintRowDiffs(t);
                }
            }
        }

        private static void PrintRowDiffs(ReplayTurn t)
        {
            if (t.RowDiffs == null)
                return;
            foreach (var d in t.RowDiffs)
            {
                var fields = string.Join(", ", d.Fields.Select(kv =>
                    kv.Key + " " + Short(kv.Value[0]) + "->" + Short(kv.Value[1])));
                Console.WriteLine("        row " + d.Skill + "->" + d.Target + " " + d.LoggedScore.ToString("0.0")
                                  + "->" + d.ReplayedScore.ToString("0.0") + (fields.Length > 0 ? "  " + fields : ""));
            }
        }

        private static string Short(JToken t)
        {
            if (t == null)
                return "-";
            var s = t.ToString(Formatting.None);
            return s.Length > 40 ? s.Substring(0, 40) + ".." : s;
        }

        private static string Pick(ReplayPick p)
        {
            return p == null ? "(no action)" : p.ToString();
        }

        private static string Now(float? score)
        {
            return score.HasValue ? "   [scores " + score.Value.ToString("0.0") + " now]" : "   [not legal now]";
        }

        private static string Then(float? score)
        {
            return score.HasValue ? "   [scored " + score.Value.ToString("0.0") + " in the log]" : "";
        }

        private static void PrintJson(Options o, ReplayEngine engine, BaselineDiff diff, string pinned)
        {
            var s = engine.Summary;
            var changed = Changed(engine);
            var shown = o.Changes <= 0 ? changed : changed.Take(o.Changes).ToList();
            var obj = new JObject
            {
                ["summary"] = new JObject
                {
                    ["files"] = s.Files,
                    ["fights"] = s.Fights,
                    ["turns"] = s.Turns,
                    ["skipped"] = s.Skipped,
                    ["full_fidelity"] = s.Full,
                    ["degraded"] = s.Degraded,
                    ["agree"] = s.Agree,
                    ["agree_pct"] = s.Turns > 0 ? Math.Round(100.0 * s.Agree / s.Turns, 2) : (double?)null,
                    ["agree_full"] = s.AgreeFull,
                    ["agree_degraded"] = s.AgreeDegraded,
                    ["changed"] = changed.Count,
                    ["rows"] = s.Rows,
                    ["rows_score_match"] = s.RowsScoreMatch
                },
                ["changes"] = JArray.FromObject(shown, JsonSerializer.CreateDefault())
            };
            if (o.AllTurns)
                obj["turns"] = JArray.FromObject(engine.Turns);
            if (diff != null)
                obj["diff"] = JObject.FromObject(diff);
            if (pinned != null)
                obj["pinned"] = pinned;
            Console.WriteLine(obj.ToString(Formatting.Indented));
        }

        private static List<string> ResolveFiles(Options o)
        {
            var files = new List<string>();
            foreach (var p in o.Paths)
            {
                if (Directory.Exists(p))
                {
                    var hit = Path.Combine(p, "decisions.jsonl");
                    if (File.Exists(hit))
                        files.Add(hit);
                    else
                        files.AddRange(SessionFiles(p, o));
                }
                else if (File.Exists(p))
                    files.Add(p);
                else
                    throw new FileNotFoundException("not found: " + p);
            }
            if (o.Paths.Count > 0)
                return files;

            var root = o.Logs ?? LogsRoot();
            if (root == null || !Directory.Exists(root))
                throw new DirectoryNotFoundException("logs root not found; pass --logs DIR or set DD2_GAME_DIR");
            return SessionFiles(root, o);
        }

        private static List<string> SessionFiles(string root, Options o)
        {
            var dirs = Directory.GetDirectories(root)
                .Select(d => new DirectoryInfo(d))
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .ToList();
            if (!string.IsNullOrEmpty(o.Since))
                dirs = dirs.Where(d => string.CompareOrdinal(d.Name, o.Since) >= 0).ToList();
            var files = new List<string>();
            foreach (var d in dirs)
            {
                var f = Path.Combine(d.FullName, "decisions.jsonl");
                if (File.Exists(f) && new FileInfo(f).Length >= 200)
                    files.Add(f);
            }
            if (string.IsNullOrEmpty(o.Since) && !o.All)
            {
                // Latest session that has at least one turn record.
                for (var i = files.Count - 1; i >= 0; i--)
                {
                    if (HasTurn(files[i]))
                        return new List<string> { files[i] };
                }
                return new List<string>();
            }
            return files;
        }

        private static bool HasTurn(string path)
        {
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (line.IndexOf("\"type\":\"turn\"", StringComparison.Ordinal) >= 0
                        || line.IndexOf("\"type\": \"turn\"", StringComparison.Ordinal) >= 0)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static string ExcelDir()
        {
            var game = GameDir();
            return game != null ? Path.Combine(game, "Darkest Dungeon II_Data", "StreamingAssets", "Excel") : null;
        }

        private static string LogsRoot()
        {
            var game = GameDir();
            return game != null ? Path.Combine(game, "BepInEx", "Dd2Autobattler", "logs") : null;
        }

        private static string GameDir()
        {
            var env = Environment.GetEnvironmentVariable("DD2_GAME_DIR");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
                return env;
            var repo = FindRepoRoot();
            if (repo != null)
            {
                var props = Path.Combine(repo, "Directory.Build.props.user");
                if (File.Exists(props))
                {
                    var m = Regex.Match(File.ReadAllText(props), "<GameDir>([^<]+)</GameDir>");
                    if (m.Success && Directory.Exists(m.Groups[1].Value.Trim()))
                        return m.Groups[1].Value.Trim();
                }
            }
            foreach (var root in SteamLibraryCandidates())
            {
                if (!Directory.Exists(root))
                    continue;
                var hit = Directory.GetDirectories(root, "Darkest*")
                    .OrderBy(d => d, StringComparer.Ordinal)
                    .FirstOrDefault(d => Path.GetFileName(d).IndexOf("Mod", StringComparison.Ordinal) < 0);
                if (hit != null)
                    return hit;
            }
            return null;
        }

        // Default Steam install folders, then a "SteamLibrary" folder at the root of every
        // ready fixed drive (Steam's default name for an extra library). Anything else:
        // pass --logs DIR, set DD2_GAME_DIR or GameDir in Directory.Build.props.user.
        private static IEnumerable<string> SteamLibraryCandidates()
        {
            foreach (var pf in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            {
                string dir = null;
                try { dir = Environment.GetFolderPath(pf); } catch { }
                if (!string.IsNullOrEmpty(dir))
                    yield return Path.Combine(dir, "Steam", "steamapps", "common");
            }
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); } catch { drives = new DriveInfo[0]; }
            foreach (var d in drives)
            {
                bool ok;
                try { ok = d.DriveType == DriveType.Fixed && d.IsReady; } catch { ok = false; }
                if (ok)
                    yield return Path.Combine(d.RootDirectory.FullName, "SteamLibrary", "steamapps", "common");
            }
        }

        // Directory holding Directory.Build.props, from cwd or the exe upward.
        private static string FindRepoRoot()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppDomain.CurrentDomain.BaseDirectory })
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))
                        && Directory.Exists(Path.Combine(dir.FullName, "src")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
            return null;
        }
    }
}
