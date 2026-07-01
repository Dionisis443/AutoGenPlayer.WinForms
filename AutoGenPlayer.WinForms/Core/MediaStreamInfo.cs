namespace AutoGen_Player.Core;

public sealed record MediaStreamInfo(
    int AudioOrdinal,
    int StreamIndex,
    string Codec,
    int Channels,
    string SampleRate,
    string ChannelLayout,
    string Language,
    string Title)
{
    public string DisplayText
    {
        get
        {
            List<string> parts = [$"Stream {StreamIndex}"];

            if (!string.IsNullOrWhiteSpace(Codec))
                parts.Add(Codec);

            if (Channels > 0)
                parts.Add(Channels == 1 ? "mono" : Channels == 2 ? "stereo" : Channels + "ch");

            if (!string.IsNullOrWhiteSpace(SampleRate))
                parts.Add(SampleRate + "Hz");

            if (!string.IsNullOrWhiteSpace(ChannelLayout))
                parts.Add(ChannelLayout);

            if (!string.IsNullOrWhiteSpace(Language))
                parts.Add(Language);

            if (!string.IsNullOrWhiteSpace(Title))
                parts.Add(Title);

            return string.Join(" - ", parts);
        }
    }
}
