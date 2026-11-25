using System.IO;
using System.Text;

namespace HtPiano;

internal static class TakeFile
{
    // HTKY v1 is little-endian: rate, duration in frames, initial keyboard layout,
    // then fixed-width (frame, opcode, zone, value) records in performance order.
    private const uint Signature = 0x594B5448;
    private const ushort Version = 1;
    private const int RecordBytes = 12;
    // Recorder quota plus its synthetic releases for all physical keys and both MIDI zones.
    private const int MaxEvents = 200_000 + 2 * (511 + 128);

    private enum Op : byte
    {
        On = 1, Off = 2, AllOff = 3, ZoneOff = 4, Instrument = 5,
        Octave = 6, KeyDown = 7, KeyUp = 8, Tuning = 9
    }

    public static PerformanceTake Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != Signature || reader.ReadUInt16() != Version ||
            reader.ReadInt32() != AudioFormat.SampleRate)
            throw InvalidFile();

        long duration = reader.ReadInt64();
        int octave = reader.ReadByte();
        int tuning = reader.ReadSByte();
        int keyCount = reader.ReadByte();
        if (duration <= 0 || !ValidOctave(octave) || !ValidTuning(tuning) ||
            (keyCount != 0 && keyCount != PianoInput.KeyCount))
            throw InvalidFile();

        int[]? keys = null;
        if (keyCount != 0)
        {
            keys = new int[keyCount];
            // Older takes can carry a custom map for the 13-key piano view.
            var seen = new HashSet<int>();
            for (int i = 0; i < keys.Length; i++)
            {
                int scan = reader.ReadUInt16();
                if (!IsClassicTypingKey(scan) || !seen.Add(scan)) throw InvalidFile();
                keys[i] = scan;
            }
        }

        int count = reader.ReadInt32();
        if (count is < 3 or > MaxEvents || stream.Length - stream.Position != (long)count * RecordBytes)
            throw InvalidFile();

        var events = new List<TakeEvent>(count);
        long previous = 0;
        for (int i = 0; i < count; i++)
        {
            long frame = reader.ReadInt64();
            Op op = (Op)reader.ReadByte();
            byte zoneCode = reader.ReadByte();
            int value = reader.ReadInt16();
            if (frame < previous || frame > duration || zoneCode > 1) throw InvalidFile();
            if (i < 2 && (frame != 0 || op != Op.Instrument || zoneCode != i)) throw InvalidFile();

            var zone = zoneCode == 0 ? PlayingZone.Main : PlayingZone.Right;
            events.Add(Decode(frame, op, zone, value));
            previous = frame;
        }

        return new PerformanceTake(events, duration, octave, tuning, keys == null ? null : Array.AsReadOnly(keys));
    }

    private static bool IsClassicTypingKey(int scan) => scan is
        >= 0x02 and <= 0x0D or >= 0x10 and <= 0x1B or >= 0x1E and <= 0x29
        or >= 0x2B and <= 0x35 or 0x56;

    private static TakeEvent Decode(long frame, Op op, PlayingZone zone, int value)
    {
        switch (op)
        {
            case Op.On or Op.Off when value is >= 0 and < 128:
            case Op.AllOff or Op.ZoneOff when value == 0:
                var kind = op switch
                {
                    Op.On => NoteCommandKind.On,
                    Op.Off => NoteCommandKind.Off,
                    Op.AllOff => NoteCommandKind.AllNotesOff,
                    _ => NoteCommandKind.ZoneNotesOff
                };
                return new(frame, TakeEventKind.Note, new NoteCommand(kind, value, zone), zone);
            case Op.Instrument when value is >= 0 and <= 2:
                return new(frame, TakeEventKind.Instrument, Zone: zone, Instrument: (Instrument)value);
            case Op.Octave when ValidOctave(value):
                return new(frame, TakeEventKind.Octave, Octave: value);
            case Op.KeyDown or Op.KeyUp when value is >= 1 and <= 511:
                return new(frame, TakeEventKind.KeyFace, Zone: zone, ScanCode: value, Down: op == Op.KeyDown);
            case Op.Tuning when ValidTuning(value):
                return new(frame, TakeEventKind.KeypadTuning, Semitones: value);
            default:
                throw InvalidFile();
        }
    }

    public static void Save(string path, PerformanceTake take)
    {
        string destination = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destination)!;
        string temporary = Path.Combine(directory, Path.GetRandomFileName());
        var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            using (stream)
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Signature);
                writer.Write(Version);
                writer.Write(AudioFormat.SampleRate);
                writer.Write(take.DurationFrames);
                writer.Write((byte)take.InitialOctave);
                writer.Write((sbyte)take.InitialKeypadSemitones);
                writer.Write((byte)(take.ClassicKeys?.Count ?? 0));
                if (take.ClassicKeys != null)
                    foreach (int key in take.ClassicKeys) writer.Write((ushort)key);
                writer.Write(take.Events.Count);
                foreach (TakeEvent change in take.Events) WriteEvent(writer, change);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            // A failed replacement must leave the previous recording untouched.
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void WriteEvent(BinaryWriter writer, TakeEvent change)
    {
        (Op op, int value) = change.Kind switch
        {
            TakeEventKind.Note => change.Note.Kind switch
            {
                NoteCommandKind.On => (Op.On, change.Note.MidiNote),
                NoteCommandKind.Off => (Op.Off, change.Note.MidiNote),
                NoteCommandKind.AllNotesOff => (Op.AllOff, 0),
                NoteCommandKind.ZoneNotesOff => (Op.ZoneOff, 0),
                _ => throw InvalidFile()
            },
            TakeEventKind.Instrument => (Op.Instrument, (int)change.Instrument),
            TakeEventKind.Octave => (Op.Octave, change.Octave),
            TakeEventKind.KeyFace => (change.Down ? Op.KeyDown : Op.KeyUp, change.ScanCode),
            TakeEventKind.KeypadTuning => (Op.Tuning, change.Semitones),
            _ => throw InvalidFile()
        };

        writer.Write(change.Frame);
        writer.Write((byte)op);
        writer.Write((byte)(change.Zone == PlayingZone.Main ? 0 : 1));
        writer.Write(checked((short)value));
    }

    private static bool ValidOctave(int value) => value is >= PianoInput.MinOctave and <= PianoInput.MaxOctave;
    private static bool ValidTuning(int value) => value is >= KeypadTuning.MinSemitones and <= KeypadTuning.MaxSemitones;

    private static InvalidDataException InvalidFile() => new(UiLanguage.Get("Lang.Recording.InvalidFile"));
}
