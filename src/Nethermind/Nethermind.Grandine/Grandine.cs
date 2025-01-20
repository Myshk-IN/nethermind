using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static Grandine.Native.NativeMethods;

namespace Grandine;

public sealed unsafe class Grandine
{
    public Grandine()
    {
    }

    public unsafe static async Task Run(Dictionary<string, List<string>> grandineConfig, CancellationToken cancellationToken)
    {
        Console.WriteLine("Running Grandine with arguments:"); // for testing
        foreach (var entry in grandineConfig)
        {
            Console.WriteLine($"{entry.Key}: {string.Join(", ", entry.Value)}");
        }

        await Task.Run(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            byte** argv = null;
            try
            {
                string[] configs = ConvertDictionaryToArray(grandineConfig);
                argv = ConvertToByteArray(configs);
                ulong result = grandine_run((ulong)configs.Length, argv);

                if (result != 0)
                {
                    throw new Exception($"Grandine failed with error code {result}.");
                }
            }
            finally
            {
                FreeByteArray(grandineConfig.Count, argv);
            }
        }, cancellationToken);
    }

    private static string[] ConvertDictionaryToArray(Dictionary<string, List<string>> grandineConfig)
    {
        var configs = new List<string>();
        foreach (var entry in grandineConfig)
        {
            foreach (var value in entry.Value)
            {
                configs.Add($"{entry.Key}={value}");
            }
        }
        return configs.ToArray();
    }

    private static byte** ConvertToByteArray(string[] configs)
    {
        byte** argv = (byte**)Marshal.AllocHGlobal(configs.Length * sizeof(byte*));
        for (int i = 0; i < configs.Length; i++)
        {
            argv[i] = (byte*)Marshal.StringToHGlobalAnsi(configs[i]).ToPointer();
        }
        return argv;
    }

    private static void FreeByteArray(int length, byte** argv)
    {
        if (argv == null) return;

        for (int i = 0; i < length; i++)
        {
            Marshal.FreeHGlobal((IntPtr)argv[i]);
        }
        Marshal.FreeHGlobal((IntPtr)argv);
    }
}
