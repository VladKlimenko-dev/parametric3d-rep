using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model.Threads;

namespace Parametric3D.Model.Features;

public sealed partial class CosmeticThreadFeature
{
    /// <summary>
    /// Full-length external thread on an isolated circular cylinder. Build its screw boundary
    /// directly, without subtracting a self-overlapping helical tool. Other bodies use strict Boolean.
    /// Single-start trapezoids use analytic ruled thread strips with or without withdrawal.
    /// The general-profile fallback uses circular crests and degree-7 Hermite sections bounded
    /// to 1e-8 mm (or a tenth of model accuracy); display facets never become modelling geometry.
    /// </summary>
    private static TSolid? DirectCylinder(TSolid body, TFace selected, ThreadSpec spec, bool inner,
        Vector3d b0, Vector3d axis, Vector3d radial, double len, double faceLength, double radius,
        bool left, bool smoothExit)
    {
        if (inner || body.Faces.Count() != 3) return null;
        double taper = spec.Taper ? Math.Tan(spec.HalfConeDeg * Math.PI / 180) : 0;
        // Tapered pipe threads: crests are truncated on the crest cone (major diameter at the small end).
        double crest = spec.Taper ? spec.DMajor / 2 : double.PositiveInfinity;
        var caps = body.Faces.Where(f => !ReferenceEquals(f, selected)).ToArray();
        if (caps.Length != 2 || caps.Any(f => GeometryAudit.AuthoritativeSurface(f) is not PlaneSurface
            || f.Loops.Count != 1)) return null;
        foreach (var f in caps)
        {
            var plane = (PlaneSurface)GeometryAudit.AuthoritativeSurface(f)!;
            if (Math.Abs(Vector3d.Dot(plane.F.W, axis)) < 1 - 1e-10) return null;
            double z = Vector3d.Dot(plane.F.Origin - b0, axis);
            if (Math.Min(Math.Abs(z), Math.Abs(z - faceLength)) > 1e-7) return null;
        }
        // The selected surface must be a complete circular wall; no trimmed window/side opening.
        var boundary = selected.Loops.SelectMany(l => l.CoEdges).Select(c => c.Edge.Curve).ToArray();
        if (boundary.Any(c => c is not (ArcCurve3 or LineCurve3))
            || boundary.OfType<ArcCurve3>().Sum(a => Math.Abs(a.A1 - a.A0)) < 4 * Math.PI - 1e-8) return null;
        double lead = spec.Lead > 0 ? spec.Lead : spec.P * Math.Max(1, spec.Starts);
        var frame = new Frame3(b0, radial, Vector3d.Cross(axis, radial));
        if (smoothExit)
        {
            if (spec.Starts != 1 || Math.Abs(lead - spec.P) > 1e-9) return null;
            return ThreadRunoutOp.Build(frame, lead, faceLength, len, radius, spec.ExtZr, left, taper: taper, crest: crest);
        }
        if (spec.Starts == 1 && Math.Abs(lead - spec.P) < 1e-9
            && spec.ExtZr.Length == 4 && spec.ExtZr.Select(p => p.Y).Distinct().Count() == 2)
            return ThreadRunoutOp.Build(frame, lead, faceLength, len, radius, spec.ExtZr, left, smoothExit: false, taper: taper, crest: crest);
        // General profiles: a partial abrupt ending still goes through the end-cut route.
        if (Math.Abs(len - faceLength) > 1e-7 || taper > 0) return null;
        var profile = ThreadSection(spec, radius, frame, left);
        return PeriodicSweepOp.Build(profile, lead, len, left);
    }

    /// <summary>Transverse solid profile of a thread; synthetic spline error is bounded, not faceted.</summary>
    public static Profile ThreadSection(ThreadSpec spec, double radius, Frame3 frame, bool left)
    {
        var zr = NonOverlappingProfile(spec, false, radius);
        int starts = Math.Max(1, spec.Starts);
        double lead = spec.Lead > 0 ? spec.Lead : spec.P * starts, sign = left ? -1 : 1;
        double Period(double x, double p) => x - p * Math.Floor(x / p);
        double Radius(double angle)
        {
            double x = Period(-sign * lead * angle / (2 * Math.PI) + spec.P / 2, spec.P) - spec.P / 2;
            double result = radius;
            for (int i = 0; i < zr.Length; i++)
            {
                var a = zr[i]; var b = zr[(i + 1) % zr.Length];
                if (x < Math.Min(a.X, b.X) - 1e-12 || x > Math.Max(a.X, b.X) + 1e-12) continue;
                if (Math.Abs(b.X - a.X) < 1e-14) { result = Math.Min(result, Math.Min(a.Y, b.Y)); continue; }
                double t = Math.Clamp((x - a.X) / (b.X - a.X), 0, 1);
                result = Math.Min(result, a.Y + t * (b.Y - a.Y));
            }
            return result;
        }
        var xs = zr.Select(p => p.X).ToList();
        for (int i = 0; i < zr.Length; i++)
        {
            var a = zr[i]; var b = zr[(i + 1) % zr.Length];
            if ((a.Y - radius) * (b.Y - radius) < 0)
                xs.Add(a.X + (b.X - a.X) * (radius - a.Y) / (b.Y - a.Y));
        }
        var angles = new List<double> { 0, 2 * Math.PI };
        for (int k = 0; k < starts; k++)
            angles.AddRange(xs.Select(x => Period(-sign * 2 * Math.PI * x / lead + 2 * Math.PI * k / starts, 2 * Math.PI)));
        angles.Sort();
        var unique = new List<double>();
        foreach (double a in angles)
            if (unique.Count == 0 || a - unique[^1] > 1e-10) unique.Add(a);
        var curves = new List<ICurve3>();
        double tol = Math.Min(1e-8, ToleranceContext.Current.ModelAccuracy * 0.1);
        for (int i = 0; i + 1 < unique.Count; i++)
        {
            double a = unique[i], b = unique[i + 1], r0 = Radius(a), r1 = Radius(b);
            if (Math.Abs(r0 - r1) < 1e-12)
            {
                curves.Add(new ArcCurve3(frame, r0, a, b));
                continue;
            }
            double slope = (r1 - r0) / (b - a);
            // Vector Hermite remainder: conservatively twice the bound on |f^(8)|.
            double maxStep = Math.Min(0.6, Math.Pow(tol * 40320 * 256 /
                (2 * (Math.Max(r0, r1) + 8 * Math.Abs(slope))), 1.0 / 8));
            int pieces = Math.Max(1, (int)Math.Ceiling((b - a) / maxStep));
            for (int j = 0; j < pieces; j++)
            {
                double t0 = a + (b - a) * j / pieces, t1 = a + (b - a) * (j + 1) / pieces, h = t1 - t0;
                (Vector3d P, Vector3d D, Vector3d D2, Vector3d D3) Eval(double t)
                {
                    double r = r0 + slope * (t - a);
                    var e = frame.U * Math.Cos(t) + frame.V * Math.Sin(t);
                    var d = frame.U * -Math.Sin(t) + frame.V * Math.Cos(t);
                    return (frame.Origin + e * r, e * slope + d * r, d * (2 * slope) - e * r, -e * (3 * slope) - d * r);
                }
                var s = Eval(t0); var e = Eval(t1); var p = new Vector3d[8];
                p[0] = s.P; p[1] = p[0] + s.D * (h / 7);
                p[2] = 2 * p[1] - p[0] + s.D2 * (h * h / 42);
                p[3] = 3 * p[2] - 3 * p[1] + p[0] + s.D3 * (h * h * h / 210);
                p[7] = e.P; p[6] = p[7] - e.D * (h / 7);
                p[5] = 2 * p[6] - p[7] + e.D2 * (h * h / 42);
                p[4] = 3 * p[5] - 3 * p[6] + p[7] - e.D3 * (h * h * h / 210);
                curves.Add(new BezierCurve3(p));
            }
        }
        // Clipping the air-side polygon introduced extra angular breakpoints on the
        // same crest circle. Merge those exact arcs instead of creating sliver faces.
        var merged = new List<ICurve3>();
        foreach (var curve in curves)
        {
            if (curve is ArcCurve3 next && merged.LastOrDefault() is ArcCurve3 previous
                && Math.Abs(previous.Radius - next.Radius) < 1e-12 && Math.Abs(previous.A1 - next.A0) < 1e-10)
                merged[^1] = new ArcCurve3(frame, next.Radius, previous.A0, next.A1);
            else merged.Add(curve);
        }
        if (merged.Count > 1 && merged[0] is ArcCurve3 first && merged[^1] is ArcCurve3 last
            && Math.Abs(first.Radius - last.Radius) < 1e-12 && Math.Abs(last.A1 - first.A0 - 2 * Math.PI) < 1e-10)
        {
            merged[0] = new ArcCurve3(frame, first.Radius, last.A0 - 2 * Math.PI, first.A1);
            merged.RemoveAt(merged.Count - 1);
        }
        return ProfileBuilder.Build(frame, merged);
    }
}
