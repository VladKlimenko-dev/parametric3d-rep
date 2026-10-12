using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Analytic;

/// <summary>
/// g.05: РАЗБИЕНИЕ ГРАНИ ТОРА кусками пересечения. Параметры (θ, φ) — оба периодичны; карта сохраняет ориентацию (якобиан
/// diag(R + r·cos φ, r) положителен), поэтому обход «наибольший левый поворот» по развёртке строит циклы правильно.
/// Циклы: стягиваемые (обороты (0, 0)) — площадь со знаком: > 0 — область-диск слева, ≤ 0 — отверстие; нестягиваемые —
/// только параллельные одному классу (1, 0) или (0, 1): они режут тор на пояса, пара соседних по поперечному углу
/// (циклически) ограничивает пояс. Отверстия — в наименьший содержащий диск, иначе в пояс, иначе (нестягиваемых нет) — в
/// «остаток» тора вне всех дисков. Диагональные классы (p, q ≠ 0), разные классы — отказ (честно, многогранный путь).
/// Точка области — сдвиг влево от куска контура, если путь сдвига не пересекает ни одной кривой и зазор до них больше допуска.
/// </summary>
public static partial class FaceSplit
{
    static readonly bool MultiBandOff = Environment.GetEnvironmentVariable("P3D_TORMULTIBAND") == "0";
    static readonly bool PolyStartOff = Environment.GetEnvironmentVariable("P3D_TORPOLYSTART") == "0";
    static List<Region>? TorusSplit(AnalyticFace f, IReadOnlyList<FaceIntersect.Piece> pieces, IReadOnlyList<Vector3d>? globalCuts, out string why)
    {
        why = "";
        // знак: развёртка (θ, φ) ориентирована по наружной нормали тора; грань может смотреть внутрь
        double sign;
        {
            var probe = f.TorusPoint(0.37, 0.41); var (th, ph) = f.TorusAngles(probe);
            var radial = f.U * Math.Cos(th) + f.V * Math.Sin(th);
            var outward = radial * Math.Cos(ph) + f.W * Math.Sin(ph);
            sign = Vector3d.Dot(f.Face.NormalAt(probe), outward) >= 0 ? 1 : -1;
        }
        Vector2d A(Vector3d x) { var (t, p) = f.TorusAngles(x); return new Vector2d(t, p); }
        Vector2d Near(Vector2d a, Vector2d r) => new(r.X + Math.IEEERemainder(a.X - r.X, 2 * Math.PI), r.Y + Math.IEEERemainder(a.Y - r.Y, 2 * Math.PI));
        Vector2d P(Vector3d x, Vector2d r) => Near(A(x), r);

        var nodes = new List<Vector3d>();
        int Node(Vector3d x) { for (int i = 0; i < nodes.Count; i++) if ((nodes[i] - x).Length < TolV) return i; nodes.Add(x); return nodes.Count - 1; }
        var darts = new List<Dart>();
        var cuts = new List<Vector3d>();
        if (globalCuts != null) cuts.AddRange(globalCuts); else foreach (var p in pieces) if (!p.Closed) { cuts.Add(p.P0); cuts.Add(p.P1); }
        var useCount = new Dictionary<TEdge, int>(ReferenceEqualityComparer.Instance);
        foreach (var ce in f.Face.Loops.SelectMany(l => l.CoEdges)) useCount[ce.Edge] = useCount.GetValueOrDefault(ce.Edge) + 1;
        foreach (var ce in f.Face.Loops.SelectMany(l => l.CoEdges))
        {
            if (useCount[ce.Edge] > 1) continue;
            var c = ce.Edge.Curve;
            double a0 = ce.Reversed ? c.T1 : c.T0, a1 = ce.Reversed ? c.T0 : c.T1;
            var inner = new List<double>();
            foreach (var x in cuts)
            {
                double t = ClosestParam(c, x);
                if ((c.PointAt(t) - x).Length < TolV && Math.Min(Math.Abs(t - c.T0), Math.Abs(t - c.T1)) > 1e-12 * Math.Max(1, c.T1 - c.T0)) inner.Add(t);
            }
            var seq = new List<double> { a0 }; seq.AddRange(inner.OrderBy(t => (t - a0) / (a1 - a0))); seq.Add(a1);
            bool closedEdge = (c.PointAt(c.T0) - c.PointAt(c.T1)).Length < TolV;
            for (int i = 0; i + 1 < seq.Count; i++)
            {
                if (Math.Abs(seq[i + 1] - seq[i]) < 1e-15) continue;
                var d = new Dart { Curve = c, T0 = seq[i], T1 = seq[i + 1], BoundaryEdge = ce.Edge };
                d.From = Node(i == 0 ? ce.Start.P : d.At(0)); d.To = Node(i + 2 == seq.Count ? ce.End.P : d.At(1));
                if (d.From == d.To && !(closedEdge && seq.Count == 2)) continue;
                darts.Add(d);
            }
        }
        foreach (var p in pieces)
        {
            var d1 = new Dart { Curve = p.Curve, T0 = p.T0, T1 = p.T1, IsPiece = true };
            var d2 = new Dart { Curve = p.Curve, T0 = p.T1, T1 = p.T0, IsPiece = true };
            d1.From = Node(d1.At(0)); d1.To = Node(d1.At(1)); d2.From = d1.To; d2.To = d1.From;
            d1.Twin = d2; d2.Twin = d1;
            darts.Add(d1); darts.Add(d2);
        }
        if (darts.Count == 0) return new List<Region>();

        Vector2d Dir(Dart d, bool outgoing)
        {
            var x0 = outgoing ? d.At(0) : d.At(1); var a = A(x0);
            for (double s = 1e-6; s <= 0.25; s *= 4)
            {
                var b = P(outgoing ? d.At(s) : d.At(1 - s), a); var v = outgoing ? b - a : a - b;
                v = new Vector2d(v.X * sign, v.Y);         // ориентация грани
                if (v.Length > 1e-12) return v.Normalized();
            }
            return Vector2d.UnitX;
        }
        var outOf = new Dictionary<int, List<Dart>>();
        foreach (var d in darts) { if (!outOf.TryGetValue(d.From, out var l)) outOf[d.From] = l = new(); l.Add(d); }
        Dart? Next(Dart cur, List<Dart> cand)
        {
            var din = Dir(cur, false); Dart? best = null; double bestTurn = double.MinValue;
            foreach (var d in cand)
            {
                if (ReferenceEquals(d, cur.Twin) && cand.Count > 1) continue;
                var dout = Dir(d, true);
                double turn = Math.Atan2(din.X * dout.Y - din.Y * dout.X, Vector2d.Dot(din, dout));
                if (turn > bestTurn) { bestTurn = turn; best = d; }
            }
            return best;
        }
        var cycles = new List<(List<Dart> Darts, List<Vector2d> Poly, int Wt, int Wp, double Area)>();
        foreach (var start in darts)
        {
            if (start.Used) continue;
            var cyc = new List<Dart>(); var cur = start;
            for (int guard = 0; ; guard++)
            {
                if (guard > darts.Count) { why = "torus: a face cycle does not close"; return null; }
                cur.Used = true; cyc.Add(cur);
                var nx = Next(cur, outOf.GetValueOrDefault(cur.To) ?? new());
                if (nx == null) { why = "torus: a dead end in the face graph"; return null; }
                if (ReferenceEquals(nx, start)) break;
                if (nx.Used) { why = "torus: a face cycle runs into a used piece"; return null; }
                cur = nx;
            }
            // ломаная в развёртке (непрерывная), обороты, площадь (в ориентации грани)
            var poly = new List<Vector2d>(); Vector2d rf = A(cyc[0].At(0)); poly.Add(rf);
            foreach (var d in cyc)
            {
                int m = Math.Max(16, (int)Math.Ceiling(Length(d) * 4));
                for (int i = 1; i <= m; i++) { var q = P(d.At((double)i / m), poly[^1]); poly.Add(q); }
            }
            int wt = (int)Math.Round((poly[^1].X - poly[0].X) / (2 * Math.PI)), wp = (int)Math.Round((poly[^1].Y - poly[0].Y) / (2 * Math.PI));
            double area = 0;
            for (int i = 0; i + 1 < poly.Count; i++) area += 0.5 * (poly[i].X * poly[i + 1].Y - poly[i + 1].X * poly[i].Y);
            cycles.Add((cyc, poly, wt * (int)sign, wp, area * sign));
        }
        // классы нестягиваемых циклов
        var wrapping = cycles.Where(c => c.Wt != 0 || c.Wp != 0).ToList();
        var contractible = cycles.Where(c => c.Wt == 0 && c.Wp == 0).ToList();
        if (wrapping.Any(c => c.Wt != 0 && c.Wp != 0)) { why = "torus: a diagonal (p, q) loop (not supported)"; return null; }
        bool byTheta = wrapping.Any(c => c.Wt != 0), byPhi = wrapping.Any(c => c.Wp != 0);
        if (byTheta && byPhi) { why = "torus: loops of different classes"; return null; }

        var regions = new List<Region>();
        var regionOfCycle = new Dictionary<int, Region>();
        var bandPairs = new List<(int Plus, int Minus, Region R)>();
        // поперечный угол цикла на заданном продольном значении (по ломаной цикла, с пересечением линии along = const)
        double AcrossAtAlong(int ci, double along, bool thetaClass)
        {
            var poly = cycles[ci].Poly; double best = double.NaN, bestD = double.MaxValue;
            for (int i = 0; i + 1 < poly.Count; i++)
            {
                double a = thetaClass ? poly[i].X : poly[i].Y, b = thetaClass ? poly[i + 1].X : poly[i + 1].Y;
                double sh = 2 * Math.PI * Math.Round(((a + b) / 2 - along) / (2 * Math.PI)); double ra = along + sh;
                if ((ra - a) * (ra - b) <= 0 && Math.Abs(b - a) > 1e-15)
                {
                    double t = (ra - a) / (b - a); double v = thetaClass ? poly[i].Y + (poly[i + 1].Y - poly[i].Y) * t : poly[i].X + (poly[i + 1].X - poly[i].X) * t;
                    return v - 2 * Math.PI * Math.Floor(v / (2 * Math.PI));
                }
                double dd = Math.Abs(a - ra); if (dd < bestD) { bestD = dd; best = thetaClass ? poly[i].Y : poly[i].X; }
            }
            return best - 2 * Math.PI * Math.Floor(best / (2 * Math.PI));
        }
        // пояса: поперечный угол — φ у класса (1, 0), θ у класса (0, 1); «нижний» цикл — с обходом в +, пара — ближайший
        // выше (по кругу) с обходом в −
        if (wrapping.Count > 0)
        {
            double Across(int ci)
            {
                var poly = cycles[ci].Poly; var q = poly[0];
                return byTheta ? q.Y : q.X;
            }
            double Along(int ci) => byTheta ? cycles[ci].Wt : cycles[ci].Wp;
            var idx = Enumerable.Range(0, cycles.Count).Where(i => cycles[i].Wt != 0 || cycles[i].Wp != 0).ToList();
            // поперечный угол цикла «на одном и том же продольном значении»: берётся точка цикла, ближайшая к опорному значению
            double refAlong = byTheta ? cycles[idx[0]].Poly[0].X : cycles[idx[0]].Poly[0].Y;
            double AcrossAt(int ci)
            {
                var poly = cycles[ci].Poly; double best = double.NaN, bestD = double.MaxValue;
                for (int i = 0; i + 1 < poly.Count; i++)
                {
                    double a = byTheta ? poly[i].X : poly[i].Y, b = byTheta ? poly[i + 1].X : poly[i + 1].Y;
                    double sh = 2 * Math.PI * Math.Round(((a + b) / 2 - refAlong) / (2 * Math.PI)); double ra = refAlong + sh;
                    if ((ra - a) * (ra - b) <= 0 && Math.Abs(b - a) > 1e-15)
                    {
                        double t = (ra - a) / (b - a); double v = byTheta ? poly[i].Y + (poly[i + 1].Y - poly[i].Y) * t : poly[i].X + (poly[i + 1].X - poly[i].X) * t;
                        return v - 2 * Math.PI * Math.Floor(v / (2 * Math.PI));
                    }
                    double dd = Math.Abs(a - ra); if (dd < bestD) { bestD = dd; best = byTheta ? poly[i].Y : poly[i].X; }
                }
                return best - 2 * Math.PI * Math.Floor(best / (2 * Math.PI));
            }
            // в развёртке с ориентацией грани «слева от цикла, идущего в +» — сторона увеличения поперечного угла у класса
            // (1, 0) и уменьшения у (0, 1) (поворот (1,0)→(0,1) — влево)
            var plus = idx.Where(i => Along(i) > 0).ToList(); var minus = idx.Where(i => Along(i) < 0).ToList();
            if (plus.Count != minus.Count) { why = $"torus: unpaired wrapping loops ({plus.Count} forward, {minus.Count} backward)"; return null; }
            var used = new HashSet<int>();
            foreach (int b in plus)
            {
                double ab = AcrossAt(b); int best = -1; double bestGap = double.MaxValue;
                foreach (int t in minus)
                {
                    if (used.Contains(t)) continue;
                    double gap = AcrossAt(t) - ab; if (!byTheta) gap = -gap * sign;
                    gap -= 2 * Math.PI * Math.Floor(gap / (2 * Math.PI));
                    if (gap < TolV) gap += 2 * Math.PI;
                    if (gap < bestGap) { bestGap = gap; best = t; }
                }
                if (best < 0) { why = "torus: a band has no second boundary"; return null; }
                used.Add(best);
                var r = new Region(); r.Loops.Add(cycles[b].Darts); r.Loops.Add(cycles[best].Darts); regions.Add(r);
                regionOfCycle[b] = r; regionOfCycle[best] = r; bandPairs.Add((b, best, r));
            }
        }
        // диски и отверстия
        bool InPoly(List<Vector2d> poly, Vector2d q)
        {
            // подъём q к ломаной (ближайший по середине габарита)
            double cx = poly.Average(p => p.X), cy = poly.Average(p => p.Y);
            q = Near(q, new Vector2d(cx, cy));
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                if ((poly[i].Y > q.Y) != (poly[j].Y > q.Y) && q.X < (poly[j].X - poly[i].X) * (q.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) inside = !inside;
            return inside;
        }
        var outers = Enumerable.Range(0, cycles.Count).Where(i => cycles[i].Wt == 0 && cycles[i].Wp == 0 && cycles[i].Area > 0).ToList();
        var holes = Enumerable.Range(0, cycles.Count).Where(i => cycles[i].Wt == 0 && cycles[i].Wp == 0 && cycles[i].Area <= 0).ToList();
        foreach (int o in outers) { var r = new Region { Area = cycles[o].Area }; r.Loops.Add(cycles[o].Darts); regions.Add(r); regionOfCycle[o] = r; }
        Region? rest = null;
        foreach (int h in holes)
        {
            var hp = cycles[h].Poly; int k0 = hp.Count / 2; var tng = hp[Math.Min(k0 + 1, hp.Count - 1)] - hp[Math.Max(k0 - 1, 0)];
            var q = hp[k0] + (tng.Length > 1e-15 ? new Vector2d(-tng.Y, tng.X).Normalized() * 1e-4 * sign : Vector2d.Zero);
            Region? best = null; double bestA = double.MaxValue;
            foreach (int o in outers) if (cycles[o].Area < bestA && InPoly(cycles[o].Poly, q)) { best = regionOfCycle[o]; bestA = cycles[o].Area; }
            if (best == null && wrapping.Count > 0)
            {
                // пояс, внутри которого точка: путь от точки к ближайшему циклу пояса без пересечений других — проще:
                // поперечный угол точки между поперечными углами циклов пояса (по кругу)
                foreach (var r in regions.Where(x => x.Loops.Count == 2 && regionOfCycle.Any(kv => kv.Value == x && (cycles[kv.Key].Wt != 0 || cycles[kv.Key].Wp != 0))))
                {
                    int b = regionOfCycle.First(kv => kv.Value == r && (cycles[kv.Key].Wt + cycles[kv.Key].Wp) * 1 != 0 && kv.Key >= 0).Key;
                    best ??= r;         // единственный пояс — однозначно; несколько — по пути (ниже, проверка точек области)
                    _ = b;
                }
                if (regions.Count(x => x.Loops.Count >= 2) > 1)
                {
                    // g.05: НЕСКОЛЬКО ПОЯСОВ — пояс, в котором отверстие: на продольном значении точки отверстия поперечный угол
                    // точки лежит между нижним (обход в +) и верхним циклом пояса (по кругу, в сторону пояса). Прежде — отказ
                    // (серия 8, случаи 9, 199). Мутация P3D_TORMULTIBAND=0.
                    if (MultiBandOff) { why = "torus: a hole between several bands (not yet supported)"; return null; }
                    bool thetaClass = byTheta;
                    double alongQ = thetaClass ? q.X : q.Y, acrossQ = thetaClass ? q.Y : q.X;
                    acrossQ -= 2 * Math.PI * Math.Floor(acrossQ / (2 * Math.PI));
                    Region? hit = null; int nHit = 0;
                    foreach (var (bp, bm, rr) in bandPairs)
                    {
                        double ab = AcrossAtAlong(bp, alongQ, thetaClass), at = AcrossAtAlong(bm, alongQ, thetaClass);
                        double gap = at - ab, g = acrossQ - ab;
                        if (!thetaClass) { gap = -gap * sign; g = -g * sign; }
                        gap -= 2 * Math.PI * Math.Floor(gap / (2 * Math.PI)); g -= 2 * Math.PI * Math.Floor(g / (2 * Math.PI));
                        if (g > 0 && g < gap) { hit = rr; nHit++; }
                    }
                    if (nHit != 1) { why = $"torus: a hole between several bands — {nHit} bands contain it"; return null; }
                    best = hit;
                }
            }
            if (best == null)
            {
                if (wrapping.Count > 0) { why = "torus: a hole lies in no region"; return null; }
                rest ??= new Region { Unbounded = true };
                if (!regions.Contains(rest)) regions.Add(rest);
                best = rest;
            }
            best.Loops.Add(cycles[h].Darts); best.Area += cycles[h].Area;
        }
        // точка внутри: сдвиг влево от середины длинного куска, путь сдвига не пересекает кривых, зазор — наибольший
        var allPolys = cycles.Select(c => c.Poly).ToList();
        var allPts3 = cycles.SelectMany(c => c.Darts).SelectMany(d => Enumerable.Range(0, 33).Select(i => d.At(i / 32.0))).ToList();
        double Clear3(Vector3d x) { double b = double.MaxValue; foreach (var y in allPts3) b = Math.Min(b, (x - y).Length); return b; }
        bool Crosses(Vector2d a, Vector2d b)
        {
            foreach (var poly in allPolys)
                for (int i = 0; i + 1 < poly.Count; i++)
                {
                    var c0 = Near(poly[i], a); var c1 = c0 + (poly[i + 1] - poly[i]);
                    var r = b - a; var e = c1 - c0; double den = r.X * e.Y - r.Y * e.X; if (Math.Abs(den) < 1e-300) continue;
                    var w = c0 - a; double t = (w.X * e.Y - w.Y * e.X) / den, u = (w.X * r.Y - w.Y * r.X) / den;
                    if (t > 1e-9 && t <= 1 && u >= 0 && u <= 1) return true;
                }
            return false;
        }
        foreach (var r in regions)
        {
            var cands = new List<(double C, Vector3d X)>(); int nCross = 0, nClear = 0, nDarts = 0; double maxLen = 0;
            foreach (var d in r.Loops.SelectMany(l => l).OrderByDescending(Length).Take(6))
            {
                var a = A(d.At(0.5)); var t = P(d.At(0.5001), a) - P(d.At(0.4999), a);
                if (t.Length < 1e-15) continue;
                // g.05: начало сдвига — на ЛОМАНОЙ, с которой сверяются пересечения, а не на кривой: хорда ломаной отходит
                // от кривой (тор × тор, серия 8, случаи 9, 31, 39: 2e-4 рад внутрь выпуклой стороны), и путь от точки кривой
                // «пересекал» собственную хорду при любом сдвиге — отказ маршрута. Мутация P3D_TORPOLYSTART=0.
                if (!PolyStartOff)
                {
                    double bd = double.MaxValue; Vector2d best = a;
                    foreach (var poly in allPolys)
                        for (int i = 0; i + 1 < poly.Count; i++)
                        {
                            var c0 = Near(poly[i], a); var e = poly[i + 1] - poly[i]; double ee = Vector2d.Dot(e, e);
                            double u = ee > 0 ? Math.Clamp(Vector2d.Dot(a - c0, e) / ee, 0, 1) : 0; var pr = c0 + e * u;
                            double dd = (pr - a).LengthSquared; if (dd < bd) { bd = dd; best = pr; }
                        }
                    a = best;
                }
                nDarts++; maxLen = Math.Max(maxLen, Length(d));
                var tt = new Vector2d(t.X * sign, t.Y); var left = new Vector2d(-tt.Y, tt.X).Normalized(); left = new Vector2d(left.X * sign, left.Y);
                foreach (double k in new[] { 0.3, 0.1, 0.03, 1e-2, 3e-3, 1e-3, 3e-4 })
                {
                    var q = a + left * k; var x = f.TorusPoint(q.X, q.Y);
                    // путь от точки куска к q — без пересечений (кроме самого куска в начале)
                    if (Crosses(a + left * 1e-7, q)) { nCross++; continue; }
                    double cl = Clear3(x);
                    if (cl < 4 * Classifier.Tol) { nClear++; continue; }
                    cands.Add((cl, x));
                }
            }
            if (cands.Count == 0 && Debug != null)
                foreach (var l in r.Loops) { var ci = cycles.FindIndex(c => c.Darts == l); if (ci >= 0) Debug($"no-interior loop w({cycles[ci].Wt},{cycles[ci].Wp}) area {cycles[ci].Area:G4} sign {sign} poly: " + string.Join(" ", cycles[ci].Poly.Where((_, k) => k % Math.Max(1, cycles[ci].Poly.Count / 24) == 0).Select(v => $"({v.X:0.###},{v.Y:0.###})"))); }
            if (cands.Count == 0) { why = $"torus: no interior point for a region (loops {r.Loops.Count}, darts {nDarts}, longest {maxLen:G3}, crossing {nCross}, too close {nClear}, unbounded {r.Unbounded})"; return null; }
            var ordered = cands.OrderByDescending(c => c.C).ToList();
            r.Inside = ordered[0].X;
            foreach (var (_, x) in ordered.Skip(1).Take(6)) r.InsideCandidates.Add(x);
            if (r.Area == 0) r.Area = 1;
        }
        Debug?.Invoke($"torus split {f.Face.Tag}: cycles {cycles.Count} (wrapping {wrapping.Count}), regions {regions.Count}, sign {sign}; " + string.Join(" | ", cycles.Select(c => $"w({c.Wt},{c.Wp}) area {c.Area:0.##} phi0 {c.Poly[0].Y:0.###}")) + "; regions: " + string.Join(" | ", regions.Select(r => $"loops [{string.Join(",", r.Loops.Select(l => { var ci = cycles.FindIndex(c => c.Darts == l); return ci < 0 ? "?" : $"w({cycles[ci].Wt},{cycles[ci].Wp})@{cycles[ci].Poly[0].Y:0.###}"; }))}] inside {r.Inside:0.##} insidePhi {f.TorusAngles(r.Inside).Ph:0.###}")));
        return regions;
    }
}
