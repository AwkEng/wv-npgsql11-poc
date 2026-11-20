using Npgsql;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;

// Wolverine.Postgresql read NpgsqlConnectionStringBuilder.Multiplexing and
// .WriteCoalescingBufferThresholdBytes from PostgresqlMessageStore.Describe(). Npgsql 11.0.0 removed both,
// so the store type failed to JIT and the host never started. Nothing here talks to a database: Describe()
// and the JIT failure both happen before the driver opens a socket.

Console.WriteLine($"Npgsql    {typeof(NpgsqlConnection).Assembly.GetName().Version}");
Console.WriteLine($"Wolverine {typeof(WolverineOptions).Assembly.GetName().Version}");

// Deliberately unreachable, and that is fine — the failure is in Wolverine's own configuration, and on a
// working combination the run still gets as far as the connection attempt and reports a SocketException.
var connectionString = "Host=127.0.0.1;Port=5499;Database=poc;Username=u;Password=p";

var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts => opts.UsePostgresqlPersistenceAndTransport(connectionString, "poc"));

using var host = builder.Build();
try
{
    var store = host.Services.GetRequiredService<IMessageStore>();
    var descriptor = store.Describe();
    Console.WriteLine($"Describe() OK  engine={descriptor.Engine} host={descriptor.ServerName} port={descriptor.Port}");
    foreach (var name in new[] { "Multiplexing", "WriteCoalescingBufferThresholdBytes" })
    {
        var entry = descriptor.Properties.FirstOrDefault(x => x.Name == name);
        // Type and RawValue's CLR type are printed too: the recommended fix rebuilds these entries by hand
        // from the connection string dictionary, so the descriptor's typed shape has to match, not just the
        // displayed text.
        Console.WriteLine(entry is null
            ? $"  {name,-36} <absent>"
            : $"  {name,-36} {entry.Value}  type={entry.Type} raw={entry.RawValue?.GetType().Name}  subject={entry.Subject}");
    }
}
catch (MissingMethodException e)
{
    Console.WriteLine($"REPRODUCED  MissingMethodException: {e.Message}");
    return 42;
}

try
{
    await host.StartAsync();
    await host.StopAsync();
    Console.WriteLine("host started");
}
catch (Exception e)
{
    var inner = e;
    while (inner.InnerException is not null) inner = inner.InnerException;
    Console.WriteLine($"host start: {inner.GetType().Name}: {inner.Message.Split('\n')[0]}");
    return inner is MissingMethodException ? 42 : 0;
}
return 0;
