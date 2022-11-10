using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using static System.Environment;

try
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
    IServiceCollection services = builder.Services;

    services.AddSingleton(
        new Database(
            GetEnvironmentVariable("REDIS_HOST")!,
            int.Parse(GetEnvironmentVariable("REDIS_PORT")!),
            GetEnvironmentVariable("REDIS_USERNAME"),
            GetEnvironmentVariable("REDIS_PASSWORD")
        )
    );

    builder.Services.AddMemoryCache();

    WebApplication app = builder.Build();

    app.UseWebSockets();

    app.MapGet(
        "/servers/{serverId}/image",
        async (HttpContext context, IMemoryCache cache, [FromRoute] ulong serverId, [FromServices] Database database) =>
        {
            IEnumerable<ulong> serverIds = await GetServerIds(context, cache);

            return !serverIds.Contains(serverId)
                ? Results.Unauthorized()
                : Results.File(await database.GetImage(serverId), "application/octet-stream");
        }
    );

    app.MapGet(
        "/servers/{serverId}/ws",
        async (HttpContext context, IMemoryCache cache, [FromRoute] ulong serverId, [FromServices] Database database) =>
        {
            IEnumerable<ulong> serverIds = await GetServerIds(context, cache);

            if (!serverIds.Contains(serverId))
                return Results.Unauthorized();

            if (context.WebSockets.IsWebSocketRequest)
            {
                CancellationTokenSource cts = new ();

                try
                {
                    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();

                    CancellationToken token = cts.Token;

                    ISubscriber subscriber = await database.GetPixelUpdates(
                        serverId,
                        async pixel =>
                        {
                            // Should not be a problem, we UnsubscribeAllAsync before we dispose the socket.
                            await socket.SendAsync(pixel.GetBytes(), WebSocketMessageType.Binary, true, token);
                        }
                    );

                    WebSocketReceiver receiver = new (socket);
                    while (!token.IsCancellationRequested)
                    {
                        Pixel pixel = await receiver.ReceivePixel(token);
                        await database.SetPixel(serverId, pixel);
                    }

                    await database.StopPixelUpdates(subscriber);
                }
                finally
                {
                    cts.Cancel();
                }
            }


            return Results.NoContent();
        }
    );

    app.Run();
}
catch (Exception e)
{
    Console.WriteLine(e);
    throw;
}

static async Task<IEnumerable<ulong>> GetServerIds(HttpContext context, IMemoryCache cache)
{
    // Make sure auth headers are provided in the http context.
    if (context.Request.Headers.Authorization.Count == 0) return Enumerable.Empty<ulong>();
    string[]? authValuesMaybe = context.Request.Headers.Authorization[0]?.Split(' ').ToArray();

    if (authValuesMaybe is not { } authValues || authValues.Length < 2 || authValues[0] is not "Bearer" ||
        authValues[1] is not { } token) return Enumerable.Empty<ulong>();

    // Check the cache for an existing list, to save on http requests.
    if (!cache.TryGetValue(token, out ulong[]? servers))
    {
        using HttpClient httpClient = new () { BaseAddress = new Uri("https://discord.com/") };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response = await httpClient.GetAsync("/api/users/@me/guilds");

        // Throw if discord does not like the request.
        response.EnsureSuccessStatusCode();

        // Parse and read the server ids from the response.
        string body = await response.Content.ReadAsStringAsync();
        servers = JsonSerializer.Deserialize<List<Server>>(body)!.Select(server => ulong.Parse(server.Id)).ToArray();

        // Add these ids to the cache for 5 minutes.
        cache.Set(token, servers, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(5)));
    }

    // Return the ids. Or none, indicating no permissions.
    return servers ?? Enumerable.Empty<ulong>();
}