// UI text. Chinese when the Windows UI language is Chinese, English otherwise.

using System.Globalization;

namespace HdrTray
{
    internal static class Strings
    {
        static readonly bool Zh = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";

        static string T(string zh, string en) { return Zh ? zh : en; }

        public static string AppName { get { return "HDR Tray"; } }

        public static string ToggleHdr { get { return T("切换 HDR", "Toggle HDR"); } }
        public static string TurnOnHdr { get { return T("开启 HDR", "Turn HDR on"); } }
        public static string TurnOffHdr { get { return T("关闭 HDR", "Turn HDR off"); } }
        public static string StartWithWindows { get { return T("开机自启", "Start with Windows"); } }
        public static string OpenDisplaySettings { get { return T("显示设置…", "Display settings…"); } }
        public static string Exit { get { return T("退出", "Exit"); } }

        public static string BuiltInDisplay { get { return T("内置显示器", "Built-in display"); } }
        public static string DisplayN(int n) { return T("显示器 " + n, "Display " + n); }

        public static string StateOn { get { return T("开", "On"); } }
        public static string StateOff { get { return T("关", "Off"); } }
        public static string StatePaused { get { return T("已暂停", "Paused"); } }
        public static string StateUnsupported { get { return T("不支持", "Not supported"); } }

        public static string TooltipOn { get { return T("HDR：开", "HDR: On"); } }
        public static string TooltipOff { get { return T("HDR：关", "HDR: Off"); } }
        public static string TooltipPaused { get { return T("HDR：已开启，但被系统暂停（如使用电池时）", "HDR: On, paused by Windows (e.g. on battery)"); } }
        public static string TooltipUnavailable { get { return T("没有支持 HDR 的显示器", "No HDR-capable display"); } }
        public static string TooltipBusy { get { return T("正在切换 HDR…", "Switching HDR…"); } }
        public static string ClickToToggle { get { return T("单击切换", "Click to toggle"); } }

        public static string AlreadyRunning { get { return T("HDR Tray 已在运行。图标可能在任务栏右下角的 ^ 隐藏区域里。", "HDR Tray is already running. Its icon may be hidden under the ^ in the taskbar corner."); } }
        public static string Welcome { get { return T("HDR Tray 已在通知区域运行，单击图标即可切换 HDR。可把图标从 ^ 隐藏区域拖到任务栏上常驻显示。", "HDR Tray is running in the notification area. Click its icon to toggle HDR. Drag it out of the ^ overflow to keep it visible."); } }
        public static string NoHdrDisplay { get { return T("未检测到支持 HDR 的显示器。", "No HDR-capable display was found."); } }
        public static string ToggleFailed { get { return T("切换 HDR 失败", "Failed to switch HDR"); } }
        public static string AutostartFailed { get { return T("无法修改开机自启设置", "Could not change the startup setting"); } }
        public static string NotSupportedOn(string display) { return T(display + " 不支持 HDR", display + " does not support HDR"); }
        public static string ReadFailed { get { return T("无法读取显示器的 HDR 状态，请稍后再试。", "Could not read the HDR state of the displays. Try again in a moment."); } }
        public static string NoEffect { get { return T("Windows 没有应用此更改", "Windows did not apply the change"); } }

        public static string Usage
        {
            get
            {
                return T(
                    "用法：HdrTray.exe [--toggle | --on | --off]\n\n不带参数时以托盘图标运行。\n--toggle  切换所有支持 HDR 的显示器\n--on      开启 HDR\n--off     关闭 HDR",
                    "Usage: HdrTray.exe [--toggle | --on | --off]\n\nWithout arguments it runs as a tray icon.\n--toggle  Toggle HDR on all HDR-capable displays\n--on      Turn HDR on\n--off     Turn HDR off");
            }
        }
    }
}
