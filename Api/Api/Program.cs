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
            GetEnvironmentVariable("REDIS_PASSWORD"),
            int.Parse(GetEnvironmentVariable("RATE_LIMIT_SECONDS") ?? "300")
        )
    );

    services.AddMemoryCache();

    WebApplication app = builder.Build();

    app.UseWebSockets();

    app.MapGet(
        "/servers/{serverId}/image",
        async (
            HttpContext context,
            [FromServices] IMemoryCache cache,
            [FromServices] Database database,
            [FromRoute] ulong serverId
        ) =>
        {
            string? userToken = GetUserToken(context);
            if (userToken == null) return Results.Unauthorized();
            IEnumerable<ulong> serverIds = await GetServerIds(userToken, cache);

            return !serverIds.Contains(serverId)
                ? Results.Unauthorized()
                : Results.File(await database.GetImage(serverId), "application/octet-stream");
        }
    );

    app.MapGet(
        "/servers/{serverId}/ws",
        async (
            HttpContext context,
            [FromServices] IMemoryCache cache,
            [FromServices] Database database,
            [FromRoute] ulong serverId
        ) =>
        {
            string? userToken = GetUserToken(context);
            if (userToken != null)
            {
                IEnumerable<ulong> serverIds = await GetServerIds(userToken, cache);
                if (!serverIds.Contains(serverId))
                    return Results.Unauthorized();
            }

            if (context.WebSockets.IsWebSocketRequest)
            {
                CancellationTokenSource cts = new ();

                try
                {
                    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
                    WebSocketReceiver receiver = new(socket);
                    CancellationToken token = cts.Token;

                    // If not authorized using headers, receive a token from the websocket.
                    if (userToken == null)
                    {
                        userToken = await receiver.ReceiveToken(token);
                        IEnumerable<ulong> serverIds = await GetServerIds(userToken, cache);
                        if (!serverIds.Contains(serverId))
                            return Results.Unauthorized();
                    }


                    ISubscriber subscriber = await database.GetPixelUpdates(
                        serverId,
                        async pixel =>
                        {
                            // Should not be a problem, we UnsubscribeAllAsync before we dispose the socket.
                            await socket.SendAsync(pixel.GetBytes(), WebSocketMessageType.Binary, true, token);
                        }
                    );

                    while (!token.IsCancellationRequested)
                    {
                        Pixel pixel = await receiver.ReceivePixel(token);
                        if (!await database.GetOnCooldown(serverId, userToken))
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

static string? GetUserToken(HttpContext context)
{
    // Make sure auth headers are provided in the http context.
    if (context.Request.Headers.Authorization.Count == 0) return null;
    string[]? authValuesMaybe = context.Request.Headers.Authorization[0]?.Split(' ').ToArray();

    if (authValuesMaybe is not { } authValues || authValues.Length < 2 || authValues[0] is not "Bearer" ||
        authValues[1] is not { } token) return null;

    return token;
}

static async Task<IEnumerable<ulong>> GetServerIds(string token, IMemoryCache cache)
{
    // Check the cache for an existing list, to save on http requests.
    if (!cache.TryGetValue(token, out ulong[]? servers))
    {
        using HttpClient httpClient = new () { BaseAddress = new Uri("https://discord.com/") };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response = await httpClient.GetAsync("/api/users/@me/guilds");

        // Throw if discord does not like the request.
        if (response.IsSuccessStatusCode)
        {
            // Parse and read the server ids from the response.
            string body = await response.Content.ReadAsStringAsync();
            servers = JsonSerializer.Deserialize<List<Server>>(body)!.Select(server => ulong.Parse(server.Id)).ToArray();

            // Add these ids to the cache for 5 minutes.
            cache.Set(token, servers, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(5)));
        }
        else
        {
            // If the request failed, store null (as this token does not have permission to anything).
            cache.Set(token, (ulong[]?)null, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromHours(1)));
        }
    }

    // Return the ids. Or none, indicating no permissions.
    return servers ?? Enumerable.Empty<ulong>();
}