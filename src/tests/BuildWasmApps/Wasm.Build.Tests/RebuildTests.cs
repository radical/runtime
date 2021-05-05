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
    public class NativeRebuildTests : BuildTestBase
    {
        public NativeRebuildTests(ITestOutputHelper output, SharedBuildPerTestClassFixture buildContext)
            : base(output, buildContext)
        {
            _enablePerTestCleanup = true;
        }

        [Theory]
        [BuildAndRun(host: RunHost.V8, aot: false, parameters: false)]
        [BuildAndRun(host: RunHost.V8, aot: false, parameters: true)]
        public void NoOpRebuild_Relinking(BuildArgs buildArgs, bool nativeRelink, RunHost host, string id)
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
        [BuildAndRun(host: RunHost.V8, aot: false)]
        public void NoOpRebuild(BuildArgs buildArgs, RunHost host, string id)
        {
            (_, var initiateState, buildArgs) = FirstBuildNative(buildArgs, host, id, "noop");

            _testOutput.WriteLine($"{Environment.NewLine}Rebuilding with no changes ..{Environment.NewLine}");

            var (_, rebuildState) = RebuildNative(buildArgs, host, id);
            new FileStateComparer(initiateState, rebuildState)
                .Unchanged();
        }

        [Theory]
        [BuildAndRun(host: RunHost.V8, aot: true)]
        [BuildAndRun(host: RunHost.V8, aot: false)]
        public void Rebuild_WithProgramCS_TrivialChange(BuildArgs buildArgs, RunHost host, string id)
        {
            (_, var initialState, buildArgs) = FirstBuildNative(buildArgs, host, id, "trivial");

            _testOutput.WriteLine($"{Environment.NewLine}Rebuilding with only Program.cs trivial change ..{Environment.NewLine}");
            File.WriteAllText(Path.Combine(_projectDir!, "Program.cs"), s_mainReturns42 + " ");
            var (_, rebuildState) = RebuildNative(buildArgs, host, id);

            new FileStateComparer(initialState, rebuildState)
                    .Changed($"{buildArgs.ProjectName}.dll.bc", $"{buildArgs.ProjectName}.dll.o", $"dotnet.js", "dotnet.wasm")
                    .Unchanged();
        }

        [Theory]
        [BuildAndRun(host: RunHost.V8, aot: true)]
        public void Rebuild_WithProgramCS_TimestampChange(BuildArgs buildArgs, RunHost host, string id)
        {
            (_, var initialState, buildArgs) = FirstBuildNative(buildArgs, host, id, "timestamp");

            _testOutput.WriteLine($"{Environment.NewLine}Rebuilding with only Program.cs trivial change ..{Environment.NewLine}");
            File.WriteAllText(Path.Combine(_projectDir!, "Program.cs"), s_mainReturns42);
            var (_, rebuildState) = RebuildNative(buildArgs, host, id);

            new FileStateComparer(initialState, rebuildState)
                    .Unchanged();
        }

        [Theory]
        [BuildAndRun(host: RunHost.V8, aot: true)]
        [BuildAndRun(host: RunHost.V8, aot: false)]
        public void Rebuild_WithProgramUsingNewAPI(BuildArgs buildArgs, RunHost host, string id)
        {
            (_, var initialState, buildArgs) = FirstBuildNative(buildArgs, host, id, "use_new_api");

            _testOutput.WriteLine($"{Environment.NewLine}Rebuilding with only Program.cs changed ..{Environment.NewLine}");

            string newProgram = @"
                using System.Text;
                public class TestClass {
                    public static int Main()
                    {
                        var sb = new StringBuilder();
                        sb.Append(""123"");
                        System.Console.WriteLine($""sb: {sb}"");
                        return 42 + sb.Length;
                    }
                }";
            File.WriteAllText(Path.Combine(_projectDir!, "Program.cs"), newProgram);

            string rebuildId = Path.GetRandomFileName();
            var (_, rebuildState) = RebuildNative(buildArgs, host, id);

            new FileStateComparer(initialState, rebuildState)
                    .Changed($"{buildArgs.ProjectName}.dll.bc", $"{buildArgs.ProjectName}.dll.o", $"dotnet.js", "dotnet.wasm");

            RunAndTestWasmApp(buildArgs, buildDir: _projectDir, expectedExitCode: 45,
                                test: output => {},
                                host: host, id: id, logToXUnit: false);
        }

        private (BuildProduct, IDictionary<string, FileState>, BuildArgs) FirstBuildNative(BuildArgs buildArgs, RunHost host, string id, string namePrefix="")
        {
            bool relinking = !buildArgs.AOT;

            string projectName = $"{namePrefix}_{buildArgs.Config}_{buildArgs.AOT}";
            buildArgs = buildArgs with { ProjectName = projectName };
            buildArgs = GetBuildArgsWith(buildArgs, $"<WasmBuildNative>{relinking}</WasmBuildNative>");

            BuildProject(buildArgs,
                        initProject: () => File.WriteAllText(Path.Combine(_projectDir!, "Program.cs"), s_mainReturns42),
                        dotnetWasmFromRuntimePack: false,
                        id: id,
                        createProject: true);

            RunAndTestWasmApp(buildArgs, buildDir: _projectDir, expectedExitCode: 42,
                                test: output => {},
                                host: host, id: id, logToXUnit: false);

            if (!_buildContext.TryGetBuildFor(buildArgs, out BuildProduct? product))
                Assert.True(false, $"Test bug: could not get the build product in the cache");

            File.Move(product!.LogFile, Path.ChangeExtension(product.LogFile!, ".first.binlog"));

            var state = GetFiles(Path.Combine(product.BuildPath, "obj"), "*.bc", "*.o");
            AddFiles(state, AppBundleDir(product.BuildPath, buildArgs.Config), "dotnet.wasm", "dotnet.js");
            //Dump(state, $"intial state from {product.BuildPath}");

            return (product, state, buildArgs);
        }

        private (BuildProduct, IDictionary<string, FileState>) RebuildNative(BuildArgs buildArgs, RunHost host, string rebuildId)
        {
            BuildProject(buildArgs,
                        () => {},
                        dotnetWasmFromRuntimePack: false,
                        id: rebuildId,
                        createProject: false,
                        useCache: false);

            if (!_buildContext.TryGetBuildFor(buildArgs, out BuildProduct? product))
                Assert.True(false, $"Test bug: could not get the build product in the cache");

            var state = GetFiles(Path.Combine(product!.BuildPath, "obj"), "*.bc", "*.o");
            AddFiles(state, AppBundleDir(product.BuildPath, buildArgs.Config), "dotnet.wasm", "dotnet.js");
            //Dump(state, $"rebuild state from {product.BuildPath}");

            return (product, state);
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
                    Assert.True(fileStateA == fileStateB, $"File expected to be identical: {foundFullPath}\nExpected: {fileStateA}\nActual:   {fileStateB}");

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
