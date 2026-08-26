using Dd2Autobattler.Combat;
using Xunit;

namespace Dd2Autobattler.Tests
{
    public sealed class KitSafetyTests
    {
        [Fact]
        public void Howling_end_pays_at_three_winded()
        {
            Assert.Equal(28f, KitSafety.WindedDelta("hel_howling_end", 3, false, 1f));
            Assert.Equal(0f, KitSafety.WindedDelta("hel_howling_end", 2, false, 1f));
        }

        [Fact]
        public void Winded_attacks_generate_until_three_then_stop()
        {
            Assert.Equal(6f, KitSafety.WindedDelta("hel_wicked_hack", 1, false, 1f));
            Assert.Equal(-32f, KitSafety.WindedDelta("hel_iron_swan", 3, false, 1f));
            Assert.Equal(0f, KitSafety.WindedDelta("hel_iron_swan", 3, true, 1f));
        }

        [Fact]
        public void Performer_preview_tokens_drive_winded_without_a_skill_name()
        {
            Assert.Equal(6f, KitSafety.WindedDelta("unknown_skill", 1, false, 1f, true, false));
            Assert.Equal(-10f, KitSafety.WindedDelta("unknown_skill", 2, false, 1f, false, true));
        }

        [Fact]
        public void Bloodlust_dumps_three_winded_when_howling_end_is_not_the_click()
        {
            Assert.Equal(16f, KitSafety.WindedDelta("hel_bloodlust", 3, false, 1f));
        }

        [Fact]
        public void Adrenaline_does_not_dump_healthy_stacks_under_three()
        {
            Assert.Equal(-10f, KitSafety.WindedDelta("hel_adrenaline_rush", 2, false, 1f));
            Assert.Equal(8f, KitSafety.WindedDelta("hel_adrenaline_rush", 2, false, 0.30f));
        }

        [Fact]
        public void Blind_chop_is_skipped_unless_the_target_has_combo()
        {
            Assert.Equal(-36f, KitSafety.BlindDelta("lep_chop", SkillKind.Attack, true, true, false, false));
            Assert.Equal(0f, KitSafety.BlindDelta("lep_chop", SkillKind.Attack, true, true, true, false));
            Assert.Equal(0f, KitSafety.BlindDelta("lep_chop", SkillKind.Attack, true, false, false, false));
        }

        [Fact]
        public void Reflection_clears_blind()
        {
            Assert.Equal(32f, KitSafety.BlindDelta("lep_reflection", SkillKind.Support, false, true, false, false));
            Assert.Equal(-8f, KitSafety.BlindDelta("lep_reflection", SkillKind.Support, false, false, false, false));
        }

        [Fact]
        public void Ruin_charges_when_not_ready()
        {
            Assert.Equal(24f, KitSafety.RuinDelta("lep_ruin", false, 3));
            Assert.Equal(-20f, KitSafety.RuinDelta("lep_ruin_u", true, 3));
            Assert.Equal(0f, KitSafety.RuinDelta("lep_chop", false, 3));
        }

        [Fact]
        public void Flagellant_stays_low_above_floor_not_when_bleeding_out()
        {
            Assert.True(KitSafety.WantsToStayLow(new TargetInfo { ClassId = "flagellant", HpPct = 0.40f, Hp = 25f }));
            Assert.True(KitSafety.WantsToStayLow(new TargetInfo { MoreMore = true, HpPct = 0.40f, Hp = 20f }));
            Assert.False(KitSafety.WantsToStayLow(new TargetInfo { ClassId = "flagellant", HpPct = 0.25f, Hp = 16f }));
            Assert.False(KitSafety.WantsToStayLow(new TargetInfo { ClassId = "flagellant", HpPct = 0.03f, Hp = 2f, NextDot = 7f }));
            Assert.False(KitSafety.WantsToStayLow(new TargetInfo { ClassId = "flagellant", HpPct = 0.50f, Hp = 30f, DiesToDot = true }));
            Assert.False(KitSafety.WantsToStayLow(new TargetInfo { ClassId = "flagellant", DeathsDoor = true, HpPct = 0f }));
            Assert.False(KitSafety.WantsToStayLow(new TargetInfo { ClassId = "man_at_arms", HpPct = 0.25f }));
        }

        [Fact]
        public void More_more_is_paid_as_self_taunt()
        {
            Assert.Equal(22f, KitSafety.TauntSetupDelta("flg_more_more", SkillKind.Support, false, 3));
            Assert.Equal(0f, KitSafety.TauntSetupDelta("flg_more_more", SkillKind.Support, false, 1));
            Assert.Equal(8f, KitSafety.TauntSetupDelta("lep_intimidate", SkillKind.Attack, true, 3));
        }

        [Fact]
        public void Finale_is_not_a_chip_cleaner()
        {
            Assert.Equal(-55f, KitSafety.FinaleDelta("jes_finale", false, 3, 1f));
            Assert.Equal(12f, KitSafety.FinaleDelta("jes_finale", true, 3, 1f));
            Assert.Equal(0f, KitSafety.FinaleDelta("jes_razors_wit", false, 3, 1f));
        }

        [Fact]
        public void Wyrd_is_not_clicked_on_a_healthy_ally()
        {
            Assert.Equal(-30f, KitSafety.WyrdDelta("occ_wyrd_reconstruction", new TargetInfo { HpPct = 0.90f }, false));
            Assert.Equal(0f, KitSafety.WyrdDelta("occ_wyrd_reconstruction", new TargetInfo { HpPct = 0.20f }, false));
            Assert.Equal(0f, KitSafety.WyrdDelta("occ_wyrd_reconstruction", new TargetInfo { DeathsDoor = true, HpPct = 0f }, false));
        }

        [Fact]
        public void Chaotic_offering_is_not_used_while_low()
        {
            Assert.Equal(-40f, KitSafety.ChaoticOfferingDelta("occ_chaotic_offering", 0.30f, false));
            Assert.Equal(8f, KitSafety.ChaoticOfferingDelta("occ_chaotic_offering", 0.80f, false));
        }

        [Fact]
        public void Duelist_does_not_recast_the_stance_it_is_in()
        {
            Assert.Equal(-22f, KitSafety.StanceDelta("dul_meditation", SkillKind.Support, false, true));
            Assert.Equal(-22f, KitSafety.StanceDelta("dul_preparation", SkillKind.Support, true, false));
            Assert.Equal(0f, KitSafety.StanceDelta("dul_fleche", SkillKind.Attack, true, false));
            Assert.Equal(20f, KitSafety.StanceDelta("dul_meditation", SkillKind.Support, false, false));
        }

        [Fact]
        public void Last_laugh_pays_an_attacker_not_a_healthy_healer()
        {
            var attacker = new TargetInfo { HpPct = 1f };
            var healer = new TargetInfo { HpPct = 0.90f };
            var door = new TargetInfo { DeathsDoor = true, HpPct = 0f };
            Assert.Equal(32f, KitSafety.ExtraActionDelta("jes_the_last_laugh", attacker, false, true, false));
            Assert.Equal(-16f, KitSafety.ExtraActionDelta("jes_the_last_laugh", healer, false, false, true));
            Assert.Equal(24f, KitSafety.ExtraActionDelta("jes_the_last_laugh", door, false, false, true));
        }

        [Fact]
        public void Conviction_blessing_generates_then_grace_spends_on_crisis()
        {
            Assert.Equal(18f, KitSafety.ConvictionDelta("ves_blessing_of_light", 0, null, false));
            Assert.Equal(-12f, KitSafety.ConvictionDelta("ves_blessing_of_light", 3, null, false));
            var low = new TargetInfo { HpPct = 0.20f };
            Assert.Equal(16f, KitSafety.ConvictionDelta("ves_divine_grace", 2, low, false));
            Assert.Equal(0f, KitSafety.ConvictionDelta("ves_divine_grace", 0, low, false));
        }

        [Fact]
        public void Dot_host_prefers_a_clean_target()
        {
            var clean = new TargetInfo { BleedDot = 0f };
            var stacked = new TargetInfo { BleedDot = 4f };
            Assert.Equal(5f, KitSafety.DotHostDelta("hel_if_it_bleeds", clean, true, false, 3));
            Assert.Equal(-10f, KitSafety.DotHostDelta("hel_if_it_bleeds", stacked, true, false, 3));
            Assert.Equal(0f, KitSafety.DotHostDelta("hel_if_it_bleeds", stacked, true, true, 3));
            Assert.True(KitSafety.AppliesBleed("hwm_open_vein_u"));
            Assert.Equal(5f, KitSafety.DotHostDelta("hwm_open_vein_u", clean, true, false, 3));
            var preview = new PreviewScore { Ok = true, ApplyBleed = 4f };
            Assert.Equal(6f, KitSafety.DotHostDelta("hwm_open_vein_u", clean, true, false, 3, preview));
            Assert.True(PreviewScore.DotApplyPay(4f, 0f, 1f, 3) > PreviewScore.DotOpenPay(1f));
            Assert.Equal(-12f, PreviewScore.DotApplyPay(4f, 0f, 0.25f, 3));
            Assert.Equal(-90f, PreviewScore.DotApplyPay(5f, 0f, 0f, 3));
            Assert.Equal(-90f, PreviewScore.DotApplyPay(5f, 0f, PreviewScore.LandFromResist(2f), 3));
        }

        [Fact]
        public void High_burn_resist_makes_opening_burn_a_waste()
        {
            Assert.Equal(0.25f, PreviewScore.LandFromResist(0.75f));
            Assert.Equal(-12f, PreviewScore.DotOpenPay(0.25f));
            Assert.Equal(-90f, PreviewScore.DotOpenPay(0f));
            Assert.Equal(5f, PreviewScore.DotOpenPay(1f));
            var preview = new PreviewScore { Ok = true, ResistOk = true, ResistBurn = 0.75f };
            var clean = new TargetInfo { BurnDot = 0f };
            Assert.Equal(-12f, KitSafety.DotHostDelta("run_firefly", clean, true, false, 3, preview));
            var immune = new PreviewScore { Ok = true, ResistOk = true, ResistBlight = 2f, ApplyBlight = 5f };
            Assert.Equal(-90f, KitSafety.DotHostDelta("pd_noxious_blast_p2", clean, true, false, 3, immune));
        }

        [Fact]
        public void Hearthlight_pays_when_anyone_is_stealthed()
        {
            Assert.Equal(22f, KitSafety.HearthlightDelta("run_hearthlight", new TargetInfo { Stealth = true }, false));
            Assert.Equal(22f, KitSafety.HearthlightDelta("run_hearthlight", new TargetInfo(), true));
            Assert.Equal(-8f, KitSafety.HearthlightDelta("run_hearthlight", new TargetInfo(), false));
        }

        [Fact]
        public void Firestarter_pays_an_attacker()
        {
            Assert.Equal(16f, KitSafety.FirestarterDelta("run_firestarter", false, true));
            Assert.Equal(-20f, KitSafety.FirestarterDelta("run_firestarter", true, true));
        }

        [Fact]
        public void Beast_skills_are_not_clicked_in_human_form()
        {
            Assert.Equal(-30f, KitSafety.BeastFormDelta("abm_rake", false));
            Assert.Equal(0f, KitSafety.BeastFormDelta("abm_rake", true));
            Assert.Equal(-30f, KitSafety.BeastFormDelta("abm_manacles", true));
            Assert.Equal(0f, KitSafety.BeastFormDelta("abm_manacles", false));
        }

        [Fact]
        public void Pull_pays_when_the_destination_is_hittable_and_current_is_not()
        {
            var party = new PartyKit();
            party.Heroes.Add(new HeroKit { Guid = 1, Living = true, Attacks = true, AttackHitRanks = 1 << 1 });
            Assert.Equal(28f, PartySynergy.MoveDelta("occ_daemons_pull", 2, true, false, party, 1f));
            Assert.Equal(0f, PartySynergy.MoveDelta("occ_daemons_pull", 1, true, false, party, 1f));
            Assert.Equal(-8f, PartySynergy.MoveDelta("maa_rampart", 0, true, false, party, 0.2f));
        }

        [Fact]
        public void Hits_rank_mask_does_not_unseat_unknown()
        {
            Assert.True(PartySynergy.HitsRankMask(1 << 3, 3));
            Assert.False(PartySynergy.HitsRankMask(1 << 1, 3));
            Assert.False(PartySynergy.HitsRankMask(PartySynergy.ComboRanksUnknown, 3));
        }

        [Fact]
        public void Front_corpse_clogs_when_every_living_enemy_is_behind_it()
        {
            Assert.True(PartySynergy.CorpseClogsRanks(0, new[] { 1, 2, 3 }));
            Assert.False(PartySynergy.CorpseClogsRanks(2, new[] { 0, 3 }));
            Assert.False(PartySynergy.CorpseClogsRanks(0, new int[0]));
        }

        [Fact]
        public void Front_walk_is_not_paid_when_an_ally_owns_rank_zero()
        {
            Assert.Equal(-24f, PartySynergy.FrontWalkDelta("hel_toe_to_toe", 1, true));
            Assert.Equal(18f, PartySynergy.FrontWalkDelta("hel_toe_to_toe", 1, false));
            Assert.Equal(0f, PartySynergy.FrontWalkDelta("hel_wicked_hack", 1, true));
        }

        [Fact]
        public void Advance_is_docked_when_it_shoves_acid_rain_off_launch()
        {
            var party = new PartyKit();
            party.Heroes.Add(new HeroKit { Guid = 1, Living = true, Rank = 1, ClassId = "flagellant", AcidRain = true });
            party.Heroes.Add(new HeroKit { Guid = 2, Living = true, Rank = 2, ClassId = "highwayman" });
            Assert.Equal(-48f, PartySynergy.AdvanceDisplaceDelta("hwm_duelists_advance", 2, party));
            Assert.Equal(0f, PartySynergy.AdvanceDisplaceDelta("hwm_duelists_advance", 1, party));
            Assert.Equal(0f, PartySynergy.AdvanceDisplaceDelta("hwm_wicked_slice", 2, party));
        }

        [Fact]
        public void Rank_walk_sits_when_an_ally_already_hits_the_must_kill()
        {
            var party = new PartyKit();
            party.Heroes.Add(new HeroKit { Guid = 1, Living = true, Rank = 0, Attacks = true, AttackHitRanks = 1 << 0 });
            party.Heroes.Add(new HeroKit { Guid = 2, Living = true, Rank = 1, Attacks = true, AttackHitRanks = 1 << 2 });
            Assert.True(party.AllyHitsEnemyRank(1, 2));
            Assert.False(party.AllyHitsEnemyRank(2, 2));
            Assert.False(party.AllyHitsEnemyRank(1, 0));
        }

        [Fact]
        public void Self_crisis_pays_solemnity_on_the_dying_performer()
        {
            var lep = new TargetInfo { Guid = 9, ClassId = "leper", Rank = 0, Hp = 2f, HpPct = 0.04f, DiesToDot = true };
            var ally = new TargetInfo { Guid = 8, ClassId = "plague_doctor", Rank = 3, Hp = 20f, HpPct = 0.50f };
            var heal = new PreviewScore { Ok = true, Heal = 12f, HealValid = true };
            Assert.Equal(40f, KitSafety.SelfCrisisDelta("lep_solemnity", SkillKind.Heal, false, lep, lep, heal));
            Assert.Equal(-25f, KitSafety.SelfCrisisDelta("medic_salve", SkillKind.Heal, false, ally, lep, heal));
            Assert.Equal(0f, KitSafety.SelfCrisisDelta("lep_solemnity", SkillKind.Heal, false, lep,
                new TargetInfo { Guid = 9, HpPct = 0.80f, Hp = 40f }, heal));
        }

        [Fact]
        public void Front_occupied_sees_a_living_hellion_in_rank_zero()
        {
            var party = new PartyKit();
            party.Heroes.Add(new HeroKit { Guid = 1, Living = true, Rank = 0, ClassId = "hellion" });
            party.Heroes.Add(new HeroKit { Guid = 2, Living = true, Rank = 1, ClassId = "highwayman" });
            Assert.True(PartySynergy.FrontOccupiedByOther(party, 2));
            Assert.False(PartySynergy.FrontOccupiedByOther(party, 1));
        }

        [Fact]
        public void Combo_apply_is_not_paid_on_a_rank_no_spender_can_hit()
        {
            CombatMemory.ResetFight();
            var preview = new PreviewScore { Ok = true };
            preview.ApplyTarget.Add("combo");
            var target = new TargetInfo { Hp = 20f, Rank = 3 };
            var party = new PartyKit();
            party.PartySpendsCombo = true;
            party.Heroes.Add(new HeroKit { Guid = 2, Living = true, SpendsCombo = true, ComboHitRanks = 1 << 0 });
            var eval = TokenPrices.Evaluate(SkillKind.Attack, true, preview, target, 2, party, 1, null);
            Assert.Equal(0f, eval.Score);
        }

        [Fact]
        public void Combo_apply_still_pays_when_spender_ranks_are_unknown()
        {
            CombatMemory.ResetFight();
            var preview = new PreviewScore { Ok = true };
            preview.ApplyTarget.Add("combo");
            var target = new TargetInfo { Hp = 20f, Rank = 3 };
            var party = new PartyKit();
            party.PartySpendsCombo = true;
            party.Heroes.Add(new HeroKit { Guid = 2, Living = true, SpendsCombo = true, ComboHitRanks = PartySynergy.ComboRanksUnknown });
            var eval = TokenPrices.Evaluate(SkillKind.Attack, true, preview, target, 2, party, 1, null);
            Assert.Equal(14f, eval.Score);
            Assert.Equal("apply_combo", eval.Reason);
        }

        [Fact]
        public void Abom_transforms_into_beast_and_does_not_revert_healthy()
        {
            Assert.Equal(55f, KitSafety.TransformDelta("abm_transform", false, 1f, false, false, 3));
            Assert.Equal(-50f, KitSafety.TransformDelta("abm_revert", true, 0.80f, false, false, 3));
            Assert.Equal(36f, KitSafety.TransformDelta("abm_revert", true, 0.20f, false, false, 3));
        }
    }
}
