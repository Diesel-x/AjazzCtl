using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace AjazzCtl;

public static class ScreenWidgetRenderer
{
    private const int PropertyTagFrameDelay = 0x5100;

    public static List<(Bitmap Frame, int DelayMs)> LoadImageFrames(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Image file not found.", filePath);

        using Image img = Image.FromFile(filePath);
        var dimension = new FrameDimension(img.FrameDimensionsList[0]);
        int totalFrames = img.GetFrameCount(dimension);
        int count = Math.Clamp(totalFrames, 1, AjazzProtocol.ScreenMaxFrames);

        int[] delays = ExtractFrameDelaysMs(img, count);
        var result = new List<(Bitmap Frame, int DelayMs)>(count);

        for (int i = 0; i < count; i++)
        {
            if (totalFrames > 1)
                img.SelectActiveFrame(dimension, i);

            Bitmap scaled = ResizeCenterCropTo128(img);
            result.Add((scaled, delays[i]));
        }

        return result;
    }

    public static List<(Bitmap Frame, int DelayMs)> OptimizeFrames(
        IReadOnlyList<(Bitmap Frame, int DelayMs)> source,
        int maxFrames)
    {
        var output = new List<(Bitmap Frame, int DelayMs)>();
        if (source.Count == 0)
            return output;

        int limit = Math.Clamp(maxFrames, 1, AjazzProtocol.ScreenMaxFrames);
        if (source.Count <= limit)
        {
            foreach (var (frame, delay) in source)
                output.Add((new Bitmap(frame), delay));
            return output;
        }

        double step = (double)source.Count / limit;
        for (int i = 0; i < limit; i++)
        {
            int start = Math.Clamp((int)Math.Floor(i * step), 0, source.Count - 1);
            int end = Math.Clamp((int)Math.Floor((i + 1) * step), start + 1, source.Count);

            int accumulatedDelay = 0;
            for (int j = start; j < end; j++)
                accumulatedDelay += source[j].DelayMs;

            int scaledDelay = Math.Clamp(accumulatedDelay, 30, 500);
            output.Add((new Bitmap(source[start].Frame), scaledDelay));
        }

        return output;
    }

    private static int[] ExtractFrameDelaysMs(Image img, int count)
    {
        int[] delays = new int[count];
        Array.Fill(delays, 100);

        if (!img.PropertyIdList.Contains(PropertyTagFrameDelay))
            return delays;

        PropertyItem? prop = img.GetPropertyItem(PropertyTagFrameDelay);
        if (prop?.Value is null || prop.Value.Length < 4)
            return delays;

        int available = prop.Value.Length / 4;
        for (int i = 0; i < Math.Min(count, available); i++)
        {
            int cs = BitConverter.ToInt32(prop.Value, i * 4);
            int ms = cs * 10;
            delays[i] = Math.Clamp(ms <= 0 ? 100 : ms, 30, 500);
        }

        return delays;
    }

    public static Bitmap ResizeCenterCropTo128(Image source)
    {
        var bmp = new Bitmap(AjazzProtocol.ScreenWidth, AjazzProtocol.ScreenHeight, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bmp);
        g.Clear(Color.Black);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;

        float scale = Math.Max(
            (float)AjazzProtocol.ScreenWidth / source.Width,
            (float)AjazzProtocol.ScreenHeight / source.Height);

        float drawW = source.Width * scale;
        float drawH = source.Height * scale;
        float drawX = (AjazzProtocol.ScreenWidth - drawW) / 2f;
        float drawY = (AjazzProtocol.ScreenHeight - drawH) / 2f;

        g.DrawImage(source, drawX, drawY, drawW, drawH);
        return bmp;
    }

    public static Bitmap RenderSystemWidget(string? label, DateTime now)
    {
        var (cpuPct, ramPct, usedGb, totalGb) = ReadSystemMetrics();
        return RenderSystemWidget(label, now, cpuPct, ramPct, usedGb, totalGb);
    }

    public static Bitmap RenderSystemWidget(string? label, DateTime now, int cpuPct, int ramPct, double usedGb, double totalGb)
    {
        TimeSpan uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);

        var bmp = new Bitmap(AjazzProtocol.ScreenWidth, AjazzProtocol.ScreenHeight, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var bgBrush = new LinearGradientBrush(
            new Rectangle(0, 0, 128, 128),
            Color.FromArgb(14, 18, 28),
            Color.FromArgb(8, 10, 16),
            LinearGradientMode.Vertical);
        g.FillRectangle(bgBrush, 0, 0, 128, 128);

        using var borderPen = new Pen(Color.FromArgb(46, 62, 92), 1);
        g.DrawRectangle(borderPen, 0, 0, 127, 127);

        string headerText = string.IsNullOrWhiteSpace(label) ? "AK820 PRO" : label.Trim().ToUpperInvariant();
        using var headerFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        using var accentBrush = new SolidBrush(Color.FromArgb(90, 200, 255));
        g.DrawString(headerText, headerFont, accentBrush, new RectangleF(6, 4, 116, 14));

        using var timeFont = new Font("Consolas", 18f, FontStyle.Bold);
        using var whiteBrush = new SolidBrush(Color.White);
        g.DrawString(now.ToString("HH:mm"), timeFont, whiteBrush, 6, 17);

        using var secFont = new Font("Consolas", 9f, FontStyle.Bold);
        using var mutedBrush = new SolidBrush(Color.FromArgb(165, 178, 204));
        g.DrawString(now.ToString(":ss"), secFont, mutedBrush, 82, 26);

        using var dateFont = new Font("Segoe UI", 8f, FontStyle.Regular);
        g.DrawString(now.ToString("ddd dd.MM.yyyy"), dateFont, mutedBrush, 7, 45);

        using var dividerPen = new Pen(Color.FromArgb(38, 50, 74), 1);
        g.DrawLine(dividerPen, 6, 62, 121, 62);

        DrawMetricBar(g, "CPU", $"{cpuPct}%", cpuPct / 100f, 68, Color.FromArgb(66, 196, 255));
        DrawMetricBar(g, "RAM", $"{ramPct}%", ramPct / 100f, 90, Color.FromArgb(124, 232, 150));

        using var footerFont = new Font("Consolas", 7f, FontStyle.Regular);
        string footer = $"UP {(int)uptime.TotalHours}h{uptime.Minutes:D2}m  {usedGb:F1}/{totalGb:F0}G";
        g.DrawString(footer, footerFont, mutedBrush, 6, 112);

        return bmp;
    }

    private static void DrawMetricBar(Graphics g, string name, string valueText, float ratio, int y, Color fillColor)
    {
        using var labelFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        using var whiteBrush = new SolidBrush(Color.FromArgb(230, 235, 245));
        g.DrawString(name, labelFont, whiteBrush, 6, y);

        var rightFormat = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString(valueText, labelFont, whiteBrush, new RectangleF(6, y, 115, 14), rightFormat);

        int barY = y + 13;
        using var trackBrush = new SolidBrush(Color.FromArgb(30, 38, 56));
        g.FillRectangle(trackBrush, 7, barY, 114, 5);

        int fillW = Math.Clamp((int)Math.Round(114 * ratio), 0, 114);
        if (fillW > 0)
        {
            using var fillBrush = new SolidBrush(fillColor);
            g.FillRectangle(fillBrush, 7, barY, fillW, 5);
        }
    }

    public static (int CpuPct, int RamPct, double UsedGb, double TotalGb) ReadSystemMetrics()
    {
        int cpuPct = SampleCpuUsagePercent();
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem) && mem.ullTotalPhys > 0)
        {
            double totalGb = mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
            double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / (1024.0 * 1024.0 * 1024.0);
            return (cpuPct, (int)mem.dwMemoryLoad, usedGb, totalGb);
        }
        return (cpuPct, 0, 0, 0);
    }

    private static int SampleCpuUsagePercent()
    {
        if (!GetSystemTimes(out FILETIME idle1, out FILETIME kernel1, out FILETIME user1))
            return 0;

        Thread.Sleep(150);

        if (!GetSystemTimes(out FILETIME idle2, out FILETIME kernel2, out FILETIME user2))
            return 0;

        ulong idle = ToUInt64(idle2) - ToUInt64(idle1);
        ulong kernel = ToUInt64(kernel2) - ToUInt64(kernel1);
        ulong user = ToUInt64(user2) - ToUInt64(user1);
        ulong total = kernel + user;
        if (total == 0)
            return 0;

        double usage = (double)(total - idle) * 100.0 / total;
        return Math.Clamp((int)Math.Round(usage), 0, 100);
    }

    private static ulong ToUInt64(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
