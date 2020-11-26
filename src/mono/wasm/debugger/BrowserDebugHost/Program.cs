// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.WebAssembly.Diagnostics
{
    public class ProxyOptions
    {
        public Uri DevToolsUrl { get; set; } = new Uri("http://localhost:9222");

        public int? OwnerPid { get; set; }
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            Uri devToolsUri = new ($"http://localhost:9222");
            if (args.Length >= 1)
                devToolsUri = new Uri($"http://localhost:{args[0]}");

            string proxyUrl = "http://127.0.0.1:0";
            if (args.Length >= 2)
                proxyUrl = $"http://127.0.0.1:{args[1]}";

            Console.WriteLine ($"Chrome devtools: {devToolsUri}");

            IWebHost host = new WebHostBuilder()
                .UseSetting("UseIISIntegration", false.ToString())
                .UseKestrel()
                .UseContentRoot(Directory.GetCurrentDirectory())
                .UseStartup<Startup>()
                .ConfigureAppConfiguration((hostingContext, config) =>
                {
                    config.AddCommandLine(args);
                })
                .ConfigureServices(services =>
                {
                    services.Configure<ProxyOptions>(options =>
                    {
                        options.DevToolsUrl = devToolsUri;
                    });
                })
                .UseUrls(proxyUrl)
                .Build();

            host.Run();
        }
    }
}
