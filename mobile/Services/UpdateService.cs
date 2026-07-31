using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;

namespace CarDiagnosticApp.Services
{
    public class UpdateService
    {
        private readonly ApiService _api;
        private readonly string _currentVersion = "1.0.15";

        public UpdateService(ApiService api)
        {
            _api = api;
        }

        public async Task<UpdateInfo> CheckForUpdateAsync(CancellationToken ct = default)
        {
            try
            {
                var versionInfo = await _api.GetAsync<VersionResponse>("/version", ct);
                var latest = ParseVersion(versionInfo.version);
                var current = ParseVersion(_currentVersion);
                bool hasUpdate = IsNewer(latest, current);

                return new UpdateInfo
                {
                    HasUpdate = hasUpdate,
                    LatestVersion = versionInfo.version,
                    CurrentVersion = _currentVersion,
                    DownloadUrl = versionInfo.latest_apk_url,
                    Changelog = versionInfo.changelog ?? new string[0],
                    IsMandatory = IsMandatoryUpdate(versionInfo.min_app_version, _currentVersion)
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Update check failed: {ex.Message}");
                return new UpdateInfo { HasUpdate = false, Error = ex.Message };
            }
        }

        public async Task<bool> DownloadAndInstallAsync(string url, IProgress<double> progress, CancellationToken ct = default)
        {
            try
            {
                var apkBytes = await _api.DownloadAsync(url, ct, progress);
                var tempPath = Path.Combine(FileSystem.CacheDirectory, "update.apk");
                await File.WriteAllBytesAsync(tempPath, apkBytes, ct);
                await Launcher.OpenAsync(new OpenFileRequest("Установка обновления", new ReadOnlyFile(tempPath)));
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Download failed: {ex.Message}");
                return false;
            }
        }

        private static Version ParseVersion(string v)
        {
            var parts = v.Split('.');
            var major = parts.Length > 0 ? int.Parse(parts[0]) : 0;
            var minor = parts.Length > 1 ? int.Parse(parts[1]) : 0;
            var build = parts.Length > 2 ? int.Parse(parts[2]) : 0;
            return new Version(major, minor, build);
        }

        private static bool IsNewer(Version latest, Version current)
        {
            return latest > current;
        }

        private static bool IsMandatoryUpdate(string minVersion, string currentVersion)
        {
            if (string.IsNullOrEmpty(minVersion)) return false;
            return IsNewer(ParseVersion(minVersion), ParseVersion(currentVersion));
        }
    }

    public class UpdateInfo
    {
        public bool HasUpdate { get; set; }
        public bool IsMandatory { get; set; }
        public string LatestVersion { get; set; }
        public string CurrentVersion { get; set; }
        public string DownloadUrl { get; set; }
        public string[] Changelog { get; set; }
        public string Error { get; set; }
    }

    public class VersionResponse
    {
        public string version { get; set; }
        public string min_app_version { get; set; }
        public string latest_apk_url { get; set; }
        public string[] changelog { get; set; }
    }
}
