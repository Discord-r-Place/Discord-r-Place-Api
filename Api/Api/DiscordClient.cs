using System.Net;
using Api.Types;
using Microsoft.Extensions.Caching.Memory;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Api;

public class DiscordClient
{
    private readonly IMemoryCache _serverCache;
    private readonly IMemoryCache _userCache;
    private readonly HttpClient _httpClient;

    public DiscordClient()
    {
        MemoryCacheOptions cacheOptions = new ();
        _serverCache = new MemoryCache(cacheOptions);
        _userCache = new MemoryCache(cacheOptions);
        _httpClient = new HttpClient { BaseAddress = new Uri("https://discord.com/") };
    }

    public async Task<IEnumerable<ulong>> GetServerIds(string token)
    {
        // Check the cache for an existing list, to save on http requests.
        if (_serverCache.TryGetValue(token, out ulong[]? serversMaybe))
            return serversMaybe ?? throw new UnauthorizedException();

        HttpRequestMessage request = new (HttpMethod.Get, "/api/users/@me/guilds");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response = await _httpClient.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitException();

        // Throw save null if discord does not like the request.
        if (!response.IsSuccessStatusCode)
        {
            _serverCache.Set(
                token,
                (ulong[]?)null,
                new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromHours(1))
            );

            throw new UnauthorizedException();
        }

        // Parse and read the server ids from the response.
        string body = await response.Content.ReadAsStringAsync();
        ulong[] servers = JsonSerializer.Deserialize<List<Server>>(body)!.Select(server => ulong.Parse(server.Id))
            .ToArray();

        // Add these ids to the cache for 5 minutes.
        _serverCache.Set(token, servers, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(5)));

        return servers;
    }

    public async Task<ulong> GetUserId(string token)
    {
        // Check the cache for an existing value, to save on http requests.
        if (_userCache.TryGetValue(token, out ulong? userIdMaybe)) return userIdMaybe ?? throw new UnauthorizedException();

        HttpRequestMessage request = new (HttpMethod.Get, "/api/users/@me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response = await _httpClient.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitException();

        // Throw save null if discord does not like the request.
        if (!response.IsSuccessStatusCode)
        {
            // If the request failed, store null (as this token does not have permission to anything).
            _userCache.Set(token, (ulong?)null, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromHours(1)));

            throw new UnauthorizedException();
        }

        // Parse and read the user ids from the response.
        string body = await response.Content.ReadAsStringAsync();
        ulong userId = ulong.Parse(JsonSerializer.Deserialize<User>(body)!.Id);

        _userCache.Set(token, userId, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromHours(1)));

        return userId;
    }
}

public class UnauthorizedException : Exception
{
}

public class RateLimitException : Exception
{
}