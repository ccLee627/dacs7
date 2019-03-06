﻿using Dacs7.Exceptions;
using Dacs7.Protocols.SiemensPlc;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Dacs7.Protocols
{
    internal delegate Task OnUpdateConnectionState(ConnectionState state);
    internal delegate Task<bool> OnDetectAndReceive(Memory<byte> payload);
    internal delegate ConnectionState OnGetConnectionState();

    internal partial class ProtocolHandler
    {
        private bool _closeCalled;
        private readonly Transport _transport;
        private SiemensPlcProtocolContext _s7Context;
        private AsyncAutoResetEvent<bool> _connectEvent = new AsyncAutoResetEvent<bool>();
        private SemaphoreSlim _concurrentJobs;
        private ILogger _logger;
        private int _referenceId;
        private readonly object _idLock = new object();
        private Action<ConnectionState> _connectionStateChanged;

        public ConnectionState ConnectionState { get; private set; } = ConnectionState.Closed;

        internal ushort GetNextReferenceId()
        {
            var id = Interlocked.Increment(ref _referenceId);

            if (id < UInt16.MinValue || id > UInt16.MaxValue)
            {
                lock (_idLock)
                {
                    id = Interlocked.Increment(ref _referenceId);
                    if (id < UInt16.MinValue || id > UInt16.MaxValue)
                    {
                        Interlocked.Exchange(ref _referenceId, 0);
                        id = Interlocked.Increment(ref _referenceId);
                    }
                }
            }
            return Convert.ToUInt16(id);

        }

        public ProtocolHandler( Transport transport,
                                SiemensPlcProtocolContext s7Context, 
                                Action<ConnectionState> connectionStateChanged,
                                ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory?.CreateLogger<ProtocolHandler>();
            _transport = transport;
            _s7Context = s7Context;
            _connectionStateChanged = connectionStateChanged;
            _logger?.LogDebug("S7Protocol-Timeout is {0} ms", _s7Context.Timeout);


            transport.OnUpdateConnectionState = UpdateConnectionState;
            transport.OnDetectAndReceive = DetectAndReceive;
            transport.OnGetConnectionState = () => ConnectionState;
            transport.ConfigureClient(loggerFactory);

        }


        public async Task OpenAsync()
        {
            try
            {
                _closeCalled = false;
                await _transport.Client.OpenAsync();
                try
                {
                    if (!await _connectEvent.WaitAsync(_s7Context.Timeout * 10))
                    {
                        await CloseAsync();
                        ExceptionThrowHelper.ThrowNotConnectedException();
                    }
                }
                catch (TimeoutException)
                {
                    await CloseAsync();
                    ExceptionThrowHelper.ThrowNotConnectedException();
                }
            }
            catch(Dacs7NotConnectedException)
            {
                await CloseAsync();
                throw;
            }
            catch(Exception ex)
            {
                ExceptionThrowHelper.ThrowNotConnectedException(ex);
            }
        }

        public async Task CloseAsync()
        {
            _closeCalled = true;
            foreach (var item in _writeHandler)
            {
                item.Value.Event?.Set(null);
            }
            foreach (var item in _readHandler)
            {
                item.Value.Event?.Set(null);
            }
            foreach (var item in _blockInfoHandler)
            {
                item.Value.Event?.Set(null);
            }
            foreach (var item in _alarmHandler)
            {
                item.Value.Event?.Set(null);
            }
            if(_alarmUpdateHandler.Id != 0)
            {
                _alarmUpdateHandler.Event?.Set(null);
                await DisableAlarmUpdatesAsync();
            }
            await _transport.Client.CloseAsync();
            await Task.Delay(1); // This ensures that the user can call connect after reconnect. (Otherwise he has so sleep for a while)
        }


        

        private Task<bool> DetectAndReceive(Memory<byte> payload)
        {
            if (_s7Context.TryDetectDatagramType(payload, out var s7DatagramType))
            {
                S7DatagramReceived(s7DatagramType, payload);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        private void S7DatagramReceived(Type datagramType, Memory<byte> buffer)
        {
            if (datagramType == typeof(S7ReadJobAckDatagram))
            {
                ReceivedReadJobAck(buffer);
            }
            else if (datagramType == typeof(S7WriteJobAckDatagram))
            {
                ReceivedWriteJobAck(buffer);
            }
            else if (datagramType == typeof(S7PlcBlockInfoAckDatagram))
            {
                ReceivedS7PlcBlockInfoAckDatagram(buffer);
            }
            else if (datagramType == typeof(S7PendingAlarmAckDatagram))
            {
                ReceivedS7PendingAlarmsAckDatagram(buffer);
            }
            else if (datagramType == typeof(S7AlarmUpdateAckDatagram))
            {
                ReceivedS7AlarmUpdateAckDatagram(buffer);
            }
            else if (datagramType == typeof(S7CommSetupAckDataDatagram))
            {
                ReceivedCommunicationSetupAck(buffer);
            }
            else if (datagramType == typeof(S7CommSetupDatagram))
            {
                _ = ReceivedCommunicationSetupJob(buffer);
            }
            else if (datagramType == typeof(S7ReadJobDatagram))
            {
                ReceivedReadJob(buffer);
            }
            else if (datagramType == typeof(S7AlarmIndicationDatagram))
            {
                ReceivedS7AlarmIndicationDatagram(buffer);
            }
        }

        private async Task StartS7CommunicationSetup()
        {
            using (var dgmem = S7CommSetupDatagram.TranslateToMemory(S7CommSetupDatagram.Build(_s7Context, GetNextReferenceId()), out var commemLength))
            {
                using (var sendData = _transport.Build(dgmem.Memory.Slice(0, commemLength), out var sendLength))
                {
                    var result = await _transport.Client.SendAsync(sendData.Memory.Slice(0, sendLength));
                    if (result == SocketError.Success)
                    {
                        await UpdateConnectionState(ConnectionState.PendingOpenPlc);
                    }
                }
            }
        }

        private async Task ReceivedCommunicationSetupJob(Memory<byte> buffer)
        {
            var data = S7CommSetupDatagram.TranslateFromMemory(buffer);
            using (var dg = S7CommSetupAckDataDatagram
                                                    .TranslateToMemory(
                                                        S7CommSetupAckDataDatagram
                                                        .BuildFrom(_s7Context, data), out int memoryLength))
            {
                using (var sendData = _transport.Build(dg.Memory.Slice(0, memoryLength), out var sendLength))
                {
                    var result = await _transport.Client.SendAsync(sendData.Memory.Slice(0, sendLength));
                    if (result == SocketError.Success)
                    {
                        //UpdateConnectionState(ConnectionState.PendingOpenPlc);
                        _s7Context.MaxAmQCalling = data.Parameter.MaxAmQCalling;
                        _s7Context.MaxAmQCalled = data.Parameter.MaxAmQCalling;
                        _s7Context.PduSize = data.Parameter.PduLength;
                        _concurrentJobs = new SemaphoreSlim(_s7Context.MaxAmQCalling);
                        await UpdateConnectionState(ConnectionState.Opened);
                    }
                }
            }
        }

        private void ReceivedCommunicationSetupAck(Memory<byte> buffer)
        {
            var data = S7CommSetupAckDataDatagram.TranslateFromMemory(buffer);
            _s7Context.MaxAmQCalling = data.Parameter.MaxAmQCalling;
            _s7Context.MaxAmQCalled = data.Parameter.MaxAmQCalled;
            _s7Context.PduSize = data.Parameter.PduLength;
            _concurrentJobs = new SemaphoreSlim(_s7Context.MaxAmQCalling);
            _ = UpdateConnectionState(ConnectionState.Opened);
            _connectEvent.Set(true);
        }

        private async Task UpdateConnectionState(ConnectionState state)
        {
            if (ConnectionState != state)
            {

                if (state == ConnectionState.TransportOpened)
                {
                    await StartS7CommunicationSetup();
                }
                else if(state == ConnectionState.Closed)
                {
                    if (_concurrentJobs != null)
                    {
                        _concurrentJobs.Dispose();
                        _concurrentJobs = null;
                    }
                }

                ConnectionState = state;
                _connectionStateChanged?.Invoke(state);
            }
        }

    }
}
