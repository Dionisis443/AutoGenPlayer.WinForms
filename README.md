# AutoGenPlayer.WinForms

Reusable WinForms video player controls built with FFmpeg.AutoGen and NAudio.

The package provides a clean player surface, optional audio level meters, and an
optional multi-stream audio mixer UI. It is intended for desktop WinForms apps
that need frame stepping, frame-based seeking, displayed-frame PTS, playback
speed control, and audio stream mixing.

## Install

```xml
<PackageReference Include="AutoGenPlayer.WinForms" Version="0.3.0" />
```

Target framework:

```xml
<TargetFramework>net8.0-windows</TargetFramework>
<UseWindowsForms>true</UseWindowsForms>
```

## FFmpeg Runtime

This NuGet package does not redistribute FFmpeg native binaries. The host
application must provide a compatible FFmpeg runtime folder.

Required files:

- `ffmpeg.exe`
- `ffprobe.exe`
- `avcodec-61.dll`
- `avdevice-61.dll`
- `avfilter-10.dll`
- `avformat-61.dll`
- `avutil-59.dll`
- `postproc-58.dll`
- `swresample-5.dll`
- `swscale-8.dll`

Lookup order:

1. Folder passed to `ConfigureFfmpegDirectory`.
2. `Native\FFmpeg` next to the application executable.
3. `FFmpeg` next to the application executable.
4. `ffmpeg.exe` and `ffprobe.exe` from the system `PATH`.

Configure FFmpeg before opening media:

```csharp
autoGenPlayerControl1.ConfigureFfmpegDirectory(@"C:\Tools\FFmpeg\bin");
```

Use FFmpeg binaries whose license is suitable for your application. FFmpeg builds
may be LGPL, GPL, or nonfree depending on how they were built. This package only
loads binaries supplied by the host application.

## Beginner Example: Open and Play

Add an `AutoGenPlayerControl` to a WinForms form, then wire an Open button:

```csharp
using AutoGen_Player.Controls;

public partial class Form1 : Form
{
    public Form1()
    {
        InitializeComponent();

        autoGenPlayerControl1.ConfigureFfmpegDirectory(@"C:\Tools\FFmpeg\bin");
    }

    private async void buttonOpen_Click(object sender, EventArgs e)
    {
        using OpenFileDialog dialog = new()
        {
            Filter = "Media files|*.mkv;*.mp4;*.mov;*.mxf;*.ts;*.m2ts;*.avi|All files|*.*"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        await autoGenPlayerControl1.OpenAsync(dialog.FileName);
        autoGenPlayerControl1.Play();
    }

    private void buttonPause_Click(object sender, EventArgs e)
    {
        autoGenPlayerControl1.Pause();
    }

    private void buttonStop_Click(object sender, EventArgs e)
    {
        autoGenPlayerControl1.Stop();
    }
}
```

## Controls

### `AutoGenPlayerControl`

The main player control. It displays video, owns playback state, exposes media
metadata, and provides methods for play, pause, seek, frame stepping, speed, and
audio mix configuration.

```csharp
var player = new AutoGenPlayerControl
{
    Dock = DockStyle.Fill
};

Controls.Add(player);
```

### `AutoGenAudioMixerControl`

Optional mixer UI with stream checkboxes, per-stream volume, advance in
milliseconds, and embedded level meters.

```csharp
var mixer = new AutoGenAudioMixerControl
{
    Dock = DockStyle.Right,
    Width = 360
};

mixer.Attach(player);
Controls.Add(mixer);
```

When enabled, the mixer switches the player to `PlayerPipelineMode.AutoGenMixer`.
When disabled, it returns to `PlayerPipelineMode.AutoGenSingleStreamAv`.

### `AutoGenAudioLevelsControl`

Optional standalone level meters control. Use it when you want meters outside
the mixer UI.

```csharp
var levels = new AutoGenAudioLevelsControl
{
    Dock = DockStyle.Bottom,
    Height = 150
};

levels.Attach(player);
Controls.Add(levels);
```

## Player API

### Main methods

```csharp
void ConfigureFfmpegDirectory(string? directory);
Task OpenAsync(string sourceFile, CancellationToken cancellationToken = default);

void Play();
void Pause();
void TogglePlayPause();
void Stop();

void Seek(TimeSpan position, bool resume);
void StepFrames(int frameDelta);
void SetPlaybackSpeed(double speed);

void SetAudioMix(IReadOnlyList<AudioMixSelection> selections, TimeSpan audioAdvance);
void ClearAudioMix();
```

### Frame and time helpers

```csharp
TimeSpan FrameToPosition(int frame);
int PositionToFrame(TimeSpan position);
TimeSpan ClampPlaybackPosition(TimeSpan position);
TimeSpan GetPlayableStartPosition();
TimeSpan GetPlayableEndPosition();
```

These helpers let an application keep its own frame-based timeline:

```csharp
trackBar.Minimum = 0;
trackBar.Maximum = player.VideoInfo.FrameCount - 1;

TimeSpan seekPosition = player.FrameToPosition(trackBar.Value);
player.Seek(seekPosition, resume: false);

int displayedFrame = player.PositionToFrame(player.CurrentPosition);
```

### Important properties

```csharp
string? SourceFile { get; }
IReadOnlyList<MediaStreamInfo> AudioStreams { get; }
MediaVideoInfo VideoInfo { get; }
PlayerPlaybackState PlaybackState { get; }

TimeSpan Position { get; }
TimeSpan CurrentPosition { get; }
string CurrentTimecode { get; }
long DisplayedFramePtsMilliseconds { get; }

double PlaybackSpeed { get; }
PlayerPipelineMode PipelineMode { get; set; }
TimeSpan AudioOutputLatencyEstimate { get; set; }
```

`DisplayedFramePtsMilliseconds` is the PTS of the frame currently shown in the
control. This is the value to use when the user selects an exact visible frame
and the host app later needs an FFmpeg `-ss` timestamp.

Example:

```csharp
double ssSeconds = player.DisplayedFramePtsMilliseconds / 1000.0;
string ffmpegSs = ssSeconds.ToString("0.###", CultureInfo.InvariantCulture);
```

### Events

```csharp
event EventHandler? MediaOpened;
event EventHandler? PositionChanged;
event EventHandler? PlaybackStateChanged;
event EventHandler<string>? StatusChanged;
event EventHandler<double>? PlaybackSpeedChanged;
event EventHandler<PcmAudioSamplesAvailableEventArgs>? MixedAudioSamplesAvailable;
event EventHandler<AudioLevelSnapshotEventArgs>? AudioLevelsAvailable;
event EventHandler<PlayerDiagnosticEventArgs>? DiagnosticAvailable;
```

Typical UI updates:

```csharp
player.MediaOpened += (_, _) => ConfigureTimeline();

player.PositionChanged += (_, _) =>
{
    textBoxTimecode.Text = player.CurrentTimecode;
    textBoxMilliseconds.Text = player.DisplayedFramePtsMilliseconds.ToString();
};

player.StatusChanged += (_, status) =>
{
    toolStripStatusLabel1.Text = status;
};
```

## Playback Speed

Speed is controlled with:

```csharp
player.SetPlaybackSpeed(1.25);
```

Current limits are clamped by the player. Calling `Play()` resets speed to
`1.00x`.

Example buttons:

```csharp
private void buttonSpeedUp_Click(object sender, EventArgs e)
{
    double step = player.PlaybackSpeed < 1.0 ? 0.10 : 0.25;
    player.SetPlaybackSpeed(player.PlaybackSpeed + step);
}

private void buttonSpeedDown_Click(object sender, EventArgs e)
{
    player.SetPlaybackSpeed(player.PlaybackSpeed - 0.10);
}
```

## Frame Stepping

```csharp
player.StepFrames(1);   // next frame
player.StepFrames(-1);  // previous frame
```

Mouse wheel on the player surface also steps frames:

- wheel up: next frame
- wheel down: previous frame

## Audio Mixer

The easiest way to use the mixer is the UI control:

```csharp
mixer.Attach(player);
mixer.MixerEnabled = true;
mixer.AudioAdvanceMilliseconds = 0;
```

The mixer exposes:

```csharp
AutoGenPlayerControl? Player { get; set; }
bool MixerEnabled { get; set; }
int AudioAdvanceMilliseconds { get; set; }
IReadOnlyList<AudioMixSelection> CurrentSelections { get; }

void Attach(AutoGenPlayerControl? player);
void ReloadStreams();
void ApplyMix(bool restartIfPlaying);
void Clear();

event EventHandler<bool>? MixerEnabledChanged;
```

Per-stream volume can be typed in the grid or changed with the mouse wheel over
the `Volume %` cell:

- wheel: `+/- 1`
- `Shift + wheel`: `+/- 10`

If all streams are unchecked while the mixer is enabled, audio becomes silent and
the level meters clear. The mixer remains enabled.

Manual mix configuration is also available:

```csharp
player.PipelineMode = PlayerPipelineMode.AutoGenMixer;

player.SetAudioMix(
[
    new AudioMixSelection(audioOrdinal: 0, streamIndex: 1, volumePercent: 100),
    new AudioMixSelection(audioOrdinal: 1, streamIndex: 2, volumePercent: 80)
],
audioAdvance: TimeSpan.Zero);

player.Play();
```

## Public Models

### `MediaStreamInfo`

```csharp
public sealed record MediaStreamInfo(
    int AudioOrdinal,
    int StreamIndex,
    string Codec,
    int Channels,
    string SampleRate,
    string ChannelLayout,
    string Language,
    string Title);
```

`DisplayText` returns a friendly stream description for UI lists.

### `MediaVideoInfo`

```csharp
public sealed record MediaVideoInfo(
    double FrameRate,
    TimeSpan Duration,
    int FrameCount,
    TimeSpan StartTime,
    int Width = 0,
    int Height = 0,
    double DisplayAspectRatio = 0);
```

### `AudioMixSelection`

```csharp
public sealed record AudioMixSelection(
    int AudioOrdinal,
    int StreamIndex,
    int VolumePercent);
```

### `PlayerPlaybackState`

```csharp
public enum PlayerPlaybackState
{
    Closed,
    Opened,
    Playing,
    Paused,
    Stopped,
    Failed
}
```

### `PlayerPipelineMode`

```csharp
public enum PlayerPipelineMode
{
    AutoGenMixer,
    AutoGenSingleStreamAv
}
```

## Diagnostics

Use `DiagnosticAvailable` while profiling startup, seek, play, and frame
stepping:

```csharp
player.DiagnosticAvailable += (_, e) =>
{
    Debug.WriteLine(e.ToString());
};
```

`PlayerDiagnosticEventArgs` exposes:

```csharp
string Operation { get; }
TimeSpan Elapsed { get; }
string Details { get; }
```

## Notes

- This package is WinForms-only.
- FFmpeg binaries are not redistributed.
- The main player control intentionally contains only the player surface. Host
  apps can provide their own buttons, menus, trackbars, and layouts.
- For frame-accurate UI timelines, use `VideoInfo.FrameCount`,
  `FrameToPosition`, `PositionToFrame`, and `DisplayedFramePtsMilliseconds`.
