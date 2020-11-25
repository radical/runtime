// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

#nullable enable

namespace Microsoft.WebAssembly.Diagnostics
{
    internal class BrowserCdpConnection : IAsyncDisposable, IDisposable
    {
        public ConcurrentDictionary<string, BrowserTarget> Targets = new ();
        public ConcurrentDictionary<string, BrowserSession> Sessions = new ();

        public InspectorClient InspectorClient { get; set; }
        public ILogger Logger { get; private set; }

        private CancellationTokenSource _cts;

        private const int ConcurrentChromeConnections = 6;
        private static SemaphoreSlim s_semaphore = new (1);
        private static SemaphoreSlim s_connectionSemaphore = new (ConcurrentChromeConnections);
        private static MethodInfo? threadpoolPump;
        private static MethodInfo? timerPump;

        private BrowserCdpConnection(InspectorClient client, ILogger logger, CancellationTokenSource cts)
        {
            Logger = logger;
            _cts = cts;
            InspectorClient = client;
            InspectorClient.RunLoopStopped += (_, _) => Dispose();
        }

        public static async Task<BrowserCdpConnection> Open(int browser_id, string testId, ILogger logger, CancellationTokenSource cts)
        {
            await s_connectionSemaphore.WaitAsync();
            // try
            // {
                var client = new InspectorClient(testId, logger);
                var connection = new BrowserCdpConnection(client, logger, cts);

                // FIXME: use a per-test logger here
                await client.Connect(
                    new Uri($"ws://{TestHarnessProxy.Endpoint.Authority}/connect-to-devtools/{browser_id}?testId={testId}"),
                    connection.MessageFallbackHandler,
                    cts.Token);

                return connection;
            // }
            // finally
            // {
            //     s_connectionSemaphore.Release();
            // }
        }

        public void RemoveSession(SessionId sessionId)
        {
            Sessions.Remove(sessionId.sessionId, out _);
            InspectorClient.RemoveMessageHandlerForSession(sessionId);
        }

        private async Task MessageFallbackHandler(JObject msg, CancellationToken token)
        {
            Logger.LogDebug($"Fallback: {msg}");
            // string? method = msg["method"]?.Value<string>();
            // JObject? args = msg["params"]?.Value<JObject>();
            // if (method == null)
            //     return;

            // switch (method)
            // {
            //     case "Target.detachedFromTarget":
            //     {
            //         // this has sessionId
            //         // string? sessionId = args?["sessionId"]?.Value<string>();
            //         // // FIXME: remove the target, and the session message handler
            //         // // Console.WriteLine ($"\tsessionId: {sessionId}");
            //         // if (sessionId != null && Sessions.TryGetValue(sessionId, out var session))
            //         // {
            //         //     // await session.DisposeAsync();
            //         // }
            //         break;
            //     }

                // targetDestroyed, ..
            // }

            await Task.CompletedTask;
        }

        public async Task<Result> SendCommandNoSession(string method, JObject? args, CancellationToken token)
            => await InspectorClient.SendCommand(method, args, token);

        public async Task<Result> SendCommand(SessionId sessionId, string method, JObject? args, CancellationToken token)
            => await InspectorClient.SendCommand(sessionId, method, args, token);

        public async Task<BrowserSession> OpenSession(
                        string url, Func<string, JObject, CancellationToken, Task> onMessage,
                        ILogger logger, string id, CancellationTokenSource cts, BrowserTarget? target=null)
        {
            Result res;
            if (target == null)
            {
                res = await SendCommandNoSession("Target.createBrowserContext", JObject.FromObject(new
                {
                    disposeOnDetach = true
                }), cts.Token);
                if (!res.IsOk)
                    throw new Exception($"Target.createBrowserContext failed with {res}");

                string browserContextId = res.Value["browserContextId"]?.Value<string>() ?? throw new Exception($"Missing browserContextId in {res}");

                res = await SendCommandNoSession("Target.createTarget", JObject.FromObject(new
                {
                    url,
                    browserContextId
                }), cts.Token);
                if (!res.IsOk)
                    throw new Exception($"Target.createTarget failed with {res}");

                var targetId = res.Value["targetId"]?.Value<string>();
                if (string.IsNullOrEmpty(targetId))
                    throw new Exception($"Target.createTarget missing a targetId: {res}");

                res = await SendCommandNoSession("Target.activateTarget", res.Value, cts.Token);
                target = new BrowserTarget(browserContextId, targetId, url, this);
                Targets[target.Id] = target;
            }

            res = await SendCommandNoSession("Target.attachToTarget", JObject.FromObject(new
            {
                targetId = target.Id,
                flatten = true
            }), cts.Token);
            // Console.WriteLine($"----> attachToTarget: {res}");
            if (res.IsErr)
                throw new Exception($"Target.attachToTarget failed with {res}");

            // TODO: um.. do this in response to attachedToTarget
            if (string.IsNullOrEmpty(res.Value["sessionId"]?.Value<string>()))
                throw new Exception($"Target.attachToTarget didn't return any sessionId. {res}");

            var sessionId = new SessionId(res.Value["sessionId"]?.Value<string>());
            InspectorClient.AddMessageHandlerForSession(sessionId, onMessage);
            var session = new BrowserSession(sessionId, target);
            Sessions[sessionId.sessionId] = session;

            return session;
        }

        public void Dispose() => CloseAsync().Wait();
        public async Task CloseAsync() => await DisposeAsync();

        // bool _disposed = false;
        bool _disposing = false;
        bool _disposed = false;
        public async ValueTask DisposeAsync()
        {
            // if (_disposed)
            //     throw new ObjectDisposedException(nameof(BrowserTarget));

            if (_disposing || _disposed)
                return;

            Logger.LogDebug($"--------------------- CdpConnection.DisposeAsync, _disposing: {_disposing}, InspectorClient.IsRunning: {InspectorClient.IsRunning} ---------------");
            _disposing = true;
            // Console.WriteLine (Environment.StackTrace);
            // close this
            // this might not actually work.. eg. if devtoolsclient is no longer running!!
            if (InspectorClient.IsRunning)
            {
                foreach (var session in Sessions.Values)
                    await session.DisposeAsync();

                Sessions.Clear();

                foreach (var target in Targets.Values)
                    await target.DisposeAsync();

                Targets.Clear();
                //FIXME:
                // Result res = await InspectorClient.SendCommand("Target.closeTarget", JObject.FromObject(new
                // {
                //     targetId = Id
                // }), new CancellationToken(false));
                // _logger.LogDebug($"-- BrowserTarget.DisposeAsync: closeTarget sent, result: {res}");

                // FIXME: um if closeTarget failed, then also we need to stop the client, and close the socket!
                // if (!res.IsOk)
                    // throw new Exception("Failed to close target {TargetId}");
            }

            var res = await InspectorClient.SendCommand("Target.getTargets", null, new CancellationToken());
            Logger.LogDebug("getTargets returned {res}");
            res = await InspectorClient.SendCommand("Target.getBrowserContexts", null, new CancellationToken());
            Logger.LogDebug("getBrowserContextx returned {res}");

            // await InspectorClient.DisposeAsync();
            InspectorClient.Dispose();

            await s_semaphore.WaitAsync();
            try
            {
                if (threadpoolPump == null || timerPump == null)
                {
                    threadpoolPump = typeof(ThreadPool).GetMethod("PumpThreadPool", BindingFlags.NonPublic | BindingFlags.Static);
                    timerPump = Type.GetType("System.Threading.TimerQueue")?.GetMethod("PumpTimerQueue", BindingFlags.NonPublic | BindingFlags.Static);
                }

                Logger.LogDebug($"CdpConnection.DisposeAsync: TP PendingWorkItemCount: {ThreadPool.PendingWorkItemCount}\n");
                threadpoolPump?.Invoke(this, null);
                timerPump?.Invoke(this, null);
                Logger.LogDebug($"CdpConnection.DisposeAsync AFTER: TP PendingWorkItemCount: {ThreadPool.PendingWorkItemCount}\n");
            } finally {
                s_semaphore.Release();
            }

            s_connectionSemaphore.Release();

            Logger.LogDebug($"-- BrowserTarget.DisposeAsync: done");
            _disposing = false;
            _disposed = true;
            // s_semaphore.Release();

            await Task.CompletedTask;
        }
    }

}
