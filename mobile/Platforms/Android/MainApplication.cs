using Android.App;
using Android.Runtime;
using Android.Util;
using System;

namespace CarDiagnosticApp
{
    [Application]
    public class MainApplication : MauiApplication
    {
        public MainApplication(IntPtr handle, JniHandleOwnership ownership)
            : base(handle, ownership)
        {
        }

        protected override MauiApp CreateMauiApp()
        {
            // Ловим необработанные исключения на уровне Android-рантайма
            AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
            {
                Log.Error("AutoDiag", $"ANDROID UNHANDLED: {e.Exception}");
                // Не роняем процесс из-за UI-потока / Java.Lang.RuntimeException из MAUI
                // (реальные фатальные ошибки всё равно дойдут до logcat).
                try
                {
                    var msg = e.Exception?.ToString() ?? "unknown";
                    var dir = System.IO.Path.Combine(
                        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                        "CarDiagnosticApp");
                    System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(dir, "crash.log"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ANDROID UNHANDLED:\n{msg}\n\n");
                }
                catch { }
                e.Handled = true;
            };

            try
            {
                Log.Info("AutoDiag", "CreateMauiApp starting...");
                var app = MauiProgram.CreateMauiApp();
                Log.Info("AutoDiag", "CreateMauiApp completed OK");
                return app;
            }
            catch (Exception ex)
            {
                Log.Error("AutoDiag", $"CreateMauiApp FAILED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }
    }
}
