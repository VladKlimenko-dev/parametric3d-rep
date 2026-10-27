using System.IO.Compression;
using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.MeshFit;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model.Features;

/// <summary>
/// ИМПОРТ СЕТКИ (STL) — операция дерева (k119). Хранит треугольники файла (float, сжато) и настройки перевода: точная
/// геометрия (плоскости, цилиндры, конусы, сферы, торы) или плоские грани, выравнивание по замыслу, допуск, масштаб.
/// Перевод (MeshToBrep) — при перестроении, если изменились треугольники или настройки; итог (текст STEP) хранится
/// в документе, поэтому открытие файла не переводит сетку заново. Правка операции — другие настройки, тот же файл.
/// </summary>
public sealed class MeshImportFeature : Feature
{
    public string SourceName = "";
    /// <summary>Треугольники: 9 float на треугольник, gzip, base64.</summary>
    public string MeshData = "";
    public bool Exact = true, Snap = true;
    public double TolScale = 1, Scale = 1;
    /// <summary>Итог перевода (STEP) и ключ настроек, при которых он получен.</summary>
    public string Step = "", StepKey = "", Report = "";
    private List<TSolid>? _cache;
    private string? _cacheText;

    public MeshImportFeature(string name = "Mesh import") { Name = name; }
    public override string Summary => $"STL «{SourceName}»: {(Exact ? "точная геометрия" : "плоские грани")}" + (Report.Length > 0 ? " — " + Report : "");

    public string Key => $"{MeshData.Length}:{Hash(MeshData)}|{Exact}|{Snap}|{TolScale:R}|{Scale:R}";

    static string Hash(string s)
    {
        ulong h = 1469598103934665603UL;
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h.ToString("x16");
    }

    public static string Pack(IReadOnlyList<(Vector3d A, Vector3d B, Vector3d C)> tris)
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        using (var w = new BinaryWriter(gz))
        {
            w.Write(tris.Count);
            foreach (var (a, b, c) in tris)
                foreach (var p in new[] { a, b, c }) { w.Write((float)p.X); w.Write((float)p.Y); w.Write((float)p.Z); }
        }
        return Convert.ToBase64String(ms.ToArray());
    }

    public static List<(Vector3d A, Vector3d B, Vector3d C)> Unpack(string data)
    {
        var res = new List<(Vector3d, Vector3d, Vector3d)>();
        if (string.IsNullOrEmpty(data)) return res;
        using var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(data)), CompressionMode.Decompress);
        using var r = new BinaryReader(gz);
        int n = r.ReadInt32();
        Vector3d P() => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        for (int i = 0; i < n; i++) res.Add((P(), P(), P()));
        return res;
    }

    /// <summary>Перевести сетку заново, если итог не соответствует настройкам; true — переведено сейчас.</summary>
    public bool EnsureConverted(List<string>? warnings = null)
    {
        string key = Key;
        if (Step.Length > 0 && StepKey == key) return false;
        var tris = Unpack(MeshData);
        var r = MeshToBrep.Convert(tris, new MeshFitOptions { Exact = Exact, Snap = Snap, TolScale = TolScale, Scale = Scale }, SourceName);
        if (r.Step == null) throw new RebuildException(OperationOutcome.InvalidInput, "STL: " + r.Summary);
        Step = r.Step; StepKey = key; Report = r.Summary;
        LastResult = r;
        if (r.Notes.Count > 0) warnings?.Add("STL: " + string.Join("; ", r.Notes));
        return true;
    }

    /// <summary>Итог последнего перевода в этом сеансе (для показа).</summary>
    public MeshFitResult? LastResult;

    static readonly bool Log = Environment.GetEnvironmentVariable("P3D_MESHFITLOG") == "1";

    public override void Execute(BuildContext ctx)
    {
        if (string.IsNullOrEmpty(MeshData)) throw new RebuildException(OperationOutcome.InvalidInput, "STL: no triangles.");
        if (Log) Console.Error.WriteLine($"MESHIMPORT execute start {DateTime.Now:HH:mm:ss.fff}");
        EnsureConverted(ctx.Warnings);
        if (Log) Console.Error.WriteLine($"MESHIMPORT converted {DateTime.Now:HH:mm:ss.fff}: {Report}");
        if (_cache == null || !ReferenceEquals(_cacheText, Step))
        {
            var w = new List<string>();
            _cache = Kernel.Export.StepImport.ReadExactSolids(Step, w);
            _cacheText = Step;
            if (_cache.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, "STL: the converted body cannot be read.");
        }
        foreach (var b0 in _cache)
        {
            var b = TopologyClone.Deep(b0);
            if (Cut) BooleanOp.Subtract(ctx.Bodies, b, ctx.Warnings);
            else if (ctx.Bodies.Count == 0) ctx.Bodies.Add(b);
            else BooleanOp.Unite(ctx.Bodies, b, ctx.Warnings);
        }
    }
}
