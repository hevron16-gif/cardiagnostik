using Microsoft.Maui.Controls;
using System;
using System.Threading.Tasks;

namespace CarDiagnosticApp
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            MainPage = new AppShell();
        }

        protected override void OnStart()
        {
            base.OnStart();
            _ = InitializeServicesAsync();
        }

        protected override void OnSleep()
        {
            base.OnSleep();
            SaveAppState();
            _ = DisconnectBluetoothAsync();
        }

        protected override void OnResume()
        {
            base.OnResume();
            RestoreAppState();
            _ = CheckForUpdatesAsync();
        }

        private async Task InitializeServicesAsync()
        {
            try
            {
                var db = LocalDatabase.Instance;
                await db.InitializeAsync();
                _ = CheckForUpdatesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Init error: {ex}");
            }
        }

        private async Task CheckForUpdatesAsync()
        {
            try
            {
                var updateService = Handler?.MauiContext?.Services.GetService<UpdateService>();
                if (updateService == null) return;

                var info = await updateService.CheckForUpdateAsync();
                if (info.HasUpdate)
                {
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        var action = await Current.MainPage.DisplayAlert(
                            "Доступно обновление",
                            $"Версия {info.LatestVersion} доступна.\n\n{string.Join("\n", info.Changelog)}",
                            "Обновить",
                            info.IsMandatory ? null : "Позже");

                        if (action && !string.IsNullOrEmpty(info.DownloadUrl))
                        {
                            var progressPage = new ContentPage
                            {
                                Title = "Загрузка обновления",
                                Content = new VerticalStackLayout
                                {
                                    Padding = 20,
                                    Children =
                                    {
                                        new Label { Text = "Загрузка...", HorizontalOptions = LayoutOptions.Center },
                                        new ProgressBar { Progress = 0, Margin = new Thickness(0, 20) }
                                    }
                                }
                            };
                            await Shell.Current.Navigation.PushModalAsync(progressPage);

                            var progress = new Progress<double>(p =>
                            {
                                MainThread.BeginInvokeOnMainThread(() =>
                                {
                                    if (progressPage.Content is VerticalStackLayout layout && layout.Children[1] is ProgressBar bar)
                                        bar.Progress = p;
                                });
                            });

                            var success = await updateService.DownloadAndInstallAsync(info.DownloadUrl, progress);
                            await Shell.Current.Navigation.PopModalAsync();

                            if (!success)
                                await Current.MainPage.DisplayAlert("Ошибка", "Не удалось загрузить обновление", "OK");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Update check error: {ex}");
            }
        }

        private async Task DisconnectBluetoothAsync()
        {
            try
            {
                var bt = BluetoothService.Instance;
                if (bt?.IsConnected == true)
                    await bt.DisconnectAsync();
            }
            catch { }
        }

        private void SaveAppState()
        {
            Preferences.Set("last_session", DateTime.UtcNow.ToString("O"));
            Preferences.Set("app_version", "1.0.15");
        }

        private void RestoreAppState()
        {
            var lastSession = Preferences.Get("last_session", null);
            if (lastSession != null && DateTime.TryParse(lastSession, out var dt))
            {
                if ((DateTime.UtcNow - dt).TotalMinutes > 30)
                    SyncService.Instance?.ResetConnections();
            }
        }
    }
}
