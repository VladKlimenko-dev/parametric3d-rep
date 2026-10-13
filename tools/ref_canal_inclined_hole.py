# KRN-06 (06.14): независимый эталон объёма скругления кромки НАКЛОННОГО отверстия (плита z ≤ 20, отверстие R вдоль оси,
# наклонённой на a вокруг X через (30, 20, 10)). Катящийся шар радиуса r: центры — пересечение плоскости z = 20 − r и
# цилиндра R + r; снятая область ограничена кольцом верхней плоскости, полосой стенки и каналовой поверхностью;
# объём — теорема Гаусса с полем (0, 0, z): плоскость — площадь по спектральной формуле, стенка — по s точно, канал —
# Гаусс–Лежандр поперёк и трапеции (спектрально точные для периодических функций) вдоль. Проверка: a = 0 — формула Паппа.
import numpy as np, math
def removed(adeg, R=5.0, r=1.5, ztop=20.0, A0=np.array([30.,20.,10.]), N=4000, M=48):
    a=math.radians(adeg)
    w=np.array([0,-math.sin(a),math.cos(a)]); u=np.array([1.,0,0]); v=np.cross(w,u)
    th=np.linspace(0,2*np.pi,N,endpoint=False); dth=2*np.pi/N
    def pt(rho,th,s): return A0[None,:]+np.outer(s,w)+rho*(np.outer(np.cos(th),u)+np.outer(np.sin(th),v))
    def s_at(rho,th,z): return (z-A0[2]-rho*np.sin(th)*v[2])/w[2]
    zc=ztop-r
    # top plane piece: area between P(th)=c(th)+(0,0,r) and rim
    def area(curve_xy):  # shoelace with spectral derivative via FFT
        x,y=curve_xy[:,0],curve_xy[:,1]
        k=np.fft.fftfreq(N,d=1.0/N)*1j
        dx=np.real(np.fft.ifft(k*np.fft.fft(x))); dy=np.real(np.fft.ifft(k*np.fft.fft(y)))
        return 0.5*np.sum(x*dy-y*dx)*dth
    c=pt(R+r,th,s_at(R+r,th,zc)); P=c+np.array([0,0,r]); rim=pt(R,th,s_at(R,th,ztop))
    Atop=abs(area(P))-abs(area(rim))
    top=ztop*Atop
    # wall piece: s from s(th) (contact, same s as spine) to s_rim; n_out = -radial, dA = R dth ds
    s1=s_at(R+r,th,zc); s2=s_at(R,th,ztop)
    rz=np.sin(th)*v[2]
    # ∫ z ds, z = A0z + s w_z + R rz
    zint=(A0[2]+R*rz)*(s2-s1)+w[2]*(s2**2-s1**2)/2
    wall=np.sum(zint*(-rz)*R)*dth
    # canal piece
    xg,wg=np.polynomial.legendre.leggauss(M)
    def X(thv,psi):
        s=s_at(R+r,thv,zc)
        cc=pt(R+r,thv,s)
        rad=np.outer(np.cos(thv),u)+np.outer(np.sin(thv),v)
        m1=np.tile(np.array([0,0,1.]),(len(thv),1)); m2=-rad
        ww=m2-m1*np.sum(m1*m2,1)[:,None]; ww/=np.linalg.norm(ww,axis=1)[:,None]
        ang=np.arccos(np.clip(np.sum(m1*m2,1),-1,1))
        ps=psi*ang
        m=np.cos(ps)[:,None]*m1+np.sin(ps)[:,None]*ww
        return cc+r*m, m, ang
    can=0.0
    for xi,wi in zip(xg,wg):
        t=(xi+1)/2
        h=1e-5
        Xc,m,ang=X(th,t)
        Xt=(X(th+h,t)[0]-X(th-h,t)[0])/(2*h)
        Xp=(X(th,t+h)[0]-X(th,t-h)[0])/(2*h)
        nrm=np.cross(Xt,Xp)
        sgn=-np.sign(np.sum(nrm*(-m),1))  # make it align with -m
        nrm=nrm*(-sgn)[:,None]
        can+=wi*0.5*np.sum(Xc[:,2]*nrm[:,2])*dth
    return top+wall+can
k=1-math.pi/4; g=(10-3*math.pi)/(3*(4-math.pi))
print('a=0 r=1.5 script',repr(removed(0)),'pappus',repr(k*1.5**2*2*math.pi*(5+g*1.5)))
for a,r in [(10,1.5),(25,1.5),(25,2.0),(40,1.0)]:
    print('a',a,'r',r,repr(removed(a,r=r,N=4000)),repr(removed(a,r=r,N=8000)))
