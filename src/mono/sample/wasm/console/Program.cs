// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

public class Test
{
    public static async Task<int> Main(string[] args)
    {
        await Task.Delay(1);
        Console.WriteLine("Hello World - jsut !");
        Console.WriteLine ($"from pinvoke: {print_line("Foo Bar")}");
        return args.Length;
    }

    [DllImport("NativeLib")]
    private static extern int print_line(string str);
}
