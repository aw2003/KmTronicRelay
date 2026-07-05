// =============================================================================
// MainWindow.axaml.cs — ALL GUI logic in one place
//
// Layout is defined in MainWindow.axaml (header, relay list area, buttons, status).
// This file builds the 8 relay rows in code and handles:
//   - click / drag / double-click on status dots
//   - COM port dropdown per relay
//   - 2-second power-cycle (double-click an ON relay)
//   - All On / All Off buttons
// =============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using System.IO.Ports;
using System.Reflection;
using System.Text;

namespace KmTronicRelay;

/**
 * @brief Main window hosting all relay GUI controls, COM monitors, and user interaction logic.
 */
public partial class MainWindow : Window {
    private const int RelayCount = KmTronicCommands.RelayCount;
    private const int DoubleClickWindowMs = 500;
    private const int SingleClickDelayMs = 450;
    private const int MaxRelayNameLength = 20;
    private const double ComDropdownWidth = 132;
    private static readonly IBrush IconBrushDisabled = new SolidColorBrush(Color.FromRgb(160, 160, 160));
    private static readonly IBrush StatusDotOnBrush = new SolidColorBrush(Color.FromRgb(16, 124, 16));
    private static readonly IBrush StatusDotOffBrush = new SolidColorBrush(Color.FromRgb(196, 43, 28));
    private static readonly IBrush EraserBodyBrushActive = new SolidColorBrush(Color.FromRgb(240, 98, 146));
    private static readonly IBrush EraserFerruleBrushActive = new SolidColorBrush(Color.FromRgb(96, 96, 96));
    private static readonly IBrush GearIconBrushActive = new SolidColorBrush(Color.FromRgb(0, 120, 212));
    private static readonly IBrush HelpIconBrush = new SolidColorBrush(Color.FromRgb(96, 96, 96));
    private static readonly Geometry HelpIconGeometry = Geometry.Parse(
        "M11,18H13V16H11V18M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M12,20C7.59,20 4,16.41 4,12C4,7.59 7.59,4 12,4C16.41,4 20,7.59 20,12C20,16.41 16.41,20 12,20M12,6A4,4 0 0,0 8,10H10A2,2 0 0,1 12,8A2,2 0 0,1 14,10C14,12 11,11.75 11,15H13C13,12.75 16,12.5 16,10A4,4 0 0,0 12,6Z");
    private static readonly Geometry EraserBodyGeometry = Geometry.Parse("M5.5,19.5 L9,7.5 L17.5,10.5 L14,22.5 Z");
    private static readonly Geometry EraserFerruleGeometry = Geometry.Parse("M9,7.5 L11.5,6 L14,8 L11.5,9.5 Z");
    private static readonly Geometry GearIconGeometry = Geometry.Parse(
        "M19.14,12.94c0.04-0.3,0.06-0.61,0.06-0.94c0-0.32-0.02-0.64-0.07-0.94l2.03-1.58c0.18-0.14,0.23-0.41,0.12-0.61l-1.92-3.32c-0.12-0.22-0.39-0.31-0.61-0.22l-2.49,1c-0.52-0.39-1.06-0.73-1.69-0.98l-0.37-2.65C14.46,2.18,14.25,2,14,2h-4c-0.25,0-0.46,0.18-0.5,0.42l-0.37,2.65c-0.63,0.25-1.17,0.59-1.69,0.98l-2.49-1c-0.22-0.09-0.49,0-0.61,0.22l-1.92,3.32c-0.12,0.22-0.07,0.49,0.12,0.61l2.03,1.58c-0.05,0.3-0.07,0.62-0.07,0.94s0.02,0.64,0.07,0.94l-2.03,1.58c-0.18,0.14-0.23,0.41-0.12,0.61l1.92,3.32c0.12,0.22,0.39,0.31,0.61,0.22l2.49-1c0.52,0.39,1.06,0.73,1.69,0.98l0.37,2.65c0.04,0.24,0.25,0.42,0.5,0.42h4c0.25,0,0.46-0.18,0.5-0.42l0.37-2.65c0.63-0.25,1.17-0.59,1.69-0.98l2.49,1c0.22,0.09,0.49,0,0.61-0.22l1.92-3.32c0.12-0.22,0.07-0.49-0.12-0.61L19.14,12.94zM12,15.5c-1.93,0-3.5-1.57-3.5-3.5s1.57-3.5,3.5-3.5s3.5,1.57,3.5,3.5S13.93,15.5,12,15.5z");

    private readonly KmTronicCommands _relay = new();
    private readonly SerialPortSettings[] _serialSettings;
    private readonly DispatcherTimer _comPortRefreshTimer;
    private readonly DispatcherTimer _settingsSaveTimer;

    // Per-relay UI controls (index 0 = relay 1)
    private readonly TextBox[] _nameBox = new TextBox[RelayCount];
    private readonly ComboBox[] _comBox = new ComboBox[RelayCount];
    private readonly Button[] _comClearButton = new Button[RelayCount];
    private readonly Button[] _comSettingsButton = new Button[RelayCount];
    private readonly Avalonia.Controls.Shapes.Path[] _comClearEraserBody = new Avalonia.Controls.Shapes.Path[RelayCount];
    private readonly Avalonia.Controls.Shapes.Path[] _comClearEraserFerrule = new Avalonia.Controls.Shapes.Path[RelayCount];
    private readonly Avalonia.Controls.Shapes.Path[] _comSettingsIcon = new Avalonia.Controls.Shapes.Path[RelayCount];
    private readonly Border[] _hitTarget = new Border[RelayCount];
    private readonly Ellipse[] _statusDot = new Ellipse[RelayCount];
    private readonly CycleRing[] _cycleRing = new CycleRing[RelayCount];
    private readonly TextBlock[] _statusText = new TextBlock[RelayCount];

    // Per-relay state
    private readonly bool[] _isOn = new bool[RelayCount];
    private readonly bool[] _isCycling = new bool[RelayCount];
    private readonly string[] _selectedCom = new string[RelayCount];
    private readonly DispatcherTimer?[] _cycleTimer = new DispatcherTimer?[RelayCount];
    private readonly SerialMonitorWindow?[] _serialMonitorWindows = new SerialMonitorWindow?[RelayCount];

    // Click / drag tracking for status dots
    private bool _pointerDown;
    private bool _dragging;
    private int? _dragStart;
    private int? _dragCurrent;
    private int _lastClickIndex = -1;
    private DateTime _lastClickTime;
    private readonly CancellationTokenSource?[] _pendingClick = new CancellationTokenSource?[RelayCount];
    private bool _refreshingComPorts;
    private bool _hardwareSynced;
    private bool _settingsSavePending;
    private RelayConnectionPhase _connectionPhase = RelayConnectionPhase.Connecting;
    private string _connectionStatusMessage = "Connecting to KmTronic 8-Port USB Relay...";

    /**
     * @brief Initializes the main window, builds relay rows, and wires board and UI events.
     */
    public MainWindow() {
        InitializeComponent();
        ConfigureAboutButton();
        ConfigureMainWindowTooltips();

        _serialSettings = _relay.LoadChannelSerialSettings();
        string[] names = _relay.LoadRelayNames();
        string[] comPorts = _relay.LoadChannelComPorts();
        for (int i = 0; i < RelayCount; i++) {
            _selectedCom[i] = comPorts[i];
            RelayRowsPanel.Children.Add(BuildRelayRow(i, names[i]));
            RefreshRowVisuals(i);
        }

        _relay.ConnectionPhaseChanged += (_, e) => Dispatcher.UIThread.Post(() => {
            if (e.Phase is RelayConnectionPhase.Disconnected or RelayConnectionPhase.NotFound or RelayConnectionPhase.Reconnecting) {
                SaveRelayStatesToDevice();
            }
            bool phaseChanged = _connectionPhase != e.Phase;
            _connectionPhase = e.Phase;
            _connectionStatusMessage = e.Message;
            ConnectionStatusText.Text = e.Message;
            if (phaseChanged || e.Phase == RelayConnectionPhase.Connected) {RefreshComPortDropdowns();}
        });

        _relay.DesiredRelayStatesChanged += (_, states) => Dispatcher.UIThread.Post(() => {
            ApplyDesiredRelayStates(states);
            SyncMonitorsToRelayStates();
        });

        _relay.SettingsSaveFailed += (_, message) => Dispatcher.UIThread.Post(() => {
            ShowRelayCommandError($"Settings save failed: {message}");
        });

        _relay.RelayStatusesChanged += (_, states) => {
            if (_hardwareSynced) {return;}
            _hardwareSynced = true;
            Dispatcher.UIThread.Post(() => {
                ApplyDesiredRelayStates(states);
                SyncMonitorsToRelayStates();
                SaveRelayStatesToDevice();
            });
        };

        _comPortRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _comPortRefreshTimer.Tick += (_, _) => RefreshComPortDropdowns();

        _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _settingsSaveTimer.Tick += (_, _) => FlushPendingSettingsSave();

        Opened += OnOpened;
        Closing += OnClosing;
    }

    // --- Build one relay row: [name] [COM dropdown] [dot] [On/Off/Cycle] ---

    /**
     * @brief Builds one relay row: name field, COM dropdown, status dot, and On/Off/Cycle label.
     * @param index Zero-based relay index (0-7).
     * @param name Initial relay display name from settings.
     * @retval Control Border wrapping the relay row grid.
     */
    private Control BuildRelayRow(int index, string name) {
        int captured = index;
        _nameBox[index] = new TextBox {
            Margin = new Thickness(0, 0, 12, 0),
            Watermark = "Relay name",
            MaxLength = MaxRelayNameLength,
            Text = ResolveRelayName(index, name)
        };
        _nameBox[index].LostFocus += (_, _) => OnRelayNameLostFocus(captured);
        ToolTip.SetTip(_nameBox[index], "Relay label (max 20 characters). Clear the field and tab out to restore the default name.");
        _comBox[index] = new ComboBox {
            Margin = new Thickness(0, 0, 12, 0),
            Width = ComDropdownWidth,
            MinWidth = ComDropdownWidth,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        _comBox[index].SelectionChanged += (_, _) => OnComChanged(captured);
        _comBox[index].PointerWheelChanged += (_, e) => OnComWheelChanged(captured, e);
        ToolTip.SetTip(_comBox[index], "Optional COM port for UART monitor on this relay. Scroll the mouse wheel while hovering to change selection.");

        _comClearButton[index] = CreateEraserButton("Clear COM selection", (_, _) => OnClearComClick(captured), index);
        _comSettingsButton[index] = CreateIconButton(GearIconGeometry, "Serial port settings", (_, _) => OnComSettingsClick(captured), out _comSettingsIcon[index]);
        StackPanel comPanel = new() {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _comBox[index], _comClearButton[index], _comSettingsButton[index] }
        };

        _statusDot[index] = new Ellipse {
            Width = 14,
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _cycleRing[index] = new CycleRing {
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid dotGrid = new() {
            Width = 32,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        dotGrid.Children.Add(_statusDot[index]);
        dotGrid.Children.Add(_cycleRing[index]);

        _hitTarget[index] = new Border {
            Width = 32, Height = 32, Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = index,
            Child = dotGrid
        };
        _hitTarget[index].PointerPressed += OnDotPressed;
        _hitTarget[index].PointerReleased += OnDotReleased;
        _hitTarget[index].PointerMoved += OnDotMoved;
        _hitTarget[index].PointerEntered += OnDotEntered;
        ToolTip.SetTip(_hitTarget[index], BuildStatusDotTooltip(index));

        _statusText[index] = new TextBlock { MinWidth = 36, Opacity = 0.85, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };

        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        grid.Children.Add(_nameBox[index]);
        grid.Children.Add(comPanel);
        StackPanel right = new() {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children = { _hitTarget[index], _statusText[index] }
        };
        grid.Children.Add(right);
        Grid.SetColumn(_nameBox[index], 0);
        Grid.SetColumn(comPanel, 1);
        Grid.SetColumn(right, 2);

        RefreshComRowActions(index);
        return new Border {
            Padding = new Thickness(20, 10),
            BorderBrush = Application.Current?.FindResource("SystemControlForegroundBaseMediumLowBrush") as IBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid
        };
    }

    /**
     * @brief Updates status dot color, text label, cycle ring visibility, and tooltip for one relay.
     * @param index Zero-based relay index.
     */
    private void RefreshRowVisuals(int index) {
        _statusDot[index].Fill = _isOn[index] ? StatusDotOnBrush : StatusDotOffBrush;
        _statusText[index].Text = _isCycling[index] ? "Cycle" : _isOn[index] ? "On" : "Off";
        _cycleRing[index].IsVisible = _isCycling[index];
        ToolTip.SetTip(_hitTarget[index], BuildStatusDotTooltip(index));
    }

    /**
     * @brief Builds the tooltip text for a relay status dot based on current state.
     * @param index Zero-based relay index.
     * @retval string Tooltip describing click, drag, and double-click behavior.
     */
    private string BuildStatusDotTooltip(int index) {
        if (_isCycling[index]) {return "Power-cycling — OFF for 2 seconds, then ON";}
        if (_isOn[index]) {
            return "Click to turn off. Double-click to power-cycle (2 s). Drag across dots to toggle a range.";
        }
        return "Click to turn on. Drag across dots to toggle a range.";
    }

    /**
     * @brief Sets tooltips on global controls (All On, All Off, connection status).
     */
    private void ConfigureMainWindowTooltips() {
        ToolTip.SetTip(AllOnButton, "Turn all relays on.");
        ToolTip.SetTip(AllOffButton, "Turn all relays off and close any open COM monitor windows.");
        ToolTip.SetTip(ConnectionStatusText, "Connection state of the KmTronic 8-Port USB Relay board.");
    }

    // --- Window lifecycle ---

    /**
     * @brief Window opened handler: starts relay driver, refreshes COM ports, shows error if not connected.
     * @param sender Event source (unused).
     * @param e Opened event arguments.
     */
    private async void OnOpened(object? sender, EventArgs e) {
        _relay.Start();
        RefreshComPortDropdowns();
        _comPortRefreshTimer.Start();

        await Task.Delay(2000);
        if (!_relay.IsConnected) {
            await MessageBoxManager
                .GetMessageBoxStandard("Error", "KmTronic 8-Port USB Relay not found!", ButtonEnum.Ok, MsBox.Avalonia.Enums.Icon.Error)
                .ShowWindowDialogAsync(this);
        }
    }

    /**
     * @brief Replaces the About button content with a help icon.
     */
    private void ConfigureAboutButton() {
        AboutButton.Padding = new Thickness(4);
        AboutButton.Content = new Viewbox {
            Width = 16,
            Height = 16,
            Child = new Avalonia.Controls.Shapes.Path {
                Data = HelpIconGeometry,
                Fill = HelpIconBrush,
                Stretch = Stretch.Uniform
            }
        };
    }

    /**
     * @brief Shows the About dialog modally.
     * @param sender Event source (unused).
     * @param e Routed event arguments.
     */
    private async void OnAboutClick(object? sender, RoutedEventArgs e) {
        AboutWindow dialog = new();
        await dialog.ShowDialog(this);
    }

    /**
     * @brief Window closing handler: stops timers, saves settings, closes monitors, disposes relay driver.
     * @param sender Event source (unused).
     * @param e Closing event arguments.
     */
    private void OnClosing(object? sender, WindowClosingEventArgs e) {
        _comPortRefreshTimer.Stop();
        _settingsSaveTimer.Stop();
        for (int i = 0; i < RelayCount; i++) {
            _pendingClick[i]?.Cancel();
            _pendingClick[i]?.Dispose();
            _pendingClick[i] = null;
            StopPowerCycle(i);
        }

        CloseAllSerialMonitors();
        _relay.SaveAllUserSettings(
            _nameBox.Select((b, i) => ResolveRelayName(i, b.Text)).ToArray(),
            _selectedCom,
            _serialSettings);
        _relay.Stop();
        _relay.Dispose();
    }

    /**
     * @brief Schedules a debounced save of relay names and COM port assignments to settings.json.
     */
    private void ScheduleSettingsSave() {
        _settingsSavePending = true;
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    /**
     * @brief Writes pending relay names and COM port assignments to settings.json.
     */
    private void FlushPendingSettingsSave() {
        _settingsSaveTimer.Stop();
        if (!_settingsSavePending) {return;}
        _settingsSavePending = false;
        _relay.SaveAllUserSettings(
            _nameBox.Select((b, i) => ResolveRelayName(i, b.Text)).ToArray(),
            _selectedCom,
            _serialSettings);
    }

    // --- All On / All Off ---

    /**
     * @brief All On button click handler.
     * @param sender Event source (unused).
     * @param e Routed event arguments.
     */
    private void OnAllOnClick(object? sender, RoutedEventArgs e) => SetAll(true);

    /**
     * @brief All Off button click handler — confirms before turning all relays off.
     * @param sender Event source (unused).
     * @param e Routed event arguments.
     */
    private async void OnAllOffClick(object? sender, RoutedEventArgs e) {
        ButtonResult result = await MessageBoxManager
            .GetMessageBoxStandard(
                "All Off",
                "Turn all relays off and close any open COM monitor windows?",
                ButtonEnum.YesNo,
                MsBox.Avalonia.Enums.Icon.Info)
            .ShowWindowDialogAsync(this);
        if (result == ButtonResult.Yes) {SetAll(false);}
    }

    /**
     * @brief Sets all relays on or off, updates UI, opens/closes monitors, and persists states.
     * @param on True to energize all relays; false to de-energize all.
     */
    private void SetAll(bool on) {
        RelayCommandResult result = _relay.SetAllRelays(on);
        ApplyDesiredRelayStates(_relay.GetDesiredRelayStates());
        SyncMonitorsToRelayStates();

        if (result == RelayCommandResult.Success) {
            SaveRelayStatesToDevice();
            return;
        }

        ShowRelayCommandError(result == RelayCommandResult.PartialFailure
            ? "All-relay command partially failed — UI synced from hardware."
            : "Could not change all relays — check connection.");
    }

    /**
     * @brief Applies desired relay states from the driver to UI dots and labels.
     * @param states Eight relay on/off states (index 0 = relay 1).
     */
    private void ApplyDesiredRelayStates(IReadOnlyList<bool> states) {
        for (int i = 0; i < RelayCount && i < states.Count; i++) {
            _isOn[i] = states[i];
            RefreshRowVisuals(i);
        }
    }

    /**
     * @brief Opens or closes UART monitors to match current relay on/off states.
     */
    private void SyncMonitorsToRelayStates() {
        for (int i = 0; i < RelayCount; i++) {
            if (_isOn[i]) {TryOpenSerialMonitor(i);}
            else {CloseSerialMonitor(i);}
        }
    }

    /**
     * @brief Shows a relay command error without losing the last connection status message.
     * @param error User-visible error detail.
     */
    private void ShowRelayCommandError(string error) {
        ConnectionStatusText.Text = $"{_connectionStatusMessage} — {error}";
    }

    // --- COM port dropdowns ---

    /**
     * @brief COM dropdown selection changed handler for one relay channel.
     * @param index Zero-based relay index.
     */
    private void OnComChanged(int index) {
        if (_refreshingComPorts) {return;}
        if (_comBox[index].SelectedItem is string selected) {_selectedCom[index] = selected;}
        RefreshComRowActions(index);
        ScheduleSettingsSave();
        Dispatcher.UIThread.Post(RefreshComPortDropdowns, DispatcherPriority.Background);
    }

    /**
     * @brief Mouse wheel handler to cycle COM dropdown selection without opening the list.
     * @param index Zero-based relay index.
     * @param e Pointer wheel event arguments.
     */
    private void OnComWheelChanged(int index, PointerWheelEventArgs e) {
        if (_refreshingComPorts || !_comBox[index].IsEnabled) {return;}
        if (_comBox[index].ItemsSource is not System.Collections.IEnumerable items) {return;}

        List<object> options = items.Cast<object>().ToList();
        if (options.Count <= 1) {return;}

        int current = _comBox[index].SelectedItem is string selected
            ? options.FindIndex(o => string.Equals(o.ToString(), selected, StringComparison.OrdinalIgnoreCase))
            : 0;
        if (current < 0) {current = 0;}

        int next = e.Delta.Y > 0 ? current - 1 : current + 1;
        next = Math.Clamp(next, 0, options.Count - 1);
        if (next != current) {_comBox[index].SelectedItem = options[next];}
        e.Handled = true;
    }

    /**
     * @brief Clears the COM port assignment for one relay and closes its monitor.
     * @param index Zero-based relay index.
     */
    private void OnClearComClick(int index) {
        if (!IsChannelComAssigned(_selectedCom[index])) {return;}
        CloseSerialMonitor(index);
        _selectedCom[index] = KmTronicCommands.NotApplicable;
        _comBox[index].SelectedItem = KmTronicCommands.NotApplicable;
        RefreshComRowActions(index);
        ScheduleSettingsSave();
        RefreshComPortDropdowns();
    }

    /**
     * @brief Opens the serial port settings dialog for one relay channel.
     * @param index Zero-based relay index.
     */
    private async void OnComSettingsClick(int index) {
        if (!IsChannelComAssigned(_selectedCom[index])) {return;}
        ComSettingsWindow dialog = new(this, _serialSettings[index].Clone());
        await dialog.ShowDialog(this);
        if (!dialog.Saved || dialog.Result == null) {return;}
        _serialSettings[index] = dialog.Result;
        _relay.SaveChannelSerialSettings(_serialSettings);
        _serialMonitorWindows[index]?.ApplySettings(_serialSettings[index]);
    }

    /**
     * @brief Enables or disables COM clear/settings buttons based on whether a port is assigned.
     * @param index Zero-based relay index.
     */
    private void RefreshComRowActions(int index) {
        bool assigned = IsChannelComAssigned(_selectedCom[index]);
        _comClearButton[index].IsEnabled = assigned;
        _comSettingsButton[index].IsEnabled = assigned;
        _comClearEraserBody[index].Fill = assigned ? EraserBodyBrushActive : IconBrushDisabled;
        _comClearEraserFerrule[index].Fill = assigned ? EraserFerruleBrushActive : IconBrushDisabled;
        _comSettingsIcon[index].Fill = assigned ? GearIconBrushActive : IconBrushDisabled;
    }

    /**
     * @brief Restores the default relay name when the name field is cleared on lost focus.
     * @param index Zero-based relay index.
     */
    private void OnRelayNameLostFocus(int index) {
        if (NormalizeRelayName(_nameBox[index].Text).Length == 0) {
            _nameBox[index].Text = GetDefaultRelayName(index);
        }
        ScheduleSettingsSave();
    }

    /**
     * @brief Trims and truncates a relay name to the maximum allowed length.
     * @param name Raw name text from the UI.
     * @retval string Normalized name (may be empty).
     */
    private static string NormalizeRelayName(string? name) {
        string text = (name ?? string.Empty).Trim();
        return text.Length > MaxRelayNameLength ? text[..MaxRelayNameLength] : text;
    }

    /**
     * @brief Returns the factory default name for a relay (e.g. "Relay 01").
     * @param index Zero-based relay index.
     * @retval string Default display name.
     */
    private static string GetDefaultRelayName(int index) => $"Relay {index + 1:D2}";

    /**
     * @brief Resolves a relay name to a non-empty display string (default if blank).
     * @param index Zero-based relay index.
     * @param name Raw or saved name text.
     * @retval string Display name for the relay.
     */
    private static string ResolveRelayName(int index, string? name) {
        string text = NormalizeRelayName(name);
        return text.Length > 0 ? text : GetDefaultRelayName(index);
    }

    /**
     * @brief Creates an eraser-icon button for clearing COM port selection.
     * @param tooltip Tooltip text for the button.
     * @param onClick Click event handler.
     * @param index Zero-based relay index (stores path references for theming).
     * @retval Button Configured eraser button.
     */
    private Button CreateEraserButton(string tooltip, EventHandler<RoutedEventArgs> onClick, int index) {
        _comClearEraserBody[index] = new Avalonia.Controls.Shapes.Path {
            Data = EraserBodyGeometry,
            Fill = IconBrushDisabled,
            Stretch = Stretch.Uniform
        };
        _comClearEraserFerrule[index] = new Avalonia.Controls.Shapes.Path {
            Data = EraserFerruleGeometry,
            Fill = IconBrushDisabled,
            Stretch = Stretch.Uniform
        };
        Button button = new() {
            Padding = new Thickness(4),
            MinWidth = 28,
            MinHeight = 28,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new Viewbox {
                Width = 14,
                Height = 14,
                Child = new Panel { Children = { _comClearEraserBody[index], _comClearEraserFerrule[index] } }
            }
        };
        ToolTip.SetTip(button, tooltip);
        button.Click += onClick;
        return button;
    }

    /**
     * @brief Creates a generic icon button with tooltip and click handler.
     * @param icon Path geometry for the icon.
     * @param tooltip Tooltip text for the button.
     * @param onClick Click event handler.
     * @param iconPath Output path element for dynamic fill updates.
     * @retval Button Configured icon button.
     */
    private static Button CreateIconButton(Geometry icon, string tooltip, EventHandler<RoutedEventArgs> onClick, out Avalonia.Controls.Shapes.Path iconPath) {
        iconPath = new Avalonia.Controls.Shapes.Path { Data = icon, Fill = IconBrushDisabled, Stretch = Stretch.Uniform };
        Button button = new() {
            Padding = new Thickness(4),
            MinWidth = 28,
            MinHeight = 28,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new Viewbox {
                Width = 14,
                Height = 14,
                Child = iconPath
            }
        };
        ToolTip.SetTip(button, tooltip);
        button.Click += onClick;
        return button;
    }

    /**
     * @brief Returns the current display name for a relay from its name text box.
     * @param index Zero-based relay index.
     * @retval string Resolved relay display name.
     */
    private string GetRelayDisplayName(int index) => ResolveRelayName(index, _nameBox[index].Text);

    /**
     * @brief Refreshes all COM dropdown item lists, excluding board port and ports taken by other relays.
     */
    private void RefreshComPortDropdowns() {
        _refreshingComPorts = true;
        try {
            IReadOnlyList<string> allPorts = KmTronicCommands.GetSystemComPorts();
            string? boardPort = _relay.GetRelayBoardComPort() ?? _relay.LoadSavedRelayBoardComPort();

            for (int i = 0; i < RelayCount; i++) {
                List<string> options = new() { KmTronicCommands.NotApplicable };
                foreach (string port in allPorts) {
                    if (!string.IsNullOrWhiteSpace(boardPort) &&
                        string.Equals(port, boardPort, StringComparison.OrdinalIgnoreCase)) {continue;}
                    if (IsComTakenByOtherRelay(port, i)) {continue;}

                    options.Add(port);
                }

                bool enabled = options.Count > 1;
                _comBox[i].ItemsSource = options;
                _comBox[i].IsEnabled = enabled;
                if (!enabled) {_selectedCom[i] = KmTronicCommands.NotApplicable;}
                else if (!options.Any(o => string.Equals(o, _selectedCom[i], StringComparison.OrdinalIgnoreCase))) {
                    _selectedCom[i] = KmTronicCommands.NotApplicable;
                }
                _comBox[i].SelectedItem = _selectedCom[i];
                RefreshComRowActions(i);
            }
        } finally {
            _refreshingComPorts = false;
        }
    }

    /**
     * @brief Checks whether a COM port is already assigned to another relay channel.
     * @param port COM port name to test.
     * @param exceptIndex Relay index to exclude from the search.
     * @retval bool True if another relay already uses this port.
     */
    private bool IsComTakenByOtherRelay(string port, int exceptIndex) {
        for (int i = 0; i < RelayCount; i++) {
            if (i != exceptIndex && string.Equals(_selectedCom[i], port, StringComparison.OrdinalIgnoreCase)) {return true;}
        }

        return false;
    }

    // --- Status dot: single click = toggle, double click on ON = power cycle, drag = multi-toggle ---

    /**
     * @brief Pointer pressed on status dot: begins click or drag tracking.
     * @param sender Status dot hit-target border.
     * @param e Pointer pressed event arguments.
     */
    private void OnDotPressed(object? sender, PointerPressedEventArgs e) {
        if (sender is not Border border || border.Tag is not int index) {return;}
        if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed || _isCycling[index]) {return;}

        _pointerDown = true;
        _dragging = false;
        _dragStart = index;
        _dragCurrent = index;
        e.Pointer.Capture(border);
        e.Handled = true;
    }

    /**
     * @brief Pointer entered another dot while dragging: extends the drag range.
     * @param sender Status dot hit-target border.
     * @param e Pointer event arguments.
     */
    private void OnDotEntered(object? sender, PointerEventArgs e) {
        if (sender is Border border && e.Pointer.Captured == border && border.Tag is int index) {EnterDrag(index);}
    }

    /**
     * @brief Pointer moved while captured: updates drag range to dot under cursor.
     * @param sender Status dot hit-target border.
     * @param e Pointer event arguments.
     */
    private void OnDotMoved(object? sender, PointerEventArgs e) {
        if (sender is not Border border || e.Pointer.Captured != border) {return;}
        int? index = RelayIndexUnderPointer(e);
        if (index.HasValue) {EnterDrag(index.Value);}
    }

    /**
     * @brief Extends an in-progress drag to include the given relay index.
     * @param index Zero-based relay index under the pointer.
     */
    private void EnterDrag(int index) {
        if (!_pointerDown || !_dragStart.HasValue || index == _dragCurrent) {return;}

        _dragging = true;
        _dragCurrent = index;
    }

    /**
     * @brief Pointer released on status dot: completes drag toggle, double-click cycle, or single click.
     * @param sender Status dot hit-target border.
     * @param e Pointer released event arguments.
     */
    private void OnDotReleased(object? sender, PointerReleasedEventArgs e) {
        if (sender is not Border border || e.Pointer.Captured != border || border.Tag is not int _) {return;}
        e.Pointer.Capture(null);
        if (!_pointerDown || !_dragStart.HasValue) {ResetDrag(); return;}

        if (_dragging && _dragCurrent.HasValue) {
            int min = Math.Min(_dragStart.Value, _dragCurrent.Value);
            int max = Math.Max(_dragStart.Value, _dragCurrent.Value);
            for (int i = min; i <= max; i++) {
                if (!_isCycling[i]) {ToggleRelay(i);}
                _pendingClick[i]?.Cancel();
            }

            SaveRelayStatesToDevice();
            ResetDrag();
            return;
        }

        int clickIndex = _dragStart.Value;
        ResetDrag();

        if (_isCycling[clickIndex]) {return;}

        DateTime now = DateTime.UtcNow;
        if (_lastClickIndex == clickIndex && _isOn[clickIndex] &&
            (now - _lastClickTime).TotalMilliseconds <= DoubleClickWindowMs) {
            _pendingClick[clickIndex]?.Cancel();
            _lastClickIndex = -1;
            StartPowerCycle(clickIndex);
            return;
        }

        _lastClickIndex = clickIndex;
        _lastClickTime = now;
        ScheduleSingleClick(clickIndex);
    }

    /**
     * @brief Delays single-click toggle to distinguish from double-click power-cycle.
     * @param index Zero-based relay index to toggle if not cancelled.
     */
    private void ScheduleSingleClick(int index) {
        _pendingClick[index]?.Cancel();
        _pendingClick[index] = new CancellationTokenSource();
        CancellationToken token = _pendingClick[index]!.Token;

        _ = Task.Run(async () => {
            try {
                await Task.Delay(SingleClickDelayMs, token);
                await Dispatcher.UIThread.InvokeAsync(() => {
                    if (token.IsCancellationRequested || _isCycling[index]) {return;}
                    ToggleRelay(index);
                    SaveRelayStatesToDevice();
                });
            } catch (OperationCanceledException) {}
        }, token);
    }

    /**
     * @brief Toggles one relay on or off and opens/closes its UART monitor as needed.
     * @param index Zero-based relay index.
     */
    private void ToggleRelay(int index) {
        bool next = !_isOn[index];
        if (_relay.SetRelay(index + 1, next)) {
            _isOn[index] = next;
            RefreshRowVisuals(index);
            if (next) {TryOpenSerialMonitor(index);}
            else {CloseSerialMonitor(index);}
            return;
        }

        ApplyDesiredRelayStates(_relay.GetDesiredRelayStates());
        SyncMonitorsToRelayStates();
        ShowRelayCommandError($"Could not turn relay {index + 1:D2} {(next ? "on" : "off")}.");
    }

    /**
     * @brief Clears click/drag tracking state after a dot interaction completes.
     */
    private void ResetDrag() {
        _pointerDown = false;
        _dragging = false;
        _dragStart = null;
        _dragCurrent = null;
    }

    /**
     * @brief Hit-tests the visual tree to find the relay index under the pointer.
     * @param e Pointer event arguments with position.
     * @retval int? Relay index from border Tag, or null if not over a dot.
     */
    private static int? RelayIndexUnderPointer(PointerEventArgs e) {
        TopLevel? top = TopLevel.GetTopLevel(e.Source as Visual);
        if (top == null) {return null;}
        for (Visual? v = top.InputHitTest(e.GetPosition(top)) as Visual; v != null; v = v.GetVisualParent()) {
            if (v is Border { Tag: int index }) {return index;}
        }
        return null;
    }

    /**
     * @brief Persists current relay on/off states to KmTronicCommands for reconnect restore.
     */
    private void SaveRelayStatesToDevice() {
        _relay.RememberDesiredRelayStates(_isOn);
    }

    // --- Per-relay UART monitor (opens on relay ON; stays open through power-cycle) ---

    /**
     * @brief Opens a UART monitor window for a relay if a COM port is assigned and relay is on.
     * @param index Zero-based relay index.
     */
    private void TryOpenSerialMonitor(int index) {
        if (!IsChannelComAssigned(_selectedCom[index])) {return;}
        if (IsSerialMonitorOpen(index)) {return;}

        int captured = index;
        SerialMonitorWindow window = new(
            _selectedCom[index],
            GetRelayDisplayName(index),
            _serialSettings[index]);
        if (!window.PortOpened) {
            window.Close();
            return;
        }

        window.MonitorClosed += (_, _) => _serialMonitorWindows[captured] = null;
        _serialMonitorWindows[index] = window;
        window.Show();
    }

    /**
     * @brief Closes the UART monitor window for one relay channel.
     * @param index Zero-based relay index.
     */
    private void CloseSerialMonitor(int index) {
        if (_serialMonitorWindows[index] != null) {
            _serialMonitorWindows[index]!.Close();
            _serialMonitorWindows[index] = null;
        }
    }

    /**
     * @brief Closes UART monitor windows for all relay channels.
     */
    private void CloseAllSerialMonitors() {
        for (int i = 0; i < RelayCount; i++) {CloseSerialMonitor(i);}
    }

    /**
     * @brief Returns whether a COM port string represents an assigned monitor port (not N/A).
     * @param comPort COM port name or "N/A".
     * @retval bool True if a real COM port is assigned.
     */
    private static bool IsChannelComAssigned(string comPort) {
        return !string.IsNullOrWhiteSpace(comPort) &&
               !string.Equals(comPort, KmTronicCommands.NotApplicable, StringComparison.OrdinalIgnoreCase);
    }

    /**
     * @brief Returns whether a UART monitor window is currently open for a relay.
     * @param index Zero-based relay index.
     * @retval bool True if a monitor window exists for this channel.
     */
    private bool IsSerialMonitorOpen(int index) => _serialMonitorWindows[index] != null;

    // --- Power cycle: OFF → wait 2s → ON (double-click an ON relay) ---

    /**
     * @brief Starts a 2-second power cycle: relay off, then back on with animated ring.
     * @param index Zero-based relay index.
     */
    private void StartPowerCycle(int index) {
        if (_isCycling[index] || !_isOn[index]) {return;}
        StopPowerCycle(index);
        _isCycling[index] = true;
        _cycleRing[index].Progress = 1.0;
        if (!_relay.SetRelay(index + 1, false)) {StopPowerCycle(index); return;}

        _isOn[index] = false;
        RefreshRowVisuals(index);

        DateTime start = DateTime.UtcNow;
        DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) => {
            double elapsed = (DateTime.UtcNow - start).TotalMilliseconds;
            if (elapsed >= 2000) {
                timer.Stop();
                if (_relay.SetRelay(index + 1, true)) {
                    _isOn[index] = true;
                } else {
                    ApplyDesiredRelayStates(_relay.GetDesiredRelayStates());
                    ShowRelayCommandError($"Power-cycle failed to restore relay {index + 1:D2} to ON.");
                }
                StopPowerCycle(index);
                SaveRelayStatesToDevice();
                return;
            }
            _cycleRing[index].Progress = 1.0 - elapsed / 2000.0;
            RefreshRowVisuals(index);
        };
        timer.Start();
        _cycleTimer[index] = timer;
    }

    /**
     * @brief Stops an in-progress power cycle and clears the cycle ring for one relay.
     * @param index Zero-based relay index.
     */
    private void StopPowerCycle(int index) {
        _cycleTimer[index]?.Stop();
        _cycleTimer[index] = null;
        _isCycling[index] = false;
        _cycleRing[index].Progress = 0;
        RefreshRowVisuals(index);
    }

    // --- About dialog ---

    /**
     * @brief Modal About dialog showing application name, version, and credits.
     */
    private sealed class AboutWindow : Window {
        /**
         * @brief Builds the About dialog layout and wires the OK button.
         */
        public AboutWindow() {
            Title = "About KmTronicRelay";
            Width = 440;
            MinWidth = 360;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            CanResize = false;

            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            string versionText = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";

            StackPanel panel = new() { Margin = new Thickness(20), Spacing = 6 };
            panel.Children.Add(new TextBlock {
                Text = "KmTronicRelay",
                FontSize = 18,
                FontWeight = FontWeight.SemiBold
            });
            panel.Children.Add(new TextBlock {
                Text = $"Version {versionText}",
                FontSize = 13,
                Opacity = 0.85
            });
            panel.Children.Add(new TextBlock { Height = 4 });
            panel.Children.Add(new TextBlock {
                Text = "Desktop GUI for KMTronic 8-port USB relay.",
                FontSize = 12,
                Opacity = 0.78,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock {
                Text = "Built with .NET 8 and Avalonia UI.",
                FontSize = 12,
                Opacity = 0.78
            });

            Button okButton = new() { MinWidth = 88, Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, Classes = { "accent" } };
            okButton.Click += (_, _) => Close();
            panel.Children.Add(new Border { Height = 8 });
            panel.Children.Add(okButton);
            Content = panel;
        }
    }

    // --- COM serial settings dialog ---

    /**
     * @brief Modal dialog for editing per-channel UART serial port parameters.
     */
    private sealed class ComSettingsWindow : Window {
        public bool Saved { get; private set; }
        public SerialPortSettings? Result { get; private set; }

        private readonly Window _owner;
        private readonly TextBox _baudBox;
        private readonly ComboBox _dataBitsBox;
        private readonly ComboBox _stopBitsBox;
        private readonly ComboBox _parityBox;
        private readonly ComboBox _flowBox;
        private readonly CheckBox _timestampsCheck;
        private readonly CheckBox _logCheck;
        private readonly TextBox _logPathBox;
        private readonly Button _browseLogButton;
        private readonly TextBlock _errorText;

        /**
         * @brief Builds the serial settings form and loads initial values.
         * @param owner Parent window used for the log file picker dialog.
         * @param initial Current serial port settings to display.
         */
        public ComSettingsWindow(Window owner, SerialPortSettings initial) {
            _owner = owner;
            Title = "Serial Port Settings";
            Width = 440;
            MinWidth = 380;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            CanResize = false;

            _baudBox = new TextBox { Watermark = "Baud rate", Text = initial.BaudRate.ToString() };
            _dataBitsBox = CreateOptionsCombo(new[] { "5", "6", "7", "8" }, initial.DataBits.ToString());
            _stopBitsBox = CreateOptionsCombo(
                new[] { ("1", "One"), ("1.5", "OnePointFive"), ("2", "Two") },
                initial.StopBits);
            _parityBox = CreateOptionsCombo(
                new[] { "None", "Odd", "Even", "Mark", "Space" },
                initial.Parity);
            _flowBox = CreateOptionsCombo(
                new[] {
                    ("None", "None"),
                    ("XON/XOFF", "XOnXOff"),
                    ("RTS/CTS", "RequestToSend"),
                    ("RTS/CTS + XON/XOFF", "RequestToSendXOnXOff")
                },
                initial.Handshake);

            Grid fields = new() {
                ColumnDefinitions = new ColumnDefinitions("120,*"),
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
                Margin = new Thickness(16, 12, 16, 0)
            };
            AddField(fields, 0, "Baud rate", _baudBox);
            AddField(fields, 1, "Data bits", _dataBitsBox);
            AddField(fields, 2, "Stop bits", _stopBitsBox);
            AddField(fields, 3, "Parity", _parityBox);
            AddField(fields, 4, "Flow control", _flowBox);

            _timestampsCheck = new CheckBox {
                Content = "Show timestamps on received UART lines",
                Margin = new Thickness(16, 8, 16, 0)
            };
            _logCheck = new CheckBox {
                Content = "Log received UART data to file",
                Margin = new Thickness(16, 4, 16, 0)
            };
            _logPathBox = new TextBox {
                Margin = new Thickness(0),
                Watermark = "Log file path (.log)",
                IsReadOnly = true
            };
            _browseLogButton = new Button { MinWidth = 88, Content = "Browse…" };
            _browseLogButton.Click += OnBrowseLogClick;
            Grid logRow = new() {
                Margin = new Thickness(16, 4, 16, 0),
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 8
            };
            logRow.Children.Add(_logPathBox);
            logRow.Children.Add(_browseLogButton);
            Grid.SetColumn(_logPathBox, 0);
            Grid.SetColumn(_browseLogButton, 1);

            _logCheck.IsCheckedChanged += (_, _) => RefreshLogControlsEnabled();
            _errorText = new TextBlock {
                Margin = new Thickness(16, 8, 16, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(196, 43, 28)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                IsVisible = false
            };
            _baudBox.TextChanged += (_, _) => { _errorText.IsVisible = false; };

            Button restoreButton = new() { MinWidth = 120, Content = "Restore Defaults" };
            restoreButton.Click += (_, _) => LoadIntoUi(SerialPortSettings.CreateDefaults());
            Button cancelButton = new() { MinWidth = 88, Content = "Cancel" };
            cancelButton.Click += (_, _) => Close();
            Button saveButton = new() { MinWidth = 88, Content = "Save", Classes = { "accent" } };
            saveButton.Click += OnSaveClick;
            StackPanel buttons = new() {
                Margin = new Thickness(16),
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Children = { restoreButton, cancelButton, saveButton }
            };

            Content = new StackPanel {
                Children = {
                    fields,
                    _timestampsCheck,
                    _logCheck,
                    logRow,
                    _errorText,
                    buttons
                }
            };
            LoadIntoUi(initial);
        }

        /**
         * @brief Enables or disables log path controls based on the log checkbox.
         */
        private void RefreshLogControlsEnabled() {
            bool enabled = _logCheck.IsChecked == true;
            _logPathBox.IsEnabled = enabled;
            _browseLogButton.IsEnabled = enabled;
        }

        /**
         * @brief Opens a save-file picker for the UART log destination.
         * @param sender Event source (unused).
         * @param e Routed event arguments.
         */
        private async void OnBrowseLogClick(object? sender, RoutedEventArgs e) {
            IStorageFile? file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                Title = "Choose UART log file",
                SuggestedFileName = $"uart-{DateTime.Now:yyyyMMdd-HHmmss}.log",
                DefaultExtension = "log",
                FileTypeChoices = new[] {
                    new FilePickerFileType("Log files") { Patterns = new[] { "*.log" }, MimeTypes = new[] { "text/plain" } }
                }
            });
            if (file?.TryGetLocalPath() is string path) {
                _logPathBox.Text = path;
                _logCheck.IsChecked = true;
            }
        }

        /**
         * @brief Populates form controls from a SerialPortSettings instance.
         * @param settings Settings to display in the UI.
         */
        private void LoadIntoUi(SerialPortSettings settings) {
            _baudBox.Text = settings.BaudRate.ToString();
            _dataBitsBox.SelectedItem = settings.DataBits.ToString();
            SelectByTag(_stopBitsBox, settings.StopBits);
            SelectByTag(_parityBox, settings.Parity);
            SelectByTag(_flowBox, settings.Handshake);
            _timestampsCheck.IsChecked = settings.ShowTimestamps;
            _logCheck.IsChecked = settings.LogToFile;
            _logPathBox.Text = settings.LogFilePath;
            RefreshLogControlsEnabled();
        }

        /**
         * @brief Validates input and saves settings to Result before closing.
         * @param sender Event source (unused).
         * @param e Routed event arguments.
         */
        private void OnSaveClick(object? sender, RoutedEventArgs e) {
            if (!int.TryParse(_baudBox.Text?.Trim(), out int baud) || baud < 300 || baud > 921600) {
                _errorText.Text = "Baud rate must be an integer from 300 to 921600.";
                _errorText.IsVisible = true;
                return;
            }

            bool logToFile = _logCheck.IsChecked == true;
            string logPath = (_logPathBox.Text ?? string.Empty).Trim();
            if (logToFile && logPath.Length == 0) {
                _errorText.Text = "Choose a log file path or disable file logging.";
                _errorText.IsVisible = true;
                return;
            }

            _errorText.IsVisible = false;
            Result = new SerialPortSettings {
                BaudRate = baud,
                DataBits = int.Parse(_dataBitsBox.SelectedItem?.ToString() ?? "8"),
                StopBits = (_stopBitsBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "One",
                Parity = (_parityBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "None",
                Handshake = (_flowBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "None",
                ShowTimestamps = _timestampsCheck.IsChecked == true,
                LogToFile = logToFile,
                LogFilePath = logPath
            };
            Saved = true;
            Close();
        }

        /**
         * @brief Creates a ComboBox from string labels with a selected value.
         * @param labels Display labels for each option.
         * @param selected Label to pre-select.
         * @retval ComboBox Populated combo box.
         */
        private static ComboBox CreateOptionsCombo(IEnumerable<string> labels, string selected) {
            ComboBox box = new();
            foreach (string label in labels) {box.Items.Add(label);}
            box.SelectedItem = labels.Contains(selected) ? selected : labels.First();
            return box;
        }

        /**
         * @brief Creates a ComboBox from labeled options with Tag values.
         * @param options Label/value pairs for each option.
         * @param selected Tag value to pre-select.
         * @retval ComboBox Populated combo box with ComboBoxItem entries.
         */
        private static ComboBox CreateOptionsCombo(IEnumerable<(string label, string value)> options, string selected) {
            ComboBox box = new();
            foreach ((string label, string value) in options) {
                box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            }
            SelectByTag(box, selected);
            return box;
        }

        /**
         * @brief Selects a ComboBox item by matching its Tag string.
         * @param box Target combo box.
         * @param tag Tag value to select (case-insensitive).
         */
        private static void SelectByTag(ComboBox box, string tag) {
            foreach (object? item in box.Items) {
                if (item is ComboBoxItem { Tag: string value } && string.Equals(value, tag, StringComparison.OrdinalIgnoreCase)) {
                    box.SelectedItem = item;
                    return;
                }
            }
            if (box.Items.Count > 0) {box.SelectedIndex = 0;}
        }

        /**
         * @brief Adds a labeled field row to the settings form grid.
         * @param grid Target two-column grid.
         * @param row Row index in the grid.
         * @param label Field label text.
         * @param input Input control for the field.
         */
        private static void AddField(Grid grid, int row, string label, Control input) {
            TextBlock text = new() { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 8) };
            input.Margin = new Thickness(0, 0, 0, 8);
            grid.Children.Add(text);
            grid.Children.Add(input);
            Grid.SetRow(text, row);
            Grid.SetRow(input, row);
            Grid.SetColumn(text, 0);
            Grid.SetColumn(input, 1);
        }
    }

    // --- Built-in UART monitor window (Windows) and VT100 decode buffer ---

    /**
     * @brief Incremental VT100/ANSI terminal emulator buffer for serial monitor display.
     */
    private sealed class Vt100TerminalBuffer {
        private const int MaxLines = 4000;
        private const int MaxCols = 256;

        private readonly List<string> _lines = new() { string.Empty };
        private readonly StringBuilder _currentLine = new();
        private int _cursorCol;
        private int _escState;
        private readonly StringBuilder _csiParams = new();

        public bool ShowTimestamps { get; set; }

        public event Action? Changed;

        /**
         * @brief Clears all buffered lines and resets cursor and escape state.
         */
        public void Clear() {
            lock (_lines) {
                _lines.Clear();
                _lines.Add(string.Empty);
                _currentLine.Clear();
                _cursorCol = 0;
                _escState = 0;
                _csiParams.Clear();
            }
            Changed?.Invoke();
        }

        /**
         * @brief Returns the full terminal text for display (completed lines plus current line).
         * @retval string Newline-joined terminal content.
         */
        public string GetDisplayText() {
            lock (_lines) {
                if (_currentLine.Length > 0) {
                    var all = new List<string>(_lines) { _currentLine.ToString() };
                    return string.Join('\n', all);
                }
                return string.Join('\n', _lines);
            }
        }

        /**
         * @brief Appends raw bytes to the terminal buffer and raises Changed.
         * @param data Received serial data bytes.
         */
        public void Append(ReadOnlySpan<byte> data) {
            lock (_lines) {
                for (int i = 0; i < data.Length; i++) {ProcessByte(data[i]);}
            }
            Changed?.Invoke();
        }

        /**
         * @brief Processes one incoming byte through the VT100 state machine.
         * @param b Raw byte from the serial stream.
         */
        private void ProcessByte(byte b) {
            char c = (char)b;
            switch (_escState) {
            case 0:
                if (b == 0x1B) {_escState = 1; return;}
                if (b == '\r') {_cursorCol = 0; return;}
                if (b == '\n') {EndLine(); return;}
                if (b == '\b') {if (_cursorCol > 0) {_cursorCol--; TrimCurrentLineToCursor();} return;}
                if (b == '\t') {_cursorCol = (_cursorCol + 8) & ~7; EnsureCurrentLineWidth(); return;}
                if (b == 0x07 || b == 0x00) {return;}
                if (b < 0x20 && b != 0x0B && b != 0x0C) {return;}
                WriteChar(c);
                return;
            case 1:
                if (b == '[') {_escState = 2; _csiParams.Clear(); return;}
                _escState = 0;
                return;
            case 2:
                if (b >= 0x40 && b <= 0x7E) {ApplyCsi(_csiParams.ToString(), (char)b); _escState = 0; return;}
                if (b == ';' || (b >= '0' && b <= '9')) {_csiParams.Append((char)b); return;}
                _escState = 0;
                return;
            }
        }

        /**
         * @brief Writes a printable character at the current cursor column.
         * @param c Character to write.
         */
        private void WriteChar(char c) {
            EnsureCurrentLineWidth();
            if (_cursorCol < _currentLine.Length) {_currentLine[_cursorCol] = c;}
            else {_currentLine.Append(c);}
            _cursorCol++;
        }

        /**
         * @brief Pads the current line with spaces up to the cursor column.
         */
        private void EnsureCurrentLineWidth() {
            while (_currentLine.Length < _cursorCol) {_currentLine.Append(' ');}
            if (_cursorCol >= MaxCols) {_cursorCol = MaxCols - 1;}
        }

        /**
         * @brief Truncates the current line to the cursor position (backspace effect).
         */
        private void TrimCurrentLineToCursor() {
            if (_cursorCol < _currentLine.Length) {_currentLine.Length = _cursorCol;}
        }

        /**
         * @brief Commits the current line to history and starts a new blank line.
         */
        private void EndLine() {
            string line = _currentLine.ToString();
            if (ShowTimestamps) {line = SerialPortSettings.FormatLogTimestamp(DateTime.Now) + line;}
            _lines.Add(line);
            _currentLine.Clear();
            _cursorCol = 0;
            TrimLineCount();
        }

        /**
         * @brief Removes oldest lines when the buffer exceeds MaxLines.
         */
        private void TrimLineCount() {
            while (_lines.Count > MaxLines) {_lines.RemoveAt(0);}
        }

        /**
         * @brief Applies a CSI escape sequence command (cursor move, erase, etc.).
         * @param paramText Semicolon-separated numeric parameters.
         * @param command Final CSI command character.
         */
        private void ApplyCsi(string paramText, char command) {
            int[] args = ParseCsiParams(paramText);
            switch (command) {
            case 'H':
            case 'f':
                int row = args.Length > 0 && args[0] > 0 ? args[0] - 1 : 0;
                int col = args.Length > 1 && args[1] > 0 ? args[1] - 1 : 0;
                FlushCurrentLineToHistory();
                while (_lines.Count <= row) {_lines.Add(string.Empty);}
                _currentLine.Clear();
                _currentLine.Append(_lines[row]);
                _lines[row] = string.Empty;
                _cursorCol = Math.Min(col, _currentLine.Length);
                EnsureCurrentLineWidth();
                return;
            case 'J':
                if (args.Length == 0 || args[0] == 0) {
                    TrimCurrentLineToCursor();
                    _currentLine.Length = _cursorCol;
                } else if (args[0] == 2) {
                    _lines.Clear();
                    _lines.Add(string.Empty);
                    _currentLine.Clear();
                    _cursorCol = 0;
                }
                return;
            case 'K':
                if (args.Length == 0 || args[0] == 0) {TrimCurrentLineToCursor();}
                else if (args[0] == 2) {_currentLine.Clear(); _cursorCol = 0;}
                return;
            case 'm':
                return;
            }
        }

        /**
         * @brief Flushes the in-progress current line into the line history.
         */
        private void FlushCurrentLineToHistory() {
            if (_currentLine.Length > 0 || _cursorCol > 0) {EndLine();}
        }

        /**
         * @brief Parses semicolon-separated CSI numeric parameters.
         * @param paramText Raw parameter string (may be empty).
         * @retval int[] Parsed integer arguments (zero for unparsable segments).
         */
        private static int[] ParseCsiParams(string paramText) {
            if (string.IsNullOrEmpty(paramText)) {return Array.Empty<int>();}
            string[] parts = paramText.Split(';', StringSplitOptions.RemoveEmptyEntries);
            int[] values = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++) {
                if (!int.TryParse(parts[i], out values[i])) {values[i] = 0;}
            }
            return values;
        }
    }

    /**
     * @brief Per-relay UART monitor window with VT100/hex display and send line.
     */
    private sealed class SerialMonitorWindow : Window {
        private const int MaxRawBytes = 65536;

        private readonly object _portSync = new();
        private readonly object _logSync = new();
        private readonly string _portName;
        private readonly string _relayName;
        private SerialPortSettings _settings;
        private readonly SerialPort _port;
        private readonly Vt100TerminalBuffer _terminal = new();
        private readonly List<byte> _rawRxBytes = new();
        private readonly CancellationTokenSource _readCancel = new();
        private readonly ScrollViewer _scroll;
        private readonly TextBlock _terminalText;
        private readonly TextBlock _headerText;
        private readonly TextBox _sendBox;
        private StreamWriter? _logWriter;
        private bool _closed;
        private bool _hexMode;

        public bool PortOpened { get; private set; }
        public event EventHandler? MonitorClosed;

        /**
         * @brief Opens a serial port, builds the monitor UI, and starts the background read loop.
         * @param portName COM port name to open.
         * @param relayName Relay display name for the window title and header.
         * @param settings Serial port parameters to apply.
         */
        public SerialMonitorWindow(string portName, string relayName, SerialPortSettings settings) {
            _portName = portName;
            _relayName = relayName;
            _settings = settings.Clone();
            _terminal.ShowTimestamps = _settings.ShowTimestamps;

            Width = 720;
            Height = 520;
            MinWidth = 420;
            MinHeight = 280;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Title = $"{portName} — {relayName}";

            _headerText = new TextBlock {
                Margin = new Thickness(12, 10, 12, 6),
                Opacity = 0.85,
                FontSize = 12
            };
            UpdateHeaderText();
            _terminalText = new TextBlock {
                FontFamily = new FontFamily("Consolas,Courier New,monospace"),
                FontSize = 13,
                TextWrapping = TextWrapping.NoWrap
            };
            _scroll = new ScrollViewer {
                Margin = new Thickness(12, 0),
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = _terminalText
            };
            _sendBox = new TextBox {
                Margin = new Thickness(0),
                Watermark = "Type here — Enter or Send to transmit",
                FontFamily = new FontFamily("Consolas,Courier New,monospace"),
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _sendBox.KeyDown += OnSendBoxKeyDown;
            Button sendButton = new() { MinWidth = 72, Content = "Send" };
            sendButton.Click += (_, _) => {SendTypedLine(); _sendBox.Focus();};
            Grid sendRow = new() {
                Margin = new Thickness(12, 0, 12, 0),
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 8
            };
            sendRow.Children.Add(_sendBox);
            sendRow.Children.Add(sendButton);
            Grid.SetColumn(_sendBox, 0);
            Grid.SetColumn(sendButton, 1);
            CheckBox hexToggle = new() { Content = "Hex", VerticalAlignment = VerticalAlignment.Center };
            hexToggle.IsCheckedChanged += (_, _) => {
                _hexMode = hexToggle.IsChecked == true;
                UpdateHeaderText();
                RefreshDisplay();
            };
            Button clearButton = new() { MinWidth = 88, Content = "Clear" };
            clearButton.Click += (_, _) => ClearDisplay();
            StackPanel buttons = new() {
                Margin = new Thickness(12),
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Children = { hexToggle, clearButton }
            };
            Content = new Grid {
                RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
                Children = { _headerText, _scroll, sendRow, buttons }
            };
            Grid.SetRow(_headerText, 0);
            Grid.SetRow(_scroll, 1);
            Grid.SetRow(sendRow, 2);
            Grid.SetRow(buttons, 3);

            _port = new SerialPort(portName);
            _settings.ApplyTo(_port);
            _port.ReadTimeout = 250;
            _port.WriteTimeout = 1000;
            _port.DtrEnable = true;
            _port.RtsEnable = true;

            _terminal.Changed += OnTerminalChanged;

            try {
                lock (_portSync) {
                    _port.Open();
                    PortOpened = true;
                }
                _ = Task.Run(() => ReadLoop(_readCancel.Token));
                OpenLogFileIfConfigured();
                Opened += (_, _) => _sendBox.Focus();
            } catch (Exception ex) {
                _terminalText.Text = $"Failed to open {portName}: {ex.Message}";
            }
        }

        /**
         * @brief Applies updated serial settings to the open port and refreshes the header.
         * @param settings New serial port settings to use.
         */
        public void ApplySettings(SerialPortSettings settings) {
            _settings = settings.Clone();
            _terminal.ShowTimestamps = _settings.ShowTimestamps;
            UpdateHeaderText();
            if (_closed) {return;}

            lock (_portSync) {
                if (!_port.IsOpen) {return;}
                try {_settings.ApplyTo(_port);}
                catch (InvalidOperationException) {}
                catch (IOException) {}
            }
            RestartLogFile();
            RefreshDisplay();
        }

        /**
         * @brief Opens or reopens the UART log file when logging is enabled in settings.
         */
        private void OpenLogFileIfConfigured() {
            CloseLogFile();
            if (!_settings.LogToFile || string.IsNullOrWhiteSpace(_settings.LogFilePath)) {return;}

            try {
                string? directory = System.IO.Path.GetDirectoryName(_settings.LogFilePath);
                if (!string.IsNullOrWhiteSpace(directory)) {Directory.CreateDirectory(directory);}
                _logWriter = new StreamWriter(_settings.LogFilePath, append: true, Encoding.UTF8) { AutoFlush = true };
                _logWriter.WriteLine($"--- Session started {DateTime.Now:O} — {_portName} / {_relayName} ---");
            } catch (IOException) {
                _logWriter = null;
            }
        }

        /**
         * @brief Restarts UART file logging after settings change.
         */
        private void RestartLogFile() {
            OpenLogFileIfConfigured();
        }

        /**
         * @brief Closes the active UART log file, if any.
         */
        private void CloseLogFile() {
            lock (_logSync) {
                if (_logWriter == null) {return;}
                try {
                    _logWriter.WriteLine($"--- Session ended {DateTime.Now:O} ---");
                    _logWriter.Dispose();
                } catch (IOException) {}
                _logWriter = null;
            }
        }

        /**
         * @brief Appends received bytes to the log file when logging is enabled.
         * @param data Received serial data span.
         */
        private void LogRx(ReadOnlySpan<byte> data) {
            if (_logWriter == null || data.Length == 0) {return;}

            lock (_logSync) {
                if (_logWriter == null) {return;}
                try {
                    if (_settings.ShowTimestamps) {_logWriter.Write(SerialPortSettings.FormatLogTimestamp(DateTime.Now));}
                    _logWriter.Write("[RX] ");
                    if (_hexMode) {
                        for (int i = 0; i < data.Length; i++) {
                            if (i > 0) {_logWriter.Write(' ');}
                            _logWriter.Write($"{data[i]:X2}");
                        }
                    } else {
                        for (int i = 0; i < data.Length; i++) {
                            byte b = data[i];
                            _logWriter.Write(b >= 32 && b < 127 ? (char)b : '.');
                        }
                    }
                    _logWriter.WriteLine();
                } catch (IOException) {
                    CloseLogFile();
                }
            }
        }

        /**
         * @brief Updates the header line with port, relay name, settings summary, and display mode.
         */
        private void UpdateHeaderText() {
            string mode = _hexMode ? "HEX" : "VT100";
            string logging = _settings.LogToFile && !string.IsNullOrWhiteSpace(_settings.LogFilePath) ? "LOG" : "no log";
            string stamps = _settings.ShowTimestamps ? "ts" : "no ts";
            _headerText.Text = $"{_portName}  |  {_relayName}  |  {_settings.ToSummaryString()}  |  {mode}  |  {logging}  |  {stamps}";
        }

        /**
         * @brief Clears received data buffers and refreshes the display.
         */
        private void ClearDisplay() {
            lock (_rawRxBytes) {_rawRxBytes.Clear();}
            _terminal.Clear();
            if (_hexMode) {RefreshHexDisplay();}
        }

        /**
         * @brief Refreshes the terminal display in VT100 or hex mode and scrolls to end.
         */
        private void RefreshDisplay() {
            if (_hexMode) {RefreshHexDisplay();}
            else {_terminalText.Text = _terminal.GetDisplayText();}
            _scroll.ScrollToEnd();
        }

        /**
         * @brief Renders received bytes as a classic hex dump with ASCII column.
         */
        private void RefreshHexDisplay() {
            byte[] snapshot;
            lock (_rawRxBytes) {snapshot = _rawRxBytes.ToArray();}

            StringBuilder sb = new();
            for (int i = 0; i < snapshot.Length; i += 16) {
                sb.Append($"{i:X8}  ");
                for (int j = 0; j < 16; j++) {
                    if (i + j < snapshot.Length) {sb.Append($"{snapshot[i + j]:X2} ");}
                    else {sb.Append("   ");}
                }
                sb.Append(' ');
                for (int j = 0; j < 16 && i + j < snapshot.Length; j++) {
                    byte b = snapshot[i + j];
                    sb.Append(b >= 32 && b < 127 ? (char)b : '.');
                }
                sb.AppendLine();
            }
            _terminalText.Text = sb.ToString();
        }

        /**
         * @brief Appends received bytes to raw storage and the VT100 buffer.
         * @param data Received serial data span.
         */
        private void AppendRx(ReadOnlySpan<byte> data) {
            lock (_rawRxBytes) {
                _rawRxBytes.AddRange(data.ToArray());
                if (_rawRxBytes.Count > MaxRawBytes) {
                    _rawRxBytes.RemoveRange(0, _rawRxBytes.Count - MaxRawBytes);
                }
            }
            _terminal.Append(data);
            LogRx(data);
            if (_hexMode) {RefreshHexDisplay(); _scroll.ScrollToEnd();}
        }

        /**
         * @brief Send box Enter key handler: transmits the typed line.
         * @param sender Event source (unused).
         * @param e Key event arguments.
         */
        private void OnSendBoxKeyDown(object? sender, KeyEventArgs e) {
            if (e.Key != Key.Enter || _closed) {return;}
            e.Handled = true;
            SendTypedLine();
        }

        /**
         * @brief Sends the send-box text as UTF-8 with CRLF terminator.
         */
        private void SendTypedLine() {
            if (_closed) {return;}
            string text = _sendBox.Text ?? string.Empty;
            if (text.Length == 0) {return;}

            try {
                byte[] data = Encoding.UTF8.GetBytes(text + "\r\n");
                lock (_portSync) {
                    if (!_port.IsOpen) {return;}
                    _port.Write(data, 0, data.Length);
                }
                _sendBox.Clear();
            } catch (IOException) {}
        }

        /**
         * @brief Cancels the read loop, closes the port, and marks the window as closed.
         */
        public void StopPort() {
            if (_closed) {return;}
            _closed = true;
            _readCancel.Cancel();
            CloseLogFile();
            lock (_portSync) {
                try {
                    if (_port.IsOpen) {_port.Close();}
                } catch (IOException) {}
                _port.Dispose();
            }
        }

        /**
         * @brief Background loop reading from the serial port until cancelled.
         * @param token Cancellation token to stop the loop.
         */
        private void ReadLoop(CancellationToken token) {
            byte[] buffer = new byte[4096];
            while (!token.IsCancellationRequested) {
                try {
                    int read;
                    lock (_portSync) {
                        if (!_port.IsOpen) {break;}
                        read = _port.Read(buffer, 0, buffer.Length);
                    }
                    if (read <= 0) {continue;}

                    byte[] chunk = new byte[read];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                    Dispatcher.UIThread.Post(() => {
                        if (!_closed) {AppendRx(chunk);}
                    });
                } catch (TimeoutException) {}
                catch (IOException) {break;}
                catch (ObjectDisposedException) {break;}
            }
        }

        /**
         * @brief VT100 buffer changed handler: updates terminal text when not in hex mode.
         */
        private void OnTerminalChanged() {
            if (_hexMode) {return;}
            _terminalText.Text = _terminal.GetDisplayText();
            _scroll.ScrollToEnd();
        }

        /**
         * @brief Window closed override: stops the port and raises MonitorClosed.
         * @param e Closed event arguments.
         */
        protected override void OnClosed(EventArgs e) {
            StopPort();
            MonitorClosed?.Invoke(this, EventArgs.Empty);
            base.OnClosed(e);
        }
    }

    // --- Blue countdown ring drawn around status dot during power cycle ---

    /**
     * @brief Circular progress ring control drawn around the status dot during power cycle.
     */
    private sealed class CycleRing : Control {
        private static readonly IBrush CycleRingBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212));
        private static readonly Pen CycleRingPen = new(CycleRingBrush, 2) { LineCap = PenLineCap.Round };

        public static readonly StyledProperty<double> ProgressProperty =
            AvaloniaProperty.Register<CycleRing, double>(nameof(Progress));

        static CycleRing() => AffectsRender<CycleRing>(ProgressProperty);

        /**
         * @brief Creates a 22×22 pixel cycle ring control.
         */
        public CycleRing() { Width = 22; Height = 22; }

        public double Progress {
            get => GetValue(ProgressProperty);
            set => SetValue(ProgressProperty, value);
        }

        /**
         * @brief Draws the blue arc segment representing remaining cycle time.
         * @param context Drawing context for render pass.
         */
        public override void Render(DrawingContext context) {
            if (Progress <= 0.001) {return;}

            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
            double radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - CycleRingPen.Thickness / 2;
            context.DrawGeometry(null, CycleRingPen, Arc(center, radius, -90, Progress * 360));
        }

        /**
         * @brief Builds a StreamGeometry arc from start angle sweeping counter-clockwise.
         * @param center Arc center point.
         * @param radius Arc radius in pixels.
         * @param startDeg Start angle in degrees (0 = right, -90 = top).
         * @param sweepDeg Sweep angle in degrees.
         * @retval StreamGeometry Arc path geometry.
         */
        private static StreamGeometry Arc(Point center, double radius, double startDeg, double sweepDeg) {
            var g = new StreamGeometry();
            using var ctx = g.Open();
            if (sweepDeg <= 0.01) {return g;}

            ctx.BeginFigure(Polar(center, radius, startDeg), false);
            ctx.ArcTo(Polar(center, radius, startDeg - sweepDeg), new Size(radius, radius), 0, sweepDeg > 180, SweepDirection.CounterClockwise);
            return g;
        }

        /**
         * @brief Converts polar coordinates to a Point relative to center.
         * @param c Center point.
         * @param r Radius.
         * @param deg Angle in degrees.
         * @retval Point Cartesian point on the circle.
         */
        private static Point Polar(Point c, double r, double deg) {
            double rad = deg * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }
    }
}
