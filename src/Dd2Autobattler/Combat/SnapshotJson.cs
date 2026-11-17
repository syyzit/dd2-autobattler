using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dd2Autobattler.Combat
{
    /// <summary>
    /// TurnSnapshot to / from the log's "snap" field. Field names are the C#
    /// names; default values (false, 0, null) are omitted to keep lines short.
    /// </summary>
    internal static class SnapshotJson
    {
        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            DefaultValueHandling = DefaultValueHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore,
            ObjectCreationHandling = ObjectCreationHandling.Auto,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Formatting = Formatting.None
        });

        public static JObject Write(TurnSnapshot snap)
        {
            if (snap == null)
                return null;
            return JObject.FromObject(snap, Serializer);
        }

        public static TurnSnapshot Read(JToken token)
        {
            if (token == null || token.Type != JTokenType.Object)
                return null;
            var snap = token.ToObject<TurnSnapshot>(Serializer);
            if (snap == null)
                return null;
            // The performer is written once in Performer and once in Heroes;
            // point Performer at the board entry so edits stay in sync.
            snap.Reindex();
            var board = snap.Find(snap.PerformerGuid);
            if (board != null)
                snap.Performer = board;
            return snap;
        }

        public static TurnSnapshot Clone(TurnSnapshot snap)
        {
            return Read(Write(snap));
        }

        public static string ToLine(TurnSnapshot snap)
        {
            var obj = Write(snap);
            return obj != null ? obj.ToString(Formatting.None) : "null";
        }
    }
}
