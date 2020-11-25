// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading.Tasks;

#nullable enable

namespace Microsoft.WebAssembly.Diagnostics
{
    internal class BrowserTarget : IAsyncDisposable
    {
        public string BrowserContextId { get; private set; }
        public string Id { get; private set; }
        public string Url { get; private set; }
        public BrowserCdpConnection Connection { get; private set; }
        private bool _disposed = false;

        public BrowserTarget(string browserContextId, string id, string url, BrowserCdpConnection connection)
        {
            BrowserContextId = browserContextId;
            Id = id;
            Url = url;
            Connection = connection;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            // FIXME: remove this from Connection
            // var res = await Connection.SendCommand(
            //     "Target.closeTarget",
            //     JObject.FromObject(new { targetId = Id }),
            //     new CancellationToken());
            //FIXME: and disposeBrowserContext?

            // if (res.IsErr)
            //     Connection.Logger.LogError($"Target.closeTarget failed with {res}");
            _disposed = true;
            await Task.CompletedTask;
        }
    }
}
