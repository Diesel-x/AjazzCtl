using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace AjazzCtl;

public enum LightMode : byte
{
    Off = 0,
    Static = 1,
    SingleOn = 2,
    SingleOff = 3,
    Glittering = 4,
    Falling = 5,
    Colourful = 6,
    Breath = 7,
    Spectrum = 8,
    Outward = 9,
    Scrolling = 10,
    Rolling = 11,
    Rotating = 12,
    Explode = 13,
    Launch = 14,
    Ripples = 15,
    Flowing = 16,
    Pulsating = 17,
    Tilt = 18,
    Shuttle = 19
}

public enum SleepTimeout : byte
{
    Never = 0,
    OneMinute = 1,
    FiveMinutes = 2,
    ThirtyMinutes = 3
}

public sealed record UsbFeaturePacket(byte[] Payload64, bool ExpectAck);

public static class AjazzProtocol
{
    public const int UsbCommandDelayMs = 35;
    public const int ScreenWidth = 128;
    public const int ScreenHeight = 128;
    public const int ScreenHeaderLength = 256;
    public const int ScreenFrameBytes = ScreenWidth * ScreenHeight * 2; // 32768
    public const int ScreenMaxFrames = 140;
    public const int PerKeyBufferLength = 576; // 0x240 bytes

    public static byte[] SessionStartPayload() => [0x04, 0x18, .. new byte[62]];
    public static byte[] SessionEndPayload() => [0x04, 0x02, .. new byte[62]];

    public static byte ComputeWirelessChecksum(ReadOnlySpan<byte> report33)
    {
        if (report33.Length < 32)
            throw new ArgumentException("Report must have at least 32 bytes before checksum.", nameof(report33));

        int sum = 0;
        for (int i = 1; i < 32; i++)
            sum += report33[i];

        return (byte)(sum & 0xFF);
    }

    public static IReadOnlyList<UsbFeaturePacket> BuildUsbTimeSyncPackets(DateTime now, byte slotId = 1)
    {
        byte[] prep = new byte[64];
        prep[0] = 0x04;
        prep[1] = 0x28;
        prep[8] = 0x01;

        byte[] payload = new byte[64];
        payload[0] = 0x00;
        payload[1] = slotId;
        payload[2] = 0x5A;
        payload[3] = (byte)(now.Year % 2000);
        payload[4] = (byte)now.Month;
        payload[5] = (byte)now.Day;
        payload[6] = (byte)now.Hour;
        payload[7] = (byte)now.Minute;
        payload[8] = (byte)now.Second;
        payload[9] = 0x00;
        payload[10] = (byte)now.DayOfWeek;
        payload[62] = 0xAA;
        payload[63] = 0x55;

        return
        [
            new UsbFeaturePacket(SessionStartPayload(), ExpectAck: true),
            new UsbFeaturePacket(prep, ExpectAck: true),
            new UsbFeaturePacket(payload, ExpectAck: true),
            new UsbFeaturePacket(SessionEndPayload(), ExpectAck: true)
        ];
    }

    public static byte[] BuildWirelessTimeSyncReport(DateTime now, byte slotId = 1)
    {
        byte[] raw = new byte[33];
        raw[0] = 0x00;
        raw[1] = 0x0C;
        raw[2] = 0x10;
        raw[3] = 0x00;
        raw[4] = 0x00;
        raw[5] = slotId;
        raw[6] = 0x5A;
        raw[7] = (byte)(now.Year % 2000);
        raw[8] = (byte)now.Month;
        raw[9] = (byte)now.Day;
        raw[10] = (byte)now.Hour;
        raw[11] = (byte)now.Minute;
        raw[12] = (byte)now.Second;
        raw[13] = 0x00;
        raw[14] = (byte)now.DayOfWeek;
        raw[18] = 0xAA;
        raw[19] = 0x55;
        raw[32] = ComputeWirelessChecksum(raw);
        return raw;
    }

    public static IReadOnlyList<UsbFeaturePacket> BuildUsbLightingPackets(
        byte mode,
        byte r,
        byte g,
        byte b,
        byte brightness,
        byte speed,
        byte direction,
        bool colorful)
    {
        byte[] prep = new byte[64];
        prep[0] = 0x04;
        prep[1] = 0x13;
        prep[8] = 0x01;

        byte[] payload = new byte[64];
        payload[0] = mode;
        payload[1] = r;
        payload[2] = g;
        payload[3] = b;
        payload[8] = Math.Min(brightness, (byte)5);
        payload[9] = Math.Min(speed, (byte)5);
        payload[10] = Math.Min(direction, (byte)3);
        payload[11] = colorful ? (byte)1 : (byte)0;
        payload[14] = 0xAA;
        payload[15] = 0x55;

        byte[] apply = new byte[64];
        apply[0] = 0x04;
        apply[1] = 0xF0;

        return
        [
            new UsbFeaturePacket(SessionStartPayload(), ExpectAck: true),
            new UsbFeaturePacket(prep, ExpectAck: true),
            new UsbFeaturePacket(payload, ExpectAck: false),
            new UsbFeaturePacket(SessionEndPayload(), ExpectAck: true),
            new UsbFeaturePacket(apply, ExpectAck: false)
        ];
    }

    public static byte[] BuildWirelessLightingReport(
        byte mode,
        byte r,
        byte g,
        byte b,
        byte brightness,
        byte speed,
        byte direction,
        bool colorful)
    {
        byte[] raw = new byte[33];
        raw[0] = 0x00;
        raw[1] = 0x05;
        raw[2] = 0x10;
        raw[3] = 0x00;
        raw[4] = mode;
        raw[5] = r;
        raw[6] = g;
        raw[7] = b;
        raw[12] = Math.Min(brightness, (byte)5);
        raw[13] = Math.Min(speed, (byte)5);
        raw[14] = Math.Min(direction, (byte)3);
        raw[15] = colorful ? (byte)1 : (byte)0;
        raw[18] = 0xAA;
        raw[19] = 0x55;
        raw[32] = ComputeWirelessChecksum(raw);
        return raw;
    }

    public static IReadOnlyList<UsbFeaturePacket> BuildUsbSettingsPackets(
        bool disableWin,
        bool disableAltF4,
        bool disableAltTab,
        byte responseTimeLevel,
        SleepTimeout sleepTime,
        bool fnSwitch)
    {
        byte[] prep = new byte[64];
        prep[0] = 0x04;
        prep[1] = 0x17;
        prep[2] = 0x01;
        prep[8] = 0x01;

        byte[] payload = new byte[64];
        payload[0] = 0x00;
        payload[1] = 0x01;
        payload[2] = disableWin ? (byte)1 : (byte)0;
        payload[3] = disableAltF4 ? (byte)1 : (byte)0;
        payload[4] = disableAltTab ? (byte)1 : (byte)0;
        payload[5] = Math.Clamp(responseTimeLevel, (byte)1, (byte)5);
        payload[6] = (byte)sleepTime;
        payload[7] = 0x00;
        payload[8] = fnSwitch ? (byte)1 : (byte)0;
        payload[62] = 0xAA;
        payload[63] = 0x55;

        return
        [
            new UsbFeaturePacket(SessionStartPayload(), ExpectAck: true),
            new UsbFeaturePacket(prep, ExpectAck: true),
            new UsbFeaturePacket(payload, ExpectAck: false),
            new UsbFeaturePacket(SessionEndPayload(), ExpectAck: true)
        ];
    }

    public static byte[] BuildWirelessSettingsReport(
        bool disableWin,
        bool disableAltF4,
        bool disableAltTab,
        byte responseTimeLevel,
        SleepTimeout sleepTime,
        bool fnSwitch)
    {
        byte[] raw = new byte[33];
        raw[0] = 0x00;
        raw[1] = 0x07;
        raw[2] = 0x10;
        raw[3] = 0x00;
        raw[4] = 0x00;
        raw[5] = 0x01;
        raw[6] = disableWin ? (byte)1 : (byte)0;
        raw[7] = disableAltF4 ? (byte)1 : (byte)0;
        raw[8] = disableAltTab ? (byte)1 : (byte)0;
        raw[9] = Math.Clamp(responseTimeLevel, (byte)1, (byte)5);
        raw[10] = (byte)sleepTime;
        raw[11] = 0x00;
        raw[12] = fnSwitch ? (byte)1 : (byte)0;
        raw[18] = 0xAA;
        raw[19] = 0x55;
        raw[32] = ComputeWirelessChecksum(raw);
        return raw;
    }

    public static ushort EncodeRgb565Pixel(byte r, byte g, byte b)
    {
        return (ushort)(((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3));
    }

    public static byte[] EncodeRgb565Frame(Bitmap bmp)
    {
        if (bmp.Width != ScreenWidth || bmp.Height != ScreenHeight)
            throw new ArgumentException($"Bitmap must be {ScreenWidth}x{ScreenHeight}.", nameof(bmp));

        byte[] output = new byte[ScreenFrameBytes];
        var rect = new Rectangle(0, 0, ScreenWidth, ScreenHeight);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            byte[] src = new byte[stride * ScreenHeight];
            Marshal.Copy(data.Scan0, src, 0, src.Length);

            int dstIdx = 0;
            for (int y = 0; y < ScreenHeight; y++)
            {
                int rowStart = y * stride;
                for (int x = 0; x < ScreenWidth; x++)
                {
                    int px = rowStart + x * 4;
                    byte b = src[px];
                    byte g = src[px + 1];
                    byte r = src[px + 2];
                    ushort rgb565 = EncodeRgb565Pixel(r, g, b);
                    output[dstIdx++] = (byte)(rgb565 & 0xFF);
                    output[dstIdx++] = (byte)(rgb565 >> 8);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return output;
    }

    public static byte[] BuildScreenBuffer(IReadOnlyList<(Bitmap Frame, int DelayMs)> frames)
    {
        if (frames.Count == 0)
            throw new ArgumentException("At least one frame is required.", nameof(frames));

        int count = Math.Min(frames.Count, ScreenMaxFrames);
        byte[] buffer = new byte[ScreenHeaderLength + count * ScreenFrameBytes];
        Array.Fill(buffer, (byte)0xFF, 0, ScreenHeaderLength);

        buffer[0] = (byte)count;
        for (int i = 0; i < count; i++)
        {
            int delayHalfMs = Math.Clamp(frames[i].DelayMs / 2, 1, 255);
            buffer[1 + i] = (byte)delayHalfMs;

            byte[] frameBytes = EncodeRgb565Frame(frames[i].Frame);
            Array.Copy(frameBytes, 0, buffer, ScreenHeaderLength + i * ScreenFrameBytes, ScreenFrameBytes);
        }

        return buffer;
    }

    public static (UsbFeaturePacket Start, UsbFeaturePacket Header, List<byte[]> Chunks, UsbFeaturePacket End) BuildUsbScreenTransfer(
        byte[] screenBuffer,
        byte slotId = 1)
    {
        int chunkCount = (screenBuffer.Length + 4095) / 4096;
        byte[] padded = new byte[chunkCount * 4096];
        Array.Fill(padded, (byte)0xFF);
        Array.Copy(screenBuffer, padded, screenBuffer.Length);

        byte[] headerPayload = new byte[64];
        headerPayload[0] = 0x04;
        headerPayload[1] = 0x72;
        headerPayload[2] = slotId;
        headerPayload[8] = (byte)(chunkCount & 0xFF);
        headerPayload[9] = (byte)((chunkCount >> 8) & 0xFF);

        var chunks = new List<byte[]>(chunkCount);
        for (int c = 0; c < chunkCount; c++)
        {
            byte[] chunk = new byte[4096];
            Array.Copy(padded, c * 4096, chunk, 0, 4096);
            chunks.Add(chunk);
        }

        return (
            new UsbFeaturePacket(SessionStartPayload(), ExpectAck: true),
            new UsbFeaturePacket(headerPayload, ExpectAck: true),
            chunks,
            new UsbFeaturePacket(SessionEndPayload(), ExpectAck: true)
        );
    }

    public static (byte[] HeaderReport, List<byte[]> DataReports) BuildWirelessScreenTransfer(
        byte[] screenBuffer,
        byte slotId = 1)
    {
        int packetCount = (screenBuffer.Length + 27) / 28;
        byte[] padded = new byte[packetCount * 28];
        Array.Fill(padded, (byte)0xFF);
        Array.Copy(screenBuffer, padded, screenBuffer.Length);

        byte[] header = new byte[33];
        header[0] = 0x00;
        header[1] = 0x7F;
        header[2] = 0x03;
        header[3] = 0x00;
        header[4] = slotId;
        header[5] = (byte)(packetCount & 0xFF);
        header[6] = (byte)((packetCount >> 8) & 0xFF);
        header[7] = (byte)((packetCount >> 16) & 0xFF);
        header[32] = ComputeWirelessChecksum(header);

        var packets = new List<byte[]>(packetCount);
        for (int p = 0; p < packetCount; p++)
        {
            byte[] pkt = new byte[33];
            pkt[0] = 0x00;
            pkt[1] = (byte)(0x80 | ((p >> 16) & 0x7F));
            pkt[2] = (byte)(p & 0xFF);
            pkt[3] = (byte)((p >> 8) & 0xFF);
            Array.Copy(padded, p * 28, pkt, 4, 28);
            pkt[32] = ComputeWirelessChecksum(pkt);
            packets.Add(pkt);
        }

        return (header, packets);
    }

    public static void SyncTime(AjazzHidSession session, DateTime now, byte slotId = 1)
    {
        if (session.Mode == ConnectionMode.UsbWired)
        {
            foreach (var pkt in BuildUsbTimeSyncPackets(now, slotId))
                session.SendUsbFeature(pkt.Payload64, pkt.ExpectAck);
        }
        else
        {
            var report = BuildWirelessTimeSyncReport(now, slotId);
            session.SendWireless32(report, postDelayMs: 5, verifyAck: true);
        }
    }

    public static void SetLighting(
        AjazzHidSession session,
        byte mode,
        byte r,
        byte g,
        byte b,
        byte brightness,
        byte speed,
        byte direction,
        bool colorful)
    {
        if (session.Mode == ConnectionMode.UsbWired)
        {
            foreach (var pkt in BuildUsbLightingPackets(mode, r, g, b, brightness, speed, direction, colorful))
                session.SendUsbFeature(pkt.Payload64, pkt.ExpectAck);
        }
        else
        {
            var report = BuildWirelessLightingReport(mode, r, g, b, brightness, speed, direction, colorful);
            session.SendWireless32(report, postDelayMs: 5, verifyAck: true);
        }
    }

    public static void SetSystemSettings(
        AjazzHidSession session,
        bool disableWin,
        bool disableAltF4,
        bool disableAltTab,
        byte responseTimeLevel,
        SleepTimeout sleepTime,
        bool fnSwitch)
    {
        if (session.Mode == ConnectionMode.UsbWired)
        {
            foreach (var pkt in BuildUsbSettingsPackets(disableWin, disableAltF4, disableAltTab, responseTimeLevel, sleepTime, fnSwitch))
                session.SendUsbFeature(pkt.Payload64, pkt.ExpectAck);
        }
        else
        {
            var report = BuildWirelessSettingsReport(disableWin, disableAltF4, disableAltTab, responseTimeLevel, sleepTime, fnSwitch);
            session.SendWireless32(report, postDelayMs: 5, verifyAck: true);
        }
    }

    public static void UploadScreen(
        AjazzHidSession session,
        byte[] screenBuffer,
        byte slotId = 1,
        Action<int>? onProgressPercent = null)
    {
        using var _ = HidNative.UseHighResTimer();
        if (session.Mode == ConnectionMode.UsbWired)
        {
            var (start, header, chunks, end) = BuildUsbScreenTransfer(screenBuffer, slotId);
            session.SendUsbFeature(start.Payload64, start.ExpectAck);
            session.SendUsbFeature(header.Payload64, header.ExpectAck);
            session.FlushBulkInput();
            for (int i = 0; i < chunks.Count; i++)
            {
                session.SendUsbBulkChunk(chunks[i]);
                onProgressPercent?.Invoke((i + 1) * 100 / chunks.Count);
            }
            System.Threading.Thread.Sleep(80);
            session.SendUsbFeature(end.Payload64, end.ExpectAck, maxAckAttempts: 30);
        }
        else
        {
            var (headerReport, dataReports) = BuildWirelessScreenTransfer(screenBuffer, slotId);
            session.FlushInput();
            if (!session.SendWireless32Reliable(headerReport, ackTimeoutMs: 200, maxAttempts: 3))
                throw new IOException("Клавиатура не подтвердила начало загрузки экрана (2.4 ГГц). Разбудите клавиатуру нажатием клавиши и повторите.");

            bool ackMode = true;
            for (int i = 0; i < dataReports.Count; i++)
            {
                if (ackMode)
                {
                    bool ok = session.SendWireless32Reliable(dataReports[i],
                        ackTimeoutMs: i == 0 ? 150 : 60,
                        maxAttempts: i == 0 ? 2 : 4);
                    if (!ok)
                    {
                        if (i == 0)
                            ackMode = false;   // keyboard does not echo data packets -> paced fallback
                        else
                            throw new IOException($"Пакет {i + 1}/{dataReports.Count} не подтверждён клавиатурой после 4 попыток. Поднесите ресивер 2.4 ГГц ближе или используйте USB-кабель.");
                    }
                }
                else
                {
                    session.SendWireless32(dataReports[i], postDelayMs: 4, verifyAck: false);
                }
                if (i % 25 == 0 || i == dataReports.Count - 1)
                    onProgressPercent?.Invoke((i + 1) * 100 / dataReports.Count);
            }
        }
    }

    public static byte[] BuildPerKeyBuffer(IReadOnlyDictionary<byte, (byte R, byte G, byte B)> keyColors)
    {
        byte[] buf = new byte[PerKeyBufferLength];
        foreach (var (lightIndex, (r, g, b)) in keyColors)
        {
            if (lightIndex < 1 || lightIndex > 142)
                continue;

            int offset = lightIndex * 4;
            buf[offset] = (r == 0 && g == 0 && b == 0) ? (byte)0x00 : lightIndex;
            buf[offset + 1] = r;
            buf[offset + 2] = g;
            buf[offset + 3] = b;
        }

        buf[574] = 0xAA;
        buf[575] = 0x55;
        return buf;
    }

    public static IReadOnlyList<UsbFeaturePacket> BuildUsbPerKeyPackets(byte[] buf576, byte brightness = 5)
    {
        if (buf576.Length != PerKeyBufferLength)
            throw new ArgumentException($"Per-key buffer must be {PerKeyBufferLength} bytes.", nameof(buf576));

        byte clampedBrightness = Math.Clamp(brightness, (byte)1, (byte)5);

        byte[] prepMode = new byte[64];
        prepMode[0] = 0x04;
        prepMode[1] = 0x13;
        prepMode[8] = 0x01;

        byte[] modePayload = new byte[64];
        modePayload[0] = 0x80;
        modePayload[8] = 0x00;
        modePayload[9] = clampedBrightness;
        modePayload[14] = 0xAA;
        modePayload[15] = 0x55;

        byte[] applyMode = new byte[64];
        applyMode[0] = 0x04;
        applyMode[1] = 0xF0;

        byte[] prepTable = new byte[64];
        prepTable[0] = 0x04;
        prepTable[1] = 0x23;
        prepTable[8] = 0x09;

        byte[] applyTable = new byte[64];
        applyTable[0] = 0x04;
        applyTable[1] = 0xF0;

        var packets = new List<UsbFeaturePacket>(18)
        {
            new(SessionStartPayload(), ExpectAck: true),
            new(prepMode, ExpectAck: true),
            new(modePayload, ExpectAck: false),
            new(SessionEndPayload(), ExpectAck: true),
            new(applyMode, ExpectAck: false),

            new(SessionStartPayload(), ExpectAck: true),
            new(prepTable, ExpectAck: true)
        };

        for (int i = 0; i < 9; i++)
        {
            byte[] chunk = new byte[64];
            Array.Copy(buf576, i * 64, chunk, 0, 64);
            packets.Add(new UsbFeaturePacket(chunk, ExpectAck: false));
        }

        packets.Add(new UsbFeaturePacket(SessionEndPayload(), ExpectAck: true));
        packets.Add(new UsbFeaturePacket(applyTable, ExpectAck: true));
        return packets;
    }

    public static IReadOnlyList<byte[]> BuildWirelessPerKeyReports(byte[] buf576, byte brightness = 5)
    {
        if (buf576.Length != PerKeyBufferLength)
            throw new ArgumentException($"Per-key buffer must be {PerKeyBufferLength} bytes.", nameof(buf576));

        byte clampedBrightness = Math.Clamp(brightness, (byte)1, (byte)5);
        var reports = new List<byte[]>(22);

        // 1. Activate User Light mode (0x80)
        byte[] modeReport = new byte[33];
        modeReport[0] = 0x00;
        modeReport[1] = 0x05;
        modeReport[2] = 0x10;
        modeReport[3] = 0x00;
        modeReport[4] = 0x80;
        modeReport[13] = clampedBrightness;
        modeReport[18] = 0xAA;
        modeReport[19] = 0x55;
        modeReport[32] = ComputeWirelessChecksum(modeReport);
        reports.Add(modeReport);

        // 2. Upload 576-byte per-key color buffer in 21 packets (20 x 28B + 1 x 16B)
        for (int blockIndex = 0; blockIndex < 21; blockIndex++)
        {
            int copyLen = blockIndex == 20 ? 16 : 28;
            byte[] pkt = new byte[33];
            pkt[0] = 0x00;
            pkt[1] = 0x14;
            pkt[2] = (byte)copyLen;
            pkt[3] = (byte)blockIndex;
            Array.Copy(buf576, blockIndex * 28, pkt, 4, copyLen);
            pkt[32] = ComputeWirelessChecksum(pkt);
            reports.Add(pkt);
        }

        return reports;
    }

    public static void SetPerKeyLighting(
        AjazzHidSession session,
        IReadOnlyDictionary<byte, (byte R, byte G, byte B)> keyColors,
        byte brightness = 5,
        Action<int>? onProgressPercent = null)
    {
        using var _ = HidNative.UseHighResTimer();
        byte[] buf576 = BuildPerKeyBuffer(keyColors);
        if (session.Mode == ConnectionMode.UsbWired)
        {
            var packets = BuildUsbPerKeyPackets(buf576, brightness);
            for (int i = 0; i < packets.Count; i++)
            {
                session.SendUsbFeature(packets[i].Payload64, packets[i].ExpectAck);
                onProgressPercent?.Invoke((i + 1) * 100 / packets.Count);
            }
        }
        else
        {
            var reports = BuildWirelessPerKeyReports(buf576, brightness);
            session.FlushInput();
            for (int i = 0; i < reports.Count; i++)
            {
                session.SendWireless32Reliable(reports[i], ackTimeoutMs: 100, maxAttempts: 3);
                onProgressPercent?.Invoke((i + 1) * 100 / reports.Count);
            }
        }
    }
}
