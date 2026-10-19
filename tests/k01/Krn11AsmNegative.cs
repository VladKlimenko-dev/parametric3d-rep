using OpenTK.Mathematics;
using Parametric3D.Model;
using Parametric3D.Model.Mates;

static partial class Program
{
    /// <summary>
    /// KRN-11 (11.2): НЕГАТИВНЫЕ ВХОДЫ СБОРКИ. Кольцо сопряжений, сопряжение детали с собой, база из чужой сборки, нулевая и
    /// нечисловая ось, нечисловые координаты, пределы «от» больше «до», несуществующая грань, деталь без тела: расстановка не
    /// бросает исключений, недопустимые сопряжения не применяются (деталь на месте, имя в списке нерасставленных), положения
    /// остальных деталей — числа, пересечения считаются по допустимым деталям.
    /// </summary>
    static void Krn11AsmNegativeChecks()
    {
        Check("KRN11-QUAL-NEGATIVE-ASSEMBLY", () =>
        {
            var fails = new List<string>();
            static bool Finite(Part p) => float.IsFinite(p.Translation.X) && float.IsFinite(p.Translation.Y) && float.IsFinite(p.Translation.Z)
                && float.IsFinite(p.Rotation.X) && float.IsFinite(p.Rotation.Y) && float.IsFinite(p.Rotation.Z) && float.IsFinite(p.Rotation.W);
            Part Cube(Assembly a, string n, double side) { var p = a.AddPart(n); Parametric3D.Model.TestGeometry.AddCube(p, side); p.Rebuild(null); return p; }
            var cases = new List<(string Name, Action<Assembly, Part, Part> Setup, bool BStuck)>
            {
                ("cycle", (a, p, q) => { p.AssemblyMate = new Mate(MateType.Weld, "m1") { BasePart = q }; q.AssemblyMate = new Mate(MateType.Weld, "m2") { BasePart = p }; }, true),
                ("self", (a, p, q) => q.AssemblyMate = new Mate(MateType.Weld, "m") { BasePart = q }, true),
                ("foreign base", (a, p, q) => { var other = new Assembly("other"); var r = other.AddPart("r"); q.AssemblyMate = new Mate(MateType.Weld, "m") { BasePart = r }; }, true),
                ("zero axis", (a, p, q) => q.AssemblyMate = new Mate(MateType.Pin, "m") { BasePart = p, AssemblyAxis = Vector3d.Zero }, true),
                ("NaN part axis", (a, p, q) => q.AssemblyMate = new Mate(MateType.Slider, "m") { BasePart = p, PartAxis = new Vector3d(double.NaN, 0, 1) }, true),
                ("NaN offset", (a, p, q) => q.AssemblyMate = new Mate(MateType.Slider, "m") { BasePart = p, Offset = double.NaN }, true),
                ("infinite origin", (a, p, q) => q.AssemblyMate = new Mate(MateType.Weld, "m") { BasePart = p, AssemblyOrigin = new Vector3d(double.PositiveInfinity, 0, 0) }, true),
                ("limits reversed", (a, p, q) => q.AssemblyMate = new Mate(MateType.Slider, "m") { BasePart = p, Offset = 5, LimitEnabled = true, MinValue = 10, MaxValue = -10 }, true),
                ("ball not a rotation", (a, p, q) => q.AssemblyMate = new Mate(MateType.Ball, "m") { BasePart = p, BallRotation = new Quaternion(0, 0, 0, 0) }, true),
                ("valid slider", (a, p, q) => q.AssemblyMate = new Mate(MateType.Slider, "m") { BasePart = p, Offset = 40, AssemblyAxis = Vector3d.UnitX }, false),
            };
            foreach (var (name, setup, bStuck) in cases)
            {
                try
                {
                    var a = new Assembly("neg " + name);
                    var p = Cube(a, "A", 20); var q = Cube(a, "B", 10); var e = a.AddPart("empty");
                    var t0 = q.Translation; var r0 = q.Rotation;
                    setup(a, p, q);
                    var stuck = a.SolveMates();
                    foreach (var x in a.Parts) if (!Finite(x)) fails.Add($"{name}: part {x.Name} placement is not a number");
                    bool isStuck = stuck.Contains("B");
                    if (bStuck && !isStuck) fails.Add($"{name}: B not reported as unplaced ({string.Join(",", stuck)})");
                    if (bStuck && (q.Translation != t0 || q.Rotation != r0)) fails.Add($"{name}: B moved by an invalid mate");
                    if (!bStuck && isStuck) fails.Add($"{name}: valid mate reported as unplaced");
                    if (!bStuck && Math.Abs(q.Translation.X - 40) > 1e-4) fails.Add($"{name}: B at {q.Translation}");
                    var inter = a.Interferences();
                    if (inter.Any(i => i.A == e || i.B == e)) fails.Add($"{name}: the empty part interferes");
                }
                catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name}: {ex.Message}"); }
            }
            // нечисловое положение, заданное прямо (испорченный файл): пересечения — без исключения, по прочим деталям
            try
            {
                var a = new Assembly("nan"); var p = Cube(a, "A", 20); var q = Cube(a, "B", 10); var r = Cube(a, "C", 10);
                q.Translation = new Vector3(float.NaN, 0, 0); r.Translation = new Vector3(5, 5, 5);
                var inter = a.Interferences();
                if (inter.Any(i => i.A == q || i.B == q)) fails.Add("NaN placement: the part interferes");
                if (!inter.Any(i => i.A == p && i.B == r)) fails.Add("NaN placement: the valid overlap A·C is lost");
            }
            catch (Exception ex) { fails.Add($"NaN placement: {ex.GetType().Name}: {ex.Message}"); }
            // несуществующая грань сопряжения: разрешение — false, опора прежняя
            {
                var a = new Assembly("tag"); var p = Cube(a, "A", 20); var q = Cube(a, "B", 10);
                var m = new Mate(MateType.Weld, "m") { BasePart = p, PartFaceTag = "no such face", PartOrigin = new Vector3d(1, 2, 3) };
                if (m.Resolve(q)) fails.Add("missing face tag resolved");
                if (m.PartOrigin != new Vector3d(1, 2, 3)) fails.Add("missing face tag changed the reference point");
            }
            Log(new { test = "KRN11-QUAL-NEGATIVE-ASSEMBLY", cases = cases.Count, fails = fails.Count });
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
