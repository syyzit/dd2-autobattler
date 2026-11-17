using System;
using System.Collections.Generic;
using System.IO;
using Dd2Autobattler.Combat;
using Dd2Autobattler.Search;
using Xunit;

namespace Dd2Autobattler.Tests
{
    public class ForwardModelTests
    {
        // Tiny CSV fixture in the game's export format.
        internal static GameData Fixture()
        {
            var dir = Path.Combine(Path.GetTempPath(), "dd2_fm_fixture_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "monster_data_export.Group.csv"), string.Join("\n",
                "element_start,grub,ActorDataClass", "m_Size,1,", "element_end",
                "element_start,grub,ActorDataStats", "key_map,health_max,speed,", "add_stats,10,3,", "sub_stat,resistance,stun,0.5,", "element_end",
                "element_start,grub_corpse,ActorDataClass", "m_Size,1,", "element_end",
                "element_start,grub_corpse,ActorDataStats", "key_map,health_max,", "add_stats,6,", "element_end",
                "element_start,cart,ActorDataClass", "m_Size,2,", "element_end",
                "element_start,cart,ActorDataStats", "key_map,health_max,", "add_stats,40,", "sub_stat,resistance,death,0,", "element_end",
                "element_start,grub_bite,ActorDataSkill", "m_IsFriendly,False,", "launch_ranks,1,2,", "target_ranks,1,2,", "m_IsMultiHit,False,", "element_end",
                "element_start,grub_bite,ActorDataStats", "key_map,health_damage,health_damage_range,crit_chance,", "add_stats,2,2,0,", "element_end",
                "element_start,grub_bite,ActorDataEffects", "target_effects,small_bleed,", "element_end",
                "element_start,grub_spit,ActorDataSkill", "m_IsFriendly,False,", "launch_ranks,3,4,", "target_ranks,1,2,3,4,", "m_IsMultiHit,True,", "element_end",
                "element_start,grub_spit,ActorDataStats", "key_map,health_damage,health_damage_range,crit_chance,", "add_stats,1,0,0,", "element_end",
                ""));
            File.WriteAllText(Path.Combine(dir, "effect_data_export.Group.csv"), string.Join("\n",
                "element_start,small_bleed,Effect", "m_Chance,1,", "m_DotAddId,bleed_dot,", "m_DotAddAmount,1,", "element_end",
                "element_start,push_1,Effect", "m_Chance,1,", "m_IgnoreResist,True,", "m_Move,1,", "element_end",
                ""));
            File.WriteAllText(Path.Combine(dir, "dots_data_export.Group.csv"), string.Join("\n",
                "element_start,bleed_dot,Dot", "m_Type,bleed,", "m_DurationAmount,3,", "effects,health_damage_2,", "element_end", ""));
            File.WriteAllText(Path.Combine(dir, "hero_data_export.Group.csv"), string.Join("\n",
                "element_start,hero_a,ActorDataClass", "m_Size,1,", "element_end",
                "element_start,hero_a,ActorDataStats", "key_map,health_max,", "add_stats,30,", "sub_stat,resistance,death,0.6,", "element_end",
                "element_start,hero_slash,ActorDataSkill", "m_IsFriendly,False,", "launch_ranks,1,2,", "target_ranks,1,2,", "element_end",
                "element_start,hero_slash,ActorDataStats", "key_map,health_damage,health_damage_range,crit_chance,", "add_stats,4,2,0,", "element_end",
                "element_start,hero_poke,ActorDataSkill", "m_IsFriendly,False,", "launch_ranks,1,2,3,4,", "target_ranks,1,2,3,4,", "element_end",
                "element_start,hero_poke,ActorDataStats", "key_map,health_damage,health_damage_range,crit_chance,", "add_stats,2,1,0,", "element_end",
                ""));
            File.WriteAllText(Path.Combine(dir, "brute_data_export.Group.csv"), string.Join("\n",
                "element_start,brute,ActorDataClass", "m_Size,1,", "element_end",
                "element_start,brute,ActorDataStats", "key_map,health_max,speed,", "add_stats,20,2,", "element_end",
                "element_start,brute_smash,ActorDataSkill", "m_IsFriendly,False,", "launch_ranks,1,2,", "target_ranks,1,2,", "m_IsMultiHit,False,", "element_end",
                "element_start,brute_smash,ActorDataStats", "key_map,health_damage,health_damage_range,crit_chance,", "add_stats,8,2,0,", "element_end",
                ""));
            var data = GameData.Load(dir);
            try { Directory.Delete(dir, true); } catch { }
            return data;
        }

        private static TargetInfo Hero(uint guid, int rank, float hp, float max = 30f)
        {
            return new TargetInfo { Guid = guid, Team = 0, Rank = rank, Hp = hp, HpMax = max, HpPct = hp / max, Living = true, Size = 1, ClassId = "hero_a", Tokens = new List<string>(), SkillIds = new List<string> { "hero_slash" } };
        }

        private static TargetInfo Grub(uint guid, int rank, float hp)
        {
            return new TargetInfo { Guid = guid, Team = 1, Rank = rank, Hp = hp, HpMax = 10f, HpPct = hp / 10f, Living = true, Size = 1, ClassId = "grub", Tokens = new List<string>(), SkillIds = new List<string> { "grub_bite", "grub_spit" } };
        }

        private static TurnSnapshot Snap(params TargetInfo[] actors)
        {
            var s = new TurnSnapshot { Schema = 2 };
            foreach (var a in actors)
            {
                if (a.Team == 0)
                    s.Heroes.Add(a);
                else
                    s.Enemies.Add(a);
            }
            s.PerformerGuid = s.Heroes.Count > 0 ? s.Heroes[0].Guid : 0;
            s.Reindex();
            s.Performer = s.Find(s.PerformerGuid);
            return s;
        }

        private static void Near(float expected, float actual)
        {
            Assert.True(Math.Abs(expected - actual) <= 0.01f, "expected " + expected + " got " + actual);
        }

        [Fact]
        public void GameDataReadsRangesRanksAndCorpses()
        {
            var d = Fixture();
            var bite = d.Skill("grub_bite");
            Assert.Equal(2f, bite.DmgMin);
            Assert.Equal(4f, bite.DmgMax);
            Assert.Equal(0b0011, bite.LaunchMask);
            Assert.True(d.Class("grub").HasCorpse);
            Assert.False(d.Class("cart").HasCorpse);
            Assert.Equal(2, d.Class("cart").Size);
            Assert.Equal(2f, d.DotPerTick(d.Effect("small_bleed")));
            Assert.Contains("grub_bite", d.FileKit("grub"));
        }

        [Theory]
        [InlineData(5f, 2f, 4f, 0f)]
        [InlineData(3f, 2f, 4f, 2f / 3f)]
        [InlineData(2f, 2f, 4f, 1f)]
        [InlineData(4f, 4f, 4f, 1f)]
        [InlineData(4.2f, 4f, 4f, 0f)]
        public void LethalProbabilityOverIntegerRolls(float hp, float lo, float hi, float expected)
        {
            Near(expected, ForwardModel.LethalP(hp, lo, hi));
        }

        [Fact]
        public void SureKillLeavesACorpseInRank()
        {
            var d = Fixture();
            var b = ForwardModel.Build(Snap(Hero(1, 0, 30), Grub(10, 0, 3), Grub(11, 1, 10)), d, null);
            var g = b.Find(10);
            ForwardModel.Damage(b, g, 5f, 1f, 4f, 6f);
            Assert.True(g.Dead);
            Assert.True(g.Corpse);
            Assert.Equal(0, g.Rank);
            Assert.Equal(1, b.Find(11).Rank);
        }

        [Fact]
        public void PartialKillChanceKeepsTheBodyWithConditionalHp()
        {
            var d = Fixture();
            var b = ForwardModel.Build(Snap(Hero(1, 0, 30), Grub(10, 0, 3)), d, null);
            var g = b.Find(10);
            ForwardModel.Damage(b, g, 3f, 1f, 2f, 4f);
            Near(1f / 3f, g.AliveP);
            Assert.True(g.Dead);
            var b2 = ForwardModel.Build(Snap(Hero(1, 0, 30), Grub(10, 0, 5)), d, null);
            var g2 = b2.Find(10);
            ForwardModel.Damage(b2, g2, 3f, 1f, 2f, 4f);
            Near(1f, g2.AliveP);
            Near(2f, g2.Hp);
        }

        [Fact]
        public void NoCorpseClassShiftsRanksUp()
        {
            var d = Fixture();
            var cart = Grub(10, 0, 4);
            cart.ClassId = "cart";
            var b = ForwardModel.Build(Snap(Hero(1, 0, 30), cart, Grub(11, 1, 10)), d, null);
            ForwardModel.Damage(b, b.Find(10), 9f, 1f);
            Assert.Equal(-1, b.Find(10).Rank);
            Assert.Equal(0, b.Find(11).Rank);
        }

        [Fact]
        public void DeathsDoorArmorAbsorbsAHitThenDeathblow()
        {
            var d = Fixture();
            var cart = Grub(10, 0, 0);
            cart.ClassId = "cart";
            cart.DeathsDoor = true;
            cart.DeathArmor = true;
            cart.Tokens.Add("deaths_door_armor");
            var b = ForwardModel.Build(Snap(Hero(1, 0, 30), cart), d, null);
            var c = b.Find(10);
            ForwardModel.Damage(b, c, 6f, 1f, 4f, 8f);
            Assert.False(c.Dead);
            Assert.Equal(0, c.Armor);
            ForwardModel.Damage(b, c, 6f, 1f, 4f, 8f);
            Assert.True(c.Dead);
        }

        [Fact]
        public void HeroGoesToDeathsDoorThenDeathblowUsesResist()
        {
            var d = Fixture();
            var b = ForwardModel.Build(Snap(Hero(1, 0, 3), Grub(10, 0, 10)), d, null);
            var h = b.Find(1);
            ForwardModel.Damage(b, h, 5f, 1f, 5f, 5f);
            Assert.Equal(1f, h.DoorP);
            Assert.Equal(0f, h.Hp);
            ForwardModel.Damage(b, h, 5f, 1f, 5f, 5f);
            Near(0.4f, h.DeathP);
        }

        [Fact]
        public void TurnOrderSkipsCurrentPerformerAndDeadActors()
        {
            var d = Fixture();
            var s = Snap(Hero(1, 0, 30), Grub(10, 0, 1), Grub(11, 1, 10));
            s.RemainingTurns = new List<uint> { 1, 10, 11 };
            var b = ForwardModel.Build(s, d, null);
            Assert.Equal(2, b.QueueLen);
            ForwardModel.Damage(b, b.Find(10), 5f, 1f);
            var next = ForwardModel.NextActor(b);
            Assert.Equal(11u, b.Actors[next].Guid);
            Assert.Equal(-1, ForwardModel.NextActor(b));
        }

        [Fact]
        public void DotTickAtTurnStartCanKillBeforeActing()
        {
            var d = Fixture();
            var g = Grub(10, 0, 2);
            g.NextDot = 3f;
            var b = ForwardModel.Build(Snap(Hero(1, 0, 30), g), d, null);
            var act = ForwardModel.BeginTurn(b, b.IndexOf(10));
            Assert.Equal(0f, act);
            Assert.True(b.Find(10).Dead);
        }

        [Fact]
        public void StunnedEnemySkipsAndStunIsSpent()
        {
            var d = Fixture();
            var g = Grub(10, 0, 10);
            g.Stun = true;
            var b = ForwardModel.Build(Snap(Hero(1, 0, 30), g), d, null);
            Assert.Equal(0f, ForwardModel.BeginTurn(b, b.IndexOf(10)));
            Assert.Equal(0f, b.Find(10).StunP);
        }

        [Fact]
        public void EnemyTurnSpreadsExpectedDamageOverLegalTargets()
        {
            var d = Fixture();
            // Grub at rank 0: only bite (launch 1-2) is usable; heroes in ranks 1-2 are targets.
            var b = ForwardModel.Build(Snap(Hero(1, 0, 30), Hero(2, 1, 30), Hero(3, 2, 30), Grub(10, 0, 10)), d, null);
            ForwardModel.EnemyTurn(b, d, b.IndexOf(10), 1f);
            Near(30f - 1.5f, b.Find(1).Hp);
            Near(30f - 1.5f, b.Find(2).Hp);
            Near(30f, b.Find(3).Hp);
            // Bleed 2 per tick, half the time each (20% default DoT resist).
            Near(0.8f, b.Find(1).Dot);
        }

        [Fact]
        public void TauntNarrowsSingleTargetAttacks()
        {
            var d = Fixture();
            var tank = Hero(2, 1, 30);
            tank.Taunt = true;
            var b = ForwardModel.Build(Snap(Hero(1, 0, 30), tank, Grub(10, 0, 10)), d, null);
            ForwardModel.EnemyTurn(b, d, b.IndexOf(10), 1f);
            Near(30f, b.Find(1).Hp);
            Near(27f, b.Find(2).Hp);
        }

        [Fact]
        public void BlockHalvesAndIsSpent()
        {
            var d = Fixture();
            var h = Hero(1, 0, 30);
            h.Tokens.Add("block");
            var b = ForwardModel.Build(Snap(h, Grub(10, 0, 10)), d, null);
            ForwardModel.EnemyTurn(b, d, b.IndexOf(10), 1f);
            Near(28.5f, b.Find(1).Hp);
            Assert.Equal(0, b.Find(1).Block);
        }

        [Fact]
        public void SlotsCountSizeTwoBodies()
        {
            var d = Fixture();
            var cart = Grub(10, 0, 40);
            cart.ClassId = "cart";
            var b = ForwardModel.Build(Snap(Hero(1, 0, 30), cart, Grub(11, 1, 10)), d, null);
            Assert.Equal(0b0011, ForwardModel.SlotMask(b, b.Find(10)));
            Assert.Equal(0b0100, ForwardModel.SlotMask(b, b.Find(11)));
            // A grub behind the cart stands in slot 3: bite (launch 1-2) is not usable.
            Assert.False(ForwardModel.EnemyCanUse(b, b.Find(11), d.Skill("grub_bite")));
            Assert.True(ForwardModel.EnemyCanUse(b, b.Find(11), d.Skill("grub_spit")));
        }

        [Fact]
        public void PreviewActionAppliesDamageKillAndTokens()
        {
            var d = Fixture();
            var s = Snap(Hero(1, 0, 30), Grub(10, 0, 4), Grub(11, 1, 10));
            var b = ForwardModel.Build(s, d, null);
            var p = new PreviewScore { Ok = true, Damage = 5f, DamageLow = 4f, DamageHigh = 6f, HitChance = 1f };
            ForwardModel.ApplyPreview(b, d, b.IndexOf(1), "hero_slash", 10, p, null);
            Assert.True(b.Find(10).Dead);
            var p2 = new PreviewScore { Ok = true, Damage = 2f, DamageLow = 2f, DamageHigh = 2f, HitChance = 1f };
            p2.ApplyTarget.Add("stun");
            p2.ResistOk = true;
            p2.ResistStun = 0.25f;
            ForwardModel.ApplyPreview(b, d, b.IndexOf(1), "hero_slash", 11, p2, null);
            Near(8f, b.Find(11).Hp);
            Near(0.75f, b.Find(11).StunP);
        }

        [Fact]
        public void BoardCopyIsIndependent()
        {
            var d = Fixture();
            var b = ForwardModel.Build(Snap(Hero(1, 0, 30), Grub(10, 0, 10)), d, null);
            var c = new SimBoard();
            c.CopyFrom(b);
            ForwardModel.Damage(c, c.Find(10), 3f, 1f);
            Assert.Equal(10f, b.Find(10).Hp);
            Assert.Equal(7f, c.Find(10).Hp);
        }
    }
}
