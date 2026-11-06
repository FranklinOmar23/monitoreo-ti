using System;
using System.IO;
using System.Linq;
using System.Text;
using MonitoreoTI.AgenteClasico;
using Xunit;

namespace MonitoreoTI.AgenteClasico.Tests
{
    /// <summary>Decisión 84: piezas de las sesiones remotas del agente clásico.</summary>
    public class RemoteSessionTests
    {
        [Fact]
        public void Frames_between_service_and_assistant_round_trip()
        {
            using (var stream = new MemoryStream())
            {
                var gate = new object();
                FrameIo.WriteText(stream, gate, new System.Collections.Generic.Dictionary<string, object> { ["tipo"] = "ver" });
                FrameIo.Write(stream, gate, FrameIo.Binary, new byte[] { 2, 0, 0, 0, 7, 42 });
                stream.Position = 0;

                var text = FrameIo.Read(stream);
                Assert.Equal(FrameIo.Text, text.Type);
                Assert.Equal("ver", Json.Read(Encoding.UTF8.GetString(text.Data)).Str("tipo"));
                var binary = FrameIo.Read(stream);
                Assert.Equal(FrameIo.Binary, binary.Type);
                Assert.Equal(new byte[] { 2, 0, 0, 0, 7, 42 }, binary.Data);
                Assert.Null(FrameIo.Read(stream));
            }
        }

        [Fact]
        public void Physical_keys_map_to_scan_codes()
        {
            Assert.Equal(0x1E, Input.ScanCodes["KeyA"]);
            Assert.Equal(0x0B, Input.ScanCodes["Digit0"]);
            Assert.Equal(0x44, Input.ScanCodes["F10"]);
            Assert.Equal(0x1C | 0x10000, Input.ScanCodes["NumpadEnter"]);
            Assert.Equal(0x4B | 0x10000, Input.ScanCodes["ArrowLeft"]);
            Assert.Equal(26, Input.ScanCodes.Keys.Count(k => k.StartsWith("Key")));
        }

        [Fact]
        public void Screen_updates_use_the_V1_tile_format()
        {
            var monitors = ScreenCapture.Monitors();
            if (monitors.Count == 0)
                return; // sin escritorio interactivo (servidor de compilación)
            var primary = monitors[0];
            using (var capture = new ScreenCapture(new MonitorArea(primary.X, primary.Y, Math.Min(200, primary.Width), Math.Min(150, primary.Height), true)))
            {
                var full = capture.Capture(50);
                Assert.NotNull(full);
                Assert.Equal(1, full[0]);
                Assert.Equal(Math.Min(200, primary.Width), BitConverter.ToUInt16(full, 1));
                var count = BitConverter.ToUInt16(full, 5);
                var offset = 7;
                for (var i = 0; i < count; i++)
                {
                    var length = BitConverter.ToInt32(full, offset + 8);
                    Assert.Equal(0xFF, full[offset + 12]);
                    Assert.Equal(0xD8, full[offset + 13]); // JPEG
                    offset += 12 + length;
                }
                Assert.Equal(full.Length, offset);
                capture.RequestFull();
                Assert.NotNull(capture.Capture(50));
            }
        }
    }
}
