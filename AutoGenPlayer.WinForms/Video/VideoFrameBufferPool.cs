using System.Collections.Concurrent;

namespace AutoGen_Player.Video;

internal static class VideoFrameBufferPool
{
    private const int MaxBuffersPerSize = 3;
    private static readonly ConcurrentDictionary<int, ConcurrentBag<byte[]>> Buffers = new();

    public static byte[] Rent(int length)
    {
        if (Buffers.TryGetValue(length, out ConcurrentBag<byte[]>? bag) &&
            bag.TryTake(out byte[]? buffer))
        {
            return buffer;
        }

        return new byte[length];
    }

    public static void Return(byte[] buffer, int length)
    {
        if (buffer.Length != length)
            return;

        ConcurrentBag<byte[]> bag = Buffers.GetOrAdd(length, static _ => []);
        if (bag.Count >= MaxBuffersPerSize)
            return;

        bag.Add(buffer);
    }

    public static void Clear()
    {
        Buffers.Clear();
    }
}
