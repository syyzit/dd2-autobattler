"""Session report, fight dump, and cited-note checks on autobattler JSONL."""
from __future__ import annotations

import argparse
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from dd2logs import (  # noqa: E402
    cite_turn,
    cls_name,
    has_seen,
    log_files,
    parse_jsonl,
    sorted_legal,
)


def party_line(heroes, hp=True):
    if not heroes:
        return ""
    parts = []
    for h in sorted(heroes, key=lambda x: x.get("rank", 99)):
        mark = ""
        if h.get("deaths_door"):
            mark = " DD"
        if not h.get("living") or h.get("corpse"):
            mark = " DEAD"
        seen = " Seen" if has_seen(h) else ""
        if hp:
            parts.append(
                "{0}@r{1} {2:.0f}%{3}{4}".format(
                    cls_name(h, 12),
                    h.get("rank"),
                    100 * float(h.get("hp_pct") or 0),
                    mark,
                    seen,
                )
            )
        else:
            parts.append("{0}@r{1}".format(cls_name(h, 12), h.get("rank")))
    return " | ".join(parts)


def enemy_line(turn):
    ens = turn.get("enemies") or []
    if not ens:
        focus = turn.get("focus") or {}
        ens = focus.get("enemies") or []
    bits = []
    for e in ens:
        cls = (e.get("class") or "?").replace("boss_eyes_", "e_").replace("cultist_", "c_")
        hp = e.get("hp")
        hp_max = e.get("hp_max")
        why = e.get("why") or e.get("focus_why") or ""
        if hp is not None and hp_max is not None:
            bits.append("{0} {1:.0f}/{2:.0f}".format(cls, hp, hp_max))
        else:
            bits.append("{0} {1}".format(cls, why)[:40])
    return " | ".join(bits)


def dump_turn(n, turn, hero_filter):
    actor = turn.get("actor") or {}
    if hero_filter:
        blob = "{0} {1}".format(actor.get("class") or "", actor.get("name") or "").lower()
        if hero_filter.lower() not in blob:
            return
    chosen = turn.get("chosen") or {}
    reason = turn.get("reason") or chosen.get("reason") or ""
    print(
        "t{0:03d} [{1}] r{2} {3} -> {4} ({5} {6:.0f})".format(
            n,
            cls_name(actor, 12),
            actor.get("rank"),
            actor.get("name") or "",
            chosen.get("skill"),
            reason,
            float(chosen.get("score") or 0),
        )
    )
    heroes = turn.get("heroes") or []
    if heroes:
        print("     party: " + party_line(heroes))
    el = enemy_line(turn)
    if el:
        print("     enemy: " + el[:220])
    legal = sorted_legal(turn)
    if legal:
        print("     top:")
        for row in legal[:5]:
            print(
                "       {0:7.1f}  {1:28s} dmg={2} kills={3} why={4}".format(
                    float(row.get("score") or 0),
                    str(row.get("skill") or ""),
                    row.get("dmg"),
                    row.get("kills"),
                    row.get("focus_why") or "",
                )
            )


def collect(files, fight_sub=None):
    fights = []
    current = None
    session = None
    for path in files:
        session = path.parent.name
        current = None
        turn_n = 0
        for o in parse_jsonl(path):
            t = o.get("type")
            if t == "fight_start":
                current = {
                    "sess": session,
                    "id": o.get("fight"),
                    "complete": None,
                    "turns": [],
                    "party": None,
                    "deaths": [],
                    "dd": [],
                    "skills": Counter(),
                    "reasons": Counter(),
                }
                fights.append(current)
                turn_n = 0
            elif t == "fight_end" and current:
                current["complete"] = o.get("complete")
                current["retreat"] = o.get("retreat")
            elif t == "turn" and current:
                turn_n += 1
                o["_n"] = turn_n
                current["turns"].append(o)
                heroes = o.get("heroes") or []
                if heroes and not current["party"]:
                    current["party"] = party_line(heroes, hp=False)
                if heroes:
                    for h in heroes:
                        nm = h.get("class")
                        if (not h.get("living") or h.get("corpse")) and nm not in current["deaths"]:
                            current["deaths"].append(nm)
                        if h.get("deaths_door") and nm not in current["dd"]:
                            current["dd"].append(nm)
                chosen = o.get("chosen") or {}
                if chosen.get("skill"):
                    current["skills"][chosen.get("skill")] += 1
                r = o.get("reason") or chosen.get("reason")
                if r:
                    current["reasons"][r] += 1
    if fight_sub:
        sub = fight_sub.lower()
        fights = [f for f in fights if sub in str(f.get("id") or "").lower()]
    return fights


def print_summary(fights):
    wins = sum(1 for f in fights if f.get("complete") is True)
    lost = sum(1 for f in fights if f.get("complete") is False)
    print("fights={0}  won={1}  lost={2}".format(len(fights), wins, lost))
    print("")
    print("======== FIGHTS ========")
    for f in fights:
        flag = "win"
        if f.get("complete") is False:
            flag = "LOST"
        elif f.get("complete") is None:
            flag = "?"
        top = ", ".join("{0}:{1}".format(k, v) for k, v in f["reasons"].most_common(3))
        print(
            "  {0:5s} [{1}] {2}  t={3}  {4}".format(
                flag, f["sess"], f["id"], len(f["turns"]), f.get("party") or ""
            )
        )
        if top:
            print("        {0}".format(top))
        if f["deaths"] or f["dd"]:
            print("        deaths={0} dd={1}".format(f["deaths"], f["dd"]))


def print_cite(fights):
    n = 0
    print("")
    print("======== CITE ========")
    by_code = Counter()
    for f in fights:
        for turn in f["turns"]:
            for code, detail in cite_turn(turn):
                n += 1
                by_code[code] += 1
                print(
                    "  {0:20s} [{1}] {2} t{3}  {4}".format(
                        code, f["sess"], f["id"], turn.get("_n"), detail
                    )
                )
    if n == 0:
        print("  (none)")
    else:
        print("")
        print("  totals: " + ", ".join("{0}={1}".format(k, v) for k, v in by_code.most_common()))
    return n


def print_top_skills(fights, n):
    skills = Counter()
    for f in fights:
        skills.update(f["skills"])
    print("")
    print("======== SKILLS ========")
    for k, v in skills.most_common(n):
        print("  {0:5d}  {1}".format(v, k))


def main():
    p = argparse.ArgumentParser(description="Autobattler log dump and cited-note checks.")
    p.add_argument("--path", help="decisions.jsonl or a session folder")
    p.add_argument("--today", action="store_true")
    p.add_argument("--since", help="session stamp prefix, e.g. 20260823")
    p.add_argument("--fight", help="substring of fight id (implies dump + cite)")
    p.add_argument("--hero", help="class or name substring for dump")
    p.add_argument("--dump", action="store_true", help="print every turn")
    p.add_argument("--quiet", action="store_true", help="skip turn dump (summary/cite only)")
    p.add_argument("--cite", action="store_true", help="print cited-note mismatches")
    p.add_argument("--list", action="store_true", help="fight ids only")
    p.add_argument("--top", type=int, default=15, help="top N skills in the summary")
    args = p.parse_args()

    files = log_files(
        path=args.path,
        today=args.today,
        since=args.since,
        latest=not (args.today or args.since or args.path),
    )
    if not files:
        print("No decision logs found.", file=sys.stderr)
        sys.exit(1)
    print("LOGS {0}".format(len(files)))
    for f in files:
        print("  {0}".format(f))

    fights = collect(files, args.fight)
    if args.list:
        for f in fights:
            flag = "win" if f.get("complete") is True else ("LOST" if f.get("complete") is False else "?")
            print("  {0:5s} {1}  t={2}".format(flag, f["id"], len(f["turns"])))
        return

    dump = (args.dump or bool(args.fight)) and not args.quiet
    cite = args.cite or bool(args.fight)
    if not dump:
        print_summary(fights)
        if args.top:
            print_top_skills(fights, args.top)
    else:
        for f in fights:
            flag = "WIN" if f.get("complete") is True else ("LOST" if f.get("complete") is False else "?")
            print("")
            print(
                "==== {0} {1} [{2}] t={3} ====".format(
                    flag, f["id"], f["sess"], len(f["turns"])
                )
            )
            print("party: {0}".format(f.get("party") or ""))
            for turn in f["turns"]:
                dump_turn(turn.get("_n"), turn, args.hero)

    if cite:
        print_cite(fights)


if __name__ == "__main__":
    main()
