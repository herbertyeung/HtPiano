using System.IO;
using System.Runtime.InteropServices;

namespace HtPiano;

[StructLayout(LayoutKind.Sequential)]
internal readonly struct PianoEvent
{
    public readonly ulong Frame, Sequence, NoteId;
    public readonly uint Type, Zone, MidiNote, Velocity, Value, Value2;
}

internal static class NativeMethods
{
    private const string Library = "htpiano.dll";
    private static readonly Lazy<nint> LocalSdk = new(() =>
        NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "native", Library)));

    static NativeMethods()
    {
        // Resolve only our SDK from the packaged native directory.
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly,
            (name, _, _) => name == Library ? LocalSdk.Value : 0);
    }

    internal static void VerifyAbi()
    {
        uint version = hp_abi_version();
        if (version != 1 || Marshal.SizeOf<PianoEvent>() != 48)
            throw new InvalidOperationException(UiLanguage.Format("Lang.Audio.AbiMismatch", version));
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void EventCallback(nint user, nint events, uint count);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint hp_abi_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int hp_create([MarshalAs(UnmanagedType.LPUTF8Str)] string bankPath, out nint session);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int hp_destroy(nint session);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int hp_subscribe(nint session, EventCallback callback, nint user);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int hp_unsubscribe(nint session);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int hp_note_on(nint session, uint zone, uint midiNote, uint velocity, out ulong noteId);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int hp_note_off(nint session, ulong noteId);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int hp_set_tempo(nint session, uint milliBpm);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int hp_set_instrument(nint session, uint zone, uint instrument);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int hp_all_off(nint session);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int hp_render_i16(nint session, ref short firstStereoSample, uint frames);
}
