using System;
using System.Diagnostics;
using System.Reflection;
using TermVis;

static class Checks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static object Get(object o, string name) { return o.GetType().GetField(name, Private).GetValue(o); }
    static void Call(object o, string name, params object[] args) { o.GetType().GetMethod(name, Private).Invoke(o, args); }
    static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
    static long Fingerprint(Screen s)
    {
        long hash = 17;
        unchecked { for (int i = 0; i < s.Ch.Length; i++) hash = hash * 31 + s.Ch[i] + s.Fg[i] * 7L + s.Bg[i]; }
        return hash;
    }
    public static void Main(string[] args)
    {
        var viz = new Viz();
        var screen = (Screen)Get(viz, "scr");
        var spectrum = (double[])Get(viz, "dbSpec");
        Call(viz, "BuildPalette");
        string[] names = { "kaleido", "tendrils", "cells", "terrain" };
        int cases = 0;
        foreach (int width in new[] { 4, 80, 160 })
        foreach (int height in new[] { 2, 24, 50 })
        {
            // Avoid terminal output from Screen.Resize in headless tests.
            screen.W = width; screen.H = height;
            screen.Ch = new char[width * height]; screen.Fg = new int[width * height]; screen.Bg = new int[width * height];
            for (int mode = 8; mode < 12; mode++)
            {
                viz.Theme = new[] { 2, 1, 11, 14 }[mode - 8];
                Call(viz, "BuildPalette");
                Require(viz.Apply("mode", names[mode - 8]) && viz.Mode == mode, "Named mode selection");
                Require(viz.Apply("mode", (mode + 1).ToString()) && viz.Mode == mode, "Numeric mode selection");
                for (int i = 0; i < spectrum.Length; i++) spectrum[i] = -100;
                screen.Clear(); Call(viz, "DrawDream", height, 0.016f);
                long quiet = Fingerprint(screen);
                for (int i = 0; i < spectrum.Length; i++) spectrum[i] = -8 + Math.Sin(i * 0.1) * 6;
                for (int frame = 0; frame < 15; frame++) { screen.Clear(); Call(viz, "DrawDream", height, 0.016f); }
                Require(quiet != Fingerprint(screen), "Audio/motion must change " + names[mode - 8]);
                Require(Array.Exists(screen.Ch, c => c != ' '), "Nonempty renderer");
                if (args.Length == 1 && width == 80 && height == 24)
                {
                    System.IO.Directory.CreateDirectory(args[0]);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(args[0], names[mode - 8] + ".json"),
                        System.Text.Json.JsonSerializer.Serialize(new { Ch = new string(screen.Ch), Fg = screen.Fg, Bg = screen.Bg }));
                }
                cases++;
            }
        }
        // Pause animation time and compare two fresh renderers: prove audio itself changes output.
        for (int mode = 8; mode < 12; mode++)
        {
            long[] results = new long[2];
            for (int signal = 0; signal < 2; signal++)
            {
                var v = new Viz(); v.Mode = mode; Call(v, "BuildPalette");
                var sc = (Screen)Get(v, "scr"); sc.W = 80; sc.H = 24;
                sc.Ch = new char[1920]; sc.Fg = new int[1920]; sc.Bg = new int[1920];
                var spec = (double[])Get(v, "dbSpec");
                for (int i = 0; i < spec.Length; i++) spec[i] = signal == 0 ? -100 : -5;
                sc.Clear(); Call(v, "DrawDream", 24, 0.016f); results[signal] = Fingerprint(sc);
            }
            Require(results[0] != results[1], "Audio responsiveness " + mode);
        }
        if (Platform.IsMac)
        {
            // Execute the real routing script with fake players: never touch personal playback.
            string[] states = { "{}", "{Spotify:'paused'}", "{Music:'playing',Spotify:'paused'}", "{Music:'paused',Spotify:'paused'}", "{Music:'paused'}", "{Music:'playing',Spotify:'playing'}" };
            string[] expected = { "No player", "Spotify", "Music", "Music", "Music", "Spotify" };
            for (int scenario = 0; scenario < states.Length; scenario++)
            for (int action = 0; action < 3; action++)
            {
                string method = action == 0 ? "playpause" : action == 1 ? "nextTrack" : "previousTrack";
                string stub = "var states=" + states[scenario] + "; var calls=[]; function FakeApplication(n){return {running:function(){return n in states;},playerState:function(){return states[n];}," + method + ":function(){calls.push(n);}};}";
                string script = stub + "var result=" + MediaControls.BuildMacScript(action, "Music").Replace("Application(", "FakeApplication(") + ";" +
                    "if(result!=='" + expected[scenario] + "'||calls.length!==" + (scenario == 0 ? "0" : "1") + ")throw Error('routing failed: '+result+' calls='+calls);result;";
                var info = new ProcessStartInfo("/usr/bin/osascript", "-l JavaScript") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var process = Process.Start(info))
                {
                    process.StandardInput.Write(script); process.StandardInput.Close();
                    Require(process.WaitForExit(5000), "Script timeout");
                    Require(process.ExitCode == 0, process.StandardError.ReadToEnd());
                    Require(process.StandardOutput.ReadToEnd().Trim() == expected[scenario], "Routing result");
                }
            }
            Console.WriteLine("PASS: 18 macOS media-routing checks with fake players.");
        }
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 120; i++) { viz.Mode = 8 + i % 4; screen.Clear(); Call(viz, "DrawDream", screen.H, 1f / 60); }
        Console.WriteLine("PASS: " + cases + " mode/size checks, four audio-response checks; average rendering " + (watch.Elapsed.TotalMilliseconds / 120).ToString("0.00") + " ms at 160x50.");
    }
}
