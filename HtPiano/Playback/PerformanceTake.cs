namespace HtPiano;

internal enum TakeEventKind { Note, Instrument, Octave, KeyFace, KeypadTuning }

internal readonly record struct TakeEvent(
    long Frame, TakeEventKind Kind, NoteCommand Note = default,
    PlayingZone Zone = PlayingZone.Main, Instrument Instrument = Instrument.YamahaC5, int Octave = 4,
    int ScanCode = 0, bool Down = false, int Semitones = 0);

internal sealed class PerformanceTake
{
    internal PerformanceTake(List<TakeEvent> events, long durationFrames, int initialOctave,
        int initialKeypadSemitones, IReadOnlyList<int>? classicKeys)
    {
        // The recorder seals itself before handing this list to playback.
        Events = events.AsReadOnly();
        DurationFrames = durationFrames;
        InitialOctave = initialOctave;
        InitialKeypadSemitones = initialKeypadSemitones;
        ClassicKeys = classicKeys;
    }

    public IReadOnlyList<TakeEvent> Events { get; }
    public long DurationFrames { get; }
    public int InitialOctave { get; }
    public int InitialKeypadSemitones { get; }
    public IReadOnlyList<int>? ClassicKeys { get; }
}

/// <summary>Owned by the UI thread; timestamps are elapsed audio frames, not wall-clock times.</summary>
internal sealed class TakeRecorder
{
    private const int EventLimit = 200_000;
    private const int MidiNotes = 128;
    // Leave room for the short minimum note gate and the sampler's 200 ms release.
    private const int ReleaseTailFrames = AudioFormat.SampleRate / 4;
    private readonly List<TakeEvent> _events = [];
    private readonly bool[] _sounding = new bool[2 * MidiNotes];
    private readonly Instrument[] _instruments;
    private readonly int _initialOctave;
    private readonly int _initialKeypadSemitones;
    private readonly IReadOnlyList<int>? _classicKeys;
    private readonly HashSet<(int Scan, PlayingZone Zone)> _pressedFaces = [];
    private int _keypadSemitones;
    private long _lastFrame;
    private readonly long[] _tailEndFrames = new long[2];
    private bool _hasNotes;
    private bool _sealed;

    public TakeRecorder(Instrument initialMain, Instrument initialRight, int initialOctave,
        int keypadSemitones = 0, int[]? classicKeys = null)
    {
        _initialOctave = initialOctave;
        _keypadSemitones = _initialKeypadSemitones = keypadSemitones;
        _classicKeys = classicKeys == null ? null : Array.AsReadOnly((int[])classicKeys.Clone());
        _instruments = [initialMain, initialRight];
        _events.Add(new TakeEvent(0, TakeEventKind.Instrument, Zone: PlayingZone.Main, Instrument: initialMain));
        _events.Add(new TakeEvent(0, TakeEventKind.Instrument, Zone: PlayingZone.Right, Instrument: initialRight));
    }

    public bool IsFull => _events.Count >= EventLimit;

    public void Note(long frame, NoteCommand command)
    {
        CheckFrame(frame);
        if (command.Kind == NoteCommandKind.AllNotesOff)
        {
            CutZone(frame, PlayingZone.Main);
            CutZone(frame, PlayingZone.Right);
        }
        else if (command.Kind == NoteCommandKind.ZoneNotesOff)
        {
            CutZone(frame, command.Zone);
        }
        else
        {
            int index = (int)command.Zone * MidiNotes + command.MidiNote;
            bool on = command.Kind == NoteCommandKind.On;
            if (on) _hasNotes = true;
            else if (_sounding[index]) _tailEndFrames[(int)command.Zone] = checked(frame + ReleaseTailFrames);
            _sounding[index] = on;
        }

        _events.Add(new TakeEvent(frame, TakeEventKind.Note, command, command.Zone));
    }

    public void SelectInstrument(long frame, PlayingZone zone, Instrument instrument)
    {
        CheckFrame(frame);
        if (_instruments[(int)zone] == instrument) return;
        _instruments[(int)zone] = instrument;
        CutZone(frame, zone);
        _events.Add(new TakeEvent(frame, TakeEventKind.Instrument, Zone: zone, Instrument: instrument));
    }

    public void KeyFace(long frame, int scanCode, bool down, PlayingZone zone)
    {
        CheckFrame(frame);
        ArgumentOutOfRangeException.ThrowIfLessThan(scanCode, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scanCode, 0x1ff);
        var key = (scanCode, zone);
        bool changed = down ? _pressedFaces.Add(key) : _pressedFaces.Remove(key);
        if (changed)
        {
            _events.Add(new TakeEvent(frame, TakeEventKind.KeyFace, Zone: zone, ScanCode: scanCode, Down: down));
        }
    }

    public void SetKeypadTuning(long frame, int semitones)
    {
        CheckFrame(frame);
        if (_keypadSemitones == semitones) return;
        _keypadSemitones = semitones;
        _events.Add(new TakeEvent(frame, TakeEventKind.KeypadTuning, Semitones: semitones));
    }

    public void SetOctave(long frame, int octave)
    {
        CheckFrame(frame);
        _events.Add(new TakeEvent(frame, TakeEventKind.Octave, Octave: octave));
    }

    public PerformanceTake? Finish(long frame)
    {
        RequireMonotonicFrame(frame);
        _sealed = true;
        if (!_hasNotes) return null;

        foreach (var (scan, zone) in _pressedFaces)
            _events.Add(new TakeEvent(frame, TakeEventKind.KeyFace, Zone: zone, ScanCode: scan, Down: false));

        // Even a full take needs synthetic key-ups when the player stops with keys or pedal down.
        for (int index = 0; index < _sounding.Length; index++)
        {
            if (!_sounding[index]) continue;
            var zone = (PlayingZone)(index / MidiNotes);
            var off = new NoteCommand(NoteCommandKind.Off, index % MidiNotes, zone);
            _events.Add(new TakeEvent(frame, TakeEventKind.Note, off, zone));
            _tailEndFrames[(int)zone] = checked(frame + ReleaseTailFrames);
        }
        long end = Math.Max(frame, Math.Max(_tailEndFrames[0], _tailEndFrames[1]));
        return new PerformanceTake(_events, end, _initialOctave, _initialKeypadSemitones, _classicKeys);
    }

    private void CutZone(long frame, PlayingZone zone)
    {
        int index = (int)zone;
        Span<bool> sounding = _sounding.AsSpan(index * MidiNotes, MidiNotes);
        if (sounding.Contains(true))
        {
            _tailEndFrames[index] = checked(frame + AudioFormat.CutFadeFrames);
            sounding.Clear();
        }
        else if (_tailEndFrames[index] > frame)
        {
            // A cut shortens a release in progress; repeated cuts never restart its fade.
            _tailEndFrames[index] = Math.Min(_tailEndFrames[index], checked(frame + AudioFormat.CutFadeFrames));
        }
    }

    private void CheckFrame(long frame)
    {
        RequireMonotonicFrame(frame);
        if (IsFull) throw new InvalidOperationException(UiLanguage.Get("Lang.Recording.CapacityError"));
        _lastFrame = frame;
    }

    private void RequireMonotonicFrame(long frame)
    {
        if (_sealed) throw new InvalidOperationException(UiLanguage.Get("Lang.Recording.AlreadyFinished"));
        ArgumentOutOfRangeException.ThrowIfLessThan(frame, _lastFrame);
    }
}

/// <summary>Audio-thread cursor. Events at the same frame keep their original arrival order.</summary>
internal sealed class TakePlayback(PerformanceTake take)
{
    private int _nextEvent;

    public long PositionFrames { get; private set; }
    public bool Finished => PositionFrames == take.DurationFrames;

    public void Render(NativePiano mixer, Span<short> stereo, float volume)
    {
        const int channels = AudioFormat.Channels;
        if (stereo.Length % channels != 0)
            throw new ArgumentException(UiLanguage.Get("Lang.Audio.IncompleteStereoFrame"), nameof(stereo));

        while (_nextEvent < take.Events.Count)
        {
            TakeEvent change = take.Events[_nextEvent];
            long distance = change.Frame - PositionFrames;
            if (distance > stereo.Length / channels) break;
            if (change.Kind is not (TakeEventKind.Note or TakeEventKind.Instrument))
            {
                // UI events in this buffer do not split the audio render.
                _nextEvent++;
                continue;
            }
            if (distance != 0)
            {
                int samples = (int)distance * channels;
                mixer.Render(stereo[..samples], volume);
                stereo = stereo[samples..];
                PositionFrames = change.Frame;
            }

            if (change.Kind == TakeEventKind.Note) mixer.Apply(change.Note);
            else if (change.Kind == TakeEventKind.Instrument) mixer.SelectInstrument(change.Zone, change.Instrument);
            _nextEvent++;
        }

        mixer.Render(stereo, volume);
        PositionFrames += Math.Min(stereo.Length / channels, take.DurationFrames - PositionFrames);
    }
}
