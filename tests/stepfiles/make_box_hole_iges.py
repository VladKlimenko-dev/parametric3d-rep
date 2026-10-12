# Hand-built IGES 5.3 file (not produced by Parametric3D): box 60x40x20 with a through hole R8 at
# (30,20). Standard conventions: DE pointers (odd line numbers), 144 boundaries via 142 curves on
# surface, cylinder as 120 (line revolved about an axis), Fortran 'D' exponents, the hole rim at
# the top written as an arc in its own plane moved by a 124 transformation matrix.
import math
ents=[]   # (type, params list of str, matrix_de, form)
def add(t, params, matrix=0, form=0):
    ents.append((t, params, matrix, form)); return 2*len(ents)-1
def f(x): return ("%.10E" % x).replace('E','D')        # Fortran style exponents
def P(*xs): return [f(x) for x in xs]
def line(a,b): return add(110, P(*a,*b))
def arc_xy(z, c, s, e, matrix=0): return add(100, P(z, c[0],c[1], s[0],s[1], e[0],e[1]), matrix)
def comp(curves): return add(102, [str(len(curves))]+[str(c) for c in curves])
def plane(n, d): return add(108, P(*n, d)+['0']+P(0,0,0,0))
def cos(surface, curve): return add(142, ['1', str(surface), '0', str(curve), '2'])
def trimmed(surface, outer, holes=()):
    return add(144, [str(surface), '1', str(len(holes)), str(cos(surface, outer))]+[str(cos(surface,h)) for h in holes])
B=[(0,0,0),(60,0,0),(60,40,0),(0,40,0),(0,0,20),(60,0,20),(60,40,20),(0,40,20)]
def quad(i,j,k,l): return comp([line(B[i],B[j]),line(B[j],B[k]),line(B[k],B[l]),line(B[l],B[i])])
R=8.0; cx,cy=30.0,20.0
# hole rims: bottom as a plain arc at z=0; top as an arc at z=0 moved up by a 124 matrix (translation 20)
rim_bot=arc_xy(0.0,(cx,cy),(cx+R,cy),(cx+R,cy))
m=add(124, P(1,0,0,0, 0,1,0,0, 0,0,1,20))
rim_top=arc_xy(0.0,(cx,cy),(cx+R,cy),(cx+R,cy), matrix=m)
# faces
pb=plane((0,0,1),0.0); trimmed(pb, quad(0,3,2,1), [rim_bot])
pt=plane((0,0,1),20.0); trimmed(pt, quad(4,5,6,7), [rim_top])
for (i,j,k,l,n,d) in [(0,1,5,4,(0,-1,0),0.0),(1,2,6,5,(1,0,0),60.0),(2,3,7,6,(0,1,0),40.0),(3,0,4,7,(-1,0,0),0.0)]:
    trimmed(plane(n,d), quad(i,j,k,l))
axis=line((cx,cy,0),(cx,cy,20)); gen=line((cx+R,cy,0),(cx+R,cy,20))
cyl=add(120, [str(axis), str(gen)]+P(0, 2*math.pi))
seam=line((cx+R,cy,0),(cx+R,cy,20))
trimmed(cyl, comp([rim_bot, seam, rim_top, seam]))
# --- write fixed-format file
def chunks(s,n):
    out=[]; cur=''
    for tok in s.split(','):
        piece=tok+','
        if len(cur)+len(piece)>n: out.append(cur); cur=''
        cur+=piece
    if cur: out.append(cur)
    return out
lines=[]
lines.append("hand-built IGES test (other system style)".ljust(72)+"S"+"1".rjust(7))
g="1H,,1H;,4HTEST,8HTEST.IGS,4HHAND,4HHAND,32,38,6,308,15,4HTEST,1.0D0,2,2HMM,1,0.08,15H20261004.120000,1.0D-6,1000.0,4HHAND,4HHAND,11,0,15H20261004.120000;"
gl=[g[i:i+72] for i in range(0,len(g),72)]
for i,c in enumerate(gl): lines.append(c.ljust(72)+"G"+str(i+1).rjust(7))
D=[]; Pl=[]; p=1
for idx,(t,params,matrix,form) in enumerate(ents):
    de=2*idx+1
    body=",".join([str(t)]+params)+";"
    parts=chunks(body[:-1],64); parts[-1]=parts[-1].rstrip(',')+';'
    parts=[x if x.endswith(';') else x for x in parts]
    start=p
    for c in parts:
        Pl.append(c.ljust(64)+str(de).rjust(8)+"P"+str(p).rjust(7)); p+=1
    d1="%8d%8d%8d%8d%8d%8d%8d%8d%8s"%(t,start,0,0,0,0,matrix,0,"00000000")
    d2="%8d%8d%8d%8d%8d%8s%8s%8s%8d"%(t,0,0,len(parts),form,"","","",0)
    D.append(d1.ljust(72)+"D"+str(de).rjust(7)); D.append(d2.ljust(72)+"D"+str(de+1).rjust(7))
lines+=D+Pl
lines.append(("S%7dG%7dD%7dP%7d"%(1,len(gl),len(D),len(Pl))).ljust(72)+"T"+"1".rjust(7))
print("\n".join(lines))
