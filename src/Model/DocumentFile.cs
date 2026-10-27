using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;

namespace Parametric3D.Model;

/// <summary>
/// СОХРАНЕНИЕ И ОТКРЫТИЕ ДЕТАЛИ. Прежде документ сохранить было нельзя вовсе: «сохранить»
/// выводило только сетку, а «открыть» было заглушкой — работу нельзя было продолжить назавтра.
///
/// Хранится ПОСТРОЕНИЕ, а не результат: переменные, эскизы и дерево операций. Тела получаются
/// перестроением при открытии — так файл остаётся маленьким и переживает изменения ядра.
/// Формат — читаемый текст (JSON) с указанием вида записи, чтобы старые файлы можно было
/// открывать после расширения формата.
/// </summary>
public static class DocumentFile
{
    // 2 — ключи операций и подписи граней «#ключ/роль» (K05); сборка вида 1 такие ссылки не поймёт.
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // ------------------------------------------------------------------ запись

    private static List<ParamDto> ParamsOut(ParameterTable? t) => t?.All.Select(p => new ParamDto
    {
        Name = p.Name,
        Kind = p.Kind.ToString(),
        Number = p.Number,
        Formula = string.IsNullOrEmpty(p.Formula) ? null : p.Formula,
        Unit = string.IsNullOrEmpty(p.Unit) ? null : p.Unit,
        Comment = string.IsNullOrEmpty(p.Comment) ? null : p.Comment,
    }).ToList() ?? new List<ParamDto>();

    private static void ParamsIn(List<ParamDto> src, ParameterTable table)
    {
        foreach (var p in src)
        {
            var kind = Enum.TryParse<ParamKind>(p.Kind, out var k) ? k : ParamKind.Real;
            var par = table.Add(p.Name, p.Number, kind, p.Unit ?? "");
            if (p.Formula != null) par.Formula = p.Formula;
            if (p.Comment != null) par.Comment = p.Comment;
        }
    }

    public static string Save(Part part, ParameterTable? parameters = null) => Save(part, parameters, withDisplay: false);

    /// <summary>
    /// k114 (замечание 12:12): ЗАПИСЬ С ПОВЕРХНОСТЯМИ. withDisplay — в файл идёт и посчитанная сетка показа детали (вершины,
    /// нормали, грани, рёбра, вершины тела, объём, габарит) с подписью описания модели: открытый файл сразу показывает
    /// деталь, перестроение идёт в фоне и заменяет её. Подпись не сошлась (файл правили вне программы) — сетка не берётся.
    /// Снимки отмены и копии деталей пишутся без неё. Мутация P3D_FILEDISPLAY=0.
    /// </summary>
    public static string Save(Part part, ParameterTable? parameters, bool withDisplay)
    {
        var doc = BuildDto(part, parameters);
        string def = JsonSerializer.Serialize(doc, Opts);
        if (!withDisplay || DisplayOff || part.IsDirty || part.ShowsInstant || part.ShowsCached || part.Mesh is not { } md || md.Vertices.Length == 0) return def;
        doc.Display = new DisplayDto { Signature = Signature(def), Volume = part.Volume, Data = Convert.ToBase64String(DisplayCache.Pack(md)) };
        return JsonSerializer.Serialize(doc, Opts);
    }

    public static readonly bool DisplayOff = Environment.GetEnvironmentVariable("P3D_FILEDISPLAY") == "0";
    static string Signature(string def) => SignatureRaw(NewlineSigOff ? def : def.Replace("\r\n", "\n"));
    static string SignatureRaw(string def) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(def)));
    /// <summary>Проверка k116: подпись текста как есть (так её считала k114/k115 — с переводами строк системы).</summary>
    public static string SignatureRawForTest(string s) => SignatureRaw(s);
    /// <summary>k116: мутация P3D_SKETCHVIS=0 — видимость эскизов в файл не пишется (после открытия скрытые показаны).</summary>
    static readonly bool SketchVisOff = Environment.GetEnvironmentVariable("P3D_SKETCHVIS") == "0";
    /// <summary>k116: мутация P3D_TREESTATE=0 — развёрнутые ветки дерева в файл не пишутся.</summary>
    static readonly bool TreeStateOff = Environment.GetEnvironmentVariable("P3D_TREESTATE") == "0";
    /// <summary>k116: мутация P3D_FILENEWLINE=0 — подпись по тексту как есть (переводы строк Windows/Linux различают её).</summary>
    static readonly bool NewlineSigOff = Environment.GetEnvironmentVariable("P3D_FILENEWLINE") == "0";

    private static DocDto BuildDto(Part part, ParameterTable? parameters)
    {
        var doc = new DocDto
        {
            Version = CurrentVersion,
            Name = part.Name,
            Units = part.Units.ToDictionary(),
            Parameters = ParamsOut(parameters),
            PartParameters = part.LocalParameters.Count > 0 ? ParamsOut(part.LocalParameters) : null,
            Configurations = part.Configurations.Select(c => new ConfigDto
            {
                Name = c.Name,
                Comment = c.Comment,
                Parameters = c.Parameters.ToDictionary(k => k.Key, v => v.Value),
                Suppressed = c.SuppressedFeatures.ToList(),
                Hidden = c.HiddenParts.ToList()
            }).ToList(),
            ActiveConfiguration = part.ActiveConfiguration,
            Stage = part.InInsertMode ? part.StageIndex : null,
            Datums = SaveDatums(part),
            FreeForm = part.FreeFormDoc, MergeFreeForm = part.MergeFreeForm,
            ConstructionPlane = part.ConstructionPlane.Origin == Vector3d.Zero && part.ConstructionPlane.W == Vector3d.UnitZ && part.ConstructionPlane.U == Vector3d.UnitX ? null
                : new[] { part.ConstructionPlane.Origin.X, part.ConstructionPlane.Origin.Y, part.ConstructionPlane.Origin.Z, part.ConstructionPlane.U.X, part.ConstructionPlane.U.Y, part.ConstructionPlane.U.Z, part.ConstructionPlane.V.X, part.ConstructionPlane.V.Y, part.ConstructionPlane.V.Z },

            Material = Save(part.Material),

            FaceSets = part.FaceSets.Select(fs => new FaceSetDto
            {
                Name = fs.Name,
                Purpose = fs.Purpose,
                Faces = fs.Faces.Select(f => new FaceRefDto
                {
                    Point = new[] { f.Point.X, f.Point.Y, f.Point.Z },
                    Normal = new[] { f.Normal.X, f.Normal.Y, f.Normal.Z },
                    Tag = f.Tag
                }).ToList()
            }).ToList(),

            Info = new InfoDto
            {
                Code = part.Info.Code,
                Title = part.Info.Title,
                Author = part.Info.Author,
                Checker = part.Info.Checker,
                Company = part.Info.Company,
                Created = part.Info.Created,
                Modified = DateTime.UtcNow,
                Revision = part.Info.Revision,
                Note = part.Info.Note,
                Uom = part.Info.Uom == "шт" ? null : part.Info.Uom,
                Units = part.Info.LengthUnits == "мм" ? null : part.Info.LengthUnits,
                Tolerances = part.Info.Tolerances == "ISO 2768-mK" ? null : part.Info.Tolerances
            },

            Sketches = part.Sketches.Select(s => { var d = SaveSketch(s); d.Slot = part.SketchSlot(s); return d; }).ToList(),
            // K10: операция, которую нельзя записать, — ошибка записи, а не молча выпавшая операция.
            Features = part.Features.Select(f =>
            {
                var d = SaveFeature(f) ?? throw new InvalidOperationException($"Feature '{f.Name}' ({f.GetType().Name}) cannot be saved.");
                d.Key = f.Key; d.Policy = f.Policy?.ToString();
                if (!string.IsNullOrEmpty(f.Group)) (d.Str ??= new())["group"] = f.Group;
                if (f.FreeForm != null) (d.Str ??= new())["ff"] = f.FreeForm;
                if (f.FFBody != null) (d.Str ??= new())["ffbody"] = f.FFBody;
                if (f.Frozen) (d.Flags ??= new()).Add("frozen");
                return d;
            }).ToList()
        };
        if (!TreeStateOff && part.TreeState.Count > 0) doc.Tree = new Dictionary<string, bool>(part.TreeState);   // k116: ветки дерева
        return doc;
    }

    private static SketchDto SaveSketch(Sketch s)
    {
        var dto = new SketchDto { Id = s.Id, Name = s.Name, Plane = new[]
        {
            s.Plane.Origin.X, s.Plane.Origin.Y, s.Plane.Origin.Z,
            s.Plane.U.X, s.Plane.U.Y, s.Plane.U.Z,
            s.Plane.V.X, s.Plane.V.Y, s.Plane.V.Z
        }, BaseFaceTag = s.BaseFaceTag,
            PlaneRef = s.PlaneDescription, BasePlaneId = s.BasePlaneId,
            BaseFrame = new[] { s.BaseFrame.Origin.X, s.BaseFrame.Origin.Y, s.BaseFrame.Origin.Z, s.BaseFrame.U.X, s.BaseFrame.U.Y, s.BaseFrame.U.Z, s.BaseFrame.V.X, s.BaseFrame.V.Y, s.BaseFrame.V.Z },
            Up = new[] { s.UpVector.X, s.UpVector.Y }, UpRef = s.UpDescription.Length > 0 ? s.UpDescription : null,
            OriginRef = s.OriginDescription, OriginBase = s.OriginBase is { } ob ? new[] { ob.X, ob.Y, ob.Z } : null,
            OriginOffset = s.OriginOffset == Vector2d.Zero ? null : new[] { s.OriginOffset.X, s.OriginOffset.Y } };
        // k116 (замечание 16:00, п. 8): видимость эскиза, заданная пользователем, — в файле
        if (!SketchVisOff && s.VisibleUserSet) dto.Shown = s.Visible;

        // ТОЧКИ, НА КОТОРЫЕ ССЫЛАЮТСЯ ЛИНИИ И ДУГИ, ЗАПИСЫВАЮТСЯ ВСЕГДА. Эскиз, собранный кодом
        // (TestGeometry, мастера), хранит концы отрезков только в самих отрезках; прежде такие
        // точки в файл не попадали, отрезки при чтении не находили концов и пропадали — деталь
        // из сохранённого файла выходила пустой («the sketch has no closed loop»).
        var written = new HashSet<uint>(s.Entities.OfType<SkPoint>().Select(p => p.Id));
        void PointOf(SkPoint p)
        {
            if (written.Add(p.Id)) dto.Entities.Add(new EntityDto { Id = p.Id, Kind = "point", X = p.X, Y = p.Y });
        }
        foreach (var e in s.Entities)
            switch (e)
            {
                case SkLine l: PointOf(l.A); PointOf(l.B); break;
                case SkCircle c: PointOf(c.Center); break;
                case SkArc a: PointOf(a.Center); PointOf(a.Start); PointOf(a.End); break;
                case SkEllipse el when !SplineSaveOff: PointOf(el.Center); break;
                case SkSpline sp when !SplineSaveOff: foreach (var q in sp.Ctrl) PointOf(q); break;
            }

        foreach (var e in s.Entities)
            switch (e)
            {
                case SkPoint p:
                    dto.Entities.Add(new EntityDto { Id = p.Id, Kind = "point", X = p.X, Y = p.Y });
                    break;
                case SkLine l:
                    dto.Entities.Add(new EntityDto { Id = l.Id, Kind = "line", A = l.A.Id, B = l.B.Id });
                    break;
                case SkCircle c:
                    dto.Entities.Add(new EntityDto { Id = c.Id, Kind = "circle", A = c.Center.Id, R = c.Radius });
                    break;
                case SkArc a:
                    dto.Entities.Add(new EntityDto { Id = a.Id, Kind = "arc", A = a.Center.Id, B = a.Start.Id, C = a.End.Id, CCW = a.CCW });
                    break;
                // KRN-07 (07.4): СПЛАЙН И ЭЛЛИПС ЭСКИЗА — в файл (опорные точки — ссылками, эллипс — полуоси, поворот, дуга).
                // Прежде не записывались вовсе: направляющие и сечения-сплайны пропадали при сохранении, лофт из файла
                // строился без них (объём 1960 вместо 3064). Мутация P3D_SKSPLINESAVE=0.
                case SkSpline sp when !SplineSaveOff:
                    dto.Entities.Add(new EntityDto { Id = sp.Id, Kind = "spline", Pts = sp.Ctrl.Select(q => q.Id).ToList() });
                    break;
                case SkEllipse el when !SplineSaveOff:
                    dto.Entities.Add(new EntityDto { Id = el.Id, Kind = "ellipse", A = el.Center.Id, R = el.RU, R2 = el.RV, Rot = el.Rotation, A0 = el.A0, A1 = el.A1 });
                    break;
            }

        // k96: РОЛЬ ЭЛЕМЕНТА (вспомогательная, осевая, ссылка), закрепление и источник ссылки — в файл. Прежде не
        // записывались: после сохранения вспомогательные и осевые линии и проекции рёбер становились основной геометрией
        // (входили в контур тела). Мутация P3D_SKROLESAVE=0.
        if (!RoleSaveOff)
        {
            var byId = new Dictionary<uint, SkEntity>();
            foreach (var e in s.Entities) { byId[e.Id] = e; foreach (var h in e.Handles) byId.TryAdd(h.Id, h); }
            foreach (var ed in dto.Entities)
                if (byId.TryGetValue(ed.Id, out var e))
                {
                    if (e.Role != SkRole.Object) ed.Role = (int)e.Role;
                    if (e.Fixed) ed.Fixed = true;
                    if (e.ReferenceSource.Length > 0) ed.Src = e.ReferenceSource;
                }
        }

        foreach (var c in s.Constraints)
            dto.Constraints.Add(new ConstraintDto
            {
                Type = c.Type.ToString(),
                Items = c.Items.Select(s.SaveIdOf).ToList(),
                Enabled = c.Enabled
            });

        foreach (var d in s.Dimensions)
            dto.Dimensions.Add(new DimensionDto
            {
                Type = d.Type.ToString(),
                Items = d.Items.Select(s.SaveIdOf).ToList(),
                Value = d.Value,
                Locked = d.Locked,
                Reference = d.Reference,
                // u12: вид размера сохраняется (место подписи, слабый/сильный/замок, стрелки, формула)
                Formula = d.Formula, IsDefault = d.IsDefault ? true : null, UserPlaced = d.UserPlaced ? true : null, Ix = d.Index >= 0 ? d.Index : null,
                Note = string.IsNullOrEmpty(d.Note) ? null : d.Note,
                Offset = d.UserPlaced ? new[] { d.TextOffset.X, d.TextOffset.Y } : null,
                Arrows = d.Arrows == ArrowPlacement.Auto ? null : d.Arrows.ToString(),
                UserLocked = d.UserLocked ? true : null, SingleArrow = d.SingleArrow ? true : null, AsLinear = d.DiameterAsLinear ? true : null
            });

        return dto;
    }

    static List<double> V(params Vector3d[] vs) => vs.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToList();
    static Vector3d At(List<double> p, int i) => new(p[3 * i], p[3 * i + 1], p[3 * i + 2]);

    private static List<DatumDto>? SaveDatums(Part part)
    {
        var res = new List<DatumDto>();
        foreach (var c in part.Children)
        {
            DatumDto? d = c switch
            {
                DatumPlane dp when !ReferenceEquals(dp, part.PlaneXY) && !ReferenceEquals(dp, part.PlaneXZ) && !ReferenceEquals(dp, part.PlaneYZ)
                    => new DatumDto { Kind = "plane", P = V(dp.Frame.Origin, dp.Frame.U, dp.Frame.V), Mode = dp.Mode.ToString(), Offset = dp.Offset,
                                      Angle = dp.Angle != 0 ? dp.Angle : null, Flip = dp.Flip ? true : null, Method = dp.Method >= 0 ? dp.Method : null, Base = dp.BaseName },
                DatumAxis ax => new DatumDto { Kind = "axis", P = V(ax.Origin, ax.Direction) },
                DatumPoint pt when !ReferenceEquals(pt, part.Origin) => new DatumDto { Kind = "point", P = V(pt.P) },
                Line3DItem ln => new DatumDto { Kind = "line", P = V(ln.A, ln.B) },
                DatumCsys cs => new DatumDto { Kind = "csys", P = V(cs.Frame.Origin, cs.Frame.U, cs.Frame.V) },
                DatumCurve dc => new DatumDto { Kind = "curve", P = V(dc.Points.ToArray()), Source = dc.Source,
                    Cv = dc.IsNurbs ? V(dc.Control!.ToArray()) : null, Wt = dc.IsNurbs ? dc.Weights : null, Kn = dc.IsNurbs ? dc.Knots : null, Deg = dc.IsNurbs ? dc.Degree : null },
                _ => null,
            };
            if (d == null) continue;
            d.Name = c.Name; d.Hidden = !c.Visible;
            if (!DatumSlotOff) d.Slot = part.ItemSlot(c);
            res.Add(d);
        }
        return res.Count > 0 ? res : null;
    }

    /// <summary>
    /// k118 (замечание 08:54, п. 6): ОПОРНЫЕ ЭЛЕМЕНТЫ НА СВОИХ МЕСТАХ В ДЕРЕВЕ. Плоскости, оси, точки писались отдельным списком и
    /// после открытия вставали над всеми операциями, хотя создавались между ними. Место — число операций перед элементом;
    /// мутация P3D_DATUMSLOT=0.
    /// </summary>
    static readonly bool DatumSlotOff = Environment.GetEnvironmentVariable("P3D_DATUMSLOT") == "0";
    [ThreadStatic] static List<(ModelItem Item, int Slot)>? _datumSlots;

    private static void LoadDatums(Part part, List<DatumDto>? list)
    {
        _datumSlots = new();
        if (list == null) return;
        foreach (var d in list)
        {
            ModelItem? it = d.Kind switch
            {
                "plane" when d.P.Count >= 9 => new DatumPlane(d.Name, new Frame3(At(d.P, 0), At(d.P, 1), At(d.P, 2)))
                    { Mode = Enum.TryParse<DatumPlaneMode>(d.Mode, out var m) ? m : DatumPlaneMode.Standard, Offset = d.Offset,
                      Angle = d.Angle ?? 0, Flip = d.Flip ?? false, Method = d.Method ?? -1, BaseName = d.Base },
                "axis" when d.P.Count >= 6 => new DatumAxis(d.Name, At(d.P, 0), At(d.P, 1)),
                "point" when d.P.Count >= 3 => new DatumPoint(d.Name, At(d.P, 0)),
                "line" when d.P.Count >= 6 => new Line3DItem(d.Name, At(d.P, 0), At(d.P, 1)),
                "csys" when d.P.Count >= 9 => new DatumCsys(d.Name, new Frame3(At(d.P, 0), At(d.P, 1), At(d.P, 2))),
                "curve" when d.P.Count >= 6 => new DatumCurve(d.Name, Enumerable.Range(0, d.P.Count / 3).Select(i => At(d.P, i)), d.Source ?? ""),
                _ => null,
            };
            if (it == null) continue;
            if (it is DatumCurve ndc && d.Cv is { Count: >= 6 } cv)
            {
                ndc.Control = Enumerable.Range(0, cv.Count / 3).Select(i => At(cv, i)).ToList();
                ndc.Weights = d.Wt; ndc.Knots = d.Kn; ndc.Degree = d.Deg ?? 3;
            }
            it.Visible = !d.Hidden;
            part.Add(it);
            if (d.Slot is int sl && sl > 0) _datumSlots!.Add((it, sl));
        }
    }

    // u13: общие поля новых операций (числа, формулы, флаги по имени)
    private static FeatureDto Dto(string kind, Feature f, Action<FeatureDto> fill)
    {
        var d = new FeatureDto { Kind = kind, Name = f.Name, Suppressed = f.Suppressed, Cut = f.Cut };
        fill(d); return d;
    }
    private static void PutV(FeatureDto d, string key, ValueRef v)
    {
        (d.Num ??= new())[key] = v.Value;
        if (v.Formula != null) (d.Str ??= new())[key] = v.Formula;
    }
    private static double[] V3(Vector3d v) => new[] { v.X, v.Y, v.Z };
    private static void PutS(FeatureDto d, string key, string? v) { if (v != null) (d.Str ??= new())[key] = v; }
    private static void Flag(FeatureDto d, string key) => (d.Flags ??= new()).Add(key);
    private static ValueRef GetV(FeatureDto d, string key, double dflt)
        => new(d.Num != null && d.Num.TryGetValue(key, out var v) ? v : dflt) { Formula = d.Str != null && d.Str.TryGetValue(key, out var f) ? f : null };
    private static double GetD(FeatureDto d, string key, double dflt) => d.Num != null && d.Num.TryGetValue(key, out var v) ? v : dflt;
    private static string? GetS(FeatureDto d, string key) => d.Str != null && d.Str.TryGetValue(key, out var v) ? v : null;
    private static bool HasFlag(FeatureDto d, string key) => d.Flags?.Contains(key) == true;

    private static FeatureDto? SaveFeature(Feature f) => f switch
    {
        ExtrudeFeature e => new FeatureDto
        {
            Kind = "extrude",
            Name = e.Name,
            Suppressed = e.Suppressed,
            SketchId = e.SketchId,
            Distance = e.Side1.Distance.Value,
            Formula = e.Side1.Distance.Formula,
            Flip = e.Side1.Flip,
            TwoSided = e.TwoSided,
            Symmetric = e.Symmetric,
            Cut = e.Cut,
            Num = e.Thin is { } th ? new() { ["thin"] = th.Value } : null,
            Str = ExtrudeStr(e),
            Flags = ExtrudeFlags(e),
            // k115 (замечание 12:39): вторая сторона и «глубина до» — прежде не записывались (открытый файл — «на расстояние»)
            Distance2 = e.Side2.Distance.Value, Formula2 = e.Side2.Distance.Formula,
        },
        RevolveFeature r => new FeatureDto
        {
            Kind = "revolve",
            Name = r.Name,
            Suppressed = r.Suppressed,
            SketchId = r.SketchId,
            AxisId = r.AxisId,
            AxisEntityId = r.AxisEntityId,
            StartAngle = r.StartAngle.Value,
            StartFormula = r.StartAngle.Formula,
            EndAngle = r.EndAngle.Value,
            EndFormula = r.EndAngle.Formula,
            Cut = r.Cut
        },

        SweepFeature w => new FeatureDto
        {
            Kind = "sweep",
            Name = w.Name,
            Suppressed = w.Suppressed,
            SketchId = w.SectionSketchId,
            PathSketchId = w.PathSketchId,
            PathLineId = w.PathLineId,
            Distance = w.LengthFraction.Value,
            Formula = w.LengthFraction.Formula,
            Twist = Math.Abs(w.Twist.Value) > 0 || w.Twist.Formula != null ? w.Twist.Value : null,
            TwistFormula = w.Twist.Formula,
            KeepOrientation = w.KeepOrientation ? true : null,
            Flags = w.Reverse ? new List<string> { "reverse" } : null,   // k107: направление протяжки
            Cut = w.Cut
        },

        // У СКРУГЛЕНИЯ И ФАСКИ СОХРАНЯЮТСЯ ССЫЛКИ НА РЁБРА — те самые устойчивые имена: пара
        // подписей соседних граней, наборы граней в концах и номер куска. Координаты пишутся
        // тоже, как запасной путь для старых ссылок.
        RoundFeature rf => new FeatureDto
        {
            Kind = "round",
            Name = rf.Name,
            Suppressed = rf.Suppressed,
            Distance = rf.Radius.Value,
            Formula = rf.Radius.Formula,
            Edges = rf.Edges.Select(SaveEdge).ToList(),
            Selectors = rf.Selectors.Count == 0 ? null : rf.Selectors.Select(x => new SelectorDto { Kind = x.Kind.ToString(), Feature = x.FeatureKey, Other = x.OtherKey }).ToList(),
            RadiusEnd = rf.RadiusEnd?.Value, RadiusEndFormula = rf.RadiusEnd?.Formula,
            LawF = rf.LawPoints.Count == 0 ? null : rf.LawPoints.Select(p => p.F).ToList(),
            LawR = rf.LawPoints.Count == 0 ? null : rf.LawPoints.Select(p => p.R.Value).ToList(),
            LawRFormulas = rf.LawPoints.Count == 0 || rf.LawPoints.All(p => p.R.Formula == null) ? null : rf.LawPoints.Select(p => p.R.Formula).ToList()
        },

        ChamferFeature cf => new FeatureDto
        {
            Kind = "chamfer",
            Name = cf.Name,
            Suppressed = cf.Suppressed,
            Distance = cf.D1.Value,
            Formula = cf.D1.Formula,
            Distance2 = cf.D2.Value,
            Formula2 = cf.D2.Formula,
            ChamferMode = cf.Mode.ToString(),
            RadiusEnd = cf.SizeEnd?.Value, RadiusEndFormula = cf.SizeEnd?.Formula,
            Edges = cf.Edges.Select(SaveEdge).ToList(),
            Selectors = cf.Selectors.Count == 0 ? null : cf.Selectors.Select(x => new SelectorDto { Kind = x.Kind.ToString(), Feature = x.FeatureKey, Other = x.OtherKey }).ToList()
        },

        VertexChamferFeature vc => new FeatureDto
        {
            Kind = "vertexChamfer", Name = vc.Name, Suppressed = vc.Suppressed,
            Distance = vc.Distance.Value, Formula = vc.Distance.Formula,
            Point = new[] { vc.Vertex.X, vc.Vertex.Y, vc.Vertex.Z },
            OpenFaces = vc.PerEdge.SelectMany(x => new[] { x.OnEdge.X, x.OnEdge.Y, x.OnEdge.Z }).ToList(),
            Values = vc.PerEdge.Count > 0 ? vc.PerEdge.Select(x => x.D.Value).ToList() : null,
            Formulas = vc.PerEdge.Count > 0 ? vc.PerEdge.Select(x => x.D.Formula).ToList() : null,
            VertexKey = vc.VertexKey
        },

        VertexRoundFeature vr => new FeatureDto
        {
            Kind = "vertexRound", Name = vr.Name, Suppressed = vr.Suppressed,
            Distance = vr.Radius.Value, Formula = vr.Radius.Formula,
            Point = new[] { vr.Vertex.X, vr.Vertex.Y, vr.Vertex.Z },
            VertexKey = vr.VertexKey
        },

        ShellFeature sh => new FeatureDto
        {
            Kind = "shell",
            Name = sh.Name,
            Suppressed = sh.Suppressed,
            Distance = sh.Thickness.Value,
            Formula = sh.Thickness.Formula,
            OpenFaces = sh.OpenFaces.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToList(),
            FaceNames = sh.OpenFaceNames.Count > 0 ? sh.OpenFaceNames.ToList() : null,
            FaceSelectors = FaceSel(sh.OpenSelectors),
            ThickFaces = sh.FaceThickness.Count > 0 ? sh.FaceThickness.SelectMany(x => new[] { x.At.X, x.At.Y, x.At.Z }).ToList() : null,
            Values = sh.FaceThickness.Count > 0 ? sh.FaceThickness.Select(x => x.T.Value).ToList() : null,
            Formulas = sh.FaceThickness.Count > 0 ? sh.FaceThickness.Select(x => x.T.Formula).ToList() : null
        },

        MirrorFeature mf => new FeatureDto
        {
            Kind = "mirror", Name = mf.Name, Suppressed = mf.Suppressed,
            Point = new[] { mf.At.X, mf.At.Y, mf.At.Z },
            Dir = new[] { mf.Normal.X, mf.Normal.Y, mf.Normal.Z },
            Sources = mf.Sources.Count == 0 ? null : mf.Sources.ToList(),
            Flags = mf.ReplaceBody ? new() { "replace" } : null
        },

        LinearPatternFeature lp => new FeatureDto
        {
            Kind = "linear", Name = lp.Name, Suppressed = lp.Suppressed,
            Dir = new[] { lp.Step.X, lp.Step.Y, lp.Step.Z },
            Distance = lp.Count.Value, Formula = lp.Count.Formula,
            Sources = lp.Sources.Count == 0 ? null : lp.Sources.ToList()
        },

        CircularPatternFeature cp => new FeatureDto
        {
            Kind = "circular", Name = cp.Name, Suppressed = cp.Suppressed,
            Point = new[] { cp.At.X, cp.At.Y, cp.At.Z },
            Dir = new[] { cp.Axis.X, cp.Axis.Y, cp.Axis.Z },
            Distance = cp.Count.Value, Formula = cp.Count.Formula,
            Sources = cp.Sources.Count == 0 ? null : cp.Sources.ToList()
        },

        DraftFeature df => new FeatureDto
        {
            Kind = "draft", Name = df.Name, Suppressed = df.Suppressed,
            Distance = df.Angle.Value, Formula = df.Angle.Formula,
            Point = new[] { df.NeutralPoint.X, df.NeutralPoint.Y, df.NeutralPoint.Z },
            Dir = new[] { df.NeutralNormal.X, df.NeutralNormal.Y, df.NeutralNormal.Z },
            OpenFaces = df.OnFaces.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToList(),
            FaceNames = df.OnFaceNames.Count > 0 ? df.OnFaceNames.ToList() : null,
            FaceSelectors = FaceSel(df.OnSelectors),
            // k112: шарнир, смещаемое ребро, итоговый угол, сторона, опорная плоскость
            Edges = df.Hinge is { } dh ? new[] { dh }.Concat(df.Moving is { } dm ? new[] { dm } : Array.Empty<EdgeRef>()).Select(SaveEdge).ToList() : new(),
            Flags = DraftFlags(df),
            Num = df.RefNormal is { } drn ? new Dictionary<string, double> { ["refNx"] = drn.X, ["refNy"] = drn.Y, ["refNz"] = drn.Z } : null
        },

        RibFeature rb => new FeatureDto
        {
            Kind = "rib", Name = rb.Name, Suppressed = rb.Suppressed,
            SketchId = rb.SketchId,
            Dir = new[] { rb.Grow.X, rb.Grow.Y, rb.Grow.Z },
            Distance = rb.Thickness.Value, Formula = rb.Thickness.Formula,
            Distance2 = rb.MaxLength.Value, Formula2 = rb.MaxLength.Formula
        },

        MoveCopyFeature mc => Dto("move_copy", mc, d =>
        {
            PutV(d, "dx", mc.Dx); PutV(d, "dy", mc.Dy); PutV(d, "dz", mc.Dz); PutV(d, "angle", mc.Angle);
            d.Point = new[] { mc.AxisAt.X, mc.AxisAt.Y, mc.AxisAt.Z }; d.Dir = new[] { mc.AxisDir.X, mc.AxisDir.Y, mc.AxisDir.Z };
            if (mc.Copy) Flag(d, "copy");
        }),
        FaceEditFeature fe => Dto("face_edit", fe, d =>
        {
            PutS(d, "mode", fe.Mode.ToString()); PutV(d, "distance", fe.Distance); PutV(d, "radius", fe.Radius);
            d.OpenFaces = fe.OnFaces.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToList();
            d.FaceNames = fe.OnFaceNames.Count > 0 ? fe.OnFaceNames.ToList() : null;
        }),
        PointsPatternFeature pp => Dto("points_pattern", pp, d =>
        {
            PutS(d, "mode", pp.Mode.ToString()); d.SketchId = pp.SketchId;
            PutV(d, "count", pp.Count); PutV(d, "spacing", pp.Spacing); PutV(d, "margin", pp.Margin);
            d.Sources = pp.Sources.Count == 0 ? null : pp.Sources.ToList();
        }),
        FullRoundFeature fr => Dto("full_round", fr, d =>
        {
            d.OpenFaces = fr.OnFaces.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToList();
            d.FaceNames = fr.OnFaceNames.Count > 0 ? fr.OnFaceNames.ToList() : null;
        }),
        CosmeticThreadFeature ct => Dto("thread", ct, d =>
        {
            PutV(d, "pitch", ct.Pitch); PutV(d, "length", ct.Length);
            d.OpenFaces = ct.OnFaces.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToList();
            d.FaceNames = ct.OnFaceNames.Count > 0 ? ct.OnFaceNames.ToList() : null;
        }),

        SurfaceFeature sf => Dto("surface", sf, d =>
        {
            PutS(d, "mode", sf.Mode.ToString()); d.SketchId = sf.SketchId; d.AxisEntityId = sf.AxisEntityId; PutV(d, "depth", sf.Depth); PutV(d, "angle", sf.Angle);
            if (sf.Flip) Flag(d, "flip");
            d.OpenFaces = sf.OnFaces.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToList();
            d.FaceNames = sf.OnFaceNames.Count > 0 ? sf.OnFaceNames.ToList() : null;
        }),
        QuiltEditFeature qe => Dto("quilt_edit", qe, d =>
        {
            PutS(d, "mode", qe.Mode.ToString()); d.Sources = qe.Sources.ToList(); PutV(d, "distance", qe.Distance);
            d.Point = new[] { qe.PlaneAt.X, qe.PlaneAt.Y, qe.PlaneAt.Z }; d.Dir = new[] { qe.PlaneNormal.X, qe.PlaneNormal.Y, qe.PlaneNormal.Z };
            if (qe.Flip) Flag(d, "flip");
        }),
        QuiltSolidFeature qs => Dto("quilt_solid", qs, d =>
        {
            PutS(d, "mode", qs.Mode.ToString()); PutS(d, "source", qs.Source); PutV(d, "thickness", qs.Thickness);
            if (qs.Flip) Flag(d, "flip");
            d.OpenFaces = qs.OnFaces.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToList();
            d.FaceNames = qs.OnFaceNames.Count > 0 ? qs.OnFaceNames.ToList() : null;
        }),
        BlendFeature bf => Dto("blend", bf, d =>
        {
            PutS(d, "mode", bf.Mode.ToString()); d.Sections = bf.SectionSketchIds.ToList(); d.PathSketchId = bf.PathSketchId; d.AxisEntityId = bf.AxisEntityId;
            if (bf.AsSurface) Flag(d, "surface");
            (d.Num ??= new())["steps"] = bf.Steps; d.Num["points"] = bf.Points;
        }),
        FreeFormFeature ffn => Dto("freeform", ffn, d => { PutS(d, "input", ffn.InputStep.Length > 0 ? ffn.InputStep : null); PutS(d, "sig", ffn.InputSig.Length > 0 ? ffn.InputSig : null); }),
        FFPrimitiveFeature pr => Dto("ff_primitive", pr, d =>
        {
            PutS(d, "shape", pr.Shape.ToString()); PutV(d, "a", pr.A); PutV(d, "b", pr.B); PutV(d, "c", pr.C);
            d.Point = V3(pr.Origin); d.Dir = new[] { pr.U.X, pr.U.Y, pr.U.Z, pr.V.X, pr.V.Y, pr.V.Z };
        }),
        FFBooleanFeature fb => Dto("ff_boolean", fb, d => { PutS(d, "op", fb.Op.ToString()); PutS(d, "target", fb.Target); d.Sources = fb.Tools.ToList(); if (fb.KeepTools) Flag(d, "keep"); }),
        FFTransformFeature ft => Dto("ff_transform", ft, d =>
        {
            PutS(d, "mode", ft.Mode.ToString()); d.Sources = ft.BodyNames.ToList();
            PutV(d, "dx", ft.Dx); PutV(d, "dy", ft.Dy); PutV(d, "dz", ft.Dz); PutV(d, "angle", ft.Angle); PutV(d, "factor", ft.Factor);
            d.Point = V3(ft.AxisAt); d.Dir = V3(ft.AxisDir); if (ft.Copy) Flag(d, "copy");
            // 13:09: масштаб по осям и рамка операции (глобальная — не пишется)
            if (ft.Sx.Value != 1 || ft.Sx.Formula != null) PutV(d, "sx", ft.Sx);
            if (ft.Sy.Value != 1 || ft.Sy.Formula != null) PutV(d, "sy", ft.Sy);
            if (ft.Sz.Value != 1 || ft.Sz.Formula != null) PutV(d, "sz", ft.Sz);
            if ((ft.FrameX - Vector3d.UnitX).Length > 1e-15 || (ft.FrameY - Vector3d.UnitY).Length > 1e-15)
                d.ThickFaces = new List<double> { ft.FrameX.X, ft.FrameX.Y, ft.FrameX.Z, ft.FrameY.X, ft.FrameY.Y, ft.FrameY.Z };
        }),
        FFSplitFeature fs => Dto("ff_split", fs, d => { PutS(d, "body", fs.Body); d.Point = V3(fs.PlaneAt); d.Dir = V3(fs.Normal); }),
        FFMirrorFeature fm => Dto("ff_mirror", fm, d => { PutS(d, "mode", fm.Mode.ToString()); d.Sources = fm.BodyNames.ToList(); d.Point = V3(fm.PlaneAt); d.Dir = V3(fm.Normal); }),
        FFArrayFeature fa => Dto("ff_array", fa, d =>
        {
            PutS(d, "kind", fa.Kind.ToString()); d.Sources = fa.BodyNames.ToList(); PutV(d, "count", fa.Count); PutV(d, "count2", fa.Count2); PutV(d, "angle", fa.Angle);
            PutS(d, "curve", fa.CurveName.Length > 0 ? fa.CurveName : null); if (fa.Align) Flag(d, "align");
            d.Point = V3(fa.AxisAt); d.Dir = V3(fa.AxisDir); d.ThickFaces = new List<double> { fa.Step.X, fa.Step.Y, fa.Step.Z, fa.Step2.X, fa.Step2.Y, fa.Step2.Z };
        }),
        FFPipeFeature fp => Dto("ff_pipe", fp, d => { PutS(d, "curve", fp.CurveName.Length > 0 ? fp.CurveName : null); d.SketchId = fp.SketchId; PutV(d, "radius", fp.Radius); PutV(d, "inner", fp.Inner); }),
        FFImprintFeature fi => Dto("ff_imprint", fi, d =>
        {
            PutS(d, "curve", fi.CurveName.Length > 0 ? fi.CurveName : null); d.SketchId = fi.SketchId;
            d.OpenFaces = fi.OnFaces.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToList();
            d.FaceNames = fi.OnFaceNames.Count > 0 ? fi.OnFaceNames.ToList() : null;
        }),
        FFNurbsQuiltFeature nq => Dto("ff_nurbs", nq, d => { d.Nurbs = nq.Def.Pack(); PutS(d, "replaces", nq.ReplacesKey); PutS(d, "quilt", nq.QuiltName.Length > 0 ? nq.QuiltName : null); }),
        FFPlanarQuiltFeature pq => Dto("ff_planar", pq, d => d.OpenFaces = pq.Boundary.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToList()),
        FFStitchFeature st => Dto("ff_stitch", st, d => d.Sources = st.Sources.ToList()),
        FFSubDFeature sd => Dto("ff_subd", sd, d => { d.Cage = sd.Cage.Pack(); PutS(d, "body", sd.Body); if (sd.ToNurbs) Flag(d, "nurbs"); }),
        FFAlignFaceFeature fl => Dto("ff_align_face", fl, d =>
        {
            PutV(d, "offset", fl.Offset); d.Point = V3(fl.PlaneAt); d.Dir = V3(fl.Normal);
            d.OpenFaces = fl.OnFaces.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToList();
            d.FaceNames = fl.OnFaceNames.Count > 0 ? fl.OnFaceNames.ToList() : null;
        }),
        MeshImportFeature mi => Dto("mesh_import", mi, d =>
        {
            PutS(d, "mesh", mi.MeshData); PutS(d, "source", mi.SourceName);
            if (mi.Step.Length > 0 && mi.StepKey == mi.Key) { PutS(d, "step", mi.Step); PutS(d, "report", mi.Report); }
            if (mi.Exact) Flag(d, "exact"); if (mi.Snap) Flag(d, "snap");
            (d.Num ??= new())["tol"] = mi.TolScale; d.Num["scale"] = mi.Scale;
        }),
        InsertBodyFeature ib => Dto("insert_body", ib, d =>
        {
            PutS(d, "step", ib.Step); PutS(d, "source", ib.SourceName); d.Point = new[] { ib.Offset.X, ib.Offset.Y, ib.Offset.Z };
        }),
        HelicalSweepFeature hs => Dto("helical_sweep", hs, d =>
        {
            d.SketchId = hs.SketchId; d.AxisId = hs.AxisId; d.AxisEntityId = hs.AxisEntityId;
            PutV(d, "pitch", hs.Pitch); PutV(d, "turns", hs.Turns); if (hs.LeftHand) Flag(d, "left");
        }),

        LoftFeature lf => new FeatureDto
        {
            Kind = "loft", Name = lf.Name, Suppressed = lf.Suppressed, Cut = lf.Cut,
            Sections = lf.SectionSketchIds.ToList(),
            Samples = lf.Samples,
            Smooth = lf.Smooth,
            Guides = lf.GuideSketchIds.Count > 0 ? lf.GuideSketchIds.ToList() : null,
            StartTangent = lf.StartTangent, EndTangent = lf.EndTangent
        },

        HoleFeature hf => new FeatureDto
        {
            Kind = "hole", Name = hf.Name, Suppressed = hf.Suppressed,
            SketchId = hf.SketchId,
            Dir = new[] { hf.Direction.X, hf.Direction.Y, hf.Direction.Z },
            OpenFaces = hf.Points.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToList(),
            FaceNames = hf.PointFaces.Any(x => x != null) ? hf.PointFaces.ToList() : null,
            HoleKindName = hf.Kind.ToString(),
            Distance = hf.Diameter.Value, Formula = hf.Diameter.Formula,
            Distance2 = hf.Depth.Value, Formula2 = hf.Depth.Formula,
            ThroughAll = hf.ThroughAll,
            HeadDiameter = hf.HeadDiameter.Value,
            HeadDepth = hf.HeadDepth.Value,
            HeadAngle = hf.HeadAngle.Value
        },

        DatumPlaneFeature dp => new FeatureDto
        {
            Kind = "datumPlane", Name = dp.Name, Suppressed = dp.Suppressed,
            DatumBy = dp.By.ToString(),
            BasePlane = dp.BasePlane, BaseAxis = dp.BaseAxis,
            Distance = dp.Offset.Value, Formula = dp.Offset.Formula,
            Distance2 = dp.Angle.Value, Formula2 = dp.Angle.Formula,
            OpenFaces = dp.Points.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToList()
        },

        DatumAxisFeature da => new FeatureDto
        {
            Kind = "datumAxis", Name = da.Name, Suppressed = da.Suppressed,
            DatumBy = da.By.ToString(),
            BasePlane = da.BasePlane, SecondPlane = da.SecondPlane, FaceTag = da.FaceTag,
            OpenFaces = da.Points.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToList()
        },

        DatumPointFeature dpt => new FeatureDto
        {
            Kind = "datumPoint", Name = dpt.Name, Suppressed = dpt.Suppressed,
            BaseAxis = dpt.BaseAxis, BasePlane = dpt.BasePlane,
            Point = new[] { dpt.At.X, dpt.At.Y, dpt.At.Z }
        },

        _ => null
    };

    private static Vector3d Vec(double[]? a) =>
        a is { Length: >= 3 } ? new Vector3d(a[0], a[1], a[2]) : Vector3d.Zero;

    private static Dictionary<string, string?>? ExtrudeStr(ExtrudeFeature e)
    {
        var d = new Dictionary<string, string?>();
        if (e.Thin is { Formula: { } tf }) d["thin"] = tf;
        if (e.Side1.End != EndType.Distance) d["end1"] = e.Side1.End.ToString();
        if (e.Side2.End != EndType.Distance) d["end2"] = e.Side2.End.ToString();
        if (e.Side1.TargetId != 0) d["target1"] = e.Side1.TargetId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (e.Side2.TargetId != 0) d["target2"] = e.Side2.TargetId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return d.Count > 0 ? d : null;
    }
    private static List<string>? ExtrudeFlags(ExtrudeFeature e)
    {
        var l = new List<string>();
        if (e.ThinOutside) l.Add("thinOutside");
        if (e.Side2.Flip) l.Add("flip2");
        return l.Count > 0 ? l : null;
    }

    private static List<string>? DraftFlags(DraftFeature df)
    {
        var l = new List<string>();
        if (df.Total) l.Add("total");
        if (df.Reverse) l.Add("reverse");
        return l.Count > 0 ? l : null;
    }

    private static EdgeDto SaveEdge(EdgeRef e) => new()
    {
        Sx = e.Start.X, Sy = e.Start.Y, Sz = e.Start.Z,
        Ex = e.End.X, Ey = e.End.Y, Ez = e.End.Z,
        Mx = e.Mid?.X, My = e.Mid?.Y, Mz = e.Mid?.Z,
        FaceA = e.FaceA, FaceB = e.FaceB,
        End0 = e.End0, End1 = e.End1,
        Piece = e.Piece
    };

    private static EdgeRef LoadEdge(EdgeDto d) => new(
        new Vector3d(d.Sx, d.Sy, d.Sz),
        new Vector3d(d.Ex, d.Ey, d.Ez),
        d.Mx is { } mx && d.My is { } my && d.Mz is { } mz ? new Vector3d(mx, my, mz) : null,
        d.FaceA, d.FaceB, d.End0, d.End1, d.Piece);

    // ------------------------------------------------------------------ чтение

    /// <summary>
    /// K10. ЧТЕНИЕ СТРОГОЕ: всё, что в файле есть, но не восстановлено (неизвестная операция,
    /// отрезок без концов, размер с потерянным участником), — отказ, а не частично принятая
    /// модель. Для восстановления повреждённого файла — LoadLenient с перечнем потерь.
    /// </summary>
    public static (Part Part, ParameterTable Parameters) Load(string json)
    {
        var r = LoadLenient(json, out var problems);
        if (problems.Count > 0)
            throw new InvalidDataException($"document is incomplete: {problems.Count} element(s) could not be restored — " +
                                           string.Join("; ", problems.Take(5)) + (problems.Count > 5 ? "; …" : ""));
        return r;
    }

    /// <summary>
    /// KRN-09 (09.6): НЕДОПУСТИМОЕ ЗНАЧЕНИЕ — ОШИБКА ЧТЕНИЯ, А НЕ ПАДЕНИЕ. Плоскость эскиза из трёх чисел давала
    /// IndexOutOfRangeException, вырожденные оси плоскости — P3DException: вызывающий код (окно приложения) ждёт
    /// InvalidDataException и не перехватывал прочие. Любой сбой разбора значений — InvalidDataException с причиной.
    /// Мутация P3D_LOADGUARD=0.
    /// </summary>
    public static (Part Part, ParameterTable Parameters) LoadLenient(string json, out List<string> problems)
    {
        if (LoadGuardOff) return LoadLenientCore(json, out problems);
        try { return LoadLenientCore(json, out problems); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or NullReferenceException or InvalidCastException
                                       or OverflowException or FormatException or Core.P3DException && ex is not InvalidDataException)
        {
            throw new InvalidDataException("недопустимое значение в файле: " + ex.Message, ex);
        }
    }
    static readonly bool LoadGuardOff = Environment.GetEnvironmentVariable("P3D_LOADGUARD") == "0";

    private static (Part Part, ParameterTable Parameters) LoadLenientCore(string json, out List<string> problems)
    {
        problems = new List<string>();
        var lost = problems;
        DocDto? doc;
        try { doc = JsonSerializer.Deserialize<DocDto>(json, Opts); }
        catch (JsonException ex) { throw new InvalidDataException("файл не разобран: " + ex.Message, ex); }
        if (doc == null) throw new InvalidDataException("файл не разобран");
        if (doc.Version > CurrentVersion)
            throw new InvalidDataException($"файл записан более новой сборкой (вид {doc.Version})");
        // k114: сетка показа из файла — если описание модели то же, что при записи
        var display = doc.Display; doc.Display = null;
        bool displayOk = false;
        if (display != null && !DisplayOff)
        {
            // k116: подпись — без учёта переводов строк (файл с Windows \r\n открывался на Linux без сетки, и обратно)
            var re = JsonSerializer.Serialize(doc, Opts);
            displayOk = display.Signature == Signature(re) || (!NewlineSigOff && display.Signature == SignatureRaw(re.Replace("\r\n", "\n").Replace("\n", "\r\n")));
        }

        var units = Core.UnitSystem.FromDictionary(doc.Units);                  // m.x4: система единиц документа
        var table = new ParameterTable { Units = units };
        ParamsIn(doc.Parameters, table);
        table.Recalculate();

        var part = new Part(doc.Name) { ActiveConfiguration = doc.ActiveConfiguration ?? "", Units = units };
        if (!TreeStateOff && doc.Tree != null) foreach (var kv in doc.Tree) part.TreeState[kv.Key] = kv.Value;
        if (doc.PartParameters is { Count: > 0 } pp)
        {
            part.LocalParameters.Units = units;
            part.LocalParameters.Parent = table;
            ParamsIn(pp, part.LocalParameters);
            part.LocalParameters.Recalculate();
        }

        if (doc.Material is { } md) part.Material = Load(md);

        foreach (var fd in doc.FaceSets)
        {
            var set = new FaceSet(fd.Name ?? "набор") { Purpose = fd.Purpose ?? "" };
            foreach (var f in fd.Faces)
                set.Faces.Add(new FaceRef(Vec(f.Point), Vec(f.Normal), f.Tag ?? ""));
            part.FaceSets.Add(set);
        }

        if (doc.Info is { } id)
            part.Info = new DocumentInfo
            {
                Code = id.Code ?? "", Title = id.Title ?? "", Author = id.Author ?? "",
                Checker = id.Checker ?? "", Company = id.Company ?? "",
                Created = id.Created, Modified = id.Modified,
                Revision = id.Revision ?? "", Note = id.Note ?? "",
                Uom = id.Uom ?? "шт", LengthUnits = id.Units ?? "мм", Tolerances = id.Tolerances ?? "ISO 2768-mK"
            };

        foreach (var cd in doc.Configurations)
        {
            var cfg = new Configuration(cd.Name) { Comment = cd.Comment ?? "" };
            foreach (var (k, v) in cd.Parameters) cfg.Parameters[k] = v;
            foreach (string f in cd.Suppressed) cfg.SuppressedFeatures.Add(f);
            foreach (string h in cd.Hidden) cfg.HiddenParts.Add(h);
            part.Configurations.Add(cfg);
        }
        LoadDatums(part, doc.Datums);
        part.FreeFormDoc = doc.FreeForm; part.MergeFreeForm = doc.MergeFreeForm;
        if (doc.ConstructionPlane is { Length: 9 } cpl)
            try { part.ConstructionPlane = new Frame3(new Vector3d(cpl[0], cpl[1], cpl[2]), new Vector3d(cpl[3], cpl[4], cpl[5]), new Vector3d(cpl[6], cpl[7], cpl[8])); } catch { }
        var sketchById = new Dictionary<uint, Sketch>();
        var byOldOf = new Dictionary<uint, Dictionary<uint, SkEntity>>();   // k110: номера элементов эскизов из файла → новые

        foreach (var sd in doc.Sketches)
        {
            var frame = new Frame3(
                new Vector3d(sd.Plane[0], sd.Plane[1], sd.Plane[2]),
                new Vector3d(sd.Plane[3], sd.Plane[4], sd.Plane[5]),
                new Vector3d(sd.Plane[6], sd.Plane[7], sd.Plane[8]));
            var s = new Sketch(sd.Name, frame) { BaseFaceTag = sd.BaseFaceTag };
            if (sd.PlaneRef != null) s.PlaneDescription = sd.PlaneRef;
            s.BasePlaneId = sd.BasePlaneId;
            s.BaseFrame = sd.BaseFrame is { Length: 9 } bf ? new Frame3(new Vector3d(bf[0], bf[1], bf[2]), new Vector3d(bf[3], bf[4], bf[5]), new Vector3d(bf[6], bf[7], bf[8])) : frame;
            if (sd.Up is { Length: 2 } up) s.UpVector = new Vector2d(up[0], up[1]);
            if (sd.UpRef != null) s.UpDescription = sd.UpRef;
            if (sd.OriginRef != null) s.OriginDescription = sd.OriginRef;
            if (sd.OriginBase is { Length: 3 } obs) s.OriginBase = new Vector3d(obs[0], obs[1], obs[2]);
            if (sd.OriginOffset is { Length: 2 } oo) s.OriginOffset = new Vector2d(oo[0], oo[1]);
            if (!SketchVisOff && sd.Shown is { } shown) { s.Visible = shown; s.VisibleUserSet = true; }

            var byOld = new Dictionary<uint, SkEntity>();
            foreach (var ed in sd.Entities.Where(x => x.Kind == "point"))
                byOld[ed.Id] = s.AddEntity(new SkPoint(ed.X, ed.Y));

            foreach (var ed in sd.Entities.Where(x => x.Kind != "point"))
            {
                SkEntity? made = ed.Kind switch
                {
                    "line" when byOld.TryGetValue(ed.A, out var a) && byOld.TryGetValue(ed.B, out var b)
                        => s.AddEntity(new SkLine((SkPoint)a, (SkPoint)b)),
                    "circle" when byOld.TryGetValue(ed.A, out var c)
                        => s.AddEntity(new SkCircle((SkPoint)c, ed.R)),
                    "arc" when byOld.TryGetValue(ed.A, out var c) && byOld.TryGetValue(ed.B, out var st)
                               && byOld.TryGetValue(ed.C, out var en)
                        => s.AddEntity(new SkArc((SkPoint)c, (SkPoint)st, (SkPoint)en) { CCW = ed.CCW }),
                    "spline" when ed.Pts is { Count: >= 2 } && ed.Pts.All(byOld.ContainsKey)
                        => s.AddEntity(new SkSpline(ed.Pts.Select(i => (SkPoint)byOld[i]).ToList())),
                    "ellipse" when byOld.TryGetValue(ed.A, out var ec)
                        => s.AddEntity(new SkEllipse((SkPoint)ec, ed.R, ed.R2, ed.Rot) { A0 = ed.A0, A1 = ed.A1 }),
                    _ => null
                };
                if (made != null) byOld[ed.Id] = made;
                else lost.Add($"sketch '{sd.Name}': entity {ed.Id} ({ed.Kind}) not restored");
            }

            foreach (var ed in sd.Entities)
                if (byOld.TryGetValue(ed.Id, out var le))
                {
                    if (ed.Role is int rl && Enum.IsDefined(typeof(SkRole), rl)) le.Role = (SkRole)rl;
                    if (ed.Fixed == true) le.Fixed = true;
                    if (ed.Src != null) le.ReferenceSource = ed.Src;
                }
            // k96: встроенные ссылки (нуль, оси) — по особым номерам
            foreach (var bid in new[] { Sketch.OriginSaveId, Sketch.AxisHSaveId, Sketch.AxisVSaveId }) byOld[bid] = s.BuiltinBySaveId(bid)!;

            foreach (var cd in sd.Constraints)
            {
                if (!Enum.TryParse<ConstraintType>(cd.Type, out var type)) { lost.Add($"sketch '{sd.Name}': constraint type {cd.Type}"); continue; }
                var items = cd.Items.Where(byOld.ContainsKey).Select(i => byOld[i]).ToArray();
                if (items.Length != cd.Items.Count) { lost.Add($"sketch '{sd.Name}': constraint {cd.Type} lost a member"); continue; }
                s.AddConstraint(type, items).Enabled = cd.Enabled;
            }

            foreach (var dd in sd.Dimensions)
            {
                if (!Enum.TryParse<DimensionType>(dd.Type, out var type)) { lost.Add($"sketch '{sd.Name}': dimension type {dd.Type}"); continue; }
                var items = dd.Items.Where(byOld.ContainsKey).Select(i => byOld[i]).ToArray();
                if (items.Length != dd.Items.Count) { lost.Add($"sketch '{sd.Name}': dimension {dd.Type} lost a member"); continue; }
                var dim = s.AddDimension(type, dd.Value, items);
                dim.Locked = dd.Locked;
                dim.Reference = dd.Reference;
                dim.Formula = dd.Formula;
                dim.Note = dd.Note;
                dim.IsDefault = dd.IsDefault == true; dim.UserPlaced = dd.UserPlaced == true;
                if (dd.Ix is int ix && ix >= 0) dim.Index = ix;   // k102: идентификатор размера dN
                if (dd.Offset is { Length: 2 } off) dim.TextOffset = new OpenTK.Mathematics.Vector2d(off[0], off[1]);
                if (dd.Arrows != null && Enum.TryParse<ArrowPlacement>(dd.Arrows, out var ap)) dim.Arrows = ap;
                dim.UserLocked = dd.UserLocked == true; dim.SingleArrow = dd.SingleArrow == true; dim.DiameterAsLinear = dd.AsLinear == true;
            }

            byOldOf[sd.Id] = byOld;
            part.Children.Add(s);
            s.Parent = part;          // k110: правка прочитанного эскиза извещает деталь (ссылки, пересчёт операций)
            sketchById[sd.Id] = s;
        }

        int featureIndex = 0;
        foreach (var fd in doc.Features)
        {
            var known = new HashSet<Feature>(part.Features);
            using var keyScope = new KeyOnAdd(part, known, string.IsNullOrEmpty(fd.Key) ? $"legacy{featureIndex}" : fd.Key!,
                                              fd.Policy != null && Enum.TryParse<Kernel.OperationPolicy>(fd.Policy, out var pol) ? pol : null,
                                              fd.Str != null && fd.Str.TryGetValue("group", out var grp) ? grp : null,
                                              GetS(fd, "ff"), GetS(fd, "ffbody"), HasFlag(fd, "frozen"));
            featureIndex++;
            switch (fd.Kind)
            {
                case "extrude":
                {
                    var e = new ExtrudeFeature(fd.Name) { Suppressed = fd.Suppressed };
                    if (sketchById.TryGetValue(fd.SketchId, out var sk)) e.SketchId = sk.Id;
                    e.Side1.Distance = new ValueRef(fd.Distance) { Formula = fd.Formula };
                    e.Side1.Flip = fd.Flip;
                    e.TwoSided = fd.TwoSided;
                    e.Symmetric = fd.Symmetric;
                    e.Cut = fd.Cut;
                    if (fd.Num != null && fd.Num.TryGetValue("thin", out var thin)) e.Thin = new ValueRef(thin) { Formula = fd.Str != null && fd.Str.TryGetValue("thin", out var tf) ? tf : null };
                    e.ThinOutside = fd.Flags?.Contains("thinOutside") == true;
                    // k115: «глубина до», вторая сторона
                    if (!ExtrudeEndOff)
                    {
                        if (GetS(fd, "end1") is { } e1 && Enum.TryParse<EndType>(e1, out var t1)) e.Side1.End = t1;
                        if (GetS(fd, "end2") is { } e2 && Enum.TryParse<EndType>(e2, out var t2)) e.Side2.End = t2;
                        if (GetS(fd, "target1") is { } g1 && uint.TryParse(g1, out var u1)) e.Side1.TargetId = u1;
                        if (GetS(fd, "target2") is { } g2 && uint.TryParse(g2, out var u2)) e.Side2.TargetId = u2;
                        if (fd.Distance2 > 0) e.Side2.Distance = new ValueRef(fd.Distance2) { Formula = fd.Formula2 };
                        e.Side2.Flip = fd.Flags?.Contains("flip2") == true;
                    }
                    part.AddFeature(e);
                    break;
                }

                case "revolve":
                {
                    var r = new RevolveFeature(fd.Name) { Suppressed = fd.Suppressed, Cut = fd.Cut };
                    if (sketchById.TryGetValue(fd.SketchId, out var sk)) r.SketchId = sk.Id;
                    r.AxisId = fd.AxisId;
                    r.AxisEntityId = fd.AxisEntityId;
                    r.StartAngle = new ValueRef(fd.StartAngle) { Formula = fd.StartFormula };
                    r.EndAngle = new ValueRef(fd.EndAngle) { Formula = fd.EndFormula };
                    part.AddFeature(r);
                    break;
                }

                case "sweep":
                {
                    var w = new SweepFeature(fd.Name) { Suppressed = fd.Suppressed, Cut = fd.Cut };
                    if (sketchById.TryGetValue(fd.SketchId, out var sec)) w.SectionSketchId = sec.Id;
                    if (sketchById.TryGetValue(fd.PathSketchId, out var path)) w.PathSketchId = path.Id;
                    w.PathLineId = fd.PathLineId;
                    w.LengthFraction = new ValueRef(fd.Distance) { Formula = fd.Formula };
                    if (fd.Twist is double tw) w.Twist = new ValueRef(tw) { Formula = fd.TwistFormula };
                    w.KeepOrientation = fd.KeepOrientation == true;
                    w.Reverse = fd.Flags?.Contains("reverse") == true;
                    part.AddFeature(w);
                    break;
                }

                case "round":
                {
                    var rf = new RoundFeature(fd.Name) { Suppressed = fd.Suppressed };
                    rf.Radius = new ValueRef(fd.Distance) { Formula = fd.Formula };
                    if (fd.RadiusEnd is double rEnd) rf.RadiusEnd = new ValueRef(rEnd) { Formula = fd.RadiusEndFormula };
                    if (fd.LawF is { } lf && fd.LawR is { } lr && lf.Count == lr.Count)
                        for (int i = 0; i < lf.Count; i++) rf.LawPoints.Add((lf[i], new ValueRef(lr[i]) { Formula = fd.LawRFormulas != null && i < fd.LawRFormulas.Count ? fd.LawRFormulas[i] : null }));
                    foreach (var ed in fd.Edges) rf.Edges.Add(LoadEdge(ed));
                    foreach (var sd in fd.Selectors ?? new())
                        if (Enum.TryParse<EdgeSelectorKind>(sd.Kind, out var sk) && sd.Feature != null) rf.Selectors.Add(new EdgeSelector(sk, sd.Feature, sd.Other));
                        else lost.Add($"round '{fd.Name}': selector {sd.Kind} not restored");
                    part.AddFeature(rf);
                    break;
                }

                case "mirror":
                {
                    var mf = new MirrorFeature(fd.Name) { Suppressed = fd.Suppressed, At = Vec(fd.Point) };
                    var n = Vec(fd.Dir);
                    if (n.LengthSquared > 1e-18) mf.Normal = n;
                    if (fd.Sources != null) mf.Sources.AddRange(fd.Sources);
                    mf.ReplaceBody = HasFlag(fd, "replace");
                    part.AddFeature(mf);
                    break;
                }

                case "linear":
                {
                    var lp = new LinearPatternFeature(fd.Name) { Suppressed = fd.Suppressed, Step = Vec(fd.Dir) };
                    lp.Count = new ValueRef(fd.Distance) { Formula = fd.Formula };
                    if (fd.Sources != null) lp.Sources.AddRange(fd.Sources);
                    part.AddFeature(lp);
                    break;
                }

                case "circular":
                {
                    var cp = new CircularPatternFeature(fd.Name) { Suppressed = fd.Suppressed, At = Vec(fd.Point) };
                    var ax = Vec(fd.Dir);
                    if (ax.LengthSquared > 1e-18) cp.Axis = ax;
                    cp.Count = new ValueRef(fd.Distance) { Formula = fd.Formula };
                    if (fd.Sources != null) cp.Sources.AddRange(fd.Sources);
                    part.AddFeature(cp);
                    break;
                }

                case "draft":
                {
                    var df = new DraftFeature(fd.Name) { Suppressed = fd.Suppressed, NeutralPoint = Vec(fd.Point) };
                    var n2 = Vec(fd.Dir);
                    if (n2.LengthSquared > 1e-18) df.NeutralNormal = n2;
                    df.Angle = new ValueRef(fd.Distance) { Formula = fd.Formula };
                    for (int i = 0; i + 2 < fd.OpenFaces.Count; i += 3)
                        df.OnFaces.Add(new Vector3d(fd.OpenFaces[i], fd.OpenFaces[i + 1], fd.OpenFaces[i + 2]));
                    if (fd.FaceNames != null) df.OnFaceNames.AddRange(fd.FaceNames);
                    df.OnSelectors.AddRange(FaceSel(fd.FaceSelectors));
                    if (fd.Edges.Count > 0) df.Hinge = LoadEdge(fd.Edges[0]);
                    if (fd.Edges.Count > 1) df.Moving = LoadEdge(fd.Edges[1]);
                    df.Total = fd.Flags?.Contains("total") == true;
                    df.Reverse = fd.Flags?.Contains("reverse") == true;
                    if (fd.Num != null && fd.Num.TryGetValue("refNx", out var rnx)) df.RefNormal = new Vector3d(rnx, fd.Num.GetValueOrDefault("refNy"), fd.Num.GetValueOrDefault("refNz"));
                    part.AddFeature(df);
                    break;
                }

                case "rib":
                {
                    var rb = new RibFeature(fd.Name) { Suppressed = fd.Suppressed };
                    if (sketchById.TryGetValue(fd.SketchId, out var rs)) rb.SketchId = rs.Id;
                    var g = Vec(fd.Dir);
                    if (g.LengthSquared > 1e-18) rb.Grow = g;
                    rb.Thickness = new ValueRef(fd.Distance) { Formula = fd.Formula };
                    rb.MaxLength = new ValueRef(fd.Distance2) { Formula = fd.Formula2 };
                    part.AddFeature(rb);
                    break;
                }

                case "move_copy":
                {
                    var mc = new MoveCopyFeature(fd.Name) { Suppressed = fd.Suppressed, Dx = GetV(fd, "dx", 0), Dy = GetV(fd, "dy", 0), Dz = GetV(fd, "dz", 0), Angle = GetV(fd, "angle", 0), AxisAt = Vec(fd.Point), Copy = HasFlag(fd, "copy") };
                    var ad = Vec(fd.Dir); if (ad.LengthSquared > 1e-18) mc.AxisDir = ad;
                    part.AddFeature(mc);
                    break;
                }
                case "face_edit":
                {
                    var fe = new FaceEditFeature(fd.Name) { Suppressed = fd.Suppressed, Distance = GetV(fd, "distance", 2), Radius = GetV(fd, "radius", 5) };
                    if (Enum.TryParse<FaceEditMode>(GetS(fd, "mode"), out var fm)) fe.Mode = fm;
                    for (int i = 0; i + 2 < fd.OpenFaces.Count; i += 3) fe.OnFaces.Add(new Vector3d(fd.OpenFaces[i], fd.OpenFaces[i + 1], fd.OpenFaces[i + 2]));
                    if (fd.FaceNames != null) fe.OnFaceNames.AddRange(fd.FaceNames);
                    part.AddFeature(fe);
                    break;
                }
                case "points_pattern":
                {
                    var ppf = new PointsPatternFeature(fd.Name) { Suppressed = fd.Suppressed, Count = GetV(fd, "count", 4), Spacing = GetV(fd, "spacing", 10), Margin = GetV(fd, "margin", 0) };
                    if (Enum.TryParse<PointsPatternMode>(GetS(fd, "mode"), out var pm)) ppf.Mode = pm;
                    if (sketchById.TryGetValue(fd.SketchId, out var psk)) ppf.SketchId = psk.Id;
                    if (fd.Sources != null) ppf.Sources.AddRange(fd.Sources);
                    part.AddFeature(ppf);
                    break;
                }
                case "full_round":
                {
                    var fr = new FullRoundFeature(fd.Name) { Suppressed = fd.Suppressed };
                    for (int i = 0; i + 2 < fd.OpenFaces.Count; i += 3) fr.OnFaces.Add(new Vector3d(fd.OpenFaces[i], fd.OpenFaces[i + 1], fd.OpenFaces[i + 2]));
                    if (fd.FaceNames != null) fr.OnFaceNames.AddRange(fd.FaceNames);
                    part.AddFeature(fr);
                    break;
                }
                case "thread":
                {
                    var ct = new CosmeticThreadFeature(fd.Name) { Suppressed = fd.Suppressed, Pitch = GetV(fd, "pitch", 1.5), Length = GetV(fd, "length", 0) };
                    for (int i = 0; i + 2 < fd.OpenFaces.Count; i += 3) ct.OnFaces.Add(new Vector3d(fd.OpenFaces[i], fd.OpenFaces[i + 1], fd.OpenFaces[i + 2]));
                    if (fd.FaceNames != null) ct.OnFaceNames.AddRange(fd.FaceNames);
                    part.AddFeature(ct);
                    break;
                }
                case "surface":
                {
                    var sf = new SurfaceFeature(fd.Name) { Suppressed = fd.Suppressed, Depth = GetV(fd, "depth", 20), Angle = GetV(fd, "angle", 360), AxisEntityId = fd.AxisEntityId, Flip = HasFlag(fd, "flip") };
                    if (Enum.TryParse<SurfaceKind>(GetS(fd, "mode"), out var sm)) sf.Mode = sm;
                    if (sketchById.TryGetValue(fd.SketchId, out var ssk)) sf.SketchId = ssk.Id;
                    for (int i = 0; i + 2 < fd.OpenFaces.Count; i += 3) sf.OnFaces.Add(new Vector3d(fd.OpenFaces[i], fd.OpenFaces[i + 1], fd.OpenFaces[i + 2]));
                    if (fd.FaceNames != null) sf.OnFaceNames.AddRange(fd.FaceNames);
                    part.AddFeature(sf);
                    break;
                }
                case "quilt_edit":
                {
                    var qe = new QuiltEditFeature(fd.Name) { Suppressed = fd.Suppressed, Distance = GetV(fd, "distance", 5), PlaneAt = Vec(fd.Point), Flip = HasFlag(fd, "flip") };
                    var qn = Vec(fd.Dir); if (qn.LengthSquared > 1e-18) qe.PlaneNormal = qn;
                    if (Enum.TryParse<QuiltEditKind>(GetS(fd, "mode"), out var qm)) qe.Mode = qm;
                    if (fd.Sources != null) qe.Sources.AddRange(fd.Sources);
                    part.AddFeature(qe);
                    break;
                }
                case "quilt_solid":
                {
                    var qs = new QuiltSolidFeature(fd.Name) { Suppressed = fd.Suppressed, Cut = fd.Cut, Source = GetS(fd, "source") ?? "", Thickness = GetV(fd, "thickness", 2), Flip = HasFlag(fd, "flip") };
                    if (Enum.TryParse<QuiltSolidKind>(GetS(fd, "mode"), out var sm2)) qs.Mode = sm2;
                    for (int i = 0; i + 2 < fd.OpenFaces.Count; i += 3) qs.OnFaces.Add(new Vector3d(fd.OpenFaces[i], fd.OpenFaces[i + 1], fd.OpenFaces[i + 2]));
                    if (fd.FaceNames != null) qs.OnFaceNames.AddRange(fd.FaceNames);
                    part.AddFeature(qs);
                    break;
                }
                case "blend":
                {
                    var bf = new BlendFeature(fd.Name) { Suppressed = fd.Suppressed, Cut = fd.Cut, AxisEntityId = fd.AxisEntityId, AsSurface = HasFlag(fd, "surface"), Steps = (int)GetD(fd, "steps", 16), Points = (int)GetD(fd, "points", 48) };
                    if (Enum.TryParse<BlendKind>(GetS(fd, "mode"), out var bm)) bf.Mode = bm;
                    foreach (uint old in fd.Sections) if (sketchById.TryGetValue(old, out var bs)) bf.SectionSketchIds.Add(bs.Id);
                    if (sketchById.TryGetValue(fd.PathSketchId, out var bp)) bf.PathSketchId = bp.Id;
                    part.AddFeature(bf);
                    break;
                }
                case "freeform":
                    part.AddFeature(new FreeFormFeature(fd.Name) { Suppressed = fd.Suppressed, InputStep = GetS(fd, "input") ?? "", InputSig = GetS(fd, "sig") ?? "" });
                    break;
                case "ff_primitive":
                {
                    var pr = new FFPrimitiveFeature(fd.Name) { Suppressed = fd.Suppressed, Cut = fd.Cut, A = GetV(fd, "a", 40), B = GetV(fd, "b", 30), C = GetV(fd, "c", 20), Origin = Vec(fd.Point) };
                    if (Enum.TryParse<FFPrimitive>(GetS(fd, "shape"), out var sh)) pr.Shape = sh;
                    if (fd.Dir is { Length: 6 } uv) { pr.U = new Vector3d(uv[0], uv[1], uv[2]); pr.V = new Vector3d(uv[3], uv[4], uv[5]); }
                    part.AddFeature(pr);
                    break;
                }
                case "ff_boolean":
                {
                    var fb = new FFBooleanFeature(fd.Name) { Suppressed = fd.Suppressed, Target = GetS(fd, "target") ?? "", KeepTools = HasFlag(fd, "keep") };
                    if (Enum.TryParse<FFBool>(GetS(fd, "op"), out var bo)) fb.Op = bo;
                    if (fd.Sources != null) fb.Tools.AddRange(fd.Sources);
                    part.AddFeature(fb);
                    break;
                }
                case "ff_transform":
                {
                    var ft = new FFTransformFeature(fd.Name) { Suppressed = fd.Suppressed, Dx = GetV(fd, "dx", 0), Dy = GetV(fd, "dy", 0), Dz = GetV(fd, "dz", 0), Angle = GetV(fd, "angle", 0), Factor = GetV(fd, "factor", 1), AxisAt = Vec(fd.Point), AxisDir = Vec(fd.Dir), Copy = HasFlag(fd, "copy") };
                    if (Enum.TryParse<FFXform>(GetS(fd, "mode"), out var xm)) ft.Mode = xm;
                    ft.Sx = GetV(fd, "sx", 1); ft.Sy = GetV(fd, "sy", 1); ft.Sz = GetV(fd, "sz", 1);
                    if (fd.ThickFaces is { Count: 6 } fr) { ft.FrameX = new Vector3d(fr[0], fr[1], fr[2]); ft.FrameY = new Vector3d(fr[3], fr[4], fr[5]); }
                    if (fd.Sources != null) ft.BodyNames.AddRange(fd.Sources);
                    part.AddFeature(ft);
                    break;
                }
                case "ff_split":
                    part.AddFeature(new FFSplitFeature(fd.Name) { Suppressed = fd.Suppressed, Body = GetS(fd, "body") ?? "", PlaneAt = Vec(fd.Point), Normal = Vec(fd.Dir) });
                    break;
                case "ff_mirror":
                {
                    var fm2 = new FFMirrorFeature(fd.Name) { Suppressed = fd.Suppressed, PlaneAt = Vec(fd.Point), Normal = Vec(fd.Dir) };
                    if (Enum.TryParse<FFMirrorMode>(GetS(fd, "mode"), out var mm)) fm2.Mode = mm;
                    if (fd.Sources != null) fm2.BodyNames.AddRange(fd.Sources);
                    part.AddFeature(fm2);
                    break;
                }
                case "ff_array":
                {
                    var fa = new FFArrayFeature(fd.Name) { Suppressed = fd.Suppressed, Count = GetV(fd, "count", 3), Count2 = GetV(fd, "count2", 1), Angle = GetV(fd, "angle", 360),
                        CurveName = GetS(fd, "curve") ?? "", Align = HasFlag(fd, "align"), AxisAt = Vec(fd.Point), AxisDir = Vec(fd.Dir) };
                    if (Enum.TryParse<FFArrayKind>(GetS(fd, "kind"), out var ak)) fa.Kind = ak;
                    if (fd.Sources != null) fa.BodyNames.AddRange(fd.Sources);
                    if (fd.ThickFaces is { Count: 6 } st) { fa.Step = new Vector3d(st[0], st[1], st[2]); fa.Step2 = new Vector3d(st[3], st[4], st[5]); }
                    part.AddFeature(fa);
                    break;
                }
                case "ff_pipe":
                {
                    var fp = new FFPipeFeature(fd.Name) { Suppressed = fd.Suppressed, Cut = fd.Cut, CurveName = GetS(fd, "curve") ?? "", Radius = GetV(fd, "radius", 3), Inner = GetV(fd, "inner", 0) };
                    if (sketchById.TryGetValue(fd.SketchId, out var psk)) fp.SketchId = psk.Id;
                    part.AddFeature(fp);
                    break;
                }
                case "ff_imprint":
                {
                    var fi = new FFImprintFeature(fd.Name) { Suppressed = fd.Suppressed, CurveName = GetS(fd, "curve") ?? "" };
                    if (sketchById.TryGetValue(fd.SketchId, out var isk)) fi.SketchId = isk.Id;
                    for (int i = 0; i + 2 < fd.OpenFaces.Count; i += 3) fi.OnFaces.Add(new Vector3d(fd.OpenFaces[i], fd.OpenFaces[i + 1], fd.OpenFaces[i + 2]));
                    if (fd.FaceNames != null) fi.OnFaceNames.AddRange(fd.FaceNames);
                    part.AddFeature(fi);
                    break;
                }
                case "ff_nurbs":
                {
                    var nq = new FFNurbsQuiltFeature(fd.Name) { Suppressed = fd.Suppressed, ReplacesKey = GetS(fd, "replaces"), QuiltName = GetS(fd, "quilt") ?? "" };
                    if (NurbsDef.Unpack(fd.Nurbs) is { } nd) nq.Def = nd;
                    part.AddFeature(nq);
                    break;
                }
                case "ff_planar":
                {
                    var pq = new FFPlanarQuiltFeature(fd.Name) { Suppressed = fd.Suppressed };
                    for (int i = 0; i + 2 < fd.OpenFaces.Count; i += 3) pq.Boundary.Add(new Vector3d(fd.OpenFaces[i], fd.OpenFaces[i + 1], fd.OpenFaces[i + 2]));
                    part.AddFeature(pq);
                    break;
                }
                case "ff_stitch":
                {
                    var st = new FFStitchFeature(fd.Name) { Suppressed = fd.Suppressed };
                    if (fd.Sources != null) st.Sources.AddRange(fd.Sources);
                    part.AddFeature(st);
                    break;
                }
                case "ff_subd":
                    part.AddFeature(new FFSubDFeature(fd.Name) { Suppressed = fd.Suppressed, Cage = SubDCage.Unpack(fd.Cage) ?? new SubDCage(), Body = GetS(fd, "body"), ToNurbs = HasFlag(fd, "nurbs") });
                    break;
                case "ff_align_face":
                {
                    var fl = new FFAlignFaceFeature(fd.Name) { Suppressed = fd.Suppressed, Offset = GetV(fd, "offset", 0), PlaneAt = Vec(fd.Point), Normal = Vec(fd.Dir) };
                    for (int i = 0; i + 2 < fd.OpenFaces.Count; i += 3) fl.OnFaces.Add(new Vector3d(fd.OpenFaces[i], fd.OpenFaces[i + 1], fd.OpenFaces[i + 2]));
                    if (fd.FaceNames != null) fl.OnFaceNames.AddRange(fd.FaceNames);
                    part.AddFeature(fl);
                    break;
                }
                case "mesh_import":
                {
                    var mi = new MeshImportFeature(fd.Name)
                    {
                        Suppressed = fd.Suppressed, Cut = fd.Cut, MeshData = GetS(fd, "mesh") ?? "", SourceName = GetS(fd, "source") ?? "",
                        Exact = HasFlag(fd, "exact"), Snap = HasFlag(fd, "snap"), TolScale = GetD(fd, "tol", 1), Scale = GetD(fd, "scale", 1),
                    };
                    if (GetS(fd, "step") is { Length: > 0 } st) { mi.Step = st; mi.StepKey = mi.Key; mi.Report = GetS(fd, "report") ?? ""; }
                    part.AddFeature(mi);
                    break;
                }
                case "insert_body":
                {
                    var ib = new InsertBodyFeature(fd.Name) { Suppressed = fd.Suppressed, Cut = fd.Cut, Step = GetS(fd, "step") ?? "", SourceName = GetS(fd, "source") ?? "", Offset = Vec(fd.Point) };
                    part.AddFeature(ib);
                    break;
                }
                case "helical_sweep":
                {
                    var hs = new HelicalSweepFeature(fd.Name) { Suppressed = fd.Suppressed, Cut = fd.Cut, AxisId = fd.AxisId, AxisEntityId = fd.AxisEntityId };
                    if (sketchById.TryGetValue(fd.SketchId, out var hsk)) hs.SketchId = hsk.Id;
                    hs.Pitch = GetV(fd, "pitch", 5); hs.Turns = GetV(fd, "turns", 3); hs.LeftHand = HasFlag(fd, "left");
                    part.AddFeature(hs);
                    break;
                }

                case "loft":
                {
                    var lf = new LoftFeature(fd.Name) { Suppressed = fd.Suppressed, Cut = fd.Cut, Samples = fd.Samples, Smooth = fd.Smooth, StartTangent = fd.StartTangent, EndTangent = fd.EndTangent };
                    foreach (uint old in fd.Sections)
                        if (sketchById.TryGetValue(old, out var sk2)) lf.SectionSketchIds.Add(sk2.Id);
                    foreach (uint old in fd.Guides ?? new())
                        if (sketchById.TryGetValue(old, out var sk3)) lf.GuideSketchIds.Add(sk3.Id);
                    part.AddFeature(lf);
                    break;
                }

                case "hole":
                {
                    var hf = new HoleFeature(fd.Name) { Suppressed = fd.Suppressed, ThroughAll = fd.ThroughAll };
                    if (sketchById.TryGetValue(fd.SketchId, out var hs)) hf.SketchId = hs.Id;
                    var hd = Vec(fd.Dir);
                    if (hd.LengthSquared > 1e-18) hf.Direction = hd;
                    if (Enum.TryParse<HoleKind>(fd.HoleKindName, out var hk)) hf.Kind = hk;
                    hf.Diameter = new ValueRef(fd.Distance) { Formula = fd.Formula };
                    hf.Depth = new ValueRef(fd.Distance2) { Formula = fd.Formula2 };
                    hf.HeadDiameter = new ValueRef(fd.HeadDiameter);
                    hf.HeadDepth = new ValueRef(fd.HeadDepth);
                    hf.HeadAngle = new ValueRef(fd.HeadAngle);
                    for (int i = 0; i + 2 < fd.OpenFaces.Count; i += 3)
                        hf.Points.Add(new Vector3d(fd.OpenFaces[i], fd.OpenFaces[i + 1], fd.OpenFaces[i + 2]));
                    if (fd.FaceNames != null) hf.PointFaces.AddRange(fd.FaceNames);
                    part.AddFeature(hf);
                    break;
                }

                case "datumPlane":
                {
                    var dp = new DatumPlaneFeature(fd.Name) { Suppressed = fd.Suppressed };
                    if (Enum.TryParse<PlaneBy>(fd.DatumBy, out var by)) dp.By = by;
                    dp.BasePlane = fd.BasePlane ?? "";
                    dp.BaseAxis = fd.BaseAxis ?? "";
                    dp.Offset = new ValueRef(fd.Distance) { Formula = fd.Formula };
                    dp.Angle = new ValueRef(fd.Distance2) { Formula = fd.Formula2 };
                    for (int i = 0; i + 2 < fd.OpenFaces.Count; i += 3)
                        dp.Points.Add(new Vector3d(fd.OpenFaces[i], fd.OpenFaces[i + 1], fd.OpenFaces[i + 2]));
                    part.AddFeature(dp);
                    break;
                }

                case "datumAxis":
                {
                    var da = new DatumAxisFeature(fd.Name) { Suppressed = fd.Suppressed };
                    if (Enum.TryParse<AxisBy>(fd.DatumBy, out var by2)) da.By = by2;
                    da.BasePlane = fd.BasePlane ?? "";
                    da.SecondPlane = fd.SecondPlane ?? "";
                    da.FaceTag = fd.FaceTag ?? "";
                    for (int i = 0; i + 2 < fd.OpenFaces.Count; i += 3)
                        da.Points.Add(new Vector3d(fd.OpenFaces[i], fd.OpenFaces[i + 1], fd.OpenFaces[i + 2]));
                    part.AddFeature(da);
                    break;
                }

                case "datumPoint":
                {
                    var dpt = new DatumPointFeature(fd.Name)
                    { Suppressed = fd.Suppressed, BaseAxis = fd.BaseAxis ?? "", BasePlane = fd.BasePlane ?? "", At = Vec(fd.Point) };
                    part.AddFeature(dpt);
                    break;
                }

                case "shell":
                {
                    var sh = new ShellFeature(fd.Name) { Suppressed = fd.Suppressed };
                    sh.Thickness = new ValueRef(fd.Distance) { Formula = fd.Formula };
                    for (int i = 0; i + 2 < fd.OpenFaces.Count; i += 3)
                        sh.OpenFaces.Add(new Vector3d(fd.OpenFaces[i], fd.OpenFaces[i + 1], fd.OpenFaces[i + 2]));
                    if (fd.FaceNames != null) sh.OpenFaceNames.AddRange(fd.FaceNames);
                    sh.OpenSelectors.AddRange(FaceSel(fd.FaceSelectors));
                    if (fd.ThickFaces != null && fd.Values != null)
                        for (int i = 0; 3 * i + 2 < fd.ThickFaces.Count && i < fd.Values.Count; i++)
                            sh.FaceThickness.Add((new Vector3d(fd.ThickFaces[3 * i], fd.ThickFaces[3 * i + 1], fd.ThickFaces[3 * i + 2]),
                                                  new ValueRef(fd.Values[i]) { Formula = fd.Formulas != null && i < fd.Formulas.Count ? fd.Formulas[i] : null }));
                    part.AddFeature(sh);
                    break;
                }

                case "chamfer":
                {
                    var cf = new ChamferFeature(fd.Name) { Suppressed = fd.Suppressed };
                    cf.D1 = new ValueRef(fd.Distance) { Formula = fd.Formula };
                    cf.D2 = new ValueRef(fd.Distance2) { Formula = fd.Formula2 };
                    if (Enum.TryParse<ChamferKind>(fd.ChamferMode, out var km)) cf.Mode = km;
                    if (fd.RadiusEnd is double cEnd) cf.SizeEnd = new ValueRef(cEnd) { Formula = fd.RadiusEndFormula };
                    foreach (var ed in fd.Edges) cf.Edges.Add(LoadEdge(ed));
                    foreach (var sd in fd.Selectors ?? new())
                        if (Enum.TryParse<EdgeSelectorKind>(sd.Kind, out var sk) && sd.Feature != null) cf.Selectors.Add(new EdgeSelector(sk, sd.Feature, sd.Other));
                        else lost.Add($"chamfer '{fd.Name}': selector {sd.Kind} not restored");
                    part.AddFeature(cf);
                    break;
                }

                case "vertexChamfer":
                {
                    var vc = new VertexChamferFeature(fd.Name) { Suppressed = fd.Suppressed };
                    vc.Distance = new ValueRef(fd.Distance) { Formula = fd.Formula };
                    if (fd.Point is { Length: 3 } vp) vc.Vertex = new Vector3d(vp[0], vp[1], vp[2]);
                    vc.VertexKey = fd.VertexKey;
                    if (fd.Values != null)
                        for (int i = 0; i < fd.Values.Count && 3 * i + 2 < fd.OpenFaces.Count; i++)
                            vc.PerEdge.Add((new Vector3d(fd.OpenFaces[3 * i], fd.OpenFaces[3 * i + 1], fd.OpenFaces[3 * i + 2]),
                                            new ValueRef(fd.Values[i]) { Formula = fd.Formulas != null && i < fd.Formulas.Count ? fd.Formulas[i] : null }));
                    part.AddFeature(vc);
                    break;
                }

                case "vertexRound":
                {
                    var vr = new VertexRoundFeature(fd.Name) { Suppressed = fd.Suppressed };
                    vr.Radius = new ValueRef(fd.Distance) { Formula = fd.Formula };
                    if (fd.Point is { Length: 3 } vp) vr.Vertex = new Vector3d(vp[0], vp[1], vp[2]);
                    vr.VertexKey = fd.VertexKey;
                    part.AddFeature(vr);
                    break;
                }

                default:
                    lost.Add($"feature '{fd.Name}': unknown kind '{fd.Kind}'");
                    break;
            }
        }

        if (doc.Stage is int stg && stg >= 0 && stg < part.Features.Count) part.StageIndex = part.ClampStage(stg);
        // k110: ссылки на другие эскизы — на новые номера эскизов
        foreach (var sk in part.Sketches)
            foreach (var e in sk.Entities.SelectMany(x => x.Handles.Prepend(x)).Distinct())
                if (SketchLinks.TryParse(e.ReferenceSource, out var oldS, out var ent) && sketchById.TryGetValue(oldS, out var ns)
                    && byOldOf.TryGetValue(oldS, out var bo) && bo.TryGetValue(ent, out var ne))
                    e.ReferenceSource = $"{SketchLinks.Prefix}{ns.Id}:{ne.Id}";
        // k109: эскизы — на своих местах в дереве (прежде все эскизы вставали перед операциями: эскиз, созданный после
        // выдавливания, после отмены/открытия оказывался выше него). Файл до k109 — эскиз не в операции, стоящий на гранях
        // операции, — сразу после неё.
        if (!SketchSlotOff)
            foreach (var sd in doc.Sketches)
            {
                if (!sketchById.TryGetValue(sd.Id, out var sk)) continue;
                int slot = sd.Slot ?? (part.Features.Any(f => f.ConsumedSketchIds().Contains(sk.Id)) ? 0 : part.SketchSlotRange(sk).Lo);
                if (slot > 0) part.PlaceSketch(sk, slot);
            }
        if (!DatumSlotOff && _datumSlots != null)
        {
            for (int di = _datumSlots.Count - 1; di >= 0; di--) part.PlaceItem(_datumSlots[di].Item, _datumSlots[di].Slot);   // с конца: одинаковые места — в прежнем порядке
            _datumSlots = null;
        }
        if (displayOk && display!.Data is { } data)
        {
            try { if (DisplayCache.Unpack(Convert.FromBase64String(data)) is { } cached) part.SetCachedDisplay(cached, display.Volume); }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or EndOfStreamException or IOException) { }   // испорченная сетка — просто без неё
        }
        else if (display != null) Interlocked.Increment(ref DisplayRejected);
        return (part, table);
    }
    public static long DisplayRejected;
    /// <summary>Мутация P3D_EXTRUDEEND=0: «глубина до» и вторая сторона выдавливания не читаются (прежде).</summary>
    static readonly bool ExtrudeEndOff = Environment.GetEnvironmentVariable("P3D_EXTRUDEEND") == "0";

    // ------------------------------------------------------------------ записи файла

    /// <summary>u13: опорные элементы, созданные пользователем (плоскости, оси, точки, системы координат, кривые, линии).</summary>
    private sealed class DatumDto
    {
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Hidden { get; set; }
        public List<double> P { get; set; } = new();
        public string? Source { get; set; }
        public string? Mode { get; set; }
        public double Offset { get; set; }
        /// <summary>k116: угол, обратное направление, способ и базовая плоскость (правка плоскости).</summary>
        public double? Angle { get; set; }
        public bool? Flip { get; set; }
        public int? Method { get; set; }
        public string? Base { get; set; }
        /// <summary>k118: место в дереве — сколько операций перед элементом.</summary>
        public int? Slot { get; set; }
        /// <summary>u13i: NURBS опорной кривой.</summary>
        public List<double>? Cv { get; set; }
        public List<double>? Wt { get; set; }
        public List<double>? Kn { get; set; }
        public int? Deg { get; set; }
    }

    private sealed class DocDto
    {
        public int Version { get; set; }
        public List<DatumDto>? Datums { get; set; }
        public string Name { get; set; } = "деталь";
        /// <summary>m.x4: система единиц документа (размерность → обозначение); нет — мм, кг, с, градус.</summary>
        public Dictionary<string, string>? Units { get; set; }
        public List<ParamDto> Parameters { get; set; } = new();
        /// <summary>UI: параметры уровня детали (нет — только параметры документа).</summary>
        public List<ParamDto>? PartParameters { get; set; }
        public List<SketchDto> Sketches { get; set; } = new();
        public List<FeatureDto> Features { get; set; } = new();
        public List<ConfigDto> Configurations { get; set; } = new();
        public string? ActiveConfiguration { get; set; }
        public MaterialDto? Material { get; set; }
        public InfoDto? Info { get; set; }
        public List<FaceSetDto> FaceSets { get; set; } = new();
        /// <summary>u13h: документ — FreeForm-тело; объединять тела при встраивании; плоскость построения (9 чисел).</summary>
        public bool FreeForm { get; set; }
        public bool MergeFreeForm { get; set; }
        public double[]? ConstructionPlane { get; set; }
        /// <summary>Строка отката (режим вставки): сколько операций в расчёте; нет — в конце.</summary>
        public int? Stage { get; set; }
        /// <summary>k114: посчитанная сетка показа (поверхности) — открыть без ожидания перестроения.</summary>
        public DisplayDto? Display { get; set; }
        /// <summary>k116: развёрнутые ветки дерева (путь узла → развёрнут).</summary>
        public Dictionary<string, bool>? Tree { get; set; }
    }

    private sealed class DisplayDto
    {
        /// <summary>Подпись описания модели (SHA-256 записи без этой сетки).</summary>
        public string Signature { get; set; } = "";
        public double Volume { get; set; }
        /// <summary>Сетка (DisplayCache.Pack): сжато, base64.</summary>
        public string? Data { get; set; }
    }

    private sealed class FaceSetDto
    {
        public string? Name { get; set; }
        public string? Purpose { get; set; }
        public List<FaceRefDto> Faces { get; set; } = new();
    }

    private sealed class FaceRefDto
    {
        public double[]? Point { get; set; }
        public double[]? Normal { get; set; }
        public string? Tag { get; set; }
    }

    /// <summary>
    /// Материал в файле. Свойства для расчёта хранятся рядом с плотностью: без них симуляция
    /// начнётся с пустых полей, и деталь придётся описывать заново в другой программе.
    /// </summary>
    private sealed class MaterialDto
    {
        public string? Name { get; set; }
        public double Density { get; set; }
        public string? Designation { get; set; }
        public double[]? Colour { get; set; }

        public double YoungModulus { get; set; }
        public double PoissonRatio { get; set; }
        public double ShearModulus { get; set; }
        public double YieldStrength { get; set; }
        public double UltimateStrength { get; set; }
        public double TangentModulus { get; set; }
        public double ThermalExpansion { get; set; }
        public double ThermalConductivity { get; set; }
        public double SpecificHeat { get; set; }

        public double Specular { get; set; } = 0.25;
        public double Shininess { get; set; } = 48;
        public double Opacity { get; set; } = 1;
        public bool? Metallic { get; set; }
        public string? Group { get; set; }

        public double[][]? StressStrain { get; set; }
        public double[][]? FatigueCurve { get; set; }
    }

    private static MaterialDto Save(Material m) => new()
    {
        Name = m.Name,
        Density = m.Density,
        Designation = m.Designation,
        Colour = new[] { m.R, m.G, m.B },
        YoungModulus = m.YoungModulus,
        PoissonRatio = m.PoissonRatio,
        ShearModulus = m.ShearModulus,
        YieldStrength = m.YieldStrength,
        UltimateStrength = m.UltimateStrength,
        TangentModulus = m.TangentModulus,
        ThermalExpansion = m.ThermalExpansion,
        ThermalConductivity = m.ThermalConductivity,
        SpecificHeat = m.SpecificHeat,
        Specular = m.Specular,
        Shininess = m.Shininess,
        Opacity = m.Opacity,
        Metallic = m.Metallic,
        Group = m.Group,
        StressStrain = m.StressStrain.Select(p => new[] { p.Strain, p.Stress }).ToArray(),
        FatigueCurve = m.FatigueCurve.Select(p => new[] { p.Cycles, p.Stress }).ToArray()
    };

    private static Material Load(MaterialDto d)
    {
        var m = new Material(d.Name ?? "Сталь", d.Density)
        {
            Designation = d.Designation ?? "",
            YoungModulus = d.YoungModulus,
            PoissonRatio = d.PoissonRatio,
            ShearModulus = d.ShearModulus,
            YieldStrength = d.YieldStrength,
            UltimateStrength = d.UltimateStrength,
            TangentModulus = d.TangentModulus,
            ThermalExpansion = d.ThermalExpansion,
            ThermalConductivity = d.ThermalConductivity,
            SpecificHeat = d.SpecificHeat,
            Specular = (float)d.Specular,
            Shininess = (float)d.Shininess,
            Opacity = d.Opacity,
            Metallic = d.Metallic ?? (Material.FromLibrary(d.Name ?? "")?.Metallic ?? false),
            Group = d.Group ?? Material.FromLibrary(d.Name ?? "")?.Group ?? "",
        };

        if (d.Colour is { Length: >= 3 }) { m.R = d.Colour[0]; m.G = d.Colour[1]; m.B = d.Colour[2]; }

        foreach (var p in d.StressStrain ?? Array.Empty<double[]>())
            if (p.Length >= 2) m.StressStrain.Add((p[0], p[1]));

        foreach (var p in d.FatigueCurve ?? Array.Empty<double[]>())
            if (p.Length >= 2) m.FatigueCurve.Add((p[0], p[1]));

        return m;
    }

    private sealed class InfoDto
    {
        public string? Code { get; set; }
        public string? Title { get; set; }
        public string? Author { get; set; }
        public string? Checker { get; set; }
        public string? Company { get; set; }
        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }
        public string? Revision { get; set; }
        public string? Note { get; set; }
        public string? Uom { get; set; }
        public string? Units { get; set; }
        public string? Tolerances { get; set; }
    }

    private sealed class ConfigDto
    {
        public string Name { get; set; } = "";
        public string? Comment { get; set; }
        public Dictionary<string, double> Parameters { get; set; } = new();
        public List<string> Suppressed { get; set; } = new();
        public List<string> Hidden { get; set; } = new();
    }

    private sealed class ParamDto
    {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "Real";
        public double Number { get; set; }
        public string? Formula { get; set; }
        public string? Unit { get; set; }
        public string? Comment { get; set; }
    }

    static readonly bool SketchSlotOff = Environment.GetEnvironmentVariable("P3D_SKETCHSLOT") == "0";

    private sealed class SketchDto
    {
        public uint Id { get; set; }
        public string Name { get; set; } = "эскиз";
        public double[] Plane { get; set; } = Array.Empty<double>();
        /// <summary>KRN-08: имя грани, на которой стоит эскиз.</summary>
        public string? BaseFaceTag { get; set; }
        /// <summary>Настройка эскиза: плоскость-базис (имя, ид, рамка без поворота «верха»), «верх», нулевая точка.</summary>
        public string? PlaneRef { get; set; }
        public uint BasePlaneId { get; set; }
        public double[]? BaseFrame { get; set; }
        public double[]? Up { get; set; }
        public string? UpRef { get; set; }
        public string? OriginRef { get; set; }
        public double[]? OriginBase { get; set; }
        public double[]? OriginOffset { get; set; }
        public List<EntityDto> Entities { get; set; } = new();
        public List<ConstraintDto> Constraints { get; set; } = new();
        public List<DimensionDto> Dimensions { get; set; } = new();
        /// <summary>k109: место в дереве — сколько операций перед эскизом (null — файл до k109).</summary>
        public int? Slot { get; set; }
        /// <summary>k116: видимость, заданная пользователем (глаз в дереве); null — по умолчанию программы.</summary>
        public bool? Shown { get; set; }
    }

    static readonly bool SplineSaveOff = Environment.GetEnvironmentVariable("P3D_SKSPLINESAVE") == "0";

    static readonly bool RoleSaveOff = Environment.GetEnvironmentVariable("P3D_SKROLESAVE") == "0";

    private sealed class EntityDto
    {
        public int? Role { get; set; }
        public bool? Fixed { get; set; }
        public string? Src { get; set; }
        public uint Id { get; set; }
        public string Kind { get; set; } = "point";
        public double X { get; set; }
        public double Y { get; set; }
        public double R { get; set; }
        public uint A { get; set; }
        public uint B { get; set; }
        public uint C { get; set; }
        public bool CCW { get; set; } = true;
        public List<uint>? Pts { get; set; }
        public double R2 { get; set; }
        public double Rot { get; set; }
        public double A0 { get; set; }
        public double A1 { get; set; }
    }

    private sealed class ConstraintDto
    {
        public string Type { get; set; } = "";
        public List<uint> Items { get; set; } = new();
        public bool Enabled { get; set; } = true;
    }

    private sealed class DimensionDto
    {
        public string Type { get; set; } = "";
        public int? Ix { get; set; }
        public List<uint> Items { get; set; } = new();
        public double Value { get; set; }
        public bool Locked { get; set; }
        public bool Reference { get; set; }
        public string? Formula { get; set; }
        public string? Note { get; set; }
        public bool? IsDefault { get; set; }
        public bool? UserPlaced { get; set; }
        public double[]? Offset { get; set; }
        public string? Arrows { get; set; }
        public bool? UserLocked { get; set; }
        public bool? SingleArrow { get; set; }
        public bool? AsLinear { get; set; }
    }

    /// <summary>По выходе из области присваивает ключ операции, добавленной в деталь внутри неё.</summary>
    private readonly struct KeyOnAdd(Part part, HashSet<Feature> known, string key, Kernel.OperationPolicy? policy, string? group = null,
                                     string? ff = null, string? ffBody = null, bool frozen = false) : IDisposable
    {
        public void Dispose()
        {
            foreach (var f in part.Features)
                if (!known.Contains(f))
                {
                    f.Key = key; f.Policy = policy; if (group != null) f.Group = group;
                    f.FreeForm = ff; f.FFBody = ffBody; f.Frozen = frozen;
                }
        }
    }

    /// <summary>KRN-08 (08.3): селекторы граней в документе (Kind — FaceSelectorKind, Other — роль).</summary>
    private static List<SelectorDto>? FaceSel(List<FaceSelector> list) =>
        list.Count == 0 ? null : list.Select(x => new SelectorDto { Kind = x.Kind.ToString(), Feature = x.FeatureKey, Other = x.Role }).ToList();
    private static IEnumerable<FaceSelector> FaceSel(List<SelectorDto>? list)
    {
        foreach (var sd in list ?? new())
            if (Enum.TryParse<FaceSelectorKind>(sd.Kind, out var k) && sd.Feature != null) yield return new FaceSelector(k, sd.Feature, sd.Other);
    }

    private sealed class SelectorDto
    {
        public string Kind { get; set; } = "";
        public string? Feature { get; set; }
        public string? Other { get; set; }
    }

    private sealed class FeatureDto
    {
        /// <summary>K05: постоянный ключ операции; у документов до K05 отсутствует.</summary>
        public string? Key { get; set; }
        /// <summary>Отдельные значения и формулы (фаска вершины: размер на каждое ребро).</summary>
        public List<double>? Values { get; set; }
        public List<string?>? Formulas { get; set; }
        /// <summary>K05: устойчивые имена выбранных граней (оболочка, уклон).</summary>
        public List<string?>? FaceNames { get; set; }
        /// <summary>K02: своя политика операции; null — как у детали.</summary>
        public string? Policy { get; set; }
        /// <summary>u13: числа, строки и флаги новых операций по имени (без отдельного поля на каждое).</summary>
        public Dictionary<string, double>? Num { get; set; }
        public Dictionary<string, string?>? Str { get; set; }
        public List<string>? Flags { get; set; }
        /// <summary>u13k: SubD-клетка шага FreeForm (SubDCage.Pack).</summary>
        public List<double>? Cage { get; set; }
        /// <summary>u13j: NURBS-поверхность шага FreeForm (NurbsDef.Pack).</summary>
        public List<double>? Nurbs { get; set; }
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Suppressed { get; set; }
        public uint SketchId { get; set; }
        public double Distance { get; set; }
        public string? Formula { get; set; }
        public bool Flip { get; set; }
        public bool TwoSided { get; set; }
        public bool Symmetric { get; set; }
        public bool Cut { get; set; }
        public uint AxisId { get; set; }
        public uint AxisEntityId { get; set; }
        public double StartAngle { get; set; }
        public string? StartFormula { get; set; }
        public double EndAngle { get; set; } = 360;
        public string? EndFormula { get; set; }
        public uint PathSketchId { get; set; }
        public uint PathLineId { get; set; }
        public double Distance2 { get; set; }
        public string? Formula2 { get; set; }
        public string ChamferMode { get; set; } = "TwoDistances";
        public List<EdgeDto> Edges { get; set; } = new();
        public List<SelectorDto>? Selectors { get; set; }
        public List<SelectorDto>? FaceSelectors { get; set; }
        public string? VertexKey { get; set; }
        public List<double> OpenFaces { get; set; } = new();
        /// <summary>KRN-06 (06.14): радиус скругления в конце ребра (переменный радиус); null — постоянный.</summary>
        public double? RadiusEnd { get; set; }
        /// <summary>u12: зеркало и массивы операций — ключи повторяемых операций.</summary>
        public List<string>? Sources { get; set; }
        /// <summary>KRN-07 (07.3): закрутка протяжки, градусы.</summary>
        public double? Twist { get; set; }
        public string? TwistFormula { get; set; }
        public bool? KeepOrientation { get; set; }
        public string? RadiusEndFormula { get; set; }
        /// <summary>KRN-06 (06.2): закон радиуса по точкам (доли длины и радиусы, формулы радиусов).</summary>
        public List<double>? LawF { get; set; }
        public List<double>? LawR { get; set; }
        public List<string?>? LawRFormulas { get; set; }
        /// <summary>KRN-07 (07.5): точки граней со своей толщиной оболочки (толщины — в Values/Formulas).</summary>
        public List<double>? ThickFaces { get; set; }
        public double[]? Point { get; set; }
        public double[]? Dir { get; set; }
        public List<uint> Sections { get; set; } = new();
        public int Samples { get; set; } = 64;
        public bool Smooth { get; set; }
        /// <summary>KRN-07 (07.4): эскизы направляющих лофта.</summary>
        public List<uint>? Guides { get; set; }
        /// <summary>KRN-07 (07.4): касательные лофта у крайних сечений.</summary>
        public double StartTangent { get; set; }
        public double EndTangent { get; set; }
        public string HoleKindName { get; set; } = "Simple";
        public bool ThroughAll { get; set; }
        public double HeadDiameter { get; set; } = 12;
        public double HeadDepth { get; set; } = 4;
        public double HeadAngle { get; set; } = 90;
        public string? DatumBy { get; set; }
        public string? BasePlane { get; set; }
        public string? SecondPlane { get; set; }
        public string? BaseAxis { get; set; }
        public string? FaceTag { get; set; }
    }

    private sealed class EdgeDto
    {
        public double Sx { get; set; }
        public double Sy { get; set; }
        public double Sz { get; set; }
        public double Ex { get; set; }
        public double Ey { get; set; }
        public double Ez { get; set; }
        public double? Mx { get; set; }
        public double? My { get; set; }
        public double? Mz { get; set; }
        public string? FaceA { get; set; }
        public string? FaceB { get; set; }
        public string? End0 { get; set; }
        public string? End1 { get; set; }
        public string? Piece { get; set; }
    }
}
