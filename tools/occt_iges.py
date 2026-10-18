#!/usr/bin/env python3
"""m.x2: независимая проверка IGES (OpenCascade через cadquery-ocp). Запуск: occt_iges.py <каталог igesdump> [--json out.json]
Каждый файл: IGESControl_Reader → грани → сшивка (BRepBuilderAPI_Sewing, 1e-6) → тело (ShapeFix_Solid) → объём; сравнение с нашим
объёмом (manifest.tsv) — относительно 1e-6. Итог «IGES: k/n agree». Код выхода 1 — есть расхождение или нечитаемый файл."""
import sys, json
from OCP.IGESControl import IGESControl_Reader
from OCP.IFSelect import IFSelect_RetDone
from OCP.BRepBuilderAPI import BRepBuilderAPI_Sewing, BRepBuilderAPI_MakeSolid
from OCP.ShapeFix import ShapeFix_Solid
from OCP.TopoDS import TopoDS
from OCP.TopAbs import TopAbs_SHELL, TopAbs_FACE
from OCP.TopExp import TopExp_Explorer
from OCP.GProp import GProp_GProps
from OCP.BRepGProp import BRepGProp


def volume(path):
    r = IGESControl_Reader()
    if r.ReadFile(path) != IFSelect_RetDone:
        return None, "unreadable"
    r.TransferRoots()
    shape = r.OneShape()
    sew = BRepBuilderAPI_Sewing(1e-6)
    ex = TopExp_Explorer(shape, TopAbs_FACE); nf = 0
    while ex.More():
        sew.Add(ex.Current()); nf += 1; ex.Next()
    sew.Perform()
    sewed = sew.SewedShape()
    total = 0.0; shells = 0
    ex = TopExp_Explorer(sewed, TopAbs_SHELL)
    while ex.More():
        mk = BRepBuilderAPI_MakeSolid(TopoDS.Shell_s(ex.Current()))
        fix = ShapeFix_Solid(mk.Solid()); fix.Perform()
        p = GProp_GProps(); BRepGProp.VolumeProperties_s(fix.Solid(), p, 1e-9)
        total += abs(p.Mass()); shells += 1; ex.Next()
    return total, f"faces {nf}, shells {shells}"


def main():
    d = sys.argv[1]
    out = sys.argv[sys.argv.index("--json") + 1] if "--json" in sys.argv else None
    rows = [l.rstrip("\n").split("\t") for l in open(f"{d}/manifest.tsv", encoding="utf-8")][1:]
    res = []; ok = 0
    for r in rows:
        bid, name, V = r[0], r[1], float(r[2])
        v, note = volume(f"{d}/{bid}.igs")
        st = "unreadable" if v is None else ("OK" if abs(v - V) <= 1e-6 * max(1.0, abs(V)) else "DIFF")
        if st == "OK": ok += 1
        res.append({"id": bid, "name": name, "ours": V, "occt": v, "status": st, "note": note})
        print(f"{bid} {st:10s} ours {V:.9g} occt {v if v is None else f'{v:.9g}'} ({note})  {name[:60]}")
    if out: json.dump(res, open(out, "w"), ensure_ascii=False, indent=1)
    print(f"IGES: {ok}/{len(res)} agree")
    sys.exit(0 if ok == len(res) else 1)


if __name__ == "__main__":
    main()
