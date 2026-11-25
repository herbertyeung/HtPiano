using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace HtPiano;

public partial class MainWindow : Window
{
    private static readonly string[] NoteNames = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];
    private static readonly InputSource MouseSource = new(InputKind.Mouse);
    private static readonly InputSource FocusedSource = new(InputKind.FocusedKey);
    private const int Space = 0x20;
    private const int Enter = 0x0D;
    private readonly PianoInput _piano;
    private readonly KeypadTuning _keypadTuning = new();
    private int _pendingKeySignatureTap;
    private int _visibleTop;
    private readonly PianoKey[] _keys = new PianoKey[PianoInput.FullKeyCount];
    private readonly Func<NativePiano> _createMixer;
    private WaveOutPlayer? _audio;
    private PianoKey? _mouseKey;
    private nint _windowHandle;
    private bool _classic;
    private Rect _multiRowBounds;
    private WindowState _multiRowState;
    private double _multiRowMinHeight;
    private double _hiddenSettingsHeight;
    private bool _closing;
    private readonly int[] _zoneGenerations = new int[2];
    private bool _metronomeOn;
    private string? _audioError;

    internal MainWindow(Func<NativePiano> createMixer, string? startupRecording)
    {
        _startupRecording = startupRecording;
        _createMixer = createMixer;
        _piano = new PianoInput(SendPlayedNote);
        _piano.KeyFaceChanged += RecordKeyFace;
        InitializeComponent();
        InitializeLanguagePicker();
        LayoutPicker.SelectedIndex = 0;
        LayoutPicker.SelectionChanged += OnLayoutSelected;
        LayoutPicker.DropDownClosed += ReleasePickerMouseFocus;

        BuildKeys();
        ComputerKeys.PointerNote += PlayDiagramPointer;
        ComputerKeys.InvokeNote += InvokeDiagramKey;
        ComputerKeys.KeySignatureChanged += pitchClass =>
        {
            if (_playingTake) return;
            _keypadTuning.Select(pitchClass);
            RecordKeypadTuning();
            RefreshLayout();
        };
        PreviewMouseDown += (_, _) => _pendingKeySignatureTap = 0;
        PreviewMouseWheel += (_, _) => _pendingKeySignatureTap = 0;
        foreach (ComboBox picker in new[] { MainInstrument, RightInstrument })
        {
            SetInstrumentNames(picker);
            picker.SelectionChanged += InstrumentChanged;
            picker.DropDownClosed += (_, _) =>
            {
                if (InputManager.Current.MostRecentInputDevice is MouseDevice && picker.IsKeyboardFocusWithin)
                    Keyboard.ClearFocus();
            };
        }
        TempoSlider.ValueChanged += (_, _) => ApplyMetronome();
        _takeTimer.Tick += UpdateTakePosition;
        UpdateTakeControls();
        RefreshLayout();
        VolumeValue.Text = UiLanguage.Format("Lang.VolumeValue", VolumeSlider.Value);
        VolumeSlider.ValueChanged += ChangeVolume;
        RefreshPlayState();
        UpdateFooter();
        Loaded += Start;
    }

    protected override void OnSourceInitialized(EventArgs args)
    {
        base.OnSourceInitialized(args);
        _windowHandle = new WindowInteropHelper(this).Handle;
        ComponentDispatcher.ThreadFilterMessage += FilterKey;
    }

    protected override void OnDeactivated(EventArgs args)
    {
        base.OnDeactivated(args);
        if (!_closing && !_playingTake)
        {
            ResetPlaying();
        }
    }

    protected override void OnClosing(CancelEventArgs args)
    {
        base.OnClosing(args);
        if (args.Cancel)
        {
            return;
        }

        _closing = true;
        _takeTimer.Stop();
        _recorder = null;
        _audio?.StopPlayback();
        ComponentDispatcher.ThreadFilterMessage -= FilterKey;
        ResetPlaying();
        _audio?.Dispose();
    }

    private void Start(object sender, RoutedEventArgs args)
    {
        Loaded -= Start;
        try
        {
            _audio = new WaveOutPlayer((float)VolumeSlider.Value / 100,
                error => Dispatcher.BeginInvoke(() => ShowAudioStatus(error)), _createMixer);
            _audio.SelectInstrument(PlayingZone.Main, (Instrument)MainInstrument.SelectedIndex);
            _audio.SelectInstrument(PlayingZone.Right, (Instrument)RightInstrument.SelectedIndex);
            ApplyMetronome();
        }
        catch (Exception error)
        {
            ShowAudioStatus(error.Message);
        }
    }

    private void ShowAudioStatus(string? error)
    {
        if (_closing)
        {
            return;
        }

        _audioError = error;
        _audioReady = error == null;
        if (!_audioReady)
        {
            if (_recorder != null) FinishRecording();
            if (_playingTake) EndPlayback();
        }
        UpdateTakeControls();
        UpdateFooter();
        if (_audioReady && _startupRecording != null)
        {
            string path = _startupRecording;
            _startupRecording = null;
            OpenRecording(path);
        }
    }

    private void BuildKeys()
    {
        for (int index = 0; index < _keys.Length; index++)
        {
            var key = new PianoKey(index + PianoInput.LowestOffset);
            key.PreviewMouseLeftButtonDown += PressMouse;
            key.PreviewMouseLeftButtonUp += ReleaseMouse;
            key.LostMouseCapture += LoseMouse;
            key.Click += InvokeKey;
            _keys[index] = key;
            Board.Children.Add(key);
        }
    }

    private void FilterKey(ref MSG message, ref bool handled)
    {
        if (handled || _playingTake || message.hwnd != _windowHandle || !IsActive || !PhysicalKey.TryRead(message, out PhysicalKey key))
        {
            return;
        }

        if ((LanguagePicker.IsKeyboardFocusWithin || LayoutPicker.IsKeyboardFocusWithin) && key.Down)
        {
            _pendingKeySignatureTap = 0;
            return;
        }

        if (HandleKeySignatureTap(key))
        {
            handled = true;
            return;
        }

        if (key.Down && WantsControlNavigation(key.VirtualKey))
        {
            return;
        }

        if (key.VirtualKey == Space && (!key.SystemKey || !key.Down))
        {
            if (!key.Down)
            {
                handled = true;
                _piano.SustainUp();
                RefreshPlayState();
            }
            else if (!HasShortcutModifier())
            {
                handled = true;
                if (!key.Repeat)
                {
                    _piano.SustainDown();
                    RefreshPlayState();
                }
            }

            return;
        }

        bool playsFocusedKey = key.VirtualKey == Enter && key.ScanCode != MultiRowKeyboard.PadEnterScanCode;
        if (playsFocusedKey && !key.Down && _piano.Release(FocusedSource))
        {
            handled = true;
            RefreshPlayState();
            return;
        }

        if (playsFocusedKey && key.Down && Keyboard.FocusedElement is PianoKey focused && !HasShortcutModifier())
        {
            handled = true;
            if (!key.Repeat)
            {
                _piano.Press(FocusedSource, focused.Offset);
                RefreshPlayState();
            }

            return;
        }

        PlayingZone zone = MultiRowKeyboard.ZoneOf(key.ScanCode);
        var source = new InputSource(InputKind.Keyboard, key.ScanCode, zone);
        int? offset = MultiRowKeyboard.FindOffset(key.ScanCode);
        if (!key.Down)
        {
            if (_piano.Release(source))
            {
                handled = true;
                RefreshPlayState();
            }

            return;
        }

        if (offset is int pitch && !key.SystemKey && !HasShortcutModifier())
        {
            handled = true;
            if (!key.Repeat)
            {
                int soundingOffset = ResolveDiagramOffset(key.ScanCode, pitch);
                _piano.Press(source, soundingOffset);
                RefreshPlayState();
            }
        }
    }

    private int ResolveDiagramOffset(int scanCode, int naturalOffset) =>
        MultiRowKeyboard.ResolveOffset(scanCode, naturalOffset, _keypadTuning.Semitones);

    private bool HandleKeySignatureTap(PhysicalKey key)
    {
        const int leftCtrl = 0x1D;
        const int leftAlt = 0x38;
        if (LanguagePicker.IsDropDownOpen || LayoutPicker.IsDropDownOpen || MainInstrument.IsDropDownOpen || RightInstrument.IsDropDownOpen ||
            ComputerKeys.KeySignatureDropDownOpen)
        {
            _pendingKeySignatureTap = 0;
            return false;
        }

        // Only a tap qualifies. Let chords (including Alt+F4 and AltGr) keep their normal behavior.
        if (key.Down && _pendingKeySignatureTap != 0 && _pendingKeySignatureTap != key.ScanCode)
        {
            _pendingKeySignatureTap = 0;
            return false;
        }
        if (key.ScanCode is not (leftCtrl or leftAlt))
            return false;

        if (key.Down)
        {
            ModifierKeys ownModifier = key.ScanCode == leftCtrl ? ModifierKeys.Control : ModifierKeys.Alt;
            if (!key.Repeat && (Keyboard.Modifiers & ~ownModifier) == 0 &&
                !Keyboard.IsKeyDown(Key.RightCtrl) && !Keyboard.IsKeyDown(Key.RightAlt))
                _pendingKeySignatureTap = key.ScanCode;
            return false;
        }
        if (_pendingKeySignatureTap != key.ScanCode)
            return false;

        _pendingKeySignatureTap = 0;
        if (_keypadTuning.Step(key.ScanCode == leftCtrl ? -1 : 1, _piano.Octave))
        {
            ComputerKeys.KeySignaturePitchClass = _keypadTuning.PitchClass;
        }
        return true;
    }

    private bool WantsControlNavigation(int virtualKey)
    {
        if (LanguagePicker.IsDropDownOpen || LayoutPicker.IsDropDownOpen || MainInstrument.IsDropDownOpen || RightInstrument.IsDropDownOpen || ComputerKeys.KeySignatureDropDownOpen) return true;
        if (virtualKey is not (>= 0x21 and <= 0x28)) return false; // Page, Home, End and arrow keys.
        return ComputerKeys.KeySignatureHasFocus || MainInstrument.IsKeyboardFocusWithin || RightInstrument.IsKeyboardFocusWithin ||
            TempoSlider.IsKeyboardFocusWithin || VolumeSlider.IsKeyboardFocusWithin;
    }

    private static bool HasShortcutModifier() =>
        (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;

    private void PressMouse(object sender, MouseButtonEventArgs args)
    {
        var key = (PianoKey)sender;
        args.Handled = true;
        if (_playingTake || _closing || _mouseKey != null || !key.CaptureMouse())
        {
            return;
        }

        _mouseKey = key;
        key.Focus();
        _piano.Press(MouseSource, key.Offset);
        RefreshPlayState();
    }

    private void ReleaseMouse(object sender, MouseButtonEventArgs args)
    {
        args.Handled = true;
        EndMouse();
    }

    private void LoseMouse(object sender, MouseEventArgs args)
    {
        if (ReferenceEquals(_mouseKey, sender))
        {
            EndMouse();
        }
    }

    private void EndMouse()
    {
        PianoKey? key = _mouseKey;
        if (key == null)
        {
            return;
        }

        _mouseKey = null;
        _piano.Release(MouseSource);
        key.ReleaseMouseCapture();
        RefreshPlayState();
    }

    private void PlayDiagramPointer(PerformanceKey key, bool pressed)
    {
        var source = new InputSource(InputKind.Mouse, key.ScanCode, MultiRowKeyboard.ZoneOf(key.ScanCode));
        bool changed = pressed
            ? !_playingTake && !_closing && _piano.Press(source, ResolveDiagramOffset(key.ScanCode, key.Offset))
            : _piano.Release(source);
        if (changed) RefreshPlayState();
    }

    private async void InvokeKey(object sender, RoutedEventArgs args)
    {
        int offset = ((PianoKey)sender).Offset;
        // Diagram accessibility sources use positive scan codes; screen piano identities are negative.
        await AccessibleTap(new InputSource(InputKind.Accessibility, offset - PianoInput.HighestOffset - 1), offset);
    }

    private async void InvokeDiagramKey(PerformanceKey key) =>
        await AccessibleTap(new InputSource(InputKind.Accessibility, key.ScanCode,
            MultiRowKeyboard.ZoneOf(key.ScanCode)), ResolveDiagramOffset(key.ScanCode, key.Offset));

    private async Task AccessibleTap(InputSource source, int offset)
    {
        if (_playingTake || _closing || !IsActive || !_piano.Press(source, offset))
        {
            return;
        }

        int zone = (int)source.Zone;
        int generation = _zoneGenerations[zone];
        RefreshPlayState();
        await Task.Delay(100);
        if (generation == _zoneGenerations[zone] && _piano.Release(source))
        {
            RefreshPlayState();
        }
    }

    private static void SetInstrumentNames(ComboBox picker)
    {
        int selected = Math.Max(0, picker.SelectedIndex);
        picker.ItemsSource = new[] { UiLanguage.Get("Lang.YamahaC5"), UiLanguage.Get("Lang.Harpsichord"), UiLanguage.Get("Lang.Marimba") };
        picker.SelectedIndex = selected;
    }

    private void InstrumentChanged(object sender, SelectionChangedEventArgs args)
    {
        var picker = (ComboBox)sender;
        if (picker.SelectedIndex is < 0 or > 2) return;

        PlayingZone zone = ReferenceEquals(picker, MainInstrument) ? PlayingZone.Main : PlayingZone.Right;
        _zoneGenerations[(int)zone]++;
        if (zone == PlayingZone.Main) EndMouse();
        ComputerKeys.ReleasePointer(zone);
        _piano.ResetZone(zone);
        var instrument = (Instrument)picker.SelectedIndex;
        _recorder?.SelectInstrument(RecordingFrame, zone, instrument);
        CheckRecordingCapacity();
        _audio?.SelectInstrument(zone, instrument);

        RefreshPlayState();
    }

    private void ToggleMetronome(object sender, RoutedEventArgs args)
    {
        _metronomeOn = !_metronomeOn;
        UpdateMetronomeText();
        ApplyMetronome();
    }

    private void UpdateMetronomeText()
    {
        MetronomeToggle.Content = _metronomeOn ? UiLanguage.Get("Lang.Stop") : UiLanguage.Get("Lang.Start");
        AutomationProperties.SetName(MetronomeToggle, _metronomeOn
            ? UiLanguage.Get("Lang.StopMetronome") : UiLanguage.Get("Lang.StartMetronome"));
    }

    private void ApplyMetronome()
    {
        int bpm = (int)Math.Round(TempoSlider.Value);
        SetLiveText(TempoValue, UiLanguage.Format("Lang.TempoValue", bpm));
        _audio?.ConfigureMetronome(_metronomeOn, bpm);
    }

    private void LowerOctave(object sender, RoutedEventArgs args) => SetOctave(-1);
    private void RaiseOctave(object sender, RoutedEventArgs args) => SetOctave(1);

    private void SetOctave(int adjustment)
    {
        _piano.SetOctave(_piano.Octave + adjustment);
        _recorder?.SetOctave(RecordingFrame, _piano.Octave);
        CheckRecordingCapacity();
        _keypadTuning.FitOctave(_piano.Octave);
        RecordKeypadTuning();
        ClearLocalInput();
        RefreshLayout();
        RefreshPlayState();
    }

    private void ToggleSustainMode(object sender, RoutedEventArgs args)
    {
        _piano.SetSustainMode(_piano.Mode == SustainMode.Hold ? SustainMode.Toggle : SustainMode.Hold);
        RefreshPlayState();
    }

    private void ChangeVolume(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        VolumeValue.Text = UiLanguage.Format("Lang.VolumeValue", args.NewValue);
        _audio?.SetVolume((float)args.NewValue / 100);
    }

    private void ResetPlaying()
    {
        _piano.Reset();
        ClearLocalInput();
        RefreshPlayState();
    }

    private void ClearLocalInput()
    {
        _pendingKeySignatureTap = 0;
        for (int zone = 0; zone < _zoneGenerations.Length; zone++) _zoneGenerations[zone]++;
        ComputerKeys.ReleasePointer();
        PianoKey? key = _mouseKey;
        _mouseKey = null;
        key?.ReleaseMouseCapture();
    }

    private void ToggleSettings(object sender, RoutedEventArgs args)
    {
        bool hide = SettingsBody.Visibility == Visibility.Visible;
        if (hide) _hiddenSettingsHeight = SettingsBody.DesiredSize.Height;
        double delta = hide ? -_hiddenSettingsHeight : _hiddenSettingsHeight;
        double normalHeight = WindowState == WindowState.Normal ? ActualHeight : RestoreBounds.Height;

        if (_classic) SizeToContent = SizeToContent.Manual;
        SettingsBody.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        SettingsChevron.RenderTransform = hide ? new RotateTransform(-90) : Transform.Identity;
        SettingsButton.SetResourceReference(ToolTipProperty, hide ? "Lang.ShowSettings" : "Lang.HideSettings");
        MinHeight += delta;
        Height = Math.Max(MinHeight, normalHeight + delta);
        if (_classic)
        {
            _multiRowMinHeight += delta;
            _multiRowBounds.Height += delta;
            // The compact window fits its content, including when manually resized beforehand.
            SizeToContent = SizeToContent.Height;
        }
    }

    private void OnLayoutSelected(object sender, SelectionChangedEventArgs args)
    {
        if (LayoutPicker.SelectedIndex < 0) return;
        bool classic = LayoutPicker.SelectedIndex == 1;
        if (_classic == classic) return;
        _classic = classic;
        if (_classic)
        {
            _multiRowState = WindowState;
            _multiRowBounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            _multiRowMinHeight = MinHeight;

            // Preserve the normal key proportions without reserving room for the diagram.
            double keyAspect = Math.Clamp(Board.ActualHeight / Math.Max(1, Board.ActualWidth), 0.10, 0.16);
            double horizontalChrome = ActualWidth - Board.ActualWidth;
            WindowState = WindowState.Normal;
            Width = Math.Min(900, _multiRowBounds.Width);
            Board.Height = Math.Clamp((Width - horizontalChrome) * keyAspect, 96, 135);
            MinHeight = SettingsBody.Visibility == Visibility.Visible ? 400 : 400 - _hiddenSettingsHeight;
            SizeToContent = SizeToContent.Height;
        }
        else
        {
            SizeToContent = SizeToContent.Manual;
            Board.ClearValue(HeightProperty);
            MinHeight = _multiRowMinHeight;
            Left = _multiRowBounds.Left;
            Top = _multiRowBounds.Top;
            Width = _multiRowBounds.Width;
            Height = _multiRowBounds.Height;
            WindowState = _multiRowState;
        }

        RefreshLayout();
    }

    private string NoteLabel(int offset)
    {
        int midi = _piano.MidiNote(offset);
        return MidiLabel(midi);
    }

    private static string MidiLabel(int midi) => $"{NoteNames[midi % 12]}{midi / 12 - 1}";

    private void RefreshLayout()
    {
        if (_playingTake) return;
        ComputerPanel.Visibility = _classic ? Visibility.Collapsed : Visibility.Visible;
        ComputerKeys.UpdateNotes(_piano.Octave, NoteLabel, _keypadTuning.Semitones);
        _visibleTop = VisiblePianoTop();
        foreach (PianoKey key in _keys)
        {
            int offset = key.Offset;
            bool visible = offset <= _visibleTop;
            key.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible)
            {
                key.UpdateLabel(NoteLabel(offset), null, compact: true);
            }
        }

        SetLiveText(OctaveValue, _piano.Octave.ToString());
        RangeLabel.Text = $"{NoteLabel(PianoInput.LowestOffset)} – {NoteLabel(_visibleTop)}";
        OctaveDown.IsEnabled = _piano.Octave > PianoInput.MinOctave;
        OctaveUp.IsEnabled = _piano.Octave < PianoInput.MaxOctave;
    }

    private int VisiblePianoTop()
    {
        int top = Math.Max(PianoInput.HighestMainOffset, MultiRowKeyboard.HighestPadOffset + _keypadTuning.Semitones);
        // Keep an old high note on screen until its key and pedal have both released it.
        for (int offset = PianoInput.HighestOffset; offset > top; offset--)
        {
            if (_piano.State(offset) != NoteState.Idle)
            {
                top = offset;
                break;
            }
        }

        return PianoBoard.IsBlack(top) ? top + 1 : top;
    }

    private void RefreshPlayState()
    {
        if (_playingTake) return;
        if (_visibleTop != VisiblePianoTop()) RefreshLayout();
        ComputerKeys.UpdatePressed(_piano);
        var active = new List<string>();
        foreach (PianoKey key in _keys)
        {
            NoteState state = _piano.State(key.Offset);
            key.UpdateState(state);
            if (state != NoteState.Idle)
            {
                string label = NoteLabel(key.Offset);
                active.Add(state == NoteState.Sustained ? UiLanguage.Format("Lang.SustainedNote", label) : label);
            }
        }

        UpdatePedalText();
        PedalStatus.Foreground = (Brush)FindResource(_piano.IsSustained ? "Accent" : "Muted");
        SetLiveText(NotesStatus, ActiveNoteCount(active.Count));
        SetLiveText(ActiveNotes, active.Count == 0 ? " " : string.Join("  ·  ", active));
    }

    private void UpdateFooter()
    {
        string hint = UiLanguage.Get("Lang.PlayHint");
        string text = _audioError != null ? UiLanguage.Get("Lang.NoSound")
            : hint;
        Footer.ToolTip = _audioError;
        SetLiveText(Footer, text);
        Footer.Foreground = (Brush)FindResource(_audioError != null ? "Accent" : "Muted");
    }

    private void UpdatePedalText()
    {
        SustainButton.Content = _piano.Mode == SustainMode.Hold
            ? UiLanguage.Get("Lang.HoldSpace") : UiLanguage.Get("Lang.ToggleSpace");
        SetLiveText(PedalStatus, _piano.IsSustained ? UiLanguage.Get("Lang.PedalOn") : UiLanguage.Get("Lang.PedalOff"));
    }

    private static string ActiveNoteCount(int count) =>
        UiLanguage.Format(count == 1 ? "Lang.OneActiveNote" : "Lang.ActiveNoteCount", count);

    private void RefreshLocalizedState()
    {
        foreach (ComboBox picker in new[] { MainInstrument, RightInstrument })
        {
            // Replacing the labels must not send an instrument change to the recorder or audio engine.
            picker.SelectionChanged -= InstrumentChanged;
            SetInstrumentNames(picker);
            picker.SelectionChanged += InstrumentChanged;
        }
        UpdateMetronomeText();
        SetLiveText(TempoValue, UiLanguage.Format("Lang.TempoValue", (int)Math.Round(TempoSlider.Value)));
        VolumeValue.Text = UiLanguage.Format("Lang.VolumeValue", VolumeSlider.Value);
        RefreshTakeText();
        if (_playingTake)
        {
            UpdatePedalText();
            UpdateReplayKeyLabels();
            bool pendingRepaint = _repaintAfterTap;
            DrawPlaybackBoard(layout: true);
            _repaintAfterTap |= pendingRepaint;
        }
        else
        {
            RefreshLayout();
            RefreshPlayState();
        }
        UpdateFooter();
    }

    private static void SetLiveText(TextBlock label, string text)
    {
        if (label.Text == text)
        {
            return;
        }

        label.Text = text;
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            UIElementAutomationPeer.CreatePeerForElement(label)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
