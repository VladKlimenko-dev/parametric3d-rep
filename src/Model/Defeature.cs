using Parametric3D.Model.Features;
using Parametric3D.Model.Params;

namespace Parametric3D.Model;

/// <summary>Мелкий элемент, найденный в дереве: что это, как называется и какого размера.</summary>
public readonly struct SmallFeature
{
    public readonly Feature Feature;
    public readonly string Kind;
    public readonly double Size;

    public SmallFeature(Feature feature, string kind, double size)
    {
        Feature = feature; Kind = kind; Size = size;
    }

    public override string ToString() => $"{Kind} «{Feature.Name}», размер {Size:0.###} мм";
}

/// <summary>
/// УПРОЩЕНИЕ ДЛЯ РАСЧЁТА. Мелкие отверстия, скругления и фаски на прочность почти не влияют, а
/// сетку портят сильнее всего: у скругления радиусом 0.5 мм на детали в 200 мм элементы выходят
/// втрое мельче нужного, и расчёт растёт в разы без пользы.
///
/// Обычно инженер гасит их вручную и потом забывает вернуть. Здесь это делается ИСПОЛНЕНИЕМ:
/// исходная модель остаётся нетронутой, а рядом появляется исполнение «для расчёта», где мелочь
/// погашена. Переключением между ними видно, что именно убрано.
///
/// Порог задаётся числом, а не угадывается: что мелко для корпуса, крупно для штуцера. Разумное
/// умолчание — сотая доля наибольшего габарита детали.
/// </summary>
public static class Defeature
{
    /// <summary>Найти элементы мельче порога. Порог в миллиметрах.</summary>
    public static List<SmallFeature> Find(Part part, ParameterTable? table, double threshold)
    {
        var res = new List<SmallFeature>();

        foreach (var f in part.Features)
        {
            switch (f)
            {
                case HoleFeature hole:
                {
                    double d = hole.Diameter.Resolve(table);
                    if (d > 0 && d < threshold) res.Add(new SmallFeature(f, "отверстие", d));
                    break;
                }

                case RoundFeature fillet:
                {
                    double r = fillet.Radius.Resolve(table);
                    if (r > 0 && r < threshold) res.Add(new SmallFeature(f, "скругление", r));
                    break;
                }

                case ChamferFeature chamfer:
                {
                    double c = chamfer.D1.Resolve(table);
                    if (c > 0 && c < threshold) res.Add(new SmallFeature(f, "фаска", c));
                    break;
                }
            }
        }

        return res;
    }

    /// <summary>
    /// Разумный порог по габаритам детали: сотая доля наибольшего размера. Это не истина, а
    /// отправная точка — порог всегда можно задать своим числом.
    /// </summary>
    public static double SuggestThreshold(Part part)
    {
        double span = 0;
        foreach (var b in part.Bodies)
            foreach (var v in b.Vertices)
            {
                span = Math.Max(span, Math.Abs(v.P.X));
                span = Math.Max(span, Math.Abs(v.P.Y));
                span = Math.Max(span, Math.Abs(v.P.Z));
            }

        return span > 0 ? span / 100 : 1;
    }

    /// <summary>
    /// Создать исполнение «для расчёта»: мелкие элементы погашены, исходная модель не тронута.
    /// Возвращает исполнение и список того, что в нём погашено.
    /// </summary>
    public static (Configuration Config, List<SmallFeature> Suppressed) MakeAnalysisConfiguration(
        Part part, ParameterTable? table, double threshold = 0, string name = "для расчёта")
    {
        double limit = threshold > 0 ? threshold : SuggestThreshold(part);
        var small = Find(part, table, limit);

        var config = new Configuration(name)
        {
            Comment = $"погашены элементы мельче {limit:0.###} мм"
        };

        foreach (var s in small) config.SuppressedFeatures.Add(s.Feature.Name);

        // Исполнение с тем же именем заменяется, а не добавляется вторым: иначе после
        // нескольких попыток подобрать порог их накопится десяток.
        part.Configurations.RemoveAll(c => c.Name == name);
        part.Configurations.Add(config);

        return (config, small);
    }
}
