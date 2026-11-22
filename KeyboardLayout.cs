using System;
using System.Collections.Generic;

namespace AjazzCtl;

public enum KeyCategory
{
    Function,
    Number,
    Letter,
    Modifier,
    Navigation,
    Arrow
}

public sealed record KeyDefinition(
    string Name,
    byte KeyCode,
    byte LightIndex,
    int X,
    int Y,
    int Width,
    int Height,
    int Row,
    KeyCategory Category,
    bool IsWasd = false);

public static class KeyboardLayout
{
    public const int CanvasWidth = 640;
    public const int CanvasHeight = 260;

    // Visual-only coordinates on the 640x260 layout matching the physical AK820 Pro top-right area
    public static readonly (int X, int Y, int Size) KnobRect = (588, 8, 32);
    public static readonly (int X, int Y, int Width, int Height) ScreenRect = (584, 174, 36, 34);

    public static readonly IReadOnlyList<KeyDefinition> Keys =
    [
        // Row 0 (14 keys)
        new("Esc",    0x29,   1,  10,  10, 28, 28, 0, KeyCategory.Function),
        new("F1",     0x3A,   2,  56,  10, 28, 28, 0, KeyCategory.Function),
        new("F2",     0x3B,   3,  94,  10, 28, 28, 0, KeyCategory.Function),
        new("F3",     0x3C,   4, 132,  10, 28, 28, 0, KeyCategory.Function),
        new("F4",     0x3D,   5, 170,  10, 28, 28, 0, KeyCategory.Function),
        new("F5",     0x3E,   6, 217,  10, 28, 28, 0, KeyCategory.Function),
        new("F6",     0x3F,   7, 254,  10, 28, 28, 0, KeyCategory.Function),
        new("F7",     0x40,   8, 292,  10, 28, 28, 0, KeyCategory.Function),
        new("F8",     0x41,   9, 330,  10, 28, 28, 0, KeyCategory.Function),
        new("F9",     0x42,  10, 377,  10, 28, 28, 0, KeyCategory.Function),
        new("F10",    0x43,  11, 415,  10, 28, 28, 0, KeyCategory.Function),
        new("F11",    0x44,  12, 453,  10, 28, 28, 0, KeyCategory.Function),
        new("F12",    0x45,  13, 491,  10, 28, 28, 0, KeyCategory.Function),
        new("Del",    0x4C, 119, 538,  10, 28, 28, 0, KeyCategory.Navigation),

        // Row 1 (15 keys)
        new("~`",     0x35,  19,  10,  57, 28, 28, 1, KeyCategory.Number),
        new("1",      0x1E,  20,  47,  57, 28, 28, 1, KeyCategory.Number),
        new("2",      0x1F,  21,  85,  57, 28, 28, 1, KeyCategory.Number),
        new("3",      0x20,  22, 123,  57, 28, 28, 1, KeyCategory.Number),
        new("4",      0x21,  23, 161,  57, 28, 28, 1, KeyCategory.Number),
        new("5",      0x22,  24, 198,  57, 28, 28, 1, KeyCategory.Number),
        new("6",      0x23,  25, 236,  57, 28, 28, 1, KeyCategory.Number),
        new("7",      0x24,  26, 274,  57, 28, 28, 1, KeyCategory.Number),
        new("8",      0x25,  27, 312,  57, 28, 28, 1, KeyCategory.Number),
        new("9",      0x26,  28, 349,  57, 28, 28, 1, KeyCategory.Number),
        new("0",      0x27,  29, 387,  57, 28, 28, 1, KeyCategory.Number),
        new("-_",     0x2D,  30, 425,  57, 28, 28, 1, KeyCategory.Number),
        new("+=",     0x2E,  31, 462,  57, 28, 28, 1, KeyCategory.Number),
        new("Bksp",   0x2A, 103, 500,  57, 66, 28, 1, KeyCategory.Modifier),
        new("Home",   0x4A, 117, 592,  57, 28, 28, 1, KeyCategory.Navigation),

        // Row 2 (15 keys)
        new("Tab",    0x2B,  37,  10,  96, 46, 28, 2, KeyCategory.Modifier),
        new("Q",      0x14,  38,  66,  96, 28, 28, 2, KeyCategory.Letter),
        new("W",      0x1A,  39, 104,  96, 28, 28, 2, KeyCategory.Letter, IsWasd: true),
        new("E",      0x08,  40, 142,  96, 28, 28, 2, KeyCategory.Letter),
        new("R",      0x15,  41, 180,  96, 28, 28, 2, KeyCategory.Letter),
        new("T",      0x17,  42, 217,  96, 28, 28, 2, KeyCategory.Letter),
        new("Y",      0x1C,  43, 255,  96, 28, 28, 2, KeyCategory.Letter),
        new("U",      0x18,  44, 293,  96, 28, 28, 2, KeyCategory.Letter),
        new("I",      0x0C,  45, 331,  96, 28, 28, 2, KeyCategory.Letter),
        new("O",      0x12,  46, 368,  96, 28, 28, 2, KeyCategory.Letter),
        new("P",      0x13,  47, 406,  96, 28, 28, 2, KeyCategory.Letter),
        new("[{",     0x2F,  48, 444,  96, 28, 28, 2, KeyCategory.Letter),
        new("]}",     0x30,  49, 481,  96, 28, 28, 2, KeyCategory.Letter),
        new("\\|",    0x31,  67, 519,  96, 47, 28, 2, KeyCategory.Modifier),
        new("PgUp",   0x4B, 118, 592,  96, 28, 28, 2, KeyCategory.Navigation),

        // Row 3 (14 keys)
        new("Caps",   0x39,  55,  10, 133, 55, 28, 3, KeyCategory.Modifier),
        new("A",      0x04,  56,  75, 133, 28, 28, 3, KeyCategory.Letter, IsWasd: true),
        new("S",      0x16,  57, 113, 133, 28, 28, 3, KeyCategory.Letter, IsWasd: true),
        new("D",      0x07,  58, 151, 133, 28, 28, 3, KeyCategory.Letter, IsWasd: true),
        new("F",      0x09,  59, 189, 133, 28, 28, 3, KeyCategory.Letter),
        new("G",      0x0A,  60, 227, 133, 28, 28, 3, KeyCategory.Letter),
        new("H",      0x0B,  61, 265, 133, 28, 28, 3, KeyCategory.Letter),
        new("J",      0x0D,  62, 302, 133, 28, 28, 3, KeyCategory.Letter),
        new("K",      0x0E,  63, 340, 133, 28, 28, 3, KeyCategory.Letter),
        new("L",      0x0F,  64, 377, 133, 28, 28, 3, KeyCategory.Letter),
        new(";:",     0x33,  65, 415, 133, 28, 28, 3, KeyCategory.Letter),
        new("'\"",    0x34,  66, 453, 133, 28, 28, 3, KeyCategory.Letter),
        new("Enter",  0x28,  85, 491, 133, 75, 28, 3, KeyCategory.Modifier),
        new("PgDn",   0x4E, 121, 592, 133, 28, 28, 3, KeyCategory.Navigation),

        // Row 4 (13 keys)
        new("Shift",  0xE1,  73,  10, 171, 75, 28, 4, KeyCategory.Modifier),
        new("Z",      0x1D,  74,  94, 171, 28, 28, 4, KeyCategory.Letter),
        new("X",      0x1B,  75, 132, 171, 28, 28, 4, KeyCategory.Letter),
        new("C",      0x06,  76, 170, 171, 28, 28, 4, KeyCategory.Letter),
        new("V",      0x19,  77, 207, 171, 28, 28, 4, KeyCategory.Letter),
        new("B",      0x05,  78, 245, 171, 28, 28, 4, KeyCategory.Letter),
        new("N",      0x11,  79, 283, 171, 28, 28, 4, KeyCategory.Letter),
        new("M",      0x10,  80, 321, 171, 28, 28, 4, KeyCategory.Letter),
        new(",<",     0x36,  81, 358, 171, 28, 28, 4, KeyCategory.Letter),
        new(".>",     0x37,  82, 396, 171, 28, 28, 4, KeyCategory.Letter),
        new("/?",     0x38,  83, 434, 171, 28, 28, 4, KeyCategory.Letter),
        new("Shift",  0xE5,  84, 472, 171, 55, 28, 4, KeyCategory.Modifier),
        new("↑",      0x52, 101, 546, 180, 28, 28, 4, KeyCategory.Arrow),

        // Row 5 (10 keys)
        new("Ctrl",   0xE0,  91,  10, 209, 37, 28, 5, KeyCategory.Modifier),
        new("Win",    0xE3,  92,  57, 209, 37, 28, 5, KeyCategory.Modifier),
        new("Alt",    0xE2,  93, 104, 209, 37, 28, 5, KeyCategory.Modifier),
        new("Space",  0x2C,  94, 151, 209,225, 28, 5, KeyCategory.Modifier),
        new("Alt",    0xE6,  95, 387, 209, 28, 28, 5, KeyCategory.Modifier),
        new("Fn",     0xAF,  96, 425, 209, 28, 28, 5, KeyCategory.Modifier),
        new("Ctrl",   0xE4,  98, 463, 209, 28, 28, 5, KeyCategory.Modifier),
        new("←",      0x50,  99, 509, 218, 28, 28, 5, KeyCategory.Arrow),
        new("↓",      0x51, 100, 546, 218, 28, 28, 5, KeyCategory.Arrow),
        new("→",      0x4F, 102, 584, 218, 28, 28, 5, KeyCategory.Arrow)
    ];

    public static Dictionary<byte, (byte R, byte G, byte B)> BuildPresetColors(
        string preset,
        (byte R, byte G, byte B)? primaryColor = null,
        (byte R, byte G, byte B)? wasdColor = null,
        (byte R, byte G, byte B)? arrowColor = null)
    {
        var map = new Dictionary<byte, (byte R, byte G, byte B)>(Keys.Count);
        string p = preset.Trim().ToLowerInvariant();
        (byte R, byte G, byte B) mainCol = primaryColor ?? ((byte)0, (byte)180, (byte)255);
        (byte R, byte G, byte B) wasdCol = wasdColor ?? ((byte)255, (byte)42, (byte)85);
        (byte R, byte G, byte B) arrCol = arrowColor ?? ((byte)255, (byte)200, (byte)0);

        static (byte R, byte G, byte B) Rgb(byte r, byte g, byte b) => (r, g, b);

        foreach (var k in Keys)
        {
            (byte R, byte G, byte B) col = p switch
            {
                "cyberpunk" => k.IsWasd ? Rgb(0, 255, 240)
                    : k.Category == KeyCategory.Arrow ? Rgb(255, 220, 0)
                    : k.Category is KeyCategory.Modifier or KeyCategory.Function or KeyCategory.Navigation ? Rgb(255, 0, 140)
                    : Rgb(0, 170, 255),
                "gamer" => k.IsWasd ? wasdCol
                    : k.Category == KeyCategory.Arrow ? arrCol
                    : k.Category == KeyCategory.Function ? Rgb(120, 90, 255)
                    : k.Category == KeyCategory.Number ? Rgb(0, 220, 160)
                    : Rgb(30, 80, 160),
                "gradient" => LerpColor(Rgb(255, 50, 120), Rgb(0, 210, 255), Math.Clamp((k.X - 10) / 574.0, 0.0, 1.0)),
                "rainbow" => k.Row switch
                {
                    0 => Rgb(255, 40, 60),
                    1 => Rgb(255, 140, 0),
                    2 => Rgb(240, 220, 0),
                    3 => Rgb(0, 230, 110),
                    4 => Rgb(0, 180, 255),
                    _ => Rgb(170, 60, 255)
                },
                "off" => Rgb(0, 0, 0),
                _ => mainCol
            };
            map[k.LightIndex] = col;
        }

        return map;
    }

    private static (byte R, byte G, byte B) LerpColor((byte R, byte G, byte B) a, (byte R, byte G, byte B) b, double t)
    {
        byte r = (byte)System.Math.Round(a.R + (b.R - a.R) * t);
        byte g = (byte)System.Math.Round(a.G + (b.G - a.G) * t);
        byte bl = (byte)System.Math.Round(a.B + (b.B - a.B) * t);
        return (r, g, bl);
    }
}
