// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Diagnostics.NETCore.Client
{
    /// <summary>
    /// Establishes server endpoint for processes to connect when configured to provide diagnostics connection is reverse mode.
    /// </summary>
    internal sealed class ReversedDiagnosticsServer : IDisposable
    {
        // returns true if the handler is complete and should be removed from the list
        delegate bool StreamHandler(Guid runtimeId, ref Stream stream);

        // The amount of time to allow parsing of the advertise data before cancelling. This allows the server to
        // remain responsive in case the advertise data is incomplete and the stream is not closed.
        private static readonly TimeSpan ParseAdvertiseTimeout = TimeSpan.FromSeconds(1);

        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly ConcurrentDictionary<Guid, ServerIpcEndpoint> _endpoints = new ConcurrentDictionary<Guid, ServerIpcEndpoint>();
        private readonly IpcServerTransport _transport;
        private readonly object _lock = new object();
        private readonly List<StreamHandler> _handlers = new List<StreamHandler>();
        private readonly Dictionary<Guid, Stream> _cachedStreams = new Dictionary<Guid, Stream>();

        private bool _disposed = false;

        /// <summary>
        /// Constructs the <see cref="ReversedDiagnosticsServer"/> instance with an endpoint bound
        /// to the location specified by <paramref name="transportPath"/>.
        /// </summary>
        /// <param name="transportPath">
        /// The path of the server endpoint.
        /// On Windows, this can be a full pipe path or the name without the "\\.\pipe\" prefix.
        /// On all other systems, this must be the full file path of the socket.
        /// </param>
        public ReversedDiagnosticsServer(string transportPath)
            : this(transportPath, MaxAllowedConnections)
        {
        }

        /// <summary>
        /// Constructs the <see cref="ReversedDiagnosticsServer"/> instance with an endpoint bound
        /// to the location specified by <paramref name="transportPath"/>.
        /// </summary>
        /// <param name="transportPath">
        /// The path of the server endpoint.
        /// On Windows, this can be a full pipe path or the name without the "\\.\pipe\" prefix.
        /// On all other systems, this must be the full file path of the socket.
        /// </param>
        /// <param name="maxConnections">The maximum number of connections the server will support.</param>
        public ReversedDiagnosticsServer(string transportPath, int maxConnections)
        {
            _transport = IpcServerTransport.Create(transportPath, maxConnections);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cancellation.Cancel();

                IEnumerable<ServerIpcEndpoint> endpoints = _endpoints.Values;
                _endpoints.Clear();

                lock(_lock)
                {
                    foreach(Stream s in _cachedStreams.Values)
                    {
                        s.Dispose();
                    }
                    _cachedStreams.Clear();

                    //TODO: do we want to cancel the outstanding handlers in some way?
                }

                _transport.Dispose();

                _cancellation.Dispose();

                _disposed = true;
            }
        }

        /// <summary>
        /// Provides endpoint information when a new runtime instance connects to the server.
        /// </summary>
        /// <param name="token">The token to monitor for cancellation requests.</param>
        /// <returns>A <see cref="IpcEndpointInfo"/> that contains information about the new runtime instance that connected.</returns>
        /// <remarks>
        /// This will only provide connection information on the first time a runtime connects to the server. Subsequent
        /// reconects not generate a notification. If a endpoint is removed
        /// using <see cref="RemoveConnection(Guid)"/> and the same runtime instance reconnects afte this call, then a
        /// new <see cref="IpcEndpointInfo"/> will be produced.
        /// </remarks>
        public async Task<IpcEndpointInfo> AcceptAsync(CancellationToken token)
        {
            VerifyNotDisposed();

            using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(token, _cancellation.Token);

            while(true)
            {
                Stream stream = null;
                IpcAdvertise advertise = null;
                try
                {
                    stream = await _transport.AcceptAsync(linkedSource.Token);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    // The advertise data could be incomplete if the runtime shuts down before completely writing
                    // the information. Catch the exception and continue waiting for a new connection.
                }

                if (null != stream)
                {
                    // Cancel parsing of advertise data after timeout period to
                    // mitigate runtimes that write partial data and do not close the stream (avoid waiting forever).
                    using var parseCancellationSource = new CancellationTokenSource();
                    using var linkedSource2 = CancellationTokenSource.CreateLinkedTokenSource(linkedSource.Token, parseCancellationSource.Token);
                    try
                    {
                        parseCancellationSource.CancelAfter(ParseAdvertiseTimeout);

                        advertise = await IpcAdvertise.ParseAsync(stream, linkedSource2.Token);
                    }
                    catch (OperationCanceledException) when (parseCancellationSource.IsCancellationRequested)
                    {
                        // Only handle cancellation if it was due to the parse timeout.
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        // The advertise data could be incomplete if the runtime shuts down before completely writing
                        // the information. Catch the exception and continue waiting for a new connection.
                    }
                }

                if (null != advertise)
                {
                    Guid runtimeCookie = advertise.RuntimeInstanceCookie;
                    int pid = unchecked((int)advertise.ProcessId);

                    ProvideStream(runtimeCookie, stream);
                    // If this runtime instance is already tracked with an endpoint then skip it.
                    // Consumers should hold onto the IpcEndpointInfo and use it for diagnostic communication,
                    // regardless of the number of times the same runtime instance connects. This requires consumers
                    // to continuously invoke the AcceptAsync method in order to handle runtime instance reconnects,
                    // even if the consumer only wants to handle a single connection.
                    ServerIpcEndpoint endpoint = null;
                    if (!_endpoints.TryGetValue(runtimeCookie, out endpoint))
                    {
                        // Create a new endpoint and connection that are cached an returned from this method.
                        endpoint = new ServerIpcEndpoint(runtimeCookie, this);
                        if (_endpoints.TryAdd(runtimeCookie, endpoint))
                        {
                            return new IpcEndpointInfo(endpoint, pid, runtimeCookie);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Removes a connection from the server so that it is no longer tracked.
        /// </summary>
        /// <param name="runtimeCookie">The runtime instance cookie that corresponds to the connection to be removed.</param>
        /// <returns>True if the connection existed and was removed; otherwise false.</returns>
        internal bool RemoveConnection(Guid runtimeCookie)
        {
            VerifyNotDisposed();

            if (_endpoints.TryRemove(runtimeCookie, out ServerIpcEndpoint endpoint))
            {
                return true;
            }
            return false;
        }

        private void VerifyNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ReversedDiagnosticsServer));
            }
        }

        /// <remarks>
        /// This will block until the diagnostic stream is provided. This block can happen if
        /// the stream is acquired previously and the runtime instance has not yet reconnected
        /// to the reversed diagnostics server.
        /// </remarks>
        internal Stream Connect(Guid runtimeId, TimeSpan timeout)
        {
            using var streamEvent = new ManualResetEvent(false);
            Stream stream = null;
            RegisterHandler(runtimeId, (Guid id, ref Stream cachedStream) =>
            {
                if (id != runtimeId)
                {
                    return false;
                }
                stream = cachedStream;
                cachedStream = null;
                return streamEvent.Set();
            });

            if (streamEvent.WaitOne(timeout))
            {
                throw new TimeoutException();
            }

            return stream;
        }

        internal async Task WaitForConnectionAsync(Guid runtimeId, CancellationToken token)
        {
            bool isConnected = false;
            do
            {
                bool ignore = false;
                TaskCompletionSource<bool> hasConnectedStreamSource = new TaskCompletionSource<bool>();
                using IDisposable _registration = token.Register(() => hasConnectedStreamSource.TrySetCanceled());
                RegisterHandler(runtimeId, (Guid id, ref Stream cachedStream) =>
                {
                    if (runtimeId != id)
                    {
                        return false;
                    }
                    if (ignore)
                    {
                        return true;
                    }
                    bool isConnected = TestStream(cachedStream);
                    if (isConnected)
                    {
                        cachedStream.Dispose();
                        cachedStream = null;
                    }
                    hasConnectedStreamSource.TrySetResult(isConnected);
                    return true;
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

        void ProvideStream(Guid runtimeId, Stream stream)
        {
            // Get the previous stream in order to dispose it later
            Stream previousStream = null;
            lock (_lock)
            {
                previousStream = _cachedStreams[runtimeId];
                RunStreamHandlers(runtimeId, stream);
            }
            // Dispose the previous stream if there was one.
            previousStream?.Dispose();
        }

        private void RunStreamHandlers(Guid runtimeId, Stream stream)
        {
            Debug.Assert(Monitor.IsEntered(_lock));

            // If there are any targets waiting for a stream, provide
            // it to the first target in the queue.
            for( int i = 0; (i < _handlers.Count) && (null != stream); i++)
            {
                StreamHandler handler = _handlers[i];
                if (handler(runtimeId, ref stream))
                {
                    _handlers.RemoveAt(i);
                    i--;
                }
            }

            // Store the stream for when a handler registers later. If
            // a handler already captured the stream, this will be null, thus
            // representing that no existing stream is waiting to be consumed.
            _cachedStreams[runtimeId] = stream;
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

        private void RegisterHandler(Guid runtimeId, StreamHandler handler)
        {
            lock (_lock)
            {
                _handlers.Add(handler);
                Stream s = _cachedStreams[runtimeId];
                if (s != null)
                {
                    RunStreamHandlers(runtimeId, s);
                }
            }
        }

        public static int MaxAllowedConnections = IpcServerTransport.MaxAllowedConnections;
    }
}
