namespace HtPiano;

internal readonly record struct PerformanceKey(int ScanCode, int Offset, string? Label = null);
internal readonly record struct KeypadCell(PerformanceKey Key, int Row, int Column, int RowSpan = 1, int ColumnSpan = 1);

/// <summary>The built-in typing-key layout, keyed by Windows Set-1 physical positions.</summary>
internal static class MultiRowKeyboard
{
    private static readonly PerformanceKey[][] Main =
    [
        [new(0x02, 12), new(0x03, 14), new(0x04, 16), new(0x05, 17), new(0x06, 19),
         new(0x07, 21), new(0x08, 23), new(0x09, 24), new(0x0A, 26), new(0x0B, 28),
         new(0x0C, 29), new(0x0D, 31)],
        [new(0x10, 0), new(0x11, 2), new(0x12, 4), new(0x13, 5), new(0x14, 7),
         new(0x15, 9), new(0x16, 11), new(0x17, 12), new(0x18, 14), new(0x19, 16),
         new(0x1A, 17), new(0x1B, 19)],
        [new(0x1E, -12), new(0x1F, -10), new(0x20, -8), new(0x21, -7), new(0x22, -5),
         new(0x23, -3), new(0x24, -1), new(0x25, 0), new(0x26, 2), new(0x27, 4), new(0x28, 5)],
        [new(0x2C, -24), new(0x2D, -22), new(0x2E, -20), new(0x2F, -19), new(0x30, -17),
         new(0x31, -15), new(0x32, -13), new(0x33, -12), new(0x34, -10), new(0x35, -8, "/?")]
    ];

    // The edit cluster plays the upper register; the inverted-T arrows play the lower register.
    // E0-prefixed physical keys remain distinct from the non-extended keypad positions.
    private static readonly PerformanceKey[][] Navigation =
    [
        [new(0x152, 17, "Ins"), new(0x147, 19, "Home"), new(0x149, 21, "PgUp")],
        [new(0x153, 12, "Del"), new(0x14F, 14, "End"), new(0x151, 16, "PgDn")],
        [new(0x148, -7, "↑")],
        [new(0x14B, -12, "←"), new(0x150, -10, "↓"), new(0x14D, -8, "→")]
    ];

    public const int HighestPadOffset = 23; // Keypad minus: high 7.
    public const int NumLockScanCode = 0x45;
    public const int PadEnterScanCode = 0x11C;
    public const int PadRowCount = 5;
    public const int PadColumnCount = 4;

    // Layout and pitch share the same table; pad digits use their physical positions with Num Lock off.
    private static readonly KeypadCell[] Pad =
    [
        new(new(NumLockScanCode, 17, "NumLk"), 0, 0),
        new(new(0x135, 19, "/"), 0, 1), new(new(0x37, 21, "*"), 0, 2), new(new(0x4A, HighestPadOffset, "−"), 0, 3),
        new(new(0x47, 11, "7"), 1, 0), new(new(0x48, 12, "8"), 1, 1), new(new(0x49, 14, "9"), 1, 2),
        new(new(0x4E, 16, "+"), 1, 3, RowSpan: 2),
        new(new(0x4B, 5, "4"), 2, 0), new(new(0x4C, 7, "5"), 2, 1), new(new(0x4D, 9, "6"), 2, 2),
        new(new(0x4F, 0, "1"), 3, 0), new(new(0x50, 2, "2"), 3, 1), new(new(0x51, 4, "3"), 3, 2),
        new(new(PadEnterScanCode, -1, "Enter"), 3, 3, RowSpan: 2),
        new(new(0x52, -5, "0"), 4, 0, ColumnSpan: 2), new(new(0x53, -3, "."), 4, 2)
    ];

    private static readonly Dictionary<int, int> Offsets = Main.Concat(Navigation)
        .SelectMany(row => row)
        .Concat(Pad.Select(cell => cell.Key))
        .ToDictionary(key => key.ScanCode, key => key.Offset);

    private static readonly HashSet<int> RightKeyCodes = Navigation.SelectMany(row => row)
        .Concat(Pad.Select(cell => cell.Key))
        .Select(key => key.ScanCode)
        .ToHashSet();

    public static PlayingZone ZoneOf(int scanCode) =>
        RightKeyCodes.Contains(scanCode) ? PlayingZone.Right : PlayingZone.Main;

    public const int RowCount = 4;
    public static ReadOnlySpan<PerformanceKey> MainRow(int row) => Main[row];
    public static ReadOnlySpan<PerformanceKey> NavigationRow(int row) => Navigation[row];
    public static ReadOnlySpan<KeypadCell> PadKeys => Pad;

    public static bool IsPadKey(int scanCode)
    {
        foreach (KeypadCell cell in Pad)
        {
            if (cell.Key.ScanCode == scanCode) return true;
        }

        return false;
    }

    public static int ResolveOffset(int scanCode, int naturalOffset, int shift)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shift, KeypadTuning.MinSemitones);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(shift, KeypadTuning.MaxSemitones);
        return shift != 0 && IsPadKey(scanCode) ? naturalOffset + shift : naturalOffset;
    }

    public static int? FindOffset(int scanCode) =>
        Offsets.TryGetValue(scanCode, out int offset) ? offset : null;
}

internal sealed class KeypadTuning
{
    public const int MinSemitones = -12;
    public const int MaxSemitones = 12;

    public int Semitones { get; private set; }
    public int PitchClass => (Semitones + 12) % 12;

    public void Select(int pitchClass)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pitchClass);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pitchClass, 11);
        if (pitchClass == PitchClass) return;

        // An explicit choice starts in the register nearest C; shortcuts preserve the direction of motion.
        Semitones = pitchClass <= 6 ? pitchClass : pitchClass - 12;
    }

    public void FitOctave(int octave)
    {
        if (Semitones > HighestShift(octave)) Semitones -= 12;
    }

    private static int HighestShift(int octave)
    {
        return Math.Min(MaxSemitones, 127 - 12 * (octave + 1) - MultiRowKeyboard.HighestPadOffset);
    }

    public bool Step(int direction, int octave = 4)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));

        int next = Semitones + direction;
        if (next < MinSemitones || next > HighestShift(octave)) return false;
        Semitones = next;
        return true;
    }
}
