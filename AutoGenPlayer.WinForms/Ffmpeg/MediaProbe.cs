using AutoGen_Player.Core;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AutoGen_Player.Ffmpeg;

internal sealed class MediaProbe
{
    private readonly string _ffprobePath;

    public MediaProbe(string ffprobePath)
    {
        _ffprobePath = ffprobePath;
    }

    public async Task<MediaProbeResult> ReadMediaInfoAsync(
        string sourceFile,
        CancellationToken cancellationToken = default)
    {
        ProcessRunResult result = await RunProcessAsync(
            [
                "-v", "error",
                "-show_entries", "stream=index,codec_type,codec_name,sample_rate,channels,channel_layout,duration,nb_frames,avg_frame_rate,r_frame_rate,start_time,width,height,sample_aspect_ratio,display_aspect_ratio:stream_tags=language,title:format=duration,start_time",
                "-of", "json",
                sourceFile
            ],
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.Output);

        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("streams", out JsonElement streams))
            return new MediaProbeResult([], MediaVideoInfo.Empty);

        List<MediaStreamInfo> audioStreams = [];
        JsonElement? videoStream = null;
        int audioOrdinal = 0;

        foreach (JsonElement stream in streams.EnumerateArray())
        {
            string codecType = GetJsonString(stream, "codec_type");
            if (codecType == "audio")
            {
                string language = string.Empty;
                string title = string.Empty;

                if (stream.TryGetProperty("tags", out JsonElement tags))
                {
                    language = GetJsonString(tags, "language");
                    title = GetJsonString(tags, "title");
                }

                audioStreams.Add(new MediaStreamInfo(
                    audioOrdinal,
                    GetJsonInt(stream, "index"),
                    GetJsonString(stream, "codec_name"),
                    GetJsonInt(stream, "channels"),
                    GetJsonString(stream, "sample_rate"),
                    GetJsonString(stream, "channel_layout"),
                    language,
                    title));

                audioOrdinal++;
            }
            else if (codecType == "video" && videoStream is null)
            {
                videoStream = stream;
            }
        }

        MediaVideoInfo videoInfo = videoStream.HasValue
            ? ReadVideoInfo(root, videoStream.Value)
            : MediaVideoInfo.Empty;

        return new MediaProbeResult(audioStreams, videoInfo);
    }

    public async Task<IReadOnlyList<MediaStreamInfo>> ReadAudioStreamsAsync(
        string sourceFile,
        CancellationToken cancellationToken = default)
    {
        ProcessRunResult result = await RunProcessAsync(
            [
                "-v", "error",
                "-select_streams", "a",
                "-show_entries", "stream=index,codec_name,sample_rate,channels,channel_layout:stream_tags=language,title",
                "-of", "json",
                sourceFile
            ],
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.Output);

        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        JsonElement streams = document.RootElement.GetProperty("streams");
        List<MediaStreamInfo> audioStreams = [];

        int audioOrdinal = 0;
        foreach (JsonElement stream in streams.EnumerateArray())
        {
            string language = string.Empty;
            string title = string.Empty;

            if (stream.TryGetProperty("tags", out JsonElement tags))
            {
                language = GetJsonString(tags, "language");
                title = GetJsonString(tags, "title");
            }

            audioStreams.Add(new MediaStreamInfo(
                audioOrdinal,
                GetJsonInt(stream, "index"),
                GetJsonString(stream, "codec_name"),
                GetJsonInt(stream, "channels"),
                GetJsonString(stream, "sample_rate"),
                GetJsonString(stream, "channel_layout"),
                language,
                title));

            audioOrdinal++;
        }

        return audioStreams;
    }

    public async Task<MediaVideoInfo> ReadVideoInfoAsync(
        string sourceFile,
        CancellationToken cancellationToken = default)
    {
        ProcessRunResult result = await RunProcessAsync(
            [
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "stream=duration,nb_frames,avg_frame_rate,r_frame_rate,start_time,width,height,sample_aspect_ratio,display_aspect_ratio:format=duration,start_time",
                "-of", "json",
                sourceFile
            ],
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.Output);

        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("streams", out JsonElement streams) || streams.GetArrayLength() == 0)
            return MediaVideoInfo.Empty;

        JsonElement stream = streams[0];
        return ReadVideoInfo(root, stream);
    }

    private static MediaVideoInfo ReadVideoInfo(JsonElement root, JsonElement stream)
    {
        double frameRate = ParseRational(GetJsonString(stream, "avg_frame_rate"));
        if (frameRate <= 0)
            frameRate = ParseRational(GetJsonString(stream, "r_frame_rate"));

        double durationSeconds = GetJsonDouble(stream, "duration");
        if (durationSeconds <= 0 &&
            root.TryGetProperty("format", out JsonElement format))
        {
            durationSeconds = GetJsonDouble(format, "duration");
        }

        int frameCount = GetJsonInt(stream, "nb_frames");
        if (frameCount <= 0 && frameRate > 0 && durationSeconds > 0)
            frameCount = (int)Math.Min(int.MaxValue, Math.Ceiling(durationSeconds * frameRate));

        double streamStartSeconds = GetJsonDouble(stream, "start_time");
        double formatStartSeconds = root.TryGetProperty("format", out JsonElement startFormat)
            ? GetJsonDouble(startFormat, "start_time")
            : 0;
        double normalizedStartSeconds = Math.Max(0, streamStartSeconds - formatStartSeconds);
        int width = GetJsonInt(stream, "width");
        int height = GetJsonInt(stream, "height");
        double displayAspectRatio = ParseAspectRatio(GetJsonString(stream, "display_aspect_ratio"));
        if (displayAspectRatio <= 0 && width > 0 && height > 0)
        {
            double sampleAspectRatio = ParseRational(GetJsonString(stream, "sample_aspect_ratio"));
            if (sampleAspectRatio <= 0)
                sampleAspectRatio = 1;

            displayAspectRatio = width * sampleAspectRatio / height;
        }

        return new MediaVideoInfo(
            frameRate,
            durationSeconds > 0 ? TimeSpan.FromSeconds(durationSeconds) : TimeSpan.Zero,
            Math.Max(0, frameCount),
            normalizedStartSeconds > 0 ? TimeSpan.FromSeconds(normalizedStartSeconds) : TimeSpan.Zero,
            Math.Max(0, width),
            Math.Max(0, height),
            displayAspectRatio > 0 ? displayAspectRatio : 0);
    }

    private async Task<ProcessRunResult> RunProcessAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo.FileName = _ffprobePath;
        process.StartInfo.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
        process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;

        foreach (string argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        StringBuilder standardOutput = new();
        StringBuilder standardError = new();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                standardOutput.AppendLine(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                standardError.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new ProcessRunResult(
            process.ExitCode,
            standardOutput.ToString().Trim(),
            standardError.ToString().Trim());
    }

    private static int GetJsonInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
            return number;

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return 0;
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value)
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static double GetJsonDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
            return number;

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return 0;
    }

    private static double ParseRational(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        string[] parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) &&
            denominator != 0)
        {
            return numerator / denominator;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
            ? result
            : 0;
    }

    private static double ParseAspectRatio(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        string normalized = value.Replace(':', '/');
        return ParseRational(normalized);
    }

    private sealed record ProcessRunResult(
        int ExitCode,
        string StandardOutput,
        string StandardError)
    {
        public string Output => string.IsNullOrWhiteSpace(StandardError)
            ? StandardOutput
            : StandardError + Environment.NewLine + StandardOutput;
    }
}
