// Adaptive-trigger effect packing adapted from John "Nielk1" Klein's
// DualSense TriggerEffectGenerator revision 6, licensed under the MIT License.
// Source: https://gist.github.com/Nielk1/6d54cc2c00d2201ccb8c2720ad7538db

namespace ForzaHaptics.Tester;

internal static class DualSenseTriggerEffects
{
    private const byte OffMode = 0x05;
    private const byte FeedbackMode = 0x21;
    private const byte WeaponMode = 0x25;
    private const byte VibrationMode = 0x26;
    private const byte BowMode = 0x22;

    public static byte[] Off()
    {
        var effect = new byte[11];
        effect[0] = OffMode;
        return effect;
    }

    public static byte[] Feedback(byte position, byte strength)
    {
        position = Math.Min(position, (byte)9);
        strength = Math.Min(strength, (byte)8);
        if (strength == 0)
        {
            return Off();
        }

        uint forceZones = 0;
        ushort activeZones = 0;
        var forceValue = (byte)((strength - 1) & 0x07);
        for (var zone = position; zone < 10; zone++)
        {
            forceZones |= (uint)(forceValue << (3 * zone));
            activeZones |= (ushort)(1 << zone);
        }

        return PackZonedEffect(FeedbackMode, activeZones, forceZones, 0);
    }

    public static byte[] SlopeFeedback(
        byte startPosition,
        byte endPosition,
        byte startStrength,
        byte endStrength)
    {
        startPosition = Math.Min(startPosition, (byte)8);
        endPosition = Math.Clamp(endPosition, (byte)(startPosition + 1), (byte)9);
        startStrength = Math.Clamp(startStrength, (byte)1, (byte)8);
        endStrength = Math.Clamp(endStrength, (byte)1, (byte)8);

        var strengths = new byte[10];
        var slope = (endStrength - startStrength) /
                    (double)(endPosition - startPosition);
        for (var zone = startPosition; zone < 10; zone++)
        {
            strengths[zone] = zone <= endPosition
                ? (byte)Math.Clamp(
                    Math.Round(startStrength + slope * (zone - startPosition)),
                    1,
                    8)
                : endStrength;
        }

        uint forceZones = 0;
        ushort activeZones = 0;
        for (var zone = 0; zone < strengths.Length; zone++)
        {
            if (strengths[zone] == 0)
            {
                continue;
            }

            forceZones |= (uint)(((strengths[zone] - 1) & 0x07) << (3 * zone));
            activeZones |= (ushort)(1 << zone);
        }

        return PackZonedEffect(FeedbackMode, activeZones, forceZones, 0);
    }

    public static byte[] Weapon(byte startPosition, byte endPosition, byte strength)
    {
        startPosition = Math.Clamp(startPosition, (byte)2, (byte)7);
        endPosition = Math.Clamp(endPosition, (byte)(startPosition + 1), (byte)8);
        strength = Math.Min(strength, (byte)8);
        if (strength == 0)
        {
            return Off();
        }

        var zones = (ushort)((1 << startPosition) | (1 << endPosition));
        var effect = new byte[11];
        effect[0] = WeaponMode;
        effect[1] = (byte)zones;
        effect[2] = (byte)(zones >> 8);
        effect[3] = (byte)(strength - 1);
        return effect;
    }

    public static byte[] Vibration(byte position, byte amplitude, byte frequency)
    {
        position = Math.Min(position, (byte)9);
        amplitude = Math.Min(amplitude, (byte)8);
        if (amplitude == 0 || frequency == 0)
        {
            return Off();
        }

        uint amplitudeZones = 0;
        ushort activeZones = 0;
        var amplitudeValue = (byte)((amplitude - 1) & 0x07);
        for (var zone = position; zone < 10; zone++)
        {
            amplitudeZones |= (uint)(amplitudeValue << (3 * zone));
            activeZones |= (ushort)(1 << zone);
        }

        return PackZonedEffect(
            VibrationMode,
            activeZones,
            amplitudeZones,
            frequency);
    }

    public static byte[] Bow(
        byte startPosition,
        byte endPosition,
        byte strength,
        byte snapStrength)
    {
        startPosition = Math.Min(startPosition, (byte)7);
        endPosition = Math.Clamp(endPosition, (byte)(startPosition + 1), (byte)8);
        strength = Math.Min(strength, (byte)8);
        snapStrength = Math.Min(snapStrength, (byte)8);
        if (strength == 0 || snapStrength == 0)
        {
            return Off();
        }

        var zones = (ushort)((1 << startPosition) | (1 << endPosition));
        var forcePair = (uint)(((strength - 1) & 0x07) |
                               (((snapStrength - 1) & 0x07) << 3));
        var effect = new byte[11];
        effect[0] = BowMode;
        effect[1] = (byte)zones;
        effect[2] = (byte)(zones >> 8);
        effect[3] = (byte)forcePair;
        effect[4] = (byte)(forcePair >> 8);
        return effect;
    }

    private static byte[] PackZonedEffect(
        byte mode,
        ushort activeZones,
        uint strengths,
        byte frequency)
    {
        var effect = new byte[11];
        effect[0] = mode;
        effect[1] = (byte)activeZones;
        effect[2] = (byte)(activeZones >> 8);
        effect[3] = (byte)strengths;
        effect[4] = (byte)(strengths >> 8);
        effect[5] = (byte)(strengths >> 16);
        effect[6] = (byte)(strengths >> 24);
        effect[9] = frequency;
        return effect;
    }
}
