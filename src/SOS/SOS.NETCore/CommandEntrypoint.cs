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
            return 1; // S_FALSE
        }
    }
}
