// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Microsoft.WebAssembly.Diagnostics
{
    internal class InspectorClient : DevToolsClient
    {
        Dictionary<MessageId, TaskCompletionSource<Result>> pending_cmds = new();
        int next_cmd_id;
        Func<JObject, CancellationToken, Task> _catchAllMessageHandler;
        Dictionary<SessionId, Func<string, JObject, CancellationToken, Task>> onMessageHandlers = new();

        public InspectorClient(string id, ILogger logger) : base (id, logger) {}

        Task HandleMessage(string msg, CancellationToken token)
        {
            try
            {
                var res = JObject.Parse(msg);
                string method = res["method"]?.Value<string>();

                if (res["id"] == null) {
                    string sidStr = res["sessionId"]?.Value<string>();
                    // }
                    if (sidStr == null)
                        return _catchAllMessageHandler(res, token);

                    var sessionId = new SessionId(sidStr);
                    if (onMessageHandlers.TryGetValue(sessionId, out var onMessage)) {
                        // Console.WriteLine($"\tcalling onMessage");
                        return onMessage(res["method"].Value<string>(), res["params"] as JObject, token);
                    } else {
                        return _catchAllMessageHandler(res, token);
                    }
                }

                var id = res.ToObject<MessageId>();
                if (!pending_cmds.Remove(id, out var item))
                    logger.LogError ($"Unable to find command {id}");

                item.SetResult(Result.FromJson(res));
                return null;
            } catch (Exception ex) {
                Console.WriteLine($"------- {ex} -------");
                logger.LogError(ex.ToString());
                // throw;

                return null;
            }
        }

        public async Task Connect(
            Uri uri,
            Func<JObject, CancellationToken, Task> catchAllMessageHandler,
            CancellationToken token)
        {
            _catchAllMessageHandler = catchAllMessageHandler;
            RunLoopStopped += (_, args) =>
            {
                logger.LogDebug($"InspectorClient: Let's fail all the pending cmds (nr: {pending_cmds.Count})!");
                if (args.reason == RunLoopStopReason.Cancelled)
                {
                    foreach (var cmd in pending_cmds.Values)
                        cmd.SetCanceled();
                }
                else
                {
                    //FIXME: um args.ex should be non-null
                    foreach (var cmd in pending_cmds.Values)
                        cmd.SetException(args.ex);
                }

            };
            await ConnectWithMainLoops(uri, HandleMessage, token);
        }

        public void AddMessageHandlerForSession(SessionId sessionId, Func<string, JObject, CancellationToken, Task> onEvent)
        {
            // logger.LogDebug($">>> Adding handler for {sessionId}");
            onMessageHandlers.Add(sessionId, onEvent);
        }

        public void RemoveMessageHandlerForSession(SessionId sessionId)
            => onMessageHandlers.Remove(sessionId);

        public Task<Result> SendCommand(string method, JObject args, CancellationToken token)
            => SendCommand(new SessionId(null), method, args, token);

        public Task<Result> SendCommand(SessionId sessionId, string method, JObject args, CancellationToken token)
        {
            if (!IsRunning)
                throw new InvalidOperationException($"DevToolsClient.RunLoop is not running cmd: {method}");

            int id = ++next_cmd_id;
            if (args == null)
                args = new JObject();

            var o = JObject.FromObject(new
            {
                id = id,
                method = method,
                @params = args
            });
            if (sessionId != SessionId.Null)
                o["sessionId"] = sessionId.sessionId;

            var tcs = new TaskCompletionSource<Result>();
            pending_cmds[new MessageId(sessionId.sessionId, id)] = tcs;

            var str = o.ToString();
            // logger.LogDebug($"SendCommand: id: {id} method: {method} params: {args}");
            // Console.WriteLine($"SendCommand: id: {id} method: {method} params: {args}");

            var bytes = Encoding.UTF8.GetBytes(str);
            Send(bytes, token);
            return tcs.Task;
        }

        protected override void LogState(StringBuilder sb)
        {
            base.LogState(sb);
            sb.Append($"Commands waiting for response: {pending_cmds.Count}\n");
            foreach (MessageId cmd_id in pending_cmds.Keys)
                sb.Append($"\t{cmd_id}\n");
        }
    }
}
