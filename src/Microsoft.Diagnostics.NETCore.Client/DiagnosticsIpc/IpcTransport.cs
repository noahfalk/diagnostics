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
    internal abstract class IpcEndpoint
    {
        // The amount of time to wait for a stream to be available for consumption by the Connect method.
        // This should be a large timeout in order to allow the runtime instance to reconnect and provide
        // a new stream once the previous stream has started its diagnostics request and response.
        protected static readonly TimeSpan ConnectWaitTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Connects to the underlying IPC Transport and opens a read/write-able Stream
        /// </summary>
        /// <returns>A Stream for writing and reading data to and from the target .NET process</returns>
        public abstract Stream Connect();

        /// <summary>
        /// Wait for an available diagnostics connection to the runtime instance.
        /// </summary>
        /// <param name="token">The token to monitor for cancellation requests.</param>
        /// <returns>
        /// A task the completes when a diagnostics connection to the runtime instance becomes available.
        /// </returns>
        public abstract Task WaitForConnectionAsync(CancellationToken token);
    }

    internal class ServerIpcEndpoint : IpcEndpoint, IDisposable
    {
        /// <summary>
        /// Updates the endpoint with a new diagnostics stream.
        /// </summary>
        internal void SetStream(Stream stream)
        {
            ProvideStream(stream);
        }

        private readonly object _lock = new object();
        private readonly Queue<Func<Stream, bool>> _handlers = new Queue<Func<Stream, bool>>();

        private bool _disposed;
        private Stream _stream;

        /// <remarks>
        /// This will block until the diagnostic stream is provided. This block can happen if
        /// the stream is acquired previously and the runtime instance has not yet reconnected
        /// to the reversed diagnostics server.
        /// </remarks>
        public override Stream Connect()
        {
            using var streamEvent = new ManualResetEvent(false);
            Stream stream = null;
            RegisterHandler(s =>
            {
                stream = s;
                return streamEvent.Set();
            });

            if (streamEvent.WaitOne(ConnectWaitTimeout))
            {
                throw new TimeoutException();
            }

            return stream;
        }

        public override async Task WaitForConnectionAsync(CancellationToken token)
        {
            bool isConnected = false;
            do
            {
                bool ignore = false;
                TaskCompletionSource<bool> hasConnectedStreamSource = new TaskCompletionSource<bool>();
                using IDisposable _registration = token.Register(() => hasConnectedStreamSource.TrySetCanceled());
                RegisterHandler(s =>
                {
                    if (ignore)
                    {
                        return false;
                    }
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
                });
                {
                    try
                    {
                        // Wait for the handler to verify we have a connected stream
                        isConnected = await hasConnectedStreamSource.Task;
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        // Handle all exceptions except cancellation
                    }
                    finally
                    {
                        // if we exited with an exception the handler might still be registered
                        // and will run later. Let it exit quickly.
                        ignore = true;
                    }
                }
            }
            while (!isConnected);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                ProvideStream(stream: null);

                _disposed = true;
            }
        }

        protected void ProvideStream(Stream stream)
        {
            // Get the previous stream in order to dispose it later
            Stream previousStream = null;
            lock (_lock)
            {
                previousStream = _stream;
                RunStreamHandlers(stream);
            }
            // Dispose the previous stream if there was one.
            previousStream?.Dispose();
        }

        private void RunStreamHandlers(Stream stream)
        {
            Debug.Assert(Monitor.IsEntered(_lock));

            // If there are any targets waiting for a stream, provide
            // it to the first target in the queue.
            if (_handlers.Count > 0)
            {
                while (null != stream)
                {
                    Func<Stream, bool> handler = _handlers.Dequeue();
                    if (handler(stream))
                    {
                        stream = null;
                    }
                }
            }

            // Store the stream for when a handler registers later. If
            // a handler already captured the stream, this will be null, thus
            // representing that no existing stream is waiting to be consumed.
            _stream = stream;
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

        private void RegisterHandler(Func<Stream, bool> handler)
        {
            lock (_lock)
            {
                _handlers.Enqueue(handler);

                if (_stream != null)
                {
                    RunStreamHandlers(_stream);
                }
            }
        }
    }

    internal class PidIpcEndpoint : IpcEndpoint
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

        public override Stream Connect()
        {
            return ConnectStream();
        }

        public override async Task WaitForConnectionAsync(CancellationToken token)
        {
            Stream s = await ConnectStreamAsync(token);
            s.Dispose();
        }


        Stream ConnectStream()
        {
            string address = GetDefaultAddress();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var namedPipe = new NamedPipeClientStream(
                    ".", address, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Impersonation);
                namedPipe.Connect((int)ConnectTimeoutMilliseconds);
                return namedPipe;
            }
            else
            {
                var socket = new UnixDomainSocket();
                socket.Connect(Path.Combine(IpcRootPath, address));
                return new ExposedSocketNetworkStream(socket, ownsSocket: true);
            }
        }

        async Task<Stream> ConnectStreamAsync(CancellationToken token)
        {
            string address = GetDefaultAddress();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var namedPipe = new NamedPipeClientStream(
                    ".", address, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Impersonation);
                await namedPipe.ConnectAsync(token);
                return namedPipe;
            }
            else
            {
                var socket = new UnixDomainSocket();
                await socket.ConnectAsync(Path.Combine(IpcRootPath, address));
                return new ExposedSocketNetworkStream(socket, ownsSocket: true);
            }
        }

        private string GetDefaultAddress()
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

            if (!TryGetDefaultAddress(_pid, out string transportName))
            {
                throw new ServerNotAvailableException($"Process {_pid} not running compatible .NET runtime.");
            }
            return transportName;
        }

        private static bool TryGetDefaultAddress(int pid, out string defaultAddress)
        {
            defaultAddress = null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                defaultAddress = $"dotnet-diagnostic-{pid}";
            }
            else
            {
                try
                {
                    defaultAddress = Directory.GetFiles(IpcRootPath, $"dotnet-diagnostic-{pid}-*-socket") // Try best match.
                        .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                        .FirstOrDefault();
                }
                catch (InvalidOperationException)
                {
                }
            }

            return !string.IsNullOrEmpty(defaultAddress);
        }
    }
}
