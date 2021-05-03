// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Xunit;
using Xunit.Abstractions;

#nullable enable

namespace Wasm.Build.Tests
{
    public class RebuildTests : BuildTestBase
    {
        public RebuildTests(ITestOutputHelper output, SharedBuildPerTestClassFixture buildContext)
            : base(output, buildContext)
        {
        }

        [Theory]
        [BuildAndRun(host: RunHost.V8, aot: false, parameters: false)]
        [BuildAndRun(host: RunHost.V8, aot: false, parameters: true)]
        // [BuildAndRun(host: RunHost.V8, aot: true,  parameters: false)]
        public void NoOpRebuild(BuildArgs buildArgs, bool nativeRelink, RunHost host, string id)
        {
            string projectName = $"rebuild_{buildArgs.Config}_{buildArgs.AOT}";
            bool dotnetWasmFromRuntimePack = !nativeRelink && !buildArgs.AOT;

            buildArgs = buildArgs with { ProjectName = projectName };
            buildArgs = GetBuildArgsWith(buildArgs, $"<WasmBuildNative>{(nativeRelink ? "true" : "false")}</WasmBuildNative>");

            BuildProject(buildArgs,
                        initProject: () => File.WriteAllText(Path.Combine(_projectDir!, "Program.cs"), s_mainReturns42),
                        dotnetWasmFromRuntimePack: dotnetWasmFromRuntimePack,
                        id: id,
                        createProject: true);

            Run();

            if (!_buildContext.TryGetBuildFor(buildArgs, out BuildProduct? product))
                Assert.True(false, $"Test bug: could not get the build product in the cache");

            File.Move(product!.LogFile, Path.ChangeExtension(product.LogFile!, ".first.binlog"));

            _testOutput.WriteLine($"{Environment.NewLine}Rebuilding with no changes ..{Environment.NewLine}");

            // no-op Rebuild
            BuildProject(buildArgs,
                        () => {},
                        dotnetWasmFromRuntimePack: dotnetWasmFromRuntimePack,
                        id: id,
                        createProject: false,
                        useCache: false);

            Run();

            void Run() => RunAndTestWasmApp(
                                buildArgs, buildDir: _projectDir, expectedExitCode: 42,
                                test: output => {},
                                host: host, id: id);
        }

        [Theory]
        [BuildAndRun(host: RunHost.V8, aot: true)]
        public void NoOpRebuildAOT(BuildArgs buildArgs, RunHost host, string id)
        {
            string projectName = $"rebuild_{buildArgs.Config}_{buildArgs.AOT}";
            bool dotnetWasmFromRuntimePack = false;

            buildArgs = buildArgs with { ProjectName = projectName };
            buildArgs = GetBuildArgsWith(buildArgs, extraProperties: "<WasmNativeStrip>false</WasmNativeStrip>");

            _testOutput.WriteLine($"{Environment.NewLine}First build{Environment.NewLine}");
            BuildProject(buildArgs,
                        initProject: () => File.WriteAllText(Path.Combine(_projectDir!, "Program.cs"), s_mainReturns42),
                        dotnetWasmFromRuntimePack: dotnetWasmFromRuntimePack,
                        id: id,
                        createProject: true);


            if (!_buildContext.TryGetBuildFor(buildArgs, out BuildProduct? product))
                throw new Exception($"Test bug: failed to find the build in the cache for {buildArgs}");

            var initialState = GetFiles(product.BuildPath, "*.bc", "*.o", "dotnet.wasm", "dotnet.js");
            Dump(initialState, $"intial state from {product.BuildPath}");

            Run(id);

            File.Move(product!.LogFile, Path.ChangeExtension(product.LogFile!, ".first.binlog"));

            _testOutput.WriteLine($"{Environment.NewLine}Rebuilding with no changes ..{Environment.NewLine}");

            string rebuildId = Path.GetRandomFileName();

            // no-op Rebuild
            BuildProject(buildArgs,
                        () => {},
                        dotnetWasmFromRuntimePack: dotnetWasmFromRuntimePack,
                        id: id,
                        createProject: false,
                        useCache: false);

            var afterRebuildState = GetFiles(product.BuildPath, "*.bc", "*.o", "dotnet.wasm", "dotnet.js");

            Run(rebuildId);

            void Run(string buildId) => RunAndTestWasmApp(
                                buildArgs, buildDir: _projectDir, expectedExitCode: 42,
                                test: output => {},
                                host: host, id: buildId, logToXUnit: false);

            foreach (var initialKvp in initialState)
            {
                string file = initialKvp.Key;
                FileState fileStateA = initialKvp.Value;

                if (!afterRebuildState.TryGetValue(file, out FileState? fileStateB))
                    Assert.True(false, $"Could not find file {file} in the second build");

                Assert.True(fileStateA == fileStateB, $"File: {file}\nExpected: {fileStateA}\nActual:   {fileStateB}");
            }
        }

        private IDictionary<string, FileState> GetFiles(string baseDir, params string[] patterns)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true
            };

            Dictionary<string, FileState> table = new();

            foreach (var pattern in patterns)
            {
                Console.WriteLine ($"-- looking for {pattern} under {baseDir}");
                var filesFound = Directory.EnumerateFiles(baseDir, pattern, options);
                foreach (var file in filesFound)
                {
                    Console.WriteLine ($"\tfound {file}");
                    DateTime writeTime = File.GetLastWriteTimeUtc(file);
                    long fileSize = new FileInfo(file).Length;
                    table.Add(file, new FileState(writeTime, fileSize));
                }
            }

            return table;
        }

        private static void Dump(IDictionary<string, FileState> table, string? label=null)
        {
            Console.WriteLine ($"-- Dump ({label}) --");

            foreach (var key in table.Keys)
                Console.WriteLine ($"[{key}] = {table[key]}");
        }
    }

    internal record FileState(DateTime LastWriteTimeUtc, long Size);
}
