using NusaTrainer.Core.Models;

namespace NusaTrainer.Core.Trainer;

public static class ValueCodec
{
    public static int SizeOf(DataType type) => type switch
    {
        DataType.Byte => 1,
        DataType.Int16 => 2,
        DataType.Int32 => 4,
        DataType.Int64 => 8,
        DataType.Float => 4,
        DataType.Double => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static byte[] Encode(DataType type, double value) => type switch
    {
        DataType.Byte => [(byte)Math.Clamp(Math.Round(value), byte.MinValue, byte.MaxValue)],
        DataType.Int16 => BitConverter.GetBytes((short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue)),
        DataType.Int32 => BitConverter.GetBytes((int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue)),
        DataType.Int64 => BitConverter.GetBytes((long)Math.Clamp(Math.Round(value), long.MinValue, long.MaxValue)),
        DataType.Float => BitConverter.GetBytes((float)value),
        DataType.Double => BitConverter.GetBytes(value),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static double Decode(DataType type, ReadOnlySpan<byte> data) => type switch
    {
        DataType.Byte => data[0],
        DataType.Int16 => BitConverter.ToInt16(data),
        DataType.Int32 => BitConverter.ToInt32(data),
        DataType.Int64 => BitConverter.ToInt64(data),
        DataType.Float => BitConverter.ToSingle(data),
        DataType.Double => BitConverter.ToDouble(data),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
