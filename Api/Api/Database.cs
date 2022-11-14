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

        if (oldValue.HasValue)
        {
            byte[] val = (byte[])oldValue.Box()!;
            uint[] palette = new uint[val.Length / 3];
            
            for (int i = 0; i < palette.Length; i++)
            {
                palette[i] = (uint)(val[i * 3] << 16 | val[i * 3 + 1] << 8 | val[i * 3 + 2]);
            }
            
            return palette;
        }

        ITransaction transaction = database.CreateTransaction();
        var tasks = DefaultPalette.Select((v, i) =>
            transaction.ExecuteAsync("BITFIELD", paletteKey, "SET", "u24", i * 24, v & 0x00FFFFFF));
        await transaction.ExecuteAsync();

        await Task.WhenAll(tasks);

        RedisValue newValue = await database.StringGetAsync(paletteKey);

        return (uint[])newValue.Box()!;
    }

    public async Task SetPixel(ulong serverId, ulong userId, Pixel pixel)
    {
        IDatabase database = _redis.GetDatabase();

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
        Task<RedisResult> logTask1 = transaction.ExecuteAsync(
            "BITFIELD",
            (RedisKey)GetLogKey(serverId),
            "SET",
            "u32",
            $"#{offset}",
            (uint)(userId >> 32)
        );
        Task<RedisResult> logTask2 = transaction.ExecuteAsync(
            "BITFIELD",
            (RedisKey)GetLogKey(serverId),
            "SET",
            "u32",
            $"#{offset + 1}",
            (uint)(userId & 0xFFFFFFFF)
        );

        await transaction.ExecuteAsync();

        await Task.WhenAll(colorTask, logTask1, logTask2);

        RedisChannel pubSubChannel = GetPubSubChannel(serverId);
        await database.PublishAsync(pubSubChannel, pixel.GetBytes());
    }

    public async Task<ulong?> GetPixelUser(ulong serverId, ushort x, ushort y)
    {
        IDatabase database = _redis.GetDatabase();
        int offset = y * Width + x;

        ITransaction transaction = database.CreateTransaction();
        Task<RedisResult> logTask1 = transaction.ExecuteAsync(
            "BITFIELD",
            (RedisKey)GetLogKey(serverId),
            "GET",
            "u32",
            $"#{offset}"
        );
        Task<RedisResult> logTask2 = transaction.ExecuteAsync(
            "BITFIELD",
            (RedisKey)GetLogKey(serverId),
            "GET",
            "u32",
            $"#{offset + 1}"
        );
        await transaction.ExecuteAsync();

        RedisResult[] logTasks = await Task.WhenAll(logTask1, logTask2);

        ulong log1 = (ulong)logTasks[0];
        ulong log2 = (ulong)logTasks[1];
        ulong userId = (log1 << 32) + log2;

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

    private static RedisChannel GetPubSubChannel(ulong serverId) => new($"server:{serverId}:pubsub", RedisChannel.PatternMode.Literal);

    // TODO make variable.
    private const ushort Width = 1920;
    private const ushort Height = 1080;
    private const int DimensionsHeaderSize = 4;

    private static readonly byte[] DefaultImage = GetDefaultImage(Width, Height);

    private static readonly uint[] DefaultPalette =
        { 0xFFFFFF, 0, 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFF00, 0xFF00FF, 0x00FFFF };

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