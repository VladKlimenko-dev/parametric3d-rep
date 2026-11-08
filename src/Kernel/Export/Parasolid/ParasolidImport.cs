namespace Parametric3D.Kernel.Export.Parasolid;

/// <summary>
/// ВХОД ИМПОРТА SOLIDWORKS И PARASOLID: файл (.sldprt — контейнер SolidWorks 2015+; .x_b — Parasolid в нейтральном двоичном
/// виде) → текст STEP с телами (XtToStep). Дальше — обычный путь импорта STEP (разметка, лечение, операция «Импорт»).
/// </summary>
public static class ParasolidImport
{
    public static bool Handles(string path)
    {
        var e = Path.GetExtension(path).ToLowerInvariant();
        return e is ".sldprt" or ".x_b" or ".xmt_bin";
    }

    public static string StepTextOf(string path, List<string>? warnings = null)
    {
        var bytes = File.ReadAllBytes(path);
        var name = Path.GetFileNameWithoutExtension(path);
        return StepText(bytes, name, warnings);
    }

    public static string StepText(byte[] bytes, string name, List<string>? warnings = null)
    {
        var xts = bytes.Length >= 2 && bytes[0] == (byte)'P' && bytes[1] == (byte)'S' ? new List<byte[]> { bytes } : SldprtFile.PartsXt(bytes, warnings);
        var files = xts.Select(x => XtReader.Read(x)).ToList();
        foreach (var f in files) if (f.Truncated) warnings?.Add($"Parasolid: the body stream is incomplete ({f.Order.Count} nodes read).");
        return XtToStep.Convert(files, name, warnings);
    }
}
