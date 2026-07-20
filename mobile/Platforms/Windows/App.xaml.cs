using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.DynamicDependency;
using System;
using System.IO;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace CarDiagnosticApp.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : MauiWinUIApplication
    {
        /// <summary>
        /// Initializes Windows App SDK for self-contained deployment.
        /// Must run before Application.Start() tries to load Microsoft.UI.Xaml.dll.
        /// MddBootstrapInitialize registers the framework package from the app directory
        /// so WinUI can resolve WinAppSDK types without system-wide installation.
        /// Without this, WinUI crashes with 0xc000027b (STOWED_EXCEPTION) on machines
        /// without the Windows App Runtime installed.
        /// </summary>
        [System.Runtime.CompilerServices.ModuleInitializer]
        public static void InitWindowsAppSDK()
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                // Self-contained: Microsoft.WindowsAppRuntime.dll лежит рядом с exe —
                // Dynamic Dependency bootstrap НЕ нужен и ломает WinUI (0xc000027b).
                var runtimeDll = Path.Combine(baseDir, "Microsoft.WindowsAppRuntime.dll");
                if (File.Exists(runtimeDll))
                {
                    LogCrash("WinAppSDK self-contained: bootstrap skipped (runtime next to exe)");
                    return;
                }

                // Framework-dependent: нужен runtime 1.7 (как в MAUI / WindowsAppSDK 1.7.x)
                // 0x00010007 = major 1, minor 7  (НЕ 0x00010000 = 1.0 — из-за этого был 0x80670016)
                const uint version17 = 0x00010007;
                if (Bootstrap.TryInitialize(version17, out int hresult))
                {
                    LogCrash($"WinAppSDK Bootstrap OK 1.7: hr=0x{hresult:X}");
                }
                else
                {
                    // fallback: любая 1.x на машине
                    if (Bootstrap.TryInitialize(0x00010000, out hresult))
                        LogCrash($"WinAppSDK Bootstrap OK 1.x fallback: hr=0x{hresult:X}");
                    else
                        LogCrash($"WinAppSDK Bootstrap failed: hr=0x{hresult:X}");
                }
            }
            catch (Exception ex)
            {
                var msg = $"WinAppSDK Bootstrap exception: {ex.GetType().Name}: {ex.Message}";
                System.Diagnostics.Debug.WriteLine(msg);
                LogCrash(msg);
            }
        }

        private static void LogCrash(string msg)
        {
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CarDiagnosticApp");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(
                    Path.Combine(logDir, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\n");
            }
            catch { }
        }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }

}
