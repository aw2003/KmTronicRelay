// =============================================================================
// KmTronicCommands.cs — ALL relay-board logic in one place
//
// Responsibilities:
//   1. Talk to the KMTronic board over serial (9600 baud)
//   2. Find the board among FTDI USB serial ports (VID 0403 / PID 6001)
//      Windows: registry + WMI (COM ports)
//   3. Save/load settings in %LocalAppData%\KmTronicRelay\settings.json
//   4. Reconnect after USB unplug and restore relay states
//
// KMTronic serial protocol (3 bytes each):
//   [0xFF, relayNumber, state]  — set one relay (1-8), state 0x00=off 0x01=on
//   [0xFF, 0x09, 0x00]          — read all 8 relay states (returns 8 bytes)
// =============================================================================

using System.IO;
using System.IO.Ports;
using System.Text.Json;
using Microsoft.Win32;
using System.Management;

namespace KmTronicRelay;

/**
 * @brief Connection lifecycle phase for the relay board driver.
 */
public enum RelayConnectionPhase {
    Connecting,
    Connected,
    Disconnected,
    Reconnecting,
    NotFound
}

/**
 * @brief Typed connection status payload for UI handlers.
 */
public sealed class RelayConnectionEventArgs : EventArgs {
    public RelayConnectionPhase Phase { get; init; }
    public string Message { get; init; } = string.Empty;
}

/**
 * @brief Result of a relay command that may partially succeed on hardware.
 */
public enum RelayCommandResult {
    Success,
    Failed,
    PartialFailure
}

/**
 * @brief Per-channel UART monitor serial port configuration (baud, parity, flow control).
 */
public sealed class SerialPortSettings {
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public string StopBits { get; set; } = "One";
    public string Parity { get; set; } = "None";
    public string Handshake { get; set; } = "None";
    public bool LogToFile { get; set; }
    public string LogFilePath { get; set; } = string.Empty;
    public bool ShowTimestamps { get; set; }

    /**
     * @brief Returns factory-default serial port settings.
     * @retval SerialPortSettings New instance with default values.
     */
    public static SerialPortSettings CreateDefaults() => new();

    /**
     * @brief Creates a deep copy of this settings object.
     * @retval SerialPortSettings Independent clone of all property values.
     */
    public SerialPortSettings Clone() => new() {
        BaudRate = BaudRate,
        DataBits = DataBits,
        StopBits = StopBits,
        Parity = Parity,
        Handshake = Handshake,
        LogToFile = LogToFile,
        LogFilePath = LogFilePath,
        ShowTimestamps = ShowTimestamps
    };

    /**
     * @brief Formats a timestamp prefix for UART log and monitor lines.
     * @param timestamp Local timestamp to format.
     * @retval string Bracketed timestamp prefix with trailing space.
     */
    public static string FormatLogTimestamp(DateTime timestamp) =>
        $"[{timestamp:yyyy-MM-dd HH:mm:ss.fff}] ";

    /**
     * @brief Builds a compact human-readable summary for the monitor header.
     * @retval string Summary string (e.g. "115200 8N1 none").
     */
    public string ToSummaryString() {
        char parityChar = Parity switch {
            "Odd" => 'O',
            "Even" => 'E',
            "Mark" => 'M',
            "Space" => 'S',
            _ => 'N'
        };
        string stop = StopBits switch {
            "Two" => "2",
            "OnePointFive" => "1.5",
            _ => "1"
        };
        string flow = Handshake switch {
            "XOnXOff" => "XON/XOFF",
            "RequestToSend" => "RTS/CTS",
            "RequestToSendXOnXOff" => "RTS/CTS+XON",
            _ => "none"
        };
        return $"{BaudRate} {DataBits}{parityChar}{stop} {flow}";
    }

    /**
     * @brief Applies these settings to an open or closed SerialPort instance.
     * @param port Target serial port to configure.
     */
    public void ApplyTo(SerialPort port) {
        port.BaudRate = BaudRate;
        port.DataBits = DataBits;
        port.StopBits = this.StopBits switch {
            "Two" => System.IO.Ports.StopBits.Two,
            "OnePointFive" => System.IO.Ports.StopBits.OnePointFive,
            "None" => System.IO.Ports.StopBits.None,
            _ => System.IO.Ports.StopBits.One
        };
        port.Parity = this.Parity switch {
            "Odd" => System.IO.Ports.Parity.Odd,
            "Even" => System.IO.Ports.Parity.Even,
            "Mark" => System.IO.Ports.Parity.Mark,
            "Space" => System.IO.Ports.Parity.Space,
            _ => System.IO.Ports.Parity.None
        };
        port.Handshake = this.Handshake switch {
            "XOnXOff" => System.IO.Ports.Handshake.XOnXOff,
            "RequestToSend" => System.IO.Ports.Handshake.RequestToSend,
            "RequestToSendXOnXOff" => System.IO.Ports.Handshake.RequestToSendXOnXOff,
            _ => System.IO.Ports.Handshake.None
        };
    }
}

/**
 * @brief KMTronic 8-port USB relay board driver: serial I/O, discovery, settings, reconnect.
 */
public sealed class KmTronicCommands : IDisposable {
    public const string NotApplicable = "N/A";
    public const int RelayCount = 8;

    // --- settings file location ---
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KmTronicRelay");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
    private static readonly string[] DefaultNames = {
        "Relay 01", "Relay 02", "Relay 03", "Relay 04",
        "Relay 05", "Relay 06", "Relay 07", "Relay 08"
    };

    public static string UserSettingsDirectory => SettingsDirectory;

    public static string UserSettingsFilePath => SettingsPath;

    // --- FTDI USB identifiers for the KMTronic board ---
    private const ushort FtdiVendorId = 0x0403;
    private const ushort FtdiDeviceId = 0x6001;
    private static readonly TimeSpan FtdiPortCacheLifetime = TimeSpan.FromSeconds(3);
    private const int HealthCheckIntervalTicks = 4;

    // --- serial port and connection state ---
    private readonly object _settingsSync = new();
    private readonly object _portSync = new();
    private readonly SerialPort _serialPort;
    private readonly System.Timers.Timer _connectionTimer;
    private readonly bool[] _desiredRelayStates = new bool[RelayCount];
    private bool[]? _pendingRestoreStates;  // snapshot taken at disconnect for restore
    private RelaySettings? _settingsCache;
    private bool _disposed;
    private bool _initialSyncCompleted;
    private string? _lockedPortName;          // COM port currently in use
    private int _monitorBusy;
    private int _restoreGeneration;
    private CancellationTokenSource? _restoreCancel;
    private readonly ManualResetEventSlim _restoreIdle = new(true);
    private int _healthCheckTicks;

    private static readonly object _ftdiCacheSync = new();
    private static IReadOnlyList<string>? _ftdiPortCache;
    private static DateTime _ftdiPortCacheTime;

    // UI listens to these events (MainWindow updates status bar / relay dots)
    public event EventHandler<RelayConnectionEventArgs>? ConnectionPhaseChanged;
    public event EventHandler<bool[]>? RelayStatusesChanged;
    public event EventHandler<bool[]>? DesiredRelayStatesChanged;
    public event EventHandler<string>? SettingsSaveFailed;

    public bool IsConnected {
        get {
            lock (_portSync) {return _serialPort.IsOpen;}
        }
    }

    /**
     * @brief Returns the relay-board COM port currently in use, if any.
     * @retval string? Active board COM port name.
     */
    public string? GetRelayBoardComPort() {
        lock (_portSync) {return _lockedPortName;}
    }

    /**
     * @brief Constructs the relay driver, configures the serial port, and starts the connection timer.
     */
    public KmTronicCommands() {
        _serialPort = new SerialPort {
            BaudRate = 9600,
            ReadTimeout = 1000,
            WriteTimeout = 1000
        };
        _serialPort.ErrorReceived += (_, _) => {
            lock (_portSync) {HandleRelayDisconnectedCore();}
        };

        // Poll every 1.5s: detect unplug, or reconnect if board reappears
        _connectionTimer = new System.Timers.Timer(1500);
        _connectionTimer.Elapsed += (_, _) => MonitorConnection();
        _connectionTimer.AutoReset = true;
    }

    // -------------------------------------------------------------------------
    // Public API — called from MainWindow
    // -------------------------------------------------------------------------

    /**
     * @brief Starts connection monitoring and attempts to open the relay board port.
     */
    public void Start() {
        _connectionTimer.Start();
        RaiseConnectionPhase(RelayConnectionPhase.Connecting, "Connecting to KmTronic 8-Port USB Relay...");
        lock (_portSync) {
            if (!TryOpenRelayPortCore()) {RaiseConnectionPhase(RelayConnectionPhase.NotFound, "KmTronic 8-Port USB Relay not found");}
        }
    }

    /**
     * @brief Stops connection monitoring and closes the relay serial port.
     */
    public void Stop() {
        _connectionTimer.Stop();
        CancelRestore();
        lock (_portSync) {CloseRelayPortCore();}
    }

    /**
     * @brief Sets a single relay on or off via the KMTronic protocol.
     * @param relayNumber Relay number (1-8).
     * @param on True to energize the relay; false to de-energize.
     * @retval bool True if the command was written successfully.
     */
    public bool SetRelay(int relayNumber, bool on) {
        if (relayNumber < 1 || relayNumber > RelayCount) {return false;}

        lock (_portSync) {
            byte state = on ? (byte)0x01 : (byte)0x00;
            if (!WriteToRelayCore(new byte[] { 0xFF, (byte)relayNumber, state })) {
                SyncDesiredStatesFromHardwareCore();
                NotifyDesiredRelayStatesChanged();
                return false;
            }

            _desiredRelayStates[relayNumber - 1] = on;
            return true;
        }
    }

    /**
     * @brief Sets all eight relays on or off with two broadcast commands.
     * @param on True to turn all relays on; false to turn all off.
     * @retval RelayCommandResult Success, Failed, or PartialFailure (hardware synced on partial failure).
     */
    public RelayCommandResult SetAllRelays(bool on) {
        lock (_portSync) {
            byte state = on ? (byte)0x01 : (byte)0x00;
            if (!WriteToRelayCore(new byte[] { 0xFF, 0x00, state })) {return RelayCommandResult.Failed;}
            if (!WriteToRelayCore(new byte[] { 0xFF, 0x01, state }) &&
                !WriteToRelayCore(new byte[] { 0xFF, 0x01, state })) {
                SyncDesiredStatesFromHardwareCore();
                NotifyDesiredRelayStatesChanged();
                return RelayCommandResult.PartialFailure;
            }

            for (int i = 0; i < _desiredRelayStates.Length; i++) {
                _desiredRelayStates[i] = on;
            }
            NotifyDesiredRelayStatesChanged();
            return RelayCommandResult.Success;
        }
    }

    /**
     * @brief Returns a snapshot of desired relay states (index 0 = relay 1).
     * @retval bool[] Clone of eight relay on/off states.
     */
    public bool[] GetDesiredRelayStates() {
        lock (_portSync) {return (bool[])_desiredRelayStates.Clone();}
    }

    /**
     * @brief Updates the in-memory desired relay states (and pending restore snapshot if disconnected).
     * @param states Current on/off state for each relay (index 0 = relay 1).
     */
    public void RememberDesiredRelayStates(IReadOnlyList<bool> states) {
        lock (_portSync) {
            for (int i = 0; i < _desiredRelayStates.Length && i < states.Count; i++) {
                _desiredRelayStates[i] = states[i];
            }

            if (_pendingRestoreStates != null) {
                for (int i = 0; i < _pendingRestoreStates.Length && i < states.Count; i++) {
                    _pendingRestoreStates[i] = states[i];
                }
            }
        }
    }

    /**
     * @brief Loads saved relay display names from settings.json.
     * @retval string[] Eight relay names, or defaults if missing or invalid.
     */
    public string[] LoadRelayNames() {
        lock (_settingsSync) {
            EnsureSettingsLoadedUnsafe();
            return (string[])_settingsCache!.RelayNames.Clone();
        }
    }

    /**
     * @brief Loads per-channel UART monitor COM port assignments from settings.
     * @retval string[] Eight COM port names or "N/A" per channel.
     */
    public string[] LoadChannelComPorts() {
        lock (_settingsSync) {
            EnsureSettingsLoadedUnsafe();
            return (string[])_settingsCache!.ChannelComPorts.Clone();
        }
    }

    /**
     * @brief Loads the last known relay-board COM port from settings.
     * @retval string? Saved board COM port, or null if unset.
     */
    public string? LoadSavedRelayBoardComPort() {
        lock (_settingsSync) {
            EnsureSettingsLoadedUnsafe();
            string? comPort = _settingsCache!.ComPort;
            return string.IsNullOrWhiteSpace(comPort) ? null : comPort;
        }
    }

    /**
     * @brief Persists relay display names to settings.json.
     * @param names Eight relay names (ignored if count differs from RelayCount).
     */
    public void SaveRelayNames(IReadOnlyList<string> names) {
        if (names.Count != RelayCount) {return;}

        lock (_settingsSync) {
            EnsureSettingsLoadedUnsafe();
            _settingsCache!.RelayNames = names.ToArray();
            SaveSettingsUnsafe(_settingsCache);
        }
    }

    /**
     * @brief Persists per-channel UART monitor COM port assignments to settings.
     * @param channelComPorts Eight COM port names (ignored if count differs from RelayCount).
     */
    public void SaveChannelComPorts(IReadOnlyList<string> channelComPorts) {
        if (channelComPorts.Count != RelayCount) {return;}

        lock (_settingsSync) {
            EnsureSettingsLoadedUnsafe();
            _settingsCache!.ChannelComPorts = channelComPorts.Select(NormalizeChannelComPort).ToArray();
            SaveSettingsUnsafe(_settingsCache);
        }
    }

    /**
     * @brief Loads per-channel UART serial settings from settings.json.
     * @retval SerialPortSettings[] Eight normalized SerialPortSettings instances.
     */
    public SerialPortSettings[] LoadChannelSerialSettings() {
        lock (_settingsSync) {
            EnsureSettingsLoadedUnsafe();
            return _settingsCache!.ChannelSerialSettings.Select(s => s.Clone()).ToArray();
        }
    }

    /**
     * @brief Persists per-channel UART serial settings to settings.json.
     * @param channelSerialSettings Eight SerialPortSettings (ignored if count differs from RelayCount).
     */
    public void SaveChannelSerialSettings(IReadOnlyList<SerialPortSettings> channelSerialSettings) {
        if (channelSerialSettings.Count != RelayCount) {return;}

        lock (_settingsSync) {
            EnsureSettingsLoadedUnsafe();
            _settingsCache!.ChannelSerialSettings = channelSerialSettings.Select(s => s.Clone()).ToArray();
            SaveSettingsUnsafe(_settingsCache);
        }
    }

    /**
     * @brief Persists relay names, channel COM ports, and serial settings in one atomic write.
     * @param names Eight relay names.
     * @param channelComPorts Eight COM port names.
     * @param channelSerialSettings Eight serial port settings objects.
     * @retval bool True if settings were written successfully.
     */
    public bool SaveAllUserSettings(
        IReadOnlyList<string> names,
        IReadOnlyList<string> channelComPorts,
        IReadOnlyList<SerialPortSettings> channelSerialSettings) {
        if (names.Count != RelayCount || channelComPorts.Count != RelayCount || channelSerialSettings.Count != RelayCount) {return false;}

        lock (_settingsSync) {
            EnsureSettingsLoadedUnsafe();
            _settingsCache!.RelayNames = names.ToArray();
            _settingsCache.ChannelComPorts = channelComPorts.Select(NormalizeChannelComPort).ToArray();
            _settingsCache.ChannelSerialSettings = channelSerialSettings.Select(s => s.Clone()).ToArray();
            return SaveSettingsUnsafe(_settingsCache);
        }
    }

    /**
     * @brief Returns all system COM ports sorted numerically then alphabetically.
     * @retval IReadOnlyList<string> Ordered list of COM port names.
     */
    public static IReadOnlyList<string> GetSystemComPorts() {
        return SerialPort.GetPortNames()
            .OrderBy(ParseComPortNumber)
            .ThenBy(port => port, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /**
     * @brief Releases the connection timer and serial port resources.
     */
    public void Dispose() {
        if (_disposed) {return;}
        _disposed = true;
        _connectionTimer.Stop();
        _connectionTimer.Dispose();
        CancelRestore();
        lock (_portSync) {CloseRelayPortCore();}
        _serialPort.Dispose();
        _restoreIdle.Dispose();
        _restoreCancel?.Dispose();
    }

    // -------------------------------------------------------------------------
    // Connection — find board, open port, probe with status command
    // -------------------------------------------------------------------------

    /**
     * @brief Attempts to open the relay board on a saved or discovered FTDI COM port.
     * @retval bool True if the port is open and the board responded to a status query.
     */
    private bool TryOpenRelayPortCore() {
        if (_serialPort.IsOpen && !string.IsNullOrEmpty(_lockedPortName)) {return true;}

        // Try saved port first, then scan all FTDI ports
        foreach (string portName in BuildCandidatePortList()) {
            if (!IsFtdiRelayPort(portName)) {continue;}
            if (TryConnectToPortCore(portName)) {return true;}
        }
        return false;
    }

    /**
     * @brief Builds an ordered candidate list: saved port first, then all FTDI relay ports.
     * @retval IReadOnlyList<string> Deduplicated COM port names to try.
     */
    private IReadOnlyList<string> BuildCandidatePortList() {
        List<string> candidates = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        string? savedPort = LoadSavedRelayBoardComPort();
        if (!string.IsNullOrWhiteSpace(savedPort) && seen.Add(savedPort)) {candidates.Add(savedPort);}
        foreach (string portName in GetFtdiRelayPortNames()) {
            if (seen.Add(portName)) {candidates.Add(portName);}
        }
        return candidates;
    }

    /**
     * @brief Opens a COM port, verifies KMTronic response, and syncs or restores relay states.
     * @param portName COM port to probe.
     * @retval bool True if connection succeeded and status was read.
     */
    private bool TryConnectToPortCore(string portName) {
        try {
            CloseRelayPortCore();
            _serialPort.PortName = portName;
            _serialPort.Open();

            // Valid KMTronic board must respond to status query with 8 bytes of 0x00/0x01
            if (!TryReadRelayStatusCore(out bool[]? relayStates) || relayStates == null) {CloseRelayPortCore(); return false;}

            _lockedPortName = portName;
            ReplaceSavedRelayBoardComPort(portName);
            _healthCheckTicks = 0;
            RaiseConnectionPhase(RelayConnectionPhase.Connected, $"KmTronic 8-Port USB Relay connected on {portName}");

            if (!_initialSyncCompleted) {
                for (int i = 0; i < relayStates.Length; i++) {_desiredRelayStates[i] = relayStates[i];}
                RelayStatusesChanged?.Invoke(this, relayStates);
                _initialSyncCompleted = true;
            } else {ScheduleRestoreDesiredRelayStates();}

            return true;
        } catch (IOException) {CloseRelayPortCore(); return false;}
        catch (UnauthorizedAccessException) {CloseRelayPortCore(); return false;}
    }

    /**
     * @brief Timer callback: validates an open connection or attempts reconnect.
     */
    private void MonitorConnection() {
        if (Interlocked.CompareExchange(ref _monitorBusy, 1, 0) != 0) {return;}

        try {
            lock (_portSync) {
                if (_disposed) {return;}

                if (_serialPort.IsOpen) {
                    if (string.IsNullOrEmpty(_lockedPortName) || !IsFtdiRelayPort(_lockedPortName)) {
                        HandleRelayDisconnectedCore();
                        return;
                    }
                    if (++_healthCheckTicks >= HealthCheckIntervalTicks) {
                        _healthCheckTicks = 0;
                        if (!TryPingRelayCore()) {HandleRelayDisconnectedCore();}
                    }
                    return;
                }
                if (!TryOpenRelayPortCore()) {
                    if (_pendingRestoreStates != null) {
                        RaiseConnectionPhase(RelayConnectionPhase.Reconnecting, "KmTronic 8-Port USB Relay disconnected — reconnecting…");
                    } else {
                        RaiseConnectionPhase(RelayConnectionPhase.NotFound, "KmTronic 8-Port USB Relay not found");
                    }
                }
            }
        } finally {
            Interlocked.Exchange(ref _monitorBusy, 0);
        }
    }

    /**
     * @brief Handles unexpected disconnect: preserves desired states and closes the port.
     */
    private void HandleRelayDisconnectedCore() {
        _pendingRestoreStates = (bool[])_desiredRelayStates.Clone();
        _lockedPortName = null;
        CloseRelayPortCore();
        InvalidateFtdiPortCache();
        RaiseConnectionPhase(RelayConnectionPhase.Disconnected, "KmTronic 8-Port USB Relay disconnected — relay states preserved");
    }

    /**
     * @brief Cancels an in-progress relay restore and waits briefly for it to finish.
     */
    private void CancelRestore() {
        _restoreCancel?.Cancel();
        _restoreIdle.Wait(TimeSpan.FromSeconds(5));
    }

    /**
     * @brief Schedules relay state restore on a background task so the timer thread is not blocked.
     */
    private void ScheduleRestoreDesiredRelayStates() {
        _restoreCancel?.Cancel();
        _restoreCancel?.Dispose();
        _restoreCancel = new CancellationTokenSource();
        int generation = Interlocked.Increment(ref _restoreGeneration);
        CancellationToken token = _restoreCancel.Token;
        _ = Task.Run(() => RestoreDesiredRelayStatesAsync(generation, token));
    }

    /**
     * @brief Re-applies desired relay states after reconnect (uses pending snapshot if set).
     * @param generation Restore generation used to ignore stale runs.
     * @param token Cancellation token to abort restore.
     */
    private async Task RestoreDesiredRelayStatesAsync(int generation, CancellationToken token) {
        _restoreIdle.Reset();
        try {
            await Task.Delay(400, token).ConfigureAwait(false);

            bool[] statesToRestore;
            lock (_portSync) {
                if (_disposed || generation != _restoreGeneration || token.IsCancellationRequested || !_serialPort.IsOpen) {return;}
                statesToRestore = (_pendingRestoreStates ?? _desiredRelayStates).ToArray();
            }

            for (int i = 0; i < statesToRestore.Length; i++) {
                if (i > 0) {await Task.Delay(80, token).ConfigureAwait(false);}

                lock (_portSync) {
                    if (_disposed || generation != _restoreGeneration || token.IsCancellationRequested || !_serialPort.IsOpen) {return;}

                    try {
                        if (i == 0) {_serialPort.DiscardInBuffer();}
                        byte state = statesToRestore[i] ? (byte)0x01 : (byte)0x00;
                        _serialPort.Write(new byte[] { 0xFF, (byte)(i + 1), state }, 0, 3);
                        _desiredRelayStates[i] = statesToRestore[i];
                    } catch (IOException) {
                        NotifyDesiredRelayStatesChanged();
                        HandleRelayDisconnectedCore();
                        return;
                    }
                }
            }

            lock (_portSync) {
                if (generation == _restoreGeneration && !token.IsCancellationRequested) {
                    _pendingRestoreStates = null;
                    NotifyDesiredRelayStatesChanged();
                }
            }
        } catch (OperationCanceledException) {}
        finally {
            _restoreIdle.Set();
        }
    }

    // -------------------------------------------------------------------------
    // Serial I/O
    // -------------------------------------------------------------------------

    /**
     * @brief Writes a 3-byte KMTronic command to the relay board.
     * @param command Protocol bytes to send.
     * @retval bool True if the write completed without error.
     */
    private bool WriteToRelayCore(byte[] command) {
        try {
            if (!_serialPort.IsOpen && !TryOpenRelayPortCore()) {return false;}

            _serialPort.Write(command, 0, command.Length);
            return true;
        } catch (IOException) {HandleRelayDisconnectedCore(); return false;}
    }

    /**
     * @brief Reads hardware relay states into _desiredRelayStates after a partial all-relay write failure.
     */
    private void SyncDesiredStatesFromHardwareCore() {
        if (!TryReadRelayStatusCore(out bool[]? states) || states == null) {return;}

        for (int i = 0; i < states.Length && i < _desiredRelayStates.Length; i++) {
            _desiredRelayStates[i] = states[i];
        }
    }

    /**
     * @brief Raises DesiredRelayStatesChanged with a snapshot of current desired states.
     */
    private void NotifyDesiredRelayStatesChanged() {
        DesiredRelayStatesChanged?.Invoke(this, (bool[])_desiredRelayStates.Clone());
    }

    /**
     * @brief Lightweight connectivity probe with a short read timeout.
     * @retval bool True if the board responded to a status query.
     */
    private bool TryPingRelayCore() {
        return TryReadRelayStatusCore(out _, readTimeoutMs: 300);
    }

    /**
     * @brief Sends the all-relay status query and reads eight state bytes.
     * @param relayStates Output array of eight on/off states when successful.
     * @param readTimeoutMs Serial read timeout in milliseconds for the status response.
     * @retval bool True if a valid 8-byte status response was received.
     */
    private bool TryReadRelayStatusCore(out bool[]? relayStates, int readTimeoutMs = 1000) {
        relayStates = null;
        if (!_serialPort.IsOpen) {return false;}

        int previousReadTimeout = _serialPort.ReadTimeout;
        try {
            _serialPort.ReadTimeout = readTimeoutMs;
            _serialPort.DiscardInBuffer();
            _serialPort.Write(new byte[] { 0xFF, 0x09, 0x00 }, 0, 3);

            byte[] status = new byte[RelayCount];
            int totalRead = 0;
            while (totalRead < status.Length) {
                totalRead += _serialPort.Read(status, totalRead, status.Length - totalRead);
            }

            bool[] states = new bool[RelayCount];
            for (int i = 0; i < states.Length; i++) {
                if (status[i] != 0x00 && status[i] != 0x01) {return false;}

                states[i] = status[i] == 0x01;
            }

            relayStates = states;
            return true;
        } catch (IOException) {return false;}
        catch (TimeoutException) {return false;} finally {
            _serialPort.ReadTimeout = previousReadTimeout;
        }
    }

    /**
     * @brief Closes the relay serial port if open.
     */
    private void CloseRelayPortCore() {
        try {
            if (_serialPort.IsOpen) {_serialPort.Close();}
        } catch (IOException) {}
    }

    /**
     * @brief Raises ConnectionPhaseChanged with a typed phase and human-readable message.
     * @param phase Connection lifecycle phase.
     * @param message Status text for the UI.
     */
    private void RaiseConnectionPhase(RelayConnectionPhase phase, string message) {
        ConnectionPhaseChanged?.Invoke(this, new RelayConnectionEventArgs { Phase = phase, Message = message });
    }

    // -------------------------------------------------------------------------
    // Settings persistence (settings.json)
    // -------------------------------------------------------------------------

    /**
     * @brief Loads settings from disk into the in-memory cache on first access.
     */
    private void EnsureSettingsLoadedUnsafe() {
        if (_settingsCache != null) {return;}
        _settingsCache = LoadSettingsFromDiskUnsafe();
    }

    /**
     * @brief Saves the relay-board COM port to settings after a successful connection.
     * @param comPort COM port name to persist.
     */
    private void ReplaceSavedRelayBoardComPort(string comPort) {
        if (string.IsNullOrWhiteSpace(comPort)) {return;}

        lock (_settingsSync) {
            EnsureSettingsLoadedUnsafe();
            _settingsCache!.ComPort = comPort;
            SaveSettingsUnsafe(_settingsCache);
        }
    }

    /**
     * @brief Loads settings from disk without locking (caller must hold _settingsSync).
     * @retval RelaySettings Deserialized settings or defaults on error.
     */
    private RelaySettings LoadSettingsFromDiskUnsafe() {
        try {
            if (!File.Exists(SettingsPath)) {return CreateDefaultSettings();}

            RelaySettings? settings = JsonSerializer.Deserialize<RelaySettings>(File.ReadAllText(SettingsPath));
            if (settings == null) {return CreateDefaultSettings();}

            if (settings.RelayNames == null || settings.RelayNames.Length != RelayCount) {
                settings.RelayNames = (string[])DefaultNames.Clone();
            }
            settings.ChannelComPorts = NormalizeChannelComPorts(settings.ChannelComPorts);
            settings.ChannelSerialSettings = NormalizeChannelSerialSettings(settings.ChannelSerialSettings);
            return settings;
        } catch (JsonException) {return CreateDefaultSettings();}
        catch (IOException) {return CreateDefaultSettings();}
    }

    /**
     * @brief Creates a fresh RelaySettings instance with normalized defaults.
     * @retval RelaySettings Default settings object.
     */
    private static RelaySettings CreateDefaultSettings() {
        return new RelaySettings {
            RelayNames = (string[])DefaultNames.Clone(),
            ChannelComPorts = NormalizeChannelComPorts(null),
            ChannelSerialSettings = NormalizeChannelSerialSettings(null)
        };
    }

    /**
     * @brief Writes settings to disk without locking (caller must hold _settingsSync).
     * @param settings Settings object to serialize to settings.json.
     * @retval bool True if the file was written successfully.
     */
    private bool SaveSettingsUnsafe(RelaySettings settings) {
        try {
            Directory.CreateDirectory(SettingsDirectory);
            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            string tempPath = SettingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsPath, overwrite: true);
            return true;
        } catch (IOException ex) {
            SettingsSaveFailed?.Invoke(this, ex.Message);
            return false;
        }
    }

    /**
     * @brief Normalizes a channel COM port array to exactly RelayCount entries.
     * @param channelComPorts Raw COM port array from settings (may be null or short).
     * @retval string[] Eight normalized COM port names or "N/A".
     */
    private static string[] NormalizeChannelComPorts(string[]? channelComPorts) {
        string[] normalized = new string[RelayCount];
        for (int i = 0; i < normalized.Length; i++) {
            normalized[i] = channelComPorts != null && i < channelComPorts.Length
                ? NormalizeChannelComPort(channelComPorts[i])
                : NotApplicable;
        }

        return normalized;
    }

    /**
     * @brief Normalizes a single channel COM port value to a port name or "N/A".
     * @param comPort Raw COM port string from settings.
     * @retval string Normalized COM port name or NotApplicable.
     */
    internal static string NormalizeChannelComPort(string? comPort) {
        return string.IsNullOrWhiteSpace(comPort) ||
               string.Equals(comPort, NotApplicable, StringComparison.OrdinalIgnoreCase)
            ? NotApplicable
            : comPort;
    }

    /**
     * @brief Normalizes a channel serial settings array to exactly RelayCount entries.
     * @param channelSerialSettings Raw settings array from disk (may be null or short).
     * @retval SerialPortSettings[] Eight validated SerialPortSettings instances.
     */
    private static SerialPortSettings[] NormalizeChannelSerialSettings(SerialPortSettings[]? channelSerialSettings) {
        SerialPortSettings[] normalized = new SerialPortSettings[RelayCount];
        for (int i = 0; i < normalized.Length; i++) {
            normalized[i] = channelSerialSettings != null && i < channelSerialSettings.Length
                ? NormalizeSerialSettings(channelSerialSettings[i])
                : SerialPortSettings.CreateDefaults();
        }
        return normalized;
    }

    /**
     * @brief Validates and clamps a single SerialPortSettings instance to supported values.
     * @param settings Settings to normalize (may be null).
     * @retval SerialPortSettings Sanitized settings with defaults for invalid fields.
     */
    internal static SerialPortSettings NormalizeSerialSettings(SerialPortSettings? settings) {
        if (settings == null) {return SerialPortSettings.CreateDefaults();}
        SerialPortSettings normalized = settings.Clone();
        if (normalized.BaudRate < 300 || normalized.BaudRate > 921600) {normalized.BaudRate = 115200;}
        if (normalized.DataBits < 5 || normalized.DataBits > 8) {normalized.DataBits = 8;}
        normalized.StopBits = normalized.StopBits switch {
            "Two" or "OnePointFive" or "None" or "One" => normalized.StopBits,
            _ => "One"
        };
        normalized.Parity = normalized.Parity switch {
            "Odd" or "Even" or "Mark" or "Space" or "None" => normalized.Parity,
            _ => "None"
        };
        normalized.Handshake = normalized.Handshake switch {
            "XOnXOff" or "RequestToSend" or "RequestToSendXOnXOff" or "None" => normalized.Handshake,
            _ => "None"
        };
        normalized.LogFilePath = (normalized.LogFilePath ?? string.Empty).Trim();
        if (normalized.LogToFile && normalized.LogFilePath.Length == 0) {normalized.LogToFile = false;}
        return normalized;
    }

    /**
     * @brief Extracts the numeric suffix from a COM port name for sorting.
     * @param portName COM port name (e.g. "COM12").
     * @retval int Port number, or int.MaxValue if not parseable.
     */
    internal static int ParseComPortNumber(string portName) {
        if (portName.Length > 3 &&
            portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(portName.AsSpan(3), out int portNumber)) {return portNumber;}

        return int.MaxValue;
    }

    // -------------------------------------------------------------------------
    // FTDI port detection (Windows — registry + WMI)
    // -------------------------------------------------------------------------

    /**
     * @brief Clears the cached FTDI port list so the next lookup rescans registry and WMI.
     */
    private static void InvalidateFtdiPortCache() {
        lock (_ftdiCacheSync) {_ftdiPortCache = null;}
    }

    /**
     * @brief Checks whether a COM port name is a known FTDI KMTronic relay port.
     * @param portName COM port to test.
     * @retval bool True if the port appears in the FTDI relay port list.
     */
    private static bool IsFtdiRelayPort(string portName) {
        foreach (string ftdiPort in GetFtdiRelayPortNames()) {
            if (string.Equals(ftdiPort, portName, StringComparison.OrdinalIgnoreCase)) {return true;}
        }
        return false;
    }

    /**
     * @brief Enumerates FTDI VID/PID 0403/6001 COM ports via registry and WMI (Windows only).
     * @retval IReadOnlyList<string> Sorted list of matching COM port names.
     */
    private static IReadOnlyList<string> GetFtdiRelayPortNames() {
        lock (_ftdiCacheSync) {
            if (_ftdiPortCache != null && DateTime.UtcNow - _ftdiPortCacheTime < FtdiPortCacheLifetime) {
                return _ftdiPortCache;
            }
        }

        if (!OperatingSystem.IsWindows()) {return Array.Empty<string>();}

        HashSet<string> ports = new(StringComparer.OrdinalIgnoreCase);
        CollectFtdiPortsFromRegistry(ports);
        CollectFtdiPortsFromWmi(ports);
        IReadOnlyList<string> discovered = ports
            .OrderBy(ParseComPortNumber)
            .ThenBy(port => port, StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_ftdiCacheSync) {
            _ftdiPortCache = discovered;
            _ftdiPortCacheTime = DateTime.UtcNow;
            return _ftdiPortCache;
        }
    }

    /**
     * @brief Scans the Windows FTDIBUS registry hive for KMTronic FTDI COM assignments.
     * @param ports Mutable set to receive discovered port names.
     */
    private static void CollectFtdiPortsFromRegistry(ISet<string> ports) {
        if (!OperatingSystem.IsWindows()) {return;}

        try {
            using RegistryKey? ftdiBus = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\FTDIBUS");
            if (ftdiBus == null) {return;}

            foreach (string deviceKeyName in ftdiBus.GetSubKeyNames()) {
                if (!ContainsFtdiIds(deviceKeyName)) {continue;}
                using RegistryKey? deviceKey = ftdiBus.OpenSubKey(deviceKeyName);
                if (deviceKey == null) {continue;}

                foreach (string instanceKeyName in deviceKey.GetSubKeyNames()) {
                    using RegistryKey? instanceKey = deviceKey.OpenSubKey(instanceKeyName);
                    using RegistryKey? deviceParams = instanceKey?.OpenSubKey("Device Parameters");
                    string? assignedPort = deviceParams?.GetValue("PortName") as string;
                    if (!string.IsNullOrWhiteSpace(assignedPort)) {ports.Add(assignedPort);}
                }
            }
        } catch (IOException) {}
    }

    /**
     * @brief Queries WMI for FTDI USB serial devices and extracts their COM port names.
     * @param ports Mutable set to receive discovered port names.
     */
    private static void CollectFtdiPortsFromWmi(ISet<string> ports) {
        if (!OperatingSystem.IsWindows()) {return;}

        try {
            using ManagementObjectSearcher searcher = new(
                "SELECT Name, Caption, DeviceID, PNPDeviceID FROM Win32_PnPEntity WHERE ClassGuid=\"{4d36e978-e325-11ce-bfc1-08002be10318}\"");
            foreach (ManagementObject entity in searcher.Get()) {
                if (!EntityIsFtdiRelay(entity)) {continue;}

                string caption = entity["Caption"] as string ?? entity["Name"] as string ?? string.Empty;
                string? portName = ExtractComPortName(caption);
                if (!string.IsNullOrWhiteSpace(portName)) {ports.Add(portName);}
            }
        } catch (ManagementException) {}
    }

    /**
     * @brief Extracts a COM port name from a PnP device caption string.
     * @param deviceName Device caption or name containing "(COMn)".
     * @retval string? COM port name, or null if not found.
     */
    private static string? ExtractComPortName(string deviceName) {
        int start = deviceName.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
        if (start < 0) {return null;}

        int end = deviceName.IndexOf(')', start);
        return end <= start ? null : deviceName.Substring(start + 1, end - start - 1);
    }

    /**
     * @brief Determines whether a WMI entity is an FTDI KMTronic relay device.
     * @param entity Win32_PnPEntity management object.
     * @retval bool True if DeviceID or PNPDeviceID contains FTDI VID/PID.
     */
    private static bool EntityIsFtdiRelay(ManagementBaseObject entity) {
        string deviceId = entity["DeviceID"] as string ?? string.Empty;
        string pnpDeviceId = entity["PNPDeviceID"] as string ?? string.Empty;
        return ContainsFtdiIds(deviceId) || ContainsFtdiIds(pnpDeviceId);
    }

    /**
     * @brief Checks whether a hardware ID string contains FTDI vendor and device IDs.
     * @param hardwareId USB hardware ID or PnP device ID string.
     * @retval bool True if both VID_0403 and PID_6001 are present.
     */
    internal static bool ContainsFtdiIds(string hardwareId) {
        if (string.IsNullOrEmpty(hardwareId)) {return false;}

        string vendorId = "VID_" + FtdiVendorId.ToString("X4");
        string deviceId = "PID_" + FtdiDeviceId.ToString("X4");
        return hardwareId.Contains(vendorId, StringComparison.OrdinalIgnoreCase) &&
               hardwareId.Contains(deviceId, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RelaySettings {
        public int SchemaVersion { get; set; } = 1;
        public string[] RelayNames { get; set; } = (string[])DefaultNames.Clone();
        public string? ComPort { get; set; }
        public string[] ChannelComPorts { get; set; } = NormalizeChannelComPorts(null);
        public SerialPortSettings[] ChannelSerialSettings { get; set; } = NormalizeChannelSerialSettings(null);
    }
}
