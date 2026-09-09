using Cassandra;
using HashidsNet;
using Microsoft.AspNetCore.Mvc;
using Scalar.AspNetCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddSingleton<HashSet<string>>(_ =>
{
    var rawDomains = builder.Configuration.GetValue<string>("BlockedDomains") ?? string.Empty;
    var domains = rawDomains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    
    return new HashSet<string>(domains, StringComparer.OrdinalIgnoreCase);
});

builder.Services.AddKeyedSingleton<IConnectionMultiplexer>("RedisIncr", (_, _) => 
    ConnectionMultiplexer.Connect(
        builder.Configuration.GetValue<string>("RedisIncr:ConnectionString")!)
);

builder.Services.AddKeyedSingleton<IConnectionMultiplexer>("RedisCache", (_, _) => 
    ConnectionMultiplexer.Connect(
        builder.Configuration.GetValue<string>("RedisCache:ConnectionString")!)
);


builder.Services.AddSingleton<Cassandra.ISession>(_ =>
{
    var host = builder.Configuration.GetValue<string>("Cassandra:Host");
    var port = builder.Configuration.GetValue<int>("Cassandra:Port");
    var keySpace = builder.Configuration.GetValue<string>("Cassandra:KeySpace");
    var table = builder.Configuration.GetValue<string>("Cassandra:Table");
    
    var cluster = Cluster.Builder()
        .AddContactPoint(host)
        .WithPort(port)
        .Build();
    
    using var setupSession = cluster.Connect();
    
    setupSession.Execute($$"""
        CREATE KEYSPACE IF NOT EXISTS {{keySpace}} 
        WITH replication = {'class': 'SimpleStrategy', 'replication_factor': 1};
    """);
    
    setupSession.Execute($"""
        CREATE TABLE IF NOT EXISTS {keySpace}.{table} (
            short_code text PRIMARY KEY,
            long_url text,
            created_at timestamp
        );
    """);

    return cluster.Connect(keySpace);
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.MapPost("/shorten", async (
    [FromBody] ShortenRequest request,
    IConfiguration configuration,
    [FromKeyedServices("RedisIncr")] IConnectionMultiplexer incrConnection,
    Cassandra.ISession cassandraSession,
    HashSet<string> blockedDomains,
    HttpContext httpContext) =>
{
    if (!Uri.TryCreate(request.LongUrl, UriKind.Absolute, out var uriResult) ||
        (uriResult.Scheme != Uri.UriSchemeHttp && uriResult.Scheme != Uri.UriSchemeHttps))
    {
        return Results.BadRequest(new { error = "Invalid URL" });
    }
    
    if (blockedDomains.Contains(uriResult.Host))
        return Results.BadRequest(new { error = "Shortening URLs from external shorteners is not allowed" });
    
    var isSameHost = string.Equals(uriResult.Host, httpContext.Request.Host.Host, StringComparison.OrdinalIgnoreCase);
    var isSamePort = !httpContext.Request.Host.Port.HasValue || uriResult.Port == httpContext.Request.Host.Port.Value;

    if (isSameHost && isSamePort)
        return Results.BadRequest(new { error = "Cannot shorten an already shortened URL from this service" });
    
    var key = configuration.GetValue<string>("RedisIncr:Key");
    var salt = configuration.GetValue<string>("HashId:Salt");
    var hashLength = configuration.GetValue<int>("HashId:Length");
    var table = configuration.GetValue<string>("Cassandra:Table");
    
    var redis = incrConnection.GetDatabase();
    var id = await redis.StringIncrementAsync(key);
    var hashids = new Hashids(salt, hashLength);
    var shortenCode = hashids.EncodeLong(id);
    
    var statement = await cassandraSession.PrepareAsync(
        $"INSERT INTO {table} (short_code, long_url, created_at) VALUES (?, ?, ?)"
    );
    await cassandraSession.ExecuteAsync(statement.Bind(shortenCode, request.LongUrl, DateTimeOffset.UtcNow));
    
    var shortUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/{shortenCode}";
    
    return Results.Created(string.Empty, new { shortUrl });
});

app.MapGet("/{shortenCode}", async (
    string shortenCode,
    IConfiguration configuration,
    Cassandra.ISession cassandraSession,
    [FromKeyedServices("RedisCache")] IConnectionMultiplexer cacheConnection) =>
{
    var redisCache = cacheConnection.GetDatabase();
    
    string? cachedLongUrl = await redisCache.StringGetAsync(shortenCode);
    if (!string.IsNullOrEmpty(cachedLongUrl))
        return Results.Redirect(cachedLongUrl);
    
    var table = configuration.GetValue<string>("Cassandra:Table");
    
    var statement = await cassandraSession.PrepareAsync(
        $"SELECT long_url FROM {table} WHERE short_code = ?"
    );
    var rowSet = await cassandraSession.ExecuteAsync(statement.Bind(shortenCode));
    var row = rowSet.FirstOrDefault();

    if (row is null)
        return Results.NotFound(new { error = "URL not found" });

    var longUrl = row.GetValue<string>("long_url");
    
    var ttlHours = configuration.GetValue<int?>("RedisCache:TtlHours") ?? 24;
    await redisCache.StringSetAsync(shortenCode, longUrl, TimeSpan.FromHours(ttlHours));
    
    return Results.Redirect(longUrl);
});

app.Run();

internal record ShortenRequest(string LongUrl);
