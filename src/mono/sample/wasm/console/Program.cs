// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Text;

public class Test
{
    public static int Main(string[] args)
    {
        var sb = new StringBuilder();
        sb.Append("123");
        //await Task.Delay(1);
        Console.WriteLine($"Hello World: {sb}!");
        //for (int i = 0; i < args.Length; i++) {
            //Console.WriteLine($"args[{i}] = {args[i]}");
        //}
        return 42 + sb.Length;
    }
}
