import numpy as np
p0=np.array([-12.318266288,11.561173044,-13.205993416]); a=np.array([0.657930019,-0.570603778,0.491466599]); a/=np.linalg.norm(a)
p1=np.array([5.642541888,-4.015719181,0.210535788]); H=(p1-p0)@a; R=9.352065559
cb=np.array([-8.351230316,-2.794054002,-13.353165515]); w=np.array([-0.123990405,0.002315895,-0.992280714]); w=-w/np.linalg.norm(w)  # toward apex? check
r0=7.807076509; ang=0.706589183; h=r0/np.tan(ang)
# decide apex direction: cone surface axis #17 points -w; apex lies along +w? try both via common volume plausibility
def area_slices(wdir, nt, nth):
    e1=np.cross(wdir,[1,0,0]); e1/=np.linalg.norm(e1); e2=np.cross(wdir,e1)
    th=np.linspace(0,2*np.pi,nth,endpoint=False); d=np.outer(np.cos(th),e1)+np.outer(np.sin(th),e2)
    da=d@a; dperp=d-np.outer(da,a); A=np.einsum('ij,ij->i',dperp,dperp)
    ts=np.linspace(0,h,nt+1); areas=[]
    for t in ts:
        c=cb+t*wdir; rho=r0*(1-t/h)
        q=c-p0; qa=q@a; qperp=q-qa*a
        B=2*(dperp@qperp); C=qperp@qperp-R*R
        disc=B*B-4*A*C
        lo=np.zeros(nth); hi=np.full(nth,rho)
        ok=disc>0; sq=np.sqrt(np.where(ok,disc,0))
        r1=(-B-sq)/(2*A); r2=(-B+sq)/(2*A)
        lo=np.maximum(lo,np.where(ok,r1,np.inf)); hi=np.minimum(hi,np.where(ok,r2,-np.inf))
        # 0 <= qa + r da <= H
        with np.errstate(divide='ignore',invalid='ignore'):
            ra=(0-qa)/da; rb=(H-qa)/da
        lo1=np.where(da>0,ra,np.where(da<0,rb,np.where((qa>=0)&(qa<=H),-np.inf,np.inf)))
        hi1=np.where(da>0,rb,np.where(da<0,ra,np.where((qa>=0)&(qa<=H),np.inf,-np.inf)))
        lo=np.maximum(lo,lo1); hi=np.minimum(hi,hi1)
        seg=np.where(hi>lo,0.5*(hi*hi-lo*lo),0)
        areas.append(seg.sum()*2*np.pi/nth)
    areas=np.array(areas); hstep=h/nt
    simp=hstep/3*(areas[0]+areas[-1]+4*areas[1:-1:2].sum()+2*areas[2:-1:2].sum())
    return simp
for wd in (w,-w):
    print([area_slices(wd,n,m) for n,m in ((2000,8000),(4000,16000),(8000,32000))])
