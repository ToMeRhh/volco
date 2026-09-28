using System.IO.Ports;
using Volco.Config;

namespace Volco.Communication;

/// <summary>
/// Serial transport for the controller connection.
///
/// Unlike <see cref="UdpConnection"/> (listen-only) and <see cref="WebSocketConnection"/> (has
/// its own accept loop), this transport holds an exclusive OS handle to a device that can
/// physically vanish (unplugged) without the handle itself reporting closed: <see cref="SerialPort.IsOpen"/>
/// can keep reporting true for a port whose underlying device is gone. So on top of reacting to
/// hard failures (ErrorReceived, exceptions from Read/Write), a watchdog timer independently
/// verifies the port is still present via <see cref="SerialPort.GetPortNames"/>, and a reconnect
/// timer retries <see cref="SerialPort.Open"/> once the port is missing. The retry interval
/// (<see cref="ReconnectIntervalMs"/>) mirrors the ESP32 firmware's own RECONNECT_INTERVAL_MS
/// (firmware/src/config.h) so both sides of the link agree on cadence.
/// </summary>
public class SerialConnection : IControllerConnection
{
    private const int WatchdogIntervalMs = 2000;
    private const int ReconnectIntervalMs = 3000;

    private readonly SerialConfig _config;
    private readonly ProtocolParser _parser = new();

    // Guards all mutable state below (_serialPort, the two timers, _disposed) so that public
    // calls (Connect/Disconnect/Dispose, all UI-thread) and the background callbacks (DataReceived/
    // ErrorReceived on the SerialPort threadpool thread, the watchdog and reconnect timers on
    // their own threadpool threads) never interleave a close/dispose with an open, or a status
    // transition with a teardown. `lock` (Monitor) is reentrant on the same thread, so
    // HandleDisconnectSignal can be called from within WatchdogTick, which already holds it.
    private readonly object _gate = new();

    private SerialPort? _serialPort;
    private Timer? _watchdogTimer;
    private Timer? _reconnectTimer;
    private bool _disposed;

    public event EventHandler<ControllerMessage>? MessageReceived;
    public event EventHandler<ConnectionStatus>? StatusChanged;
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

    /// <summary>True while the watchdog and/or reconnect timers are running. Internal test seam
    /// only — production code should never need to inspect this.</summary>
    internal bool HasBackgroundTimers => _watchdogTimer != null || _reconnectTimer != null;

    public SerialConnection(SerialConfig config)
    {
        _config = config;
        _parser.MessageParsed += (_, msg) => MessageReceived?.Invoke(this, msg);
    }

    public void Connect()
    {
        if (string.IsNullOrEmpty(_config.ComPort))
        {
            // Defensive guard for Bug 2: a blank ComPort should never reach here now that
            // ConnectionViewModel ignores/repairs null selections from the port-list refresh,
            // but if it ever does, fail cleanly instead of letting SerialPort's constructor
            // throw ArgumentNullException out of Connect(). This is the one genuine
            // configuration failure — it gets Error, not Disconnected, and never schedules a
            // retry (a blank port will never spontaneously become valid).
            AppLog.Error("Failed to open serial port: no COM port configured (PortName is null or empty)");
            SetStatus(ConnectionStatus.Error);
            return;
        }

        lock (_gate)
        {
            if (_disposed) return;
            TryOpenLocked(initialConnect: true);
        }
    }

    /// <summary>Attempts to open the configured port and, on success, wires up the watchdog and
    /// clears any pending reconnect. Must be called with <see cref="_gate"/> held. Used both by
    /// the initial <see cref="Connect"/> and by <see cref="ReconnectTick"/>. A failure to open is
    /// never a configuration error here (that's the blank-port guard in <see cref="Connect"/>'s
    /// job) — it just means the controller isn't present yet, so it lands on Disconnected and
    /// schedules a retry.</summary>
    private bool TryOpenLocked(bool initialConnect)
    {
        SerialPort? port = null;
        try
        {
            port = new SerialPort(_config.ComPort, _config.BaudRate);
            port.ErrorReceived += OnErrorReceived;
            port.DataReceived += OnDataReceived;
            port.Open();

            _serialPort = port;
            StopReconnectLocked();
            StartWatchdogLocked();
            SetStatus(ConnectionStatus.Connected);
            return true;
        }
        catch (Exception ex)
        {
            if (port != null)
            {
                port.DataReceived -= OnDataReceived;
                port.ErrorReceived -= OnErrorReceived;
                try { port.Dispose(); } catch { /* already gone */ }
            }

            if (initialConnect)
            {
                AppLog.Error($"Failed to open serial port {_config.ComPort}: {ex.Message}");
                SetStatus(ConnectionStatus.Disconnected);
                StartReconnectLocked();
            }

            return false;
        }
    }

    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        HandleDisconnectSignal($"serial error ({e.EventType})");
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (sender is not SerialPort port) return;

        try
        {
            // Ignore events from a port instance we've already torn down/replaced, and treat
            // IsOpen==false the same as before (still possible between Close() and the event
            // handler being unsubscribed).
            if (!ReferenceEquals(port, _serialPort) || !port.IsOpen) return;

            var bytesToRead = port.BytesToRead;
            if (bytesToRead <= 0) return;

            var buffer = new byte[bytesToRead];
            port.Read(buffer, 0, bytesToRead);
            _parser.FeedBytes(buffer);
        }
        catch (IOException ex)
        {
            HandleDisconnectSignal($"read failed: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            HandleDisconnectSignal($"read failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            HandleDisconnectSignal($"read failed: {ex.Message}");
        }
        catch (OperationCanceledException ex)
        {
            HandleDisconnectSignal($"read failed: {ex.Message}");
        }
    }

    public void Send(byte[] data)
    {
        var port = _serialPort;
        if (port is not { IsOpen: true }) return;

        try
        {
            port.Write(data, 0, data.Length);
        }
        catch (IOException ex)
        {
            HandleDisconnectSignal($"write failed: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            HandleDisconnectSignal($"write failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            HandleDisconnectSignal($"write failed: {ex.Message}");
        }
    }

    /// <summary>Common path for every way we learn the controller is gone: ErrorReceived,
    /// an exception out of Read/Write, or the watchdog finding the port missing. Idempotent —
    /// a second signal while already disconnected (or disposed) is a no-op, since concurrent
    /// callbacks (e.g. a failing Write racing the watchdog) can all reach here for the same
    /// underlying event.</summary>
    private void HandleDisconnectSignal(string reason)
    {
        lock (_gate)
        {
            if (_disposed || _serialPort == null) return;

            AppLog.Info($"Serial connection lost ({_config.ComPort}): {reason}");
            CleanupPortLocked();
            StopWatchdogLocked();
            SetStatus(ConnectionStatus.Disconnected);
            AppLog.Info($"Retrying serial connection to {_config.ComPort} every {ReconnectIntervalMs}ms");
            StartReconnectLocked();
        }
    }

    /// <summary>Verifies the currently-open port is still genuinely present. SerialPort.IsOpen
    /// alone is not reliable — Windows can keep reporting a handle open for a device that has
    /// physically disappeared — so this also cross-checks GetPortNames(). Must be called with
    /// <see cref="_gate"/> held (Timer callback acquires it itself).</summary>
    private void WatchdogTick(object? state)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var port = _serialPort;
            if (port == null) return;

            bool stillPresent;
            try
            {
                stillPresent = port.IsOpen && Array.IndexOf(SerialPort.GetPortNames(), _config.ComPort) >= 0;
            }
            catch
            {
                stillPresent = false;
            }

            if (!stillPresent)
                HandleDisconnectSignal("device no longer present");
        }
    }

    /// <summary>Retries opening the configured port every <see cref="ReconnectIntervalMs"/> while
    /// disconnected. Silent on failure (HandleDisconnectSignal already logged once when the
    /// retry loop started) — only success is logged. Must be called with <see cref="_gate"/> held
    /// (Timer callback acquires it itself).</summary>
    private void ReconnectTick(object? state)
    {
        lock (_gate)
        {
            if (_disposed || _serialPort != null) return;

            bool present;
            try
            {
                present = Array.IndexOf(SerialPort.GetPortNames(), _config.ComPort) >= 0;
            }
            catch
            {
                present = false;
            }

            if (!present) return;

            if (TryOpenLocked(initialConnect: false))
                AppLog.Info($"Serial port {_config.ComPort} reconnected");
        }
    }

    private void StartWatchdogLocked()
    {
        _watchdogTimer ??= new Timer(WatchdogTick, null, WatchdogIntervalMs, WatchdogIntervalMs);
    }

    private void StopWatchdogLocked()
    {
        _watchdogTimer?.Dispose();
        _watchdogTimer = null;
    }

    private void StartReconnectLocked()
    {
        _reconnectTimer ??= new Timer(ReconnectTick, null, ReconnectIntervalMs, ReconnectIntervalMs);
    }

    private void StopReconnectLocked()
    {
        _reconnectTimer?.Dispose();
        _reconnectTimer = null;
    }

    /// <summary>Unsubscribes, closes and disposes the current port, if any. Must be called with
    /// <see cref="_gate"/> held.</summary>
    private void CleanupPortLocked()
    {
        var port = _serialPort;
        _serialPort = null;
        if (port == null) return;

        port.DataReceived -= OnDataReceived;
        port.ErrorReceived -= OnErrorReceived;
        try { if (port.IsOpen) port.Close(); } catch { /* already gone */ }
        try { port.Dispose(); } catch { /* already gone */ }
    }

    private void SetStatus(ConnectionStatus status)
    {
        if (Status == status) return;
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            if (_disposed) return;
            StopReconnectLocked();
            StopWatchdogLocked();
            CleanupPortLocked();
            SetStatus(ConnectionStatus.Disconnected);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopReconnectLocked();
            StopWatchdogLocked();
            CleanupPortLocked();
            SetStatus(ConnectionStatus.Disconnected);
        }
    }
}
