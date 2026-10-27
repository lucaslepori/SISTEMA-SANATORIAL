using Dapper;
using Microsoft.AspNetCore.Http;
using SistemaGestion.Compartido;

namespace SistemaGestion.Rrhh;

/// <summary>
/// Documentación con vencimiento (matrícula, seguro de mala praxis, apto médico, vacunas).
/// Renovar es cargar un documento nuevo: el anterior queda como historia.
/// </summary>
internal static class Documentos
{
    private const string Recurso = "documento_empleado";
    private const int MaxDiasAviso = 365;

    public static async Task<IResult> CrearAsync(
        Guid empleadoId, CrearDocumentoSolicitud x, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!Empleados.PuedeOperar(ctx))
            return Empleados.SinPermiso();
        var errores = Validacion.Validar(x);
        if (errores.Count > 0)
            return Results.ValidationProblem(errores);

        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        if (await Empleados.ObtenerAsync(s, empleadoId, ct) is null)
            return Respuestas.NoEncontrado("Empleado");

        var dto = new DocumentoDto(Guid.CreateVersion7(), empleadoId, x.Tipo, x.Descripcion.Trim(), Empleados.Limpiar(x.Numero),
            x.Emitido, x.Vence);
        await s.Conexion.ExecuteAsync(new CommandDefinition("""
            INSERT INTO rrhh.documentos (id, empresa_id, empleado_id, tipo, descripcion, numero, emitido, vence, creado_por)
            VALUES (@Id, @empresa, @EmpleadoId, @Tipo, @Descripcion, @Numero, @emitido::date, @vence::date, @usuario)
            """, new
        {
            dto.Id, empresa = ctx.EmpresaId, dto.EmpleadoId, dto.Tipo, dto.Descripcion, dto.Numero,
            emitido = dto.Emitido is { } e ? Empleados.Fecha(e) : null, vence = dto.Vence is { } v ? Empleados.Fecha(v) : null,
            usuario = ctx.UsuarioId,
        }, s.Transaccion, cancellationToken: ct));

        await Auditoria.RegistrarAsync(s, AccionAuditoria.Crear, Recurso, dto.Id, dto, ct);
        await s.ConfirmarAsync(ct);
        return Results.Created($"/api/v1/rrhh/empleados/{empleadoId}/documentos/{dto.Id}", dto);
    }

    public static async Task<IResult> ListarAsync(Guid empleadoId, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!Empleados.PuedeOperar(ctx))
            return Empleados.SinPermiso();

        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        if (await Empleados.ObtenerAsync(s, empleadoId, ct) is null)
            return Respuestas.NoEncontrado("Empleado");
        var filas = (await s.Conexion.QueryAsync<FilaDocumento>(new CommandDefinition("""
            SELECT id, empleado_id, tipo, descripcion, numero, emitido, vence
            FROM rrhh.documentos WHERE empleado_id = @empleadoId
            ORDER BY tipo, vence DESC NULLS FIRST, creado_en DESC
            """, new { empleadoId }, s.Transaccion, cancellationToken: ct))).Select(f => f.ADto()).ToList();
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Leer, Recurso, null, new { empleadoId }, ct);
        await s.ConfirmarAsync(ct);
        return Results.Ok(filas);
    }

    /// <summary>
    /// Qué vence: por persona activa y tipo, el documento cargado más recientemente, si ya venció o vence dentro de los días pedidos.
    /// Un documento renovado tapa al anterior aunque el anterior esté vencido.
    /// </summary>
    public static async Task<IResult> VencimientosAsync(int? dias, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!Empleados.PuedeOperar(ctx))
            return Empleados.SinPermiso();
        var plazo = dias ?? 30;
        if (plazo is < 0 or > MaxDiasAviso)
            return Respuestas.Invalido("dias", $"El plazo va de 0 a {MaxDiasAviso} días.");

        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var hoy = await Empleados.HoyAsync(s, ct);
        var filas = (await s.Conexion.QueryAsync<FilaVencimiento>(new CommandDefinition("""
            SELECT d.id, d.empleado_id, e.legajo, e.apellidos, e.nombres, d.tipo, d.descripcion, d.vence
            FROM (
                SELECT DISTINCT ON (empleado_id, tipo) *
                FROM rrhh.documentos
                ORDER BY empleado_id, tipo, creado_en DESC
            ) d
            JOIN rrhh.empleados e ON e.id = d.empleado_id
            WHERE d.vence IS NOT NULL AND d.vence <= @limite::date
              AND (e.fecha_egreso IS NULL OR e.fecha_egreso >= @hoy::date)
            ORDER BY d.vence, e.apellidos
            """, new { limite = Empleados.Fecha(hoy.AddDays(plazo)), hoy = Empleados.Fecha(hoy) },
            s.Transaccion, cancellationToken: ct))).Select(f => f.ADto(hoy)).ToList();
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Buscar, Recurso, null, new { dias = plazo, resultados = filas.Count }, ct);
        await s.ConfirmarAsync(ct);
        return Results.Ok(filas);
    }

    private sealed class FilaDocumento
    {
        public Guid id { get; init; }
        public Guid empleado_id { get; init; }
        public string tipo { get; init; } = "";
        public string descripcion { get; init; } = "";
        public string? numero { get; init; }
        public DateOnly? emitido { get; init; }
        public DateOnly? vence { get; init; }

        public DocumentoDto ADto() => new(id, empleado_id, tipo, descripcion, numero, emitido, vence);
    }

    private sealed class FilaVencimiento
    {
        public Guid id { get; init; }
        public Guid empleado_id { get; init; }
        public int legajo { get; init; }
        public string apellidos { get; init; } = "";
        public string nombres { get; init; } = "";
        public string tipo { get; init; } = "";
        public string descripcion { get; init; } = "";
        public DateOnly vence { get; init; }

        public VencimientoDto ADto(DateOnly hoy) =>
            new(id, empleado_id, legajo, apellidos, nombres, tipo, descripcion, vence, vence.DayNumber - hoy.DayNumber);
    }
}
