using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace OpenRgbSimhubBridge
{
    /// <summary>
    /// Settings page shown in SimHub's left menu (via <see cref="FlagLightPlugin.GetWPFSettingsControl"/>).
    /// Edits the plugin's live <see cref="Config"/> in place — colours/brightness/flash apply instantly,
    /// device/host/port changes trigger a reconnect — and debounce-saves to OpenRgbSimhubBridge.json.
    /// </summary>
    public partial class SettingsControl : UserControl
    {
        // Flags shown in priority order (highest first), matching the keys in Config.Flags.
        private static readonly string[] FlagOrder =
            { "Checkered", "Black", "Orange", "Yellow", "Blue", "White", "Green" };

        private readonly FlagLightPlugin _plugin;
        private readonly Config _config;
        private readonly DispatcherTimer _saveTimer;   // debounces config writes
        private readonly DispatcherTimer _statusTimer; // polls the render thread's status line

        private bool _loading; // suppresses change handlers while controls are populated

        // Device picker state.
        private readonly List<string> _knownDeviceNames = new List<string>();
        private readonly HashSet<string> _detectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _haveRefreshed;

        // Per-flag controls, kept for rebuilds/reset.
        private readonly Dictionary<string, TextBox> _flagColorBoxes = new Dictionary<string, TextBox>();
        private readonly Dictionary<string, Border> _flagSwatches = new Dictionary<string, Border>();

        internal SettingsControl(FlagLightPlugin plugin)
        {
            _plugin = plugin;
            _config = plugin.Config;

            InitializeComponent();

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); _plugin.SaveConfig(); };

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statusTimer.Tick += (s, e) => StatusText.Text = _plugin.StatusLine;

            WireStaticHandlers();
            LoadFromConfig();

            Loaded += (s, e) => { StatusText.Text = _plugin.StatusLine; _statusTimer.Start(); };
            Unloaded += (s, e) => { _statusTimer.Stop(); _saveTimer.Stop(); _plugin.SaveConfig(); };
        }

        // --- wiring & population ----------------------------------------------------------------

        private void WireStaticHandlers()
        {
            HostBox.LostFocus += (s, e) => { if (_loading) return; _config.Host = HostBox.Text.Trim(); ScheduleSave(); _plugin.RequestReconnect(); };
            PortBox.LostFocus += OnPortLostFocus;

            BrightnessSlider.ValueChanged += (s, e) =>
            {
                BrightnessLabel.Text = ((int)Math.Round(BrightnessSlider.Value * 100)) + "%";
                if (_loading) return;
                _config.Brightness = BrightnessSlider.Value;
                ScheduleSave();
            };
            FlashSlider.ValueChanged += (s, e) =>
            {
                FlashLabel.Text = FlashSlider.Value.ToString("0.0", CultureInfo.InvariantCulture) + " Hz";
                if (_loading) return;
                _config.FlashHz = FlashSlider.Value;
                ScheduleSave();
            };

            OnlyWhenGameRunningCheck.Checked   += (s, e) => { if (!_loading) { _config.OnlyWhenGameRunning = true;  ScheduleSave(); } };
            OnlyWhenGameRunningCheck.Unchecked += (s, e) => { if (!_loading) { _config.OnlyWhenGameRunning = false; ScheduleSave(); } };

            IdleColorBox.TextChanged += (s, e) =>
            {
                UpdateSwatch(IdleSwatch, IdleColorBox.Text);
                if (_loading || IdleOffCheck.IsChecked == true) return;
                if (Rgb.TryParse(IdleColorBox.Text, out _)) { _config.IdleColor = IdleColorBox.Text.Trim(); ScheduleSave(); }
            };
            IdleOffCheck.Checked   += (s, e) => OnIdleOffChanged();
            IdleOffCheck.Unchecked += (s, e) => OnIdleOffChanged();

            AddDeviceButton.Click += OnAddDeviceByName;
            RefreshButton.Click   += OnRefreshDevices;
            ResetButton.Click     += OnResetDefaults;
        }

        private void LoadFromConfig()
        {
            _loading = true;
            try
            {
                HostBox.Text = _config.Host ?? "";
                PortBox.Text = _config.Port.ToString(CultureInfo.InvariantCulture);

                BrightnessSlider.Value = Clamp(_config.Brightness, 0, 1);
                BrightnessLabel.Text = ((int)Math.Round(BrightnessSlider.Value * 100)) + "%";
                FlashSlider.Value = Clamp(_config.FlashHz, 0, FlashSlider.Maximum);
                FlashLabel.Text = FlashSlider.Value.ToString("0.0", CultureInfo.InvariantCulture) + " Hz";

                OnlyWhenGameRunningCheck.IsChecked = _config.OnlyWhenGameRunning;

                bool idleOff = string.IsNullOrWhiteSpace(_config.IdleColor);
                IdleOffCheck.IsChecked = idleOff;
                IdleColorBox.Text = _config.IdleColor ?? "";
                IdleColorBox.IsEnabled = !idleOff;
                UpdateSwatch(IdleSwatch, IdleColorBox.Text);

                BuildFlagRows();
                RebuildKnownDevices();
                RebuildDevicesPanel();
            }
            finally { _loading = false; }
        }

        // --- flags ------------------------------------------------------------------------------

        private void BuildFlagRows()
        {
            FlagsPanel.Children.Clear();
            _flagColorBoxes.Clear();
            _flagSwatches.Clear();

            foreach (var key in FlagOrder)
            {
                var setting = GetFlag(key);

                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var label = new TextBlock { Text = key, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(label, 0);

                var box = new TextBox { Text = setting.Color ?? "", VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(box, 1);

                var swatch = new Border
                {
                    Width = 26,
                    Height = 20,
                    Margin = new Thickness(8, 0, 0, 0),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80)),
                    BorderThickness = new Thickness(1)
                };
                Grid.SetColumn(swatch, 2);

                var flash = new CheckBox
                {
                    Content = "Flash",
                    IsChecked = setting.Flash,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0)
                };
                Grid.SetColumn(flash, 3);

                string k = key; // capture
                box.TextChanged += (s, e) =>
                {
                    UpdateSwatch(swatch, box.Text);
                    if (_loading) return;
                    if (Rgb.TryParse(box.Text, out _)) { GetFlag(k).Color = box.Text.Trim(); ScheduleSave(); }
                };
                flash.Checked   += (s, e) => { if (!_loading) { GetFlag(k).Flash = true;  ScheduleSave(); } };
                flash.Unchecked += (s, e) => { if (!_loading) { GetFlag(k).Flash = false; ScheduleSave(); } };

                UpdateSwatch(swatch, box.Text);

                row.Children.Add(label);
                row.Children.Add(box);
                row.Children.Add(swatch);
                row.Children.Add(flash);
                FlagsPanel.Children.Add(row);

                _flagColorBoxes[key] = box;
                _flagSwatches[key] = swatch;
            }
        }

        private FlagSetting GetFlag(string key)
        {
            if (_config.Flags == null) _config.Flags = new Dictionary<string, FlagSetting>();
            if (!_config.Flags.TryGetValue(key, out var fs) || fs == null)
            {
                fs = new FlagSetting("#000000", false);
                _config.Flags[key] = fs;
            }
            return fs;
        }

        // --- devices ----------------------------------------------------------------------------

        private void RebuildKnownDevices()
        {
            _knownDeviceNames.Clear();
            foreach (var n in _config.Devices)
                if (!string.IsNullOrWhiteSpace(n) && !_knownDeviceNames.Contains(n, StringComparer.OrdinalIgnoreCase))
                    _knownDeviceNames.Add(n);
            foreach (var n in _detectedNames)
                if (!_knownDeviceNames.Contains(n, StringComparer.OrdinalIgnoreCase))
                    _knownDeviceNames.Add(n);
        }

        private void RebuildDevicesPanel()
        {
            DevicesPanel.Children.Clear();

            if (_knownDeviceNames.Count == 0)
            {
                DevicesPanel.Children.Add(new TextBlock
                {
                    Text = "No devices yet — click Refresh (with OpenRGB running) or add one by name.",
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            bool wasLoading = _loading;
            _loading = true;
            try
            {
                foreach (var name in _knownDeviceNames)
                {
                    bool selected = _config.Devices.Contains(name, StringComparer.OrdinalIgnoreCase);
                    bool detected = !_haveRefreshed || _detectedNames.Contains(name);
                    var cb = new CheckBox
                    {
                        Content = detected ? name : name + "  — not detected",
                        Tag = name,
                        IsChecked = selected,
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    cb.Checked   += OnDeviceToggle;
                    cb.Unchecked += OnDeviceToggle;
                    DevicesPanel.Children.Add(cb);
                }
            }
            finally { _loading = wasLoading; }
        }

        private void OnDeviceToggle(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            ApplyDeviceSelection();
        }

        private void ApplyDeviceSelection()
        {
            var selected = DevicesPanel.Children.OfType<CheckBox>()
                .Where(cb => cb.IsChecked == true)
                .Select(cb => (string)cb.Tag)
                .ToList();
            _config.Devices = selected; // atomic reference swap (render thread reads it once per bind)
            ScheduleSave();
            _plugin.RequestReconnect();
        }

        private void OnAddDeviceByName(object sender, RoutedEventArgs e)
        {
            var name = AddDeviceBox.Text?.Trim();
            if (string.IsNullOrEmpty(name)) return;

            if (!_knownDeviceNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                _knownDeviceNames.Add(name);
            if (!_config.Devices.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                var list = new List<string>(_config.Devices) { name };
                _config.Devices = list;
            }
            AddDeviceBox.Text = "";
            RebuildDevicesPanel();
            ScheduleSave();
            _plugin.RequestReconnect();
        }

        private void OnRefreshDevices(object sender, RoutedEventArgs e)
        {
            RefreshButton.IsEnabled = false;
            StatusText.Text = "Querying OpenRGB…";

            Task.Run(() =>
            {
                var names = _plugin.QueryDeviceNames(out string error);
                Dispatcher.Invoke(() =>
                {
                    _haveRefreshed = true;
                    _detectedNames.Clear();
                    foreach (var n in names) _detectedNames.Add(n);
                    RebuildKnownDevices();
                    RebuildDevicesPanel();
                    RefreshButton.IsEnabled = true;
                    if (error != null)
                        StatusText.Text = "Could not reach OpenRGB: " + error;
                    else if (names.Length == 0)
                        StatusText.Text = "OpenRGB reachable but reported no devices.";
                    else
                        StatusText.Text = $"Found {names.Length} device(s).";
                });
            });
        }

        // --- idle / port / reset ----------------------------------------------------------------

        private void OnIdleOffChanged()
        {
            if (_loading) return;
            bool off = IdleOffCheck.IsChecked == true;
            IdleColorBox.IsEnabled = !off;
            if (off)
                _config.IdleColor = null;
            else
                _config.IdleColor = Rgb.TryParse(IdleColorBox.Text, out _) ? IdleColorBox.Text.Trim() : null;
            ScheduleSave();
        }

        private void OnPortLostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (int.TryParse(PortBox.Text.Trim(), out int port) && port > 0 && port <= 65535)
            {
                _config.Port = port;
                ScheduleSave();
                _plugin.RequestReconnect();
            }
            else
            {
                PortBox.Text = _config.Port.ToString(CultureInfo.InvariantCulture); // revert invalid entry
            }
        }

        private void OnResetDefaults(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Reset all OpenRGB Flag Bridge settings to their defaults?",
                    "Reset to defaults", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var d = new Config();
            _config.Devices = new List<string>(d.Devices);
            _config.Host = d.Host;
            _config.Port = d.Port;
            _config.Brightness = d.Brightness;
            _config.FlashHz = d.FlashHz;
            _config.OnlyWhenGameRunning = d.OnlyWhenGameRunning;
            _config.IdleColor = d.IdleColor;
            _config.Flags = d.Flags;

            LoadFromConfig();
            _plugin.SaveConfig();
            _plugin.RequestReconnect();
        }

        // --- helpers ----------------------------------------------------------------------------

        private void ScheduleSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private static void UpdateSwatch(Border swatch, string hex)
        {
            if (Rgb.TryParse(hex, out var rgb))
            {
                swatch.Background = new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B));
                swatch.BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80));
            }
            else
            {
                swatch.Background = Brushes.Transparent;
                swatch.BorderBrush = Brushes.OrangeRed; // signal an unparseable colour
            }
        }

        private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
    }
}
