// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Diagnostics.NETCore.Client
{
    /// <summary>
    /// An endpoint used to acquire the diagnostics stream for a runtime instance.
    /// </summary>
    internal interface IIpcEndpoint
    {
        /// <summary>
        /// Wait for an available diagnostics connection to the runtime instance.
        /// </summary>
        /// <param name="token">The token to monitor for cancellation requests.</param>
        /// <returns>
        /// A task the completes when a diagnostics connection to the runtime instance becomes available.
        /// </returns>
        Task WaitForConnectionAsync(CancellationToken token);

        /// <summary>
        /// Connects to the underlying IPC Transport and opens a read/write-able Stream
        /// </summary>
        /// <returns>A Stream for writing and reading data to and from the target .NET process</returns>
        Stream Connect();
    }

    internal abstract class BaseIpcEndpoint : IIpcEndpoint
    {
        // The amount of time to wait for a stream to be available for consumption by the Connect method.
        // This should be a large timeout in order to allow the runtime instance to reconnect and provide
        // a new stream once the previous stream has started its diagnostics request and response.
        private static readonly TimeSpan ConnectWaitTimeout = TimeSpan.FromSeconds(30);
        // The amount of time to wait for exclusive "lock" on the stream field. This should be relatively
        // short since modification of the stream field so not take a significant amount of time.
        private static readonly TimeSpan StreamSemaphoreWaitTimeout = TimeSpan.FromSeconds(5);
        // The amount of time to wait before testing the stream again after previously
        // testing the stream within the WaitForConnectionAsync method.
        private static readonly TimeSpan WaitForConnectionInterval = TimeSpan.FromMilliseconds(20);

        private readonly SemaphoreSlim _streamSemaphore = new SemaphoreSlim(1);
        private readonly Queue<StreamTarget> _targets = new Queue<StreamTarget>();

        private bool _disposed;
        private Stream _stream;

        /// <inheritdoc cref="IIpcEndpoint.Connect"/>
        /// <remarks>
        /// This will block until the diagnostic stream is provided. This block can happen if
        /// the stream is acquired previously and the runtime instance has not yet reconnected
        /// to the reversed diagnostics server.
        /// </remarks>
        public Stream Connect()
        {
            using var streamEvent = new ManualResetEvent(false);
            using var target = new StreamTarget(_ => streamEvent.Set());

            RegisterTarget(target);

            streamEvent.WaitOne(ConnectWaitTimeout);

            return target.Stream;
        }

        /// <inheritdoc cref="IIpcEndpoint.WaitForConnectionAsync(CancellationToken)"/>
        public async Task WaitForConnectionAsync(CancellationToken token)
        {
            bool isConnected = false;
            do
            {
                TaskCompletionSource<bool> hasConnectedStreamSource = new TaskCompletionSource<bool>();
                using IDisposable _registration = token.Register(() => hasConnectedStreamSource.TrySetCanceled());
                using (var target = new StreamTarget(s =>
                {
                    bool isConnected = TestStream(s);
                    if (isConnected)
                    {
                        s.Dispose();
                    }
                    hasConnectedStreamSource.TrySetResult(isConnected);
                    // if the stream wasn't connected we accept it so that we can dispose of it
                    // if the stream was connected then we don't accept it to keep it available
                    // for later consumers
                    return !isConnected;
                }))
                {
                    try
                    {
                        await RegisterTargetAsync(target, token);

                        // Wait for a stream to be provided by the target
                        isConnected = await hasConnectedStreamSource.Task;
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        // Handle all exceptions except cancellation
                    }
                }

                // If the stream is not connected, wait briefly to allow
                // the runtime instance to possibly repopulate a new connection stream.
                if (!isConnected)
                {
                    await Task.Delay(WaitForConnectionInterval, token);
                }
            }
            while (!isConnected);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                ProvideStream(stream: null);

                _streamSemaphore.Dispose();

                _disposed = true;
            }
        }

        protected void ProvideStream(Stream stream)
        {
            if (!_streamSemaphore.Wait(StreamSemaphoreWaitTimeout))
            {
                throw new TimeoutException();
            }

            ProvideStreamReleaseSemaphore(stream);
        }

        private void ProvideStreamReleaseSemaphore(Stream stream)
        {
            BindStreamToTarget(stream);
        }

        private void BindStreamToTarget(Stream stream)
        {
            // Get the previous stream in order to dispose it later
            Stream previousStream = stream != _stream ? _stream : null;
            try
            {
                // If there are any targets waiting for a stream, provide
                // it to the first target in the queue.
                if (_targets.Count > 0)
                {
                    while (null != stream)
                    {
                        StreamTarget target = _targets.Dequeue();
                        if (target.SetStream(stream))
                        {
                            stream = null;
                        }
                        else
                        {
                            // The target didn't accept the stream; this could be
                            // due to:
                            // 1. the thread that registered the target no longer
                            // needing the stream (e.g. it was async awaiting and
                            // was cancelled).
                            // 2. the target only wanted to test that the stream
                            // was available but didn't need to use it.
                            // Dispose the target to release any resources it may
                            // have.
                            target.Dispose();
                        }
                    }
                }

                // Store the stream for when a target registers for the stream. If
                // a target was already provided the stream, this will be null, thus
                // representing that no existing stream is waiting to be consumed.
                _stream = stream;

                // Dispose the previous stream if there was one.
                previousStream?.Dispose();
            }
            finally
            {
                _streamSemaphore.Release();
            }
        }

        protected virtual Stream RefreshStream()
        {
            return null;
        }

        private bool TestStream(Stream stream)
        {
            if (null == stream)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (stream is ExposedSocketNetworkStream networkStream)
            {
                // Update Connected state of socket by sending non-blocking zero-byte data.
                Socket socket = networkStream.Socket;
                bool blocking = socket.Blocking;
                try
                {
                    socket.Blocking = false;
                    socket.Send(Array.Empty<byte>(), 0, SocketFlags.None);
                }
                catch (Exception)
                {
                }
                finally
                {
                    socket.Blocking = blocking;
                }
                return socket.Connected;
            }
            else if (stream is PipeStream pipeStream)
            {
                Debug.Assert(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Pipe stream should only be used on Windows.");

                // PeekNamedPipe will return false if the pipe is disconnected/broken.
                return NativeMethods.PeekNamedPipe(
                    pipeStream.SafePipeHandle,
                    null,
                    0,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);
            }

            return false;
        }

        private void RegisterTarget(StreamTarget target)
        {
            if (!_streamSemaphore.Wait(StreamSemaphoreWaitTimeout))
            {
                throw new TimeoutException();
            }

            RegisterTargetReleaseSemaphore(target);
        }

        private async Task RegisterTargetAsync(StreamTarget target, CancellationToken token)
        {
            if (!(await _streamSemaphore.WaitAsync(StreamSemaphoreWaitTimeout, token)))
            {
                throw new TimeoutException();
            }

            RegisterTargetReleaseSemaphore(target);
        }

        private void RegisterTargetReleaseSemaphore(StreamTarget target)
        {
            try
            {
                // Allow transport specific implementation to refresh
                // the stream before possibly consuming it.
                if (null == _stream)
                {
                    _stream = RefreshStream();
                }
                _targets.Enqueue(target);

                if (_stream != null)
                {
                    BindStreamToTarget(_stream);
                }
            }
            finally
            {
                _streamSemaphore.Release();
            }
        }

        /// <summary>
        /// Base class for providing streams to callers.
        /// </summary>
        private class StreamTarget : IDisposable
        {
            private bool _isDisposed;
            private Func<Stream, bool> _acceptStreamHandler;

            public StreamTarget(Func<Stream, bool> acceptStreamHandler)
            {
                _acceptStreamHandler = acceptStreamHandler;
            }

            public void Dispose()
            {
                if (!_isDisposed)
                {
                    _isDisposed = true;
                }
            }

            public bool SetStream(Stream stream)
            {
                if (_isDisposed)
                {
                    return false;
                }

                Stream = stream;

                bool acceptedStream = AcceptStream();

                if (!acceptedStream)
                {
                    Stream = null;
                }

                return acceptedStream;
            }

            protected bool AcceptStream() => _acceptStreamHandler(Stream);

            public Stream Stream { get; private set; }
        }
    }

    internal class ServerIpcEndpoint : BaseIpcEndpoint, IIpcEndpoint, IDisposable
    {
        /// <summary>
        /// Updates the endpoint with a new diagnostics stream.
        /// </summary>
        internal void SetStream(Stream stream)
        {
            ProvideStream(stream);
        }
    }

    internal class PidIpcEndpoint : BaseIpcEndpoint, IIpcEndpoint
    {
        private static double ConnectTimeoutMilliseconds { get; } = TimeSpan.FromSeconds(3).TotalMilliseconds;
        public static string IpcRootPath { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"\\.\pipe\" : Path.GetTempPath();
        public static string DiagnosticsPortPattern { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"^dotnet-diagnostic-(\d+)$" : @"^dotnet-diagnostic-(\d+)-(\d+)-socket$";

        private int _pid;

        /// <summary>
        /// Creates a reference to a .NET process's IPC Transport
        /// using the default rules for a given pid
        /// </summary>
        /// <param name="pid">The pid of the target process</param>
        /// <returns>A reference to the IPC Transport</returns>
        public PidIpcEndpoint(int pid)
        {
            _pid = pid;
        }

        protected override Stream RefreshStream()
        {
            try
            {
                var process = Process.GetProcessById(_pid);
            }
            catch (ArgumentException)
            {
                throw new ServerNotAvailableException($"Process {_pid} is not running.");
            }
            catch (InvalidOperationException)
            {
                throw new ServerNotAvailableException($"Process {_pid} seems to be elevated.");
            }

            if (!TryGetTransportName(_pid, out string transportName))
            {
                throw new ServerNotAvailableException($"Process {_pid} not running compatible .NET runtime.");
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var namedPipe = new NamedPipeClientStream(
                    ".", transportName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Impersonation);
                namedPipe.Connect((int)ConnectTimeoutMilliseconds);
                return namedPipe;
            }
            else
            {
                var socket = new UnixDomainSocket();
                socket.Connect(Path.Combine(IpcRootPath, transportName));
                return new ExposedSocketNetworkStream(socket, ownsSocket: true);
            }
        }

        private static bool TryGetTransportName(int pid, out string transportName)
        {
            transportName = null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                transportName = $"dotnet-diagnostic-{pid}";
            }
            else
            {
                try
                {
                    transportName = Directory.GetFiles(IpcRootPath, $"dotnet-diagnostic-{pid}-*-socket") // Try best match.
                        .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                        .FirstOrDefault();
                }
                catch (InvalidOperationException)
                {
                }
            }

            return !string.IsNullOrEmpty(transportName);
        }
    }
}
