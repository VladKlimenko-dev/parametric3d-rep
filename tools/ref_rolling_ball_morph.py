# Независимая проверка модели катящегося шара: удалённое = точки тела, не покрытые ни одним шаром радиуса r,
# целиком лежащим в теле локально (центр c: |c_yz| <= R-r, |c_xy| >= rho+r). Интеграл по столбцам вдоль z.
import numpy as np, math
R, rho, r = 20.0, 4.0, 1.0
sR, sP = R - r, rho + r
def dist_C(x, y, z):
    # расстояние от точки до множества центров C = {|c_yz|<=sR} ∩ {|c_xy|>=sP}
    ryz = math.hypot(y, z); rxy = math.hypot(x, y)
    inA = ryz <= sR; inB = rxy >= sP
    if inA and inB: return 0.0
    best = float('inf')
    # проекция на ∂A (цилиндр радиуса sR вокруг X): точка (x, y*sR/ryz, z*sR/ryz)
    if ryz > 0:
        py, pz = y * sR / ryz, z * sR / ryz
        if math.hypot(x, py) >= sP: best = min(best, abs(ryz - sR))
    # проекция на ∂B (цилиндр радиуса sP вокруг Z)
    if rxy > 0:
        px, py = x * sP / rxy, y * sP / rxy
        if math.hypot(py, z) <= sR: best = min(best, abs(rxy - sP))
    # кривая ∂A∩∂B: (sP cos t, sP sin t, ±sqrt(sR^2 - sP^2 sin^2 t)) — верхняя ветвь
    ts = np.linspace(-math.pi, math.pi, 721)
    cx = sP*np.cos(ts); cy = sP*np.sin(ts); cz = np.sqrt(sR*sR - cy*cy)
    d = np.sqrt((cx-x)**2 + (cy-y)**2 + (cz-z)**2); i = int(np.argmin(d))
    a, b = ts[max(i-1,0)], ts[min(i+1,len(ts)-1)]
    f = lambda t: math.dist((sP*math.cos(t), sP*math.sin(t), math.sqrt(sR*sR - (sP*math.sin(t))**2)), (x, y, z))
    for _ in range(80):
        m1 = a + (b-a)/3; m2 = b - (b-a)/3
        if f(m1) < f(m2): b = m2
        else: a = m1
    best = min(best, f((a+b)/2))
    return best
def removed(x, y, z): return dist_C(x, y, z) > r
def column(x, y):
    zt = math.sqrt(R*R - y*y)
    if not removed(x, y, zt - 1e-12): return 0.0
    lo, hi = zt - 3.0, zt   # removed at hi, kept at lo
    if removed(x, y, lo): raise Exception('column too long')
    for _ in range(60):
        m = (lo + hi) / 2
        if removed(x, y, m): hi = m
        else: lo = m
    return zt - hi
# полярные координаты вокруг оси Z: ρ ∈ [rho, rho+2r], θ ∈ [0, 2π); симметрия: четверть × 4
nth, nr = 16, 32
gx, gw = np.polynomial.legendre.leggauss(8)
tot = 0.0
for i in range(nth):
    for a, wa in zip(gx, gw):
        th = (math.pi/2) * (i + (a+1)/2) / nth; wth = wa/2 * (math.pi/2) / nth
        # граница опоры по ρ — бисекцией
        lo, hi = rho, rho + 2.5*r
        for _ in range(30):
            m = (lo+hi)/2
            if column(m*math.cos(th), m*math.sin(th)) > 0: lo = m
            else: hi = m
        rmax = lo
        for j in range(nr // 8):
            for b, wb in zip(gx, gw):
                rr = rho + (rmax - rho) * (j + (b+1)/2) / (nr//8); wr = wb/2 * (rmax - rho) / (nr//8)
                tot += column(rr*math.cos(th), rr*math.sin(th)) * rr * wr * wth
print('removed volume (full rim):', 4*tot)
