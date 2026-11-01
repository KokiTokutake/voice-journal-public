using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VoiceJournal.Infrastructure.Power;

/// <summary>
/// Keeps the system from sleeping on idle while recording (detail spec 3.5 of the Phase B plan). The display
/// may still turn off. Uses a power request object, so it does not depend on the calling thread, and it shows
/// up in <c>powercfg /requests</c> with the given reason. Disposing releases it.
/// </summary>
public sealed class SleepBlocker : IDisposable
{
    private SafePowerRequestHandle? _handle;

    private SleepBlocker(SafePowerRequestHandle handle) => _handle = handle;

    /// <exception cref="Win32Exception">The request could not be created or set.</exception>
    public static SleepBlocker Acquire(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        var context = new NativeMethods.ReasonContext
        {
            Version = NativeMethods.PowerRequestContextVersion,
            Flags = NativeMethods.PowerRequestContextSimpleString,
            SimpleReasonString = reason,
        };
        var handle = NativeMethods.PowerCreateRequest(ref context);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!NativeMethods.PowerSetRequest(handle, NativeMethods.PowerRequestType.SystemRequired))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return new SleepBlocker(handle);
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, null);
        if (handle is null) return;
        NativeMethods.PowerClearRequest(handle, NativeMethods.PowerRequestType.SystemRequired);
        handle.Dispose();
    }
}

internal sealed class SafePowerRequestHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePowerRequestHandle() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

internal static class NativeMethods
{
    public const uint PowerRequestContextVersion = 0;
    public const uint PowerRequestContextSimpleString = 0x1;

    public enum PowerRequestType
    {
        DisplayRequired = 0,
        SystemRequired = 1,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern SafePowerRequestHandle PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PowerSetRequest(SafePowerRequestHandle handle, PowerRequestType type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PowerClearRequest(SafePowerRequestHandle handle, PowerRequestType type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);
}
