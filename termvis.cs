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

    // Commands run off the rendering thread; at most one request is in flight.
    sealed class MediaControls
    {
        [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        int busy;
        string lastPlayer = "";
        public volatile string Message = "";

        internal static string BuildMacScript(int action, string lastPlayer)
        {
            string command = action == 0 ? "playpause" : action == 1 ? "nextTrack" : "previousTrack";
            return "(function(){var names=['Spotify','Music'], live=[];" +
                            "for(var i=0;i<names.length;i++){try{var a=Application(names[i]);if(a.running())live.push({name:names[i],app:a});}catch(e){}}" +
                            "var target=null;for(var i=0;i<live.length;i++){if(live[i].app.playerState()==='playing'){target=live[i];break;}}" +
                            "if(!target){for(var i=0;i<live.length;i++){if(live[i].name==='" + lastPlayer + "')target=live[i];}}" +
                            "if(!target && live.length)target=live[0];" +
                            "if(target){target.app." + command + "();return target.name;}return 'No player';})()";
        }

        public void Send(int action) // 0 toggle, 1 next, 2 previous
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
            Message = "Sending player command...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (Platform.IsWindows)
                    {
                        byte key = (byte)(action == 0 ? 0xB3 : action == 1 ? 0xB0 : 0xB1);
                        keybd_event(key, 0, 1, UIntPtr.Zero);
                        keybd_event(key, 0, 3, UIntPtr.Zero);
                        Message = "Media key sent";
                    }
                    else if (Platform.IsMac)
                    {
                        // JXA resolves dictionaries only for installed, running applications.
                        string script = BuildMacScript(action, lastPlayer);
                        var info = new ProcessStartInfo("/usr/bin/osascript", "-l JavaScript")
                        {
                            UseShellExecute = false, RedirectStandardInput = true,
                            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                        };
                        using (var process = Process.Start(info))
                        {
                            process.StandardInput.Write(script);
                            process.StandardInput.Close();
                            if (!process.WaitForExit(8000))
                            {
                                process.Kill();
                                Message = "Player timed out; check macOS Automation permissions";
                            }
                            else if (process.ExitCode != 0)
                                Message = "Player control failed; check macOS Automation permissions";
                            else
                            {
                                string player = process.StandardOutput.ReadToEnd().Trim();
                                if (player == "Spotify" || player == "Music")
                                {
                                    lastPlayer = player;
                                    Message = player + ": " + (action == 0 ? "play/pause" : action == 1 ? "next track" : "previous track");
                                }
                                else Message = "Open Spotify or Music to control playback";
                            }
                        }
                    }
                    else Message = "Playback controls require Windows or macOS";
                }
                catch (Exception) { Message = "Could not send player command"; }
                finally { Interlocked.Exchange(ref busy, 0); }
            });
        }
    }

    interface IAudioSource : IDisposable
    {
        int SampleRate { get; }
        int Read(AudioRings rings);   // drains pending audio into the rings, returns frames read
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
        readonly float[] pending = new float[Rate * 2];          // interleaved L/R
        readonly float[] scratch = new float[BufferFrames * 2];
        int pendingCount;
        IntPtr queue;
        volatile bool running;

        public int SampleRate { get { return Rate; } }

        public MacAudioCapture()
        {
            var fmt = new StreamFormat
            {
                SampleRate = Rate, FormatID = LinearPCM, FormatFlags = FloatPacked,
                BytesPerPacket = 8, FramesPerPacket = 1, BytesPerFrame = 8, ChannelsPerFrame = 2, BitsPerChannel = 32,
            };
            callback = OnInput;
            // A null run loop makes the queue call back on its own internal thread.
            Check(AudioQueueNewInput(ref fmt, callback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out queue), "AudioQueueNewInput");
            for (int i = 0; i < BufferCount; i++)
            {
                IntPtr buf;
                Check(AudioQueueAllocateBuffer(queue, BufferFrames * 8, out buf), "AudioQueueAllocateBuffer");
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
            int count = Math.Min(scratch.Length, Marshal.ReadInt32(buffer, IntPtr.Size * 2) / 8 * 2);
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

        public int Read(AudioRings rings)
        {
            lock (gate)
            {
                for (int i = 0; i + 1 < pendingCount; i += 2) rings.Push(pending[i], pending[i + 1]);
                int n = pendingCount / 2;
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

        // Drains every pending packet into the rings. Returns frames read.
        public int Read(AudioRings rings)
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
                    for (int i = 0; i < n; i++) rings.Push(0f, 0f);
                else
                    Convert(data, n, rings);
                capture.ReleaseBuffer(frames);
                total += n;
                capture.GetNextPacketSize(out packet);
            }
            return total;
        }

        // Converts any supported sample format to floats, then pushes channel 0 as left and channel 1 as right.
        void Convert(IntPtr data, int frames, AudioRings rings)
        {
            int count = frames * channels;
            if (fbuf.Length < count) fbuf = new float[count];
            if (isFloat && bits == 32)
                Marshal.Copy(data, fbuf, 0, count);
            else if (bits == 16)
            {
                if (sbuf.Length < count) sbuf = new short[count];
                Marshal.Copy(data, sbuf, 0, count);
                for (int i = 0; i < count; i++) fbuf[i] = sbuf[i] / 32768f;
            }
            else if (bits == 32)
            {
                if (ibuf.Length < count) ibuf = new int[count];
                Marshal.Copy(data, ibuf, 0, count);
                for (int i = 0; i < count; i++) fbuf[i] = ibuf[i] / 2147483648f;
            }
            else if (bits == 24)
            {
                int bytes = count * 3;
                if (bbuf.Length < bytes) bbuf = new byte[bytes];
                Marshal.Copy(data, bbuf, 0, bytes);
                for (int i = 0, j = 0; i < count; i++, j += 3)
                    fbuf[i] = ((bbuf[j] << 8 | bbuf[j + 1] << 16 | bbuf[j + 2] << 24) >> 8) / 8388608f;
            }
            else return;

            for (int f = 0; f < frames; f++)
            {
                int i = f * channels;
                float l = fbuf[i], r = channels > 1 ? fbuf[i + 1] : l;
                rings.Push(l, r);
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
        public long Written; // total samples ever pushed

        public Ring(int sizePow2) { buf = new float[sizePow2]; mask = sizePow2 - 1; }
        public int Size { get { return buf.Length; } }

        public void Push(float v) { buf[pos] = v; pos = (pos + 1) & mask; Written++; }

        // Copies the newest n samples into dst, oldest first.
        public void CopyLatest(float[] dst, int n)
        {
            int start = (pos - n) & mask;
            for (int i = 0; i < n; i++) dst[i] = buf[(start + i) & mask];
        }
    }

    // Mono mix for the spectrum modes plus separate left/right for the XY scope.
    sealed class AudioRings
    {
        public readonly Ring Mono, Left, Right;

        public AudioRings(int sizePow2) { Mono = new Ring(sizePow2); Left = new Ring(sizePow2); Right = new Ring(sizePow2); }

        public void Push(float l, float r)
        {
            Mono.Push((l + r) * 0.5f);
            Left.Push(l);
            Right.Push(r);
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
        public readonly Func<bool> Visible;
        public Param(string name, Func<string> get, Action<int> adjust, Func<bool> visible = null)
        { Name = name; Get = get; Adjust = adjust; Visible = visible; }
    }

    // ---------------------------------------------------------------- Visualizer

    sealed class Viz
    {
        static readonly string[] ModeNames = { "Bars", "Mirror", "Wave", "Waterfall", "Rain", "Code", "Radial", "Scope",
            "Retro", "Warp", "Tunnel", "Nebula", "Radar", "Reactor", "Ocean", "Reef", "Jellyfish", "Aurora", "Kaleido", "Tendrils", "Cells", "Terrain" };
        // T applies a suggested palette; changing modes keeps the user's chosen colors.
        static readonly string[] ModeThemes = { "Fire", "Ocean", "Phosphor", "Ice", "Matrix", "Cyberpunk", "Neon", "Phosphor",
            "Arcade", "Starlight", "Laser", "Cosmic", "Sonar", "Plasma", "Lagoon", "Coral", "Abyss", "Borealis", "Neon", "Ocean", "Toxic", "Phosphor" };
        static readonly string[] ThemeNames =
        {
            "Fire", "Ocean", "Neon", "Matrix", "Rainbow", "Mono",
            "Synthwave", "Cyberpunk", "Sunset", "Amber", "Ice", "Toxic", "Blood", "Vapor", "Phosphor",
            "Arcade", "Starlight", "Laser", "Cosmic", "Sonar", "Plasma", "Lagoon", "Coral", "Abyss", "Borealis",
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
            new[] { 0x002A10, 0x00A040, 0x40FF80, 0xE0FFEA }, // P31 green CRT phosphor
            new[] { 0x190B46, 0x7336D9, 0xFF387C, 0xFFB94A, 0xFFF1A8 },
            new[] { 0x100C38, 0x4841AB, 0x609EFF, 0xB8E7FF, 0xFFFFFF },
            new[] { 0x071B42, 0x006E95, 0x00E5FF, 0xA84DFF, 0xFFE8FF },
            new[] { 0x160A35, 0x542A98, 0xB345C8, 0xF27CA8, 0xFFE3C8 },
            new[] { 0x021C16, 0x087650, 0x20D890, 0xACFFAF, 0xEDFFE1 },
            new[] { 0x200B46, 0x6414D9, 0xD52EFF, 0xFF8B37, 0xFFF6AE },
            new[] { 0x021C38, 0x006B87, 0x00BFA6, 0x76EDCE, 0xE0FFF6 },
            new[] { 0x071C47, 0x216A98, 0xEF638A, 0xFFAB80, 0xFFF0BC },
            new[] { 0x020C26, 0x173975, 0x4A65DB, 0x5BE5DF, 0xD0FFF9 },
            new[] { 0x101636, 0x205B72, 0x28D79C, 0xA0F4BA, 0xDB9FFF },
        };
        static readonly string[] GlyphNames = { "Blocks", "ASCII", "Code", "Dots" };
        static readonly string[] ColorByNames = { "Height", "Frequency", "Loudness" };
        static readonly string[] ColorModeNames = { "Auto", "24-bit", "256" };
        static readonly int[] FpsSteps = { 30, 45, 60, 90, 120, 144 };
        const string EIGHTHS = " ▁▂▃▄▅▆▇█";
        const string ASCII9 = " .:-=+*%#";
        const string PEAKCHARS = "▔-=⠉";
        // The classic Windows console (conhost) has no glyph for ▔, so use ▀ there; Windows Terminal sets WT_SESSION.
        static readonly char BlockPeak = Platform.IsWindows && Environment.GetEnvironmentVariable("WT_SESSION") == null ? '▀' : '▔';
        const string CODECHARS = "0123456789ABCDEFabcdef{}[]()<>;:=+-*/&|!?#$%@_~^";
        static readonly int[] DotFill = { 0, 0xC0, 0xC0, 0xE4, 0xE4, 0xF6, 0xF6, 0xFF, 0xFF };
        const int N = 4096; // FFT size

        // ---- settings (saved to termvis.ini)
        public int Mode, Theme, BarWidth, Fps, Glyphs, ColorBy, ColorMode;
        public double GainDb, RangeDb, MinHz, MaxHz, Tilt;
        public float Smooth, PeakGravity, RainSpeed, CodeSpeed, SpinSpeed;
        public float ScopeDecay, BeamIntensity, ScopeZoom;
        sealed class EffectOption
        {
            public int Mode;
            public string Key, Name;
            public float Value, Default, Min, Max, Step;
            public bool Density;
            public void Set(float value) { Value = Math.Max(Min, Math.Min(Max, Step >= 1 ? (float)Math.Round(value) : value)); }
            public string Display() { return Step >= 1 ? Value.ToString("0") : "x" + Value.ToString("0.00"); }
        }
        readonly List<EffectOption> effectOptions = new List<EffectOption>();
        readonly Dictionary<string, EffectOption> optionKeys = new Dictionary<string, EffectOption>();
        readonly Dictionary<string, EffectOption>[] modeOptions = new Dictionary<string, EffectOption>[ModeNames.Length];
        public float EffectSpeed
        {
            get { return modeOptions[Math.Max(8, Mode)]["speed"].Value; }
            set { modeOptions[Math.Max(8, Mode)]["speed"].Set(value); }
        }
        // Compatibility with the first scene release's detail multiplier.
        public float EffectDetail
        {
            get { foreach (EffectOption o in effectOptions) if (o.Mode == Math.Max(8, Mode) && o.Density) return o.Value / o.Default; return 1; }
            set { foreach (EffectOption o in effectOptions) if (o.Mode == Math.Max(8, Mode) && o.Density) o.Set(o.Default * value); }
        }
        public bool AutoGain, Gap, Peaks, UseMic, ShowStatus, ScopeGrid, ScopeSmooth;

        // ---- runtime state
        readonly Screen scr = new Screen();
        readonly AudioRings rings = new AudioRings(16384);
        Ring ring { get { return rings.Mono; } }
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
        readonly double[] effectTimes = new double[ModeNames.Length];
        double effectTime { get { return effectTimes[Mode]; } set { effectTimes[Mode] = value; } }
        float waterfallClock;
        readonly Smoother effectSmooth = new Smoother();
        double dreamTime { get { return effectTime; } set { effectTime = value; } }
        readonly Smoother dreamSmooth = new Smoother();
        readonly MediaControls media = new MediaControls();
        float mediaNotice;


        // scope state: beam energy per braille dot, decaying like phosphor
        float[] glow = new float[0];
        float[] scopeL = new float[0], scopeR = new float[0];
        int glowW, glowH;
        long scopeRead;

        public Viz()
        {
            CreateEffectOptions();
            Defaults();
            for (int i = 0; i < N; i++) hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1));
            pars = new List<Param>
            {
                new Param("Mode", () => ModeNames[Mode], d => Mode = Wrap(Mode + d, ModeNames.Length)),
                new Param("Color theme", () => ThemeNames[Theme], d => { Theme = Wrap(Theme + d, ThemeNames.Length); BuildPalette(); }),
                new Param("Suggested colors", () => ModeThemes[Mode] + " (T)", d => SuggestedTheme()),
                new Param("Glyphs (bars)", () => GlyphNames[Glyphs], d => Glyphs = Wrap(Glyphs + d, GlyphNames.Length), () => Mode <= 1),
                new Param("Color by", () => ColorByNames[ColorBy], d => ColorBy = Wrap(ColorBy + d, ColorByNames.Length), () => Mode <= 1 || Mode == 5),
                new Param("Source", () => SourceName, d => ToggleSource()),
                new Param("Gain", () => GainDb.ToString("+0;-0;0") + " dB", d => GainDb = ClampD(GainDb + 2 * d, -30, 30)),
                new Param("Auto-gain", () => OnOff(AutoGain), d => AutoGain = !AutoGain, () => Mode != 7),
                new Param("Dynamic range", () => RangeDb.ToString("0") + " dB", d => RangeDb = ClampD(RangeDb + 5 * d, 20, 90), () => Mode != 2 && Mode != 7),
                new Param("Low cut", () => MinHz.ToString("0") + " Hz", d => MinHz = ClampD(Math.Round(MinHz * Math.Pow(1.25, d)), 20, 500), () => Mode != 2 && Mode != 7),
                new Param("High cut", () => (MaxHz / 1000).ToString("0.0") + " kHz", d => MaxHz = ClampD(MaxHz * Math.Pow(1.15, d), 2000, 22000), () => Mode != 2 && Mode != 7),
                new Param("Treble boost", () => Tilt.ToString("0.0") + " dB/oct", d => Tilt = ClampD(Tilt + 0.5 * d, 0, 6), () => Mode != 2 && Mode != 7),
                new Param("Smoothing", () => Smooth.ToString("0.00") + " s", d => Smooth = (float)ClampD(Smooth * Math.Pow(1.25, d), 0.02, 1), () => Mode != 2 && Mode != 3 && Mode != 4 && Mode != 7),
                new Param("Bar width", () => BarWidth.ToString(), d => BarWidth = Clamp(BarWidth + d, 1, 8), () => Mode <= 1 || Mode == 5),
                new Param("Bar gaps", () => OnOff(Gap), d => Gap = !Gap, () => Mode <= 1 || Mode == 5),
                new Param("Peak markers", () => OnOff(Peaks), d => Peaks = !Peaks, () => Mode <= 1),
                new Param("Peak gravity", () => PeakGravity.ToString("0.00"), d => PeakGravity = (float)ClampD(PeakGravity + 0.25 * d, 0.25, 6), () => Mode <= 1 && Peaks),
                new Param("Rain speed", () => "x" + RainSpeed.ToString("0.00"), d => RainSpeed = (float)ClampD(RainSpeed * Math.Pow(1.25, d), 0.25, 4), () => Mode == 4),
                new Param("Code scroll speed", () => "x" + CodeSpeed.ToString("0.00"), d => CodeSpeed = (float)ClampD(CodeSpeed * Math.Pow(1.25, d), 0.25, 4), () => Mode == 5),
                new Param("Radial spin", () => SpinSpeed.ToString("+0.0;-0.0;0.0"), d => SpinSpeed = (float)ClampD(Math.Round(SpinSpeed + 0.1 * d, 1), -3, 3), () => Mode == 6),
                new Param("Scope zoom", () => "x" + ScopeZoom.ToString("0.00"), d => ScopeZoom = (float)ClampD(ScopeZoom * Math.Pow(1.25, d), 0.25, 8), () => Mode == 7),
                new Param("Scope persistence", () => (ScopeDecay * 1000).ToString("0") + " ms", d => ScopeDecay = (float)ClampD(ScopeDecay * Math.Pow(1.4, d), 0.005, 2), () => Mode == 7),
                new Param("Beam intensity", () => "x" + BeamIntensity.ToString("0.00"), d => BeamIntensity = (float)ClampD(BeamIntensity * Math.Pow(1.4, d), 0.05, 20), () => Mode == 7),
                new Param("Scope smooth beam", () => OnOff(ScopeSmooth), d => ScopeSmooth = !ScopeSmooth, () => Mode == 7),
                new Param("Scope graticule", () => OnOff(ScopeGrid), d => ScopeGrid = !ScopeGrid, () => Mode == 7),
                new Param("Frame rate", () => Fps + " fps", d => Fps = FpsSteps[Clamp(Array.IndexOf(FpsSteps, Fps) + d, 0, FpsSteps.Length - 1)]),
                new Param("Terminal colors", () => ColorModeNames[ColorMode], d => ColorMode = Wrap(ColorMode + d, ColorModeNames.Length)),
                new Param("Status bar", () => OnOff(ShowStatus), d => ShowStatus = !ShowStatus),
            };
            var scenePars = new List<Param>();
            foreach (EffectOption option in effectOptions)
            {
                EffectOption o = option;
                scenePars.Add(new Param(o.Name, () => o.Display(), d => o.Set(o.Value + o.Step * d), () => Mode == o.Mode));
            }
            scenePars.Add(new Param("Reset this effect", () => "< apply >", d => ResetEffect()));
            pars.InsertRange(3, scenePars);
        }

        List<Param> ActiveParams()
        {
            var active = new List<Param>();
            foreach (Param p in pars) if (p.Visible == null || p.Visible()) active.Add(p);
            selected = Clamp(selected, 0, active.Count - 1);
            return active;
        }

        void AddOption(int mode, string key, string name, float value, float min, float max, float step, bool density = false)
        {
            var option = new EffectOption { Mode = mode, Key = ModeNames[mode].ToLowerInvariant() + "." + key,
                Name = name, Value = value, Default = value, Min = min, Max = max, Step = step, Density = density };
            effectOptions.Add(option); optionKeys.Add(option.Key, option); modeOptions[mode].Add(key, option);
        }

        float SceneValue(string key) { return modeOptions[Mode][key].Value; }

        void CreateEffectOptions()
        {
            for (int i = 0; i < modeOptions.Length; i++) modeOptions[i] = new Dictionary<string, EffectOption>();
            AddOption(2, "zoom", "Wave zoom", 1, 0.25f, 4, 0.25f);
            AddOption(3, "speed", "Scroll speed", 1, 0.25f, 4, 0.25f);
            AddOption(4, "density", "Rain density", 1, 0.25f, 4, 0.25f);
            AddOption(4, "trails", "Trail length", 1, 0.25f, 4, 0.25f);
            AddOption(5, "glitch", "Glitch amount", 1, 0, 3, 0.25f);
            AddOption(6, "bands", "Spectrum bands", 48, 12, 96, 4);
            AddOption(6, "radius", "Ring size", 1, 0.5f, 2, 0.1f);
            for (int i = 8; i < ModeNames.Length; i++)
            {
                AddOption(i, "speed", "Motion speed", 1, 0.25f, 4, 0.25f);
                AddOption(i, "response", "Audio response", 1, 0, 2, 0.1f);
            }
            AddOption(8, "grid", "Grid lines", 14, 4, 24, 1);
            AddOption(8, "sun", "Sun size", 1, 0.5f, 1.8f, 0.1f);
            AddOption(9, "stars", "Star count", 170, 20, 1000, 10, true);
            AddOption(9, "streak", "Streak length", 1, 0.1f, 3, 0.1f);
            AddOption(9, "spread", "Star spread", 1, 0.5f, 2, 0.1f);
            AddOption(10, "rings", "Tunnel rings", 15, 3, 40, 1, true);
            AddOption(10, "sides", "Polygon sides", 12, 3, 24, 1);
            AddOption(10, "twist", "Tunnel twist", 1, -2, 2, 0.1f);
            AddOption(11, "particles", "Particle count", 650, 100, 2000, 50, true);
            AddOption(11, "arms", "Galaxy arms", 4, 1, 8, 1);
            AddOption(11, "spread", "Arm spread", 1, 0.25f, 2, 0.25f);
            AddOption(12, "contacts", "Contact count", 40, 5, 150, 5, true);
            AddOption(12, "rings", "Range rings", 4, 1, 8, 1);
            AddOption(12, "trail", "Sweep trail", 1, 0.25f, 2, 0.25f);
            AddOption(13, "rings", "Reactor rings", 5, 1, 8, 1);
            AddOption(13, "rotation", "Ring rotation", 1, 0.25f, 3, 0.25f);
            AddOption(13, "core", "Core size", 1, 0.5f, 2, 0.1f);
            AddOption(14, "layers", "Wave layers", 14, 3, 40, 1, true);
            AddOption(14, "height", "Wave height", 1, 0.25f, 3, 0.25f);
            AddOption(14, "wavelength", "Wavelength", 1, 0.5f, 2, 0.1f);
            AddOption(15, "plants", "Plant count", 32, 4, 80, 2, true);
            AddOption(15, "bubbles", "Bubble count", 36, 0, 150, 5, true);
            AddOption(15, "sway", "Plant sway", 1, 0, 3, 0.25f);
            AddOption(16, "count", "Jellyfish count", 5, 1, 20, 1, true);
            AddOption(16, "size", "Jellyfish size", 1, 0.5f, 2, 0.1f);
            AddOption(16, "tentacles", "Tentacle count", 7, 2, 16, 1);
            AddOption(16, "length", "Tentacle length", 1, 0.25f, 2, 0.25f);
            AddOption(17, "curtains", "Light curtains", 4, 1, 10, 1, true);
            AddOption(17, "height", "Curtain height", 1, 0.25f, 3, 0.25f);
            AddOption(17, "stars", "Star count", 65, 0, 300, 10);
            AddOption(18, "folds", "Mirror folds", 8, 3, 16, 1);
            AddOption(19, "limbs", "Tendril count", 36, 8, 72, 4, true);
            AddOption(20, "cells", "Cell count", 7, 1, 8, 1, true);
            AddOption(21, "layers", "Terrain layers", 30, 5, 60, 5, true);
        }

        void ResetEffect()
        {
            foreach (EffectOption o in effectOptions) if (o.Mode == Mode) o.Value = o.Default;
            switch (Mode)
            {
                case 0: case 1: BarWidth = 2; Glyphs = 0; ColorBy = 0; Gap = Peaks = true; PeakGravity = 1.5f; break;
                case 4: RainSpeed = 1; break;
                case 5: CodeSpeed = 1; BarWidth = 2; Gap = true; ColorBy = 0; break;
                case 6: SpinSpeed = 0.3f; break;
                case 7: ScopeZoom = 1; ScopeDecay = 0.06f; BeamIntensity = 1; ScopeSmooth = ScopeGrid = true; break;
            }
        }

        public void Defaults()
        {
            Mode = 0; Theme = 0; BarWidth = 2; Fps = 60; Glyphs = 0; ColorBy = 0; ColorMode = 0;
            GainDb = 0; RangeDb = 50; MinHz = 30; MaxHz = 16000; Tilt = 3;
            Smooth = 0.12f; PeakGravity = 1.5f; RainSpeed = 1; CodeSpeed = 1; SpinSpeed = 0.3f;
            ScopeDecay = 0.06f; BeamIntensity = 1; ScopeZoom = 1; ScopeGrid = true; ScopeSmooth = true;
            foreach (EffectOption o in effectOptions) o.Value = o.Default;
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
            string text = string.Join(Environment.NewLine, new[]
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
                    "scopezoom=" + ScopeZoom.ToString(inv),
                    "scopedecay=" + ScopeDecay.ToString(inv),
                    "beam=" + BeamIntensity.ToString(inv),
                    "scopesmooth=" + ScopeSmooth,
                    "scopegrid=" + ScopeGrid,
                    "status=" + ShowStatus,
                    "fps=" + Fps,
                }) + Environment.NewLine;
            foreach (EffectOption o in effectOptions) text += o.Key + "=" + o.Value.ToString(inv) + Environment.NewLine;
            return text;
        }

        public bool Apply(string key, string val)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string v = val.ToLowerInvariant();
            double d;
            bool ok = double.TryParse(val, System.Globalization.NumberStyles.Float, inv, out d) && !double.IsNaN(d) && !double.IsInfinity(d);
            bool b = v == "true" || v == "1" || v == "yes" || v == "on";
            EffectOption option;
            if (optionKeys.TryGetValue(key, out option)) { if (ok) option.Set((float)d); return ok; }
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
                case "effectspeed": if (ok) foreach (EffectOption o in effectOptions) if (o.Mode >= 8 && o.Key.EndsWith(".speed")) o.Set((float)d); return ok;
                case "effectdetail": if (ok) foreach (EffectOption o in effectOptions) if (o.Density) o.Set(o.Default * (float)ClampD(d, 0.5, 2)); return ok;
                case "scopezoom": if (ok) ScopeZoom = (float)ClampD(d, 0.25, 8); return ok;
                case "scopedecay": if (ok) ScopeDecay = (float)ClampD(d, 0.005, 2); return ok;
                case "beam": if (ok) BeamIntensity = (float)ClampD(d, 0.05, 20); return ok;
                case "scopesmooth": ScopeSmooth = b; return true;
                case "scopegrid": ScopeGrid = b; return true;
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
                    List<Param> active = ActiveParams();
                    switch (k.Key)
                    {
                        case ConsoleKey.UpArrow: selected = Wrap(selected - 1, active.Count); continue;
                        case ConsoleKey.DownArrow: selected = Wrap(selected + 1, active.Count); continue;
                        case ConsoleKey.LeftArrow: active[selected].Adjust(-1); continue;
                        case ConsoleKey.RightArrow: active[selected].Adjust(1); continue;
                        case ConsoleKey.Tab: case ConsoleKey.Escape: case ConsoleKey.Enter: showSettings = false; continue;
                    }
                }
                switch (k.Key)
                {
                    case ConsoleKey.Q: case ConsoleKey.Escape: quit = true; break;
                    case ConsoleKey.Tab: case ConsoleKey.E: showSettings = !showSettings; showHelp = false; break;
                    case ConsoleKey.M: Mode = Wrap(Mode + ((k.Modifiers & ConsoleModifiers.Shift) != 0 || k.KeyChar == 'M' ? -1 : 1), ModeNames.Length); break;
                    case ConsoleKey.T: SuggestedTheme(); break;
                    case ConsoleKey.Spacebar: media.Send(0); mediaNotice = 10; break;
                    case ConsoleKey.N: media.Send(1); mediaNotice = 10; break;
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
                    case ConsoleKey.P:
                        if ((k.Modifiers & ConsoleModifiers.Shift) != 0) Peaks = !Peaks;
                        else { media.Send(2); mediaNotice = 10; }
                        break;
                    case ConsoleKey.B: ShowStatus = !ShowStatus; break;
                    case ConsoleKey.H: showHelp = !showHelp; showSettings = false; break;
                    case ConsoleKey.R:
                        bool mic = UseMic;
                        Defaults(); BuildPalette();
                        if (mic != UseMic) reopen = true;
                        break;
                    default:
                        // matched by character since [ and ] map to different ConsoleKeys across platforms
                        if (k.KeyChar >= '1' && k.KeyChar <= '8') Mode = k.KeyChar - '1';
                        else if (k.KeyChar == '9') Mode = 18;
                        else if (k.KeyChar == '0') Mode = 19;
                        else if (k.KeyChar == '-') Mode = 20;
                        else if (k.KeyChar == '=') Mode = 21;
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
                if (cap.Read(rings) > 0) lastData = now;
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
            for (int i = 0; i < n; i++) rings.Push(0f, 0f);
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

        void SuggestedTheme()
        {
            Theme = IndexOf(ThemeNames, ModeThemes[Mode].ToLowerInvariant());
            BuildPalette();
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
            mediaNotice = Math.Max(0, mediaNotice - dt);
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
                    case 7: DrawScope(vh, dt); break;
                    default: if (Mode < 18) DrawEffect(vh, dt); else DrawDream(vh, dt); break;
                }
            }
            if (ShowStatus) DrawStatus(h - 1);
            if (mediaNotice > 0) scr.Text(0, Math.Max(0, h - (ShowStatus ? 2 : 1)), " " + media.Message + " ", 0xFFFFFF, 0x242438);
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
                        for (int dx = 0; dx < bw; dx++) scr.Set(xs + dx, up - 1 - pb, Glyphs == 0 ? BlockPeak : PEAKCHARS[Glyphs], peakCol, -1);
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

            scale *= SceneValue("zoom");
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

            waterfallClock += dt * 60 * SceneValue("speed");
            int rows = (int)waterfallClock;
            waterfallClock -= rows;
            for (int slice = 0; slice < rows; slice++)
            {
                float[] row;
                if (history.Count >= capRows) { row = history[history.Count - 1]; history.RemoveAt(history.Count - 1); }
                else row = new float[n];
                Array.Copy(wfSmooth.V, row, n);
                history.Insert(0, row);
            }
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

            float decay = dt * 1.4f * (float)Math.Sqrt(RainSpeed) / SceneValue("trails");
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
                float rate = v * v * v * 10f * RainSpeed * SceneValue("density");
                if (colDrops[x] < Math.Max(1, (int)(3 * SceneValue("density"))) && rng.NextDouble() < rate * dt)
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
            if (bass - bassAvg > 0.12f && beatCooldown <= 0) { glitch = 0.12f * SceneValue("glitch"); beatCooldown = 0.2f; }
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
            int n = (int)SceneValue("bands");
            Bands(n);
            radSmooth.Apply(raw, n, dt, 0.015f, Smooth);
            float bass = Average(radSmooth.V, 0, n / 8);
            spin += SpinSpeed * dt * (1 + bass * 2);

            float cx = pw / 2f, cy = ph / 2f, size = Math.Min(pw, ph) / 2f;
            float r0 = size * 0.28f * SceneValue("radius") * (1 + 0.25f * bass), extra = size * 0.68f;
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

        // Scene effects share a smoothed spectrum, but use independent geometry and motion.
        // Time is measured in seconds so speed stays consistent at every frame rate.
        void DrawEffect(int vh, float dt)
        {
            Bands(64);
            effectSmooth.Apply(raw, 64, dt, 0.025f, Smooth);
            float response = SceneValue("response");
            float bass = Clamp01(Average(effectSmooth.V, 0, 10) * response), energy = Clamp01(Average(effectSmooth.V, 0, 64) * response);
            effectTime += dt * EffectSpeed * (0.35 + bass * 1.3);
            braille.Begin(scr.W, vh);
            switch (Mode)
            {
                case 8: DrawRetro(vh, bass); break;
                case 9: DrawWarp(vh, bass); break;
                case 10: DrawTunnel(vh, bass); break;
                case 11: DrawNebula(vh, energy); break;
                case 12: DrawRadar(vh, energy); break;
                case 13: DrawReactor(vh, bass); break;
                case 14: DrawOcean(vh, bass); break;
                case 15: DrawReef(vh, energy); break;
                case 16: DrawJellyfish(vh, bass); break;
                case 17: DrawAurora(vh, energy); break;
            }
            braille.Draw(scr, v => Scale(Pal(v), 0.35 + 0.65 * v));
        }

        float Band(float position)
        {
            return Clamp01(effectSmooth.V[Clamp((int)(position * 63), 0, 63)] * SceneValue("response"));
        }

        static double Frac(double x) { return x - Math.Floor(x); }
        static float Seed(int i) { return (Hash(i) % 100000) / 100000f; }

        void Circle(float cx, float cy, float radius, float color, int sides)
        {
            for (int i = 0; i < sides; i++)
            {
                double a = i * Math.PI * 2 / sides, b = (i + 1) * Math.PI * 2 / sides;
                braille.Line(cx + (float)Math.Cos(a) * radius, cy + (float)Math.Sin(a) * radius,
                    cx + (float)Math.Cos(b) * radius, cy + (float)Math.Sin(b) * radius, color, color);
            }
        }

        // An outrun horizon: striped sun, moving perspective grid and spectrum mountains.
        void DrawRetro(int vh, float bass)
        {
            float w = scr.W * 2, h = vh * 4, horizon = h * 0.48f, cx = w / 2;
            float r = Math.Min(w * 0.19f, h * 0.27f) * SceneValue("sun") * (1 + bass * 0.12f), cy = horizon - r * 0.65f;
            for (int y = (int)(cy - r); y < cy + r; y++)
            {
                if (y > cy && y % 6 >= 3) continue;
                float dy = y - cy, dx = (float)Math.Sqrt(Math.Max(0, r * r - dy * dy));
                braille.Line(cx - dx, y, cx + dx, y, 0.65f + (cy - y) / r * 0.25f, 0.9f);
            }
            for (int i = -8; i <= 8; i++)
                braille.Line(cx + i * w * 0.025f, horizon, cx + i * w * 0.22f, h - 1, 0.12f, 0.6f);
            int grid = (int)SceneValue("grid");
            for (int i = 0; i < grid; i++)
            {
                float z = (float)Frac(i / (double)grid + effectTime * 0.28);
                float y = horizon + z * z * (h - horizon);
                braille.Line(0, y, w - 1, y, 0.15f + z * 0.4f, 0.15f + z * 0.4f);
            }
            float px = 0, py = horizon;
            for (int i = 0; i < 64; i++)
            {
                float x = i * (w - 1) / 63, y = horizon - Band(i / 63f) * h * 0.21f;
                if (i > 0) braille.Line(px, py, x, y, 0.7f, 0.85f);
                px = x; py = y;
            }
        }

        // Deterministic 3D stars recycle in depth; nearer stars leave longer streaks.
        void DrawWarp(int vh, float bass)
        {
            float w = scr.W * 2, h = vh * 4, cx = w / 2, cy = h / 2;
            int count = (int)SceneValue("stars");
            for (int i = 0; i < count; i++)
            {
                double a = Seed(i * 7 + 1) * Math.PI * 2;
                float z = 0.12f + (float)Frac(Seed(i * 7 + 2) - effectTime * 0.38) * 3.8f;
                float spread = (0.08f + Seed(i * 7 + 3) * 0.9f) * SceneValue("spread");
                float dx = (float)Math.Cos(a) * w * spread, dy = (float)Math.Sin(a) * h * spread;
                float tail = z + (0.025f + bass * 0.2f + Band(Seed(i + 9)) * 0.12f) * SceneValue("streak");
                float x = cx + dx / z, y = cy + dy / z;
                // Offscreen stars do not need expensive clipped line traversal.
                if (x < 0 || x >= w || y < 0 || y >= h) continue;
                braille.Line(cx + dx / tail, cy + dy / tail, x, y, 0.25f, 0.55f + 0.45f / (1 + z));
            }
        }

        void DrawTunnel(int vh, float bass)
        {
            float w = scr.W * 2, h = vh * 4, size = Math.Min(w, h) * 0.9f;
            int rings = (int)SceneValue("rings");
            for (int j = rings; j >= 0; j--)
            {
                float z = (float)Frac(j / (double)(rings + 1) + effectTime * 0.22);
                float radius = 3 + z * z * size;
                float cx = w / 2 + (float)Math.Sin(effectTime * 0.7 + z * 2) * w * 0.08f * z;
                float cy = h / 2 + (float)Math.Cos(effectTime * 0.6 + z * 2) * h * 0.08f * z;
                int sides = (int)SceneValue("sides");
                for (int k = 0; k < sides; k++)
                {
                    double a = k * Math.PI * 2 / sides + (effectTime * 0.25 + z) * SceneValue("twist"), b = a + Math.PI * 2 / sides;
                    float r0 = radius * (1 + bass * 0.08f + Band(k / (float)(sides - 1)) * 0.16f);
                    float r1 = radius * (1 + bass * 0.08f + Band((k + 1) % sides / (float)(sides - 1)) * 0.16f);
                    braille.Line(cx + (float)Math.Cos(a) * r0, cy + (float)Math.Sin(a) * r0,
                        cx + (float)Math.Cos(b) * r1, cy + (float)Math.Sin(b) * r1, 0.15f + z * 0.8f, 0.15f + z * 0.8f);
                }
            }
        }

        // A rotating spiral galaxy: each arm ripples to a different part of the spectrum.
        void DrawNebula(int vh, float energy)
        {
            float w = scr.W * 2, h = vh * 4, size = Math.Min(w, h) * 0.48f;
            int count = (int)SceneValue("particles");
            for (int i = 0; i < count; i++)
            {
                float r = (float)Math.Sqrt(Seed(i * 3 + 1));
                int arms = (int)SceneValue("arms");
                double a = i % arms * Math.PI * 2 / arms + r * 5 - effectTime * 0.35 + (Seed(i * 3 + 2) - 0.5) * 0.55 * SceneValue("spread");
                float ripple = 1 + Band(r) * 0.22f;
                float x = w / 2 + (float)Math.Cos(a) * r * size * ripple;
                float y = h / 2 + (float)Math.Sin(a) * r * size * 0.65f * ripple;
                braille.Plot((int)x, (int)y, 0.25f + (1 - r) * 0.55f + energy * 0.2f);
            }
            Circle(w / 2, h / 2, 2 + energy * 4, 1, 24);
        }

        void DrawRadar(int vh, float energy)
        {
            float cx = scr.W, cy = vh * 2, size = Math.Min(scr.W * 2, vh * 4) * 0.46f;
            int rings = (int)SceneValue("rings");
            for (int j = 1; j <= rings; j++) Circle(cx, cy, size * j / rings, 0.12f, 100);
            braille.Line(cx - size, cy, cx + size, cy, 0.12f, 0.12f);
            braille.Line(cx, cy - size, cx, cy + size, 0.12f, 0.12f);
            double sweep = effectTime * 1.5;
            for (int j = 0; j < 24; j++)
            {
                double a = sweep - j * 0.024 * SceneValue("trail");
                float c = 0.15f + (1 - j / 24f) * 0.65f;
                braille.Line(cx, cy, cx + (float)Math.Cos(a) * size, cy + (float)Math.Sin(a) * size, c, c);
            }
            for (int i = 0; i < (int)SceneValue("contacts"); i++)
            {
                double a = Seed(i * 5 + 1) * Math.PI * 2;
                float r = size * (0.15f + Seed(i * 5 + 2) * 0.8f);
                double age = Frac((sweep - a) / (Math.PI * 2));
                float v = Band(Seed(i * 5 + 3));
                float bright = (float)Math.Exp(-age * 5 / SceneValue("trail")) * (0.35f + v * 0.65f);
                if (bright < 0.12f) continue;
                Circle(cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r, 1 + v * 3, bright, 12);
            }
            Circle(cx, cy, 2 + energy * 3, 0.9f, 20);
        }

        void DrawReactor(int vh, float bass)
        {
            float cx = scr.W, cy = vh * 2, size = Math.Min(scr.W * 2, vh * 4) * 0.46f;
            int ringCount = (int)SceneValue("rings");
            for (int j = 0; j < ringCount; j++)
            {
                float radius = size * (0.22f + j * 0.68f / Math.Max(1, ringCount - 1)) * (1 + bass * 0.06f);
                for (int k = 0; k < 120; k++)
                {
                    if ((k / 10 + j) % 3 == 0) continue;
                    double a = k * Math.PI / 60 + effectTime * (j % 2 == 0 ? 0.6 : -0.4) * SceneValue("rotation");
                    double b = a + Math.PI / 60;
                    float v = Band(k / 119f), r = radius + v * size * 0.06f;
                    braille.Line(cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r,
                        cx + (float)Math.Cos(b) * r, cy + (float)Math.Sin(b) * r, 0.2f + v * 0.75f, 0.2f + v * 0.75f);
                }
            }
            for (int k = 0; k < 6; k++)
            {
                double a = k * Math.PI / 3 - effectTime;
                braille.Line(cx + (float)Math.Cos(a) * size * 0.07f, cy + (float)Math.Sin(a) * size * 0.07f,
                    cx + (float)Math.Cos(a + 0.8) * size * (0.17f + bass * 0.1f) * SceneValue("core"),
                    cy + (float)Math.Sin(a + 0.8) * size * (0.17f + bass * 0.1f) * SceneValue("core"), 1, 0.6f);
            }
        }

        // Layered swells in perspective; the foreground crests react most strongly to bass.
        void DrawOcean(int vh, float bass)
        {
            float w = scr.W * 2, h = vh * 4;
            int layers = (int)SceneValue("layers");
            for (int j = 0; j < layers; j++)
            {
                float depth = j / (float)Math.Max(1, layers - 1), px = 0, py = 0;
                for (int x = 0; x < w; x += 2)
                {
                    double a = x / w * 9 / SceneValue("wavelength") + effectTime * (0.6 + depth) + j * 0.6;
                    float swell = (float)(Math.Sin(a) + Math.Sin(a * 1.7 - effectTime) * 0.35);
                    float y = h * (0.22f + depth * 0.65f) + swell * h * (0.012f + depth * 0.065f) * (0.5f + bass) * SceneValue("height");
                    y -= Band(x / w) * h * 0.045f * depth;
                    if (x > 0) braille.Line(px, py, x, y, 0.2f + depth * 0.65f, 0.2f + depth * 0.65f);
                    px = x; py = y;
                }
            }
        }

        void DrawReef(int vh, float energy)
        {
            float w = scr.W * 2, h = vh * 4;
            for (int i = 0; i < (int)SceneValue("plants"); i++)
            {
                float root = Seed(i * 11 + 1) * w, height = h * (0.15f + Seed(i * 11 + 2) * 0.45f);
                float px = root, py = h - 1;
                for (int j = 1; j <= 24; j++)
                {
                    float t = j / 24f;
                    float x = root + (float)Math.Sin(effectTime + t * 2 + i) * t * t * (4 + energy * 12) * SceneValue("sway");
                    float y = h - 1 - height * t * (0.7f + Band(Seed(i)) * 0.3f);
                    braille.Line(px, py, x, y, 0.25f + t * 0.5f, 0.25f + t * 0.5f);
                    if (j % 5 == 0)
                        braille.Line(x, y, x + (j % 2 == 0 ? 1 : -1) * height * 0.15f, y - height * 0.12f, 0.4f, 0.85f);
                    px = x; py = y;
                }
            }
            for (int i = 0; i < (int)SceneValue("bubbles"); i++)
            {
                float y = h * (1 - (float)Frac(Seed(i + 200) + effectTime * (0.04 + Seed(i) * 0.07)));
                float x = Seed(i + 400) * w + (float)Math.Sin(effectTime + i + y * 0.06) * 3;
                Circle(x, y, 1 + Seed(i + 700) * 2, 0.3f + energy * 0.6f, 10);
            }
        }

        void DrawJellyfish(int vh, float bass)
        {
            float w = scr.W * 2, h = vh * 4;
            int count = (int)SceneValue("count");
            for (int i = 0; i < count; i++)
            {
                float cx = w * (i + 0.5f) / count + (float)Math.Sin(effectTime * 0.4 + i) * w * 0.035f;
                float cy = h * (0.22f + Seed(i + 80) * 0.3f) + (float)Math.Sin(effectTime + i) * h * 0.06f;
                float r = Math.Min(w / (count * 2.5f), h * 0.12f) * (0.9f + bass * 0.3f) * SceneValue("size");
                for (int k = 0; k <= 60; k++)
                {
                    double a = Math.PI * k / 60;
                    float x = cx + (float)Math.Cos(a) * r, y = cy - (float)Math.Sin(a) * r * 0.75f;
                    braille.Plot((int)x, (int)y, 0.7f + bass * 0.3f);
                    if (k % 6 == 0) braille.Line(x, y, cx + (x - cx) * 0.6f, cy, 0.6f, 0.35f);
                }
                braille.Line(cx - r, cy, cx + r, cy, 0.8f, 0.8f);
                int tentacles = (int)SceneValue("tentacles");
                for (int k = 0; k < tentacles; k++)
                {
                    float u = k / (float)(tentacles - 1);
                    float px = cx - r * 0.8f + r * 1.6f * u, py = cy;
                    float length = r * (2 + Seed(i * 13 + k) * 2) * (0.7f + Band(u) * 0.4f) * SceneValue("length");
                    for (int j = 1; j <= 30; j++)
                    {
                        float t = j / 30f;
                        float x = cx - r * 0.8f + r * 1.6f * u + (float)Math.Sin(t * 5 - effectTime * 2 + k) * t * r * 0.3f;
                        float y = cy + length * t;
                        braille.Line(px, py, x, y, 0.65f - t * 0.4f, 0.65f - t * 0.4f);
                        px = x; py = y;
                    }
                }
            }
        }

        void DrawAurora(int vh, float energy)
        {
            float w = scr.W * 2, h = vh * 4;
            for (int i = 0; i < (int)SceneValue("stars"); i++)
                braille.Plot((int)(Seed(i + 900) * w), (int)(Seed(i + 1000) * h * 0.65f), 0.2f + Seed(i + 1100) * 0.3f);
            int curtains = (int)SceneValue("curtains");
            for (int j = 0; j < curtains; j++)
                for (int x = 0; x < w; x += 2)
                {
                    float u = x / w;
                    double a = u * 7 + j * 0.8 + effectTime * 0.35;
                    float y = h * (0.23f + j * 0.06f) + (float)(Math.Sin(a) + Math.Sin(a * 1.6) * 0.35) * h * 0.1f;
                    float length = h * (0.08f + Band(u) * 0.22f + energy * 0.05f) * SceneValue("height");
                    braille.Line(x, y, x, y + length, 0.7f + Band(u) * 0.25f, 0.08f);
                }
        }

        // Four different audio-driven geometries. Work is bounded by terminal size.
        void DrawDream(int vh, float dt)
        {
            Bands(48);
            dreamSmooth.Apply(raw, 48, dt, 0.035f, Smooth);
            float response = SceneValue("response");
            float bass = Clamp01(Average(dreamSmooth.V, 0, 8) * response);
            float mid = Clamp01(Average(dreamSmooth.V, 8, 22) * response);
            float treble = Clamp01(Average(dreamSmooth.V, 30, 18) * response);
            dreamTime += dt * EffectSpeed * (0.3 + bass * 1.4);
            double t = dreamTime;
            if (Mode == 18 || Mode == 20)
            {
                // Half-block pixels retain color detail without doubling terminal output.
                double scale = Math.Max(1, Math.Min(scr.W, vh * 2));
                for (int y = 0; y < vh; y++)
                    for (int x = 0; x < scr.W; x++)
                    {
                        int top = DreamPixel((2 * x - scr.W) / scale, (4 * y - vh * 2) / scale, t, bass, mid, treble);
                        int bottom = DreamPixel((2 * x - scr.W) / scale, (4 * y + 2 - vh * 2) / scale, t, bass, mid, treble);
                        scr.Set(x, y, '▀', top, bottom);
                    }
                return;
            }
            braille.Begin(scr.W, vh);
            float pw = scr.W * 2, ph = vh * 4, size = Math.Min(pw, ph) * 0.42f;
            if (Mode == 19)
            {
                // A drifting colony of jellyfish/roots: each limb follows its own band.
                int limbs = (int)SceneValue("limbs");
                for (int limb = 0; limb < limbs; limb++)
                {
                    float energy = Clamp01(dreamSmooth.V[limb % 48] * response);
                    double angle = limb * Math.PI * 2 / limbs + t * 0.12;
                    float ox = pw / 2, oy = ph / 2;
                    for (int j = 1; j <= 80; j++)
                    {
                        double u = j / 80.0;
                        double bend = angle + Math.Sin(u * 7 - t * 2 + limb * 0.7) * u * (0.35 + mid) + Math.Sin(t + u * 3) * 0.2;
                        double r = size * u * (0.55 + energy * 0.65);
                        float nx = pw / 2 + (float)(Math.Cos(bend) * r);
                        float ny = ph / 2 + (float)(Math.Sin(bend) * r);
                        braille.Line(ox, oy, nx, ny, 0.2f + energy * 0.6f, (float)(0.2 + energy * 0.55 + u * 0.2));
                        ox = nx; oy = ny;
                    }
                }
            }
            else
            {
                // A perspective topographic ocean: bass lifts hills, highs ruffle ridges.
                int layers = (int)SceneValue("layers");
                for (int row = 0; row < layers; row++)
                {
                    double depth = row / (double)(layers - 1), perspective = 0.15 + depth * depth * 0.85;
                    float ox = 0, oy = 0;
                    for (int col = 0; col <= 100; col++)
                    {
                        double u = col / 50.0 - 1, z = row * 0.32 + t;
                        double wave = Math.Sin(u * 5 + z) * Math.Cos(z * 0.7 + u * 2) * (0.25 + bass);
                        wave += Math.Sin(u * 17 - z * 2) * treble * 0.22;
                        wave += dreamSmooth.V[Math.Min(47, col * 48 / 101)] * mid * 0.6;
                        float nx = pw / 2 + (float)(u * pw * 0.75 * perspective);
                        float ny = (float)(ph * (0.15 + depth * 0.8) - wave * ph * 0.23 * perspective);
                        float color = (float)ClampD(0.2 + depth * 0.35 + wave * 0.3 + mid * 0.2, 0, 1);
                        if (col > 0) braille.Line(ox, oy, nx, ny, color, color);
                        ox = nx; oy = ny;
                    }
                }
            }
            braille.Draw(scr, v => Pal(v));
        }

        int DreamPixel(double x, double y, double t, float bass, float mid, float treble)
        {
            double value;
            if (Mode == 18)
            {
                double r = Math.Sqrt(x * x + y * y), angle = Math.Atan2(y, x);
                double fold = Math.Cos(angle * SceneValue("folds") + t * 0.6);
                double tunnel = Math.Log(0.12 + r) * (7 + bass * 5) - t * 4;
                value = 0.5 + 0.5 * Math.Sin(tunnel + fold * (2 + mid * 4) + Math.Sin(r * 13 - t) * treble * 3);
                value = Math.Pow(value, 1.5) * (0.45 + mid * 0.55);
            }
            else
            {
                double field = 0;
                for (int i = 0; i < (int)SceneValue("cells"); i++)
                {
                    double cx = Math.Sin(t * (0.4 + i * 0.03) + i * 2.4) * 0.75;
                    double cy = Math.Cos(t * (0.3 + i * 0.04) + i * 1.7) * 0.75;
                    double dx = x - cx, dy = y - cy;
                    field += (0.045 + Clamp01(dreamSmooth.V[i * 6] * SceneValue("response")) * 0.08) / (dx * dx + dy * dy + 0.025);
                }
                // Interference contours make membranes ripple and fuse like living cells.
                value = (0.5 + 0.5 * Math.Sin(field * (2 + mid) - t)) * Math.Min(1, field * 0.65);
                value = Math.Pow(value, 2 - treble) * (0.45 + bass * 0.55);
            }
            return Pal((float)value);
        }

        // XY oscilloscope, as used for oscilloscope music: the left channel drives the beam horizontally and
        // the right channel vertically. Every sample is traced, and the beam deposits a fixed amount of energy
        // per unit of time, so slow strokes glow bright, fast jumps stay faint, and the phosphor fades out.
        void DrawScope(int vh, float dt)
        {
            int pw = scr.W * 2, ph = vh * 4;
            if (glowW != pw || glowH != ph) { glow = new float[pw * ph]; glowW = pw; glowH = ph; }

            float fade = (float)Math.Exp(-dt / ScopeDecay);
            for (int i = 0; i < glow.Length; i++) glow[i] *= fade;

            // Samples that arrived since the last frame, plus history for the spline on either side.
            long written = rings.Left.Written;
            int n = (int)Math.Min(written - scopeRead, sampleRate / 10);
            if (scopeRead == 0) n = Math.Min(n, (int)(sampleRate * dt));
            scopeRead = written;
            int len = n + 3;
            if (scopeL.Length < len) { scopeL = new float[len]; scopeR = new float[len]; }
            rings.Left.CopyLatest(scopeL, len);
            rings.Right.CopyLatest(scopeR, len);

            // Square screen centered in the window; braille dots are close to square, so no aspect correction.
            float half = (Math.Min(pw, ph) - 2) / 2f;
            float scale = half * ScopeZoom * (float)Math.Pow(10, GainDb / 20.0);
            float cx = pw / 2f, cy = ph / 2f;
            int sub = ScopeSmooth ? 4 : 1; // upsampling stands in for the smooth reconstruction of a real DAC
            float energy = 0.1f * BeamIntensity / sub;

            // The newest sample has no successor yet, so the trace runs one sample behind and ends at index n + 1.
            float px = cx + scopeL[1] * scale, py = cy - scopeR[1] * scale;
            for (int k = 1; k <= n; k++)
                for (int s = 1; s <= sub; s++)
                {
                    float l, r;
                    if (sub == 1) { l = scopeL[k + 1]; r = scopeR[k + 1]; }
                    else
                    {
                        float t = (float)s / sub;
                        l = CatmullRom(scopeL[k - 1], scopeL[k], scopeL[k + 1], scopeL[k + 2], t);
                        r = CatmullRom(scopeR[k - 1], scopeR[k], scopeR[k + 1], scopeR[k + 2], t);
                    }
                    float x = cx + l * scale, y = cy - r * scale;
                    Stroke(px, py, x, y, energy);
                    px = x; py = y;
                }

            if (ScopeGrid) DrawGraticule(cx, cy, half);

            for (int row = 0; row < vh; row++)
                for (int col = 0; col < scr.W; col++)
                {
                    int bits = 0;
                    float best = 0;
                    for (int sy = 0; sy < 4; sy++)
                        for (int sx = 0; sx < 2; sx++)
                        {
                            float g = glow[(row * 4 + sy) * glowW + col * 2 + sx];
                            if (g <= 0) continue;
                            float b = 1f - (float)Math.Exp(-g); // phosphor saturates rather than clipping
                            if (b < 0.04f) continue;
                            bits |= sy < 3 ? (sx == 0 ? 1 << sy : 8 << sy) : (sx == 0 ? 0x40 : 0x80);
                            if (b > best) best = b;
                        }
                    if (bits != 0)
                        scr.Set(col, row, (char)(0x2800 + bits), Scale(Pal(0.2f + 0.8f * best), 0.4 + 0.6 * best), -1);
                }
        }

        // Spreads one beam segment's energy evenly along its length, so energy per dot falls as speed rises.
        void Stroke(float x0, float y0, float x1, float y1, float e)
        {
            float dx = x1 - x0, dy = y1 - y0;
            int steps = Math.Min(4 * (glowW + glowH), (int)Math.Max(Math.Abs(dx), Math.Abs(dy)) + 1);
            float per = e / steps;
            for (int i = 0; i < steps; i++)
            {
                float f = (float)i / steps;
                int x = (int)Math.Floor(x0 + dx * f + 0.5f), y = (int)Math.Floor(y0 + dy * f + 0.5f);
                if ((uint)x < (uint)glowW && (uint)y < (uint)glowH) glow[y * glowW + x] += per;
            }
        }

        static float CatmullRom(float p0, float p1, float p2, float p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2 * p1 + (p2 - p0) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (3 * p1 - p0 - 3 * p2 + p3) * t3);
        }

        // 8x8 division graticule over the square screen, dimmer than any trace.
        void DrawGraticule(float cx, float cy, float half)
        {
            int dim = Lerp(0x1C1C1C, Pal(0.4f), 0.2), axis = Lerp(0x2A2A2A, Pal(0.5f), 0.3);
            int left = (int)((cx - half) / 2), right = (int)((cx + half) / 2);
            int top = (int)((cy - half) / 4), bottom = Math.Min(glowH / 4 - 1, (int)((cy + half) / 4));
            var cols = new int[9];
            var rows = new int[9];
            for (int i = 0; i <= 8; i++)
            {
                cols[i] = Math.Min(scr.W - 1, (int)((cx - half + i * half / 4) / 2));
                rows[i] = Math.Min(bottom, (int)((cy - half + i * half / 4) / 4));
            }
            for (int i = 0; i <= 8; i++)
            {
                int c = i == 4 ? axis : dim;
                for (int y = top; y <= bottom; y++) scr.Set(cols[i], y, '·', c, -1);
                for (int x = left; x <= right; x += 2) scr.Set(x, rows[i], '·', c, -1);
            }
            for (int i = 0; i <= 8; i++)
                for (int j = 0; j <= 8; j++)
                    scr.Set(cols[i], rows[j], '+', i == 4 || j == 4 ? axis : dim, -1);
        }

        // ---------------- overlays

        void DrawStatus(int y)
        {
            string gain = GainDb.ToString("+0;-0;0") + " dB" + (AutoGain ? " auto" : "");
            string src = SourceName + (srcError != null ? " (" + srcError + ")" : "");
            string s = " DMZ TermVis | " + ModeNames[Mode] + " | " + ThemeNames[Theme] + " | " + GlyphNames[Glyphs] + " | " + src +
                       " | Gain " + gain + " | Space play/pause  N next  P prev | Tab settings  H help";
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
                "M / Shift+M  next / previous mode (22 modes)",
                "1-8          original modes: bars through scope",
                "9/0/-/=      kaleido / tendrils / cells / terrain",
                "Space        play / pause music",
                "N / P        next / previous track",
                "T            suggested colors for this mode",
                "Tab          settings for the selected effect",
                "C / Shift+C  next / previous color theme",
                "K            glyph style: blocks, ascii, code, dots",
                "V            color by: height, frequency, loudness",
                "S            source: system audio / mic (Windows)",
                "Up / Down    gain +/- 2 dB",
                "A            auto-gain on/off",
                "Left / Right bar width",
                "G            gaps between bars",
                "Shift+P      peak markers",
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
            List<Param> active = ActiveParams();
            const int nameW = 20, valW = 18;
            int bw = nameW + valW + 8;
            int visible = Math.Min(active.Count, Math.Max(1, scr.H - 6));
            int first = Clamp(selected - visible / 2, 0, active.Count - visible);
            int bh = visible + 4;
            int x0 = Math.Max(0, (scr.W - bw) / 2), y0 = Math.Max(0, (scr.H - bh) / 2);
            int border = Pal(0.8f), bg = 0x14141E;
            DrawBox(x0, y0, bw, bh, border, bg);
            scr.Text(x0 + 2, y0 + 1, ModeNames[Mode] + " settings", border, bg);
            string hint = "Up/Down select  Left/Right edit  Tab close";
            scr.Text(x0 + 2, y0 + bh - 2, hint.Length > bw - 4 ? hint.Substring(0, bw - 4) : hint, 0x8C8C96, bg);
            for (int r = 0; r < visible; r++)
            {
                int i = first + r;
                bool sel = i == selected;
                int rowBg = sel ? Scale(Pal(0.7f), 0.35) : bg;
                string val = active[i].Get();
                string text = " " + active[i].Name.PadRight(nameW) + (sel ? "< " : "  ") + val.PadRight(valW - 4) + (sel ? " >" : "  ");
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
                    Console.WriteLine("DMZ TermVis 1.3 - terminal audio visualizer");
                    Console.WriteLine();
                    Console.WriteLine("Usage: termvis [options]");
                    Console.WriteLine("  --mic                 Windows: visualize the microphone instead of system audio");
                    Console.WriteLine("  --system              Windows: visualize system audio (default)");
                    Console.WriteLine("                        (macOS always uses the default input device)");
                    Console.WriteLine("  --mode <name|1-22>    bars, mirror, wave, waterfall, rain, code, radial, scope,");
                    Console.WriteLine("                        retro, warp, tunnel, nebula, radar, reactor,");
                    Console.WriteLine("                        ocean, reef, jellyfish, aurora, kaleido, tendrils, cells, terrain");
                    Console.WriteLine("  --theme <name>        fire, ocean, neon, matrix, rainbow, mono, synthwave,");
                    Console.WriteLine("                        cyberpunk, sunset, amber, ice, toxic, blood, vapor, phosphor");
                    Console.WriteLine("                        arcade, starlight, laser, cosmic, sonar, plasma,");
                    Console.WriteLine("                        lagoon, coral, abyss, borealis");
                    Console.WriteLine("  --effectspeed <n>     set all scene speeds, 0.25..4 (legacy shortcut)");
                    Console.WriteLine("  --effectdetail <n>    set scene count multipliers, 0.5..2 (legacy shortcut)");
                    Console.WriteLine("  --jellyfish.count <n> jellyfish count, 1..20");
                    Console.WriteLine("  --warp.stars <n>      warp star count, 20..1000");
                    Console.WriteLine("  --<mode>.<key> <n>    any individual effect key from termvis.ini");
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
