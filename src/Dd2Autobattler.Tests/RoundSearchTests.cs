using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dd2Autobattler.Combat;
using Dd2Autobattler.Replay;
using Dd2Autobattler.Search;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Dd2Autobattler.Tests
{
    public class RoundSearchTests
    {
        private static readonly GameData Data = ForwardModelTests.Fixture();

        // Performer H1 (slash) and a weak ally H2 (poke 2-3) who acts next,
        // then a Brute (smash 8-10) at 5 HP, then a full-HP Grub. Greedy
        // likes the bigger hit on the Grub; finishing range on the Brute lets
        // H2 kill it before it swings.
        private static TurnSnapshot KillBeforeItActs()
        {
            var s = new TurnSnapshot { Schema = 2, PerformerGuid = 1 };
            s.Heroes.Add(new TargetInfo { Guid = 1, Team = 0, Rank = 0, Hp = 30, HpMax = 30, HpPct = 1, Living = true, Size = 1, ClassId = "hero_a", Tokens = new List<string>(), SkillIds = new List<string> { "hero_slash" } });
            s.Heroes.Add(new TargetInfo { Guid = 2, Team = 0, Rank = 1, Hp = 30, HpMax = 30, HpPct = 1, Living = true, Size = 1, ClassId = "hero_a", Tokens = new List<string>(), SkillIds = new List<string> { "hero_poke" } });
            s.Enemies.Add(new TargetInfo { Guid = 10, Team = 1, Rank = 0, Hp = 5, HpMax = 20, HpPct = 0.25f, Living = true, Size = 1, ClassId = "brute", Tokens = new List<string>(), SkillIds = new List<string> { "brute_smash" } });
            s.Enemies.Add(new TargetInfo { Guid = 11, Team = 1, Rank = 1, Hp = 10, HpMax = 10, HpPct = 1, Living = true, Size = 1, ClassId = "grub", Tokens = new List<string>(), SkillIds = new List<string> { "grub_bite" } });
            s.Legal.Add(Legal("hero_slash", 10, 4f));
            s.Legal.Add(Legal("hero_slash", 11, 7f));
            s.RemainingTurns = new List<uint> { 1, 2, 10, 11 };
            s.Memory.NoteRound(1);
            s.Reindex();
            s.Performer = s.Find(1);
            return s;
        }

        private static LegalSnap Legal(string skill, uint target, float dmg)
        {
            var p = new PreviewScore { Ok = true, Damage = dmg, DamageLow = dmg, DamageHigh = dmg, HitChance = 1f, DamageValid = true };
            p.HitGuids.Add(target);
            return new LegalSnap { Skill = skill, Target = target, Preview = p };
        }

        private static SearchSettings On()
        {
            return new SearchSettings { Mode = SearchMode.On, HardCapMs = 10000f };
        }

        [Fact]
        public void GreedyPrefersTheBiggerHitHere()
        {
            var r = TurnDecider.DecideSnapshot(KillBeforeItActs());
            Assert.Equal(11u, r.Chosen.TargetGuid);
            Assert.Equal("attack", r.Trace.Gate);
        }

        [Fact]
        public void SearchSetsUpTheKillBeforeTheBruteActs()
        {
            var snap = KillBeforeItActs();
            var r = TurnDecider.DecideSnapshot(snap);
            var s = SearchHook.Apply(snap, r, On(), Data);
            Assert.Null(s.Abstain);
            Assert.False(s.Agrees);
            Assert.True(s.Overrode);
            Assert.Equal(10u, r.Chosen.TargetGuid);
            Assert.Equal(SearchHook.Reason, r.Chosen.Reason);
            Assert.Same(r.Picked, s.Pick.Action);
            // Ally kills land at AllyTrust (0.75).
            Assert.True(s.Pick.Outcome.EnemyKills > 0.7f);
            Assert.True(s.Pick.Outcome.HeroHpLost < s.Greedy.Outcome.HeroHpLost);
            Assert.Contains(s.Line, l => l.Contains("hero_poke"));
        }

        [Fact]
        public void CompareModeLogsButKeepsTheGreedyPick()
        {
            var snap = KillBeforeItActs();
            var r = TurnDecider.DecideSnapshot(snap);
            var settings = On();
            settings.Mode = SearchMode.Compare;
            var s = SearchHook.Apply(snap, r, settings, Data);
            Assert.False(s.Agrees);
            Assert.False(s.Overrode);
            Assert.Equal(11u, r.Chosen.TargetGuid);
            var json = s.ToJson();
            Assert.Equal("compare", (string)json["mode"]);
            Assert.Equal(10u, (uint)json["pick"]["target"]);
            Assert.NotNull(json["line"]);
        }

        [Fact]
        public void SearchNeverPicksAVetoedCandidate()
        {
            var snap = KillBeforeItActs();
            var r = TurnDecider.DecideSnapshot(snap);
            // Pretend a cited rule vetoed the Brute hit.
            r.Trace.Eligible.RemoveAll(c => c.TargetGuid == 10);
            var s = SearchHook.Apply(snap, r, On(), Data);
            Assert.Equal(11u, r.Chosen.TargetGuid);
            Assert.True(s.Abstain == "single_option" || s.Agrees);
        }

        [Theory]
        [InlineData("crisis_heal")]
        [InlineData("cultist_defense")]
        [InlineData("reposition")]
        [InlineData("setup")]
        [InlineData("free_item")]
        public void ForcedGatesAreNeverSearched(string gate)
        {
            var snap = KillBeforeItActs();
            var r = TurnDecider.DecideSnapshot(snap);
            r.Trace.Gate = gate;
            var s = SearchHook.Apply(snap, r, On(), Data);
            Assert.Equal("gate:" + gate, s.Abstain);
            Assert.Equal(11u, r.Chosen.TargetGuid);
        }

        [Fact]
        public void NoTurnOrderAbstains()
        {
            var snap = KillBeforeItActs();
            snap.RemainingTurns.Clear();
            var r = TurnDecider.DecideSnapshot(snap);
            var s = SearchHook.Apply(snap, r, On(), Data);
            Assert.Equal("no_turn_order", s.Abstain);
        }

        [Fact]
        public void OffModeDoesNothing()
        {
            var snap = KillBeforeItActs();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.Null(SearchHook.Apply(snap, r, new SearchSettings(), Data));
            Assert.Equal(11u, r.Chosen.TargetGuid);
        }

        [Fact]
        public void SearchIsDeterministicAndDoesNotMutateTheSnapshot()
        {
            var snap = KillBeforeItActs();
            var before = SnapshotJson.Write(snap);
            var a = RoundSearch.Run(snap, TurnDecider.DecideSnapshot(snap), On(), Data).ToJson();
            var b = RoundSearch.Run(snap, TurnDecider.DecideSnapshot(snap), On(), Data).ToJson();
            a.Remove("ms");
            b.Remove("ms");
            Assert.True(JToken.DeepEquals(a, b));
            Assert.True(JToken.DeepEquals(before, SnapshotJson.Write(snap)));
        }

        [Fact]
        public void BudgetStillEvaluatesTheGreedyPick()
        {
            var snap = KillBeforeItActs();
            var r = TurnDecider.DecideSnapshot(snap);
            var settings = On();
            settings.NodeBudget = 1;
            var s = RoundSearch.Run(snap, r, settings, Data);
            Assert.Null(s.Abstain);
            Assert.Equal(11u, s.Greedy.Action.TargetGuid);
            Assert.Single(s.Evaluated);
        }

        [Fact]
        public void HardCapAbandonsTheSearch()
        {
            var snap = KillBeforeItActs();
            var r = TurnDecider.DecideSnapshot(snap);
            var settings = On();
            settings.HardCapMs = -1f;
            var s = SearchHook.Apply(snap, r, settings, Data);
            Assert.Equal("timeout", s.Abstain);
            Assert.Equal(11u, r.Chosen.TargetGuid);
        }

        // ---- golden cases with search ON ----

        private static string GoldenDir
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Golden"); }
        }

        public static IEnumerable<object[]> GoldenFiles()
        {
            return SnapshotReplayTests.GoldenFiles();
        }

        private static GameData _gameData;

        // The real game CSVs (null when the game folder is not on this machine).
        internal static GameData GameCsv()
        {
            if (_gameData != null)
                return _gameData.Loaded ? _gameData : null;
            var dir = Environment.GetEnvironmentVariable("DD2_GAME_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (d != null && !File.Exists(Path.Combine(d.FullName, "Directory.Build.props")))
                    d = d.Parent;
                var props = d != null ? Path.Combine(d.FullName, "Directory.Build.props.user") : null;
                if (props != null && File.Exists(props))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(props), "<GameDir>([^<]+)</GameDir>");
                    if (m.Success)
                        dir = m.Groups[1].Value.Trim();
                }
            }
            var excel = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "Darkest Dungeon II_Data", "StreamingAssets", "Excel");
            _gameData = GameData.Load(excel);
            return _gameData.Loaded ? _gameData : null;
        }

        private readonly Xunit.Abstractions.ITestOutputHelper _out;

        public RoundSearchTests(Xunit.Abstractions.ITestOutputHelper output)
        {
            _out = output;
        }

        // What the search did on each golden turn with a synthetic order
        // (run with: dotnet test --logger "console;verbosity=detailed").
        [Fact]
        public void GoldenSearchReport()
        {
            var data = GameCsv();
            if (data == null)
                return;
            foreach (var f in GoldenFiles())
            {
                var g = GoldenCase.Load(Path.Combine(GoldenDir, (string)f[0]));
                if (g.Snap.RemainingTurns == null || g.Snap.RemainingTurns.Count == 0)
                    g.Snap.RemainingTurns = g.Snap.Enemies.Where(e => e.Living && !e.Corpse).Select(e => e.Guid)
                    .Concat(g.Snap.Heroes.Where(h => h.Living && h.Guid != g.Snap.PerformerGuid).Select(h => h.Guid)).ToList();
                var r = TurnDecider.DecideSnapshot(g.Snap);
                var s = RoundSearch.Run(g.Snap, r, On(), data);
                _out.WriteLine(g.Name + ": " + (s.Abstain ?? ("agree " + s.Agrees + " gain " + s.Gain.ToString("0.0") + " evaluated " + s.Evaluated.Count
                                                               + " pick " + s.Pick.Action.SkillId + "->" + s.Pick.Action.TargetGuid)));
            }
        }

        // Golden turns hold with search On. Schema-1 fixtures have no turn
        // order, so they are also checked with a synthetic order (every other
        // living actor, enemies first) to exercise the search itself.
        [Theory]
        [MemberData(nameof(GoldenFiles))]
        public void GoldenCaseHoldsWithSearchOn(string file)
        {
            var data = GameCsv();
            if (data == null)
                return;
            foreach (var synthetic in new[] { false, true })
            {
                var g = GoldenCase.Load(Path.Combine(GoldenDir, file));
                if (synthetic && (g.Snap.RemainingTurns == null || g.Snap.RemainingTurns.Count == 0))
                {
                    g.Snap.RemainingTurns = g.Snap.Enemies.Where(e => e.Living && !e.Corpse).Select(e => e.Guid)
                        .Concat(g.Snap.Heroes.Where(h => h.Living && h.Guid != g.Snap.PerformerGuid).Select(h => h.Guid)).ToList();
                }
                ChosenAction chosen;
                var fail = g.Check(out chosen, On(), data);
                Assert.True(fail == null, g.Name + (synthetic ? " (synthetic order)" : "") + ": " + fail);
            }
        }
    }
}
