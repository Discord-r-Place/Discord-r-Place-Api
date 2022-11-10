namespace Api;

public struct Pixel
{
    public ushort X { get; set; }
    public ushort Y { get; set; }
    public byte Color { get; set; }

    public byte[] GetBytes() => new[] { (byte)(X >> 8), (byte)(X & 0xff), (byte)(Y >> 8), (byte)(Y & 0xff), Color };
    public string GetString() => Convert.ToBase64String(GetBytes());

    public static Pixel FromBytes(byte[] bytes) =>
        new () { X = (ushort)((bytes[0] << 8) + bytes[1]), Y = (ushort)((bytes[2] << 8) + bytes[3]), Color = bytes[4] };

    public static Pixel FromString(string s) => FromBytes(Convert.FromBase64String(s));
     
}