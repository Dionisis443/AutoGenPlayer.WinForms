namespace AutoGen_Player.Video;

internal sealed class VideoFrameEventArgs : EventArgs, IDisposable
{
    private readonly bool _returnBufferToPool;
    private bool _disposed;

    public VideoFrameEventArgs(
        byte[] bgr24Buffer,
        int width,
        int height,
        TimeSpan timestamp,
        double displayAspectRatio,
        int bufferLength = 0,
        bool returnBufferToPool = false)
    {
        Bgr24Buffer = bgr24Buffer;
        Width = width;
        Height = height;
        Timestamp = timestamp;
        DisplayAspectRatio = displayAspectRatio;
        BufferLength = bufferLength > 0 ? bufferLength : width * height * 3;
        _returnBufferToPool = returnBufferToPool;
    }

    public byte[] Bgr24Buffer { get; }
    public int BufferLength { get; }
    public int Width { get; }
    public int Height { get; }
    public TimeSpan Timestamp { get; }
    public double DisplayAspectRatio { get; }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_returnBufferToPool)
            VideoFrameBufferPool.Return(Bgr24Buffer, BufferLength);
    }
}
