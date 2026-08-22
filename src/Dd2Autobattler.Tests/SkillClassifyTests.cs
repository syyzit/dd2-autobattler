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
    }
}
