using AutoGen_Player.Audio;
using AutoGen_Player.Core;
using AutoGen_Player.Ffmpeg;
using AutoGen_Player.Video;
using System.Diagnostics;

namespace AutoGen_Player.Controls;

public sealed class AutoGenPlayerControl : UserControl
{
    private readonly VideoSurfaceControl _videoSurface = new();
    private readonly object _frameStepQueueRoot = new();
    private readonly Queue<int> _frameStepQueue = [];
    private FfmpegTools? _tools;
    private MediaProbe? _probe;
    private ExternalFfmpegAudioMixPlayback? _audioMixPlayback;
    private AutoGenVideoPlayback? _videoPlayback;
    private AutoGenSingleStreamAvPlayback? _singleStreamAvPlayback;
    private string? _preferredFfmpegDirectory;
    private long _playStartedTimestamp;
    private TimeSpan _playStartedPosition;
    private bool _clockRunning;
    private bool _waitingForVideoPreroll;
    private int? _pendingStepFrame;
    private int _pendingStepDirection;
    private bool _frameStepWorkerRunning;
    private int _frameStepGeneration;
    private TaskCompletionSource<bool>? _frameStepCompletion;
    private int _memoryCompactGeneration;
    private TimeSpan _pendingPlaybackStartPosition;
    private long _pendingPlayFirstFrameTimestamp;
    private long _pendingSeekFrameTimestamp;
    private long _pendingFrameStepTimestamp;
    private bool _videoWarmPaused;
    private bool _hasDisplayedSeekFrame;
    public event EventHandler? PositionChanged;

    public AutoGenPlayerControl()
    {
        BackColor = Color.Black;
        DoubleBuffered = true;

        _videoSurface.Dock = DockStyle.Fill;
        _videoSurface.FramePresented += (_, timestamp) =>
        {
            DisplayedFramePtsMilliseconds = (long)Math.Round(timestamp.TotalMilliseconds);
            ReportPendingFrameDiagnostics(timestamp);
            SyncPositionToDisplayedFrame(timestamp);
            CompleteVideoPrerollIfNeeded(timestamp);
        };

        Controls.Add(_videoSurface);

        _videoSurface.MouseDown += (_, e) => OnMouseDown(e);
        _videoSurface.MouseUp += (_, e) => OnMouseUp(e);
        _videoSurface.MouseMove += (_, e) => OnMouseMove(e);
        _videoSurface.MouseClick += (_, e) => OnMouseClick(e);
        _videoSurface.MouseDoubleClick += (_, e) => OnMouseDoubleClick(e);
        _videoSurface.MouseWheel += (_, e) =>
        {
            OnMouseWheel(e);
            StepFrames(e.Delta > 0 ? 1 : -1);
        };
        _videoSurface.MouseEnter += (_, _) =>
        {
            _videoSurface.Focus();
            OnMouseEnter(EventArgs.Empty);
        };
        _videoSurface.MouseLeave += (_, _) => OnMouseLeave(EventArgs.Empty);
        _videoSurface.MouseClick += videoSurface_MouseClick;
    }

    public string? SourceFile { get; private set; }
    public IReadOnlyList<MediaStreamInfo> AudioStreams { get; private set; } = [];
    public MediaVideoInfo VideoInfo { get; private set; } = MediaVideoInfo.Empty;
    public PlayerPlaybackState PlaybackState { get; private set; } = PlayerPlaybackState.Closed;
    public TimeSpan Position { get; private set; }
    public long DisplayedFramePtsMilliseconds { get; private set; }
    public TimeSpan AudioOutputLatencyEstimate
    {
        get => _audioMixPlayback?.OutputLatencyEstimate ?? TimeSpan.FromMilliseconds(160);
        set
        {
            EnsureFfmpeg();
            _audioMixPlayback!.OutputLatencyEstimate = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        }
    }
    public TimeSpan CurrentPosition => _clockRunning && PipelineMode == PlayerPipelineMode.AutoGenSingleStreamAv && _singleStreamAvPlayback?.IsPlaying == true
        ? _singleStreamAvPlayback.PlaybackClock
        : _clockRunning && _audioMixPlayback?.IsPlaying == true
        ? _audioMixPlayback.PlaybackClock
        : _clockRunning
        ? StopwatchClock
        : Position;
    private TimeSpan StopwatchClock => _playStartedPosition + TimeSpan.FromTicks((long)(GetElapsedSince(_playStartedTimestamp).Ticks * PlaybackSpeed));
    public double PlaybackSpeed { get; private set; } = 1.0;
    public PlayerPipelineMode PipelineMode { get; set; } = PlayerPipelineMode.AutoGenSingleStreamAv;

    public event EventHandler? MediaOpened;
    public event EventHandler? PlaybackStateChanged;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<double>? PlaybackSpeedChanged;
    public event EventHandler<PcmAudioSamplesAvailableEventArgs>? MixedAudioSamplesAvailable;
    public event EventHandler<AudioLevelSnapshotEventArgs>? AudioLevelsAvailable;
    public event EventHandler<PlayerDiagnosticEventArgs>? DiagnosticAvailable;

    public void ConfigureFfmpegDirectory(string? directory)
    {
        if (_tools is not null)
            throw new InvalidOperationException("Configure the FFmpeg directory before opening or playing media.");

        _preferredFfmpegDirectory = string.IsNullOrWhiteSpace(directory) ? null : directory;
    }

    public async Task OpenAsync(string sourceFile, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceFile))
            throw new FileNotFoundException("The media file does not exist.", sourceFile);

        Stopwatch openStopwatch = Stopwatch.StartNew();
        EnsureFfmpeg();

        Stop();
        SourceFile = sourceFile;
        SetStatus("Reading audio streams...");

        Stopwatch probeStopwatch = Stopwatch.StartNew();
        MediaProbeResult mediaInfo = await _probe!.ReadMediaInfoAsync(sourceFile, cancellationToken).ConfigureAwait(true);
        AudioStreams = mediaInfo.AudioStreams;
        VideoInfo = mediaInfo.VideoInfo;
        ReportDiagnostic("Probe media info", probeStopwatch.Elapsed, $"{AudioStreams.Count} audio stream(s), {VideoInfo.FrameRate:0.###} fps, {VideoInfo.FrameCount:n0} frame(s), duration {VideoInfo.Duration:hh\\:mm\\:ss\\.fff}");
        PlaybackState = PlayerPlaybackState.Opened;
        Position = GetPlayableStartPosition();
        _hasDisplayedSeekFrame = false;

        _videoSurface.SetMessage(Path.GetFileName(sourceFile) + Environment.NewLine +
            $"{AudioStreams.Count} audio stream(s) found" + Environment.NewLine +
            $"{VideoInfo.FrameCount:n0} frame(s)");

        MediaOpened?.Invoke(this, EventArgs.Empty);
        _videoPlayback?.PrepareForFile(sourceFile); // ← ΝΕΟ
        RaisePlaybackStateChanged();
        SetStatus("Media opened");
        ReportDiagnostic("Open media", openStopwatch.Elapsed, Path.GetFileName(sourceFile));
    }

    public void SetAudioMix(IReadOnlyList<AudioMixSelection> selections, TimeSpan audioAdvance)
    {
        if (SourceFile is null)
            throw new InvalidOperationException("Open a media file first.");

        Stopwatch stopwatch = Stopwatch.StartNew();
        EnsureFfmpeg();
        _audioMixPlayback!.Configure(SourceFile, selections, audioAdvance);
        _audioMixPlayback.PlaybackSpeed = PlaybackSpeed;
        SetStatus($"Audio mix configured: {selections.Count} stream(s)");
        ReportDiagnostic("Configure audio mix", stopwatch.Elapsed, $"{selections.Count} stream(s), advance {audioAdvance.TotalMilliseconds:0} ms");
    }

    public void ClearAudioMix()
    {
        TimeSpan currentPosition = CurrentPosition;
        _audioMixPlayback?.Clear();
        AudioLevelsAvailable?.Invoke(
            this,
            new AudioLevelSnapshotEventArgs([], [], default));

        if (PlaybackState == PlayerPlaybackState.Playing && PipelineMode == PlayerPipelineMode.AutoGenMixer)
        {
            StopClock(currentPosition);
            StartClock(currentPosition);
        }

        SetStatus("Audio mix cleared");
        ReportDiagnostic("Clear audio mix", TimeSpan.Zero, "0 stream(s)");
    }

    public void SetPlaybackSpeed(double speed)
    {
        double newSpeed = Math.Round(Math.Clamp(speed, 0.10, 4.00), 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(newSpeed - PlaybackSpeed) < 0.0001)
            return;

        TimeSpan currentPosition = ClampPlaybackPosition(_clockRunning ? CurrentPosition : Position);
        bool wasPlaying = PlaybackState == PlayerPlaybackState.Playing;
        PlaybackSpeed = newSpeed;
        PlaybackSpeedChanged?.Invoke(this, PlaybackSpeed);

        if (_audioMixPlayback is not null)
            _audioMixPlayback.PlaybackSpeed = PlaybackSpeed;
        if (_singleStreamAvPlayback is not null)
            _singleStreamAvPlayback.PlaybackSpeed = PlaybackSpeed;

        if (wasPlaying)
        {
            if (PipelineMode == PlayerPipelineMode.AutoGenSingleStreamAv)
            {
                _singleStreamAvPlayback?.Stop();
                StopClock(currentPosition);
                PlayAutoGenSingleStreamAvPipeline();
                SetStatus($"Speed: {PlaybackSpeed:0.00}x");
                return;
            }

            StopClock(currentPosition);
            if (_audioMixPlayback?.Selections.Count > 0)
            {
                _audioMixPlayback.Seek(currentPosition, resume: true);
                StartClock(currentPosition);
            }
            else
            {
                StartClock(currentPosition);
            }
        }

        SetStatus($"Speed: {PlaybackSpeed:0.00}x");
    }

    public void Play()
    {
        if (SourceFile is null)
            throw new InvalidOperationException("Open a media file first.");

        EnsureFfmpeg();

        if (Math.Abs(PlaybackSpeed - 1.0) > 0.0001)
        {
            SetPlaybackSpeed(1.0);
            if (PlaybackState == PlayerPlaybackState.Playing)
                return;
        }

        if (PlaybackState == PlayerPlaybackState.Playing || _waitingForVideoPreroll)
            return;

        if (PipelineMode == PlayerPipelineMode.AutoGenSingleStreamAv)
        {
            PlayAutoGenSingleStreamAvPipeline();
            return;
        }

        bool fastResume = PlaybackState == PlayerPlaybackState.Paused && !_waitingForVideoPreroll;
        TimeSpan startPosition = CurrentPosition;
        ClearFrameStepQueue();
        _pendingStepFrame = null;
        _pendingStepDirection = 0;
        _audioMixPlayback!.Pause();
        _audioMixPlayback.PlaybackSpeed = PlaybackSpeed;

        if (fastResume || _hasDisplayedSeekFrame)
            BeginFastPlayback(startPosition);
        else
            BeginPlaybackAfterVideoPreroll(startPosition);
    }

    public void Pause()
    {
        TimeSpan pausePosition = CurrentPosition;
        ClearFrameStepQueue();
        _pendingStepFrame = null;
        _pendingStepDirection = 0;
        _audioMixPlayback?.Pause();
        if (PipelineMode == PlayerPipelineMode.AutoGenSingleStreamAv)
        {
            _singleStreamAvPlayback?.Stop();
            pausePosition = GetDisplayedFramePositionOr(pausePosition);
        }

        Position = ClampPlaybackPosition(pausePosition);
        StopClock(Position);
        if (_waitingForVideoPreroll)
        {
            _videoPlayback?.Stop();
            _videoWarmPaused = false;
        }
        else
        {
            _videoWarmPaused = _videoPlayback?.IsPlaying == true;
        }

        _waitingForVideoPreroll = false;
        _hasDisplayedSeekFrame = PipelineMode == PlayerPipelineMode.AutoGenSingleStreamAv && DisplayedFramePtsMilliseconds > 0;
        PlaybackState = PlayerPlaybackState.Paused;
        RaisePlaybackStateChanged();
        SetStatus("Paused");
    }

    public void TogglePlayPause()
    {
        if (SourceFile is null || _waitingForVideoPreroll)
            return;

        if (PlaybackState == PlayerPlaybackState.Playing || _clockRunning)
            Pause();
        else
            Play();
    }

    public void Stop()
    {
        ClearFrameStepQueue();
        _audioMixPlayback?.Clear();
        _singleStreamAvPlayback?.Stop();
        bool videoStopped = _videoPlayback?.Stop(waitForDecoder: true) ?? true;
        _waitingForVideoPreroll = false;
        _videoWarmPaused = false;
        _hasDisplayedSeekFrame = false;
        _pendingStepFrame = null;
        _pendingStepDirection = 0;
        _videoSurface.ClearFrame();
        _videoSurface.SetMessage("No media loaded");
        SourceFile = null;
        AudioStreams = [];
        VideoInfo = MediaVideoInfo.Empty;
        Position = TimeSpan.Zero;
        DisplayedFramePtsMilliseconds = 0;
        StopClock(Position);
        PlaybackState = PlayerPlaybackState.Closed;
        RaisePlaybackStateChanged();
        SetStatus("Ready");
        CompactMemoryAfterFullStop();
        ScheduleDeferredMemoryCompaction(videoStopped);
    }

    public void Seek(TimeSpan position, bool resume)
    {
        Seek(position, resume, clearPendingStep: true, preserveVideoFrameCache: false);
    }

    private void Seek(TimeSpan position, bool resume, bool clearPendingStep, bool preserveVideoFrameCache)
    {
        CancelWarmVideoPause();
        _singleStreamAvPlayback?.Stop();
        Position = ClampPlaybackPosition(position);
        if (clearPendingStep)
        {
            ClearFrameStepQueue();
            _pendingStepFrame = null;
            _pendingStepDirection = 0;
        }

        if (!preserveVideoFrameCache)
            _videoPlayback?.Stop(clearFrameCache: true);

        StopClock(Position);

        if (!preserveVideoFrameCache)
            _audioMixPlayback?.Seek(Position, resume: false);

        if (resume && SourceFile is not null && PipelineMode == PlayerPipelineMode.AutoGenSingleStreamAv)
        {
            _videoPlayback?.ShowFrameAtForStep(SourceFile, Position);
            PlaybackState = PlayerPlaybackState.Paused;
            RaisePlaybackStateChanged();
            PlayAutoGenSingleStreamAvPipeline();
            SetStatus($"Seek/play: {Position:hh\\:mm\\:ss\\.fff}");
            return;
        }
        else if (resume && SourceFile is not null)
        {
            _hasDisplayedSeekFrame = false;
            BeginPlaybackAfterVideoPreroll(Position);
        }
        else if (SourceFile is not null && preserveVideoFrameCache)
        {
            _videoPlayback?.ShowFrameAtForStep(SourceFile, Position);
        }
        else if (SourceFile is not null)
        {
            _pendingSeekFrameTimestamp = Stopwatch.GetTimestamp();
            _videoPlayback?.ShowFrameAtForStep(SourceFile, Position);
        }

        PlaybackState = resume ? PlayerPlaybackState.Playing : PlayerPlaybackState.Paused;
        RaisePlaybackStateChanged();
        SetStatus($"Seek: {Position:hh\\:mm\\:ss\\.fff}");
    }

    public void StepFrames(int frameDelta)
    {
        if (SourceFile is null)
            return;

        if (VideoInfo.FrameRate <= 0 || VideoInfo.FrameCount <= 0)
            return;

        if (PlaybackState == PlayerPlaybackState.Playing || _clockRunning)
            Pause();
        else
        {
            _audioMixPlayback?.Pause();
            _waitingForVideoPreroll = false;
            PlaybackState = PlayerPlaybackState.Paused;
            RaisePlaybackStateChanged();
        }

        CancelWarmVideoPause();
        QueueFrameSteps(frameDelta);
    }

    public string CurrentTimecode
    {
        get
        {
            TimeSpan pos = CurrentPosition;
            double frameRate = VideoInfo.FrameRate > 0 ? VideoInfo.FrameRate : 25;
            int frames = (int)(pos.TotalSeconds * frameRate) % (int)Math.Round(frameRate);
            return $"{pos.Hours:D2}:{pos.Minutes:D2}:{pos.Seconds:D2}:{frames:D2}";
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearFrameStepQueue();
            _audioMixPlayback?.Dispose();
            _videoPlayback?.Dispose();
            _singleStreamAvPlayback?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void QueueFrameSteps(int frameDelta)
    {
        int direction = Math.Sign(frameDelta);
        if (direction == 0)
            return;

        int steps = Math.Abs(frameDelta);
        int generation;
        bool startWorker = false;
        TaskCompletionSource<bool>? pendingCompletion = null;

        lock (_frameStepQueueRoot)
        {
            for (int i = 0; i < steps; i++)
                _frameStepQueue.Enqueue(direction);

            generation = _frameStepGeneration;

            // ΝΕΟ: αν υπάρχει completion που περιμένει, ξύπνα το αμέσως
            if (_frameStepCompletion is not null)
            {
                pendingCompletion = _frameStepCompletion;
                _frameStepCompletion = null;
            }

            if (!_frameStepWorkerRunning)
            {
                _frameStepWorkerRunning = true;
                startWorker = true;
            }
        }

        // Έξω από το lock για να μην κάνουμε deadlock
        pendingCompletion?.TrySetResult(false);

        if (startWorker)
            _ = ProcessFrameStepQueueAsync(generation);
    }

    private async Task ProcessFrameStepQueueAsync(int generation)
    {
        while (!IsDisposed)
        {
            int direction;
            lock (_frameStepQueueRoot)
            {
                if (generation != _frameStepGeneration)
                    return;

                if (_frameStepQueue.Count == 0)
                {
                    _frameStepWorkerRunning = false;
                    return;
                }

                direction = _frameStepQueue.Dequeue();
            }

            if (SourceFile is null || VideoInfo.FrameRate <= 0 || VideoInfo.FrameCount <= 0)
            {
                ClearFrameStepQueue();
                return;
            }

            int currentFrame = PositionToFrame(Position);
            int targetFrame = Math.Clamp(
                currentFrame + direction,
                0,
                Math.Max(0, VideoInfo.FrameCount - 1));

            if (targetFrame == currentFrame)
                continue;

            TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _frameStepCompletion = completion;
            _pendingStepFrame = targetFrame;
            _pendingStepDirection = direction;
            _pendingFrameStepTimestamp = Stopwatch.GetTimestamp();

            Seek(FrameToPosition(targetFrame), resume: false, clearPendingStep: false, preserveVideoFrameCache: true);
            SetStatus($"Frame {targetFrame + 1:n0} / {VideoInfo.FrameCount:n0}");

            bool displayed = false;
            try
            {
                Task completedTask = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromMilliseconds(800)));
                displayed = completedTask == completion.Task && completion.Task.Result;
            }
            catch
            {
            }

            if (generation != _frameStepGeneration)
                return;

            if (!displayed)
            {
                _pendingStepFrame = null;
                _pendingStepDirection = 0;
                _frameStepCompletion = null;
            }
        }
    }

    private void ClearFrameStepQueue()
    {
        TaskCompletionSource<bool>? completion;
        lock (_frameStepQueueRoot)
        {
            _frameStepQueue.Clear();
            _frameStepGeneration++;
            _frameStepWorkerRunning = false;
            completion = _frameStepCompletion;
            _frameStepCompletion = null;
        }

        completion?.TrySetResult(false);
    }

    private void EnsureFfmpeg()
    {
        if (_tools is not null)
            return;

        _tools = FfmpegToolLocator.Locate(_preferredFfmpegDirectory);
        FfmpegLibraryLoader.Configure(_tools.LibraryDirectory);
        _probe = new MediaProbe(_tools.FfprobePath);
        _audioMixPlayback = new ExternalFfmpegAudioMixPlayback(_tools.FfmpegPath);
        _videoPlayback = new AutoGenVideoPlayback();
        _singleStreamAvPlayback = new AutoGenSingleStreamAvPlayback();
        _audioMixPlayback.MixedAudioSamplesAvailable += (_, e) => MixedAudioSamplesAvailable?.Invoke(this, e);
        _audioMixPlayback.AudioLevelsAvailable += (_, e) => AudioLevelsAvailable?.Invoke(this, e);
        _videoPlayback.FrameAvailable += (_, e) =>
        {
            _videoSurface.ShowFrame(e);
        };
        _videoPlayback.DecoderLog += (_, e) => SetStatus(e);
        _singleStreamAvPlayback.FrameAvailable += (_, e) =>
        {
            _videoSurface.ShowFrame(e);
        };
        _singleStreamAvPlayback.Log += (_, e) =>
        {
            SetStatus(e);
            ReportDiagnostic("AutoGen A/V trace", TimeSpan.Zero, e);
        };
        _videoPlayback.DecodeTasksDrained += (_, _) =>
        {
            if (PlaybackState != PlayerPlaybackState.Closed)
                return;

            RunCompactionOnUiThread();
        };
    }

    private static void CompactMemoryAfterFullStop()
    {
        VideoFrameBufferPool.Clear();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private void ScheduleDeferredMemoryCompaction(bool decoderStopped)
    {
        int generation = Interlocked.Increment(ref _memoryCompactGeneration);
        int[] delays = decoderStopped ? [250, 1000] : [500, 1500, 3000];

        _ = Task.Run(async () =>
        {
            foreach (int delay in delays)
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (Volatile.Read(ref _memoryCompactGeneration) != generation || IsDisposed)
                    return;

                try
                {
                    if (IsHandleCreated)
                        BeginInvoke(new Action(CompactMemoryAfterFullStop));
                }
                catch
                {
                    return;
                }
            }
        });
    }

    private void RunCompactionOnUiThread()
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(RunCompactionOnUiThread));
            }
            catch
            {
            }

            return;
        }

        CompactMemoryAfterFullStop();
    }

    private void RaisePlaybackStateChanged()
    {
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetStatus(string status)
    {
        StatusChanged?.Invoke(this, status);
    }

    private void ReportDiagnostic(string operation, TimeSpan elapsed, string? details = null)
    {
        DiagnosticAvailable?.Invoke(this, new PlayerDiagnosticEventArgs(operation, elapsed, details));
    }

    private void CancelWarmVideoPause()
    {
        if (!_videoWarmPaused)
            return;

        _videoWarmPaused = false;
        _videoPlayback?.Stop(clearFrameCache: false);
    }

    private void StartClock(TimeSpan position)
    {
        _playStartedPosition = position;
        _playStartedTimestamp = Stopwatch.GetTimestamp();
        _clockRunning = true;
    }

    private void PlayAutoGenSingleStreamAvPipeline()
    {
        if (SourceFile is null)
            throw new InvalidOperationException("Open a media file first.");

        EnsureFfmpeg();
        if (_singleStreamAvPlayback is null)
            throw new InvalidOperationException("The AutoGen A/V pipeline is not initialized.");

        TimeSpan startPosition = CurrentPosition;
        ClearFrameStepQueue();
        _audioMixPlayback?.Pause();
        _videoPlayback?.Stop(clearFrameCache: false);

        MediaStreamInfo? audioStream = AudioStreams.Count > 0 ? AudioStreams[0] : null;
        _singleStreamAvPlayback.PlaybackSpeed = PlaybackSpeed;
        _singleStreamAvPlayback.Start(SourceFile, startPosition, audioStream);
        StartClock(startPosition);
        PlaybackState = PlayerPlaybackState.Playing;
        RaisePlaybackStateChanged();
        SetStatus("Playing AutoGen A/V pipeline");
        ReportDiagnostic("AutoGen A/V play start", TimeSpan.Zero, audioStream is null ? "video only" : $"audio stream {audioStream.StreamIndex}");
    }

    private void StopClock(TimeSpan position)
    {
        Position = position;
        _playStartedPosition = position;
        _playStartedTimestamp = Stopwatch.GetTimestamp();
        _clockRunning = false;
    }

    private void SyncPositionToDisplayedFrame(TimeSpan timestamp)
    {

        if (PlaybackState == PlayerPlaybackState.Playing && !_waitingForVideoPreroll)
            return;

        if (_waitingForVideoPreroll || PlaybackState == PlayerPlaybackState.Playing)
            return;

        Position = ClampPlaybackPosition(timestamp);
        StopClock(Position);
        _hasDisplayedSeekFrame = true;
        int displayedFrame = PositionToFrame(Position);
        if (_pendingStepFrame.HasValue)
        {
            bool reachedPendingFrame = _pendingStepDirection > 0
                ? displayedFrame >= _pendingStepFrame.Value
                : _pendingStepDirection < 0
                ? displayedFrame <= _pendingStepFrame.Value
                : displayedFrame == _pendingStepFrame.Value;

            if (reachedPendingFrame)
            {
                _pendingStepFrame = null;
                _pendingStepDirection = 0;
                _frameStepCompletion?.TrySetResult(true);
                _frameStepCompletion = null;
            }
            else
            {
                PositionChanged?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        PositionChanged?.Invoke(this, EventArgs.Empty);

    }

    private static TimeSpan GetElapsedSince(long startTimestamp)
    {
        long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
        double elapsedSeconds = elapsedTicks / (double)Stopwatch.Frequency;
        return TimeSpan.FromSeconds(elapsedSeconds);
    }

    private TimeSpan GetPlaybackClockForVideo()
    {
        if (_waitingForVideoPreroll)
            return _pendingPlaybackStartPosition;

        if (_audioMixPlayback?.IsPlaying == true)
            return _audioMixPlayback.PlaybackClock;

        return _clockRunning ? StopwatchClock : Position;
    }

    private TimeSpan GetDisplayedFramePositionOr(TimeSpan fallback)
    {
        if (DisplayedFramePtsMilliseconds <= 0)
            return ClampPlaybackPosition(fallback);

        return ClampPlaybackPosition(TimeSpan.FromMilliseconds(DisplayedFramePtsMilliseconds));
    }

    private void BeginPlaybackAfterVideoPreroll(TimeSpan startPosition)
    {
        if (SourceFile is null || _videoPlayback is null || _audioMixPlayback is null)
            return;

        Position = startPosition;
        _pendingPlaybackStartPosition = startPosition;
        _waitingForVideoPreroll = true;
        _videoWarmPaused = false;
        _hasDisplayedSeekFrame = false;
        _pendingSeekFrameTimestamp = Stopwatch.GetTimestamp();
        StopClock(startPosition);

        // ΝΕΟ: δείξε πρώτα το σωστό frame μέσω του stepper
        // Όταν εμφανιστεί, το FramePresented event θα καλέσει
        // CompleteVideoPrerollIfNeeded που θα ξεκινήσει τον continuous decoder
        _videoPlayback.ShowFrameAtForStep(SourceFile, startPosition);

        PlaybackState = PlayerPlaybackState.Playing;
        RaisePlaybackStateChanged();
        SetStatus("Preparing video frame...");
    }

    private void BeginFastPlayback(TimeSpan startPosition)
    {
        if (SourceFile is null || _videoPlayback is null || _audioMixPlayback is null)
            return;

        Position = ClampPlaybackPosition(startPosition);
        bool instantVisualStart = _hasDisplayedSeekFrame;
        bool warmResume = _videoWarmPaused && _videoPlayback.IsPlaying;
        _waitingForVideoPreroll = false;
        _pendingPlaybackStartPosition = Position;
        StopClock(Position);

        if (warmResume)
        {
            _videoWarmPaused = false;
            _hasDisplayedSeekFrame = false;
            ReportDiagnostic("Warm video resume", TimeSpan.Zero, $"position {Position:hh\\:mm\\:ss\\.fff}");
            ReportDiagnostic("Play to first frame", TimeSpan.Zero, $"warm displayed frame pts {DisplayedFramePtsMilliseconds} ms");
        }
        else
        {
            _videoWarmPaused = false;
            _videoPlayback.Stop();
            if (instantVisualStart)
            {
                TimeSpan nextFramePosition = VideoInfo.FrameRate > 0
                    ? ClampPlaybackPosition(Position + TimeSpan.FromSeconds(1.0 / VideoInfo.FrameRate))
                    : Position;
                _pendingPlayFirstFrameTimestamp = Stopwatch.GetTimestamp();
                _videoPlayback.ShowFrameAtForStep(SourceFile, nextFramePosition);
            }

            _videoPlayback.Start(SourceFile, Position, GetPlaybackClockForVideo, closeFrameStepper: !instantVisualStart);
        }

        _hasDisplayedSeekFrame = false;
        if (_pendingPlayFirstFrameTimestamp == 0 && !warmResume && !instantVisualStart)
            _pendingPlayFirstFrameTimestamp = Stopwatch.GetTimestamp();
        StartClock(Position);
        if (_audioMixPlayback.Selections.Count > 0)
            _audioMixPlayback.Start(Position);
        PlaybackState = PlayerPlaybackState.Playing;
        RaisePlaybackStateChanged();
        SetStatus("Playing");
    }

    private void CompleteVideoPrerollIfNeeded(TimeSpan frameTimestamp = default)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => CompleteVideoPrerollIfNeeded(frameTimestamp)));
            return;
        }

        if (!_waitingForVideoPreroll || _audioMixPlayback is null)
            return;

        double frameRate = VideoInfo.FrameRate > 0 ? VideoInfo.FrameRate : 25;
        TimeSpan oneFrame = TimeSpan.FromSeconds(1.0 / frameRate);
        if (frameTimestamp != default && frameTimestamp > _pendingPlaybackStartPosition + oneFrame)
            return;

        TimeSpan startPosition = _pendingPlaybackStartPosition;
        _waitingForVideoPreroll = false;
        _videoWarmPaused = false;
        _hasDisplayedSeekFrame = false;
        Position = startPosition;

        // Τώρα ξεκίνα τον continuous decoder από το σωστό frame
        _videoPlayback!.Stop();
        _videoPlayback.Start(SourceFile!, startPosition, GetPlaybackClockForVideo);

        _pendingPlayFirstFrameTimestamp = Stopwatch.GetTimestamp();
        StartClock(startPosition);
        if (_audioMixPlayback.Selections.Count > 0)
            _audioMixPlayback.Start(startPosition);
        PlaybackState = PlayerPlaybackState.Playing;
        RaisePlaybackStateChanged();
        SetStatus("Playing");
    }

    private void ReportPendingFrameDiagnostics(TimeSpan frameTimestamp)
    {
        long playStarted = Interlocked.Exchange(ref _pendingPlayFirstFrameTimestamp, 0);
        if (playStarted != 0)
        {
            if (PlaybackState == PlayerPlaybackState.Playing && !_waitingForVideoPreroll)
                _videoPlayback?.ReleaseFrameStepper();

            ReportDiagnostic("Play to first frame", Stopwatch.GetElapsedTime(playStarted), $"frame pts {frameTimestamp:hh\\:mm\\:ss\\.fff}");
        }

        long seekStarted = Interlocked.Exchange(ref _pendingSeekFrameTimestamp, 0);
        if (seekStarted != 0)
            ReportDiagnostic("Seek to displayed frame", Stopwatch.GetElapsedTime(seekStarted), $"frame pts {frameTimestamp:hh\\:mm\\:ss\\.fff}");

        long stepStarted = Interlocked.Exchange(ref _pendingFrameStepTimestamp, 0);
        if (stepStarted != 0)
            ReportDiagnostic("Frame step to displayed frame", Stopwatch.GetElapsedTime(stepStarted), $"frame pts {frameTimestamp:hh\\:mm\\:ss\\.fff}");
    }

    private void videoSurface_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            TogglePlayPause();
    }

    public TimeSpan FrameToPosition(int frame)
    {
        if (VideoInfo.FrameRate <= 0)
            return GetPlayableStartPosition();

        if (frame >= Math.Max(0, VideoInfo.FrameCount - 1))
            return GetPlayableEndPosition();

        return ClampPlaybackPosition(GetPlayableStartPosition() + TimeSpan.FromSeconds(frame / VideoInfo.FrameRate));
    }

    public int PositionToFrame(TimeSpan position)
    {
        if (VideoInfo.FrameRate <= 0)
            return 0;

        TimeSpan relativePosition = position - GetPlayableStartPosition();
        if (relativePosition < TimeSpan.Zero)
            relativePosition = TimeSpan.Zero;

        double frame = relativePosition.TotalSeconds * VideoInfo.FrameRate;
        return Math.Clamp((int)Math.Round(frame), 0, Math.Max(0, VideoInfo.FrameCount - 1));
    }

    public TimeSpan ClampPlaybackPosition(TimeSpan position)
    {
        TimeSpan startPosition = GetPlayableStartPosition();
        if (position < startPosition)
            return startPosition;

        TimeSpan endPosition = GetPlayableEndPosition();
        return position > endPosition ? endPosition : position;
    }

    public TimeSpan GetPlayableStartPosition()
    {
        return VideoInfo.StartTime > TimeSpan.Zero ? VideoInfo.StartTime : TimeSpan.Zero;
    }

    public TimeSpan GetPlayableEndPosition()
    {
        if (VideoInfo.Duration <= TimeSpan.Zero)
            return TimeSpan.MaxValue;

        TimeSpan frameDuration = VideoInfo.FrameRate > 0
            ? TimeSpan.FromSeconds(1 / VideoInfo.FrameRate)
            : TimeSpan.Zero;
        TimeSpan endPosition = GetPlayableStartPosition() + VideoInfo.Duration - frameDuration;
        TimeSpan startPosition = GetPlayableStartPosition();

        return endPosition > startPosition ? endPosition : startPosition;
    }
}
