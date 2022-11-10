using StackExchange.Redis;

namespace Api;

public class Database
{
    private readonly ConnectionMultiplexer _redis;

    public Database(string hostname, int port, string? username, string? password)
    {
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
        await database.StringSetAsync(imageKey, new RedisValue(DefaultImage));

        RedisValue newValue = await database.StringGetAsync(imageKey);

        return (byte[])newValue.Box()!;
    }

    public async Task SetPixel(ulong serverId, Pixel pixel)
    {
        IDatabase database = _redis.GetDatabase();

        string imageKey = GetImageKey(serverId);

        int offset = pixel.Y * Width + pixel.X;
        await database.ExecuteAsync("BITFIELD", (RedisKey)imageKey, "SET", "u8", $"#{offset}", pixel.Color.ToString());

        RedisChannel pubSubChannel = GetPubSubChannel(serverId);
        await database.PublishAsync(pubSubChannel, new RedisValue(pixel.GetString()));
    }

    public async Task<ISubscriber> GetPixelUpdates(ulong serverId, Func<Pixel, Task> callback)
    {
        ISubscriber subscriber = _redis.GetSubscriber();

        RedisChannel pubSubChannel = GetPubSubChannel(serverId);

        ChannelMessageQueue channel = await subscriber.SubscribeAsync(pubSubChannel);
        channel.OnMessage(async (channelMessage) => await callback(Pixel.FromString(channelMessage.Message!)));

        return subscriber;
    }

    public async Task StopPixelUpdates(ISubscriber subscriber)
    {
        await subscriber.UnsubscribeAllAsync();
    }

    private static string GetImageKey(ulong serverId) => $"server:{serverId}:image";
    private static RedisChannel GetPubSubChannel(ulong serverId) =>
        new ($"server:{serverId}:pubsub", RedisChannel.PatternMode.Literal);

    private const int Width = 1920;
    private const int Height = 1080;

    private static readonly string DefaultImage = System.Text.Encoding.ASCII.GetString(new byte[Width * Height]);
}