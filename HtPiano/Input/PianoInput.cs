namespace HtPiano;

internal enum PlayingZone { Main, Right }
internal enum NoteCommandKind { On, Off, AllNotesOff, ZoneNotesOff }

internal readonly record struct NoteCommand(NoteCommandKind Kind, int MidiNote = 0, PlayingZone Zone = PlayingZone.Main);

internal enum NoteState { Idle, Held, Sustained }
internal enum SustainMode { Hold, Toggle }
internal enum InputKind { Keyboard, Mouse, FocusedKey, Accessibility }
internal readonly record struct InputSource(InputKind Kind, int Id = 0, PlayingZone Zone = PlayingZone.Main);

internal sealed class PianoInput(Action<NoteCommand> send)
{
    public const int MinOctave = 2;
    public const int MaxOctave = 7;
    public const int KeyCount = 13; // The classic preset, including its upper C.
    public const int LowestOffset = -24;
    public const int HighestMainOffset = 31;
    public const int HighestOffset = 35; // B on the keypad, raised by an octave.
    public const int FullKeyCount = HighestOffset - LowestOffset + 1;
    private readonly Dictionary<InputSource, int> _sources = new();
    private readonly int[] _owners = new int[2 * FullKeyCount];
    private readonly bool[] _gates = new bool[2 * FullKeyCount];
    private bool _sustainKeyHeld;

    public event Action<int, bool, PlayingZone>? KeyFaceChanged;

    public int Octave { get; private set; } = 4;
    public SustainMode Mode { get; private set; }
    public bool IsSustained { get; private set; }

    public int MidiNote(int offset) => 12 * (Octave + 1) + offset;

    public bool IsPressed(InputSource source) => _sources.ContainsKey(source);

    public NoteState State(int offset)
    {
        int main = ToIndex(PlayingZone.Main, offset);
        int right = ToIndex(PlayingZone.Right, offset);
        if (_owners[main] > 0 || _owners[right] > 0)
        {
            return NoteState.Held;
        }

        return _gates[main] || _gates[right] ? NoteState.Sustained : NoteState.Idle;
    }

    public bool Press(InputSource source, int offset)
    {
        int index = ToIndex(source.Zone, offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MidiNote(offset), 127);
        bool firstFaceOwner = KeyFaceChanged != null && HasKeyFace(source) && !IsFaceDown(source.Id, source.Zone);
        if (!_sources.TryAdd(source, offset))
        {
            return false;
        }

        if (_owners[index]++ == 0)
        {
            _gates[index] = true;
            send(new NoteCommand(NoteCommandKind.On, MidiNote(offset), source.Zone));
        }

        if (firstFaceOwner) KeyFaceChanged?.Invoke(source.Id, true, source.Zone);
        return true;
    }

    // Screen-piano mouse input uses zero and its accessibility sources use negative IDs;
    // positive IDs from the diagram and physical keyboard are Set-1 scan codes.
    private static bool HasKeyFace(InputSource source) =>
        source.Id is > 0 and <= 0x1ff && source.Kind is InputKind.Keyboard or InputKind.Mouse or InputKind.Accessibility;

    private bool IsFaceDown(int scan, PlayingZone zone) =>
        IsPressed(new InputSource(InputKind.Keyboard, scan, zone)) ||
        IsPressed(new InputSource(InputKind.Mouse, scan, zone)) ||
        IsPressed(new InputSource(InputKind.Accessibility, scan, zone));

    private void NotifyFaceRelease(InputSource source)
    {
        if (KeyFaceChanged != null && HasKeyFace(source) && !IsFaceDown(source.Id, source.Zone))
            KeyFaceChanged(source.Id, false, source.Zone);
    }

    public bool Release(InputSource source)
    {
        if (!_sources.Remove(source, out int offset))
        {
            return false;
        }

        int index = ToIndex(source.Zone, offset);
        if (--_owners[index] == 0 && !IsSustained)
        {
            ReleaseGate(index);
        }

        NotifyFaceRelease(source);
        return true;
    }

    public void SustainDown()
    {
        if (_sustainKeyHeld)
        {
            return;
        }

        _sustainKeyHeld = true;
        SetPedal(Mode == SustainMode.Hold || !IsSustained);
    }

    public void SustainUp()
    {
        _sustainKeyHeld = false;
        if (Mode == SustainMode.Hold)
        {
            SetPedal(false);
        }
    }

    public void SetSustainMode(SustainMode mode)
    {
        if (Mode != mode)
        {
            Mode = mode;
            SetPedal(false);
        }
    }

    public void SetOctave(int octave)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(octave, MinOctave);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(octave, MaxOctave);
        if (octave != Octave)
        {
            Reset();
            Octave = octave;
        }
    }

    public void Reset()
    {
        if (KeyFaceChanged == null)
        {
            _sources.Clear();
        }
        else
        {
            foreach (InputSource source in _sources.Keys.ToArray())
            {
                _sources.Remove(source);
                NotifyFaceRelease(source);
            }
        }
        Array.Clear(_owners);
        Array.Clear(_gates);
        _sustainKeyHeld = false;
        IsSustained = false;
        send(new NoteCommand(NoteCommandKind.AllNotesOff));
    }

    public void ResetZone(PlayingZone zone)
    {
        foreach (InputSource source in _sources.Keys.Where(source => source.Zone == zone).ToArray())
        {
            _sources.Remove(source);
            NotifyFaceRelease(source);
        }

        int first = ToIndex(zone, LowestOffset);
        Array.Clear(_owners, first, FullKeyCount);
        Array.Clear(_gates, first, FullKeyCount);
        send(new NoteCommand(NoteCommandKind.ZoneNotesOff, Zone: zone));
    }

    private void SetPedal(bool sustained)
    {
        if (IsSustained == sustained)
        {
            return;
        }

        IsSustained = sustained;
        if (!sustained)
        {
            for (int index = 0; index < _gates.Length; index++)
            {
                if (_gates[index] && _owners[index] == 0)
                {
                    ReleaseGate(index);
                }
            }
        }
    }

    private static int ToIndex(PlayingZone zone, int offset)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(offset, LowestOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, HighestOffset);
        return (int)zone * FullKeyCount + offset - LowestOffset;
    }

    private void ReleaseGate(int index)
    {
        _gates[index] = false;
        int offset = LowestOffset + index % FullKeyCount;
        send(new NoteCommand(NoteCommandKind.Off, MidiNote(offset), (PlayingZone)(index / FullKeyCount)));
    }
}
