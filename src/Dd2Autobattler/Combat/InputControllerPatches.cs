using System;
using Assets.Code.Actor.ActorController;
using Dd2Autobattler.Logging;
using HarmonyLib;

namespace Dd2Autobattler.Combat
{
    internal static class InputControllerPatches
    {
        internal static void Apply(Harmony harmony)
        {
            var inputType = AccessTools.TypeByName("Assets.Code.Actor.ActorController.ActorControllerInput");
            if (inputType == null)
            {
                DecisionLog.Error("ActorControllerInput type not found");
                return;
            }

            var selectSkill = AccessTools.Method(inputType, "SelectSkilIId");
            var selectTarget = AccessTools.Method(inputType, "SelectTargetGuid");
            if (selectSkill == null || selectTarget == null)
            {
                DecisionLog.Error("Could not find SelectSkilIId / SelectTargetGuid on ActorControllerInput");
                return;
            }

            harmony.Patch(selectSkill, prefix: new HarmonyMethod(typeof(InputControllerPatches), nameof(SelectSkillPrefix)));
            harmony.Patch(selectTarget, prefix: new HarmonyMethod(typeof(InputControllerPatches), nameof(SelectTargetPrefix)));
            DecisionLog.Info("Patched ActorControllerInput skill/target selection.");
        }

        private static bool SelectSkillPrefix(object __instance, ref string __result)
        {
            if (Plugin.Enabled == null || !Plugin.Enabled.Value)
                return true;
            if (CombatMemory.HandsOff)
                return true;

            BattleLifecycle.Subscribe();

            try
            {
                var controller = __instance as ActorControllerBase;
                if (controller == null)
                    return true;

                var chosen = TurnDecider.Decide(controller);
                if (chosen == null || string.IsNullOrEmpty(chosen.SkillId))
                    return true;

                __result = chosen.SkillId;
                return false;
            }
            catch (Exception ex)
            {
                DecisionLog.Error("SelectSkilIId failed; handing back to the game", ex);
                return true;
            }
        }

        private static bool SelectTargetPrefix(ref uint __result)
        {
            if (Plugin.Enabled == null || !Plugin.Enabled.Value)
                return true;
            if (CombatMemory.HandsOff)
                return true;

            var target = TurnDecider.ConsumePendingTarget();
            if (target == 0)
                return true;

            __result = target;
            return false;
        }
    }
}
