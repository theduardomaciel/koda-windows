# SmoothMotion.Windows

A reusable Direct3D 11 frame-interpolation engine, integrated into Koda for Windows.
The first backend generates intermediate frames for SDR video up to 1920×1080
(including portrait video with the same pixel count). Koda presents at up to 120 fps.

## Engine

`MotionEngine` has no WinUI or MediaPlayer dependency. It accepts a GPU surface and
estimates motion in both directions using a luma pyramid capped at 320 pixels on
its longest side. Coarse-to-fine block matching and vector median filtering produce
the motion fields. The renderer warps both source frames to each output phase,
softens uncertain matches and uses an original frame across scene cuts. Endpoint
phases preserve the original decoded pixels.

The Windows shaders implement this algorithm independently in HLSL. The design is
inspired by [smoothmotion-android](https://github.com/Ivorisnoob/smoothmotion-android);
visual parity with the Android engine has not been established.

The host serializes engine calls using `SyncRoot`, supplies decoded frames through `InputSurface`,
calls `SubmitFrame()`, and calls `Render(phase)` and `Present()` for each output tick.
`InputSurface` and `SwapChain` are borrowed COM pointers: the engine owns them.
`SetPresentationSize()` maps the swap chain to the host's logical display size.
`RenderLatest()` handles a single decoded frame after a paused seek. `Reset()`
discards the pair on seeks and discontinuities; `Dispose()` releases GPU resources.
Phase zero is the older frame and phase one is the newer frame. Interpolation
requires one source frame of lookahead; the host must delay audio accordingly.

## Koda integration

Enable **Settings → Videos → Smooth motion (experimental)**, or the same switch in
the video's settings menu. It is off by default. Choose **Up to 60 fps** or
**Up to 120 fps** (the default cap). Play a 24/25/30/50/60 fps video at a fixed
quality of 1080p or below. Koda currently enables interpolation on its separate VP9
video/audio path, where it can compensate audio with the shared media clock. Auto
quality and the H.264 DASH fallback continue through native presentation. The status
explains when a source cannot use interpolation.

The watch page, fullscreen player, video bar and PiP use the same surface adapter.
Moving the picture between surfaces restores native presentation and rebuilds the
GPU presenter for the new target. Audio offsets are restored when interpolation
stops. The shared clock lets the delayed soundtrack finish at the end of a video.

Energy Saver, live video, playback speeds other than 1×, video already near the output rate,
and resolutions above 1080p bypass interpolation. Koda's VP9 path selects 8-bit SDR
streams. GPU errors and missing frames restore native playback; missing over 10% of
presentation ticks over ten seconds after a six-second grace steps high-rate output
down to 60 fps, then restores native playback if 60 cannot be sustained. Turn the
feature off and on, or change video/quality, to retry.

No display mode is changed. Output respects the current window's display refresh
rate, including monitor changes. A dedicated high-resolution waitable timer drives
presentation independently of XAML rendering callbacks. HDR and automatic-quality
interpolation remain future work.

## Stats for nerds

Enable **Settings → Videos → Stats for nerds** or the same switch in the watch
player's settings menu. The scrollable overlay follows the player into fullscreen;
its close button saves the switch as off. It shows the video ID, dimensions, source
frame rate, format, nominal stream bitrate, Windows buffering progress, playback
state and position, GPU, requested/target/display limits, capture and presentation
counts, intermediate-render count, missed output ticks, skipped input captures,
CPU submission time and audio lookahead delay. A 30-second graph compares measured
captured input and accepted output rates. Text also reports intermediate renders
per second. Counters reset for a new video or quality; graph history resets for a
new video, and has gaps while paused or using native presentation.

Accepted Presents are submissions to DXGI, rather than verified physical scanouts.
Intermediate renders may choose originals at scene cuts or soften bad matches.
CPU submission time measures dispatch and Present calls; it is not GPU utilization
or GPU execution time. Native output FPS is shown as unavailable. The panel keeps
these distinctions visible instead of displaying requested rates as measurements.
Windows can reject the optional buffer-progress getter; that field then shows
unavailable. Optional diagnostics failures are contained so playback continues.

## Verification

On Windows with .NET 10:

```powershell
dotnet test tests/SmoothMotion.Windows.Tests
dotnet build src/Koda/Koda.csproj -p:Platform=x64
```

Tests exercise real hardware and WARP shaders, original pixels, scene cuts,
motion-compensated translation against a plain-blend baseline, single-frame paused
seeks, and 1080p GPU resources. Headless tests decode the included generated H.264
and VP9 clips with Windows MediaPlayer, interpolate real decoded frames, toggle
frame-server mode during playback and seek while paused. They create no windows.
The fixtures were generated from FFmpeg's `testsrc2` source at 256×144, 24 fps.

Visual acceptance still requires the app: compare a panning shot with the switch
off/on; check dialogue sync, scene cuts, pause/seek/resume, fullscreen, PiP and
returning to the watch page. Check the status for an active rate or fallback reason.
