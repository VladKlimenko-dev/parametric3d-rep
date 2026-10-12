static partial class Program
{
    static void Krn05UniteChainChecks()
    {
        Check("KRN05-UNITE-MULTIBODY-NO-CYCLE", () =>
        {
            // Объединение с набором из нескольких тел: цилиндр, конус и тор (серия 9, d9_0021 — единственный «предел
            // времени» серий): после первого объединения тела остаются раздельными, и присоединение тора к набору шло снова
            // многотельным путём по кругу (2481 вызов точного маршрута, deadline at mesh.face). Теперь — к каждому телу
            // набора по одному: точный итог (OpenCascade согласен, разовая сверка), быстро.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var row = ReplayDiffCase(9, 21, "122112212112212222121");
            double sec = sw.Elapsed.TotalSeconds;
            Log(new { test = "KRN05-UNITE-MULTIBODY", outcome = row[3], guarantee = row[4], bodies = row[5], volume = row[6], seconds = sec, detail = row.Length > 10 ? row[10] : "" });
            Require(row[3] == "Success" && row[4] == "ExactAnalytic" && sec < 60, $"d9_0021: {row[3]} {row[4]} in {sec:F1} s {(row.Length > 10 ? row[10] : "")}");
        });
    }
}
