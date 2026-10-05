using System.Globalization;
using System.Runtime.InteropServices;

const uint SdlInitGamepad = 0x00002000;

if (!Sdl.SDL_Init(SdlInitGamepad))
{
    Console.Error.WriteLine($"SDL initialization failed: {Sdl.GetError()}");
    return 1;
}

try
{
    var gamepadIds = Sdl.GetGamepads();
    if (gamepadIds.Length == 0)
    {
        Console.Error.WriteLine("SDL did not find any gamepads.");
        return 2;
    }

    Console.WriteLine($"SDL found {gamepadIds.Length} gamepad(s):");

    for (var index = 0; index < gamepadIds.Length; index++)
    {
        var gamepad = Sdl.SDL_OpenGamepad(gamepadIds[index]);
        if (gamepad == IntPtr.Zero)
        {
            Console.WriteLine($"  [{index}] Could not open SDL gamepad id {gamepadIds[index]}: {Sdl.GetError()}");
            continue;
        }

        try
        {
            Console.WriteLine(
                $"  [{index}] {Sdl.PtrToString(Sdl.SDL_GetGamepadName(gamepad))} " +
                $"(VID {Sdl.SDL_GetGamepadVendor(gamepad):X4}, PID {Sdl.SDL_GetGamepadProduct(gamepad):X4})");
        }
        finally
        {
            Sdl.SDL_CloseGamepad(gamepad);
        }
    }

    if (args.Length == 0 || args[0] is "--list" or "-l")
    {
        Console.WriteLine();
        Console.WriteLine("Opt-in test: --rumble [gamepad-index] [low-0..1] [high-0..1] [milliseconds]");
        Console.WriteLine("Example:     --rumble 0 0.20 0.20 300");
        return 0;
    }

    if (!string.Equals(args[0], "--rumble", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Unknown command. Use --list or --rumble.");
        return 3;
    }

    var selectedIndex = ParseInt(args, 1, 0, 0, gamepadIds.Length - 1);
    var low = ParseDouble(args, 2, 0.20, 0, 1);
    var high = ParseDouble(args, 3, low, 0, 1);
    var milliseconds = ParseInt(args, 4, 300, 1, 5_000);

    var selected = Sdl.SDL_OpenGamepad(gamepadIds[selectedIndex]);
    if (selected == IntPtr.Zero)
    {
        Console.Error.WriteLine($"Could not open gamepad {selectedIndex}: {Sdl.GetError()}");
        return 4;
    }

    try
    {
        var lowValue = (ushort)Math.Round(low * ushort.MaxValue);
        var highValue = (ushort)Math.Round(high * ushort.MaxValue);
        Console.WriteLine(
            $"Rumbling {Sdl.PtrToString(Sdl.SDL_GetGamepadName(selected))}: " +
            $"low={low:P0}, high={high:P0}, duration={milliseconds} ms");

        if (!Sdl.SDL_RumbleGamepad(selected, lowValue, highValue, (uint)milliseconds))
        {
            Console.Error.WriteLine($"SDL rumble failed: {Sdl.GetError()}");
            return 5;
        }

        var deadline = Environment.TickCount64 + milliseconds + 100;
        while (Environment.TickCount64 < deadline)
        {
            Sdl.SDL_UpdateGamepads();
            Thread.Sleep(10);
        }

        Sdl.SDL_RumbleGamepad(selected, 0, 0, 0);
        Sdl.SDL_UpdateGamepads();
        Console.WriteLine("Rumble command completed.");
        return 0;
    }
    finally
    {
        Sdl.SDL_CloseGamepad(selected);
    }
}
finally
{
    Sdl.SDL_Quit();
}

static int ParseInt(string[] values, int index, int fallback, int minimum, int maximum)
{
    if (index >= values.Length)
    {
        return fallback;
    }

    if (!int.TryParse(values[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
        parsed < minimum ||
        parsed > maximum)
    {
        throw new ArgumentOutOfRangeException(
            values[index],
            $"Expected an integer between {minimum} and {maximum}.");
    }

    return parsed;
}

static double ParseDouble(string[] values, int index, double fallback, double minimum, double maximum)
{
    if (index >= values.Length)
    {
        return fallback;
    }

    if (!double.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
        parsed < minimum ||
        parsed > maximum)
    {
        throw new ArgumentOutOfRangeException(
            values[index],
            $"Expected a number between {minimum} and {maximum}.");
    }

    return parsed;
}

internal static partial class Sdl
{
    private const string LibraryName = "SDL3";

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SDL_Init(uint flags);

    [LibraryImport(LibraryName)]
    internal static partial void SDL_Quit();

    [LibraryImport(LibraryName)]
    internal static partial IntPtr SDL_GetGamepads(out int count);

    [LibraryImport(LibraryName)]
    internal static partial IntPtr SDL_OpenGamepad(uint instanceId);

    [LibraryImport(LibraryName)]
    internal static partial void SDL_CloseGamepad(IntPtr gamepad);

    [LibraryImport(LibraryName)]
    internal static partial IntPtr SDL_GetGamepadName(IntPtr gamepad);

    [LibraryImport(LibraryName)]
    internal static partial ushort SDL_GetGamepadVendor(IntPtr gamepad);

    [LibraryImport(LibraryName)]
    internal static partial ushort SDL_GetGamepadProduct(IntPtr gamepad);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SDL_RumbleGamepad(
        IntPtr gamepad,
        ushort lowFrequencyRumble,
        ushort highFrequencyRumble,
        uint durationMs);

    [LibraryImport(LibraryName)]
    internal static partial void SDL_UpdateGamepads();

    [LibraryImport(LibraryName)]
    internal static partial IntPtr SDL_GetError();

    [LibraryImport(LibraryName)]
    internal static partial void SDL_free(IntPtr memory);

    internal static uint[] GetGamepads()
    {
        var pointer = SDL_GetGamepads(out var count);
        if (pointer == IntPtr.Zero || count <= 0)
        {
            return [];
        }

        try
        {
            var result = new uint[count];
            for (var index = 0; index < count; index++)
            {
                result[index] = unchecked((uint)Marshal.ReadInt32(pointer, index * sizeof(uint)));
            }

            return result;
        }
        finally
        {
            SDL_free(pointer);
        }
    }

    internal static string PtrToString(IntPtr pointer) =>
        Marshal.PtrToStringUTF8(pointer) ?? "(unnamed)";

    internal static string GetError() => PtrToString(SDL_GetError());
}
