using Microsoft.Diagnostics.DebugServices;
using SOS.Dbgeng.Interop;
using System;
using System.Collections.Generic;
using System.Text;

namespace SOS.Dbgeng
{
    class ConsoleService : IConsoleService
    {
        IDebugControl _control;
        public ConsoleService(IDebugClient client)
        {
            _control = (IDebugControl)client;
        }
        public void Write(string value)
        {
            _control.Output(DEBUG_OUTPUT.NORMAL, value);
        }

        public void WriteError(string value)
        {
            _control.Output(DEBUG_OUTPUT.ERROR, value);
        }

        public void Exit()
        {
            throw new NotImplementedException();
        }
    }
}
