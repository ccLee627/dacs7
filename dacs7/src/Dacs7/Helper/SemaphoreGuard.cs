﻿// Copyright (c) Benjamin Proemmer. All rights reserved.
// See License in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Dacs7.Helper
{
    internal sealed class SemaphoreGuard : IDisposable
    {
        private SemaphoreSlim _semaphore;
        private bool IsDisposed => _semaphore == null;
        public SemaphoreGuard(SemaphoreSlim semaphore, bool wait = true)
        {
            _semaphore = semaphore;
            if (wait)
            {
                _semaphore.Wait();
            }
        }

        public static async Task<SemaphoreGuard> Async(SemaphoreSlim semaphore)
        {
            var guard = new SemaphoreGuard(semaphore, false);
            await semaphore.WaitAsync().ConfigureAwait(false);
            return guard;
        }

        public void Dispose()
        {
            if (IsDisposed)
                ThrowObjectDisposedException(this);

            _semaphore.Release();
            _semaphore = null;
        }

        private static void ThrowObjectDisposedException(SemaphoreGuard guard) => throw new ObjectDisposedException(guard.ToString());
    }


}
