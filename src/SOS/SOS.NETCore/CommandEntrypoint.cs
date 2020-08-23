using SOS.Dbgeng;
using SOS.Dbgeng.Interop;
using System;
using System.Runtime.InteropServices;

namespace SOS
{
    public static class CommandEntrypoint
    {
        public static int TryRunCommand(
            [MarshalAs(UnmanagedType.LPStr)] string commandName,
            IntPtr xClrData,
            IntPtr debugClient,
            [MarshalAs(UnmanagedType.LPStr)] string args)
        {
            IDebugClient client = (IDebugClient)Marshal.GetObjectForIUnknown(debugClient);
            ConsoleService console = new ConsoleService(client);
            console.Write("Managed code run!" + Environment.NewLine);
            return 1; // S_FALSE
        }
    }
}
