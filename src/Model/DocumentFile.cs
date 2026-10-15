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

    public static string Save(Part part, ParameterTable? parameters = null)
    {
        var doc = new DocDto
        {
            Version = CurrentVersion,
            Name = part.Name,
            Units = part.Units.ToDictionary(),
            Parameters = parameters?.All.Select(p => new ParamDto
            {
                Name = p.Name,
                Kind = p.Kind.ToString(),
                Number = p.Number,
                Formula = string.IsNullOrEmpty(p.Formula) ? null : p.Formula,
                Unit = string.IsNullOrEmpty(p.Unit) ? null : p.Unit
            }).ToList() ?? new List<ParamDto>(),
            Configurations = part.Configurations.Select(c => new ConfigDto
            {
                Name = c.Name,
                Comment = c.Comment,
                Parameters = c.Parameters.ToDictionary(k => k.Key, v => v.Value),
                Suppressed = c.SuppressedFeatures.ToList(),
                Hidden = c.HiddenParts.ToList()
            }).ToList(),
            ActiveConfiguration = part.ActiveConfiguration,

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
                Note = part.Info.Note
            },

            Sketches = part.Sketches.Select(SaveSketch).ToList(),
            // K10: операция, которую нельзя записать, — ошибка записи, а не молча выпавшая операция.
            Features = part.Features.Select(f =>
            {
                var d = SaveFeature(f) ?? throw new InvalidOperationException($"Feature '{f.Name}' ({f.GetType().Name}) cannot be saved.");
                d.Key = f.Key; d.Policy = f.Policy?.ToString(); return d;
            }).ToList()
        };
        return JsonSerializer.Serialize(doc, Opts);
    }

    private static SketchDto SaveSketch(Sketch s)
    {
        var dto = new SketchDto { Id = s.Id, Name = s.Name, Plane = new[]
        {
            s.Plane.Origin.X, s.Plane.Origin.Y, s.Plane.Origin.Z,
            s.Plane.U.X, s.Plane.U.Y, s.Plane.U.Z,
            s.Plane.V.X, s.Plane.V.Y, s.Plane.V.Z
        }, BaseFaceTag = s.BaseFaceTag };

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
            }

        foreach (var c in s.Constraints)
            dto.Constraints.Add(new ConstraintDto
            {
                Type = c.Type.ToString(),
                Items = c.Items.Select(i => i.Id).ToList(),
                Enabled = c.Enabled
            });

        foreach (var d in s.Dimensions)
            dto.Dimensions.Add(new DimensionDto
            {
                Type = d.Type.ToString(),
                Items = d.Items.Select(i => i.Id).ToList(),
                Value = d.Value,
                Locked = d.Locked,
                Reference = d.Reference
            });

        return dto;
    }

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
            Cut = e.Cut
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
            RadiusEnd = rf.RadiusEnd?.Value, RadiusEndFormula = rf.RadiusEnd?.Formula
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
            Dir = new[] { mf.Normal.X, mf.Normal.Y, mf.Normal.Z }
        },

        LinearPatternFeature lp => new FeatureDto
        {
            Kind = "linear", Name = lp.Name, Suppressed = lp.Suppressed,
            Dir = new[] { lp.Step.X, lp.Step.Y, lp.Step.Z },
            Distance = lp.Count.Value, Formula = lp.Count.Formula
        },

        CircularPatternFeature cp => new FeatureDto
        {
            Kind = "circular", Name = cp.Name, Suppressed = cp.Suppressed,
            Point = new[] { cp.At.X, cp.At.Y, cp.At.Z },
            Dir = new[] { cp.Axis.X, cp.Axis.Y, cp.Axis.Z },
            Distance = cp.Count.Value, Formula = cp.Count.Formula
        },

        DraftFeature df => new FeatureDto
        {
            Kind = "draft", Name = df.Name, Suppressed = df.Suppressed,
            Distance = df.Angle.Value, Formula = df.Angle.Formula,
            Point = new[] { df.NeutralPoint.X, df.NeutralPoint.Y, df.NeutralPoint.Z },
            Dir = new[] { df.NeutralNormal.X, df.NeutralNormal.Y, df.NeutralNormal.Z },
            OpenFaces = df.OnFaces.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToList(),
            FaceNames = df.OnFaceNames.Count > 0 ? df.OnFaceNames.ToList() : null,
            FaceSelectors = FaceSel(df.OnSelectors)
        },

        RibFeature rb => new FeatureDto
        {
            Kind = "rib", Name = rb.Name, Suppressed = rb.Suppressed,
            SketchId = rb.SketchId,
            Dir = new[] { rb.Grow.X, rb.Grow.Y, rb.Grow.Z },
            Distance = rb.Thickness.Value, Formula = rb.Thickness.Formula,
            Distance2 = rb.MaxLength.Value, Formula2 = rb.MaxLength.Formula
        },

        LoftFeature lf => new FeatureDto
        {
            Kind = "loft", Name = lf.Name, Suppressed = lf.Suppressed, Cut = lf.Cut,
            Sections = lf.SectionSketchIds.ToList(),
            Samples = lf.Samples,
            Smooth = lf.Smooth
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

        var units = Core.UnitSystem.FromDictionary(doc.Units);                  // m.x4: система единиц документа
        var table = new ParameterTable { Units = units };
        foreach (var p in doc.Parameters)
        {
            var kind = Enum.TryParse<ParamKind>(p.Kind, out var k) ? k : ParamKind.Real;
            var par = table.Add(p.Name, p.Number, kind, p.Unit ?? "");
            if (p.Formula != null) par.Formula = p.Formula;
        }
        table.Recalculate();

        var part = new Part(doc.Name) { ActiveConfiguration = doc.ActiveConfiguration ?? "", Units = units };

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
                Revision = id.Revision ?? "", Note = id.Note ?? ""
            };

        foreach (var cd in doc.Configurations)
        {
            var cfg = new Configuration(cd.Name) { Comment = cd.Comment ?? "" };
            foreach (var (k, v) in cd.Parameters) cfg.Parameters[k] = v;
            foreach (string f in cd.Suppressed) cfg.SuppressedFeatures.Add(f);
            foreach (string h in cd.Hidden) cfg.HiddenParts.Add(h);
            part.Configurations.Add(cfg);
        }
        var sketchById = new Dictionary<uint, Sketch>();

        foreach (var sd in doc.Sketches)
        {
            var frame = new Frame3(
                new Vector3d(sd.Plane[0], sd.Plane[1], sd.Plane[2]),
                new Vector3d(sd.Plane[3], sd.Plane[4], sd.Plane[5]),
                new Vector3d(sd.Plane[6], sd.Plane[7], sd.Plane[8]));
            var s = new Sketch(sd.Name, frame) { BaseFaceTag = sd.BaseFaceTag };

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
                    _ => null
                };
                if (made != null) byOld[ed.Id] = made;
                else lost.Add($"sketch '{sd.Name}': entity {ed.Id} ({ed.Kind}) not restored");
            }

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
            }

            part.Children.Add(s);
            sketchById[sd.Id] = s;
        }

        int featureIndex = 0;
        foreach (var fd in doc.Features)
        {
            var known = new HashSet<Feature>(part.Features);
            using var keyScope = new KeyOnAdd(part, known, string.IsNullOrEmpty(fd.Key) ? $"legacy{featureIndex}" : fd.Key!,
                                              fd.Policy != null && Enum.TryParse<Kernel.OperationPolicy>(fd.Policy, out var pol) ? pol : null);
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
                    part.AddFeature(w);
                    break;
                }

                case "round":
                {
                    var rf = new RoundFeature(fd.Name) { Suppressed = fd.Suppressed };
                    rf.Radius = new ValueRef(fd.Distance) { Formula = fd.Formula };
                    if (fd.RadiusEnd is double rEnd) rf.RadiusEnd = new ValueRef(rEnd) { Formula = fd.RadiusEndFormula };
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
                    part.AddFeature(mf);
                    break;
                }

                case "linear":
                {
                    var lp = new LinearPatternFeature(fd.Name) { Suppressed = fd.Suppressed, Step = Vec(fd.Dir) };
                    lp.Count = new ValueRef(fd.Distance) { Formula = fd.Formula };
                    part.AddFeature(lp);
                    break;
                }

                case "circular":
                {
                    var cp = new CircularPatternFeature(fd.Name) { Suppressed = fd.Suppressed, At = Vec(fd.Point) };
                    var ax = Vec(fd.Dir);
                    if (ax.LengthSquared > 1e-18) cp.Axis = ax;
                    cp.Count = new ValueRef(fd.Distance) { Formula = fd.Formula };
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

                case "loft":
                {
                    var lf = new LoftFeature(fd.Name) { Suppressed = fd.Suppressed, Cut = fd.Cut, Samples = fd.Samples, Smooth = fd.Smooth };
                    foreach (uint old in fd.Sections)
                        if (sketchById.TryGetValue(old, out var sk2)) lf.SectionSketchIds.Add(sk2.Id);
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

        return (part, table);
    }

    // ------------------------------------------------------------------ записи файла

    private sealed class DocDto
    {
        public int Version { get; set; }
        public string Name { get; set; } = "деталь";
        /// <summary>m.x4: система единиц документа (размерность → обозначение); нет — мм, кг, с, градус.</summary>
        public Dictionary<string, string>? Units { get; set; }
        public List<ParamDto> Parameters { get; set; } = new();
        public List<SketchDto> Sketches { get; set; } = new();
        public List<FeatureDto> Features { get; set; } = new();
        public List<ConfigDto> Configurations { get; set; } = new();
        public string? ActiveConfiguration { get; set; }
        public MaterialDto? Material { get; set; }
        public InfoDto? Info { get; set; }
        public List<FaceSetDto> FaceSets { get; set; } = new();
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
            Opacity = d.Opacity
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
    }

    private sealed class SketchDto
    {
        public uint Id { get; set; }
        public string Name { get; set; } = "эскиз";
        public double[] Plane { get; set; } = Array.Empty<double>();
        /// <summary>KRN-08: имя грани, на которой стоит эскиз.</summary>
        public string? BaseFaceTag { get; set; }
        public List<EntityDto> Entities { get; set; } = new();
        public List<ConstraintDto> Constraints { get; set; } = new();
        public List<DimensionDto> Dimensions { get; set; } = new();
    }

    private sealed class EntityDto
    {
        public uint Id { get; set; }
        public string Kind { get; set; } = "point";
        public double X { get; set; }
        public double Y { get; set; }
        public double R { get; set; }
        public uint A { get; set; }
        public uint B { get; set; }
        public uint C { get; set; }
        public bool CCW { get; set; } = true;
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
        public List<uint> Items { get; set; } = new();
        public double Value { get; set; }
        public bool Locked { get; set; }
        public bool Reference { get; set; }
    }

    /// <summary>По выходе из области присваивает ключ операции, добавленной в деталь внутри неё.</summary>
    private readonly struct KeyOnAdd(Part part, HashSet<Feature> known, string key, Kernel.OperationPolicy? policy) : IDisposable
    {
        public void Dispose()
        {
            foreach (var f in part.Features)
                if (!known.Contains(f)) { f.Key = key; f.Policy = policy; }
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
        /// <summary>KRN-07 (07.3): закрутка протяжки, градусы.</summary>
        public double? Twist { get; set; }
        public string? TwistFormula { get; set; }
        public bool? KeepOrientation { get; set; }
        public string? RadiusEndFormula { get; set; }
        /// <summary>KRN-07 (07.5): точки граней со своей толщиной оболочки (толщины — в Values/Formulas).</summary>
        public List<double>? ThickFaces { get; set; }
        public double[]? Point { get; set; }
        public double[]? Dir { get; set; }
        public List<uint> Sections { get; set; } = new();
        public int Samples { get; set; } = 64;
        public bool Smooth { get; set; }
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
