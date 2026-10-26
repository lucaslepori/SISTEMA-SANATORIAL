using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using SistemaGestion.Migraciones;

namespace SistemaGestion.Tests;

/// <summary>
/// Crea una base PostgreSQL nueva por corrida, aplica las migraciones reales y levanta la API.
/// Necesita la variable SG_TEST_DB_ADMIN con una cadena de conexión de superusuario, por ejemplo:
///   Host=localhost;Username=postgres;Password=postgres;Database=postgres
/// </summary>
public sealed class BaseDePrueba : IAsyncLifetime
{
    public const string Empresa1 = "0192f000-0000-7000-8000-000000000001";
    public const string Empresa2 = "0192f000-0000-7000-8000-000000000002";
    private const string ClaveApi = "sg_api_test_clave";
    private const string ClaveWorker = "sg_worker_test_clave";

    private readonly string _nombreBase = $"sg_test_{Guid.NewGuid():N}";
    private string _cadenaAdminServidor = "";

    public string CadenaAdmin { get; private set; } = "";
    public string CadenaApi { get; private set; } = "";
    public string CadenaWorker { get; private set; } = "";
    public WebApplicationFactory<Program> Fabrica { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _cadenaAdminServidor = Environment.GetEnvironmentVariable("SG_TEST_DB_ADMIN")
            ?? throw new InvalidOperationException(
                "Definí SG_TEST_DB_ADMIN con una cadena de conexión de superusuario de PostgreSQL para correr las pruebas.");

        await EjecutarAsync(_cadenaAdminServidor, $"CREATE DATABASE {_nombreBase}");
        CadenaAdmin = new NpgsqlConnectionStringBuilder(_cadenaAdminServidor) { Database = _nombreBase }.ToString();

        await Migrador.AplicarAsync(CadenaAdmin, TextWriter.Null);
        await EjecutarAsync(CadenaAdmin, $"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'sg_api_test') THEN
                    CREATE ROLE sg_api_test LOGIN NOBYPASSRLS PASSWORD '{ClaveApi}';
                END IF;
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'sg_worker_test') THEN
                    CREATE ROLE sg_worker_test LOGIN NOBYPASSRLS PASSWORD '{ClaveWorker}';
                END IF;
            END $$;
            GRANT sg_api TO sg_api_test;
            GRANT sg_worker TO sg_worker_test;
            """);
        await EjecutarAsync(CadenaAdmin, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "datos-desarrollo.sql")));

        CadenaApi = new NpgsqlConnectionStringBuilder(CadenaAdmin) { Username = "sg_api_test", Password = ClaveApi }.ToString();
        CadenaWorker = new NpgsqlConnectionStringBuilder(CadenaAdmin) { Username = "sg_worker_test", Password = ClaveWorker }.ToString();

        Fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            // UseSetting (y no ConfigureAppConfiguration) porque Program lee la configuración antes de Build().
            b.UseSetting("ConnectionStrings:Api", CadenaApi);
            b.UseSetting("ConnectionStrings:Worker", CadenaWorker);
            b.UseSetting("Autenticacion:Modo", "Desarrollo");
            b.UseSetting("Publicador:Habilitado", "false");
        });
    }

    public HttpClient Cliente(string usuario, string empresa)
    {
        var c = Fabrica.CreateClient();
        c.DefaultRequestHeaders.Add("X-Usuario-Sujeto", usuario);
        c.DefaultRequestHeaders.Add("X-Empresa-Id", empresa);
        return c;
    }

    public async Task<T?> EscalarAsync<T>(string sql)
    {
        await using var c = new NpgsqlConnection(CadenaAdmin);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return (T?)await cmd.ExecuteScalarAsync();
    }

    public static async Task EjecutarAsync(string cadena, string sql)
    {
        await using var c = new NpgsqlConnection(cadena);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await Fabrica.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await EjecutarAsync(_cadenaAdminServidor, $"DROP DATABASE IF EXISTS {_nombreBase} WITH (FORCE)");
    }
}

[CollectionDefinition(Nombre)]
public sealed class ColeccionBase : ICollectionFixture<BaseDePrueba>
{
    public const string Nombre = "base";
}
