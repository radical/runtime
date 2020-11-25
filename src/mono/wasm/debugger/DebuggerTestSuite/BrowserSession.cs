// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

#nullable enable

namespace Microsoft.WebAssembly.Diagnostics
{
    internal class BrowserSession : IAsyncDisposable
    {
        public SessionId Id { get; private set; }
        public BrowserTarget Target { get; private set; }
        public BrowserCdpConnection Connection => Target.Connection;
        public DateTime StartTime { get; } = DateTime.Now;

        private bool _disposed = false;
        //fIXME: disposing

        public BrowserSession(SessionId id, BrowserTarget target)
        {
            Id = id;
            Target = target;
        }

        public async Task<Result> SendCommand(string method, JObject? args, CancellationToken token)
            => await Connection.SendCommand(Id, method, args, token);

        public async Task ShutdownConnection(CancellationToken token)
            => await Connection.DisposeAsync();

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            // FIXME: remove this from Connection

            await Connection.SendCommandNoSession("Target.detachFromTarget", JObject.FromObject(new { sessionId = Id.sessionId }), new CancellationToken());
            Connection.RemoveSession(Id);

            //FIXME: check error
            _disposed = true;
        }
    }
}
