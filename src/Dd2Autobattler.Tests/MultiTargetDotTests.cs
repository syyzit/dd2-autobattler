using System;
using System.Collections.Generic;
using System.IO;
using Dd2Autobattler.Combat;
using Xunit;

namespace Dd2Autobattler.Tests
{
    // Multi-target DoT valuation (per hit, per resist), DoT ticks on large-HP
    // targets, and the Shackles of Denial Magnesium Rain click.
    public class MultiTargetDotTests
    {
        private const uint Melee = 51, Ranged = 52, Health = 50, Stress = 53;
        private const uint Pd = 3, Maa = 2;

        private static void Near(double expected, double actual)
        {
            Assert.Equal(expected, actual, 2);
        }

        // ---------------- preview: per target, never a sum ----------------

        [Fact]
        public void TopLevelDotIsTheClickedTargetsNotTheSum()
        {
            // IL: one SkillPreview per actor hit. Two targets each listing a
            // blight 4 must read as 4 on the click (it used to be able to add up).
            var p = new PreviewScore { Ok = true, PerformerGuid = Pd };
            p.Hits.Add(new PreviewHit { Guid = 10, Blight = 4f, ResistOk = true, ResistBlight = 0.2f });
            p.Hits.Add(new PreviewHit { Guid = 11, Blight = 4f, ResistOk = true, ResistBlight = 0.6f });
            SkillPreviewReader.FinishPerTarget(p, 11);
            Assert.True(p.DotPerHit);
            Assert.Equal(4f, p.ApplyBlight);
            // Top-level resist is the clicked target's, not the last item's.
            Near(0.6f, p.ResistBlight);
            Near(0.8f, p.LandOn(10, "blight"));
            Near(0.4f, p.LandOn(11, "blight"));
            Near(3.2f, p.DotLandedOn(10));
            Near(1.6f, p.DotLandedOn(11));
        }

        [Fact]
        public void ClickWithoutItsOwnDotTakesTheLargestHit()
        {
            // Guard redirect: the click target has no item; the guardian does.
            var p = new PreviewScore { Ok = true, PerformerGuid = Pd };
            p.Hits.Add(new PreviewHit { Guid = Pd, Bleed = 9f });
            p.Hits.Add(new PreviewHit { Guid = 12, Burn = 3f });
            SkillPreviewReader.FinishPerTarget(p, 99);
            Assert.Equal(3f, p.ApplyBurn);
            Assert.Equal(0f, p.ApplyBleed);
        }

        [Fact]
        public void OldLogsWithoutPerHitDotsReadTheTopLevelForEveryHit()
        {
            var p = new PreviewScore { Ok = true, ApplyBurn = 3f, ResistOk = true, ResistBurn = 0.5f };
            p.Hits.Add(new PreviewHit { Guid = 10 });
            p.Hits.Add(new PreviewHit { Guid = 11 });
            Assert.False(p.DotPerHit);
            Assert.Equal(3f, p.DotOn(10, "burn"));
            Assert.Equal(3f, p.DotOn(11, "burn"));
            Near(0.5f, p.LandOn(11, "burn"));
            Assert.Equal(3f, p.Turns());
        }

        [Fact]
        public void PerHitFieldsSurviveTheSnapshotRoundTrip()
        {
            var snap = Board(Pd, PdHero());
            MagnesiumRain(snap, Ranged);
            snap.Reindex();
            var again = SnapshotJson.Read(SnapshotJson.Write(snap));
            var p = again.Legal[0].Preview;
            Assert.True(p.DotPerHit);
            Assert.Equal(3f, p.DotTurns);
            Assert.Equal(4, p.Hits.Count);
            Near(0.5f, p.HitFor(Health).ResistBurn);
            Assert.Equal(3f, p.HitFor(Stress).Burn);
            var c = p.Clone();
            Near(0.5f, c.HitFor(Health).ResistBurn);
            Assert.NotSame(p.HitFor(Health), c.HitFor(Health));
        }

        // ---------------- general scorer ----------------

        private static TargetInfo Enemy(uint g, float hp, float max)
        {
            return new TargetInfo { Guid = g, Team = 1, Living = true, Hp = hp, HpMax = max, HpPct = hp / max, ClassId = "enemy" };
        }

        private static TurnSnapshot TwoEnemies(TargetInfo a, TargetInfo b)
        {
            var snap = new TurnSnapshot { PerformerGuid = Pd };
            snap.Heroes.Add(new TargetInfo { Guid = Pd, Team = 0, Living = true, Hp = 20, HpMax = 30, HpPct = 0.66f });
            snap.Enemies.Add(a);
            snap.Enemies.Add(b);
            snap.Reindex();
            return snap;
        }

        [Fact]
        public void GrenadePaysEachTargetItsOwnBlightAndResist()
        {
            var a = Enemy(10, 20f, 20f);
            var b = Enemy(11, 20f, 20f);
            var snap = TwoEnemies(a, b);
            var p = new PreviewScore { Ok = true, DotPerHit = true, ApplyBlight = 4f };
            p.Hits.Add(new PreviewHit { Guid = 10, Damage = 2.5f, Blight = 4f, ResistOk = true, ResistBlight = 0.2f });
            p.Hits.Add(new PreviewHit { Guid = 11, Damage = 2.5f, Blight = 4f, ResistOk = true, ResistBlight = 0.6f });
            // Small targets: 1.5 ticks each, each with its own land.
            var both = KitSafety.DotHostDelta("pd_plague_grenade_u", a, true, false, 2, p, snap);
            Near(4f * 0.8f * 1.5f + 4f * 0.4f * 1.5f, both);
            // Without the board (older callers) only the click is paid.
            Near(4f * 0.8f * 1.5f, KitSafety.DotHostDelta("pd_plague_grenade_u", a, true, false, 2, p));
        }

        [Fact]
        public void AnImmuneSecondTargetNeverVetoesTheClick()
        {
            var a = Enemy(10, 20f, 20f);
            var b = Enemy(11, 20f, 20f);
            var snap = TwoEnemies(a, b);
            var p = new PreviewScore { Ok = true, DotPerHit = true, ApplyBlight = 4f };
            p.Hits.Add(new PreviewHit { Guid = 10, Blight = 4f, ResistOk = true, ResistBlight = 0f });
            p.Hits.Add(new PreviewHit { Guid = 11, Blight = 4f, ResistOk = true, ResistBlight = 2f });
            Near(6f, KitSafety.DotHostDelta("pd_plague_grenade", a, true, false, 2, p, snap));
        }

        [Fact]
        public void LargeTargetsPayTheTicksThatLand()
        {
            // Blight 5 x land 0.9: a 20 HP mob keeps the old 1.5 ticks; a 200 HP
            // boss pays the DoT's 3 ticks (CSV m_DurationAmount).
            var p = new PreviewScore { Ok = true, ApplyBlight = 5f, ResistOk = true, ResistBlight = 0.1f, DotTurns = 3f };
            var mob = Enemy(10, 20f, 20f);
            var boss = Enemy(11, 200f, 250f);
            Near(6.75f, KitSafety.DotHostDelta("pd_noxious_blast_u", mob, true, false, 3, p));
            Near(13.5f, KitSafety.DotHostDelta("pd_noxious_blast_u", boss, true, false, 3, p));
            // The last enemy alone is still a boss: no 1-tick cut.
            Near(13.5f, KitSafety.DotHostDelta("pd_noxious_blast_u", boss, true, false, 1, p));
            // A 4-turn DoT (skill_*_4_turns) on a boss.
            var four = new PreviewScore { Ok = true, ApplyBlight = 5f, DotTurns = 4f };
            Near(20f, KitSafety.DotHostDelta("pd_noxious_blast_u", boss, true, false, 3, four));
            // 36 HP left: the party takes about 12 a round, so 3 rounds.
            Near(3f, KitSafety.LongDotTicks(36f, 3f));
            Near(2.5f, KitSafety.LongDotTicks(30f, 3f));
        }

        [Fact]
        public void StackingOnALargeTargetAddsUnlessTheOldDotAlreadyKills()
        {
            // IL DotContainer.Add: every application is its own DotInstance and
            // all of them tick (logs: Health ticking 8 = 3 + 5).
            var p = new PreviewScore { Ok = true, ApplyBlight = 5f, DotTurns = 3f };
            var boss = Enemy(11, 200f, 250f);
            boss.BlightDot = 4f;
            boss.NextDot = 4f;
            Near(15f, KitSafety.DotHostDelta("pd_noxious_blast_u", boss, true, false, 3, p));
            // A 40 HP lock already ticking 20 (x2 left = 40): nothing left to take.
            var doomed = Enemy(12, 40f, 55f);
            doomed.BlightDot = 20f;
            doomed.NextDot = 20f;
            Near(-8f, KitSafety.DotHostDelta("pd_noxious_blast_u", doomed, true, false, 3, p));
            // Capped at what the old DoT leaves: 40 - 2 x 15 = 10 < 15.
            var capped = Enemy(13, 40f, 55f);
            capped.BlightDot = 15f;
            Near(10f, KitSafety.DotHostDelta("pd_noxious_blast_u", capped, true, false, 3, p));
            // Small targets keep the old stacking dock.
            var mob = Enemy(14, 20f, 20f);
            mob.BlightDot = 4f;
            Near(-8f, KitSafety.DotHostDelta("pd_noxious_blast_u", mob, true, false, 3, p));
        }

        // ---------------- Shackles of Denial: Magnesium Rain ----------------

        private static TargetInfo Lock(uint g, int rank, float hp = 55f)
        {
            var cls = g == Health ? "boss_brain_lock_health" : g == Melee ? "boss_brain_lock_melee" : g == Ranged ? "boss_brain_lock_ranged" : "boss_brain_lock_stress";
            return new TargetInfo { Guid = g, ClassId = cls, Team = 1, Living = true, Hp = hp, HpMax = 55f, HpPct = hp / 55f, Rank = rank };
        }

        // CSV boss_brain lock *: burn resist melee 0.1, ranged 0.1, health 0.5,
        // stress 0.2; blight 0.1 / 0.3 / 0.5 / 0.2.
        private static float BurnRes(uint g)
        {
            return g == Health ? 0.5f : g == Stress ? 0.2f : 0.1f;
        }

        private static float BlightRes(uint g)
        {
            return g == Health ? 0.5f : g == Stress ? 0.2f : g == Ranged ? 0.3f : 0.1f;
        }

        private static SkillSnap Skill(string id, string tag, int launchMask, bool friendly = false)
        {
            var masks = new int[16];
            for (var i = 0; i < masks.Length; i++)
                masks[i] = 0xF;
            return new SkillSnap
            {
                Id = id, Tags = new List<string> { tag }, LaunchMask = launchMask, TargetMasks = masks, IsFriendly = friendly,
                Role = new SkillRole { Attacks = !friendly }
            };
        }

        private static TargetInfo PdHero()
        {
            return new TargetInfo
            {
                Guid = Pd, ClassId = "plague_doctor", Team = 0, Living = true, Hp = 28f, HpMax = 30f, HpPct = 28f / 30f, Rank = 3,
                SkillIds = new List<string> { "pd_noxious_blast_u", "pd_magnesium_rain", "pd_plague_grenade_u", "pd_battlefield_medicine_u", "pd_emboldening_vapours" }
            };
        }

        // Ranged-tagged party; locks Melee r0, Ranged r1, Health r2, Stress r3
        // (the Denial board the other Denial tests use). Kill order Ranged,
        // Melee, Health, Stress: weights 4 / 3 / 2 / 1.
        private static TurnSnapshot Board(uint performer, TargetInfo body)
        {
            var snap = new TurnSnapshot { PerformerGuid = performer, Performer = body, FightId = "mountain_boss_brain_2" };
            snap.Heroes.Add(body);
            snap.Heroes.Add(new TargetInfo
            {
                Guid = 4, ClassId = "highwayman", Team = 0, Living = true, Hp = 30f, HpMax = 35f, HpPct = 30f / 35f, Rank = 1,
                SkillIds = new List<string> { "hwm_pistol_shot_u" }
            });
            snap.Enemies.Add(Lock(Melee, 0));
            snap.Enemies.Add(Lock(Ranged, 1));
            snap.Enemies.Add(Lock(Health, 2));
            snap.Enemies.Add(Lock(Stress, 3));
            foreach (var e in snap.Enemies)
                snap.Threats.Add(new EnemyThreat { Guid = e.Guid, ClassId = e.ClassId, Boss = true });
            snap.Skills["pd_noxious_blast_u"] = Skill("pd_noxious_blast_u", "ranged", 0xE);
            var rain = Skill("pd_magnesium_rain", "ranged", 0xE);
            // CSV pd_magnesium_rain: target_team_effects clear_corpse.
            rain.ClearCorpseEffect = true;
            rain.Role.Burn = true;
            snap.Skills["pd_magnesium_rain"] = rain;
            snap.Skills["hwm_pistol_shot_u"] = Skill("hwm_pistol_shot_u", "ranged", 0xE);
            snap.Skills["pd_emboldening_vapours"] = Skill("pd_emboldening_vapours", "pd_emboldening_vapours", 0xF, true);
            return snap;
        }

        // CSV pd_magnesium_rain: 0 direct, skill_dot_medium_burn (3 x 3) on every
        // enemy rank. Per-hit preview: each lock with its own burn resist.
        private static void MagnesiumRain(TurnSnapshot snap, params uint[] clicks)
        {
            foreach (var click in clicks)
            {
                var p = new PreviewScore { Ok = true, HitChance = 1f, DotPerHit = true, DotTurns = 3f, ApplyBurn = 3f, ResistOk = true, ResistBurn = BurnRes(click) };
                foreach (var e in snap.Enemies)
                {
                    p.HitGuids.Add(e.Guid);
                    p.Hits.Add(new PreviewHit { Guid = e.Guid, Burn = 3f, ResistOk = true, ResistBurn = BurnRes(e.Guid), ResistBlight = BlightRes(e.Guid) });
                }
                snap.Legal.Add(new LegalSnap { Skill = "pd_magnesium_rain", Target = click, Preview = p });
            }
        }

        private static void Noxious(TurnSnapshot snap, uint target)
        {
            var p = new PreviewScore
            {
                Ok = true, HitChance = 1f, Damage = 3.5f, DamageValid = true, DotPerHit = true, DotTurns = 3f,
                ApplyBlight = 5f, ResistOk = true, ResistBlight = BlightRes(target), ResistBurn = BurnRes(target)
            };
            p.HitGuids.Add(target);
            p.Hits.Add(new PreviewHit { Guid = target, Damage = 3.5f, Blight = 5f, ResistOk = true, ResistBlight = BlightRes(target), ResistBurn = BurnRes(target) });
            snap.Legal.Add(new LegalSnap { Skill = "pd_noxious_blast_u", Target = target, Preview = p });
        }

        [Fact]
        public void MagnesiumRainIsValuedOncePerLockWithEachLocksResist()
        {
            var snap = Board(Pd, PdHero());
            MagnesiumRain(snap, Ranged);
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            var rain = r.Candidates.Find(c => c.SkillId == "pd_magnesium_rain");
            var plan = DenialPlay.Plan(EnemyFocus.Scan(snap), snap);
            // Ranged 3 x 0.9 x 3 = 8.1 (w4) + Melee 8.1 (w3) + Health 3 x 0.5 x 3
            // = 4.5 (w2) + Stress 7.2 inside Health's 33% heal band (0).
            Assert.Equal(8.1 * 4 + 8.1 * 3 + 4.5 * 2, (double)DenialPlay.Value(rain, snap, plan), 2);
            // Unweighted, before the heal band: 8.1 + 8.1 + 4.5 + 7.2 = 27.9 lock HP.
            var total = 0.0;
            foreach (var e in snap.Enemies)
                total += DenialPlay.DotOnLock(rain.Preview, e.Guid);
            Assert.Equal(27.9, total, 2);
        }

        [Fact]
        public void MagnesiumRainBeatsNoxiousOnFourFreshLocks()
        {
            var snap = Board(Pd, PdHero());
            // Noxious on Ranged: (3.5 + 5 x 0.7 x 3) x 4 = 56; on Melee 17 x 3 = 51.
            Noxious(snap, Ranged);
            Noxious(snap, Melee);
            MagnesiumRain(snap, Melee, Ranged, Health, Stress);
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.Equal("pd_magnesium_rain", r.Chosen.SkillId);
        }

        [Fact]
        public void NoxiousStaysTheClickWhenOnlyTheHeadLockIsLeft()
        {
            // One lock left at 40: Magnesium burns only it (8.1) against Noxious 17.
            var snap = Board(Pd, PdHero());
            snap.Enemies.Clear();
            snap.Threats.Clear();
            snap.Enemies.Add(Lock(Melee, 0, 40f));
            snap.Threats.Add(new EnemyThreat { Guid = Melee, ClassId = "boss_brain_lock_melee", Boss = true });
            Noxious(snap, Melee);
            MagnesiumRain(snap, Melee);
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.Equal("pd_noxious_blast_u", r.Chosen.SkillId);
        }

        [Fact]
        public void OldLogMagnesiumRainUsesTheClickForEveryLock()
        {
            // Snapshot without per-hit fields: the old reading (top-level burn and
            // the click's resist on every lock) still replays.
            var snap = Board(Pd, PdHero());
            var p = new PreviewScore { Ok = true, HitChance = 1f, ApplyBurn = 3f, ResistOk = true, ResistBurn = 0.1f };
            foreach (var e in snap.Enemies)
            {
                p.HitGuids.Add(e.Guid);
                p.Hits.Add(new PreviewHit { Guid = e.Guid });
            }
            snap.Legal.Add(new LegalSnap { Skill = "pd_magnesium_rain", Target = Ranged, Preview = p });
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            var rain = r.Candidates.Find(c => c.SkillId == "pd_magnesium_rain");
            var plan = DenialPlay.Plan(EnemyFocus.Scan(snap), snap);
            Assert.Equal(8.1 * 4 + 8.1 * 3 + 8.1 * 2, (double)DenialPlay.Value(rain, snap, plan), 2);
        }

        // A logged Denial turn (golden denial_noxious_over_incision: PD rank 2,
        // round 4, Melee 28 with blight 5 on it, Ranged 46, Health 41, Stress 55)
        // with Magnesium Rain added to the legal list. Each lock's burn resist
        // is the one the game previewed for that lock on the same turn.
        private static Replay.GoldenCase DenialTurnWithMagnesium(out uint pd)
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Golden");
            var g = Replay.GoldenCase.Load(Path.Combine(dir, "denial_noxious_over_incision.json"));
            var snap = g.Snap;
            pd = snap.PerformerGuid;
            var burnRes = new Dictionary<uint, float>();
            foreach (var l in snap.Legal)
            {
                if (l.Preview != null && l.Preview.ResistOk && snap.IsEnemy(l.Target) && !burnRes.ContainsKey(l.Target))
                    burnRes[l.Target] = l.Preview.ResistBurn;
            }
            var rain = Skill("pd_magnesium_rain", "ranged", 0xE);
            rain.ClearCorpseEffect = true;
            rain.Role.Burn = true;
            snap.Skills["pd_magnesium_rain"] = rain;
            foreach (var click in snap.Enemies)
            {
                if (!click.Living || click.Corpse)
                    continue;
                var p = new PreviewScore { Ok = true, HitChance = 1f, DotPerHit = true, DotTurns = 3f, ApplyBurn = 3f, ResistOk = true };
                p.ResistBurn = burnRes.ContainsKey(click.Guid) ? burnRes[click.Guid] : BurnRes(click.Guid);
                foreach (var e in snap.Enemies)
                {
                    if (!e.Living || e.Corpse)
                        continue;
                    p.HitGuids.Add(e.Guid);
                    p.Hits.Add(new PreviewHit { Guid = e.Guid, Burn = 3f, ResistOk = true, ResistBurn = burnRes.ContainsKey(e.Guid) ? burnRes[e.Guid] : 0.2f });
                }
                snap.Legal.Add(new LegalSnap { Skill = "pd_magnesium_rain", Target = click.Guid, Preview = p });
            }
            snap.Reindex();
            return g;
        }

        [Fact]
        public void LoggedDenialTurnTakesMagnesiumRainWhenItIsWorthMore()
        {
            uint pd;
            var g = DenialTurnWithMagnesium(out pd);
            var snap = g.Snap;
            var r = TurnDecider.DecideSnapshot(snap);
            var plan = DenialPlay.Plan(EnemyFocus.Scan(snap), snap);
            TurnDecider.ScoredAction bestRain = null, bestNox = null;
            var rainV = float.NegativeInfinity;
            var noxV = float.NegativeInfinity;
            foreach (var c in r.Candidates)
            {
                if (!DenialPlay.IsLockAttack(c))
                    continue;
                var v = DenialPlay.Value(c, snap, plan);
                if (c.SkillId == "pd_magnesium_rain" && v > rainV) { rainV = v; bestRain = c; }
                if (c.SkillId == "pd_noxious_blast_u" && v > noxV) { noxV = v; bestNox = c; }
            }
            Assert.NotNull(bestRain);
            Assert.NotNull(bestNox);
            // Burn on all four locks (8.1 on Melee / Ranged, 4.5 Health, 7.2
            // Stress before the kill-order weights and Health's heal band)
            // against Noxious's 4.5 + blight 5 x 3 on one lock.
            Assert.True(rainV > noxV + DenialPlay.EvMargin, "rain " + rainV + " noxious " + noxV);
            Assert.Equal("pd_magnesium_rain", r.Chosen.SkillId);        }

        // ---------------- Emboldening Vapours ----------------

        [Fact]
        public void VapoursGoesOnAHitterNotOnTheDoctor()
        {
            var party = new PartyKit();
            party.Heroes.Add(new HeroKit { Guid = Pd, Living = true, Attacks = true });
            party.Heroes.Add(new HeroKit { Guid = Maa, Living = true, Attacks = true });
            var pd = new TargetInfo { Guid = Pd };
            var maa = new TargetInfo { Guid = Maa };
            Assert.Equal(-KitSafety.VapoursSelfDock, KitSafety.VapoursDelta("pd_emboldening_vapours_u", false, pd, pd, party));
            Assert.Equal(0f, KitSafety.VapoursDelta("pd_emboldening_vapours_u", false, maa, pd, party));
            // Alone (or every hitter dead): herself is fine.
            var alone = new PartyKit();
            alone.Heroes.Add(new HeroKit { Guid = Pd, Living = true, Attacks = true });
            Assert.Equal(0f, KitSafety.VapoursDelta("pd_emboldening_vapours", false, pd, pd, alone));
        }

        [Fact]
        public void VapoursIsTheRangedLockFallbackNotAPass()
        {
            // Ranged lock: Noxious / Magnesium are ranged-tagged and not legal.
            // Only Vapours (untagged) and the pass are.
            var snap = Board(Pd, PdHero());
            foreach (var g in new[] { Pd, 4u })
            {
                var p = new PreviewScore { Ok = true, HitChance = 1f };
                p.ApplyTarget.Add("strength");
                p.HitGuids.Add(g);
                snap.Legal.Add(new LegalSnap { Skill = "pd_emboldening_vapours", Target = g, Preview = p });
            }
            snap.Skills["pass_stress"] = new SkillSnap { Id = "pass_stress" };
            snap.Legal.Add(new LegalSnap { Skill = "pass_stress", Target = Pd, Preview = new PreviewScore { Ok = true } });
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.Equal("pd_emboldening_vapours", r.Chosen.SkillId);
            Assert.Equal(4u, r.Chosen.TargetGuid);
        }

        // ---------------- Chirurgeon: a live Poison Dart blight ----------------

        [Fact]
        public void ChirurgeonStillGetsTheDirectHitWhenPoisonDartsBlightIsRead()
        {
            // The live reader now reads gr_poison_dart's on_not_crit blight
            // (CSV skill_dot_small_blight, 2 x 3). The Chirurgeon note
            // (a DoT on him becomes a HoT) must still win.
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Golden");
            var g = Replay.GoldenCase.Load(Path.Combine(dir, "chirurgeon_direct_hit_over_dot.json"));
            foreach (var l in g.Snap.Legal)
            {
                if (l.Skill == "gr_poison_dart" && l.Preview != null)
                    l.Preview.ApplyBlight = 2f;
            }
            var r = TurnDecider.DecideSnapshot(g.Snap);
            Assert.Equal("gr_thrown_dagger_u", r.Chosen.SkillId);
        }
    }
}
