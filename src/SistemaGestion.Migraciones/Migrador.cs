using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace SistemaGestion.Migraciones;

/// <summary>
/// Aplica en orden los scripts db/migraciones/V###__nombre.sql, una sola vez cada uno.
/// Un script ya aplicado no se puede modificar: se agrega uno nuevo.
/// </summary>
public static partial class Migrador
{
    public sealed record Migracion(int Version, string Nombre, string Sql, string Checksum);

    public static IReadOnlyList<Migracion> Cargar()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith("migraciones.", StringComparison.Ordinal))
            .Select(n =>
            {
                var archivo = n["migraciones.".Length..];
                var m = PatronNombre().Match(archivo);
                if (!m.Success)
                    throw new InvalidOperationException($"Nombre de migración inválido: {archivo}");
                using var stream = asm.GetManifestResourceStream(n)!;
                using var lector = new StreamReader(stream, Encoding.UTF8);
                var sql = lector.ReadToEnd();
                var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
                return new Migracion(int.Parse(m.Groups[1].Value), archivo, sql, checksum);
            })
            .OrderBy(x => x.Version)
            .ToList();
    }

    public static async Task<int> AplicarAsync(string cadenaConexion, TextWriter log, CancellationToken ct = default)
    {
        var migraciones = Cargar();
        await using var conexion = new NpgsqlConnection(cadenaConexion);
        await conexion.OpenAsync(ct);

        await using (var cmd = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS public.migraciones_aplicadas (
                version     int PRIMARY KEY,
                nombre      text NOT NULL,
                checksum    text NOT NULL,
                aplicado_en timestamptz NOT NULL DEFAULT now()
            )
            """, conexion))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var aplicadas = new Dictionary<int, string>();
        await using (var cmd = new NpgsqlCommand("SELECT version, checksum FROM public.migraciones_aplicadas", conexion))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                aplicadas[r.GetInt32(0)] = r.GetString(1);
        }

        var nuevas = 0;
        foreach (var m in migraciones)
        {
            if (aplicadas.TryGetValue(m.Version, out var checksum))
            {
                if (checksum != m.Checksum)
                    throw new InvalidOperationException(
                        $"La migración {m.Nombre} ya fue aplicada y su contenido cambió. Creá una migración nueva en lugar de editarla.");
                continue;
            }

            await using var tx = await conexion.BeginTransactionAsync(ct);
            await using (var cmd = new NpgsqlCommand(m.Sql, conexion, tx))
                await cmd.ExecuteNonQueryAsync(ct);
            await using (var cmd = new NpgsqlCommand(
                "INSERT INTO public.migraciones_aplicadas (version, nombre, checksum) VALUES ($1, $2, $3)", conexion, tx))
            {
                cmd.Parameters.AddWithValue(m.Version);
                cmd.Parameters.AddWithValue(m.Nombre);
                cmd.Parameters.AddWithValue(m.Checksum);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            await log.WriteLineAsync($"Aplicada {m.Nombre}");
            nuevas++;
        }

        await log.WriteLineAsync(nuevas == 0 ? "La base ya estaba al día." : $"{nuevas} migraciones aplicadas.");
        return nuevas;
    }

    /// <summary>
    /// Crea la base indicada en la cadena si todavía no existe. Se conecta a la base de mantenimiento "postgres".
    /// </summary>
    public static async Task CrearBaseSiFaltaAsync(string cadenaConexion, TextWriter log, CancellationToken ct = default)
    {
        var datos = new NpgsqlConnectionStringBuilder(cadenaConexion);
        var nombre = datos.Database;
        if (string.IsNullOrEmpty(nombre) || !NombreBase().IsMatch(nombre))
            throw new InvalidOperationException($"Nombre de base inválido: '{nombre}'. Usar minúsculas, números y _.");
        datos.Database = "postgres";
        await using var conexion = new NpgsqlConnection(datos.ToString());
        await conexion.OpenAsync(ct);
        await using (var existe = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = $1", conexion))
        {
            existe.Parameters.AddWithValue(nombre);
            if (await existe.ExecuteScalarAsync(ct) is not null)
                return;
        }
        // El nombre ya se validó: no puede ir como parámetro en CREATE DATABASE.
        await using (var crear = new NpgsqlCommand($"CREATE DATABASE \"{nombre}\"", conexion))
            await crear.ExecuteNonQueryAsync(ct);
        await log.WriteLineAsync($"Base {nombre} creada.");
    }

    /// <summary>
    /// SÓLO PARA EL ENTORNO LOCAL: crea los usuarios de la aplicación con contraseñas conocidas
    /// y carga los datos de ejemplo. Se puede repetir sin duplicar nada.
    /// </summary>
    public static async Task CargarDatosDesarrolloAsync(string cadenaConexion, TextWriter log, CancellationToken ct = default)
    {
        var asm = Assembly.GetExecutingAssembly();
        await using var conexion = new NpgsqlConnection(cadenaConexion);
        await conexion.OpenAsync(ct);
        foreach (var nombre in asm.GetManifestResourceNames().Where(n => n.StartsWith("desarrollo.", StringComparison.Ordinal)).Order())
        {
            using var lector = new StreamReader(asm.GetManifestResourceStream(nombre)!, Encoding.UTF8);
            await using var cmd = new NpgsqlCommand(await lector.ReadToEndAsync(ct), conexion);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await log.WriteLineAsync("Usuarios locales y datos de ejemplo cargados.");
    }

    [GeneratedRegex(@"^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex NombreBase();

    [GeneratedRegex(@"^V(\d{3})__[a-z0-9_]+\.sql$")]
    private static partial Regex PatronNombre();
}
