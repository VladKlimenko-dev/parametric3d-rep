// Заглушка System.Drawing.Common для проверок без сети: только то, что нужно Core/Settings.cs.
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
