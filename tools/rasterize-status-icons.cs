#:package Svg.Skia@3.0.4
// Rasterize the Status/*.svg source art to PNGs with the SAME engine the app draws with
// (SkiaSharp), so the shipped bitmaps match on-canvas rendering exactly.
//
// Reads SVGs from  Resources/Raw/status/      (the source of truth — raw art, not shipped as images)
// Writes PNGs to   Resources/Images/Status/   (build action MauiImage — the shipped icon assets,
//                                               referenced in XAML by filename e.g. "strength.png")
//
// Run from the repo root:
//   dotnet run --file tools/rasterize-status-icons.cs
// Optionally pass a comma-separated size list (square, px). Default: a single 128px master named
// "<stat>.png" (MAUI generates the density buckets; the panel displays it at 16/24/32).
//   dotnet run --file tools/rasterize-status-icons.cs -- 128,256
//
// SVGs stay the source of truth; re-run this whenever they change or you want new sizes.

using SkiaSharp;
using Svg.Skia;

var svgDir = Path.GetFullPath(Path.Combine("Resources", "Raw", "status"));
var outDir = Path.GetFullPath(Path.Combine("Resources", "Images", "Status"));
if (!Directory.Exists(svgDir))
{
    Console.Error.WriteLine($"Status SVG dir not found: {svgDir} (run from repo root)");
    return 1;
}
Directory.CreateDirectory(outDir);

int[] sizes = args.Length > 0
    ? args[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Select(int.Parse).ToArray()
    : [128];

int written = 0;
foreach (var svgPath in Directory.EnumerateFiles(svgDir, "*.svg").OrderBy(p => p))
{
    var name = Path.GetFileNameWithoutExtension(svgPath);
    using var svg = new SKSvg();
    if (svg.Load(svgPath) is not { } picture)
    {
        Console.Error.WriteLine($"  ! failed to load {name}.svg");
        continue;
    }
    var src = picture.CullRect; // the SVG's own coordinate box (512x512 here)

    foreach (var size in sizes)
    {
        // Normal (buff) icon.
        Emit(picture, src, size, name, colorFilter: null, suffix: "");
        // Debuff variant: monochrome + bruised tint, so a lone negative icon can't be mistaken
        // for its positive twin. Only the ±stats have a debuff form — glow/invis and the full-colour
        // afflictions (deaf/blind/dumb/crippled) are single-state and ship as-is.
        if (name is not ("glow" or "invis" or "deaf" or "blind" or "dumb" or "crippled"))
            Emit(picture, src, size, name, colorFilter: SicklyFilter(), suffix: "_neg");
    }
}

Console.WriteLine($"Done — {written} PNG(s) written to {outDir}");
return 0;

// Desaturate to luma, then colorize toward a bruised purple-brown — a distinct "afflicted/bad"
// look that reads clearly even as the only icon in a stack.
static SKColorFilter SicklyFilter()
{
    const float lr = 0.299f, lg = 0.587f, lb = 0.114f;
    (float r, float g, float b) tint = (0.58f, 0.34f, 0.52f);
    float[] m =
    [
        lr * tint.r, lg * tint.r, lb * tint.r, 0, 0,
        lr * tint.g, lg * tint.g, lb * tint.g, 0, 0,
        lr * tint.b, lg * tint.b, lb * tint.b, 0, 0,
        0,           0,           0,           1, 0,
    ];
    return SKColorFilter.CreateColorMatrix(m);
}

void Emit(SKPicture pic, SKRect src, int size, string baseName, SKColorFilter? colorFilter, string suffix)
{
    using var bmp = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using (var canvas = new SKCanvas(bmp))
    {
        canvas.Clear(SKColors.Transparent);
        float sx = size / src.Width, sy = size / src.Height;
        canvas.Scale(sx, sy);
        canvas.Translate(-src.Left, -src.Top);
        using var paint = colorFilter is null ? null : new SKPaint { ColorFilter = colorFilter };
        canvas.DrawPicture(pic, paint);
    }
    var outName = sizes.Length == 1 ? $"{baseName}{suffix}.png" : $"{baseName}{suffix}_{size}.png";
    var outPath = Path.Combine(outDir, outName);
    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.Create(outPath);
    data.SaveTo(fs);
    Console.WriteLine($"  {outName}  ({size}x{size})");
    written++;
}
