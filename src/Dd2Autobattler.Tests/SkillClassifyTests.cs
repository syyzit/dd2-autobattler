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
            Assert.True(focus.ReachPhase2);
            Assert.False(focus.ReachPhase3);
        }

        [Fact]
        public void Rank_0_1_are_wiki_front_two_and_3_is_back()
        {
            Assert.True(TurnDecider.RankIsFrontTwo(0));
            Assert.True(TurnDecider.RankIsFrontTwo(1));
            Assert.False(TurnDecider.RankIsFrontTwo(2));
            Assert.True(TurnDecider.RankIsBack(3));
            Assert.False(TurnDecider.RankIsBack(2));
        }

        [Fact]
        public void Undertow_walk_when_marked_in_front_and_hand_lives()
        {
            Assert.True(TurnDecider.ShouldWalkOffUndertow(true, true, 0, false));
            Assert.True(TurnDecider.ShouldWalkOffUndertow(true, true, 1, false));
            Assert.False(TurnDecider.ShouldWalkOffUndertow(true, true, 0, true));
            Assert.False(TurnDecider.ShouldWalkOffUndertow(true, true, 2, false));
            Assert.False(TurnDecider.ShouldWalkOffUndertow(true, false, 0, false));
            Assert.False(TurnDecider.ShouldWalkOffUndertow(false, true, 0, false));
        }

        [Fact]
        public void Exemplar_taunt_skip_only_from_rank_4()
        {
            Assert.Equal(50f, TurnDecider.ExemplarTauntSkip(true, true, 3));
            Assert.Equal(0f, TurnDecider.ExemplarTauntSkip(true, true, 0));
            Assert.Equal(0f, TurnDecider.ExemplarTauntSkip(true, false, 3));
            Assert.Equal(0f, TurnDecider.ExemplarTauntSkip(false, true, 3));
        }

        [Fact]
        public void Exemplar_guard_redirects_combo_unless_the_tank_also_has_combo()
        {
            Assert.Equal(40f, TurnDecider.ExemplarGuardCombo(true, true, true, false));
            Assert.Equal(0f, TurnDecider.ExemplarGuardCombo(true, true, true, true));
            Assert.Equal(0f, TurnDecider.ExemplarGuardCombo(true, true, false, false));
        }

        [Fact]
        public void Haymaker_guard_beats_heal_on_the_contempt_mark()
        {
            var guard = TurnDecider.HaymakerGuardBonus(true, true, true);
            var heal = TurnDecider.HaymakerHealBonus(true, true, true);
            Assert.True(guard > heal);
            Assert.Equal(0f, TurnDecider.HaymakerGuardBonus(true, true, false));
            Assert.Equal(22f, TurnDecider.HaymakerBluntBonus(true, true, true, false));
            Assert.Equal(22f, TurnDecider.HaymakerBluntBonus(true, true, false, true));
            Assert.Equal(0f, TurnDecider.HaymakerBluntBonus(true, false, true, true));
        }

        [Fact]
        public void Peel_bonus_prices_riposte_above_a_small_hit()
        {
            var riposte = TurnDecider.PeelBonus(new TargetInfo { Riposte = true });
            var dodge = TurnDecider.PeelBonus(new TargetInfo { Dodge = true });
            Assert.True(riposte > dodge);
            Assert.True(riposte >= 16f);
        }

        [Fact]
        public void Leviathan_note_flags_the_hand_even_when_dying_to_dot()
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
                Boss = true
            };
            focus.Enemies.Add(hand);
            focus.Enemies.Add(body);
            EnemyFocus.ApplyLeviathanNote(focus);
            Assert.True(focus.LeviathanHandUp);
        }

        [Fact]
        public void Exemplar_note_sets_exemplar_up()
        {
            var focus = new EnemyFocus();
            focus.Enemies.Add(new EnemyThreat { Guid = 3, ClassId = "cultist_exemplar", Boss = true });
            EnemyFocus.ApplyCultistNote(focus);
            Assert.True(focus.ExemplarUp);
        }
    }
}
