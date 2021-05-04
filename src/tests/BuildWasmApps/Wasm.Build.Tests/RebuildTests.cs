// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            var (product, initiateState) = FirstBuildAOT(projectName, false, buildArgs, host, id);

            _testOutput.WriteLine($"{Environment.NewLine}Rebuilding with no changes ..{Environment.NewLine}");

            string rebuildId = Path.GetRandomFileName();
            var (_, rebuildState) = RebuildAOT(projectName, false, buildArgs, host, id);

            new FileStateComparer(initiateState, rebuildState)
                .Unchanged();
        }

        [Theory]
        [BuildAndRun(host: RunHost.V8, aot: true)]
        public void Rebuild_WithOnlyMainAssemblyChangeAOT(BuildArgs buildArgs, RunHost host, string id)
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

            var initialState = GetFiles(product.BuildPath, "*.bc", "*.o");
            AddFiles(initialState, AppBundleDir(product.BuildPath, buildArgs.Config), "dotnet.wasm", "dotnet.js");

            Dump(initialState, $"intial state from {product.BuildPath}");

            Run(id);

            File.Move(product!.LogFile, Path.ChangeExtension(product.LogFile!, ".first.binlog"));

            _testOutput.WriteLine($"{Environment.NewLine}Rebuilding with no changes ..{Environment.NewLine}");

            string rebuildId = Path.GetRandomFileName();
            File.WriteAllText(Path.Combine(_projectDir!, "Program.cs"), s_mainReturns42 + " ");

            // no-op Rebuild
            BuildProject(buildArgs,
                        () => {},
                        dotnetWasmFromRuntimePack: dotnetWasmFromRuntimePack,
                        id: id,
                        createProject: false,
                        useCache: false);

            var afterRebuildState = GetFiles(product.BuildPath, "*.bc", "*.o");
            AddFiles(afterRebuildState, AppBundleDir(product.BuildPath, buildArgs.Config), "dotnet.wasm", "dotnet.js");

            Run(rebuildId);

            void Run(string buildId) => RunAndTestWasmApp(
                                buildArgs, buildDir: _projectDir, expectedExitCode: 42,
                                test: output => {},
                                host: host, id: buildId, logToXUnit: false);

            new FileStateComparer(initialState, afterRebuildState)
                    .Changed($"{projectName}.dll.bc", $"{projectName}.dll.o", $"dotnet.js", "dotnet.wasm")
                    .Unchanged();
        }

        private (BuildProduct, IDictionary<string, FileState>) FirstBuildAOT(string projectName, bool dotnetWasmFromRuntimePack, BuildArgs buildArgs, RunHost host, string id)
        {
            buildArgs = buildArgs with { ProjectName = projectName };
            buildArgs = GetBuildArgsWith(buildArgs, $"<WasmBuildNative>false</WasmBuildNative>");

            BuildProject(buildArgs,
                        initProject: () => File.WriteAllText(Path.Combine(_projectDir!, "Program.cs"), s_mainReturns42),
                        dotnetWasmFromRuntimePack: dotnetWasmFromRuntimePack,
                        id: id,
                        createProject: true);

            RunAndTestWasmApp(buildArgs, buildDir: _projectDir, expectedExitCode: 42,
                                test: output => {},
                                host: host, id: id, logToXUnit: false);

            if (!_buildContext.TryGetBuildFor(buildArgs, out BuildProduct? product))
                Assert.True(false, $"Test bug: could not get the build product in the cache");

            File.Move(product!.LogFile, Path.ChangeExtension(product.LogFile!, ".first.binlog"));

            var initialState = GetFiles(Path.Combine(product.BuildPath, "obj"), "*.bc", "*.o");
            AddFiles(initialState, AppBundleDir(product.BuildPath, buildArgs.Config), "dotnet.wasm", "dotnet.js");
            Dump(initialState, $"intial state from {product.BuildPath}");

            return (product, initialState);
        }

        private (BuildProduct, IDictionary<string, FileState>) RebuildAOT(string projectName, bool dotnetWasmFromRuntimePack, BuildArgs buildArgs, RunHost host, string rebuildId)
        {
            buildArgs = buildArgs with { ProjectName = projectName };
            buildArgs = GetBuildArgsWith(buildArgs, $"<WasmBuildNative>false</WasmBuildNative>");

            BuildProject(buildArgs,
                        () => {},
                        dotnetWasmFromRuntimePack: false,
                        id: rebuildId,
                        createProject: false,
                        useCache: false);

            if (!_buildContext.TryGetBuildFor(buildArgs, out BuildProduct? product))
                Assert.True(false, $"Test bug: could not get the build product in the cache");

            RunAndTestWasmApp(buildArgs, buildDir: _projectDir, expectedExitCode: 42,
                                test: output => {},
                                host: host, id: rebuildId, logToXUnit: false);

            var initialState = GetFiles(Path.Combine(product!.BuildPath, "obj"), "*.bc", "*.o");
            AddFiles(initialState, AppBundleDir(product.BuildPath, buildArgs.Config), "dotnet.wasm", "dotnet.js");
            Dump(initialState, $"intial state from {product.BuildPath}");

            return (product, initialState);
        }

        private IDictionary<string, FileState> GetFiles(string baseDir, params string[] patterns)
            => AddFiles(new Dictionary<string, FileState>(), baseDir, patterns);

        private IDictionary<string, FileState> AddFiles(IDictionary<string, FileState> table, string baseDir, params string[] patterns)
        {
            //FIXME: static
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true
            };

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

    internal class FileStateComparer
    {
        IDictionary<string, FileState> _first, _second;
        public FileStateComparer(IDictionary<string, FileState> first, IDictionary<string, FileState> second)
        {
            _first = new Dictionary<string, FileState>(first);
            _second = new Dictionary<string, FileState>(second);
        }

        public FileStateComparer Unchanged(params string[] filenames)
            => ChangedInternal(false, filenames);

        public FileStateComparer Changed(params string[] filenames)
            => ChangedInternal(true, filenames);

        private FileStateComparer ChangedInternal(bool expectChanged, params string[] filenames)
        {
            if (filenames.Length == 0)
                filenames = _first.Keys.Select(path => Path.GetFileName(path)).ToArray();

            List<string> toRemove = new();
            foreach (var filename in filenames)
            {
                Console.WriteLine ($"-> file: {filename}");
                string? foundFullPath = _first.Keys!.Where(fullpath => Path.GetFileName(fullpath) == filename).FirstOrDefault();
                Assert.True(foundFullPath != null, $"Could not find any file named {filename}");

                FileState fileStateA = _first[foundFullPath!];

                if (!_second.TryGetValue(foundFullPath!, out FileState? fileStateB))
                    Assert.True(false, $"Could not find file {foundFullPath} in the second build");

                if (expectChanged)
                    Assert.True(fileStateA != fileStateB, $"File expected to be different: {foundFullPath}: {fileStateA}");
                else
                    Assert.True(fileStateA == fileStateB, $"File: {foundFullPath}\nExpected: {fileStateA}\nActual:   {fileStateB}");

                toRemove.Add(foundFullPath!);
            }

            foreach (string fileToRemove in toRemove)
            {
                _first.Remove(fileToRemove);
                _second.Remove(fileToRemove);
            }

            return this;
        }


    }
}
