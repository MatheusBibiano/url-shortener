using Cassandra;
namespace UrlShortener.API;

 public sealed partial class CassandraSchemaInitializer : IHostedLifecycleService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CassandraSchemaInitializer> _logger;

    public CassandraSchemaInitializer(
        IConfiguration configuration,
        ILogger<CassandraSchemaInitializer> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var host = _configuration.GetValue<string>("Cassandra:Host");
        var port = _configuration.GetValue<int>("Cassandra:Port");
        var keySpace = _configuration.GetValue<string>("Cassandra:KeySpace");
        var table = _configuration.GetValue<string>("Cassandra:Table");

        using var cluster = Cluster.Builder()
            .AddContactPoints(host)
            .WithPort(port)
            .Build();

        using var session = await cluster.ConnectAsync();

        LogCheckingKeyspace(keySpace!);

        await session.ExecuteAsync(new SimpleStatement($$"""
                                                             CREATE KEYSPACE IF NOT EXISTS {{keySpace}}
                                                             WITH replication = {'class': 'SimpleStrategy', 'replication_factor': 3};
                                                         """));

        await session.ExecuteAsync(new SimpleStatement($"""
                                                            CREATE TABLE IF NOT EXISTS {keySpace}.{table} (
                                                                short_code text PRIMARY KEY,
                                                                long_url text,
                                                                created_at timestamp
                                                            );
                                                        """));

        _logger.LogInformation("Cassandra schema successfully verified");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    
    [LoggerMessage(LogLevel.Information, "Checking Cassandra keyspace {KeySpace}")]
    private partial void LogCheckingKeyspace(string keySpace);
}
