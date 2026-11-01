Run `dotnet run --project tests/TermVis.Tests.csproj` from the repository root.

The dependency-free checks render all four new modes at nine terminal sizes, switch modes repeatedly, verify named/numeric selection and audio response independently of elapsed animation time, and report rendering cost. On macOS, 18 additional checks execute the actual JXA routing logic with fake players to cover playing/paused priority, sticky selection, missing apps, and all three commands. No audio capture, real player commands, or user settings are touched.

Optionally pass a directory after `--` to export four JSON screen buffers for previewing.

Manual platform checks:
- Open music in Spotify/Music on macOS or a media-key-compatible Windows player. Space toggles playback; N skips forward; P goes back. Repeat with Tab settings open.
- On macOS, pause and resume with both apps running: the last controlled app should resume. Denying Automation access should show an error while rendering continues. No running player should show an actionable message.
- 1–8 retain their original modes; 9/0/-/= select the new modes. M and Shift+M wrap through all twelve. Shift+P toggles peaks.
- Resize the terminal, switch themes, and restart to check persistence.

Native player control requires interactive checks on each target OS; renderer tests do not establish that the OS/player received a command.
