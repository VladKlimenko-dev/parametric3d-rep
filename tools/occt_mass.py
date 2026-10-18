#!/usr/bin/env python3
"""m.q2: независимая проверка массовых характеристик с границей ошибки (OpenCascade через cadquery-ocp).

Запуск: python3 tools/occt_mass.py <каталог выгрузки q2dump> [--json out.json]
Для каждого тела manifest.tsv: STEP читается OpenCascade, BRepGProp.VolumeProperties (eps 1e-10) даёт объём, центр масс
и тензор инерции относительно центра масс; наш ответ (MassMeasure) должен отличаться не больше своей границы ошибки
плюс точность OCCT (её оценка по eps × величина). Код выхода 1 — есть выход за границу.
"""
import sys, json, math
from OCP.STEPControl import STEPControl_Reader
from OCP.IFSelect import IFSelect_RetDone
from OCP.GProp import GProp_GProps
from OCP.BRepGProp import BRepGProp

EPS = 1e-10


def props(path):
    r = STEPControl_Reader()
    if r.ReadFile(path) != IFSelect_RetDone:
        return None
    r.TransferRoots()
    shape = r.OneShape()
    p = GProp_GProps()
    err = BRepGProp.VolumeProperties_s(shape, p, EPS)
    c = p.CentreOfMass()
    m = p.MatrixOfInertia()
    J = [[m.Value(i, j) for j in (1, 2, 3)] for i in (1, 2, 3)]
    return p.Mass(), (c.X(), c.Y(), c.Z()), J, err


def main():
    d = sys.argv[1]
    out = sys.argv[sys.argv.index("--json") + 1] if "--json" in sys.argv else None
    rows = [l.rstrip("\n").split("\t") for l in open(f"{d}/manifest.tsv", encoding="utf-8")][1:]
    res = []; bad = 0
    for r in rows:
        (bid, name, V, eV, cx, cy, cz, eC, J11, J12, J13, J22, J23, J33, eJ, route) = r
        V, eV, eC, eJ = float(V), float(eV), float(eC), float(eJ)
        c = (float(cx), float(cy), float(cz))
        Jo = [[float(J11), float(J12), float(J13)], [float(J12), float(J22), float(J23)], [float(J13), float(J23), float(J33)]]
        pr = props(f"{d}/{bid}.step")
        if pr is None:
            res.append({"id": bid, "name": name, "status": "unreadable"}); bad += 1; continue
        m, cc, J, gerr = pr
        # точность OCCT: оценка относительной ошибки интегрирования (возврат VolumeProperties) с запасом
        rel = max(abs(gerr), 1e-9) * 10
        size = max(abs(V) ** (1 / 3), 1e-9)
        dV = abs(m - V); dC = math.dist(cc, c); dJ = max(abs(J[i][j] - Jo[i][j]) for i in range(3) for j in range(3))
        J0 = max(abs(J[i][j]) for i in range(3) for j in range(3))
        okV = dV <= eV + rel * abs(m)
        okC = dC <= eC + rel * size * 10
        okJ = dJ <= eJ + rel * J0
        st = "ok" if okV and okC and okJ else "BOUND-VIOLATION"
        if st != "ok": bad += 1
        res.append({"id": bid, "name": name, "route": route, "status": st, "dV": dV, "eV": eV, "dC": dC, "eC": eC, "dJ": dJ, "eJ": eJ,
                    "relV": eV / abs(m) if m else None, "relJ": eJ / J0 if J0 else None, "occtErr": gerr})
        print(f"{bid} {st:15s} {route[:28]:28s} dV {dV:.2e}/{eV:.2e} dC {dC:.2e}/{eC:.2e} dJ {dJ:.2e}/{eJ:.2e}  {name[:50]}")
    if out:
        json.dump(res, open(out, "w"), ensure_ascii=False, indent=1)
    print(f"bodies {len(res)}, bound violations {bad}")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
