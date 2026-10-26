using System.Text.Json;
using Dapper;

namespace SistemaGestion.Compartido.Eventos;

/// <summary>
/// Guarda un evento de integración en la misma transacción que el cambio que lo origina
/// (patrón outbox). Así nunca se publica un evento de algo que no se confirmó, ni se pierde
/// el evento de algo que sí se confirmó.
/// </summary>
public static class BandejaSalida
{
    public static Task AgregarAsync(
        SesionBd sesion, string tipo, int version, string agregadoTipo, Guid agregadoId, object datos, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO integracion.bandeja_salida (id, empresa_id, tipo, version, agregado_tipo, agregado_id, datos)
            VALUES (@Id, @EmpresaId, @Tipo, @Version, @AgregadoTipo, @AgregadoId, @Datos::jsonb)
            """;
        return sesion.Conexion.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = Guid.CreateVersion7(),
            sesion.Contexto.EmpresaId,
            Tipo = tipo,
            Version = version,
            AgregadoTipo = agregadoTipo,
            AgregadoId = agregadoId,
            Datos = JsonSerializer.Serialize(datos, Json.Opciones),
        }, sesion.Transaccion, cancellationToken: ct));
    }
}
