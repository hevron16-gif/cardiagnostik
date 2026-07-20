#if ANDROID
using Android.Bluetooth;
using Android.Content;
using Android.OS;
using Java.Util;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Android classic Bluetooth RFCOMM/SPP transport for ELM327.
/// </summary>
public class AndroidBluetoothTransport : IBluetoothTransport
{
    private BluetoothSocket? _socket;
    private Stream? _inputStream;
    private Stream? _outputStream;
    private static readonly UUID SppUuid = UUID.FromString("00001101-0000-1000-8000-00805F9B34FB")!;

    // Типичные имена адаптеров (китайские клоны часто HC-05 / OBDII / без ELM в имени)
    private static readonly string[] NameHints =
    {
        "ELM", "OBD", "OBDII", "OBD2", "Vgate", "Vlinker", "Konnwei", "KONNWEI",
        "Carista", "Viecar", "Veepeak", "Bafx", "ScanTool", "PLX", "Kiwi",
        "HC-05", "HC-06", "SPP", "BT", "Android-Vlink", "OBDLink", "MX+", "LELink",
    };

    public bool IsConnected => _socket?.IsConnected == true;

    public async Task<string> ConnectAsync(int scanTimeoutMs = 12000, CancellationToken ct = default)
    {
        // ── Разрешения ──
        if (!PlatformPermissionService.HasBluetoothPermissions())
        {
            var act = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (act != null)
                PlatformPermissionService.RequestBluetoothPermissions(act);

            // Дать пользователю время принять диалог
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(250, ct);
                if (PlatformPermissionService.HasBluetoothPermissions())
                    break;
            }

            if (!PlatformPermissionService.HasBluetoothPermissions())
                throw new InvalidOperationException(
                    "Нет разрешений Bluetooth.\n" +
                    "Настройки → Приложения → АвтоДиагностика → Разрешения →\n" +
                    "включите «Устройства поблизости» (или Bluetooth / Геолокация).");
        }

        var adapter = BluetoothAdapter.DefaultAdapter;
        if (adapter == null)
            throw new InvalidOperationException("Bluetooth не поддерживается на этом телефоне.");

        if (!adapter.IsEnabled)
            throw new InvalidOperationException("Bluetooth выключен. Включите Bluetooth и повторите.");

        // Остановить чужой discovery — иначе Connect часто падает
        try { if (adapter.IsDiscovering) adapter.CancelDiscovery(); } catch { }

        // ── 1) Сопряжённые устройства (основной путь) ──
        var bonded = GetBondedDevicesSafe(adapter);
        var candidates = bonded
            .Where(d => LooksLikeObd(d))
            .Concat(bonded.Where(d => !LooksLikeObd(d))) // остальные сопряжённые — запасной вариант
            .GroupBy(d => d.Address)
            .Select(g => g.First())
            .ToList();

        Exception? lastError = null;

        foreach (var device in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                try { adapter.CancelDiscovery(); } catch { }
                await ConnectToDeviceAsync(device, ct);
                return device.Name ?? device.Address ?? "ELM327";
            }
            catch (Exception ex)
            {
                lastError = ex;
                await DisconnectAsync();
            }
        }

        // ── 2) Поиск (discovery) ──
        BluetoothDevice? found = null;
        try
        {
            found = await DiscoverObdDeviceAsync(adapter, Math.Max(scanTimeoutMs, 10000), ct);
        }
        catch (Exception ex)
        {
            lastError = ex;
        }

        if (found != null)
        {
            try
            {
                try { adapter.CancelDiscovery(); } catch { }
                await ConnectToDeviceAsync(found, ct);
                return found.Name ?? found.Address ?? "ELM327";
            }
            catch (Exception ex)
            {
                lastError = ex;
                await DisconnectAsync();
            }
        }

        var bondedNames = bonded.Count == 0
            ? "нет сопряжённых устройств"
            : string.Join(", ", bonded.Select(d => d.Name ?? d.Address).Take(8));

        throw new InvalidOperationException(
            "Не удалось подключиться к ELM327.\n\n" +
            "1) Сопрягите адаптер в настройках Bluetooth телефона\n" +
            "2) Адаптер в OBD2, зажигание ON\n" +
            "3) Разрешения «Устройства поблизости» включены\n" +
            "4) Нужен Bluetooth classic (не только BLE)\n\n" +
            $"Сопряжённые: {bondedNames}\n" +
            (lastError != null ? $"Ошибка: {lastError.Message}" : ""));
    }

    private static List<BluetoothDevice> GetBondedDevicesSafe(BluetoothAdapter adapter)
    {
        try
        {
            return adapter.BondedDevices?.ToList() ?? new List<BluetoothDevice>();
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AutoDiag", $"BondedDevices: {ex}");
            return new List<BluetoothDevice>();
        }
    }

    private static bool LooksLikeObd(BluetoothDevice d)
    {
        var name = (d.Name ?? "").Trim();
        if (string.IsNullOrEmpty(name))
            return false; // безымянные — не считаем OBD, но всё равно попробуем как fallback в общем списке

        foreach (var h in NameHints)
        {
            if (name.Contains(h, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private async Task ConnectToDeviceAsync(BluetoothDevice device, CancellationToken ct)
    {
        // Сначала стандартный SPP UUID
        Exception? err = null;
        try
        {
            var socket = device.CreateRfcommSocketToServiceRecord(SppUuid)
                         ?? throw new InvalidOperationException("socket=null");
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                socket.Connect();
            }, ct);

            if (socket.IsConnected)
            {
                _socket = socket;
                _inputStream = socket.InputStream;
                _outputStream = socket.OutputStream;
                return;
            }
            try { socket.Close(); } catch { }
        }
        catch (Exception ex)
        {
            err = ex;
            Android.Util.Log.Warn("AutoDiag", $"SPP connect fail {device.Name}: {ex.Message}");
        }

        // Fallback: insecure SPP
        try
        {
            var socket = device.CreateInsecureRfcommSocketToServiceRecord(SppUuid)
                         ?? throw new InvalidOperationException("insecure socket=null");
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                socket.Connect();
            }, ct);

            if (socket.IsConnected)
            {
                _socket = socket;
                _inputStream = socket.InputStream;
                _outputStream = socket.OutputStream;
                return;
            }
            try { socket.Close(); } catch { }
        }
        catch (Exception ex)
        {
            err = ex;
            Android.Util.Log.Warn("AutoDiag", $"Insecure SPP fail {device.Name}: {ex.Message}");
        }

        // Fallback: reflection channel 1 (HC-05 / многие ELM-клоны)
        try
        {
            var socket = CreateRfcommSocketChannel1(device)
                         ?? throw new InvalidOperationException("channel1 socket=null");
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                socket.Connect();
            }, ct);

            if (socket.IsConnected)
            {
                _socket = socket;
                _inputStream = socket.InputStream;
                _outputStream = socket.OutputStream;
                return;
            }
            try { socket.Close(); } catch { }
        }
        catch (Exception ex)
        {
            err = ex;
            Android.Util.Log.Warn("AutoDiag", $"Channel1 fail {device.Name}: {ex.Message}");
        }

        throw new InvalidOperationException(
            $"Не удалось открыть RFCOMM к «{device.Name ?? device.Address}»" +
            (err != null ? $": {err.Message}" : ""));
    }

    private static BluetoothSocket? CreateRfcommSocketChannel1(BluetoothDevice device)
    {
        try
        {
            // Java: BluetoothDevice.createRfcommSocket(int channel) — скрытый API
            var method = device.Class.GetMethod("createRfcommSocket", Java.Lang.Integer.Type!);
            var socket = method?.Invoke(device, new Java.Lang.Integer(1));
            return socket as BluetoothSocket;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("AutoDiag", $"CreateRfcommSocketChannel1: {ex.Message}");
            return null;
        }
    }

    private static async Task<BluetoothDevice?> DiscoverObdDeviceAsync(
        BluetoothAdapter adapter, int timeoutMs, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<BluetoothDevice?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new ScanReceiver(d =>
        {
            if (LooksLikeObd(d) || !string.IsNullOrEmpty(d.Name))
            {
                // Предпочитаем OBD-имена; первый попавшийся OBD — берём
                if (LooksLikeObd(d))
                    tcs.TrySetResult(d);
            }
        });

        try
        {
            var filter = new IntentFilter();
            filter.AddAction(BluetoothDevice.ActionFound);
            filter.AddAction(BluetoothAdapter.ActionDiscoveryFinished);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            {
                Android.App.Application.Context.RegisterReceiver(
                    receiver, filter, ReceiverFlags.Exported);
            }
            else
            {
                Android.App.Application.Context.RegisterReceiver(receiver, filter);
            }

            if (!adapter.StartDiscovery())
            {
                Android.Util.Log.Warn("AutoDiag", "StartDiscovery returned false");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try
            {
                return await tcs.Task.WaitAsync(cts.Token);
            }
            catch (System.OperationCanceledException)
            {
                return null;
            }
        }
        finally
        {
            try { adapter.CancelDiscovery(); } catch { }
            try { Android.App.Application.Context.UnregisterReceiver(receiver); } catch { }
        }
    }

    public Task DisconnectAsync()
    {
        try { _inputStream?.Close(); } catch { }
        try { _outputStream?.Close(); } catch { }
        try { _socket?.Close(); } catch { }
        _inputStream = null;
        _outputStream = null;
        _socket = null;
        return Task.CompletedTask;
    }

    public async Task<string> SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (_outputStream == null || _inputStream == null)
            return "";

        try
        {
            await _outputStream.WriteAsync(data, 0, data.Length, ct);
            await _outputStream.FlushAsync(ct);

            var buffer = new List<byte>(256);
            var buf = new byte[256];
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(5000);

            while (!readCts.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = await _inputStream.ReadAsync(buf, 0, buf.Length, readCts.Token);
                }
                catch (System.OperationCanceledException)
                {
                    break;
                }

                if (n <= 0) break;

                for (int i = 0; i < n; i++)
                {
                    buffer.Add(buf[i]);
                    if (buf[i] == (byte)'>')
                    {
                        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
                    }
                }

                // Защита от бесконечного буфера
                if (buffer.Count > 8192)
                    break;
            }

            return buffer.Count == 0
                ? ""
                : System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch
        {
            return "";
        }
    }

    private class ScanReceiver : BroadcastReceiver
    {
        private readonly Action<BluetoothDevice> _onFound;

        public ScanReceiver(Action<BluetoothDevice> onFound) => _onFound = onFound;

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action != BluetoothDevice.ActionFound)
                return;

            BluetoothDevice? device = null;
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                {
                    device = intent.GetParcelableExtra(BluetoothDevice.ExtraDevice, Java.Lang.Class.FromType(typeof(BluetoothDevice))) as BluetoothDevice;
                }
                else
                {
#pragma warning disable CS0618
                    device = intent.GetParcelableExtra(BluetoothDevice.ExtraDevice) as BluetoothDevice;
#pragma warning restore CS0618
                }
            }
            catch
            {
                try
                {
#pragma warning disable CS0618
                    device = intent.GetParcelableExtra(BluetoothDevice.ExtraDevice) as BluetoothDevice;
#pragma warning restore CS0618
                }
                catch { }
            }

            if (device != null)
                _onFound(device);
        }
    }
}
#endif
