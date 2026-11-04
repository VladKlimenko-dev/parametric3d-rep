using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    /// <summary>
    /// k160 (предложение пользователя 05.11, 00:33 — одна операция «Переход»):
    /// KRN-BLEND-UNIFIED-K160 — переход по сечениям (BlendFeature, режим Sections): квадрат 20 → 10 на высоте 30 — объём
    /// усечённой пирамиды 30/3·(400 + 100 + 200) = 7000 (эталон по формуле), как у «Перехода» LoftFeature; круг R10 → R5 —
    /// объём конуса π·30/3·(100 + 50 + 25) до 0.5 % (сетка сечений); поверхность (без торцов) — квилт, тел нет; запись и чтение
    /// файла: режим, гладкость, касание нормалей, точки по сечению — те же, объём тот же.
    /// </summary>
    static void Blend160Checks()
    {
        Check("KRN-BLEND-UNIFIED-K160", () =>
        {
            Frame3 Z(double z) => new(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
            var p = new Part("bl"); var a = PolySketch(p, Z(0), (-10, -10), (10, -10), (10, 10), (-10, 10)); var b = PolySketch(p, Z(30), (-5, -5), (5, -5), (5, 5), (-5, 5));
            var bf = new BlendFeature { Mode = BlendKind.Sections, Smooth = false }; bf.SectionSketchIds.Add(a.Id); bf.SectionSketchIds.Add(b.Id); p.AddFeature(bf);
            p.Rebuild(null);
            var q = new Part("lf"); var a2 = PolySketch(q, Z(0), (-10, -10), (10, -10), (10, 10), (-10, 10)); var b2 = PolySketch(q, Z(30), (-5, -5), (5, -5), (5, 5), (-5, 5));
            var lf = new LoftFeature(); lf.SectionSketchIds.Add(a2.Id); lf.SectionSketchIds.Add(b2.Id); q.AddFeature(lf); q.Rebuild(null);
            var c = new Part("cone"); var ca = CircleSketch(c, 0, 0, 0, 10); var cb = CircleSketch(c, 30, 0, 0, 5);
            var cf = new BlendFeature { Mode = BlendKind.Sections }; cf.SectionSketchIds.Add(ca.Id); cf.SectionSketchIds.Add(cb.Id); c.AddFeature(cf); c.Rebuild(null);
            double cone = Math.PI * 30 / 3 * (100 + 50 + 25);
            // поверхность
            var s = new Part("srf"); var sa = PolySketch(s, Z(0), (-10, -10), (10, -10), (10, 10), (-10, 10)); var sb = PolySketch(s, Z(30), (-5, -5), (5, -5), (5, 5), (-5, 5));
            var sf = new BlendFeature { Mode = BlendKind.Sections, AsSurface = true, Smooth = false }; sf.SectionSketchIds.Add(sa.Id); sf.SectionSketchIds.Add(sb.Id); s.AddFeature(sf); s.Rebuild(null);
            // запись и чтение
            bf.StartTangent = 0; bf.Samples = 48;
            var text = DocumentFile.Save(p, null);
            var (r, rp) = DocumentFile.Load(text); r.Rebuild(rp);
            var rb = r.Features.OfType<BlendFeature>().Single();
            Log(new { test = "KRN-BLEND-UNIFIED-K160", v = p.Volume, loft = q.Volume, cone = c.Volume, coneWant = cone, surfaceBodies = s.Bodies.Count, errs = string.Join("; ", new[] { bf.Error, cf.Error, sf.Error }.Where(e => e != null)), back = r.Volume, rb.Mode, rb.Smooth, rb.Samples });
            Require(bf.Error == null && Math.Abs(p.Volume - 7000) < 1e-6, $"sections blend volume {p.Volume}, want 7000");
            Require(Math.Abs(p.Volume - q.Volume) < 1e-6, $"blend {p.Volume} vs loft {q.Volume}");
            Require(cf.Error == null && Math.Abs(c.Volume - cone) < 0.005 * cone, $"cone {c.Volume:0.##}, want {cone:0.##}");
            Require(sf.Error == null && s.Bodies.Count == 0, "surface blend made a body");
            Require(rb.Mode == BlendKind.Sections && !rb.Smooth && rb.Samples == 48 && Math.Abs(r.Volume - 7000) < 1e-6, "file round trip");
        });
    }
}
