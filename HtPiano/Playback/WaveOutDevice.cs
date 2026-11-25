using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HtPiano;

internal sealed class WaveOutDevice : IDisposable
{
    private const uint CallbackEvent = 0x00050000;
    private const uint WaveMapper = uint.MaxValue;
    private const uint HeaderDone = 0x00000001;
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();
    private static readonly int FlagsOffset = Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags)).ToInt32();
    private readonly Action<string?> _report;
    private readonly SafeWaitHandle _eventHandle;
    private bool _eventRetained;
    private nint _device;

    public EventWaitHandle Completed { get; } = new(false, EventResetMode.ManualReset);
    public Buffer[] Buffers { get; }

    public WaveOutDevice(int bufferCount, int sampleCount, Action<string?> report, int sampleRate, ushort channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfZero(channels);
        ushort blockAlign = checked((ushort)(channels * sizeof(short)));
        _report = report;
        Buffers = new Buffer[bufferCount];
        _eventHandle = Completed.SafeWaitHandle;

        var format = new WaveFormat
        {
            FormatTag = 1,
            Channels = channels,
            SamplesPerSecond = (uint)sampleRate,
            BitsPerSample = 16,
            BlockAlign = blockAlign,
            BytesPerSecond = checked((uint)sampleRate * blockAlign)
        };

        try
        {
            _eventHandle.DangerousAddRef(ref _eventRetained);
            Check(waveOutOpen(out nint device, WaveMapper, ref format,
                _eventHandle.DangerousGetHandle(), 0, CallbackEvent), "Lang.Audio.WinMM.Open");
            _device = device;
            for (int index = 0; index < Buffers.Length; index++)
            {
                var buffer = new Buffer(sampleCount);
                Buffers[index] = buffer;
                Check(waveOutPrepareHeader(_device, buffer.Header, HeaderSize), "Lang.Audio.WinMM.PrepareBuffer");
                buffer.Prepared = true;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Flush() => Check(waveOutReset(_device), "Lang.Audio.WinMM.Stop");

    public void Write(Buffer buffer, short[] samples)
    {
        Marshal.Copy(samples, 0, buffer.Samples, samples.Length);
        Check(waveOutWrite(_device, buffer.Header, HeaderSize), "Lang.Audio.WinMM.Play");
        buffer.Submitted = true;
    }

    public void Dispose()
    {
        string? failure = null;
        if (_device != 0)
        {
            uint reset = waveOutReset(_device);
            if (reset != 0)
            {
                failure = UiLanguage.Format("Lang.Audio.WinMM.Stop", reset);
            }

            foreach (Buffer? buffer in Buffers)
            {
                if (buffer is not { Prepared: true })
                {
                    continue;
                }

                uint unprepare = waveOutUnprepareHeader(_device, buffer.Header, HeaderSize);
                if (unprepare == 0)
                {
                    buffer.Prepared = false;
                }
                else
                {
                    failure ??= UiLanguage.Format("Lang.Audio.WinMM.ReturnBuffer", unprepare);
                }
            }

            uint close = waveOutClose(_device);
            if (close == 0)
            {
                _device = 0;
            }
            else
            {
                failure ??= UiLanguage.Format("Lang.Audio.WinMM.Close", close);
            }
        }

        foreach (Buffer? buffer in Buffers)
        {
            if (buffer != null && (_device == 0 || !buffer.Prepared))
            {
                buffer.Free();
            }
        }

        // On a failed driver close, keep anything the driver could still access alive
        // until process exit rather than freeing memory or closing the callback handle.
        if (_device == 0 && _eventRetained)
        {
            _eventHandle.DangerousRelease();
            _eventRetained = false;
        }

        Completed.Dispose();
        if (failure != null)
        {
            _report(failure);
        }
    }

    private static void Check(uint status, string errorKey)
    {
        if (status != 0)
        {
            throw new InvalidOperationException(UiLanguage.Format(errorKey, status));
        }
    }

    internal sealed class Buffer
    {
        public nint Header { get; private set; }
        public nint Samples { get; }
        public bool Prepared { get; set; }
        public bool Submitted { get; set; }
        public bool IsAvailable => !Submitted || (Marshal.ReadInt32(Header, FlagsOffset) & HeaderDone) != 0;

        public Buffer(int sampleCount)
        {
            int bytes = sampleCount * sizeof(short);
            Header = Marshal.AllocHGlobal((int)HeaderSize + bytes);
            Samples = Header + (int)HeaderSize;
            Marshal.StructureToPtr(new WaveHeader { Data = Samples, Length = (uint)bytes }, Header, false);
        }

        public void Free()
        {
            Marshal.FreeHGlobal(Header);
            Header = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSecond, BytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public uint Length, BytesRecorded;
        public nuint User;
        public uint Flags, Loops;
        public nint Next;
        public nuint Reserved;
    }

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutOpen(out nint device, uint deviceId,
        ref WaveFormat format, nint callback, nuint instance, uint flags);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutPrepareHeader(nint device, nint header, uint size);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutWrite(nint device, nint header, uint size);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutReset(nint device);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutUnprepareHeader(nint device, nint header, uint size);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutClose(nint device);
}
