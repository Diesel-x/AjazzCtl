using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AjazzCtl;

public enum KeySoundProfile
{
    Thocky,
    ClickyBlue,
    SilentTactile,
    Typewriter,
    BubblePop,
    SciFi,
    CustomWav
}

public enum KeySoundKind
{
    Normal,
    Space,
    Enter,
    Backspace
}

public sealed unsafe class KeySoundEngine : IDisposable
{
    public const int SampleRate = 44100;
    private const int BufferCount = 4;
    private const int BufferSamples = 512;
    private const int MaxVoices = 16;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const uint WAVE_MAPPER = unchecked((uint)-1);
    private const uint CALLBACK_EVENT = 0x00050000;
    private const uint WHDR_DONE = 0x00000001;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private sealed class ActiveVoice
    {
        public required float[] Samples;
        public float Position;
        public float Rate;
        public float Gain;
    }

    private sealed class WaveBufferSlot
    {
        public required short[] Samples;
        public GCHandle SamplesPin;
        public IntPtr HeaderPtr;
    }

    private readonly object _voiceLock = new();
    private readonly object _lifecycleLock = new();
    private readonly List<ActiveVoice> _voices = new(MaxVoices);
    private readonly HashSet<int> _pressedVkCodes = new();
    private readonly Random _rng = new();

    private Dictionary<KeySoundProfile, Dictionary<KeySoundKind, float[]>> _bank = BuildDefaultBank();
    private float[]? _customWavSamples;

    private LowLevelKeyboardProc? _hookProc;
    private IntPtr _hookHandle;

    private IntPtr _hWaveOut;
    private AutoResetEvent? _bufferEvent;
    private WaveBufferSlot[]? _slots;
    private Thread? _audioThread;
    private volatile bool _audioRunning;
    private bool _disposed;

    public bool IsEnabled { get; private set; }
    public KeySoundProfile Profile { get; set; } = KeySoundProfile.Thocky;
    public float Volume { get; set; } = 0.70f;
    public bool UseSpecialKeys { get; set; } = true;
    public bool PitchVariation { get; set; } = true;
    public string? CustomWavPath { get; private set; }
    public bool HasCustomWav => _customWavSamples is { Length: > 0 };

    public void Start()
    {
        ThrowIfDisposed();
        lock (_lifecycleLock)
        {
            EnsureAudioStarted();
            if (_hookHandle == IntPtr.Zero)
            {
                _hookProc = HookCallback;
                IntPtr hMod = GetModuleHandleW(null);
                _hookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, _hookProc, hMod, 0);
                if (_hookHandle == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new Win32Exception(err, $"Не удалось установить глобальный перехват клавиш (ошибка {err}).");
                }
            }
            IsEnabled = true;
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            IsEnabled = false;
            if (_hookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }
            lock (_pressedVkCodes)
            {
                _pressedVkCodes.Clear();
            }
            StopAudioInternal();
        }
    }

    public void PlayTest(KeySoundKind kind)
    {
        ThrowIfDisposed();
        lock (_lifecycleLock)
        {
            EnsureAudioStarted();
        }
        TriggerVoice(kind);
    }

    public void LoadCustomWav(string path)
    {
        float[] samples = LoadWavSamples(path);
        lock (_voiceLock)
        {
            _customWavSamples = samples;
            CustomWavPath = path;
        }
    }

    private void EnsureAudioStarted()
    {
        if (_audioRunning)
            return;

        _bufferEvent = new AutoResetEvent(false);
        var wfx = new WAVEFORMATEX
        {
            wFormatTag = 1,
            nChannels = 1,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = 16,
            nBlockAlign = 2,
            nAvgBytesPerSec = SampleRate * 2,
            cbSize = 0
        };

        int rc = waveOutOpen(
            out _hWaveOut,
            WAVE_MAPPER,
            ref wfx,
            _bufferEvent.SafeWaitHandle.DangerousGetHandle(),
            IntPtr.Zero,
            CALLBACK_EVENT);

        if (rc != 0)
        {
            _bufferEvent.Dispose();
            _bufferEvent = null;
            _hWaveOut = IntPtr.Zero;
            throw new IOException($"Ошибка открытия аудиоустройства waveOutOpen (код {rc}).");
        }

        _slots = new WaveBufferSlot[BufferCount];
        int hdrSize = sizeof(WAVEHDR);
        for (int i = 0; i < BufferCount; i++)
        {
            short[] pcm = new short[BufferSamples];
            GCHandle pin = GCHandle.Alloc(pcm, GCHandleType.Pinned);
            IntPtr hdrPtr = Marshal.AllocHGlobal(hdrSize);
            var hdr = (WAVEHDR*)hdrPtr;
            *hdr = default;
            hdr->lpData = pin.AddrOfPinnedObject();
            hdr->dwBufferLength = (uint)(BufferSamples * sizeof(short));

            _slots[i] = new WaveBufferSlot
            {
                Samples = pcm,
                SamplesPin = pin,
                HeaderPtr = hdrPtr
            };

            waveOutPrepareHeader(_hWaveOut, hdrPtr, hdrSize);
            waveOutWrite(_hWaveOut, hdrPtr, hdrSize);
        }

        _audioRunning = true;
        _audioThread = new Thread(AudioLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "AjazzKeySoundAudio"
        };
        _audioThread.Start();
    }

    private void StopAudioInternal()
    {
        _audioRunning = false;
        _bufferEvent?.Set();

        if (_audioThread is not null && _audioThread.IsAlive)
        {
            _audioThread.Join(500);
        }
        _audioThread = null;

        if (_hWaveOut != IntPtr.Zero)
        {
            waveOutReset(_hWaveOut);
            if (_slots is not null)
            {
                int hdrSize = sizeof(WAVEHDR);
                foreach (var slot in _slots)
                {
                    if (slot.HeaderPtr != IntPtr.Zero)
                    {
                        waveOutUnprepareHeader(_hWaveOut, slot.HeaderPtr, hdrSize);
                        Marshal.FreeHGlobal(slot.HeaderPtr);
                        slot.HeaderPtr = IntPtr.Zero;
                    }
                    if (slot.SamplesPin.IsAllocated)
                        slot.SamplesPin.Free();
                }
            }
            waveOutClose(_hWaveOut);
            _hWaveOut = IntPtr.Zero;
        }

        _slots = null;
        _bufferEvent?.Dispose();
        _bufferEvent = null;

        lock (_voiceLock)
        {
            _voices.Clear();
        }
    }

    private void AudioLoop()
    {
        int hdrSize = sizeof(WAVEHDR);
        while (_audioRunning)
        {
            _bufferEvent?.WaitOne(40);
            if (!_audioRunning || _slots is null || _hWaveOut == IntPtr.Zero)
                break;

            foreach (var slot in _slots)
            {
                if (!_audioRunning)
                    break;
                var hdr = (WAVEHDR*)slot.HeaderPtr;
                if ((hdr->dwFlags & WHDR_DONE) != 0)
                {
                    MixIntoBuffer(slot.Samples);
                    waveOutWrite(_hWaveOut, slot.HeaderPtr, hdrSize);
                }
            }
        }
    }

    private void MixIntoBuffer(short[] dst)
    {
        Array.Clear(dst);
        lock (_voiceLock)
        {
            if (_voices.Count == 0)
                return;

            Span<float> mix = stackalloc float[dst.Length];
            mix.Clear();

            for (int v = _voices.Count - 1; v >= 0; v--)
            {
                var voice = _voices[v];
                float[] src = voice.Samples;
                float pos = voice.Position;
                float rate = voice.Rate;
                float gain = voice.Gain;

                for (int i = 0; i < mix.Length; i++)
                {
                    int idx = (int)pos;
                    if (idx >= src.Length - 1)
                    {
                        if (idx < src.Length)
                            mix[i] += src[idx] * gain;
                        pos = src.Length;
                        break;
                    }

                    float frac = pos - idx;
                    float s = src[idx] + (src[idx + 1] - src[idx]) * frac;
                    mix[i] += s * gain;
                    pos += rate;
                }

                voice.Position = pos;
                if ((int)pos >= src.Length)
                {
                    _voices.RemoveAt(v);
                }
            }

            for (int i = 0; i < mix.Length; i++)
            {
                float sample = MathF.Tanh(mix[i]);
                dst[i] = (short)Math.Clamp((int)(sample * 32767f), -32768, 32767);
            }
        }
    }

    private void TriggerVoice(KeySoundKind kind)
    {
        float vol = Math.Clamp(Volume, 0f, 1f);
        if (vol <= 0.001f)
            return;

        float[]? samples = ResolveSamples(kind);
        if (samples is null || samples.Length == 0)
            return;

        float rate = 1.0f;
        lock (_voiceLock)
        {
            if (PitchVariation)
                rate = 0.96f + (float)_rng.NextDouble() * 0.08f;

            if (_voices.Count >= MaxVoices)
                _voices.RemoveAt(0);

            _voices.Add(new ActiveVoice
            {
                Samples = samples,
                Position = 0f,
                Rate = rate,
                Gain = vol
            });
        }
    }

    private float[]? ResolveSamples(KeySoundKind kind)
    {
        if (Profile == KeySoundProfile.CustomWav && _customWavSamples is { Length: > 0 })
            return _customWavSamples;

        var profileKey = Profile == KeySoundProfile.CustomWav ? KeySoundProfile.Thocky : Profile;
        if (_bank.TryGetValue(profileKey, out var map))
        {
            if (map.TryGetValue(kind, out var s))
                return s;
            if (map.TryGetValue(KeySoundKind.Normal, out var fallback))
                return fallback;
        }
        return null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsEnabled)
        {
            int msg = wParam.ToInt32();
            int vkCode = Marshal.ReadInt32(lParam);

            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                bool isFirstPress;
                lock (_pressedVkCodes)
                {
                    isFirstPress = _pressedVkCodes.Add(vkCode);
                }

                if (isFirstPress)
                {
                    KeySoundKind kind = KeySoundKind.Normal;
                    if (UseSpecialKeys)
                    {
                        kind = vkCode switch
                        {
                            0x20 => KeySoundKind.Space,
                            0x0D => KeySoundKind.Enter,
                            0x08 => KeySoundKind.Backspace,
                            _ => KeySoundKind.Normal
                        };
                    }
                    TriggerVoice(kind);
                }
            }
            else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
            {
                lock (_pressedVkCodes)
                {
                    _pressedVkCodes.Remove(vkCode);
                }
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static Dictionary<KeySoundProfile, Dictionary<KeySoundKind, float[]>> BuildDefaultBank()
    {
        var dict = new Dictionary<KeySoundProfile, Dictionary<KeySoundKind, float[]>>();
        foreach (KeySoundProfile p in Enum.GetValues<KeySoundProfile>())
        {
            if (p != KeySoundProfile.CustomWav)
                dict[p] = SynthesizeProfile(p);
        }
        return dict;
    }

    public static Dictionary<KeySoundKind, float[]> SynthesizeProfile(KeySoundProfile profile)
    {
        return profile switch
        {
            KeySoundProfile.Thocky => new Dictionary<KeySoundKind, float[]>
            {
                [KeySoundKind.Normal] = SynthThock(freq1: 215f, freq2: 520f, clickFreq: 1900f, durationMs: 65, noiseMix: 0.22f, gain: 0.90f),
                [KeySoundKind.Space] = SynthThock(freq1: 145f, freq2: 340f, clickFreq: 1200f, durationMs: 90, noiseMix: 0.28f, gain: 1.00f),
                [KeySoundKind.Enter] = SynthThock(freq1: 170f, freq2: 410f, clickFreq: 1550f, durationMs: 80, noiseMix: 0.25f, gain: 0.95f),
                [KeySoundKind.Backspace] = SynthThock(freq1: 245f, freq2: 590f, clickFreq: 2100f, durationMs: 58, noiseMix: 0.20f, gain: 0.88f)
            },
            KeySoundProfile.ClickyBlue => new Dictionary<KeySoundKind, float[]>
            {
                [KeySoundKind.Normal] = SynthClicky(clickFreq: 4200f, bodyFreq: 310f, durationMs: 48, gain: 0.85f),
                [KeySoundKind.Space] = SynthClicky(clickFreq: 3100f, bodyFreq: 210f, durationMs: 68, gain: 0.92f),
                [KeySoundKind.Enter] = SynthClicky(clickFreq: 3650f, bodyFreq: 255f, durationMs: 60, gain: 0.90f),
                [KeySoundKind.Backspace] = SynthClicky(clickFreq: 4500f, bodyFreq: 350f, durationMs: 45, gain: 0.82f)
            },
            KeySoundProfile.SilentTactile => new Dictionary<KeySoundKind, float[]>
            {
                [KeySoundKind.Normal] = SynthSilent(freq: 155f, durationMs: 42, gain: 0.65f),
                [KeySoundKind.Space] = SynthSilent(freq: 115f, durationMs: 58, gain: 0.72f),
                [KeySoundKind.Enter] = SynthSilent(freq: 132f, durationMs: 50, gain: 0.68f),
                [KeySoundKind.Backspace] = SynthSilent(freq: 175f, durationMs: 38, gain: 0.62f)
            },
            KeySoundProfile.Typewriter => new Dictionary<KeySoundKind, float[]>
            {
                [KeySoundKind.Normal] = SynthTypewriterStrike(freq: 1150f, clackFreq: 2800f, durationMs: 75, gain: 0.90f),
                [KeySoundKind.Space] = SynthTypewriterStrike(freq: 620f, clackFreq: 1650f, durationMs: 95, gain: 0.95f),
                [KeySoundKind.Enter] = SynthTypewriterBell(gain: 0.85f),
                [KeySoundKind.Backspace] = SynthTypewriterStrike(freq: 1380f, clackFreq: 3100f, durationMs: 62, gain: 0.85f)
            },
            KeySoundProfile.BubblePop => new Dictionary<KeySoundKind, float[]>
            {
                [KeySoundKind.Normal] = SynthBubble(startFreq: 370f, endFreq: 760f, durationMs: 55, gain: 0.82f),
                [KeySoundKind.Space] = SynthBubble(startFreq: 250f, endFreq: 520f, durationMs: 75, gain: 0.90f),
                [KeySoundKind.Enter] = SynthBubble(startFreq: 310f, endFreq: 680f, durationMs: 68, gain: 0.86f),
                [KeySoundKind.Backspace] = SynthBubble(startFreq: 480f, endFreq: 300f, durationMs: 50, gain: 0.80f)
            },
            KeySoundProfile.SciFi => new Dictionary<KeySoundKind, float[]>
            {
                [KeySoundKind.Normal] = SynthSciFi(freq: 1180f, modFreq: 1770f, durationMs: 48, gain: 0.75f),
                [KeySoundKind.Space] = SynthSciFi(freq: 740f, modFreq: 1110f, durationMs: 68, gain: 0.85f),
                [KeySoundKind.Enter] = SynthSciFi(freq: 980f, modFreq: 1960f, durationMs: 85, gain: 0.85f),
                [KeySoundKind.Backspace] = SynthSciFi(freq: 860f, modFreq: 540f, durationMs: 48, gain: 0.75f)
            },
            _ => SynthesizeProfile(KeySoundProfile.Thocky)
        };
    }

    private static float[] SynthThock(float freq1, float freq2, float clickFreq, int durationMs, float noiseMix, float gain)
    {
        int len = SampleRate * durationMs / 1000;
        float[] buf = new float[len];
        uint seed = 0x51A7F00D;
        float lpNoise = 0f;

        for (int i = 0; i < len; i++)
        {
            float t = i / (float)SampleRate;
            float envBody = MathF.Exp(-t * 58f);
            float envAttack = MathF.Exp(-t * 420f);
            float sweep = freq1 * (1f + 0.55f * MathF.Exp(-t * 180f));

            seed = seed * 1664525u + 1013904223u;
            float white = ((seed >> 16) / 32768f) - 1f;
            lpNoise = lpNoise * 0.65f + white * 0.35f;

            float s = MathF.Sin(2f * MathF.PI * sweep * t) * 0.68f * envBody
                    + MathF.Sin(2f * MathF.PI * freq2 * t) * 0.25f * MathF.Exp(-t * 115f)
                    + MathF.Sin(2f * MathF.PI * clickFreq * t) * 0.22f * envAttack
                    + lpNoise * noiseMix * MathF.Exp(-t * 220f);

            float ramp = Math.Min(1f, i / 12f);
            buf[i] = Math.Clamp(s * ramp * gain, -1f, 1f);
        }
        return buf;
    }

    private static float[] SynthClicky(float clickFreq, float bodyFreq, int durationMs, float gain)
    {
        int len = SampleRate * durationMs / 1000;
        float[] buf = new float[len];
        uint seed = 0x9E3779B9;
        int secondClickSample = SampleRate * 4 / 1000; // crisp double click jacket transient

        for (int i = 0; i < len; i++)
        {
            float t = i / (float)SampleRate;
            seed = seed * 1664525u + 1013904223u;
            float white = ((seed >> 16) / 32768f) - 1f;

            float click1 = MathF.Sin(2f * MathF.PI * clickFreq * t) * MathF.Exp(-t * 520f);
            float click2 = i >= secondClickSample
                ? MathF.Sin(2f * MathF.PI * (clickFreq * 1.45f) * (t - 0.004f)) * MathF.Exp(-(t - 0.004f) * 650f) * 0.75f
                : 0f;
            float body = MathF.Sin(2f * MathF.PI * bodyFreq * t) * MathF.Exp(-t * 85f) * 0.42f;
            float burst = white * MathF.Exp(-t * 480f) * 0.32f;

            float ramp = Math.Min(1f, i / 6f);
            buf[i] = Math.Clamp((click1 * 0.55f + click2 + body + burst) * ramp * gain, -1f, 1f);
        }
        return buf;
    }

    private static float[] SynthSilent(float freq, int durationMs, float gain)
    {
        int len = SampleRate * durationMs / 1000;
        float[] buf = new float[len];
        uint seed = 0x12345678;
        float lp = 0f;

        for (int i = 0; i < len; i++)
        {
            float t = i / (float)SampleRate;
            seed = seed * 1664525u + 1013904223u;
            float white = ((seed >> 16) / 32768f) - 1f;
            lp = lp * 0.85f + white * 0.15f;

            float bump = MathF.Sin(2f * MathF.PI * freq * t) * MathF.Exp(-t * 95f) * 0.72f;
            float rub = lp * MathF.Exp(-t * 160f) * 0.25f;
            float ramp = Math.Min(1f, i / 20f);
            buf[i] = Math.Clamp((bump + rub) * ramp * gain, -1f, 1f);
        }
        return buf;
    }

    private static float[] SynthTypewriterStrike(float freq, float clackFreq, int durationMs, float gain)
    {
        int len = SampleRate * durationMs / 1000;
        float[] buf = new float[len];
        uint seed = 0xCAFEBABE;

        for (int i = 0; i < len; i++)
        {
            float t = i / (float)SampleRate;
            seed = seed * 1664525u + 1013904223u;
            float white = ((seed >> 16) / 32768f) - 1f;

            float metal = MathF.Sin(2f * MathF.PI * freq * t) * MathF.Exp(-t * 90f) * 0.48f
                        + MathF.Sin(2f * MathF.PI * clackFreq * t) * MathF.Exp(-t * 280f) * 0.35f;
            float impact = white * MathF.Exp(-t * 340f) * 0.45f;
            float ramp = Math.Min(1f, i / 8f);
            buf[i] = Math.Clamp((metal + impact) * ramp * gain, -1f, 1f);
        }
        return buf;
    }

    private static float[] SynthTypewriterBell(float gain)
    {
        int len = SampleRate * 220 / 1000;
        float[] buf = new float[len];
        for (int i = 0; i < len; i++)
        {
            float t = i / (float)SampleRate;
            float ding = MathF.Sin(2f * MathF.PI * 2280f * t) * MathF.Exp(-t * 14f) * 0.50f
                       + MathF.Sin(2f * MathF.PI * 4560f * t) * MathF.Exp(-t * 24f) * 0.28f
                       + MathF.Sin(2f * MathF.PI * 380f * t) * MathF.Exp(-t * 120f) * 0.35f;
            float ramp = Math.Min(1f, i / 12f);
            buf[i] = Math.Clamp(ding * ramp * gain, -1f, 1f);
        }
        return buf;
    }

    private static float[] SynthBubble(float startFreq, float endFreq, int durationMs, float gain)
    {
        int len = SampleRate * durationMs / 1000;
        float[] buf = new float[len];
        double phase = 0;

        for (int i = 0; i < len; i++)
        {
            float u = i / (float)Math.Max(1, len - 1);
            float f = startFreq + (endFreq - startFreq) * u;
            phase += 2.0 * Math.PI * f / SampleRate;
            float env = MathF.Sin(MathF.PI * u) * MathF.Exp(-u * 2.1f);
            buf[i] = Math.Clamp((float)Math.Sin(phase) * env * gain, -1f, 1f);
        }
        return buf;
    }

    private static float[] SynthSciFi(float freq, float modFreq, int durationMs, float gain)
    {
        int len = SampleRate * durationMs / 1000;
        float[] buf = new float[len];

        for (int i = 0; i < len; i++)
        {
            float t = i / (float)SampleRate;
            float env = MathF.Exp(-t * 90f);
            float fm = MathF.Sin(2f * MathF.PI * modFreq * t) * 1.4f * MathF.Exp(-t * 140f);
            float s = MathF.Sin(2f * MathF.PI * freq * t + fm) * 0.65f * env
                    + MathF.Sin(2f * MathF.PI * (freq * 1.5f) * t) * 0.30f * MathF.Exp(-t * 130f);
            float ramp = Math.Min(1f, i / 10f);
            buf[i] = Math.Clamp(s * ramp * gain, -1f, 1f);
        }
        return buf;
    }

    public static float[] LoadWavSamples(string path)
    {
        using var fs = File.OpenRead(path);
        return LoadWavSamples(fs);
    }

    public static float[] LoadWavSamples(Stream stream)
    {
        using var br = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (br.ReadUInt32() != 0x46464952) // "RIFF"
            throw new InvalidDataException("Файл не является RIFF WAV.");
        br.ReadUInt32(); // file size
        if (br.ReadUInt32() != 0x45564157) // "WAVE"
            throw new InvalidDataException("Некорректный заголовок WAVE.");

        ushort audioFormat = 0;
        ushort channels = 0;
        uint srcSampleRate = 0;
        ushort bitsPerSample = 0;
        byte[]? dataBytes = null;

        while (stream.Position + 8 <= stream.Length)
        {
            uint chunkId = br.ReadUInt32();
            uint chunkSize = br.ReadUInt32();
            long nextPos = stream.Position + chunkSize + (chunkSize & 1);

            if (chunkId == 0x20746D66) // "fmt "
            {
                audioFormat = br.ReadUInt16();
                channels = br.ReadUInt16();
                srcSampleRate = br.ReadUInt32();
                br.ReadUInt32(); // byteRate
                br.ReadUInt16(); // blockAlign
                bitsPerSample = br.ReadUInt16();
            }
            else if (chunkId == 0x61746164) // "data"
            {
                int toRead = (int)Math.Min(chunkSize, Math.Max(0, stream.Length - stream.Position));
                dataBytes = br.ReadBytes(toRead);
            }

            if (nextPos > stream.Length)
                break;
            stream.Position = nextPos;
        }

        if (dataBytes is null || dataBytes.Length == 0 || channels == 0 || srcSampleRate == 0)
            throw new InvalidDataException("В WAV-файле отсутствуют аудиоданные PCM.");

        bool isFloat = audioFormat == 3;
        bool isPcm = audioFormat == 1 || audioFormat == 0xFFFE;
        if (!isPcm && !isFloat)
            throw new InvalidDataException($"Неподдерживаемый формат WAV (код {audioFormat}). Используйте PCM или IEEE Float WAV.");

        int bytesPerSample = bitsPerSample / 8;
        int frameBytes = channels * bytesPerSample;
        if (bytesPerSample == 0 || frameBytes == 0)
            throw new InvalidDataException("Некорректная разрядность WAV.");

        int frameCount = dataBytes.Length / frameBytes;
        float[] mono = new float[frameCount];

        for (int f = 0; f < frameCount; f++)
        {
            float sum = 0f;
            int baseOff = f * frameBytes;
            for (int ch = 0; ch < channels; ch++)
            {
                int off = baseOff + ch * bytesPerSample;
                float sample = bitsPerSample switch
                {
                    8 => (dataBytes[off] - 128) / 128f,
                    16 => BitConverter.ToInt16(dataBytes, off) / 32768f,
                    24 => ((sbyte)dataBytes[off + 2] << 16 | dataBytes[off + 1] << 8 | dataBytes[off]) / 8388608f,
                    32 when isFloat => BitConverter.ToSingle(dataBytes, off),
                    32 => BitConverter.ToInt32(dataBytes, off) / 2147483648f,
                    _ => throw new InvalidDataException($"Неподдерживаемая разрядность {bitsPerSample} бит.")
                };
                sum += sample;
            }
            mono[f] = Math.Clamp(sum / channels, -1f, 1f);
        }

        // Resample to 44100 Hz and cap at 0.5 sec (22050 samples)
        int maxOut = SampleRate / 2;
        if (srcSampleRate == SampleRate)
        {
            if (mono.Length <= maxOut)
                return mono;
            return mono[..maxOut];
        }

        double ratio = srcSampleRate / (double)SampleRate;
        int targetLen = Math.Min(maxOut, Math.Max(1, (int)(mono.Length / ratio)));
        float[] resampled = new float[targetLen];
        for (int i = 0; i < targetLen; i++)
        {
            double srcPos = i * ratio;
            int idx = (int)srcPos;
            double frac = srcPos - idx;
            float s0 = mono[Math.Min(idx, mono.Length - 1)];
            float s1 = mono[Math.Min(idx + 1, mono.Length - 1)];
            resampled[i] = s0 + (s1 - s0) * (float)frac;
        }
        return resampled;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(KeySoundEngine));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(
        out IntPtr phwo,
        uint uDeviceID,
        ref WAVEFORMATEX pwfx,
        IntPtr dwCallback,
        IntPtr dwInstance,
        uint fdwOpen);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr hwo);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hwo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
