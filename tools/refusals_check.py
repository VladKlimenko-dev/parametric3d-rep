#!/usr/bin/env python3
"""KRN-11 (11.7): СВЕРКА ПЕРЕЧНЯ ОТКАЗОВ С ПРОГОНОМ. Каждый отказ и каждое расхождение базового прогона (ворота g.14, IGES,
каталог KRN04, разностные серии 8–10) должен стоять в таблице «Отказы корпуса» журнала ограничений (KNOWN_LIMITATIONS.md)
с воспроизводителем и причиной; строка таблицы, которой в прогоне нет, — устаревшая (предупреждение).

Запуск: refusals_check.py <каталог базового прогона> [--limits src/docs/release/KNOWN_LIMITATIONS.md]
Читает: g14/occt.json, iges/occt.json, krn04/occt.json, d8/r.json, d9/r.json, d10/r.json (что есть).
Код выхода 1 — есть отказ или расхождение без строки в таблице."""
import sys, os, json, re


def table_ids(path):
    ids = set()
    inside = False
    for line in open(path, encoding="utf-8-sig"):
        if line.startswith("# Отказы корпуса"): inside = True; continue
        if inside and line.startswith("# "): break
        if inside and line.startswith("|"):
            cell = line.split("|")[1]
            # идентификаторы в первой ячейке: d8_0042, k4_109, c04, i17 …
            ids.update(re.findall(r"\b(?:d\d+_\d{4}|k4_\d+|c\d{2,3}|i\d{2,3})\b", cell))
    return ids


def occurrences(base):
    occ = {}   # id → (источник, вердикт)
    def load(rel):
        p = os.path.join(base, rel)
        if not os.path.exists(p): return None
        try: return json.load(open(p, encoding="utf-8"))
        except Exception as ex: occ[f"<{rel}>"] = (rel, f"unreadable report: {ex}"); return None
    g14 = load("g14/occt.json")
    for it in g14 or []:
        v = it.get("verdict", "")
        if v != "OK": occ[it["id"]] = ("g14", v)
    iges = load("iges/occt.json")
    for it in iges or []:
        if it.get("status") != "OK": occ[it["id"]] = ("iges", f"{it.get('status')} ours {it.get('ours')} occt {it.get('occt')}")
    for rel, src in (("krn04/occt.json", "krn04"), ("d8/r.json", "d8"), ("d9/r.json", "d9"), ("d10/r.json", "d10")):
        for it in load(rel) or []:
            v = it.get("verdict", "")
            if v not in ("agree", "refused"): occ[it["id"]] = (src, v)
    return occ


def main():
    if len(sys.argv) < 2: print(__doc__); sys.exit(2)
    base = sys.argv[1]
    lim = sys.argv[sys.argv.index("--limits") + 1] if "--limits" in sys.argv else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "docs", "release", "KNOWN_LIMITATIONS.md")
    listed = table_ids(lim)
    occ = occurrences(base)
    unlisted = {k: v for k, v in occ.items() if k not in listed}
    stale = sorted(i for i in listed if i not in occ)
    for k, (src, v) in sorted(unlisted.items()): print(f"UNLISTED {k} [{src}]: {v[:180]}")
    for k in stale: print(f"STALE {k}: listed in KNOWN_LIMITATIONS, not in this run")
    print(f"REFUSALS: {len(occ)} in run, {len(occ) - len(unlisted)} listed, {len(unlisted)} unlisted, {len(stale)} stale")
    sys.exit(1 if unlisted else 0)


if __name__ == "__main__":
    main()
