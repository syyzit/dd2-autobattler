using System;

namespace Dd2Autobattler.Combat
{
    // In-combat party facts: Combo must be spendable on this rank, and two
    // frontliners must not steal rank 0 from each other. Harvest hunger walks
    // are not gated here (ApplyHarvestHungerGuard still pays +80).
    internal static class PartySynergy
    {
        public const int ComboRanksUnknown = -1;

        internal static bool PrefersFront(string classId)
        {
            return KitSafety.IdHas(classId, "hellion")
                   || KitSafety.IdHas(classId, "leper")
                   || KitSafety.IdHas(classId, "flagellant")
                   || KitSafety.IdHas(classId, "man_at_arms");
        }

        internal static bool IsFrontWalk(string skillId)
        {
            return KitSafety.IdHas(skillId, "toe_to_toe")
                   || KitSafety.IdHas(skillId, "hold_the_line")
                   || KitSafety.IdHas(skillId, "rampart");
        }

        internal static bool FrontOccupiedByOther(PartyKit party, uint performerGuid)
        {
            if (party == null)
                return false;
            for (var i = 0; i < party.Heroes.Count; i++)
            {
                var hero = party.Heroes[i];
                if (hero == null || !hero.Living || hero.Guid == performerGuid)
                    continue;
                if (hero.Rank == 0 && PrefersFront(hero.ClassId))
                    return true;
            }
            return false;
        }

        // Fail open when ComboHitRanks is unknown so the playtested four still mark.
        internal static bool HitsRankMask(int attackHitRanks, int enemyRank)
        {
            if (attackHitRanks == ComboRanksUnknown || enemyRank < 0 || enemyRank >= 8)
                return false;
            return (attackHitRanks & (1 << enemyRank)) != 0;
        }

        internal static bool FollowUpHitsRank(PartyKit party, uint performerGuid, int enemyRank)
        {
            if (party == null || !party.PartySpendsCombo)
                return false;
            for (var i = 0; i < party.Heroes.Count; i++)
            {
                var hero = party.Heroes[i];
                if (hero == null || !hero.Living || !hero.SpendsCombo || hero.Guid == performerGuid)
                    continue;
                if (CombatMemory.ComboSpenderActedThisRound(hero.Guid))
                    continue;
                if (hero.ComboHitRanks == ComboRanksUnknown)
                    return true;
                if (enemyRank >= 0 && enemyRank < 8 && (hero.ComboHitRanks & (1 << enemyRank)) != 0)
                    return true;
            }
            return false;
        }

        internal static float FrontWalkDelta(string skillId, int performerRank, bool frontOccupied)
        {
            if (!IsFrontWalk(skillId))
                return 0f;
            if (frontOccupied)
                return -24f;
            if (performerRank >= 1)
                return 18f;
            return 0f;
        }

        // Duelist's Advance swaps with the hero in front. Acid Rain launches from
        // ranks 0–1 only — shoving that ally to rank 2+ wastes the blight.
        internal static float AdvanceDisplaceDelta(string skillId, int performerRank, PartyKit party)
        {
            if (party == null || performerRank < 2)
                return 0f;
            if (!KitSafety.IdHas(skillId, "duelists_advance"))
                return 0f;
            for (var i = 0; i < party.Heroes.Count; i++)
            {
                var hero = party.Heroes[i];
                if (hero == null || !hero.Living || !hero.AcidRain)
                    continue;
                if (hero.Rank == performerRank - 1)
                    return -48f;
            }
            return 0f;
        }

        internal static bool IsPull(string skillId)
        {
            return KitSafety.IdHas(skillId, "pull") || KitSafety.IdHas(skillId, "manacles");
        }

        internal static bool IsKnock(string skillId)
        {
            return KitSafety.IdHas(skillId, "rampart")
                   || KitSafety.IdHas(skillId, "cuff")
                   || KitSafety.IdHas(skillId, "knockback")
                   || KitSafety.IdHas(skillId, "stagger");
        }

        // Fail open (true) when no attacker published ranks, so we do not
        // invent pulls on the playtested four.
        internal static bool PartyHitsRank(PartyKit party, int enemyRank)
        {
            if (party == null || enemyRank < 0 || enemyRank > 7)
                return true;
            var anyKnown = false;
            for (var i = 0; i < party.Heroes.Count; i++)
            {
                var hero = party.Heroes[i];
                if (hero == null || !hero.Living || !hero.Attacks)
                    continue;
                if (hero.AttackHitRanks == ComboRanksUnknown)
                    continue;
                anyKnown = true;
                if ((hero.AttackHitRanks & (1 << enemyRank)) != 0)
                    return true;
            }
            return !anyKnown;
        }

        // Pull lowers enemy rank (toward the party). Knockback raises it.
        // Pay only when the destination is hittable and the current tile is not.
        internal static float MoveDelta(string skillId, int targetRank, bool enemyTarget, bool corpse, PartyKit party, float moveLand)
        {
            if (!enemyTarget || corpse || party == null)
                return 0f;
            if (moveLand <= 0.35f && (IsPull(skillId) || IsKnock(skillId)))
                return -8f;
            var dest = -1;
            if (IsPull(skillId))
                dest = targetRank - 1;
            else if (IsKnock(skillId))
                dest = targetRank + 1;
            else
                return 0f;
            if (dest < 0 || dest > 3)
                return 0f;
            if (PartyHitsRank(party, targetRank) || !PartyHitsRank(party, dest))
                return 0f;
            return 28f * moveLand;
        }

        // A corpse closer to the party than every living enemy is clogging the front.
        internal static bool CorpseClogsRanks(int corpseRank, int[] livingRanks)
        {
            if (livingRanks == null || livingRanks.Length == 0)
                return false;
            var min = livingRanks[0];
            for (var i = 1; i < livingRanks.Length; i++)
            {
                if (livingRanks[i] < min)
                    min = livingRanks[i];
            }
            return corpseRank < min;
        }
    }
}
