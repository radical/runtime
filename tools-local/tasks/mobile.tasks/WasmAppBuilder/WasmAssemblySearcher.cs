// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

public class WasmAssemblySearcher : Task
{
    public string? MainAssembly { get; set; }

    // If true, continue when a referenced assembly cannot be found.
    // If false, throw an exception.
    public bool SkipMissingAssemblies { get; set; }

    // Either one of these two need to be set
    public string[]? AssemblySearchPaths { get; set; }
    public string[]? Assemblies { get; set; }
    public string[]? ExtraAssemblies { get; set; }

    // The set of assemblies the app will use
    [Output]
    public string[]? ReferencedAssemblies { get; private set; }

    private SortedDictionary<string, Assembly> _assemblies = new SortedDictionary<string, Assembly>();

    public override bool Execute ()
    {
        if (AssemblySearchPaths == null && Assemblies == null)
        {
            Log.LogError("Either the AssemblySearchPaths or the Assemblies property needs to be set.");
            return false;
        }

        Resolver? _resolver;

        if (AssemblySearchPaths != null)
        {
            if (MainAssembly == null)
            {
                Log.LogError($"When AssemblySearchPaths is specified, the MainAssembly property needs to be set.");
                return false;
            }

            var mainAssemblyFullPath = Path.GetFullPath(MainAssembly);

            if (!File.Exists(mainAssemblyFullPath))
            {
                Log.LogError($"Could not find main assembly '{mainAssemblyFullPath}'");
                return false;
            }

            // Collect and load assemblies used by the app
            foreach (var path in AssemblySearchPaths)
            {
                if (!Directory.Exists(path))
                {
                    Log.LogError($"Directory '{path}' does not exist or is not a directory.");
                    return false;
                }
            }
            _resolver = new Resolver(AssemblySearchPaths);
            var mlc = new MetadataLoadContext(_resolver, "System.Private.CoreLib");

            var mainAssembly = mlc.LoadFromAssemblyPath(mainAssemblyFullPath);
            Add(mlc, mainAssembly);

            if (ExtraAssemblies != null)
            {
                foreach (var asm in ExtraAssemblies)
                {
                    var asmFullPath = Path.GetFullPath(asm);
                    try
                    {
                        var refAssembly = mlc.LoadFromAssemblyPath(asmFullPath);
                        Add(mlc, refAssembly);
                    }
                    catch (Exception ex) when (ex is FileLoadException || ex is BadImageFormatException || ex is FileNotFoundException)
                    {
                        if (SkipMissingAssemblies)
                            Log.LogMessage(MessageImportance.Low, $"Loading extra assembly '{asm}' failed with {ex}. Skipping");
                        else
                        {
                            Log.LogError($"Failed to load assembly from ExtraAssemblies '{asm}': {ex}");
                            return false;
                        }
                    }
                }
            }
        }
        else
        {
            string? corelibPath = Assemblies!.FirstOrDefault(asm => asm.EndsWith("System.Private.CoreLib.dll"));
            if (corelibPath == null)
            {
                Log.LogError("Could not find 'System.Private.CoreLib.dll' within Assemblies.");
                return false;
            }
            _resolver = new Resolver(new string[] { corelibPath });
            var mlc = new MetadataLoadContext(_resolver, "System.Private.CoreLib");

            foreach (var asm in Assemblies!)
            {
                var assembly = mlc.LoadFromAssemblyPath(asm);
                Add(mlc, assembly);
            }
        }

        ReferencedAssemblies = _assemblies.Values.Select(asm => asm.Location).ToArray();

        return !Log.HasLoggedErrors;
    }

    private void Add(MetadataLoadContext mlc, Assembly assembly)
    {
        if (_assemblies!.ContainsKey(assembly.GetName().Name!))
            return;
        _assemblies![assembly.GetName().Name!] = assembly;
        foreach (var aname in assembly.GetReferencedAssemblies())
        {
            try
            {
                Assembly refAssembly = mlc.LoadFromAssemblyName(aname);
                Add(mlc, refAssembly);
            }
            catch (FileNotFoundException)
            {
            }
        }
    }
}

internal class Resolver : MetadataAssemblyResolver
{
    private readonly string[] _searchPaths;

    public Resolver(string[] searchPaths)
    {
        _searchPaths = searchPaths;
    }

    public override Assembly? Resolve(MetadataLoadContext context, AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        foreach (var dir in _searchPaths)
        {
            var path = Path.Combine(dir, name + ".dll");
            if (File.Exists(path))
            {
                return context.LoadFromAssemblyPath(path);
            }
        }
        return null;
    }
}
