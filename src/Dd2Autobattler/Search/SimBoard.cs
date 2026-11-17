using System;
using System.Collections.Generic;

namespace Dd2Autobattler.Search
{
    /// <summary>
    /// One actor in the forward model. Values are expectations: Hp is the
    /// expected HP, AliveP / DoorP / DeathP / StunP are probabilities. An
    /// actor is structurally dead (rank freed, corpse placed, skipped in the
    /// turn order) once AliveP drops below 0.5.
    /// </summary>
    internal sealed class SimActor
    {
        public uint Guid;
        public bool Hero;
        public string ClassId;
        public List<string> Skills;
        public int Rank;
        public int Size = 1;
        public float Hp;
        public float HpMax;
        // Variance of Hp from expected hits taken inside the model.
        public float HpVar;
        public float AliveP = 1f;
        public bool Corpse;
        public bool Healthless;
        public bool DeathArmor;
        // Death's Door armor tokens: each absorbs one hit at 0 HP.
        public int Armor;
        // Heroes: probability of being on Death's Door / dead.
        public float DoorP;
        public float DeathP;
        public float DeathResist;
        public float Dot;
        public float Hot;
        public float StunP;
        public float ResistStun;
        public float ResistMove;
        public int Block;      // block tokens (50%)
        public int BlockPlus;  // block+ tokens (75%)
        public int Dodge;      // dodge tokens (50%)
        public int DodgePlus;  // dodge+ tokens (75%)
        public bool Vulnerable;
        public bool Weak;
        public bool Strength;
        public bool Taunt;
        public bool Combo;
        public bool Blind;
        public bool Stealth;
        public bool Riposte;
        // Enemy focus weight (EnemyFocus score, cited notes) and flags.
        public float Focus;
        public bool Deferred;
        public bool MustKill;
        // Turns this actor took inside the model (diagnostics).
        public int Acted;
        // Expected damage dealt / taken inside the model (diagnostics + eval).
        public float DamageTaken;
        public float DamageDealt;

        public bool Dead
        {
            get { return AliveP < 0.5f; }
        }

        // A living body (not corpse, not dead) on the board.
        public bool Up
        {
            get { return !Dead && !Corpse && Rank >= 0; }
        }

        public void CopyFrom(SimActor o)
        {
            Guid = o.Guid;
            Hero = o.Hero;
            ClassId = o.ClassId;
            Skills = o.Skills;
            Rank = o.Rank;
            Size = o.Size;
            Hp = o.Hp;
            HpMax = o.HpMax;
            HpVar = o.HpVar;
            AliveP = o.AliveP;
            Corpse = o.Corpse;
            Healthless = o.Healthless;
            DeathArmor = o.DeathArmor;
            Armor = o.Armor;
            DoorP = o.DoorP;
            DeathP = o.DeathP;
            DeathResist = o.DeathResist;
            Dot = o.Dot;
            Hot = o.Hot;
            StunP = o.StunP;
            ResistStun = o.ResistStun;
            ResistMove = o.ResistMove;
            Block = o.Block;
            BlockPlus = o.BlockPlus;
            Dodge = o.Dodge;
            DodgePlus = o.DodgePlus;
            Vulnerable = o.Vulnerable;
            Weak = o.Weak;
            Strength = o.Strength;
            Taunt = o.Taunt;
            Combo = o.Combo;
            Blind = o.Blind;
            Stealth = o.Stealth;
            Riposte = o.Riposte;
            Focus = o.Focus;
            Deferred = o.Deferred;
            MustKill = o.MustKill;
            Acted = o.Acted;
            DamageTaken = o.DamageTaken;
            DamageDealt = o.DamageDealt;
        }

        public bool Occupies(int rank)
        {
            return Rank >= 0 && rank >= Rank && rank < Rank + Math.Max(1, Size);
        }

        public override string ToString()
        {
            return (Hero ? "H" : "E") + Guid + " r" + Rank + " " + Hp.ToString("0.0") + "/" + HpMax.ToString("0")
                   + (AliveP < 0.999f ? " alive" + AliveP.ToString("0.00") : "")
                   + (Corpse ? " corpse" : "")
                   + (DoorP > 0.001f ? " door" + DoorP.ToString("0.00") : "")
                   + (DeathP > 0.001f ? " death" + DeathP.ToString("0.00") : "")
                   + (StunP > 0.001f ? " stun" + StunP.ToString("0.00") : "");
        }
    }

    /// <summary>
    /// Forward-model board: both teams plus the remaining turn order of the
    /// current round. Fixed-capacity and reusable (CopyFrom) so the search
    /// does not allocate per node.
    /// </summary>
    internal sealed class SimBoard
    {
        public const int Capacity = 16;
        public const int QueueCapacity = 32;

        public readonly SimActor[] Actors = new SimActor[Capacity];
        public int Count;
        public readonly int[] Queue = new int[QueueCapacity];
        public int QueueLen;
        public int QueuePos;
        public int Round;
        public GameData Data;
        // Effects the model met and could not represent (summons, class
        // changes, extra turns). Bit flags, see ForwardModel.Unmodeled*.
        public int Unmodeled;

        public SimBoard()
        {
            for (var i = 0; i < Capacity; i++)
                Actors[i] = new SimActor();
        }

        public void CopyFrom(SimBoard o)
        {
            Count = o.Count;
            for (var i = 0; i < o.Count; i++)
                Actors[i].CopyFrom(o.Actors[i]);
            QueueLen = o.QueueLen;
            QueuePos = o.QueuePos;
            Array.Copy(o.Queue, Queue, o.QueueLen);
            Round = o.Round;
            Data = o.Data;
            Unmodeled = o.Unmodeled;
        }

        public int IndexOf(uint guid)
        {
            for (var i = 0; i < Count; i++)
            {
                if (Actors[i].Guid == guid)
                    return i;
            }
            return -1;
        }

        public SimActor Find(uint guid)
        {
            var i = IndexOf(guid);
            return i >= 0 ? Actors[i] : null;
        }

        public bool AnyEnemyUp()
        {
            for (var i = 0; i < Count; i++)
            {
                var a = Actors[i];
                if (!a.Hero && a.Up && !a.Healthless)
                    return true;
            }
            return false;
        }

        public override string ToString()
        {
            var parts = new List<string>();
            for (var i = 0; i < Count; i++)
                parts.Add(Actors[i].ToString());
            return string.Join("; ", parts);
        }
    }
}
