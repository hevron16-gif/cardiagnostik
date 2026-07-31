using Microsoft.Extensions.Logging;
using CarDiagnosticApp.Services;

namespace CarDiagnosticApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("Inter-Regular.ttf", "InterRegular");
                fonts.AddFont("Inter-Bold.ttf", "InterBold");
                fonts.AddFont("Inter-Medium.ttf", "InterMedium");
                fonts.AddFont("Inter-SemiBold.ttf", "InterSemiBold");
                fonts.AddFont("MaterialIcons-Regular.ttf", "MaterialIcons");
            });

        builder.Services.AddSingleton<ApiService>();
        builder.Services.AddSingleton<BluetoothService>();
        builder.Services.AddSingleton<SyncService>();
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddSingleton<LocalDatabase>();
        builder.Services.AddSingleton<OfflineDatabase>();
        builder.Services.AddSingleton<ErrorCodeDbService>();
        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton<ConnectivityService>();
        builder.Services.AddSingleton<DiagramDbService>();
        builder.Services.AddSingleton<ErrorHistoryService>();
        builder.Services.AddSingleton<CarBrandCacheService>();
        builder.Services.AddSingleton<SchemeService>();
        builder.Services.AddSingleton<VinDecoderService>();
        builder.Services.AddSingleton<ReportService>();
        builder.Services.AddSingleton<RussianAutoService>();
        builder.Services.AddSingleton<AutoIndustryService>();
        builder.Services.AddSingleton<LearningDbService>();
        builder.Services.AddSingleton<LicenseService>();

        builder.Services.AddTransient<MainPage>();
        builder.Services.AddTransient<LiveDataPage>();
        builder.Services.AddTransient<LiveChartsPage>();
        builder.Services.AddTransient<SchemePage>();
        builder.Services.AddTransient<HistoryPage>();
        builder.Services.AddTransient<ResultPage>();
        builder.Services.AddTransient<RepairGuidePage>();
        builder.Services.AddTransient<KnowledgePage>();
        builder.Services.AddTransient<KnowledgeBasePage>();
        builder.Services.AddTransient<CodingPage>();
        builder.Services.AddTransient<GraphPage>();
        builder.Services.AddTransient<AdminPanelPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
