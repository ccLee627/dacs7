﻿// Copyright (c) Benjamin Proemmer. All rights reserved.
// See License in the project root for license information.

using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Dacs7.Communication
{

    internal sealed class ClientSocket : SocketBase, IDisposable
    {
        private System.Net.Sockets.Socket _socket;
        private readonly ClientSocketConfiguration _config;
        private CancellationTokenSource _tokenSource;


        public sealed override string Identity
        {
            get
            {

                if (_identity == null)
                {
                    if (_socket != null)
                    {
                        var epLocal = _socket.LocalEndPoint as IPEndPoint;
                        try
                        {
                            var epRemote = _socket.RemoteEndPoint as IPEndPoint;
                            _identity = $"{epLocal.Address}:{epLocal.Port}-{(epRemote != null ? epRemote.Address.ToString() : _config.Hostname)}:{(epRemote != null ? epRemote.Port : _config.ServiceName)}";
                        }
                        catch (Exception)
                        {
                            return string.Empty;
                        };
                    }
                    else
                    {
                        return string.Empty;
                    }
                }
                return _identity;
            }
        }

        public ClientSocket(ClientSocketConfiguration configuration, ILoggerFactory loggerFactory) : base(configuration, loggerFactory?.CreateLogger<ClientSocket>()) => _config = configuration;


        /// <summary>
        /// Starts the server such that it is listening for 
        /// incoming connection requests.    
        /// </summary>
        public sealed override async Task OpenAsync()
        {
            await base.OpenAsync().ConfigureAwait(false);
            await InternalOpenAsync().ConfigureAwait(false);
        }

        protected sealed override async Task InternalOpenAsync(bool internalCall = false)
        {
            try
            {
                if (_shutdown) return;
                _identity = null;
                _socket = new System.Net.Sockets.Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    ReceiveBufferSize = _configuration.ReceiveBufferSize,
                    NoDelay = true
                };
                _logger?.LogDebug("Socket connecting. ({0}:{1})", _config.Hostname, _config.ServiceName);
                await _socket.ConnectAsync(_config.Hostname, _config.ServiceName).ConfigureAwait(false);
                EnsureConnected();
                _logger?.LogDebug("Socket connected. ({0}:{1})", _config.Hostname, _config.ServiceName);
                if (_config.KeepAlive)
                    _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, 1);
                _disableReconnect = false; // we have a connection, so enable reconnect


                _tokenSource = new CancellationTokenSource();
                _ = await Task.Factory.StartNew(() => StartReceive(), _tokenSource.Token,TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
                await PublishConnectionStateChanged(true).ConfigureAwait(false);
            }
            catch (Exception)
            {
                DisposeSocket();
                await HandleSocketDown().ConfigureAwait(false);
                if (!internalCall) throw;
            }
        }

        public sealed override async Task<SocketError> SendAsync(Memory<byte> data)
        {
            // Write the locally buffered data to the network.
            try
            {
                var result = await _socket.SendAsync(new ArraySegment<byte>(data.ToArray()), SocketFlags.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                //TODO
                // If this is an unknown status it means that the error if fatal and retry will likely fail.
                //if (SocketError.GetStatus(exception.HResult) == SocketErrorStatus.Unknown)
                //{
                //    throw;
                //}
                return SocketError.Fault;
            }
            return SocketError.Success;
        }

        public sealed override async Task CloseAsync()
        {
            await base.CloseAsync().ConfigureAwait(false);
            DisposeSocket();
        }

        private void DisposeSocket()
        {
            if (_socket != null)
            {
                try
                {
                    _socket?.Dispose();
                }
                catch (ObjectDisposedException) { }
                _socket = null;
            }
        }

        private async Task StartReceive()
        {
            var connectionInfo = _socket.RemoteEndPoint.ToString();
            _logger?.LogDebug("Socket connection receive loop started. ({0})", connectionInfo);
            var receiveBuffer = ArrayPool<byte>.Shared.Rent(_socket.ReceiveBufferSize * 2);
            var receiveOffset = 0;
            var bufferOffset = 0;
            var span = new Memory<byte>(receiveBuffer);
            try
            {
                while (_socket != null && _tokenSource != null && !_tokenSource.IsCancellationRequested)
                {
                    try
                    {
                        var buffer = new ArraySegment<byte>(receiveBuffer, receiveOffset, _socket.ReceiveBufferSize);
                        var received = await _socket.ReceiveAsync(buffer, SocketFlags.Partial).ConfigureAwait(false);

                        if (received == 0) return;

                        var toProcess = received + (receiveOffset - bufferOffset);
                        var processed = 0;
                        do
                        {
                            var off = bufferOffset + processed;
                            var length = toProcess - processed;
                            var slice = span.Slice(off, length);
                            var proc = await ProcessData(slice).ConfigureAwait(false);
                            if (proc == 0)
                            {
                                if (length > 0)
                                {
                                    receiveOffset += received;
                                    bufferOffset = receiveOffset - (toProcess - processed);
                                }
                                else
                                {
                                    receiveOffset = 0;
                                    bufferOffset = 0;
                                }
                                break;
                            }
                            processed += proc;
                        } while (processed < toProcess);
                    }
                    catch (Exception ex)
                    {
                        if (_socket != null && !_shutdown)
                        {
                            _logger?.LogError("Socket exception ({0}): {1}", connectionInfo, ex.Message);
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(receiveBuffer);
                _ = HandleSocketDown();
                _logger?.LogDebug("Socket connection receive loop ended. ({0})", connectionInfo);
            }

        }

        protected sealed override Task HandleSocketDown()
        {
            _ = HandleReconnectAsync();
            return PublishConnectionStateChanged(false);
        }

        private void EnsureConnected()
        {
            var blocking = true;

            try
            {
                blocking = _socket.Blocking;
                _socket.Blocking = false;
                _socket.Send(Array.Empty<byte>(), 0, 0);
            }
            catch (SocketException se)
            {
                // 10035 == WSAEWOULDBLOCK
                if (!se.SocketErrorCode.Equals(10035))
                {
                    throw;   //Throw the Exception for handling in OnConnectedToServer
                }
            }

            //restore blocking mode
            _socket.Blocking = blocking;
        }

        public void Dispose() => DisposeSocket();
    }
}
