using FFmpeg.AutoGen;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AutoGen_Player.Video;

internal sealed unsafe class AutoGenVideoPlayback : IDisposable
{
    private const int SwsBilinear = 2;
    private static readonly TimeSpan PlaybackStartTolerance = TimeSpan.Zero;
    private static readonly TimeSpan CachedFrameTolerance = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan SingleFrameFallbackWindow = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SingleFrameSearchWindow = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan TransportStreamVideoSeekPreroll = TimeSpan.FromSeconds(5);
    private readonly PersistentFrameStepper _frameStepper = new();
    private const int SingleFrameCacheCount = 12;
    private const long SingleFrameCacheMaxBytes = 64L * 1024 * 1024;
    private readonly object _syncRoot = new();
    private readonly VideoFrameCache _singleFrameCache = new(SingleFrameCacheMaxBytes);
    private readonly List<Task> _activeDecodeTasks = [];
    private CancellationTokenSource? _cancellation;
    private Task? _decodeTask;
    private long _requestSerial;
    private bool _disposed;

    public bool IsPlaying { get; private set; }

    public event EventHandler<VideoFrameEventArgs>? FrameAvailable;
    public event EventHandler<string>? DecoderLog;
    public event EventHandler? DecodeTasksDrained;

    // ΝΕΟ: constructor
    public AutoGenVideoPlayback()
    {
        _frameStepper.FrameAvailable += (_, e) => FrameAvailable?.Invoke(this, e);
        _frameStepper.Log += (_, msg) => DecoderLog?.Invoke(this, msg);
    }

    public void Start(string sourceFile, TimeSpan position, Func<TimeSpan>? clock = null, bool closeFrameStepper = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (closeFrameStepper)
            _frameStepper.Close();

        lock (_syncRoot)
        {
            StopLocked();
            ClearAllFrameCache();
            long requestSerial = Interlocked.Increment(ref _requestSerial);
            _cancellation = new CancellationTokenSource();
            Task decodeTask = Task.Run(
                () => DecodeLoop(sourceFile, position, clock, singleFrame: false, requestSerial, _cancellation.Token),
                _cancellation.Token);
            _decodeTask = decodeTask;
            TrackDecodeTask(decodeTask);
            IsPlaying = true;
        }
    }

    public void ShowFrameAt(string sourceFile, TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (TryShowCachedFrame(sourceFile, position))
            return;

        StartSingleFrameDecode(sourceFile, position, clearFrameCache: false);
    }

    public void ShowFrameAtForStep(string sourceFile, TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _frameStepper.RequestFrame(sourceFile, position);
    }

    public void ReleaseFrameStepper()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _frameStepper.Close();
    }

    private void StartSingleFrameDecode(string sourceFile, TimeSpan position, bool clearFrameCache)
    {
        lock (_syncRoot)
        {
            StopLocked();
            if (clearFrameCache)
                ClearFrameCache(sourceFile);
            else
                _singleFrameCache.PrepareForSource(sourceFile);

            long requestSerial = Interlocked.Increment(ref _requestSerial);
            _cancellation = new CancellationTokenSource();
            Task decodeTask = Task.Run(
                () => DecodeLoop(sourceFile, position, clock: null, singleFrame: true, requestSerial, _cancellation.Token),
                _cancellation.Token);
            _decodeTask = decodeTask;
            TrackDecodeTask(decodeTask);
            IsPlaying = true;
        }
    }

    public bool Stop(bool clearFrameCache = true, bool waitForDecoder = false)
    {
        lock (_syncRoot)
        {
            Interlocked.Increment(ref _requestSerial);
            bool stopped = StopLocked(waitForDecoder);
            if (clearFrameCache)
                ClearAllFrameCache();
            IsPlaying = false;
            return stopped;
        }
    }

    private bool StopLocked(bool waitForDecoder = false)
    {
        CancellationTokenSource? cancellation = _cancellation;
        Task? decodeTask = _decodeTask;
        _decodeTask = null;
        _cancellation = null;

        try
        {
            cancellation?.Cancel();
        }
        catch
        {
        }

        bool taskCompleted = decodeTask is null || decodeTask.IsCompleted;
        try
        {
            if (waitForDecoder)
                taskCompleted = WaitForActiveDecodeTasks(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static inner => inner is OperationCanceledException or TaskCanceledException))
        {
            taskCompleted = true;
        }
        catch (OperationCanceledException)
        {
            taskCompleted = true;
        }
        catch
        {
            taskCompleted = true;
        }

        if (taskCompleted)
            cancellation?.Dispose();
        else
            _ = decodeTask!.ContinueWith(_ => cancellation?.Dispose(), TaskScheduler.Default);

        return taskCompleted;
    }

    private void TrackDecodeTask(Task decodeTask)
    {
        _activeDecodeTasks.RemoveAll(static task => task.IsCompleted);
        _activeDecodeTasks.Add(decodeTask);
        _ = decodeTask.ContinueWith(
            completedTask =>
            {
                bool drained;
                lock (_syncRoot)
                {
                    _activeDecodeTasks.Remove(completedTask);
                    _activeDecodeTasks.RemoveAll(static task => task.IsCompleted);
                    drained = _activeDecodeTasks.Count == 0;
                }

                if (drained)
                    DecodeTasksDrained?.Invoke(this, EventArgs.Empty);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool WaitForActiveDecodeTasks(TimeSpan timeout)
    {
        Task[] tasks = _activeDecodeTasks
            .Where(static task => !task.IsCompleted && Task.CurrentId != task.Id)
            .ToArray();

        if (tasks.Length == 0)
            return true;

        Stopwatch stopwatch = Stopwatch.StartNew();
        foreach (Task task in tasks)
        {
            TimeSpan remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return false;

            if (!task.Wait(remaining))
                return false;
        }

        _activeDecodeTasks.RemoveAll(static task => task.IsCompleted);
        return true;
    }

    private void DecodeLoop(
        string sourceFile,
        TimeSpan position,
        Func<TimeSpan>? clock,
        bool singleFrame,
        long requestSerial,
        CancellationToken cancellationToken)
    {
        AVFormatContext* formatContext = null;
        AVCodecContext* codecContext = null;
        AVFrame* frame = null;
        AVFrame* bgrFrame = null;
        AVPacket* packet = null;
        SwsContext* swsContext = null;
        VideoFrameEventArgs? singleFrameFallback = null;
        bool singleFrameEmitted = false;
        int cacheFramesRemaining = 0;

        try
        {
            ThrowIfError(ffmpeg.avformat_open_input(&formatContext, sourceFile, null, null), "open input");
            ThrowIfError(ffmpeg.avformat_find_stream_info(formatContext, null), "find stream info");
            TimeSpan mediaStartTime = GetFormatStartTime(formatContext);

            int videoStreamIndex = FindVideoStream(formatContext);
            if (videoStreamIndex < 0)
                throw new InvalidOperationException("No video stream was found.");

            AVStream* videoStream = formatContext->streams[videoStreamIndex];
            AVCodecParameters* codecParameters = videoStream->codecpar;
            AVCodec* codec = ffmpeg.avcodec_find_decoder(codecParameters->codec_id);
            if (codec is null)
                throw new InvalidOperationException("No decoder was found for the video stream.");

            codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (codecContext is null)
                throw new InvalidOperationException("Could not allocate video codec context.");

            ThrowIfError(ffmpeg.avcodec_parameters_to_context(codecContext, codecParameters), "copy codec parameters");
            codecContext->thread_count = Math.Min(4, Environment.ProcessorCount);
            codecContext->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
            ThrowIfError(ffmpeg.avcodec_open2(codecContext, codec, null), "open decoder");

            frame = ffmpeg.av_frame_alloc();
            bgrFrame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            if (frame is null || bgrFrame is null || packet is null)
                throw new InvalidOperationException("Could not allocate video decode buffers.");

            int width = codecContext->width;
            int height = codecContext->height;
            double displayAspectRatio = GetDisplayAspectRatio(codecContext, videoStream, width, height);
            bgrFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGR24;
            bgrFrame->width = width;
            bgrFrame->height = height;
            ThrowIfError(ffmpeg.av_frame_get_buffer(bgrFrame, 1), "allocate BGR frame");

            swsContext = ffmpeg.sws_getContext(
                width,
                height,
                codecContext->pix_fmt,
                width,
                height,
                AVPixelFormat.AV_PIX_FMT_BGR24,
                SwsBilinear,
                null,
                null,
                null);

            if (swsContext is null)
                throw new InvalidOperationException("Could not create video scaler.");

            TimeSpan decodeStartPosition = GetDecodeStartPosition(sourceFile, position);
            if (decodeStartPosition > TimeSpan.Zero)
                Seek(formatContext, codecContext, videoStreamIndex, videoStream, decodeStartPosition, mediaStartTime);

            while (!cancellationToken.IsCancellationRequested &&
                   ffmpeg.av_read_frame(formatContext, packet) >= 0)
            {
                try
                {
                    if (packet->stream_index != videoStreamIndex)
                        continue;

                    int sendResult = ffmpeg.avcodec_send_packet(codecContext, packet);
                    if (sendResult < 0)
                    {
                        if (IsTransportStream(sourceFile))
                            continue;

                        ThrowIfError(sendResult, "send video packet");
                    }

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        int receiveResult = ffmpeg.avcodec_receive_frame(codecContext, frame);
                        if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
                            break;

                        ThrowIfError(receiveResult, "receive video frame");
                        if (singleFrameEmitted)
                        {
                            if (!IsCurrentRequest(requestSerial))
                                return;

                            AddDecodedFrameToCache(
                                sourceFile,
                                swsContext,
                                frame,
                                bgrFrame,
                                videoStream,
                                width,
                                height,
                                displayAspectRatio,
                                mediaStartTime);

                            cacheFramesRemaining--;
                            if (cacheFramesRemaining <= 0)
                                return;

                            continue;
                        }

                        if (HandleDecodedFrame(
                            swsContext,
                            frame,
                            bgrFrame,
                            videoStream,
                            width,
                            height,
                            displayAspectRatio,
                            mediaStartTime,
                            position,
                            singleFrame,
                            requestSerial,
                            clock,
                            cancellationToken,
                            ref singleFrameFallback))
                        {
                            if (!singleFrame)
                                return;

                            singleFrameEmitted = true;
                            cacheFramesRemaining = SingleFrameCacheCount;
                        }
                    }
                }
                finally
                {
                    ffmpeg.av_packet_unref(packet);
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                int drainResult = ffmpeg.avcodec_send_packet(codecContext, null);
                if (drainResult >= 0)
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        int receiveResult = ffmpeg.avcodec_receive_frame(codecContext, frame);
                        if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
                            break;

                        ThrowIfError(receiveResult, "receive video frame");
                        if (singleFrameEmitted)
                        {
                            if (!IsCurrentRequest(requestSerial))
                                return;

                            AddDecodedFrameToCache(
                                sourceFile,
                                swsContext,
                                frame,
                                bgrFrame,
                                videoStream,
                                width,
                                height,
                                displayAspectRatio,
                                mediaStartTime);

                            cacheFramesRemaining--;
                            if (cacheFramesRemaining <= 0)
                                return;

                            continue;
                        }

                        if (HandleDecodedFrame(
                            swsContext,
                            frame,
                            bgrFrame,
                            videoStream,
                            width,
                            height,
                            displayAspectRatio,
                            mediaStartTime,
                            position,
                            singleFrame,
                            requestSerial,
                            clock,
                            cancellationToken,
                            ref singleFrameFallback))
                        {
                            if (!singleFrame)
                                return;

                            singleFrameEmitted = true;
                            cacheFramesRemaining = SingleFrameCacheCount;
                        }
                    }
                }
            }

            if (singleFrameFallback is not null)
            {
                if (IsCurrentRequest(requestSerial))
                    FrameAvailable?.Invoke(this, singleFrameFallback);
                else
                    singleFrameFallback.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DecoderLog?.Invoke(this, "[Video] " + ex.GetType().Name + ": " + ex.Message);
            Debug.WriteLine("[Video] " + ex);
        }
        finally
        {
            if (packet is not null)
                ffmpeg.av_packet_free(&packet);
            if (frame is not null)
                ffmpeg.av_frame_free(&frame);
            if (bgrFrame is not null)
                ffmpeg.av_frame_free(&bgrFrame);
            if (swsContext is not null)
                ffmpeg.sws_freeContext(swsContext);
            if (codecContext is not null)
                ffmpeg.avcodec_free_context(&codecContext);
            if (formatContext is not null)
                ffmpeg.avformat_close_input(&formatContext);

            IsPlaying = false;
        }
    }

    private static int FindVideoStream(AVFormatContext* formatContext)
    {
        for (int i = 0; i < formatContext->nb_streams; i++)
        {
            if (formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                return i;
        }

        return -1;
    }

    private static void Seek(
        AVFormatContext* formatContext,
        AVCodecContext* codecContext,
        int videoStreamIndex,
        AVStream* videoStream,
        TimeSpan position,
        TimeSpan mediaStartTime)
    {
        TimeSpan seekTime = position + mediaStartTime;
        long timestamp = (long)(seekTime.TotalSeconds / AvRationalToDouble(videoStream->time_base));
        int result = ffmpeg.av_seek_frame(
            formatContext,
            videoStreamIndex,
            timestamp,
            ffmpeg.AVSEEK_FLAG_BACKWARD);

        if (result >= 0)
            ffmpeg.avcodec_flush_buffers(codecContext);
    }

    private static TimeSpan GetFrameTimestamp(AVFrame* frame, AVStream* stream, TimeSpan mediaStartTime)
    {
        long timestamp = frame->best_effort_timestamp;
        if (timestamp == ffmpeg.AV_NOPTS_VALUE)
            timestamp = frame->pts;

        if (timestamp == ffmpeg.AV_NOPTS_VALUE)
            return TimeSpan.Zero;

        TimeSpan rawTimestamp = TimeSpan.FromSeconds(timestamp * AvRationalToDouble(stream->time_base));
        TimeSpan normalizedTimestamp = rawTimestamp - mediaStartTime;
        return normalizedTimestamp < TimeSpan.Zero ? TimeSpan.Zero : normalizedTimestamp;
    }

    private bool ShouldDisplayFrame(
        TimeSpan frameTimestamp,
        Func<TimeSpan>? clock,
        CancellationToken cancellationToken)
    {
        if (clock is null || frameTimestamp == TimeSpan.Zero)
            return true;

        TimeSpan playbackTime = clock();
        TimeSpan delta = frameTimestamp - playbackTime;

        if (delta < TimeSpan.FromMilliseconds(-160))
            return false;

        while (delta > TimeSpan.FromMilliseconds(2))
        {
            TimeSpan wait = delta > TimeSpan.FromMilliseconds(80)
                ? TimeSpan.FromMilliseconds(10)
                : delta;
            Task.Delay(wait, cancellationToken).Wait(cancellationToken);
            playbackTime = clock();
            delta = frameTimestamp - playbackTime;
        }

        return true;
    }

    private bool HandleDecodedFrame(
        SwsContext* swsContext,
        AVFrame* frame,
        AVFrame* bgrFrame,
        AVStream* videoStream,
        int width,
        int height,
        double displayAspectRatio,
        TimeSpan mediaStartTime,
        TimeSpan position,
        bool singleFrame,
        long requestSerial,
        Func<TimeSpan>? clock,
        CancellationToken cancellationToken,
        ref VideoFrameEventArgs? singleFrameFallback)
    {
        if (!IsCurrentRequest(requestSerial))
            return true;

        TimeSpan timestamp = GetFrameTimestamp(frame, videoStream, mediaStartTime);
        if (singleFrame)
        {
            if (timestamp < position - SingleFrameFallbackWindow)
                return false;

            if (timestamp <= position + SingleFrameSearchWindow)
            {
                VideoFrameEventArgs candidateFrame = CreateFrameEventArgs(
                    swsContext,
                    frame,
                    bgrFrame,
                    width,
                    height,
                    displayAspectRatio,
                    timestamp,
                    usePooledBuffer: false);

                if (singleFrameFallback is null ||
                    Math.Abs((candidateFrame.Timestamp - position).Ticks) <
                    Math.Abs((singleFrameFallback.Timestamp - position).Ticks))
                {
                    singleFrameFallback?.Dispose();
                    singleFrameFallback = candidateFrame;
                }
                else
                {
                    candidateFrame.Dispose();
                }

                return false;
            }

            if (!IsCurrentRequest(requestSerial))
                return true;

            if (singleFrameFallback is not null)
            {
                FrameAvailable?.Invoke(this, singleFrameFallback);
                singleFrameFallback = null;
                return true;
            }
        }
        else if (timestamp < position - PlaybackStartTolerance)
        {
            return false;
        }

        if (!singleFrame && !ShouldDisplayFrame(timestamp, clock, cancellationToken))
            return false;

        if (!IsCurrentRequest(requestSerial))
            return true;

        VideoFrameEventArgs frameEvent;
        if (singleFrameFallback is not null && singleFrameFallback.Timestamp == timestamp)
        {
            frameEvent = singleFrameFallback;
            singleFrameFallback = null;
        }
        else
        {
            frameEvent = CreateFrameEventArgs(
                swsContext,
                frame,
                bgrFrame,
                width,
                height,
                displayAspectRatio,
                timestamp,
                usePooledBuffer: !singleFrame);
        }

        FrameAvailable?.Invoke(this, frameEvent);

        return singleFrame;
    }

    private bool IsCurrentRequest(long requestSerial)
    {
        return Interlocked.Read(ref _requestSerial) == requestSerial;
    }

    private bool TryShowCachedFrame(string sourceFile, TimeSpan position)
    {
        if (!_singleFrameCache.TryGet(sourceFile, position, CachedFrameTolerance, out VideoFrameEventArgs cachedFrame))
            return false;

        Interlocked.Increment(ref _requestSerial);
        FrameAvailable?.Invoke(this, cachedFrame);
        return true;
    }

    private void ClearFrameCache(string sourceFile)
    {
        _singleFrameCache.PrepareForSource(sourceFile);
    }

    private void ClearAllFrameCache()
    {
        _singleFrameCache.Clear();
    }

    private void AddDecodedFrameToCache(
        string sourceFile,
        SwsContext* swsContext,
        AVFrame* frame,
        AVFrame* bgrFrame,
        AVStream* videoStream,
        int width,
        int height,
        double displayAspectRatio,
        TimeSpan mediaStartTime)
    {
        VideoFrameEventArgs frameEvent = CreateFrameEventArgs(
            swsContext,
            frame,
            bgrFrame,
            width,
            height,
            displayAspectRatio,
            GetFrameTimestamp(frame, videoStream, mediaStartTime),
            usePooledBuffer: false);

        _singleFrameCache.Add(sourceFile, frameEvent, CachedFrameTolerance);
    }

    private VideoFrameEventArgs CreateFrameEventArgs(
        SwsContext* swsContext,
        AVFrame* frame,
        AVFrame* bgrFrame,
        int width,
        int height,
        double displayAspectRatio,
        TimeSpan timestamp,
        bool usePooledBuffer)
    {
        ScaleFrame(swsContext, frame, bgrFrame, height);
        byte[] buffer = CopyBgrFrame(bgrFrame, width, height, usePooledBuffer, out int bufferLength);
        return new VideoFrameEventArgs(
            buffer,
            width,
            height,
            timestamp,
            displayAspectRatio,
            bufferLength,
            usePooledBuffer);
    }

    private static byte[] CopyBgrFrame(AVFrame* frame, int width, int height, bool usePooledBuffer, out int bufferLength)
    {
        int sourceStride = frame->linesize[0];
        int destinationStride = width * 3;
        bufferLength = destinationStride * height;
        byte[] buffer = usePooledBuffer
            ? VideoFrameBufferPool.Rent(bufferLength)
            : new byte[bufferLength];

        for (int y = 0; y < height; y++)
        {
            IntPtr source = (IntPtr)(frame->data[0] + y * sourceStride);
            Marshal.Copy(source, buffer, y * destinationStride, destinationStride);
        }

        return buffer;
    }

    private static void ScaleFrame(SwsContext* swsContext, AVFrame* sourceFrame, AVFrame* destinationFrame, int height)
    {
        byte*[] sourceData =
        [
            sourceFrame->data[0],
            sourceFrame->data[1],
            sourceFrame->data[2],
            sourceFrame->data[3]
        ];
        int[] sourceLinesize =
        [
            sourceFrame->linesize[0],
            sourceFrame->linesize[1],
            sourceFrame->linesize[2],
            sourceFrame->linesize[3]
        ];
        byte*[] destinationData =
        [
            destinationFrame->data[0],
            destinationFrame->data[1],
            destinationFrame->data[2],
            destinationFrame->data[3]
        ];
        int[] destinationLinesize =
        [
            destinationFrame->linesize[0],
            destinationFrame->linesize[1],
            destinationFrame->linesize[2],
            destinationFrame->linesize[3]
        ];

        int scaledHeight = ffmpeg.sws_scale(
            swsContext,
            sourceData,
            sourceLinesize,
            0,
            height,
            destinationData,
            destinationLinesize);

        if (scaledHeight <= 0)
            throw new InvalidOperationException("FFmpeg failed to scale video frame.");
    }

    private static double AvRationalToDouble(AVRational rational)
    {
        return rational.den == 0 ? 0 : rational.num / (double)rational.den;
    }

    private static double GetDisplayAspectRatio(
        AVCodecContext* codecContext,
        AVStream* videoStream,
        int width,
        int height)
    {
        if (width <= 0 || height <= 0)
            return 1;

        AVRational sampleAspectRatio = codecContext->sample_aspect_ratio;
        if (sampleAspectRatio.num <= 0 || sampleAspectRatio.den <= 0)
            sampleAspectRatio = videoStream->sample_aspect_ratio;

        double sar = AvRationalToDouble(sampleAspectRatio);
        if (sar <= 0)
            sar = 1;

        double dar = width * sar / height;
        double broadcastDar = GetBroadcastDisplayAspectRatio(width, height, sampleAspectRatio);
        return broadcastDar > 0 ? broadcastDar : dar;
    }

    private static double GetBroadcastDisplayAspectRatio(int width, int height, AVRational sampleAspectRatio)
    {
        if (sampleAspectRatio.num <= 0 || sampleAspectRatio.den <= 0)
            return 0;

        bool isPalStandardDefinition = height == 576 && (width == 720 || width == 704);
        bool isNtscStandardDefinition = height == 480 && (width == 720 || width == 704);
        if (!isPalStandardDefinition && !isNtscStandardDefinition)
            return 0;

        if (IsRationalClose(sampleAspectRatio, 16, 11) ||
            IsRationalClose(sampleAspectRatio, 64, 45) ||
            IsRationalClose(sampleAspectRatio, 32, 27) ||
            IsRationalClose(sampleAspectRatio, 40, 33))
        {
            return 16 / 9d;
        }

        if (IsRationalClose(sampleAspectRatio, 12, 11) ||
            IsRationalClose(sampleAspectRatio, 10, 11) ||
            IsRationalClose(sampleAspectRatio, 8, 9))
        {
            return 4 / 3d;
        }

        return 0;
    }

    private static bool IsRationalClose(AVRational rational, int numerator, int denominator)
    {
        double actual = AvRationalToDouble(rational);
        double expected = numerator / (double)denominator;
        return Math.Abs(actual - expected) < 0.01;
    }

    private static TimeSpan GetFormatStartTime(AVFormatContext* formatContext)
    {
        return formatContext->start_time == ffmpeg.AV_NOPTS_VALUE
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(formatContext->start_time / (double)ffmpeg.AV_TIME_BASE);
    }

    private static TimeSpan GetDecodeStartPosition(string sourceFile, TimeSpan position)
    {
        if (!IsTransportStream(sourceFile))
            return position;

        return position > TransportStreamVideoSeekPreroll
            ? position - TransportStreamVideoSeekPreroll
            : TimeSpan.Zero;
    }

    private static bool IsTransportStream(string sourceFile)
    {
        return string.Equals(Path.GetExtension(sourceFile), ".ts", StringComparison.OrdinalIgnoreCase);
    }

    private static void ThrowIfError(int error, string operation)
    {
        if (error >= 0)
            return;

        Span<byte> buffer = stackalloc byte[1024];
        fixed (byte* bufferPtr = buffer)
        {
            ffmpeg.av_strerror(error, bufferPtr, (ulong)buffer.Length);
            string message = Marshal.PtrToStringAnsi((IntPtr)bufferPtr) ?? "Unknown FFmpeg error";
            throw new InvalidOperationException($"FFmpeg failed to {operation}: {message}");
        }
    }

    public void PrepareForFile(string sourceFile)
    {
        _frameStepper.PrepareForFile(sourceFile);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _frameStepper.Dispose(); // ← ΝΕΟ
        _disposed = true;
    }
}
