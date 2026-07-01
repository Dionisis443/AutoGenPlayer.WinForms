using AutoGen_Player.Core;
using FFmpeg.AutoGen;
using NAudio.Wave;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AutoGen_Player.Video;

internal sealed unsafe class AutoGenSingleStreamAvPlayback : IDisposable
{
    private const int OutputSampleRate = 48000;
    private const int OutputChannels = 2;
    private const int OutputBytesPerSample = 2;
    private const int OutputBytesPerSecond = OutputSampleRate * OutputChannels * OutputBytesPerSample;
    private const int MinResampleRate = 1000;
    private const int MaxResampleRate = 480000;
    private const int SwsBilinear = 2;
    private static readonly TimeSpan AudioStartPrebuffer = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan AudioLowWatermark = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan AudioHighWatermark = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan ImmediateFirstFrameTolerance = TimeSpan.FromMilliseconds(70);
    private static readonly TimeSpan TransportStreamSeekPreroll = TimeSpan.FromSeconds(1);
    private readonly object _syncRoot = new();
    private CancellationTokenSource? _cancellation;
    private Task? _decodeTask;
    private Task? _presentTask;
    private BlockingCollection<QueuedVideoFrame>? _videoFrameQueue;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _waveBuffer;
    private long _audioBytesQueued;
    private long _startTimestamp;
    private long _traceStartTimestamp;
    private TimeSpan _startPosition;
    private TimeSpan? _videoTimestampOffset;
    private volatile bool _audioClockStarted;
    private bool _traceFirstAudioDecoded;
    private bool _traceAudioStarted;
    private bool _traceFirstVideoDecoded;
    private bool _traceFirstVideoPresented;
    private bool _traceDroppedFrame;
    private bool _disposed;

    private sealed record QueuedVideoFrame(VideoFrameEventArgs Frame, TimeSpan PresentationTimestamp);

    public bool IsPlaying { get; private set; }
    public TimeSpan OutputLatencyEstimate { get; set; } = TimeSpan.FromMilliseconds(40);
    public double PlaybackSpeed { get; set; } = 1.0;
    public TimeSpan PlaybackClock
    {
        get
        {
            if (_waveBuffer is not null && _audioClockStarted)
            {
                TimeSpan queued = BytesToDuration(Interlocked.Read(ref _audioBytesQueued));
                TimeSpan buffered = _waveBuffer.BufferedDuration;
                TimeSpan played = queued - buffered - OutputLatencyEstimate;
                if (played < TimeSpan.Zero)
                    played = TimeSpan.Zero;

                return _startPosition + TimeSpan.FromTicks((long)(played.Ticks * Math.Max(0.01, PlaybackSpeed)));
            }

            long elapsedTicks = Stopwatch.GetTimestamp() - _startTimestamp;
            double elapsedSeconds = elapsedTicks / (double)Stopwatch.Frequency;
            return _startPosition + TimeSpan.FromSeconds(elapsedSeconds * Math.Max(0.01, PlaybackSpeed));
        }
    }

    public event EventHandler<VideoFrameEventArgs>? FrameAvailable;
    public event EventHandler<string>? Log;

    public void Start(string sourceFile, TimeSpan position, MediaStreamInfo? audioStream)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_syncRoot)
        {
            StopLocked(waitForDecoder: true);
            _startPosition = position < TimeSpan.Zero ? TimeSpan.Zero : position;
            _startTimestamp = Stopwatch.GetTimestamp();
            _traceStartTimestamp = _startTimestamp;
            _traceFirstAudioDecoded = false;
            _traceAudioStarted = false;
            _traceFirstVideoDecoded = false;
            _traceFirstVideoPresented = false;
            _traceDroppedFrame = false;
            _videoTimestampOffset = null;
            _audioClockStarted = false;
            Interlocked.Exchange(ref _audioBytesQueued, 0);
            Trace($"start pos={_startPosition:hh\\:mm\\:ss\\.fff} audioStream={audioStream?.StreamIndex.ToString() ?? "none"}");

            _waveBuffer = new BufferedWaveProvider(new WaveFormat(OutputSampleRate, 16, OutputChannels))
            {
                BufferDuration = TimeSpan.FromSeconds(3),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };

            _waveOut = new WaveOutEvent
            {
                DesiredLatency = 120,
                NumberOfBuffers = 4
            };
            _waveOut.Init(_waveBuffer);

            _cancellation = new CancellationTokenSource();
            _videoFrameQueue = new BlockingCollection<QueuedVideoFrame>(boundedCapacity: 6);
            _presentTask = Task.Run(
                () => PresentVideoFrames(_videoFrameQueue, _cancellation.Token),
                _cancellation.Token);
            _decodeTask = Task.Run(
                () => DecodeLoop(sourceFile, _startPosition, audioStream?.StreamIndex, _cancellation.Token),
                _cancellation.Token);
            IsPlaying = true;
        }
    }

    public void Stop()
    {
        lock (_syncRoot)
        {
            StopLocked(waitForDecoder: false);
            IsPlaying = false;
        }
    }

    private void StopLocked(bool waitForDecoder)
    {
        CancellationTokenSource? cancellation = _cancellation;
        Task? decodeTask = _decodeTask;
        Task? presentTask = _presentTask;
        BlockingCollection<QueuedVideoFrame>? videoFrameQueue = _videoFrameQueue;
        _cancellation = null;
        _decodeTask = null;
        _presentTask = null;
        _videoFrameQueue = null;

        try
        {
            cancellation?.Cancel();
            videoFrameQueue?.CompleteAdding();
        }
        catch
        {
        }

        if (waitForDecoder && decodeTask is not null && !decodeTask.IsCompleted)
        {
            try
            {
                decodeTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }

        if (presentTask is not null && !presentTask.IsCompleted)
        {
            try
            {
                presentTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }
        }

        DrainVideoQueue(videoFrameQueue);

        try
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
        }
        catch
        {
        }

        _waveOut = null;
        _waveBuffer = null;
        _videoTimestampOffset = null;
        _audioClockStarted = false;
        cancellation?.Dispose();
    }

    private void DecodeLoop(string sourceFile, TimeSpan position, int? preferredAudioStreamIndex, CancellationToken cancellationToken)
    {
        AVFormatContext* formatContext = null;
        AVCodecContext* videoCodecContext = null;
        AVCodecContext* audioCodecContext = null;
        SwsContext* swsContext = null;
        SwrContext* swrContext = null;
        AVFrame* videoFrame = null;
        AVFrame* bgrFrame = null;
        AVFrame* audioFrame = null;
        AVPacket* packet = null;
        byte* audioOutputBuffer = null;

        try
        {
            ThrowIfError(ffmpeg.avformat_open_input(&formatContext, sourceFile, null, null), "open input");
            ThrowIfError(ffmpeg.avformat_find_stream_info(formatContext, null), "find stream info");

            TimeSpan mediaStartTime = GetFormatStartTime(formatContext);
            int videoStreamIndex = FindStream(formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, null);
            int audioStreamIndex = FindStream(formatContext, AVMediaType.AVMEDIA_TYPE_AUDIO, preferredAudioStreamIndex);
            if (videoStreamIndex < 0)
                throw new InvalidOperationException("No video stream was found.");

            AVStream* videoStream = formatContext->streams[videoStreamIndex];
            OpenCodec(videoStream, &videoCodecContext);

            AVStream* audioStream = null;
            if (audioStreamIndex >= 0)
            {
                audioStream = formatContext->streams[audioStreamIndex];
                OpenCodec(audioStream, &audioCodecContext);
                int audioResampleRate = GetAudioResampleRate(PlaybackSpeed);
                swrContext = CreateAudioResampler(audioCodecContext, audioResampleRate);
                audioOutputBuffer = (byte*)ffmpeg.av_malloc(MaxResampleRate * OutputChannels * OutputBytesPerSample);
            }

            int width = videoCodecContext->width;
            int height = videoCodecContext->height;
            double displayAspectRatio = GetDisplayAspectRatio(videoCodecContext, videoStream, width, height);
            swsContext = ffmpeg.sws_getContext(
                width,
                height,
                videoCodecContext->pix_fmt,
                width,
                height,
                AVPixelFormat.AV_PIX_FMT_BGR24,
                SwsBilinear,
                null,
                null,
                null);

            if (swsContext is null)
                throw new InvalidOperationException("FFmpeg failed to create video scaler.");

            videoFrame = ffmpeg.av_frame_alloc();
            bgrFrame = ffmpeg.av_frame_alloc();
            audioFrame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            if (videoFrame is null || bgrFrame is null || audioFrame is null || packet is null)
                throw new InvalidOperationException("FFmpeg failed to allocate decoder frames.");

            bgrFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGR24;
            bgrFrame->width = width;
            bgrFrame->height = height;
            ThrowIfError(ffmpeg.av_frame_get_buffer(bgrFrame, 1), "allocate BGR frame");

            Seek(formatContext, videoCodecContext, audioCodecContext, videoStreamIndex, videoStream, sourceFile, position, mediaStartTime);

            bool audioStarted = audioStreamIndex < 0;
            bool firstVideoFrameShown = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                int readResult = ffmpeg.av_read_frame(formatContext, packet);
                if (readResult == ffmpeg.AVERROR_EOF)
                    break;

                ThrowIfError(readResult, "read packet");

                if (packet->stream_index == audioStreamIndex && audioCodecContext is not null && swrContext is not null)
                {
                    DecodeAudioPacket(audioCodecContext, swrContext, audioFrame, packet, audioStream, mediaStartTime, position, audioOutputBuffer, ref audioStarted, cancellationToken);
                }
                else if (packet->stream_index == videoStreamIndex)
                {
                    DecodeVideoPacket(
                        videoCodecContext,
                        swsContext,
                        videoFrame,
                        bgrFrame,
                        packet,
                        videoStream,
                        mediaStartTime,
                        position,
                        width,
                        height,
                        displayAspectRatio,
                        audioStarted,
                        ref firstVideoFrameShown,
                        cancellationToken);
                }

                ffmpeg.av_packet_unref(packet);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, "[AutoGen A/V] " + ex.Message);
        }
        finally
        {
            if (packet is not null)
                ffmpeg.av_packet_free(&packet);
            if (videoFrame is not null)
                ffmpeg.av_frame_free(&videoFrame);
            if (bgrFrame is not null)
                ffmpeg.av_frame_free(&bgrFrame);
            if (audioFrame is not null)
                ffmpeg.av_frame_free(&audioFrame);
            if (swsContext is not null)
                ffmpeg.sws_freeContext(swsContext);
            if (swrContext is not null)
                ffmpeg.swr_free(&swrContext);
            if (audioOutputBuffer is not null)
                ffmpeg.av_free(audioOutputBuffer);
            if (videoCodecContext is not null)
                ffmpeg.avcodec_free_context(&videoCodecContext);
            if (audioCodecContext is not null)
                ffmpeg.avcodec_free_context(&audioCodecContext);
            if (formatContext is not null)
                ffmpeg.avformat_close_input(&formatContext);
        }
    }

    private void DecodeAudioPacket(
        AVCodecContext* codecContext,
        SwrContext* swrContext,
        AVFrame* frame,
        AVPacket* packet,
        AVStream* stream,
        TimeSpan mediaStartTime,
        TimeSpan startPosition,
        byte* outputBuffer,
        ref bool audioStarted,
        CancellationToken cancellationToken)
    {
        int sendResult = ffmpeg.avcodec_send_packet(codecContext, packet);
        if (sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            return;

        if (sendResult < 0)
            return;

        while (!cancellationToken.IsCancellationRequested)
        {
            int receiveResult = ffmpeg.avcodec_receive_frame(codecContext, frame);
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
                break;

            if (receiveResult < 0)
                break;

            TimeSpan timestamp = GetFrameTimestamp(frame, stream, mediaStartTime);
            if (timestamp < startPosition - TimeSpan.FromMilliseconds(100))
                continue;

            if (!_traceFirstAudioDecoded)
            {
                _traceFirstAudioDecoded = true;
                Trace($"first audio decoded pts={timestamp:hh\\:mm\\:ss\\.fff} start={startPosition:hh\\:mm\\:ss\\.fff}");
            }

            int outputSamples = (int)ffmpeg.av_rescale_rnd(
                ffmpeg.swr_get_delay(swrContext, codecContext->sample_rate) + frame->nb_samples,
                GetAudioResampleRate(PlaybackSpeed),
                codecContext->sample_rate,
                AVRounding.AV_ROUND_UP);

            if (outputSamples <= 0 || outputSamples > MaxResampleRate)
                continue;

            byte* destination = outputBuffer;
            int converted = ffmpeg.swr_convert(
                swrContext,
                &destination,
                outputSamples,
                frame->extended_data,
                frame->nb_samples);

            if (converted <= 0)
                continue;

            int byteCount = converted * OutputChannels * OutputBytesPerSample;
            byte[] managedBytes = new byte[byteCount];
            Marshal.Copy((IntPtr)outputBuffer, managedBytes, 0, byteCount);

            BufferedWaveProvider? buffer = _waveBuffer;
            if (buffer is null)
                return;

            while (!cancellationToken.IsCancellationRequested &&
                   buffer.BufferedBytes + byteCount > buffer.BufferLength)
            {
                Thread.Sleep(2);
            }

            buffer.AddSamples(managedBytes, 0, byteCount);
            Interlocked.Add(ref _audioBytesQueued, byteCount);

            if (!audioStarted && buffer.BufferedDuration >= AudioStartPrebuffer)
            {
                _startTimestamp = Stopwatch.GetTimestamp();
                _waveOut?.Play();
                _audioClockStarted = true;
                audioStarted = true;
                if (!_traceAudioStarted)
                {
                    _traceAudioStarted = true;
                    Trace($"audio output started buffered={buffer.BufferedDuration.TotalMilliseconds:0}ms queued={BytesToDuration(Interlocked.Read(ref _audioBytesQueued)).TotalMilliseconds:0}ms");
                }
            }
        }
    }

    private void DecodeVideoPacket(
        AVCodecContext* codecContext,
        SwsContext* swsContext,
        AVFrame* frame,
        AVFrame* bgrFrame,
        AVPacket* packet,
        AVStream* stream,
        TimeSpan mediaStartTime,
        TimeSpan startPosition,
        int width,
        int height,
        double displayAspectRatio,
        bool audioStarted,
        ref bool firstVideoFrameShown,
        CancellationToken cancellationToken)
    {
        int sendResult = ffmpeg.avcodec_send_packet(codecContext, packet);
        if (sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            return;

        if (sendResult < 0)
            return;

        while (!cancellationToken.IsCancellationRequested)
        {
            int receiveResult = ffmpeg.avcodec_receive_frame(codecContext, frame);
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
                break;

            if (receiveResult < 0)
                break;

            TimeSpan timestamp = GetFrameTimestamp(frame, stream, mediaStartTime);
            if (timestamp < startPosition - TimeSpan.FromMilliseconds(80))
                continue;

            if (!firstVideoFrameShown)
            {
                firstVideoFrameShown = true;
                if (!_traceFirstVideoDecoded)
                {
                    _traceFirstVideoDecoded = true;
                    Trace($"first video decoded pts={timestamp:hh\\:mm\\:ss\\.fff} start={startPosition:hh\\:mm\\:ss\\.fff} diff={(timestamp - startPosition).TotalMilliseconds:0}ms tolerance={ImmediateFirstFrameTolerance.TotalMilliseconds:0}ms");
                }

                if (timestamp <= startPosition + ImmediateFirstFrameTolerance)
                {
                    Trace("first video immediate present");
                    PresentFrame(swsContext, frame, bgrFrame, width, height, displayAspectRatio, timestamp);
                    continue;
                }

                Trace("first video queued");
            }

            TimeSpan playbackTimestamp = NormalizeVideoTimestamp(timestamp, startPosition);

            EnqueueVideoFrame(
                new QueuedVideoFrame(
                    CreateVideoFrame(swsContext, frame, bgrFrame, width, height, displayAspectRatio, timestamp),
                    playbackTimestamp),
                cancellationToken);
        }
    }

    private TimeSpan NormalizeVideoTimestamp(TimeSpan timestamp, TimeSpan startPosition)
    {
        TimeSpan offset = _videoTimestampOffset ?? TimeSpan.Zero;
        TimeSpan normalized = timestamp - offset;
        return normalized < startPosition ? startPosition : normalized;
    }

    private void PresentVideoFrames(BlockingCollection<QueuedVideoFrame> queue, CancellationToken cancellationToken)
    {
        try
        {
            foreach (QueuedVideoFrame queuedFrame in queue.GetConsumingEnumerable(cancellationToken))
            {
                TimeSpan delta = queuedFrame.PresentationTimestamp - PlaybackClock;
                if (delta < TimeSpan.FromMilliseconds(-160))
                {
                    queuedFrame.Frame.Dispose();
                    continue;
                }

                while (delta > TimeSpan.FromMilliseconds(2) && !cancellationToken.IsCancellationRequested)
                {
                    Thread.Sleep(delta > TimeSpan.FromMilliseconds(20) ? 4 : 1);
                    delta = queuedFrame.PresentationTimestamp - PlaybackClock;
                }

                if (!_traceFirstVideoPresented)
                {
                    _traceFirstVideoPresented = true;
                    Trace($"first video presented mediaPts={queuedFrame.Frame.Timestamp:hh\\:mm\\:ss\\.fff} presentPts={queuedFrame.PresentationTimestamp:hh\\:mm\\:ss\\.fff} clock={PlaybackClock:hh\\:mm\\:ss\\.fff} waitLeft={delta.TotalMilliseconds:0}ms queue={queue.Count}");
                }

                FrameAvailable?.Invoke(this, queuedFrame.Frame);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void EnqueueVideoFrame(QueuedVideoFrame frame, CancellationToken cancellationToken)
    {
        BlockingCollection<QueuedVideoFrame>? queue = _videoFrameQueue;
        if (queue is null || queue.IsAddingCompleted)
        {
            frame.Frame.Dispose();
            return;
        }

        try
        {
            while (!queue.TryAdd(frame, millisecondsTimeout: 2, cancellationToken))
            {
                if (IsAudioBufferLow())
                    DropOldestQueuedFrame(queue);
            }
        }
        catch
        {
            frame.Frame.Dispose();
        }
    }

    private void DropOldestQueuedFrame(BlockingCollection<QueuedVideoFrame> queue)
    {
        if (queue.TryTake(out QueuedVideoFrame? dropped))
        {
            if (!_traceDroppedFrame)
            {
                _traceDroppedFrame = true;
                Trace($"dropping queued video frames queue={queue.Count} audioBuffered={_waveBuffer?.BufferedDuration.TotalMilliseconds:0}ms");
            }

            dropped.Frame.Dispose();
        }
    }

    private bool IsAudioBufferLow()
    {
        BufferedWaveProvider? buffer = _waveBuffer;
        return buffer is not null && buffer.BufferedDuration < AudioLowWatermark;
    }

    private static void DrainVideoQueue(BlockingCollection<QueuedVideoFrame>? queue)
    {
        if (queue is null)
            return;

        try
        {
            while (queue.TryTake(out QueuedVideoFrame? frame))
                frame.Frame.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        queue.Dispose();
    }

    private void PresentFrame(
        SwsContext* swsContext,
        AVFrame* frame,
        AVFrame* bgrFrame,
        int width,
        int height,
        double displayAspectRatio,
        TimeSpan timestamp)
    {
        if (!_traceFirstVideoPresented)
        {
            _traceFirstVideoPresented = true;
            Trace($"first video presented immediate mediaPts={timestamp:hh\\:mm\\:ss\\.fff} clock={PlaybackClock:hh\\:mm\\:ss\\.fff}");
        }

        FrameAvailable?.Invoke(
            this,
            CreateVideoFrame(swsContext, frame, bgrFrame, width, height, displayAspectRatio, timestamp));
    }

    private static VideoFrameEventArgs CreateVideoFrame(
        SwsContext* swsContext,
        AVFrame* frame,
        AVFrame* bgrFrame,
        int width,
        int height,
        double displayAspectRatio,
        TimeSpan timestamp)
    {
        ScaleFrame(swsContext, frame, bgrFrame, height);
        int stride = bgrFrame->linesize[0];
        int destinationStride = width * 3;
        int bufferLength = destinationStride * height;
        byte[] buffer = VideoFrameBufferPool.Rent(bufferLength);

        for (int y = 0; y < height; y++)
        {
            IntPtr source = (IntPtr)(bgrFrame->data[0] + y * stride);
            Marshal.Copy(source, buffer, y * destinationStride, destinationStride);
        }

        return new VideoFrameEventArgs(buffer, width, height, timestamp, displayAspectRatio, bufferLength, returnBufferToPool: true);
    }

    private static void OpenCodec(AVStream* stream, AVCodecContext** codecContext)
    {
        AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec is null)
            throw new InvalidOperationException("FFmpeg could not find a decoder.");

        *codecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (*codecContext is null)
            throw new InvalidOperationException("FFmpeg could not allocate codec context.");

        ThrowIfError(ffmpeg.avcodec_parameters_to_context(*codecContext, stream->codecpar), "copy codec parameters");
        ThrowIfError(ffmpeg.avcodec_open2(*codecContext, codec, null), "open decoder");
    }

    private static SwrContext* CreateAudioResampler(AVCodecContext* codecContext, int outputSampleRate)
    {
        if (codecContext->ch_layout.nb_channels == 0)
            ffmpeg.av_channel_layout_default(&codecContext->ch_layout, codecContext->ch_layout.nb_channels <= 1 ? 1 : codecContext->ch_layout.nb_channels);

        AVChannelLayout outputLayout;
        ffmpeg.av_channel_layout_default(&outputLayout, OutputChannels);
        SwrContext* swrContext = null;
        ThrowIfError(
            ffmpeg.swr_alloc_set_opts2(
                &swrContext,
                &outputLayout,
                AVSampleFormat.AV_SAMPLE_FMT_S16,
                outputSampleRate,
                &codecContext->ch_layout,
                codecContext->sample_fmt,
                codecContext->sample_rate,
                0,
                null),
            "allocate audio resampler");
        ThrowIfError(ffmpeg.swr_init(swrContext), "initialize audio resampler");
        return swrContext;
    }

    private static int GetAudioResampleRate(double playbackSpeed)
    {
        double speed = Math.Max(0.01, playbackSpeed);
        return Math.Clamp((int)Math.Round(OutputSampleRate / speed), MinResampleRate, MaxResampleRate);
    }

    private static void Seek(
        AVFormatContext* formatContext,
        AVCodecContext* videoCodecContext,
        AVCodecContext* audioCodecContext,
        int videoStreamIndex,
        AVStream* videoStream,
        string sourceFile,
        TimeSpan position,
        TimeSpan mediaStartTime)
    {
        TimeSpan decodeStartPosition = IsTransportStream(sourceFile) && position > TransportStreamSeekPreroll
            ? position - TransportStreamSeekPreroll
            : TimeSpan.Zero;

        if (!IsTransportStream(sourceFile))
            decodeStartPosition = position;

        TimeSpan seekTime = decodeStartPosition + mediaStartTime;
        long timestamp = (long)(seekTime.TotalSeconds / AvRationalToDouble(videoStream->time_base));
        int result = ffmpeg.av_seek_frame(formatContext, videoStreamIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (result >= 0)
        {
            ffmpeg.avcodec_flush_buffers(videoCodecContext);
            if (audioCodecContext is not null)
                ffmpeg.avcodec_flush_buffers(audioCodecContext);
        }
    }

    private static bool IsTransportStream(string sourceFile)
    {
        return string.Equals(Path.GetExtension(sourceFile), ".ts", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindStream(AVFormatContext* formatContext, AVMediaType mediaType, int? preferredStreamIndex)
    {
        if (preferredStreamIndex.HasValue &&
            preferredStreamIndex.Value >= 0 &&
            preferredStreamIndex.Value < formatContext->nb_streams &&
            formatContext->streams[preferredStreamIndex.Value]->codecpar->codec_type == mediaType)
        {
            return preferredStreamIndex.Value;
        }

        for (int i = 0; i < formatContext->nb_streams; i++)
        {
            if (formatContext->streams[i]->codecpar->codec_type == mediaType)
                return i;
        }

        return -1;
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

        int scaledHeight = ffmpeg.sws_scale(swsContext, sourceData, sourceLinesize, 0, height, destinationData, destinationLinesize);
        if (scaledHeight <= 0)
            throw new InvalidOperationException("FFmpeg failed to scale video frame.");
    }

    private static double GetDisplayAspectRatio(AVCodecContext* codecContext, AVStream* videoStream, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return 1;

        AVRational sampleAspectRatio = codecContext->sample_aspect_ratio;
        if (sampleAspectRatio.num <= 0 || sampleAspectRatio.den <= 0)
            sampleAspectRatio = videoStream->sample_aspect_ratio;

        double sar = AvRationalToDouble(sampleAspectRatio);
        if (sar <= 0)
            sar = 1;

        return width * sar / height;
    }

    private static TimeSpan GetFormatStartTime(AVFormatContext* formatContext)
    {
        return formatContext->start_time == ffmpeg.AV_NOPTS_VALUE
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(formatContext->start_time / (double)ffmpeg.AV_TIME_BASE);
    }

    private static double AvRationalToDouble(AVRational rational)
    {
        return rational.den == 0 ? 0 : rational.num / (double)rational.den;
    }

    private static TimeSpan BytesToDuration(long bytes)
    {
        return TimeSpan.FromSeconds(bytes / (double)OutputBytesPerSecond);
    }

    private void Trace(string message)
    {
        long elapsedTicks = Stopwatch.GetTimestamp() - _traceStartTimestamp;
        double elapsedMilliseconds = elapsedTicks * 1000d / Stopwatch.Frequency;
        Log?.Invoke(this, $"[AutoGen A/V trace +{elapsedMilliseconds:0.0}ms] {message}");
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

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _disposed = true;
    }
}
