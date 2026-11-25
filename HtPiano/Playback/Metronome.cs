namespace HtPiano;

/// <summary>Frame-driven four-beat click. Owned by the render thread.</summary>
internal sealed class Metronome
{
    public const int MinTempo = 40;
    public const int MaxTempo = 240;
    private const int UnitsPerBeat = AudioFormat.SampleRate * 60;
    private static readonly float[] Accent = CreateClick(1660, .24f);
    private static readonly float[] Regular = CreateClick(1120, .15f);
    private int _bpm = 120;
    private int _phase;
    private int _beat;
    private int _clickFrame;
    public bool Enabled { get; private set; }

    public void Configure(bool enabled, int bpm)
    {
        bpm = Math.Clamp(bpm, MinTempo, MaxTempo);
        if (Enabled == enabled && _bpm == bpm)
        {
            return;
        }

        Enabled = enabled;
        _bpm = bpm;
        _phase = 0;
        _beat = 0;
        _clickFrame = 0;
    }

    public float NextFrame()
    {
        if (!Enabled)
        {
            return 0;
        }

        float[] click = _beat == 0 ? Accent : Regular;
        float sample = _clickFrame < click.Length ? click[_clickFrame++] : 0;
        _phase += _bpm;
        if (_phase >= UnitsPerBeat)
        {
            _phase -= UnitsPerBeat;
            _beat = (_beat + 1) % 4;
            _clickFrame = 0;
        }
        return sample;
    }

    private static float[] CreateClick(int frequency, float level)
    {
        var click = new float[AudioFormat.SampleRate * 28 / 1000];
        for (int i = 0; i < click.Length; i++)
        {
            float envelope = Math.Min(1, i / 72f) * (1 - i / (float)click.Length);
            click[i] = level * envelope * envelope * MathF.Sin(MathF.Tau * frequency * i / AudioFormat.SampleRate);
        }
        return click;
    }
}
