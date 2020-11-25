// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

#nullable enable

namespace Microsoft.WebAssembly.Diagnostics
{
    internal class BrowserInstance : IAsyncDisposable
    {
        static int s_nextId = 0;

        private Uri _remoteConnectionUri;
        public int Id { get; private set; }

        private Process _process;
        private ILogger _poolLogger;

        // private ConcurrentDictionary<string, BrowserCdpConnection> targets = new ConcurrentDictionary<string, BrowserCdpConnection>();
        public bool HasExited => _process.HasExited;

        public CancellationToken CancellationToken => _cancellationTokenSource.Token;
        private CancellationTokenSource _cancellationTokenSource;

        private BrowserInstance(Uri remoteConnectionUri, Process process, ILogger logger)
        {
            _remoteConnectionUri = remoteConnectionUri;
            _process = process;
            _poolLogger = logger;
            _cancellationTokenSource = new CancellationTokenSource();

            Id = Interlocked.Increment(ref s_nextId);

            _poolLogger.LogDebug($"Adding [{Id}] = {remoteConnectionUri}");
            TestHarnessProxy.LauncherData.IdToDevToolsUrl[Id.ToString()] = remoteConnectionUri;
        }

        private async Task<BrowserCdpConnection> OpenConnection(string testId, ILogger testLogger)
        {
            return await BrowserCdpConnection.Open(Id, testId, testLogger, _cancellationTokenSource);
        }

        public static async Task<BrowserInstance> StartAsync(ILogger logger, CancellationToken token, int port=0)
        {
            (var _process, var uri) = await LaunchChrome(new []
            {
                "--headless",
                "--no-first-run",
                "--disable-gpu",
                "--lang=en-US",
                "--incognito",
                $"--remote-debugging-port={port}",
                "--user-data-dir=/tmp/asd",
                "--enable"

            }, logger);

            return new BrowserInstance(uri, _process, logger);
        }

        public async Task<BrowserSession> OpenSession(string relativeUrl,
                                            Func<string, JObject, CancellationToken, Task> onMessage,
                                            Action<(RunLoopStopReason reason, Exception? ex)> onRunLoopFailedOrCanceled,
                                            string testId,
                                            ILogger logger,
                                            CancellationTokenSource cts,
                                            BrowserCdpConnection? connection=null,
                                            BrowserTarget? target=null)
        {

            if (connection == null)
            {
                connection = await OpenConnection(testId, logger);
                connection.InspectorClient!.RunLoopStopped += (_, args) => onRunLoopFailedOrCanceled(args);
            }

            var session = await connection.OpenSession(//this,
                                    $"{TestHarnessProxy.Endpoint.Scheme}://{TestHarnessProxy.Endpoint.Authority}{relativeUrl}",
                                    onMessage,
                                    logger,
                                    testId,
                                    cts,
                                    target);

            return session;
        }

        static async Task<(Process, Uri)> LaunchChrome(string[] args, ILogger logger)
        {
            var psi = new ProcessStartInfo
            {
                Arguments = string.Join(' ', args),
                UseShellExecute = false,
                FileName = FindChromePath(),
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            var tcs = new TaskCompletionSource<string>();

            logger.LogDebug($"Launching browser with '{psi.FileName}' args: '{psi.Arguments}'");
            var process = Process.Start(psi);
            try
            {
                process!.ErrorDataReceived += (sender, e) =>
                {
                    string? str = e.Data;
                    logger.LogTrace($"browser-stderr: {str}");

                    if (tcs.Task.IsCompleted)
                        return;

                    var match = parseConnection.Match(str!);
                    if (match.Success)
                    {
                        tcs.TrySetResult(match.Groups[1].Captures[0].Value);
                    }
                };

                process.OutputDataReceived += (sender, e) =>
                {
                    logger.LogTrace($"browser-stdout: {e.Data}");
                };

                process.Exited += (_, e) => logger.LogDebug($"browser exited with {process.ExitCode}");

                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                if (await Task.WhenAny(tcs.Task, Task.Delay(5000)) != tcs.Task)
                {
                    logger.LogError("Didnt get the con string after 5s.");

                    process.CancelErrorRead();
                    process.CancelOutputRead();
                    process.Kill();
                    process.WaitForExit();
                    process.Close();


                    throw new Exception("Didn't get the remote connection string after 5s");
                }

                var line = await tcs.Task;
                logger.LogDebug($"Chrome devtools listening on {line}");

                return (process, new Uri(line));
            }
            catch (Exception e)
            {
                logger.LogError("got exception {0}", e);
                throw;
            }
        }

        // public void Dispose()
        // {
        // }

        private bool _disposing = false;
        private bool _disposed = false;
        public async ValueTask DisposeAsync()
        {
            if (_disposed || _disposing)
                return;

            _disposing = true;
            //FIXME: um..this Id type, and the type in the dict need to match!
            TestHarnessProxy.LauncherData.IdToDevToolsUrl.Remove(Id.ToString());
            await Task.CompletedTask;
            // var tasks = new List<ValueTask>();
            // foreach (var tgt in targets.Values)
                // await tgt.DisposeAsync();
                // tasks.Add(tgt.DisposeAsync());

            // await Task.WhenAll(tasks);
            _disposing = false;
            _disposed = true;
        }

        static string[] PROBE_LIST = {
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
            "/Applications/Google Chrome Canary.app/Contents/MacOS/Google Chrome Canary",
            "/usr/bin/chromium",
            "/usr/bin/chromium-browser",
        };

        static string? chrome_path;

        static string FindChromePath()
        {
            if (chrome_path != null)
                return chrome_path;

            foreach (var s in PROBE_LIST)
            {
                if (File.Exists(s))
                {
                    chrome_path = s;
                    // Console.WriteLine($"Using chrome path: ${s}");
                    return s;
                }
            }
            throw new Exception("Could not find an installed Chrome to use");
        }

        static Regex parseConnection = new Regex(@"listening on (ws?s://[^\s]*)");
    }
}
