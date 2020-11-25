// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

#nullable enable

namespace Microsoft.WebAssembly.Diagnostics
{
    internal class BrowserPool
    {
        static BrowserInstance? s_browserInstance;
        static SemaphoreSlim s_myLock = new SemaphoreSlim(1);
        static Task? s_proxyTask;

        public static async Task<BrowserInstance> GetInstanceAsync(ILogger logger, string debuggerTestPath, CancellationToken token)
        {
            if (s_browserInstance != null && !s_browserInstance.HasExited)
                return s_browserInstance;

            //FIXME: um use some better way to do this.. we want to init just once
            await s_myLock.WaitAsync();
            try {
                if (s_browserInstance?.HasExited == true)
                {
                    logger.LogError($"Chrome has crashed, cleaning up");
                    await s_browserInstance.DisposeAsync();
                    s_browserInstance = null;
                }

                if (s_proxyTask == null)
                    s_proxyTask = TestHarnessProxy.Start(debuggerTestPath, logger, token);

                if (s_browserInstance == null)
                {
                    Task<BrowserInstance> browserTask = BrowserInstance.StartAsync(logger, token);

                    await Task.WhenAll(s_proxyTask, browserTask);
                    if (s_proxyTask.IsCompletedSuccessfully && browserTask.IsCompletedSuccessfully)
                    {
                        s_browserInstance = await browserTask;
                    }
                    else
                    {
                        // -- umm.. throw!!
                        logger.LogError($"----- EEERRRRRRRRRRR.. proxy or browser launch failed");
                        throw new Exception("proxy or brwoser failed");
                    }
                }
            } finally {
                s_myLock.Release();
            }

            return s_browserInstance;
        }
    }
}
