#!/usr/bin/env python3
"""m.x2: обратная проверка IGES — OpenCascade пишет тела (режим граней: 144/142/120/124/100/126/128), наш импорт читает
(режим K01Checks igesreadall <каталог>). Запуск: occt_iges_write.py <каталог>; в каталоге — o*.igs и manifest.tsv (id, имя, объём)."""
import sys, os
from OCP.BRepPrimAPI import (BRepPrimAPI_MakeBox, BRepPrimAPI_MakeCylinder, BRepPrimAPI_MakeSphere, BRepPrimAPI_MakeCone,
                             BRepPrimAPI_MakeTorus, BRepPrimAPI_MakeRevol, BRepPrimAPI_MakePrism)
from OCP.BRepAlgoAPI import BRepAlgoAPI_Cut, BRepAlgoAPI_Fuse
from OCP.BRepFilletAPI import BRepFilletAPI_MakeFillet
from OCP.IGESControl import IGESControl_Writer
from OCP.gp import gp_Ax2, gp_Ax1, gp_Pnt, gp_Dir, gp_Vec
from OCP.GProp import GProp_GProps
from OCP.BRepGProp import BRepGProp
from OCP.TopExp import TopExp_Explorer
from OCP.TopAbs import TopAbs_EDGE
from OCP.TopoDS import TopoDS
from OCP.BRepBuilderAPI import BRepBuilderAPI_MakeEdge, BRepBuilderAPI_MakeWire, BRepBuilderAPI_MakeFace
from OCP.GC import GC_MakeArcOfCircle


def ax(x=0, y=0, z=0, d=(0, 0, 1)): return gp_Ax2(gp_Pnt(x, y, z), gp_Dir(*d))


def cases():
    yield "box", BRepPrimAPI_MakeBox(gp_Pnt(-5, -6, -7), 10, 12, 14).Shape()
    yield "cylinder", BRepPrimAPI_MakeCylinder(ax(1, 2, 3), 5, 10).Shape()
    yield "cylinder 1.2 rad", BRepPrimAPI_MakeCylinder(ax(1, 2, 3), 5, 10, 1.2).Shape()
    yield "cylinder tilted", BRepPrimAPI_MakeCylinder(ax(0, 0, 0, (1, 1, 1)), 4, 9).Shape()
    yield "sphere", BRepPrimAPI_MakeSphere(gp_Pnt(3, -2, 1), 7).Shape()
    yield "sphere wedge 2 rad", BRepPrimAPI_MakeSphere(ax(0, 0, 0), 6, 2.0).Shape()
    yield "sphere zone", BRepPrimAPI_MakeSphere(ax(0, 0, 0), 6, -0.5, 0.9).Shape()
    yield "cone frustum", BRepPrimAPI_MakeCone(ax(0, 0, 0), 6, 3, 8).Shape()
    yield "cone apex", BRepPrimAPI_MakeCone(ax(2, 0, 0, (0, 1, 0)), 5, 0, 7).Shape()
    yield "torus", BRepPrimAPI_MakeTorus(ax(0, 0, 0), 20, 3).Shape()
    yield "torus 2 rad", BRepPrimAPI_MakeTorus(ax(0, 0, 0, (0, 1, 0)), 20, 3, 2.0).Shape()
    box = BRepPrimAPI_MakeBox(gp_Pnt(0, 0, 0), 60, 40, 20).Shape()
    yield "box minus cylinder", BRepAlgoAPI_Cut(box, BRepPrimAPI_MakeCylinder(ax(30, 20, -1), 8, 22).Shape()).Shape()
    yield "box minus sphere", BRepAlgoAPI_Cut(box, BRepPrimAPI_MakeSphere(gp_Pnt(60, 40, 20), 12).Shape()).Shape()
    yield "box plus cylinder", BRepAlgoAPI_Fuse(box, BRepPrimAPI_MakeCylinder(ax(30, 20, 20), 10, 15).Shape()).Shape()
    fl = BRepFilletAPI_MakeFillet(BRepPrimAPI_MakeBox(gp_Pnt(0, 0, 0), 30, 20, 10).Shape())
    ex = TopExp_Explorer(fl.Shape() if False else BRepPrimAPI_MakeBox(gp_Pnt(0, 0, 0), 30, 20, 10).Shape(), TopAbs_EDGE)
    b2 = BRepPrimAPI_MakeBox(gp_Pnt(0, 0, 0), 30, 20, 10).Shape(); fl = BRepFilletAPI_MakeFillet(b2)
    ex = TopExp_Explorer(b2, TopAbs_EDGE)
    while ex.More(): fl.Add(2.0, TopoDS.Edge(ex.Current())); ex.Next()
    yield "box all edges fillet 2", fl.Shape()
    # кольцо вращением прямоугольника на 90°
    pts = [gp_Pnt(10, 0, 0), gp_Pnt(15, 0, 0), gp_Pnt(15, 0, 6), gp_Pnt(10, 0, 6)]
    w = BRepBuilderAPI_MakeWire()
    for i in range(4): w.Add(BRepBuilderAPI_MakeEdge(pts[i], pts[(i + 1) % 4]).Edge())
    face = BRepBuilderAPI_MakeFace(w.Wire()).Face()
    yield "revolve rectangle 90 deg", BRepPrimAPI_MakeRevol(face, gp_Ax1(gp_Pnt(0, 0, 0), gp_Dir(0, 0, 1)), 1.5707963267948966).Shape()
    # выдавливание сегмента круга
    arc = GC_MakeArcOfCircle(gp_Pnt(-5, 0, 0), gp_Pnt(0, 4, 0), gp_Pnt(5, 0, 0)).Value()
    w = BRepBuilderAPI_MakeWire(BRepBuilderAPI_MakeEdge(arc).Edge(), BRepBuilderAPI_MakeEdge(gp_Pnt(5, 0, 0), gp_Pnt(-5, 0, 0)).Edge())
    yield "prism of circular segment", BRepPrimAPI_MakePrism(BRepBuilderAPI_MakeFace(w.Wire()).Face(), gp_Vec(0, 0, 7)).Shape()


def main():
    d = sys.argv[1]; os.makedirs(d, exist_ok=True)
    rows = ["id\tname\tvolume"]
    for i, (name, sh) in enumerate(cases()):
        bid = f"o{i:02d}"
        w = IGESControl_Writer("MM", 0); w.AddShape(sh); w.ComputeModel(); w.Write(f"{d}/{bid}.igs")
        p = GProp_GProps(); BRepGProp.VolumeProperties_s(sh, p, 1e-10)
        rows.append(f"{bid}\t{name}\t{abs(p.Mass())!r}")
    open(f"{d}/manifest.tsv", "w", encoding="utf-8").write("\n".join(rows) + "\n")
    print(f"{len(rows) - 1} files written to {d}")


if __name__ == "__main__":
    main()
