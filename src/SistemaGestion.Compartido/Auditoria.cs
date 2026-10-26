using System.Text.Json;
using Dapper;

namespace SistemaGestion.Compartido;

public static class AccionAuditoria
{
    public const string Leer = "LEER";
    public const string Buscar = "BUSCAR";
    public const string Crear = "CREAR";
    public const string Modificar = "MODIFICAR";
}

/// <summary>
/// Registro de quién vio y quién cambió cada dato. Se escribe en la misma transacción
/// que la operación: si la auditoría falla, la operación no se confirma.
/// </summary>
public static class Auditoria
{
    public static Task RegistrarAsync(
        SesionBd sesion, string accion, string recursoTipo, Guid? recursoId, object? detalle, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO auditoria.eventos (empresa_id, usuario_id, accion, recurso_tipo, recurso_id, detalle, origen_ip)
            VALUES (@EmpresaId, @UsuarioId, @Accion, @RecursoTipo, @RecursoId, @Detalle::jsonb, @OrigenIp::inet)
            """;
        return sesion.Conexion.ExecuteAsync(new CommandDefinition(sql, new
        {
            sesion.Contexto.EmpresaId,
            sesion.Contexto.UsuarioId,
            Accion = accion,
            RecursoTipo = recursoTipo,
            RecursoId = recursoId,
            Detalle = JsonSerializer.Serialize(detalle ?? new { }, Json.Opciones),
            OrigenIp = sesion.Contexto.OrigenIp?.ToString(),
        }, sesion.Transaccion, cancellationToken: ct));
    }
}
