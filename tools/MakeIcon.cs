// Build helper: writes Pegline.ico from the same drawing code the tray uses.
namespace Pegline
{
    static class MakeIcon
    {
        static void Main(string[] args)
        {
            AppIcon.Write(args[0]);
            if (args.Length > 1)
                using (var bmp = AppIcon.Draw(256)) bmp.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
