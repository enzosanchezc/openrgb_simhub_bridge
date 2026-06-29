using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace OpenRgbSimhubBridge
{
    /// <summary>
    /// User-editable configuration, stored as JSON next to the plugin DLL
    /// (OpenRgbSimhubBridge.json). Created with sensible defaults on first run.
    /// </summary>
    internal sealed class Config
    {
        // Substring match, case-insensitive. "HyperX Alloy Origins" also works (matches the "(HP)" variant).
        public string DeviceName { get; set; } = "HyperX Alloy Origins (HP)";
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 6742;

        /// <summary>0..1 global brightness multiplier applied to every colour.</summary>
        public double Brightness { get; set; } = 1.0;

        /// <summary>Flashing rate, in full on/off cycles per second.</summary>
        public double FlashHz { get; set; } = 2.0;

        /// <summary>When true, the keyboard is left alone unless a game is actively running.</summary>
        public bool OnlyWhenGameRunning { get; set; } = true;

        /// <summary>Colour shown when no flag is active (e.g. "#101010", or null to turn the keyboard off).</summary>
        public string IdleColor { get; set; } = null;

        /// <summary>Per-flag colour + flash settings. Keys: Green, Yellow, Blue, White, Black, Orange, Checkered.</summary>
        public Dictionary<string, FlagSetting> Flags { get; set; } = DefaultFlags();

        private static Dictionary<string, FlagSetting> DefaultFlags() => new Dictionary<string, FlagSetting>
        {
            ["Green"]     = new FlagSetting("#00FF00", false),
            ["Yellow"]    = new FlagSetting("#FFB400", true),
            ["Blue"]      = new FlagSetting("#0040FF", false),
            ["White"]     = new FlagSetting("#FFFFFF", false),
            ["Black"]     = new FlagSetting("#FF0000", false),
            ["Orange"]    = new FlagSetting("#FF5000", true),   // meatball / mechanical black
            ["Checkered"] = new FlagSetting("#FFFFFF", true),
        };

        public static Config LoadOrCreate(string path, Action<string> log)
        {
            try
            {
                if (File.Exists(path))
                {
                    var cfg = JsonConvert.DeserializeObject<Config>(File.ReadAllText(path));
                    if (cfg != null)
                    {
                        if (cfg.Flags == null || cfg.Flags.Count == 0) cfg.Flags = DefaultFlags();
                        return cfg;
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("Failed to read config, using defaults: " + ex.Message);
            }

            var def = new Config();
            try { def.Save(path); log?.Invoke("Wrote default config to " + path); }
            catch (Exception ex) { log?.Invoke("Could not write default config: " + ex.Message); }
            return def;
        }

        public void Save(string path)
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }

    internal sealed class FlagSetting
    {
        public FlagSetting() { }

        public FlagSetting(string color, bool flash)
        {
            Color = color;
            Flash = flash;
        }

        public string Color { get; set; }
        public bool Flash { get; set; }
    }

    /// <summary>Parsed "#RRGGBB" colour.</summary>
    internal readonly struct Rgb
    {
        public readonly byte R, G, B;

        public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }

        public Rgb Scale(double factor)
        {
            factor = Math.Max(0, Math.Min(1, factor));
            return new Rgb((byte)(R * factor), (byte)(G * factor), (byte)(B * factor));
        }

        public bool Equals(Rgb other) => R == other.R && G == other.G && B == other.B;

        public static readonly Rgb Off = new Rgb(0, 0, 0);

        public static bool TryParse(string hex, out Rgb rgb)
        {
            rgb = Off;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            hex = hex.Trim().TrimStart('#');
            if (hex.Length != 6) return false;
            if (byte.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
                byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
                byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                rgb = new Rgb(r, g, b);
                return true;
            }
            return false;
        }
    }
}
