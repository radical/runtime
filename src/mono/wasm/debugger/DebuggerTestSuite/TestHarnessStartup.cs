// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json.Linq;

namespace Microsoft.WebAssembly.Diagnostics
{
    public class TestHarnessStartup
    {
        public TestHarnessStartup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; set; }
        public ILogger<TestHarnessProxy> ServerLogger { get; private set; }
        private ILoggerFactory _loggerFactory;

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddRouting()
                .Configure<TestHarnessOptions>(Configuration);
        }

        async Task SendNodeVersion(HttpContext context)
        {
            ServerLogger.LogTrace("hello chrome! json/version");
            var resp_obj = new JObject();
            resp_obj["Browser"] = "node.js/v9.11.1";
            resp_obj["Protocol-Version"] = "1.1";

            var response = resp_obj.ToString();
            await context.Response.WriteAsync(response, new CancellationTokenSource().Token);
        }

        async Task SendNodeList(HttpContext context)
        {
            ServerLogger.LogTrace("webserver: hello chrome! json/list");
            try
            {
                var response = new JArray(JObject.FromObject(new
                {
                    description = "node.js instance",
                    devtoolsFrontendUrl = "chrome-devtools://devtools/bundled/inspector.html?experiments=true&v8only=true&ws=localhost:9300/91d87807-8a81-4f49-878c-a5604103b0a4",
                    faviconUrl = "https://nodejs.org/static/favicon.ico",
                    id = "91d87807-8a81-4f49-878c-a5604103b0a4",
                    title = "foo.js",
                    type = "node",
                    webSocketDebuggerUrl = "ws://localhost:9300/91d87807-8a81-4f49-878c-a5604103b0a4"
                })).ToString();

                ServerLogger.LogTrace($"webserver: sending: {response}");
                await context.Response.WriteAsync(response, new CancellationTokenSource().Token);
            }
            catch (Exception e) { ServerLogger.LogError(e, "webserver: SendNodeList failed"); }
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IOptionsMonitor<TestHarnessOptions> optionsAccessor, IWebHostEnvironment env, ILogger<TestHarnessProxy> logger, ILoggerFactory loggerFactory)
        {
            this.ServerLogger = logger;
            this._loggerFactory = loggerFactory;

            // _launcherData = app.ApplicationServices.GetRequiredService<ProxyLauncherData>();

            app.UseWebSockets();
            // app.UseStaticFiles();

            TestHarnessOptions options = optionsAccessor.CurrentValue;

            var provider = new FileExtensionContentTypeProvider();
            provider.Mappings[".wasm"] = "application/wasm";

            foreach (var extn in new string[] { ".dll", ".pdb", ".dat", ".blat" })
            {
                provider.Mappings[extn] = "application/octet-stream";
            }

            ServerLogger.LogInformation($"Starting webserver with appPath: {options.AppPath}");

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(options.AppPath),
                ServeUnknownFileTypes = true, //Cuz .wasm is not a known file type :cry:
                // RequestPath = "",
                ContentTypeProvider = provider
            });

            var devToolsUrl = options.DevToolsUrl;
            app.UseRouter(router =>
            {
                router.MapGet("/connect-to-devtools/{browserInstanceId:int:required}", async context =>
                {
                    try
                    {
                        var id = context.Request.RouteValues["browserInstanceId"].ToString();
                        string testId = "unknown";
                        if (context.Request.Query.TryGetValue("testId", out StringValues values))
                            testId = values.ToString();

                        var testLogger = _loggerFactory.CreateLogger($"{typeof(TestHarnessProxy)}-{testId}");

                        testLogger.LogDebug($"New test request for browserId: {id}, test_id: {testId}, with kestrel connection id: {context.Connection.Id}");
                        if (!TestHarnessProxy.LauncherData.IdToDevToolsUrl.TryGetValue(id, out Uri remoteConnectionUri))
                            throw new Exception($"Unknown browser id {id}");

                        // string logFilename = $"{testId}-proxy.log";
                        // var proxyLoggerFactory = LoggerFactory.Create(
                        //     builder => builder
                        //         // .AddFile(logFilename, minimumLevel: LogLevel.Debug)
                        //         .AddFilter(null, LogLevel.Trace));

                        var proxy = new DebuggerProxy(_loggerFactory, null, testId);
                        var browserUri = remoteConnectionUri;// options.RemoteConnectionUri;
                        var ideSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

                        await proxy.Run(browserUri, ideSocket).ConfigureAwait(false);
                        // Console.WriteLine("Proxy done");
                        testLogger.LogDebug($"Closing proxy for browser {context.Request.Path}{context.Request.QueryString}");
                    }
                    catch (Exception ex)
                    {
                        ServerLogger.LogError($"{context.Request.Path}{context.Request.QueryString} failed with {ex}");
                    }
                });
            });

            // if (options.NodeApp != null)
            // {
            //     Logger.LogTrace($"Doing the nodejs: {options.NodeApp}");
            //     var nodeFullPath = Path.GetFullPath(options.NodeApp);
            //     Logger.LogTrace(nodeFullPath);
            //     var psi = new ProcessStartInfo();

            //     psi.UseShellExecute = false;
            //     psi.RedirectStandardError = true;
            //     psi.RedirectStandardOutput = true;

            //     psi.Arguments = $"--inspect-brk=localhost:0 {nodeFullPath}";
            //     psi.FileName = "node";

            //     app.UseRouter(router =>
            //     {
            //         //Inspector API for using chrome devtools directly
            //         router.MapGet("json", SendNodeList);
            //         router.MapGet("json/list", SendNodeList);
            //         router.MapGet("json/version", SendNodeVersion);
            //         router.MapGet("launch-done-and-connect", async context =>
            //         {
            //             await LaunchAndServe(psi, context, null);
            //         });
            //     });
            // }
        }
    }
}
