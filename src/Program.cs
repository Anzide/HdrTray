using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HdrTray
{
    internal static class Program
    {
        const string MutexName = @"Local\HdrTray.SingleInstance.9C3E7A51";
        const string ActivateEventName = @"Local\HdrTray.Activate.9C3E7A51";

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length > 0)
                return RunCommand(args[0]);

            bool createdNew;
            Mutex mutex;
            try
            {
                mutex = new Mutex(true, MutexName, out createdNew);
            }
            catch (UnauthorizedAccessException)
            {
                return 0;   // held by an elevated instance
            }

            using (mutex)
            using (EventWaitHandle activate = OpenActivateEvent())
            {
                if (!createdNew)
                {
                    // Already running: let the first instance say so, since its
                    // icon may be hidden in the overflow.
                    if (activate != null)
                        activate.Set();
                    return 0;
                }

                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                // Keep the tray alive on an unexpected UI-thread exception.
                Application.ThreadException += delegate { };
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

                using (var app = new TrayApp(activate))
                    Application.Run(app);

                mutex.ReleaseMutex();
            }
            return 0;
        }

        static EventWaitHandle OpenActivateEvent()
        {
            try
            {
                return new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Command-line mode, handy for shortcuts and scripts.</summary>
        static int RunCommand(string arg)
        {
            string cmd = arg.TrimStart('-', '/').ToLowerInvariant();
            if (cmd != "toggle" && cmd != "on" && cmd != "off")
            {
                MessageBox.Show(Strings.Usage, Strings.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return cmd == "help" || cmd == "h" || cmd == "?" ? 0 : 2;
            }

            try
            {
                // A read can fail transiently, e.g. while a previous switch is
                // still settling. Retry briefly; never decide from a partial view.
                List<DisplayInfo> displays;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    displays = HdrController.GetDisplays();
                    if (!displays.Exists(d => d.QueryFailed) || sw.ElapsedMilliseconds >= 1500)
                        break;
                    Thread.Sleep(100);
                }
                if (displays.Exists(d => d.QueryFailed))
                {
                    MessageBox.Show(Strings.ReadFailed, Strings.ToggleFailed, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 3;
                }

                var capable = new List<DisplayInfo>();
                bool anyEnabled = false;
                foreach (var d in displays)
                {
                    if (!d.HdrSupported) continue;
                    capable.Add(d);
                    anyEnabled |= d.HdrEnabled;
                }
                if (capable.Count == 0)
                {
                    MessageBox.Show(Strings.NoHdrDisplay, Strings.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
                }

                bool enable = cmd == "on" || (cmd == "toggle" && !anyEnabled);
                var todo = capable.FindAll(d => d.HdrEnabled != enable);
                List<string> errors = HdrController.Switch(todo, enable);
                if (errors.Count > 0)
                {
                    MessageBox.Show(string.Join("\n", errors.ToArray()), Strings.ToggleFailed, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 3;
                }
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Strings.ToggleFailed, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 3;
            }
        }
    }

    /// <summary>Start-with-Windows via the per-user Run key.</summary>
    internal static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        // Task Manager's "Disable" on the Startup tab is recorded here.
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string ValueName = "HDR Tray";

        static string ExePath
        {
            // Application.ExecutablePath URL-unescapes the path ("%41" -> "A").
            get { return System.Reflection.Assembly.GetEntryAssembly().Location; }
        }

        static string Command
        {
            get { return "\"" + ExePath + "\""; }
        }

        public static bool IsEnabled()
        {
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                if (run == null || run.GetValue(ValueName) == null)
                    return false;
            }
            using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey))
            {
                byte[] flags = approved == null ? null : approved.GetValue(ValueName) as byte[];
                // Odd first byte = disabled in Task Manager.
                return flags == null || flags.Length == 0 || (flags[0] & 1) == 0;
            }
        }

        public static void SetEnabled(bool enable)
        {
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable)
                    run.SetValue(ValueName, Command, RegistryValueKind.String);
                else
                    run.DeleteValue(ValueName, false);
            }
            // Clear any Task Manager override so the Run entry is honoured.
            using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
            {
                if (approved != null)
                    approved.DeleteValue(ValueName, false);
            }
        }

        /// <summary>
        /// If the registered exe no longer exists (it was moved or renamed),
        /// point the Run entry at this one. Another copy that still exists is
        /// left alone, so running a test build does not take over autostart.
        /// </summary>
        public static void RepairPath()
        {
            try
            {
                using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (run == null)
                        return;
                    string current = run.GetValue(ValueName) as string;
                    if (current == null || string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                        return;
                    string registered = UnquoteExe(current);
                    if (registered.Length > 0 && !System.IO.File.Exists(registered))
                        run.SetValue(ValueName, Command, RegistryValueKind.String);
                }
            }
            catch (Exception)
            {
            }
        }

        static string UnquoteExe(string command)
        {
            command = command.Trim();
            if (command.StartsWith("\""))
            {
                int end = command.IndexOf('"', 1);
                return end > 0 ? command.Substring(1, end - 1) : command.Trim('"');
            }
            return command;
        }
    }
}
