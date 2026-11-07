using System.Buffers.Binary;
using System.Text;
using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Export.Parasolid;

/// <summary>Узел файла Parasolid XT: тип, имя типа, номер и поля по именам схемы.</summary>
public sealed class XtNode
{
    public int Type;
    public string Name = "";
    public int Id;
    public readonly Dictionary<string, object?> F = new();

    public int P(string field) => F.TryGetValue(field, out var v) && v is int i ? i : 0;
    public int[] PA(string field) => F.TryGetValue(field, out var v) && v is int[] a ? a : Array.Empty<int>();
    public double D(string field) => F.TryGetValue(field, out var v) ? v switch { double d => d, int i => i, _ => double.NaN } : double.NaN;
    public double[] DA(string field) => F.TryGetValue(field, out var v) && v is double[] a ? a : Array.Empty<double>();
    public Vector3d V(string field) => F.TryGetValue(field, out var v) && v is Vector3d x ? x : Vector3d.Zero;
    public Vector3d[] VA(string field) => F.TryGetValue(field, out var v) && v is Vector3d[] a ? a : Array.Empty<Vector3d>();
    public char C(string field) => F.TryGetValue(field, out var v) && v is char c ? c : '\0';
    public bool L(string field) => F.TryGetValue(field, out var v) && v is bool b && b;
    public int I(string field) => F.TryGetValue(field, out var v) ? v switch { int i => i, char c => c, bool b => b ? 1 : 0, _ => 0 } : 0;
    public int[] IA(string field) => F.TryGetValue(field, out var v) && v is int[] a ? a : Array.Empty<int>();
    public override string ToString() => $"{Name}#{Id}";
}

/// <summary>Прочитанный файл XT: заголовок и узлы по номерам (номер 1 — пустой указатель).</summary>
public sealed class XtFile
{
    public string Modeller = "", Schema = "";
    public readonly Dictionary<int, XtNode> Nodes = new();
    public readonly List<XtNode> Order = new();
    public bool Truncated;
    public XtNode? this[int id] => id > 1 && Nodes.TryGetValue(id, out var n) ? n : null;
    public IEnumerable<XtNode> OfType(string name) => Order.Where(n => n.Name == name);
}

/// <summary>
/// ЧТЕНИЕ PARASOLID XT в нейтральном двоичном виде (.x_b; поток внутри SLDPRT). Устройство — по Parasolid XT Format Reference
/// (раздел «Physical layout / Binary», «Embedded schemas»): после заголовка («PS\0\0», версия модельера, имя схемы, наибольший
/// номер типа, размер полей пользователя) — узлы: тип (2 байта); при первом узле типа — отличие его полей от базовой схемы
/// SCH_13006 (255 — совпадает; иначе правка C/D/I/A…Z; новый тип — описание полей); у узла переменной длины — длина (4 байта);
/// номер (указатель); поля по схеме. Числа — big-endian, IEEE. Узел типа 1 — конец. Поступь — как у ps-parser (MIT) и
/// sldprt-re: те же правила разбора проверены ими на файлах Parasolid 30–35.
/// </summary>
public static class XtReader
{
    public static XtFile Read(byte[] buf, bool partial = true)
    {
        var r = new R(buf);
        var f = new XtFile();
        if (buf.Length < 8 || buf[0] != (byte)'P' || buf[1] != (byte)'S') throw new InvalidDataException("XT: not a neutral binary transmit file (no 'PS' header).");
        r.P = 4;
        int ml = r.I16(); f.Modeller = r.Str(ml);
        int sl = r.I32(); f.Schema = r.Str(sl);
        bool embedded = f.Schema.Split('_').Length >= 4;   // SCH_<версия>_<схема>_<база>: новый вид с отличиями от базы
        if (embedded) r.I16();                             // наибольший номер типа узла
        int userField = r.I32();
        if (userField != 0) throw new InvalidDataException($"XT: user fields ({userField}) are not supported.");
        var layouts = new Dictionary<int, (string Name, bool Var, List<XtSchema13006.Field> Fields)>();
        while (true)
        {
            int start = r.P;
            try
            {
                if (r.P + 2 > buf.Length) { f.Truncated = true; return f; }
                int nt = r.I16();
                if (nt == 1) return f;
                if (!layouts.TryGetValue(nt, out var lay))
                {
                    XtSchema13006.Types.TryGetValue(nt, out var bt);
                    if (!embedded)
                    {
                        if (bt == null) throw new InvalidDataException($"XT: node type {nt} is not in the base schema.");
                        lay = (bt.Name, bt.Variable, bt.Fields.ToList());
                    }
                    else
                    {
                        int fc = r.U8();
                        if (fc == 255 && bt != null) lay = (bt.Name, bt.Variable, bt.Fields.ToList());
                        else if (bt != null)
                        {
                            var fl = new List<XtSchema13006.Field>(); int bi = 0;
                            while (true)
                            {
                                char c = (char)r.U8();
                                if (c == 'Z') break;
                                if (c == 'C') { if (bi >= bt.Fields.Length) throw new InvalidDataException($"XT: schema copy past the base fields of type {nt}"); fl.Add(bt.Fields[bi++]); }
                                else if (c == 'D') bi++;
                                else if (c is 'I' or 'A') fl.Add(Emb(r, out _));
                                else throw new InvalidDataException($"XT: bad schema edit '{c}' at {r.P - 1} (type {nt})");
                            }
                            lay = (bt.Name, bt.Variable, fl);
                        }
                        else
                        {
                            string name = r.Str(r.U8()); r.Str(r.U8());
                            var fl = new List<XtSchema13006.Field>(); bool var = false;
                            for (int k = 0; k < fc; k++) { fl.Add(Emb(r, out bool vv)); var |= vv; }
                            lay = (name, var, fl);
                        }
                    }
                    layouts[nt] = lay;
                }
                int count = lay.Var ? r.I32() : 0;
                int id = r.Ptr();
                var node = new XtNode { Type = nt, Name = lay.Name, Id = id };
                for (int k = 0; k < lay.Fields.Count; k++)
                {
                    var fd = lay.Fields[k];
                    bool last = k == lay.Fields.Count - 1;
                    if (lay.Var && last && fd.N == 1)
                        node.F[fd.Name] = fd.Code == 'c' ? r.Str(count) : Arr(r, fd.Code, count);
                    else if (fd.N > 1) node.F[fd.Name] = Arr(r, fd.Code, fd.N);
                    else node.F[fd.Name] = Val(r, fd.Code);
                }
                f.Nodes[id] = node; f.Order.Add(node);
            }
            catch (Exception ex) when (partial && ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
            {
                f.Truncated = true; r.P = start; return f;
            }
        }
    }

    static XtSchema13006.Field Emb(R r, out bool var)
    {
        string name = r.Str(r.U8());
        int pc = r.I16();
        int n = r.I16();
        char code = pc != 0 ? 'p' : r.Str(r.U8()) is { Length: > 0 } s ? s[0] : '?';
        var = false;
        if (n == 2) var = r.U8() != 0;
        int nn = n > 2 ? n - 1 : n == 2 ? 1 : 0;
        return new XtSchema13006.Field(name, code, var ? 1 : nn, pc);
    }

    static object Arr(R r, char code, int n) => code switch
    {
        'p' => Enumerable.Range(0, n).Select(_ => r.Ptr()).ToArray(),
        'f' => Enumerable.Range(0, n).Select(_ => r.F64()).ToArray(),
        'd' => Enumerable.Range(0, n).Select(_ => r.I32()).ToArray(),
        'n' or 'w' => Enumerable.Range(0, n).Select(_ => r.I16()).ToArray(),
        'u' => Enumerable.Range(0, n).Select(_ => r.U8()).ToArray(),
        'l' => Enumerable.Range(0, n).Select(_ => r.U8() != 0 ? 1 : 0).ToArray(),
        'c' => Enumerable.Range(0, n).Select(_ => (int)r.U8()).ToArray(),
        'v' or 'h' => Enumerable.Range(0, n).Select(_ => new Vector3d(r.F64(), r.F64(), r.F64())).ToArray(),
        'i' => Enumerable.Range(0, 2 * n).Select(_ => r.F64()).ToArray(),
        'b' => Enumerable.Range(0, 6 * n).Select(_ => r.F64()).ToArray(),
        _ => throw new InvalidDataException($"XT: field code '{code}'")
    };

    static object Val(R r, char code) => code switch
    {
        'u' => r.U8(),
        'c' => (char)r.U8(),
        'l' => r.U8() != 0,
        'n' or 'w' => r.I16(),
        'd' => r.I32(),
        'p' => r.Ptr(),
        'f' => r.F64(),
        'i' => new[] { r.F64(), r.F64() },
        'v' or 'h' => new Vector3d(r.F64(), r.F64(), r.F64()),
        'b' => new[] { r.F64(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64() },
        _ => throw new InvalidDataException($"XT: field code '{code}'")
    };

    sealed class R
    {
        readonly byte[] b; public int P;
        public R(byte[] b) { this.b = b; }
        public int U8() => b[P++];
        public int I16() { int v = BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(P, 2)); P += 2; return v; }
        public int I32() { int v = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(P, 4)); P += 4; return v; }
        public double F64() { double v = BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(P, 8)); P += 8; return v; }
        public string Str(int n) { var s = Encoding.Latin1.GetString(b, P, n); P += n; return s; }
        /// <summary>Указатель (номер узла): 0..32766 — одно короткое (номер + 1); больше — пара (−остаток, частное).
        /// Возвращается номер + 1 (так 1 — пустой указатель, как в потоке).</summary>
        public int Ptr()
        {
            int rr = I16();
            if (rr >= 0) return rr;
            int q = I16();
            return q * 32767 - rr;
        }
    }
}
