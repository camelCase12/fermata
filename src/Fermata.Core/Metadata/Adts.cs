namespace Fermata.Metadata;

/// <summary>Raw AAC streams in ADTS framing (.aac files).</summary>
internal static class Adts
{
    private static readonly int[] SampleRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    /// <summary>Reads the stream properties by walking the frame headers.</summary>
    /// <remarks>ADTS has no global header.</remarks>
    public static void Read(ByteSource source, long start, long end, AudioTags tags)
    {
        const int MaxFrames = 200_000; // about 80 minutes at 44.1 kHz; longer streams are extrapolated
        Span<byte> header = stackalloc byte[7];
        long position = start;
        long frames = 0;
        int sampleRate = 0;
        while (position + 7 <= end && frames < MaxFrames)
        {
            if (source.Read(position, header) < 7 || header[0] != 0xFF || (header[1] & 0xF6) != 0xF0)
                break;
            int rateIndex = header[2] >> 2 & 0xF;
            if (rateIndex >= SampleRates.Length)
                break;
            if (frames == 0)
            {
                sampleRate = SampleRates[rateIndex];
                int profile = header[2] >> 6;
                tags.Codec = profile == 0 ? "AAC Main" : "AAC";
                tags.Channels = (header[2] & 1) << 2 | header[3] >> 6;
                tags.SampleRate = sampleRate;
            }
            int frameLength = (header[3] & 3) << 11 | header[4] << 3 | header[5] >> 5;
            if (frameLength < 7)
                break;
            int blocks = (header[6] & 3) + 1;
            frames += blocks;
            position += frameLength;
        }
        if (frames == 0 || sampleRate == 0)
            return;
        double seconds = frames * 1024.0 / sampleRate;
        long walked = position - start;
        if (position < end && frames >= MaxFrames && walked > 0)
            seconds *= (double)(end - start) / walked;
        tags.Duration = TimeSpan.FromSeconds(seconds);
        tags.Bitrate = (int)Math.Round((end - start) * 8 / seconds / 1000);
    }
}
