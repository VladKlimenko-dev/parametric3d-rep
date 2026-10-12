# Hand-built STEP AP214 file (not produced by Parametric3D): box 60x40x20 with a through hole R8
# at (30,20). Conventions of other systems: full circles with one vertex, a seam line on the
# cylinder, several faces with same_sense .F., edges reused in opposite orientation.
import math, sys
UNIT=sys.argv[1] if len(sys.argv)>1 else 'none'
K={'none':1.0,'mm':1.0,'inch':25.4,'m':1000.0}[UNIT]
L=[]; n=[0]
def E(s):
    n[0]+=1; L.append(f"#{n[0]} = {s};"); return n[0]
def P(x,y,z): return E(f"CARTESIAN_POINT('',({x/K:.12g},{y/K:.12g},{z/K:.12g}))")
def D(x,y,z): return E(f"DIRECTION('',({x:.6f},{y:.6f},{z:.6f}))")
def AX(o,z,x): return E(f"AXIS2_PLACEMENT_3D('',#{P(*o)},#{D(*z)},#{D(*x)})")
verts={}
def V(p):
    if p not in verts: verts[p]=E(f"VERTEX_POINT('',#{P(*p)})")
    return verts[p]
edges={}
def line_edge(a,b):
    key=frozenset([a,b])
    if key in edges: e,s=edges[key]; return e, (s==a)
    d=[b[i]-a[i] for i in range(3)]; ln=math.sqrt(sum(x*x for x in d))
    vec=E(f"VECTOR('',#{D(*[x/ln for x in d])},{ln/K:.12g})")
    li=E(f"LINE('',#{P(*a)},#{vec})")
    e=E(f"EDGE_CURVE('',#{V(a)},#{V(b)},#{li},.T.)"); edges[key]=(e,a); return e,True
def oe(e,o): return E(f"ORIENTED_EDGE('',*,*,#{e},{'.T.' if o else '.F.'})")
def newell(pts):
    nx=ny=nz=0
    for i in range(len(pts)):
        a=pts[i]; b=pts[(i+1)%len(pts)]
        nx+=(a[1]-b[1])*(a[2]+b[2]); ny+=(a[2]-b[2])*(a[0]+b[0]); nz+=(a[0]-b[0])*(a[1]+b[1])
    return (nx,ny,nz)
def poly_loop(pts, outward):
    nw=newell(pts)
    if sum(nw[i]*outward[i] for i in range(3))<0: pts=pts[::-1]
    oes=[]
    for i in range(len(pts)):
        e,fwd=line_edge(pts[i],pts[(i+1)%len(pts)]); oes.append(oe(e,fwd))
    return E(f"EDGE_LOOP('',({','.join('#'+str(x) for x in oes)}))")
R=8.0; cx,cy=30.0,20.0
vb=(cx+R,cy,0.0); vt=(cx+R,cy,20.0)
cb=E(f"CIRCLE('',#{AX((cx,cy,0.0),(0,0,1),(1,0,0))},{R/K:.12g})"); ecb=E(f"EDGE_CURVE('',#{V(vb)},#{V(vb)},#{cb},.T.)")
ct=E(f"CIRCLE('',#{AX((cx,cy,20.0),(0,0,1),(1,0,0))},{R/K:.12g})"); ect=E(f"EDGE_CURVE('',#{V(vt)},#{V(vt)},#{ct},.T.)")
seam,_=line_edge(vb,vt)
faces=[]
def plane_face(origin, normal, xdir, loops_def, same):
    pl=E(f"PLANE('',#{AX(origin, normal if same else tuple(-c for c in normal), xdir)})")
    return E(f"ADVANCED_FACE('',({','.join('#'+str(b) for b in loops_def)}),#{pl},{'.T.' if same else '.F.'})")
box=[(0,0,0),(60,0,0),(60,40,0),(0,40,0),(0,0,20),(60,0,20),(60,40,20),(0,40,20)]
B=lambda i: tuple(float(c) for c in box[i])
# bottom: outward -z, plane normal written as +z with same_sense .F.
lb=poly_loop([B(0),B(1),B(2),B(3)],(0,0,-1))
hb=E(f"EDGE_LOOP('',(#{oe(ecb,True)}))")    # CCW about +z = CW about outward -z: a hole
faces.append(plane_face((0,0,0),(0,0,-1),(1,0,0),[E(f"FACE_OUTER_BOUND('',#{lb},.T.)"),E(f"FACE_BOUND('',#{hb},.T.)")],False))
lt=poly_loop([B(4),B(5),B(6),B(7)],(0,0,1))
ht=E(f"EDGE_LOOP('',(#{oe(ect,False)}))")   # CW about +z: a hole in the top
faces.append(plane_face((0,0,20),(0,0,1),(1,0,0),[E(f"FACE_OUTER_BOUND('',#{lt},.T.)"),E(f"FACE_BOUND('',#{ht},.T.)")],True))
for idx,nrm,same in [([0,1,5,4],(0,-1,0),False),([1,2,6,5],(1,0,0),True),([2,3,7,6],(0,1,0),True),([3,0,4,7],(-1,0,0),False)]:
    pts=[B(i) for i in idx]
    lp=poly_loop(pts,nrm)
    xd=(1,0,0) if abs(nrm[0])<0.5 else (0,1,0)
    faces.append(plane_face(pts[0],nrm,xd,[E(f"FACE_OUTER_BOUND('',#{lp},.T.)")],same))
# hole wall: cylinder normal points away from axis, the face looks toward the axis -> .F.
cyl=E(f"CYLINDRICAL_SURFACE('',#{AX((cx,cy,0.0),(0,0,1),(1,0,0))},{R/K:.12g})")
wl=E(f"EDGE_LOOP('',(#{oe(ecb,False)},#{oe(seam,True)},#{oe(ect,True)},#{oe(seam,False)}))")
faces.append(E(f"ADVANCED_FACE('',(#{E(f'FACE_OUTER_BOUND(\'\',#{wl},.T.)')}),#{cyl},.F.)"))
if UNIT!='none':
    if UNIT=='mm': lu=E("(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.))")
    elif UNIT=='m': lu=E("(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT($,.METRE.))")
    else:
        base=E("(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.))")
        mw=E(f"LENGTH_MEASURE_WITH_UNIT(LENGTH_MEASURE(25.4),#{base})")
        de=E("DIMENSIONAL_EXPONENTS(1.,0.,0.,0.,0.,0.,0.)")
        lu=E(f"(CONVERSION_BASED_UNIT('INCH',#{mw})LENGTH_UNIT()NAMED_UNIT(#{de}))")
    au=E("(NAMED_UNIT(*)PLANE_ANGLE_UNIT()SI_UNIT($,.RADIAN.))")
    su=E("(NAMED_UNIT(*)SI_UNIT($,.STERADIAN.)SOLID_ANGLE_UNIT())")
    E(f"(GEOMETRIC_REPRESENTATION_CONTEXT(3)GLOBAL_UNIT_ASSIGNED_CONTEXT((#{lu},#{au},#{su}))REPRESENTATION_CONTEXT('Context #1','3D Context with UNIT and UNCERTAINTY'))")
sh=E(f"CLOSED_SHELL('',({','.join('#'+str(f) for f in faces)}))")
E(f"MANIFOLD_SOLID_BREP('Box with hole',#{sh})")
print("ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('hand-built test'),'2;1');\nFILE_NAME('box_hole.step','2026-10-04',(''),(''),'','','');\nFILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }'));\nENDSEC;\nDATA;")
print("\n".join(L)); print("ENDSEC;\nEND-ISO-10303-21;")
