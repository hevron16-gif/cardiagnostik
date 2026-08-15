using CarDiagnosticApp.Pages;
using CarDiagnosticApp.Services;
using System.Diagnostics;

namespace CarDiagnosticApp;

public partial class App : Application
{
    public static ConnectivityService Connectivity { get; } = new();
    public static LearningDbService Learning { get; } = new();
    public static SpecialVehicleService SpecialVehicles { get; } = new();

    private static ErrorCodeDbService? _errorCodes;
    public static ErrorCodeDbService ErrorCodes => _errorCodes ??= new ErrorCodeDbService();

    private static DtcReferenceService? _dtc;
    public static DtcReferenceService Dtc => _dtc ??= new DtcReferenceService();

    private static UserRepository? _userRepo;
    public static UserRepository UserRepo => _userRepo ??= new UserRepository();

    public App()
    {
        InitializeComponent();
        Debug.WriteLine("[App] Constructor — InitializeComponent done");
        
        // Запускаем проверку интернета в фоне (не блокируем UI)
        _ = Task.Run(async () =>
        {
            try
            {
                await Connectivity.CheckOnStartupAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[App] Startup connectivity check failed: {ex.Message}");
            }
        });
        Connectivity.StartListening();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var stopwatch = Stopwatch.StartNew();

#if ANDROID
        // На Android Shell-страницы часто имеют Navigation == null →
        // любой PushAsync/GoToAsync из MainPage роняет процесс.
        // NavigationPage даёт рабочий стек навигации.
        Debug.WriteLine("[App] CreateWindow — Android NavigationPage(MainPage)...");
        var main = new MainPage();
        var nav = new NavigationPage(main)
        {
            BarBackgroundColor = Color.FromArgb("#1565C0"),
            BarTextColor = Colors.White,
        };
        Debug.WriteLine($"[App] CreateWindow — NavigationPage ready ({stopwatch.ElapsedMilliseconds}ms)");
        return new Window(nav);
#else
        Debug.WriteLine("[App] CreateWindow — building AppShell...");
        var shell = new AppShell();
        Debug.WriteLine($"[App] CreateWindow — AppShell ready ({stopwatch.ElapsedMilliseconds}ms)");
        return new Window(shell);
#endif
    }
}
