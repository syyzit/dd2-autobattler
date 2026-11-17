using System;
using System.Collections.Generic;
using Assets.Code.Actor;
using Dd2Autobattler.Combat;
using Dd2Autobattler.Logging;
using Newtonsoft.Json.Linq;

namespace Dd2Autobattler
{
    /// <summary>
    /// The whole surface a companion plugin may use. The combat plugin never needs a subscriber: with none, every hook is a no-op.
    /// <list type="bullet">
    /// <item>Records: write through <see cref="DecisionLog"/> (Record / Info / Warn / Error, SessionStamp,
    ///   JsonlPath, FightId, Schema) so lines land in the same decisions.jsonl.</item>
    /// <item><see cref="Progress"/>: raised on battle_begin, round and action (a combat heartbeat).</item>
    /// <item><see cref="FightEnded"/>: the fight_end record, after it is written.</item>
    /// <item>Combat mode: <see cref="IsAuto"/> / <see cref="SetAuto"/>, <see cref="PlayScriptedFights"/>,
    ///   <see cref="InCombat"/>.</item>
    /// <item>Party reads the logs already use: <see cref="PartyActors"/>, <see cref="HeroState"/>,
    ///   <see cref="GetActor"/>, <see cref="IsCorpse"/>.</item>
    /// </list>
    /// </summary>
    public static class AutobattlerApi
    {
        public static string PluginVersion { get { return Plugin.PluginVersion; } }

        /// <summary>Commit stamped into the combat plugin at build time (null when unknown).</summary>
        public static string GitCommit { get { return BuildInfo.GitCommit; } }

        public static bool IsAuto { get { return Plugin.IsAuto; } }

        /// <summary>Combat.Enabled on, ShadowMode off: the bot clicks every hero turn.</summary>
        public static void SetAuto()
        {
            Plugin.SetAuto();
        }

        /// <summary>True while a battle is running (between fight_start and fight_end).</summary>
        public static bool InCombat { get { return CombatMemory.InCombat; } }

        /// <summary>
        /// Off by default: the bot leaves story/shrine fights to the player. A companion plugin that plays
        /// with nobody at the mouse turns it on.
        /// </summary>
        public static bool PlayScriptedFights
        {
            get { return CombatMemory.PlayScriptedFights; }
            set { CombatMemory.PlayScriptedFights = value; }
        }

        /// <summary>Combat progress ("battle_begin", "round", "action"). Handler exceptions are swallowed.</summary>
        public static event Action<string> Progress;

        /// <summary>The fight_end record, raised after the line is written.</summary>
        public static event Action<JObject> FightEnded
        {
            add { OutcomeLog.FightEnded += value; }
            remove { OutcomeLog.FightEnded -= value; }
        }

        /// <summary>The run's party (roster manager), living or not.</summary>
        public static IEnumerable<ActorInstance> PartyActors()
        {
            return OutcomeLog.RunTrackerParty();
        }

        /// <summary>The per-hero state object fight_end and run records carry (hp, stress, living, dead, ...).</summary>
        public static JObject HeroState(ActorInstance actor, uint guid)
        {
            return OutcomeLog.HeroState(actor, guid);
        }

        public static ActorInstance GetActor(uint guid)
        {
            return OutcomeLog.GetActor(guid);
        }

        public static bool IsCorpse(ActorInstance actor)
        {
            return GameSnapshot.IsCorpse(actor);
        }

        internal static void RaiseProgress(string what)
        {
            var h = Progress;
            if (h == null)
                return;
            try { h(what); } catch { }
        }
    }
}
