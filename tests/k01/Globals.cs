using System.Reflection;
using System.Runtime.CompilerServices;

static partial class Program
{
    /// <summary>Изменяемые статические поля ядра и модели, не привязанные к контексту вычисления.</summary>
    internal static List<string> UnboundGlobals()
    {
        var found = new List<string>();
        var asm = typeof(Parametric3D.Kernel.KernelContext).Assembly;
        foreach (var t in asm.GetTypes())
        {
            string ns = t.Namespace ?? "";
            if (!(ns.StartsWith("Parametric3D.Kernel") || ns.StartsWith("Parametric3D.Model") || ns.StartsWith("Parametric3D.Core"))) continue;
            if (t.IsDefined(typeof(CompilerGeneratedAttribute))) continue;
            foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (f.IsLiteral || f.IsInitOnly) continue;
                if (f.IsDefined(typeof(ThreadStaticAttribute))) continue;
                if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;            // трассы и наблюдатели — не результат
                string name = f.Name;
                var m = System.Text.RegularExpressions.Regex.Match(name, @"^<(\w+)>k__BackingField$");
                if (m.Success) name = m.Groups[1].Value;                                   // статическое авто-свойство
                // Привязано к контексту: поле _gX при свойстве X, запись которого идёт через KernelContext.SetGlobal.
                if (name.StartsWith("_g") && t.GetProperty(name[2..], BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null) continue;
                found.Add($"{t.FullName!.Replace("Parametric3D.", "")}.{name}");
            }
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }
}
