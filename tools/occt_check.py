#!/usr/bin/env python3
"""Ворота g.14: независимая проверка точного STEP другим ядром (OpenCascade через пакет cadquery-ocp).

Запуск: python3 tools/occt_check.py <каталог выгрузки g14dump> [--json out.json]
Для каждого файла каталога (manifest.tsv): чтение STEPControl_Reader, годность BRepCheck_Analyzer, число тел (SOLID)
и граней, объём и площадь — интегрированием OCCT (BRepGProp), сравнение с нашим ответом и с формулой, если она есть.
Код выхода 1 — есть расхождение. Разногласие ядер не доказывает, кто прав (инструкция): каждый случай разбирается.
"""
import sys, json, math, os
from OCP.STEPControl import STEPControl_Reader
from OCP.IFSelect import IFSelect_RetDone
from OCP.BRepCheck import BRepCheck_Analyzer
from OCP.TopExp import TopExp_Explorer
from OCP.TopAbs import TopAbs_SOLID, TopAbs_FACE, TopAbs_SHELL
from OCP.GProp import GProp_GProps
from OCP.BRepGProp import BRepGProp
from OCP.BRep import BRep_Tool
from OCP.TopoDS import TopoDS
from OCP.Bnd import Bnd_Box
from OCP.BRepBndLib import BRepBndLib
from OCP.GeomAdaptor import GeomAdaptor_Surface
from OCP.ShapeAnalysis import ShapeAnalysis_Shell
from OCP.Interface import Interface_Static

# Допуски сравнения. Тело ExactAnalytic: относительное 1e-8 (OCCT интегрирует адаптивной квадратурой с eps 1e-9; замер:
# аналитические детали каталога сходятся до 2e-9). Тело иного уровня (многогранная форма главная, рёбра в файле
# приближены): расхождение не больше смещения границы на ЗАЯВЛЕННУЮ в файле точность u (UNCERTAINTY_MEASURE):
# |ΔV| ≤ u·A, |ΔA| ≤ 2·u·L (L — длина всех рёбер).
REL_EXACT = 1e-8

SURF = {0: "plane", 1: "cylinder", 2: "cone", 3: "sphere", 4: "torus", 5: "bezier", 6: "bspline",
        7: "revolution", 8: "extrusion", 9: "offset", 10: "other"}


def count(shape, kind):
    e = TopExp_Explorer(shape, kind); n = 0
    while e.More():
        n += 1; e.Next()
    return n


def file_shell_components(path):
    """Структура САМОГО ФАЙЛА (до исправлений читателя — OpenCascade при чтении сам делит несвязную оболочку): число
    CLOSED_SHELL, грани которых не связаны общими EDGE_CURVE в одно целое."""
    import re
    txt = open(path, encoding="utf-8", errors="replace").read().replace("\n", "")
    ent = {}
    for m in re.finditer(r"#(\d+)\s*=\s*([A-Z_0-9]+)\s*\((.*?)\)\s*;", txt):
        ent[int(m.group(1))] = (m.group(2), m.group(3))
    refs = lambda a: [int(x) for x in re.findall(r"#(\d+)", a)]
    def edges_of_face(fid):
        out = set()
        stack = [fid]; seen = set()
        while stack:
            k = stack.pop()
            if k in seen or k not in ent: continue
            seen.add(k); t, a = ent[k]
            if t == "EDGE_CURVE": out.add(k); continue
            if t in ("ADVANCED_FACE",): stack += refs(a.split(")")[0])          # только границы, не поверхность
            elif t in ("FACE_BOUND", "FACE_OUTER_BOUND", "EDGE_LOOP", "ORIENTED_EDGE"): stack += refs(a)
        return out
    bad = 0
    for k, (t, a) in ent.items():
        if t != "CLOSED_SHELL": continue
        faces = refs(a); es = [edges_of_face(f) for f in faces]
        parent = list(range(len(faces)))
        def find(x):
            while parent[x] != x: parent[x] = parent[parent[x]]; x = parent[x]
            return x
        owner = {}
        for i, s_ in enumerate(es):
            for e in s_:
                if e in owner: parent[find(i)] = find(owner[e])
                else: owner[e] = i
        if len({find(i) for i in range(len(faces))}) > 1: bad += 1
    return bad


def check(path):
    r = STEPControl_Reader()
    st = r.ReadFile(path)
    if st != IFSelect_RetDone:
        return {"read": False}
    r.TransferRoots()
    shape = r.OneShape()
    res = {"read": True}
    res["valid"] = bool(BRepCheck_Analyzer(shape).IsValid())
    res["solids"] = count(shape, TopAbs_SOLID)
    res["faces"] = count(shape, TopAbs_FACE)
    # свободные рёбра оболочек (незамкнутость)
    free = 0
    e = TopExp_Explorer(shape, TopAbs_SHELL)
    while e.More():
        sa = ShapeAnalysis_Shell(); sa.LoadShells(e.Current()); sa.CheckOrientedShells(e.Current(), True)
        if sa.HasFreeEdges(): free += 1
        if sa.HasBadEdges(): res["badEdges"] = True
        e.Next()
    res["shellsWithFreeEdges"] = free
    # связность каждой оболочки: грани одной CLOSED_SHELL связаны общими рёбрами (иначе это не оболочка по стандарту —
    # например, наружная оболочка и полость, записанные одним CLOSED_SHELL)
    from OCP.TopAbs import TopAbs_EDGE
    disc = 0
    e = TopExp_Explorer(shape, TopAbs_SHELL)
    while e.More():
        faces = []
        fe = TopExp_Explorer(e.Current(), TopAbs_FACE)
        while fe.More():
            edges = []
            ee = TopExp_Explorer(fe.Current(), TopAbs_EDGE)
            while ee.More(): edges.append(ee.Current()); ee.Next()
            faces.append(edges); fe.Next()
        parent = list(range(len(faces)))
        def find(x):
            while parent[x] != x: parent[x] = parent[parent[x]]; x = parent[x]
            return x
        for a in range(len(faces)):
            for b in range(a + 1, len(faces)):
                if find(a) != find(b) and any(x.IsSame(y) for x in faces[a] for y in faces[b]): parent[find(a)] = find(b)
        if len({find(k) for k in range(len(faces))}) > 1: disc += 1
        e.Next()
    res["disconnectedShells"] = disc
    res["fileDisconnectedShells"] = file_shell_components(path)
    kinds = {}
    e = TopExp_Explorer(shape, TopAbs_FACE)
    while e.More():
        s = BRep_Tool.Surface_s(TopoDS.Face(e.Current()))
        k = SURF.get(int(GeomAdaptor_Surface(s).GetType()), "?"); kinds[k] = kinds.get(k, 0) + 1
        e.Next()
    res["surfaces"] = kinds
    g = GProp_GProps(); BRepGProp.VolumeProperties_s(shape, g, 1e-9, False, False); res["volume"] = g.Mass()
    # объём по каждому телу: знак показывает ориентацию (вывернутое тело — отрицательный объём)
    vols = []
    e = TopExp_Explorer(shape, TopAbs_SOLID)
    while e.More():
        gs = GProp_GProps(); BRepGProp.VolumeProperties_s(e.Current(), gs, 1e-9, False, False); vols.append(gs.Mass()); e.Next()
    res["solidVolumes"] = vols
    ga = GProp_GProps(); BRepGProp.SurfaceProperties_s(shape, ga, 1e-9, False); res["area"] = ga.Mass()
    gl = GProp_GProps(); BRepGProp.LinearProperties_s(shape, gl, False, False); res["edgeLength"] = gl.Mass() / 2   # рёбра — по разу
    # g.05: СОБСТВЕННАЯ НЕОПРЕДЕЛЁННОСТЬ ЧИТАТЕЛЯ. Рёбрам без p-кривых в файле OpenCascade строит их проекцией и назначает
    # допуск (эллипс на цилиндре — 1e-5): интегралы по грани идут по этим приближениям. Сдвиг границы на δ вдоль ребра длины
    # L меняет ∫x·n dA/3 не больше чем на δ·L·D/3 (D — диагональ габарита). Учитываются рёбра с допуском больше 1e-7.
    from OCP.BRep import BRep_Tool as _BT
    from OCP.TopAbs import TopAbs_EDGE as _E
    bb = Bnd_Box(); BRepBndLib.Add_s(shape, bb); mn, mx = bb.CornerMin(), bb.CornerMax()
    diag = ((mx.X() - mn.X()) ** 2 + (mx.Y() - mn.Y()) ** 2 + (mx.Z() - mn.Z()) ** 2) ** 0.5
    unc = 0.0
    ex = TopExp_Explorer(shape, _E)
    while ex.More():                                     # ребро оболочки встречается дважды — вклад пополам
        ed = TopoDS.Edge(ex.Current()); t = _BT.Tolerance_s(ed)
        if t > 1e-7:
            ge = GProp_GProps(); BRepGProp.LinearProperties_s(ed, ge, False, False); unc += t * ge.Mass() * diag / 3 / 2
        ex.Next()
    res["occtUncertainty"] = unc
    txt = open(path, encoding="utf-8", errors="replace").read()
    import re
    m = re.search(r"UNCERTAINTY_MEASURE_WITH_UNIT\(LENGTH_MEASURE\(([-0-9.Ee+]+)\)", txt)
    res["uncertainty"] = float(m.group(1)) if m else None
    return res


def main():
    d = sys.argv[1]
    out = sys.argv[sys.argv.index("--json") + 1] if "--json" in sys.argv else None
    rows = [l.rstrip("\n").split("\t") for l in open(os.path.join(d, "manifest.tsv"), encoding="utf-8")][1:]
    report, bad = [], 0
    for row in rows:
        row += [""] * (11 - len(row))
        cid, name, status, bodies, faces, vol, area, expected, guar, mesh, verr = row[:11]
        item = {"id": cid, "name": name, "status": status}
        if status != "ok":
            # честный отказ точной записи (нет точной поверхности) — пробел матрицы, не расхождение; ошибка построения — провал
            honest = status.startswith("no exact step")
            item["verdict"] = ("SKIP (ours: " if honest else "FAIL (ours: ") + status + ")"
            bad += not honest; report.append(item); continue
        o = check(os.path.join(d, cid + ".step")); item["occt"] = o
        probs = []
        if not o["read"]:
            probs.append("not read")
        else:
            if not o["valid"]: probs.append("BRepCheck invalid")
            if o["shellsWithFreeEdges"]: probs.append(f"{o['shellsWithFreeEdges']} open shells")
            if o["disconnectedShells"] or o["fileDisconnectedShells"]:
                probs.append(f"shells of several disconnected parts: in the file {o['fileDisconnectedShells']}, after reading {o['disconnectedShells']}")
            if o["solids"] != int(bodies): probs.append(f"solids {o['solids']} vs {bodies}")
            if o["faces"] != int(faces): probs.append(f"faces {o['faces']} vs {faces}")
            if any(v <= 0 for v in o["solidVolumes"]): probs.append("inverted solid (volume ≤ 0)")
            v, a = float(vol), float(area)
            rv = abs(o["volume"] - v) / v; ra = abs(o["area"] - a) / a
            item["relVol"], item["relArea"] = rv, ra
            exact = all(g == "ExactAnalytic" for g in guar.split(","))
            u = o["uncertainty"] or 0.0
            # не точное тело: объём — многогранной формы; допуск — и заявленная граница ошибки объёма детали (g.04), а не только
            # точность модели в файле (она о рёбрах, а форма в файле — точная)
            ve = float(verr) if verr not in ("", "NaN") else 0.0
            tv = max(REL_EXACT * v, o.get("occtUncertainty", 0.0)) if exact else max(REL_EXACT * v, u * o["area"], ve, o.get("occtUncertainty", 0.0))
            item["occtUncertainty"] = o.get("occtUncertainty", 0.0)
            # площадь не точного тела — с той же относительной границей, что объём (отход вписанных фасет — второго порядка и там,
            # и там); своей границы площади у детали пока нет
            ta = REL_EXACT * a if exact else max(REL_EXACT * a, 2 * u * o["edgeLength"], (ve / v) * a if v > 0 else 0.0)
            item["tol"] = ("1e-8" if exact else f"declared u={u:.3g}, volume bound {ve:.3g}") + (f", reader {o.get('occtUncertainty', 0):.3g}" if o.get("occtUncertainty", 0) > 0 else "")
            if abs(o["volume"] - v) > tv: probs.append(f"volume {o['volume']:.12g} vs ours {v:.12g} (rel {rv:.2e}, tol {item['tol']})")
            if abs(o["area"] - a) > ta: probs.append(f"area {o['area']:.12g} vs ours {a:.12g} (rel {ra:.2e}, tol {item['tol']})")
            if expected not in ("", "NaN"):
                e = float(expected); re_ = abs(o["volume"] - e) / e; item["relExpected"] = re_
                if abs(o["volume"] - e) > tv: probs.append(f"OCCT volume vs formula {e:.12g} rel {re_:.2e}")
                ro = abs(v - e) / e; item["relOursExpected"] = ro
                if abs(v - e) > tv: probs.append(f"our volume vs formula {e:.12g} rel {ro:.2e}")
        item["verdict"] = "OK" if not probs else "DIFF: " + "; ".join(probs)
        bad += bool(probs); report.append(item)
    for it in report:
        print(f"{it['id']}  {it['verdict'][:150]:150s}  {it['name']}")
    n = sum(1 for it in report if it["status"] == "ok")
    nb = sum(1 for it in report if it["verdict"].startswith("FAIL"))
    print(f"OCCT: {n - (bad - nb)}/{n} agree; our build failures {nb}; honest refusals of exact STEP {len(report) - n - nb}")
    if out: json.dump(report, open(out, "w"), indent=1, ensure_ascii=False)
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
