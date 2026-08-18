using System;
using Assets.Code.Combat;
using Assets.Code.Combat.Events;
using Assets.Code.Events;
using Dd2Autobattler.Logging;
using Newtonsoft.Json.Linq;

namespace Dd2Autobattler.Combat
{
    internal static class BattleLifecycle
    {
        private static bool _subscribed;

        public static void Subscribe()
        {
            if (_subscribed)
                return;
            try
            {
                EventManager.AddListener<EventBattleBegin>(OnBattleBegin, false, EventManager.PRIORITY_DEFAULT);
                EventManager.AddListener<EventBattleStartRound>(OnBattleStartRound, false, EventManager.PRIORITY_DEFAULT);
                EventManager.AddListener<EventBattleResult>(OnBattleResult, false, EventManager.PRIORITY_DEFAULT);
                _subscribed = true;
                DecisionLog.Info("Subscribed to battle begin/end.");
            }
            catch (Exception ex)
            {
                DecisionLog.Error("Could not subscribe to battle events yet", ex);
            }
        }

        public static void Unsubscribe()
        {
            if (!_subscribed)
                return;
            try
            {
                EventManager.RemoveListener<EventBattleBegin>(OnBattleBegin);
                EventManager.RemoveListener<EventBattleStartRound>(OnBattleStartRound);
                EventManager.RemoveListener<EventBattleResult>(OnBattleResult);
            }
            catch
            {
                // game may already be tearing down
            }
            _subscribed = false;
        }

        private static void OnBattleBegin(EventBattleBegin evt)
        {
            string id = "battle";
            try
            {
                if (evt != null && evt.m_BattleConfiguration != null)
                    id = evt.m_BattleConfiguration.GetKey();
            }
            catch { }

            CombatSource source = null;
            try { if (evt != null) source = evt.m_CombatSource; } catch { }
            CombatMemory.BeginBattle(id, source);
            DecisionLog.BeginFight(id, new JObject
            {
                ["index"] = evt != null ? evt.m_battleIndex : -1,
                ["load"] = evt != null && evt.m_IsLoad,
                ["source"] = source != null ? source.ToString() : null,
                ["hands_off"] = CombatMemory.HandsOff
            });
            if (CombatMemory.HandsOff)
                DecisionLog.Turn(new JObject { ["note"] = CombatMemory.HandsOffReason },
                    "Hands off: play this shrine/story fight yourself");
        }

        private static void OnBattleStartRound(EventBattleStartRound evt)
        {
            var round = 0;
            try { if (evt != null) round = evt.m_Round; } catch { }
            CombatMemory.NoteRound(round);
        }

        private static void OnBattleResult(EventBattleResult evt)
        {
            var extra = new JObject();
            try
            {
                if (evt != null && evt.m_BattleResult != null)
                {
                    extra["complete"] = evt.m_BattleResult.IsFightComplete;
                    extra["retreat"] = evt.m_BattleResult.m_IsRetreat;
                    extra["force_end"] = evt.m_BattleResult.m_IsForceEnd;
                    extra["boss"] = evt.m_BattleResult.m_IsBiomeBossBattle || evt.m_BattleResult.m_IsExpeditionBossBattle;
                }
            }
            catch { }
            CombatMemory.ResetFight();
            DecisionLog.EndFight(extra);
        }
    }
}
