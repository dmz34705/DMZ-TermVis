# DMZ TermVis

A real-time audio visualizer that runs entirely in your terminal. It turns music into animated text using colored block characters, braille dots and scrolling code. It's a single standalone program with nothing to install.

- **7 modes:** Bars, Mirror, Wave (oscilloscope), Waterfall (spectrogram), Rain (falling code), Code (scrolling source code lit up by the spectrum), Radial
- **14 color themes:** Fire, Ocean, Neon, Matrix, Rainbow, Mono, Synthwave, Cyberpunk, Sunset, Amber, Ice, Toxic, Blood, Vapor
- **4 glyph styles** for bars: Blocks, ASCII, Code, Dots
- A **settings panel** with 22 adjustable parameters, saved automatically so it reopens where you left off
- **Windows:** visualizes whatever your PC is playing (no setup needed), or the microphone
- **macOS:** visualizes the default input device (mic, or system audio via [BlackHole](#visualizing-system-audio-on-macos))

## Download

Get the archive for your system from the [Releases](../../releases) page:

| System | File |
|---|---|
| Windows 10/11 (64-bit) | `DMZ-TermVis-1.0-windows-x64.zip` |
| Mac with Apple Silicon (M1 and later) | `DMZ-TermVis-1.0-macos-arm64.tar.gz` |
| Mac with Intel processor | `DMZ-TermVis-1.0-macos-intel-x64.tar.gz` |

### Windows

1. Extract the zip.
2. Double-click `termvis.exe`, or run it from a terminal.
3. Play some music.

It looks best in **Windows Terminal**, the default on Windows 11. If Windows SmartScreen warns about an unknown publisher, click **More info → Run anyway**.

### macOS

1. Double-click the `.tar.gz` to extract it.
2. Open **Terminal**, go to the extracted folder, and run the one-time setup:
   ```sh
   cd ~/Downloads/DMZ-TermVis-1.0-macos-arm64
   sh setup-mac.sh
   ```
   The setup removes the "downloaded from the internet" block and applies a local signature, which Apple Silicon Macs require.
3. Start it:
   ```sh
   ./termvis
   ```
4. The first time, macOS asks whether Terminal may use the microphone. Click **Allow**. If you missed the prompt, turn it on in **System Settings → Privacy & Security → Microphone**.

It looks best in a terminal with 24-bit color, such as [iTerm2](https://iterm2.com), Ghostty, WezTerm or Terminal on recent macOS. In other terminals TermVis switches to 256 colors automatically. You can force either one with `--colors 24-bit` or `--colors 256`.

## Visualizing system audio on macOS

macOS doesn't let apps listen to system audio directly. The free [BlackHole](https://existential.audio/blackhole/) virtual audio device works around this:

1. Install **BlackHole 2ch**.
2. Open **Audio MIDI Setup**, click **+**, and choose **Create Multi-Output Device**. Tick both your speakers or headphones and **BlackHole 2ch**.
3. In **System Settings → Sound**, set **Output** to the Multi-Output Device and **Input** to **BlackHole 2ch**.
4. Run `./termvis`. It now shows whatever your Mac is playing, and you still hear it.

To go back, set Output to your speakers and Input to your microphone.

## Controls

Press **H** at any time to see these on screen, or **Tab** to open the settings panel.

| Key | Action |
|---|---|
| **M** / **1–7** | Change mode |
| **C** / **Shift+C** | Next / previous color theme |
| **K** | Glyph style: blocks, ascii, code, dots |
| **V** | Color by: height, frequency, loudness |
| **S** | Source: system audio / microphone (Windows) |
| **↑ / ↓** | Gain |
| **A** | Auto-gain on/off |
| **← / →** | Bar width |
| **G** | Gaps between bars |
| **P** | Peak markers |
| **[ / ]** | Less / more smoothing |
| **Tab** or **E** | Settings panel (↑↓ select, ←→ change) |
| **B** | Status bar on/off |
| **R** | Reset to defaults |
| **Q** / **Esc** | Quit |

The settings panel also covers dynamic range, low and high frequency cutoffs, treble boost, peak gravity, rain speed, code scroll speed, radial spin, frame rate and terminal color mode.

Settings are saved to `termvis.ini` next to the program about a second after any change, so closing the window keeps them. You can also edit that file by hand.

## Command-line options

```
termvis [options]
  --mic                 Windows: visualize the microphone instead of system audio
  --system              Windows: visualize system audio (default)
  --mode <name|1-7>     bars, mirror, wave, waterfall, rain, code, radial
  --theme <name>        fire, ocean, neon, matrix, rainbow, mono, synthwave,
                        cyberpunk, sunset, amber, ice, toxic, blood, vapor
  --glyphs <name>       blocks, ascii, code, dots
  --colorby <name>      height, frequency, loudness
  --gain <dB>           extra gain, -30..30
  --range <dB>          dynamic range shown, 20..90
  --lowcut <Hz>         lowest frequency, 20..500
  --highcut <Hz>        highest frequency, 2000..22000
  --tilt <dB/oct>       treble boost, 0..6
  --fps <n>             frames per second, 10..144 (default 60)
  --colors <mode>       auto, 24-bit, 256
  --barwidth <n>        bar width in columns, 1..8
  --reset               ignore saved settings
```

## Building from source

Everything is in one file, `termvis.cs`.

- **Windows, quick:** `run.bat` compiles and runs it in memory using the C# compiler built into Windows. You don't need an SDK. `build.bat` makes a `termvis.exe` the same way.
- **Release builds:** install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and Python 3, then run `release.ps1`. It writes self-contained Windows and macOS archives to `dist/`.

## How it works

Audio is captured with WASAPI loopback on Windows and AudioQueue on macOS. It's analyzed with a 4096-point FFT, split into log-spaced frequency bands, and drawn as one full frame per tick using ANSI escape codes.

## License

[MIT](LICENSE)
