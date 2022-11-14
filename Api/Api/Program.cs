using System.Net.WebSockets;
using Api;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;
using static System.Environment;

const string corsPolicy = "cors";

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

    services.AddSingleton<DiscordClient>();

    services.AddCors(
        options =>
        {
            options.AddPolicy(
                corsPolicy,
                configure => { configure.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod(); }
            );
        }
    );

    WebApplication app = builder.Build();

    app.UseCors(corsPolicy);

    app.UseWebSockets();

    app.MapGet(
        "/servers/{serverId}/image",
        async (
            HttpContext context,
            [FromServices] DiscordClient discordClient,
            [FromServices] Database database,
            [FromRoute] ulong serverId
        ) =>
        {
            try
            {
                string? userToken = GetUserToken(context);
                if (userToken == null) return Results.Unauthorized();
                IEnumerable<ulong> serverIds = await discordClient.GetServerIds(userToken);

                return !serverIds.Contains(serverId)
                    ? Results.Unauthorized()
                    : Results.File(await database.GetImage(serverId), "application/octet-stream");
            }
            catch (UnauthorizedException)
            {
                return Results.Unauthorized();
            }
            catch (RateLimitException)
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }
        }
    );

    app.MapGet(
        "/servers/{serverId}/user/{x}/{y}",
        async (
            HttpContext context,
            [FromServices] DiscordClient discordClient,
            [FromServices] Database database,
            [FromRoute] ulong serverId,
            [FromRoute] ushort x,
            [FromRoute] ushort y
        ) =>
        {
            try
            {
                string? userToken = GetUserToken(context);
                if (userToken == null) return Results.Unauthorized();
                IEnumerable<ulong> serverIds = await discordClient.GetServerIds(userToken);

                return !serverIds.Contains(serverId)
                    ? Results.Unauthorized()
                    : Results.Text((await database.GetPixelUser(serverId, x, y)).ToString());
            }
            catch (UnauthorizedException)
            {
                return Results.Unauthorized();
            }
            catch (RateLimitException)
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }
        }
    );

    app.MapGet(
        "/servers/{serverId}/ws",
        async (
            HttpContext context,
            [FromServices] DiscordClient discordClient,
            [FromServices] Database database,
            [FromRoute] ulong serverId
        ) =>
        {
            try
            {
                string? userToken = GetUserToken(context);
                if (userToken != null)
                {
                    IEnumerable<ulong> serverIds = await discordClient.GetServerIds(userToken);
                    if (!serverIds.Contains(serverId))
                        return Results.Unauthorized();
                }

                if (!context.WebSockets.IsWebSocketRequest) return Results.BadRequest("Use websocket");

                CancellationTokenSource cts = new ();

                try
                {
                    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
                    WebSocketReceiver receiver = new (socket);
                    CancellationToken token = cts.Token;

                    // If not authorized using headers, receive a token from the websocket.
                    if (userToken == null)
                    {
                        userToken = await receiver.ReceiveToken(token);
                        IEnumerable<ulong> serverIds = await discordClient.GetServerIds(userToken);
                        if (!serverIds.Contains(serverId))
                            return Results.Unauthorized();
                    }

                    ulong userId = await discordClient.GetUserId(userToken);

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
                            await database.SetPixel(serverId, userId, pixel);
                    }

                    await database.StopPixelUpdates(subscriber);
                }
                finally
                {
                    cts.Cancel();
                }

                return Results.NoContent();
            }
            catch (UnauthorizedException)
            {
                return Results.Unauthorized();
            }
            catch (RateLimitException)
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }
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