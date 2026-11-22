using System.Reflection;
using System.Runtime.Versioning;

// Without this the CLR applies .NET 4.0 compatibility quirks, because a bare
// csc.exe build carries no target framework.
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

[assembly: AssemblyTitle("HDR Tray")]
[assembly: AssemblyProduct("HDR Tray")]
[assembly: AssemblyDescription("Toggle Windows HDR from the notification area")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
