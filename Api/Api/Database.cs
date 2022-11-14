using System.Diagnostics;
using StackExchange.Redis;

namespace Api;

public class Database
{
    private readonly int _rateLimitSeconds;
    private readonly ConnectionMultiplexer _redis;

    public Database(string hostname, int port, string? username, string? password, int rateLimitSeconds)
    {
        _rateLimitSeconds = rateLimitSeconds;
        _redis = username is { } || password is { }
            ? ConnectionMultiplexer.Connect($"{username}:{password}@{hostname}:{port}")
            : ConnectionMultiplexer.Connect($"{hostname}:{port}");
    }

    uint[] BytesToUInts(byte[] bytes)
    {
        Debug.Assert(bytes.Length % 4 == 0);
        uint[] result = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }

    public async Task<byte[]> GetImage(ulong serverId)
    {
        IDatabase database = _redis.GetDatabase();

        string imageKey = GetImageKey(serverId);
        RedisValue oldValue = await database.StringGetAsync(imageKey);

        if (oldValue.HasValue) return (byte[])oldValue.Box()!;
        await database.StringSetAsync(imageKey, DefaultImage);

        RedisValue newValue = await database.StringGetAsync(imageKey);

        return (byte[])newValue.Box()!;
    }

    public async Task<uint[]> GetPalette(ulong serverId)
    {
        IDatabase database = _redis.GetDatabase();

        string paletteKey = GetPaletteKey(serverId);
        RedisValue oldValue = await database.StringGetAsync(paletteKey);

        if (oldValue.HasValue) return BytesToUInts((byte[])oldValue.Box()!);

        await database.StringSetAsync(paletteKey, DefaultPalette);

        RedisValue newValue = await database.StringGetAsync(paletteKey);

        return BytesToUInts((byte[])newValue.Box()!);
    }

    public async Task SetPixel(ulong serverId, ulong userId, Pixel pixel)
    {
        IDatabase database = _redis.GetDatabase();

        long length = await database.StringLengthAsync(GetPaletteKey(serverId));

        // Prevent invalid palette indexes
        if (pixel.Color >= length / 4) return;

        int offset = pixel.Y * Width + pixel.X;

        ITransaction transaction = database.CreateTransaction();
        Task<RedisResult> colorTask = transaction.ExecuteAsync(
            "BITFIELD",
            (RedisKey)GetImageKey(serverId),
            "SET",
            "u8",
            $"#{DimensionsHeaderSize + offset}",
            pixel.Color.ToString()
        );
        // ulong is currently not supported by redis.
        Task<RedisResult> logTask = transaction.ExecuteAsync(
            "BITFIELD",
            (RedisKey)GetLogKey(serverId),
            "SET",
            "u32",
            $"#{2 * offset}",
            (uint)(userId >> 32),
            "SET",
            "u32",
            $"#{2 * offset + 1}",
            (uint)(userId & 0xFFFFFFFF)
        );

        await transaction.ExecuteAsync();

        await Task.WhenAll(colorTask, logTask);

        RedisChannel pubSubChannel = GetPubSubChannel(serverId);
        await database.PublishAsync(pubSubChannel, pixel.GetBytes());
    }

    public async Task<ulong?> GetPixelUser(ulong serverId, ushort x, ushort y)
    {
        IDatabase database = _redis.GetDatabase();
        int offset = y * Width + x;

        ulong[] result = (ulong[])(await database.ExecuteAsync(
            "BITFIELD",
            (RedisKey)GetLogKey(serverId),
            "GET",
            "u32",
            $"#{2 * offset}",
            "GET",
            "u32",
            $"#{2 * offset + 1}"
        ))!;

        ulong userId = (result[0] << 32) + result[1];

        if (userId == 0) return null;

        return userId;
    }

    public async Task<ISubscriber> GetPixelUpdates(ulong serverId, Func<Pixel, Task> callback)
    {
        ISubscriber subscriber = _redis.GetSubscriber();

        RedisChannel pubSubChannel = GetPubSubChannel(serverId);

        ChannelMessageQueue channel = await subscriber.SubscribeAsync(pubSubChannel);
        channel.OnMessage(
            async channelMessage => await callback(Pixel.FromBytes((byte[])channelMessage.Message.Box()!))
        );

        return subscriber;
    }

    public async Task StopPixelUpdates(ISubscriber subscriber)
    {
        await subscriber.UnsubscribeAllAsync();
    }

    // Potentially should not use the token, but they seem to be consistent across requests.
    public async Task<bool> GetOnCooldown(ulong serverId, string token)
    {
        if (_rateLimitSeconds == 0) return false;

        IDatabase database = _redis.GetDatabase();

        string rateLimitKey = GetRateLimitKey(serverId, token);

        RedisValue result = await database.StringGetAsync(rateLimitKey);
        if (result.HasValue) return true;

        await database.StringSetAsync(rateLimitKey, "Limit", TimeSpan.FromSeconds(_rateLimitSeconds));
        return false;
    }

    private static string GetImageKey(ulong serverId) => $"server:{serverId}:image";
    private static string GetPaletteKey(ulong serverId) => $"server:{serverId}:palette";
    private static string GetLogKey(ulong serverId) => $"server:{serverId}:log";
    private static string GetRateLimitKey(ulong serverId, string token) => $"server:{serverId}:user:{token}";

    private static RedisChannel GetPubSubChannel(ulong serverId) =>
        new($"server:{serverId}:pubsub", RedisChannel.PatternMode.Literal);

    // TODO make variable.
    private const ushort Width = 1920;
    private const ushort Height = 1080;
    private const int DimensionsHeaderSize = 4;

    private static readonly byte[] DefaultImage = GetDefaultImage(Width, Height);

    private static readonly byte[] DefaultPalette =
        new uint[]
            {
                0xE4E4E4, 0xA0A7A7, 0x414141, 0x181414, 0x9E2B27, 0xEA7E35, 0xC2B51C, 0x39BA2E,
                0x364B18, 0x6387D2, 0x267191, 0x253193, 0x7E34BF, 0xBE49C9, 0xD98199, 0x56331C
            }
            .SelectMany(BitConverter.GetBytes).ToArray();

    private static byte[] GetDefaultImage(ushort width, ushort height)
    {
        byte[] buffer = new byte[DimensionsHeaderSize + width * height];
        buffer[0] = (byte)(width >> 8);
        buffer[1] = (byte)(width & 0xff);
        buffer[2] = (byte)(height >> 8);
        buffer[3] = (byte)(height & 0xff);
        return buffer;
    }
}