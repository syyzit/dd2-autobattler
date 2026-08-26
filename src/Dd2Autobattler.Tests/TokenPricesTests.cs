using Dd2Autobattler.Combat;
using Xunit;

namespace Dd2Autobattler.Tests
{
    public sealed class TokenPricesTests
    {
        public TokenPricesTests()
        {
            CombatMemory.ResetFight();
        }

        [Fact]
        public void Hitting_combo_without_spending_costs_32()
        {
            var preview = new PreviewScore { Ok = true };
            var target = new TargetInfo { Combo = true, Hp = 20f };
            var eval = TokenPrices.Evaluate(SkillKind.Attack, true, preview, target, 3, null, 1, null);
            Assert.Equal(-32f, eval.Score);
            Assert.Equal("save_combo", eval.Reason);
        }

        [Fact]
        public void Open_vein_on_combo_is_a_spend_not_a_save()
        {
            var preview = new PreviewScore { Ok = true };
            var target = new TargetInfo { Combo = true, Hp = 20f };
            var role = new SkillRole { SpendsCombo = true, Bleed = true, ComboSpendValue = 12f };
            var eval = TokenPrices.Evaluate(SkillKind.Attack, true, preview, target, 3, null, 1, role);
            Assert.True(eval.Score > 0f);
            Assert.Equal("spend_combo", eval.Reason);
        }

        [Fact]
        public void Applying_combo_with_a_follow_up_spender_is_14()
        {
            var preview = new PreviewScore { Ok = true };
            preview.ApplyTarget.Add("combo");
            var target = new TargetInfo { Hp = 20f };
            var party = new PartyKit();
            party.PartySpendsCombo = true;
            party.Heroes.Add(new HeroKit { Guid = 2, Living = true, SpendsCombo = true });
            var eval = TokenPrices.Evaluate(SkillKind.Attack, true, preview, target, 3, party, 1, null);
            Assert.Equal(14f, eval.Score);
            Assert.Equal("apply_combo", eval.Reason);
        }

        [Fact]
        public void Attack_self_riposte_is_paid_on_the_performer()
        {
            var preview = new PreviewScore { Ok = true };
            preview.ApplyPerformer.Add("riposte");
            var target = new TargetInfo { Hp = 10f };
            var performer = new TargetInfo { Rank = 0, Riposte = false };
            var eval = TokenPrices.Evaluate(SkillKind.Attack, true, preview, target, 2, null, 1, null, performer);
            Assert.Equal(8f, eval.Score);
            Assert.Equal("self_riposte", eval.Reason);
        }

        [Fact]
        public void Blocked_zero_damage_hit_is_penalized_unless_it_strips_block()
        {
            var bounce = new PreviewScore { Ok = true, Blocked = true, Damage = 0f };
            var peel = new PreviewScore { Ok = true, Blocked = true, Damage = 0f };
            peel.RemoveTarget.Add("block");
            var target = new TargetInfo { Hp = 20f, BlockCount = 2 };
            var miss = TokenPrices.Evaluate(SkillKind.Attack, true, bounce, target, 2, null, 1, null);
            var strip = TokenPrices.Evaluate(SkillKind.Attack, true, peel, target, 2, null, 1, null);
            Assert.True(miss.Score < 0f);
            Assert.Equal("blocked_hit", miss.Reason);
            Assert.True(strip.Score > miss.Score);
            Assert.Equal("peel_block", strip.Reason);
        }

        [Fact]
        public void Guard_redirect_does_not_count_as_killing_the_click_target()
        {
            var preview = new PreviewScore { Ok = true, Damage = 8f, GuardGuid = 99 };
            var add = new TargetInfo { Guid = 1, Hp = 5f };
            TurnDecider.NoteKillFromHp(preview, add, null);
            Assert.False(preview.Kills);
        }

        [Fact]
        public void Guard_guid_on_the_click_target_is_the_protected_actor()
        {
            var preview = new PreviewScore { Ok = true, Damage = 4.4f, Kills = true, GuardGuid = 10 };
            preview.Hits.Add(new PreviewHit { Guid = 10, Damage = 4.4f, Kills = true });
            preview.Hits.Add(new PreviewHit { Guid = 20, Damage = 4.4f, Kills = true });
            preview.HitGuids.Add(10);
            preview.HitGuids.Add(20);
            var drummer = new TargetInfo { Guid = 10, Hp = 6f };
            Assert.Equal(20u, TurnDecider.GuardBarGuid(preview, drummer));
            Assert.Equal(20u, TurnDecider.FocusPayGuid(preview, drummer));
            TurnDecider.NoteKillFromHp(preview, drummer, null);
            Assert.True(preview.Kills);
            Assert.False(TurnDecider.CountsAsDamagingFocusClick("gr_thrown_dagger_p3_u", preview, false));
        }

        [Fact]
        public void Damage_into_a_0_hp_bar_is_a_kill()
        {
            var preview = new PreviewScore { Ok = true, Damage = 5f };
            var armor = new TargetInfo { Guid = 3, Hp = 0f, DeathArmor = true };
            TurnDecider.NoteKillFromHp(preview, armor, null);
            Assert.True(preview.Kills);

            var tap = new PreviewScore { Ok = true, Damage = 0f };
            var root = new TargetInfo { Guid = 4, Hp = 0f, Healthless = true };
            TurnDecider.NoteKillFromHp(tap, root, null);
            Assert.False(tap.Kills);
        }

        [Fact]
        public void Stun_the_next_enemy_in_order_pays_extra()
        {
            var preview = new PreviewScore { Ok = true };
            preview.ApplyTarget.Add("stun");
            var target = new TargetInfo { Hp = 10f, Guid = 50 };
            var later = TokenPrices.Evaluate(SkillKind.Attack, true, preview, target, 2, null, 1, null, null, 99);
            var next = TokenPrices.Evaluate(SkillKind.Attack, true, preview, target, 2, null, 1, null, null, 50);
            Assert.True(next.Score > later.Score);
            Assert.Equal("stun_next", next.Reason);
        }

        [Fact]
        public void Stun_pay_scales_with_preview_resist()
        {
            var preview = new PreviewScore { Ok = true, ResistOk = true, ResistStun = 0.80f };
            preview.ApplyTarget.Add("stun");
            var target = new TargetInfo { Hp = 10f };
            var eval = TokenPrices.Evaluate(SkillKind.Attack, true, preview, target, 2, null, 1, null);
            Assert.True(eval.Score > 1.1f && eval.Score < 1.3f);
        }

        [Fact]
        public void Applying_combo_to_a_1hp_chip_is_zero()
        {
            CombatMemory.NoteRound(1);
            var preview = new PreviewScore { Ok = true };
            preview.ApplyTarget.Add("combo");
            var target = new TargetInfo { Hp = 1f };
            var party = new PartyKit();
            party.PartySpendsCombo = true;
            party.Heroes.Add(new HeroKit { Guid = 2, Living = true, SpendsCombo = true });
            var eval = TokenPrices.Evaluate(SkillKind.Attack, true, preview, target, 3, party, 1, null);
            Assert.Equal(0f, eval.Score);
            Assert.Null(eval.Reason);
        }

        [Fact]
        public void Applying_combo_to_deaths_door_is_zero()
        {
            CombatMemory.NoteRound(1);
            var preview = new PreviewScore { Ok = true };
            preview.ApplyTarget.Add("combo");
            var target = new TargetInfo { Hp = 0f, DeathsDoor = true };
            var party = new PartyKit();
            party.PartySpendsCombo = true;
            party.Heroes.Add(new HeroKit { Guid = 2, Living = true, SpendsCombo = true });
            var eval = TokenPrices.Evaluate(SkillKind.Attack, true, preview, target, 3, party, 1, null);
            Assert.Equal(0f, eval.Score);
        }

        [Fact]
        public void Chip_hp_is_three_or_less()
        {
            Assert.True(TokenPrices.IsChipHp(1f));
            Assert.True(TokenPrices.IsChipHp(3f));
            Assert.False(TokenPrices.IsChipHp(3.1f));
            Assert.False(TokenPrices.IsChipHp(0f));
        }

        [Fact]
        public void Applying_combo_after_the_spender_already_acted_is_zero()
        {
            var preview = new PreviewScore { Ok = true };
            preview.ApplyTarget.Add("combo");
            var target = new TargetInfo { Hp = 20f };
            var party = new PartyKit();
            party.PartySpendsCombo = true;
            party.Heroes.Add(new HeroKit { Guid = 2, Living = true, SpendsCombo = true });
            CombatMemory.NoteComboSpenderActed(2);
            var eval = TokenPrices.Evaluate(SkillKind.Attack, true, preview, target, 3, party, 1, null);
            Assert.Equal(0f, eval.Score);
        }

        [Fact]
        public void Early_setup_is_rounds_1_and_2_with_a_pack()
        {
            Assert.False(TokenPrices.IsEarlySetup(0, 3));
            Assert.True(TokenPrices.IsEarlySetup(1, 3));
            Assert.True(TokenPrices.IsEarlySetup(2, 4));
            Assert.False(TokenPrices.IsEarlySetup(3, 4));
            Assert.False(TokenPrices.IsEarlySetup(1, 1));
            Assert.Equal(1.5f, TokenPrices.EarlySetupScale(1, 3));
            Assert.Equal(1f, TokenPrices.EarlySetupScale(3, 3));
        }

        [Fact]
        public void Early_round_pays_more_to_apply_combo()
        {
            CombatMemory.NoteRound(1);
            var preview = new PreviewScore { Ok = true };
            preview.ApplyTarget.Add("combo");
            var target = new TargetInfo { Hp = 20f };
            var party = new PartyKit();
            party.PartySpendsCombo = true;
            party.Heroes.Add(new HeroKit { Guid = 2, Living = true, SpendsCombo = true });
            var eval = TokenPrices.Evaluate(SkillKind.Attack, true, preview, target, 3, party, 1, null);
            Assert.Equal(21f, eval.Score);
        }
    }
}
