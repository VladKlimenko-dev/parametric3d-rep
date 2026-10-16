#!/usr/bin/env python3
"""Поля x:Name для разметки Avalonia (генератор Avalonia требует Roslyn 4.14, а в .NET 8 SDK его нет).

Запуск: python3 tools/gen_avalonia_names.py src/App/Views/MainWindow.axaml [...]
Пишет рядом <Имя>.Names.cs: поля именованных элементов и InitializeComponent() (загрузка разметки и поиск полей).
Префиксы пространств имён: vp: → Parametric3D.App.Viewport, vm: → Parametric3D.App.ViewModels, v: → Parametric3D.App.Views.
"""
import re, sys, os
NS = {'vp': 'Viewport', 'vm': 'ViewModels', 'v': 'Views'}
for path in sys.argv[1:]:
    x = open(path, encoding='utf-8-sig').read()
    cls = re.search(r'x:Class="([\w.]+)"', x).group(1)
    ns, name = cls.rsplit('.', 1)
    items = []
    for m in re.finditer(r'<(?:(\w+):)?(\w+)\b[^>]*?\bx:Name="(\w+)"', x, re.S):
        pre, t, n = m.groups()
        full = f'Parametric3D.App.{NS[pre]}.{t}' if pre in NS else t
        items.append((full, n))
    fields = '\n'.join(f'    private {t} {n} = null!;' for t, n in items)
    init = '\n'.join(f'        {n} = this.FindControl<{t}>("{n}")!;' for t, n in items)
    out = f'''using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace {ns};

// Создано tools/gen_avalonia_names.py из {os.path.basename(path)} — не править вручную.
public partial class {name}
{{
{fields}

    private void InitializeComponent()
    {{
        AvaloniaXamlLoader.Load(this);
{init}
    }}
}}
'''
    open(os.path.splitext(path)[0] + '.Names.cs', 'w', encoding='utf-8').write(out)
    print(path, len(items))
