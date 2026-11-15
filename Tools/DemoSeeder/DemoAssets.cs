using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Astra.Demo;

public static class DemoAssets
{
    public static void Import(string root)
    {
        var destination = Path.Combine(root, "Tools", "DemoSeeder", "Assets");
        Directory.CreateDirectory(destination);
        foreach (var scenario in DemoSeries.Scenarios)
        {
            using var image = Image.Load(Path.Combine(root, ".demo", "raw-portraits-v1", scenario.Pin + ".png"));
            image.Mutate(x => x.AutoOrient().Resize(new ResizeOptions { Size = new Size(320, 240), Mode = ResizeMode.Crop }));
            image.SaveAsJpeg(Path.Combine(destination, scenario.Pin + ".jpg"), new JpegEncoder { Quality = 90 });
        }
        Console.WriteLine("Imported six generated fictional portraits as distinct 320x240 JPEG assets.");
    }
}
