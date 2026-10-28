using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            var glass = new LinearGradientBrush(Color.FromRgb(94, 171, 232), Color.FromRgb(71, 104, 179), 45);
            dc.DrawRoundedRectangle(glass, new Pen(new SolidColorBrush(Color.FromRgb(189, 230, 255)), 3), new Rect(12, 12, 232, 232), 58, 58);
            for (int i = 0; i < 4; i++)
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(i is 0 or 3 ? 240 : 166), 255, 255, 255)), null, new Rect(54 + i % 2 * 84, 54 + i / 2 * 84, 64, 64), 19, 19);
        }
        var image = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
        image.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var png = new MemoryStream();
        encoder.Save(png);
        var bytes = png.ToArray();
        using var output = new BinaryWriter(File.Create(args[0]));
        output.Write((ushort)0);
        output.Write((ushort)1);
        output.Write((ushort)1);
        output.Write((byte)0);
        output.Write((byte)0);
        output.Write((byte)0);
        output.Write((byte)0);
        output.Write((ushort)1);
        output.Write((ushort)32);
        output.Write((uint)bytes.Length);
        output.Write((uint)22);
        output.Write(bytes);
    }
}
