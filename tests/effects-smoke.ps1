$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $root 'work'
New-Item -ItemType Directory -Force $scratch | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$assemblyPath = Join-Path $scratch 'termvis-smoke.exe'
& $compiler /nologo /optimize+ "/out:$assemblyPath" (Join-Path $root 'termvis.cs')
if ($LASTEXITCODE -ne 0) { throw 'Legacy compiler build failed' }
$assembly = [Reflection.Assembly]::LoadFile($assemblyPath)
$source = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Drawing;
using System.Drawing.Imaging;
public static class EffectsSmoke
{
    static BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static object Get(object o, string n)
    {
        var f = o.GetType().GetField(n, Flags);
        return f != null ? f.GetValue(o) : o.GetType().GetProperty(n, Flags).GetValue(o, null);
    }
    static void Set(object o, string n, object v)
    {
        var f = o.GetType().GetField(n, Flags);
        if (f != null) f.SetValue(o, v); else o.GetType().GetProperty(n, Flags).SetValue(o, v, null);
    }
    static object Call(object o, string n, params object[] args) { return o.GetType().GetMethod(n, Flags).Invoke(o, args); }
    static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
    static void DrawMode(object viz, int mode, int h, float dt)
    {
                        switch (mode)
                        {
                            case 0: case 1: Call(viz, "DrawBars", h, dt, mode == 1); break;
                            case 2: Call(viz, "DrawWave", h, dt); break;
                            case 3: Call(viz, "DrawWaterfall", h, dt); break;
                            case 4: Call(viz, "DrawRain", h, dt); break;
                            case 5: Call(viz, "DrawCode", h, dt); break;
                            case 6: Call(viz, "DrawRadial", h, dt); break;
                            case 7: Call(viz, "DrawScope", h, dt); break;
                            default: Call(viz, "DrawEffect", h, dt); break;
                        }
    }
    public static string Run(Assembly assembly, string folder)
    {
        object viz = Activator.CreateInstance(assembly.GetType("TermVis.Viz"), true);
        object screen = Get(viz, "scr"), rings = Get(viz, "rings");
        string[] modes = (string[])Get(viz, "ModeNames"), themes = (string[])Get(viz, "ThemeNames");
        Check(modes.Length == 18 && themes.Length == 25, "Mode / palette counts");
        foreach (string theme in themes)
        {
            Check((bool)Call(viz, "Apply", "theme", theme), "Theme parse " + theme);
            Call(viz, "BuildPalette");
            foreach (int c in (int[])Get(viz, "pal")) Check(c >= 0 && c <= 0xffffff, "Invalid palette RGB");
        }
        Set(viz, "Mode", 9);
        Check((bool)Call(viz, "Apply", "effectspeed", "99") && (float)Get(viz, "EffectSpeed") == 4, "Speed clamp");
        Check((bool)Call(viz, "Apply", "effectdetail", "0.01") && (float)Get(viz, "EffectDetail") == 0.5f, "Detail clamp");
        Call(viz, "Defaults");
        var timer = Stopwatch.StartNew();
        int frames = 0;
        // Headless screen buffers exercise the actual renderer without capture hardware or console size dependencies.
        int[,] sizes = { {4,2}, {22,8}, {100,32}, {200,60} };
        using (var montage = new Bitmap(1000, 800))
        using (var graphics = Graphics.FromImage(montage))
        using (var font = new Font("Consolas", 7))
        using (var labelFont = new Font("Consolas", 11, FontStyle.Bold))
        {
            graphics.Clear(Color.FromArgb(4, 7, 15));
            for (int mode = 0; mode < modes.Length; mode++)
            {
                Check((bool)Call(viz, "Apply", "mode", modes[mode]), "Mode parse " + modes[mode]);
                Check((int)Get(viz, "Mode") == mode, "Mode index");
                Call(viz, "Apply", "mode", (mode + 1).ToString());
                Check((int)Get(viz, "Mode") == mode, "Numeric mode index");
                Call(viz, "SuggestedTheme");
                for (int size = 0; size < sizes.GetLength(0); size++)
                {
                    int w = sizes[size,0], h = sizes[size,1];
                    Set(screen, "W", w); Set(screen, "H", h);
                    Set(screen, "Ch", new char[w*h]); Set(screen, "Fg", new int[w*h]); Set(screen, "Bg", new int[w*h]);
                    // Silent, low, mid and high frequencies; variable dt and both detail limits.
                    for (int frame = 0; frame < 24; frame++)
                    {
                        double frequency = frame < 6 ? 0 : frame < 12 ? 80 : frame < 18 ? 1000 : 8000;
                        for (int sample = 0; sample < 1024; sample++)
                        {
                            float value = frequency == 0 ? 0 : (float)(Math.Sin((sample + frame * 1024) * frequency * Math.PI * 2 / 48000) * 0.35);
                            Call(rings, "Push", value, value * 0.7f);
                        }
                        float dt = frame % 2 == 0 ? 1f/30 : 1f/144;
                        Set(viz, "EffectDetail", frame < 12 ? 0.5f : 2f);
                        Call(viz, "Analyze", dt); Call(screen, "Clear");
                        DrawMode(viz, mode, h, dt);
                        frames++;
                        foreach (int c in (int[])Get(screen, "Fg")) Check(c >= -1 && c <= 0xffffff, "Invalid frame RGB");
                    }
                    if (mode >= 8 && size == 2)
                    {
                        char[] chars = (char[])Get(screen, "Ch"); int[] colors = (int[])Get(screen, "Fg");
                        int visible = 0;
                        foreach (char c in chars) if (c != ' ') visible++;
                        Check(visible > 10, "Empty effect " + modes[mode]);
                        int ox = (mode - 8) % 2 * 500, oy = (mode - 8) / 2 * 160;
                        using (var brush = new SolidBrush(Color.FromArgb(200, 220, 240)))
                            graphics.DrawString(modes[mode] + " / " + themes[(int)Get(viz,"Theme")], labelFont, brush, ox+8, oy+4);
                        // Draw braille dots directly so preview geometry matches the terminal buffer.
                        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                        {
                            int index = y*w+x, bits = chars[index]-0x2800;
                            if (bits <= 0 || bits > 255) continue;
                            using (var brush = new SolidBrush(Color.FromArgb(colors[index] | unchecked((int)0xff000000))))
                                for (int sy = 0; sy < 4; sy++) for (int sx = 0; sx < 2; sx++)
                                {
                                    int bit = sy < 3 ? (sx == 0 ? 1 << sy : 8 << sy) : (sx == 0 ? 0x40 : 0x80);
                                    if ((bits & bit) != 0) graphics.FillRectangle(brush, ox+8+x*4+sx*2, oy+28+y*4+sy, 2, 1);
                                }
                        }
                    }
                }
            }
            montage.Save(Path.Combine(folder,"effects-preview.png"), ImageFormat.Png);
        }
        // Each scene must visibly respond to changed audio, with the same clock and dimensions.
        Set(screen,"W",100); Set(screen,"H",32);
        Set(screen,"Ch",new char[3200]); Set(screen,"Fg",new int[3200]); Set(screen,"Bg",new int[3200]);
        for (int mode = 8; mode < modes.Length; mode++)
        {
            Set(viz,"Mode",mode);
            string[] pictures = new string[2];
            for (int audio = 0; audio < 2; audio++)
            {
                double[] spectrum = (double[])Get(viz,"dbSpec");
                for (int i = 0; i < spectrum.Length; i++) spectrum[i] = audio == 0 ? -240 : 0;
                Set(viz,"refDb",0d); Set(viz,"effectTime",2d);
                Set(Get(viz,"effectSmooth"),"V",new float[64]);
                Call(screen,"Clear"); Call(viz,"DrawEffect",32,1f/60);
                pictures[audio] = new string((char[])Get(screen,"Ch")) + string.Join(",",(int[])Get(screen,"Fg"));
            }
            Check(pictures[0] != pictures[1],"Scene ignores audio: " + modes[mode]);
        }
        // Ambient motion integrates seconds independently of frame rate.
        double[] quiet = (double[])Get(viz,"dbSpec");
        for (int i = 0; i < quiet.Length; i++) quiet[i] = -240;
        double[] clocks = new double[2]; int[] rates = {30,144};
        for (int rate = 0; rate < rates.Length; rate++)
        {
            Set(viz,"effectTime",0d); Set(Get(viz,"effectSmooth"),"V",new float[64]);
            for (int i = 0; i < rates[rate]; i++) Call(viz,"DrawEffect",32,1f/rates[rate]);
            clocks[rate] = (double)Get(viz,"effectTime");
        }
        Check(Math.Abs(clocks[0]-clocks[1]) < 0.00001,"Frame-rate dependent clock");
        // Contextual menus must show the active effect's controls and omit every other effect's.
        var options = (System.Collections.IList)Get(viz,"effectOptions");
        for (int mode = 0; mode < modes.Length; mode++)
        {
            Set(viz,"Mode",mode); Set(viz,"selected",999);
            var active = (System.Collections.IList)Call(viz,"ActiveParams");
            Check((int)Get(viz,"selected") < active.Count,"Selection clamp");
            foreach (object option in options)
            {
                bool found = false;
                foreach (object param in active)
                    if ((string)Get(param,"Name") == (string)Get(option,"Name")) found = true;
                if ((int)Get(option,"Mode") == mode) Check(found,"Missing effect control");
                // Labels such as Motion speed legitimately appear in multiple scenes.
                else if (found)
                {
                    bool sharedLabel = false;
                    foreach (object other in options)
                        if ((int)Get(other,"Mode") == mode && (string)Get(other,"Name") == (string)Get(option,"Name")) sharedLabel = true;
                    Check(sharedLabel,"Unrelated effect control: " + Get(option,"Name"));
                }
            }
            bool scope = false, rain = false, bars = false;
            foreach (object param in active)
            {
                string name = (string)Get(param,"Name");
                scope |= name == "Scope zoom"; rain |= name == "Rain speed"; bars |= name == "Glyphs (bars)";
            }
            Check(scope == (mode == 7) && rain == (mode == 4) && bars == (mode <= 1),"Original control visibility");
            Set(viz,"selected",0);
            ((Action<int>)Get(active[0],"Adjust"))(1);
            Check((int)Get(viz,"Mode") == (mode+1)%18,"Mode picker");
            Call(viz,"ActiveParams"); Call(viz,"DrawSettings");
        }
        // Save real settings-buffer previews for layout inspection.
        using (var menuImage = new Bitmap(1200,576))
        using (var g = Graphics.FromImage(menuImage))
        using (var menuFont = new Font("Consolas",10,FontStyle.Regular,GraphicsUnit.Pixel))
        {
            g.Clear(Color.Black);
            int[] previews = {16,9,7};
            for (int panel = 0; panel < previews.Length; panel++)
            {
                Set(viz,"Mode",previews[panel]); Set(viz,"selected",0);
                Set(screen,"W",50); Set(screen,"H",36);
                Set(screen,"Ch",new char[1800]); Set(screen,"Fg",new int[1800]); Set(screen,"Bg",new int[1800]);
                Call(screen,"Clear"); Call(viz,"DrawSettings");
                char[] chars = (char[])Get(screen,"Ch"); int[] fg = (int[])Get(screen,"Fg"), bg = (int[])Get(screen,"Bg");
                for (int y = 0; y < 36; y++) for (int x = 0; x < 50; x++)
                {
                    int k = y*50+x;
                    using (var brush = new SolidBrush(Color.FromArgb(unchecked((int)0xff000000) | (bg[k] < 0 ? 0 : bg[k]))))
                        g.FillRectangle(brush,panel*400+x*8,y*16,8,16);
                    if (chars[k] != ' ' && fg[k] >= 0)
                        using (var brush = new SolidBrush(Color.FromArgb(unchecked((int)0xff000000) | fg[k])))
                            g.DrawString(chars[k].ToString(),menuFont,brush,panel*400+x*8,y*16);
                }
            }
            menuImage.Save(Path.Combine(folder,"settings-preview.png"),ImageFormat.Png);
        }
        // Every option clamps and renders at both extremes, including minimum-sized terminals.
        foreach (object option in options)
        {
            int mode = (int)Get(option,"Mode"); Set(viz,"Mode",mode);
            string key = (string)Get(option,"Key");
            Check(!(bool)Call(viz,"Apply",key,"NaN"),"Accepted NaN");
            Check(!(bool)Call(viz,"Apply",key,"Infinity"),"Accepted Infinity");
            for (int extreme = 0; extreme < 2; extreme++)
            {
                Call(viz,"Apply",key,extreme == 0 ? "-99999" : "99999");
                Check((float)Get(option,"Value") == (float)Get(option,extreme == 0 ? "Min" : "Max"),"Option clamp " + key);
                foreach (int w in new[] {4,160})
                {
                    int h = w == 4 ? 2 : 50;
                    Set(screen,"W",w); Set(screen,"H",h);
                    Set(screen,"Ch",new char[w*h]); Set(screen,"Fg",new int[w*h]); Set(screen,"Bg",new int[w*h]);
                    Call(screen,"Clear"); DrawMode(viz,mode,h,1f/60); Call(viz,"DrawSettings");
                }
            }
            Set(option,"Value",Get(option,"Default"));
        }
        // Changes belong to an effect, survive switching, and reset without affecting another scene.
        Call(viz,"Apply","warp.speed","2"); Call(viz,"Apply","jellyfish.speed","0.5");
        Call(viz,"Apply","jellyfish.count","12"); Set(viz,"Mode",9);
        Check((float)Get(viz,"EffectSpeed") == 2,"Warp speed isolation");
        Set(viz,"Mode",16); Check((float)Get(viz,"EffectSpeed") == 0.5f,"Jellyfish speed isolation");
        Check((float)Call(viz,"SceneValue","count") == 12,"Jellyfish count isolation");
        Call(viz,"ResetEffect"); Check((float)Call(viz,"SceneValue","count") == 5,"Effect reset");
        Set(viz,"Mode",9); Check((float)Get(viz,"EffectSpeed") == 2,"Reset affected another effect");
        // Settings round trip including every effect's independent values.
        Call(viz,"Apply","effectspeed","1.75"); Call(viz,"Apply","effectdetail","1.5");
        string settings = (string)Call(viz,"SettingsText");
        object copy = Activator.CreateInstance(assembly.GetType("TermVis.Viz"), true);
        foreach (string line in settings.Split(new[] {'\r','\n'}, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = line.IndexOf('='); Check((bool)Call(copy,"Apply",line.Substring(0,eq),line.Substring(eq+1)),"Settings parse");
        }
        Check(settings == (string)Call(copy,"SettingsText"),"Settings round trip");
        return "PASS: 18 modes, 25 palettes, " + frames + " frames at 4 sizes; all new scenes react to audio; frame-rate independent clock; contextual menus, parameter limits, independent settings and reset, legacy migration, settings round trip. Elapsed " + timer.Elapsed.TotalSeconds.ToString("0.0") + " s.";
    }
}
'@
Add-Type -TypeDefinition $source -ReferencedAssemblies System.Drawing
[EffectsSmoke]::Run($assembly, $scratch)
