using Npgsql;

namespace SistemaGestion.Compartido;

/// <summary>
/// Una transacción por solicitud con la empresa y el usuario fijados como variables locales
/// de la transacción. Las políticas de fila de PostgreSQL las leen con nucleo.empresa_actual().
/// Como son locales a la transacción, no se filtran a otra solicitud que reuse la conexión del pool.
/// </summary>
public sealed class SesionBd : IAsyncDisposable
{
    private SesionBd(NpgsqlConnection conexion, NpgsqlTransaction transaccion, ContextoSolicitud contexto)
    {
        Conexion = conexion;
        Transaccion = transaccion;
        Contexto = contexto;
    }

    public NpgsqlConnection Conexion { get; }
    public NpgsqlTransaction Transaccion { get; }
    public ContextoSolicitud Contexto { get; }

    public static async Task<SesionBd> AbrirAsync(NpgsqlDataSource origen, ContextoSolicitud contexto, CancellationToken ct)
    {
        if (!contexto.Resuelto)
            throw new InvalidOperationException("No se puede abrir una sesión de base sin usuario y empresa.");

        var conexion = await origen.OpenConnectionAsync(ct);
        try
        {
            var tx = await conexion.BeginTransactionAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT set_config('app.empresa_id', $1, true), set_config('app.usuario_id', $2, true)", conexion, tx);
            cmd.Parameters.AddWithValue(contexto.EmpresaId.ToString());
            cmd.Parameters.AddWithValue(contexto.UsuarioId.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
            return new SesionBd(conexion, tx, contexto);
        }
        catch
        {
            await conexion.DisposeAsync();
            throw;
        }
    }

    public Task ConfirmarAsync(CancellationToken ct) => Transaccion.CommitAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await Transaccion.DisposeAsync();
        await Conexion.DisposeAsync();
    }
}

/// <summary>Orígenes de conexión con roles distintos de la base.</summary>
public sealed record OrigenesBd(NpgsqlDataSource Api, NpgsqlDataSource Worker);
