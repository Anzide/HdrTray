// Draws the tray icons at runtime so they are crisp at any DPI and follow
// the light/dark taskbar theme.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HdrTray
{
    internal enum TrayState
    {
        On,
        Off,
        Paused,       // HDR is enabled but Windows has suspended it
        Unavailable,  // no HDR-capable display
    }

    internal static class IconRenderer
    {
        static readonly Color AmberLight = Color.FromArgb(255, 0xFF, 0xD1, 0x4A);
        static readonly Color AmberDark = Color.FromArgb(255, 0xFF, 0x8A, 0x1F);
        static readonly Color AmberOnLight = Color.FromArgb(255, 0xC8, 0x5A, 0x00);
        static readonly Color InkOnAmber = Color.FromArgb(255, 0x1C, 0x1C, 0x1C);

        public static bool TaskbarIsLight()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = key == null ? null : key.GetValue("SystemUsesLightTheme");
                    return v is int && (int)v != 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static OwnedIcon CreateIcon(TrayState state, int size, bool lightTaskbar)
        {
            using (Bitmap bmp = Render(state, size, lightTaskbar))
            {
                // Going through PNG keeps the alpha channel intact; Windows
                // has accepted PNG icon resources since Vista.
                byte[] png;
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    png = ms.ToArray();
                }
                IntPtr hIcon = CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, size, size, 0);
                if (hIcon == IntPtr.Zero)
                    hIcon = bmp.GetHicon();
                return new OwnedIcon(hIcon);
            }
        }

        public static Bitmap Render(TrayState state, int size, bool lightTaskbar)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;

                Color fg = lightTaskbar ? Color.FromArgb(255, 0x1C, 0x1C, 0x1C) : Color.White;

                float stroke = Math.Max(1f, (float)Math.Round(size / 16f));
                float badgeH = (float)Math.Round(size * 0.72f);
                float top = (float)Math.Floor((size - badgeH) / 2f);
                var outer = new RectangleF(0, top, size, badgeH);
                // Strokes are centred on the path, so inset by half a stroke to stay inside the pixel grid.
                var strokeRect = RectangleF.Inflate(outer, -stroke / 2f, -stroke / 2f);
                float radius = Math.Max(2f, size * 0.2f);

                var textBox = RectangleF.Inflate(outer, -(stroke + size * 0.07f), -(stroke + size * 0.1f));

                switch (state)
                {
                    case TrayState.On:
                        using (GraphicsPath badge = RoundedRect(outer, radius))
                        using (var brush = new LinearGradientBrush(outer, AmberLight, AmberDark, LinearGradientMode.Vertical))
                            g.FillPath(brush, badge);
                        DrawLabel(g, textBox, InkOnAmber);
                        break;

                    case TrayState.Paused:
                        Color amber = lightTaskbar ? AmberOnLight : AmberDark;
                        using (GraphicsPath badge = RoundedRect(strokeRect, radius))
                        using (var pen = new Pen(amber, stroke))
                            g.DrawPath(pen, badge);
                        DrawLabel(g, textBox, amber);
                        break;

                    case TrayState.Off:
                    case TrayState.Unavailable:
                        Color c = state == TrayState.Off ? fg : Color.FromArgb(110, fg);
                        using (GraphicsPath badge = RoundedRect(strokeRect, radius))
                        using (var pen = new Pen(c, stroke))
                            g.DrawPath(pen, badge);
                        DrawLabel(g, textBox, c);
                        break;
                }
            }
            return bmp;
        }

        static void DrawLabel(Graphics g, RectangleF box, Color color)
        {
            using (var path = new GraphicsPath())
            using (FontFamily family = PickFamily())
            {
                path.AddString("HDR", family, (int)FontStyle.Bold, 100f, PointF.Empty, StringFormat.GenericTypographic);
                RectangleF b = path.GetBounds();
                if (b.Width <= 0 || b.Height <= 0)
                    return;

                // Fit into the box; allow up to 20% horizontal condensing so the
                // glyphs can be taller in tiny tray sizes.
                float sy = Math.Min(box.Height / b.Height, box.Width / (b.Width * 0.8f));
                float sx = Math.Min(box.Width / b.Width, sy);
                float w = b.Width * sx, h = b.Height * sy;
                float x = box.X + (box.Width - w) / 2f;
                float y = box.Y + (box.Height - h) / 2f;

                using (var m = new Matrix())
                {
                    m.Translate(x, y);
                    m.Scale(sx, sy);
                    m.Translate(-b.X, -b.Y);
                    path.Transform(m);
                }
                using (var brush = new SolidBrush(color))
                    g.FillPath(brush, path);
            }
        }

        static FontFamily PickFamily()
        {
            try { return new FontFamily("Segoe UI"); }
            catch (ArgumentException) { return new FontFamily(GenericFontFamilies.SansSerif); }
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>
        /// Small-icon size at the taskbar's DPI. The shell blurs icons it has to
        /// rescale, and the taskbar monitor's DPI can differ from the system DPI.
        /// </summary>
        public static int TrayIconSize()
        {
            try
            {
                IntPtr monitor = MonitorFromWindow(FindWindow("Shell_TrayWnd", null), MONITOR_DEFAULTTOPRIMARY);
                uint dpiX, dpiY;
                uint dpi = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY) == 0 ? dpiX : 0;
                if (dpi == 0)
                    dpi = GetDpiForSystem();
                int size = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
                if (size > 0)
                    return size;
            }
            catch (EntryPointNotFoundException)
            {
                // Before Windows 10 1607.
            }
            catch (DllNotFoundException)
            {
            }
            return System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        }

        /// <summary>System DPI, which is what WinForms sizes menu fonts by.</summary>
        public static int SystemDpi()
        {
            try
            {
                uint dpi = GetDpiForSystem();
                if (dpi > 0)
                    return (int)dpi;
            }
            catch (EntryPointNotFoundException)
            {
            }
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                return (int)Math.Round(g.DpiX);
        }

        /// <summary>Writes a multi-resolution .ico (PNG frames) for the executable.</summary>
        public static void WriteIcoFile(string path, TrayState state, bool lightTaskbar, int[] sizes)
        {
            var frames = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
            {
                using (Bitmap bmp = Render(state, sizes[i], lightTaskbar))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    frames[i] = ms.ToArray();
                }
            }

            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((ushort)0);            // reserved
                w.Write((ushort)1);            // type: icon
                w.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0);          // palette size
                    w.Write((byte)0);          // reserved
                    w.Write((ushort)1);        // planes
                    w.Write((ushort)32);       // bpp
                    w.Write(frames[i].Length);
                    w.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (byte[] f in frames)
                    w.Write(f);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr CreateIconFromResourceEx(byte[] presbits, uint dwResSize, bool fIcon, uint dwVer, int cxDesired, int cyDesired, uint flags);

        const int SM_CXSMICON = 49;
        const uint MONITOR_DEFAULTTOPRIMARY = 1;
        const int MDT_EFFECTIVE_DPI = 0;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("shcore.dll")]
        static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        static extern int GetSystemMetricsForDpi(int index, uint dpi);
    }

    /// <summary>An Icon plus the HICON it wraps; Icon.FromHandle never frees the handle itself.</summary>
    internal sealed class OwnedIcon : IDisposable
    {
        IntPtr handle;
        public readonly Icon Icon;

        public OwnedIcon(IntPtr hIcon)
        {
            handle = hIcon;
            Icon = Icon.FromHandle(hIcon);
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero)
                return;
            Icon.Dispose();
            IconRenderer.DestroyIcon(handle);
            handle = IntPtr.Zero;
        }
    }
}
