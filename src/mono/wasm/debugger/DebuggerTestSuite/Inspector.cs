// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.WebAssembly.Diagnostics;
using Newtonsoft.Json.Linq;

#nullable enable

namespace DebuggerTests
{
    class Inspector
    {
        Dictionary<string, TaskCompletionSource<JObject>> notifications = new Dictionary<string, TaskCompletionSource<JObject>>();
        Dictionary<string, Func<JObject, CancellationToken, Task>> eventListeners = new Dictionary<string, Func<JObject, CancellationToken, Task>>();

        public const string PAUSE = "pause";
        public const string READY = "ready";
        public CancellationToken Token => _cancellationTokenSource.Token;
        public InspectorClient? Client { get; private set; }

        public BrowserSession? Session { get; private set; }

        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly string _id;

        protected ILoggerFactory _loggerFactory;
        protected ILogger _logger;

        public Inspector(string id, CancellationTokenSource cts)
        {
            _id = id;
            _cancellationTokenSource = cts;

            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddSimpleConsole(c =>
                {
                    c.ColorBehavior = LoggerColorBehavior.Enabled;
                    c.TimestampFormat = "[HH:mm:ss.fff] ";
                    c.SingleLine = true;
                }).AddFilter(null, LogLevel.Trace);
            });

            // Client = new InspectorClient(_id, _loggerFactory.CreateLogger($"{typeof(InspectorClient)}-{_id}"));
            _logger = _loggerFactory.CreateLogger($"{typeof(Inspector)}-{_id}");
        }

        public Task<JObject> WaitFor(string what)
        {
            if (notifications.TryGetValue(what, out TaskCompletionSource<JObject>? tcs))
            {
                if (tcs.Task.IsCompleted)
                {
                    notifications.Remove(what);
                    return tcs.Task;
                }

                throw new Exception($"Invalid internal state, waiting for {what} while another wait is already setup");
            }
            else
            {
                var n = new TaskCompletionSource<JObject>();
                notifications[what] = n;
                return n.Task;
            }
        }

        void NotifyOf(string what, JObject args)
        {
            if (notifications.TryGetValue(what, out TaskCompletionSource<JObject>? tcs))
            {
                if (tcs.Task.IsCompleted)
                    throw new Exception($"Invalid internal state. Notifying for {what} again, but the previous one hasn't been read.");

                notifications[what].SetResult(args);
                notifications.Remove(what);
            }
            else
            {
                var n = new TaskCompletionSource<JObject>();
                notifications[what] = n;
                n.SetResult(args);
            }
        }

        public void On(string evtName, Func<JObject, CancellationToken, Task> cb)
        {
            eventListeners[evtName] = cb;
        }

        void FailAllWaiters(Exception? exception = null)
        {
            // Because we can create already completed tasks,
            // when we get a NotifyOf before the corresponding
            // WaitFor, it might already be completed. So, use
            // *Try* methods

            if (exception != null)
            {
                foreach (var tcs in notifications.Values)
                    tcs.TrySetException(exception);
            }
            else
            {
                foreach (var tcs in notifications.Values)
                    tcs.TrySetCanceled();
            }
        }

        async Task OnMessage(string method, JObject args, CancellationToken token)
        {
            switch (method)
            {
                case "Debugger.paused":
                    NotifyOf(PAUSE, args);
                    break;
                case "Mono.runtimeReady":
                    NotifyOf(READY, args);
                    break;
                case "Runtime.consoleAPICalled":
                    _logger.LogInformation("CWL: {0}", args["args"]);
                    break;
            }
            if (eventListeners.TryGetValue(method, out var listener))
            {
                await listener(args, token).ConfigureAwait(false);
            }
            else if (String.Compare(method, "Runtime.exceptionThrown") == 0)
            {
                _logger.LogDebug($"Failing all waiters because: {method}: {args}");
                FailAllWaiters(new ArgumentException(args.ToString()));
            }
        }

        public async Task OpenSessionAsync(string relativeUrl, Func<BrowserSession, CancellationToken, List<(string, Task<Result>)>> getInitCmds)
        {
            var start = DateTime.Now;
            try
            {
                // var uri = new Uri($"ws://{TestHarnessProxy.Endpoint.Authority}/launch-chrome-and-connect?testId={_id}");

                BrowserInstance browser = await BrowserPool.GetInstanceAsync(_logger, DebuggerTestBase.DebuggerTestAppPath, Token);
                Session = await browser.OpenSession(
                                            relativeUrl,
                                            OnMessage,
                                            RunLoopStoppedHandler,
                                            _id,
                                            _logger,
                                            _cancellationTokenSource);

                Client = Session.Connection.InspectorClient;

                // await Client.Connect(uri, OnMessage, _cancellationTokenSource.Token);
                // Client.RunLoopStopped += (_, args) =>

                var init_cmds = getInitCmds(Session, _cancellationTokenSource.Token);

                Task<Result> readyTask = Task.Run(async () => Result.FromJson(await WaitFor(READY)));
                init_cmds.Add((READY, readyTask));

                _logger.LogInformation("waiting for the runtime to be ready");
                while (!_cancellationTokenSource.IsCancellationRequested && init_cmds.Count > 0)
                {
                    var cmd_tasks = init_cmds.Select(ct => ct.Item2);
                    Task<Result> t = await Task.WhenAny(cmd_tasks);

                    int cmdIdx = init_cmds.FindIndex(ct => ct.Item2 == t);
                    string cmd_name = init_cmds[cmdIdx].Item1;

                    if (t.IsCanceled)
                    {
                        throw new TaskCanceledException(
                                    $"Command {cmd_name} timed out during init for the test." +
                                    $"Remaining commands: {RemainingCommandsToString(cmd_name, init_cmds)}." +
                                    $"Total time: {(DateTime.Now - start).TotalSeconds}");
                    }

                    if (t.IsFaulted)
                    {
                        _logger.LogError($"Command {cmd_name} failed with {t.Exception}. Remaining commands: {RemainingCommandsToString(cmd_name, init_cmds)}.");
                        throw t.Exception!;
                    }

                    Result res = t.Result;
                    if (res.IsErr)
                        throw new ArgumentException($"Command {cmd_name} failed with: {res.Error}. Remaining commands: {RemainingCommandsToString(cmd_name, init_cmds)}");

                    init_cmds.RemoveAt(cmdIdx);
                }

                _logger.LogInformation("runtime ready, TEST TIME");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex.ToString());
                throw;
            }

            void RunLoopStoppedHandler((RunLoopStopReason reason, Exception? ex) args)
            {
                switch (args.reason)
                {
                    case RunLoopStopReason.Exception:
                        FailAllWaiters(args.ex);
                        break;

                    case RunLoopStopReason.Cancelled when Token.IsCancellationRequested:
                        FailAllWaiters(new TaskCanceledException($"Test timed out (elapsed time: {(DateTime.Now - start).TotalSeconds}"));
                        break;

                    default:
                        FailAllWaiters();
                        break;
                };
            };

            static string RemainingCommandsToString(string cmd_name, IList<(string, Task<Result>)> cmds)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < cmds.Count; i++)
                {
                    var (name, task) = cmds[i];

                    if (cmd_name == name)
                        continue;

                    sb.Append($"[{name}: {task.Status}], ");
                }

                if (sb.Length > 2)
                    sb.Length -= 2;

                return sb.ToString();
            }
        }

        public async Task ShutdownAsync()
        {
            if (Client == null)
                throw new InvalidOperationException($"InspectorClient is null. Duplicate Shutdown?");

            try
            {
                _logger?.LogDebug($"- test done,. let's close the client");
                await Client.Shutdown(_cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex.ToString());
                throw;
            }
            finally
            {
                _cancellationTokenSource.Cancel();
                Client.Dispose();
                _loggerFactory?.Dispose();
                _cancellationTokenSource.Dispose();
            }
        }
    }
}
