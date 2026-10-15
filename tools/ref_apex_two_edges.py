"""KRN-06 (06.1/06.9): независимый эталон снятого объёма скругления ДВУХ рёбер пирамиды у вершины степени n > 3.
Снятое = (уголок₁ ∪ уголок₂) ∩ тело; уголок ребра — дельтоид «ребро — точки касания — ось шара» минус круг (сечение),
продлённый вдоль ребра. V = V₁ + V₂ − V₁₂: Vᵢ — как в ref_apex_edge.py (∫∫ по сечению уголка длины отрезка вдоль ребра внутри
тела), V₁₂ — ∫∫ по сечению уголка₁ длины множества точек прямой вдоль ребра₁ внутри тела и уголка₂ (дельтоид₂ — четыре
полупространства: отрезок; круг₂ — квадратное уравнение: вычитается отрезок внутри круга). Адаптивная квадратура scipy.
Пирамида — как PyramidBody (K01): грани q через вершину (20, 20, 40), наклон sl_q (0.5 или slope2 у q = 1), азимут 360q/n.
Аргументы: n slope2 r qa qb (рёбра — между гранями q и q + 1); три и больше рёбер — включение-исключение по подмножествам
(пересечение уголков — пересечение отрезков на прямой вдоль первого ребра)."""
import sys, math
import numpy as np
from scipy.integrate import dblquad

A = np.array([20.0, 20.0, 40.0])

def faces(n, slope2):
    res = []
    for q in range(n):
        sl = slope2 if q == 1 else 0.5; th = 2 * math.pi * q / n
        nrm = np.array([math.cos(th), math.sin(th), sl]); res.append(nrm / np.linalg.norm(nrm))
    return res

class Corner:
    def __init__(self, F, n, q, r):
        n0, n1 = F[q], F[(q + 1) % n]
        t = np.cross(n0, n1); t /= np.linalg.norm(t)
        if t[2] > 0: t = -t
        self.t, self.n0, self.n1, self.r = t, n0, n1, r
        self.others = [F[k] for k in range(n) if k not in (q, (q + 1) % n)]
        u1 = np.cross(t, n0); u1 = u1 if np.dot(u1, n1) < 0 else -u1; u1 /= np.linalg.norm(u1)
        u2 = np.cross(t, n1); u2 = u2 if np.dot(u2, n0) < 0 else -u2; u2 /= np.linalg.norm(u2)
        c12 = np.dot(n0, n1)
        self.C = A + (-(n0 + n1)) * (r / (1 + c12))
        self.beta = math.acos(np.clip(np.dot(u1, u2), -1, 1))
        self.e1 = u1; e2 = u2 - u1 * np.dot(u1, u2); self.e2 = e2 / np.linalg.norm(e2)
        # дельтоид: грани (n·(x − A) ≤ 0) и прямые «ось — точка касания» (сторона ребра)
        Qa = self.C + n0 * r; Qb = self.C + n1 * r
        ma = np.cross(t, n0); ma = ma if np.dot(ma, A - self.C) > 0 else -ma
        mb = np.cross(t, n1); mb = mb if np.dot(mb, A - self.C) > 0 else -mb
        self.kite = [(n0, A), (n1, A), (-ma, self.C), (-mb, self.C)]      # nn·(x − pp) ≤ 0

    def rho_max(self, psi):
        d = self.e1 * math.cos(psi) + self.e2 * math.sin(psi)
        w = A - self.C; bq = 2 * np.dot(w, d); cq = np.dot(w, w) - self.r * self.r; disc = bq * bq - 4 * cq
        return (-bq - math.sqrt(max(disc, 0))) / 2

    def point(self, rho, psi):
        return A + (self.e1 * math.cos(psi) + self.e2 * math.sin(psi)) * rho

def interval(cons, p0, t):
    lo, hi = -1e9, 1e9
    for nn, pp in cons:
        a = np.dot(nn, t); b = np.dot(nn, p0 - pp)
        if abs(a) < 1e-15:
            if b > 0: return None
            continue
        if a > 0: hi = min(hi, -b / a)
        else: lo = max(lo, -b / a)
    return (lo, hi) if hi > lo else None

def body_cons(F):
    return [(f, A) for f in F] + [(np.array([0, 0, -1.0]), np.array([0, 0, 0.0]))]

def single(c, F):
    cons = c.others + [np.array([0, 0, -1.0])]
    cons = [(f, A) for f in c.others] + [(np.array([0, 0, -1.0]), np.array([0, 0, 0.0]))]
    def L(rho, psi):
        iv = interval(cons, c.point(rho, psi), c.t)
        return (iv[1] - iv[0]) * rho if iv else 0.0
    return dblquad(L, 0, c.beta, lambda psi: 0, c.rho_max, epsabs=1e-13, epsrel=1e-13)[0]

def overlap(c1, c2, F):
    bc = body_cons(F)
    def L(rho, psi):
        p0 = c1.point(rho, psi); t = c1.t
        iv = interval(bc + c2.kite, p0, t)
        if not iv: return 0.0
        lo, hi = iv; ln = hi - lo
        # минус часть внутри круга₂ (цилиндр с осью через C₂ вдоль t₂)
        w = p0 - c2.C; d = t
        wp = w - c2.t * np.dot(w, c2.t); dp = d - c2.t * np.dot(d, c2.t)
        aq = np.dot(dp, dp); bq = 2 * np.dot(wp, dp); cq = np.dot(wp, wp) - c2.r ** 2
        if aq > 1e-30:
            disc = bq * bq - 4 * aq * cq
            if disc > 0:
                s1 = (-bq - math.sqrt(disc)) / (2 * aq); s2 = (-bq + math.sqrt(disc)) / (2 * aq)
                ln -= max(0.0, min(hi, s2) - max(lo, s1))
        elif cq < 0: ln = 0.0
        return ln * rho
    return dblquad(L, 0, c1.beta, lambda psi: 0, c1.rho_max, epsabs=1e-13, epsrel=1e-13)[0]

def corner_set(c2, p0, t, lo, hi):
    """Отрезки s ∈ [lo, hi] прямой p0 + s·t внутри уголка c2 (дельтоид₂ минус круг₂)."""
    iv = interval(c2.kite, p0, t)
    if not iv: return []
    a, b = max(lo, iv[0]), min(hi, iv[1])
    if b <= a: return []
    w = p0 - c2.C; wp = w - c2.t * np.dot(w, c2.t); dp = t - c2.t * np.dot(t, c2.t)
    aq = np.dot(dp, dp); bq = 2 * np.dot(wp, dp); cq = np.dot(wp, wp) - c2.r ** 2
    if aq > 1e-30:
        disc = bq * bq - 4 * aq * cq
        if disc > 0:
            s1 = (-bq - math.sqrt(disc)) / (2 * aq); s2 = (-bq + math.sqrt(disc)) / (2 * aq)
            out = []
            if s1 > a: out.append((a, min(b, s1)))
            if s2 < b: out.append((max(a, s2), b))
            return [x for x in out if x[1] > x[0]]
    elif cq < 0: return []
    return [(a, b)]

def inter(L1, L2):
    out = []
    for a1, b1 in L1:
        for a2, b2 in L2:
            a, b = max(a1, a2), min(b1, b2)
            if b > a: out.append((a, b))
    return out

def overlap_many(c1, others, F):
    bc = body_cons(F)
    def L(rho, psi):
        p0 = c1.point(rho, psi); iv = interval(bc, p0, c1.t)
        if not iv: return 0.0
        S = [iv]
        for c in others:
            S = inter(S, corner_set(c, p0, c1.t, iv[0], iv[1]))
            if not S: return 0.0
        return sum(b - a for a, b in S) * rho
    return dblquad(L, 0, c1.beta, lambda psi: 0, c1.rho_max, epsabs=1e-13, epsrel=1e-13)[0]

def removed_union(n, s2, r, qs):
    """Включение-исключение по всем подмножествам рёбер."""
    from itertools import combinations
    F = faces(n, s2); C = [Corner(F, n, q, r) for q in qs]; tot = 0.0
    for k in range(1, len(C) + 1):
        for sub in combinations(range(len(C)), k):
            v = single(C[sub[0]], F) if k == 1 else overlap_many(C[sub[0]], [C[j] for j in sub[1:]], F)
            tot += v if k % 2 == 1 else -v
    return tot

if __name__ == "__main__" and len(sys.argv) > 6:
    n, s2, r = int(sys.argv[1]), float(sys.argv[2]), float(sys.argv[3])
    print(repr(removed_union(n, s2, r, [int(x) for x in sys.argv[4:]])))
    sys.exit(0)

if __name__ == "__main__":
    n, s2, r, qa, qb = int(sys.argv[1]), float(sys.argv[2]), float(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5])
    F = faces(n, s2)
    c1, c2 = Corner(F, n, qa, r), Corner(F, n, qb, r)
    v1, v2, v12 = single(c1, F), single(c2, F), overlap(c1, c2, F)
    print(repr(v1 + v2 - v12), repr(v1), repr(v2), repr(v12))
