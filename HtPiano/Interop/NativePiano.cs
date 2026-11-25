using System.Runtime.InteropServices;

namespace HtPiano;

/// <summary>Native instrument mixer owned by the WaveOutPlayer worker.</summary>
internal sealed class NativePiano : IDisposable
{
    private const float NativeVolume = .65f;
    private static readonly int EventSize = Marshal.SizeOf<PianoEvent>();
    private readonly ulong[][] _noteIds = [new ulong[128], new ulong[128]];
    private readonly Instrument[] _instruments = new Instrument[2];
    private readonly Metronome _metronome = new();
    private readonly Action<PianoEvent[]>? _onEvents;
    private volatile Exception? _callbackFailure;
    private float _volume = NativeVolume;
    private short _lastLeft, _lastRight;
    private short _cutLeft, _cutRight;
    private int _cutLength, _cutRemaining;
    private nint _session;
    private GCHandle _callbackRoot;
    private bool _subscribed;

    // onEvents runs on the native dispatcher. It must not wait for the UI or the audio worker.
    public NativePiano(string bankPath, Action<PianoEvent[]>? onEvents = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bankPath);
        _onEvents = onEvents;
        NativeMethods.VerifyAbi();
        Check(NativeMethods.hp_create(bankPath, out _session), "hp_create");
        try
        {
            if (onEvents != null)
            {
                NativeMethods.EventCallback callback = ReceiveEvents;
                _callbackRoot = GCHandle.Alloc(callback);
                Check(NativeMethods.hp_subscribe(_session, callback, 0), "hp_subscribe");
                _subscribed = true;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool MetronomePlaying => _metronome.Enabled;

    public void ConfigureMetronome(bool enabled, int bpm)
    {
        int tempo = Math.Clamp(bpm, Metronome.MinTempo, Metronome.MaxTempo);
        Check(NativeMethods.hp_set_tempo(_session, (uint)tempo * 1000), "hp_set_tempo");
        _metronome.Configure(enabled, tempo);
    }

    public void SelectInstrument(PlayingZone zone, Instrument instrument)
    {
        int index = (int)zone;
        if (_instruments[index] == instrument) return;
        Check(NativeMethods.hp_set_instrument(_session, (uint)zone, (uint)instrument), "hp_set_instrument");
        // Switching instruments closes the SDK gates for this zone, not the opposite keyboard.
        Array.Clear(_noteIds[index]);
        _instruments[index] = instrument;
    }

    public void Apply(NoteCommand note)
    {
        switch (note.Kind)
        {
            case NoteCommandKind.AllNotesOff:
                ResetNotes();
                // Unflushed device buffers still end at the previous stereo sample.
                // Keep repeat cuts within the original fade rather than restarting it.
                _cutLength = _cutRemaining > 0 ? _cutRemaining : AudioFormat.CutFadeFrames;
                _cutRemaining = _lastLeft != 0 || _lastRight != 0 ? _cutLength : 0;
                _cutLeft = _lastLeft;
                _cutRight = _lastRight;
                break;
            case NoteCommandKind.ZoneNotesOff:
                ulong[] zoneIds = _noteIds[(int)note.Zone];
                for (int pitch = 0; pitch < zoneIds.Length; pitch++)
                    Release(ref zoneIds[pitch]);
                break;
            case NoteCommandKind.On:
                // PianoInput can retrigger a sustained pitch before sending its effective note-off.
                ref ulong gate = ref _noteIds[(int)note.Zone][note.MidiNote];
                Release(ref gate);
                Check(NativeMethods.hp_note_on(_session, (uint)note.Zone, (uint)note.MidiNote, 127, out gate),
                    "hp_note_on");
                break;
            case NoteCommandKind.Off:
                Release(ref _noteIds[(int)note.Zone][note.MidiNote]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(note));
        }
    }

    private void Release(ref ulong id)
    {
        if (id == 0) return;
        Check(NativeMethods.hp_note_off(_session, id), "hp_note_off");
        id = 0;
    }

    public void ClearImmediately()
    {
        ResetNotes();
        _cutRemaining = 0;
        _lastLeft = _lastRight = 0;
    }

    private void ResetNotes()
    {
        Check(NativeMethods.hp_all_off(_session), "hp_all_off");
        foreach (ulong[] zoneIds in _noteIds) Array.Clear(zoneIds);
    }

    public void Render(Span<short> stereo, float volume)
    {
        if (_callbackFailure is { } error)
            throw new InvalidOperationException(UiLanguage.Get("Lang.Audio.EventDeliveryFailed"), error);
        if (stereo.Length % 2 != 0)
            throw new ArgumentException(UiLanguage.Get("Lang.Audio.IncompleteStereoFrame"), nameof(stereo));
        if (stereo.IsEmpty) return;

        Check(NativeMethods.hp_render_i16(_session, ref MemoryMarshal.GetReference(stereo),
            (uint)(stereo.Length / 2)), "hp_render_i16");
        // Crossfade only the instrument signal: an ongoing metronome beat must not be cut.
        for (int i = 0; _cutRemaining > 0 && i < stereo.Length; i += 2)
        {
            float oldWeight = (float)_cutRemaining / _cutLength;
            _cutRemaining--;
            stereo[i] = ToSample(_cutLeft * oldWeight + stereo[i] * (1 - oldWeight));
            stereo[i + 1] = ToSample(_cutRight * oldWeight + stereo[i + 1] * (1 - oldWeight));
        }
        _lastLeft = stereo[^2];
        _lastRight = stereo[^1];

        float target = Math.Clamp(volume, 0f, 1f);
        if (target == NativeVolume && _volume == target && !_metronome.Enabled) return;
        for (int frame = 0; frame < stereo.Length; frame += 2)
        {
            _volume += (target - _volume) * .008f;
            if (MathF.Abs(target - _volume) < .00001f) _volume = target;
            float gain = _volume / NativeVolume;
            float click = _metronome.NextFrame() * _volume * short.MaxValue;
            // Only reserve headroom for gain or click energy added after the native limiter.
            float headroom = Math.Min(short.MaxValue * .18f, short.MaxValue * Math.Max(0f, gain - 1f) + MathF.Abs(click));
            stereo[frame] = MixOutput(stereo[frame] * gain + click, headroom);
            stereo[frame + 1] = MixOutput(stereo[frame + 1] * gain + click, headroom);
        }
    }

    private static short MixOutput(float sample, float headroom)
    {
        float knee = short.MaxValue - headroom;
        float excess = MathF.Abs(sample) - knee;
        if (headroom > 0 && excess > 0)
            sample = MathF.CopySign(knee + headroom * excess / (headroom + excess), sample);
        return ToSample(sample);
    }

    private static short ToSample(float sample) =>
        (short)Math.Clamp(MathF.Round(sample), short.MinValue, short.MaxValue);

    private void ReceiveEvents(nint user, nint events, uint count)
    {
        try
        {
            var batch = new PianoEvent[checked((int)count)];
            for (int i = 0; i < batch.Length; i++)
                batch[i] = Marshal.PtrToStructure<PianoEvent>(events + i * EventSize);
            _onEvents!(batch);
        }
        catch (Exception error)
        {
            // The render worker reports the error; never unwind through the C callback.
            _callbackFailure = error;
        }
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            // The SDK waits for in-flight callbacks before we free their delegate.
            Check(NativeMethods.hp_unsubscribe(_session), "hp_unsubscribe");
            _subscribed = false;
        }
        if (_callbackRoot.IsAllocated) _callbackRoot.Free();
        if (_session != 0)
        {
            Check(NativeMethods.hp_destroy(_session), "hp_destroy");
            _session = 0;
        }
    }

    private static void Check(int status, string operation)
    {
        if (status != 0)
            throw new InvalidOperationException(UiLanguage.Format("Lang.Audio.NativeFailure", operation, status));
    }
}
