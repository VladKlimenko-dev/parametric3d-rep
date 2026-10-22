using OpenTK.Mathematics;

namespace Parametric3D.Model.Sketching;

/// <summary>
/// Состояние эскиза после решения. Ok — полностью определён; Underconstrained — есть свобода; Overconstrained — лишнее, но
/// согласное условие; Inconsistent — противоречие; DidNotConverge — решатель не справился; Singular (KRN-08, 08.9) —
/// условия выполнены лишь потому, что геометрия выродилась (отрезок нулевой длины, окружность нулевого радиуса);
/// ставит Sketch.Solve по самой геометрии.
/// </summary>
public enum SolveStatus { Ok, Underconstrained, Overconstrained, DidNotConverge, Inconsistent, Singular }

public interface ISketchSolver
{
    /// <param name="dragged">Точка, которую тянет пользователь: она удерживается жёстче остальных.</param>
    /// <param name="dof">Оценка числа оставшихся степеней свободы.</param>
    SolveStatus Solve(Sketch sketch, SkPoint? dragged, out int dof);
}

/// <summary>
/// Численный решатель ограничений (Левенберг — Марквардт с численным якобианом).
///
/// Ограничения и размеры превращаются в невязки; система решается итеративно.
/// Слабая регуляризация «оставайся рядом с текущим положением» делает поведение
/// при перетаскивании предсказуемым: недоопределённый эскиз не «взрывается».
///
/// Это самодостаточная реализация без внешних зависимостей. Если нужен
/// символьный решатель (как в вашем GCS.cs) — он подставляется сюда же
/// через ISketchSolver, остальной код менять не придётся.
/// </summary>
public sealed class NumericSketchSolver : ISketchSolver
{
    /// <summary>Наименьший радиус, до которого решатель сжимает окружность.</summary>
    public const double MinRadiusValue = Var.MinRadius;

    public int MaxIterations = 60;
    public double Tolerance = 1e-9;
    /// <summary>
    /// Вес регуляризации — притяжения переменных к исходному положению.
    ///
    /// Он ДОЛЖЕН быть много меньше веса уравнений ограничений. Иначе
    /// решатель начинает торговать: чуть нарушить ограничение, зато меньше
    /// сдвинуть точки. Здесь регуляризация нужна только чтобы выбрать одно
    /// решение из бесконечного множества у недоопределённого эскиза,
    /// и для этого достаточно любого ненулевого веса.
    /// </summary>
    public double Regularization = 1e-5;

    /// <summary>
    /// Вес удержания перетаскиваемой точки у курсора.
    ///
    /// БЫЛО 100 — то есть в сто раз БОЛЬШЕ веса ограничений. На
    /// недоопределённом эскизе это не мешало: ограничения выполнялись
    /// точно, а вес лишь выбирал, куда пойти в свободных направлениях.
    /// Но как только эскиз определён полностью, курсор и ограничения
    /// вступают в спор — и с весом 100 побеждал курсор. Прямоугольник
    /// переставал быть прямоугольным прямо во время перетаскивания.
    ///
    /// Проверка на прямоугольнике 87x93 при сдвиге угла на (60, 40):
    /// со старым весом ограничения нарушались на 21 мм, с новым — на 1e-6.
    ///
    /// Перетаскивание работает и при крошечном весе: точку сдвигают
    /// К КУРСОРУ ещё до решения, а решатель лишь восстанавливает
    /// ограничения, оставляя её настолько близко к курсору, насколько
    /// они позволяют.
    /// </summary>
    public double DragWeight = 1e-4;

    /// <summary>
    /// Перетаскивание фигуры целиком: все её точки удерживаются как перетаскиваемая (вес DragWeight),
    /// чтобы решатель не возвращал одну из них, восстанавливая ограничения за счёт остальных. Null — нет.
    /// </summary>
    public HashSet<SkPoint>? DraggedPoints;

    private bool IsDragged(SkPoint? p, SkPoint? dragged) =>
        p != null && (p == dragged || (DraggedPoints != null && DraggedPoints.Contains(p)));

    /// <summary>KRN-10 (10.8): с какого числа переменных связной части — разреженная алгебра (меньше — плотная).</summary>
    public const int SparseMin = 150;
    static readonly bool SparseOff = Environment.GetEnvironmentVariable("P3D_SKSPARSE") == "0";
    [ThreadStatic] public static bool SparseOffForTests;
    static bool UseSparse(int n) => !SparseOff && !SparseOffForTests && n >= SparseMin;
    /// <summary>Число разреженных шагов (разбор).</summary>
    public static long SparseSteps;

    private sealed class Var
    {
        public SkPoint? Point;
        public SkCircle? Circle;
        public SkEllipse? Ellipse;
        /// <summary>Для точки — координата Y, для эллипса — малая полуось.</summary>
        public bool IsY;
        public double Start;

        public double Get()
        {
            if (Point != null) return IsY ? Point.Y : Point.X;
            if (Ellipse != null) return IsY ? Ellipse.RV : Ellipse.RU;
            return Circle!.Radius;
        }

        /// <summary>
        /// Наименьший радиус. Ниже него окружность вырождается в точку
        /// и обратно уже не восстанавливается: при отрицательном значении
        /// все формулы дают бессмыслицу, а на экране фигура пропадает.
        ///
        /// Ограничение нужно именно здесь, в переменной. Управляя касательной
        /// линией, пользователь по сути управляет радиусом: проведи линию
        /// через центр — и решатель честно потянет радиус к нулю, а затем
        /// и за него.
        /// </summary>
        public const double MinRadius = 1e-4;

        public void Set(double v)
        {
            if (Point != null) { if (IsY) Point.Y = v; else Point.X = v; }
            else if (Ellipse != null)
            {
                if (IsY) Ellipse.RV = Math.Max(v, MinRadius);
                else Ellipse.RU = Math.Max(v, MinRadius);
            }
            else Circle!.Radius = Math.Max(v, MinRadius);
        }
    }

    public SolveStatus Solve(Sketch sk, SkPoint? dragged, out int dof)
    {
        var vars = CollectVars(sk);
        dof = vars.Count;
        if (vars.Count == 0) return SolveStatus.Ok;

        var residuals = BuildResiduals(sk);
        int m = residuals.Count;
        if (m == 0) { dof = vars.Count; return SolveStatus.Underconstrained; }

        // K09. РАЗБИЕНИЕ НА НЕЗАВИСИМЫЕ ЧАСТИ. Метод Левенберга — Марквардта с плотной матрицей
        // стоит O(n³) на шаг: эскиз из 50 несвязанных прямоугольников (400 переменных) решался
        // 9 с. Связь переменных определяется по самим невязкам (сдвиг переменной — какие
        // невязки изменились), и каждая связная часть решается отдельно.
        var comps = Components(vars, residuals);
        if (comps.Count > 1)
        {
            dof = 0;
            bool inconsistent = false, didNotConverge = false, over = false;
            foreach (var (cv, cf) in comps)
            {
                if (cf.Count == 0) { dof += cv.Count; continue; }
                var st = SolveCore(sk, cv, cf, dragged, out int d);
                dof += d;
                inconsistent |= st == SolveStatus.Inconsistent;
                didNotConverge |= st == SolveStatus.DidNotConverge;
                over |= st == SolveStatus.Overconstrained;
            }
            // Связь найдена численно: у невязки с нулевой производной в текущей точке связь
            // может не проявиться. Проверка целиком; если части вместе не сходятся — решение
            // всей системы одним куском, как прежде.
            if (!inconsistent && !didNotConverge)
            {
                var xs = vars.Select(v => v.Get()).ToArray();
                double L = 1e-9;
                foreach (var v in vars) L = Math.Max(L, Math.Abs(v.Get()));
                if (ConstraintResidual(vars, xs, residuals) > Math.Pow(1e-7 * L, 2))
                    return SolveCore(sk, vars, residuals, dragged, out dof);
            }
            if (inconsistent) return SolveStatus.Inconsistent;
            if (didNotConverge) return SolveStatus.DidNotConverge;
            if (over) return SolveStatus.Overconstrained;
            return dof > 0 ? SolveStatus.Underconstrained : SolveStatus.Ok;
        }
        return SolveCore(sk, vars, residuals, dragged, out dof);
    }

    /// <summary>Связные части: переменные и невязки, связанные через общие переменные.</summary>
    private static List<(List<Var> Vars, List<ResidualFn> Fns)> Components(List<Var> vars, List<ResidualFn> fns)
    {
        int n = vars.Count, m = fns.Count;
        var x0 = vars.Select(v => v.Get()).ToArray();
        var baseOut = new List<double>[m];
        for (int k = 0; k < m; k++) { baseOut[k] = new List<double>(); fns[k](baseOut[k]); }
        var parent = Enumerable.Range(0, n + m).ToArray();
        int Find(int a) { while (parent[a] != a) a = parent[a] = parent[parent[a]]; return a; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
        var buf = new List<double>();
        for (int j = 0; j < n; j++)
        {
            double h = 1e-6 * Math.Max(1, Math.Abs(x0[j]));
            vars[j].Set(x0[j] + h);
            for (int k = 0; k < m; k++)
            {
                buf.Clear(); fns[k](buf);
                bool changed = buf.Count != baseOut[k].Count;
                for (int r = 0; !changed && r < buf.Count; r++) changed = buf[r] != baseOut[k][r];
                if (changed) Union(j, n + k);
            }
            vars[j].Set(x0[j]);
        }
        var groups = new Dictionary<int, (List<Var>, List<ResidualFn>)>();
        for (int j = 0; j < n; j++)
        {
            int r = Find(j);
            if (!groups.TryGetValue(r, out var g)) groups[r] = g = (new List<Var>(), new List<ResidualFn>());
            g.Item1.Add(vars[j]);
        }
        for (int k = 0; k < m; k++)
        {
            int r = Find(n + k);
            if (!groups.TryGetValue(r, out var g)) groups[r] = g = (new List<Var>(), new List<ResidualFn>());
            g.Item2.Add(fns[k]);
        }
        return groups.Values.Where(g => g.Item1.Count > 0).ToList();
    }

    private SolveStatus SolveCore(Sketch sk, List<Var> vars, List<ResidualFn> residuals, SkPoint? dragged, out int dof)
    {
        int n = vars.Count;
        var x = new double[n];
        for (int i = 0; i < n; i++) { x[i] = vars[i].Get(); vars[i].Start = x[i]; }

        double lambda = 1e-3;
        double prev = Residual(vars, x, residuals, dragged);

        // K09. ДОПУСКИ — ОТ МАСШТАБА ЭСКИЗА. Прежде пороги были абсолютными (сумма квадратов
        // невязок 1e-9 — «сошлось», 1e-4 — «не сошлось»): у эскиза в сотые доли миллиметра
        // решение останавливалось с ошибкой, сравнимой с самими размерами, а у метрового
        // «сошлось» требовало точности выше машинной. Масштаб L — наибольшая координата или размер.
        double L = 1e-9;
        foreach (var v in vars) L = Math.Max(L, Math.Abs(v.Get()));
        foreach (var dm in sk.Dimensions) if (dm.IsDriving) L = Math.Max(L, Math.Abs(dm.Value));
        double tolConverged = Math.Pow(1e-12 * L, 2), tolFailed = Math.Pow(1e-7 * L, 2);

        Sparse? sparse = null;
        for (int iter = 0; iter < MaxIterations; iter++)
        {
            if (ConstraintResidual(vars, x, residuals) < tolConverged) break;

            var r = EvalVector(vars, x, residuals, dragged, out int rows);

            // K09. РАЗРЕЖЕННЫЙ ЯКОБИАН. Невязка зависит от нескольких переменных; прежде на каждую
            // переменную пересчитывались ВСЕ невязки и все переменные выставлялись заново, а J^T J
            // собиралась тройным циклом по плотной матрице: связная сетка 13×13 (338 переменных)
            // решалась 7 с. Теперь сдвиг переменной пересчитывает только зависящие от неё невязки,
            // а J^T J собирается по ненулевым элементам строк.
            using (Parametric3D.Kernel.Diagnostics.Prof.Time("эскиз: структура якобиана")) sparse ??= SparseStructure(vars, x, residuals);
            var (rowCols, rowVals) = SparseJacobian(vars, x, residuals, dragged, sparse, r);
            var b = new double[n];
            bool solved; double[] d;
            if (UseSparse(n))
            {
                // KRN-10 (10.8): разреженная J^T J + Холецкий в профиле после RCM (сетка 31×31, 1922 переменные: шаг ЛМ
                // плотным методом Гаусса — O(n³)). Мутация P3D_SKSPARSE=0 — плотный путь.
                var As = new Dictionary<int, double>[n];
                for (int i = 0; i < n; i++) As[i] = new Dictionary<int, double>();
                for (int k = 0; k < rows; k++)
                {
                    var cols = rowCols[k]; var vals = rowVals[k];
                    for (int p1 = 0; p1 < cols.Count; p1++)
                    {
                        int i = cols[p1];
                        b[i] -= vals[p1] * r[k];
                        var Ai = As[i];
                        for (int p2 = 0; p2 < cols.Count; p2++) { int j = cols[p2]; Ai[j] = (Ai.TryGetValue(j, out var o) ? o : 0) + vals[p1] * vals[p2]; }
                    }
                }
                for (int i = 0; i < n; i++) { double dg = As[i].TryGetValue(i, out var o) ? o : 0; As[i][i] = dg + lambda * (1.0 + dg); }
                using (Parametric3D.Kernel.Diagnostics.Prof.Time("эскиз: шаг ЛМ (решение)")) solved = SparseLinear.SolveSpd(As, b, out d);
                Interlocked.Increment(ref SparseSteps);
            }
            else
            {
                var A = new double[n, n];
                for (int k = 0; k < rows; k++)
                {
                    var cols = rowCols[k]; var vals = rowVals[k];
                    for (int p1 = 0; p1 < cols.Count; p1++)
                    {
                        int i = cols[p1];
                        b[i] -= vals[p1] * r[k];
                        for (int p2 = 0; p2 < cols.Count; p2++) A[i, cols[p2]] += vals[p1] * vals[p2];
                    }
                }
                for (int i = 0; i < n; i++) A[i, i] += lambda * (1.0 + A[i, i]);
                using (Parametric3D.Kernel.Diagnostics.Prof.Time("эскиз: шаг ЛМ (решение)")) solved = SolveLinear(A, b, out d);
            }
            if (!solved) { lambda *= 10; continue; }

            var trial = new double[n];
            for (int i = 0; i < n; i++) trial[i] = x[i] + d[i];

            double now = Residual(vars, trial, residuals, dragged);
            if (now < prev)
            {
                Array.Copy(trial, x, n);
                prev = now;
                lambda = Math.Max(lambda * 0.4, 1e-10);
            }
            else
            {
                lambda *= 6;
                if (lambda > 1e10) break;
            }
        }

        for (int i = 0; i < n; i++) vars[i].Set(x[i]);

        // Степени свободы считаются по РАНГУ якобиана, а не по разнице
        // «переменных минус уравнений».
        //
        // Разница врёт в обе стороны: два одинаковых ограничения дают два
        // уравнения, но убирают одну степень свободы, а ограничение,
        // выродившееся в текущем положении, не убирает ни одной. Ранг
        // говорит, сколько уравнений ДЕЙСТВИТЕЛЬНО независимы.
        int equations = CountEquations(vars, x, residuals);
        int rank; using (Parametric3D.Kernel.Diagnostics.Prof.Time("эскиз: ранг")) rank = JacobianRank(vars, x, residuals, sparse);
        dof = Math.Max(0, n - rank);

        // K09. ДОВОДКА: несколько шагов Гаусса — Ньютона по одним ограничениям (решение наименьшей
        // нормы, без притяжения к начальному положению). Регуляризация Левенберга — Марквардта
        // оставляла невязку 1e-8…2e-7 мм (точка на окружности R5 — на 6e-8 от неё, касание двух
        // окружностей — с ошибкой расстояния центров 2.4e-7) — для модели это много.
        using (Parametric3D.Kernel.Diagnostics.Prof.Time("эскиз: доводка")) Polish(vars, x, residuals, sparse);

        // Решение оценивается по невязке ОГРАНИЧЕНИЙ, без регуляризации (она не нулевая всегда,
        // когда точки сдвинулись). Противоречие (невязка не убывает до нуля при зависимых
        // уравнениях) — Inconsistent; избыточность без противоречия — Overconstrained. Прежде оба
        // случая назывались одинаково, и пользователь не отличал лишнее условие от неверного.
        double constraintResidual = ConstraintResidual(vars, x, residuals);
        if (constraintResidual > tolFailed)
            return rank < equations ? SolveStatus.Inconsistent : SolveStatus.DidNotConverge;

        // Уравнений независимо меньше, чем их написано: часть избыточна.
        if (rank < equations) return SolveStatus.Overconstrained;

        return dof > 0 ? SolveStatus.Underconstrained : SolveStatus.Ok;
    }

    // -------------------------------------------------------------- переменные

    private static List<Var> CollectVars(Sketch sk)
    {
        var seen = new HashSet<SkPoint>();
        var list = new List<Var>();
        foreach (var e in sk.Entities)
        {
            if (e.Fixed) continue;

            // Ссылочная геометрия принадлежит ЧУЖИМ эскизам и телам.
            // Она задаёт положение, а не подчиняется ему: попади её точки
            // в число неизвестных, и ограничение, наложенное на проекцию,
            // сдвинуло бы саму проекцию вместо того, чтобы подтянуть
            // к ней свой чертёж.
            if (e.Role == SkRole.Reference) continue;
            foreach (var p in e.Handles)
            {
                if (p.Fixed || !seen.Add(p)) continue;
                list.Add(new Var { Point = p, IsY = false });
                list.Add(new Var { Point = p, IsY = true });
            }
            if (e is SkCircle c) list.Add(new Var { Circle = c });

            // Полуоси эллипса — тоже свободные величины, иначе размер
            // на них не подействует.
            if (e is SkEllipse el)
            {
                list.Add(new Var { Ellipse = el, IsY = false });
                list.Add(new Var { Ellipse = el, IsY = true });
            }
        }
        return list;
    }

    // -------------------------------------------------------------- невязки

    private delegate void ResidualFn(List<double> dst);

    /// <summary>
    /// K09. Условия, которые решатель не умеет выразить (сочетание элементов без уравнения), —
    /// последнего решения в этом потоке. Прежде такие условия пропускались молча.
    /// </summary>
    [ThreadStatic] private static List<string>? _unsupported;
    public static IReadOnlyList<string> Unsupported => _unsupported ??= new List<string>();

    private static List<ResidualFn> BuildResiduals(Sketch sk)
    {
        var fns = new List<ResidualFn>();
        (_unsupported ??= new List<string>()).Clear();

        // Неявное условие дуги: начало и конец на одном радиусе.
        foreach (var e in sk.Entities)
            if (e is SkArc arc)
                fns.Add(dst => dst.Add((arc.Start.P - arc.Center.P).Length - (arc.End.P - arc.Center.P).Length));

        _building = sk;
        foreach (var c in sk.Constraints)
        {
            // Конфликтующие исключены: их нельзя удовлетворить вместе
            // с остальными, и попытка это сделать развалила бы эскиз целиком.
            if (!c.Enabled || c.Conflict) continue;
            var it = c.Items;
            int fnsBefore = fns.Count;
            AddConstraintResiduals(c, it, fns);
            // Условие не дало ни одного уравнения (сочетание элементов, для которого его нет) —
            // в список «не выражено», а не молча мимо. Fix выражается переменными, не уравнением.
            if (fns.Count == fnsBefore && c.Type != ConstraintType.Fix && _unsupported!.All(u => !u.StartsWith(c.Type + " ")))
                _unsupported!.Add($"{c.Type} {string.Join("+", it.Select(x => x.GetType().Name))}");
        }
        _building = null;
        return FinishResiduals(sk, fns);
    }

    [ThreadStatic] static Sketch? _building;
    /// <summary>Мутация P3D_SKTANGENTEND=0: касание дуги и отрезка у общего конца — расстоянием до прямой (прежнее).</summary>
    static readonly bool TangentEndOff = Environment.GetEnvironmentVariable("P3D_SKTANGENTEND") == "0";

    /// <summary>
    /// k97: конец дуги, лежащий на отрезке по связи (совпадение с концом отрезка, общая точка, «точка на кривой»). Касание
    /// у такого конца выражается перпендикулярностью радиуса прямой: «расстояние от центра до прямой = радиус» при точке
    /// на прямой — минимум функции, производная вдоль прямой нулевая, и решатель видел избыточность (скругление угла
    /// давало «эскиз переопределён»).
    /// </summary>
    static SkPoint? ArcEndOnLine(SkLine l, SkArc a)
    {
        if (TangentEndOff || _building is not { } sk) return null;
        foreach (var e in new[] { a.Start, a.End })
        {
            if (ReferenceEquals(e, l.A) || ReferenceEquals(e, l.B)) return e;
            foreach (var c in sk.Constraints)
            {
                if (!c.Enabled || c.Conflict || !c.Items.Contains(e)) continue;
                if (c.Type == ConstraintType.Coincident && c.Items.Any(x => ReferenceEquals(x, l.A) || ReferenceEquals(x, l.B))) return e;
                if (c.Type == ConstraintType.PointOnEntity && c.Items.Contains(l)) return e;
            }
        }
        return null;
    }

    private static void AddConstraintResiduals(SkConstraint c, IReadOnlyList<SkEntity> it, List<ResidualFn> fns)
    {
        {
            switch (c.Type)
            {
                case ConstraintType.Coincident:
                    if (it.Count >= 2 && it[0] is SkPoint p0 && it[1] is SkPoint p1)
                        fns.Add(dst => { dst.Add(p0.X - p1.X); dst.Add(p0.Y - p1.Y); });
                    break;

                case ConstraintType.PointOnEntity:
                    if (it.Count >= 2 && it[0] is SkPoint pp)
                    {
                        if (it[1] is SkLine ln)
                            fns.Add(dst => dst.Add(Cross(ln.B.P - ln.A.P, pp.P - ln.A.P) / Math.Max(1e-9, (ln.B.P - ln.A.P).Length)));
                        else if (it[1] is SkCircle ci)
                            fns.Add(dst => dst.Add((pp.P - ci.Center.P).Length - ci.Radius));
                        else if (it[1] is SkArc ar)
                            fns.Add(dst => dst.Add((pp.P - ar.Center.P).Length - ar.Radius));
                        // K09: точка на эллипсе. Прежде условие молча пропускалось: эскиз решался
                        // «недоопределённым», а точка оставалась где была (x²/a² + y²/b² = 1.39).
                        else if (it[1] is SkEllipse el)
                            fns.Add(dst =>
                            {
                                var d = pp.P - el.Center.P;
                                double c = Math.Cos(el.Rotation), sn = Math.Sin(el.Rotation);
                                double qx = d.X * c + d.Y * sn, qy = -d.X * sn + d.Y * c;
                                double rr = Math.Sqrt(qx * qx / (el.RU * el.RU) + qy * qy / (el.RV * el.RV));
                                dst.Add((rr - 1) * Math.Min(el.RU, el.RV));
                            });
                        else _unsupported!.Add($"{c.Type} {string.Join("+", it.Select(x => x.GetType().Name))}");
                    }
                    break;

                case ConstraintType.Horizontal:
                    if (it.Count >= 1 && it[0] is SkLine hl) fns.Add(dst => dst.Add(hl.A.Y - hl.B.Y));
                    else if (it.Count >= 2 && it[0] is SkPoint ha && it[1] is SkPoint hb) fns.Add(dst => dst.Add(ha.Y - hb.Y));
                    break;

                case ConstraintType.Vertical:
                    if (it.Count >= 1 && it[0] is SkLine vl) fns.Add(dst => dst.Add(vl.A.X - vl.B.X));
                    else if (it.Count >= 2 && it[0] is SkPoint va && it[1] is SkPoint vb) fns.Add(dst => dst.Add(va.X - vb.X));
                    break;

                case ConstraintType.Parallel:
                    if (it.Count >= 2 && it[0] is SkLine pa && it[1] is SkLine pb)
                        fns.Add(dst => dst.Add(Cross(Dir(pa), Dir(pb))));
                    break;

                case ConstraintType.Perpendicular:
                    if (it.Count >= 2 && it[0] is SkLine qa && it[1] is SkLine qb)
                        fns.Add(dst => dst.Add(Vector2d.Dot(Dir(qa), Dir(qb))));
                    break;

                case ConstraintType.Tangent:
                    if (it.Count >= 2)
                    {
                        // Расстояние берётся ПО МОДУЛЮ.
                        //
                        // DistPointLine знаковое: у окружности слева от прямой
                        // оно отрицательно. Требуя равенства знакового
                        // расстояния радиусу, решатель перетаскивал окружность
                        // на другую сторону прямой — отсюда прыжки далеко
                        // от исходного положения. По модулю касание достигается
                        // с той стороны, где фигура уже находится.
                        // K09: касание отрезка и эллипса. В системе эллипса для прямой n·q = d
                        // (n — единичная нормаль) условие касания: d² = a²nₓ² + b²n_y².
                        if ((it[0] is SkLine && it[1] is SkEllipse) || (it[0] is SkEllipse && it[1] is SkLine))
                        {
                            var tle = it[0] as SkLine ?? (SkLine)it[1];
                            var tel = it[0] as SkEllipse ?? (SkEllipse)it[1];
                            fns.Add(dst =>
                            {
                                double c = Math.Cos(tel.Rotation), sn = Math.Sin(tel.Rotation);
                                Vector2d L(Vector2d p) { var d = p - tel.Center.P; return new(d.X * c + d.Y * sn, -d.X * sn + d.Y * c); }
                                var a = L(tle.A.P); var b = L(tle.B.P); var dir = b - a;
                                if (dir.LengthSquared < 1e-24) { dst.Add(0); return; }
                                var nrm = new Vector2d(-dir.Y, dir.X).Normalized();
                                double off = Math.Abs(Vector2d.Dot(nrm, a));
                                dst.Add(off - Math.Sqrt(tel.RU * tel.RU * nrm.X * nrm.X + tel.RV * tel.RV * nrm.Y * nrm.Y));
                            });
                        }
                        else if (it[0] is SkLine tl && it[1] is SkCircle tc)
                            fns.Add(dst => dst.Add(Math.Abs(DistPointLine(tc.Center.P, tl)) - tc.Radius));
                        else if (it[0] is SkCircle tc2 && it[1] is SkLine tl2)
                            fns.Add(dst => dst.Add(Math.Abs(DistPointLine(tc2.Center.P, tl2)) - tc2.Radius));
                        else if ((it[0] is SkLine && it[1] is SkArc) || (it[0] is SkArc && it[1] is SkLine))
                        {
                            var tl3 = it[0] as SkLine ?? (SkLine)it[1];
                            var ta = it[0] as SkArc ?? (SkArc)it[1];
                            if (ArcEndOnLine(tl3, ta) is { } te)
                                fns.Add(dst => dst.Add(Vector2d.Dot(te.P - ta.Center.P, Dir(tl3))));
                            else
                                fns.Add(dst => dst.Add(Math.Abs(DistPointLine(ta.Center.P, tl3)) - ta.Radius));
                        }
                        // u13: касание дуг (и дуги с окружностью) — как у окружностей: внешнее или внутреннее, ближнее
                        else if ((it[0] is SkArc || it[1] is SkArc) && (it[0] is SkArc or SkCircle) && (it[1] is SkArc or SkCircle))
                        {
                            SkPoint C0 = it[0] is SkArc a0 ? a0.Center : ((SkCircle)it[0]).Center, C1 = it[1] is SkArc a1 ? a1.Center : ((SkCircle)it[1]).Center;
                            var e0 = it[0]; var e1 = it[1];
                            double R(SkEntity e) => e is SkArc aa ? aa.Radius : ((SkCircle)e).Radius;
                            fns.Add(dst =>
                            {
                                double d = (C0.P - C1.P).Length, r0 = R(e0), r1 = R(e1);
                                double outer = Math.Abs(d - (r0 + r1)), inner = Math.Abs(d - Math.Abs(r0 - r1));
                                dst.Add(outer <= inner ? d - (r0 + r1) : d - Math.Abs(r0 - r1));
                            });
                        }
                        else if (it[0] is SkCircle c1 && it[1] is SkCircle c2)
                            fns.Add(dst =>
                            {
                                double d = (c1.Center.P - c2.Center.P).Length;
                                double outer = Math.Abs(d - (c1.Radius + c2.Radius));
                                double inner = Math.Abs(d - Math.Abs(c1.Radius - c2.Radius));
                                dst.Add(outer <= inner ? d - (c1.Radius + c2.Radius) : d - Math.Abs(c1.Radius - c2.Radius));
                            });
                    }
                    break;

                case ConstraintType.Equal:
                    if (it.Count >= 2)
                    {
                        if (it[0] is SkLine ea && it[1] is SkLine eb) fns.Add(dst => dst.Add(ea.Length - eb.Length));
                        else if (it[0] is SkCircle ec && it[1] is SkCircle ed) fns.Add(dst => dst.Add(ec.Radius - ed.Radius));
                        else if (it[0] is SkArc ga && it[1] is SkArc gb) fns.Add(dst => dst.Add(ga.Radius - gb.Radius));
                        // u13: дуга и окружность (кнопка «Равенство»)
                        else if (it[0] is SkArc ha && it[1] is SkCircle hc) fns.Add(dst => dst.Add(ha.Radius - hc.Radius));
                        else if (it[0] is SkCircle hc2 && it[1] is SkArc ha2) fns.Add(dst => dst.Add(hc2.Radius - ha2.Radius));
                    }
                    break;

                case ConstraintType.MidPoint:
                    if (it.Count >= 2 && it[0] is SkPoint mp && it[1] is SkLine ml)
                        fns.Add(dst =>
                        {
                            dst.Add(mp.X - (ml.A.X + ml.B.X) * 0.5);
                            dst.Add(mp.Y - (ml.A.Y + ml.B.Y) * 0.5);
                        });
                    break;

                case ConstraintType.Symmetric:
                    if (it.Count >= 3 && it[0] is SkPoint sa && it[1] is SkPoint sb && it[2] is SkLine sl)
                        fns.Add(dst =>
                        {
                            Vector2d mid = (sa.P + sb.P) * 0.5;
                            Vector2d d = Dir(sl);
                            dst.Add(Cross(d, mid - sl.A.P));            // середина на оси
                            dst.Add(Vector2d.Dot(d, sb.P - sa.P));      // отрезок перпендикулярен оси
                        });
                    break;

                case ConstraintType.Concentric:
                    {
                        SkPoint? ca = CenterOf(it.Count > 0 ? it[0] : null);
                        SkPoint? cb = CenterOf(it.Count > 1 ? it[1] : null);
                        if (ca != null && cb != null)
                            fns.Add(dst => { dst.Add(ca.X - cb.X); dst.Add(ca.Y - cb.Y); });
                    }
                    break;

                // ЗАКРЕПЛЕНИЕ. Вид ограничения был объявлен, но в решателе НЕ ОБРАБАТЫВАЛСЯ:
                // закреплённая точка спокойно уезжала (в проверке — на (0.15, 0.35)). Невязка
                // держит каждую точку элемента у её положения на начало решения.
                case ConstraintType.Fix:
                    foreach (var item in it)
                        foreach (var pt in PointsOf(item))
                        {
                            double fx = pt.X, fy = pt.Y;
                            var p2 = pt;
                            fns.Add(dst => { dst.Add(p2.X - fx); dst.Add(p2.Y - fy); });
                        }
                    break;
            }
        }

    }

    private static List<ResidualFn> FinishResiduals(Sketch sk, List<ResidualFn> fns)
    {
        foreach (var d in sk.Dimensions)
        {
            // В решении участвуют только УПРАВЛЯЮЩИЕ размеры — те, что
            // заблокированы пользователем. Остальные эскиз измеряют,
            // а не задают: держи они геометрию всерьёз, только что
            // нарисованную фигуру нельзя было бы ни потянуть, ни поправить.
            if (!d.IsDriving) continue;
            var it = d.Items;
            switch (d.Type)
            {
                case DimensionType.DistancePointPoint:
                    if (it.Count >= 2 && it[0] is SkPoint da && it[1] is SkPoint db)
                    {
                        // k96: НУЛЕВОЙ РАЗМЕР — совпадение (две невязки по осям): у |a − b| в нуле нет производной, и решатель
                        // не сходился к нулю (размер 0 отвергался вовсе). Мутация P3D_SKZERODIM=0.
                        if (d.Value == 0 && !ZeroDimOff) fns.Add(dst => { dst.Add(da.X - db.X); dst.Add(da.Y - db.Y); });
                        else fns.Add(dst => dst.Add((da.P - db.P).Length - d.Value));
                    }
                    break;
                case DimensionType.DistancePointLine:
                    if (it.Count >= 2 && it[0] is SkPoint dp && it[1] is SkLine dl)
                    {
                        // k96: СТОРОНА — та, где точка сейчас. Размер хранит расстояние без знака, а уравнение было знаковым:
                        // точка по другую сторону прямой перескакивала через неё на +значение. Мутация P3D_SKSIDE=0.
                        double side = SideOff ? 1 : SideOf(DistPointLine(dp.P, dl));
                        fns.Add(dst => dst.Add(side * DistPointLine(dp.P, dl) - d.Value));
                    }
                    break;
                case DimensionType.DistanceLineLine:
                    if (it.Count >= 2 && it[0] is SkLine la && it[1] is SkLine lb)
                    {
                        double side = SideOff ? 1 : SideOf(DistPointLine(lb.A.P, la));
                        fns.Add(dst => dst.Add(side * DistPointLine(lb.A.P, la) - d.Value));
                    }
                    break;
                case DimensionType.AngleLineLine:
                    if (it.Count >= 2 && it[0] is SkLine aa && it[1] is SkLine ab)
                        // Та же формула, что и у измерения: иначе размер
                        // начнёт требовать угол, которого никто не задавал.
                        fns.Add(dst => dst.Add(DimensionMeasure.LineAngleDegrees(aa, ab) - d.Value));
                    break;
                case DimensionType.Radius:
                    if (it.Count >= 1 && it[0] is SkCircle rc) fns.Add(dst => dst.Add(rc.Radius - d.Value));
                    else if (it.Count >= 1 && it[0] is SkArc ra) fns.Add(dst => dst.Add(ra.Radius - d.Value));
                    break;
                case DimensionType.Diameter:
                    if (it.Count >= 1 && it[0] is SkCircle dc) fns.Add(dst => dst.Add(dc.Radius * 2 - d.Value));
                    else if (it.Count >= 1 && it[0] is SkArc darc) fns.Add(dst => dst.Add(darc.Radius * 2 - d.Value));
                    break;

                case DimensionType.EllipseRadiusU:
                    if (it.Count >= 1 && it[0] is SkEllipse eu) fns.Add(dst => dst.Add(eu.RU - d.Value));
                    break;
                case DimensionType.ArcLength:
                    if (it.Count >= 1 && it[0] is SkArc alc) fns.Add(dst => dst.Add(alc.Radius * Math.Abs(alc.Sweep) - d.Value));
                    break;

                case DimensionType.EllipseRadiusV:
                    if (it.Count >= 1 && it[0] is SkEllipse ev) fns.Add(dst => dst.Add(ev.RV - d.Value));
                    break;
                case DimensionType.CoordX:
                    if (it.Count >= 1 && it[0] is SkPoint cx) fns.Add(dst => dst.Add(cx.X - d.Value));
                    break;
                case DimensionType.CoordY:
                    if (it.Count >= 1 && it[0] is SkPoint cy) fns.Add(dst => dst.Add(cy.Y - d.Value));
                    break;
            }
        }

        return fns;
    }

    static readonly bool ZeroDimOff = Environment.GetEnvironmentVariable("P3D_SKZERODIM") == "0";
    static readonly bool SideOff = Environment.GetEnvironmentVariable("P3D_SKSIDE") == "0";
    static double SideOf(double signed) => signed < 0 ? -1 : 1;

    /// <summary>Точки элемента: у отрезка — концы, у окружности — центр, у точки — она сама.</summary>
    private static IEnumerable<SkPoint> PointsOf(SkEntity? e)
    {
        switch (e)
        {
            case SkPoint p: yield return p; break;
            case SkLine l: yield return l.A; yield return l.B; break;
            case SkCircle c: yield return c.Center; break;
            case SkArc a: yield return a.Center; break;
        }
    }

    private static SkPoint? CenterOf(SkEntity? e) => e switch
    {
        SkCircle c => c.Center,
        SkArc a => a.Center,
        SkEllipse el => el.Center,
        SkPoint p => p,
        _ => null
    };

    private static Vector2d Dir(SkLine l)
    {
        Vector2d d = l.B.P - l.A.P;
        double n = d.Length;
        return n < 1e-12 ? new Vector2d(1, 0) : d / n;
    }

    private static double Cross(Vector2d a, Vector2d b) => a.X * b.Y - a.Y * b.X;

    private static double DistPointLine(Vector2d p, SkLine l)
        => Cross(Dir(l), p - l.A.P);

    // -------------------------------------------------------------- численность

    private double[] EvalVector(List<Var> vars, double[] x, List<ResidualFn> fns,
                                SkPoint? dragged, out int rows)
    {
        for (int i = 0; i < vars.Count; i++) vars[i].Set(x[i]);

        var buf = new List<double>(fns.Count * 2 + vars.Count);
        foreach (var f in fns) f(buf);

        // Регуляризация: удерживаем переменные около исходных значений.
        for (int i = 0; i < vars.Count; i++)
        {
            double w = Regularization;
            if (IsDragged(vars[i].Point, dragged)) w = DragWeight;
            buf.Add(w * (x[i] - vars[i].Start));
        }

        rows = buf.Count;
        return buf.ToArray();
    }

    private double Residual(List<Var> vars, double[] x, List<ResidualFn> fns, SkPoint? dragged)
    {
        var r = EvalVector(vars, x, fns, dragged, out int rows);
        double s = 0;
        for (int i = 0; i < rows; i++) s += r[i] * r[i];
        return s;
    }

    /// <summary>Сумма квадратов невязок ограничений и размеров (без регуляризации и перетаскивания).</summary>
    private static double ConstraintResidual(List<Var> vars, double[] x, List<ResidualFn> fns)
    {
        for (int i = 0; i < vars.Count; i++) vars[i].Set(x[i]);
        var buf = new List<double>();
        foreach (var f in fns) f(buf);
        double s = 0; foreach (double v in buf) s += v * v;
        return s;
    }

    /// <summary>Сколько уравнений дают ограничения и размеры (без регуляризации).</summary>
    private static int CountEquations(List<Var> vars, double[] x, List<ResidualFn> fns)
    {
        for (int i = 0; i < vars.Count; i++) vars[i].Set(x[i]);
        var buf = new List<double>();
        foreach (var f in fns) f(buf);
        return buf.Count;
    }

    /// <summary>
    /// Ранг якобиана системы ограничений — методом Гаусса с выбором
    /// главного элемента.
    ///
    /// Две вещи здесь принципиальны, и обе выяснились на практике.
    ///
    /// ЦЕНТРАЛЬНЫЕ разности вместо односторонних. У односторонних
    /// погрешность порядка шага: строка, которая на самом деле линейно
    /// зависит от других, после исключения даёт остаток около 1e-8 вместо
    /// нуля. Это больше любого разумного порога, и зависимая строка
    /// засчитывается как независимая — ранг завышается, степени свободы
    /// занижаются. У центральных разностей погрешность квадратична,
    /// и тот же остаток падает до 1e-16.
    ///
    /// НОРМИРОВКА строк. Без неё порог приходится подбирать под масштаб
    /// задачи: у координатного уравнения производная равна единице,
    /// у углового — обратной длине. После нормировки все строки
    /// сравнимы, и порог имеет один смысл на любом чертеже.
    ///
    /// Регуляризация в расчёт не идёт: она не ограничение, а приём,
    /// удерживающий недоопределённый эскиз от расползания. Учти её —
    /// и степеней свободы никогда бы не оказалось.
    /// </summary>
    private int JacobianRank(List<Var> vars, double[] x, List<ResidualFn> fns, Sparse? sp = null)
    {
        int n = vars.Count;
        int m = CountEquations(vars, x, fns);
        if (m == 0 || n == 0) return 0;
        if (UseSparse(n) && sp != null && sp.ConstraintRows == m)
        {
            // KRN-10 (10.8): разреженный якобиан (центральные разности только по зависящим невязкам), нормировка строк,
            // исключение по разреженным строкам — тот же порог
            var rowsS = new List<Dictionary<int, double>>(m);
            for (int k = 0; k < m; k++) rowsS.Add(new Dictionary<int, double>());
            var xp2 = (double[])x.Clone();
            for (int i = 0; i < n; i++) vars[i].Set(x[i]);
            var bp = new List<double>(); var bm = new List<double>();
            for (int j = 0; j < n; j++)
            {
                double h = Math.Max(1e-6, Math.Abs(x[j]) * 1e-6);
                foreach (int k in sp.FnsOfVar[j])
                {
                    vars[j].Set(x[j] + h); bp.Clear(); fns[k](bp);
                    vars[j].Set(x[j] - h); bm.Clear(); fns[k](bm);
                    vars[j].Set(x[j]);
                    for (int t = 0; t < sp.FnCount[k] && t < bp.Count && t < bm.Count; t++)
                    {
                        double v = (bp[t] - bm[t]) / (2 * h);
                        if (v != 0) rowsS[sp.FnOffset[k] + t][j] = v;
                    }
                }
            }
            foreach (var rowS in rowsS)
            {
                double norm = Math.Sqrt(rowS.Values.Sum(v => v * v));
                if (norm < 1e-300) continue;
                foreach (var key in rowS.Keys.ToList()) rowS[key] /= norm;
            }
            return SparseLinear.Rank(rowsS, n, 1e-7);
        }

        var J = new double[m, n];
        var xp = (double[])x.Clone();

        for (int j = 0; j < n; j++)
        {
            double h = Math.Max(1e-6, Math.Abs(x[j]) * 1e-6);

            xp[j] = x[j] + h;
            var plus = EvalEquations(vars, xp, fns);
            xp[j] = x[j] - h;
            var minus = EvalEquations(vars, xp, fns);
            xp[j] = x[j];

            for (int k = 0; k < m; k++) J[k, j] = (plus[k] - minus[k]) / (2 * h);
        }

        for (int i = 0; i < n; i++) vars[i].Set(x[i]);

        // Нормировка строк: после неё порог не зависит от масштаба задачи.
        for (int k = 0; k < m; k++)
        {
            double norm = 0;
            for (int j = 0; j < n; j++) norm += J[k, j] * J[k, j];
            norm = Math.Sqrt(norm);
            if (norm < 1e-300) continue;
            for (int j = 0; j < n; j++) J[k, j] /= norm;
        }

        const double eps = 1e-7;

        int rank = 0;
        int row = 0;
        for (int col = 0; col < n && row < m; col++)
        {
            int piv = -1;
            double best = eps;
            for (int k = row; k < m; k++)
            {
                double a = Math.Abs(J[k, col]);
                if (a > best) { best = a; piv = k; }
            }
            if (piv < 0) continue;

            if (piv != row)
                for (int j = 0; j < n; j++) (J[row, j], J[piv, j]) = (J[piv, j], J[row, j]);

            for (int k = row + 1; k < m; k++)
            {
                double f = J[k, col] / J[row, col];
                if (f == 0) continue;
                for (int j = col; j < n; j++) J[k, j] -= f * J[row, j];
            }

            rank++;
            row++;
        }
        return rank;
    }

    /// <summary>Значения одних только уравнений ограничений, без регуляризации.</summary>
    private static double[] EvalEquations(List<Var> vars, double[] x, List<ResidualFn> fns)
    {
        for (int i = 0; i < vars.Count; i++) vars[i].Set(x[i]);
        var buf = new List<double>();
        foreach (var f in fns) f(buf);
        return buf.ToArray();
    }

    private double[,] Jacobian(List<Var> vars, double[] x, List<ResidualFn> fns,
                               SkPoint? dragged, int rows)
    {
        int n = vars.Count;
        var J = new double[rows, n];
        var baseR = EvalVector(vars, x, fns, dragged, out _);
        var xp = new double[n];
        Array.Copy(x, xp, n);

        for (int j = 0; j < n; j++)
        {
            double h = Math.Max(1e-7, Math.Abs(x[j]) * 1e-7);
            xp[j] = x[j] + h;
            var r = EvalVector(vars, xp, fns, dragged, out _);
            xp[j] = x[j];
            for (int k = 0; k < rows; k++) J[k, j] = (r[k] - baseR[k]) / h;
        }

        for (int i = 0; i < n; i++) vars[i].Set(x[i]);
        return J;
    }

    private void Polish(List<Var> vars, double[] x, List<ResidualFn> fns, Sparse? sp)
    {
        int n = vars.Count;
        sp ??= SparseStructure(vars, x, fns);
        for (int it = 0; it < 8; it++)
        {
            for (int i = 0; i < n; i++) vars[i].Set(x[i]);
            var r = new List<double>(); foreach (var f in fns) f(r);
            int m = sp.ConstraintRows;
            if (r.Count != m || m == 0) return;
            double before = r.Sum(v => v * v);
            if (before < 1e-28) return;
            var (rowCols, rowVals) = SparseJacobian(vars, x, fns, null, sp, r.Concat(new double[n]).ToArray());
            // (J Jᵀ + εI) y = r; dx = −Jᵀ y.
            if (UseSparse(n))
            {
                // KRN-10 (10.8): J·Jᵀ разреженно — строки, делящие столбец; Холецкий в профиле
                var colRows = new Dictionary<int, List<(int Row, double V)>>();
                for (int a = 0; a < m; a++) for (int p = 0; p < rowCols[a].Count; p++) { int c = rowCols[a][p]; if (!colRows.TryGetValue(c, out var lst)) colRows[c] = lst = new(); lst.Add((a, rowVals[a][p])); }
                var As = new Dictionary<int, double>[m]; for (int a = 0; a < m; a++) As[a] = new Dictionary<int, double>();
                foreach (var lst in colRows.Values)
                    foreach (var (ra, va) in lst) foreach (var (rb, vb) in lst) As[ra][rb] = (As[ra].TryGetValue(rb, out var o) ? o : 0) + va * vb;
                double dmax = 0; for (int a = 0; a < m; a++) dmax = Math.Max(dmax, As[a].TryGetValue(a, out var o) ? o : 0);
                for (int a = 0; a < m; a++) As[a][a] = (As[a].TryGetValue(a, out var o) ? o : 0) + 1e-12 * Math.Max(dmax, 1e-30);
                if (!SparseLinear.SolveSpd(As, r.ToArray(), out double[] ys)) return;
                var trialS = (double[])x.Clone();
                for (int a = 0; a < m; a++) { var ca = rowCols[a]; var va = rowVals[a]; for (int p = 0; p < ca.Count; p++) trialS[ca[p]] -= va[p] * ys[a]; }
                for (int i = 0; i < n; i++) vars[i].Set(trialS[i]);
                var r2s = new List<double>(); foreach (var f in fns) f(r2s);
                if (r2s.Sum(v => v * v) >= before) { for (int i = 0; i < n; i++) vars[i].Set(x[i]); return; }
                Array.Copy(trialS, x, n);
                continue;
            }
            var A = new double[m, m];
            for (int a = 0; a < m; a++)
                for (int b = a; b < m; b++)
                {
                    double sum = 0;
                    var ca = rowCols[a]; var va = rowVals[a]; var cb = rowCols[b]; var vb = rowVals[b];
                    for (int p = 0; p < ca.Count; p++) { int idx = cb.IndexOf(ca[p]); if (idx >= 0) sum += va[p] * vb[idx]; }
                    A[a, b] = A[b, a] = sum;
                }
            double diag = 0; for (int a = 0; a < m; a++) diag = Math.Max(diag, A[a, a]);
            for (int a = 0; a < m; a++) A[a, a] += 1e-12 * Math.Max(diag, 1e-30);
            if (!SolveLinear(A, r.ToArray(), out double[] y)) return;
            var trial = (double[])x.Clone();
            for (int a = 0; a < m; a++) { var ca = rowCols[a]; var va = rowVals[a]; for (int p = 0; p < ca.Count; p++) trial[ca[p]] -= va[p] * y[a]; }
            for (int i = 0; i < n; i++) vars[i].Set(trial[i]);
            var r2 = new List<double>(); foreach (var f in fns) f(r2);
            double after = r2.Sum(v => v * v);
            if (after >= before) { for (int i = 0; i < n; i++) vars[i].Set(x[i]); return; }
            Array.Copy(trial, x, n);
        }
        for (int i = 0; i < n; i++) vars[i].Set(x[i]);
    }

    private sealed class Sparse
    {
        public int[] FnOffset = null!, FnCount = null!;
        public List<int>[] FnsOfVar = null!;
        public int ConstraintRows;
    }

    /// <summary>Какие невязки зависят от какой переменной и где строки каждой невязки.</summary>
    private static Sparse SparseStructure(List<Var> vars, double[] x, List<ResidualFn> fns)
    {
        int n = vars.Count, m = fns.Count;
        for (int i = 0; i < n; i++) vars[i].Set(x[i]);
        var sp = new Sparse { FnOffset = new int[m], FnCount = new int[m], FnsOfVar = new List<int>[n] };
        var baseOut = new List<double>[m];
        int off = 0;
        for (int k = 0; k < m; k++)
        {
            baseOut[k] = new List<double>(); fns[k](baseOut[k]);
            sp.FnOffset[k] = off; sp.FnCount[k] = baseOut[k].Count; off += baseOut[k].Count;
        }
        sp.ConstraintRows = off;
        var buf = new List<double>();
        for (int j = 0; j < n; j++)
        {
            sp.FnsOfVar[j] = new List<int>();
            // Два пробных сдвига разного знака и размера: у невязки с нулевой производной в одной
            // точке связь проявится в другой.
            foreach (double frac in new[] { 1e-4, -3e-3 })
            {
                double h = frac * Math.Max(1, Math.Abs(x[j]));
                vars[j].Set(x[j] + h);
                for (int k = 0; k < m; k++)
                {
                    if (sp.FnsOfVar[j].Contains(k)) continue;
                    buf.Clear(); fns[k](buf);
                    for (int t = 0; t < buf.Count; t++)
                        if (t >= baseOut[k].Count || buf[t] != baseOut[k][t]) { sp.FnsOfVar[j].Add(k); break; }
                }
                vars[j].Set(x[j]);
            }
        }
        return sp;
    }

    /// <summary>Строки якобиана: столбцы и значения ненулевых элементов (вперёд разностью).</summary>
    private (List<int>[] Cols, List<double>[] Vals) SparseJacobian(List<Var> vars, double[] x, List<ResidualFn> fns,
                                                                   SkPoint? dragged, Sparse sp, double[] baseR)
    {
        int n = vars.Count, rows = baseR.Length;
        var cols = new List<int>[rows]; var vals = new List<double>[rows];
        for (int k = 0; k < rows; k++) { cols[k] = new List<int>(4); vals[k] = new List<double>(4); }
        for (int i = 0; i < n; i++) vars[i].Set(x[i]);
        var buf = new List<double>();
        for (int j = 0; j < n; j++)
        {
            double h = Math.Max(1e-7, Math.Abs(x[j]) * 1e-7);
            vars[j].Set(x[j] + h);
            foreach (int k in sp.FnsOfVar[j])
            {
                buf.Clear(); fns[k](buf);
                for (int t = 0; t < sp.FnCount[k] && t < buf.Count; t++)
                {
                    int row = sp.FnOffset[k] + t;
                    double dv = (buf[t] - baseR[row]) / h;
                    if (dv != 0) { cols[row].Add(j); vals[row].Add(dv); }
                }
            }
            vars[j].Set(x[j]);
            // Строка регуляризации переменной j — диагональ.
            double w = Regularization;
            if (IsDragged(vars[j].Point, dragged)) w = DragWeight;
            int rr = sp.ConstraintRows + j;
            if (rr < rows) { cols[rr].Add(j); vals[rr].Add(w); }
        }
        return (cols, vals);
    }

    /// <summary>Метод Гаусса с выбором главного элемента.</summary>
    private static bool SolveLinear(double[,] A, double[] b, out double[] x)
    {
        int n = b.Length;
        x = new double[n];
        var M = (double[,])A.Clone();
        var v = (double[])b.Clone();

        for (int c = 0; c < n; c++)
        {
            int piv = c;
            double best = Math.Abs(M[c, c]);
            for (int r = c + 1; r < n; r++)
            {
                double a = Math.Abs(M[r, c]);
                if (a > best) { best = a; piv = r; }
            }
            if (best < 1e-14) return false;

            if (piv != c)
            {
                for (int k = 0; k < n; k++) (M[c, k], M[piv, k]) = (M[piv, k], M[c, k]);
                (v[c], v[piv]) = (v[piv], v[c]);
            }

            for (int r = c + 1; r < n; r++)
            {
                double f = M[r, c] / M[c, c];
                if (f == 0) continue;
                for (int k = c; k < n; k++) M[r, k] -= f * M[c, k];
                v[r] -= f * v[c];
            }
        }

        for (int r = n - 1; r >= 0; r--)
        {
            double s = v[r];
            for (int k = r + 1; k < n; k++) s -= M[r, k] * x[k];
            x[r] = s / M[r, r];
            if (double.IsNaN(x[r]) || double.IsInfinity(x[r])) return false;
        }
        return true;
    }
}
