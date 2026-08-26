"""Locate DD2 logs / Excel and walk decision JSONL."""
from __future__ import annotations

import json
import os
import re
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
STEAM_ROOTS = [
    Path(r"D:\SteamLibrary\steamapps\common"),
    Path(r"C:\Program Files (x86)\Steam\steamapps\common"),
    Path(r"C:\Program Files\Steam\steamapps\common"),
    Path(r"E:\SteamLibrary\steamapps\common"),
]


def game_dir() -> Path:
    env = os.environ.get("DD2_GAME_DIR")
    if env:
        p = Path(env)
        if p.is_dir():
            return p
    props = REPO / "Directory.Build.props.user"
    if props.is_file():
        text = props.read_text(encoding="utf-8", errors="replace")
        m = re.search(r"<GameDir>([^<]+)</GameDir>", text)
        if m:
            p = Path(m.group(1).strip())
            if p.is_dir():
                return p
    for root in STEAM_ROOTS:
        if not root.is_dir():
            continue
        hits = sorted(root.glob("Darkest*"))
        for hit in hits:
            if hit.is_dir() and "Mod" not in hit.name:
                return hit
    raise FileNotFoundError(
        "Could not find the Darkest Dungeon II folder. Set DD2_GAME_DIR or Directory.Build.props.user."
    )


def logs_root() -> Path:
    return game_dir() / "BepInEx" / "Dd2Autobattler" / "logs"


def excel_dir() -> Path:
    return game_dir() / "Darkest Dungeon II_Data" / "StreamingAssets" / "Excel"


def log_files(path=None, today=False, since=None, latest=True):
    if path:
        p = Path(path)
        if p.is_dir():
            hit = p / "decisions.jsonl"
            return [hit] if hit.is_file() else []
        return [p] if p.is_file() else []
    root = logs_root()
    if not root.is_dir():
        return []
    dirs = sorted(
        (d for d in root.iterdir() if d.is_dir()),
        key=lambda d: d.name,
        reverse=True,
    )
    if since:
        dirs = [d for d in dirs if d.name >= since]
    if today:
        from datetime import datetime

        stamp = datetime.now().strftime("%Y%m%d")
        dirs = [d for d in dirs if d.name.startswith(stamp)]
    elif latest and not since:
        dirs = dirs[:1]
    files = []
    for d in dirs:
        hit = d / "decisions.jsonl"
        if hit.is_file() and hit.stat().st_size >= 200:
            files.append(hit)
    return files


def parse_jsonl(path: Path):
    with path.open(encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                yield json.loads(line)
            except json.JSONDecodeError:
                continue


def cls_name(obj, n=16):
    if not obj:
        return "?"
    return str(obj.get("class") or obj.get("name") or "?")[:n]


def token_ids(actor):
    names = []
    for tk in actor.get("tokens") or []:
        if isinstance(tk, str):
            names.append(tk.lower())
        elif isinstance(tk, dict):
            names.append(str(tk.get("id") or tk.get("name") or "").lower())
    return names


def has_seen(actor):
    return any("eyes_focus" in t or t == "seen" for t in token_ids(actor))


def enemy_classes(turn):
    out = []
    for e in turn.get("enemies") or []:
        out.append(str(e.get("class") or ""))
    focus = turn.get("focus") or {}
    if not out:
        for e in focus.get("enemies") or []:
            out.append(str(e.get("class") or ""))
    return out


def stalks_up(turn):
    return any("eyes_stalk" in c for c in enemy_classes(turn))


def by_guid(turn, guid):
    if guid is None:
        return None
    for side in (turn.get("enemies"), turn.get("heroes")):
        for a in side or []:
            if a.get("guid") == guid:
                return a
    return None


def is_stalk(actor_or_class):
    if actor_or_class is None:
        return False
    if isinstance(actor_or_class, dict):
        return "eyes_stalk" in str(actor_or_class.get("class") or "")
    return "eyes_stalk" in str(actor_or_class or "")


def match_legal(turn):
    chosen = turn.get("chosen") or {}
    legal = turn.get("legal") or []
    skill = chosen.get("skill")
    target = chosen.get("target")
    for row in legal:
        if row.get("skill") == skill and row.get("target") == target:
            return row
    for row in legal:
        if row.get("skill") == skill:
            return row
    return None


def sorted_legal(turn):
    legal = list(turn.get("legal") or [])
    legal.sort(key=lambda r: float(r.get("score") or 0), reverse=True)
    return legal


def is_aoe_skill(skill):
    s = (skill or "").lower()
    return "flashing" in s or "blinding_gas" in s or "zealous" in s


def is_setup_click(skill, kind, reason):
    s = (skill or "").lower()
    r = (reason or "").lower()
    k = (kind or "").lower()
    if r.startswith("item_stress") or r in ("pass_stress", "setup_once", "support"):
        return True
    if s in ("laudanum", "pass_stress", "pass"):
        return True
    if k in ("support", "pass") and r.startswith("item_"):
        return True
    return False


def cited_kill(row):
    if not row or not row.get("kills"):
        return False
    why = str(row.get("focus_why") or row.get("why") or "").lower()
    return any(
        key in why
        for key in (
            "eyes",
            "altar",
            "librarian",
            "drummer",
            "bishop",
            "chirurgeon",
            "exemplar",
            "must",
        )
    )


def cite_turn(turn):
    """Return list of (code, detail) cited-note mismatches on this logged turn."""
    hits = []
    chosen = turn.get("chosen") or {}
    if not chosen:
        return hits
    skill = chosen.get("skill") or ""
    reason = turn.get("reason") or chosen.get("reason") or ""
    picked = match_legal(turn)
    legal = sorted_legal(turn)
    kind = (picked or {}).get("kind") or ""
    actor = turn.get("actor") or {}

    kill_rows = [r for r in legal if cited_kill(r)]
    if is_setup_click(skill, kind, reason) and kill_rows:
        best = kill_rows[0]
        hits.append(
            (
                "setup_over_kill",
                "{0} ({1}) over kill {2} score={3:.0f}".format(
                    skill, reason, best.get("skill"), float(best.get("score") or 0)
                ),
            )
        )
    elif is_setup_click(skill, kind, reason):
        best_atk = next(
            (
                r
                for r in legal
                if (r.get("kind") or "") == "Attack" and r.get("enemy")
            ),
            None,
        )
        chosen_score = float(chosen.get("score") or 0)
        if best_atk and float(best_atk.get("score") or 0) >= chosen_score + 30:
            hits.append(
                (
                    "setup_over_swing",
                    "{0} ({1} {2:.0f}) over {3} {4:.0f}".format(
                        skill,
                        reason,
                        chosen_score,
                        best_atk.get("skill"),
                        float(best_atk.get("score") or 0),
                    ),
                )
            )

    if stalks_up(turn):
        aoe = is_aoe_skill(skill)
        kills = bool(picked and picked.get("kills"))
        hit_n = int((picked or {}).get("hit_n") or 0)
        dot = 0.0
        if picked:
            dot = float(picked.get("apply_bleed") or 0) + float(
                picked.get("apply_blight") or 0
            ) + float(picked.get("apply_burn") or 0)
        if aoe and not kills and hit_n >= 2 and dot <= 0.05:
            st = next(
                (
                    r
                    for r in legal
                    if r.get("enemy")
                    and not is_aoe_skill(r.get("skill"))
                    and (r.get("kind") or "") == "Attack"
                ),
                None,
            )
            alt = st.get("skill") if st else "ST"
            hits.append(
                (
                    "stalk_aoe_chip",
                    "{0} hit_n={1} non-kill; wiki wants ST/DoT (alt {2})".format(
                        skill, hit_n, alt
                    ),
                )
            )
        tgt = by_guid(turn, (picked or {}).get("target"))
        tgt_hp = float((picked or {}).get("target_hp") or 0)
        if (
            picked
            and picked.get("enemy")
            and is_stalk(tgt)
            and not kills
            and tgt_hp <= 2.05
            and tgt_hp > 0
            and (picked.get("kind") or "") == "Attack"
        ):
            hits.append(
                (
                    "stalk_leave_chip",
                    "{0} left stalk at {1:.0f} HP (1 HP Cluster still Gazes)".format(
                        skill, tgt_hp
                    ),
                )
            )
        if (
            picked
            and not picked.get("kills")
            and kill_rows
            and not is_setup_click(skill, kind, reason)
        ):
            best = kill_rows[0]
            if abs(float(picked.get("score") or 0) - float(best.get("score") or 0)) < 8:
                hits.append(
                    (
                        "stalk_skip_kill",
                        "{0} {1:.0f} over kill {2} {3:.0f}".format(
                            skill,
                            float(picked.get("score") or 0),
                            best.get("skill"),
                            float(best.get("score") or 0),
                        ),
                    )
                )

    controller_legal = False
    chosen_add = False
    if picked and "add" in str(picked.get("focus_why") or ""):
        chosen_add = True
    for row in legal:
        why = str(row.get("focus_why") or "")
        if (
            row.get("enemy")
            and why
            and "add" not in why
            and any(k in why for k in ("boss", "summon", "rez", "support", "altar"))
        ):
            controller_legal = True
    if controller_legal and chosen_add and (picked or {}).get("kind") == "Attack":
        if not (picked and picked.get("kills")):
            hits.append(
                (
                    "add_while_controller",
                    "{0} -> add while controller legal ({1})".format(
                        skill, picked.get("focus_why") if picked else ""
                    ),
                )
            )

    dd_heal = False
    chose_heal = reason.startswith("heal") or kind == "Heal"
    for row in legal:
        if (row.get("kind") or "") == "Heal" and not row.get("enemy") and row.get(
            "deaths_door"
        ):
            dd_heal = True
    if dd_heal and not chose_heal:
        hits.append(("heal_skip_dd", "{0} skipped a Death's Door heal".format(skill)))

    if (actor.get("class") or "") == "grave_robber" and actor.get("rank") == 0:
        thrown_ok = any(
            "thrown" in str(r.get("skill") or "") and float(r.get("score") or 0) > 0
            for r in legal
        )
        if not thrown_ok and "pick" in skill:
            hits.append(
                (
                    "rank_miss",
                    "GR r0 Pick; Thrown/Flashing not legal (shoved off launch rank)",
                )
            )

    return hits
