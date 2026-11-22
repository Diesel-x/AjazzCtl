using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace AjazzCtl;

internal static class Program
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "gui" or "--gui")
        {
            var app = new System.Windows.Application();
            return app.Run(new MainWindow());
        }

        if (!Console.IsOutputRedirected && !Console.IsErrorRedirected)
            AttachConsole(ATTACH_PARENT_PROCESS);

        if (args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        if (args[0] is "--smoke-test-gui")
        {
            return RunGuiSmokeTest();
        }

        string cmd = args[0].ToLowerInvariant();
        string[] rest = args.Skip(1).ToArray();

        try
        {
            return cmd switch
            {
                "status" => RunStatus(),
                "sync-time" => RunSyncTime(rest),
                "light" => RunLight(rest),
                "per-key" => RunPerKey(rest),
                "settings" => RunSettings(rest),
                "screen-image" => RunScreenImage(rest),
                "screen-widget" => RunScreenWidget(rest),
                "test" => RunSelfTests(),
                _ => UnknownCommand(cmd)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int UnknownCommand(string cmd)
    {
        Console.Error.WriteLine($"Unknown command '{cmd}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            AjazzCtl - Direct HID utility for AJAZZ AK820 Pro (USB & 2.4G)

            Usage:
              AjazzCtl                  (launches windowed GUI)
              AjazzCtl gui              (launches windowed GUI)
              AjazzCtl status
              AjazzCtl sync-time [--mode usb|24g] [--slot 1] [--dry-run]
              AjazzCtl light --mode <0..19> [--color #RRGGBB] [--brightness 0..5] [--speed 0..5] [--direction 0..3] [--colorful] [--conn usb|24g] [--dry-run]
              AjazzCtl per-key [--preset cyberpunk|gamer|gradient|rainbow|solid|off] [--color #RRGGBB] [--wasd #RRGGBB] [--arrows #RRGGBB] [--brightness 1..5] [--conn usb|24g] [--dry-run]
              AjazzCtl settings [--win-lock 0|1] [--altf4-lock 0|1] [--alttab-lock 0|1] [--debounce 1..5] [--sleep 0..3] [--fn-switch 0|1] [--conn usb|24g] [--dry-run]
              AjazzCtl screen-image <path-to-img-or-gif> [--slot 1] [--conn usb|24g] [--dry-run]
              AjazzCtl screen-widget [--text <label>] [--save-preview <out.png>] [--upload] [--slot 1] [--conn usb|24g] [--dry-run]
              AjazzCtl test
              AjazzCtl --smoke-test-gui
            """);
    }

    private static int RunStatus()
    {
        var devices = HidDiscovery.EnumerateAjazzDevices();
        if (devices.Count == 0)
        {
            Console.WriteLine("No AJAZZ AK820 Pro HID devices (VID_0C45) found.");
            return 0;
        }

        Console.WriteLine($"Found {devices.Count} AJAZZ HID collection(s):");
        foreach (var d in devices)
        {
            string role = d.IsUsbControlFeature ? "USB Control Feature (65B)"
                : d.IsUsbScreenBulk ? "USB Screen Bulk Output (4097B)"
                : d.IsWirelessVendor ? "2.4G Vendor Command Pipe (33B)"
                : "Standard Keyboard/Consumer HID";

            Console.WriteLine($"  - [{d.Mode}] VID_{d.VendorId:X4}&PID_{d.ProductId:X4} | UsagePage=0x{d.UsagePage:X4} Usage=0x{d.Usage:X4} | In={d.InputReportByteLength} Out={d.OutputReportByteLength} Feat={d.FeatureReportByteLength} | {role}");
            if (!string.IsNullOrWhiteSpace(d.ProductString))
                Console.WriteLine($"      Product: {d.ProductString}");
            Console.WriteLine($"      Path:    {d.DevicePath}");
        }
        return 0;
    }

    private static int RunSyncTime(string[] args)
    {
        bool dryRun = HasFlag(args, "--dry-run");
        byte slot = ParseByteOption(args, "--slot", 1);
        ConnectionMode? preferred = ParseConnectionOption(args, "--mode") ?? ParseConnectionOption(args, "--conn");
        DateTime now = DateTime.Now;

        if (dryRun)
        {
            Console.WriteLine($"[DRY-RUN] Time Sync for {now:yyyy-MM-dd HH:mm:ss} (DayOfWeek={(int)now.DayOfWeek}, slot={slot})");
            DumpUsbPackets("USB Wired Feature sequence", AjazzProtocol.BuildUsbTimeSyncPackets(now, slot));
            DumpHex("2.4G Wireless 33-byte report", AjazzProtocol.BuildWirelessTimeSyncReport(now, slot));
            return 0;
        }

        using var session = HidDiscovery.OpenSession(preferred);
        AjazzProtocol.SyncTime(session, now, slot);
        Console.WriteLine($"Time synchronized via {session.Mode}: {now:yyyy-MM-dd HH:mm:ss}");
        return 0;
    }

    private static int RunLight(string[] args)
    {
        bool dryRun = HasFlag(args, "--dry-run");
        byte mode = ParseByteOption(args, "--mode", (byte)LightMode.Rolling);
        var (r, g, b) = ParseColorOption(args, "--color", (255, 128, 0));
        byte brightness = ParseByteOption(args, "--brightness", 5);
        byte speed = ParseByteOption(args, "--speed", 3);
        byte direction = ParseByteOption(args, "--direction", 0);
        bool colorful = HasFlag(args, "--colorful");
        ConnectionMode? preferred = ParseConnectionOption(args, "--conn");

        if (dryRun)
        {
            Console.WriteLine($"[DRY-RUN] Lighting mode={mode} RGB=({r},{g},{b}) brightness={brightness} speed={speed} dir={direction} colorful={colorful}");
            DumpUsbPackets("USB Wired Feature sequence", AjazzProtocol.BuildUsbLightingPackets(mode, r, g, b, brightness, speed, direction, colorful));
            DumpHex("2.4G Wireless 33-byte report", AjazzProtocol.BuildWirelessLightingReport(mode, r, g, b, brightness, speed, direction, colorful));
            return 0;
        }

        using var session = HidDiscovery.OpenSession(preferred);
        AjazzProtocol.SetLighting(session, mode, r, g, b, brightness, speed, direction, colorful);
        Console.WriteLine($"Lighting updated via {session.Mode} (mode={mode}, RGB=#{r:X2}{g:X2}{b:X2}, brightness={brightness}, speed={speed}).");
        return 0;
    }

    private static int RunPerKey(string[] args)
    {
        bool dryRun = HasFlag(args, "--dry-run");
        string preset = GetOptionValue(args, "--preset") ?? "gamer";
        var primary = ParseColorOption(args, "--color", (0, 180, 255));
        var wasd = ParseColorOption(args, "--wasd", (255, 42, 85));
        var arrows = ParseColorOption(args, "--arrows", (255, 200, 0));
        byte brightness = Math.Clamp(ParseByteOption(args, "--brightness", 5), (byte)1, (byte)5);
        ConnectionMode? preferred = ParseConnectionOption(args, "--conn");

        var colors = KeyboardLayout.BuildPresetColors(preset, primary, wasd, arrows);
        byte[] buf576 = AjazzProtocol.BuildPerKeyBuffer(colors);

        if (dryRun)
        {
            Console.WriteLine($"[DRY-RUN] Per-Key RGB preset='{preset}' keys={colors.Count} brightness={brightness}");
            DumpUsbPackets("USB Wired Per-Key Feature sequence", AjazzProtocol.BuildUsbPerKeyPackets(buf576, brightness));
            var wireless = AjazzProtocol.BuildWirelessPerKeyReports(buf576, brightness);
            Console.WriteLine($"  2.4G Wireless Per-Key sequence ({wireless.Count} reports):");
            DumpHex("    [Mode 0x80]", wireless[0]);
            DumpHex("    [Block 0]", wireless[1]);
            DumpHex("    [Block 20]", wireless[21]);
            return 0;
        }

        using var session = HidDiscovery.OpenSession(preferred);
        AjazzProtocol.SetPerKeyLighting(session, colors, brightness);
        Console.WriteLine($"Per-Key RGB updated via {session.Mode} (preset='{preset}', keys={colors.Count}, brightness={brightness}).");
        return 0;
    }

    private static int RunSettings(string[] args)
    {
        bool dryRun = HasFlag(args, "--dry-run");
        bool disableWin = ParseByteOption(args, "--win-lock", 0) != 0;
        bool disableAltF4 = ParseByteOption(args, "--altf4-lock", 0) != 0;
        bool disableAltTab = ParseByteOption(args, "--alttab-lock", 0) != 0;
        byte debounce = ParseByteOption(args, "--debounce", 1);
        SleepTimeout sleep = (SleepTimeout)Math.Clamp(ParseByteOption(args, "--sleep", 2), (byte)0, (byte)3);
        bool fnSwitch = ParseByteOption(args, "--fn-switch", 0) != 0;
        ConnectionMode? preferred = ParseConnectionOption(args, "--conn");

        if (dryRun)
        {
            Console.WriteLine($"[DRY-RUN] Settings winLock={disableWin} altF4Lock={disableAltF4} altTabLock={disableAltTab} debounce={debounce} sleep={sleep} fnSwitch={fnSwitch}");
            DumpUsbPackets("USB Wired Feature sequence", AjazzProtocol.BuildUsbSettingsPackets(disableWin, disableAltF4, disableAltTab, debounce, sleep, fnSwitch));
            DumpHex("2.4G Wireless 33-byte report", AjazzProtocol.BuildWirelessSettingsReport(disableWin, disableAltF4, disableAltTab, debounce, sleep, fnSwitch));
            return 0;
        }

        using var session = HidDiscovery.OpenSession(preferred);
        AjazzProtocol.SetSystemSettings(session, disableWin, disableAltF4, disableAltTab, debounce, sleep, fnSwitch);
        Console.WriteLine($"System settings updated via {session.Mode}.");
        return 0;
    }

    private static int RunScreenImage(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--"))
            throw new ArgumentException("Image path is required: screen-image <path>");

        string imagePath = args[0];
        bool dryRun = HasFlag(args, "--dry-run");
        byte slot = ParseByteOption(args, "--slot", 1);
        ConnectionMode? preferred = ParseConnectionOption(args, "--conn");

        var frames = ScreenWidgetRenderer.LoadImageFrames(imagePath);
        try
        {
            byte[] screenBuf = AjazzProtocol.BuildScreenBuffer(frames);
            return HandleScreenBufferUpload(screenBuf, frames.Count, slot, preferred, dryRun);
        }
        finally
        {
            foreach (var (bmp, _) in frames)
                bmp.Dispose();
        }
    }

    private static int RunScreenWidget(string[] args)
    {
        bool dryRun = HasFlag(args, "--dry-run");
        bool upload = HasFlag(args, "--upload");
        string? label = GetOptionValue(args, "--text");
        string? previewOut = GetOptionValue(args, "--save-preview");
        byte slot = ParseByteOption(args, "--slot", 1);
        ConnectionMode? preferred = ParseConnectionOption(args, "--conn");

        using Bitmap bmp = ScreenWidgetRenderer.RenderSystemWidget(label, DateTime.Now);
        if (!string.IsNullOrWhiteSpace(previewOut))
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(previewOut));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            bmp.Save(previewOut, ImageFormat.Png);
            Console.WriteLine($"Saved 128x128 widget preview to: {previewOut}");
        }

        byte[] screenBuf = AjazzProtocol.BuildScreenBuffer([(bmp, 100)]);
        if (!upload && !dryRun)
        {
            if (string.IsNullOrWhiteSpace(previewOut))
                Console.WriteLine("Rendered widget (pass --upload to send to keyboard or --save-preview <path> to save PNG).");
            return 0;
        }

        return HandleScreenBufferUpload(screenBuf, 1, slot, preferred, dryRun);
    }

    private static int HandleScreenBufferUpload(
        byte[] screenBuf,
        int frameCount,
        byte slot,
        ConnectionMode? preferred,
        bool dryRun)
    {
        if (dryRun)
        {
            var (start, usbHdr, usbChunks, end) = AjazzProtocol.BuildUsbScreenTransfer(screenBuf, slot);
            var (wHdr, wPkts) = AjazzProtocol.BuildWirelessScreenTransfer(screenBuf, slot);
            Console.WriteLine($"[DRY-RUN] Screen buffer: {frameCount} frame(s), {screenBuf.Length} bytes");
            Console.WriteLine($"  Header[0..7]: {BitConverter.ToString(screenBuf, 0, 8)}");
            Console.WriteLine($"  USB Bulk chunks: {usbChunks.Count} x 4096 bytes");
            DumpHex("  USB Screen Header Feature", usbHdr.Payload64);
            Console.WriteLine($"  2.4G Wireless packets: {wPkts.Count} x 33 bytes");
            DumpHex("  2.4G Screen Header Report", wHdr);
            DumpHex("  2.4G First Data Packet", wPkts[0]);
            return 0;
        }

        using var session = HidDiscovery.OpenSession(preferred);
        Console.WriteLine($"Uploading {frameCount} frame(s) ({screenBuf.Length} bytes) via {session.Mode}...");
        int lastPrinted = -1;
        AjazzProtocol.UploadScreen(session, screenBuf, slot, pct =>
        {
            if (pct / 20 != lastPrinted / 20 || pct == 100)
            {
                lastPrinted = pct;
                Console.WriteLine($"  Progress: {pct}%");
            }
        });
        Console.WriteLine("Screen upload completed.");
        return 0;
    }

    private static int RunSelfTests()
    {
        int passed = 0;

        // 1. Checksum & Wireless Time Sync
        var dt = new DateTime(2026, 11, 22, 17, 30, 45); // Sunday = 0
        byte[] wTime = AjazzProtocol.BuildWirelessTimeSyncReport(dt, slotId: 1);
        Assert(wTime.Length == 33, "Wireless time report length == 33");
        Assert(wTime[0] == 0x00 && wTime[1] == 0x0C && wTime[2] == 0x10 && wTime[5] == 0x01 && wTime[6] == 0x5A, "Wireless time header bytes");
        Assert(wTime[7] == 26 && wTime[8] == 11 && wTime[9] == 22 && wTime[10] == 17 && wTime[11] == 30 && wTime[12] == 45 && wTime[14] == 0, "Wireless time fields");
        Assert(wTime[18] == 0xAA && wTime[19] == 0x55, "Wireless time trailer 0xAA 0x55");
        Assert(wTime[32] == AjazzProtocol.ComputeWirelessChecksum(wTime), "Wireless time checksum");
        passed++;

        // 2. USB Time Sync sequence
        var uTime = AjazzProtocol.BuildUsbTimeSyncPackets(dt, slotId: 1);
        Assert(uTime.Count == 4, "USB time sequence has 4 feature reports");
        Assert(uTime[0].Payload64[0] == 0x04 && uTime[0].Payload64[1] == 0x18 && uTime[0].ExpectAck, "USB session start");
        Assert(uTime[1].Payload64[0] == 0x04 && uTime[1].Payload64[1] == 0x28 && uTime[1].Payload64[8] == 0x01, "USB time prep");
        Assert(uTime[2].Payload64[1] == 0x01 && uTime[2].Payload64[2] == 0x5A && uTime[2].Payload64[3] == 26 && uTime[2].Payload64[62] == 0xAA && uTime[2].Payload64[63] == 0x55, "USB time payload");
        Assert(uTime[3].Payload64[0] == 0x04 && uTime[3].Payload64[1] == 0x02 && uTime[3].ExpectAck, "USB session end");
        passed++;

        // 3. RGB Lighting (USB & 2.4G)
        var uLight = AjazzProtocol.BuildUsbLightingPackets((byte)LightMode.Breath, 0x12, 0x34, 0x56, 4, 2, 1, true);
        Assert(uLight.Count == 5 && uLight[2].Payload64[0] == 7 && uLight[2].Payload64[1] == 0x12 && uLight[2].Payload64[8] == 4 && uLight[2].Payload64[11] == 1 && uLight[2].Payload64[14] == 0xAA && uLight[2].Payload64[15] == 0x55, "USB lighting payload");
        Assert(uLight[4].Payload64[0] == 0x04 && uLight[4].Payload64[1] == 0xF0, "USB lighting apply packet");
        var wLight = AjazzProtocol.BuildWirelessLightingReport((byte)LightMode.Breath, 0x12, 0x34, 0x56, 4, 2, 1, true);
        Assert(wLight[1] == 0x05 && wLight[2] == 0x10 && wLight[4] == 7 && wLight[5] == 0x12 && wLight[12] == 4 && wLight[15] == 1 && wLight[18] == 0xAA && wLight[19] == 0x55, "Wireless lighting payload");
        Assert(wLight[32] == AjazzProtocol.ComputeWirelessChecksum(wLight), "Wireless lighting checksum");
        passed++;

        // 4. Settings (USB & 2.4G)
        var wSet = AjazzProtocol.BuildWirelessSettingsReport(true, false, true, 3, SleepTimeout.FiveMinutes, true);
        Assert(wSet[1] == 0x07 && wSet[2] == 0x10 && wSet[5] == 0x01 && wSet[6] == 1 && wSet[7] == 0 && wSet[8] == 1 && wSet[9] == 3 && wSet[10] == 2 && wSet[12] == 1, "Wireless settings payload");
        Assert(wSet[32] == AjazzProtocol.ComputeWirelessChecksum(wSet), "Wireless settings checksum");
        passed++;

        // 5. RGB565 Pixel Encoding (Little-Endian)
        Assert(AjazzProtocol.EncodeRgb565Pixel(255, 0, 0) == 0xF800, "Pure Red RGB565 == 0xF800");
        Assert(AjazzProtocol.EncodeRgb565Pixel(0, 255, 0) == 0x07E0, "Pure Green RGB565 == 0x07E0");
        Assert(AjazzProtocol.EncodeRgb565Pixel(0, 0, 255) == 0x001F, "Pure Blue RGB565 == 0x001F");
        Assert(AjazzProtocol.EncodeRgb565Pixel(255, 255, 255) == 0xFFFF, "Pure White RGB565 == 0xFFFF");
        passed++;

        // 6. Screen Buffer & Framing (USB 4096 + 2.4G 28B chunks)
        using (var bmp = new Bitmap(128, 128, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
                g.Clear(Color.FromArgb(255, 0, 0));

            byte[] screenBuf = AjazzProtocol.BuildScreenBuffer([(bmp, 100)]);
            Assert(screenBuf.Length == 256 + 32768, "Single-frame screen buffer length == 33024");
            Assert(screenBuf[0] == 1 && screenBuf[1] == 50 && screenBuf[2] == 0xFF && screenBuf[255] == 0xFF, "Screen 256-byte header");
            Assert(screenBuf[256] == 0x00 && screenBuf[257] == 0xF8, "First pixel LE RGB565 pure red (00 F8)");

            var (_, uHdr, uChunks, _) = AjazzProtocol.BuildUsbScreenTransfer(screenBuf, slotId: 1);
            Assert(uChunks.Count == 9, "USB chunks count for 33024 bytes == 9");
            Assert(uHdr.Payload64[0] == 0x04 && uHdr.Payload64[1] == 0x72 && uHdr.Payload64[2] == 1 && uHdr.Payload64[8] == 9 && uHdr.Payload64[9] == 0, "USB screen header");

            var (wHdr, wPkts) = AjazzProtocol.BuildWirelessScreenTransfer(screenBuf, slotId: 1);
            Assert(wPkts.Count == 1180, "2.4G packet count for 33024 bytes == 1180");
            Assert(wHdr[1] == 0x7F && wHdr[2] == 0x03 && wHdr[4] == 1 && wHdr[5] == (1180 & 0xFF) && wHdr[6] == (1180 >> 8), "2.4G screen header");
            Assert(wPkts[0][1] == 0x80 && wPkts[0][2] == 0x00 && wPkts[0][3] == 0x00 && wPkts[0][4] == 0x01 && wPkts[0][5] == 50, "2.4G first screen data packet");
            Assert(wPkts[0][32] == AjazzProtocol.ComputeWirelessChecksum(wPkts[0]), "2.4G first screen packet checksum");
        }
        passed++;

        // 7. MainWindow WPF GUI smoke test
        Assert(RunGuiSmokeTest() == 0, "MainWindow WPF GUI smoke test");
        passed++;

        // 8. Per-Key RGB layout, 576B buffer, USB & 2.4G framing
        Assert(KeyboardLayout.Keys.Count == 81, "AK820 Pro layout has 81 keys");
        var uniqueLightIndices = KeyboardLayout.Keys.Select(k => k.LightIndex).Distinct().ToList();
        Assert(uniqueLightIndices.Count == 81, "All 81 keys have unique LightIndex");
        Assert(uniqueLightIndices.All(idx => idx >= 1 && idx <= 121), "All LightIndex values are in 1..121");

        var testColors = KeyboardLayout.BuildPresetColors("gamer");
        byte[] pkBuf = AjazzProtocol.BuildPerKeyBuffer(testColors);
        Assert(pkBuf.Length == 576, "Per-key buffer length == 576");
        Assert(pkBuf[574] == 0xAA && pkBuf[575] == 0x55, "Per-key buffer trailer 0xAA 0x55");
        // Check Esc (LightIndex=1) and W (LightIndex=39)
        var escColor = testColors[1];
        Assert(pkBuf[4] == 1 && pkBuf[5] == escColor.R && pkBuf[6] == escColor.G && pkBuf[7] == escColor.B, "Per-key buffer Esc slot at offset 4");
        var wColor = testColors[39];
        Assert(pkBuf[39 * 4] == 39 && pkBuf[39 * 4 + 1] == wColor.R && pkBuf[39 * 4 + 2] == wColor.G && pkBuf[39 * 4 + 3] == wColor.B, "Per-key buffer W slot at offset 156");

        var uPerKey = AjazzProtocol.BuildUsbPerKeyPackets(pkBuf, brightness: 5);
        Assert(uPerKey.Count == 18, "USB Per-Key sequence has 18 feature packets");
        Assert(uPerKey[2].Payload64[0] == 0x80 && uPerKey[2].Payload64[9] == 5 && uPerKey[2].Payload64[14] == 0xAA && uPerKey[2].Payload64[15] == 0x55, "USB Per-Key mode 0x80 packet");
        Assert(uPerKey[6].Payload64[0] == 0x04 && uPerKey[6].Payload64[1] == 0x23 && uPerKey[6].Payload64[8] == 9, "USB Per-Key 0x04 0x23 prepare packet");

        var wPerKey = AjazzProtocol.BuildWirelessPerKeyReports(pkBuf, brightness: 5);
        Assert(wPerKey.Count == 22, "2.4G Per-Key sequence has 22 reports");
        Assert(wPerKey[0][1] == 0x05 && wPerKey[0][2] == 0x10 && wPerKey[0][4] == 0x80 && wPerKey[0][13] == 5 && wPerKey[0][32] == AjazzProtocol.ComputeWirelessChecksum(wPerKey[0]), "2.4G Per-Key mode 0x80 packet");
        Assert(wPerKey[1][1] == 0x14 && wPerKey[1][2] == 0x1C && wPerKey[1][3] == 0 && wPerKey[1][32] == AjazzProtocol.ComputeWirelessChecksum(wPerKey[1]), "2.4G Per-Key block 0 (28B)");
        Assert(wPerKey[21][1] == 0x14 && wPerKey[21][2] == 0x10 && wPerKey[21][3] == 20 && wPerKey[21][32] == AjazzProtocol.ComputeWirelessChecksum(wPerKey[21]), "2.4G Per-Key block 20 (16B)");
        passed++;

        // 9. GIF Frame Optimization (Subsampling preserving duration)
        using (var dummy = new Bitmap(128, 128, PixelFormat.Format32bppArgb))
        {
            var raw120 = Enumerable.Range(0, 120).Select(_ => (dummy, 40)).ToList();
            var opt30 = ScreenWidgetRenderer.OptimizeFrames(raw120, 30);
            try
            {
                Assert(opt30.Count == 30, "OptimizeFrames subsamples 120 frames down to 30");
                Assert(opt30.All(f => f.DelayMs == 160), "OptimizeFrames scales 40ms * 4 = 160ms per frame");
            }
            finally
            {
                foreach (var (f, _) in opt30)
                    f.Dispose();
            }
        }
        passed++;

        // 10. Key Press Sound Synthesizer & WAV Loader
        foreach (KeySoundProfile prof in Enum.GetValues<KeySoundProfile>())
        {
            if (prof == KeySoundProfile.CustomWav)
                continue;
            var bank = KeySoundEngine.SynthesizeProfile(prof);
            Assert(bank.Count == 4, $"Profile {prof} synthesizes all 4 key kinds");
            foreach (var (_, samples) in bank)
            {
                Assert(samples.Length >= 500, $"Profile {prof} generates non-empty samples");
                Assert(samples.All(s => float.IsFinite(s) && MathF.Abs(s) <= 1.05f), $"Profile {prof} stays finite within [-1.05, 1.05]");
            }
        }
        var typewriter = KeySoundEngine.SynthesizeProfile(KeySoundProfile.Typewriter);
        Assert(typewriter[KeySoundKind.Enter].Length > typewriter[KeySoundKind.Normal].Length, "Typewriter Enter bell lasts longer than key strike");

        using (var wavStream = new MemoryStream())
        {
            using (var bw = new BinaryWriter(wavStream, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                const int nSamples = 441;
                bw.Write(0x46464952); // RIFF
                bw.Write(36 + nSamples * 2);
                bw.Write(0x45564157); // WAVE
                bw.Write(0x20746D66); // fmt 
                bw.Write(16);
                bw.Write((ushort)1);
                bw.Write((ushort)1);
                bw.Write(44100u);
                bw.Write(88200u);
                bw.Write((ushort)2);
                bw.Write((ushort)16);
                bw.Write(0x61746164); // data
                bw.Write(nSamples * 2);
                for (int i = 0; i < nSamples; i++)
                    bw.Write((short)(MathF.Sin(i * 0.2f) * 16000f));
            }
            wavStream.Position = 0;
            float[] loadedWav = KeySoundEngine.LoadWavSamples(wavStream);
            Assert(loadedWav.Length == 441, "LoadWavSamples decodes 16-bit 44.1kHz PCM mono cleanly");
        }
        passed++;

        // 11. Multi-Language Localization & Safe Uninstall Path Guards
        var detected = Loc.DetectDefaultLanguage();
        Assert(Enum.IsDefined(detected), "DetectDefaultLanguage returns a valid AppLanguage");
        foreach (AppLanguage lang in Enum.GetValues<AppLanguage>())
        {
            foreach (string key in Loc.AllKeys)
            {
                Assert(Loc.TryGetExact(lang, key, out string val) && !string.IsNullOrWhiteSpace(val),
                    $"Localization key '{key}' has non-empty translation for {lang}");
            }
            for (byte mode = 0; mode < 20; mode++)
            {
                Assert(!string.IsNullOrWhiteSpace(Loc.GetLightingModeName(lang, mode)),
                    $"Lighting mode {mode} has non-empty name for {lang}");
            }
        }
        Assert(!UninstallHelper.IsSafeToDeleteDirectory(@"C:\"), "Uninstall guard rejects drive root");
        Assert(!UninstallHelper.IsSafeToDeleteDirectory(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)), "Uninstall guard rejects Desktop");
        Assert(!UninstallHelper.IsSafeToDeleteDirectory(@"c:\Repos\AjazzSoft\AjazzCtl"), "Uninstall guard rejects source directory with .csproj/.cs");
        Assert(UninstallHelper.IsSafeToDeleteDirectory(@"C:\Temp\AjazzCtl_Portable"), "Uninstall guard allows dedicated portable Ajazz folder");
        passed++;

        Console.WriteLine($"All {passed} protocol test suites passed.");
        return 0;
    }

    private static int RunGuiSmokeTest()
    {
        var window = new MainWindow();
        try
        {
            Assert(window.DetectedDeviceCount >= 0, "MainWindow device scan succeeds");
        }
        finally
        {
            window.ShutdownTray();
            window.Close();
        }
        return 0;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"Assertion failed: {message}");
    }

    private static void DumpUsbPackets(string title, IReadOnlyList<UsbFeaturePacket> packets)
    {
        Console.WriteLine($"  {title} ({packets.Count} packets):");
        for (int i = 0; i < packets.Count; i++)
        {
            DumpHex($"    [{i}] (ack={packets[i].ExpectAck})", packets[i].Payload64);
        }
    }

    private static void DumpHex(string label, byte[] bytes)
    {
        Console.WriteLine($"  {label} ({bytes.Length}B): {BitConverter.ToString(bytes).Replace("-", " ")}");
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? GetOptionValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static byte ParseByteOption(string[] args, string name, byte defaultVal)
    {
        string? raw = GetOptionValue(args, name);
        return raw is not null && byte.TryParse(raw, out byte val) ? val : defaultVal;
    }

    private static ConnectionMode? ParseConnectionOption(string[] args, string name)
    {
        string? raw = GetOptionValue(args, name)?.ToLowerInvariant();
        return raw switch
        {
            "usb" => ConnectionMode.UsbWired,
            "24g" or "2.4g" or "wireless" => ConnectionMode.Wireless24G,
            _ => null
        };
    }

    private static (byte R, byte G, byte B) ParseColorOption(string[] args, string name, (byte, byte, byte) fallback)
    {
        string? raw = GetOptionValue(args, name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        raw = raw.TrimStart('#');
        if (raw.Length == 6 &&
            byte.TryParse(raw[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
            byte.TryParse(raw[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
            byte.TryParse(raw[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            return (r, g, b);
        }
        return fallback;
    }
}
