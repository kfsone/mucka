using System.Buffers.Binary;

namespace Mucka.Terminal.Tests;

/// <summary>
/// The terminal draws every run on a fixed column grid measured from the regular Cascadia Mono
/// face (Rendering.TerminalFont), and draws italic runs in the italic file. Operator rule under
/// test: italic keeps the cell grid - so both embedded files must give every glyph the same one
/// advance and share the vertical metrics the line box is built from. Read from the font files
/// themselves (the OpenType head, hhea and hmtx tables); nothing here reaches the MAUI assembly.
/// </summary>
public class TerminalFontFilesTests
{
    private static string FontsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mucka.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "Resources", "Fonts");
    }

    private sealed record Metrics(int UnitsPerEm, int Ascent, int Descent, int LineGap, IReadOnlySet<int> Advances);

    private static Metrics Read(string file)
    {
        var b = File.ReadAllBytes(Path.Combine(FontsDir(), file));
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(4));
        var tables = new Dictionary<string, int>();
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            var tag = System.Text.Encoding.ASCII.GetString(b, rec, 4);
            tables[tag] = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(rec + 8));
        }
        int head = tables["head"], hhea = tables["hhea"], hmtx = tables["hmtx"];
        int upem = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(head + 18));
        int ascent = BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(hhea + 4));
        int descent = BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(hhea + 6));
        int lineGap = BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(hhea + 8));
        int numberOfHMetrics = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(hhea + 34));
        var advances = new HashSet<int>();
        for (int i = 0; i < numberOfHMetrics; i++)
            advances.Add(BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(hmtx + i * 4)));
        return new Metrics(upem, ascent, descent, lineGap, advances);
    }

    [Fact]
    public void TheItalicFace_HasTheRegularFacesOneAdvance_AndItsLineMetrics()
    {
        var regular = Read("CascadiaMono.ttf");
        var italic = Read("CascadiaMonoItalic.ttf");

        var cell = Assert.Single(regular.Advances, a => a != 0);   // a true monospace: one advance
        Assert.All(italic.Advances, a => Assert.True(a == 0 || a == cell, $"italic advance {a}, cell {cell}"));
        Assert.Equal(regular.UnitsPerEm, italic.UnitsPerEm);
        Assert.Equal((regular.Ascent, regular.Descent, regular.LineGap), (italic.Ascent, italic.Descent, italic.LineGap));
    }

    [Fact]
    public void TheItalicFace_IsEmbeddedUnderTheNameTheRendererLoads()
    {
        var csproj = File.ReadAllText(Path.Combine(FontsDir(), "..", "..", "Mucka.csproj"));
        var font = File.ReadAllText(Path.Combine(FontsDir(), "..", "..", "Rendering", "TerminalFont.cs"));
        const string name = "Mucka.CascadiaMonoItalic.ttf";
        Assert.Contains($"Include=\"Resources\\Fonts\\CascadiaMonoItalic.ttf\" LogicalName=\"{name}\"", csproj);
        Assert.Contains($"\"{name}\"", font);
    }
}
