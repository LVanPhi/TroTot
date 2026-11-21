using Dapper;
using Npgsql;
using Pgvector.Dapper;

namespace TroViec.Api.Infrastructure.Db;

public static class DatabaseSetup
{
    /// <summary>Registers one pooled NpgsqlDataSource (pgvector enabled) and the global Dapper settings.</summary>
    public static IServiceCollection AddDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        ConfigureDapper();
        services.AddSingleton(_ =>
        {
            var connectionString = configuration.GetConnectionString("Main")
                ?? throw new InvalidOperationException("ConnectionStrings__Main is not set");
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            // PostgreSQL here uses SCRAM over the Docker network, never Kerberos; skipping the GSS probe avoids a
            // "libgssapi_krb5.so.2 not found" error on every new connection in the slim .NET runtime image.
            builder.ConnectionStringBuilder.GssEncryptionMode = GssEncryptionMode.Disable;
            builder.UseVector();
            return builder.Build();
        });
        return services;
    }

    private static void ConfigureDapper()
    {
        // Global, set once at startup; idempotent so test hosts can call it repeatedly.
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.RemoveTypeMap(typeof(Pgvector.Vector));
        SqlMapper.AddTypeHandler(new VectorTypeHandler());
    }
}
