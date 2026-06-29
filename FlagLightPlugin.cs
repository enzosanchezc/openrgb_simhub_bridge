using System;
using System.IO;
using System.Reflection;
using System.Threading;
using GameReaderCommon;
using SimHub.Plugins;

namespace OpenRgbSimhubBridge
{
    /// <summary>
    /// SimHub data plugin that mirrors the current race flag onto an OpenRGB device
    /// (e.g. a HyperX Alloy Origins keyboard) by talking to OpenRGB's SDK server.
    ///
    /// DataUpdate (called every frame) only records the desired flag - it must stay cheap.
    /// A background thread does the OpenRGB network I/O, drives the flashing animation,
    /// and reconnects if OpenRGB isn't running yet.
    /// </summary>
    [PluginName("OpenRGB Flag Bridge")]
    [PluginAuthor("openrgb_simhub_bridge")]
    [PluginDescription("Shows race flags on an OpenRGB device (HyperX keyboard, etc.) via the OpenRGB SDK.")]
    public class FlagLightPlugin : IPlugin, IDataPlugin
    {
        public PluginManager PluginManager { get; set; }

        private enum Flag { None, Green, Yellow, Blue, White, Black, Orange, Checkered }

        private Config _config;
        private string _baseDir;
        private FileLogger _log;

        private volatile bool _running;
        private volatile Flag _currentFlag = Flag.None;
        private volatile bool _gameRunning;
        private Thread _renderThread;

        public void Init(PluginManager pluginManager)
        {
            _baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory;
            _log = new FileLogger(Path.Combine(_baseDir, "OpenRgbSimhubBridge.log"));
            _log.Info("Plugin init.");

            _config = Config.LoadOrCreate(Path.Combine(_baseDir, "OpenRgbSimhubBridge.json"), m => _log.Info(m));
            _log.Info($"Target device='{_config.DeviceName}', endpoint={_config.Host}:{_config.Port}, brightness={_config.Brightness}.");

            _running = true;
            _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "OpenRGB-Flag-Render" };
            _renderThread.Start();
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
            _running = false;
            try { _renderThread?.Join(1000); } catch { /* ignore */ }
        }

        // --- background rendering ---------------------------------------------------------------

        private void RenderLoop()
        {
            OpenRgbClient client = null;
            OpenRgbDevice device = null;
            Rgb lastSent = new Rgb(1, 2, 3); // unlikely first colour -> force initial push
            bool haveLast = false;
            DateTime nextReconnect = DateTime.MinValue;

            while (_running)
            {
                try
                {
                    if (client == null || !client.IsConnected || device == null)
                    {
                        if (DateTime.UtcNow < nextReconnect) { Thread.Sleep(50); continue; }
                        nextReconnect = DateTime.UtcNow.AddSeconds(2);

                        client?.Dispose();
                        client = new OpenRgbClient(_config.Host, _config.Port, "SimHub Flag Bridge");
                        client.Connect();
                        device = client.FindDevice(_config.DeviceName);
                        if (device == null)
                        {
                            _log.Warn($"Connected to OpenRGB but no device matched '{_config.DeviceName}'. " +
                                      "Available: " + DescribeDevices(client));
                            client.Dispose();
                            client = null;
                            continue;
                        }
                        client.SetCustomMode((uint)device.Index);
                        haveLast = false;
                        _log.Info($"Bound to device #{device.Index} '{device.Name}' ({device.LedCount} LEDs).");
                    }

                    Rgb target = ComputeTargetColor();
                    if (!haveLast || !target.Equals(lastSent))
                    {
                        client.SetSolidColor(device, target.R, target.G, target.B);
                        lastSent = target;
                        haveLast = true;
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("OpenRGB I/O error, will reconnect: " + ex.Message);
                    try { client?.Dispose(); } catch { /* ignore */ }
                    client = null;
                    device = null;
                    nextReconnect = DateTime.UtcNow.AddSeconds(2);
                }

                Thread.Sleep(25); // ~40 Hz render tick (enough for smooth flashing)
            }

            try { client?.Dispose(); } catch { /* ignore */ }
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
