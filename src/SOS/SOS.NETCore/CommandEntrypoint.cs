using Microsoft.Diagnostics.Runtime;
using SOS.Dbgeng;
using SOS.Dbgeng.Interop;
using System;
using System.Linq;
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
            DataTarget target = DataTarget.CreateFromDebuggerInterface(
                (Microsoft.Diagnostics.Runtime.Interop.IDebugClient)Marshal.GetObjectForIUnknown(debugClient));
            ClrRuntime r = target.ClrVersions[0].CreateRuntime();

            switch(commandName)
            {
                case "DumpAsync":
                    DumpAsync.Run(console, r);
                    return 0;
                default:
                    return 1;
            }
        }
    }
}
