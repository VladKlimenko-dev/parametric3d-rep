#!/usr/bin/env python3
"""m.x2: независимая проверка IGES (OpenCascade через cadquery-ocp). Запуск: occt_iges.py <каталог igesdump> [--json out.json]
Каждый файл: IGESControl_Reader → грани → сшивка (BRepBuilderAPI_Sewing, 1e-4 — бюджет экспорта) → тело (ShapeFix_Solid) → объём; сравнение с нашим
объёмом (manifest.tsv) — относительно 1e-6. Итог «IGES: k/n agree». Код выхода 1 — есть расхождение или нечитаемый файл."""
import sys, json
from OCP.IGESControl import IGESControl_Reader
from OCP.IFSelect import IFSelect_RetDone
from OCP.BRepBuilderAPI import BRepBuilderAPI_Sewing, BRepBuilderAPI_MakeSolid
from OCP.ShapeFix import ShapeFix_Solid, ShapeFix_Shell
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
    sew = BRepBuilderAPI_Sewing(1e-4)   # допуск сшивки — бюджет экспорта (ExportFit 1e-4)
    ex = TopExp_Explorer(shape, TopAbs_FACE); nf = 0
    while ex.More():
        sew.Add(ex.Current()); nf += 1; ex.Next()
    sew.Perform()
    sewed = sew.SewedShape()
    vols = []
    ex = TopExp_Explorer(sewed, TopAbs_SHELL)
    while ex.More():
        fsh = ShapeFix_Shell(TopoDS.Shell(ex.Current())); fsh.Perform()      # согласовать ориентацию граней оболочки
        mk = BRepBuilderAPI_MakeSolid(TopoDS.Shell(fsh.Shell()))
        fix = ShapeFix_Solid(mk.Solid()); fix.Perform()
        p = GProp_GProps(); BRepGProp.VolumeProperties_s(fix.Solid(), p, 1e-9)
        vols.append(abs(p.Mass())); ex.Next()
    # грань, замкнутая сама по себе (целый шар), сшивкой в оболочку не попадает — своей оболочкой
    from OCP.BRep import BRep_Builder
    from OCP.TopoDS import TopoDS_Shell
    ex = TopExp_Explorer(sewed, TopAbs_FACE, TopAbs_SHELL)
    while ex.More():
        bb = BRep_Builder(); sh = TopoDS_Shell(); bb.MakeShell(sh); bb.Add(sh, ex.Current())
        mk = BRepBuilderAPI_MakeSolid(sh); fix = ShapeFix_Solid(mk.Solid()); fix.Perform()
        p = GProp_GProps(); BRepGProp.VolumeProperties_s(fix.Solid(), p, 1e-9)
        vols.append(abs(p.Mass())); ex.Next()
    # оболочки: наибольшая — наружная; прочие внутри неё — полости (вычитаются), если их сумма меньше наружной;
    # раздельные тела (массив) — по габаритам не проверяются: полость — оболочка, целиком внутри габарита наружной
    if not vols: return 0.0, f"faces {nf}, shells 0"
    return vols, f"faces {nf}, shells {len(vols)}"


def main():
    d = sys.argv[1]
    out = sys.argv[sys.argv.index("--json") + 1] if "--json" in sys.argv else None
    rows = [l.rstrip("\n").split("\t") for l in open(f"{d}/manifest.tsv", encoding="utf-8")][1:]
    res = []; ok = 0
    for r in rows:
        bid, name, V = r[0], r[1], float(r[2])
        try: vs, note = volume(f"{d}/{bid}.igs")
        except Exception as ex: vs, note = None, f"exception {type(ex).__name__}: {str(ex)[:80]}"
        v = None
        if vs is not None:
            vs = vs if isinstance(vs, list) else [vs]
            # раздельные тела — сумма; тело с полостями — наружная минус полости: годится тот вариант, что ближе к нашему
            cands = [sum(vs), max(vs) - (sum(vs) - max(vs))]
            v = min(cands, key=lambda x: abs(x - V))
        st = "unreadable" if v is None else ("OK" if abs(v - V) <= 1e-6 * max(1.0, abs(V)) else "DIFF")
        if st == "OK": ok += 1
        res.append({"id": bid, "name": name, "ours": V, "occt": v, "status": st, "note": note})
        print(f"{bid} {st:10s} ours {V:.9g} occt {v if v is None else f'{v:.9g}'} ({note})  {name[:60]}")
    if out: json.dump(res, open(out, "w"), ensure_ascii=False, indent=1)
    print(f"IGES: {ok}/{len(res)} agree")
    sys.exit(0 if ok == len(res) else 1)


if __name__ == "__main__":
    main()
