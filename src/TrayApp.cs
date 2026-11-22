// The tray icon: left-click toggles HDR, right-click opens the menu.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HdrTray
{
    internal sealed class TrayApp : ApplicationContext, IMessageFilter
    {
        const int WM_CLOSE = 0x0010;

        const int PollIntervalMs = 2000;
        // Swallow stray clicks (e.g. an impatient double-click) right after a switch.
        static readonly TimeSpan Cooldown = TimeSpan.FromMilliseconds(800);
        // How long after start-up / an Explorer restart to keep checking that
        // the icon really made it into the notification area.
        static readonly TimeSpan IconVerifyWindow = TimeSpan.FromMinutes(2);

        readonly SynchronizationContext ui;
        readonly NotifyIcon notifyIcon;
        readonly ContextMenuStrip menu;
        readonly Font boldFont;
        // Transparent image that makes WinForms widen the menu's image margin
        // to ImageScalingSize, so the DPI-scaled check mark fits.
        readonly Bitmap marginPlaceholder = new Bitmap(1, 1);
        readonly MessageWindow messageWindow;
        readonly System.Windows.Forms.Timer pollTimer;
        readonly System.Windows.Forms.Timer settleTimer;
        readonly RegisteredWaitHandle activateWait;

        List<DisplayInfo> displays = new List<DisplayInfo>();
        TrayState state;
        bool lightTaskbar;
        int iconSize;
        OwnedIcon currentIcon;
        bool busy;
        bool exitPending;
        DateTime cooldownUntil = DateTime.MinValue;
        DateTime verifyIconUntil;
        bool disposed;

        /// <param name="activate">Signalled when the user launches a second copy.</param>
        public TrayApp(WaitHandle activate)
        {
            ui = SynchronizationContext.Current;

            menu = new ContextMenuStrip();
            menu.Renderer = new MenuRenderer();
            int imageSize = 16 * IconRenderer.SystemDpi() / 96;
            menu.ImageScalingSize = new Size(imageSize, imageSize);
            boldFont = new Font(menu.Font, FontStyle.Bold);
            // The strip is populated before NotifyIcon positions it (on mouse
            // down, see below). WinForms pre-cancels Opening for an empty strip.
            menu.Opening += (s, e) => { e.Cancel = menu.Items.Count == 0; };

            notifyIcon = new NotifyIcon();
            notifyIcon.ContextMenuStrip = menu;
            notifyIcon.MouseClick += OnMouseClick;
            // NotifyIcon shows the menu on right-button *up* and places it by its
            // current size, so rebuild on button down to get the position right.
            notifyIcon.MouseDown += (s, e) => { if (e.Button == MouseButtons.Right) BuildMenu(); };

            pollTimer = new System.Windows.Forms.Timer();
            pollTimer.Interval = PollIntervalMs;
            pollTimer.Tick += delegate
            {
                VerifyIconRegistered();
                if (!busy) RefreshState(false);
            };

            // Coalesces bursts of WM_DISPLAYCHANGE / power events.
            settleTimer = new System.Windows.Forms.Timer();
            settleTimer.Interval = 600;
            settleTimer.Tick += delegate { settleTimer.Stop(); if (!busy) RefreshState(false); };

            messageWindow = new MessageWindow(this);
            Application.AddMessageFilter(this);

            lightTaskbar = IconRenderer.TaskbarIsLight();
            RefreshState(true);
            BuildMenu();   // so a keyboard-opened menu is never empty
            notifyIcon.Visible = true;
            verifyIconUntil = DateTime.UtcNow + IconVerifyWindow;
            pollTimer.Start();

            if (activate != null)
            {
                activateWait = ThreadPool.RegisterWaitForSingleObject(activate, delegate
                {
                    ui.Post(delegate { if (!disposed) ShowInfo(Strings.AlreadyRunning); }, null);
                }, null, Timeout.Infinite, false);
            }

            Autostart.RepairPath();
            ShowWelcomeOnce();
        }

        // ---- State -------------------------------------------------------

        void RefreshState(bool forceIcon)
        {
            List<DisplayInfo> list;
            try
            {
                list = HdrController.GetDisplays();
            }
            catch (Exception)
            {
                // Transient failures happen while the display topology is
                // changing; keep the last known state and retry on next tick.
                if (currentIcon == null)
                {
                    state = TrayState.Unavailable;
                    iconSize = IconRenderer.TrayIconSize();
                    UpdateIcon();
                    UpdateTooltip();
                }
                return;
            }

            // A display whose state could not be read right now keeps its
            // previous state instead of flickering to "unsupported".
            foreach (DisplayInfo d in list)
            {
                if (!d.QueryFailed)
                    continue;
                foreach (DisplayInfo old in displays)
                {
                    if (old.Key == d.Key)
                    {
                        d.Api = old.Api;
                        d.HdrSupported = old.HdrSupported;
                        d.HdrEnabled = old.HdrEnabled;
                        d.HdrActive = old.HdrActive;
                        d.LimitedByPolicy = old.LimitedByPolicy;
                        break;
                    }
                }
            }
            displays = list;

            TrayState newState = ComputeState(list);
            int size = IconRenderer.TrayIconSize();
            if (forceIcon || newState != state || size != iconSize || currentIcon == null)
            {
                state = newState;
                iconSize = size;
                UpdateIcon();
            }
            UpdateTooltip();
        }

        static TrayState ComputeState(List<DisplayInfo> list)
        {
            bool anySupported = false, anyEnabled = false, anyActive = false;
            foreach (var d in list)
            {
                if (!d.HdrSupported)
                    continue;
                anySupported = true;
                anyEnabled |= d.HdrEnabled;
                anyActive |= d.HdrActive;
            }
            if (!anySupported) return TrayState.Unavailable;
            if (anyActive) return TrayState.On;
            if (anyEnabled) return TrayState.Paused;
            return TrayState.Off;
        }

        void UpdateIcon()
        {
            // NotifyIcon keeps using the HICON it was given (e.g. to re-add the
            // icon after an Explorer restart), so free the old one only after
            // the new one is in place.
            OwnedIcon old = currentIcon;
            currentIcon = IconRenderer.CreateIcon(state, iconSize, lightTaskbar);
            notifyIcon.Icon = currentIcon.Icon;
            if (old != null)
                old.Dispose();
        }

        void UpdateTooltip()
        {
            string text;
            if (busy)
                text = Strings.TooltipBusy;
            else
            {
                switch (state)
                {
                    case TrayState.On: text = Strings.TooltipOn; break;
                    case TrayState.Paused: text = Strings.TooltipPaused; break;
                    case TrayState.Off: text = Strings.TooltipOff; break;
                    default: text = Strings.TooltipUnavailable; break;
                }
                string hint = "\n" + Strings.ClickToToggle;
                if (state != TrayState.Unavailable && text.Length + hint.Length <= 63)
                    text += hint;
            }
            // NotifyIcon.Text throws above 63 characters on .NET Framework.
            if (text.Length > 63)
                text = text.Substring(0, 62) + "…";
            notifyIcon.Text = text;
        }

        // ---- Switching ---------------------------------------------------

        void OnMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                ToggleAll();
        }

        bool CanSwitch
        {
            get { return !busy && !exitPending && DateTime.UtcNow >= cooldownUntil; }
        }

        void ToggleAll()
        {
            if (!CanSwitch)
                return;

            RefreshState(false);
            var capable = new List<DisplayInfo>();
            bool anyEnabled = false;
            foreach (var d in displays)
            {
                if (!d.HdrSupported)
                    continue;
                capable.Add(d);
                anyEnabled |= d.HdrEnabled;
            }
            if (capable.Count == 0)
            {
                ShowError(Strings.AppName, Strings.NoHdrDisplay);
                return;
            }
            // If HDR is on anywhere, a click means "off"; otherwise "on".
            // This keeps multiple displays in step instead of flipping each.
            Switch(capable, !anyEnabled);
        }

        void Switch(List<DisplayInfo> targets, bool enable)
        {
            var todo = targets.FindAll(d => d.HdrEnabled != enable);
            if (todo.Count == 0)
                return;

            busy = true;
            UpdateTooltip();

            // Switching blocks while Windows reconfigures the display and the
            // new state settles, so keep it off the UI thread.
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> errors;
                try
                {
                    errors = HdrController.Switch(todo, enable);
                }
                catch (Exception ex)
                {
                    errors = new List<string> { ex.Message };
                }
                ui.Post(delegate
                {
                    if (disposed)
                        return;
                    busy = false;
                    if (exitPending)
                    {
                        ExitThread();
                        return;
                    }
                    cooldownUntil = DateTime.UtcNow + Cooldown;
                    RefreshState(false);
                    if (errors.Count > 0)
                        ShowError(Strings.ToggleFailed, string.Join("\n", errors.ToArray()));
                }, null);
            });
        }

        void ShowError(string title, string message)
        {
            if (message.Length > 255)
                message = message.Substring(0, 254) + "…";
            notifyIcon.ShowBalloonTip(5000, title, message, ToolTipIcon.Warning);
        }

        void ShowInfo(string message)
        {
            notifyIcon.ShowBalloonTip(5000, Strings.AppName, message, ToolTipIcon.Info);
        }

        // ---- Menu --------------------------------------------------------

        void BuildMenu()
        {
            if (menu.Visible)
                return;
            if (!busy)
                RefreshState(false);

            while (menu.Items.Count > 0)
                menu.Items[0].Dispose();   // disposing also removes it from the strip

            bool anySupported = false, anyEnabled = false;
            foreach (var d in displays)
            {
                if (!d.HdrSupported) continue;
                anySupported = true;
                anyEnabled |= d.HdrEnabled;
            }

            var toggle = new ToolStripMenuItem(anyEnabled ? Strings.TurnOffHdr : Strings.TurnOnHdr);
            toggle.Font = boldFont;
            toggle.Image = marginPlaceholder;
            toggle.ImageScaling = ToolStripItemImageScaling.SizeToFit;
            toggle.Enabled = anySupported && CanSwitch;
            toggle.Click += delegate { ToggleAll(); };
            menu.Items.Add(toggle);

            // Per-display control only matters with more than one display.
            if (displays.Count > 1)
            {
                menu.Items.Add(new ToolStripSeparator());
                foreach (var d in displays)
                {
                    DisplayInfo display = d;
                    var item = new ToolStripMenuItem(display.Name.Replace("&", "&&"));
                    item.ShortcutKeyDisplayString = StateText(display);
                    item.Checked = display.HdrSupported && display.HdrEnabled;
                    item.Enabled = display.HdrSupported && CanSwitch;
                    item.Click += delegate
                    {
                        if (CanSwitch)
                            Switch(new List<DisplayInfo> { display }, !display.HdrEnabled);
                    };
                    menu.Items.Add(item);
                }
            }

            menu.Items.Add(new ToolStripSeparator());

            var settings = new ToolStripMenuItem(Strings.OpenDisplaySettings);
            settings.Click += delegate { OpenDisplaySettings(); };
            menu.Items.Add(settings);

            var autostart = new ToolStripMenuItem(Strings.StartWithWindows);
            autostart.Checked = Autostart.IsEnabled();
            autostart.Click += delegate
            {
                try
                {
                    Autostart.SetEnabled(!Autostart.IsEnabled());
                }
                catch (Exception ex)
                {
                    ShowError(Strings.AutostartFailed, ex.Message);
                }
            };
            menu.Items.Add(autostart);

            var exit = new ToolStripMenuItem(Strings.Exit);
            exit.Click += delegate { RequestExit(); };
            menu.Items.Add(exit);
        }

        /// <summary>
        /// "taskkill /im HdrTray.exe" (without /F) posts WM_CLOSE to just one
        /// top-level window, typically NotifyIcon's private one, which would
        /// only destroy the icon and leave the process running. Treat a posted
        /// WM_CLOSE to any of our windows as Exit; there is no closable UI.
        /// </summary>
        bool IMessageFilter.PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_CLOSE)
                return false;
            RequestExit();
            return true;
        }

        internal void RequestExit()
        {
            // Quitting mid-switch would abandon the worker between displays;
            // finish the switch first.
            exitPending = true;   // also blocks clicks queued before WM_QUIT
            if (!busy)
                ExitThread();
        }

        static string StateText(DisplayInfo d)
        {
            if (!d.HdrSupported) return Strings.StateUnsupported;
            if (d.HdrEnabled && !d.HdrActive) return Strings.StatePaused;
            return d.HdrEnabled ? Strings.StateOn : Strings.StateOff;
        }

        static void OpenDisplaySettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true });
            }
            catch (Exception)
            {
            }
        }

        void ShowWelcomeOnce()
        {
            // Windows 11 puts new tray icons in the hidden overflow, so a first
            // launch can otherwise look like nothing happened.
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\HDR Tray"))
                {
                    if (key.GetValue("WelcomeShown") != null)
                        return;
                    key.SetValue("WelcomeShown", 1, RegistryValueKind.DWord);
                }
                ShowInfo(Strings.Welcome);
            }
            catch (Exception)
            {
            }
        }

        // ---- Notification-area registration -----------------------------

        static readonly FieldInfo NotifyIconWindowField =
            typeof(NotifyIcon).GetField("window", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo NotifyIconIdField =
            typeof(NotifyIcon).GetField("id", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// WinForms ignores Shell_NotifyIcon failures. At logon Explorer can
        /// time out NIM_ADD while it is still busy starting up, after the
        /// TaskbarCreated broadcast that would make NotifyIcon retry. Check
        /// for a while and re-add the icon if it is missing.
        /// </summary>
        void VerifyIconRegistered()
        {
            if (DateTime.UtcNow > verifyIconUntil || !notifyIcon.Visible)
                return;
            if (TrayIconExists() == false)
            {
                notifyIcon.Visible = false;
                notifyIcon.Visible = true;
            }
        }

        /// <returns>null if it cannot be determined.</returns>
        bool? TrayIconExists()
        {
            try
            {
                if (NotifyIconWindowField == null || NotifyIconIdField == null)
                    return null;
                var window = NotifyIconWindowField.GetValue(notifyIcon) as NativeWindow;
                if (window == null || window.Handle == IntPtr.Zero)
                    return null;

                // A no-op modify succeeds only if the icon is registered.
                var data = new NOTIFYICONDATA();
                data.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
                data.hWnd = window.Handle;
                data.uID = (int)NotifyIconIdField.GetValue(notifyIcon);
                if (Shell_NotifyIcon(NIM_MODIFY, ref data))
                    return true;
                // A busy Explorer (the logon case) can time out; that says
                // nothing about whether the icon exists.
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_TIMEOUT || err == unchecked((int)0x800705B4))
                    return null;
                return false;
            }
            catch (Exception)
            {
                return null;
            }
        }

        const int NIM_MODIFY = 1;
        const int ERROR_TIMEOUT = 1460;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

        // ---- Window messages --------------------------------------------

        internal void OnDisplayChanged()
        {
            settleTimer.Stop();
            settleTimer.Start();
        }

        internal void OnTaskbarCreated()
        {
            // Explorer restarted or the taskbar DPI changed. NotifyIcon re-adds
            // itself; re-render in case the icon size changed, and verify the
            // re-add actually worked.
            verifyIconUntil = DateTime.UtcNow + IconVerifyWindow;
            if (!busy)
                RefreshState(true);
        }

        internal void OnThemeChanged()
        {
            bool light = IconRenderer.TaskbarIsLight();
            if (light != lightTaskbar)
            {
                lightTaskbar = light;
                UpdateIcon();
            }
        }

        protected override void ExitThreadCore()
        {
            notifyIcon.Visible = false;
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                Application.RemoveMessageFilter(this);
                if (activateWait != null)
                    activateWait.Unregister(null);
                pollTimer.Dispose();
                settleTimer.Dispose();
                messageWindow.DestroyHandle();
                notifyIcon.Visible = false;
                notifyIcon.Dispose();
                menu.Dispose();
                boldFont.Dispose();
                marginPlaceholder.Dispose();
                if (currentIcon != null)
                    currentIcon.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// Hidden top-level window: broadcast messages such as WM_DISPLAYCHANGE
        /// never reach message-only windows.
        /// </summary>
        sealed class MessageWindow : NativeWindow
        {
            const int WM_DISPLAYCHANGE = 0x007E;
            const int WM_SETTINGCHANGE = 0x001A;
            const int WM_POWERBROADCAST = 0x0218;
            const int WM_DPICHANGED = 0x02E0;
            const int WS_POPUP = unchecked((int)0x80000000);

            static readonly int WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");

            readonly TrayApp owner;

            public MessageWindow(TrayApp owner)
            {
                this.owner = owner;
                // Never shown.
                var cp = new CreateParams();
                cp.Style = WS_POPUP;
                CreateHandle(cp);

                // If running elevated, UIPI would drop Explorer's TaskbarCreated
                // broadcast. With it, OnTaskbarCreated arms the icon check,
                // which re-adds the icon after an Explorer restart.
                if (WM_TASKBARCREATED != 0)
                {
                    try { ChangeWindowMessageFilterEx(Handle, WM_TASKBARCREATED, MSGFLT_ALLOW, IntPtr.Zero); }
                    catch (EntryPointNotFoundException) { }
                }
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_TASKBARCREATED && WM_TASKBARCREATED != 0)
                    owner.OnTaskbarCreated();

                switch (m.Msg)
                {
                    case WM_CLOSE:   // sent rather than posted; see PreFilterMessage
                        owner.RequestExit();
                        return;
                    case WM_DISPLAYCHANGE:
                    case WM_POWERBROADCAST:   // AC/battery changes can pause or resume HDR
                    case WM_DPICHANGED:
                        owner.OnDisplayChanged();
                        break;
                    case WM_SETTINGCHANGE:
                        if (m.LParam != IntPtr.Zero)
                        {
                            string area = Marshal.PtrToStringUni(m.LParam);
                            if (area == "ImmersiveColorSet")
                                owner.OnThemeChanged();
                        }
                        break;
                }
                base.WndProc(ref m);
            }

            const int MSGFLT_ALLOW = 1;

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            static extern int RegisterWindowMessage(string message);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, int message, int action, IntPtr changeInfo);
        }

        /// <summary>
        /// .NET Framework only scales ToolStrip glyphs with an app.config
        /// opt-in, which would make this more than a single exe. Draw the check
        /// mark as vectors instead, sized to the (DPI-scaled) image area.
        /// </summary>
        sealed class MenuRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                Rectangle r = e.ImageRectangle;
                float s = Math.Min(r.Width, r.Height);
                float x = r.X + (r.Width - s) / 2f;
                float y = r.Y + (r.Height - s) / 2f;
                Color color = e.Item.Enabled ? e.Item.ForeColor : SystemColors.GrayText;

                SmoothingMode old = e.Graphics.SmoothingMode;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(color, Math.Max(1.5f, s / 10f)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    e.Graphics.DrawLines(pen, new[]
                    {
                        new PointF(x + s * 0.20f, y + s * 0.53f),
                        new PointF(x + s * 0.41f, y + s * 0.74f),
                        new PointF(x + s * 0.80f, y + s * 0.30f),
                    });
                }
                e.Graphics.SmoothingMode = old;
            }
        }
    }
}
