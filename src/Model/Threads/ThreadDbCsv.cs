using System.Globalization;
using System.Text;
using OpenTK.Mathematics;

namespace Parametric3D.Model.Threads;

// Разбор threads_table.csv: ';', поле в кавычках с "" внутри; профиль впадины — трапеция по ширинам и глубине.
public static partial class ThreadDb
{
    public static List<ThreadSpec> ParseCsv(string[] lines)
    {
        var res = new List<ThreadSpec>();
        if (lines.Length < 2) return res;
        var head = SplitCsv(lines[0]);
        int Col(string name) => head.FindIndex(h => h.Trim().TrimStart('\uFEFF') == name);
        int cFam = Col("family"), cDes = Col("designation"), cSer = Col("series"), cD = Col("d_major_mm"), cP = Col("P_mm"),
            cD1 = Col("D1_mm"), cD3 = Col("d3_mm"), cTap = Col("tap_drill_mm"),
            cER = Col("ext_start_R_mm"), cEd = Col("ext_depth_mm"), cEw0 = Col("ext_w0_mm"), cEwb = Col("ext_wbot_mm"),
            cIR = Col("int_start_R_mm"), cId = Col("int_depth_mm"), cIw0 = Col("int_w0_mm"), cIwb = Col("int_wbot_mm"),
            cTaper = Col("taper_ratio_on_diameter");
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var f = SplitCsv(lines[i]);
            string S(int c) => c >= 0 && c < f.Count ? f[c].Trim() : "";
            double? N(int c) => double.TryParse(S(c), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            var t = new ThreadSpec
            {
                Family = S(cFam), Profile = S(cFam), Designation = S(cDes), Series = S(cSer),
                DMajor = N(cD) ?? 0, P = N(cP) ?? 0, D1 = N(cD1), D3 = N(cD3), TapDrill = N(cTap),
            };
            t.Lead = t.P;
            var ratio = S(cTaper);
            if (ratio.Length > 0)
            {
                t.Taper = true;
                // «1:16» по диаметру → половина угла atan(1/32)
                var parts = ratio.Split(':');
                double k = parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var kk) && kk > 0 ? kk : 16;
                t.HalfConeDeg = Math.Atan(1 / (2 * k)) * 180 / Math.PI;
            }
            if (!(t.P > 0) || !(t.DMajor > 0) || N(cER) is not { } er || N(cEd) is not { } ed || N(cIR) is not { } ir || N(cId) is not { } id) continue;
            double over = 0.15 * t.P;
            t.ExtStartR = er; t.IntStartR = ir;
            t.ExtZr = Trapezoid(er, -ed, N(cEw0) ?? t.P / 2, N(cEwb) ?? 0, over);
            t.IntZr = Trapezoid(ir, id, N(cIw0) ?? t.P / 2, N(cIwb) ?? 0, over);
            res.Add(t);
        }
        return res;
    }

    /// <summary>Впадина трапецией: поверхность радиуса r0, дно на r0 + depth (наружная — depth &lt; 0), ширины у поверхности и у
    /// дна; перерез за поверхность на over по продолжению боковых сторон.</summary>
    private static Vector2d[] Trapezoid(double r0, double depth, double w0, double wb, double over)
    {
        double h0 = w0 / 2, hb = wb / 2, d = Math.Abs(depth);
        double hOver = d > 1e-12 ? h0 + (h0 - hb) * over / d : h0;
        double rOut = r0 - Math.Sign(depth) * over, rBot = r0 + depth;
        return new[] { new Vector2d(-hOver, rOut), new Vector2d(hOver, rOut), new Vector2d(hb, rBot), new Vector2d(-hb, rBot) };
    }

    private static List<string> SplitCsv(string line)
    {
        var res = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') q = false;
                else sb.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ';') { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        res.Add(sb.ToString());
        return res;
    }
}
