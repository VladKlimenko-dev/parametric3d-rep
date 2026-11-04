using System.Globalization;
using System.Text;

namespace Parametric3D.Model.Drawing;

/// <summary>
/// k156 (D5): ВЫВОД ЛИСТА В DXF (AutoCAD R12, ASCII — читают все программы). Единицы — мм листа. Слои по линиям ГОСТ 2.303:
/// THICK (основная 0.7), THIN (тонкая 0.25), HIDDEN (штриховая, тип линии HIDDEN), CENTER (штрихпунктирная, CENTER), TEXT;
/// заливки (стрелки) — SOLID, окружности — CIRCLE, тексты — TEXT с высотой, поворотом и выравниванием. Несколько листов —
/// рядом слева направо с промежутком 20 мм.
/// </summary>
public static class DxfWriter
{
    static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    static string Layer(LineKind k) => k switch { LineKind.Thick => "THICK", LineKind.Hidden => "HIDDEN", LineKind.Center => "CENTER", _ => "THIN" };

    public static string Write(IEnumerable<SheetGraphics> pages)
    {
        var sb = new StringBuilder();
        void P(int code, string v) { sb.Append(code).Append('\n').Append(v).Append('\n'); }
        void Pd(int code, double v) => P(code, N(v));
        P(0, "SECTION"); P(2, "HEADER");
        P(9, "$ACADVER"); P(1, "AC1009");
        P(9, "$INSUNITS"); P(70, "4");
        P(9, "$LTSCALE"); Pd(40, 1);
        P(0, "ENDSEC");
        P(0, "SECTION"); P(2, "TABLES");
        P(0, "TABLE"); P(2, "LTYPE"); P(70, "3");
        void Lt(string name, string desc, double[] pat)
        {
            P(0, "LTYPE"); P(2, name); P(70, "0"); P(3, desc); P(72, "65"); P(73, pat.Length.ToString(CultureInfo.InvariantCulture)); Pd(40, pat.Sum(Math.Abs));
            foreach (var d in pat) Pd(49, d);
        }
        Lt("CONTINUOUS", "Solid line", Array.Empty<double>());
        Lt("HIDDEN", "__ __ __", new[] { 4.0, -1.5 });
        Lt("CENTER", "____ . ____", new[] { 15.0, -2, 0.5, -2 });
        P(0, "ENDTAB");
        P(0, "TABLE"); P(2, "LAYER"); P(70, "5");
        foreach (var (name, lt, color) in new[] { ("THICK", "CONTINUOUS", 7), ("THIN", "CONTINUOUS", 7), ("HIDDEN", "HIDDEN", 7), ("CENTER", "CENTER", 1), ("TEXT", "CONTINUOUS", 7) })
        { P(0, "LAYER"); P(2, name); P(70, "0"); P(62, color.ToString(CultureInfo.InvariantCulture)); P(6, lt); }
        P(0, "ENDTAB");
        P(0, "ENDSEC");
        P(0, "SECTION"); P(2, "ENTITIES");
        double dx = 0;
        foreach (var g in pages)
        {
            void Line(double x1, double y1, double x2, double y2, LineKind k)
            {
                P(0, "LINE"); P(8, Layer(k)); Pd(10, x1 + dx); Pd(20, y1); Pd(30, 0); Pd(11, x2 + dx); Pd(21, y2); Pd(31, 0);
            }
            foreach (var l in g.Lines) Line(l.X1, l.Y1, l.X2, l.Y2, l.Kind);
            // ломаные — POLYLINE (тип линии идёт по всей ломаной, как на листе)
            foreach (var pl in g.Polylines)
            {
                if (pl.Points.Count < 2) continue;
                if (pl.Points.Count == 2) { Line(pl.Points[0].X, pl.Points[0].Y, pl.Points[1].X, pl.Points[1].Y, pl.Kind); continue; }
                P(0, "POLYLINE"); P(8, Layer(pl.Kind)); P(66, "1"); Pd(10, 0); Pd(20, 0); Pd(30, 0); P(70, "128");
                foreach (var (x, y) in pl.Points) { P(0, "VERTEX"); P(8, Layer(pl.Kind)); Pd(10, x + dx); Pd(20, y); Pd(30, 0); }
                P(0, "SEQEND"); P(8, Layer(pl.Kind));
            }
            foreach (var f in g.Fills)
            {
                if (f.Count < 3) continue;
                // SOLID: четыре вершины (3-я и 4-я в порядке «зигзага»); многоугольник — веером треугольников
                for (int i = 1; i + 1 < f.Count; i++)
                {
                    P(0, "SOLID"); P(8, "THIN");
                    Pd(10, f[0].X + dx); Pd(20, f[0].Y); Pd(30, 0);
                    Pd(11, f[i].X + dx); Pd(21, f[i].Y); Pd(31, 0);
                    Pd(12, f[i + 1].X + dx); Pd(22, f[i + 1].Y); Pd(32, 0);
                    Pd(13, f[i + 1].X + dx); Pd(23, f[i + 1].Y); Pd(33, 0);
                }
            }
            foreach (var (x, y, r, k) in g.Circles) { P(0, "CIRCLE"); P(8, Layer(k)); Pd(10, x + dx); Pd(20, y); Pd(30, 0); Pd(40, r); }
            foreach (var t in g.Texts)
            {
                if (string.IsNullOrEmpty(t.Text)) continue;
                P(0, "TEXT"); P(8, "TEXT");
                Pd(10, t.X + dx); Pd(20, t.Y); Pd(30, 0); Pd(40, t.Height); P(1, t.Text.Replace('\n', ' ').Replace(DrawingDocument.Dia, "%%c").Replace("±", "%%p").Replace("°", "%%d"));
                if (t.Angle != 0) Pd(50, t.Angle);
                if (t.Align != TextAlign.Left)
                {
                    P(72, t.Align == TextAlign.Center ? "1" : "2");
                    Pd(11, t.X + dx); Pd(21, t.Y); Pd(31, 0);
                }
            }
            dx += g.Width + 20;
        }
        P(0, "ENDSEC");
        P(0, "EOF");
        return sb.ToString();
    }

    /// <summary>DXF в файл: кодировка — UTF-8 (AutoCAD 2007+ и LibreCAD; старым программам — ANSI_1251 по выбору позже).</summary>
    public static void Save(string path, IEnumerable<SheetGraphics> pages) => File.WriteAllText(path, Write(pages), new UTF8Encoding(false));
}
