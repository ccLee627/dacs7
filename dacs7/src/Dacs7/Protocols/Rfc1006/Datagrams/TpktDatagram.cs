﻿// Copyright (c) Benjamin Proemmer. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License in the project root for license information.

using System;

namespace Dacs7.Protocols.Rfc1006
{
    internal sealed class TpktDatagram
    {
        public byte Sync1 { get; set; }
        public byte Sync2 { get; set; }
        public ushort Length { get; set; } = 4;
    }
}
