using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Model.Sketching;

namespace Parametric3D.App.Sketching;

/// <summary>
/// k176: ШЕСТЕРНЯ — как окружность: первый щелчок — центр, второй — делительный радиус (модуль m = 2R/z при числе зубьев из
/// последних параметров; поворот — по направлению на второй щелчок). Шестерня ложится в эскиз замкнутым контуром из отрезков,
/// дуг и кривых Безье (вид — по профилю зуба); параметры правятся на панели свойств при выборе любого её элемента.
/// </summary>
public sealed class GearTool : SketchToolBase
{
    /// <summary>Параметры следующей шестерни (последние заданные на панели или в инструменте).</summary>
    public static GearParams Defaults = new();
    /// <summary>Последняя построенная шестерня (вид выбирает её контур, панель свойств показывает её параметры).</summary>
    public static SkGear? LastCreated;

    public override string Title => "Шестерня";
    public override string Hint => Pts.Count == 0 ? Loc.L("Щёлкните центр шестерни ({0} зубьев)", Defaults.Teeth) : "Щёлкните делительный радиус";
    public override ISketchTool Restart() => Restarted(new GearTool());

    GearParams At(Vector2d c, Vector2d q, bool preview)
    {
        var p = Defaults.Clone();
        var r = q - c; double R = r.Length;
        p.CenterX = c.X; p.CenterY = c.Y;
        if (R > Tol.Weld) { p.Module = 2 * R / Math.Max(3, p.Teeth); p.Rotation = Math.Atan2(r.Y, r.X) * 180 / Math.PI; }
        if (preview) { p.Tolerance = Math.Max(p.Tolerance, 0.02 * p.Module); p.PitchCircle = false; }
        return p;
    }

    public override bool Click(SketchInput i)
    {
        Pts.Add(i.P); Snaps.Add(i.Snap); if (Pts.Count < 2) return false;
        Finished = true;
        if ((Pts[1] - Pts[0]).Length < Tol.Weld) return false;
        var p = At(Pts[0], Pts[1], preview: false);
        var g = i.Sketch.AddGear(p, out _);
        if (g == null) return false;
        LastCreated = g;
        Defaults.Module = p.Module;
        // центр шестерни — к пойманной точке (совпадение — подсказка; центр закреплён параметрами, решатель его не двигает)
        return true;
    }

    public override void Preview(List<Vector2d[]> lines, List<Vector2d> points)
    {
        points.Add(Cursor); if (Pts.Count == 0) return;
        var p = At(Pts[0], Cursor, preview: true);
        lines.Add(Circle(Pts[0], p.PitchRadius));
        if (p.Teeth > 400) return;
        if (GearGeometry.Build(p, out _) is not { } segs) return;
        var poly = new List<Vector2d>();
        foreach (var s in segs) Tessellate(s, poly);
        if (poly.Count > 1) lines.Add(poly.ToArray());
    }

    /// <summary>Ломаная элемента профиля (для предпросмотра).</summary>
    public static void Tessellate(GearGeometry.Seg s, List<Vector2d> dst)
    {
        switch (s.Kind)
        {
            case GearGeometry.SegKind.Line: if (dst.Count == 0) dst.Add(s.Start); dst.Add(s.End); break;
            case GearGeometry.SegKind.Arc:
            {
                var u = s.Start - s.C; var v = s.End - s.C; double R = u.Length;
                double a0 = Math.Atan2(u.Y, u.X), a1 = Math.Atan2(v.Y, v.X), sw = a1 - a0;
                if (s.Ccw) { while (sw <= 0) sw += 2 * Math.PI; } else { while (sw >= 0) sw -= 2 * Math.PI; }
                int n = Math.Clamp((int)(Math.Abs(sw) * 12), 2, 64);
                for (int k = dst.Count == 0 ? 0 : 1; k <= n; k++) { double a = a0 + sw * k / n; dst.Add(s.C + new Vector2d(Math.Cos(a), Math.Sin(a)) * R); }
                break;
            }
            default:
            {
                int n = 12;
                for (int k = dst.Count == 0 ? 0 : 1; k <= n; k++)
                {
                    double t = k / (double)n; var tmp = s.P.ToArray();
                    for (int a = tmp.Length - 1; a > 0; a--) for (int b = 0; b < a; b++) tmp[b] = tmp[b] * (1 - t) + tmp[b + 1] * t;
                    dst.Add(tmp[0]);
                }
                break;
            }
        }
    }
}
