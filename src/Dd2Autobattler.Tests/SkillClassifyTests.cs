using Dd2Autobattler.Combat;
using Xunit;

namespace Dd2Autobattler.Tests
{
    public sealed class SkillClassifyTests
    {
        [Fact]
        public void Enemy_attack_with_self_heal_is_still_an_attack()
        {
            var preview = new PreviewScore { Ok = true, Damage = 6.5f, Heal = 5f, HealValid = true };
            var kind = TurnDecider.Classify("maa_crush", null, preview, true);
            Assert.Equal(SkillKind.Attack, kind);
        }

        [Fact]
        public void Ally_heal_preview_is_a_heal()
        {
            var preview = new PreviewScore { Ok = true, Heal = 8f, HealValid = true };
            var kind = TurnDecider.Classify("pd_battlefield_medicine_u", null, preview, false);
            Assert.Equal(SkillKind.Heal, kind);
        }

        [Fact]
        public void Pass_heal_without_preview_is_rest_not_a_skill_heal()
        {
            var kind = TurnDecider.Classify("pass_heal", null, null, false);
            Assert.Equal(SkillKind.Pass, kind);
        }

        [Fact]
        public void Shadow_rank_is_1_when_human_matches_the_top_score()
        {
            var legal = new System.Collections.Generic.List<Newtonsoft.Json.Linq.JObject>
            {
                new Newtonsoft.Json.Linq.JObject { ["skill"] = "maa_crush", ["target"] = 10, ["score"] = 40f },
                new Newtonsoft.Json.Linq.JObject { ["skill"] = "maa_bellow", ["target"] = 10, ["score"] = 22f }
            };
            Assert.Equal(1, TurnDecider.RankInLegal(legal, "maa_crush", 10));
            Assert.Equal(2, TurnDecider.RankInLegal(legal, "maa_bellow", 10));
            Assert.Equal(-1, TurnDecider.RankInLegal(legal, "pd_noxious", 10));
        }

        [Fact]
        public void Shadow_compare_flags_a_mismatch_and_the_score_gap()
        {
            var bot = new ChosenAction { SkillId = "maa_bellow", TargetGuid = 10, Reason = "peel", Score = 30f };
            var legal = new System.Collections.Generic.List<Newtonsoft.Json.Linq.JObject>
            {
                new Newtonsoft.Json.Linq.JObject { ["skill"] = "maa_bellow", ["target"] = 10, ["score"] = 30f },
                new Newtonsoft.Json.Linq.JObject { ["skill"] = "maa_crush", ["target"] = 10, ["score"] = 12f }
            };
            var row = TurnDecider.ShadowCompare(bot, legal, "maa_crush", 10);
            Assert.False(row.Value<bool>("match"));
            Assert.Equal(2, row["human"].Value<int>("rank"));
            Assert.Equal(18f, row.Value<float>("gap"));
        }

        [Fact]
        public void Leave_chip_ply_makes_overkill_worse_than_a_real_hit()
        {
            var overkill = TurnDecider.LeaveChipPly(14f);
            Assert.True(overkill < 0f);
            var small = TurnDecider.LeaveChipPly(2f);
            Assert.True(small > overkill);
        }

        [Fact]
        public void Librarian_stack_guid_matches()
        {
            var focus = new EnemyFocus();
            focus.Enemies.Add(new EnemyThreat
            {
                Guid = 511,
                ClassId = "fanatic_librarian_stack_s"
            });
            focus.Enemies.Add(new EnemyThreat
            {
                Guid = 512,
                ClassId = "fanatic_librarian"
            });
            Assert.True(focus.IsLibrarianStack(511));
            Assert.False(focus.IsLibrarianStack(512));
            Assert.False(focus.IsLibrarianStack(1));
        }

        [Fact]
        public void Chirurgeon_note_defers_patients()
        {
            var focus = new EnemyFocus();
            var patient = new EnemyThreat
            {
                Guid = 1,
                ClassId = "shared_lost_soul_patient",
                Boss = true
            };
            var chirurgeon = new EnemyThreat
            {
                Guid = 2,
                ClassId = "shared_lost_soul_chirurgeon",
                Boss = true
            };
            focus.Enemies.Add(patient);
            focus.Enemies.Add(chirurgeon);
            EnemyFocus.ApplyChirurgeonNote(focus);
            Assert.True(focus.HasMustKillFirst);
            Assert.True(chirurgeon.MustKillFirst);
            Assert.False(chirurgeon.Defer);
            Assert.True(patient.Defer);
            Assert.True(patient.Add);
            Assert.False(patient.Boss);
        }

        [Fact]
        public void Leviathan_note_must_kills_the_hand()
        {
            var focus = new EnemyFocus();
            var hand = new EnemyThreat
            {
                Guid = 674,
                ClassId = "coastal_boss_leviathan_hand",
                Boss = true,
                Add = true
            };
            var body = new EnemyThreat
            {
                Guid = 673,
                ClassId = "coastal_boss_leviathan",
                Boss = true,
                Summons = true
            };
            focus.Enemies.Add(hand);
            focus.Enemies.Add(body);
            EnemyFocus.ApplyLeviathanNote(focus);
            Assert.True(focus.HasMustKillFirst);
            Assert.True(hand.MustKillFirst);
            Assert.False(hand.Defer);
            Assert.False(hand.Add);
            Assert.True(body.Defer);
            Assert.True(body.Add);
            Assert.False(body.MustKillFirst);
        }

        [Fact]
        public void Leviathan_note_defers_a_hand_dying_to_dot()
        {
            var focus = new EnemyFocus();
            var hand = new EnemyThreat
            {
                Guid = 674,
                ClassId = "coastal_boss_leviathan_hand",
                Boss = true,
                DiesToDot = true
            };
            var body = new EnemyThreat
            {
                Guid = 673,
                ClassId = "coastal_boss_leviathan",
                Boss = true,
                Summons = true
            };
            focus.Enemies.Add(hand);
            focus.Enemies.Add(body);
            EnemyFocus.ApplyLeviathanNote(focus);
            Assert.False(hand.MustKillFirst);
            Assert.True(hand.Defer);
            Assert.True(hand.Add);
            Assert.False(body.Defer);
            Assert.False(body.Add);
            Assert.False(body.MustKillFirst);
        }

        [Fact]
        public void Leviathan_note_noop_without_the_hand()
        {
            var focus = new EnemyFocus();
            var body = new EnemyThreat
            {
                Guid = 673,
                ClassId = "coastal_boss_leviathan",
                Boss = true,
                Summons = true
            };
            focus.Enemies.Add(body);
            EnemyFocus.ApplyLeviathanNote(focus);
            Assert.False(focus.HasMustKillFirst);
            Assert.False(body.Defer);
            Assert.False(body.MustKillFirst);
        }

        [Fact]
        public void Deacon_note_kills_altar_before_the_boss()
        {
            var focus = new EnemyFocus();
            var altar = new EnemyThreat { Guid = 1, ClassId = "cultist_altar", Supports = true };
            var deacon = new EnemyThreat { Guid = 2, ClassId = "cultist_deacon", Boss = true };
            focus.Enemies.Add(altar);
            focus.Enemies.Add(deacon);
            EnemyFocus.ApplyCultistNote(focus);
            Assert.True(altar.MustKillFirst);
            Assert.False(altar.Defer);
            Assert.True(deacon.Defer);
            Assert.False(deacon.MustKillFirst);
        }

        [Fact]
        public void Exemplar_note_focuses_the_boss_and_defers_the_altar()
        {
            var focus = new EnemyFocus();
            var altar = new EnemyThreat { Guid = 1, ClassId = "cultist_altar", Supports = true };
            var herald = new EnemyThreat { Guid = 2, ClassId = "cultist_herald" };
            var exemplar = new EnemyThreat { Guid = 3, ClassId = "cultist_exemplar", Boss = true, Add = true };
            focus.Enemies.Add(altar);
            focus.Enemies.Add(herald);
            focus.Enemies.Add(exemplar);
            EnemyFocus.ApplyCultistNote(focus);
            Assert.True(exemplar.MustKillFirst);
            Assert.False(exemplar.Add);
            Assert.True(altar.Defer);
            Assert.False(herald.Defer);
            Assert.False(herald.Add);
        }

        [Fact]
        public void Body_of_work_kills_proclaimers_before_the_god()
        {
            var focus = new EnemyFocus();
            var cherub = new EnemyThreat { Guid = 1, ClassId = "boss_body_cherub" };
            var god = new EnemyThreat { Guid = 2, ClassId = "boss_body_phase3", Boss = true };
            focus.Enemies.Add(cherub);
            focus.Enemies.Add(god);
            EnemyFocus.ApplyBodyOfWorkNote(focus);
            Assert.True(cherub.MustKillFirst);
            Assert.True(god.Defer);
            Assert.False(god.MustKillFirst);
        }

        [Fact]
        public void Body_of_work_kills_the_spectre_before_the_god()
        {
            var focus = new EnemyFocus();
            var spectre = new EnemyThreat { Guid = 1, ClassId = "boss_body_failure_pd" };
            var god = new EnemyThreat { Guid = 2, ClassId = "boss_body_phase3", Boss = true };
            focus.Enemies.Add(spectre);
            focus.Enemies.Add(god);
            EnemyFocus.ApplyBodyOfWorkNote(focus);
            Assert.True(spectre.MustKillFirst);
            Assert.True(god.Defer);
        }

        [Fact]
        public void Ravenous_reach_is_must_kill()
        {
            var focus = new EnemyFocus();
            var arms = new EnemyThreat { Guid = 1, ClassId = "boss_arms_phase2", Boss = true };
            focus.Enemies.Add(arms);
            EnemyFocus.ApplyRavenousReachNote(focus);
            Assert.True(focus.HasMustKillFirst);
            Assert.True(arms.MustKillFirst);
            Assert.False(arms.Defer);
        }
    }
}
