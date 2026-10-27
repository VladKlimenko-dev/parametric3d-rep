using OpenTK.Mathematics;

namespace Parametric3D.Model.Sketching;

/// <summary>Почему правка эскиза отвергнута.</summary>
public enum SketchEditFailure
{
    None,
    /// <summary>Условия противоречат друг другу: решения нет.</summary>
    Inconsistent,
    /// <summary>Новая связь избыточна: она уже следует из других.</summary>
    Redundant,
    /// <summary>Решение есть только с выродившейся геометрией (отрезок нулевой длины, окружность нулевого радиуса).</summary>
    Singular,
    /// <summary>Решатель не сошёлся.</summary>
    DidNotConverge,
}

/// <summary>Итог правки: принята или нет, и с чем она спорит.</summary>
public sealed class SketchEditResult
{
    public bool Ok;
    public SolveStatus Status;
    public SketchEditFailure Failure;
    /// <summary>Связи и сильные размеры, без любого из которых правка проходит (участники противоречия).</summary>
    public readonly List<SkConstraint> Constraints = new();
    public readonly List<SkDimension> Dimensions = new();
    /// <summary>Выродившиеся элементы (Singular).</summary>
    public readonly List<SkEntity> Degenerate = new();
    /// <summary>Правка прошла, удерживая слабые размеры (как в Creo: меняется только заданное).</summary>
    public bool HeldWeak;
    /// <summary>k123: участники не найдены точно — перечислены связи и размеры, касающиеся элементов правки.</summary>
    public bool Related;
}

/// <summary>
/// k96: ПРАВКА ЭСКИЗА С ПРОВЕРКОЙ И ОБЪЯСНЕНИЕМ (отзыв 17:55, пп. 15–18).
///
/// Прежде окно правки размера решало эскиз и при статусе «переопределён» или «противоречив» возвращало значение — но не
/// геометрию: точки оставались там, куда их увёл неудачный расчёт (снимок 3: размер 47.14 прежний, контур уехал за ось).
/// «Переопределён» при этом означал лишь избыточность (автопривязка ставила пустые связи у каждого узла ломаной) — любая
/// правка отвергалась без объяснения.
///
/// Теперь:
///   * отвергается только настоящее противоречие (нет решения), вырождение и несошедшийся расчёт; избыточность согласных
///     условий — нет (связь, ставшая лишней, при наложении — отвергается: её незачем добавлять);
///   * при отказе геометрия возвращается точно (снимок положений);
///   * участники противоречия находятся снятием по одному (без участника правка проходит) — их показывают красным и
///     перечисляют, панель «Разрешить эскиз» предлагает удалить участника или сделать размер справочным;
///   * новое значение размера достигается шагами (продолжение по параметру), удерживая СЛАБЫЕ размеры: набор слабых
///     размеров определяет эскиз полностью, и меняется только заданный размер (как в Creo); если со слабыми решения нет —
///     без них (наименьшее перемещение).
/// </summary>
public static class SketchEditing
{
    /// <summary>P3D_SKHOLDWEAK=0 — без удержания слабых размеров (наименьшее перемещение).</summary>
    public static readonly bool HoldWeakOff = Environment.GetEnvironmentVariable("P3D_SKHOLDWEAK") == "0";
    /// <summary>P3D_SKCONTINUE=0 — новое значение сразу, без шагов.</summary>
    public static readonly bool ContinuationOff = Environment.GetEnvironmentVariable("P3D_SKCONTINUE") == "0";
    /// <summary>P3D_SKRESTORE=0 — при отказе геометрия не возвращается (прежнее поведение).</summary>
    public static readonly bool RestoreOff = Environment.GetEnvironmentVariable("P3D_SKRESTORE") == "0";
    public static long HeldWeakEdits, FreeEdits, RejectedEdits, Diagnosed;

    /// <summary>Снимок положений свободных величин: точки, радиусы, полуоси и поворот эллипсов.</summary>
    public sealed class Geometry
    {
        readonly List<(SkPoint P, double X, double Y)> _pts = new();
        readonly List<(SkCircle C, double R)> _circles = new();
        readonly List<(SkEllipse E, double U, double V, double Rot)> _ellipses = new();

        public static Geometry Take(Sketch sk)
        {
            var g = new Geometry();
            var seen = new HashSet<SkPoint>(ReferenceEqualityComparer.Instance);
            foreach (var e in sk.Entities)
            {
                foreach (var h in e.Handles) if (seen.Add(h)) g._pts.Add((h, h.X, h.Y));
                if (e is SkCircle c) g._circles.Add((c, c.Radius));
                if (e is SkEllipse el) g._ellipses.Add((el, el.RU, el.RV, el.Rotation));
            }
            return g;
        }

        public void Restore()
        {
            foreach (var (p, x, y) in _pts) { p.X = x; p.Y = y; }
            foreach (var (c, r) in _circles) c.Radius = r;
            foreach (var (e, u, v, rot) in _ellipses) { e.RU = u; e.RV = v; e.Rotation = rot; }
        }

        /// <summary>Наибольший сдвиг точки относительно снимка.</summary>
        public double MaxShift() => _pts.Count == 0 ? 0 : _pts.Max(t => Math.Sqrt((t.P.X - t.X) * (t.P.X - t.X) + (t.P.Y - t.Y) * (t.P.Y - t.Y)));
    }

    /// <summary>Правка не годится: нет решения, вырождение (новое) или не сошлось; при <paramref name="redundant"/> — и новая избыточность.</summary>
    public static SketchEditFailure FailureOf(SolveStatus before, SolveStatus after, bool redundant) => after switch
    {
        SolveStatus.Inconsistent => SketchEditFailure.Inconsistent,
        SolveStatus.DidNotConverge => SketchEditFailure.DidNotConverge,
        SolveStatus.Singular when before != SolveStatus.Singular => SketchEditFailure.Singular,
        SolveStatus.Overconstrained when redundant && before is not (SolveStatus.Overconstrained or SolveStatus.Inconsistent or SolveStatus.DidNotConverge) => SketchEditFailure.Redundant,
        _ => SketchEditFailure.None,
    };

    // ------------------------------------------------------------------ размер

    /// <summary>
    /// Новое значение размера (число, формула — уже вычислена в <paramref name="value"/>). Размер становится сильным.
    /// Отказ — всё как было (значение, флаги, геометрия), в итоге — участники противоречия.
    /// </summary>
    public static SketchEditResult SetDimensionValue(Sketch sk, SkDimension d, double value, string? formula, ISketchSolver solver)
    {
        var res = new SketchEditResult();
        var geo = Geometry.Take(sk);
        var st0 = sk.Solve(solver);
        geo.Restore();
        var sizes0 = Sizes(sk); double scale0 = Scale(sk);
        // свои элементы размера (длина отрезка, радиус) стягиваются, только если размер к этому и ведёт (значение около 0)
        var own = new HashSet<SkEntity>(ReferenceEqualityComparer.Instance);
        if (Math.Abs(value) < 1e-3 * scale0) foreach (var it in d.Items) { own.Add(it); foreach (var e in sk.Entities) if (e.Handles.Any(h => ReferenceEquals(h, it))) own.Add(e); }
        var saved = (d.Value, d.Formula, d.Locked, d.IsDefault, d.Reference, d.Conflict);
        double from = DimensionMeasure.Value(d);
        if (double.IsNaN(from)) from = d.Value;
        if (d.Type is DimensionType.CoordX or DimensionType.CoordY) { } // знак — у значения (сторона от нуля)
        d.Formula = formula; d.Locked = true; d.IsDefault = false; d.Reference = false;

        // удерживаются слабые размеры, НЕ касающиеся правимого места: точек размера (и совпадающих с ними) и кривых,
        // сходящихся в этих точках. Прямоугольник-ступенька (снимок 2): верх 47.14 → 10 — сдвигается правый конец верха,
        // наклонный отрезок укорачивается, остальное на месте. Удерживать ВСЕ слабые (точно как Creo) значило бы
        // сохранить и длину наклонного отрезка — узел у основания уходил вниз за нижнюю сторону.
        var near = NearPoints(sk, d);
        var adjacent = new HashSet<SkEntity>(sk.Entities.Where(e => !d.Items.Contains(e) && e.Handles.Any(near.Contains)), ReferenceEqualityComparer.Instance);
        var weak = sk.Dimensions.Where(x => !ReferenceEquals(x, d) && x.IsDefault && !x.Locked && x.Enabled && !x.Reference
                                            && !x.Items.Any(i => i is SkPoint ip && near.Contains(ip) || adjacent.Contains(i))).ToList();
        SolveStatus st = st0;
        bool ok = false;
        if (!HoldWeakOff && weak.Count > 0)
        {
            foreach (var w in weak) { double m = DimensionMeasure.Value(w); if (!double.IsNaN(m)) w.Value = m; w.Locked = true; }
            try { ok = Reach(sk, d, from, value, solver, st0, out st); }
            finally { foreach (var w in weak) w.Locked = false; }
            if (ok) { res.HeldWeak = true; Interlocked.Increment(ref HeldWeakEdits); }
            else geo.Restore();
        }
        if (!ok)
        {
            ok = Reach(sk, d, from, value, solver, st0, out st);
            if (ok) Interlocked.Increment(ref FreeEdits);
        }
        if (ok && Shrunk(sk, sizes0, scale0, own) is { Count: > 0 } shrunk)
        {
            // k124: размер достигнут ценой стянутого элемента — снова, удерживая его длину; иначе — отказ «вырождение»
            geo.Restore();
            var tmp = Hold(sk, shrunk, sizes0);
            bool okHeld;
            try { okHeld = Reach(sk, d, from, value, solver, st0, out st) && Shrunk(sk, sizes0, scale0, own).Count == 0; }
            finally { foreach (var t in tmp) sk.Dimensions.Remove(t); sk.BumpStructure(); }
            if (okHeld) Interlocked.Increment(ref CollapseHeld);
            else { Interlocked.Increment(ref CollapseRejected); ok = false; st = SolveStatus.Singular; res.Degenerate.AddRange(shrunk); }
        }
        if (ok)
        {
            d.Value = value;
            res.Status = sk.Solve(solver);
            res.Ok = true;
            return res;
        }
        // отказ: участники — при новом значении, затем всё назад
        res.Failure = FailureOf(st0, st, false);
        if (res.Failure == SketchEditFailure.None) res.Failure = SketchEditFailure.Inconsistent;
        if (res.Failure == SketchEditFailure.Singular && res.Degenerate.Count == 0) res.Degenerate.AddRange(sk.DegenerateEntities);
        d.Value = value;
        geo.Restore();
        Diagnose(sk, solver, geo, st0, redundant: false, new object[] { d }, res);
        if (res.Constraints.Count + res.Dimensions.Count == 0) AddRelated(sk, d.Items, new object[] { d }, res);
        if (RestoreOff) { d.Value = value; sk.Solve(solver); }   // мутация: геометрия остаётся там, куда её увёл неудачный расчёт
        (d.Value, d.Formula, d.Locked, d.IsDefault, d.Reference, d.Conflict) = saved;
        if (!RestoreOff) geo.Restore();
        res.Status = sk.Solve(solver);
        if (!RestoreOff) geo.Restore();
        Interlocked.Increment(ref RejectedEdits);
        return res;
    }

    /// <summary>Точки размера: сами точки, концы и центры его кривых — и совпадающие с ними (связь «Совпадение»).</summary>
    static HashSet<SkPoint> NearPoints(Sketch sk, SkDimension d)
    {
        var res = new HashSet<SkPoint>(ReferenceEqualityComparer.Instance);
        foreach (var it in d.Items)
            if (!it.Builtin) foreach (var h in it.Handles) res.Add(h);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var c in sk.Constraints)
            {
                if (!c.Enabled || c.Type != ConstraintType.Coincident) continue;
                var ps = c.Items.OfType<SkPoint>().Where(p => !p.Builtin).ToList();
                if (ps.Any(res.Contains)) foreach (var p in ps) grew |= res.Add(p);
            }
        }
        return res;
    }

    /// <summary>Значение размера — шагами от нынешнего к новому (каждый шаг — решение от предыдущего).</summary>
    static bool Reach(Sketch sk, SkDimension d, double from, double to, ISketchSolver solver, SolveStatus st0, out SolveStatus st)
    {
        int n = 1;
        if (!ContinuationOff && !double.IsNaN(from) && from != to)
        {
            double scale = Math.Max(Math.Max(Math.Abs(from), Math.Abs(to)), 1e-3);
            n = Math.Clamp((int)Math.Ceiling(Math.Abs(to - from) / (0.1 * scale)), 1, 24);
        }
        st = st0;
        for (int k = 1; k <= n; k++)
        {
            d.Value = k == n ? to : from + (to - from) * k / n;
            st = sk.Solve(solver);
            if (FailureOf(st0, st, false) != SketchEditFailure.None) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ связи

    /// <summary>
    /// Наложить связи; противоречие, вырождение или избыточность — отказ (связи сняты, геометрия как была) с участниками.
    /// Повтор уже наложенной связи пропускается.
    /// </summary>
    public static SketchEditResult AddConstraints(Sketch sk, IEnumerable<(ConstraintType T, SkEntity[] Items)> plan, ISketchSolver solver, List<SkConstraint>? added = null)
    {
        var res = new SketchEditResult();
        var geo = Geometry.Take(sk);
        var st0 = sk.Solve(solver);
        int conflicts0 = sk.Constraints.Count(c => c.Conflict) + sk.Dimensions.Count(x => x.Conflict);
        var made = new List<SkConstraint>();
        foreach (var (t, items) in plan)
        {
            if (sk.Constraints.Any(c => c.Type == t && c.SameItems(items))) continue;
            var c = new SkConstraint(t, items);
            if (Sketch.IsTrivial(c)) continue;
            made.Add(c);
        }
        // k106: выполнена ли новая связь уже сейчас (при решении без неё)
        var geoOld = Geometry.Take(sk);
        double scale = Scale(sk);
        var sizes0 = Sizes(sk);
        double r0 = NumericSketchSolver.ResidualOf(sk, made);
        foreach (var c in made) sk.Constraints.Add(c);
        sk.BumpStructure();
        var st = sk.Solve(solver);
        var fail = FailureOf(st0, st, redundant: true);
        bool oldGood = st0 is SolveStatus.Ok or SolveStatus.Underconstrained or SolveStatus.Overconstrained;
        // k106 (снимок 13:13): «избыточна» — по рангу якобиана; у касания по расстоянию вместе с общей точкой (и других
        // вырожденных записей) ранг теряется, хотя связь ничего не повторяет. Связь избыточна, только если она СЛЕДУЕТ из
        // прочих: выполнена при любом их решении. Не выполнена при нынешнем решении без неё — не следует (принять);
        // выполнена — проверка возмущением: прочие решаются от сдвинутой геометрии, и если связь где-то нарушена — не
        // следует. Мутация P3D_SKREDUNDANTCHECK=0 — по рангу (прежде).
        if (fail == SketchEditFailure.Redundant && oldGood && !RedundantCheckOff)
        {
            if (r0 > 1e-6 * scale || !Implied(sk, solver, made, scale)) fail = SketchEditFailure.None;
        }
        // k106: противоречие или нет схождения — повтор от слегка сдвинутой геометрии (вырожденный старт: точки ровно на
        // особой линии); прежде одна неудачная попытка отвергала выполнимую связь. Мутация P3D_SKRETRY=0.
        if (fail is SketchEditFailure.Inconsistent or SketchEditFailure.DidNotConverge && oldGood && !RetryOff)
        {
            for (int trial = 1; trial <= 3 && fail != SketchEditFailure.None; trial++)
            {
                geoOld.Restore();
                Perturb(sk, 1e-3 * scale * trial, trial);
                var st2 = sk.Solve(solver);
                var f2 = FailureOf(st0, st2, redundant: true);
                if (f2 == SketchEditFailure.Redundant && !RedundantCheckOff && (r0 > 1e-6 * scale || !Implied(sk, solver, made, scale))) f2 = SketchEditFailure.None;
                if (f2 == SketchEditFailure.None) { fail = f2; st = st2; }
            }
            if (fail != SketchEditFailure.None) { geoOld.Restore(); sk.Solve(solver); }
        }
        if (fail == SketchEditFailure.None && sk.Constraints.Count(c => c.Conflict) + sk.Dimensions.Count(x => x.Conflict) > conflicts0)
        {
            fail = SketchEditFailure.Inconsistent;
            // структурный конфликт (горизонтально + вертикально на одном отрезке): участники — те, с кем спорит
            foreach (var c in made)
                foreach (var cf in ConstraintConflicts.Find(sk, c.Type, c.Items, ignore: c))
                {
                    if (cf.Constraint != null && !res.Constraints.Contains(cf.Constraint)) res.Constraints.Add(cf.Constraint);
                    if (cf.Dimension != null && !res.Dimensions.Contains(cf.Dimension)) res.Dimensions.Add(cf.Dimension);
                }
        }
        if (fail == SketchEditFailure.None)
        {
            var shrunk = Shrunk(sk, sizes0, scale);
            if (shrunk.Count > 0)
            {
                // стянуло элемент — снова от прежней геометрии, удерживая его длину
                geoOld.Restore();
                var tmp = Hold(sk, shrunk, sizes0);
                var st3 = sk.Solve(solver);
                bool okHeld = FailureOf(st0, st3, redundant: false) == SketchEditFailure.None && Shrunk(sk, sizes0, scale).Count == 0;
                foreach (var t in tmp) sk.Dimensions.Remove(t);
                sk.BumpStructure();
                if (okHeld) { Interlocked.Increment(ref CollapseHeld); st = sk.Solve(solver); }
                else
                {
                    Interlocked.Increment(ref CollapseRejected);
                    fail = SketchEditFailure.Singular;
                    res.Degenerate.AddRange(shrunk);
                    geoOld.Restore();
                }
            }
        }
        if (fail == SketchEditFailure.None)
        {
            res.Ok = true; res.Status = st;
            added?.AddRange(made);
            return res;
        }
        res.Failure = fail;
        if (fail == SketchEditFailure.Singular && res.Degenerate.Count == 0) res.Degenerate.AddRange(sk.DegenerateEntities);
        if (res.Constraints.Count == 0 && res.Dimensions.Count == 0)
        {
            geo.Restore();
            Diagnose(sk, solver, geo, st0, redundant: fail == SketchEditFailure.Redundant, made.Cast<object>().ToArray(), res);
            if (res.Constraints.Count + res.Dimensions.Count == 0) AddRelated(sk, made.SelectMany(c => c.Items), made.Cast<object>().ToArray(), res);
        }
        foreach (var c in made) sk.Constraints.Remove(c);
        sk.BumpStructure();
        geo.Restore();
        res.Status = sk.Solve(solver);
        geo.Restore();
        Interlocked.Increment(ref RejectedEdits);
        return res;
    }

    public static readonly bool RedundantCheckOff = Environment.GetEnvironmentVariable("P3D_SKREDUNDANTCHECK") == "0";

    /// <summary>
    /// k124 (замечание 27.10, 23:41: «длина отрезка становится 0»): РЕШЕНИЕ СТЯНУЛО ЭЛЕМЕНТ. Связь или размер выполнены, но
    /// ценой отрезка (окружности), стянутого почти в точку: решатель ищет ближайшее решение, и иногда ближайшее — схлопнуть
    /// соседний отрезок. Такое решение не принимается: сперва — снова, удерживая длины стянутых (как слабые размеры), не
    /// выйдет — отказ «вырождение» с этими элементами. Мутация P3D_SKCOLLAPSE=0 — принимается.
    /// </summary>
    public static readonly bool CollapseOff = Environment.GetEnvironmentVariable("P3D_SKCOLLAPSE") == "0";
    static Dictionary<SkEntity, double> Sizes(Sketch sk)
    {
        var d = new Dictionary<SkEntity, double>(ReferenceEqualityComparer.Instance);
        foreach (var e in sk.Entities)
        {
            if (e.Role == SkRole.Reference || e.Builtin) continue;
            if (e is SkLine l) d[e] = (l.B.P - l.A.P).Length;
            else if (e is SkCircle c) d[e] = c.Radius;
            else if (e is SkArc a) d[e] = a.Radius;
        }
        return d;
    }
    /// <summary>Элементы, стянутые правкой: были заметной величины, стали в сотни раз меньше и почти нулевыми.</summary>
    static List<SkEntity> Shrunk(Sketch sk, Dictionary<SkEntity, double> before, double scale, ICollection<SkEntity>? allowed = null)
    {
        var res = new List<SkEntity>();
        if (CollapseOff) return res;
        foreach (var (e, now) in Sizes(sk))
            if (before.TryGetValue(e, out var was) && was > 1e-3 * scale && now < 1e-3 * scale && now < was * 0.01 && (allowed == null || !allowed.Contains(e)))
                res.Add(e);
        return res;
    }
    /// <summary>Удерживать длины (радиусы) элементов временными сильными размерами; возвращает их (снять после).</summary>
    static List<SkDimension> Hold(Sketch sk, IEnumerable<SkEntity> es, Dictionary<SkEntity, double> before)
    {
        var tmp = new List<SkDimension>();
        foreach (var e in es)
        {
            SkDimension? d = e switch
            {
                SkLine l => new SkDimension(DimensionType.DistancePointPoint, before[e], l.A, l.B),
                SkCircle c => new SkDimension(DimensionType.Radius, before[e], c),
                SkArc a => new SkDimension(DimensionType.Radius, before[e], a),
                _ => null,
            };
            if (d == null) continue;
            d.Locked = true; d.IsDefault = false; sk.Dimensions.Add(d); tmp.Add(d);
        }
        sk.BumpStructure();
        return tmp;
    }
    public static long CollapseHeld, CollapseRejected;
    public static readonly bool RetryOff = Environment.GetEnvironmentVariable("P3D_SKRETRY") == "0";

    /// <summary>Размер эскиза (наибольшая координата или размер) — масштаб допусков.</summary>
    static double Scale(Sketch sk)
    {
        double L = 1;
        foreach (var p in sk.AllPoints()) L = Math.Max(L, Math.Max(Math.Abs(p.X), Math.Abs(p.Y)));
        foreach (var e in sk.Entities) if (e is SkCircle c) L = Math.Max(L, c.Radius);
        return L;
    }

    /// <summary>Сдвинуть свободные точки (и радиусы окружностей) на величину порядка amount — детерминированно.</summary>
    static void Perturb(Sketch sk, double amount, int seed)
    {
        var rnd = new Random(7919 * seed);
        var seen = new HashSet<SkPoint>(ReferenceEqualityComparer.Instance);
        foreach (var e in sk.Entities)
        {
            if (e.Fixed || e.Role == SkRole.Reference) continue;
            foreach (var h in e.Handles)
                if (!h.Fixed && !h.Builtin && seen.Add(h)) { h.X += (rnd.NextDouble() * 2 - 1) * amount; h.Y += (rnd.NextDouble() * 2 - 1) * amount; }
            if (e is SkCircle c) c.Radius = Math.Max(1e-6, c.Radius + (rnd.NextDouble() * 2 - 1) * amount * 0.5);
        }
    }

    /// <summary>
    /// Следуют ли связи cs из прочих: прочие решаются от нескольких сдвинутых положений; cs нарушены хоть раз — не следуют.
    /// Эскиз и геометрия после проверки — как до неё.
    /// </summary>
    public static bool Implied(Sketch sk, ISketchSolver solver, IReadOnlyList<SkConstraint> cs, double scale)
    {
        var geo = Geometry.Take(sk);
        var on = cs.Select(c => c.Enabled).ToList();
        foreach (var c in cs) c.Enabled = false;
        bool implied = true;
        try
        {
            for (int trial = 1; trial <= 3 && implied; trial++)
            {
                geo.Restore();
                Perturb(sk, 0.02 * scale, trial);
                var st = sk.Solve(solver);
                if (st is SolveStatus.Inconsistent or SolveStatus.DidNotConverge) continue;
                if (NumericSketchSolver.ResidualOf(sk, cs) > 1e-5 * scale) implied = false;
            }
        }
        finally
        {
            for (int i = 0; i < cs.Count; i++) cs[i].Enabled = on[i];
            geo.Restore();
            sk.Solve(solver);
            geo.Restore();
        }
        return implied;
    }

    // ------------------------------------------------------------------ участники противоречия

    /// <summary>
    /// Участники противоречия: связи и сильные размеры (кроме правимых <paramref name="focus"/>), без любого из которых эскиз
    /// решается. Каждая проба — от одной и той же геометрии.
    /// </summary>
    /// <summary>
    /// k123 (замечание 27.10, 21:55, п. 5): снятие по одному не нашло участника (спор нескольких сразу) — в таблицу идут
    /// связи и сильные размеры, касающиеся элементов правки (сами элементы, их концы, элементы с общими концами): пустой
    /// список при «удалите участника (красные)» путал. Признак Related — список приблизительный. P3D_SKRELATED=0 — нет.
    /// </summary>
    public static void AddRelated(Sketch sk, IEnumerable<SkEntity> items, IReadOnlyCollection<object> focus, SketchEditResult res)
    {
        if (RelatedOff) return;
        var near = new HashSet<SkEntity>(ReferenceEqualityComparer.Instance);
        foreach (var e in items)
        {
            near.Add(e);
            foreach (var h in e.Handles) near.Add(h);
        }
        // соседи через общие концы
        foreach (var e in sk.Entities)
            if (e.Handles.Any(h => near.Contains(h))) near.Add(e);
        bool Touches(IEnumerable<SkEntity> xs) => xs.Any(x => near.Contains(x) || x.Handles.Any(h => near.Contains(h)));
        foreach (var c in sk.Constraints)
            if (c.Enabled && !focus.Contains(c) && Touches(c.Items) && !res.Constraints.Contains(c)) res.Constraints.Add(c);
        foreach (var x in sk.Dimensions)
            if (x.IsDriving && !focus.Contains(x) && Touches(x.Items) && !res.Dimensions.Contains(x)) res.Dimensions.Add(x);
        res.Related = res.Constraints.Count + res.Dimensions.Count > 0;
    }
    public static readonly bool RelatedOff = Environment.GetEnvironmentVariable("P3D_SKRELATED") == "0";

    public static void Diagnose(Sketch sk, ISketchSolver solver, Geometry geo, SolveStatus st0, bool redundant, IReadOnlyCollection<object> focus, SketchEditResult res)
    {
        Interlocked.Increment(ref Diagnosed);
        var cons = sk.Constraints.Where(c => c.Enabled && !c.Conflict && !focus.Contains(c)).ToList();
        var dims = sk.Dimensions.Where(x => x.IsDriving && !focus.Contains(x)).ToList();
        // слабые (не управляющие) в решение не входят — не участники
        foreach (var c in cons)
        {
            c.Enabled = false;
            geo.Restore();
            SolveStatus st;
            try { st = sk.Solve(solver); } finally { c.Enabled = true; }
            if (FailureOf(st0, st, redundant) == SketchEditFailure.None && !res.Constraints.Contains(c)) res.Constraints.Add(c);
        }
        foreach (var x in dims)
        {
            x.Enabled = false;
            geo.Restore();
            SolveStatus st;
            double keep = x.Value;   // выключенный размер не управляет — решение перемерило бы его значение
            try { st = sk.Solve(solver); } finally { x.Enabled = true; x.Value = keep; }
            if (FailureOf(st0, st, redundant) == SketchEditFailure.None && !res.Dimensions.Contains(x)) res.Dimensions.Add(x);
        }
        geo.Restore();
    }

    /// <summary>Состояние эскиза целиком для панели «Разрешить эскиз»: участники противоречия или избыточности, вырождение.</summary>
    public static SketchEditResult Inspect(Sketch sk, ISketchSolver solver)
    {
        var res = new SketchEditResult();
        var geo = Geometry.Take(sk);
        var st = sk.Solve(solver);
        res.Status = st;
        if (st == SolveStatus.Singular) { res.Failure = SketchEditFailure.Singular; res.Degenerate.AddRange(sk.DegenerateEntities); }
        else if (st is SolveStatus.Inconsistent or SolveStatus.DidNotConverge) res.Failure = st == SolveStatus.Inconsistent ? SketchEditFailure.Inconsistent : SketchEditFailure.DidNotConverge;
        else if (st == SolveStatus.Overconstrained) res.Failure = SketchEditFailure.Redundant;
        foreach (var c in sk.Constraints.Where(c => c.Conflict)) res.Constraints.Add(c);
        foreach (var x in sk.Dimensions.Where(x => x.Conflict)) res.Dimensions.Add(x);
        if (res.Failure is SketchEditFailure.Inconsistent or SketchEditFailure.DidNotConverge or SketchEditFailure.Redundant)
        {
            // без участника эскиз становится «хорошим» (недо- или полностью определён)
            var cons = sk.Constraints.Where(c => c.Enabled && !c.Conflict).ToList();
            var dims = sk.Dimensions.Where(x => x.IsDriving).ToList();
            bool Good(SolveStatus s) => s is SolveStatus.Ok or SolveStatus.Underconstrained;
            foreach (var c in cons)
            {
                c.Enabled = false; geo.Restore();
                SolveStatus s; try { s = sk.Solve(solver); } finally { c.Enabled = true; }
                if (Good(s) && !res.Constraints.Contains(c)) res.Constraints.Add(c);
            }
            foreach (var x in dims)
            {
                x.Enabled = false; geo.Restore();
                double keep = x.Value;
                SolveStatus s; try { s = sk.Solve(solver); } finally { x.Enabled = true; x.Value = keep; }
                if (Good(s) && !res.Dimensions.Contains(x)) res.Dimensions.Add(x);
            }
            geo.Restore();
            res.Status = sk.Solve(solver);
            if (res.Status is SolveStatus.Ok or SolveStatus.Underconstrained or SolveStatus.Overconstrained) { } else geo.Restore();
            // k106: «избыточные» по рангу — только те, что следуют из прочих (вырожденная запись касания и т. п. — нет)
            if (res.Failure == SketchEditFailure.Redundant && !RedundantCheckOff)
            {
                double scale = Scale(sk);
                res.Constraints.RemoveAll(c => !c.Conflict && !Implied(sk, solver, new[] { c }, scale));
                if (res.Constraints.Count == 0 && res.Dimensions.Count == 0) res.Failure = SketchEditFailure.None;
                geo.Restore();
            }
        }
        res.Ok = res.Failure == SketchEditFailure.None;
        return res;
    }

    // ------------------------------------------------------------------ удаление размеров

    /// <summary>
    /// п. 17: удалить размеры. Сильный становится слабым (если он нужен, чтобы эскиз оставался определённым, он остаётся
    /// слабым после пересборки слабых размеров, иначе исчезает); справочный — удаляется; слабый — остаётся, если нужен.
    /// Возврат: (удалено, стало слабыми).
    /// </summary>
    public static (int Removed, int Weakened) DeleteDimensions(Sketch sk, IEnumerable<SkDimension> dims, ISketchSolver solver)
    {
        var list = dims.Distinct().ToList();
        var wasWeak = list.Where(d => d.IsDefault && !d.Locked).ToList();
        var weakened = new List<SkDimension>();
        foreach (var d in list)
        {
            if (!sk.Dimensions.Contains(d)) continue;
            if (d.Reference || !Core.Settings.Current.ShowDefaultDimensions) { sk.Dimensions.Remove(d); continue; }
            d.Formula = null; d.UserLocked = false; d.Reference = false;
            d.IsDefault = true; d.Locked = false;
            weakened.Add(d);
        }
        // бывшие сильные — первыми в очереди пересборки (остаются слабыми, если нужны); выбранные слабые — последними
        // (если без них можно — их место займут другие)
        foreach (var d in weakened) { sk.Dimensions.Remove(d); sk.Dimensions.Insert(0, d); }
        sk.BumpStructure();
        sk.Solve(solver);
        DefaultDimensions.Refresh(sk, solver, demote: wasWeak);
        int stayed = list.Count(sk.Dimensions.Contains);
        return (list.Count - stayed, stayed);
    }

    /// <summary>Сделать размеры слабыми (противоположность «Сделать сильным»).</summary>
    public static void MakeWeak(Sketch sk, IEnumerable<SkDimension> dims, ISketchSolver solver)
    {
        foreach (var d in dims)
        {
            if (d.UserLocked) continue;
            d.IsDefault = true; d.Locked = false; d.Reference = false; d.Formula = null;
        }
        sk.BumpStructure();
        sk.Solve(solver);
        DefaultDimensions.Refresh(sk, solver);
    }
}
