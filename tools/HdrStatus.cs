// Console diagnostic: prints every active display and its HDR state.
// Read-only; it never changes any setting.

using System;
using System.Runtime.InteropServices;

namespace HdrTray
{
    internal static class HdrStatus
    {
        static int Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("OS build: " + HdrController.OsBuild + "  API: " + HdrController.PreferredApi
                + (Environment.Is64BitProcess ? "  (64-bit process)" : "  (32-bit process)"));
            Console.WriteLine("sizeof PATH_INFO={0} MODE_INFO={1} HEADER={2} TARGET_NAME={3} ACI={4} ACI2={5} SET={6}",
                Marshal.SizeOf(typeof(NativeMethods.DISPLAYCONFIG_PATH_INFO)),
                Marshal.SizeOf(typeof(NativeMethods.DISPLAYCONFIG_MODE_INFO)),
                Marshal.SizeOf(typeof(NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER)),
                Marshal.SizeOf(typeof(NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME)),
                Marshal.SizeOf(typeof(NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO)),
                Marshal.SizeOf(typeof(NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2)),
                Marshal.SizeOf(typeof(NativeMethods.DISPLAYCONFIG_SET_HDR_STATE)));

            var displays = HdrController.GetDisplays();
            Console.WriteLine("Active displays: " + displays.Count);
            foreach (var d in displays)
            {
                Console.WriteLine();
                Console.WriteLine("  " + d.Name + "  [" + d.Key + "]");
                Console.WriteLine("    api={0} supported={1} enabled={2} active={3} limitedByPolicy={4}",
                    d.Api, d.HdrSupported, d.HdrEnabled, d.HdrActive, d.LimitedByPolicy);
                Console.WriteLine("    rawFlags=0x{0:X8} bitsPerChannel={1} activeColorMode={2}",
                    d.RawFlags, d.BitsPerColorChannel, d.ActiveColorMode);
            }
            return 0;
        }
    }
}
