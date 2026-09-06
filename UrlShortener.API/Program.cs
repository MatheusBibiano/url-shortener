using HashidsNet;
using Microsoft.AspNetCore.Mvc;
using Scalar.AspNetCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddSingleton<IConnectionMultiplexer>(_ => 
    ConnectionMultiplexer.Connect(
        builder.Configuration.GetValue<string>("RedisIncr:ConnectionString")!)
    );

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
    IConnectionMultiplexer connection,
    HttpContext httpContext) =>
{
    if (!Uri.TryCreate(request.LongUrl, UriKind.Absolute, out var uriResult) ||
        (uriResult.Scheme != Uri.UriSchemeHttp && uriResult.Scheme != Uri.UriSchemeHttps))
    {
        return Results.BadRequest(new { error = "Invalid URL" });
    }
    
    var key = configuration.GetValue<string>("RedisIncr:Key");
    var salt = configuration.GetValue<string>("HashId:Salt");
    var hashLength = configuration.GetValue<int>("HashId:Length");
    
    var redis = connection.GetDatabase();
    var id = await redis.StringIncrementAsync(key);
    var hashids = new Hashids(salt, hashLength);
    
    var shortenCode = hashids.EncodeLong(id);
    var shortUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/{shortenCode}";
    
    return Results.Created(string.Empty, new { shortUrl });
});

app.Run();

internal record ShortenRequest(string LongUrl);
