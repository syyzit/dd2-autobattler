"""Look up a hero skill in the game CSVs (launch ranks, tags, path replacements)."""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from dd2logs import excel_dir  # noqa: E402

SKILL_KEYS = (
    "launch_ranks",
    "target_ranks",
    "m_Tags",
    "m_IsMultiHit",
    "m_IsFriendly",
    "m_Cooldown",
    "m_Limit",
    "token_ignores",
    "performer_effects",
    "target_effects",
    "target_team_effects",
    "performer_buffs",
    "m_AllConditionIds",
    "move_forward_1",
    "move_backward_1",
    "m_IsMoveToTarget",
)


def csv_files(root: Path):
    files = list(root.glob("*_data_export*.csv"))
    files += list(root.glob("dlc_*/*_data_export*.csv"))
    return files


def parse_blocks(path: Path, needle: str):
    needle = needle.lower()
    blocks = []
    cur = None
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return blocks
    for raw in text.splitlines():
        parts = [p.strip() for p in raw.split(",")]
        if not parts or not parts[0]:
            continue
        if parts[0] == "element_start" and len(parts) >= 3:
            ident = parts[1]
            kind = parts[2]
            if needle in ident.lower() or needle in kind.lower():
                cur = {
                    "file": path.name,
                    "id": ident,
                    "kind": kind,
                    "rows": [],
                }
                blocks.append(cur)
            else:
                cur = None
            continue
        if parts[0] == "element_end":
            cur = None
            continue
        if cur is not None:
            cur["rows"].append(parts)
    return blocks


def path_replacements(root: Path, skill_id: str):
    hits = []
    path = root / "actor_paths_data_export.Group.csv"
    if not path.is_file():
        return hits
    current_path = "?"
    current_repl = None
    for raw in path.read_text(encoding="utf-8", errors="replace").splitlines():
        parts = [p.strip() for p in raw.split(",")]
        if not parts:
            continue
        if parts[0] == "element_start" and len(parts) >= 2:
            if len(parts) >= 3 and parts[2] == "ActorDataPath":
                current_path = parts[1]
            if len(parts) >= 3 and parts[2] == "SkillReplacement":
                current_repl = parts[1]
        if parts[0] == "m_FromActorDataSkillId" and skill_id in ",".join(parts):
            hits.append((current_path, current_repl or "?", "from", parts[1:]))
        if parts[0] == "m_ToActorDataSkillId" and skill_id in ",".join(parts):
            hits.append((current_path, current_repl or "?", "to", parts[1:]))
    return hits


def main():
    p = argparse.ArgumentParser(description="CSV lookup for a DD2 skill id substring.")
    p.add_argument("skill", help="substring, e.g. flashing_daggers or gr_pick")
    p.add_argument("--all-keys", action="store_true", help="print every row, not just the usual ones")
    args = p.parse_args()

    root = excel_dir()
    if not root.is_dir():
        print("Excel folder not found: {0}".format(root), file=sys.stderr)
        sys.exit(1)

    blocks = []
    for f in csv_files(root):
        blocks.extend(parse_blocks(f, args.skill))
    if not blocks:
        print("No CSV element matched {0!r}.".format(args.skill))
        sys.exit(1)

    shown = set()
    useful = ("ActorDataSkill", "ActorDataStats", "ActorDataEffects")
    for b in blocks:
        if not args.all_keys and b["kind"] not in useful:
            continue
        key = (b["id"], b["kind"], b["file"])
        if key in shown:
            continue
        shown.add(key)
        print("")
        print("==== {0}  {1}  ({2}) ====".format(b["id"], b["kind"], b["file"]))
        for row in b["rows"]:
            if not args.all_keys and row[0] not in SKILL_KEYS and not row[0].endswith("_effects"):
                if row[0] not in ("add_stats", "key_map"):
                    continue
            print("  " + ", ".join(x for x in row if x))

    # Path replacements for the first matching skill id
    ids = sorted({b["id"] for b in blocks if b["kind"] == "ActorDataSkill"})
    for sid in ids:
        reps = path_replacements(root, sid)
        if not reps:
            continue
        print("")
        print("==== path replacements for {0} ====".format(sid))
        for path_id, repl, direction, rest in reps:
            print("  {0:22s} {1} {2} {3}".format(path_id, repl, direction, ",".join(rest)))


if __name__ == "__main__":
    main()
