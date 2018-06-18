﻿using Dacs7.Communication.S7Online;
using System;
using System.Buffers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Dacs7.Communication
{
    internal class S7OnlineClient : SocketBase
    {
        private bool _disableReconnect;
        private bool _closeCalled;
        private int _connectionHandle = -1;

        private readonly S7OnlineConfiguration _config;
        public override string Identity
        {
            get
            {

                if (_identity == null)
                {
                    //if (_socket != null)
                    //{
                    //    var epLocal = _socket.LocalEndPoint as IPEndPoint;
                    //    IPEndPoint epRemote = null;
                    //    try
                    //    {
                    //        epRemote = _socket.RemoteEndPoint as IPEndPoint;
                    //        _identity = $"{epLocal.Address}:{epLocal.Port}-{(epRemote != null ? epRemote.Address.ToString() : _configuration.Hostname)}:{(epRemote != null ? epRemote.Port : _configuration.ServiceName)}";
                    //    }
                    //    catch (Exception)
                    //    {
                    //        return string.Empty;
                    //    };
                    //}
                    //else
                    //    return string.Empty;
                }
                return _identity;
            }
        }

        public S7OnlineClient(S7OnlineConfiguration configuration) : base(configuration)
        {
            _config = configuration;
        }


        /// <summary>
        /// Starts the server such that it is listening for 
        /// incoming connection requests.    
        /// </summary>
        public override Task OpenAsync()
        {
            _closeCalled = false;
            _disableReconnect = true;
            return InternalOpenAsync();
        }

        private async Task InternalOpenAsync(bool internalCall = false)
        {
            try
            {
                if (_closeCalled) return;
                _identity = null;


                // Connect
                _connectionHandle = Native.SCP_open("S7ONLINE");    // TODO: Configurable

                if (_connectionHandle >= 0)
                {

                    _disableReconnect = false; // we have a connection, so enable reconnect


                    _ = Task.Factory.StartNew(() => StartReceive(), TaskCreationOptions.LongRunning);
                    await PublishConnectionStateChanged(true);
                }
                else
                {
                    throw new Exception($"{Native.SCP_get_errno()}"); // todo create real exception
                }
            }
            catch (Exception ex)
            {
                DisposeSocket();
                await HandleSocketDown();
                if (!internalCall) throw;
            }
        }

        public override Task<SocketError> SendAsync(Memory<byte> data)
        {
            // Write the locally buffered data to the network.
            try
            {
                int ret = Native.SCP_send(_connectionHandle, (ushort)data.Length, data.ToArray());
                if (ret < 0)
                {
                    Task.FromResult(SocketError.Fault);
                }
            }
            catch (Exception)
            {
                //TODO
                // If this is an unknown status it means that the error if fatal and retry will likely fail.
                //if (SocketError.GetStatus(exception.HResult) == SocketErrorStatus.Unknown)
                //{
                //    throw;
                //}
                return Task.FromResult(SocketError.Fault);
            }
            return Task.FromResult( SocketError.Success);
        }

        public async override Task CloseAsync()
        {
            _disableReconnect = _closeCalled = true;
            await base.CloseAsync();
            DisposeSocket();
        }

        private void DisposeSocket()
        {
            Native.SCP_close(_connectionHandle);
            _connectionHandle = -1;
        }

        private async Task StartReceive()
        {
            var receiveBuffer = ArrayPool<byte>.Shared.Rent(Marshal.SizeOf(ReceiveBufferSize));
            var receiveOffset = 0;
            var bufferOffset = 0;
            var span = new Memory<byte>(receiveBuffer);
            try
            {
                while (_connectionHandle >= 0)
                {
                    try
                    {
                        var receivedLength = new int[1];
                        Native.SCP_receive(_connectionHandle, 0, receivedLength, (ushort)receiveBuffer.Length, receiveBuffer);

                        var received = receivedLength[0];
                        if (received == 0)
                            return;

                        var toProcess = received + (receiveOffset - bufferOffset);
                        var processed = 0;
                        do
                        {
                            var off = bufferOffset + processed;
                            var length = toProcess - processed;
                            var slice = span.Slice(off, length);
                            var proc = await ProcessData(slice);
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
                    catch (Exception) { }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(receiveBuffer);
                _ = HandleSocketDown();
            }

        }
        protected override Task HandleSocketDown()
        {
            _ = HandleReconnectAsync();
            return PublishConnectionStateChanged(false);
        }


        private async Task HandleReconnectAsync()
        {
            if (!_disableReconnect && _configuration.AutoconnectTime > 0)
            {
                await Task.Delay(_configuration.AutoconnectTime);
                await InternalOpenAsync(true);
            }
        }
    }
}
