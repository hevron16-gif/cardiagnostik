#if WINDOWS
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Windows classic Bluetooth RFCOMM transport for ELM327.
/// Uses Windows.Devices.Bluetooth.Rfcomm + StreamSocket.
/// </summary>
public class WindowsBluetoothTransport : IBluetoothTransportExtended
{
    private StreamSocket? _socket;
    private DataWriter? _writer;
    private DataReader? _reader;
    private readonly Guid _sppUuid = Guid.Parse("00001101-0000-1000-8000-00805F9B34FB");

    public bool IsConnected => _socket != null;

    public async Task<string> ConnectAsync(int scanTimeoutMs = 5000, CancellationToken ct = default)
    {
        // ── Scan for RFCOMM Serial Port devices ──
        var selector = RfcommDeviceService.GetDeviceSelector(
            RfcommServiceId.FromUuid(_sppUuid));

        var allDevices = await DeviceInformation.FindAllAsync(selector);

        var elmDevice = allDevices.FirstOrDefault(d =>
            d.Name.Contains("ELM", StringComparison.OrdinalIgnoreCase) ||
            d.Name.Contains("OBD", StringComparison.OrdinalIgnoreCase) ||
            d.Name.Contains("Vgate", StringComparison.OrdinalIgnoreCase));

        if (elmDevice == null)
            throw new InvalidOperationException(
                "ELM327-адаптер не найден. Убедитесь, что адаптер включён и сопряжён с Windows (Параметры → Bluetooth).");

        // ── Connect to RFCOMM service ──
        var service = await RfcommDeviceService.FromIdAsync(elmDevice.Id);
        if (service == null)
            throw new InvalidOperationException("Не удалось открыть RFCOMM-сервис адаптера.");

        _socket = new StreamSocket();
        await _socket.ConnectAsync(
            service.ConnectionHostName,
            service.ConnectionServiceName,
            SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);

        _writer = new DataWriter(_socket.OutputStream);
        _reader = new DataReader(_socket.InputStream);
        _reader.InputStreamOptions = InputStreamOptions.Partial;

        return elmDevice.Name;
    }

    public async Task DisconnectAsync()
    {
        try
        {
            _writer?.DetachStream();
            _reader?.DetachStream();
            _socket?.Dispose();
        }
        catch { /* already closed */ }
        finally
        {
            _writer = null;
            _reader = null;
            _socket = null;
        }

        await Task.CompletedTask;
    }

    public async Task<string> SendAsync(byte[] data, CancellationToken ct = default)
        => await SendAsync(data, 3000, ct);

    public async Task<string> SendAsync(byte[] data, int timeoutMs, CancellationToken ct = default)
    {
        if (_writer == null || _reader == null)
            return "";

        try
        {
            // ── Write command ──
            _writer.WriteBytes(data);
            await _writer.StoreAsync().AsTask(ct);

            // ── Read response until ">" prompt ──
            var buffer = new List<byte>();
            var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(timeoutMs); // configurable timeout

            while (!readCts.IsCancellationRequested)
            {
                var loaded = await _reader.LoadAsync(256).AsTask(readCts.Token);
                if (loaded == 0) break;

                while (_reader.UnconsumedBufferLength > 0)
                {
                    var b = _reader.ReadByte();
                    buffer.Add(b);

                    // Stop at ">" prompt (end of ELM327 response)
                    if (b == '>' && buffer.Count >= 2)
                    {
                        // Also stop if previous char was \r or \n (end of line)
                        var prev = buffer[^2];
                        if (prev is (byte)'\r' or (byte)'\n' or (byte)' ')
                            break;
                    }
                }

                // Check if we've received the prompt
                if (buffer.Count >= 2 && buffer[^1] == '>')
                    break;
            }

            if (buffer.Count == 0) return "";

            var text = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            return text;
        }
        catch (OperationCanceledException)
        {
            return "";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
#endif
