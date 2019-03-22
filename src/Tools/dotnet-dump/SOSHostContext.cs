using Microsoft.Diagnostic.SnapshotAnalysis.Abstractions;
using SOS;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Text;
using System.Threading;

namespace Microsoft.Diagnostic.Tools.Dump
{
    public class SOSHostContext : ISOSHostContext
    {
        AnalyzeContext _analyzeContext;
        IConsole _console;
        SOSHost _sosHost;

        public SOSHostContext(AnalyzeContext analyzeContext, IConsole console)
        {
            _analyzeContext = analyzeContext;
            _console = console;
        }

        public SOSHost SOSHost
        {
            get
            {
                if(_sosHost == null)
                {
                    _sosHost = new SOSHost(_analyzeContext.Runtime.DataTarget.DataReader, this);
                }
                return _sosHost;
            }
        }

        public int CurrentThreadId
        {
            get { return _analyzeContext.CurrentThreadId; }
            set { _analyzeContext.CurrentThreadId = value; }
        }

        public CancellationToken CancellationToken => _analyzeContext.CancellationToken;

        public void Write(string text)
        {
            _console.Out.Write(text);
        }
    }
}
