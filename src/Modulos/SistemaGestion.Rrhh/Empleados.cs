using Dapper;
using Microsoft.AspNetCore.Http;
using Npgsql;
using SistemaGestion.Compartido;
using SistemaGestion.Compartido.Eventos;

namespace SistemaGestion.Rrhh;

/// <summary>Legajos del personal. Sólo los roles rrhh y administrador los leen y escriben.</summary>
internal static class Empleados
{
    private const string Recurso = "empleado";
    private const int MaxResultados = 200;

    private const string Columnas = """
        e.id, e.legajo, e.apellidos, e.nombres, e.cuil, e.fecha_nacimiento, e.sexo_registral, e.email, e.telefono,
        e.domicilio, e.modalidad, e.convenio, e.categoria, e.horas_semanales, e.sector_id, e.puesto_id,
        e.fecha_ingreso, e.fecha_egreso, e.motivo_egreso, e.profesional_id, e.usuario_id, e.version,
        (e.fecha_egreso IS NULL OR e.fecha_egreso >= rrhh_hoy.hoy) AS activo
        """;

    // Fecha de hoy en la zona horaria de la empresa: un egreso "hoy" sigue activo hasta que termine el día.
    private const string Hoy = """
        (SELECT (now() AT TIME ZONE zona_horaria)::date AS hoy FROM nucleo.empresas WHERE id = nucleo.empresa_actual()) rrhh_hoy
        """;

    public static bool PuedeOperar(ContextoSolicitud ctx) => ctx.TieneAlgunRol(Roles.Rrhh, Roles.Administrador);

    public static IResult SinPermiso() => Respuestas.SinPermiso("Se requiere el rol rrhh o administrador");

    public static async Task<IResult> CrearAsync(CrearEmpleadoSolicitud x, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!PuedeOperar(ctx))
            return SinPermiso();

        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var errores = Validacion.Validar(x, await HoyAsync(s, ct));
        if (errores.Count > 0)
            return Results.ValidationProblem(errores);

        var id = Guid.CreateVersion7();
        try
        {
            await s.Conexion.ExecuteAsync(new CommandDefinition("""
                INSERT INTO rrhh.empleados
                    (id, empresa_id, legajo, apellidos, nombres, cuil, fecha_nacimiento, sexo_registral, email, telefono,
                     domicilio, modalidad, convenio, categoria, horas_semanales, sector_id, puesto_id, fecha_ingreso,
                     profesional_id, usuario_id, creado_por)
                VALUES
                    (@id, @empresa, @Legajo, @apellidos, @nombres, @Cuil, @nacimiento::date, @SexoRegistral, @email, @telefono,
                     @domicilio, @Modalidad, @convenio, @categoria, @horas, @SectorId, @PuestoId, @ingreso::date,
                     @ProfesionalId, @UsuarioId, @usuario)
                """, new
            {
                id,
                empresa = ctx.EmpresaId,
                usuario = ctx.UsuarioId,
                x.Legajo,
                apellidos = x.Apellidos.Trim(),
                nombres = x.Nombres.Trim(),
                x.Cuil,
                nacimiento = Fecha(x.FechaNacimiento),
                x.SexoRegistral,
                email = Limpiar(x.Email),
                telefono = Limpiar(x.Telefono),
                domicilio = Limpiar(x.Domicilio),
                x.Modalidad,
                convenio = Limpiar(x.Convenio),
                categoria = Limpiar(x.Categoria),
                horas = (short?)x.HorasSemanales,
                x.SectorId,
                x.PuestoId,
                ingreso = Fecha(x.FechaIngreso),
                x.ProfesionalId,
                x.UsuarioId,
            }, s.Transaccion, cancellationToken: ct));
        }
        catch (PostgresException ex) when (TraducirError(ex) is { } r)
        {
            return r;
        }

        var dto = (await ObtenerAsync(s, id, ct))!;
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Crear, Recurso, id, dto, ct);
        await BandejaSalida.AgregarAsync(s, EventosRrhh.EmpleadoAlta, 1, EventosRrhh.AgregadoEmpleado, id, ParaEvento(dto), ct);
        await s.ConfirmarAsync(ct);
        return Respuestas.ConVersion(Results.Created($"/api/v1/rrhh/empleados/{id}", dto), dto.Version);
    }

    public static async Task<IResult> LeerAsync(Guid id, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!PuedeOperar(ctx))
            return SinPermiso();

        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var dto = await ObtenerAsync(s, id, ct);
        if (dto is null)
            return Respuestas.NoEncontrado("Empleado");
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Leer, Recurso, id, null, ct);
        await s.ConfirmarAsync(ct);
        return Respuestas.ConVersion(Results.Ok(dto), dto.Version);
    }

    public static async Task<IResult> ListarAsync(
        string? texto, Guid? sectorId, bool? incluirEgresados, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!PuedeOperar(ctx))
            return SinPermiso();

        var filtro = string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
        int? legajo = int.TryParse(filtro, out var n) ? n : null;
        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var filas = (await s.Conexion.QueryAsync<EmpleadoResumenDto>(new CommandDefinition($"""
            SELECT e.id, e.legajo, e.apellidos, e.nombres, se.nombre AS sector, pu.nombre AS puesto, e.modalidad,
                   e.profesional_id AS ProfesionalId, (e.fecha_egreso IS NULL OR e.fecha_egreso >= rrhh_hoy.hoy) AS activo
            FROM rrhh.empleados e
            JOIN rrhh.sectores se ON se.id = e.sector_id
            JOIN rrhh.puestos pu ON pu.id = e.puesto_id
            CROSS JOIN {Hoy}
            WHERE (@sectorId::uuid IS NULL OR e.sector_id = @sectorId)
              AND (@incluirEgresados OR e.fecha_egreso IS NULL OR e.fecha_egreso >= rrhh_hoy.hoy)
              AND (@filtro::text IS NULL
                   OR e.legajo = @legajo
                   OR e.cuil = @filtro
                   OR lower(e.apellidos || ' ' || e.nombres) LIKE '%' || lower(@filtro) || '%')
            ORDER BY e.apellidos, e.nombres
            LIMIT {MaxResultados}
            """, new { sectorId, incluirEgresados = incluirEgresados ?? false, filtro, legajo },
            s.Transaccion, cancellationToken: ct))).ToList();
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Buscar, Recurso, null,
            new { texto = filtro, sectorId, incluirEgresados, resultados = filas.Count }, ct);
        await s.ConfirmarAsync(ct);
        return Results.Ok(filas);
    }

    public static async Task<IResult> ModificarAsync(
        Guid id, ModificarEmpleadoSolicitud x, HttpRequest req, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!PuedeOperar(ctx))
            return SinPermiso();
        if (!Respuestas.LeerVersion(req, out var version))
            return Respuestas.FaltaVersion(Recurso);

        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var errores = Validacion.Validar(x, await HoyAsync(s, ct));
        if (errores.Count > 0)
            return Results.ValidationProblem(errores);
        var anterior = await ObtenerAsync(s, id, ct);
        if (anterior is null)
            return Respuestas.NoEncontrado("Empleado");

        int filas;
        try
        {
            filas = await s.Conexion.ExecuteAsync(new CommandDefinition("""
                UPDATE rrhh.empleados SET
                    apellidos = @apellidos, nombres = @nombres, fecha_nacimiento = @nacimiento::date,
                    sexo_registral = @SexoRegistral, email = @email, telefono = @telefono, domicilio = @domicilio,
                    modalidad = @Modalidad, convenio = @convenio, categoria = @categoria, horas_semanales = @horas,
                    sector_id = @SectorId, puesto_id = @PuestoId, fecha_ingreso = @ingreso::date,
                    profesional_id = @ProfesionalId, usuario_id = @UsuarioId,
                    version = version + 1, modificado_en = now(), modificado_por = @usuario
                WHERE id = @id AND version = @version
                """, new
            {
                id,
                version,
                usuario = ctx.UsuarioId,
                apellidos = x.Apellidos.Trim(),
                nombres = x.Nombres.Trim(),
                nacimiento = Fecha(x.FechaNacimiento),
                x.SexoRegistral,
                email = Limpiar(x.Email),
                telefono = Limpiar(x.Telefono),
                domicilio = Limpiar(x.Domicilio),
                x.Modalidad,
                convenio = Limpiar(x.Convenio),
                categoria = Limpiar(x.Categoria),
                horas = (short?)x.HorasSemanales,
                x.SectorId,
                x.PuestoId,
                ingreso = Fecha(x.FechaIngreso),
                x.ProfesionalId,
                x.UsuarioId,
            }, s.Transaccion, cancellationToken: ct));
        }
        catch (PostgresException ex) when (TraducirError(ex) is { } r)
        {
            return r;
        }
        if (filas == 0)
            return Respuestas.VersionVieja(Recurso);

        var actual = (await ObtenerAsync(s, id, ct))!;
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Modificar, Recurso, id, new { anterior, actual }, ct);
        await BandejaSalida.AgregarAsync(s, EventosRrhh.EmpleadoModificado, 1, EventosRrhh.AgregadoEmpleado, id, ParaEvento(actual), ct);
        await s.ConfirmarAsync(ct);
        return Respuestas.ConVersion(Results.Ok(actual), actual.Version);
    }

    public static async Task<IResult> EgresoAsync(Guid id, EgresoSolicitud x, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!PuedeOperar(ctx))
            return SinPermiso();
        if (string.IsNullOrWhiteSpace(x.Motivo))
            return Respuestas.Invalido("motivo", "El motivo de egreso es obligatorio.");

        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var anterior = await ObtenerAsync(s, id, ct);
        if (anterior is null)
            return Respuestas.NoEncontrado("Empleado");
        if (anterior.FechaEgreso is not null)
            return Respuestas.Conflicto("El empleado ya tiene registrado un egreso");
        if (x.Fecha < anterior.FechaIngreso)
            return Respuestas.Invalido("fecha", "El egreso no puede ser anterior al ingreso.");

        await s.Conexion.ExecuteAsync(new CommandDefinition("""
            UPDATE rrhh.empleados
            SET fecha_egreso = @fecha::date, motivo_egreso = @motivo, version = version + 1, modificado_en = now(), modificado_por = @usuario
            WHERE id = @id AND fecha_egreso IS NULL
            """, new { id, fecha = Fecha(x.Fecha), motivo = x.Motivo.Trim(), usuario = ctx.UsuarioId },
            s.Transaccion, cancellationToken: ct));

        // Las licencias pedidas o aprobadas que empiezan después del egreso ya no corresponden.
        var anuladas = (await s.Conexion.QueryAsync<Guid>(new CommandDefinition("""
            UPDATE rrhh.licencias SET estado = 'ANULADA', version = version + 1
            WHERE empleado_id = @id AND estado IN ('SOLICITADA', 'APROBADA') AND desde > @fecha::date
            RETURNING id
            """, new { id, fecha = Fecha(x.Fecha) }, s.Transaccion, cancellationToken: ct))).ToList();

        var actual = (await ObtenerAsync(s, id, ct))!;
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Modificar, Recurso, id,
            new { operacion = "egreso", x.Fecha, motivo = x.Motivo.Trim(), licenciasAnuladas = anuladas }, ct);
        await BandejaSalida.AgregarAsync(s, EventosRrhh.EmpleadoEgreso, 1, EventosRrhh.AgregadoEmpleado, id, ParaEvento(actual), ct);
        await s.ConfirmarAsync(ct);
        return Respuestas.ConVersion(Results.Ok(actual), actual.Version);
    }

    /// <summary>
    /// Un reingreso reactiva el mismo legajo (una persona, un legajo). El período anterior queda en la auditoría.
    /// </summary>
    public static async Task<IResult> ReingresoAsync(Guid id, ReingresoSolicitud x, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!PuedeOperar(ctx))
            return SinPermiso();

        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var anterior = await ObtenerAsync(s, id, ct);
        if (anterior is null)
            return Respuestas.NoEncontrado("Empleado");
        if (anterior.FechaEgreso is not { } egreso)
            return Respuestas.Conflicto("El empleado no tiene un egreso registrado");
        if (x.Fecha <= egreso)
            return Respuestas.Invalido("fecha", "El reingreso debe ser posterior al último egreso.");

        await s.Conexion.ExecuteAsync(new CommandDefinition("""
            UPDATE rrhh.empleados
            SET fecha_ingreso = @fecha::date, fecha_egreso = NULL, motivo_egreso = NULL,
                version = version + 1, modificado_en = now(), modificado_por = @usuario
            WHERE id = @id
            """, new { id, fecha = Fecha(x.Fecha), usuario = ctx.UsuarioId }, s.Transaccion, cancellationToken: ct));

        var actual = (await ObtenerAsync(s, id, ct))!;
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Modificar, Recurso, id,
            new { operacion = "reingreso", periodoAnterior = new { desde = anterior.FechaIngreso, hasta = egreso, motivo = anterior.MotivoEgreso }, x.Fecha }, ct);
        await BandejaSalida.AgregarAsync(s, EventosRrhh.EmpleadoReingreso, 1, EventosRrhh.AgregadoEmpleado, id, ParaEvento(actual), ct);
        await s.ConfirmarAsync(ct);
        return Respuestas.ConVersion(Results.Ok(actual), actual.Version);
    }

    public static async Task<EmpleadoDto?> ObtenerAsync(SesionBd s, Guid id, CancellationToken ct)
    {
        var f = await s.Conexion.QuerySingleOrDefaultAsync<FilaEmpleado>(new CommandDefinition(
            $"SELECT {Columnas} FROM rrhh.empleados e CROSS JOIN {Hoy} WHERE e.id = @id",
            new { id }, s.Transaccion, cancellationToken: ct));
        return f?.ADto();
    }

    public static Task<DateOnly> HoyAsync(SesionBd s, CancellationToken ct) =>
        s.Conexion.ExecuteScalarAsync<DateOnly>(new CommandDefinition(
            $"SELECT rrhh_hoy.hoy FROM {Hoy}", transaction: s.Transaccion, cancellationToken: ct));

    /// <summary>
    /// Lo que viaja a otros sistemas: identifica a la persona y su puesto, sin CUIL, nacimiento ni contacto.
    /// Quien necesite más datos los pide por la API, con su usuario y su auditoría.
    /// </summary>
    private static object ParaEvento(EmpleadoDto e) => new
    {
        e.Id, e.Legajo, e.Apellidos, e.Nombres, e.Modalidad, e.SectorId, e.PuestoId,
        e.FechaIngreso, e.FechaEgreso, e.ProfesionalId, e.UsuarioId, e.Activo, e.Version,
    };

    private static IResult? TraducirError(PostgresException ex) => ex.ConstraintName switch
    {
        "uq_empleados_cuil" => Respuestas.Conflicto("Ya existe un legajo con ese CUIL. Si la persona reingresa, usá el reingreso de su legajo."),
        "uq_empleados_profesional" => Respuestas.Conflicto("Ese profesional ya tiene un legajo"),
        _ when Respuestas.EsDuplicado(ex) => Respuestas.Conflicto("Ya existe un empleado con ese número de legajo"),
        _ when Respuestas.EsReferenciaInvalida(ex) => Respuestas.NoProcesable("El sector, puesto, profesional o usuario no existe en esta empresa"),
        _ => null,
    };

    internal static string Fecha(DateOnly d) => d.ToString("yyyy-MM-dd");

    internal static string? Limpiar(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>Fila de rrhh.empleados. Clase con propiedades porque Dapper no mapea DateOnly ni smallint por constructor.</summary>
    private sealed class FilaEmpleado
    {
        public Guid id { get; init; }
        public int legajo { get; init; }
        public string apellidos { get; init; } = "";
        public string nombres { get; init; } = "";
        public string cuil { get; init; } = "";
        public DateOnly fecha_nacimiento { get; init; }
        public string sexo_registral { get; init; } = "";
        public string? email { get; init; }
        public string? telefono { get; init; }
        public string? domicilio { get; init; }
        public string modalidad { get; init; } = "";
        public string? convenio { get; init; }
        public string? categoria { get; init; }
        public short? horas_semanales { get; init; }
        public Guid sector_id { get; init; }
        public Guid puesto_id { get; init; }
        public DateOnly fecha_ingreso { get; init; }
        public DateOnly? fecha_egreso { get; init; }
        public string? motivo_egreso { get; init; }
        public Guid? profesional_id { get; init; }
        public Guid? usuario_id { get; init; }
        public int version { get; init; }
        public bool activo { get; init; }

        public EmpleadoDto ADto() => new(id, legajo, apellidos, nombres, cuil, fecha_nacimiento, sexo_registral, email, telefono,
            domicilio, modalidad, convenio, categoria, horas_semanales, sector_id, puesto_id, fecha_ingreso, fecha_egreso,
            motivo_egreso, profesional_id, usuario_id, activo, version);
    }
}
