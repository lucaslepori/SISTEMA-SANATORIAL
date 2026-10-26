using Dapper;
using Microsoft.AspNetCore.Http;
using Npgsql;
using SistemaGestion.Compartido;
using SistemaGestion.Compartido.Eventos;

namespace SistemaGestion.Rrhh;

/// <summary>
/// Licencias y ausencias. Se piden, se aprueban o rechazan, y se anulan; nunca se borran.
/// La base impide que una persona tenga dos licencias pedidas o aprobadas que se superpongan.
/// </summary>
internal static class Licencias
{
    private const string Recurso = "licencia";

    private const string Columnas = """
        l.id, l.empleado_id, l.tipo, l.desde, l.hasta, l.estado, l.observaciones, l.motivo_rechazo, l.version
        """;

    public static async Task<IResult> CrearAsync(
        Guid empleadoId, CrearLicenciaSolicitud x, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!Empleados.PuedeOperar(ctx))
            return Empleados.SinPermiso();
        var errores = Validacion.Validar(x);
        if (errores.Count > 0)
            return Results.ValidationProblem(errores);

        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var empleado = await Empleados.ObtenerAsync(s, empleadoId, ct);
        if (empleado is null)
            return Respuestas.NoEncontrado("Empleado");
        if (x.Desde < empleado.FechaIngreso || (empleado.FechaEgreso is { } egreso && x.Hasta > egreso))
            return Respuestas.NoProcesable("La licencia cae fuera del período en que la persona trabaja en la empresa");

        var id = Guid.CreateVersion7();
        try
        {
            await s.Conexion.ExecuteAsync(new CommandDefinition("""
                INSERT INTO rrhh.licencias (id, empresa_id, empleado_id, tipo, desde, hasta, observaciones, creado_por)
                VALUES (@id, @empresa, @empleadoId, @Tipo, @desde::date, @hasta::date, @obs, @usuario)
                """, new
            {
                id, empresa = ctx.EmpresaId, empleadoId, x.Tipo, desde = Empleados.Fecha(x.Desde), hasta = Empleados.Fecha(x.Hasta),
                obs = Empleados.Limpiar(x.Observaciones), usuario = ctx.UsuarioId,
            }, s.Transaccion, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.ConstraintName == "ex_licencias_superpuestas")
        {
            return Respuestas.Conflicto("La persona ya tiene una licencia pedida o aprobada que se superpone con esas fechas");
        }

        var dto = (await ObtenerAsync(s, id, ct))!;
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Crear, Recurso, id, dto, ct);
        await s.ConfirmarAsync(ct);
        return Respuestas.ConVersion(Results.Created($"/api/v1/rrhh/licencias/{id}", dto), dto.Version);
    }

    public static async Task<IResult> ListarAsync(
        Guid? empleadoId, string? estado, DateOnly? desde, DateOnly? hasta, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!Empleados.PuedeOperar(ctx))
            return Empleados.SinPermiso();
        if (empleadoId is null && (desde is null || hasta is null))
            return Respuestas.Invalido("criterio", "Indicá un empleado o un rango de fechas (desde y hasta).");
        if (desde is { } d && hasta is { } h && h < d)
            return Respuestas.Invalido("hasta", "El rango termina antes de empezar.");

        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var filas = (await s.Conexion.QueryAsync<FilaLicencia>(new CommandDefinition($"""
            SELECT {Columnas} FROM rrhh.licencias l
            WHERE (@empleadoId::uuid IS NULL OR l.empleado_id = @empleadoId)
              AND (@estado::text IS NULL OR l.estado = @estado)
              AND (@desde::date IS NULL OR l.hasta >= @desde::date)
              AND (@hasta::date IS NULL OR l.desde <= @hasta::date)
            ORDER BY l.desde, l.empleado_id
            LIMIT 500
            """, new
        {
            empleadoId, estado, desde = desde is { } a ? Empleados.Fecha(a) : null, hasta = hasta is { } b ? Empleados.Fecha(b) : null,
        }, s.Transaccion, cancellationToken: ct))).Select(f => f.ADto()).ToList();
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Buscar, Recurso, empleadoId,
            new { empleadoId, estado, desde, hasta, resultados = filas.Count }, ct);
        await s.ConfirmarAsync(ct);
        return Results.Ok(filas);
    }

    public static async Task<IResult> ResolverAsync(
        Guid id, ResolverLicenciaSolicitud x, HttpRequest req, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!Empleados.PuedeOperar(ctx))
            return Empleados.SinPermiso();
        if (!Respuestas.LeerVersion(req, out var version))
            return Respuestas.FaltaVersion(Recurso);
        if (!x.Aprobar && string.IsNullOrWhiteSpace(x.MotivoRechazo))
            return Respuestas.Invalido("motivoRechazo", "Para rechazar una licencia hay que indicar el motivo.");

        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var anterior = await ObtenerAsync(s, id, ct);
        if (anterior is null)
            return Respuestas.NoEncontrado("Licencia");
        if (anterior.Estado != EstadosLicencia.Solicitada)
            return Respuestas.Conflicto($"Sólo se resuelve una licencia solicitada; esta está {anterior.Estado}");

        var nuevo = x.Aprobar ? EstadosLicencia.Aprobada : EstadosLicencia.Rechazada;
        var filas = await s.Conexion.ExecuteAsync(new CommandDefinition("""
            UPDATE rrhh.licencias
            SET estado = @nuevo, motivo_rechazo = @motivo, resuelta_en = now(), resuelta_por = @usuario, version = version + 1
            WHERE id = @id AND version = @version AND estado = 'SOLICITADA'
            """, new
        {
            id, version, nuevo, motivo = x.Aprobar ? null : x.MotivoRechazo!.Trim(), usuario = ctx.UsuarioId,
        }, s.Transaccion, cancellationToken: ct));
        if (filas == 0)
            return Respuestas.VersionVieja(Recurso);

        var actual = (await ObtenerAsync(s, id, ct))!;
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Modificar, Recurso, id, new { anterior, actual }, ct);
        if (x.Aprobar)
            await BandejaSalida.AgregarAsync(s, EventosRrhh.LicenciaAprobada, 1, EventosRrhh.AgregadoLicencia, id,
                await ParaEventoAsync(s, actual, ct), ct);
        await s.ConfirmarAsync(ct);
        return Respuestas.ConVersion(Results.Ok(actual), actual.Version);
    }

    public static async Task<IResult> AnularAsync(Guid id, HttpRequest req, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!Empleados.PuedeOperar(ctx))
            return Empleados.SinPermiso();
        if (!Respuestas.LeerVersion(req, out var version))
            return Respuestas.FaltaVersion(Recurso);

        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var anterior = await ObtenerAsync(s, id, ct);
        if (anterior is null)
            return Respuestas.NoEncontrado("Licencia");
        if (anterior.Estado is not (EstadosLicencia.Solicitada or EstadosLicencia.Aprobada))
            return Respuestas.Conflicto($"La licencia ya está {anterior.Estado}");

        var filas = await s.Conexion.ExecuteAsync(new CommandDefinition("""
            UPDATE rrhh.licencias SET estado = 'ANULADA', version = version + 1
            WHERE id = @id AND version = @version AND estado IN ('SOLICITADA', 'APROBADA')
            """, new { id, version }, s.Transaccion, cancellationToken: ct));
        if (filas == 0)
            return Respuestas.VersionVieja(Recurso);

        var actual = (await ObtenerAsync(s, id, ct))!;
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Modificar, Recurso, id, new { anterior, actual }, ct);
        // Sólo interesa a otros sistemas si antes se había avisado la aprobación.
        if (anterior.Estado == EstadosLicencia.Aprobada)
            await BandejaSalida.AgregarAsync(s, EventosRrhh.LicenciaAnulada, 1, EventosRrhh.AgregadoLicencia, id,
                await ParaEventoAsync(s, actual, ct), ct);
        await s.ConfirmarAsync(ct);
        return Respuestas.ConVersion(Results.Ok(actual), actual.Version);
    }

    private static async Task<LicenciaDto?> ObtenerAsync(SesionBd s, Guid id, CancellationToken ct)
    {
        var f = await s.Conexion.QuerySingleOrDefaultAsync<FilaLicencia>(new CommandDefinition(
            $"SELECT {Columnas} FROM rrhh.licencias l WHERE l.id = @id", new { id }, s.Transaccion, cancellationToken: ct));
        return f?.ADto();
    }

    /// <summary>
    /// Para turnos y otros sistemas: quién falta y cuándo, con el profesional si lo es. El tipo no viaja:
    /// "ENFERMEDAD" es un dato de salud y a turnos sólo le importa que no atiende.
    /// </summary>
    private static async Task<object> ParaEventoAsync(SesionBd s, LicenciaDto l, CancellationToken ct)
    {
        var profesionalId = await s.Conexion.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT profesional_id FROM rrhh.empleados WHERE id = @id", new { id = l.EmpleadoId }, s.Transaccion, cancellationToken: ct));
        return new { l.Id, l.EmpleadoId, ProfesionalId = profesionalId, l.Desde, l.Hasta, l.Estado, l.Version };
    }

    private sealed class FilaLicencia
    {
        public Guid id { get; init; }
        public Guid empleado_id { get; init; }
        public string tipo { get; init; } = "";
        public DateOnly desde { get; init; }
        public DateOnly hasta { get; init; }
        public string estado { get; init; } = "";
        public string? observaciones { get; init; }
        public string? motivo_rechazo { get; init; }
        public int version { get; init; }

        public LicenciaDto ADto() => new(id, empleado_id, tipo, desde, hasta, hasta.DayNumber - desde.DayNumber + 1, estado,
            observaciones, motivo_rechazo, version);
    }
}
