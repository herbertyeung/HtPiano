using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace HtPiano;

internal readonly record struct PhysicalKey(int ScanCode, int VirtualKey, bool Down, bool Repeat, bool SystemKey)
{
    public static bool TryRead(MSG message, out PhysicalKey key)
    {
        const int keyDown = 0x100;
        const int keyUp = 0x101;
        const int systemDown = 0x104;
        const int systemUp = 0x105;
        if (message.message is not (keyDown or keyUp or systemDown or systemUp))
        {
            key = default;
            return false;
        }

        long flags = message.lParam.ToInt64();
        int scanCode = (int)((flags >> 16) & 0x1FF);
        int virtualKey = (int)message.wParam;
        const int numLock = 0x90;
        if (virtualKey == numLock)
        {
            scanCode = MultiRowKeyboard.NumLockScanCode;
        }
        else if (scanCode == MultiRowKeyboard.NumLockScanCode)
        {
            // Pause can share this scan byte; only VK_NUMLOCK plays the keypad note.
            key = default;
            return false;
        }

        key = new PhysicalKey(
            scanCode, virtualKey,
            message.message is keyDown or systemDown, (flags & (1L << 30)) != 0,
            message.message is systemDown or systemUp);
        return true;
    }

    public static string Label(int scanCode)
    {
        var text = new StringBuilder(64);
        return GetKeyNameTextW(scanCode << 16, text, text.Capacity) > 0 ? text.ToString() : UiLanguage.Format("Lang.UnknownKey", scanCode);
    }

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameTextW(int flags, StringBuilder text, int size);
}
