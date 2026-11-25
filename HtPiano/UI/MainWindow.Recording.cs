using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace HtPiano;

public partial class MainWindow
{
    private readonly DispatcherTimer _takeTimer = new(DispatcherPriority.Input)
    {
        Interval = TimeSpan.FromMilliseconds(16)
    };
    private readonly bool[,] _replayNotes = new bool[2, 128];
    private readonly bool[,] _newAttacks = new bool[2, 128];
    private readonly HashSet<int> _replayKeys = [];
    private readonly HashSet<int> _keyAttacks = [];
    private readonly List<int> _soundingPitches = [];
    private bool _repaintAfterTap;
    private readonly Stopwatch _recordingClock = new();
    private TakeRecorder? _recorder;
    private PerformanceTake? _take;
    private bool _playingTake;
    private bool _audioReady;
    private int _replayOctave;
    private int _replayKeypadSemitones;
    private int _nextVisualEvent;
    private long _displayedSecond = -1;
    private UiMessage _idleTakeStatus = new("Lang.NoRecording");

    private long RecordingFrame => _recordingClock.Elapsed.Ticks * AudioFormat.SampleRate / TimeSpan.TicksPerSecond;

    private void SendPlayedNote(NoteCommand note)
    {
        _audio?.Send(note);
        _recorder?.Note(RecordingFrame, note);
        CheckRecordingCapacity();
    }

    private void RecordKeyFace(int scanCode, bool down, PlayingZone zone)
    {
        _recorder?.KeyFace(RecordingFrame, scanCode, down, zone);
        CheckRecordingCapacity();
    }

    private void RecordKeypadTuning()
    {
        _recorder?.SetKeypadTuning(RecordingFrame, _keypadTuning.Semitones);
        CheckRecordingCapacity();
    }

    private void RecordTake(object sender, RoutedEventArgs args)
    {
        if (!_audioReady || _playingTake || _recorder != null) return;

        // Do not import notes or a pedal already held before pressing Record.
        ResetPlaying();
        _recorder = new TakeRecorder((Instrument)MainInstrument.SelectedIndex,
            (Instrument)RightInstrument.SelectedIndex, _piano.Octave, _keypadTuning.Semitones);
        _recordingClock.Restart();
        _displayedSecond = -1;
        UpdateElapsed(0);
        UpdateTakeControls();
        _takeTimer.Start();
    }

    private void StopTake(object sender, RoutedEventArgs args)
    {
        if (_recorder != null) FinishRecording();
        else if (_playingTake) EndPlayback();
    }

    private void CheckRecordingCapacity()
    {
        if (_recorder?.IsFull == true)
        {
            // This can run inside a press/release notification; don't reset its input owner.
            FinishRecording(resetPlaying: false);
            SetIdleTakeStatus(new("Lang.RecordingLimit"));
        }
    }

    private void FinishRecording(bool resetPlaying = true)
    {
        if (_recorder is not { } recorder) return;

        _recordingClock.Stop();
        _recorder = null;
        PerformanceTake? finished = recorder.Finish(RecordingFrame);
        _takeTimer.Stop();
        // Stop silences any keys still held; Finish already captured their final releases.
        if (resetPlaying) ResetPlaying();
        if (finished != null)
        {
            _take = finished;
            _takeNeedsSaving = true;
        }
        if (finished == null)
            SetIdleTakeStatus(_take == null ? new("Lang.NoNotesRecorded")
                : new("Lang.PreviousTakeKept"));
        else
        {
            string duration = FormatTime(finished.DurationFrames);
            SetIdleTakeStatus(new("Lang.RecordedDuration", duration));
        }
        UpdateTakeControls();
    }

    private void PlayTake(object sender, RoutedEventArgs args) => StartTakePlayback();

    private void StartTakePlayback()
    {
        if (!_audioReady || _take == null || _audio == null || _playingTake || _recorder != null) return;

        ResetPlaying();
        Array.Clear(_replayNotes);
        Array.Clear(_newAttacks);
        _replayKeys.Clear();
        _keyAttacks.Clear();
        _repaintAfterTap = false;
        _nextVisualEvent = 0;
        _displayedSecond = -1;
        _replayOctave = _take.InitialOctave;
        _replayKeypadSemitones = _take.InitialKeypadSemitones;
        _playingTake = true;
        if (!_classic) ComputerKeys.BeginPlayback(_take.ClassicKeys);
        UpdateReplayKeyLabels();
        _audio.StartPlayback(_take);
        UpdateTakeControls();
        DrawPlaybackBoard(layout: true);
        UpdateElapsed(0);
        _takeTimer.Start();
    }

    private void EndPlayback()
    {
        _audio?.StopPlayback();
        ComputerKeys.EndPlayback();
        ComputerKeys.KeySignaturePitchClass = _keypadTuning.PitchClass;
        _playingTake = false;
        _takeTimer.Stop();
        Array.Clear(_replayNotes);
        RefreshLayout();
        RefreshPlayState();
        string duration = _take == null ? "" : FormatTime(_take.DurationFrames);
        SetIdleTakeStatus(_take == null ? new("Lang.NoRecording") : new("Lang.ReadyToPlay", duration));
        UpdateTakeControls();
    }

    private void UpdateTakeControls()
    {
        bool idle = !_playingTake && _recorder == null;
        RecordButton.IsEnabled = _audioReady && idle;
        StopTakeButton.IsEnabled = !idle;
        PlayTakeButton.IsEnabled = RecordButton.IsEnabled && _take != null;
        SaveTakeButton.IsEnabled = idle && _take != null;
        OpenTakeButton.IsEnabled = idle && _audioReady;
        LayoutPicker.IsEnabled = !_playingTake;
        ComputerPanel.IsEnabled = !_playingTake;
        Board.IsHitTestVisible = !_playingTake;
        MainInstrument.IsEnabled = RightInstrument.IsEnabled = !_playingTake;
        SustainButton.IsEnabled = MetronomeToggle.IsEnabled = TempoSlider.IsEnabled = !_playingTake;
        OctaveDown.IsEnabled = !_playingTake && _piano.Octave > PianoInput.MinOctave;
        OctaveUp.IsEnabled = !_playingTake && _piano.Octave < PianoInput.MaxOctave;
    }

    private void UpdateTakePosition(object? sender, EventArgs args)
    {
        if (_recorder != null)
        {
            UpdateElapsed(RecordingFrame);
            return;
        }

        if (!_playingTake || _take == null || _audio == null) return;
        var (frame, finished) = _audio.PlaybackProgress();
        if (finished)
        {
            EndPlayback();
            return;
        }

        // Use the driver's completed audio frames, not dispatcher ticks, as the visual clock.
        int firstEvent = _nextVisualEvent;
        int previousOctave = _replayOctave;
        int previousTuning = _replayKeypadSemitones;
        while (_nextVisualEvent < _take.Events.Count && _take.Events[_nextVisualEvent].Frame <= frame)
            ApplyVisualEvent(_take.Events[_nextVisualEvent++]);
        if (_nextVisualEvent != firstEvent || _repaintAfterTap)
        {
            if (previousOctave != _replayOctave || previousTuning != _replayKeypadSemitones)
                UpdateReplayKeyLabels();
            DrawPlaybackBoard(previousOctave != _replayOctave);
            ComputerKeys.ShowReplayKeys(_replayKeys, _keyAttacks);
            _repaintAfterTap |= !_keyAttacks.IsSubsetOf(_replayKeys);
            _keyAttacks.Clear();
            Array.Clear(_newAttacks);
        }
        UpdateElapsed(frame);
    }

    private void ApplyVisualEvent(TakeEvent change)
    {
        if (change.Kind == TakeEventKind.KeyFace)
        {
            if (change.Down)
            {
                _replayKeys.Add(change.ScanCode);
                _keyAttacks.Add(change.ScanCode);
            }
            else _replayKeys.Remove(change.ScanCode);
            return;
        }
        if (change.Kind == TakeEventKind.KeypadTuning)
        {
            _replayKeypadSemitones = change.Semitones;
            return;
        }
        if (change.Kind == TakeEventKind.Octave)
        {
            _replayOctave = change.Octave;
            return;
        }

        int zone = (int)change.Zone;
        if (change.Kind == TakeEventKind.Instrument || change.Note.Kind == NoteCommandKind.ZoneNotesOff)
        {
            Array.Clear(_replayNotes, zone * 128, 128);
            Array.Clear(_newAttacks, zone * 128, 128);
        }
        else if (change.Note.Kind == NoteCommandKind.AllNotesOff)
        {
            Array.Clear(_replayNotes);
            Array.Clear(_newAttacks);
        }
        else
        {
            bool on = change.Note.Kind == NoteCommandKind.On;
            _replayNotes[zone, change.Note.MidiNote] = on;
            if (on) _newAttacks[zone, change.Note.MidiNote] = true;
        }
    }

    private void UpdateReplayKeyLabels()
    {
        int baseNote = 12 * (_replayOctave + 1);
        ComputerKeys.KeySignaturePitchClass = (_replayKeypadSemitones + 12) % 12;
        ComputerKeys.UpdateNotes(_replayOctave, offset => MidiLabel(baseNote + offset), _replayKeypadSemitones);
    }

    private void DrawPlaybackBoard(bool layout)
    {
        int baseNote = 12 * (_replayOctave + 1);
        int top = Math.Min(PianoInput.HighestOffset, 127 - baseNote);
        _soundingPitches.Clear();
        bool changed = layout;
        _repaintAfterTap = false;
        foreach (PianoKey key in _keys)
        {
            int midi = baseNote + key.Offset;
            bool visible = key.Offset <= top;
            if (layout)
            {
                key.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (visible) key.UpdateLabel(MidiLabel(midi), null, compact: true);
            }
            bool sounding = visible && (_replayNotes[0, midi] || _replayNotes[1, midi]);
            // A complete tap can land between two UI ticks. Show its attack once.
            if (!sounding && visible && (_newAttacks[0, midi] || _newAttacks[1, midi]))
            {
                sounding = true;
                _repaintAfterTap = true;
            }
            changed |= key.ShowPlaybackState(sounding);
            if (sounding) _soundingPitches.Add(midi);
        }

        if (layout)
        {
            RangeLabel.Text = $"{MidiLabel(baseNote + PianoInput.LowestOffset)} – {MidiLabel(baseNote + top)}";
            SetLiveText(OctaveValue, _replayOctave.ToString());
        }
        if (changed)
        {
            int count = _soundingPitches.Count;
            SetLiveText(ActiveNotes, count == 0 ? " " : string.Join("  ·  ", _soundingPitches.Select(MidiLabel)));
            SetLiveText(NotesStatus, ActiveNoteCount(count));
        }
    }

    private void UpdateElapsed(long frame)
    {
        long seconds = frame / AudioFormat.SampleRate;
        if (seconds == _displayedSecond) return;
        _displayedSecond = seconds;
        RefreshTakeText();
    }

    private void SetIdleTakeStatus(UiMessage status)
    {
        _idleTakeStatus = status;
        SetLiveText(TakeStatus, status.Display);
    }

    private void RefreshTakeText()
    {
        if (!_playingTake && _recorder == null)
        {
            SetLiveText(TakeStatus, _idleTakeStatus.Display);
            return;
        }

        string elapsed = FormatTime(Math.Max(0, _displayedSecond) * AudioFormat.SampleRate);
        string text = _playingTake
            ? UiLanguage.Format("Lang.PlaybackProgress", elapsed, FormatTime(_take!.DurationFrames))
            : UiLanguage.Format("Lang.RecordingProgress", elapsed);
        SetLiveText(TakeStatus, text);
    }

    private static string FormatTime(long frame)
    {
        long seconds = frame / AudioFormat.SampleRate;
        return $"{seconds / 60}:{seconds % 60:00}";
    }
}
