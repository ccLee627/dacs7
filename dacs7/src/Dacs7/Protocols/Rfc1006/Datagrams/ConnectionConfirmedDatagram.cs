﻿// Copyright (c) insite-gmbh. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License in the project root for license information.

using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Dacs7.Protocols.Rfc1006
{
    internal class ConnectionConfirmedDatagram : IDisposable
    {
        private IMemoryOwner<byte> _sizeTpduReceiving;
        private IMemoryOwner<byte> _destTsap;
        private IMemoryOwner<byte> _sourceTsap;


        public TpktDatagram Tkpt { get; set; } = new TpktDatagram
        {
            Sync1 = 0x03,
            Sync2 = 0x00
        };

        public byte Li { get; set; }

        public byte PduType { get; set; } // = 0xd0;

        public Int16 DstRef { get; set; } //  = 0x0001;                     // TPDU Destination Reference

        public Int16 SrcRef { get; set; } // = 0x0001;                     // TPDU Source-Reference (my own reference, should not be zero)

        public byte ClassOption { get; set; } // = 0x00;                 // PDU Class 0 and no Option

        public byte ParmCodeTpduSize { get; set; } = 0xc0;

        public byte SizeTpduReceivingLength { get; set; } 

        public Memory<byte> SizeTpduReceiving { get; set; }              // Allowed sizes: 128(7), 256(8), 512(9), 1024(10), 2048(11) octets

        public byte ParmCodeSrcTsap { get; set; } = 0xc1;

        public byte SourceTsapLength { get; set; }

        public Memory<byte> SourceTsap { get; set; }

        public byte ParmCodeDestTsap { get; set; } = 0xc2;

        public byte DestTsapLength { get; set; }

        public Memory<byte> DestTsap { get; set; }


        public void Dispose()
        {
            _sizeTpduReceiving?.Dispose();
            _sizeTpduReceiving = null;
            _sourceTsap?.Dispose();
            _sourceTsap = null;
            _destTsap?.Dispose();
            _destTsap = null;
        }

        public ConnectionConfirmedDatagram BuildCc(Rfc1006ProtocolContext context, ConnectionRequestDatagram req)
        {
            context.CalcLength(context, out byte li, out ushort length);
            req.SizeTpduReceiving.Span.CopyTo(context.SizeTpduSending.Span);
            var result = new ConnectionConfirmedDatagram
            {
                Li = li,
                SizeTpduReceiving = context.SizeTpduReceiving,
                SourceTsapLength = req.SourceTsapLength,
                SourceTsap = req.SourceTsap,
                DestTsapLength = req.DestTsapLength,
                DestTsap = req.DestTsap
            };

            result.Tkpt.Length = length;
            return result;
        }


        public static Memory<byte> TranslateToMemory(ConnectionConfirmedDatagram datagram)
        {
            var length = datagram.Tkpt.Length;
            var result = new Memory<byte>(new byte[length]);  // check if we could use ArrayBuffer
            var span = result.Span;

            span[0] = datagram.Tkpt.Sync1;
            span[1] = datagram.Tkpt.Sync2;
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(2, 2), datagram.Tkpt.Length);
            span[4] = datagram.Li;
            span[5] = datagram.PduType;
            BinaryPrimitives.WriteInt16BigEndian(span.Slice(6, 2), datagram.DstRef);
            BinaryPrimitives.WriteInt16BigEndian(span.Slice(8, 2), datagram.SrcRef);
            span[10] = datagram.ClassOption;
            span[11] = datagram.ParmCodeTpduSize;

            var offset = 11;
            span[offset++] = datagram.ParmCodeTpduSize;
            span[offset++] = datagram.SizeTpduReceivingLength;
            datagram.SizeTpduReceiving.CopyTo(result.Slice(offset));
            offset += datagram.SizeTpduReceivingLength;

            span[offset++] = datagram.ParmCodeSrcTsap;
            span[offset++] = datagram.SourceTsapLength;
            datagram.SourceTsap.CopyTo(result.Slice(offset));
            offset += datagram.SourceTsapLength;

            span[offset++] = datagram.ParmCodeDestTsap;
            span[offset++] = datagram.DestTsapLength;
            datagram.DestTsap.CopyTo(result.Slice(offset));
            offset += datagram.DestTsapLength;

            return result;
        }

        public static ConnectionConfirmedDatagram TranslateFromMemory(Memory<byte> data, out int processed)
        {
            var span = data.Span;
            var result = new ConnectionConfirmedDatagram
            {
                Tkpt = new TpktDatagram
                {
                    Sync1 = span[0],
                    Sync2 = span[1],
                    Length = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(2,2))
                },
                Li = span[4],
                PduType = span[5],
                DstRef = BinaryPrimitives.ReadInt16BigEndian(span.Slice(6, 2)),
                SrcRef = BinaryPrimitives.ReadInt16BigEndian(span.Slice(8, 2)),
                ClassOption = span[10],

            };

            int offset;
            for (offset = 11; offset < data.Length;)
            {
                switch (span[offset])
                {
                    case 0xc0:
                        {
                            result.ParmCodeTpduSize = span[offset++];
                            result.SizeTpduReceivingLength = span[offset++];
                            result._sizeTpduReceiving = MemoryPool<byte>.Shared.Rent(result.SizeTpduReceivingLength);
                            data.Slice(offset, result.SizeTpduReceivingLength).CopyTo(result._sizeTpduReceiving.Memory);
                            result.SizeTpduReceiving = result._sizeTpduReceiving.Memory.Slice(0, result.SizeTpduReceivingLength);
                            offset += result.SizeTpduReceivingLength;
                        }
                        break;

                    case 0xc1:
                        {
                            result.ParmCodeSrcTsap = span[offset++];
                            result.SourceTsapLength = span[offset++];
                            var tmp = new byte[result.SourceTsapLength];
                            result._sourceTsap = MemoryPool<byte>.Shared.Rent(result.SourceTsapLength);
                            data.Slice(offset, result.SourceTsapLength).CopyTo(result._sourceTsap.Memory);
                            result.SourceTsap = result._sourceTsap.Memory.Slice(0, result.SourceTsapLength);
                            offset += result.SourceTsapLength;
                        }
                        break;

                    case 0xc2:
                        {
                            result.ParmCodeDestTsap = span[offset++];
                            result.DestTsapLength = span[offset++];
                            result._destTsap = MemoryPool<byte>.Shared.Rent(result.DestTsapLength);
                            data.Slice(offset, result.DestTsapLength).CopyTo(result._destTsap.Memory);
                            result.DestTsap = result._destTsap.Memory.Slice(0, result.DestTsapLength);
                            offset += result.DestTsapLength;
                        }
                        break;

                    default:
                        break;
                }

            }


            processed = offset;
            return result;
        }

        
    }
}
