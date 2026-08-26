using System;
using System.Collections.Generic;
using Assets.Code.Combat;
using Dd2Autobattler.Logging;
using Newtonsoft.Json.Linq;

namespace Dd2Autobattler.Combat
{
    /// <summary>
    /// Per-fight stall budget and "hands off" for scripted shrine/story fights.
    /// </summary>
    internal static class CombatMemory
    {
        private static uint _lastEnemyGuid;
        private static bool _setupSpent;
        private static uint _itemActorGuid;
        private static int _itemsUsedThisActorTurn;
        private static int _round;
        private static int _taprootHitsThisRound;
        private static readonly HashSet<uint> _comboSpendersActed = new HashSet<uint>();
        private static readonly HashSet<uint> _crisisHealsThisRound = new HashSet<uint>();
        private static readonly HashSet<uint> _reachWalkedThisRound = new HashSet<uint>();

        public static bool HandsOff { get; private set; }
        public static string HandsOffReason { get; private set; }
        public static bool InCombat { get; private set; }
        public static int Round { get { return _round; } }
        public static int TaprootHitsThisRound { get { return _taprootHitsThisRound; } }
        public static uint ShadowActor { get; private set; }
        public static ChosenAction ShadowBot { get; private set; }
        public static List<JObject> ShadowLegal { get; private set; }

        public static void ResetFight()
        {
            _lastEnemyGuid = 0;
            _setupSpent = false;
            _itemActorGuid = 0;
            _itemsUsedThisActorTurn = 0;
            _round = 0;
            _taprootHitsThisRound = 0;
            _comboSpendersActed.Clear();
            _crisisHealsThisRound.Clear();
            _reachWalkedThisRound.Clear();
            HandsOff = false;
            HandsOffReason = null;
            InCombat = false;
            ClearShadow();
        }

        public static void NoteRound(int round)
        {
            if (round > 0)
                _round = round;
            else
                _round++;
            _taprootHitsThisRound = 0;
            _comboSpendersActed.Clear();
            _crisisHealsThisRound.Clear();
            _reachWalkedThisRound.Clear();
            ClearShadow();
        }

        public static void NoteShadowPick(uint actorGuid, ChosenAction bot, List<JObject> legal)
        {
            ShadowActor = actorGuid;
            ShadowBot = bot;
            ShadowLegal = legal;
        }

        public static void ClearShadow()
        {
            ShadowActor = 0;
            ShadowBot = null;
            ShadowLegal = null;
        }

        public static void NoteReachWalk(uint actorGuid)
        {
            if (actorGuid != 0)
                _reachWalkedThisRound.Add(actorGuid);
        }

        public static bool ReachWalkedThisRound(uint actorGuid)
        {
            return actorGuid != 0 && _reachWalkedThisRound.Contains(actorGuid);
        }

        public static bool PartyReachWalkedThisRound()
        {
            return _reachWalkedThisRound.Count > 0;
        }

        public static void NoteComboSpenderActed(uint actorGuid)
        {
            if (actorGuid != 0)
                _comboSpendersActed.Add(actorGuid);
        }

        public static bool ComboSpenderActedThisRound(uint actorGuid)
        {
            return actorGuid != 0 && _comboSpendersActed.Contains(actorGuid);
        }

        public static void NoteCrisisHeal(uint targetGuid)
        {
            if (targetGuid != 0)
                _crisisHealsThisRound.Add(targetGuid);
        }

        public static bool CrisisHealThisRound(uint targetGuid)
        {
            return targetGuid != 0 && _crisisHealsThisRound.Contains(targetGuid);
        }

        public static void NoteTaprootHit()
        {
            _taprootHitsThisRound++;
        }

        public static void BeginBattle(string fightId, CombatSource source)
        {
            ResetFight();
            InCombat = true;
            HandsOff = IsScriptedStoryFight(fightId, source);
            if (HandsOff)
            {
                HandsOffReason = "story/shrine: " + (fightId ?? "?");
                DecisionLog.Info("Hands off - " + HandsOffReason);
            }
        }

        public static bool IsScriptedStoryFight(string fightId, CombatSource source)
        {
            try
            {
                if (source != null && source == CombatSource.STORY_HERO)
                    return true;
            }
            catch { }

            if (string.IsNullOrEmpty(fightId))
                return false;
            return fightId.IndexOf("chapter", StringComparison.OrdinalIgnoreCase) >= 0
                   || fightId.IndexOf("herostory", StringComparison.OrdinalIgnoreCase) >= 0
                   || fightId.IndexOf("shrine", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool CanSpendSetup(uint lastEnemyGuid)
        {
            if (lastEnemyGuid == 0)
                return false;
            if (lastEnemyGuid != _lastEnemyGuid)
            {
                _lastEnemyGuid = lastEnemyGuid;
                _setupSpent = false;
            }
            return !_setupSpent;
        }

        public static void NoteChosen(uint lastEnemyGuid, bool wasSetup)
        {
            if (lastEnemyGuid == 0)
                return;
            if (lastEnemyGuid != _lastEnemyGuid)
            {
                _lastEnemyGuid = lastEnemyGuid;
                _setupSpent = false;
            }
            if (wasSetup)
                _setupSpent = true;
        }

        public static bool CanSpendItem(uint actorGuid, bool crisis)
        {
            if (actorGuid == 0)
                return false;
            if (actorGuid != _itemActorGuid)
            {
                _itemActorGuid = actorGuid;
                _itemsUsedThisActorTurn = 0;
            }
            if (_itemsUsedThisActorTurn < 1)
                return true;
            return crisis && _itemsUsedThisActorTurn < 2;
        }

        public static void NoteItemUsed(uint actorGuid)
        {
            if (actorGuid == 0)
                return;
            if (actorGuid != _itemActorGuid)
            {
                _itemActorGuid = actorGuid;
                _itemsUsedThisActorTurn = 0;
            }
            _itemsUsedThisActorTurn++;
        }
    }
}
