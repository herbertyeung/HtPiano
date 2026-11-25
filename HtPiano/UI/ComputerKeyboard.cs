using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace HtPiano;

/// <summary>Shows the performance mapping and reports presses on its playable key faces.</summary>
internal sealed class ComputerKeyboard : Grid
{
    private static readonly Brush Pressed = Freeze(Color.FromRgb(124, 214, 175));
    private static readonly Brush PressedInk = Freeze(Color.FromRgb(16, 29, 26));
    private readonly List<KeyFace> _keys = [];
    private readonly ComboBox _keySignature = new();
    private KeyFace? _capturedKey;
    private List<KeyFace>? _classicReplayKeys;
    private StackPanel? _classicReplayPanel;

    public event Action<PerformanceKey, bool>? PointerNote;
    public event Action<PerformanceKey>? InvokeNote;
    public event Action<int>? KeySignatureChanged;

    public int KeySignaturePitchClass
    {
        get => _keySignature.SelectedIndex;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value, _keySignature.Items.Count);
            _keySignature.SelectedIndex = value;
        }
    }

    public bool KeySignatureDropDownOpen => _keySignature.IsDropDownOpen;
    public bool KeySignatureHasFocus => _keySignature.IsKeyboardFocusWithin;

    private const double KeyHeight = 34;
    private const double KeyMargin = 2;

    public ComputerKeyboard()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
        AddMainKeyboard(CreateZone("Lang.MainKeys", 0));
        AddNavigation(CreateZone("Lang.NavigationKeys", 1));
        AddNumberPad(CreateZone("Lang.NumberPad", 2));
    }

    private StackPanel CreateZone(string titleResource, int column)
    {
        var zone = new StackPanel { Margin = column == 0 ? default : new Thickness(10, 0, 0, 0) };
        SetColumn(zone, column);
        Children.Add(zone);
        var heading = new TextBlock { Margin = new Thickness(2, 0, 0, 5) };
        heading.SetResourceReference(TextBlock.TextProperty, titleResource);
        heading.SetResourceReference(StyleProperty, "Caption");
        zone.Children.Add(heading);
        return zone;
    }

    private void AddMainKeyboard(StackPanel zone)
    {
        // In this grid a normal key occupies four columns, so wider keys can use quarter units.
        const int keyWidth = 4;
        Grid grid = CreateKeyGrid(rows: 6, columns: 60);
        Place(grid, SystemFace("Esc"), 0, 0, width: keyWidth);
        for (int group = 0; group < 3; group++)
        {
            for (int key = 0; key < 4; key++)
            {
                Place(grid, SystemFace($"F{group * 4 + key + 1}"), 0, 8 + group * 18 + key * keyWidth, width: keyWidth);
            }
        }

        Place(grid, SystemFace("Grave"), 1, 0, width: keyWidth);
        PlaceMainRow(0, 1, 4);
        Place(grid, SystemFace("Backspace"), 1, 52, width: 8);

        Place(grid, SystemFace("Tab"), 2, 0, width: 6);
        PlaceMainRow(1, 2, 6);
        Place(grid, SystemFace("Enter", "Lang.FocusPlay"), 2, 54, width: 6, height: 2);

        Place(grid, SystemFace("Caps"), 3, 0, width: 10);
        PlaceMainRow(2, 3, 10);

        Place(grid, SystemFace("Shift", "Lang.LeftShift"), 4, 0, width: 10);
        PlaceMainRow(3, 4, 10);
        Place(grid, SystemFace("Shift", "Lang.RightShift"), 4, 50, width: 10);

        Place(grid, SystemFace("Ctrl ♭", "Lang.PadLower"), 5, 0, width: 6);
        Place(grid, SystemFace("Win", "Lang.LeftWin"), 5, 6, width: 4);
        Place(grid, SystemFace("Alt ♯", "Lang.PadRaise"), 5, 10, width: 5);
        Place(grid, SystemFace("Space", "Lang.SpacePedal"), 5, 15, width: 23);
        Place(grid, SystemFace("Alt", "Lang.RightAlt"), 5, 38, width: 5);
        Place(grid, SystemFace("Win", "Lang.RightWin"), 5, 43, width: 5);
        Place(grid, SystemFace("Menu"), 5, 48, width: 5);
        Place(grid, SystemFace("Ctrl", "Lang.RightCtrl"), 5, 53, width: 7);
        zone.Children.Add(grid);

        void PlaceMainRow(int row, int displayRow, int startColumn)
        {
            int column = startColumn;
            foreach (PerformanceKey key in MultiRowKeyboard.MainRow(row))
            {
                Place(grid, AddKeyFace(key), displayRow, column, width: keyWidth);
                column += keyWidth;
            }
        }
    }

    private void AddNavigation(StackPanel zone)
    {
        Grid grid = CreateKeyGrid(MultiRowKeyboard.RowCount + 1, 3);
        Place(grid, SystemFace("PrtSc"), 0, 0);
        Place(grid, SystemFace("ScrLk"), 0, 1);
        Place(grid, SystemFace("Pause"), 0, 2);
        for (int row = 0; row < MultiRowKeyboard.RowCount; row++)
        {
            int column = row == 2 ? 1 : 0;
            foreach (PerformanceKey key in MultiRowKeyboard.NavigationRow(row))
            {
                Place(grid, AddKeyFace(key), row + 1, column++);
            }
        }
        zone.Children.Add(grid);

        // This space sits directly to the left of the number-pad key selector.
        var shortcuts = new TextBlock
        {
            FontSize = 11, Margin = new Thickness(KeyMargin, 5, KeyMargin, 0),
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
        };
        shortcuts.SetResourceReference(TextBlock.TextProperty, "Lang.KeypadShortcuts");
        shortcuts.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        zone.Children.Add(shortcuts);
    }

    private void AddNumberPad(StackPanel zone)
    {
        Grid grid = CreateKeyGrid(MultiRowKeyboard.PadRowCount, MultiRowKeyboard.PadColumnCount);
        foreach (KeypadCell cell in MultiRowKeyboard.PadKeys)
        {
            Place(grid, AddKeyFace(cell.Key), cell.Row, cell.Column, cell.ColumnSpan, cell.RowSpan);
        }
        zone.Children.Add(grid);

        var row = new DockPanel { Margin = new Thickness(KeyMargin, 5, KeyMargin, 0) };
        var label = new TextBlock
        {
            Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center
        };
        label.SetResourceReference(TextBlock.TextProperty, "Lang.KeypadSignature");
        label.SetResourceReference(StyleProperty, "Caption");
        row.Children.Add(label);
        _keySignature.ItemsSource = new[] { "1=C", "1=D♭", "1=D", "1=E♭", "1=E", "1=F",
            "1=F♯", "1=G", "1=A♭", "1=A", "1=B♭", "1=B" };
        _keySignature.SelectedIndex = 0;
        _keySignature.MinHeight = 27;
        AutomationProperties.SetAutomationId(_keySignature, "KeypadKeySignature");
        AutomationProperties.SetLabeledBy(_keySignature, label);
        _keySignature.SelectionChanged += (_, _) => KeySignatureChanged?.Invoke(KeySignaturePitchClass);
        _keySignature.DropDownClosed += (_, _) =>
        {
            if (InputManager.Current.MostRecentInputDevice is MouseDevice && KeySignatureHasFocus)
                Keyboard.ClearFocus();
        };
        row.Children.Add(_keySignature);
        zone.Children.Add(row);
    }

    private static Grid CreateKeyGrid(int rows, int columns)
    {
        var grid = new Grid();
        for (int row = 0; row < rows; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(KeyHeight + 2 * KeyMargin) });
        }
        for (int column = 0; column < columns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        return grid;
    }

    private static void Place(Grid grid, Border face, int row, int column, int width = 1, int height = 1)
    {
        SetRow(face, row);
        SetColumn(face, column);
        SetColumnSpan(face, width);
        SetRowSpan(face, height);
        grid.Children.Add(face);
    }

    private static Border SystemFace(string name, string descriptionResource = "Lang.NonPlayable")
    {
        var label = new TextBlock
        {
            Text = name, FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        AutomationProperties.SetName(label, name);
        label.SetResourceReference(AutomationProperties.HelpTextProperty, descriptionResource);
        var face = new Border
        {
            Child = label, CornerRadius = new CornerRadius(4), Margin = new Thickness(KeyMargin),
            BorderThickness = new Thickness(1)
        };
        face.SetResourceReference(ToolTipProperty, descriptionResource);
        face.SetResourceReference(Border.BorderBrushProperty, "Line");
        return face;
    }

    private Border AddKeyFace(PerformanceKey key)
    {
        var face = new KeyFace(key);
        _keys.Add(face);
        face.Button.PreviewMouseLeftButtonDown += (_, args) =>
        {
            args.Handled = true;
            if (_capturedKey == null && face.Button.CaptureMouse())
            {
                _capturedKey = face;
                face.Button.Focus();
                PointerNote?.Invoke(key, true);
            }
        };
        face.Button.PreviewMouseLeftButtonUp += (_, args) =>
        {
            if (ReferenceEquals(_capturedKey, face))
            {
                args.Handled = true;
                ReleasePointer();
            }
        };
        face.Button.LostMouseCapture += (_, _) =>
        {
            if (ReferenceEquals(_capturedKey, face)) ReleasePointer();
        };
        face.Button.Click += (_, _) => InvokeNote?.Invoke(key);
        return face.Face;
    }

    public void ReleasePointer(PlayingZone? onlyZone = null)
    {
        if (_capturedKey is not { } face ||
            (onlyZone.HasValue && MultiRowKeyboard.ZoneOf(face.Key.ScanCode) != onlyZone))
        {
            return;
        }

        _capturedKey = null;
        face.Button.ReleaseMouseCapture();
        PointerNote?.Invoke(face.Key, false);
    }

    public void BeginPlayback(IReadOnlyList<int>? classicBindings)
    {
        if (classicBindings == null) return;

        foreach (UIElement zone in Children) zone.Visibility = Visibility.Collapsed;
        _classicReplayPanel = CreateZone("Lang.ClassicKeys", 0);
        SetColumnSpan(_classicReplayPanel, ColumnDefinitions.Count);
        var row = new UniformGrid { Columns = classicBindings.Count, Height = KeyHeight + 2 * KeyMargin };
        _classicReplayKeys = new List<KeyFace>(classicBindings.Count);
        for (int offset = 0; offset < classicBindings.Count; offset++)
        {
            var face = new KeyFace(new PerformanceKey(classicBindings[offset], offset));
            _classicReplayKeys.Add(face);
            row.Children.Add(face.Face);
        }
        _classicReplayPanel.Children.Add(row);
    }

    public void EndPlayback()
    {
        if (_classicReplayPanel == null) return;

        Children.Remove(_classicReplayPanel);
        _classicReplayPanel = null;
        _classicReplayKeys = null;
        foreach (UIElement zone in Children) zone.Visibility = Visibility.Visible;
    }

    public void UpdateNotes(int octave, Func<int, string> noteLabel, int keypadSemitones)
    {
        string hint = UiLanguage.Format("Lang.KeypadTuningHint", noteLabel(keypadSemitones));
        _keySignature.ToolTip = hint;
        AutomationProperties.SetHelpText(_keySignature, hint);
        foreach (KeyFace key in _classicReplayKeys ?? _keys)
        {
            int offset = key.Key.Offset;
            int fromLowest = offset - PianoInput.LowestOffset;
            int pitchClass = fromLowest % 12;
            const string registers = "₀₁₂₃₄₅₆₇₈₉";
            int register = octave + PianoInput.LowestOffset / 12 + fromLowest / 12;
            string number = pitchClass switch
            {
                0 => "1", 1 => "♯1", 2 => "2", 3 => "♯2", 4 => "3", 5 => "4",
                6 => "♯4", 7 => "5", 8 => "♯5", 9 => "6", 10 => "♯6", _ => "7"
            };
            key.Degree.Text = number + registers[register];
            int soundingOffset = _classicReplayKeys == null
                ? MultiRowKeyboard.ResolveOffset(key.Key.ScanCode, offset, keypadSemitones) : offset;
            string description = UiLanguage.Format("Lang.KeyPitch", key.DisplayName, noteLabel(soundingOffset));
            key.Face.ToolTip = description;
            AutomationProperties.SetName(key.KeyLabel, description);
            AutomationProperties.SetName(key.Button, description);
        }
    }

    public void UpdatePressed(PianoInput input)
    {
        foreach (KeyFace key in _keys)
        {
            PlayingZone zone = MultiRowKeyboard.ZoneOf(key.Key.ScanCode);
            bool down = input.IsPressed(new InputSource(InputKind.Keyboard, key.Key.ScanCode, zone)) ||
                input.IsPressed(new InputSource(InputKind.Mouse, key.Key.ScanCode, zone)) ||
                input.IsPressed(new InputSource(InputKind.Accessibility, key.Key.ScanCode, zone));
            SetPressed(key, down);
        }
    }

    public void ShowReplayKeys(HashSet<int> down, HashSet<int> attacks)
    {
        foreach (KeyFace key in _classicReplayKeys ?? _keys)
        {
            int code = key.Key.ScanCode;
            SetPressed(key, down.Contains(code) || attacks.Contains(code));
        }
    }

    private static void SetPressed(KeyFace key, bool down)
    {
        if (key.Pressed == down) return;
        key.Pressed = down;
        key.KeyLabel.Text = down ? $"{key.DisplayName} ▾" : key.DisplayName;
        key.KeyLabel.SetResourceReference(AutomationProperties.ItemStatusProperty, down ? "Lang.KeyDown" : "Lang.KeyUp");
        key.Button.SetResourceReference(AutomationProperties.ItemStatusProperty, down ? "Lang.KeyDown" : "Lang.KeyUp");
        if (down)
        {
            key.Face.Background = Pressed;
            key.KeyLabel.Foreground = PressedInk;
            key.Degree.Foreground = PressedInk;
        }
        else
        {
            key.Face.SetResourceReference(Border.BackgroundProperty, "Surface");
            key.KeyLabel.SetResourceReference(TextBlock.ForegroundProperty, "Paper");
            key.Degree.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        }
    }

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed class KeyFace
    {
        public PerformanceKey Key { get; }
        public string DisplayName { get; }
        public Border Face { get; }
        public Button Button { get; }
        public TextBlock KeyLabel { get; }
        public TextBlock Degree { get; }
        public bool Pressed { get; set; }

        public KeyFace(PerformanceKey key)
        {
            Key = key;
            DisplayName = key.Label ?? PhysicalKey.Label(key.ScanCode);
            KeyLabel = new TextBlock { Text = DisplayName, FontSize = 10, TextAlignment = TextAlignment.Center };
            Degree = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
            KeyLabel.SetResourceReference(TextBlock.ForegroundProperty, "Paper");
            Degree.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            KeyLabel.SetResourceReference(AutomationProperties.ItemStatusProperty, "Lang.KeyUp");
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(KeyLabel);
            stack.Children.Add(Degree);
            Button = new Button { Content = stack };
            AutomationProperties.SetAutomationId(Button, $"Scan{key.ScanCode:X4}");
            Button.SetResourceReference(StyleProperty, "DiagramNoteButton");
            Button.SetResourceReference(AutomationProperties.ItemStatusProperty, "Lang.KeyUp");
            Face = new Border
            {
                CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
                Margin = new Thickness(KeyMargin), Child = Button
            };
            Face.SetResourceReference(Border.BackgroundProperty, "Surface");
            Face.SetResourceReference(Border.BorderBrushProperty, "Line");
        }
    }
}
