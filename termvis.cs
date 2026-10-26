// DMZ TermVis - a terminal audio visualizer for Windows and macOS.
// Windows: captures system audio (WASAPI loopback) or the microphone.
// macOS:   captures the default input device (microphone, or BlackHole for system audio).
// Run:     run.bat    (Windows: compiles in memory and runs, no exe produced)
// Build:   build.bat  (Windows: termvis.exe with the C# compiler that ships with Windows)
//          release.ps1 (self-contained Windows + macOS release builds with the .NET 8 SDK)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TermVis
{
    static class Platform
    {
        public static readonly bool IsWindows = Path.DirectorySeparatorChar == '\\';
        public static readonly bool IsMac = !IsWindows && Directory.Exists("/System/Library/Frameworks/AudioToolbox.framework");
    }

    interface IAudioSource : IDisposable
    {
        int SampleRate { get; }
        int Read(Ring ring);          // drains pending audio into the ring as mono samples, returns frames read
        bool DeviceChanged();         // true when the system default device moved elsewhere
    }

    // ---------------------------------------------------------------- macOS capture (AudioQueue)

    sealed class MacAudioCapture : IAudioSource
    {
        const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
        const uint LinearPCM = 0x6C70636D;  // 'lpcm'
        const uint FloatPacked = 1 | 8;     // kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked
        const int Rate = 48000, BufferFrames = 1024, BufferCount = 4;

        [StructLayout(LayoutKind.Sequential)]
        struct StreamFormat
        {
            public double SampleRate;
            public uint FormatID, FormatFlags, BytesPerPacket, FramesPerPacket, BytesPerFrame, ChannelsPerFrame, BitsPerChannel, Reserved;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void InputCallback(IntPtr userData, IntPtr queue, IntPtr buffer, IntPtr startTime, uint packets, IntPtr packetDescs);

        [DllImport(AudioToolbox)] static extern int AudioQueueNewInput(ref StreamFormat format, InputCallback callback, IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);
        [DllImport(AudioToolbox)] static extern int AudioQueueAllocateBuffer(IntPtr queue, uint bytes, out IntPtr buffer);
        [DllImport(AudioToolbox)] static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint descCount, IntPtr descs);
        [DllImport(AudioToolbox)] static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);
        [DllImport(AudioToolbox)] static extern int AudioQueueStop(IntPtr queue, byte immediate);
        [DllImport(AudioToolbox)] static extern int AudioQueueDispose(IntPtr queue, byte immediate);

        readonly InputCallback callback; // held so the delegate outlives the native queue
        readonly object gate = new object();
        readonly float[] pending = new float[Rate];
        readonly float[] scratch = new float[BufferFrames];
        int pendingCount;
        IntPtr queue;
        volatile bool running;

        public int SampleRate { get { return Rate; } }

        public MacAudioCapture()
        {
            var fmt = new StreamFormat
            {
                SampleRate = Rate, FormatID = LinearPCM, FormatFlags = FloatPacked,
                BytesPerPacket = 4, FramesPerPacket = 1, BytesPerFrame = 4, ChannelsPerFrame = 1, BitsPerChannel = 32,
            };
            callback = OnInput;
            // A null run loop makes the queue call back on its own internal thread.
            Check(AudioQueueNewInput(ref fmt, callback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out queue), "AudioQueueNewInput");
            for (int i = 0; i < BufferCount; i++)
            {
                IntPtr buf;
                Check(AudioQueueAllocateBuffer(queue, BufferFrames * 4, out buf), "AudioQueueAllocateBuffer");
                Check(AudioQueueEnqueueBuffer(queue, buf, 0, IntPtr.Zero), "AudioQueueEnqueueBuffer");
            }
            running = true;
            Check(AudioQueueStart(queue, IntPtr.Zero), "AudioQueueStart");
        }

        static void Check(int status, string what)
        {
            if (status != 0) throw new InvalidOperationException(what + " failed: " + status);
        }

        void OnInput(IntPtr userData, IntPtr q, IntPtr buffer, IntPtr startTime, uint packets, IntPtr packetDescs)
        {
            if (!running) return;
            // AudioQueueBuffer layout: UInt32 capacity, void* data, UInt32 byteSize
            IntPtr data = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            int count = Math.Min(scratch.Length, Marshal.ReadInt32(buffer, IntPtr.Size * 2) / 4);
            if (count > 0)
            {
                Marshal.Copy(data, scratch, 0, count);
                lock (gate)
                {
                    if (pendingCount + count > pending.Length) pendingCount = 0; // reader stalled; drop old audio
                    Array.Copy(scratch, 0, pending, pendingCount, count);
                    pendingCount += count;
                }
            }
            AudioQueueEnqueueBuffer(q, buffer, 0, IntPtr.Zero);
        }

        public int Read(Ring ring)
        {
            lock (gate)
            {
                for (int i = 0; i < pendingCount; i++) ring.Push(pending[i]);
                int n = pendingCount;
                pendingCount = 0;
                return n;
            }
        }

        public bool DeviceChanged() { return false; }

        public void Dispose()
        {
            running = false;
            if (queue == IntPtr.Zero) return;
            AudioQueueStop(queue, 1);
            AudioQueueDispose(queue, 1);
            queue = IntPtr.Zero;
        }
    }

    // ---------------------------------------------------------------- Windows capture (WASAPI)

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        void OpenPropertyStore(int access, out IntPtr store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        void Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint padding);
        void IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr handle);
        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint frames);
        void GetNextPacketSize(out uint frames);
    }

    sealed class AudioCapture : IAudioSource
    {
        static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        const int LOOPBACK = 0x00020000;
        const int BUFFER_SILENT = 0x2;

        IAudioClient client;
        IAudioCaptureClient capture;
        readonly int rate;
        readonly string deviceId;
        readonly int channels, bits;
        readonly bool isFloat, mic;
        float[] fbuf = new float[0];
        short[] sbuf = new short[0];
        int[] ibuf = new int[0];
        byte[] bbuf = new byte[0];

        public static string GetDefaultId(bool mic)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev;
            en.GetDefaultAudioEndpoint(mic ? 1 : 0, 1, out dev);
            string id;
            dev.GetId(out id);
            return id;
        }

        public int SampleRate { get { return rate; } }

        public bool DeviceChanged()
        {
            try { return GetDefaultId(mic) != deviceId; }
            catch { return false; }
        }

        public AudioCapture(bool mic)
        {
            this.mic = mic;
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev;
            en.GetDefaultAudioEndpoint(mic ? 1 : 0, 1, out dev); // eRender/eCapture, eMultimedia
            dev.GetId(out deviceId);

            Guid iid = IID_IAudioClient;
            object o;
            dev.Activate(ref iid, 23, IntPtr.Zero, out o);
            client = (IAudioClient)o;

            IntPtr fmt;
            client.GetMixFormat(out fmt);
            try
            {
                int tag = Marshal.ReadInt16(fmt, 0) & 0xFFFF;
                channels = Marshal.ReadInt16(fmt, 2);
                rate = Marshal.ReadInt32(fmt, 4);
                bits = Marshal.ReadInt16(fmt, 14);
                if (tag == 0xFFFE)
                {
                    var sub = (Guid)Marshal.PtrToStructure(new IntPtr(fmt.ToInt64() + 24), typeof(Guid));
                    isFloat = sub.ToString().StartsWith("00000003");
                }
                else isFloat = tag == 3;

                client.Initialize(0, mic ? 0 : LOOPBACK, 2000000, 0, fmt, IntPtr.Zero); // 200 ms buffer
            }
            finally { Marshal.FreeCoTaskMem(fmt); }

            Guid ciid = IID_IAudioCaptureClient;
            client.GetService(ref ciid, out o);
            capture = (IAudioCaptureClient)o;
            client.Start();
        }

        // Drains every pending packet into the ring as mono samples. Returns frames read.
        public int Read(Ring ring)
        {
            int total = 0;
            uint packet;
            capture.GetNextPacketSize(out packet);
            while (packet > 0)
            {
                IntPtr data;
                uint frames, flags;
                ulong p1, p2;
                capture.GetBuffer(out data, out frames, out flags, out p1, out p2);
                int n = (int)frames;
                if ((flags & BUFFER_SILENT) != 0)
                    for (int i = 0; i < n; i++) ring.Push(0f);
                else
                    Convert(data, n, ring);
                capture.ReleaseBuffer(frames);
                total += n;
                capture.GetNextPacketSize(out packet);
            }
            return total;
        }

        void Convert(IntPtr data, int frames, Ring ring)
        {
            int count = frames * channels;
            float inv = 1f / channels;
            if (isFloat && bits == 32)
            {
                if (fbuf.Length < count) fbuf = new float[count];
                Marshal.Copy(data, fbuf, 0, count);
                for (int f = 0, i = 0; f < frames; f++)
                {
                    float s = 0;
                    for (int c = 0; c < channels; c++) s += fbuf[i++];
                    ring.Push(s * inv);
                }
            }
            else if (bits == 16)
            {
                if (sbuf.Length < count) sbuf = new short[count];
                Marshal.Copy(data, sbuf, 0, count);
                for (int f = 0, i = 0; f < frames; f++)
                {
                    float s = 0;
                    for (int c = 0; c < channels; c++) s += sbuf[i++] / 32768f;
                    ring.Push(s * inv);
                }
            }
            else if (bits == 32)
            {
                if (ibuf.Length < count) ibuf = new int[count];
                Marshal.Copy(data, ibuf, 0, count);
                for (int f = 0, i = 0; f < frames; f++)
                {
                    float s = 0;
                    for (int c = 0; c < channels; c++) s += ibuf[i++] / 2147483648f;
                    ring.Push(s * inv);
                }
            }
            else if (bits == 24)
            {
                int bytes = count * 3;
                if (bbuf.Length < bytes) bbuf = new byte[bytes];
                Marshal.Copy(data, bbuf, 0, bytes);
                for (int f = 0, i = 0; f < frames; f++)
                {
                    float s = 0;
                    for (int c = 0; c < channels; c++, i += 3)
                        s += ((bbuf[i] << 8 | bbuf[i + 1] << 16 | bbuf[i + 2] << 24) >> 8) / 8388608f;
                    ring.Push(s * inv);
                }
            }
        }

        public void Dispose()
        {
            try { client.Stop(); } catch { }
            try { Marshal.ReleaseComObject(capture); } catch { }
            try { Marshal.ReleaseComObject(client); } catch { }
        }
    }

    // ---------------------------------------------------------------- DSP helpers

    sealed class Ring
    {
        readonly float[] buf;
        readonly int mask;
        int pos;

        public Ring(int sizePow2) { buf = new float[sizePow2]; mask = sizePow2 - 1; }
        public int Size { get { return buf.Length; } }

        public void Push(float v) { buf[pos] = v; pos = (pos + 1) & mask; }

        // Copies the newest n samples into dst, oldest first.
        public void CopyLatest(float[] dst, int n)
        {
            int start = (pos - n) & mask;
            for (int i = 0; i < n; i++) dst[i] = buf[(start + i) & mask];
        }
    }

    static class Fft
    {
        public static void Transform(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    double t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                int halfLen = len >> 1;
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < halfLen; k++)
                    {
                        int a = i + k, b = a + halfLen;
                        double tr = re[b] * cr - im[b] * ci;
                        double ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        double ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }
    }

    sealed class Smoother
    {
        public float[] V = new float[0];

        public void Apply(float[] raw, int n, float dt, float riseTau, float fallTau)
        {
            if (V.Length != n) V = new float[n];
            float ar = 1f - (float)Math.Exp(-dt / riseTau);
            float af = 1f - (float)Math.Exp(-dt / fallTau);
            for (int i = 0; i < n; i++)
            {
                float r = raw[i];
                V[i] += (r - V[i]) * (r > V[i] ? ar : af);
            }
        }
    }

    // ---------------------------------------------------------------- Screen buffer

    sealed class Screen
    {
        const string CSI = "\u001b[";
        public int W, H;
        public bool TrueColor = true;
        public char[] Ch = new char[0];
        public int[] Fg = new int[0], Bg = new int[0];
        readonly Stream stdout = Console.OpenStandardOutput();
        readonly StringBuilder sb = new StringBuilder(1 << 16);
        readonly UTF8Encoding utf8 = new UTF8Encoding(false);

        public void Resize(int w, int h)
        {
            W = w; H = h;
            Ch = new char[w * h]; Fg = new int[w * h]; Bg = new int[w * h];
            Write(CSI + "0m" + CSI + "2J");
        }

        public void Clear()
        {
            for (int i = 0; i < Ch.Length; i++) { Ch[i] = ' '; Fg[i] = -1; Bg[i] = -1; }
        }

        public void Set(int x, int y, char c, int fg, int bg)
        {
            if (x < 0 || y < 0 || x >= W || y >= H) return;
            int i = y * W + x;
            Ch[i] = c; Fg[i] = fg; Bg[i] = bg;
        }

        public void Text(int x, int y, string s, int fg, int bg)
        {
            for (int i = 0; i < s.Length; i++) Set(x + i, y, s[i], fg, bg);
        }

        public void Write(string s)
        {
            byte[] b = utf8.GetBytes(s);
            stdout.Write(b, 0, b.Length);
            stdout.Flush();
        }

        public void Flush()
        {
            sb.Clear();
            sb.Append(CSI).Append("0m");
            int cf = -1, cb = -1;
            for (int y = 0; y < H; y++)
            {
                sb.Append(CSI).Append(y + 1).Append(";1H");
                int w = y == H - 1 ? W - 1 : W; // never touch the bottom-right cell (avoids scrolling)
                for (int x = 0; x < w; x++)
                {
                    int i = y * W + x;
                    char c = Ch[i];
                    int f = Fg[i], b = Bg[i];
                    if (b != cb) { AppendColor(b, false); cb = b; }
                    if (f != cf && c != ' ') { AppendColor(f, true); cf = f; }
                    sb.Append(c);
                }
            }
            Write(sb.ToString());
        }

        void AppendColor(int c, bool fg)
        {
            if (c < 0) { sb.Append(CSI).Append(fg ? "39m" : "49m"); return; }
            int r = c >> 16, g = (c >> 8) & 255, b = c & 255;
            if (TrueColor)
                sb.Append(CSI).Append(fg ? "38;2;" : "48;2;").Append(r).Append(';').Append(g).Append(';').Append(b).Append('m');
            else
                sb.Append(CSI).Append(fg ? "38;5;" : "48;5;").Append(To256(r, g, b)).Append('m');
        }

        // Nearest xterm-256 color: either the 6x6x6 cube or the 24-step gray ramp.
        static int To256(int r, int g, int b)
        {
            int ri = (r * 5 + 127) / 255, gi = (g * 5 + 127) / 255, bi = (b * 5 + 127) / 255;
            int cr = ri == 0 ? 0 : 55 + ri * 40, cg = gi == 0 ? 0 : 55 + gi * 40, cbv = bi == 0 ? 0 : 55 + bi * 40;
            int avg = (r + g + b) / 3;
            int gray = Math.Min(23, Math.Max(0, (avg - 3) / 10));
            int gv = 8 + gray * 10;
            int dCube = Sq(r - cr) + Sq(g - cg) + Sq(b - cbv);
            int dGray = Sq(r - gv) + Sq(g - gv) + Sq(b - gv);
            return dGray < dCube ? 232 + gray : 16 + 36 * ri + 6 * gi + bi;
        }

        static int Sq(int v) { return v * v; }
    }

    // ---------------------------------------------------------------- Braille canvas (2x4 dots per cell)

    sealed class Braille
    {
        public int W, H;
        byte[] dots = new byte[0];
        float[] val = new float[0];

        public void Begin(int w, int h)
        {
            W = w; H = h;
            int n = w * h;
            if (dots.Length < n) { dots = new byte[n]; val = new float[n]; }
            Array.Clear(dots, 0, n);
            Array.Clear(val, 0, n);
        }

        // t is a 0..1 color value; each cell keeps the highest t plotted into it.
        public void Plot(int px, int py, float t)
        {
            if (px < 0 || py < 0 || px >= W * 2 || py >= H * 4) return;
            int i = (py >> 2) * W + (px >> 1);
            int sx = px & 1, sy = py & 3;
            int bit = sy < 3 ? (sx == 0 ? 1 << sy : 8 << sy) : (sx == 0 ? 0x40 : 0x80);
            dots[i] |= (byte)bit;
            if (t > val[i]) val[i] = t;
        }

        public void Line(float x0, float y0, float x1, float y1, float t0, float t1)
        {
            int steps = (int)Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)) + 1;
            for (int s = 0; s <= steps; s++)
            {
                float f = (float)s / steps;
                Plot((int)Math.Round(x0 + (x1 - x0) * f), (int)Math.Round(y0 + (y1 - y0) * f), t0 + (t1 - t0) * f);
            }
        }

        public void Draw(Screen scr, Func<float, int> color)
        {
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    if (dots[i] != 0) scr.Set(x, y, (char)(0x2800 + dots[i]), color(val[i]), -1);
                }
        }
    }

    // A tweakable setting shown in the Tab panel.
    sealed class Param
    {
        public readonly string Name;
        public readonly Func<string> Get;
        public readonly Action<int> Adjust;
        public Param(string name, Func<string> get, Action<int> adjust) { Name = name; Get = get; Adjust = adjust; }
    }

    // ---------------------------------------------------------------- Visualizer

    sealed class Viz
    {
        static readonly string[] ModeNames = { "Bars", "Mirror", "Wave", "Waterfall", "Rain", "Code", "Radial" };
        static readonly string[] ThemeNames =
        {
            "Fire", "Ocean", "Neon", "Matrix", "Rainbow", "Mono",
            "Synthwave", "Cyberpunk", "Sunset", "Amber", "Ice", "Toxic", "Blood", "Vapor",
        };
        static readonly int[][] ThemeStops =
        {
            new[] { 0x400000, 0xC81E00, 0xFF8C00, 0xFFE650, 0xFFFFDC },
            new[] { 0x00143C, 0x005AB4, 0x00BEDC, 0x96FFFF },
            new[] { 0x5000A0, 0xC800C8, 0xFF3C8C, 0xFFB43C },
            new[] { 0x002800, 0x008C1E, 0x3CFF5A, 0xC8FFC8 },
            new[] { 0x283CFF, 0x00C8FF, 0x00FF78, 0xFFF000, 0xFF2828 },
            new[] { 0x3C3C3C, 0xE6E6E6 },
            new[] { 0x2B0F54, 0xAB1FC4, 0xFF2A6D, 0xFF9E3D, 0xFFD319 },
            new[] { 0x3A00A0, 0x7000FF, 0xFF00A0, 0x00F0FF, 0xFCEE0A },
            new[] { 0x2D0B4E, 0x8B1E5F, 0xE8485A, 0xFF9A3C, 0xFFE29A },
            new[] { 0x2A1200, 0x7A3E00, 0xFFB000, 0xFFE0A0 },
            new[] { 0x0B1D3A, 0x3A7BD5, 0xA8E6FF, 0xFFFFFF },
            new[] { 0x0A2400, 0x4CBB17, 0xC6FF00, 0xF4FF81 },
            new[] { 0x280000, 0x800010, 0xFF2040, 0xFFB0B0 },
            new[] { 0x01CDFE, 0xB967FF, 0xFF71CE, 0xFFFB96 },
        };
        static readonly string[] GlyphNames = { "Blocks", "ASCII", "Code", "Dots" };
        static readonly string[] ColorByNames = { "Height", "Frequency", "Loudness" };
        static readonly string[] ColorModeNames = { "Auto", "24-bit", "256" };
        static readonly int[] FpsSteps = { 30, 45, 60, 90, 120, 144 };
        const string EIGHTHS = " ▁▂▃▄▅▆▇█";
        const string ASCII9 = " .:-=+*%#";
        const string PEAKCHARS = "▔-=⠉";
        const string CODECHARS = "0123456789ABCDEFabcdef{}[]()<>;:=+-*/&|!?#$%@_~^";
        static readonly int[] DotFill = { 0, 0xC0, 0xC0, 0xE4, 0xE4, 0xF6, 0xF6, 0xFF, 0xFF };
        const int N = 4096; // FFT size

        // ---- settings (saved to termvis.ini)
        public int Mode, Theme, BarWidth, Fps, Glyphs, ColorBy, ColorMode;
        public double GainDb, RangeDb, MinHz, MaxHz, Tilt;
        public float Smooth, PeakGravity, RainSpeed, CodeSpeed, SpinSpeed;
        public bool AutoGain, Gap, Peaks, UseMic, ShowStatus;

        // ---- runtime state
        readonly Screen scr = new Screen();
        readonly Ring ring = new Ring(16384);
        readonly Random rng = new Random();
        readonly List<Param> pars;
        IAudioSource cap;
        int sampleRate = 48000, frame;
        string srcError;
        volatile bool quit;
        bool showHelp, showSettings, reopen;
        int selected;
        double lastData, lastOpenTry = -10;

        readonly float[] tbuf = new float[N];
        readonly double[] hann = new double[N], re = new double[N], im = new double[N];
        readonly double[] dbSpec = new double[N / 2 + 1];
        double agc = -20, refDb;
        float wavePeak = 0.1f;

        float[] raw = new float[0], wbuf = new float[0];
        float[] peak = new float[0], peakVel = new float[0], peakHold = new float[0];
        readonly Smoother barSmooth = new Smoother(), wfSmooth = new Smoother(), rainSmooth = new Smoother(), radSmooth = new Smoother();
        readonly Braille braille = new Braille();
        readonly List<float[]> history = new List<float[]>();
        int wfWidth;
        readonly int[] pal = new int[64], heat = new int[64];

        // rain state
        sealed class Drop { public int X, LastRow; public float Y, Speed; }
        readonly List<Drop> drops = new List<Drop>();
        char[] rainCh = new char[0];
        float[] rainB = new float[0];
        int[] colDrops = new int[0];
        int rainW, rainH;

        // code state
        readonly List<string> codeLines = new List<string>();
        int codeIndent;
        float codeScroll, bassAvg, glitch, beatCooldown;

        // radial state
        float spin;

        public Viz()
        {
            Defaults();
            for (int i = 0; i < N; i++) hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1));
            pars = new List<Param>
            {
                new Param("Mode", () => ModeNames[Mode], d => Mode = Wrap(Mode + d, ModeNames.Length)),
                new Param("Color theme", () => ThemeNames[Theme], d => { Theme = Wrap(Theme + d, ThemeNames.Length); BuildPalette(); }),
                new Param("Glyphs (bars)", () => GlyphNames[Glyphs], d => Glyphs = Wrap(Glyphs + d, GlyphNames.Length)),
                new Param("Color by", () => ColorByNames[ColorBy], d => ColorBy = Wrap(ColorBy + d, ColorByNames.Length)),
                new Param("Source", () => SourceName, d => ToggleSource()),
                new Param("Gain", () => GainDb.ToString("+0;-0;0") + " dB", d => GainDb = ClampD(GainDb + 2 * d, -30, 30)),
                new Param("Auto-gain", () => OnOff(AutoGain), d => AutoGain = !AutoGain),
                new Param("Dynamic range", () => RangeDb.ToString("0") + " dB", d => RangeDb = ClampD(RangeDb + 5 * d, 20, 90)),
                new Param("Low cut", () => MinHz.ToString("0") + " Hz", d => MinHz = ClampD(Math.Round(MinHz * Math.Pow(1.25, d)), 20, 500)),
                new Param("High cut", () => (MaxHz / 1000).ToString("0.0") + " kHz", d => MaxHz = ClampD(MaxHz * Math.Pow(1.15, d), 2000, 22000)),
                new Param("Treble boost", () => Tilt.ToString("0.0") + " dB/oct", d => Tilt = ClampD(Tilt + 0.5 * d, 0, 6)),
                new Param("Smoothing", () => Smooth.ToString("0.00") + " s", d => Smooth = (float)ClampD(Smooth * Math.Pow(1.25, d), 0.02, 1)),
                new Param("Bar width", () => BarWidth.ToString(), d => BarWidth = Clamp(BarWidth + d, 1, 8)),
                new Param("Bar gaps", () => OnOff(Gap), d => Gap = !Gap),
                new Param("Peak markers", () => OnOff(Peaks), d => Peaks = !Peaks),
                new Param("Peak gravity", () => PeakGravity.ToString("0.00"), d => PeakGravity = (float)ClampD(PeakGravity + 0.25 * d, 0.25, 6)),
                new Param("Rain speed", () => "x" + RainSpeed.ToString("0.00"), d => RainSpeed = (float)ClampD(RainSpeed * Math.Pow(1.25, d), 0.25, 4)),
                new Param("Code scroll speed", () => "x" + CodeSpeed.ToString("0.00"), d => CodeSpeed = (float)ClampD(CodeSpeed * Math.Pow(1.25, d), 0.25, 4)),
                new Param("Radial spin", () => SpinSpeed.ToString("+0.0;-0.0;0.0"), d => SpinSpeed = (float)ClampD(Math.Round(SpinSpeed + 0.1 * d, 1), -3, 3)),
                new Param("Frame rate", () => Fps + " fps", d => Fps = FpsSteps[Clamp(Array.IndexOf(FpsSteps, Fps) + d, 0, FpsSteps.Length - 1)]),
                new Param("Terminal colors", () => ColorModeNames[ColorMode], d => ColorMode = Wrap(ColorMode + d, ColorModeNames.Length)),
                new Param("Status bar", () => OnOff(ShowStatus), d => ShowStatus = !ShowStatus),
            };
        }

        public void Defaults()
        {
            Mode = 0; Theme = 0; BarWidth = 2; Fps = 60; Glyphs = 0; ColorBy = 0; ColorMode = 0;
            GainDb = 0; RangeDb = 50; MinHz = 30; MaxHz = 16000; Tilt = 3;
            Smooth = 0.12f; PeakGravity = 1.5f; RainSpeed = 1; CodeSpeed = 1; SpinSpeed = 0.3f;
            AutoGain = true; Gap = true; Peaks = true; UseMic = false; ShowStatus = true;
        }

        // ---------------- settings file

        // run.bat sets TERMVIS_HOME to its own folder; built binaries use their own folder.
        static string IniPath
        {
            get
            {
                string home = Environment.GetEnvironmentVariable("TERMVIS_HOME");
                if (string.IsNullOrEmpty(home)) home = AppDomain.CurrentDomain.BaseDirectory;
                return Path.Combine(home, "termvis.ini");
            }
        }
        string savedText;

        public void Load()
        {
            try
            {
                if (!File.Exists(IniPath)) return;
                foreach (string line in File.ReadAllLines(IniPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) Apply(line.Substring(0, eq).Trim().ToLowerInvariant(), line.Substring(eq + 1).Trim());
                }
            }
            catch { }
        }

        // Writes the settings file only when something changed since the last write.
        public void Save()
        {
            string text = SettingsText();
            if (text == savedText) return;
            try
            {
                File.WriteAllText(IniPath, text);
                savedText = text;
            }
            catch { }
        }

        string SettingsText()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return string.Join(Environment.NewLine, new[]
                {
                    "mode=" + ModeNames[Mode].ToLowerInvariant(),
                    "theme=" + ThemeNames[Theme].ToLowerInvariant(),
                    "glyphs=" + GlyphNames[Glyphs].ToLowerInvariant(),
                    "colorby=" + ColorByNames[ColorBy].ToLowerInvariant(),
                    "colors=" + ColorModeNames[ColorMode].ToLowerInvariant(),
                    "source=" + (UseMic ? "mic" : "system"),
                    "gain=" + GainDb.ToString(inv),
                    "autogain=" + AutoGain,
                    "range=" + RangeDb.ToString(inv),
                    "lowcut=" + MinHz.ToString(inv),
                    "highcut=" + MaxHz.ToString(inv),
                    "tilt=" + Tilt.ToString(inv),
                    "smooth=" + Smooth.ToString(inv),
                    "barwidth=" + BarWidth,
                    "gap=" + Gap,
                    "peaks=" + Peaks,
                    "peakgravity=" + PeakGravity.ToString(inv),
                    "rainspeed=" + RainSpeed.ToString(inv),
                    "codespeed=" + CodeSpeed.ToString(inv),
                    "spin=" + SpinSpeed.ToString(inv),
                    "status=" + ShowStatus,
                    "fps=" + Fps,
                }) + Environment.NewLine;
        }

        public bool Apply(string key, string val)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string v = val.ToLowerInvariant();
            double d;
            bool ok = double.TryParse(val, System.Globalization.NumberStyles.Float, inv, out d);
            bool b = v == "true" || v == "1" || v == "yes" || v == "on";
            switch (key)
            {
                case "mode": { int i = IndexOf(ModeNames, v); if (i >= 0) Mode = i; else if (ok) Mode = Clamp((int)d - 1, 0, ModeNames.Length - 1); return i >= 0 || ok; }
                case "theme": { int i = IndexOf(ThemeNames, v); if (i >= 0) Theme = i; return i >= 0; }
                case "glyphs": { int i = IndexOf(GlyphNames, v); if (i >= 0) Glyphs = i; return i >= 0; }
                case "colorby": { int i = IndexOf(ColorByNames, v); if (i >= 0) ColorBy = i; return i >= 0; }
                case "colors": { int i = IndexOf(ColorModeNames, v); if (i >= 0) ColorMode = i; return i >= 0; }
                case "source": UseMic = v == "mic" || v == "microphone"; return true;
                case "gain": if (ok) GainDb = ClampD(d, -30, 30); return ok;
                case "autogain": AutoGain = b; return true;
                case "range": if (ok) RangeDb = ClampD(d, 20, 90); return ok;
                case "lowcut": if (ok) MinHz = ClampD(d, 20, 500); return ok;
                case "highcut": if (ok) MaxHz = ClampD(d, 2000, 22000); return ok;
                case "tilt": if (ok) Tilt = ClampD(d, 0, 6); return ok;
                case "smooth": if (ok) Smooth = (float)ClampD(d, 0.02, 1.0); return ok;
                case "barwidth": if (ok) BarWidth = Clamp((int)d, 1, 8); return ok;
                case "gap": Gap = b; return true;
                case "peaks": Peaks = b; return true;
                case "peakgravity": if (ok) PeakGravity = (float)ClampD(d, 0.25, 6); return ok;
                case "rainspeed": if (ok) RainSpeed = (float)ClampD(d, 0.25, 4); return ok;
                case "codespeed": if (ok) CodeSpeed = (float)ClampD(d, 0.25, 4); return ok;
                case "spin": if (ok) SpinSpeed = (float)ClampD(d, -3, 3); return ok;
                case "status": ShowStatus = b; return true;
                case "fps": if (ok) Fps = Clamp((int)d, 10, 144); return ok;
            }
            return false;
        }

        static int IndexOf(string[] names, string v)
        {
            for (int i = 0; i < names.Length; i++) if (names[i].ToLowerInvariant() == v) return i;
            return -1;
        }

        // ---------------- main loop

        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
        [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint mode);
        [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint mode);
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

        public void Run()
        {
            if (Platform.IsWindows)
            {
                IntPtr hOut = GetStdHandle(-11);
                uint mode;
                GetConsoleMode(hOut, out mode);
                SetConsoleMode(hOut, mode | 0x0004); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
                timeBeginPeriod(1);
            }
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; quit = true; };
            scr.Write("\u001b[?1049h\u001b[?25l"); // alternate screen, hide cursor
            BuildPalette();

            var sw = Stopwatch.StartNew();
            double last = 0, lastDevCheck = 0, lastSave = 0;
            OpenCapture(0);
            try
            {
                while (!quit)
                {
                    double now = sw.Elapsed.TotalSeconds;
                    float dt = (float)Math.Max(0.001, Math.Min(0.1, now - last));
                    last = now;

                    HandleKeys();
                    if (reopen) { reopen = false; CloseCapture(); OpenCapture(now); }
                    Pump(now, dt);
                    if (now - lastDevCheck > 2) { lastDevCheck = now; CheckDevice(now); }
                    if (now - lastSave > 1) { lastSave = now; Save(); } // autosave, so closing the window keeps settings
                    Analyze(dt);
                    Render(dt);

                    int ms = (int)((1.0 / Fps - (sw.Elapsed.TotalSeconds - now)) * 1000);
                    if (ms > 0) Thread.Sleep(ms);
                }
            }
            finally
            {
                CloseCapture();
                if (Platform.IsWindows) timeEndPeriod(1);
                scr.Write("\u001b[0m\u001b[?25h\u001b[?1049l");
                Save();
            }
        }

        void HandleKeys()
        {
            while (Console.KeyAvailable)
            {
                var k = Console.ReadKey(true);
                if (showSettings)
                {
                    switch (k.Key)
                    {
                        case ConsoleKey.UpArrow: selected = Wrap(selected - 1, pars.Count); continue;
                        case ConsoleKey.DownArrow: selected = Wrap(selected + 1, pars.Count); continue;
                        case ConsoleKey.LeftArrow: pars[selected].Adjust(-1); continue;
                        case ConsoleKey.RightArrow: pars[selected].Adjust(1); continue;
                        case ConsoleKey.Tab: case ConsoleKey.Escape: case ConsoleKey.Enter: showSettings = false; continue;
                    }
                }
                switch (k.Key)
                {
                    case ConsoleKey.Q: case ConsoleKey.Escape: quit = true; break;
                    case ConsoleKey.Tab: case ConsoleKey.E: showSettings = !showSettings; showHelp = false; break;
                    case ConsoleKey.M: Mode = Wrap(Mode + 1, ModeNames.Length); break;
                    case ConsoleKey.C:
                        bool back = (k.Modifiers & ConsoleModifiers.Shift) != 0 || k.KeyChar == 'C';
                        Theme = Wrap(Theme + (back ? -1 : 1), ThemeNames.Length); BuildPalette();
                        break;
                    case ConsoleKey.K: Glyphs = Wrap(Glyphs + 1, GlyphNames.Length); break;
                    case ConsoleKey.V: ColorBy = Wrap(ColorBy + 1, ColorByNames.Length); break;
                    case ConsoleKey.S: ToggleSource(); break;
                    case ConsoleKey.UpArrow: GainDb = Math.Min(30, GainDb + 2); break;
                    case ConsoleKey.DownArrow: GainDb = Math.Max(-30, GainDb - 2); break;
                    case ConsoleKey.A: AutoGain = !AutoGain; break;
                    case ConsoleKey.RightArrow: BarWidth = Math.Min(8, BarWidth + 1); break;
                    case ConsoleKey.LeftArrow: BarWidth = Math.Max(1, BarWidth - 1); break;
                    case ConsoleKey.G: Gap = !Gap; break;
                    case ConsoleKey.P: Peaks = !Peaks; break;
                    case ConsoleKey.B: ShowStatus = !ShowStatus; break;
                    case ConsoleKey.H: showHelp = !showHelp; showSettings = false; break;
                    case ConsoleKey.R:
                        bool mic = UseMic;
                        Defaults(); BuildPalette();
                        if (mic != UseMic) reopen = true;
                        break;
                    default:
                        // matched by character since [ and ] map to different ConsoleKeys across platforms
                        if (k.KeyChar >= '1' && k.KeyChar <= '0' + ModeNames.Length) Mode = k.KeyChar - '1';
                        else if (k.KeyChar == '[') Smooth = Math.Max(0.02f, Smooth / 1.25f);
                        else if (k.KeyChar == ']') Smooth = Math.Min(1.0f, Smooth * 1.25f);
                        break;
                }
            }
        }

        // macOS has no system-audio loopback, so there the source is always the default input device.
        string SourceName
        {
            get { return Platform.IsWindows ? (UseMic ? "Microphone" : "System audio") : "Input device"; }
        }

        void ToggleSource()
        {
            if (!Platform.IsWindows) return;
            UseMic = !UseMic;
            reopen = true;
        }

        // ---------------- audio

        void OpenCapture(double now)
        {
            lastOpenTry = now;
            try
            {
                if (Platform.IsWindows) cap = new AudioCapture(UseMic);
                else if (Platform.IsMac) cap = new MacAudioCapture();
                else throw new PlatformNotSupportedException();
                sampleRate = cap.SampleRate;
                srcError = null;
                lastData = now;
            }
            catch (Exception)
            {
                cap = null;
                srcError = "no device found - retrying";
            }
        }

        void CloseCapture()
        {
            if (cap != null) { cap.Dispose(); cap = null; }
        }

        void CheckDevice(double now)
        {
            if (cap != null && cap.DeviceChanged()) { CloseCapture(); OpenCapture(now); }
        }

        void Pump(double now, float dt)
        {
            if (cap == null)
            {
                if (now - lastOpenTry > 1) OpenCapture(now);
                PushSilence(dt);
                return;
            }
            try
            {
                // Loopback delivers nothing while the system is silent, so feed zeros to let the display fall.
                if (cap.Read(ring) > 0) lastData = now;
                else if (now - lastData > 0.1) PushSilence(dt);
            }
            catch (Exception)
            {
                CloseCapture();
                srcError = "device lost - retrying";
            }
        }

        void PushSilence(float dt)
        {
            int n = Math.Min(ring.Size, (int)(dt * sampleRate));
            for (int i = 0; i < n; i++) ring.Push(0f);
        }

        void Analyze(float dt)
        {
            ring.CopyLatest(tbuf, N);
            for (int i = 0; i < N; i++) { re[i] = tbuf[i] * hann[i]; im[i] = 0; }
            Fft.Transform(re, im);

            double df = (double)sampleRate / N;
            int half = N / 2;
            int kmin = Math.Max(1, (int)(MinHz / df)), kmax = Math.Min(half, (int)(MaxHz / df));
            double maxDb = -300;
            for (int k = 1; k <= half; k++)
            {
                double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) * 4.0 / N; // full-scale sine ~= 1.0
                double f = k * df;
                // tilt around 1 kHz so treble is as visible as bass
                double db = 20 * Math.Log10(mag + 1e-12) + Tilt * Math.Log(f / 1000.0, 2);
                dbSpec[k] = db;
                if (k >= kmin && k <= kmax && db > maxDb) maxDb = db;
            }
            dbSpec[0] = dbSpec[1];

            if (maxDb > agc) agc += (maxDb - agc) * 0.5;
            else agc -= 8 * dt;
            if (agc < -50) agc = -50;
            refDb = (AutoGain ? agc : 0) - GainDb;
        }

        double DbAt(double bin)
        {
            int half = N / 2;
            int k = Clamp((int)bin, 0, half - 1);
            double frac = Math.Max(0, Math.Min(1, bin - k));
            return dbSpec[k] + (dbSpec[k + 1] - dbSpec[k]) * frac;
        }

        // Splits the spectrum into n log-spaced bands, normalized to 0..1, into raw[].
        void Bands(int n)
        {
            if (raw.Length < n) raw = new float[n];
            double df = (double)sampleRate / N;
            double hi = Math.Min(MaxHz, sampleRate * 0.48), lo = Math.Min(MinHz, hi / 2), ratio = hi / lo;
            for (int i = 0; i < n; i++)
            {
                double f0 = lo * Math.Pow(ratio, (double)i / n);
                double f1 = lo * Math.Pow(ratio, (double)(i + 1) / n);
                int k0 = (int)Math.Ceiling(f0 / df), k1 = (int)Math.Floor(f1 / df);
                double d;
                if (k1 - k0 < 1) d = DbAt(Math.Sqrt(f0 * f1) / df);
                else
                {
                    d = -999;
                    for (int k = k0; k <= k1; k++) if (dbSpec[k] > d) d = dbSpec[k];
                }
                raw[i] = Clamp01((float)((d - (refDb - RangeDb)) / RangeDb));
            }
        }

        static float Average(float[] v, int from, int count)
        {
            float s = 0;
            for (int i = from; i < from + count; i++) s += v[i];
            return count > 0 ? s / count : 0;
        }

        // ---------------- rendering

        void BuildPalette()
        {
            int[] stops = ThemeStops[Theme];
            for (int i = 0; i < 64; i++)
            {
                double t = i / 63.0 * (stops.Length - 1);
                int s = Math.Min(stops.Length - 2, (int)t);
                pal[i] = Lerp(stops[s], stops[s + 1], t - s);
                heat[i] = Scale(pal[i], Math.Min(1.0, i / 63.0 * 1.6));
            }
        }

        int Pal(float t) { return pal[Clamp((int)(t * 63 + 0.5f), 0, 63)]; }
        int Heat(float t) { return heat[Clamp((int)(t * 63 + 0.5f), 0, 63)]; }

        int BarColor(int b, int rows, int i, int n, float v)
        {
            float h = rows > 1 ? (float)b / (rows - 1) : 1f;
            switch (ColorBy)
            {
                case 1: return Pal(n > 1 ? (float)i / (n - 1) : 0f);
                case 2: return Pal(v * 0.85f + 0.15f * h);
                default: return Pal(h);
            }
        }

        static int Hash(int x)
        {
            unchecked
            {
                uint h = (uint)x;
                h ^= h >> 16; h *= 0x7feb352d;
                h ^= h >> 15; h *= 0x846ca68b;
                h ^= h >> 16;
                return (int)(h & 0x7fffffff);
            }
        }

        // A code character fixed to a cell that changes every so often, staggered per cell.
        char CodeChar(int x, int y)
        {
            int seed = Hash(x * 7919 + y * 104729);
            int t = (frame + (seed & 63)) / 10;
            return CODECHARS[Hash(seed + t * 31337) % CODECHARS.Length];
        }

        char RandCode() { return CODECHARS[rng.Next(CODECHARS.Length)]; }

        // filled = eighths of the cell covered (0..8), drawn in the current glyph style.
        char Glyph(int filled, int x, int y)
        {
            if (filled <= 0) return ' ';
            switch (Glyphs)
            {
                case 1: return ASCII9[filled];
                case 2: return CodeChar(x, y);
                case 3: return (char)(0x2800 + DotFill[filled]);
                default: return EIGHTHS[filled];
            }
        }

        // Windows consoles all do 24-bit color; elsewhere trust COLORTERM (macOS Terminal often lacks it).
        static readonly bool AutoTrueColor = Platform.IsWindows || ColorTermSaysTrue();

        static bool ColorTermSaysTrue()
        {
            string ct = (Environment.GetEnvironmentVariable("COLORTERM") ?? "").ToLowerInvariant();
            string tp = Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? "";
            return ct.Contains("truecolor") || ct.Contains("24bit") || tp == "iTerm.app" || tp == "WezTerm" || tp == "ghostty";
        }

        void Render(float dt)
        {
            frame++;
            int w = Console.WindowWidth, h = Console.WindowHeight;
            if (w != scr.W || h != scr.H) scr.Resize(w, h);
            scr.TrueColor = ColorMode == 1 || (ColorMode == 0 && AutoTrueColor);
            scr.Clear();
            int vh = ShowStatus ? h - 1 : h;
            if (vh >= 2 && w >= 4)
            {
                switch (Mode)
                {
                    case 0: DrawBars(vh, dt, false); break;
                    case 1: DrawBars(vh, dt, true); break;
                    case 2: DrawWave(vh, dt); break;
                    case 3: DrawWaterfall(vh, dt); break;
                    case 4: DrawRain(vh, dt); break;
                    case 5: DrawCode(vh, dt); break;
                    case 6: DrawRadial(vh, dt); break;
                }
            }
            if (ShowStatus) DrawStatus(h - 1);
            if (showHelp) DrawHelp();
            if (showSettings) DrawSettings();
            scr.Flush();
        }

        // Shared bar layout: how many bars fit and where the first one starts.
        int BarLayout(out int x0)
        {
            int gap = Gap ? 1 : 0, bw = BarWidth;
            int n = Math.Max(1, (scr.W + gap) / (bw + gap));
            x0 = (scr.W - (n * (bw + gap) - gap)) / 2;
            return n;
        }

        void DrawBars(int vh, float dt, bool mirror)
        {
            int x0;
            int n = BarLayout(out x0), bw = BarWidth, step = BarWidth + (Gap ? 1 : 0);
            Bands(n);
            barSmooth.Apply(raw, n, dt, 0.015f, Smooth);
            UpdatePeaks(n, dt);
            int peakCol = Lerp(pal[63], 0xFFFFFF, 0.5);

            int up = mirror ? vh / 2 : vh;       // rows above the baseline
            int down = mirror ? vh - up : 0;     // reflected rows below it
            for (int i = 0; i < n; i++)
            {
                float v = barSmooth.V[i];
                int xs = x0 + i * step;
                float h8 = v * up * 8;
                for (int b = 0; b < up; b++)
                {
                    int filled = Clamp((int)(h8 - b * 8), 0, 8);
                    if (filled == 0) break;
                    int col = BarColor(b, up, i, n, v);
                    if (Glyphs == 2 && filled < 8) col = Scale(col, 0.5 + filled / 16.0);
                    int y = up - 1 - b;
                    for (int dx = 0; dx < bw; dx++) scr.Set(xs + dx, y, Glyph(filled, xs + dx, y), col, -1);
                }
                if (Peaks && !mirror && peak[i] > 0.01f)
                {
                    int pb = Math.Min(up - 1, (int)(peak[i] * up));
                    if (Clamp((int)(h8 - pb * 8), 0, 8) == 0)
                        for (int dx = 0; dx < bw; dx++) scr.Set(xs + dx, up - 1 - pb, PEAKCHARS[Glyphs], peakCol, -1);
                }
                if (mirror)
                {
                    float h2 = v * down * 2;
                    for (int d = 0; d < down; d++)
                    {
                        int filled = Clamp((int)(h2 - d * 2), 0, 2);
                        if (filled == 0) break;
                        int col = Scale(BarColor(d, down, i, n, v), 0.45);
                        int y = up + d;
                        for (int dx = 0; dx < bw; dx++)
                        {
                            char c;
                            if (Glyphs == 0) c = filled == 2 ? '█' : '▀';
                            else if (Glyphs == 3) c = (char)(0x2800 + (filled == 2 ? 0xFF : 0x1B));
                            else c = Glyph(filled * 4, xs + dx, y);
                            scr.Set(xs + dx, y, c, col, -1);
                        }
                    }
                }
            }
        }

        void UpdatePeaks(int n, float dt)
        {
            if (peak.Length != n) { peak = new float[n]; peakVel = new float[n]; peakHold = new float[n]; }
            for (int i = 0; i < n; i++)
            {
                float v = barSmooth.V[i];
                if (v >= peak[i]) { peak[i] = v; peakVel[i] = 0; peakHold[i] = 0.4f; }
                else if (peakHold[i] > 0) peakHold[i] -= dt;
                else
                {
                    peakVel[i] += PeakGravity * dt;
                    peak[i] = Math.Max(0, peak[i] - peakVel[i] * dt);
                }
            }
        }

        // Oscilloscope drawn with braille dots.
        void DrawWave(int vh, float dt)
        {
            int pw = scr.W * 2, ph = vh * 4;
            int search = Math.Min(2048, ring.Size - pw);
            int len = pw + search;
            if (wbuf.Length < len) wbuf = new float[len];
            ring.CopyLatest(wbuf, len);

            // Lock onto the latest rising zero crossing so the waveform stands still.
            int t = search;
            for (int i = search - 1; i > 0; i--)
                if (wbuf[i - 1] < 0 && wbuf[i] >= 0) { t = i; break; }

            float pk = 0;
            for (int x = 0; x < pw; x++) pk = Math.Max(pk, Math.Abs(wbuf[t + x]));
            if (pk > wavePeak) wavePeak = pk;
            else wavePeak += (pk - wavePeak) * (1f - (float)Math.Exp(-dt / 0.6f));
            wavePeak = Math.Max(wavePeak, 0.02f);
            float scale = (AutoGain ? 0.9f / wavePeak : 1f) * (float)Math.Pow(10, GainDb / 20.0);

            braille.Begin(scr.W, vh);
            float mid = (ph - 1) / 2f, py = -1;
            for (int x = 0; x < pw; x++)
            {
                float s = wbuf[t + x] * scale;
                float y = Math.Max(0, Math.Min(ph - 1, (0.5f - s * 0.5f) * (ph - 1)));
                if (py < 0) py = y;
                float ta = Math.Abs(py - mid) / mid, tb = Math.Abs(y - mid) / mid;
                braille.Line(x, py, x, y, ta, tb);
                py = y;
            }
            braille.Draw(scr, v => Pal(0.25f + 0.75f * v));
        }

        // Scrolling spectrogram: newest at the top, two time slices per row via half blocks.
        void DrawWaterfall(int vh, float dt)
        {
            int n = scr.W;
            Bands(n);
            wfSmooth.Apply(raw, n, dt, 0.02f, 0.06f);
            int capRows = vh * 2;
            if (wfWidth != n) { history.Clear(); wfWidth = n; }

            float[] row;
            if (history.Count >= capRows) { row = history[history.Count - 1]; history.RemoveAt(history.Count - 1); }
            else row = new float[n];
            Array.Copy(wfSmooth.V, row, n);
            history.Insert(0, row);
            while (history.Count > capRows) history.RemoveAt(history.Count - 1);

            for (int r = 0; r < vh; r++)
            {
                float[] top = r * 2 < history.Count ? history[r * 2] : null;
                float[] bot = r * 2 + 1 < history.Count ? history[r * 2 + 1] : null;
                for (int x = 0; x < n; x++)
                    scr.Set(x, r, '▀', Heat(top == null ? 0 : top[x]), Heat(bot == null ? 0 : bot[x]));
            }
        }

        // Digital rain: each column is a frequency band; louder bands spawn faster, denser streams.
        void DrawRain(int vh, float dt)
        {
            int n = scr.W;
            if (rainW != n || rainH != vh)
            {
                rainCh = new char[n * vh]; rainB = new float[n * vh]; colDrops = new int[n];
                drops.Clear(); rainW = n; rainH = vh;
            }
            Bands(n);
            rainSmooth.Apply(raw, n, dt, 0.02f, 0.15f);

            float decay = dt * 1.4f * (float)Math.Sqrt(RainSpeed);
            for (int i = 0; i < rainB.Length; i++) if (rainB[i] > 0) rainB[i] = Math.Max(0, rainB[i] - decay);
            int mutations = (int)(n * vh * dt * 0.6f) + 1;
            for (int m = 0; m < mutations; m++)
            {
                int idx = rng.Next(rainB.Length);
                if (rainB[idx] > 0) rainCh[idx] = RandCode();
            }

            Array.Clear(colDrops, 0, n);
            foreach (Drop d in drops) colDrops[d.X]++;
            for (int x = 0; x < n; x++)
            {
                float v = rainSmooth.V[x];
                float rate = v * v * v * 10f * RainSpeed;
                if (colDrops[x] < 3 && rng.NextDouble() < rate * dt)
                    drops.Add(new Drop { X = x, Y = 0, LastRow = -1, Speed = (12 + 45 * v) * RainSpeed });
            }

            for (int i = drops.Count - 1; i >= 0; i--)
            {
                Drop d = drops[i];
                d.Y += d.Speed * dt * (0.6f + rainSmooth.V[d.X]);
                int row = Math.Min(vh - 1, (int)d.Y);
                for (int r = d.LastRow + 1; r <= row; r++)
                {
                    rainCh[r * n + d.X] = RandCode();
                    rainB[r * n + d.X] = 1f;
                }
                d.LastRow = row;
                if (d.Y >= vh) drops.RemoveAt(i);
            }

            for (int y = 0; y < vh; y++)
                for (int x = 0; x < n; x++)
                {
                    float b = rainB[y * n + x];
                    if (b > 0.03f) scr.Set(x, y, rainCh[y * n + x], Heat(b), -1);
                }
            int headCol = Lerp(pal[63], 0xFFFFFF, 0.7);
            foreach (Drop d in drops)
            {
                int y = (int)d.Y;
                if (y < vh) scr.Set(d.X, y, rainCh[y * n + d.X], headCol, -1);
            }
        }

        // Scrolling source code that the spectrum lights up; bass speeds the scroll, beats glitch it.
        void DrawCode(int vh, float dt)
        {
            int x0;
            int n = BarLayout(out x0), step = BarWidth + (Gap ? 1 : 0);
            Bands(n);
            barSmooth.Apply(raw, n, dt, 0.015f, Smooth);

            float bass = Average(barSmooth.V, 0, Math.Max(1, n / 8));
            beatCooldown -= dt;
            glitch -= dt;
            if (bass - bassAvg > 0.12f && beatCooldown <= 0) { glitch = 0.12f; beatCooldown = 0.2f; }
            bassAvg += (bass - bassAvg) * (1f - (float)Math.Exp(-dt / 0.5f));

            codeScroll += dt * CodeSpeed * (1.5f + 30f * bass * bass);
            while (codeScroll >= 1) { codeScroll -= 1; codeLines.Add(GenLine()); }
            while (codeLines.Count < vh) codeLines.Add(GenLine());
            if (codeLines.Count > vh) codeLines.RemoveRange(0, codeLines.Count - vh);

            int dim = Lerp(0x262626, Pal(0.5f), 0.3);
            for (int y = 0; y < vh; y++)
            {
                string line = codeLines[y];
                int b = vh - 1 - y;
                for (int x = 0; x < scr.W; x++)
                {
                    char c = x < line.Length ? line[x] : ' ';
                    int i = Clamp((x - x0) / step, 0, n - 1);
                    float v = barSmooth.V[i];
                    float h = v * vh;
                    int col;
                    if (b < h)
                    {
                        col = BarColor(b, vh, i, n, v);
                        if (b == (int)h) col = Lerp(col, 0xFFFFFF, 0.6); // bright "cursor" at the top edge
                        if (c == ' ') { c = '·'; col = Scale(col, 0.35); }
                    }
                    else col = dim;
                    if (glitch > 0 && rng.Next(30) == 0) { c = RandCode(); col = Lerp(col, 0xFFFFFF, 0.5); }
                    if (c != ' ') scr.Set(x, y, c, col, -1);
                }
            }
        }

        static readonly string[] Ids = { "buf", "signal", "fft", "freq", "gain", "sample", "peak", "phase", "bin", "node", "ptr", "ctx", "frame", "delta", "amp", "decay", "mix", "osc", "wave", "band", "rms", "env", "lfo", "voice" };
        static readonly string[] Types = { "int", "float", "u8", "f32", "char*", "void*", "bool", "double", "size_t", "uint32_t" };
        static readonly string[] Cmps = { "<", ">", "<=", ">=", "==", "!=" };
        static readonly string[] Comments = { "TODO: optimize this loop", "magic number, do not touch", "fft butterfly stage", "sync with audio thread", "here be dragons", "normalize to unit range", "hot path", "bit-twiddling hack", "unroll x4", "clamp to avoid overflow", "FIXME: race condition?", "do not remove, breaks everything" };

        string Pick(string[] a) { return a[rng.Next(a.Length)]; }
        string Num() { return rng.Next(2) == 0 ? rng.Next(2, 1024).ToString() : "0x" + rng.Next(0x10000).ToString("X4"); }

        string GenLine()
        {
            string ind = new string(' ', codeIndent * 4);
            int r = rng.Next(100);
            if (codeIndent > 0 && r < 15) { codeIndent--; return new string(' ', codeIndent * 4) + "}"; }
            if (r < 28 && codeIndent < 6)
            {
                codeIndent++;
                switch (rng.Next(6))
                {
                    case 0: return ind + "for (int i = 0; i < " + Num() + "; i++) {";
                    case 1: return ind + "if (" + Pick(Ids) + " " + Pick(Cmps) + " " + Num() + ") {";
                    case 2: return ind + "while (" + Pick(Ids) + "->" + Pick(Ids) + " != NULL) {";
                    case 3: return ind + "fn " + Pick(Ids) + "_" + Pick(Ids) + "(" + Pick(Ids) + ": " + Pick(Types) + ") -> " + Pick(Types) + " {";
                    case 4: return ind + "static " + Pick(Types) + " " + Pick(Ids) + "_" + Pick(Ids) + "(" + Pick(Types) + " " + Pick(Ids) + ") {";
                    default: return ind + "switch (" + Pick(Ids) + " & 0x" + rng.Next(256).ToString("X2") + ") {";
                }
            }
            if (r < 36) return ind + (rng.Next(2) == 0 ? "// " + Pick(Comments) : "/* " + Pick(Comments) + " */");
            if (r < 42)
            {
                var sb = new StringBuilder(ind);
                int count = rng.Next(4, 12);
                for (int i = 0; i < count; i++) sb.Append("0x").Append(rng.Next(0x10000).ToString("X4")).Append(", ");
                return sb.ToString();
            }
            switch (rng.Next(8))
            {
                case 0: return ind + Pick(Types) + " " + Pick(Ids) + " = " + Pick(Ids) + " * " + Num() + ";";
                case 1: return ind + Pick(Ids) + "[" + Pick(Ids) + "] ^= " + Pick(Ids) + " >> " + rng.Next(1, 16) + ";";
                case 2: return ind + Pick(Ids) + "." + Pick(Ids) + "(" + Pick(Ids) + ", " + Num() + ");";
                case 3: return ind + "return " + Pick(Ids) + " + " + Pick(Ids) + ";";
                case 4: return ind + Pick(Ids) + " += sin(" + Pick(Ids) + " * 6.2831f) * " + rng.NextDouble().ToString("0.000") + "f;";
                case 5: return ind + "*" + Pick(Ids) + "++ = (" + Pick(Types) + ")" + Pick(Ids) + ";";
                case 6: return ind + "memcpy(" + Pick(Ids) + ", " + Pick(Ids) + ", sizeof(" + Pick(Types) + ") * " + Num() + ");";
                default: return ind + "printf(\"%d %x\\n\", " + Pick(Ids) + ", " + Pick(Ids) + ");";
            }
        }

        // Circular spectrum, mirrored left/right, that pulses with the bass and spins.
        void DrawRadial(int vh, float dt)
        {
            int pw = scr.W * 2, ph = vh * 4;
            int n = 48;
            Bands(n);
            radSmooth.Apply(raw, n, dt, 0.015f, Smooth);
            float bass = Average(radSmooth.V, 0, n / 8);
            spin += SpinSpeed * dt * (1 + bass * 2);

            float cx = pw / 2f, cy = ph / 2f, size = Math.Min(pw, ph) / 2f;
            float r0 = size * 0.28f * (1 + 0.25f * bass), extra = size * 0.68f;
            braille.Begin(scr.W, vh);

            int ringSteps = (int)(r0 * 7) + 8;
            for (int s = 0; s < ringSteps; s++)
            {
                double a = 2 * Math.PI * s / ringSteps;
                braille.Plot((int)Math.Round(cx + Math.Cos(a) * r0), (int)Math.Round(cy + Math.Sin(a) * r0), 0.15f + bass * 0.5f);
            }
            int spokes = n * 2;
            for (int j = 0; j < spokes; j++)
            {
                float v = radSmooth.V[j < n ? j : spokes - 1 - j];
                double a = spin + 2 * Math.PI * j / spokes - Math.PI / 2;
                float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
                float len = r0 + v * extra;
                braille.Line(cx + c * r0, cy + s * r0, cx + c * len, cy + s * len, 0.2f, 0.2f + 0.8f * v);
            }
            braille.Draw(scr, t => Pal(t));
        }

        // ---------------- overlays

        void DrawStatus(int y)
        {
            string gain = GainDb.ToString("+0;-0;0") + " dB" + (AutoGain ? " auto" : "");
            string src = SourceName + (srcError != null ? " (" + srcError + ")" : "");
            string s = " DMZ TermVis | " + ModeNames[Mode] + " | " + ThemeNames[Theme] + " | " + GlyphNames[Glyphs] + " | " + src +
                       " | Gain " + gain + " | Tab settings  H help  Q quit";
            if (s.Length < scr.W) s = s.PadRight(scr.W);
            scr.Text(0, y, s, 0xB4B4BE, 0x1E1E28);
        }

        void DrawBox(int x0, int y0, int bw, int bh, int border, int bg)
        {
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                {
                    char c = ' ';
                    bool top = y == 0, bottom = y == bh - 1, left = x == 0, right = x == bw - 1;
                    if (top && left) c = '┌'; else if (top && right) c = '┐';
                    else if (bottom && left) c = '└'; else if (bottom && right) c = '┘';
                    else if (top || bottom) c = '─'; else if (left || right) c = '│';
                    scr.Set(x0 + x, y0 + y, c, border, bg);
                }
        }

        void DrawHelp()
        {
            string[] lines =
            {
                "DMZ TermVis controls",
                "",
                "M / 1-7      mode: bars, mirror, wave, waterfall,",
                "             rain, code, radial",
                "C / Shift+C  next / previous color theme",
                "K            glyph style: blocks, ascii, code, dots",
                "V            color by: height, frequency, loudness",
                "S            source: system audio / mic (Windows)",
                "Up / Down    gain +/- 2 dB",
                "A            auto-gain on/off",
                "Left / Right bar width",
                "G            gaps between bars",
                "P            peak markers",
                "[ / ]        less / more smoothing",
                "Tab or E     settings panel (all parameters)",
                "B            status bar on/off",
                "R            reset to defaults",
                "H            close this help",
                "Q / Esc      quit (settings are saved)",
            };
            int bw = 0;
            foreach (string l in lines) bw = Math.Max(bw, l.Length);
            bw += 4;
            int bh = lines.Length + 2;
            int x0 = Math.Max(0, (scr.W - bw) / 2), y0 = Math.Max(0, (scr.H - bh) / 2);
            int border = Pal(0.8f);
            DrawBox(x0, y0, bw, bh, border, 0x14141E);
            for (int i = 0; i < lines.Length; i++)
                scr.Text(x0 + 2, y0 + 1 + i, lines[i], i == 0 ? border : 0xDCDCE6, 0x14141E);
        }

        void DrawSettings()
        {
            const int nameW = 20, valW = 18;
            int bw = nameW + valW + 8;
            int visible = Math.Min(pars.Count, Math.Max(1, scr.H - 6));
            int first = Clamp(selected - visible / 2, 0, pars.Count - visible);
            int bh = visible + 4;
            int x0 = Math.Max(0, (scr.W - bw) / 2), y0 = Math.Max(0, (scr.H - bh) / 2);
            int border = Pal(0.8f), bg = 0x14141E;
            DrawBox(x0, y0, bw, bh, border, bg);
            scr.Text(x0 + 2, y0 + 1, "Settings", border, bg);
            string hint = "Up/Down select  Left/Right change  Tab close";
            scr.Text(x0 + 2, y0 + bh - 2, hint.Length > bw - 4 ? hint.Substring(0, bw - 4) : hint, 0x8C8C96, bg);
            for (int r = 0; r < visible; r++)
            {
                int i = first + r;
                bool sel = i == selected;
                int rowBg = sel ? Scale(Pal(0.7f), 0.35) : bg;
                string val = pars[i].Get();
                string text = " " + pars[i].Name.PadRight(nameW) + (sel ? "< " : "  ") + val.PadRight(valW - 4) + (sel ? " >" : "  ");
                if (text.Length > bw - 2) text = text.Substring(0, bw - 2);
                scr.Text(x0 + 1, y0 + 2 + r, text.PadRight(bw - 2), sel ? 0xFFFFFF : 0xC8C8D2, rowBg);
            }
        }

        // ---------------- small utils

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
        static double ClampD(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }
        static float Clamp01(float v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
        static int Wrap(int v, int n) { return ((v % n) + n) % n; }
        static string OnOff(bool b) { return b ? "on" : "off"; }

        static int Lerp(int a, int b, double t)
        {
            int r = (int)((a >> 16) + ((b >> 16) - (a >> 16)) * t);
            int g = (int)(((a >> 8) & 255) + (((b >> 8) & 255) - ((a >> 8) & 255)) * t);
            int bl = (int)((a & 255) + ((b & 255) - (a & 255)) * t);
            return r << 16 | g << 8 | bl;
        }

        static int Scale(int c, double k) { return Lerp(0, c, k); }
    }

    public static class Program
    {
        public static int Main(string[] args)
        {
            var viz = new Viz();
            viz.Load();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].TrimStart('-', '/').ToLowerInvariant();
                if (a == "h" || a == "help" || a == "?")
                {
                    Console.WriteLine("DMZ TermVis 1.0 - terminal audio visualizer");
                    Console.WriteLine();
                    Console.WriteLine("Usage: termvis [options]");
                    Console.WriteLine("  --mic                 Windows: visualize the microphone instead of system audio");
                    Console.WriteLine("  --system              Windows: visualize system audio (default)");
                    Console.WriteLine("                        (macOS always uses the default input device)");
                    Console.WriteLine("  --mode <name|1-7>     bars, mirror, wave, waterfall, rain, code, radial");
                    Console.WriteLine("  --theme <name>        fire, ocean, neon, matrix, rainbow, mono, synthwave,");
                    Console.WriteLine("                        cyberpunk, sunset, amber, ice, toxic, blood, vapor");
                    Console.WriteLine("  --glyphs <name>       blocks, ascii, code, dots");
                    Console.WriteLine("  --colorby <name>      height, frequency, loudness");
                    Console.WriteLine("  --gain <dB>           extra gain, -30..30");
                    Console.WriteLine("  --range <dB>          dynamic range shown, 20..90");
                    Console.WriteLine("  --lowcut <Hz>         lowest frequency, 20..500");
                    Console.WriteLine("  --highcut <Hz>        highest frequency, 2000..22000");
                    Console.WriteLine("  --tilt <dB/oct>       treble boost, 0..6");
                    Console.WriteLine("  --fps <n>             frames per second, 10..144 (default 60)");
                    Console.WriteLine("  --colors <mode>       auto, 24-bit, 256 (use 256 if colors look wrong)");
                    Console.WriteLine("  --barwidth <n>        bar width in columns, 1..8");
                    Console.WriteLine("  --reset               ignore saved settings");
                    Console.WriteLine();
                    Console.WriteLine("Press Tab while running for the settings panel, H for key controls.");
                    Console.WriteLine("Settings are saved to termvis.ini next to the program.");
                    return 0;
                }
                if (a == "mic") viz.UseMic = true;
                else if (a == "system") viz.UseMic = false;
                else if (a == "reset") viz.Defaults();
                else if (i + 1 < args.Length && viz.Apply(a, args[i + 1])) i++;
                else { Console.Error.WriteLine("Unknown option: " + args[i] + "  (try --help)"); return 1; }
            }
            if (Console.IsOutputRedirected) { Console.Error.WriteLine("TermVis needs to run in a console window."); return 1; }
            viz.Run();
            return 0;
        }
    }
}
