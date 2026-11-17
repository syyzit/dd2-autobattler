using System.Collections.Generic;

namespace Dd2Autobattler.Combat
{
    /// <summary>
    /// Plain-data per-fight memory the decider reads (round, once-per-round
    /// gates, setup and item budgets). CombatMemory holds the live copy; a
    /// TurnSnapshot carries a clone so a turn can be re-decided offline.
    /// Methods keep the exact semantics CombatMemory had (including the
    /// probe side effects of CanSpendSetup / CanSpendItem).
    /// </summary>
    internal sealed class MemoryState
    {
        public uint LastEnemyGuid;
        public bool SetupSpent;
        public uint ItemActorGuid;
        public int ItemsUsedThisActorTurn;
        public int Round;
        public int TaprootHitsThisRound;
        public HashSet<uint> ComboSpendersActed = new HashSet<uint>();
        public HashSet<uint> CrisisHealsThisRound = new HashSet<uint>();
        public HashSet<uint> ReachWalkedThisRound = new HashSet<uint>();

        public MemoryState Clone()
        {
            return new MemoryState
            {
                LastEnemyGuid = LastEnemyGuid,
                SetupSpent = SetupSpent,
                ItemActorGuid = ItemActorGuid,
                ItemsUsedThisActorTurn = ItemsUsedThisActorTurn,
                Round = Round,
                TaprootHitsThisRound = TaprootHitsThisRound,
                ComboSpendersActed = new HashSet<uint>(ComboSpendersActed ?? new HashSet<uint>()),
                CrisisHealsThisRound = new HashSet<uint>(CrisisHealsThisRound ?? new HashSet<uint>()),
                ReachWalkedThisRound = new HashSet<uint>(ReachWalkedThisRound ?? new HashSet<uint>())
            };
        }

        public void ResetFight()
        {
            LastEnemyGuid = 0;
            SetupSpent = false;
            ItemActorGuid = 0;
            ItemsUsedThisActorTurn = 0;
            Round = 0;
            TaprootHitsThisRound = 0;
            ComboSpendersActed.Clear();
            CrisisHealsThisRound.Clear();
            ReachWalkedThisRound.Clear();
        }

        public void NoteRound(int round)
        {
            if (round > 0)
                Round = round;
            else
                Round++;
            TaprootHitsThisRound = 0;
            ComboSpendersActed.Clear();
            CrisisHealsThisRound.Clear();
            ReachWalkedThisRound.Clear();
        }

        public void NoteReachWalk(uint actorGuid)
        {
            if (actorGuid != 0)
                ReachWalkedThisRound.Add(actorGuid);
        }

        public bool ReachWalked(uint actorGuid)
        {
            return actorGuid != 0 && ReachWalkedThisRound.Contains(actorGuid);
        }

        public bool PartyReachWalked()
        {
            return ReachWalkedThisRound.Count > 0;
        }

        public void NoteComboSpenderActed(uint actorGuid)
        {
            if (actorGuid != 0)
                ComboSpendersActed.Add(actorGuid);
        }

        public bool ComboSpenderActed(uint actorGuid)
        {
            return actorGuid != 0 && ComboSpendersActed.Contains(actorGuid);
        }

        public void NoteCrisisHeal(uint targetGuid)
        {
            if (targetGuid != 0)
                CrisisHealsThisRound.Add(targetGuid);
        }

        public bool CrisisHealed(uint targetGuid)
        {
            return targetGuid != 0 && CrisisHealsThisRound.Contains(targetGuid);
        }

        public void NoteTaprootHit()
        {
            TaprootHitsThisRound++;
        }

        // Probe: switching to a new last enemy resets the setup budget, even
        // on a shadow turn. CombatMemory had the same side effect.
        public bool CanSpendSetup(uint lastEnemyGuid)
        {
            if (lastEnemyGuid == 0)
                return false;
            if (lastEnemyGuid != LastEnemyGuid)
            {
                LastEnemyGuid = lastEnemyGuid;
                SetupSpent = false;
            }
            return !SetupSpent;
        }

        public void NoteChosen(uint lastEnemyGuid, bool wasSetup)
        {
            if (lastEnemyGuid == 0)
                return;
            if (lastEnemyGuid != LastEnemyGuid)
            {
                LastEnemyGuid = lastEnemyGuid;
                SetupSpent = false;
            }
            if (wasSetup)
                SetupSpent = true;
        }

        // Probe: a new actor resets the per-actor item counter.
        public bool CanSpendItem(uint actorGuid, bool crisis)
        {
            if (actorGuid == 0)
                return false;
            if (actorGuid != ItemActorGuid)
            {
                ItemActorGuid = actorGuid;
                ItemsUsedThisActorTurn = 0;
            }
            if (ItemsUsedThisActorTurn < 1)
                return true;
            return crisis && ItemsUsedThisActorTurn < 2;
        }

        public void NoteItemUsed(uint actorGuid)
        {
            if (actorGuid == 0)
                return;
            if (actorGuid != ItemActorGuid)
            {
                ItemActorGuid = actorGuid;
                ItemsUsedThisActorTurn = 0;
            }
            ItemsUsedThisActorTurn++;
        }
    }
}
