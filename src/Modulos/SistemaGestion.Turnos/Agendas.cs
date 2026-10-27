using Dapper;
using Microsoft.AspNetCore.Http;
using Npgsql;
using SistemaGestion.Compartido;
using SistemaGestion.Compartido.Eventos;

namespace SistemaGestion.Turnos;

/// <summary>Agendas semanales y bloqueos (licencias, feriados). Sólo el rol administrador los define.</summary>
internal static class Agendas
{
    private const string Recurso = "agenda";

    private const string Columnas = """
        a.id, a.profesional_id, a.servicio_id, a.consultorio_id, a.dia_semana, a.hora_desde, a.hora_hasta,
        a.duracion_minutos, a.vigente_desde, a.vigente_hasta, a.capacidad, a.sobreturnos, a.gestionada_por,
        a.activa, a.version,
        coalesce((SELECT array_agg(ac.cobertura_id) FROM turnos.agendas_coberturas ac WHERE ac.agenda_id = a.id), '{}') AS coberturas
        """;

    public static async Task<IResult> CrearAsync(CrearAgendaSolicitud x, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!ctx.TieneAlgunRol(Roles.Administrador))
            return Respuestas.SinPermiso("Se requiere el rol administrador");
        var errores = Validar(x);
        if (errores.Count > 0)
            return Results.ValidationProblem(errores);

        var id = Guid.CreateVersion7();
        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        try
        {
            await s.Conexion.ExecuteAsync(new CommandDefinition("""
                INSERT INTO turnos.agendas
                    (id, empresa_id, profesional_id, servicio_id, consultorio_id, dia_semana, hora_desde, hora_hasta,
                     duracion_minutos, vigente_desde, vigente_hasta, capacidad, sobreturnos, gestionada_por, geclisa_id, creado_por)
                VALUES
                    (@id, @empresa, @ProfesionalId, @ServicioId, @ConsultorioId, @DiaSemana, @desde::time, @hasta::time,
                     @DuracionMinutos, @vigDesde::date, @vigHasta::date, @capacidad, @sobreturnos, @gestion, @GeclisaId, @usuario)
                """, new
            {
                id,
                empresa = ctx.EmpresaId,
                usuario = ctx.UsuarioId,
                x.ProfesionalId,
                x.ServicioId,
                x.ConsultorioId,
                x.DiaSemana,
                desde = x.HoraDesde.ToString("HH:mm"),
                hasta = x.HoraHasta.ToString("HH:mm"),
                x.DuracionMinutos,
                vigDesde = x.VigenteDesde.ToString("yyyy-MM-dd"),
                vigHasta = x.VigenteHasta?.ToString("yyyy-MM-dd"),
                capacidad = x.Capacidad ?? 1,
                sobreturnos = x.Sobreturnos ?? 0,
                gestion = x.GestionadaPor ?? GestionAgenda.SistemaNuevo,
                x.GeclisaId,
            }, s.Transaccion, cancellationToken: ct));

            foreach (var cob in (x.CoberturaIds ?? []).Distinct())
            {
                await s.Conexion.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO turnos.agendas_coberturas (empresa_id, agenda_id, cobertura_id) VALUES (@empresa, @id, @cob)",
                    new { empresa = ctx.EmpresaId, id, cob }, s.Transaccion, cancellationToken: ct));
            }
        }
        catch (PostgresException ex) when (ex.ConstraintName == "ex_agendas_superpuestas")
        {
            return Respuestas.Conflicto("El profesional ya tiene una agenda activa que se superpone ese día y horario");
        }
        catch (PostgresException ex) when (Respuestas.EsReferenciaInvalida(ex))
        {
            return Respuestas.NoProcesable("El profesional, servicio, consultorio o cobertura no existe en esta empresa");
        }
        catch (PostgresException ex) when (Respuestas.EsDuplicado(ex))
        {
            return Respuestas.Conflicto("Ya existe una agenda con ese código de Geclisa");
        }

        var agenda = (await ObtenerAsync(s, id, ct))!;
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Crear, Recurso, id, agenda, ct);
        await s.ConfirmarAsync(ct);
        return Respuestas.ConVersion(Results.Created($"/api/v1/agendas/{id}", agenda), agenda.Version);
    }

    public static async Task<IResult> ListarAsync(Guid? profesionalId, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var filas = await s.Conexion.QueryAsync<FilaAgenda>(new CommandDefinition($"""
            SELECT {Columnas} FROM turnos.agendas a
            WHERE @profesionalId::uuid IS NULL OR a.profesional_id = @profesionalId
            ORDER BY a.profesional_id, a.dia_semana, a.hora_desde
            """, new { profesionalId }, s.Transaccion, cancellationToken: ct));
        return Results.Ok(filas.Select(f => f.ADto()));
    }

    /// <summary>
    /// Pasa una agenda de Geclisa al sistema nuevo (o la devuelve). Es el interruptor para migrar agenda por agenda:
    /// en cada momento sólo uno de los dos sistemas da turnos en ella.
    /// </summary>
    public static async Task<IResult> CambiarGestionAsync(
        Guid id, CambiarGestionSolicitud x, HttpRequest req, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!ctx.TieneAlgunRol(Roles.Administrador))
            return Respuestas.SinPermiso("Se requiere el rol administrador");
        if (!Respuestas.LeerVersion(req, out var version))
            return Respuestas.FaltaVersion(Recurso);
        if (!GestionAgenda.Todos.Contains(x.GestionadaPor ?? ""))
            return Respuestas.Invalido("gestionadaPor", $"Valores admitidos: {string.Join(", ", GestionAgenda.Todos)}.");

        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var anterior = await ObtenerAsync(s, id, ct);
        if (anterior is null)
            return Respuestas.NoEncontrado("Agenda");
        var filas = await s.Conexion.ExecuteAsync(new CommandDefinition("""
            UPDATE turnos.agendas
            SET gestionada_por = @gestion, version = version + 1, modificado_en = now(), modificado_por = @usuario
            WHERE id = @id AND version = @version
            """, new { id, version, gestion = x.GestionadaPor, usuario = ctx.UsuarioId }, s.Transaccion, cancellationToken: ct));
        if (filas == 0)
            return Respuestas.VersionVieja(Recurso);

        var actual = (await ObtenerAsync(s, id, ct))!;
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Modificar, Recurso, id, new { anterior, actual }, ct);
        await BandejaSalida.AgregarAsync(s, EventosTurnos.AgendaGestionCambiada, 1, Recurso, id, actual, ct);
        await s.ConfirmarAsync(ct);
        return Respuestas.ConVersion(Results.Ok(actual), actual.Version);
    }

    public static async Task<IResult> CrearBloqueoAsync(
        CrearBloqueoSolicitud x, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!ctx.TieneAlgunRol(Roles.Administrador))
            return Respuestas.SinPermiso("Se requiere el rol administrador");
        if (x.Hasta <= x.Desde)
            return Respuestas.Invalido("hasta", "El fin del bloqueo debe ser posterior al inicio.");
        if (string.IsNullOrWhiteSpace(x.Motivo))
            return Respuestas.Invalido("motivo", "El motivo es obligatorio.");

        var id = Guid.CreateVersion7();
        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        try
        {
            await s.Conexion.ExecuteAsync(new CommandDefinition("""
                INSERT INTO turnos.bloqueos (id, empresa_id, profesional_id, periodo, motivo, creado_por)
                VALUES (@id, @empresa, @ProfesionalId, tstzrange(@desde, @hasta, '[)'), @motivo, @usuario)
                """, new
            {
                id, empresa = ctx.EmpresaId, x.ProfesionalId, desde = x.Desde.UtcDateTime, hasta = x.Hasta.UtcDateTime,
                motivo = x.Motivo.Trim(), usuario = ctx.UsuarioId,
            }, s.Transaccion, cancellationToken: ct));
        }
        catch (PostgresException ex) when (Respuestas.EsReferenciaInvalida(ex))
        {
            return Respuestas.NoProcesable("El profesional no existe en esta empresa");
        }

        // El bloqueo no cancela turnos ya dados: los informa para que admisión los reprograme.
        var afectados = (await s.Conexion.QueryAsync<Guid>(new CommandDefinition("""
            SELECT id FROM turnos.turnos
            WHERE estado <> 'CANCELADO' AND horario && tstzrange(@desde, @hasta, '[)')
              AND (@profesional::uuid IS NULL OR profesional_id = @profesional)
            ORDER BY lower(horario)
            """, new { desde = x.Desde.UtcDateTime, hasta = x.Hasta.UtcDateTime, profesional = x.ProfesionalId },
            s.Transaccion, cancellationToken: ct))).ToList();

        var dto = new BloqueoDto(id, x.ProfesionalId, x.Desde, x.Hasta, x.Motivo.Trim(), afectados);
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Crear, "bloqueo", id, dto, ct);
        await s.ConfirmarAsync(ct);
        return Results.Created($"/api/v1/bloqueos/{id}", dto);
    }

    public static async Task<AgendaDto?> ObtenerAsync(SesionBd s, Guid id, CancellationToken ct)
    {
        var f = await s.Conexion.QuerySingleOrDefaultAsync<FilaAgenda>(new CommandDefinition(
            $"SELECT {Columnas} FROM turnos.agendas a WHERE a.id = @id", new { id }, s.Transaccion, cancellationToken: ct));
        return f?.ADto();
    }

    private static Dictionary<string, string[]> Validar(CrearAgendaSolicitud x)
    {
        var e = new Dictionary<string, string[]>();
        if (x.DiaSemana is < 1 or > 7)
            e["diaSemana"] = ["El día de la semana va de 1 (lunes) a 7 (domingo)."];
        if (x.HoraHasta <= x.HoraDesde)
            e["horaHasta"] = ["La hora de fin debe ser posterior a la de inicio."];
        if (x.DuracionMinutos is < 5 or > 240)
            e["duracionMinutos"] = ["La duración del turno debe estar entre 5 y 240 minutos."];
        else if ((x.HoraHasta - x.HoraDesde).TotalMinutes < x.DuracionMinutos)
            e["duracionMinutos"] = ["La franja horaria es más corta que un turno."];
        if (x.VigenteHasta is { } vh && vh < x.VigenteDesde)
            e["vigenteHasta"] = ["La vigencia termina antes de empezar."];
        if (x.Capacidad is < 1 or > 20)
            e["capacidad"] = ["La capacidad va de 1 a 20 pacientes por horario."];
        if (x.Sobreturnos is < 0 or > 10)
            e["sobreturnos"] = ["Los sobreturnos van de 0 a 10 por horario."];
        if (x.GestionadaPor is not null && !GestionAgenda.Todos.Contains(x.GestionadaPor))
            e["gestionadaPor"] = [$"Valores admitidos: {string.Join(", ", GestionAgenda.Todos)}."];
        return e;
    }

    /// <summary>Fila de turnos.agendas. Clase con propiedades (no record posicional) porque Dapper no mapea uuid[] por constructor.</summary>
    internal sealed class FilaAgenda
    {
        public Guid id { get; init; }
        public Guid profesional_id { get; init; }
        public Guid servicio_id { get; init; }
        public Guid consultorio_id { get; init; }
        public short dia_semana { get; init; }
        public TimeOnly hora_desde { get; init; }
        public TimeOnly hora_hasta { get; init; }
        public short duracion_minutos { get; init; }
        public DateOnly vigente_desde { get; init; }
        public DateOnly? vigente_hasta { get; init; }
        public short capacidad { get; init; }
        public short sobreturnos { get; init; }
        public string gestionada_por { get; init; } = "";
        public bool activa { get; init; }
        public int version { get; init; }
        public Guid[] coberturas { get; init; } = [];

        public AgendaDto ADto() => new(id, profesional_id, servicio_id, consultorio_id, dia_semana, hora_desde, hora_hasta,
            duracion_minutos, vigente_desde, vigente_hasta, capacidad, sobreturnos, gestionada_por, activa, version, coberturas);

        public ReglaAgenda Regla() => new(dia_semana, hora_desde, hora_hasta, duracion_minutos, vigente_desde, vigente_hasta);
    }
}
