#!/usr/bin/env bash
# Каталог автономных зависимостей для -p:LocalDeps=<каталог> (сборка проверок без nuget.org).
# OTKMath — исходники OpenTK.Mathematics 4.9.4 (GitHub) с проектом net8.0; SDCStub — заглушка System.Drawing
# (Font, FontStyle, FontFamily — всё, что нужно Core/Settings.cs). Использование: make_localdeps.sh <каталог>
set -euo pipefail
D="$1"; mkdir -p "$D/OTKMath" "$D/SDCStub"; cd "$D"
cat > NuGet.Config <<'X'
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /></packageSources></configuration>
X
curl -sSL -o otk.tgz https://codeload.github.com/opentk/opentk/tar.gz/refs/tags/4.9.4
tar -xzf otk.tgz opentk-4.9.4/src/OpenTK.Mathematics
cp -r opentk-4.9.4/src/OpenTK.Mathematics/* OTKMath/ && rm -rf opentk-4.9.4 otk.tgz
cat > OTKMath/OpenTK.Mathematics.csproj <<'X'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework><AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <RootNamespace>OpenTK.Mathematics</RootNamespace><Nullable>disable</Nullable>
    <NoWarn>$(NoWarn);CS1591;CS0618;CS8632;CS0436</NoWarn><Optimize>true</Optimize>
  </PropertyGroup>
</Project>
X
cat > SDCStub/System.Drawing.Common.csproj <<'X'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>SDCStub</AssemblyName></PropertyGroup>
</Project>
X
cat > SDCStub/Stub.cs <<'X'
namespace System.Drawing
{
    [Flags] public enum FontStyle { Regular = 0, Bold = 1, Italic = 2, Underline = 4, Strikeout = 8 }
    public sealed class FontFamily
    {
        public string Name { get; }
        public FontFamily(string name) { Name = name; }
        public static FontFamily GenericSansSerif { get; } = new("Sans");
    }
    public sealed class Font : IDisposable
    {
        public string Name { get; } public float Size { get; } public FontStyle Style { get; }
        public Font(string name, float size, FontStyle style = FontStyle.Regular) { Name = name; Size = size; Style = style; }
        public Font(FontFamily f, float size, FontStyle style = FontStyle.Regular) : this(f.Name, size, style) { }
        public void Dispose() { }
    }
}
X
echo "LocalDeps: $D"
