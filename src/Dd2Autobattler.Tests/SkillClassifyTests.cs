using Dd2Autobattler.Combat;
using Xunit;

namespace Dd2Autobattler.Tests
{
    public sealed class SkillClassifyTests
    {
        [Fact]
        public void Dies_to_dot_nets_regen_and_ignores_death_armor()
        {
            Assert.True(GameSnapshot.DiesToDotNow(2f, 4f, 0f, false, false));
            Assert.False(GameSnapshot.DiesToDotNow(2f, 4f, 3f, false, false));
            Assert.False(GameSnapshot.DiesToDotNow(2f, 4f, 0f, false, true));
            Assert.False(GameSnapshot.DiesToDotNow(2f, 4f, 0f, true, false));
            Assert.False(GameSnapshot.DiesToDotNow(5f, 4f, 0f, false, false));
        }

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
        public void Heal_in_the_skill_id_is_a_heal_when_the_def_is_missing()
        {
            var kind = TurnDecider.Classify("cru_battle_heal", null, null, false);
            Assert.Equal(SkillKind.Heal, kind);
        }

        [Fact]
        public void More_more_is_not_a_heal_from_the_name()
        {
            var kind = TurnDecider.Classify("flg_more_more", null, null, false);
            Assert.Equal(SkillKind.Attack, kind);
        }

        [Fact]
        public void Zero_heal_absinthe_is_not_a_crisis_heal()
        {
            var preview = new PreviewScore { Ok = true, Heal = 0f, HealValid = true };
            var target = new TargetInfo { Hp = 13f, HpPct = 0.342f };
            Assert.False(TurnDecider.RestoresHp(preview));
            Assert.False(TurnDecider.IsCrisisHealClick(SkillKind.Heal, "gr_artemisia_u", false, target, preview));
        }

        [Fact]
        public void Real_hp_restore_at_35_percent_is_a_crisis_heal()
        {
            var preview = new PreviewScore { Ok = true, Heal = 12f, HealValid = true };
            var target = new TargetInfo { Hp = 12f, HpPct = 0.31f };
            Assert.True(TurnDecider.RestoresHp(preview));
            Assert.True(TurnDecider.IsCrisisHealClick(SkillKind.Heal, "gr_artemisia_u", false, target, preview));
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
        public void Crush_self_heal_is_not_an_ally_heal_to_walk_for()
        {
            Assert.False(TurnDecider.IsAllyHealSkill("maa_crush_u", null));
            Assert.False(TurnDecider.IsAllyHealSkill("maa_hold_the_line", null));
            Assert.False(TurnDecider.IsAllyHealSkill("pass_heal", null));
            Assert.True(TurnDecider.IsAllyHealSkill("pd_battlefield_medicine_u", null));
            Assert.True(TurnDecider.IsAllyHealSkill("cru_battle_heal", null));
        }

        [Fact]
        public void Tracking_shot_on_healthless_taproot_is_a_wasted_mark()
        {
            var preview = new PreviewScore { Ok = true, Damage = 0f };
            preview.ApplyTarget.Add("combo");
            Assert.True(TurnDecider.IsComboOnlyTap("hwm_tracking_shot", preview));
            Assert.True(TurnDecider.IsComboOnlyTap("pd_blinding_gas", preview));
            Assert.False(TurnDecider.IsComboOnlyTap("hwm_pistol_shot", new PreviewScore { Ok = true, Damage = 4f, HitChance = 1f }));
            var tap = new TargetInfo { Healthless = true, ClassId = "lost_battalion_boss_taproot" };
            Assert.Equal(-80f, KitSafety.HealthlessMarkDelta("hwm_tracking_shot", tap, true, preview));
            Assert.Equal(0f, KitSafety.HealthlessMarkDelta("gr_thrown_dagger_u", tap, true, new PreviewScore { Ok = true, HitChance = 1f }));
            Assert.True(TurnDecider.ComboMarkWaste("hwm_tracking_shot", preview, tap));
            Assert.True(TurnDecider.ComboMarkWaste("pd_blinding_gas", preview,
                new TargetInfo { Combo = true, ClassId = "lost_battalion_boss_dreaming_general" }));
            Assert.False(TurnDecider.ComboMarkWaste("pd_noxious_blast",
                new PreviewScore { Ok = true, Damage = 2.5f, HitChance = 1f },
                new TargetInfo { Combo = true, ClassId = "lost_battalion_boss_dreaming_general" }));
            Assert.False(TurnDecider.ComboMarkWaste("pd_blinding_gas", preview,
                new TargetInfo { Combo = false, Hp = 80f, ClassId = "lost_battalion_boss_dreaming_general" }));
            Assert.True(TurnDecider.IsZeroDamageMark("hwm_tracking_shot", preview));
            Assert.False(TurnDecider.IsZeroDamageMark("hwm_wicked_slice",
                new PreviewScore { Ok = true, Damage = 2.8f, HitChance = 1f }));
            Assert.True(TurnDecider.ComboMarkWaste("hwm_tracking_shot", preview,
                new TargetInfo { Hp = 0f, DeathsDoor = true, ClassId = "plague_eater_dinner_cart" }));
            Assert.Equal(0f, TurnDecider.FocusPay("hwm_tracking_shot", preview, 138f));
            Assert.Equal(138f, TurnDecider.FocusPay("hwm_wicked_slice_u",
                new PreviewScore { Ok = true, Damage = 4.5f, HitChance = 1f }, 138f));
            Assert.Equal(138f, TurnDecider.FocusPay("gr_flashing_daggers",
                new PreviewScore { Ok = true, Damage = 6.2f, HitChance = 1f }, 138f));
            Assert.False(TurnDecider.ReachWalkSkill("hwm_tracking_shot"));
            Assert.False(TurnDecider.ReachWalkSkill("pd_blinding_gas"));
            Assert.True(TurnDecider.ReachWalkSkill("hwm_wicked_slice_u"));
            Assert.True(TurnDecider.ReachWalkSkill("hwm_pistol_shot"));
        }

        [Fact]
        public void Rest_does_not_spend_the_crisis_heal_slot()
        {
            Assert.False(TurnDecider.CountsAsCrisisHealSpend("pass_heal", SkillKind.Heal));
            Assert.False(TurnDecider.CountsAsCrisisHealSpend("pass_heal", SkillKind.Pass));
            Assert.True(TurnDecider.CountsAsCrisisHealSpend("pd_battlefield_medicine_u", SkillKind.Heal));
            Assert.True(TurnDecider.CountsAsCrisisHealSpend("gr_artemisia_u", SkillKind.Heal));
            Assert.False(TurnDecider.CountsAsCrisisHealSpend("hwm_wicked_slice_u", SkillKind.Attack));
        }

        [Fact]
        public void Rank_walk_preserves_ally_reach_only_on_the_librarian()
        {
            Assert.True(TurnDecider.PreserveAllyReach("academic_boss_librarian"));
            Assert.False(TurnDecider.PreserveAllyReach("academic_boss_librarian_book_stack"));
            Assert.False(TurnDecider.PreserveAllyReach("cultist_altar"));
            Assert.False(TurnDecider.PreserveAllyReach("cultist_cherub"));
            Assert.False(TurnDecider.PreserveAllyReach("cultist_deacon"));
        }

        [Fact]
        public void Last_killable_finish_ignores_taproot_and_slaps_death_armor()
        {
            var crush = new PreviewScore { Ok = true, Damage = 6f, Kills = true };
            var general = new TargetInfo { Hp = 5f, Healthless = false };
            Assert.True(TurnDecider.LastKillableFinish(crush, general, 1));
            Assert.False(TurnDecider.LastKillableFinish(crush, general, 2));

            var chip = new PreviewScore { Ok = true, Damage = 6f, Kills = false };
            Assert.False(TurnDecider.LastKillableFinish(chip, new TargetInfo { Hp = 12f }, 1));
            Assert.True(TurnDecider.LastKillableFinish(chip, new TargetInfo { Hp = 0f, DeathsDoor = true }, 1));

            var tap = new TargetInfo { Healthless = true, ClassId = "lost_battalion_boss_taproot", Hp = 99f };
            Assert.False(TurnDecider.LastKillableFinish(new PreviewScore { Ok = true, Damage = 0f, HitChance = 1f }, tap, 1));
        }

        [Fact]
        public void Flashing_daggers_that_splash_a_corpse_lose_to_two_living_hits()
        {
            Assert.Equal(0f, TurnDecider.CorpseSplashDelta(2, 0, 2, true));
            Assert.Equal(-80f, TurnDecider.CorpseSplashDelta(1, 1, 2, true));
            Assert.Equal(-50f, TurnDecider.CorpseSplashDelta(1, 1, 1, true));
            Assert.Equal(0f, TurnDecider.CorpseSplashDelta(1, 1, 1, false));
            Assert.Equal(0f, TurnDecider.CorpseSplashDelta(2, 1, 2, true));
        }

        [Fact]
        public void Purge_on_a_corpse_scores_clear_not_skip()
        {
            Assert.Equal(-250f, TurnDecider.CorpseTargetScore(false, 2));
            Assert.Equal(28f, TurnDecider.CorpseTargetScore(true, 2));
            Assert.Equal(42f, TurnDecider.CorpseTargetScore(true, 1));
            Assert.Equal(20f, TurnDecider.CorpseTargetScore(true, 3));
            Assert.True(ItemPolicy.ClearsCorpse("lep_purge", null));
            Assert.True(ItemPolicy.ClearsCorpse("lep_purge_u", null));
            Assert.False(ItemPolicy.ClearsCorpse("lep_chop", null));
        }

        [Fact]
        public void AoE_sums_living_hit_damage_and_ignores_corpses()
        {
            var mixed = new PreviewScore { Damage = 16f };
            mixed.Hits.Add(new PreviewHit { Guid = 1, Damage = 8f });
            mixed.Hits.Add(new PreviewHit { Guid = 2, Damage = 8f });
            int n;
            bool kills;
            Assert.Equal(8f, TurnDecider.SumLivingHitDamage(mixed, g => g == 1, out n, out kills));
            Assert.Equal(1, n);
            Assert.Equal(16f, TurnDecider.SumLivingHitDamage(mixed, g => g == 1 || g == 2, out n, out kills));
            Assert.Equal(2, n);

            var twoLiving = new PreviewScore { Damage = 8f };
            twoLiving.Hits.Add(new PreviewHit { Guid = 1, Damage = 8f });
            twoLiving.HitGuids.Add(1);
            twoLiving.HitGuids.Add(3);
            Assert.Equal(16f, TurnDecider.SumLivingHitDamage(twoLiving, g => g == 1 || g == 3, out n, out kills));
            Assert.Equal(2, n);

            var empty = new PreviewScore { Damage = 11f };
            Assert.Equal(11f, TurnDecider.SumLivingHitDamage(empty, g => true, out n, out kills));
        }

        [Fact]
        public void Click_kill_uses_that_target_hit_not_the_AoE_sum()
        {
            var preview = new PreviewScore { Ok = true, Damage = 16f };
            preview.Hits.Add(new PreviewHit { Guid = 10, Damage = 8f });
            preview.Hits.Add(new PreviewHit { Guid = 11, Damage = 8f });
            var click = new TargetInfo { Guid = 10, Hp = 12f };
            TurnDecider.NoteKillFromHp(preview, click, null);
            Assert.False(preview.Kills);
            click.Hp = 7f;
            TurnDecider.NoteKillFromHp(preview, click, null);
            Assert.True(preview.Kills);
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
        public void Eye_stalks_are_not_left_as_chips()
        {
            Assert.False(TurnDecider.CanLeaveChip("boss_eyes_stalk_l"));
            Assert.False(TurnDecider.CanLeaveChip("boss_eyes_stalk_m"));
            Assert.False(TurnDecider.CanLeaveChip("boss_eyes_stalk_s"));
            Assert.True(TurnDecider.CanLeaveChip("cultist_cherub"));
            Assert.True(TurnDecider.CanLeaveChip("boss_eyes"));
            Assert.True(TurnDecider.CanLeaveChip(null));
        }

        [Fact]
        public void Non_kill_stalk_aoe_loses_to_a_single_target()
        {
            Assert.Equal(-16f, TurnDecider.StalkChipAoEDelta(true, true, false, 2, 0f));
            Assert.Equal(0f, TurnDecider.StalkChipAoEDelta(true, true, true, 2, 0f));
            Assert.Equal(0f, TurnDecider.StalkChipAoEDelta(true, true, false, 1, 0f));
            Assert.Equal(0f, TurnDecider.StalkChipAoEDelta(true, true, false, 2, 3.5f));
            Assert.Equal(0f, TurnDecider.StalkChipAoEDelta(false, true, false, 2, 0f));
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
            var cherub = new EnemyThreat { Guid = 3, ClassId = "cultist_cherub", Supports = true };
            var deacon = new EnemyThreat { Guid = 2, ClassId = "cultist_deacon", Boss = true };
            focus.Enemies.Add(altar);
            focus.Enemies.Add(cherub);
            focus.Enemies.Add(deacon);
            EnemyFocus.ApplyCultistNote(focus);
            EnemyFocus.ScoreEnemies(focus);
            Assert.True(altar.MustKillFirst);
            Assert.False(altar.Defer);
            Assert.True(cherub.MustKillFirst);
            Assert.True(deacon.Defer);
            Assert.False(deacon.MustKillFirst);
            Assert.Equal(cherub.Score + EnemyFocus.AltarMustKillBias, altar.Score);
            Assert.True(altar.Score > cherub.Score);
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
