using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Model.Mates;

namespace Parametric3D.Model;

/// <summary>Сборка: детали и вложенные сборки плюс собственная базовая геометрия.</summary>
public sealed class Assembly : ModelItem
{
    /// <summary>m.x4: система единиц сборки для ввода и показа; детали хранят свою, геометрия — в мм.</summary>
    public Core.UnitSystem Units = Core.UnitSystem.Metric;


    /// <summary>
    /// Исполнения: именованные наборы отличий. Хранится только отличие от основной модели —
    /// правка общей части сразу отражается во всех исполнениях.
    /// </summary>
    public readonly List<Configuration> Configurations = new();

    /// <summary>Имя действующего исполнения; пусто — основная модель.</summary>
    public string ActiveConfiguration = "";

    /// <summary>Разнесённые виды: показ узла в разобранном состоянии.</summary>
    public readonly List<ExplodedView> ExplodedViews = new();

    public override ItemKind Kind => ItemKind.Assembly;

    public readonly DatumPoint Origin;
    public readonly DatumPlane PlaneXY, PlaneXZ, PlaneYZ;

    public Vector3 Translation = Vector3.Zero;
    public Quaternion Rotation = Quaternion.Identity;

    public Matrix4 LocalTransform =>
        Matrix4.CreateFromQuaternion(Rotation) * Matrix4.CreateTranslation(Translation);

    public Assembly(string name)
    {
        Name = name;
        Origin = new DatumPoint("Origin", Vector3d.Zero);
        PlaneXY = new DatumPlane("XY", Frame3.XY);
        PlaneXZ = new DatumPlane("XZ", Frame3.XZ);
        PlaneYZ = new DatumPlane("YZ", Frame3.YZ);
        Add(Origin); Add(PlaneXY); Add(PlaneXZ); Add(PlaneYZ);
    }

    public IEnumerable<Part> Parts => Children.OfType<Part>();

    /// <summary>
    /// РАССТАНОВКА ДЕТАЛЕЙ ПО ЦЕПОЧКЕ СОПРЯЖЕНИЙ. Деталь ставится только после своей базовой,
    /// иначе её положение считалось бы от старого места базы. Кольцевая зависимость (А от Б, Б
    /// от А) неразрешима: такие детали остаются на месте, а их имена возвращаются списком.
    /// </summary>
    public List<string> SolveMates()
    {
        var parts = Parts.ToList();
        var done = new HashSet<Part>();
        var stuck = new List<string>();

        // Деталь без сопряжения считается стоящей на месте.
        foreach (var p in parts) if (p.AssemblyMate == null) done.Add(p);

        bool moved = true;
        while (moved)
        {
            moved = false;
            foreach (var p in parts)
            {
                if (done.Contains(p)) continue;
                var m = p.AssemblyMate!;
                if (m.BasePart != null && !done.Contains(m.BasePart)) continue;   // база ещё не на месте
                m.ApplyTo(p);
                done.Add(p);
                moved = true;
            }
        }

        // РАССТАНОВКА ПО ОГРАНИЧЕНИЯМ. Ограничение, в отличие от соединения, не задаёт положение
        // прямо: оно говорит, каким условиям положение обязано отвечать. Поэтому положение
        // подбирается — спуском по шести координатам (три сдвига и три поворота) с уменьшающимся
        // шагом, пока общая невязка не станет нулевой. Мерило то же, что у связей, поэтому
        // ограничения и соединения живут вместе.
        foreach (var p in parts)
        {
            var active = p.Constraints.Where(c => c.Enabled).ToList();
            if (active.Count == 0) continue;

            double Total()
            {
                double sum = 0;
                foreach (var c in active)
                {
                    var (d, a) = c.Residual(p);
                    sum += d + a * 0.1;              // градус приравнен к десятой доле миллиметра
                }
                return sum;
            }

            double best = Total();
            Vector3 t0 = p.Translation;
            Quaternion r0 = p.Rotation;

            double stepT = 20, stepR = 30;           // мм и градусы
            for (int pass = 0; pass < 80 && best > 1e-9; pass++)
            {
                bool better = false;

                foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                    foreach (double sgn in new[] { 1.0, -1.0 })
                    {
                        var keep = p.Translation;
                        p.Translation = keep + axis * (float)(stepT * sgn);
                        double now = Total();
                        if (now < best - 1e-12) { best = now; better = true; }
                        else p.Translation = keep;
                    }

                foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                    foreach (double sgn in new[] { 1.0, -1.0 })
                    {
                        var keep = p.Rotation;
                        p.Rotation = Quaternion.FromAxisAngle(axis, (float)(stepR * sgn * Math.PI / 180)) * keep;
                        double now = Total();
                        if (now < best - 1e-12) { best = now; better = true; }
                        else p.Rotation = keep;
                    }

                if (!better) { stepT /= 2; stepR /= 2; }
                if (stepT < 1e-7 && stepR < 1e-7) break;
            }

            // Ограничения, оставшиеся невыполненными, называются вслух: молча показать сборку,
            // которой не существует, хуже, чем сказать, что не сошлось.
            foreach (var c in active)
            {
                var (d, a) = c.Residual(p);
                if (d > 1e-3 || a > 1e-2)
                    stuck.Add($"{p.Name}: ограничение «{c.Name}» не выполнено (расхождение {d:0.###} мм, {a:0.##}°)");
            }

            // Прежде здесь стояло присваивание переменной самой себе — остаток отладки; оно
            // ничего не делало и вызывало предупреждение сборки.
        }

        foreach (var p in parts) if (!done.Contains(p)) stuck.Add(p.Name);

        // РЕШАТЕЛЬ: свободные координаты первой связи подбираются так, чтобы невязка остальных
        // стала наименьшей. Спуск по координатам с уменьшающимся шагом — метод простой, но
        // честный: мерило невязки точное, а число свободных координат не больше трёх.
        foreach (var p in parts)
        {
            if (p.Mates.Count < 2 || p.AssemblyMate is not { } main) continue;
            if (main.DegreesOfFreedom == 0) continue;

            double Total()
            {
                double sum = 0;
                for (int i = 1; i < p.Mates.Count; i++)
                {
                    var (d, a) = p.Mates[i].Residual(p);
                    sum += d + a * 0.1;              // градус приравнен к десятой доле миллиметра
                }
                return sum;
            }

            void Place() => main.ApplyTo(p);

            Place();
            double best = Total();

            // Какие координаты свободны у этой связи.
            var knobs = new List<(Func<double> Get, Action<double> Set, double Step)>();
            if (main.Type is MateType.Pin or MateType.Cylinder)
                knobs.Add((() => main.Angle, v => main.Angle = v, Math.PI / 4));
            if (main.Type is MateType.Slider or MateType.Cylinder)
                knobs.Add((() => main.Offset, v => main.Offset = v, 10));
            if (main.Type == MateType.Planar)
            {
                knobs.Add((() => main.PlanarOffset.X, v => main.PlanarOffset = new Vector2d(v, main.PlanarOffset.Y), 10));
                knobs.Add((() => main.PlanarOffset.Y, v => main.PlanarOffset = new Vector2d(main.PlanarOffset.X, v), 10));
                knobs.Add((() => main.PlanarAngle, v => main.PlanarAngle = v, Math.PI / 4));
            }

            for (int pass = 0; pass < 60 && best > 1e-9; pass++)
            {
                bool better = false;
                foreach (var (get, set, step0) in knobs)
                {
                    double step = step0 / (1 << Math.Min(pass, 20));
                    double at = get();
                    foreach (double trial in new[] { at + step, at - step })
                    {
                        set(trial);
                        Place();
                        double now = Total();
                        if (now < best - 1e-12) { best = now; at = trial; better = true; }
                        else { set(at); Place(); }
                    }
                }
                if (!better && pass > 25) break;
            }
            Place();
        }

        // Связи, которые решатель не смог удовлетворить, называются вслух:
        // молча оставить невыполненную связь — значит показать сборку, которой не существует.
        foreach (var p in parts)
            for (int i = 1; i < p.Mates.Count; i++)
            {
                var (dist, angle) = p.Mates[i].Residual(p);
                if (dist > 1e-3 || angle > 1e-3)
                    stuck.Add($"{p.Name}: связь «{p.Mates[i].Name}» не выполнена (расхождение {dist:0.###} мм, {angle:0.##}°)");
            }

        return stuck;
    }
    public IEnumerable<Assembly> SubAssemblies => Children.OfType<Assembly>();

    /// <summary>
    /// ПЕРЕСЕЧЕНИЯ ДЕТАЛЕЙ. Сборка молча допускала, что две детали занимают одно место. Объём
    /// общей части считается через объединение: V(A) + V(B) − V(A∪B) — отдельной операции
    /// пересечения в ядре нет, а это равенство точное. Возвращаются пары с объёмом больше
    /// порога (по умолчанию кубический миллиметр: меньшее — обычно касание).
    /// </summary>
    public List<(Part A, Part B, double Volume)> Interferences(double minVolume = 1.0)
    {
        var found = new List<(Part, Part, double)>();
        var parts = Parts.Where(p => p.Bodies.Count > 0).ToList();

        static Kernel.Ops.TransformOp.Xform Place(Part p)
        {
            Quaternion r = p.Rotation;
            Vector3 t = p.Translation;
            Vector3d Point(Vector3d v)
            {
                Vector3 w = Vector3.Transform(new Vector3((float)v.X, (float)v.Y, (float)v.Z), r) + t;
                return new Vector3d(w.X, w.Y, w.Z);
            }
            Vector3d Dir(Vector3d v)
            {
                Vector3 w = Vector3.Transform(new Vector3((float)v.X, (float)v.Y, (float)v.Z), r);
                return new Vector3d(w.X, w.Y, w.Z);
            }
            return new Kernel.Ops.TransformOp.Xform(Point, Dir, false);
        }

        static double Volume(Kernel.Topology.TSolid s) =>
            Math.Abs(Kernel.Tess.MassProps.SignedVolume(Kernel.Tess.Tessellator.Tessellate(s)));

        for (int i = 0; i < parts.Count; i++)
            for (int j = i + 1; j < parts.Count; j++)
            {
                var wa = new List<string>();
                double overlap = 0;

                foreach (var ba in parts[i].Bodies)
                    foreach (var bb in parts[j].Bodies)
                    {
                        var ca = Kernel.Ops.TransformOp.Copy(ba, Place(parts[i]));
                        var cb = Kernel.Ops.TransformOp.Copy(bb, Place(parts[j]));
                        double va = Volume(ca), vb = Volume(cb);

                        var united = new List<Kernel.Topology.TSolid>();
                        Kernel.Ops.BooleanOp.Unite(united, ca, wa);
                        Kernel.Ops.BooleanOp.Unite(united, cb, wa);
                        double vu = united.Sum(Volume);

                        double d = va + vb - vu;
                        if (d > 0) overlap += d;
                    }

                if (overlap >= minVolume) found.Add((parts[i], parts[j], overlap));
            }

        return found;
    }

    public Part AddPart(string name)
    {
        var p = new Part(name);
        Add(p);
        return p;
    }

    public Assembly AddSubAssembly(string name)
    {
        var a = new Assembly(name);
        Add(a);
        return a;
    }

    /// <summary>Все детали сборки с их положением в мировых координатах (для экспорта).</summary>
    public void CollectParts(List<(Part Part, Matrix4 World)> into, Matrix4 parent)
    {
        Matrix4 m = LocalTransform * parent;
        foreach (var c in Children)
        {
            switch (c)
            {
                case Part p: into.Add((p, p.LocalTransform * m)); break;
                case Assembly sub: sub.CollectParts(into, m); break;
            }
        }
    }

    /// <summary>Габарит всей сборки в мировых координатах.</summary>
    public void AccumulateBounds(ref Bounds3 b, Matrix4 parent)
    {
        Matrix4 m = LocalTransform * parent;
        foreach (var c in Children)
        {
            switch (c)
            {
                case Part p when p.Mesh != null:
                    {
                        Matrix4 pm = p.LocalTransform * m;
                        Vector3 lo = p.Bounds.Min.ToF(), hi = p.Bounds.Max.ToF();
                        for (int i = 0; i < 8; i++)
                        {
                            var corner = new Vector3(
                                (i & 1) == 0 ? lo.X : hi.X,
                                (i & 2) == 0 ? lo.Y : hi.Y,
                                (i & 4) == 0 ? lo.Z : hi.Z);
                            Vector3 w = Vector3.TransformPosition(corner, pm);
                            b.Add(new Vector3d(w.X, w.Y, w.Z));
                        }
                        break;
                    }
                case Assembly sub:
                    sub.AccumulateBounds(ref b, m);
                    break;
            }
        }
    }
}
