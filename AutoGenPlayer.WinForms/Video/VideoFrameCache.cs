namespace AutoGen_Player.Video;

internal sealed class VideoFrameCache
{
    private readonly object _syncRoot = new();
    private readonly List<VideoFrameEventArgs> _frames = [];
    private readonly long _maxBytes;
    private string? _sourceFile;
    private long _cachedBytes;

    public VideoFrameCache(long maxBytes)
    {
        _maxBytes = Math.Max(1, maxBytes);
    }

    public bool TryGet(string sourceFile, TimeSpan position, TimeSpan tolerance, out VideoFrameEventArgs frame)
    {
        lock (_syncRoot)
        {
            if (!string.Equals(_sourceFile, sourceFile, StringComparison.OrdinalIgnoreCase))
            {
                frame = null!;
                return false;
            }

            int frameIndex = -1;
            double closestDelta = double.MaxValue;
            for (int i = 0; i < _frames.Count; i++)
            {
                double delta = Math.Abs((_frames[i].Timestamp - position).TotalMilliseconds);
                if (delta > tolerance.TotalMilliseconds || delta >= closestDelta)
                    continue;

                closestDelta = delta;
                frameIndex = i;
            }

            if (frameIndex < 0)
            {
                frame = null!;
                return false;
            }

            frame = _frames[frameIndex];
            return true;
        }
    }

    public void Add(string sourceFile, VideoFrameEventArgs frame, TimeSpan duplicateTolerance)
    {
        lock (_syncRoot)
        {
            EnsureSource(sourceFile);

            if (_frames.Count == 0 ||
                Math.Abs((_frames[^1].Timestamp - frame.Timestamp).TotalMilliseconds) > duplicateTolerance.TotalMilliseconds)
            {
                _frames.Add(frame);
                _cachedBytes += frame.BufferLength;
            }
            else
            {
                frame.Dispose();
            }

            Trim();
        }
    }

    public void PrepareForSource(string sourceFile)
    {
        lock (_syncRoot)
            EnsureSource(sourceFile);
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _sourceFile = null;
            DisposeFrames();
            _frames.Clear();
            _cachedBytes = 0;
        }
    }

    private void EnsureSource(string sourceFile)
    {
        if (string.Equals(_sourceFile, sourceFile, StringComparison.OrdinalIgnoreCase))
            return;

        _sourceFile = sourceFile;
        DisposeFrames();
        _frames.Clear();
        _cachedBytes = 0;
    }

    private void Trim()
    {
        while (_frames.Count > 0 && _cachedBytes > _maxBytes)
        {
            _cachedBytes -= _frames[0].BufferLength;
            _frames[0].Dispose();
            _frames.RemoveAt(0);
        }
    }

    private void DisposeFrames()
    {
        foreach (VideoFrameEventArgs frame in _frames)
            frame.Dispose();
    }
}
