using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using Syncfusion.Maui.Toolkit.Hosting;
using System;

namespace CarDiagnosticApp
{
    public static class MauiProgram
    {
        private static void LogInfo(string msg)
        {
#if ANDROID
            Android.Util.Log.Info("AutoDiag", msg);
#else
            System.Diagnostics.Debug.WriteLine($"[AutoDiag] {msg}");
#endif
        }

        private static void LogError(string msg)
        {
#if ANDROID
            Android.Util.Log.Error("AutoDiag", msg);
#else
            System.Diagnostics.Debug.WriteLine($"[AutoDiag] ERROR: {msg}");
#endif
        }

        public static MauiApp CreateMauiApp()
        {
            // Запись лога в файл для диагностики крашей
            var logPath = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "CarDiagnosticApp", "crash.log");
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath));
            }
            catch { }

            void WriteLog(string msg)
            {
                try
                {
                    System.IO.File.AppendAllText(logPath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\n");
                }
                catch { }
                LogInfo(msg);
            }

            WriteLog("=== CarDiagnosticApp starting ===");

            // Глобальный перехват необработанных исключений
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                var msg = $"AppDomain.UnhandledException: {ex?.GetType().Name}: {ex?.Message}";
                WriteLog(msg);
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                var msg = $"UnobservedTaskException: {e.Exception?.GetBaseException().Message}";
                WriteLog(msg);
                e.SetObserved();
            };

            var step = "CreateBuilder";
            try
            {
                WriteLog("CreateMauiApp starting...");

                var builder = MauiApp.CreateBuilder();

                // HttpClient через DI-фабрику — управляет временем жизни сокетов
                step = "AddHttpClient";
                builder.Services.AddHttpClient<Services.ApiService>(client =>
                {
                    // Production: основной домен kitdiag.ru
                    client.BaseAddress = new Uri("https://api.kitdiag.ru");
                    // Render free cold-start + DeepSeek: до 90 с
                    client.Timeout = TimeSpan.FromSeconds(90);
                });

                // Bluetooth-транспорт (классический RFCOMM/SPP)
                step = "BluetoothTransport";
#if WINDOWS
                builder.Services.AddSingleton<Services.IBluetoothTransport, Services.WindowsBluetoothTransport>();
#elif ANDROID
                builder.Services.AddSingleton<Services.IBluetoothTransport, Services.AndroidBluetoothTransport>();
#endif

                // Bluetooth-сервис (ELM327 протокол)
                step = "BluetoothService";
                builder.Services.AddSingleton<Services.BluetoothService>();

                // Симулятор OBD для тестирования без авто
                step = "ObdSimulator";
                builder.Services.AddSingleton<Services.ObdSimulator>(
                    _ => new Services.ObdSimulator("Lada Vesta 1.8"));

                // Модуль схем узлов — загрузка с сервера + локальный кеш
                step = "DiagramDbService";
                builder.Services.AddSingleton<Services.DiagramDbService>();
                step = "LicenseService";
                builder.Services.AddSingleton<Services.LicenseService>();
                step = "UserRepository";
                builder.Services.AddSingleton<Services.UserRepository>();
                step = "SchemeService";
                builder.Services.AddTransient<Services.SchemeService>();

                step = "UseMauiApp";
                builder
                    .UseMauiApp<App>()
                    .UseMauiCommunityToolkit()
                    .ConfigureSyncfusionToolkit()
                    .ConfigureMauiHandlers(handlers =>
                    {
#if WINDOWS
                        Microsoft.Maui.Controls.Handlers.Items.CollectionViewHandler.Mapper.AppendToMapping("KeyboardAccessibleCollectionView", (handler, view) =>
                        {
                            handler.PlatformView.SingleSelectionFollowsFocus = false;
                        });
#endif
                    })
                    .ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                        fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                        fonts.AddFont("SegoeUI-Semibold.ttf", "SegoeSemibold");
                        fonts.AddFont("FluentSystemIcons-Regular.ttf", FluentUI.FontFamily);
                        fonts.AddFont("Inter-Regular.ttf", "InterRegular");
                        fonts.AddFont("Inter-Medium.ttf", "InterMedium");
                        fonts.AddFont("Inter-SemiBold.ttf", "InterSemiBold");
                        fonts.AddFont("Inter-Bold.ttf", "InterBold");
                        fonts.AddFont("MaterialIcons-Regular.ttf", "MaterialIcons");
                    });

#if DEBUG
                builder.Logging.AddDebug();
                builder.Services.AddLogging(configure => configure.AddDebug());
#endif

                step = "Build";
                WriteLog("builder.Build()...");
                var app = builder.Build();
                WriteLog("CreateMauiApp completed OK");
                return app;
            }
            catch (Exception ex)
            {
                WriteLog($"CreateMauiApp FAILED at step '{step}': {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }
    }
}
