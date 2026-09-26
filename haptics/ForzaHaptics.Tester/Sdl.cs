using System.Runtime.InteropServices;

namespace ForzaHaptics.Tester;

internal static partial class Sdl
{
    internal const uint InitGamepad = 0x00002000;
    private const string LibraryName = "SDL3";
    private static Task<bool>? _gamepadInitialization;

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SDL_SetHint(string name, string value);

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
    internal static partial IntPtr SDL_GetGamepadNameForID(uint instanceId);

    [LibraryImport(LibraryName)]
    internal static partial ushort SDL_GetGamepadVendorForID(uint instanceId);

    [LibraryImport(LibraryName)]
    internal static partial ushort SDL_GetGamepadProductForID(uint instanceId);

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
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SDL_RumbleGamepadTriggers(
        IntPtr gamepad,
        ushort leftRumble,
        ushort rightRumble,
        uint durationMs);

    [LibraryImport(LibraryName)]
    internal static partial void SDL_UpdateGamepads();

    [LibraryImport(LibraryName)]
    internal static partial IntPtr SDL_GetError();

    [LibraryImport(LibraryName)]
    internal static partial void SDL_free(IntPtr memory);

    [LibraryImport(LibraryName)]
    internal static partial IntPtr SDL_hid_enumerate(ushort vendorId, ushort productId);

    [LibraryImport(LibraryName)]
    internal static partial int SDL_hid_init();

    [LibraryImport(LibraryName)]
    internal static partial int SDL_hid_exit();

    [LibraryImport(LibraryName)]
    internal static partial void SDL_hid_free_enumeration(IntPtr devices);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr SDL_hid_open_path(string path);

    [LibraryImport(LibraryName)]
    internal static partial int SDL_hid_write(IntPtr device, byte[] data, nuint length);

    [LibraryImport(LibraryName)]
    internal static partial int SDL_hid_close(IntPtr device);

    internal static bool IsGamepadInitialized =>
        _gamepadInitialization is { IsCompletedSuccessfully: true } &&
        _gamepadInitialization.Result;

    internal static void StartGamepadInitialization()
    {
        if (_gamepadInitialization is not null)
        {
            return;
        }

        SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
        SDL_SetHint("SDL_JOYSTICK_HIDAPI_STEAM", "0");
        _gamepadInitialization = Task.Run(() => SDL_Init(InitGamepad));
    }

    internal static async Task<bool> WaitForGamepadInitializationAsync(
        TimeSpan timeout)
    {
        StartGamepadInitialization();
        var initialization = _gamepadInitialization!;
        var completed = await Task.WhenAny(initialization, Task.Delay(timeout));
        return completed == initialization &&
               initialization.IsCompletedSuccessfully &&
               initialization.Result;
    }

    internal static void QuitGamepads()
    {
        if (IsGamepadInitialized)
        {
            SDL_Quit();
        }
    }

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
