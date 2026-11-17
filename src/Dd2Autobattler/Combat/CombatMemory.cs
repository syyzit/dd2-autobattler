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
        // Decider-visible state. Plain data so a turn snapshot can carry a copy.
        private static MemoryState _state = new MemoryState();

        public static bool HandsOff { get; private set; }
        /// <summary>Set through AutobattlerApi when nobody else will click, so story/shrine fights are played too.</summary>
        public static bool PlayScriptedFights;
        public static string HandsOffReason { get; private set; }
        public static bool InCombat { get; private set; }
        public static int Round { get { return _state.Round; } }
        public static int TaprootHitsThisRound { get { return _state.TaprootHitsThisRound; } }

        // Live state, read-only use (pure helpers fall back to it when no
        // snapshot memory is passed, e.g. unit tests).
        internal static MemoryState Live { get { return _state; } }

        // Copy for a turn snapshot.
        internal static MemoryState Capture()
        {
            return _state.Clone();
        }

        // Write back the state a decision probed / committed.
        internal static void Restore(MemoryState state)
        {
            if (state != null)
                _state = state.Clone();
        }

        public static uint ShadowActor { get; private set; }
        public static ChosenAction ShadowBot { get; private set; }
        public static List<JObject> ShadowLegal { get; private set; }

        public static void ResetFight()
        {
            _state.ResetFight();
            HandsOff = false;
            HandsOffReason = null;
            InCombat = false;
            ClearShadow();
        }

        public static void NoteRound(int round)
        {
            _state.NoteRound(round);
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
            _state.NoteReachWalk(actorGuid);
        }

        public static bool ReachWalkedThisRound(uint actorGuid)
        {
            return _state.ReachWalked(actorGuid);
        }

        public static bool PartyReachWalkedThisRound()
        {
            return _state.PartyReachWalked();
        }

        public static void NoteComboSpenderActed(uint actorGuid)
        {
            _state.NoteComboSpenderActed(actorGuid);
        }

        public static bool ComboSpenderActedThisRound(uint actorGuid)
        {
            return _state.ComboSpenderActed(actorGuid);
        }

        public static void NoteCrisisHeal(uint targetGuid)
        {
            _state.NoteCrisisHeal(targetGuid);
        }

        public static bool CrisisHealThisRound(uint targetGuid)
        {
            return _state.CrisisHealed(targetGuid);
        }

        public static void NoteTaprootHit()
        {
            _state.NoteTaprootHit();
        }

        public static void BeginBattle(string fightId, CombatSource source)
        {
            ResetFight();
            InCombat = true;
            HandsOff = !PlayScriptedFights && IsScriptedStoryFight(fightId, source);
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
            return _state.CanSpendSetup(lastEnemyGuid);
        }

        public static void NoteChosen(uint lastEnemyGuid, bool wasSetup)
        {
            _state.NoteChosen(lastEnemyGuid, wasSetup);
        }

        public static bool CanSpendItem(uint actorGuid, bool crisis)
        {
            return _state.CanSpendItem(actorGuid, crisis);
        }

        public static void NoteItemUsed(uint actorGuid)
        {
            _state.NoteItemUsed(actorGuid);
        }
    }
}
