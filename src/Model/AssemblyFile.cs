using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTK.Mathematics;
using Parametric3D.Model.Mates;
using Parametric3D.Model.Params;

namespace Parametric3D.Model;

/// <summary>
/// СОХРАНЕНИЕ И ОТКРЫТИЕ СБОРКИ. Деталь сохранять мы научились, а узел — нет: сопряжения,
/// цепочки и проверка пересечений жили только в памяти.
///
/// Каждая деталь хранится своим документом (тем же, что и отдельная деталь), а сборка добавляет
/// к этому положение деталей и связи. Базовая деталь связи хранится НОМЕРОМ В СПИСКЕ, а не
/// ссылкой: при чтении ссылки восстанавливаются по этому номеру.
/// </summary>
public static class AssemblyFile
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Save(Assembly asm, ParameterTable? parameters = null)
    {
        var parts = asm.Parts.ToList();

        var dto = new AsmDto
        {
            Version = CurrentVersion,
            Name = asm.Name,
            Units = asm.Units.ToDictionary(),
            Explodes = asm.ExplodedViews.Select(v => new ExplodeDto
            {
                Name = v.Name,
                Steps = v.Steps.Select(st => new ExplodeStepDto
                {
                    PartName = st.PartName,
                    Direction = new[] { st.Direction.X, st.Direction.Y, st.Direction.Z },
                    Distance = st.Distance,
                    Order = st.Order
                }).ToList()
            }).ToList(),

            Parts = parts.Select(p => new AsmPartDto
            {
                Document = DocumentFile.Save(p, parameters),
                Translation = new[] { (double)p.Translation.X, p.Translation.Y, p.Translation.Z },
                Rotation = new[] { (double)p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W },
                Fixed = p.Fixed, Skeleton = p.Skeleton, SourcePath = p.SourcePath, Hidden = !p.Visible, SubAssembly = p.SubAssembly,
                Constraints = p.Constraints.Select(c => new ConstraintDto
                {
                    Kind = c.Kind.ToString(),
                    Name = c.Name,
                    BasePart = c.BasePart == null ? -1 : parts.IndexOf(c.BasePart),
                    AssemblyOrigin = new[] { c.AssemblyOrigin.X, c.AssemblyOrigin.Y, c.AssemblyOrigin.Z },
                    AssemblyAxis = new[] { c.AssemblyAxis.X, c.AssemblyAxis.Y, c.AssemblyAxis.Z },
                    PartOrigin = new[] { c.PartOrigin.X, c.PartOrigin.Y, c.PartOrigin.Z },
                    PartAxis = new[] { c.PartAxis.X, c.PartAxis.Y, c.PartAxis.Z },
                    Offset = c.Offset,
                    Angle = c.Angle,
                    Enabled = c.Enabled
                }).ToList(),

                Mates = p.Mates.Select(m => new MateDto
                {
                    Type = m.Type.ToString(),
                    Name = m.Name,
                    BasePart = m.BasePart == null ? -1 : parts.IndexOf(m.BasePart),
                    AssemblyOrigin = new[] { m.AssemblyOrigin.X, m.AssemblyOrigin.Y, m.AssemblyOrigin.Z },
                    AssemblyAxis = new[] { m.AssemblyAxis.X, m.AssemblyAxis.Y, m.AssemblyAxis.Z },
                    PartOrigin = new[] { m.PartOrigin.X, m.PartOrigin.Y, m.PartOrigin.Z },
                    PartAxis = new[] { m.PartAxis.X, m.PartAxis.Y, m.PartAxis.Z },
                    Angle = m.Angle,
                    Offset = m.Offset,
                    LimitEnabled = m.LimitEnabled,
                    MinValue = double.IsInfinity(m.MinValue) ? null : m.MinValue,
                    MaxValue = double.IsInfinity(m.MaxValue) ? null : m.MaxValue,
                    PartFaceTag = m.PartFaceTag,
                    BaseFaceTag = m.BaseFaceTag,
                    SlotNormal = m.SlotNormal.LengthSquared > 0 ? new[] { m.SlotNormal.X, m.SlotNormal.Y, m.SlotNormal.Z } : null
                }).ToList()
            }).ToList()
        };

        return JsonSerializer.Serialize(dto, Opts);
    }

    public static (Assembly Asm, ParameterTable Parameters) Load(string json)
    {
        var dto = JsonSerializer.Deserialize<AsmDto>(json, Opts)
                  ?? throw new InvalidDataException("файл сборки не разобран");
        if (dto.Version > CurrentVersion)
            throw new InvalidDataException($"файл записан более новой сборкой (вид {dto.Version})");

        var asm = new Assembly(dto.Name) { Units = Core.UnitSystem.FromDictionary(dto.Units) };
        var table = new ParameterTable { Units = asm.Units };
        var parts = new List<Part>();

        foreach (var pd in dto.Parts)
        {
            var (part, t) = DocumentFile.Load(pd.Document);
            foreach (var p in t.All)          // переменные деталей сводятся в одну таблицу
                if (table.Find(p.Name) == null)
                {
                    var np = table.Add(p.Name, p.Number, p.Kind, p.Unit);
                    if (!string.IsNullOrEmpty(p.Formula)) np.Formula = p.Formula;
                }

            part.Translation = new Vector3((float)pd.Translation[0], (float)pd.Translation[1], (float)pd.Translation[2]);
            part.Rotation = new Quaternion((float)pd.Rotation[0], (float)pd.Rotation[1],
                                           (float)pd.Rotation[2], (float)pd.Rotation[3]);
            part.Fixed = pd.Fixed; part.Skeleton = pd.Skeleton; part.SourcePath = pd.SourcePath; part.Visible = !pd.Hidden; part.SubAssembly = pd.SubAssembly;
            asm.Children.Add(part);
            parts.Add(part);
        }
        table.Recalculate();

        // Ограничения — тем же вторым проходом и по той же причине.
        for (int i = 0; i < dto.Parts.Count; i++)
            foreach (var cd in dto.Parts[i].Constraints)
            {
                if (!Enum.TryParse<ConstraintKind>(cd.Kind, out var kind)) continue;
                var c = new AssemblyConstraint(kind, cd.Name)
                {
                    AssemblyOrigin = Vec(cd.AssemblyOrigin),
                    AssemblyAxis = Vec(cd.AssemblyAxis),
                    PartOrigin = Vec(cd.PartOrigin),
                    PartAxis = Vec(cd.PartAxis),
                    Offset = cd.Offset,
                    Angle = cd.Angle,
                    Enabled = cd.Enabled
                };
                if (cd.BasePart >= 0 && cd.BasePart < parts.Count) c.BasePart = parts[cd.BasePart];
                parts[i].Constraints.Add(c);
            }

        // Связи восстанавливаются вторым проходом: базовая деталь может стоять в списке позже.
        for (int i = 0; i < dto.Parts.Count; i++)
            foreach (var md in dto.Parts[i].Mates)
            {
                if (!Enum.TryParse<MateType>(md.Type, out var type)) continue;
                var m = new Mate(type, md.Name)
                {
                    AssemblyOrigin = Vec(md.AssemblyOrigin),
                    AssemblyAxis = Vec(md.AssemblyAxis),
                    PartOrigin = Vec(md.PartOrigin),
                    PartAxis = Vec(md.PartAxis),
                    Angle = md.Angle,
                    Offset = md.Offset,
                    LimitEnabled = md.LimitEnabled,
                    PartFaceTag = md.PartFaceTag,
                    BaseFaceTag = md.BaseFaceTag,
                    SlotNormal = md.SlotNormal == null ? Vector3d.Zero : Vec(md.SlotNormal)
                };
                if (md.MinValue is { } lo) m.MinValue = lo;
                if (md.MaxValue is { } hi) m.MaxValue = hi;
                if (md.BasePart >= 0 && md.BasePart < parts.Count) m.BasePart = parts[md.BasePart];
                parts[i].Mates.Add(m);
            }

        foreach (var vd in dto.Explodes)
        {
            var v = new ExplodedView(vd.Name);
            foreach (var st in vd.Steps)
                v.Steps.Add(new ExplodedView.Step
                {
                    PartName = st.PartName,
                    Direction = Vec(st.Direction),
                    Distance = st.Distance,
                    Order = st.Order
                });
            asm.ExplodedViews.Add(v);
        }

        return (asm, table);
    }

    private static Vector3d Vec(double[]? a) =>
        a is { Length: >= 3 } ? new Vector3d(a[0], a[1], a[2]) : Vector3d.Zero;

    private sealed class AsmDto
    {
        public int Version { get; set; }
        public string Name { get; set; } = "сборка";
        /// <summary>m.x4: система единиц сборки; у каждой детали — своя (в её документе).</summary>
        public Dictionary<string, string>? Units { get; set; }
        public List<AsmPartDto> Parts { get; set; } = new();
        public List<ExplodeDto> Explodes { get; set; } = new();
    }

    private sealed class ExplodeDto
    {
        public string Name { get; set; } = "";
        public List<ExplodeStepDto> Steps { get; set; } = new();
    }

    private sealed class ExplodeStepDto
    {
        public string PartName { get; set; } = "";
        public double[]? Direction { get; set; }
        public double Distance { get; set; }
        public int Order { get; set; }
    }

    private sealed class AsmPartDto
    {
        public string Document { get; set; } = "";
        public double[] Translation { get; set; } = { 0, 0, 0 };
        public double[] Rotation { get; set; } = { 0, 0, 0, 1 };
        public List<MateDto> Mates { get; set; } = new();
        public List<ConstraintDto> Constraints { get; set; } = new();
        public bool Fixed { get; set; }
        public bool Skeleton { get; set; }
        public string? SubAssembly { get; set; }
        public string? SourcePath { get; set; }
        public bool Hidden { get; set; }
    }

    private sealed class ConstraintDto
    {
        public string Kind { get; set; } = "Mate";
        public string Name { get; set; } = "";
        public int BasePart { get; set; } = -1;
        public double[]? AssemblyOrigin { get; set; }
        public double[]? AssemblyAxis { get; set; }
        public double[]? PartOrigin { get; set; }
        public double[]? PartAxis { get; set; }
        public double Offset { get; set; }
        public double Angle { get; set; }
        public bool Enabled { get; set; } = true;
    }

    private sealed class MateDto
    {
        public string Type { get; set; } = "Weld";
        public string Name { get; set; } = "";
        public int BasePart { get; set; } = -1;
        public double[]? AssemblyOrigin { get; set; }
        public double[]? AssemblyAxis { get; set; }
        public double[]? PartOrigin { get; set; }
        public double[]? PartAxis { get; set; }
        public double Angle { get; set; }
        public double Offset { get; set; }
        public bool LimitEnabled { get; set; }
        public double? MinValue { get; set; }
        public double? MaxValue { get; set; }
        public string? PartFaceTag { get; set; }
        public string? BaseFaceTag { get; set; }
        public double[]? SlotNormal { get; set; }
    }
}
