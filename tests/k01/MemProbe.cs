using Parametric3D.Model;

static partial class Program
{
    /// <summary>Разбор: удерживаемая память при повторных сборках одной детали (деталь каждый раз новая).</summary>
    static void MemProbe(string path)
    {
        long Mem() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
        var json = File.ReadAllText(path);
        Console.WriteLine($"MEM start {Mem() / 1048576.0:0.0} MB");
        for (int i = 0; i < 12; i++)
        {
            var (p, _) = DocumentFile.Load(json); p.Rebuild(null);
            double v = p.Volume; p = null!;
            Console.WriteLine($"MEM after build {i}: {Mem() / 1048576.0:0.0} MB (volume {v:0.###})");
            if (i > 1) break;
        }
        // ThreadStatic этого потока — обнулить и замерить
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Parametric3D") || a.GetName().Name == "K01Checks"))
            foreach (var t in asm.GetTypes())
                foreach (var fi in t.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    if (!t.IsGenericTypeDefinition && fi.GetCustomAttributes(typeof(ThreadStaticAttribute), false).Length > 0 && !fi.FieldType.IsValueType)
                        try { if (fi.GetValue(null) != null) { Console.WriteLine($"MEM threadstatic {t.Name}.{fi.Name} set"); fi.SetValue(null, null); } } catch { }
        Console.WriteLine($"MEM after clearing thread statics (main thread): {Mem() / 1048576.0:0.0} MB");
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Parametric3D") || a.GetName().Name == "K01Checks"))
            foreach (var t in asm.GetTypes())
                foreach (var fi in t.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    if (t.IsGenericTypeDefinition || fi.IsLiteral) continue;
                    object? v; try { v = fi.GetValue(null); } catch { continue; }
                    if (v == null || v is string || v.GetType().IsPrimitive || v is Delegate) continue;
                    int n = -1;
                    if (v is System.Collections.ICollection c) n = c.Count;
                    else if (v.GetType().GetProperty("Count") is { } cp && cp.PropertyType == typeof(int) && cp.GetIndexParameters().Length == 0) { try { n = (int)cp.GetValue(v)!; } catch { } }
                    if (n > 20 || (n < 0 && v.GetType().Name.Contains("ConditionalWeakTable")))
                    {
                        int cw = -1;
                        if (v.GetType().Name.Contains("ConditionalWeakTable")) { try { cw = ((System.Collections.IEnumerable)v).Cast<object>().Count(); } catch { } }
                        Console.WriteLine($"MEM static {t.FullName}.{fi.Name}: count {n} cwt {cw} ({v.GetType().Name})");
                    }
                }
    }
}
