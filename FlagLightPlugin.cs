using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;

namespace OpenRgbSimhubBridge
{
    /// <summary>
    /// SimHub data plugin that mirrors the current race flag onto one or more OpenRGB devices
    /// (e.g. a keyboard, mouse, RAM) by talking to OpenRGB's SDK server.
    ///
    /// DataUpdate (called every frame) only records the desired flag - it must stay cheap.
    /// A background thread does the OpenRGB network I/O, drives the flashing animation,
    /// and reconnects if OpenRGB isn't running yet.
    ///
    /// Implements <see cref="IWPFSettingsV2"/> so everything is configurable from a page in
    /// SimHub's left menu; the same settings are still persisted to OpenRgbSimhubBridge.json.
    /// </summary>
    [PluginName("OpenRGB Flag Bridge")]
    [PluginAuthor("openrgb_simhub_bridge")]
    [PluginDescription("Shows race flags on OpenRGB devices (keyboard, mouse, etc.) via the OpenRGB SDK.")]
    public class FlagLightPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        public PluginManager PluginManager { get; set; }

        private enum Flag { None, Green, Yellow, Blue, White, Black, Orange, Checkered }

        private Config _config;
        private string _baseDir;
        private string _configPath;
        private FileLogger _log;

        private volatile bool _running;
        private volatile bool _forceReconnect;
        private volatile Flag _currentFlag = Flag.None;
        private volatile bool _gameRunning;
        private volatile string _statusLine = "Starting…";
        private Thread _renderThread;

        public void Init(PluginManager pluginManager)
        {
            _baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory;
            _configPath = Path.Combine(_baseDir, "OpenRgbSimhubBridge.json");
            _log = new FileLogger(Path.Combine(_baseDir, "OpenRgbSimhubBridge.log"));
            _log.Info("Plugin init.");

            _config = Config.LoadOrCreate(_configPath, m => _log.Info(m));
            _log.Info($"Devices=[{string.Join(", ", _config.Devices)}], endpoint={_config.Host}:{_config.Port}, brightness={_config.Brightness}.");

            _running = true;
            _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "OpenRGB-Flag-Render" };
            _renderThread.Start();
        }

        // --- IWPFSettingsV2 (settings page in SimHub's left menu) --------------------------------

        public string LeftMenuTitle => "OpenRGB Flag Bridge";

        public ImageSource PictureIcon => _icon ?? (_icon = BuildIcon());

        public Control GetWPFSettingsControl(PluginManager pluginManager) => new SettingsControl(this);

        private static ImageSource _icon;

        /// <summary>
        /// Left-menu icon: a small checkered flag on a pole, drawn as a frozen WPF vector so there's
        /// no binary asset to ship. White on transparent so it reads on SimHub's dark sidebar.
        /// </summary>
        private static ImageSource BuildIcon()
        {
            var white = new SolidColorBrush(Colors.White);
            white.Freeze();

            var group = new DrawingGroup();

            // Flag pole.
            group.Children.Add(new GeometryDrawing(white, null,
                new RectangleGeometry(new Rect(5, 3, 2, 26))));

            // Checkered flag: 6×4 grid of squares, filling every other cell.
            const double ox = 8, oy = 4, sq = 4;
            const int cols = 6, rows = 4;

            var checkers = new GeometryGroup();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    if (((r + c) & 1) == 0)
                        checkers.Children.Add(new RectangleGeometry(new Rect(ox + c * sq, oy + r * sq, sq, sq)));
            group.Children.Add(new GeometryDrawing(white, null, checkers));

            // Flag outline so the silhouette reads as a rectangle even where cells are empty.
            var pen = new Pen(white, 1.0);
            pen.Freeze();
            group.Children.Add(new GeometryDrawing(null, pen,
                new RectangleGeometry(new Rect(ox, oy, cols * sq, rows * sq))));

            var img = new DrawingImage(group);
            img.Freeze();
            return img;
        }

        /// <summary>The live config the settings UI edits in place.</summary>
        internal Config Config => _config;

        /// <summary>Human-readable connection state for the settings UI.</summary>
        internal string StatusLine => _statusLine;

        /// <summary>Persist the current config to disk (best-effort; never throws to the UI).</summary>
        internal void SaveConfig()
        {
            try { _config.Save(_configPath); }
            catch (Exception ex) { _log?.Warn("Could not save config: " + ex.Message); }
        }

        /// <summary>Ask the render thread to drop its connection and rebind (e.g. after a device/host/port change).</summary>
        internal void RequestReconnect() => _forceReconnect = true;

        /// <summary>
        /// Connect to OpenRGB once and list the device names it exposes, for the settings UI's
        /// device picker. Runs on a background thread (the UI calls it off a Task), separate from
        /// the render thread; OpenRGB accepts multiple SDK clients so this is safe.
        /// </summary>
        internal string[] QueryDeviceNames(out string error)
        {
            error = null;
            OpenRgbClient client = null;
            try
            {
                client = new OpenRgbClient(_config.Host, _config.Port, "SimHub Flag Bridge (picker)");
                client.Connect();
                var names = new List<string>();
                foreach (var d in client.ListDevices())
                    names.Add(d.Name);
                return names.ToArray();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return Array.Empty<string>();
            }
            finally
            {
                try { client?.Dispose(); } catch { /* ignore */ }
            }
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            _gameRunning = data.GameRunning;

            var sd = data.NewData;
            if (sd == null || (!_gameRunning && _config.OnlyWhenGameRunning))
            {
                _currentFlag = Flag.None;
                return;
            }

            // Highest priority first.
            Flag f;
            if (sd.Flag_Checkered > 0) f = Flag.Checkered;
            else if (sd.Flag_Black > 0) f = Flag.Black;
            else if (sd.Flag_Orange > 0) f = Flag.Orange;
            else if (sd.Flag_Yellow > 0) f = Flag.Yellow;
            else if (sd.Flag_Blue > 0) f = Flag.Blue;
            else if (sd.Flag_White > 0) f = Flag.White;
            else if (sd.Flag_Green > 0) f = Flag.Green;
            else f = Flag.None;

            _currentFlag = f;
        }

        public void End(PluginManager pluginManager)
        {
            _log?.Info("Plugin shutting down.");
            SaveConfig(); // save-on-shutdown safety net for any unsaved tweaks
            _running = false;
            try { _renderThread?.Join(1000); } catch { /* ignore */ }
        }

        // --- background rendering ---------------------------------------------------------------

        private void RenderLoop()
        {
            OpenRgbClient client = null;
            List<OpenRgbDevice> devices = null;
            // Original mode/colours captured (once) per device name, so a reconnect while we're
            // already driving can't overwrite the genuine pre-takeover state with our own colours.
            var snapshots = new Dictionary<string, DeviceSnapshot>(StringComparer.OrdinalIgnoreCase);
            Rgb lastSent = new Rgb(1, 2, 3); // unlikely first colour -> force initial push
            bool haveLast = false;
            bool takenOver = false;   // we've switched the device(s) to Custom mode and are driving them
            bool needsRestore = false; // device(s) still hold our colours and must be handed back
            DateTime nextReconnect = DateTime.MinValue;

            while (_running)
            {
                try
                {
                    if (_forceReconnect)
                    {
                        _forceReconnect = false;
                        try { client?.Dispose(); } catch { /* ignore */ }
                        client = null;
                        devices = null;
                        takenOver = false;
                        needsRestore = false;
                        snapshots.Clear(); // device/host/port may have changed - re-capture originals
                        nextReconnect = DateTime.MinValue; // rebind immediately
                    }

                    // Nothing to do until the user has picked at least one device.
                    var filters = _config.Devices; // read the reference once (UI swaps it atomically)
                    if (filters == null || filters.Count == 0)
                    {
                        if (client != null) { try { client.Dispose(); } catch { } client = null; devices = null; takenOver = false; needsRestore = false; }
                        _statusLine = "No devices configured — add one in Settings.";
                        Thread.Sleep(200);
                        continue;
                    }

                    if (client == null || !client.IsConnected || devices == null)
                    {
                        if (DateTime.UtcNow < nextReconnect) { Thread.Sleep(50); continue; }
                        nextReconnect = DateTime.UtcNow.AddSeconds(2);

                        client?.Dispose();
                        client = new OpenRgbClient(_config.Host, _config.Port, "SimHub Flag Bridge");
                        client.Connect();

                        devices = MatchDevices(client, filters);
                        if (devices.Count == 0)
                        {
                            _statusLine = "No devices matched. Available: " + DescribeDevices(client);
                            _log.Warn($"Connected to OpenRGB but no device matched [{string.Join(", ", filters)}]. " +
                                      "Available: " + DescribeDevices(client));
                            client.Dispose();
                            client = null;
                            devices = null;
                            continue;
                        }

                        // Remember each device's original mode/colours the first time we bind it,
                        // before we ever switch it to Custom mode, so we can restore it later.
                        foreach (var d in devices)
                            if (!snapshots.ContainsKey(d.Name))
                                snapshots[d.Name] = new DeviceSnapshot(d.ActiveModeIndex, d.ActiveModeBytes, d.LedColorsBlock);

                        takenOver = false; // don't grab the device(s) until a game is actually running
                        haveLast = false;
                        _statusLine = $"Connected — {devices.Count} device(s) bound.";
                        _log.Info($"Bound {devices.Count} device(s): " +
                                  string.Join(", ", devices.ConvertAll(d => $"#{d.Index} '{d.Name}' ({d.LedCount} LEDs)")));
                    }

                    // Only drive the device(s) while a game is running (unless the user opted out);
                    // otherwise leave them to OpenRGB and restore whatever they were showing.
                    bool active = !_config.OnlyWhenGameRunning || _gameRunning;

                    if (active)
                    {
                        if (!takenOver)
                        {
                            foreach (var d in devices)
                                client.SetCustomMode((uint)d.Index);
                            takenOver = true;
                            needsRestore = true;
                            haveLast = false;
                            _statusLine = $"Active — {devices.Count} device(s) showing flags.";
                        }

                        Rgb target = ComputeTargetColor();
                        if (!haveLast || !target.Equals(lastSent))
                        {
                            foreach (var d in devices)
                                client.SetSolidColor(d, target.R, target.G, target.B);
                            lastSent = target;
                            haveLast = true;
                        }
                    }
                    else
                    {
                        if (needsRestore)
                        {
                            RestoreDevices(client, devices, snapshots);
                            needsRestore = false;
                            _log.Info("No game running — released device(s) back to OpenRGB.");
                        }
                        takenOver = false;
                        haveLast = false;
                        _statusLine = "Idle — no game running; device(s) left to OpenRGB.";
                    }
                }
                catch (Exception ex)
                {
                    _statusLine = "OpenRGB not reachable, retrying… (" + ex.Message + ")";
                    _log.Warn("OpenRGB I/O error, will reconnect: " + ex.Message);
                    try { client?.Dispose(); } catch { /* ignore */ }
                    client = null;
                    devices = null;
                    takenOver = false;
                    // keep needsRestore: if we were driving, we still owe a restore once reconnected
                    nextReconnect = DateTime.UtcNow.AddSeconds(2);
                }

                Thread.Sleep(25); // ~40 Hz render tick (enough for smooth flashing)
            }

            // On shutdown, hand the device(s) back to OpenRGB if we're still holding them.
            try
            {
                if (needsRestore && client != null && client.IsConnected && devices != null)
                    RestoreDevices(client, devices, snapshots);
            }
            catch (Exception ex) { _log.Warn("Restore on shutdown failed: " + ex.Message); }
            try { client?.Dispose(); } catch { /* ignore */ }
        }

        /// <summary>Re-apply each bound device's captured original mode and colours (best-effort restore).</summary>
        private static void RestoreDevices(OpenRgbClient client, List<OpenRgbDevice> devices,
            Dictionary<string, DeviceSnapshot> snapshots)
        {
            foreach (var d in devices)
            {
                if (!snapshots.TryGetValue(d.Name, out var s)) continue;
                client.RestoreMode((uint)d.Index, s.ModeIndex, s.ModeBytes);
                client.RestoreLedColors((uint)d.Index, s.ColorsBlock);
            }
        }

        /// <summary>Every connected device whose name contains any of the configured filters (case-insensitive).</summary>
        private static List<OpenRgbDevice> MatchDevices(OpenRgbClient client, List<string> filters)
        {
            var matched = new List<OpenRgbDevice>();
            foreach (var d in client.ListDevices())
            {
                foreach (var f in filters)
                {
                    if (!string.IsNullOrWhiteSpace(f) &&
                        d.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        matched.Add(d);
                        break;
                    }
                }
            }
            return matched;
        }

        private Rgb ComputeTargetColor()
        {
            Flag f = _currentFlag;

            if (f == Flag.None)
            {
                if (string.IsNullOrWhiteSpace(_config.IdleColor)) return Rgb.Off;
                return Rgb.TryParse(_config.IdleColor, out var idle) ? idle.Scale(_config.Brightness) : Rgb.Off;
            }

            if (_config.Flags == null || !_config.Flags.TryGetValue(f.ToString(), out var setting) || setting == null)
                return Rgb.Off;
            if (!Rgb.TryParse(setting.Color, out var color))
                return Rgb.Off;

            color = color.Scale(_config.Brightness);

            if (setting.Flash && _config.FlashHz > 0)
            {
                double periodMs = 1000.0 / _config.FlashHz;
                double phase = (DateTime.UtcNow.TimeOfDay.TotalMilliseconds % periodMs) / periodMs;
                if (phase >= 0.5) return Rgb.Off; // off half of the cycle
            }

            return color;
        }

        /// <summary>A device's original mode + colours, captured before takeover so we can restore it.</summary>
        private sealed class DeviceSnapshot
        {
            public DeviceSnapshot(int modeIndex, byte[] modeBytes, byte[] colorsBlock)
            {
                ModeIndex = modeIndex;
                ModeBytes = modeBytes;
                ColorsBlock = colorsBlock;
            }

            public int ModeIndex { get; }
            public byte[] ModeBytes { get; }
            public byte[] ColorsBlock { get; }
        }

        private static string DescribeDevices(OpenRgbClient client)
        {
            try
            {
                var names = new System.Text.StringBuilder();
                foreach (var d in client.ListDevices())
                    names.Append($"[#{d.Index} '{d.Name}' {d.LedCount}LEDs] ");
                return names.Length == 0 ? "(none)" : names.ToString();
            }
            catch { return "(could not list)"; }
        }
    }

    /// <summary>Tiny append-only file logger; keeps the plugin free of external logging deps.</summary>
    internal sealed class FileLogger
    {
        private readonly string _path;
        private readonly object _gate = new object();

        public FileLogger(string path) { _path = path; }

        public void Info(string msg) => Write("INFO", msg);
        public void Warn(string msg) => Write("WARN", msg);

        private void Write(string level, string msg)
        {
            try
            {
                lock (_gate)
                    File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
            }
            catch { /* never let logging crash the plugin */ }
        }
    }
}
