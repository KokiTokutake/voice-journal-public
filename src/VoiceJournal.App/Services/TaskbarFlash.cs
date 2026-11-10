using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VoiceJournal.App.Services;

/// <summary>
/// Flashes the window's taskbar button until the window comes to the front (FlashWindowEx, FLASHW_ALL | FLASHW_TIMERNOFG).
/// Used to tell that a record is ready while the app is minimized or behind other windows.
/// </summary>
internal static class TaskbarFlash
{
    private const uint FlashAll = 0x3;          // FLASHW_CAPTION | FLASHW_TRAY
    private const uint FlashUntilFront = 0xC;   // FLASHW_TIMERNOFG

    public static void Start(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var info = new NativeMethods.FlashInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.FlashInfo>(),
            Window = handle,
            Flags = FlashAll | FlashUntilFront,
            Count = uint.MaxValue,
            Timeout = 0,
        };
        NativeMethods.FlashWindowEx(ref info); // the return value is the previous state, not success
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FlashWindowEx(ref FlashInfo info);
    }
}
