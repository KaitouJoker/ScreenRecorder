# FFmpeg shared libraries

The recorder links FFmpeg through **FFmpeg.AutoGen 9.0.1.1** (managed P/Invoke bindings), which
binds the **FFmpeg 9.x** ABI. The native shared DLLs are **not committed** (they are ~230 MB; see
`.gitignore`) — drop them into `win-x64/` before building/running an executable (the console
harness, and later the app).

## Required DLLs (`Externals/ffmpeg/win-x64/`)

FFmpeg 9.x generation, matched to FFmpeg.AutoGen 9.0.1.1. Only these **5** are needed (we use file
output + our own WGC/WASAPI capture, so `avdevice` and `avfilter` are intentionally NOT shipped —
dropping them cut the installer ~106 MB; `FFmpegBootstrap` no longer calls `avdevice_register_all`):

| DLL | Major |
|---|---|
| `avcodec-63.dll` | 63 |
| `avformat-63.dll` | 63 |
| `avutil-61.dll` | 61 |
| `swresample-7.dll` | 7 |
| `swscale-10.dll` | 10 |

## How to obtain

Use a **shared** (not static) Windows x64 build of FFmpeg 9.x:
- BtbN builds: https://github.com/BtbN/FFmpeg-Builds (pick `ffmpeg-master-latest-win64-gpl-shared` or `ffmpeg-n9.*-win64-gpl-shared`)
- Copy the DLLs from its `bin/` folder into `win-x64/`.

The build wires these into each executable's output directory (`<None>` copy in
`ScreenRecorder.Engine.csproj`), and the engine sets `ffmpeg.RootPath = AppContext.BaseDirectory`
at startup so the loader finds them next to the exe. CI fetches them in the release workflow (P6);
the NSIS installer ships them (P5).

> GPL note: the recorder is GPL-3.0, so a `*-gpl-shared` FFmpeg build is license-compatible.
