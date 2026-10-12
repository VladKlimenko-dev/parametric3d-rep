using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn03FaceCompChecks()
    {
        Check("KRN03-FACE-ONE-COMPONENT", () =>
        {
            // 03.2: грань — одна связная компонента. Тело из двух раздельных брусков в ОДНОЙ оболочке, верхние грани которых
            // слиты в одну грань с двумя внешними контурами, проверка отвергает («face-components»); те же бруски с раздельными
            // верхними гранями — годны. Объединение двух раздельных брусков (2.5-мерный маршрут) даёт отдельные торцы.
            var fails = new List<string>();
            TSolid Two(bool merge)
            {
                var a = Box(0, 0, 0, 10, 10, 10); var b = TransformOp.Copy(Box(0, 0, 0, 10, 10, 10), TransformOp.Move(new Vector3d(30, 0, 0)));
                var s = new TSolid(); var sh = new TShell { Solid = s }; s.Shells.Add(sh);
                var ca = TopologyClone.Deep(a); var cb = TopologyClone.Deep(b);
                TFace? topA = null;
                foreach (var f in ca.Faces.ToList()) { sh.Add(f); if (PlanarTweak.OutwardNormal(f) is { } n && n.Z > 0.999) topA = f; }
                foreach (var f in cb.Faces.ToList())
                {
                    if (merge && PlanarTweak.OutwardNormal(f) is { } n && n.Z > 0.999 && topA != null)
                    {
                        foreach (var l in f.Loops.ToList()) { f.Loops.Remove(l); l.Face = topA; topA.Loops.Add(l); }
                        continue;
                    }
                    sh.Add(f);
                }
                // приведение к соглашению само делит грань по компонентам (KNOWN_LIMITATIONS 3) — проверка ловит построитель,
                // собравший тело мимо него: соглашение помечается без приведения (обход брусков уже согласован)
                s.TopologyConvention = TopologyConventions.Version;
                return s;
            }
            var good = Two(false); var bad = Two(true);
            var vg = SolidValidator.ValidateBRep(good); var vb = SolidValidator.ValidateBRep(bad);
            Log(new { test = "KRN03-FACE-ONE-COMPONENT", good = vg.Summary(), bad = vb.Summary(), codes = string.Join(",", vb.Issues.Select(i => i.Code).Distinct()) });
            if (vg.Issues.Any(i => i.Code == "face-components")) fails.Add("separate top faces were flagged");
            if (!vb.Issues.Any(i => i.Code == "face-components" && i.Severity == ValidationSeverity.Error)) fails.Add($"a face of two disjoint pieces was not rejected ({vb.Summary()})");
            var u = new List<TSolid> { Box(0, 0, 0, 10, 10, 10) }; BooleanOp.Unite(u, Box(30, 0, 0, 10, 10, 10), new List<string>());
            int multi = u.Sum(b => b.Faces.Count(f => f.Loops.Count(l => l.IsOuter) > 1));
            if (u.Any(b => !SolidValidator.ValidateBRep(b).IsValid)) fails.Add("union of two disjoint boxes is invalid");
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
