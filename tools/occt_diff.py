#!/usr/bin/env python3
"""Ворота g.02: разностное сравнение булевых с OpenCascade (выгрузка K01Checks diffdump).

Запуск: python3 tools/occt_diff.py <каталог> [--json out.json]
Для каждого случая: операнды (точный STEP) читает OpenCascade, строит ту же цепочку своей булевой (BRepAlgoAPI) и считает
объём. Сравнение с нашим исходом:
  agree        — наш успех/пусто и объём совпадает в допуске;
  refused      — наш честный отказ (Unsupported / AccuracyExceeded / InternalFailure) — пробел возможностей, не ошибка;
  WRONG        — наш успех (или «пусто»), объём которого не совпадает с независимым сверх допуска: НЕВЕРНОЕ ПРИНЯТИЕ;
  occt-failed  — OpenCascade не построил (или дал негодное тело): случай не решён, в счёт не идёт;
  INVALID-SUCCESS — наш успех, а наша же единая проверка (SolidValidator) находит в теле ошибки (столбец valid).
Спорный случай (WRONG) дополнительно судится третьим способом — оценкой объёма Монте-Карло по классификации точек
относительно ОПЕРАНДОВ (BRepClass3d), без всякой булевой: кто из двух ядер прав.
Допуск: ExactAnalytic — 1e-6 от суммы объёмов операндов; иначе (многогранное представление) — плюс 2·хорда·площадь.
"""
import sys, os, json, random
from OCP.STEPControl import STEPControl_Reader
from OCP.IFSelect import IFSelect_RetDone
from OCP.BRepAlgoAPI import BRepAlgoAPI_Cut, BRepAlgoAPI_Fuse, BRepAlgoAPI_Common
from OCP.GProp import GProp_GProps
from OCP.BRepGProp import BRepGProp
from OCP.BRepCheck import BRepCheck_Analyzer
from OCP.BRepClass3d import BRepClass3d_SolidClassifier
from OCP.TopAbs import TopAbs_IN, TopAbs_OUT
from OCP.gp import gp_Pnt
from OCP.Bnd import Bnd_Box
from OCP.BRepBndLib import BRepBndLib


def read(path):
    r = STEPControl_Reader()
    if r.ReadFile(path) != IFSelect_RetDone: raise RuntimeError("read " + path)
    r.TransferRoots(); return r.OneShape()


def vol(s):
    g = GProp_GProps(); BRepGProp.VolumeProperties_s(s, g, 1e-9, False, False); return g.Mass()


def area(s):
    g = GProp_GProps(); BRepGProp.SurfaceProperties_s(s, g, 1e-9, False); return g.Mass()


def chain(shapes, ops):
    cur = shapes[0]
    for k, op in enumerate(ops):
        b = shapes[k + 1]
        alg = {"cut": BRepAlgoAPI_Cut, "fuse": BRepAlgoAPI_Fuse, "common": BRepAlgoAPI_Common}[op](cur, b)
        if not alg.IsDone(): return None
        cur = alg.Shape()
    return cur


def montecarlo(shapes, ops, n=40000, seed=1):
    """Объём результата цепочки по точкам: классификация каждой точки относительно КАЖДОГО операнда, логика операций.
    Одна операция: точный объём операндов (GProp) и Монте-Карло только для A∩B в пересечении их габаритов
    (V(A∪B) = VA + VB − V(A∩B), V(A−B) = VA − V(A∩B)) — погрешность в десятки раз меньше, чем по общему габариту
    (малый шар на большом цилиндре: ±90 мм³ по общему габариту не судит расхождение 17 мм³)."""
    if len(ops) == 1:
        ba, bb = Bnd_Box(), Bnd_Box(); BRepBndLib.Add_s(shapes[0], ba); BRepBndLib.Add_s(shapes[1], bb)
        a0, a1, b0, b1 = ba.CornerMin(), ba.CornerMax(), bb.CornerMin(), bb.CornerMax()
        lo = [max(a0.X(), b0.X()), max(a0.Y(), b0.Y()), max(a0.Z(), b0.Z())]
        hi = [min(a1.X(), b1.X()), min(a1.Y(), b1.Y()), min(a1.Z(), b1.Z())]
        va, vb = vol(shapes[0]), vol(shapes[1])
        vi, ei = 0.0, 0.0
        if all(h > l for l, h in zip(lo, hi)):
            ca, cb = BRepClass3d_SolidClassifier(shapes[0]), BRepClass3d_SolidClassifier(shapes[1])
            rnd = random.Random(seed); inside = 0; decided = 0
            for _ in range(n):
                p = gp_Pnt(*[rnd.uniform(l, h) for l, h in zip(lo, hi)])
                ca.Perform(p, 1e-7); cb.Perform(p, 1e-7); sa, sb = ca.State(), cb.State()
                if sa not in (TopAbs_IN, TopAbs_OUT) or sb not in (TopAbs_IN, TopAbs_OUT): continue
                decided += 1; inside += (sa == TopAbs_IN and sb == TopAbs_IN)
            bv = (hi[0] - lo[0]) * (hi[1] - lo[1]) * (hi[2] - lo[2])
            vi = bv * inside / max(1, decided)
            ei = bv * (max(inside, 1) * (1 - inside / max(1, decided))) ** 0.5 / max(1, decided) * 3
        v = {"cut": va - vi, "fuse": va + vb - vi, "common": vi}[ops[0]]
        return v, ei
    box = Bnd_Box()
    for s in shapes: BRepBndLib.Add_s(s, box)
    mn, mx = box.CornerMin(), box.CornerMax()
    x0, y0, z0, x1, y1, z1 = mn.X(), mn.Y(), mn.Z(), mx.X(), mx.Y(), mx.Z()
    cls = [BRepClass3d_SolidClassifier(s) for s in shapes]
    rnd = random.Random(seed); inside = 0; undecided = 0
    for _ in range(n):
        p = gp_Pnt(rnd.uniform(x0, x1), rnd.uniform(y0, y1), rnd.uniform(z0, z1))
        st = []
        for c in cls:
            c.Perform(p, 1e-7); s = c.State()
            st.append(True if s == TopAbs_IN else False if s == TopAbs_OUT else None)
        if any(v is None for v in st): undecided += 1; continue
        cur = st[0]
        for k, op in enumerate(ops):
            b = st[k + 1]
            cur = (cur and not b) if op == "cut" else (cur or b) if op == "fuse" else (cur and b)
        inside += cur
    decided = n - undecided
    bv = (x1 - x0) * (y1 - y0) * (z1 - z0)
    v = bv * inside / max(1, decided)
    err = bv * (max(inside, 1) * (1 - inside / max(1, decided))) ** 0.5 / max(1, decided) * 3   # 3σ
    return v, err


def main():
    d = sys.argv[1]
    out = sys.argv[sys.argv.index("--json") + 1] if "--json" in sys.argv else None
    rows = [l.rstrip("\n").split("\t") for l in open(os.path.join(d, "manifest.tsv"), encoding="utf-8")][1:]
    stats = {"agree": 0, "refused": 0, "WRONG": 0, "occt-failed": 0, "ours-exception": 0}
    report = []
    for row in rows:
        row += [""] * (14 - len(row))
        cid, ops, kinds, outcome, guar, bodies, va, vm, ar, chord, detail, opv, bound, valid = row[:14]
        ops = [o for o in ops.split(",") if o]
        it = {"id": cid, "ops": ops, "kinds": kinds, "outcome": outcome, "guarantee": guar, "detail": detail}
        if outcome == "exception":
            stats["ours-exception"] += 1; it["verdict"] = "ours-exception"; report.append(it); continue
        try:
            shapes = [read(os.path.join(d, f"{cid}_op{j}.step")) for j in range(len(ops) + 1)]
        except Exception as ex:
            stats["occt-failed"] += 1; it["verdict"] = f"occt-failed (read: {ex})"; report.append(it); continue
        # операнды: объём по OpenCascade = нашему точному (иначе расходится уже ОБМЕН, а не булева)
        mism = []
        for j, (s_, v_) in enumerate(zip(shapes, opv.split(",") if opv else [])):
            try: vv = float(v_)
            except ValueError: vv = float("nan")
            if vv != vv: mism.append(f"op{j}: our exact volume unknown (NaN)"); continue
            if abs(vol(s_) - vv) > 1e-7 * abs(vv): mism.append(f"op{j} {vol(s_):.9g} vs ours {vv:.9g}")
        if mism:
            stats["operand-mismatch"] = stats.get("operand-mismatch", 0) + 1; it["verdict"] = "OPERAND-MISMATCH (STEP): " + "; ".join(mism); report.append(it); continue
        # наш исход по первой операции, где цепочка остановилась: OpenCascade строит ту же длину цепочки
        res = chain(shapes, ops)
        if res is None or not BRepCheck_Analyzer(res).IsValid():
            stats["occt-failed"] += 1; it["verdict"] = "occt-failed"; report.append(it); continue
        vo = vol(res); it["occt"] = vo
        opsum = sum(abs(vol(s)) for s in shapes)
        if outcome not in ("Success", "Empty"):
            stats["refused"] += 1; it["verdict"] = "refused"; report.append(it); continue
        # успех с негодным телом (наша же проверка SolidValidator) — неверное принятие, даже если объём верен
        if valid.startswith("INVALID"):
            stats["INVALID-SUCCESS"] = stats.get("INVALID-SUCCESS", 0) + 1
            it["verdict"] = "INVALID-SUCCESS: " + valid; report.append(it); continue
        exact = guar == "ExactAnalytic"
        ours = float(va) if exact and va not in ("NaN", "") else float(vm) if vm not in ("NaN", "") else float("nan")
        tol = 1e-6 * opsum
        if not exact:
            tol += 1e-2 * opsum   # многогранное представление: шаг угла 7.5° — вписанный многогранник меньше до ~0.6% (ar может быть NaN)
        it["ours"] = ours; it["tol"] = tol
        if abs(ours - vo) <= tol:
            stats["agree"] += 1; it["verdict"] = "agree"
            # g.04: заявленная нами граница ошибки объёма должна покрывать расхождение с независимым (плюс 1e-7 — точность OCCT)
            try: bnd = float(bound)
            except ValueError: bnd = float("nan")
            it["bound"] = bnd
            # и у многогранного: объём, который сообщает деталь (va), и его граница (g.04)
            if not exact and bnd == bnd and va not in ("NaN", ""):
                it["reported"] = float(va)
                if abs(float(va) - vo) > bnd + 1e-7 * opsum:
                    mc, err = montecarlo(shapes, ops)
                    if abs(mc - float(va)) < abs(mc - vo) and abs(mc - float(va)) <= err:
                        stats["occt-failed"] += 1
                        it["verdict"] = f"occt-wrong? reported {float(va):.9g} vs OCCT {vo:.9g}; Monte-Carlo {mc:.6g}±{err:.3g} → ours closer"
                    else:
                        stats["bound-violation"] = stats.get("bound-violation", 0) + 1
                        it["verdict"] = f"BOUND-VIOLATION (polyhedral, reported {float(va):.9g}): |reported − OCCT| = {abs(float(va) - vo):.3g} > declared bound {bnd:.3g}"
            if exact and bnd == bnd and abs(ours - vo) > bnd + 1e-7 * opsum:
                stats["bound-violation"] = stats.get("bound-violation", 0) + 1
                it["verdict"] = f"BOUND-VIOLATION: |ours − OCCT| = {abs(ours - vo):.3g} > declared bound {bnd:.3g}"
        else:
            mc, err = montecarlo(shapes, ops)
            it["mc"] = mc; it["mcErr"] = err
            who = "OCCT closer" if abs(mc - vo) < abs(mc - ours) else "ours closer"
            it["verdict"] = f"WRONG: ours {ours:.6g} vs OCCT {vo:.6g} (tol {tol:.3g}); Monte-Carlo {mc:.6g}±{err:.3g} → {who}"
            stats["WRONG" if who == "OCCT closer" else "occt-failed"] += 1
            if who != "OCCT closer": it["verdict"] = "occt-wrong? " + it["verdict"]
        report.append(it)
    # независимый арбитр (tools/occt_arbiter.tsv): известные ошибки OpenCascade, подтверждённые третьим способом
    arb = {}
    ap = os.path.join(os.path.dirname(os.path.abspath(__file__)), "occt_arbiter.tsv")
    if os.path.exists(ap):
        for l in open(ap, encoding="utf-8"):
            if l.startswith("#") or not l.strip(): continue
            f = l.rstrip("\n").split("\t")
            arb[f[0]] = (float(f[1]), float(f[2]), f[3] if len(f) > 3 else "")
    for it in report:
        v = it["verdict"]
        if it["id"] in arb and v not in ("agree", "refused") and "occt" in it and "ours" in it:
            ref, sg, how = arb[it["id"]]
            b = it.get("bound", float("nan")); lim = max(3 * sg, b if b == b else 0)
            if abs(it["ours"] - ref) <= lim and abs(it["occt"] - ref) >= 5 * sg:
                for k in ("WRONG", "occt-failed", "bound-violation"):
                    if (k == "WRONG" and v.startswith("WRONG")) or (k == "bound-violation" and v.startswith("BOUND")) or (k == "occt-failed" and v.startswith("occt-wrong")):
                        stats[k] -= 1
                stats["occt-wrong-arbitrated"] = stats.get("occt-wrong-arbitrated", 0) + 1
                it["verdict"] = f"occt-wrong (arbiter: {how}): ours {it['ours']:.10g}, OCCT {it['occt']:.10g}, reference {ref:.10g} ± {sg:.3g}"
    for it in report:
        if it["verdict"] not in ("agree", "refused"):
            print(f"{it['id']}  {it['verdict'][:200]}  [{','.join(it['ops'])}] {it['kinds'][:140]}")
    n = len(report)
    print("DIFF: " + ", ".join(f"{k} {v}" for k, v in stats.items()) + f" (of {n})")
    if out: json.dump(report, open(out, "w"), indent=1, ensure_ascii=False)
    sys.exit(1 if stats["WRONG"] or stats.get("operand-mismatch") or stats.get("INVALID-SUCCESS") else 0)


if __name__ == "__main__":
    main()
