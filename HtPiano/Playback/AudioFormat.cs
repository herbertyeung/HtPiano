namespace HtPiano;

// The SDK renders 48 kHz interleaved stereo PCM.
internal static class AudioFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int CutFadeFrames = SampleRate / 200;
}

// Numeric values cross the hp_set_instrument ABI.
internal enum Instrument { YamahaC5 = 0, Harpsichord = 1, Marimba = 2 }
