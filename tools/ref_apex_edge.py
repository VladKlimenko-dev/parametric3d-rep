"""KRN-06 (06.1/06.9): независимый эталон снятого объёма скругления ОДНОГО ребра пирамиды (вершина степени n > 3).
Снятое = уголок ребра (сечение S: дельтоид «ребро — точки касания — центр шара» минус сектор) ∩ тело (полупространства
остальных граней и основания). Для точки сечения (a, b) отрезок вдоль ребра внутри тела — линейные неравенства по s;
объём = ∫∫_S L(a, b) dA, полярные координаты от ребра, адаптивная квадратура (scipy dblquad, 1e-13).
Пирамида — как PyramidBody (K01): грани q через вершину (20, 20, 40), наклон sl_q (0.5 или slope2 у q = 1), азимут 360q/n.
Аргументы: n slope2 r ang (азимут ребра в градусах)."""
import sys, math
import numpy as np
from scipy.integrate import dblquad

def faces(n, slope2):
    A = np.array([20.0, 20.0, 40.0]); res = []
    for q in range(n):
        sl = slope2 if q == 1 else 0.5; th = 2 * math.pi * q / n
        nrm = np.array([math.cos(th), math.sin(th), sl]); res.append((nrm / np.linalg.norm(nrm), A.copy()))
    return res

def removed(n, slope2, r, ang):
    F = faces(n, slope2)
    # ребро между гранями q и q+1, где азимут биссектрисы ≈ ang
    best = None
    for q in range(n):
        th = (360.0 * (q + 0.5) / n) % 360
        if abs(((th - ang + 540) % 360) - 180) < 1e-6 or best is None and False: best = q
    if best is None:
        # азимут ребра — по самому ребру: прямая пересечения граней q, q+1
        for q in range(n):
            n0, a0 = F[q]; n1, _ = F[(q + 1) % n]; d = np.cross(n0, n1); d = d if d[2] < 0 else -d
            az = math.degrees(math.atan2(d[1], d[0])) % 360
            if abs(((az - ang + 540) % 360) - 180) < 1: best = q
    q = best; (n0, A), (n1, _) = F[q], F[(q + 1) % n]
    t = np.cross(n0, n1); t = t / np.linalg.norm(t)
    if t[2] > 0: t = -t                                   # от вершины вниз
    others = [F[k] for k in range(n) if k not in (q, (q + 1) % n)]
    base = (np.array([0, 0, -1.0]), np.array([0, 0, 0.0]))
    cons = others + [base]
    # сечение в плоскости ⟂ t через вершину: оси u1 — в грани 0 от ребра внутрь, u2 — в грани 1
    u1 = np.cross(t, n0); u1 = u1 if np.dot(u1, n1) < 0 else -u1; u1 /= np.linalg.norm(u1)
    u2 = np.cross(t, n1); u2 = u2 if np.dot(u2, n0) < 0 else -u2; u2 /= np.linalg.norm(u2)
    c12 = np.dot(n0, n1)
    C = A + (-(n0 + n1)) * (r / (1 + c12))               # центр шара (выпукло: внутрь)
    beta = math.acos(np.clip(np.dot(u1, u2), -1, 1))
    e1 = u1; e2 = u2 - u1 * np.dot(u1, u2); e2 /= np.linalg.norm(e2)
    def rho_max(psi):
        d = e1 * math.cos(psi) + e2 * math.sin(psi)
        w = A - C; bq = 2 * np.dot(w, d); cq = np.dot(w, w) - r * r; disc = bq * bq - 4 * cq
        return (-bq - math.sqrt(max(disc, 0))) / 2
    def L(rho, psi):
        p0 = A + (e1 * math.cos(psi) + e2 * math.sin(psi)) * rho
        lo, hi = -1e9, 1e9
        for nn, pp in cons:                                # nn·(p0 + s t − pp) ≤ 0
            a = np.dot(nn, t); b = np.dot(nn, p0 - pp)
            if abs(a) < 1e-15:
                if b > 0: return 0.0
                continue
            if a > 0: hi = min(hi, -b / a)
            else: lo = max(lo, -b / a)
        return max(0.0, hi - lo) * rho
    v, err = dblquad(lambda rho, psi: L(rho, psi), 0, beta, lambda psi: 0, rho_max, epsabs=1e-13, epsrel=1e-13)
    return v, err

if __name__ == "__main__":
    n, s2, r, ang = int(sys.argv[1]), float(sys.argv[2]), float(sys.argv[3]), float(sys.argv[4])
    v, e = removed(n, s2, r, ang)
    print(repr(v), e)
