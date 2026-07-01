using AutoGen_Player.Core;
using NAudio.Wave;
using System.Diagnostics;
using System.Globalization;

namespace AutoGen_Player.Audio;

internal sealed class ExternalFfmpegAudioMixPlayback : IDisposable
{
    private static readonly TimeSpan DefaultSeekPreroll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TransportStreamSeekPreroll = TimeSpan.FromSeconds(5);
    private readonly string _ffmpegPath;
    private readonly object _syncRoot = new();
    private Process? _process;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _bufferedWaveProvider;
    private CancellationTokenSource? _pumpCancellation;
    private Task? _pumpTask;
    private bool _disposed;
    private byte[] _pendingBusBytes = [];
    private readonly object _clockRoot = new();
    private long _mixedBytesQueued;

    public ExternalFfmpegAudioMixPlayback(string ffmpegPath)
    {
        _ffmpegPath = ffmpegPath;
    }

    public bool IsPlaying { get; private set; }
    public string? SourceFile { get; private set; }
    public IReadOnlyList<AudioMixSelection> Selections { get; private set; } = [];
    public TimeSpan AudioAdvance { get; private set; }
    public TimeSpan StartPosition { get; private set; }
    public TimeSpan OutputLatencyEstimate { get; set; } = TimeSpan.FromMilliseconds(160);
    public double PlaybackSpeed { get; set; } = 1.0;
    public TimeSpan PlaybackClock
    {
        get
        {
            lock (_clockRoot)
            {
                TimeSpan queuedAudio = BytesToDuration(_mixedBytesQueued);
                TimeSpan buffered = _bufferedWaveProvider?.BufferedDuration ?? TimeSpan.Zero;
                double speed = Math.Max(0, PlaybackSpeed);
                TimeSpan playedOutput = queuedAudio - buffered - OutputLatencyEstimate;
                if (playedOutput < TimeSpan.Zero)
                    playedOutput = TimeSpan.Zero;

                TimeSpan clock = StartPosition + TimeSpan.FromTicks((long)(playedOutput.Ticks * speed));
                return clock < StartPosition ? StartPosition : clock;
            }
        }
    }

    public event EventHandler<PcmAudioSamplesAvailableEventArgs>? MixedAudioSamplesAvailable;
    public event EventHandler<AudioLevelSnapshotEventArgs>? AudioLevelsAvailable;

    public void Configure(
        string sourceFile,
        IReadOnlyList<AudioMixSelection> selections,
        TimeSpan audioAdvance)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (selections.Count == 0)
            throw new InvalidOperationException("Select at least one audio stream.");

        SourceFile = sourceFile;
        Selections = selections.ToArray();
        AudioAdvance = audioAdvance;
    }

    public void Start(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(SourceFile))
            throw new InvalidOperationException("No audio mix source has been configured.");

        lock (_syncRoot)
        {
            StopLocked();
            _pendingBusBytes = [];

            StartPosition = position < TimeSpan.Zero ? TimeSpan.Zero : position;
            lock (_clockRoot)
                _mixedBytesQueued = 0;

            _process = StartFfmpeg(StartPosition);

            WaveFormat waveFormat = new(48000, 16, 2);
            _bufferedWaveProvider = new BufferedWaveProvider(waveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(5),
                DiscardOnBufferOverflow = false,
                ReadFully = true
            };

            _pumpCancellation = new CancellationTokenSource();
            _pumpTask = Task.Run(
                () => PumpFfmpegAudioAsync(_process, _bufferedWaveProvider, _pumpCancellation.Token),
                _pumpCancellation.Token);

            _waveOut = new WaveOutEvent
            {
                DesiredLatency = 160,
                NumberOfBuffers = 4
            };

            _waveOut.Init(_bufferedWaveProvider);
            _waveOut.Play();
            IsPlaying = true;
        }
    }

    public void Pause()
    {
        lock (_syncRoot)
        {
            StopLocked();
            IsPlaying = false;
        }
    }

    public void Stop()
    {
        lock (_syncRoot)
        {
            StopLocked();
            IsPlaying = false;
            StartPosition = TimeSpan.Zero;
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            StopLocked();
            IsPlaying = false;
            SourceFile = null;
            Selections = [];
            AudioAdvance = TimeSpan.Zero;
            StartPosition = TimeSpan.Zero;
        }
    }

    public void Seek(TimeSpan position, bool resume)
    {
        lock (_syncRoot)
        {
            StopLocked();
            IsPlaying = false;
            StartPosition = position < TimeSpan.Zero ? TimeSpan.Zero : position; // ← μέσα στο lock
        }

        if (resume)
            Start(StartPosition);
    }

    private Process StartFfmpeg(TimeSpan position)
    {
        Process process = new();
        process.StartInfo.FileName = _ffmpegPath;
        process.StartInfo.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
        process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        foreach (string argument in BuildFfmpegArguments(position))
            process.StartInfo.ArgumentList.Add(argument);

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Debug.WriteLine("[ExternalAudioMix] " + e.Data);
        };

        process.Start();
        process.BeginErrorReadLine();
        return process;
    }

    private async Task PumpFfmpegAudioAsync(
        Process process,
        BufferedWaveProvider buffer,
        CancellationToken cancellationToken)
    {
        byte[] readBuffer = new byte[32 * 1024];

        try
        {
            Stream stream = process.StandardOutput.BaseStream;

            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await stream
                    .ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), cancellationToken)
                    .ConfigureAwait(false);

                if (bytesRead <= 0)
                    break;

                byte[] mixedBytes = MixBusToStereo(readBuffer, bytesRead);
                if (mixedBytes.Length == 0)
                    continue;

                while (!cancellationToken.IsCancellationRequested &&
                       buffer.BufferedBytes + mixedBytes.Length > buffer.BufferLength)
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }

                buffer.AddSamples(mixedBytes, 0, mixedBytes.Length);
                lock (_clockRoot)
                    _mixedBytesQueued += mixedBytes.Length;

                MixedAudioSamplesAvailable?.Invoke(
                    this,
                    new PcmAudioSamplesAvailableEventArgs(mixedBytes, 0, mixedBytes.Length, 48000, 2));

                while (!cancellationToken.IsCancellationRequested &&
                       buffer.BufferedDuration > TimeSpan.FromMilliseconds(900))
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[ExternalAudioMix] Audio pump stopped: " + ex.Message);
        }
    }

    private IReadOnlyList<string> BuildFfmpegArguments(TimeSpan position)
    {
        if (SourceFile is null)
            throw new InvalidOperationException("No source file has been configured.");

        List<string> filters = [];
        List<string> labels = [];
        TimeSpan audioStartPosition = position + AudioAdvance;
        if (audioStartPosition < TimeSpan.Zero)
            audioStartPosition = TimeSpan.Zero;

        TimeSpan seekPreroll = IsTransportStream(SourceFile)
            ? TransportStreamSeekPreroll
            : DefaultSeekPreroll;

        TimeSpan inputSeekPosition;
        TimeSpan audioTrim;

        if (audioStartPosition > seekPreroll)
        {
            inputSeekPosition = audioStartPosition - seekPreroll;
            audioTrim = seekPreroll;
        }
        else
        {
            inputSeekPosition = TimeSpan.Zero;
            audioTrim = audioStartPosition;
        }

        for (int i = 0; i < Selections.Count; i++)
        {
            AudioMixSelection selection = Selections[i];
            string label = $"a{i}";
            string trimFilter = audioTrim > TimeSpan.Zero
                ? $"atrim=start={FormatSeconds(audioTrim)},asetpts=PTS-STARTPTS,"
                : string.Empty;

            filters.Add(
                $"[0:{selection.StreamIndex}]{trimFilter}aresample=48000:async=1000:min_hard_comp=0.100:first_pts=0," +
                $"aformat=sample_fmts=s16:sample_rates=48000:channel_layouts=stereo[{label}]");
            labels.Add($"[{label}]");
        }

        string speedFilter = BuildAtempoFilter(PlaybackSpeed);
        string filterComplex = string.Join(";", filters) + ";" +
            string.Join(string.Empty, labels) +
            $"amerge=inputs={Selections.Count},aformat=sample_fmts=s16:sample_rates=48000{speedFilter}[bus]";

        return
        [
            "-hide_banner",
            "-loglevel", "warning",
            "-nostdin",
            "-ss", FormatSeconds(inputSeekPosition),
            "-i", SourceFile,
            "-filter_complex", filterComplex,
            "-map", "[bus]",
            "-vn",
            "-sn",
            "-dn",
            "-f", "s16le",
            "-acodec", "pcm_s16le",
            "-ar", "48000",
            "pipe:1"
        ];
    }

    private byte[] MixBusToStereo(byte[] sourceBuffer, int bytesRead)
    {
        int streamCount = Selections.Count;
        if (streamCount == 0)
            return [];

        int frameBytes = streamCount * 2 * sizeof(short);
        int totalBytes = _pendingBusBytes.Length + bytesRead;
        byte[] busBytes = new byte[totalBytes];

        if (_pendingBusBytes.Length > 0)
            Buffer.BlockCopy(_pendingBusBytes, 0, busBytes, 0, _pendingBusBytes.Length);

        Buffer.BlockCopy(sourceBuffer, 0, busBytes, _pendingBusBytes.Length, bytesRead);

        int usableBytes = totalBytes - (totalBytes % frameBytes);
        int remainderBytes = totalBytes - usableBytes;

        if (remainderBytes > 0)
        {
            _pendingBusBytes = new byte[remainderBytes];
            Buffer.BlockCopy(busBytes, usableBytes, _pendingBusBytes, 0, remainderBytes);
        }
        else
        {
            _pendingBusBytes = [];
        }

        if (usableBytes == 0)
            return [];

        int frameCount = usableBytes / frameBytes;
        byte[] mixedBytes = new byte[frameCount * 2 * sizeof(short)];
        float[] leftPeaks = new float[streamCount];
        float[] rightPeaks = new float[streamCount];
        float mixedLeftPeak = 0;
        float mixedRightPeak = 0;

        for (int frame = 0; frame < frameCount; frame++)
        {
            int frameOffset = frame * frameBytes;
            float mixedLeft = 0;
            float mixedRight = 0;

            for (int stream = 0; stream < streamCount; stream++)
            {
                int sampleOffset = frameOffset + stream * 2 * sizeof(short);
                float volume = Math.Max(0, Selections[stream].VolumePercent / 100f);
                float left = BitConverter.ToInt16(busBytes, sampleOffset) / 32768f * volume;
                float right = BitConverter.ToInt16(busBytes, sampleOffset + sizeof(short)) / 32768f * volume;

                leftPeaks[stream] = Math.Max(leftPeaks[stream], Math.Abs(left));
                rightPeaks[stream] = Math.Max(rightPeaks[stream], Math.Abs(right));

                mixedLeft += left;
                mixedRight += right;
            }

            mixedLeft = SoftClip(mixedLeft);
            mixedRight = SoftClip(mixedRight);
            mixedLeftPeak = Math.Max(mixedLeftPeak, Math.Abs(mixedLeft));
            mixedRightPeak = Math.Max(mixedRightPeak, Math.Abs(mixedRight));

            int outputOffset = frame * 2 * sizeof(short);
            short leftSample = (short)Math.Clamp((int)MathF.Round(mixedLeft * short.MaxValue), short.MinValue, short.MaxValue);
            short rightSample = (short)Math.Clamp((int)MathF.Round(mixedRight * short.MaxValue), short.MinValue, short.MaxValue);
            BitConverter.GetBytes(leftSample).CopyTo(mixedBytes, outputOffset);
            BitConverter.GetBytes(rightSample).CopyTo(mixedBytes, outputOffset + sizeof(short));
        }

        StereoLevel[] streamLevels = new StereoLevel[streamCount];
        for (int i = 0; i < streamCount; i++)
            streamLevels[i] = new StereoLevel(Math.Clamp(leftPeaks[i], 0, 1), Math.Clamp(rightPeaks[i], 0, 1));

        AudioLevelsAvailable?.Invoke(
            this,
            new AudioLevelSnapshotEventArgs(
                Selections,
                streamLevels,
                new StereoLevel(Math.Clamp(mixedLeftPeak, 0, 1), Math.Clamp(mixedRightPeak, 0, 1))));

        return mixedBytes;
    }

    private static float SoftClip(float value)
    {
        return MathF.Tanh(value) * 0.95f;
    }

    private void StopLocked()
    {
        try { _waveOut?.Stop(); } catch { }
        _waveOut?.Dispose();
        _waveOut = null;

        try { _pumpCancellation?.Cancel(); } catch { }

        // ΝΕΟ: περίμενε το pump task να τελειώσει πριν κάνεις dispose τα resources
        try { _pumpTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }

        _pumpTask = null;
        _pumpCancellation?.Dispose();
        _pumpCancellation = null;
        _bufferedWaveProvider = null;

        if (_process != null)
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            _process.Dispose();
            _process = null;
        }

        _pendingBusBytes = [];
        lock (_clockRoot)
            _mixedBytesQueued = 0;
    }

    private static string FormatSeconds(TimeSpan value)
    {
        return Math.Max(0, value.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static bool IsTransportStream(string path)
    {
        return string.Equals(Path.GetExtension(path), ".ts", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildAtempoFilter(double playbackSpeed)
    {
        double speed = Math.Max(0, playbackSpeed);
        if (speed <= 0)
            throw new InvalidOperationException("Audio playback speed must be greater than 0.");

        if (Math.Abs(speed - 1.0) < 0.0001)
            return string.Empty;

        List<string> filters = [];
        while (speed < 0.5)
        {
            filters.Add("atempo=0.5");
            speed /= 0.5;
        }

        while (speed > 2.0)
        {
            filters.Add("atempo=2.0");
            speed /= 2.0;
        }

        filters.Add("atempo=" + speed.ToString("0.###", CultureInfo.InvariantCulture));
        return "," + string.Join(",", filters);
    }

    private static TimeSpan BytesToDuration(long bytes)
    {
        const int sampleRate = 48000;
        const int channels = 2;
        const int bytesPerSample = 2;
        const int bytesPerSecond = sampleRate * channels * bytesPerSample;
        return bytes <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(bytes / (double)bytesPerSecond);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _disposed = true;
    }
}
