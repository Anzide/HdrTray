// Build-time helper: renders the application icon (app.ico) so the repo
// needs no binary assets.

using System;

namespace HdrTray
{
    internal static class IconGen
    {
        static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("usage: IconGen <out.ico>");
                return 2;
            }
            IconRenderer.WriteIcoFile(args[0], TrayState.On, false, new[] { 16, 20, 24, 32, 40, 48, 64, 256 });
            return 0;
        }
    }
}
