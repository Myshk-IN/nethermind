using System;
using System.Runtime.InteropServices;
using static Grandine.Native.NativeMethods;

namespace Grandine;

public sealed unsafe class Grandine
{
    public Grandine()
    {
    }

    public void Run(string[] args)
    {
        Console.WriteLine("Running Grandine with arguments: " + string.Join(" ", args)); // for testing
        byte** argv = ConvertToByteArray(args);
        
        try
        {
            ulong result = grandine_run((ulong)args.Length, argv);

            if (result != 0)
            {
                throw new Exception($"Grandine failed with error code {result}.");
            }
        }
        finally
        {
            FreeByteArray(args.Length, argv);
        }
    }

    private byte** ConvertToByteArray(string[] args)
    {
        byte** argv = (byte**)Marshal.AllocHGlobal(args.Length * sizeof(byte*));
        for (int i = 0; i < args.Length; i++)
        {
            argv[i] = (byte*)Marshal.StringToHGlobalAnsi(args[i]).ToPointer();
        }
        return argv;
    }

    private void FreeByteArray(int length, byte** argv)
    {
        for (int i = 0; i < length; i++)
        {
            Marshal.FreeHGlobal((IntPtr)argv[i]);
        }
        Marshal.FreeHGlobal((IntPtr)argv);
    }
}
