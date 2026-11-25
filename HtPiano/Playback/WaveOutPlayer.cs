namespace HtPiano;

/// <summary>Owns the default Windows output device on one dedicated worker.</summary>
internal sealed class WaveOutPlayer : IDisposable
{
    // The shorter 15 ms queue broke up on the Windows output path.
    private const int BufferCount = 4;
    private const int BufferMilliseconds = 10;
    private const int BufferSamples = AudioFormat.SampleRate * BufferMilliseconds / 1000 * AudioFormat.Channels;
    private const int CommandCapacity = 512;
    private const int NotesPerZone = 128;
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Queue<AudioChange> _changes = new(CommandCapacity);
    private readonly bool[] _desiredNotes = new bool[2 * NotesPerZone];
    private readonly Instrument[] _instruments = new Instrument[2];
    private readonly Action<string?> _report;
    private readonly Func<NativePiano> _createMixer;
    private bool _metronome;
    private int _bpm = 120;
    private bool _resync;
    private bool _stopping;
    private float _volume;

    // The UI requests a new cursor for each Play. Only the worker advances a cursor;
    // its identity prevents a late notification from completing a newer take.
    private TakePlayback? _requestedPlayback;
    private long _completedFrames;

    public WaveOutPlayer(float volume, Action<string?> report, Func<NativePiano> createMixer)
    {
        _volume = volume;
        _report = report;
        _createMixer = createMixer;
        // Closing the UI requests shutdown; the worker returns native buffers.
        var worker = new Thread(Run) { Name = "HtPiano audio" };
        try
        {
            worker.Start();
        }
        catch
        {
            _wake.Dispose();
            throw;
        }
    }

    public void SetVolume(float volume) => Volatile.Write(ref _volume, volume);

    public void StartPlayback(PerformanceTake take)
    {
        ArgumentNullException.ThrowIfNull(take);
        var playback = new TakePlayback(take);
        lock (_gate)
        {
            if (_stopping) return;
            _completedFrames = 0;
            ChangeTransport(playback);
        }
    }

    public void StopPlayback()
    {
        lock (_gate)
        {
            if (_requestedPlayback == null || _stopping) return;
            _completedFrames = 0;
            ChangeTransport(null);
        }
    }

    public (long Frames, bool Finished) PlaybackProgress()
    {
        lock (_gate)
        {
            return (_completedFrames, _requestedPlayback == null);
        }
    }

    // Caller holds _gate. TakeChanges reconstructs live settings after a stop.
    private void ChangeTransport(TakePlayback? playback)
    {
        _requestedPlayback = playback;
        _changes.Clear();
        Array.Clear(_desiredNotes);
        _resync = true;
        _wake.Set();
    }

    public void Send(NoteCommand command)
    {
        lock (_gate)
        {
            if (_stopping || _requestedPlayback != null) return;
            if (command.Kind == NoteCommandKind.AllNotesOff)
            {
                Array.Clear(_desiredNotes);
                _resync = true;
            }
            else if (command.Kind == NoteCommandKind.ZoneNotesOff)
            {
                ClearDesiredZone(command.Zone);
            }
            else
            {
                _desiredNotes[(int)command.Zone * NotesPerZone + command.MidiNote] =
                    command.Kind == NoteCommandKind.On;
            }

            Enqueue(new AudioChange(ChangeKind.Note, Note: command));
        }
    }

    public void SelectInstrument(PlayingZone zone, Instrument instrument)
    {
        lock (_gate)
        {
            if (_stopping || _instruments[(int)zone] == instrument) return;
            _instruments[(int)zone] = instrument;
            ClearDesiredZone(zone);
            if (_requestedPlayback == null)
            {
                Enqueue(new AudioChange(ChangeKind.Instrument, Zone: zone, Instrument: instrument));
            }
        }
    }

    public void ConfigureMetronome(bool enabled, int bpm)
    {
        lock (_gate)
        {
            if (_stopping || (_metronome == enabled && _bpm == bpm)) return;
            _metronome = enabled;
            _bpm = bpm;
            if (_requestedPlayback == null)
            {
                Enqueue(new AudioChange(ChangeKind.Metronome, MetroEnabled: enabled, Bpm: bpm));
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_stopping)
            {
                _stopping = true;
                _wake.Set();
            }
        }
    }

    private void ClearDesiredZone(PlayingZone zone) =>
        Array.Clear(_desiredNotes, (int)zone * NotesPerZone, NotesPerZone);

    // Called with the mailbox lock held; the current desired state repairs any overflow.
    private void Enqueue(AudioChange change)
    {
        if (_changes.Count == CommandCapacity) _resync = true;
        if (!_resync) _changes.Enqueue(change);
        _wake.Set();
    }

    private void Run()
    {
        try
        {
            using NativePiano mixer = _createMixer();
            using var device = new WaveOutDevice(BufferCount, BufferSamples, _report,
                AudioFormat.SampleRate, AudioFormat.Channels);
            var pending = new AudioChange[CommandCapacity];
            var samples = new short[BufferSamples];
            WaitHandle[] waits = [_wake, device.Completed];
            bool starting = true;
            TakePlayback? playback = null;
            var bufferEnds = new long[BufferCount];
            long completed = 0;

            while (TakeChanges(pending, out int count, out bool resync, out TakePlayback? requested))
            {
                if (!ReferenceEquals(playback, requested))
                {
                    if (requested != null)
                    {
                        device.Flush();
                        mixer.ClearImmediately();
                    }
                    else
                    {
                        // Discarding queued PCM cuts a held note at an arbitrary phase.
                        // Let the queue drain in order and follow it with a short release.
                        mixer.Apply(new NoteCommand(NoteCommandKind.AllNotesOff));
                    }
                    Array.Clear(bufferEnds);
                    completed = 0;
                    playback = requested;
                    if (playback != null)
                    {
                        mixer.ConfigureMetronome(false, 120);
                    }
                }
                else if (resync)
                {
                    // Leave a running click alone when focus changes; at most one queued buffer group remains.
                    if (mixer.MetronomePlaying)
                    {
                        mixer.Apply(new NoteCommand(NoteCommandKind.AllNotesOff));
                    }
                    else
                    {
                        device.Flush();
                        mixer.ClearImmediately();
                    }
                }

                for (int i = 0; i < count; i++)
                {
                    AudioChange change = pending[i];
                    switch (change.Kind)
                    {
                        case ChangeKind.Note:
                            mixer.Apply(change.Note);
                            break;
                        case ChangeKind.Instrument:
                            mixer.SelectInstrument(change.Zone, change.Instrument);
                            break;
                        case ChangeKind.Metronome:
                            mixer.ConfigureMetronome(change.MetroEnabled, change.Bpm);
                            break;
                    }
                }

                // Clear before scanning headers so completions during the scan aren't lost.
                device.Completed.Reset();
                for (int i = 0; i < device.Buffers.Length; i++)
                {
                    WaveOutDevice.Buffer buffer = device.Buffers[i];
                    if (!buffer.IsAvailable) continue;

                    float volume = Volatile.Read(ref _volume);
                    if (playback == null)
                    {
                        mixer.Render(samples, volume);
                    }
                    else
                    {
                        // This header has returned from the driver. Its end is a better
                        // UI clock than the render cursor, which leads by up to four buffers.
                        completed = Math.Max(completed, bufferEnds[i]);
                        if (playback.Finished)
                        {
                            Array.Clear(samples);
                            bufferEnds[i] = 0;
                        }
                        else
                        {
                            playback.Render(mixer, samples, volume);
                            bufferEnds[i] = playback.PositionFrames;
                        }
                    }

                    device.Write(buffer, samples);
                }
                if (playback != null)
                {
                    ReportPlayback(playback, completed);
                }

                if (starting)
                {
                    _report(null);
                    starting = false;
                }

                WaitHandle.WaitAny(waits, BufferMilliseconds);
            }
        }
        catch (Exception error)
        {
            _report(error.Message);
        }
        finally
        {
            lock (_gate)
            {
                _stopping = true;
                _requestedPlayback = null;
                _wake.Dispose();
            }
        }
    }

    private void ReportPlayback(TakePlayback playback, long completed)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_requestedPlayback, playback)) return;
            _completedFrames = completed;
            if (playback.Finished && completed >= playback.PositionFrames)
            {
                ChangeTransport(null);
            }
        }
    }

    private bool TakeChanges(AudioChange[] pending, out int count, out bool resync, out TakePlayback? playback)
    {
        lock (_gate)
        {
            playback = _requestedPlayback;
            resync = _resync;
            count = 0;
            if (_stopping) return false;
            if (playback != null)
            {
                _resync = false;
                return true;
            }
            if (resync)
            {
                for (int zone = 0; zone < _instruments.Length; zone++)
                {
                    pending[count++] = new AudioChange(ChangeKind.Instrument,
                        Zone: (PlayingZone)zone, Instrument: _instruments[zone]);
                }
                pending[count++] = new AudioChange(ChangeKind.Metronome, MetroEnabled: _metronome, Bpm: _bpm);
                for (int note = 0; note < _desiredNotes.Length; note++)
                {
                    if (_desiredNotes[note])
                    {
                        pending[count++] = new AudioChange(ChangeKind.Note, new NoteCommand(
                            NoteCommandKind.On, note % NotesPerZone, (PlayingZone)(note / NotesPerZone)));
                    }
                }
            }
            else
            {
                count = _changes.Count;
                _changes.CopyTo(pending, 0);
            }

            _changes.Clear();
            _resync = false;
            return true;
        }
    }

    private enum ChangeKind { Note, Instrument, Metronome }

    private readonly record struct AudioChange(ChangeKind Kind, NoteCommand Note = default,
        PlayingZone Zone = default, Instrument Instrument = default, bool MetroEnabled = false, int Bpm = 120);
}
