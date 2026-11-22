// Windows DisplayConfig interop for reading and switching HDR.
//
// Windows 11 24H2 (build 26100) split "advanced color" into HDR and WCG and
// added GET_ADVANCED_COLOR_INFO_2 / SET_HDR_STATE. On those builds the old
// SET_ADVANCED_COLOR_STATE may toggle Auto Color Management instead of HDR,
// so the API family is picked once from the OS build and never mixed.
//
// None of these packets are documented on learn.microsoft.com; layouts come
// from wingdi.h in the Windows SDK 10.0.26100.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace HdrTray
{
    internal enum HdrApi
    {
        None,
        Legacy,   // GET_ADVANCED_COLOR_INFO / SET_ADVANCED_COLOR_STATE (Win10 .. Win11 23H2)
        Modern,   // GET_ADVANCED_COLOR_INFO_2 / SET_HDR_STATE (Win11 24H2+)
    }

    internal sealed class DisplayInfo
    {
        public NativeMethods.LUID AdapterId;
        public uint TargetId;
        public string Name;
        public HdrApi Api;

        public bool HdrSupported;
        // The user's "Use HDR" setting; this is what a toggle flips.
        public bool HdrEnabled;
        // HDR is actually being output right now. Can differ from HdrEnabled
        // when Windows suspends HDR, e.g. on battery power.
        public bool HdrActive;
        public bool LimitedByPolicy;
        // Windows could not report the state this time (e.g. mid mode change).
        public bool QueryFailed;

        // Raw values, for diagnostics.
        public uint RawFlags;
        public uint BitsPerColorChannel;
        public uint ActiveColorMode;

        public string Key
        {
            get { return AdapterId.HighPart.ToString("X8") + AdapterId.LowPart.ToString("X8") + ":" + TargetId.ToString(); }
        }

        public DisplayInfo Clone()
        {
            return (DisplayInfo)MemberwiseClone();
        }
    }

    internal static class HdrController
    {
        public const int ModernMinBuild = 26100;
        public static readonly int OsBuild = NativeMethods.GetOsBuild();
        public static readonly HdrApi PreferredApi = OsBuild >= ModernMinBuild ? HdrApi.Modern : HdrApi.Legacy;

        // Mode switches renegotiate the display link; the new state can take
        // a few seconds to become readable.
        const int SettleBudgetMs = 4000;
        const int FailedCallBudgetMs = 1000;
        const int PollMs = 100;

        public static List<DisplayInfo> GetDisplays()
        {
            var result = new List<DisplayInfo>();
            var sources = new List<NativeMethods.DISPLAYCONFIG_PATH_SOURCE_INFO>();
            var seen = new HashSet<string>();

            foreach (var path in QueryActivePaths())
            {
                if (path.targetInfo.targetAvailable == 0)
                    continue;

                var info = new DisplayInfo();
                info.AdapterId = path.targetInfo.adapterId;
                info.TargetId = path.targetInfo.id;
                if (!seen.Add(info.Key))
                    continue;   // clone mode: several sources on one target

                info.Name = GetTargetName(info.AdapterId, info.TargetId, path.targetInfo.outputTechnology);
                info.QueryFailed = !ReadHdrState(info);
                result.Add(info);
                sources.Add(path.sourceInfo);
            }

            // Give unnamed displays a label, and tell identical monitors apart
            // by their GDI name (DISPLAY1, DISPLAY2, ...).
            var nameCount = new Dictionary<string, int>();
            foreach (var d in result)
            {
                if (!string.IsNullOrEmpty(d.Name))
                    nameCount[d.Name] = (nameCount.ContainsKey(d.Name) ? nameCount[d.Name] : 0) + 1;
            }
            var suffix = new string[result.Count];
            for (int i = 0; i < result.Count; i++)
            {
                string name = result[i].Name;
                if (!string.IsNullOrEmpty(name) && nameCount[name] > 1)
                    suffix[i] = GetSourceGdiName(sources[i]) ?? (i + 1).ToString();
            }
            // In clone mode several targets share one source, hence one GDI name.
            for (int i = 0; i < result.Count; i++)
            {
                if (suffix[i] == null)
                    continue;
                for (int j = 0; j < result.Count; j++)
                {
                    if (j != i && result[j].Name == result[i].Name && suffix[j] == suffix[i])
                    {
                        suffix[i] += " #" + (i + 1);
                        break;
                    }
                }
            }
            int unnamed = 0;
            for (int i = 0; i < result.Count; i++)
            {
                DisplayInfo d = result[i];
                if (string.IsNullOrEmpty(d.Name))
                {
                    unnamed++;
                    d.Name = Strings.DisplayN(unnamed);
                }
                else if (suffix[i] != null)
                {
                    d.Name += " (" + suffix[i] + ")";
                }
            }
            return result;
        }

        /// <summary>
        /// Switches HDR on the given displays and waits until Windows reports the
        /// new state. Blocks for up to a few seconds per display, so call it off
        /// the UI thread. Returns one message per display that did not switch.
        /// </summary>
        public static List<string> Switch(IList<DisplayInfo> targets, bool enable)
        {
            var errors = new List<string>();
            bool anyChanged = false;

            // Windows recentres the mouse cursor during the mode change.
            NativeMethods.POINT cursor;
            bool haveCursor = NativeMethods.GetCursorPos(out cursor);

            foreach (DisplayInfo target in targets)
            {
                DisplayInfo d = target.Clone();
                if (d.Api == HdrApi.None || !d.HdrSupported)
                {
                    errors.Add(Strings.NotSupportedOn(d.Name));
                    continue;
                }

                int rc = SendSetHdr(d, enable);
                if (haveCursor)
                    RestoreCursorIfRecentred(cursor);

                // The return code is not trustworthy: some drivers report
                // ERROR_ACCESS_DENIED for SET_HDR_STATE yet apply the change.
                // Success is whatever the display reports afterwards.
                bool callLooksOk = rc == NativeMethods.ERROR_SUCCESS || rc == NativeMethods.ERROR_ACCESS_DENIED;
                if (WaitForState(d, enable, callLooksOk ? SettleBudgetMs : FailedCallBudgetMs))
                {
                    anyChanged = true;
                }
                else
                {
                    string reason = rc != NativeMethods.ERROR_SUCCESS ? new Win32Exception(rc).Message : Strings.NoEffect;
                    errors.Add(d.Name + ": " + reason);
                }
            }

            if (anyChanged)
            {
                RefreshCalibration();
                if (haveCursor)
                    RestoreCursorIfRecentred(cursor);
            }
            return errors;
        }

        static int SendSetHdr(DisplayInfo info, bool enable)
        {
            if (info.Api == HdrApi.Modern)
            {
                var packet = new NativeMethods.DISPLAYCONFIG_SET_HDR_STATE();
                packet.header.type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE;
                packet.header.size = (uint)Marshal.SizeOf(typeof(NativeMethods.DISPLAYCONFIG_SET_HDR_STATE));
                packet.header.adapterId = info.AdapterId;
                packet.header.id = info.TargetId;
                packet.value = enable ? 1u : 0u;
                return NativeMethods.DisplayConfigSetDeviceInfo(ref packet);
            }
            else
            {
                var packet = new NativeMethods.DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE();
                packet.header.type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE;
                packet.header.size = (uint)Marshal.SizeOf(typeof(NativeMethods.DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE));
                packet.header.adapterId = info.AdapterId;
                packet.header.id = info.TargetId;
                packet.value = enable ? 1u : 0u;
                return NativeMethods.DisplayConfigSetDeviceInfo(ref packet);
            }
        }

        static bool WaitForState(DisplayInfo d, bool enable, int budgetMs)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                // A failed read mid-switch just means "not settled yet".
                if (ReadHdrState(d) && d.HdrEnabled == enable)
                    return true;
                if (sw.ElapsedMilliseconds >= budgetMs)
                    return false;
                Thread.Sleep(PollMs);
            }
        }

        static void RestoreCursorIfRecentred(NativeMethods.POINT saved)
        {
            // The recentre can land during or after SetDeviceInfo. Only move
            // the cursor back if it sits exactly at a screen centre, so a user
            // who moved the mouse meanwhile is left alone.
            // (Plain Win32 rather than WinForms Screen: in CLI mode this runs
            // on an STA thread without a message loop, where Screen would
            // create an unpumped SystemEvents window.)
            NativeMethods.POINT now;
            if (!NativeMethods.GetCursorPos(out now) || (now.X == saved.X && now.Y == saved.Y))
                return;
            IntPtr monitor = NativeMethods.MonitorFromPoint(now, NativeMethods.MONITOR_DEFAULTTONULL);
            if (monitor == IntPtr.Zero)
                return;
            var mi = new NativeMethods.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO));
            if (!NativeMethods.GetMonitorInfo(monitor, ref mi))
                return;
            int cx = mi.rcMonitor.Left + (mi.rcMonitor.Right - mi.rcMonitor.Left) / 2;
            int cy = mi.rcMonitor.Top + (mi.rcMonitor.Bottom - mi.rcMonitor.Top) / 2;
            if (Math.Abs(now.X - cx) <= 2 && Math.Abs(now.Y - cy) <= 2)
                NativeMethods.SetCursorPos(saved.X, saved.Y);
        }

        /// <summary>
        /// Windows does not re-apply the per-mode colour profile after a
        /// programmatic HDR switch until Settings > Display is opened. The
        /// undocumented mscms!InternalRefreshCalibration fixes that (same
        /// approach as res2k/HDRTray). Its signature is not public, so only call
        /// it where extra arguments are harmless: the 64-bit register ABI.
        /// </summary>
        static void RefreshCalibration()
        {
            if (!Environment.Is64BitProcess)
                return;
            try
            {
                IntPtr module = NativeMethods.LoadLibraryEx("mscms.dll", IntPtr.Zero, NativeMethods.LOAD_LIBRARY_SEARCH_SYSTEM32);
                if (module == IntPtr.Zero)
                    return;
                IntPtr fn = NativeMethods.GetProcAddress(module, "InternalRefreshCalibration");
                if (fn == IntPtr.Zero)
                    return;
                var refresh = (NativeMethods.InternalRefreshCalibrationFn)Marshal.GetDelegateForFunctionPointer(
                    fn, typeof(NativeMethods.InternalRefreshCalibrationFn));
                refresh(IntPtr.Zero, UIntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception)
            {
                // Cosmetic only.
            }
        }

        static NativeMethods.DISPLAYCONFIG_PATH_INFO[] QueryActivePaths()
        {
            // The topology can change between the size query and the actual
            // query; Windows then reports ERROR_INSUFFICIENT_BUFFER and we retry.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                uint pathCount, modeCount;
                int err = NativeMethods.GetDisplayConfigBufferSizes(NativeMethods.QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount);
                if (err != NativeMethods.ERROR_SUCCESS)
                    throw new Win32Exception(err);
                // QueryDisplayConfig rejects zero-sized buffers; no active
                // display (e.g. monitor switched off) is a valid answer.
                if (pathCount == 0)
                    return new NativeMethods.DISPLAYCONFIG_PATH_INFO[0];

                var paths = new NativeMethods.DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new NativeMethods.DISPLAYCONFIG_MODE_INFO[modeCount];
                err = NativeMethods.QueryDisplayConfig(NativeMethods.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (err == NativeMethods.ERROR_INSUFFICIENT_BUFFER)
                    continue;
                if (err != NativeMethods.ERROR_SUCCESS)
                    throw new Win32Exception(err);

                if (pathCount < paths.Length)
                    Array.Resize(ref paths, (int)pathCount);
                return paths;
            }
            throw new Win32Exception(NativeMethods.ERROR_INSUFFICIENT_BUFFER);
        }

        /// <summary>Reads the HDR state into <paramref name="info"/>; false if Windows could not report it.</summary>
        static bool ReadHdrState(DisplayInfo info)
        {
            info.Api = HdrApi.None;
            info.HdrSupported = false;
            info.HdrEnabled = false;
            info.HdrActive = false;
            info.LimitedByPolicy = false;
            info.RawFlags = 0;
            info.BitsPerColorChannel = 0;
            info.ActiveColorMode = 0;

            if (PreferredApi == HdrApi.Modern)
            {
                var modern = new NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2();
                modern.header.type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2;
                modern.header.size = (uint)Marshal.SizeOf(typeof(NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2));
                modern.header.adapterId = info.AdapterId;
                modern.header.id = info.TargetId;
                if (NativeMethods.DisplayConfigGetDeviceInfo(ref modern) != NativeMethods.ERROR_SUCCESS)
                    return false;

                uint v = modern.value;
                info.Api = HdrApi.Modern;
                info.LimitedByPolicy = (v & 0x08) != 0;   // advancedColorLimitedByPolicy
                info.HdrSupported = (v & 0x10) != 0;      // highDynamicRangeSupported
                info.HdrEnabled = (v & 0x20) != 0;        // highDynamicRangeUserEnabled
                // advancedColorActive (0x02) is also set in WCG mode, so the
                // colour mode is the only reliable "HDR is on" signal.
                info.HdrActive = modern.activeColorMode == NativeMethods.DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR;
                info.RawFlags = v;
                info.BitsPerColorChannel = modern.bitsPerColorChannel;
                info.ActiveColorMode = modern.activeColorMode;
                return true;
            }
            else
            {
                var legacy = new NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
                legacy.header.type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
                legacy.header.size = (uint)Marshal.SizeOf(typeof(NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO));
                legacy.header.adapterId = info.AdapterId;
                legacy.header.id = info.TargetId;
                if (NativeMethods.DisplayConfigGetDeviceInfo(ref legacy) != NativeMethods.ERROR_SUCCESS)
                    return false;

                uint v = legacy.value;
                bool supported = (v & 0x1) != 0;           // advancedColorSupported
                bool enabled = (v & 0x2) != 0;             // advancedColorEnabled
                bool wideColorEnforced = (v & 0x4) != 0;
                bool forceDisabled = (v & 0x8) != 0;       // advancedColorForceDisabled

                // An SDR panel with Auto Color Management reports advanced
                // color as supported but with wide color enforced; there
                // "advanced color" means WCG, not HDR, and must not be touched.
                info.Api = HdrApi.Legacy;
                info.HdrSupported = supported && !wideColorEnforced;
                info.HdrEnabled = enabled && !wideColorEnforced;
                info.HdrActive = info.HdrEnabled && !forceDisabled;
                info.LimitedByPolicy = forceDisabled;
                info.RawFlags = v;
                info.BitsPerColorChannel = legacy.bitsPerColorChannel;
                return true;
            }
        }

        static string GetTargetName(NativeMethods.LUID adapterId, uint targetId, uint outputTechnology)
        {
            var name = new NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME();
            name.header.type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            name.header.size = (uint)Marshal.SizeOf(typeof(NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME));
            name.header.adapterId = adapterId;
            name.header.id = targetId;
            if (NativeMethods.DisplayConfigGetDeviceInfo(ref name) == NativeMethods.ERROR_SUCCESS
                && !string.IsNullOrEmpty(name.monitorFriendlyDeviceName))
            {
                return name.monitorFriendlyDeviceName;
            }

            // Laptop panels usually have no EDID friendly name.
            if (IsInternal(outputTechnology))
                return Strings.BuiltInDisplay;
            return null;
        }

        static string GetSourceGdiName(NativeMethods.DISPLAYCONFIG_PATH_SOURCE_INFO source)
        {
            var name = new NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            name.header.type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            name.header.size = (uint)Marshal.SizeOf(typeof(NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME));
            name.header.adapterId = source.adapterId;
            name.header.id = source.id;
            if (NativeMethods.DisplayConfigGetDeviceInfo(ref name) != NativeMethods.ERROR_SUCCESS
                || string.IsNullOrEmpty(name.viewGdiDeviceName))
                return null;
            return name.viewGdiDeviceName.Replace(@"\\.\", "");
        }

        static bool IsInternal(uint tech)
        {
            return tech == NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
                || tech == NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED
                || tech == NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED;
        }
    }

    internal static class NativeMethods
    {
        public const int ERROR_SUCCESS = 0;
        public const int ERROR_ACCESS_DENIED = 5;
        public const int ERROR_INSUFFICIENT_BUFFER = 122;

        public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

        public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
        public const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
        public const uint DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE = 10;
        public const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2 = 15;
        public const uint DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE = 16;

        public const uint DISPLAYCONFIG_ADVANCED_COLOR_MODE_SDR = 0;
        public const uint DISPLAYCONFIG_ADVANCED_COLOR_MODE_WCG = 1;
        public const uint DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR = 2;

        public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11;
        public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED = 13;
        public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;

        public const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;
        public const uint MONITOR_DEFAULTTONULL = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        // 72 bytes.
        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        // 64 bytes. We never read the mode union, so it is kept opaque.
        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            public ulong u0, u1, u2, u3, u4, u5;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string monitorDevicePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;
            public uint colorEncoding;
            public uint bitsPerColorChannel;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;
            public uint colorEncoding;
            public uint bitsPerColorChannel;
            public uint activeColorMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_SET_HDR_STATE
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct RTL_OSVERSIONINFOW
        {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate bool InternalRefreshCalibrationFn(IntPtr a, UIntPtr b, IntPtr c, IntPtr d);

        /// <summary>Real OS build; unlike Environment.OSVersion it does not depend on the manifest.</summary>
        public static int GetOsBuild()
        {
            try
            {
                var v = new RTL_OSVERSIONINFOW();
                v.dwOSVersionInfoSize = (uint)Marshal.SizeOf(typeof(RTL_OSVERSIONINFOW));
                if (RtlGetVersion(ref v) == 0)
                    return (int)v.dwBuildNumber;
            }
            catch (Exception)
            {
            }
            return Environment.OSVersion.Version.Build;
        }

        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 requestPacket);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE setPacket);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_HDR_STATE setPacket);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [DllImport("ntdll.dll")]
        static extern int RtlGetVersion(ref RTL_OSVERSIONINFOW versionInfo);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibraryEx(string fileName, IntPtr reserved, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false)]
        public static extern IntPtr GetProcAddress(IntPtr module, string procName);
    }
}
