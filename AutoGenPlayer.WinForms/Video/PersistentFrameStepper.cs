using FFmpeg.AutoGen;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AutoGen_Player.Video;

/// <summary>
/// Keeps an FFmpeg format/codec context open across frame steps so that
/// seek + decode does not pay the cost of avformat_open_input /
/// avformat_find_stream_info on every button press.
///
/// Thread-safety: all public methods are called from the UI thread.
/// The decode work runs on a dedicated background thread; results are
/// delivered via <see cref="FrameAvailable"/>.
/// </summary>
internal sealed unsafe class PersistentFrameStepper : IDisposable
{
    // ------------------------------------------------------------------ constants
    private const int SwsBilinear = 2;

    /// How close a decoded frame's timestamp must be to the requested
    /// position before we accept it as "the" target frame.
    private static readonly TimeSpan FrameAcceptWindow = TimeSpan.FromMilliseconds(60);

    /// If we haven't found the exact frame within this window we still
    /// show the closest one we decoded (fallback).
    private static readonly TimeSpan FrameFallbackWindow = TimeSpan.FromMilliseconds(250);

    // ------------------------------------------------------------------ state
    private readonly object _lock = new();

    // The open decoder — null when no file is loaded.
    private OpenDecoder? _decoder;

    // The currently pending step request (latest wins).
    private StepRequest? _pendingRequest;
    private bool _workerRunning;
    private int _closeRequested;
    private bool _disposed;

    // ------------------------------------------------------------------ events
    public event EventHandler<VideoFrameEventArgs>? FrameAvailable;
    public event EventHandler<string>? Log;

    // ------------------------------------------------------------------ public API

    /// <summary>
    /// Ensure the decoder is open for <paramref name="sourceFile"/>.
    /// Call this when a file is opened so the first frame step has no
    /// extra latency. Safe to call multiple times with the same file.
    /// </summary>
    public void PrepareForFile(string sourceFile)
    {
        lock (_lock)
        {
            EnsureDecoder(sourceFile);
        }
    }

    /// <summary>
    /// Request the frame nearest to <paramref name="position"/>.
    /// Returns immediately; the frame is delivered via
    /// <see cref="FrameAvailable"/> on a thread-pool thread (marshal to
    /// UI with BeginInvoke as needed).
    /// </summary>
    public void RequestFrame(string sourceFile, TimeSpan position)
    {
        bool startWorker = false;
        lock (_lock)
        {
            _closeRequested = 0;
            // Latest request always wins — drop any pending one.
            _pendingRequest = new StepRequest(sourceFile, position);

            if (!_workerRunning)
            {
                _workerRunning = true;
                startWorker = true;
            }
        }

        if (startWorker)
            _ = Task.Run(WorkerLoop);
    }

    /// <summary>
    /// Close the decoder and release all FFmpeg resources.
    /// Call when the user closes the file or when playback starts
    /// (the continuous playback path opens its own decoder).
    /// </summary>
    public void Close()
    {
        OpenDecoder? decoderToDispose = null;
        lock (_lock)
        {
            _closeRequested = 1;
            _pendingRequest = null;
            if (_workerRunning)
                return;

            decoderToDispose = _decoder;
            _decoder = null;
        }

        decoderToDispose?.Dispose();
    }

    private void CloseAfterWorkerIfRequested()
    {
        OpenDecoder? decoderToDispose = null;
        lock (_lock)
        {
            if (_closeRequested == 0 || _workerRunning)
                return;

            decoderToDispose = _decoder;
            _decoder = null;
            _closeRequested = 0;
        }

        decoderToDispose?.Dispose();
    }

    private void DisposeDecoderNow()
    {
        OpenDecoder? decoderToDispose;
        lock (_lock)
        {
            decoderToDispose = _decoder;
            _decoder = null;
        }

        decoderToDispose?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Close();
    }

    // ------------------------------------------------------------------ worker

    private void WorkerLoop()
    {
        while (true)
        {
            StepRequest? request;
            lock (_lock)
            {
                request = _pendingRequest;
                _pendingRequest = null;

                if (request is null)
                {
                    _workerRunning = false;
                    CloseAfterWorkerIfRequested();
                    return;
                }
            }

            try
            {
                ProcessRequest(request);
            }
            catch (Exception ex)
            {
                Log?.Invoke(this, "[FrameStepper] " + ex.Message);
                Debug.WriteLine("[FrameStepper] " + ex);

                // If something went catastrophically wrong, close the
                // decoder so the next request rebuilds it from scratch.
                lock (_lock)
                {
                    _workerRunning = false;
                }
                DisposeDecoderNow();
                CloseAfterWorkerIfRequested();
                return;
            }
        }
    }

    private void ProcessRequest(StepRequest request)
    {
        OpenDecoder? decoder;
        lock (_lock)
        {
            if (_disposed)
                return;

            // Rebuild decoder if the source file changed.
            if (_decoder is not null &&
                !string.Equals(_decoder.SourceFile, request.SourceFile, StringComparison.OrdinalIgnoreCase))
            {
                _decoder.Dispose();
                _decoder = null;
            }

            decoder = EnsureDecoder(request.SourceFile);
        }

        // Check again — a newer request may have cancelled this one.
        if (HasNewerRequest())
            return;

        VideoFrameEventArgs? frame = decoder.DecodeFrameAt(request.Position, FrameAcceptWindow, FrameFallbackWindow, HasNewerRequest);

        if (frame is null || HasNewerRequest())
        {
            frame?.Dispose();
            return;
        }

        FrameAvailable?.Invoke(this, frame);
    }

    private OpenDecoder EnsureDecoder(string sourceFile)
    {
        // Must be called under _lock.
        if (_decoder is null)
            _decoder = OpenDecoder.Open(sourceFile);

        return _decoder;
    }

    private bool HasNewerRequest()
    {
        lock (_lock)
            return _pendingRequest is not null || _disposed;
    }

    // ------------------------------------------------------------------ nested types

    private sealed record StepRequest(string SourceFile, TimeSpan Position);

    /// <summary>
    /// Wraps the raw FFmpeg handles for one open file.
    /// Not thread-safe — the caller (WorkerLoop) serialises access.
    /// </summary>
    private sealed unsafe class OpenDecoder : IDisposable
    {
        private const int SwsBilinear = 2;

        private AVFormatContext* _formatContext;
        private AVCodecContext* _codecContext;
        private AVFrame* _frame;
        private AVFrame* _bgrFrame;
        private AVPacket* _packet;
        private SwsContext* _swsContext;
        private readonly int _videoStreamIndex;
        private readonly AVStream* _videoStream;
        private readonly int _width;
        private readonly int _height;
        private readonly double _displayAspectRatio;
        private readonly TimeSpan _mediaStartTime;
        private bool _disposed;

        public string SourceFile { get; }

        private OpenDecoder(
            string sourceFile,
            AVFormatContext* formatContext,
            AVCodecContext* codecContext,
            AVFrame* frame,
            AVFrame* bgrFrame,
            AVPacket* packet,
            SwsContext* swsContext,
            int videoStreamIndex,
            AVStream* videoStream,
            int width,
            int height,
            double displayAspectRatio,
            TimeSpan mediaStartTime)
        {
            SourceFile = sourceFile;
            _formatContext = formatContext;
            _codecContext = codecContext;
            _frame = frame;
            _bgrFrame = bgrFrame;
            _packet = packet;
            _swsContext = swsContext;
            _videoStreamIndex = videoStreamIndex;
            _videoStream = videoStream;
            _width = width;
            _height = height;
            _displayAspectRatio = displayAspectRatio;
            _mediaStartTime = mediaStartTime;
        }

        public static OpenDecoder Open(string sourceFile)
        {
            AVFormatContext* formatContext = null;
            AVCodecContext* codecContext = null;
            AVFrame* frame = null;
            AVFrame* bgrFrame = null;
            AVPacket* packet = null;
            SwsContext* swsContext = null;

            try
            {
                ThrowIfError(ffmpeg.avformat_open_input(&formatContext, sourceFile, null, null), "open input");
                ThrowIfError(ffmpeg.avformat_find_stream_info(formatContext, null), "find stream info");

                TimeSpan mediaStartTime = GetFormatStartTime(formatContext);
                int videoStreamIndex = FindVideoStream(formatContext);

                if (videoStreamIndex < 0)
                    throw new InvalidOperationException("No video stream found.");

                AVStream* videoStream = formatContext->streams[videoStreamIndex];
                AVCodec* codec = ffmpeg.avcodec_find_decoder(videoStream->codecpar->codec_id);

                if (codec is null)
                    throw new InvalidOperationException("No decoder found for the video stream.");

                codecContext = ffmpeg.avcodec_alloc_context3(codec);
                if (codecContext is null)
                    throw new InvalidOperationException("Could not allocate codec context.");

                ThrowIfError(ffmpeg.avcodec_parameters_to_context(codecContext, videoStream->codecpar), "copy codec parameters");

                // Use up to 4 threads for decoding — helps with ProRes and MPEG-2.
                codecContext->thread_count = Math.Min(4, Environment.ProcessorCount);
                codecContext->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;

                ThrowIfError(ffmpeg.avcodec_open2(codecContext, codec, null), "open decoder");

                frame = ffmpeg.av_frame_alloc();
                bgrFrame = ffmpeg.av_frame_alloc();
                packet = ffmpeg.av_packet_alloc();

                if (frame is null || bgrFrame is null || packet is null)
                    throw new InvalidOperationException("Could not allocate decode buffers.");

                int width = codecContext->width;
                int height = codecContext->height;
                double displayAspectRatio = GetDisplayAspectRatio(codecContext, videoStream, width, height);

                bgrFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGR24;
                bgrFrame->width = width;
                bgrFrame->height = height;
                ThrowIfError(ffmpeg.av_frame_get_buffer(bgrFrame, 1), "allocate BGR frame");

                swsContext = ffmpeg.sws_getContext(
                    width, height, codecContext->pix_fmt,
                    width, height, AVPixelFormat.AV_PIX_FMT_BGR24,
                    SwsBilinear, null, null, null);

                if (swsContext is null)
                    throw new InvalidOperationException("Could not create video scaler.");

                return new OpenDecoder(
                    sourceFile, formatContext, codecContext,
                    frame, bgrFrame, packet, swsContext,
                    videoStreamIndex, videoStream,
                    width, height, displayAspectRatio, mediaStartTime);
            }
            catch
            {
                if (packet is not null) ffmpeg.av_packet_free(&packet);
                if (frame is not null) ffmpeg.av_frame_free(&frame);
                if (bgrFrame is not null) ffmpeg.av_frame_free(&bgrFrame);
                if (swsContext is not null) ffmpeg.sws_freeContext(swsContext);
                if (codecContext is not null) ffmpeg.avcodec_free_context(&codecContext);
                if (formatContext is not null) ffmpeg.avformat_close_input(&formatContext);
                throw;
            }
        }

        /// <summary>
        /// Seek to <paramref name="position"/> and decode frames until we
        /// find the one closest to the target. Returns null only if
        /// <paramref name="cancelCheck"/> returns true (newer request arrived).
        /// </summary>
        public VideoFrameEventArgs? DecodeFrameAt(
            TimeSpan position,
            TimeSpan acceptWindow,
            TimeSpan fallbackWindow,
            Func<bool> cancelCheck)
        {
            // Seek to a keyframe at or before the target position.
            SeekTo(position);

            VideoFrameEventArgs? best = null;
            double bestDeltaTicks = double.MaxValue;

            while (ffmpeg.av_read_frame(_formatContext, _packet) >= 0)
            {
                try
                {
                    if (_packet->stream_index != _videoStreamIndex)
                        continue;

                    int sendResult = ffmpeg.avcodec_send_packet(_codecContext, _packet);
                    if (sendResult < 0)
                    {
                        // Transport streams can have corrupt packets — skip them.
                        if (IsTransportStream(SourceFile))
                            continue;
                        ThrowIfError(sendResult, "send packet");
                    }

                    while (true)
                    {
                        int receiveResult = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
                        if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
                            break;

                        ThrowIfError(receiveResult, "receive frame");

                        if (cancelCheck())
                        {
                            best?.Dispose();
                            return null;
                        }

                        TimeSpan ts = GetFrameTimestamp(_frame, _videoStream, _mediaStartTime);
                        double deltaTicks = Math.Abs((ts - position).Ticks);

                        // Frame is before our fallback window — keep decoding.
                        if (ts < position - fallbackWindow)
                            continue;

                        // Frame is within the accept window and closer than what
                        // we already have — keep it as the new best candidate.
                        if (deltaTicks < bestDeltaTicks)
                        {
                            best?.Dispose();
                            best = CreateFrame(ts);
                            bestDeltaTicks = deltaTicks;
                        }

                        // We've passed the target by more than the accept window.
                        // The best candidate we have is good enough — stop.
                        if (ts > position + acceptWindow)
                            return best;
                    }
                }
                finally
                {
                    ffmpeg.av_packet_unref(_packet);
                }
            }

            // Drain the decoder.
            ffmpeg.avcodec_send_packet(_codecContext, null);
            while (true)
            {
                int receiveResult = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
                if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
                    break;

                if (receiveResult < 0)
                    break;

                if (cancelCheck())
                {
                    best?.Dispose();
                    return null;
                }

                TimeSpan ts = GetFrameTimestamp(_frame, _videoStream, _mediaStartTime);
                double deltaTicks = Math.Abs((ts - position).Ticks);
                if (deltaTicks < bestDeltaTicks)
                {
                    best?.Dispose();
                    best = CreateFrame(ts);
                    bestDeltaTicks = deltaTicks;
                }
            }

            return best;
        }

        private void SeekTo(TimeSpan position)
        {
            TimeSpan seekPosition = IsTransportStream(SourceFile)
                ? position - TimeSpan.FromSeconds(1.5)
                : position;

            if (seekPosition < TimeSpan.Zero)
                seekPosition = TimeSpan.Zero;

            TimeSpan seekTime = seekPosition + _mediaStartTime;
            long timestamp = (long)(seekTime.TotalSeconds / AvRationalToDouble(_videoStream->time_base));

            int result = ffmpeg.av_seek_frame(
                _formatContext,
                _videoStreamIndex,
                timestamp,
                ffmpeg.AVSEEK_FLAG_BACKWARD);

            if (result >= 0)
                ffmpeg.avcodec_flush_buffers(_codecContext);
        }

        private VideoFrameEventArgs CreateFrame(TimeSpan timestamp)
        {
            // Scale to BGR24.
            byte*[] srcData = [_frame->data[0], _frame->data[1], _frame->data[2], _frame->data[3]];
            int[] srcLinesize = [_frame->linesize[0], _frame->linesize[1], _frame->linesize[2], _frame->linesize[3]];
            byte*[] dstData = [_bgrFrame->data[0], _bgrFrame->data[1], _bgrFrame->data[2], _bgrFrame->data[3]];
            int[] dstLinesize = [_bgrFrame->linesize[0], _bgrFrame->linesize[1], _bgrFrame->linesize[2], _bgrFrame->linesize[3]];

            int scaledHeight = ffmpeg.sws_scale(_swsContext, srcData, srcLinesize, 0, _height, dstData, dstLinesize);
            if (scaledHeight <= 0)
                throw new InvalidOperationException("FFmpeg failed to scale video frame.");

            int stride = _width * 3;
            int bufferLength = stride * _height;
            byte[] buffer = new byte[bufferLength];

            for (int y = 0; y < _height; y++)
            {
                IntPtr src = (IntPtr)(_bgrFrame->data[0] + y * _bgrFrame->linesize[0]);
                Marshal.Copy(src, buffer, y * stride, stride);
            }

            return new VideoFrameEventArgs(buffer, _width, _height, timestamp, _displayAspectRatio, bufferLength, returnBufferToPool: false);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            AVPacket* packet = _packet;
            if (packet is not null) { _packet = null; ffmpeg.av_packet_free(&packet); }

            AVFrame* frame = _frame;
            if (frame is not null) { _frame = null; ffmpeg.av_frame_free(&frame); }

            AVFrame* bgrFrame = _bgrFrame;
            if (bgrFrame is not null) { _bgrFrame = null; ffmpeg.av_frame_free(&bgrFrame); }

            SwsContext* swsContext = _swsContext;
            if (swsContext is not null) { _swsContext = null; ffmpeg.sws_freeContext(swsContext); }

            AVCodecContext* codecContext = _codecContext;
            if (codecContext is not null) { _codecContext = null; ffmpeg.avcodec_free_context(&codecContext); }

            AVFormatContext* formatContext = _formatContext;
            if (formatContext is not null) { _formatContext = null; ffmpeg.avformat_close_input(&formatContext); }
        }

        // ---------------------------------------------------------------- helpers

        private static int FindVideoStream(AVFormatContext* ctx)
        {
            for (int i = 0; i < ctx->nb_streams; i++)
                if (ctx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    return i;
            return -1;
        }

        private static TimeSpan GetFormatStartTime(AVFormatContext* ctx)
        {
            return ctx->start_time == ffmpeg.AV_NOPTS_VALUE
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(ctx->start_time / (double)ffmpeg.AV_TIME_BASE);
        }

        private static TimeSpan GetFrameTimestamp(AVFrame* frame, AVStream* stream, TimeSpan mediaStartTime)
        {
            long ts = frame->best_effort_timestamp;
            if (ts == ffmpeg.AV_NOPTS_VALUE)
                ts = frame->pts;
            if (ts == ffmpeg.AV_NOPTS_VALUE)
                return TimeSpan.Zero;

            TimeSpan raw = TimeSpan.FromSeconds(ts * AvRationalToDouble(stream->time_base));
            TimeSpan normalized = raw - mediaStartTime;
            return normalized < TimeSpan.Zero ? TimeSpan.Zero : normalized;
        }

        private static double AvRationalToDouble(AVRational r) =>
            r.den == 0 ? 0 : r.num / (double)r.den;

        private static bool IsTransportStream(string path) =>
            string.Equals(Path.GetExtension(path), ".ts", StringComparison.OrdinalIgnoreCase);

        private static void ThrowIfError(int error, string operation)
        {
            if (error >= 0)
                return;

            Span<byte> buf = stackalloc byte[1024];
            fixed (byte* p = buf)
            {
                ffmpeg.av_strerror(error, p, (ulong)buf.Length);
                string msg = Marshal.PtrToStringAnsi((IntPtr)p) ?? "Unknown FFmpeg error";
                throw new InvalidOperationException($"FFmpeg failed to {operation}: {msg}");
            }
        }

        private static double GetDisplayAspectRatio(AVCodecContext* ctx, AVStream* stream, int width, int height)
        {
            if (width <= 0 || height <= 0)
                return 1;

            AVRational sar = ctx->sample_aspect_ratio;
            if (sar.num <= 0 || sar.den <= 0)
                sar = stream->sample_aspect_ratio;

            double sarD = AvRationalToDouble(sar);
            if (sarD <= 0)
                sarD = 1;

            // Snap to broadcast standard DAR when applicable.
            double broadcastDar = GetBroadcastDar(width, height, sar);
            return broadcastDar > 0 ? broadcastDar : width * sarD / height;
        }

        private static double GetBroadcastDar(int width, int height, AVRational sar)
        {
            bool isPal = height == 576 && (width == 720 || width == 704);
            bool isNtsc = height == 480 && (width == 720 || width == 704);
            if (!isPal && !isNtsc)
                return 0;

            if (IsRationalClose(sar, 16, 11) || IsRationalClose(sar, 64, 45) ||
                IsRationalClose(sar, 32, 27) || IsRationalClose(sar, 40, 33))
                return 16 / 9d;

            if (IsRationalClose(sar, 12, 11) || IsRationalClose(sar, 10, 11) ||
                IsRationalClose(sar, 8, 9))
                return 4 / 3d;

            return 0;
        }

        private static bool IsRationalClose(AVRational r, int num, int den) =>
            Math.Abs(AvRationalToDouble(r) - num / (double)den) < 0.01;
    }
}
