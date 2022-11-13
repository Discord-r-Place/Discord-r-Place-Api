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
        await database.StringSetAsync(imageKey, RedisValue.Unbox(DefaultImage));

        RedisValue newValue = await database.StringGetAsync(imageKey);

        return (byte[])newValue.Box()!;
    }

    public async Task SetPixel(ulong serverId, Pixel pixel)
    {
        IDatabase database = _redis.GetDatabase();

        string imageKey = GetImageKey(serverId);

        int offset = DimensionsHeaderSize + pixel.Y * Width + pixel.X;
        await database.ExecuteAsync("BITFIELD", (RedisKey)imageKey, "SET", "u8", $"#{offset}", pixel.Color.ToString());

        RedisChannel pubSubChannel = GetPubSubChannel(serverId);
        await database.PublishAsync(pubSubChannel, RedisValue.Unbox(pixel.GetBytes()));
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
        IDatabase database = _redis.GetDatabase();

        string rateLimitKey = GetRateLimitKey(serverId, token);

        RedisValue result = await database.StringGetAsync(rateLimitKey);
        if (result.HasValue) return true;

        await database.StringSetAsync(rateLimitKey, new RedisValue("Limit"), TimeSpan.FromSeconds(_rateLimitSeconds));
        return false;
    }

    private static string GetImageKey(ulong serverId) => $"server:{serverId}:image";
    private static string GetRateLimitKey(ulong serverId, string token) => $"server:{serverId}:user:{token}";

    private static RedisChannel GetPubSubChannel(ulong serverId) =>
        new ($"server:{serverId}:pubsub", RedisChannel.PatternMode.Literal);

    // TODO make variable.
    private const short Width = 1920;
    private const short Height = 1080;
    private const int DimensionsHeaderSize = 4;

    private static readonly byte[] DefaultImage = GetDefaultImage(Width, Height);

    private static byte[] GetDefaultImage(short width, short height)
    {
        byte[] buffer = new byte[DimensionsHeaderSize + width * height];
        buffer[0] = (byte)(width >> 8);
        buffer[1] = (byte)(width & 0xff);
        buffer[2] = (byte)(height >> 8);
        buffer[3] = (byte)(height & 0xff);
        return buffer;
    }
}