using System.Collections.Generic;
using System.IO;
using Dd2Autobattler.Combat;
using Xunit;
using ScoredAction = Dd2Autobattler.Combat.TurnDecider.ScoredAction;

namespace Dd2Autobattler.Tests
{
    // CSV pass_stress: performer_effects stress_damage_2. The pass is the
    // last resort: any free click replaces it (TurnDecider.FreeOverPass).
    public class PassLastResortTests
    {
        private const uint Maa = 1, Hwm = 2, Gr = 3, Pd = 4, Foe = 10, Foe2 = 11, Body = 12;

        private static TargetInfo Hero(uint guid, string cls, int rank, float hp, float max, float stress)
        {
            return new TargetInfo { Guid = guid, ClassId = cls, Team = 0, Rank = rank, Living = true, Hp = hp, HpMax = max, HpPct = hp / max, Stress = stress };
        }

        private static TargetInfo Enemy(uint guid, string cls, int rank, float hp = 10f, bool corpse = false)
        {
            return new TargetInfo { Guid = guid, ClassId = cls, Team = 1, Rank = rank, Living = !corpse, Corpse = corpse, Hp = hp, HpMax = 15f, HpPct = hp / 15f };
        }

        private static TurnSnapshot Snap(uint performer, params TargetInfo[] enemies)
        {
            var snap = new TurnSnapshot();
            snap.Heroes.Add(Hero(Maa, "man_at_arms", 0, 30f, 40f, 3f));
            snap.Heroes.Add(Hero(Hwm, "highwayman", 1, 30f, 35f, 2f));
            snap.Heroes.Add(Hero(Gr, "grave_robber", 2, 25f, 29f, 1f));
            snap.Heroes.Add(Hero(Pd, "plague_doctor", 3, 25f, 30f, 4f));
            foreach (var e in enemies)
                snap.Enemies.Add(e);
            snap.PerformerGuid = performer;
            snap.Performer = snap.Find(performer);
            return snap;
        }

        private static ScoredAction Act(string skill, SkillKind kind, TargetInfo target, float score, float dmg = 0f, bool kills = false)
        {
            return new ScoredAction
            {
                SkillId = skill,
                TargetGuid = target.Guid,
                Kind = kind,
                EnemyTarget = target.Team == 1,
                Target = target,
                Preview = new PreviewScore { Ok = true, Damage = dmg, Kills = kills },
                Score = score
            };
        }

        private static ScoredAction Pass(TurnSnapshot snap)
        {
            return Act("pass_stress", SkillKind.Pass, snap.Performer, -50f);
        }

        private static ScoredAction Decide(TurnSnapshot snap, ScoredAction picked, List<ScoredAction> all, EnemyFocus focus = null)
        {
            return TurnDecider.FreeOverPass(picked, all, focus ?? new EnemyFocus(), snap, snap.Performer, 1);
        }

        [Fact]
        public void FreeGuardBeatsThePass()
        {
            var foe = Enemy(Foe, "cultist_herald", 3);
            var snap = Snap(Maa, foe);
            var pass = Pass(snap);
            var guard = Act("maa_defender", SkillKind.Support, snap.Find(Pd), -110f);
            var pick = Decide(snap, pass, new List<ScoredAction> { pass, guard });
            Assert.Same(guard, pick);
            Assert.Equal("over_pass_support", pick.NoteReason);
        }

        [Fact]
        public void TierOrderGuardLowAllyThenHitThenCorpseThenSupportThenMark()
        {
            var front = Enemy(Body, "cultist_evangelist_corpse", 0, 4f, corpse: true);
            var add = Enemy(Foe, "lost_battalion_foot_soldier", 1);
            var far = Enemy(Foe2, "cultist_herald", 3);
            var snap = Snap(Pd, front, add, far);
            var pass = Pass(snap);
            var mark = Act("pd_blinding_gas", SkillKind.Attack, far, 13f);
            var buff = Act("pd_ounce_of_prevention", SkillKind.Support, snap.Find(Maa), -78f);
            var corpse = Act("pd_noxious_blast_u", SkillKind.Attack, front, -250f, dmg: 4.5f);
            var chip = Act("pd_noxious_blast_u", SkillKind.Attack, add, -86f, dmg: 1f);
            var all = new List<ScoredAction> { pass, mark, buff, corpse, chip };
            Assert.Same(chip, Decide(snap, pass, all));
            all.Remove(chip);
            Assert.Same(corpse, Decide(snap, pass, all));
            Assert.Equal("over_pass_corpse", corpse.NoteReason);
            all.Remove(corpse);
            Assert.Same(buff, Decide(snap, pass, all));
            all.Remove(buff);
            Assert.Same(mark, Decide(snap, pass, all));

            // An ally at 35% or less: the guard on that ally comes first.
            var low = snap.Find(Hwm);
            low.Hp = 5f; low.HpPct = 5f / 35f;
            var guard = Act("maa_defender", SkillKind.Support, low, -66f);
            var bolster = Act("maa_bolster", SkillKind.Support, snap.Find(Pd), -54f);
            snap.PerformerGuid = Maa;
            snap.Performer = snap.Find(Maa);
            var maaChip = Act("maa_crush_u", SkillKind.Attack, add, -87f, dmg: 6f);
            var pick = Decide(snap, pass, new List<ScoredAction> { pass, bolster, maaChip, guard });
            Assert.Same(guard, pick);
            Assert.Equal("over_pass_guard", pick.NoteReason);
        }

        [Fact]
        public void CorpseChipOnlyWhenItBlocksAndRanksLast()
        {
            var body = Enemy(Body, "shared_lost_soul_yeoman_corpse", 0, 17f, corpse: true);
            var widow = Enemy(Foe, "shared_lost_soul_widow", 1);
            var snap = Snap(Pd, body, widow);
            var pass = Pass(snap);
            var partial = Act("pd_incision", SkillKind.Attack, body, -250f, dmg: 5.5f);
            Assert.Same(partial, Decide(snap, pass, new List<ScoredAction> { pass, partial }));
            Assert.Equal("over_pass_corpse_chip", partial.NoteReason);
            var mark = Act("pd_blinding_gas", SkillKind.Attack, widow, -60f);
            Assert.Same(mark, Decide(snap, pass, new List<ScoredAction> { pass, partial, mark }));

            // Nothing living behind it: hitting it changes no rank.
            var behind = Enemy(Body, "shared_lost_soul_yeoman_corpse", 2, 3f, corpse: true);
            var s2 = Snap(Pd, Enemy(Foe, "shared_lost_soul_widow", 0), behind);
            var p2 = Pass(s2);
            var waste = Act("pd_incision", SkillKind.Attack, behind, -250f, dmg: 5.5f);
            Assert.Same(p2, Decide(s2, p2, new List<ScoredAction> { p2, waste }));
        }

        [Fact]
        public void HarmfulClicksLeaveThePass()
        {
            var foe = Enemy(Foe, "lost_battalion_arbalist", 2);
            var snap = Snap(Hwm, foe);
            var pass = Pass(snap);
            var move = Act("hwm_move", SkillKind.Move, snap.Find(Maa), -1000f);
            var advance = Act("hwm_duelists_advance", SkillKind.Attack, foe, -60f, dmg: 4f);
            var cursed = Act("hwm_wicked_slice_u", SkillKind.Attack, foe, -52f, dmg: 7f);
            cursed.Cursed = true;
            var item = Act("pyro_bomb", SkillKind.Attack, foe, -40f, dmg: 6f);
            item.IsItem = true;
            var rest = Act("pass_heal", SkillKind.Heal, snap.Performer, -30f);
            var all = new List<ScoredAction> { pass, move, advance, cursed, item, rest };
            Assert.Same(pass, Decide(snap, pass, all));

            // Riposte, stealth, Taproot.
            var rip = Enemy(Foe2, "lost_battalion_knight", 1);
            rip.Riposte = true;
            var hide = Enemy(Body, "cultist_altar", 0);
            hide.Stealth = true;
            var root = Enemy(20, "boss_dreaming_general_taproot", 3);
            root.Healthless = true;
            var s2 = Snap(Gr, rip, hide, root);
            var p2 = Pass(s2);
            var all2 = new List<ScoredAction>
            {
                p2,
                Act("gr_pick_to_the_face", SkillKind.Attack, rip, -90f, dmg: 6f),
                Act("gr_thrown_dagger", SkillKind.Attack, hide, -200f, dmg: 5f),
                Act("gr_thrown_dagger", SkillKind.Attack, root, -10f),
                Act("gr_artemisia", SkillKind.Heal, s2.Performer, -20f)
            };
            Assert.Same(p2, Decide(s2, p2, all2));
            // A Riposte target that dies to this hit is fine.
            var kill = Act("gr_pick_to_the_face", SkillKind.Attack, rip, -90f, dmg: 12f, kills: true);
            all2.Add(kill);
            Assert.Same(kill, Decide(s2, p2, all2));
        }

        [Fact]
        public void LimitedUseSkillsAreKept()
        {
            Assert.True(TurnDecider.HasUseLimit("pd_battlefield_medicine_u"));
            Assert.True(TurnDecider.HasUseLimit("gr_artemisia"));
            Assert.True(TurnDecider.HasUseLimit("gr_dead_of_night"));
            Assert.False(TurnDecider.HasUseLimit("pd_ounce_of_prevention"));
            Assert.False(TurnDecider.HasUseLimit("maa_bolster"));
            var snap = Snap(Pd, Enemy(Foe, "x", 3));
            var pass = Pass(snap);
            var bm = Act("pd_battlefield_medicine", SkillKind.Heal, snap.Find(Maa), -25f);
            Assert.Same(pass, Decide(snap, pass, new List<ScoredAction> { pass, bm }));
        }

        [Fact]
        public void SelfMoversOnlyFromTheEndTheyMoveTo()
        {
            Assert.False(TurnDecider.MovesPerformer("maa_rampart", Hero(Maa, "man_at_arms", 0, 30f, 40f, 0f)));
            Assert.True(TurnDecider.MovesPerformer("maa_rampart", Hero(Maa, "man_at_arms", 1, 30f, 40f, 0f)));
            Assert.True(TurnDecider.MovesPerformer("hwm_point_blank_shot", Hero(Hwm, "highwayman", 0, 30f, 40f, 0f)));
            Assert.False(TurnDecider.MovesPerformer("hwm_point_blank_shot", Hero(Hwm, "highwayman", 3, 30f, 40f, 0f)));
            Assert.False(TurnDecider.MovesPerformer("hwm_wicked_slice", Hero(Hwm, "highwayman", 1, 30f, 40f, 0f)));
        }

        [Fact]
        public void LibrarianStacksAndDeferredBossKillsLeaveThePass()
        {
            var stack = Enemy(Foe, "fanatic_librarian_stack_m", 0, 15f);
            var lib = Enemy(Foe2, "fanatic_librarian", 2, 80f);
            var snap = Snap(Maa, stack, lib);
            var focus = new EnemyFocus();
            focus.Enemies.Add(new EnemyThreat { Guid = Foe, ClassId = stack.ClassId, Add = true, Defer = true });
            focus.Enemies.Add(new EnemyThreat { Guid = Foe2, ClassId = lib.ClassId, Boss = true, MustKillFirst = true });
            var pass = Pass(snap);
            var punch = Act("maa_crush_u", SkillKind.Attack, stack, -121f, dmg: 6f);
            Assert.Same(pass, Decide(snap, pass, new List<ScoredAction> { pass, punch }, focus));

            // Denial: a chip on a deferred lock is fine, finishing it is not.
            var lockHp = Enemy(Foe, "boss_brain_lock_health", 0, 4f);
            var s2 = Snap(Gr, lockHp);
            var f2 = new EnemyFocus { DenialLocks = true };
            f2.Enemies.Add(new EnemyThreat { Guid = Foe, ClassId = lockHp.ClassId, Boss = true, Defer = true });
            var p2 = Pass(s2);
            var finish = Act("gr_pick_to_the_face_u", SkillKind.Attack, lockHp, -56f, dmg: 6f, kills: true);
            Assert.Same(p2, Decide(s2, p2, new List<ScoredAction> { p2, finish }, f2));
            var chip = Act("gr_poison_dart", SkillKind.Attack, lockHp, -60f, dmg: 2f);
            Assert.Same(chip, Decide(s2, p2, new List<ScoredAction> { p2, finish, chip }, f2));
        }

        [Fact]
        public void OnlyThePlainPassIsReplaced()
        {
            var foe = Enemy(Foe, "x", 1);
            var snap = Snap(Maa, foe);
            var guard = Act("maa_defender", SkillKind.Support, snap.Find(Pd), -110f);
            var rest = Act("pass_heal", SkillKind.Heal, snap.Performer, 200f);
            rest.Preview.Heal = 8f;
            Assert.Same(rest, Decide(snap, rest, new List<ScoredAction> { rest, guard }));
            var swing = Act("maa_crush", SkillKind.Attack, foe, 20f, dmg: 6f);
            Assert.Same(swing, Decide(snap, swing, new List<ScoredAction> { swing, guard }));
            Assert.Null(TurnDecider.FreeOverPass(null, new List<ScoredAction>(), null, snap, snap.Performer, 1));
        }

        // The replacement is a cited gate: the round search abstains on it.
        [Fact]
        public void ReplacementAbstainsFromSearch()
        {
            var dir = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Golden");
            var g = Replay.GoldenCase.Load(Path.Combine(dir, "over_pass_mark_not_pass.json"));
            var r = TurnDecider.DecideSnapshot(g.Snap);
            Assert.Equal("over_pass", r.Trace.Gate);
            Assert.Equal("over_pass_mark", r.Reason);
        }
    }
}
