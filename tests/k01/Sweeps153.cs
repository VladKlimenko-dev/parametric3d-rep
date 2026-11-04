using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

static partial class Program
{
    /// <summary>
    /// k153 (замечание 04.11, 10:34, пп. 10–12).
    /// 10. Переход вращением: два сечения (квадрат 10×10 в плоскости XZ на расстоянии 20 от оси Z и он же в плоскости YZ), ось —
    ///     отрезок отдельного третьего эскиза. Строится, тело годно (прежде «no axis»: ось искалась только в первом сечении;
    ///     P3D_BLENDAXISANY=0). Сечения в параллельных плоскостях поперёк оси — отказ с понятной причиной.
    /// 11. Переход с протяжкой: траектория — опорная кривая (DatumCurve) — строится (P3D_BLENDPATHCURVE=0 — «path sketch not found»).
    /// 12. Протяжка: квадрат 10×10 нарисован в точке (30…40, 30…40) плоскости XY, траектория — отрезок от (35, 35, 0) вверх на 50.
    ///     «Как нарисовано» — тело над нарисованным квадратом (x 30…40); смещение по X сечения 5 — x 35…45; без «как
    ///     нарисовано» (прежнее) — начало эскиза в начало траектории (x 65…75). P3D_SWEEPANCHOR=0 — прежнее у всех.
    /// </summary>
    static void Sweep153Checks()
    {
        Sketch Square(Part p, Frame3 f, double x0, double y0, double a)
        {
            var s = new Sketch($"S{p.Sketches.Count() + 1}", f); p.Add(s);
            var q = new[] { new SkPoint(x0, y0), new SkPoint(x0 + a, y0), new SkPoint(x0 + a, y0 + a), new SkPoint(x0, y0 + a) };
            for (int i = 0; i < 4; i++) s.AddEntity(new SkLine(q[i], q[(i + 1) % 4]));
            return s;
        }

        Check("KRN-BLEND-ROT-AXIS-K153", () =>
        {
            var p = new Part("rb");
            var a = Square(p, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ), 20, 0, 10);      // XZ
            var b = Square(p, new Frame3(Vector3d.Zero, Vector3d.UnitY, Vector3d.UnitZ), 20, 0, 10);      // YZ
            var axs = new Sketch("Axis", new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ)); p.Add(axs);
            var ln = new SkLine(new SkPoint(0, -10), new SkPoint(0, 30)) { Role = SkRole.Centerline }; axs.AddEntity(ln);
            var bf = new BlendFeature("RB") { Mode = BlendKind.Rotational, AxisEntityId = ln.Id };
            bf.SectionSketchIds.Add(a.Id); bf.SectionSketchIds.Add(b.Id); p.AddFeature(bf);
            p.Rebuild(null);
            Log(new { test = "KRN-BLEND-ROT-AXIS-K153", error = bf.Error, bodies = p.Bodies.Count });
            Require(bf.Error == null && p.Bodies.Count == 1 && SolidValidator.Validate(p.Bodies[0]).IsValid, "rotational blend with the axis in a separate sketch: " + (bf.Error ?? "invalid"));
            // сечения в параллельных плоскостях поперёк оси — понятный отказ
            var p2 = new Part("rb2");
            var c1 = Square(p2, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), 5, 5, 10);
            var c2 = Square(p2, new Frame3(new Vector3d(0, 0, 30), Vector3d.UnitX, Vector3d.UnitY), 5, 5, 10);
            var ax2 = new Sketch("Axis", new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ)); p2.Add(ax2);
            var l2 = new SkLine(new SkPoint(0, -10), new SkPoint(0, 40)) { Role = SkRole.Centerline }; ax2.AddEntity(l2);
            var bf2 = new BlendFeature("RB2") { Mode = BlendKind.Rotational, AxisEntityId = l2.Id };
            bf2.SectionSketchIds.Add(c1.Id); bf2.SectionSketchIds.Add(c2.Id); p2.AddFeature(bf2);
            p2.Rebuild(null);
            Log(new { test = "KRN-BLEND-ROT-AXIS-K153", parallel = bf2.Error });
            Require(bf2.Error != null && bf2.Error.Contains("plane through the axis"), "parallel sections: " + (bf2.Error ?? "built"));
        });

        Check("KRN-BLEND-SWEPT-CURVE-K153", () =>
        {
            var p = new Part("sb");
            var path = new DatumCurve("Curve1", new[] { new Vector3d(5, 5, 0), new Vector3d(5, 5, 20), new Vector3d(5, 5, 40) }); p.Add(path);
            var a = Square(p, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), 0, 0, 10);
            var b = Square(p, new Frame3(new Vector3d(0, 0, 40), Vector3d.UnitX, Vector3d.UnitY), 2, 2, 6);
            var bf = new BlendFeature("SB") { Mode = BlendKind.Swept, PathLineId = path.Id };
            bf.SectionSketchIds.Add(a.Id); bf.SectionSketchIds.Add(b.Id); p.AddFeature(bf);
            p.Rebuild(null);
            Log(new { test = "KRN-BLEND-SWEPT-CURVE-K153", error = bf.Error, bodies = p.Bodies.Count });
            Require(bf.Error == null && p.Bodies.Count == 1 && SolidValidator.Validate(p.Bodies[0]).IsValid, "swept blend along a datum curve: " + (bf.Error ?? "invalid"));
        });

        Check("KRN-SWEEP-ANCHOR-K153", () =>
        {
            (double Lo, double Hi, string? Err) Run(bool asDrawn, double du)
            {
                var p = new Part("sw");
                var sec = Square(p, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), 30, 30, 10);
                var line = new Line3DItem("L", new Vector3d(35, 35, 0), new Vector3d(35, 35, 50)); p.Add(line);
                var f = new SweepFeature { SectionSketchId = sec.Id, PathLineId = line.Id, SectionAsDrawn = asDrawn, OffsetU = new ValueRef(du) };
                p.AddFeature(f); p.Rebuild(null);
                return p.Bodies.Count == 1 ? (p.Bodies[0].Bounds.Min.X, p.Bodies[0].Bounds.Max.X, f.Error) : (double.NaN, double.NaN, f.Error ?? "no body");
            }
            var drawn = Run(true, 0); var shifted = Run(true, 5); var old = Run(false, 0);
            Log(new { test = "KRN-SWEEP-ANCHOR-K153", drawn = $"{drawn.Lo:0.###}…{drawn.Hi:0.###}", shifted = $"{shifted.Lo:0.###}…{shifted.Hi:0.###}", old = $"{old.Lo:0.###}…{old.Hi:0.###}", err = drawn.Err ?? shifted.Err ?? old.Err });
            Require(Math.Abs(drawn.Lo - 30) < 0.05 && Math.Abs(drawn.Hi - 40) < 0.05, "as drawn: " + drawn);
            Require(Math.Abs(shifted.Lo - 35) < 0.05 && Math.Abs(shifted.Hi - 45) < 0.05, "offset 5: " + shifted);
            Require(Math.Abs(old.Hi - 75) < 0.05, "previous anchoring (sketch origin at the path start) changed: " + old);
        });
    }
}
