using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BackupAgent.Services;

[DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
[DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
const int SW_HIDE = 0;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

string exePath = Process.GetCurrentProcess().MainModule.FileName;
string exeDir = Path.GetDirectoryName(exePath)!;
string configPath = Path.Combine(exeDir, "appsettings.json");

if (args.Contains("--install"))
{
    TaskInstaller.Run(exePath);
    return;
}

if (args.Contains("--config") || !File.Exists(configPath))
{
    ConfigWizard.Run(configPath);
    return;
}

bool isManualRun = args.Contains("--run");
if (!isManualRun)
{
    ShowWindow(GetConsoleWindow(), SW_HIDE);
}

BackupEngine.Run(exeDir, isManualRun);