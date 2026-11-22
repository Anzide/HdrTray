// Renders the images used by README.md into docs/, with the same code that
// draws the tray icon, so they always match the real thing:
//
//   powershell -ExecutionPolicy Bypass -File build.ps1 -Docs

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace HdrTray
{
    internal static class DocImages
    {
        // Approximate Windows 11 taskbar colours, with a hairline border so the
        // tiles stay visible on both light and dark GitHub themes.
        static readonly Color DarkTile = Color.FromArgb(0x20, 0x20, 0x20);
        static readonly Color DarkBorder = Color.FromArgb(0x45, 0x45, 0x45);
        static readonly Color LightTile = Color.FromArgb(0xF3, 0xF3, 0xF3);
        static readonly Color LightBorder = Color.FromArgb(0xC8, 0xC8, 0xC8);
        static readonly Color Arrow = Color.FromArgb(0x8B, 0x94, 0x9E);

        static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("usage: DocImages <out-dir>");
                return 2;
            }
            string dir = args[0];
            Directory.CreateDirectory(dir);

            using (Bitmap logo = IconRenderer.Render(TrayState.On, 256, false))
                logo.Save(Path.Combine(dir, "logo.png"), ImageFormat.Png);

            SaveState(dir, "state-on.png", TrayState.On);
            SaveState(dir, "state-off.png", TrayState.Off);
            SaveState(dir, "state-paused.png", TrayState.Paused);
            SaveState(dir, "state-unavailable.png", TrayState.Unavailable);
            SaveToggle(Path.Combine(dir, "toggle.png"));
            return 0;
        }

        /// <summary>One state on a dark and on a light taskbar tile, side by side.</summary>
        static void SaveState(string dir, string name, TrayState state)
        {
            const int tile = 96, gap = 12, icon = 64;
            using (var bmp = new Bitmap(tile * 2 + gap, tile, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = NewGraphics(bmp))
                {
                    DrawTile(g, new RectangleF(0, 0, tile, tile), false, state, icon);
                    DrawTile(g, new RectangleF(tile + gap, 0, tile, tile), true, state, icon);
                }
                bmp.Save(Path.Combine(dir, name), ImageFormat.Png);
            }
        }

        /// <summary>Off -> On, the one thing the app does.</summary>
        static void SaveToggle(string path)
        {
            const int tile = 192, gap = 136, icon = 128;
            using (var bmp = new Bitmap(tile * 2 + gap, tile, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = NewGraphics(bmp))
                {
                    DrawTile(g, new RectangleF(0, 0, tile, tile), false, TrayState.Off, icon);
                    DrawTile(g, new RectangleF(tile + gap, 0, tile, tile), false, TrayState.On, icon);

                    float y = tile / 2f, x0 = tile + 36, x1 = tile + gap - 36, head = 18;
                    using (var pen = new Pen(Arrow, 8f))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        pen.LineJoin = LineJoin.Round;
                        g.DrawLine(pen, x0, y, x1, y);
                        g.DrawLines(pen, new[] { new PointF(x1 - head, y - head), new PointF(x1, y), new PointF(x1 - head, y + head) });
                    }
                }
                bmp.Save(path, ImageFormat.Png);
            }
        }

        static Graphics NewGraphics(Bitmap bmp)
        {
            Graphics g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            return g;
        }

        static void DrawTile(Graphics g, RectangleF r, bool light, TrayState state, int iconSize)
        {
            const float border = 2f;
            RectangleF inner = RectangleF.Inflate(r, -border / 2f, -border / 2f);
            using (GraphicsPath path = RoundedRect(inner, r.Width * 0.18f))
            using (var fill = new SolidBrush(light ? LightTile : DarkTile))
            using (var pen = new Pen(light ? LightBorder : DarkBorder, border))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            using (Bitmap icon = IconRenderer.Render(state, iconSize, light))
                g.DrawImageUnscaled(icon, (int)(r.X + (r.Width - iconSize) / 2f), (int)(r.Y + (r.Height - iconSize) / 2f));
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2f;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
