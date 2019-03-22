// --------------------------------------------------------------------
// 
// Copyright (c) Microsoft Corporation.  All rights reserved.
// 
// --------------------------------------------------------------------
using Microsoft.Diagnostics.Runtime;
using System;
using System.Threading;

namespace Microsoft.Diagnostic.SnapshotAnalysis.Abstractions
{
    /// <summary>
    /// The the common context for analyze commands
    /// </summary>
    public class AnalyzeContext
    {
        private ClrRuntime _runtime;

        public AnalyzeContext(DataTarget target)
        {
            Target = target;
        }

        /// <summary>
        /// ClrMD data target
        /// </summary>
        public DataTarget Target { get; }

        /// <summary>
        /// ClrMD runtime info
        /// </summary>
        public ClrRuntime Runtime
        {
            get 
            {
                if (_runtime == null)
                {
                    if (Target.ClrVersions.Count != 1) {
                        throw new InvalidOperationException("More or less than 1 CLR version is present");
                    }
                    _runtime = Target.ClrVersions[0].CreateRuntime();
                }
                return _runtime;
            }
        }

        /// <summary>
        /// Delegate to invoke to exit repl
        /// </summary>
        public Action Exit { get; }

        /// <summary>
        /// Current OS thread Id
        /// </summary>
        public int CurrentThreadId { get; set; }

        /// <summary>
        /// Cancellation token for current command
        /// </summary>
        public CancellationToken CancellationToken { get; set; }
    }
}