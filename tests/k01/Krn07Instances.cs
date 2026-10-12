using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;

static partial class Program
{
    static void Krn07InstanceChecks()
    {
        Check("KRN07-PATTERN-INSTANCE-IDENTITY", () =>
        {
            // 07.7 ИДЕНТИЧНОСТЬ ЭКЗЕМПЛЯРОВ: брусок 10×10×10 со сквозным отверстием d4, линейный массив ×3 шагом 20; кромка входа
            // отверстия ТРЕТЬЕГО экземпляра скруглена r0.5 (ссылка — имена граней и точки). Правка числа копий 3→5 и шага
            // 20→25: скругление остаётся на третьем экземпляре (x = 45, затем 55), прямым построителем, объём — формула
            // Паппа; число копий 2 — экземпляра нет: явная ошибка «no longer exists», а не неоднозначность и не другой экземпляр.
            // Селектор граней по роли и селектор кромок отверстия видят все экземпляры.
            var fails = new List<string>();
            var (p, lp, rf, h) = InstanceCase();
            double R = 2, r = 0.5, cell = 1000 - Math.PI * R * R * 10;
            double dV = 2 * Math.PI * (R + r * (10 - 3 * Math.PI) / (12 - 3 * Math.PI)) * r * r * (1 - Math.PI / 4);   // Папп
            double Cx() { var f = p.Bodies.SelectMany(b => b.Faces).Where(x => x.Tag.StartsWith("#" + rf.Key + "/")).ToList();
                return f.Count == 0 ? double.NaN : f.SelectMany(x => x.Loops).SelectMany(l => l.CoEdges).Average(ce => ce.Edge.Curve.PointAt(0.5).X); }
            void Want(string w, int n, double x)
            {
                string direct = p.Warnings.Any(s => s.StartsWith("Round: direct blend")) ? "direct" : "fallback";
                Console.WriteLine($"  {w}: V {p.Volume:R} (formula {n * cell - dV:R}) round at x≈{Cx():F6}, {direct}, err {rf.Error ?? "-"}");
                if (rf.Error != null) { fails.Add($"{w}: {rf.Error}"); return; }
                if (Math.Abs(p.Volume - (n * cell - dV)) > 1e-6 * p.Volume) fails.Add($"{w}: V {p.Volume:R} vs {n * cell - dV:R}");
                if (!(Math.Abs(Cx() - x) < 1e-6)) fails.Add($"{w}: round at x {Cx():F6}, expected {x}");
                if (direct != "direct") fails.Add($"{w}: the round went the fallback way");
            }
            Want("×3 step 20", 3, 45);
            int roles = p.Bodies.SelectMany(b => b.Faces).Count(new FaceSelector(FaceSelectorKind.Role, p.Features.OfType<ExtrudeFeature>().First().Key, "top").Matches);
            int rims = new EdgeSelector(EdgeSelectorKind.Rim, h.Key).Resolve(p.Bodies).Count;
            if (roles != 3) fails.Add($"role selector 'top': {roles} faces, expected 3 (one per instance)");
            if (rims < 6) fails.Add($"Rim(hole): {rims} edges, expected all instances");
            var tags = p.Bodies.SelectMany(b => b.Faces).Select(f => f.Tag).Where(t => !t.StartsWith("#" + rf.Key + "/")).ToList();
            if (tags.Count != tags.Distinct().Count()) fails.Add($"duplicate face names among instances: {string.Join(", ", tags.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key + "×" + g.Count()))}");
            lp.Count = new ValueRef(5); p.MarkDirtyFrom(lp); p.Rebuild(null); Want("×5 step 20", 5, 45);
            lp.Step = new Vector3d(25, 0, 0); p.MarkDirtyFrom(lp); p.Rebuild(null); Want("×5 step 25", 5, 55);
            lp.Count = new ValueRef(2); p.MarkDirtyFrom(lp); p.Rebuild(null);
            Console.WriteLine($"  ×2: err {rf.Error ?? "-"}");
            if (rf.Error == null || !rf.Error.Contains("no longer exists")) fails.Add($"×2 (third instance gone): {rf.Error ?? "no error"}");
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails));
        });
    }
}
