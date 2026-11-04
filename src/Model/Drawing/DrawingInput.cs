using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model.Drawing;

/// <summary>
/// k156 (D2): ИСТОЧНИК ЧЕРТЕЖА — тела в системе координат модели и ключ детали каждого тела (для позиций), деталь (поля
/// надписи, размеры модели) и номера позиций из перечня сборки. Деталь — её тела как есть; сборка — тела всех деталей
/// (кроме скелетов, подавленных и ниже строки отката) в своих положениях, вложенные сборки — с положением сборки.
/// </summary>
public sealed class DrawingInput
{
    public readonly List<TSolid> Bodies = new();
    public readonly List<string> Items = new();
    public Part? Part;
    public Dictionary<string, int>? Positions;
    public bool IsAssembly;

    public static DrawingInput FromPart(Part p)
    {
        var d = new DrawingInput { Part = p };
        d.Bodies.AddRange(p.Bodies);
        return d;
    }

    public static DrawingInput FromAssembly(Assembly asm)
    {
        var d = new DrawingInput { IsAssembly = true };
        void Walk(Assembly a, Matrix4d parent)
        {
            foreach (var c in a.Children)
            {
                if (c is Part p)
                {
                    if (p.Skeleton || p.Suppressed || a.IsBeyondStage(p)) continue;
                    var m = ToD(p.LocalTransform) * parent;
                    bool identity = (m - Matrix4d.Identity).Row0.LengthSquared + (m - Matrix4d.Identity).Row1.LengthSquared + (m - Matrix4d.Identity).Row2.LengthSquared + (m - Matrix4d.Identity).Row3.LengthSquared < 1e-24;
                    var x = new TransformOp.Xform(q => Vector3d.TransformPosition(q, m), v => Vector3d.TransformVector(v, m), m.Determinant < 0);
                    foreach (var b in p.Bodies)
                    {
                        d.Bodies.Add(identity ? b : TransformOp.Copy(b, x));
                        d.Items.Add(BomDocument.KeyOf(p));
                    }
                }
                else if (c is Assembly sa) Walk(sa, ToD(sa.LocalTransform) * parent);
            }
        }
        Walk(asm, Matrix4d.Identity);
        var bom = new BomDocument { Expand = true };
        bom.UpdateFrom(asm);
        d.Positions = bom.Rows.Where(r => r.Key.Length > 0 && r.Position > 0).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First().Position);
        return d;
    }

    static Matrix4d ToD(Matrix4 m) => new(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);

    /// <summary>Из файла детали (.p3d) или сборки (.p3a): тело из файла, иначе перестроение.</summary>
    public static DrawingInput FromFile(string path)
    {
        string text = File.ReadAllText(path);
        if (path.EndsWith(".p3a", StringComparison.OrdinalIgnoreCase))
        {
            var (asm, pars) = AssemblyFile.Load(text);
            foreach (var p in asm.Parts) if (!p.RestoredFromFile && p.IsDirty) p.Rebuild(pars);
            return FromAssembly(asm);
        }
        var (part, ps) = DocumentFile.Load(text);
        if (!part.RestoredFromFile) part.Rebuild(ps);
        return FromPart(part);
    }

    /// <summary>Отпечаток тел (для метки «устарел»).</summary>
    public static string Signature(IEnumerable<TSolid> bodies) => string.Join(";", bodies.Select(b => $"{b.Faces.Count()}/{b.Edges.Count()}/{b.Id}"));
}
