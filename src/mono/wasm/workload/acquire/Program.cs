// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace acquire
{
    public class Program
    {
        private const string ManifestVersion = "6.0.100";

        private static string MuxerPath { get; } = GetDotnetPath();

        private static string GetDotnetPath()
        {
            // Process.MainModule is app[.exe] and not `dotnet`. We can instead calculate the dotnet SDK path
            // by looking at the shared fx directory instead.
            // depsFile = /dotnet/shared/Microsoft.NETCore.App/6.0-preview2/Microsoft.NETCore.App.deps.json
            var depsFile = (string)AppContext.GetData("FX_DEPS_FILE");
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(depsFile), "..", "..", "..", "dotnet" + (OperatingSystem.IsWindows() ? ".exe" : "")));
        }

        public static int Main(string[] args)
        {
            Console.WriteLine(MuxerPath);
            string sdkDirectory = Path.GetDirectoryName(MuxerPath);
            var tempDirectory = Path.Combine(Directory.GetCurrentDirectory(), "tmp", Path.GetRandomFileName());
            var restoreDirectory = Path.Combine(tempDirectory, ".nuget");
            string runtimeVersion = null;
            string manifestPath = null;
            string workloadName = null;

            foreach (var arg in args) {
                if (arg.StartsWith ("--runtime="))
                    runtimeVersion = arg.Replace ("--runtime=", "");
                else if (arg.StartsWith ("--manifest"))
                    manifestPath = arg.Replace ("--manifest=", "");
                else if (arg.StartsWith ("--workload"))
                    workloadName = arg.Replace ("--workload=", "");
                else
                    sdkDirectory = arg;
            }

            if (string.IsNullOrEmpty(workloadName))
            {
                Console.WriteLine ($"Missing --workload=<workload_name> argument");
                return 1;
            }

            try
            {
                var restore = Restore(tempDirectory, restoreDirectory, runtimeVersion, manifestPath, workloadName, out var packs);
                if (restore != 0)
                {
                    return restore;
                }

                var sourceManifestDirectory = Path.Combine(restoreDirectory, workloadName.ToLower(), ManifestVersion);
                var targetManifestDirectory = Path.Combine(sdkDirectory, "sdk-manifests", ManifestVersion, workloadName);
                Move(sourceManifestDirectory, targetManifestDirectory);

                foreach (var (id, version) in packs)
                {
                    var source = Path.Combine(restoreDirectory, id.ToLowerInvariant(), version);
                    var destination = Path.Combine(sdkDirectory, "packs", id, version);

                    Move(source, destination);
                }

                var sdkVersionProc = Process.Start(new ProcessStartInfo
                {
                    FileName = MuxerPath,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                });
                sdkVersionProc.WaitForExit();
                var sdkVersion = sdkVersionProc.StandardOutput.ReadToEnd().Trim();
                var sentinelPath = Path.Combine(sdkDirectory, "sdk", sdkVersion, "EnableWorkloadResolver.sentinel");
                Console.WriteLine($"Writing sentinel to {sentinelPath}.");

                File.WriteAllBytes(sentinelPath, Array.Empty<byte>());
            }
            finally
            {
                //Directory.Delete(tempDirectory, recursive: true);
            }

            return 0;
        }

        private static void Move(string source, string destination)
        {
            Console.WriteLine($"Moving {source} to {destination}...");
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination));

            Directory.Move(source, destination);
        }

        private static int Restore(string tempDirectory, string restoreDirectory, string runtimeVersion, string manifestPath, string workloadName, out List<(string, string)> packs)
        {
            packs = null;

            var restoreProject = Path.Combine(tempDirectory, "restore", "Restore.csproj");
            var restoreProjectDirectory = Directory.CreateDirectory(Path.GetDirectoryName(restoreProject));

            File.WriteAllText(Path.Combine(restoreProjectDirectory.FullName, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(restoreProjectDirectory.FullName, "Directory.Build.targets"), "<Project />");

            var projectFile = $@"
<Project Sdk=""Microsoft.NET.Sdk"">
    <PropertyGroup>
        <TargetFramework>net6.0</TargetFramework>
    </PropertyGroup>
    <ItemGroup>
        <PackageReference Include=""{workloadName}"" Version=""6.0.0-*"" />
    </ItemGroup>
</Project>
";
            File.WriteAllText(restoreProject, projectFile);

            Console.WriteLine("Restoring...");

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = MuxerPath,
                ArgumentList = { "restore", restoreProject },
                Environment =
                {
                    ["NUGET_PACKAGES"] = restoreDirectory,
                },
            });
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine($"Unable to restore {workloadName} workload.");
                return 1;
            }

            var manifestDirectory = Path.Combine(restoreDirectory, workloadName.ToLower());
            var version = Directory.EnumerateDirectories(manifestDirectory).First();

            manifestDirectory = Path.Combine(manifestDirectory, ManifestVersion);
            Directory.Move(version, manifestDirectory);

            if (manifestPath != null)
                File.Copy(Path.GetFullPath(manifestPath), Path.Combine(manifestDirectory, "WorkloadManifest.json"), true);

            var manifestPath2 = Path.Combine(manifestDirectory, "WorkloadManifest.json");
            var manifest = JsonSerializer.Deserialize<PackInformation>(File.ReadAllBytes(manifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));

            projectFile = @"
<Project Sdk=""Microsoft.NET.Sdk"">
    <PropertyGroup>
        <TargetFramework>net6.0</TargetFramework>
        <NoWarn>$(NoWarn);NU1213</NoWarn>
    </PropertyGroup>
    <ItemGroup>
";
            packs = new List<(string id, string version)>();
            foreach (var item in manifest.Packs)
            {
                var packageName = item.Key;
                if (item.Value.AliasTo is Dictionary<string, string> alias)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        packageName = Environment.Is64BitProcess ? alias["win-x64"] : alias["win-x86"];
                    }
                    else if (OperatingSystem.IsMacOS())
                    {
                        packageName = alias["osx-x64"];
                    }
                    else if (OperatingSystem.IsLinux())
                    {
                        packageName = alias["linux-x64"];
                    }
                    else
                    {
                        Console.Error.WriteLine("Unsupported platform.");
                        return 1;
                    }
                }
                var version2 = runtimeVersion ?? item.Value.Version;
                //version2 = "6.0.0-preview.4.21174.6";
                var name = packageName.Contains("cross") && false ? "FrameworkReference" : "PackageReference";
                projectFile += $"<{name} Include=\"{packageName}\" Version=\"{version2}\" />";
                packs.Add((packageName, version2));
            }

            projectFile += @"
    </ItemGroup>
</Project>
";
            File.WriteAllText(restoreProject, projectFile);

            process = Process.Start(new ProcessStartInfo
            {
                FileName = MuxerPath,
                ArgumentList = { "restore", restoreProject },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                Environment =
                {
                    ["NUGET_PACKAGES"] = restoreDirectory,
                },
            });
            process.WaitForExit();


            return 0;
        }

        private record PackInformation(IDictionary<string, PackVersionInformation> Packs);

        private record PackVersionInformation(string Version, [property: JsonPropertyName("alias-to")] Dictionary<string, string> AliasTo);
    }
}
