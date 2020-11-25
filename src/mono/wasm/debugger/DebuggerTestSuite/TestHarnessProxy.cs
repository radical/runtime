// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

#nullable enable

namespace Microsoft.WebAssembly.Diagnostics
{
    public class TestHarnessProxy
    {
        static IWebHost? host;
        static Task? hostTask;
        static CancellationTokenSource cts = new CancellationTokenSource();
        static object proxyLock = new object();

        private static Uri? s_endpoint = null;
        public static Uri Endpoint => s_endpoint ?? throw new ArgumentException($"Cannot access `{nameof(Endpoint)}` before `{nameof(TestHarnessProxy)}` has been started");
        public static ProxyLauncherData LauncherData { get; } = new ();

        public static Task Start(string appPath, ILogger logger, CancellationToken token)
        {
            lock (proxyLock)
            {
                if (host != null && hostTask != null)
                    return hostTask;

                host = WebHost.CreateDefaultBuilder()
                    .UseSetting("UseIISIntegration", false.ToString())
                    .ConfigureAppConfiguration((hostingContext, config) =>
                    {
                        config.AddEnvironmentVariables(prefix: "WASM_TESTS_");
                    })
                    .ConfigureLogging(logging =>
                    {
                        logging.AddSimpleConsole(c =>
                        {
                            c.ColorBehavior = LoggerColorBehavior.Enabled;
                            c.TimestampFormat = "[HH:mm:ss.fff] ";
                            c.SingleLine = true;
                        });
                    })
                    .ConfigureServices((ctx, services) =>
                    {
                        services.Configure<TestHarnessOptions>(ctx.Configuration);
                        services.Configure<TestHarnessOptions>(options =>
                        {
                            options.AppPath = appPath;
                            options.DevToolsUrl = new Uri("http://localhost:0");
                        });
                    })
                    .UseStartup<TestHarnessStartup>()
                    .UseUrls("http://127.0.0.1:0")
                    .Build();

                logger.LogDebug("Starting webserver, and the proxy launcher");
                hostTask = host.StartAsync(cts.Token).ContinueWith(t =>
                {
                    s_endpoint = new Uri(host.ServerFeatures
                                        .Get<IServerAddressesFeature>()
                                        .Addresses
                                        .First());
                }, TaskContinuationOptions.OnlyOnRanToCompletion);
            }

            logger.LogDebug("WebServer Ready!");
            return hostTask;
        }
    }
}
