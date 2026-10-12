# Hand-built STEP: quarter cylinder R=10, length 20 along z, with the curved face as a RATIONAL
# B-spline surface and its arcs as rational B-spline curves, both as COMPLEX instances (the way
# other systems write NURBS). Volume = pi*R^2*L/4.
import math
L=[]; n=[0]
def E(s):
    n[0]+=1; L.append(f"#{n[0]} = {s};"); return n[0]
def P(x,y,z): return E(f"CARTESIAN_POINT('',({x:.12g},{y:.12g},{z:.12g}))")
def D(x,y,z): return E(f"DIRECTION('',({x:.12g},{y:.12g},{z:.12g}))")
def AX(o,z,x): return E(f"AXIS2_PLACEMENT_3D('',#{P(*o)},#{D(*z)},#{D(*x)})")
R=10.0; H=20.0; w=math.sqrt(2)/2
verts={}
def V(p):
    if p not in verts: verts[p]=E(f"VERTEX_POINT('',#{P(*p)})")
    return verts[p]
def line(a,b):
    d=[b[i]-a[i] for i in range(3)]; ln=math.sqrt(sum(x*x for x in d))
    return E(f"LINE('',#{P(*a)},#{E(f'VECTOR(\'\',#{D(*[x/ln for x in d])},{ln:.12g})')})")
def arc_curve(z):
    cps=[P(R,0,z),P(R,R,z),P(0,R,z)]
    return E("(BOUNDED_CURVE()B_SPLINE_CURVE(2,("+",".join('#'+str(c) for c in cps)+"),.CIRCULAR_ARC.,.F.,.F.)"
             "B_SPLINE_CURVE_WITH_KNOTS((3,3),(0.,1.),.UNSPECIFIED.)CURVE()GEOMETRIC_REPRESENTATION_ITEM()"
             f"RATIONAL_B_SPLINE_CURVE((1.,{w:.15g},1.))REPRESENTATION_ITEM(''))")
def EC(a,b,c,same=True): return E(f"EDGE_CURVE('',#{V(a)},#{V(b)},#{c},{'.T.' if same else '.F.'})")
def OE(e,o): return E(f"ORIENTED_EDGE('',*,*,#{e},{'.T.' if o else '.F.'})")
def loop(oes): return E("EDGE_LOOP('',("+",".join('#'+str(x) for x in oes)+"))")
A0=(R,0.0,0.0); B0=(0.0,R,0.0); A1=(R,0.0,H); B1=(0.0,R,H); O0=(0.0,0.0,0.0); O1=(0.0,0.0,H)
e_arc0=EC(A0,B0,arc_curve(0.0)); e_arc1=EC(A1,B1,arc_curve(H))
e_a=EC(A0,A1,line(A0,A1)); e_b=EC(B0,B1,line(B0,B1))
e_o=EC(O0,O1,line(O0,O1)); e_oa0=EC(O0,A0,line(O0,A0)); e_ob0=EC(O0,B0,line(O0,B0)); e_oa1=EC(O1,A1,line(O1,A1)); e_ob1=EC(O1,B1,line(O1,B1))
faces=[]
def face(bounds,surf,same): return E(f"ADVANCED_FACE('',("+",".join('#'+str(E(f"FACE_OUTER_BOUND('',#{b},.T.)")) for b in bounds)+f"),#{surf},{'.T.' if same else '.F.'})")
# Curved face: NURBS u along the arc (rows = arc control points), v along z. Normal of S_u x S_v
# with u from A to B (counter-clockwise) and v up points OUTWARD (away from the axis).
rows=[]
for (x,y) in [(R,0),(R,R),(0,R)]:
    rows.append("(#"+str(P(x,y,0.0))+",#"+str(P(x,y,H))+")")
wrow="((1.,1.),("+f"{w:.15g},{w:.15g}"+"),(1.,1.))"
surf=E("(BOUNDED_SURFACE()B_SPLINE_SURFACE(2,1,("+",".join(rows)+"),.UNSPECIFIED.,.F.,.F.,.F.)"
       "B_SPLINE_SURFACE_WITH_KNOTS((3,3),(2,2),(0.,1.),(0.,1.),.UNSPECIFIED.)GEOMETRIC_REPRESENTATION_ITEM()"
       f"RATIONAL_B_SPLINE_SURFACE({wrow})REPRESENTATION_ITEM('')SURFACE())")
# outward loop: A0 -> B0 (arc .T.), B0 -> B1, B1 -> A1 (arc .F.), A1 -> A0
faces.append(face([loop([OE(e_arc0,True),OE(e_b,True),OE(e_arc1,False),OE(e_a,False)])],surf,True))
# x=0 plane (normal -x): O0 -> O1 -> B1 -> B0 ? check CCW about -x: use points (y,z): O0(0,0) O1(0,H) B1(R,H) B0(R,0)
pl_x=E(f"PLANE('',#{AX(O0,(-1,0,0),(0,1,0))})")
faces.append(face([loop([OE(e_o,True),OE(e_ob1,True),OE(e_b,False),OE(e_ob0,False)])],pl_x,True))
# y=0 plane (normal -y): O0 -> A0 -> A1 -> O1
pl_y=E(f"PLANE('',#{AX(O0,(0,-1,0),(1,0,0))})")
faces.append(face([loop([OE(e_oa0,True),OE(e_a,True),OE(e_oa1,False),OE(e_o,False)])],pl_y,True))
# bottom z=0 (normal -z): O0 -> B0 -> A0 (arc reversed) -> O0
pl_b=E(f"PLANE('',#{AX(O0,(0,0,-1),(1,0,0))})")
faces.append(face([loop([OE(e_ob0,True),OE(e_arc0,False),OE(e_oa0,False)])],pl_b,True))
# top z=H (normal +z): O1 -> A1 -> B1 (arc) -> O1
pl_t=E(f"PLANE('',#{AX(O1,(0,0,1),(1,0,0))})")
faces.append(face([loop([OE(e_oa1,True),OE(e_arc1,True),OE(e_ob1,False)])],pl_t,True))
sh=E("CLOSED_SHELL('',("+",".join('#'+str(f) for f in faces)+"))")
E(f"MANIFOLD_SOLID_BREP('quarter',#{sh})")
print("ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION((''),'2;1');\nFILE_NAME('q.step','',(''),(''),'','','');\nFILE_SCHEMA(('AUTOMOTIVE_DESIGN'));\nENDSEC;\nDATA;")
print("\n".join(L)); print("ENDSEC;\nEND-ISO-10303-21;")
