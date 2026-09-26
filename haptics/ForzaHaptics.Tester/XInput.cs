using System.Runtime.InteropServices;

namespace ForzaHaptics.Tester;

internal static partial class XInput
{
    internal const uint Success = 0;

    [LibraryImport("xinput1_4.dll")]
    internal static partial uint XInputGetState(uint userIndex, out State state);

    [LibraryImport("xinput1_4.dll")]
    internal static partial uint XInputSetState(uint userIndex, ref Vibration vibration);

    [StructLayout(LayoutKind.Sequential)]
    internal struct State
    {
        public uint PacketNumber;
        public Gamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Vibration
    {
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }
}
