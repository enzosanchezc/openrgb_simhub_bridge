using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace OpenRgbSimhubBridge
{
    /// <summary>
    /// Minimal, dependency-free client for the OpenRGB SDK network protocol.
    ///
    /// We deliberately speak protocol version 0 (we never send REQUEST_PROTOCOL_VERSION, so the
    /// server serializes controller data in the original layout - no brightness/segment fields).
    /// That keeps parsing simple and is fully supported by current OpenRGB. All we need is:
    /// "find a device by name, learn its LED count, set every LED to one colour".
    /// </summary>
    internal sealed class OpenRgbClient : IDisposable
    {
        // Header: "ORGB" + uint32 device_index + uint32 command_id + uint32 data_size (all little-endian).
        private static readonly byte[] Magic = { (byte)'O', (byte)'R', (byte)'G', (byte)'B' };

        private const uint CmdRequestControllerCount = 0;
        private const uint CmdRequestControllerData = 1;
        private const uint CmdSetClientName = 50;
        private const uint CmdSetCustomMode = 1004;
        private const uint CmdUpdateLeds = 1050;

        private readonly string _host;
        private readonly int _port;
        private readonly string _clientName;

        private TcpClient _tcp;
        private NetworkStream _stream;

        public OpenRgbClient(string host, int port, string clientName)
        {
            _host = host;
            _port = port;
            _clientName = clientName;
        }

        public bool IsConnected => _tcp != null && _tcp.Connected;

        public void Connect()
        {
            Dispose();
            _tcp = new TcpClient { NoDelay = true };
            _tcp.Connect(_host, _port);
            _tcp.ReceiveTimeout = 3000;
            _tcp.SendTimeout = 3000;
            _stream = _tcp.GetStream();

            // Announce ourselves (raw bytes + null terminator, length carried by the header).
            var nameBytes = Encoding.ASCII.GetBytes(_clientName);
            var payload = new byte[nameBytes.Length + 1];
            Array.Copy(nameBytes, payload, nameBytes.Length);
            Send(0, CmdSetClientName, payload);
        }

        /// <summary>Enumerate all controllers and return the index/LED-count of the first whose name
        /// contains <paramref name="deviceNameFilter"/> (case-insensitive). null if not found.</summary>
        public OpenRgbDevice FindDevice(string deviceNameFilter)
        {
            Send(0, CmdRequestControllerCount, Array.Empty<byte>());
            var countPayload = Receive(out uint cmd, out _);
            if (cmd != CmdRequestControllerCount || countPayload.Length < 4)
                throw new IOException("Unexpected reply to controller-count request.");
            uint count = BitConverter.ToUInt32(countPayload, 0);

            for (uint i = 0; i < count; i++)
            {
                Send(i, CmdRequestControllerData, Array.Empty<byte>());
                var data = Receive(out uint dataCmd, out _);
                if (dataCmd != CmdRequestControllerData)
                    continue;

                var dev = ParseController(i, data);
                if (dev.Name.IndexOf(deviceNameFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                    return dev;
            }
            return null;
        }

        /// <summary>List all controller names (for diagnostics / config help).</summary>
        public List<OpenRgbDevice> ListDevices()
        {
            var result = new List<OpenRgbDevice>();
            Send(0, CmdRequestControllerCount, Array.Empty<byte>());
            var countPayload = Receive(out uint cmd, out _);
            if (cmd != CmdRequestControllerCount || countPayload.Length < 4)
                return result;
            uint count = BitConverter.ToUInt32(countPayload, 0);
            for (uint i = 0; i < count; i++)
            {
                Send(i, CmdRequestControllerData, Array.Empty<byte>());
                var data = Receive(out uint dataCmd, out _);
                if (dataCmd == CmdRequestControllerData)
                    result.Add(ParseController(i, data));
            }
            return result;
        }

        /// <summary>Switch a device into its "Custom/Direct" mode so live LED updates take effect.</summary>
        public void SetCustomMode(uint deviceIndex)
        {
            Send(deviceIndex, CmdSetCustomMode, Array.Empty<byte>());
        }

        /// <summary>Set every LED of the device to a single RGB colour.</summary>
        public void SetSolidColor(OpenRgbDevice device, byte r, byte g, byte b)
        {
            int ledCount = device.LedCount;
            if (ledCount <= 0) return;

            // payload: uint32 data_size, uint16 num_colors, then num_colors * (R,G,B,0)
            int payloadLen = 4 + 2 + ledCount * 4;
            var buf = new byte[payloadLen];
            using (var ms = new MemoryStream(buf))
            using (var w = new BinaryWriter(ms))
            {
                w.Write((uint)payloadLen);
                w.Write((ushort)ledCount);
                for (int i = 0; i < ledCount; i++)
                {
                    w.Write(r);
                    w.Write(g);
                    w.Write(b);
                    w.Write((byte)0);
                }
            }
            Send((uint)device.Index, CmdUpdateLeds, buf);
        }

        // --- protocol-version-0 controller-data parser -----------------------------------------

        private static OpenRgbDevice ParseController(uint index, byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var r = new BinaryReader(ms))
            {
                r.ReadUInt32();           // data_size (repeats header size)
                r.ReadInt32();            // device_type
                string name = ReadString(r);
                ReadString(r);            // description
                ReadString(r);            // version
                ReadString(r);            // serial
                ReadString(r);            // location

                ushort numModes = r.ReadUInt16();
                r.ReadInt32();            // active_mode
                for (int m = 0; m < numModes; m++)
                    SkipMode(r);

                ushort numZones = r.ReadUInt16();
                for (int z = 0; z < numZones; z++)
                    SkipZone(r);

                ushort numLeds = r.ReadUInt16();
                for (int l = 0; l < numLeds; l++)
                {
                    ReadString(r);        // led name
                    r.ReadUInt32();       // led value
                }
                // (trailing colour array intentionally not read - we have the LED count we need)

                return new OpenRgbDevice((int)index, name, numLeds);
            }
        }

        private static void SkipMode(BinaryReader r)
        {
            ReadString(r);                // name
            r.ReadInt32();                // value
            r.ReadUInt32();               // flags
            r.ReadUInt32();               // speed_min
            r.ReadUInt32();               // speed_max
            r.ReadUInt32();               // colors_min
            r.ReadUInt32();               // colors_max
            r.ReadUInt32();               // speed
            r.ReadUInt32();               // direction
            r.ReadUInt32();               // color_mode
            ushort modeColors = r.ReadUInt16();
            r.BaseStream.Seek(modeColors * 4, SeekOrigin.Current); // mode colours
        }

        private static void SkipZone(BinaryReader r)
        {
            ReadString(r);                // name
            r.ReadInt32();                // type
            r.ReadUInt32();               // leds_min
            r.ReadUInt32();               // leds_max
            r.ReadUInt32();               // leds_count
            ushort matrixLen = r.ReadUInt16();
            if (matrixLen > 0)            // matrix block (height/width/map) - size in bytes
                r.BaseStream.Seek(matrixLen, SeekOrigin.Current);
        }

        // OpenRGB string: uint16 length (includes trailing null) + that many bytes.
        private static string ReadString(BinaryReader r)
        {
            ushort len = r.ReadUInt16();
            if (len == 0) return string.Empty;
            byte[] bytes = r.ReadBytes(len);
            int actual = len > 0 && bytes[len - 1] == 0 ? len - 1 : len;
            return Encoding.ASCII.GetString(bytes, 0, actual);
        }

        // --- framing ---------------------------------------------------------------------------

        private void Send(uint deviceIndex, uint command, byte[] payload)
        {
            var header = new byte[16];
            Array.Copy(Magic, header, 4);
            Array.Copy(BitConverter.GetBytes(deviceIndex), 0, header, 4, 4);
            Array.Copy(BitConverter.GetBytes(command), 0, header, 8, 4);
            Array.Copy(BitConverter.GetBytes((uint)payload.Length), 0, header, 12, 4);
            _stream.Write(header, 0, header.Length);
            if (payload.Length > 0)
                _stream.Write(payload, 0, payload.Length);
            _stream.Flush();
        }

        private byte[] Receive(out uint command, out uint deviceIndex)
        {
            var header = ReadExactly(16);
            if (header[0] != 'O' || header[1] != 'R' || header[2] != 'G' || header[3] != 'B')
                throw new IOException("Bad magic in OpenRGB reply.");
            deviceIndex = BitConverter.ToUInt32(header, 4);
            command = BitConverter.ToUInt32(header, 8);
            uint size = BitConverter.ToUInt32(header, 12);
            return size == 0 ? Array.Empty<byte>() : ReadExactly((int)size);
        }

        private byte[] ReadExactly(int count)
        {
            var buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = _stream.Read(buf, read, count - read);
                if (n <= 0) throw new IOException("OpenRGB connection closed.");
                read += n;
            }
            return buf;
        }

        public void Dispose()
        {
            try { _stream?.Dispose(); } catch { /* ignore */ }
            try { _tcp?.Close(); } catch { /* ignore */ }
            _stream = null;
            _tcp = null;
        }
    }

    internal sealed class OpenRgbDevice
    {
        public OpenRgbDevice(int index, string name, int ledCount)
        {
            Index = index;
            Name = name ?? string.Empty;
            LedCount = ledCount;
        }

        public int Index { get; }
        public string Name { get; }
        public int LedCount { get; }
    }
}
