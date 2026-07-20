namespace CarDiagnosticApp.Services;

/// <summary>
/// Platform-specific classic Bluetooth (RFCOMM/SPP) transport.
/// Replaces Plugin.BLE with native Rfcomm/BluetoothSocket.
/// </summary>
public interface IBluetoothTransport
{
    /// <summary>True when connected to an ELM327 adapter.</summary>
    bool IsConnected { get; }

    /// <summary>Scan for ELM327 devices and connect to the first one found.</summary>
    /// <returns>Device name.</returns>
    Task<string> ConnectAsync(int scanTimeoutMs = 5000, CancellationToken ct = default);

    /// <summary>Disconnect from the adapter.</summary>
    Task DisconnectAsync();

    /// <summary>Send raw bytes and return raw response.</summary>
    Task<string> SendAsync(byte[] data, CancellationToken ct = default);
}
