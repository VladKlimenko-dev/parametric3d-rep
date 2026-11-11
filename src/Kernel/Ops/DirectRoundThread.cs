using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

public static partial class DirectRound
{
    public static long ExactThreadCells;

    /// <summary>Analytic ruled strips, clipped by the blank's end planes before topology assembly.</summary>
    internal static TSolid? ThreadCell(ThreadRunoutLaw law, double za, double zb, bool full,
        IReadOnlyList<double> phases, ref string? why)
    {
        var faces = new List<FDesc>();
        var caps = new[] { new List<Seg>(), new List<Seg>() };
        var times = new List<double>();
        for (int k = (int)Math.Floor(za / law.Lead) - 3; k <= (int)Math.Ceiling(zb / law.Lead) + 3; k++)
            foreach (double phase in phases) times.Add(k * law.Lead + phase);
        times.Sort();
        for (int strip = 0; strip < 4; strip++)
            for (int i = 0; i + 1 < times.Count; i++)
            {
                KernelContext.Checkpoint("thread.patch");
                int kind = strip;
                double ta = times[i], tb = times[i + 1];
                (double A, double B, double DA, double DB) Range(double t)
                {
                    var a = law.Boundary(kind, t, full);
                    var b = law.Boundary(kind + 1, t, full);
                    if (a.Q < za - t) a = (za - t, -1);
                    if (b.Q > zb - t) b = (zb - t, -1);
                    return (a.Q, b.Q, a.Derivative, b.Derivative);
                }
                if (Range((ta + tb) / 2) is var middle && middle.B - middle.A <= 1e-12) continue;
                double slope = kind == 0 ? law.ML : kind == 2 ? law.MR : 0;
                double qRoot = kind == 0 ? law.Q0 : law.Q1;
                Vector3d Point(double t, double q)
                {
                    double r = kind == 3 ? law.Radius : law.Root + slope * (q - qRoot) + law.Offset(t, full).D;
                    double angle = law.Omega * t;
                    var radial = law.Frame.U * Math.Cos(angle) + law.Frame.V * Math.Sin(angle);
                    return law.Frame.Origin + law.Frame.W * (t + q) + radial * r;
                }
                SweepSection Section(double t)
                {
                    var r = Range(t);
                    return SweepSection.Line(Point(t, r.A), Point(t, r.B));
                }
                Vector3d Derivative(double t, double v)
                {
                    var range = Range(t); var offset = law.Offset(t, full);
                    double q = range.A * (1 - v) + range.B * v;
                    double dq = range.DA * (1 - v) + range.DB * v;
                    double r = kind == 3 ? law.Radius : law.Root + slope * (q - qRoot) + offset.D;
                    double dr = kind == 3 ? 0 : slope * dq + offset.Derivative;
                    double angle = law.Omega * t;
                    var radial = law.Frame.U * Math.Cos(angle) + law.Frame.V * Math.Sin(angle);
                    var tangent = law.Frame.V * Math.Cos(angle) - law.Frame.U * Math.Sin(angle);
                    return law.Frame.W * (1 + dq) + radial * dr + tangent * (r * law.Omega);
                }
                var stations = Enumerable.Range(0, 17).Select(j => ta + (tb - ta) * j / 16).ToArray();
                // Keep the serialized formula table well within modelling accuracy too.
                var surf = new SectionSurface(Section, stations, stations.Select(Section).ToArray(), false, ta, tb,
                    tableStep: (tb - ta) / 4096);
                double tableError = 0;
                for (int sample = 0; sample < 65; sample++)
                {
                    double t = ta + (tb - ta) * (sample + 0.5) / 65;
                    var tab = surf.SectionAt(t); var exact = Section(t);
                    tableError = Math.Max(tableError, Math.Max((tab.Point(0) - exact.Point(0)).Length,
                        (tab.Point(1) - exact.Point(1)).Length));
                }
                if (tableError > Math.Min(1e-8, 0.1 * ToleranceContext.Current.ModelAccuracy))
                { why = "thread surface table exceeds modelling accuracy"; return null; }
                surf.PreferExactFormula();
                surf.ExactDerivativeU = Derivative;
                var fd = new FDesc { Surf = surf, Rev = law.Left, Tag = $"sweep:side[thread.{kind}]:{i}",
                    Hint = Section((ta + tb) / 2).Point(0.5) };
                var loop = new List<Seg>();
                void Add(ICurve3 curve, Vector3d a, Vector3d b)
                {
                    if ((a - b).Length < 1e-9) return; // collapsed section at a triangular patch tip
                    var seg = new Seg { Curve = curve, A = a, B = b };
                    loop.Add(seg);
                    double z0 = Vector3d.Dot(a - law.Frame.Origin, law.Frame.W);
                    double z1 = Vector3d.Dot(b - law.Frame.Origin, law.Frame.W);
                    double zm = Vector3d.Dot(curve.PointAt((curve.T0 + curve.T1) / 2) - law.Frame.Origin, law.Frame.W);
                    for (int c = 0; c < 2; c++)
                    {
                        double z = c == 0 ? za : zb;
                        if (Math.Max(Math.Abs(z0 - z), Math.Max(Math.Abs(z1 - z), Math.Abs(zm - z))) < 1e-8)
                            caps[c].Add(seg.Clone());
                    }
                }
                var first = Section(ta); var last = Section(tb);
                var a0 = first.Point(0); var b0 = first.Point(1);
                var a1 = last.Point(0); var b1 = last.Point(1);
                Add(new LineCurve3(a0, b0), a0, b0);
                Add(new ProcCurve3(t => Section(t).Point(1), ta, tb, false, "thread boundary"), b0, b1);
                Add(new LineCurve3(a1, b1), b1, a1);
                Add(new ProcCurve3(t => Section(t).Point(0), ta, tb, false, "thread boundary"), a1, a0);
                if (loop.Count < 3) { why = "collapsed thread patch"; return null; }
                fd.Loops.Add(loop); faces.Add(fd);
            }
        for (int c = 0; c < 2; c++)
        {
            var pending = caps[c]; var ordered = new List<Seg>();
            if (pending.Count == 0) { why = "empty thread cap"; return null; }
            ordered.Add(pending[0]); pending.RemoveAt(0);
            while (pending.Count > 0)
            {
                var p = ordered[^1].B;
                int at = pending.FindIndex(s => (s.A - p).Length < 1e-7 || (s.B - p).Length < 1e-7);
                if (at < 0) { why = "thread cap chain is open"; return null; }
                var seg = pending[at]; pending.RemoveAt(at);
                if ((seg.A - p).Length > (seg.B - p).Length) (seg.A, seg.B) = (seg.B, seg.A);
                ordered.Add(seg);
            }
            if ((ordered[^1].B - ordered[0].A).Length > 1e-7) { why = "thread cap not closed"; return null; }
            var normal = law.Frame.W * (c == 0 ? -1 : 1);
            var origin = law.Frame.Origin + law.Frame.W * (c == 0 ? za : zb);
            var cap = new FDesc { Surf = new PlaneSurface(Frame3.FromNormal(origin, normal)),
                Tag = c == 0 ? "cap:start" : "cap:end", N = normal, Hint = origin };
            cap.Loops.Add(ordered); faces.Add(cap);
        }
        var result = FinishDirect(faces, new List<FDesc>(), "thread.runout", ref why);
        if (result != null) Interlocked.Increment(ref ExactThreadCells);
        return result;
    }
}
