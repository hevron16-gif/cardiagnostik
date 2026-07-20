#if ANDROID
using Android;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
#endif
using Microsoft.Maui.ApplicationModel;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Runtime-разрешения Bluetooth / Location для ELM327.
/// </summary>
public static class PlatformPermissionService
{
#if ANDROID
    public static void RequestBluetoothPermissions(Android.App.Activity activity)
    {
        var needed = GetNeededBluetoothPermissions(activity);
        if (needed.Length == 0) return;

        Android.Util.Log.Info("AutoDiag",
            $"[Permission] Requesting: {string.Join(", ", needed)}");

        ActivityCompat.RequestPermissions(activity, needed, 1001);
    }

    public static bool HasBluetoothPermissions(Android.App.Activity? activity = null)
    {
        var ctx = activity ?? Platform.CurrentActivity;
        if (ctx == null) return false;

        foreach (var perm in GetNeededBluetoothPermissions(ctx, includeGrantedCheck: false))
        {
            if (ContextCompat.CheckSelfPermission(ctx, perm) != Permission.Granted)
                return false;
        }
        return true;
    }

    /// <summary>
    /// MAUI + native dialog. Возвращает true, если можно подключаться.
    /// </summary>
    public static async Task<bool> EnsureBluetoothPermissionsAsync()
    {
        try
        {
            // Android 12+ — Nearby devices
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                var bt = await Permissions.CheckStatusAsync<Permissions.Bluetooth>();
                if (bt != PermissionStatus.Granted)
                    bt = await Permissions.RequestAsync<Permissions.Bluetooth>();

                if (bt != PermissionStatus.Granted)
                {
                    // fallback native
                    var act = Platform.CurrentActivity;
                    if (act != null)
                    {
                        RequestBluetoothPermissions(act);
                        for (int i = 0; i < 40; i++)
                        {
                            await Task.Delay(250);
                            if (HasBluetoothPermissions(act))
                                return true;
                        }
                    }
                    return false;
                }
                return true;
            }

            // Android 11- — location for classic discovery
            var loc = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (loc != PermissionStatus.Granted)
                loc = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();

            if (loc != PermissionStatus.Granted)
            {
                var act = Platform.CurrentActivity;
                if (act != null)
                {
                    RequestBluetoothPermissions(act);
                    for (int i = 0; i < 40; i++)
                    {
                        await Task.Delay(250);
                        if (HasBluetoothPermissions(act))
                            return true;
                    }
                }
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
#if ANDROID
            Android.Util.Log.Error("AutoDiag", $"EnsureBluetoothPermissions: {ex}");
#endif
            // Последняя попытка native
            try
            {
                var act = Platform.CurrentActivity;
                if (act != null)
                {
                    RequestBluetoothPermissions(act);
                    await Task.Delay(1500);
                    return HasBluetoothPermissions(act);
                }
            }
            catch { }
            return false;
        }
    }

    private static string[] GetNeededBluetoothPermissions(
        Android.App.Activity activity, bool includeGrantedCheck = true)
    {
        var needed = new List<string>();

        if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
        {
            MaybeAdd(needed, activity, Manifest.Permission.BluetoothScan!, includeGrantedCheck);
            MaybeAdd(needed, activity, Manifest.Permission.BluetoothConnect!, includeGrantedCheck);
        }
        else
        {
            MaybeAdd(needed, activity, Manifest.Permission.AccessFineLocation!, includeGrantedCheck);
            MaybeAdd(needed, activity, Manifest.Permission.Bluetooth!, includeGrantedCheck);
            MaybeAdd(needed, activity, Manifest.Permission.BluetoothAdmin!, includeGrantedCheck);
        }

        return needed.ToArray();
    }

    private static void MaybeAdd(List<string> list, Android.App.Activity activity, string perm, bool onlyIfMissing)
    {
        if (string.IsNullOrEmpty(perm)) return;
        if (onlyIfMissing)
        {
            if (ContextCompat.CheckSelfPermission(activity, perm) != Permission.Granted)
                list.Add(perm);
        }
        else
        {
            list.Add(perm);
        }
    }
#else
    public static void RequestBluetoothPermissions(object activity) { }
    public static bool HasBluetoothPermissions(object? activity = null) => true;
    public static Task<bool> EnsureBluetoothPermissionsAsync() => Task.FromResult(true);
#endif
}
