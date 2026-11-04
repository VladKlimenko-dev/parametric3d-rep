using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Parametric3D.App.Views;
using Parametric3D.Model;
using Parametric3D.Model.Features;

namespace Parametric3D.App.Services.Mcp;

/// <summary>
/// k154: ИНСТРУМЕНТЫ MCP — тонкая обёртка над моделью и ядром открытого документа. Чтение: документ, дерево операций, одна
/// операция (все её значения), параметры, массовые характеристики и проверка тел, снимок вида. Правка — одним шагом отмены
/// («Claude: …»): значение операции, параметр, подавление, отмена и возврат; открыть, сохранить, экспорт. После правки —
/// перестроение и итог (ошибки операций, объём).
/// </summary>
public static class McpTools
{
    public sealed record Tool(string Name, string Description, JsonObject Schema, bool Writes, Func<MainWindow, JsonObject, Task<JsonObject>> Run)
    {
        public JsonObject Describe() => new()
        {
            ["name"] = Name, ["description"] = Description, ["inputSchema"] = (JsonObject)Schema.DeepClone(),
            ["annotations"] = new JsonObject { ["readOnlyHint"] = !Writes },
        };
    }

    static JsonObject Obj(params (string Name, string Type, string Desc, bool Required)[] props)
    {
        var p = new JsonObject(); var req = new JsonArray();
        foreach (var (n, t, d, r) in props)
        {
            p[n] = t == "any" ? new JsonObject { ["description"] = d } : new JsonObject { ["type"] = t, ["description"] = d };
            if (r) req.Add(n);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = req };
    }

    public static readonly Tool[] All =
    {
        new("get_document", "Summary of the document open in Parametric3D: name, file, units, parts, number of features, whether it is built, errors.",
            Obj(), false, GetDocument),
        new("list_features", "The build tree of the active part: index, key, name, type, summary, error, suppressed, rollback position.",
            Obj(), false, ListFeatures),
        new("get_feature", "All editable values of one feature (by key or name): numbers with formulas, flags, choices, reference counts.",
            Obj(("feature", "string", "feature key or name", true)), false, GetFeature),
        new("set_feature_value", "Set one value of a feature and rebuild (one undo step). field is a member path such as 'Diameter', 'Radius', 'D1', 'Side1.Distance', 'ThroughAll'; value is a number, boolean, choice name, or a formula string starting with '='.",
            Obj(("feature", "string", "feature key or name", true), ("field", "string", "member path", true), ("value", "any", "number, boolean, choice name or '=formula'", true)), true, SetFeatureValue),
        new("suppress_feature", "Suppress or unsuppress a feature and rebuild (one undo step).",
            Obj(("feature", "string", "feature key or name", true), ("suppressed", "boolean", "true to suppress", true)), true, SuppressFeature),
        new("get_parameters", "Document parameters: name, value, formula, unit, error.", Obj(), false, GetParameters),
        new("set_parameter", "Set a document parameter (number or '=formula') and rebuild (one undo step).",
            Obj(("name", "string", "parameter name", true), ("value", "any", "number or '=formula'", true)), true, SetParameter),
        new("rebuild", "Rebuild the active part (from the first changed feature) and report errors, volume and build time.", Obj(), false, Rebuild),
        new("get_mass_properties", "Volume, surface area, mass, bounding box of the active part; per body: guarantee level (exact / polyhedral), validity.", Obj(), false, MassProperties),
        new("capture_view", "A picture (PNG) of the current 3D view of the document.", Obj(), false, CaptureView),
        new("undo", "Undo the last change in the document.", Obj(), true, (w, _) => Ui(w, () => { w.Session.Undo(); return "undone"; })),
        new("redo", "Redo the last undone change.", Obj(), true, (w, _) => Ui(w, () => { w.Session.Redo(); return "redone"; })),
        new("open_document", "Open a Parametric3D document (.p3d) from a path on this computer.",
            Obj(("path", "string", "full path of the .p3d file", true)), true, OpenDocument),
        new("save_document", "Save the document (to its file, or to the given path).",
            Obj(("path", "string", "optional new path", false)), true, SaveDocument),
        new("export", "Export the active part to STEP, IGES, STL or OBJ at the given path.",
            Obj(("path", "string", "output file path", true), ("format", "string", "Step, Iges, Stl or Obj", true)), true, Export),
    };

    public static Tool? Find(string name) => All.FirstOrDefault(t => t.Name == name);

    /// <summary>Вызов инструмента в потоке окна (модель — однопоточная, как и правка пользователем).</summary>
    public static async Task<JsonObject> CallAsync(Func<MainWindow?> window, string name, JsonObject args)
    {
        var tool = Find(name) ?? throw new ArgumentException($"unknown tool '{name}'");
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var w = window() ?? throw new InvalidOperationException("Parametric3D has no open window");
                if (tool.Writes) w.ShowToast(Loc.L("Claude: {0}", tool.Name), error: false);
                return await tool.Run(w, args);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return McpProtocol.ToolText(ex.Message, isError: true); }
    }

    // ------------------------------------------------------------------ общие

    static Task<JsonObject> Ui(MainWindow w, Func<string> f) => Task.FromResult(McpProtocol.ToolText(f()));

    static Part ActivePart(MainWindow w) => w.Session.Part ?? w.Session.ActivePart ?? throw new InvalidOperationException("no active part (an assembly is open — activate a component)");

    static Feature FindFeature(Part p, string key) =>
        p.Features.FirstOrDefault(f => f.Key == key)
        ?? p.Features.FirstOrDefault(f => string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"no feature '{key}' (use list_features)");

    static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Перестроить грязные детали (как фоновый расчёт окна) и дождаться; null — успел, иначе причина.</summary>
    static async Task<string?> BuildAsync(MainWindow w, int seconds = 900)
    {
        var doc = w.Session.Document;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (doc.IsBuilding || doc.AllParts().Any(p => p.IsDirty))
        {
            if (sw.Elapsed.TotalSeconds > seconds) return $"the build is still running after {seconds} s";
            await doc.RebuildDirtyAsync();
            await Task.Delay(20);
        }
        return null;
    }

    static string BuildReport(Part p, double ms = double.NaN)
    {
        var sb = new StringBuilder();
        var errs = p.Features.Where(f => f.Error != null).ToList();
        sb.Append($"part '{p.Name}': {p.Bodies.Count} body(ies), volume {Num(p.Volume)} mm³");
        if (!double.IsNaN(ms)) sb.Append($", built in {ms / 1000:0.0} s");
        sb.Append(errs.Count == 0 ? "; no feature errors." : $"; {errs.Count} feature error(s):");
        foreach (var f in errs) sb.Append($"\n- {f.Name} [{f.Key}]: {f.Error}");
        if (p.Warnings.Count > 0) sb.Append("\nwarnings: " + string.Join(" | ", p.Warnings.Take(5)));
        return sb.ToString();
    }

    // ------------------------------------------------------------------ чтение

    static Task<JsonObject> GetDocument(MainWindow w, JsonObject a)
    {
        var s = w.Session; var doc = s.Document;
        var parts = doc.AllParts().ToList();
        var o = new JsonObject
        {
            ["name"] = doc.Title, ["file"] = s.FilePath, ["modified"] = s.Modified, ["assembly"] = s.IsAssembly,
            ["building"] = doc.IsBuilding,
            ["parts"] = new JsonArray(parts.Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Name, ["features"] = p.Features.Count, ["errors"] = p.Features.Count(f => f.Error != null),
                ["bodies"] = p.Bodies.Count, ["dirty"] = p.IsDirty, ["units"] = p.Units?.ToString(),
            }).ToArray()),
        };
        return Task.FromResult(McpProtocol.ToolText(o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })));
    }

    static Task<JsonObject> ListFeatures(MainWindow w, JsonObject a)
    {
        var p = ActivePart(w);
        var arr = new JsonArray();
        for (int i = 0; i < p.Features.Count; i++)
        {
            var f = p.Features[i];
            arr.Add(new JsonObject
            {
                ["index"] = i, ["key"] = f.Key, ["name"] = f.Name, ["type"] = f.GetType().Name.Replace("Feature", ""),
                ["summary"] = f.Summary, ["error"] = f.Error, ["suppressed"] = f.Suppressed, ["beyondRollback"] = p.IsBeyondStage(f),
            });
        }
        return Task.FromResult(McpProtocol.ToolText(arr.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })));
    }

    /// <summary>Значения операции: открытые поля и свойства её типа — числа (с формулой), флаги, выборы, вложенные стороны.</summary>
    static JsonObject Values(object o, int depth = 0)
    {
        var res = new JsonObject();
        var t = o.GetType();
        foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            object? v; Type mt;
            if (m is FieldInfo fi) { v = fi.GetValue(o); mt = fi.FieldType; }
            else if (m is PropertyInfo pi && pi.CanRead && pi.GetIndexParameters().Length == 0 && pi.GetMethod?.IsStatic == false) { try { v = pi.GetValue(o); } catch { continue; } mt = pi.PropertyType; }
            else continue;
            JsonNode? node = v switch
            {
                ValueRef vr => vr.Formula != null ? new JsonObject { ["value"] = vr.Value, ["formula"] = vr.Formula } : JsonValue.Create(vr.Value),
                double d => JsonValue.Create(d),
                int n => JsonValue.Create(n),
                uint u => JsonValue.Create(u),
                bool b => JsonValue.Create(b),
                string s => JsonValue.Create(s),
                Enum e => JsonValue.Create(e.ToString() + " (" + string.Join("|", Enum.GetNames(e.GetType())) + ")"),
                OpenTK.Mathematics.Vector3d vv => new JsonArray(vv.X, vv.Y, vv.Z),
                System.Collections.ICollection c => JsonValue.Create($"{c.Count} item(s)"),
                null => null,
                _ when depth == 0 && mt.IsClass && mt.Namespace?.StartsWith("Parametric3D.Model") == true => Values(v, 1),
                _ => null,
            };
            if (node != null || v == null) res[m.Name] = node;
        }
        return res;
    }

    static Task<JsonObject> GetFeature(MainWindow w, JsonObject a)
    {
        var f = FindFeature(ActivePart(w), (string)a["feature"]!);
        var o = new JsonObject { ["key"] = f.Key, ["name"] = f.Name, ["type"] = f.GetType().Name, ["error"] = f.Error, ["values"] = Values(f) };
        return Task.FromResult(McpProtocol.ToolText(o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })));
    }

    static Task<JsonObject> GetParameters(MainWindow w, JsonObject a)
    {
        var arr = new JsonArray();
        foreach (var p in w.Session.Document.Parameters.All)
            arr.Add(new JsonObject { ["name"] = p.Name, ["value"] = p.Value, ["formula"] = string.IsNullOrEmpty(p.Formula) ? null : p.Formula, ["unit"] = p.Unit, ["error"] = p.Error, ["comment"] = string.IsNullOrEmpty(p.Comment) ? null : p.Comment });
        return Task.FromResult(McpProtocol.ToolText(arr.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })));
    }

    static async Task<JsonObject> Rebuild(MainWindow w, JsonObject a)
    {
        var p = ActivePart(w);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? late = await BuildAsync(w);
        return McpProtocol.ToolText((late != null ? late + ". " : "") + BuildReport(p, sw.Elapsed.TotalMilliseconds));
    }

    static async Task<JsonObject> MassProperties(MainWindow w, JsonObject a)
    {
        var p = ActivePart(w);
        if (await BuildAsync(w) is { } late) return McpProtocol.ToolText(late, isError: true);
        var bb = p.Bounds;
        var o = new JsonObject
        {
            ["part"] = p.Name, ["volume_mm3"] = p.Volume, ["area_mm2"] = p.Area, ["mass_g"] = p.MassByBodies(),
            ["bounds_min"] = new JsonArray(bb.Min.X, bb.Min.Y, bb.Min.Z), ["bounds_max"] = new JsonArray(bb.Max.X, bb.Max.Y, bb.Max.Z),
            ["bodies"] = new JsonArray(p.Bodies.Select((b, i) => (JsonNode)new JsonObject
            {
                ["name"] = p.BodyName(i), ["guarantee"] = b.Guarantee.ToString(), ["faces"] = b.Faces.Count(),
                ["valid"] = Parametric3D.Kernel.Topology.SolidValidator.Validate(b).IsValid,
            }).ToArray()),
        };
        return McpProtocol.ToolText(o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    static async Task<JsonObject> CaptureView(MainWindow w, JsonObject a)
    {
        var tcs = new TaskCompletionSource<byte[]>();
        w.ViewPort.CaptureNextFrame = (px, pw, ph) =>
        {
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), $"p3d_mcp_{Guid.NewGuid():N}.png");
                Diagnostics.Shots.SavePng(tmp, px, pw, ph, flip: true);
                var bytes = File.ReadAllBytes(tmp); File.Delete(tmp);
                tcs.TrySetResult(bytes);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        };
        w.ViewPort.MarkDirty(); w.ViewPort.Redraw();
        var done = await Task.WhenAny(tcs.Task, Task.Delay(10000));
        w.ViewPort.CaptureNextFrame = null;
        if (done != tcs.Task) return McpProtocol.ToolText("the view did not render a frame (is the window minimised?)", isError: true);
        return McpProtocol.ToolText($"view of '{w.Session.Document.Title}'", png: await tcs.Task);
    }

    // ------------------------------------------------------------------ правка

    /// <summary>Член по пути «A.B»: последний — поле или свойство с записью; предыдущие — объекты-классы.</summary>
    static (object Owner, MemberInfo Member, Type Type) Resolve(object root, string path)
    {
        object cur = root;
        var parts = path.Split('.');
        for (int i = 0; i < parts.Length; i++)
        {
            var t = cur.GetType();
            var m = (MemberInfo?)t.GetField(parts[i], BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                    ?? t.GetProperty(parts[i], BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                    ?? throw new ArgumentException($"'{parts[i]}' is not a value of {t.Name} (use get_feature)");
            Type mt = m is FieldInfo f ? f.FieldType : ((PropertyInfo)m).PropertyType;
            if (i == parts.Length - 1) return (cur, m, mt);
            cur = (m is FieldInfo f2 ? f2.GetValue(cur) : ((PropertyInfo)m).GetValue(cur)) ?? throw new ArgumentException($"'{parts[i]}' is empty");
            if (cur.GetType().IsValueType) throw new ArgumentException($"'{parts[i]}' is a value, not an object");
        }
        throw new ArgumentException("empty path");
    }

    static object Convert(JsonNode? v, Type t)
    {
        string? s = v is JsonValue jv && jv.TryGetValue<string>(out var str) ? str : null;
        if (t == typeof(ValueRef))
        {
            if (s != null && s.StartsWith('=')) return new ValueRef(0) { Formula = s[1..].Trim() };
            return new ValueRef(s != null ? double.Parse(s, CultureInfo.InvariantCulture) : v!.GetValue<double>());
        }
        if (t == typeof(double)) return s != null ? double.Parse(s, CultureInfo.InvariantCulture) : v!.GetValue<double>();
        if (t == typeof(int)) return s != null ? int.Parse(s, CultureInfo.InvariantCulture) : (int)v!.GetValue<double>();
        if (t == typeof(bool)) return s != null ? bool.Parse(s) : v!.GetValue<bool>();
        if (t == typeof(string)) return s ?? v!.ToJsonString();
        if (t.IsEnum) return Enum.Parse(t, s ?? v!.ToString(), ignoreCase: true);
        throw new ArgumentException($"values of type {t.Name} are not editable here");
    }

    static async Task<JsonObject> SetFeatureValue(MainWindow w, JsonObject a)
    {
        var p = ActivePart(w);
        var f = FindFeature(p, (string)a["feature"]!);
        string field = (string)a["field"]!;
        var (owner, m, t) = Resolve(f, field);
        var val = Convert(a["value"], t);
        if (m is PropertyInfo pi && !pi.CanWrite) throw new ArgumentException($"'{field}' is read-only");
        if (m is FieldInfo fi0 && fi0.IsInitOnly) throw new ArgumentException($"'{field}' is read-only");
        w.Session.Edit(Loc.L("Claude: {0}", $"{f.Name}.{field}"), () =>
        {
            if (m is FieldInfo fi) fi.SetValue(owner, val); else ((PropertyInfo)m).SetValue(owner, val);
            p.MarkDirtyFrom(f);
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? late = await BuildAsync(w);
        return McpProtocol.ToolText($"{f.Name}.{field} = {a["value"]?.ToJsonString()}. " + (late != null ? late + ". " : "") + BuildReport(p, sw.Elapsed.TotalMilliseconds), isError: f.Error != null);
    }

    static async Task<JsonObject> SuppressFeature(MainWindow w, JsonObject a)
    {
        var p = ActivePart(w);
        var f = FindFeature(p, (string)a["feature"]!);
        bool sup = a["suppressed"]!.GetValue<bool>();
        w.Session.Edit(Loc.L("Claude: {0}", (sup ? "suppress " : "unsuppress ") + f.Name), () => { f.Suppressed = sup; p.MarkDirtyFrom(f); });
        string? late = await BuildAsync(w);
        return McpProtocol.ToolText((late != null ? late + ". " : "") + BuildReport(p));
    }

    static async Task<JsonObject> SetParameter(MainWindow w, JsonObject a)
    {
        string name = (string)a["name"]!;
        var table = w.Session.Document.Parameters;
        var prm = table.Find(name) ?? throw new ArgumentException($"no parameter '{name}' (use get_parameters)");
        var v = a["value"];
        string? s = v is JsonValue jv && jv.TryGetValue<string>(out var str) ? str : null;
        w.Session.Edit(Loc.L("Claude: {0}", "parameter " + name), () =>
        {
            if (s != null && s.StartsWith('=')) prm.Formula = s[1..].Trim();
            else { prm.Formula = ""; prm.Number = s != null ? double.Parse(s, CultureInfo.InvariantCulture) : v!.GetValue<double>(); }
            table.Recalculate();
        });
        string? late = await BuildAsync(w);
        var p = ActivePart(w);
        return McpProtocol.ToolText($"{name} = {Num(prm.Value)}{(prm.Error != null ? " (error: " + prm.Error + ")" : "")}. " + (late != null ? late + ". " : "") + BuildReport(p), isError: prm.Error != null);
    }

    static async Task<JsonObject> OpenDocument(MainWindow w, JsonObject a)
    {
        string path = (string)a["path"]!;
        if (!File.Exists(path)) throw new FileNotFoundException("no such file", path);
        await w.OpenAsync(path);
        return McpProtocol.ToolText($"opened {path}; " + (w.Session.Part is { } p ? (p.RestoredFromFile ? "the part is shown from the stored body (no rebuild). " : "") + BuildReport(p) : "assembly"));
    }

    static Task<JsonObject> SaveDocument(MainWindow w, JsonObject a)
    {
        string? path = (string?)a["path"];
        if (path != null) w.Session.SaveAs(path); else w.Session.Save();
        return Task.FromResult(McpProtocol.ToolText("saved to " + (path ?? w.Session.FilePath)));
    }

    static async Task<JsonObject> Export(MainWindow w, JsonObject a)
    {
        var p = ActivePart(w);
        if (await BuildAsync(w) is { } late) return McpProtocol.ToolText(late, isError: true);
        var fmt = Enum.Parse<Exchange.Format>((string)a["format"]!, ignoreCase: true);
        var warnings = new List<string>();
        string text = Exchange.Write(p, fmt, warnings);
        string path = (string)a["path"]!;
        File.WriteAllText(path, text);
        return McpProtocol.ToolText($"exported {fmt} to {path}" + (warnings.Count > 0 ? "; warnings: " + string.Join(" | ", warnings.Take(5)) : ""));
    }
}
