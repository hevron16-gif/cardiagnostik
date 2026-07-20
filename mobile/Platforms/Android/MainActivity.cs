using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;

namespace CarDiagnosticApp;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize
        | ConfigChanges.Orientation
        | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        try
        {
            base.OnCreate(savedInstanceState);
            System.Diagnostics.Debug.WriteLine("[MainActivity] OnCreate done");
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainActivity] OnCreate FAILED: {ex}");
            try
            {
                new Android.App.AlertDialog.Builder(this)
                    .SetTitle("Ошибка запуска")
                    .SetMessage(ex.Message)
                    .SetPositiveButton("OK", (_, _) => Finish())
                    .Show();
            }
            catch { }
        }
    }

    /// <summary>
    /// Пробрасывает результат запроса разрешений в MAUI Essentials (API 23+).
    /// </summary>
    public override void OnRequestPermissionsResult(
        int requestCode,
        string[] permissions,
        [GeneratedEnum] Permission[] grantResults)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            Microsoft.Maui.ApplicationModel.Platform.OnRequestPermissionsResult(
                requestCode, permissions, grantResults);
        }
    }
}
