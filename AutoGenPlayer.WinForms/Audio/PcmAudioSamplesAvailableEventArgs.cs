namespace AutoGen_Player.Audio;

public sealed class PcmAudioSamplesAvailableEventArgs : EventArgs
{
    public PcmAudioSamplesAvailableEventArgs(
        byte[] buffer,
        int offset,
        int count,
        int sampleRate,
        int channels)
    {
        Buffer = buffer;
        Offset = offset;
        Count = count;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public byte[] Buffer { get; }
    public int Offset { get; }
    public int Count { get; }
    public int SampleRate { get; }
    public int Channels { get; }
}
