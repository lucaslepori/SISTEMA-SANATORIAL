using Dapper;
using Microsoft.AspNetCore.Http;
using Npgsql;
using SistemaGestion.Compartido;
using SistemaGestion.Compartido.Eventos;

namespace SistemaGestion.Turnos;

internal static class Turnos
{
    private const string Recurso = "turno";
    private const int MaxDiasDisponibilidad = 31;
    private const int MaxReintentos = 5;
    private static readonly HashSet<string> Origenes = ["PRESENCIAL", "TELEFONO", "WEB", "API"];

    private const string Columnas = """
        t.id, t.agenda_id, t.profesional_id, t.servicio_id, t.consultorio_id, t.paciente_id,
        lower(t.horario) AS inicio, upper(t.horario) AS fin, t.es_sobreturno, t.estado, t.cobertura_id, t.plan_id,
        t.numero_afiliado, t.observaciones, t.origen, t.motivo_cancelacion, t.version
        """;

    // ------------------------------------------------------------------
    // Disponibilidad
    // ------------------------------------------------------------------

    public static async Task<IResult> DisponibilidadAsync(
        Guid? profesionalId, Guid? servicioId, Guid? coberturaId, DateOnly desde, DateOnly hasta,
        ContextoSolicitud ctx, OrigenesBd bd, TimeProvider reloj, CancellationToken ct)
    {
        if (profesionalId is null && servicioId is null)
            return Respuestas.Invalido("criterio", "Indicá un profesional o un servicio.");
        if (hasta < desde || hasta.DayNumber - desde.DayNumber >= MaxDiasDisponibilidad)
            return Respuestas.Invalido("hasta", $"El rango debe ser de 1 a {MaxDiasDisponibilidad} días.");

        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var zona = await ZonaAsync(s, ct);
        var agendas = (await s.Conexion.QueryAsync<Agendas.FilaAgenda>(new CommandDefinition("""
            SELECT a.id, a.profesional_id, a.servicio_id, a.consultorio_id, a.dia_semana, a.hora_desde, a.hora_hasta,
                   a.duracion_minutos, a.vigente_desde, a.vigente_hasta, a.capacidad, a.sobreturnos, a.gestionada_por,
                   a.activa, a.version, '{}'::uuid[] AS coberturas
            FROM turnos.agendas a
            WHERE a.activa AND a.gestionada_por = 'SISTEMA_NUEVO'
              AND (@profesionalId::uuid IS NULL OR a.profesional_id = @profesionalId)
              AND (@servicioId::uuid IS NULL OR a.servicio_id = @servicioId)
              AND a.vigencia && daterange(@desde::date, @hasta::date, '[]')
              AND (@coberturaId::uuid IS NULL
                   OR NOT EXISTS (SELECT 1 FROM turnos.agendas_coberturas ac WHERE ac.agenda_id = a.id)
                   OR EXISTS (SELECT 1 FROM turnos.agendas_coberturas ac WHERE ac.agenda_id = a.id AND ac.cobertura_id = @coberturaId))
            """, new { profesionalId, servicioId, coberturaId, desde = Fecha(desde), hasta = Fecha(hasta) },
            s.Transaccion, cancellationToken: ct))).ToList();
        if (agendas.Count == 0)
            return Results.Ok(Array.Empty<HorarioDisponibleDto>());

        // Rango amplio en UTC: cubre cualquier desplazamiento de la zona.
        var rangoDesde = desde.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(-1);
        var rangoHasta = hasta.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(2);
        var profesionales = agendas.Select(a => a.profesional_id).Distinct().ToArray();

        var ocupados = (await s.Conexion.QueryAsync<(Guid Profesional, DateTime Inicio, DateTime Fin, bool Sobre)>(
            new CommandDefinition("""
                SELECT profesional_id, lower(horario), upper(horario), es_sobreturno
                FROM turnos.turnos
                WHERE estado <> 'CANCELADO' AND profesional_id = ANY(@profesionales)
                  AND horario && tstzrange(@rangoDesde, @rangoHasta)
                """, new { profesionales, rangoDesde, rangoHasta }, s.Transaccion, cancellationToken: ct))).ToList();
        var bloqueos = (await s.Conexion.QueryAsync<(Guid? Profesional, DateTime Desde, DateTime Hasta)>(
            new CommandDefinition("""
                SELECT profesional_id, lower(periodo), upper(periodo)
                FROM turnos.bloqueos
                WHERE (profesional_id IS NULL OR profesional_id = ANY(@profesionales))
                  AND periodo && tstzrange(@rangoDesde, @rangoHasta)
                """, new { profesionales, rangoDesde, rangoHasta }, s.Transaccion, cancellationToken: ct))).ToList();

        var ahora = reloj.GetUtcNow();
        var resultado = new List<HorarioDisponibleDto>();
        foreach (var a in agendas)
        {
            foreach (var (inicio, fin) in Grilla.Horarios(a.Regla(), zona, desde, hasta))
            {
                if (inicio <= ahora)
                    continue;
                bool Pisa(DateTime d, DateTime h) => d < fin.UtcDateTime && h > inicio.UtcDateTime;
                if (bloqueos.Any(b => (b.Profesional is null || b.Profesional == a.profesional_id) && Pisa(b.Desde, b.Hasta)))
                    continue;
                var delHorario = ocupados.Where(o => o.Profesional == a.profesional_id && Pisa(o.Inicio, o.Fin)).ToList();
                var libres = Math.Max(0, a.capacidad - delHorario.Count(o => !o.Sobre));
                var sobreLibres = Math.Max(0, a.sobreturnos - delHorario.Count(o => o.Sobre));
                if (libres + sobreLibres == 0)
                    continue;
                resultado.Add(new HorarioDisponibleDto(a.id, a.profesional_id, a.servicio_id, a.consultorio_id,
                    inicio, fin, libres, sobreLibres));
            }
        }
        await s.ConfirmarAsync(ct);
        return Results.Ok(resultado.OrderBy(h => h.Inicio).ThenBy(h => h.ProfesionalId));
    }

    // ------------------------------------------------------------------
    // Asignación
    // ------------------------------------------------------------------

    public static async Task<IResult> AsignarAsync(
        AsignarTurnoSolicitud x, ContextoSolicitud ctx, OrigenesBd bd, TimeProvider reloj, CancellationToken ct)
    {
        if (!ctx.TieneAlgunRol(Roles.Admision, Roles.Administrador))
            return Respuestas.SinPermiso("Se requiere el rol admision o administrador");
        var origen = x.Origen ?? "PRESENCIAL";
        if (!Origenes.Contains(origen))
            return Respuestas.Invalido("origen", $"Valores admitidos: {string.Join(", ", Origenes)}.");
        if (x.PlanId is not null && x.CoberturaId is null)
            return Respuestas.Invalido("planId", "Un plan requiere indicar su cobertura.");
        if (x.Inicio <= reloj.GetUtcNow())
            return Respuestas.NoProcesable("No se pueden dar turnos en el pasado");

        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        // FOR SHARE: la agenda no puede cambiar (por ejemplo pasar a Geclisa) mientras se da el turno.
        var agenda = await s.Conexion.QuerySingleOrDefaultAsync<Agendas.FilaAgenda>(new CommandDefinition("""
            SELECT a.id, a.profesional_id, a.servicio_id, a.consultorio_id, a.dia_semana, a.hora_desde, a.hora_hasta,
                   a.duracion_minutos, a.vigente_desde, a.vigente_hasta, a.capacidad, a.sobreturnos, a.gestionada_por,
                   a.activa, a.version,
                   coalesce((SELECT array_agg(ac.cobertura_id) FROM turnos.agendas_coberturas ac WHERE ac.agenda_id = a.id), '{}') AS coberturas
            FROM turnos.agendas a WHERE a.id = @id FOR SHARE
            """, new { id = x.AgendaId }, s.Transaccion, cancellationToken: ct));
        if (agenda is null || !agenda.activa)
            return Respuestas.NoProcesable("La agenda no existe o no está activa");
        if (agenda.gestionada_por != GestionAgenda.SistemaNuevo)
            return Respuestas.Conflicto("Esta agenda todavía la gestiona Geclisa: el turno se da desde Geclisa");

        var zona = await ZonaAsync(s, ct);
        if (!Grilla.EsHorarioDeLaAgenda(agenda.Regla(), zona, x.Inicio))
            return Respuestas.NoProcesable("El horario no corresponde a un turno de esta agenda");
        if (agenda.coberturas.Length > 0 && (x.CoberturaId is null || !agenda.coberturas.Contains(x.CoberturaId.Value)))
            return Respuestas.NoProcesable("La agenda no atiende esa cobertura");

        var inicio = x.Inicio.UtcDateTime;
        var fin = inicio.AddMinutes(agenda.duracion_minutos);
        var bloqueado = await s.Conexion.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM turnos.bloqueos
                           WHERE (profesional_id IS NULL OR profesional_id = @profesional)
                             AND periodo && tstzrange(@inicio, @fin, '[)'))
            """, new { profesional = agenda.profesional_id, inicio, fin }, s.Transaccion, cancellationToken: ct));
        if (bloqueado)
            return Respuestas.Conflicto("El profesional no atiende en ese horario (licencia o feriado)");

        var sobreturno = x.Sobreturno == true;
        var (primerCupo, ultimoCupo) = sobreturno
            ? (agenda.capacidad + 1, agenda.capacidad + agenda.sobreturnos)
            : (1, (int)agenda.capacidad);
        if (ultimoCupo < primerCupo)
            return Respuestas.Conflicto("La agenda no admite sobreturnos");

        // Los turnos de un mismo profesional se dan de a uno (bloqueo consultivo hasta el fin de la transacción).
        // Sin esto, dos inserciones simultáneas en la restricción de exclusión pueden bloquearse mutuamente
        // y PostgreSQL tarda un segundo en detectarlo. La restricción sigue siendo la garantía final.
        await s.Conexion.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtextextended('turnos.profesional:' || @profesional::text, 0))",
            new { profesional = agenda.profesional_id }, s.Transaccion, cancellationToken: ct));

        var ocupados = (await s.Conexion.QueryAsync<short>(new CommandDefinition("""
            SELECT cupo FROM turnos.turnos
            WHERE profesional_id = @profesional AND estado <> 'CANCELADO' AND horario && tstzrange(@inicio, @fin, '[)')
            """, new { profesional = agenda.profesional_id, inicio, fin }, s.Transaccion, cancellationToken: ct))).ToHashSet();

        // La base es la que garantiza que no haya doble turno (restricción de exclusión). Si otra persona
        // tomó el mismo cupo entre la lectura de arriba y el INSERT, la base lo rechaza y se prueba el siguiente.
        var id = Guid.CreateVersion7();
        var asignado = false;
        var reintentos = 0;
        for (var cupo = primerCupo; cupo <= ultimoCupo && !asignado; cupo++)
        {
            if (ocupados.Contains((short)cupo))
                continue;
            await s.Transaccion.SaveAsync("cupo", ct);
            try
            {
                await s.Conexion.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO turnos.turnos
                        (id, empresa_id, agenda_id, profesional_id, servicio_id, consultorio_id, paciente_id, horario, cupo,
                         es_sobreturno, cobertura_id, plan_id, numero_afiliado, observaciones, origen, creado_por)
                    VALUES
                        (@id, @empresa, @agenda, @profesional, @servicio, @consultorio, @paciente, tstzrange(@inicio, @fin, '[)'),
                         @cupo, @sobreturno, @CoberturaId, @PlanId, @numero, @obs, @origen, @usuario)
                    """, new
                {
                    id, empresa = ctx.EmpresaId, agenda = agenda.id, profesional = agenda.profesional_id,
                    servicio = agenda.servicio_id, consultorio = agenda.consultorio_id, paciente = x.PacienteId,
                    inicio, fin, cupo = (short)cupo, sobreturno, x.CoberturaId, x.PlanId,
                    numero = Limpiar(x.NumeroAfiliado), obs = Limpiar(x.Observaciones), origen, usuario = ctx.UsuarioId,
                }, s.Transaccion, cancellationToken: ct));
                asignado = true;
            }
            catch (PostgresException ex) when (ex.ConstraintName == "ex_turnos_profesional_cupo")
            {
                await s.Transaccion.RollbackAsync("cupo", ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DeadlockDetected && reintentos < MaxReintentos)
            {
                // Puede pasar si el mismo paciente pide a la vez turnos con dos profesionales distintos:
                // PostgreSQL corta una de las dos transacciones. Se reintenta el mismo cupo.
                await s.Transaccion.RollbackAsync("cupo", ct);
                reintentos++;
                cupo--;
            }
            catch (PostgresException ex) when (ex.ConstraintName == "ex_turnos_paciente")
            {
                return Respuestas.Conflicto("El paciente ya tiene otro turno en ese horario");
            }
            catch (PostgresException ex) when (Respuestas.EsReferenciaInvalida(ex))
            {
                return Respuestas.NoProcesable("El paciente, la cobertura o el plan no existen en esta empresa (o el plan no es de esa cobertura)");
            }
        }
        if (!asignado)
            return Respuestas.Conflicto(sobreturno ? "No quedan sobreturnos en ese horario" : "El horario ya está ocupado");

        var turno = (await ObtenerAsync(s, id, ct))!;
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Crear, Recurso, id, null, ct);
        await BandejaSalida.AgregarAsync(s, EventosTurnos.Asignado, 1, EventosTurnos.Agregado, id, turno, ct);
        await s.ConfirmarAsync(ct);
        return Respuestas.ConVersion(Results.Created($"/api/v1/turnos/{id}", turno), turno.Version);
    }

    // ------------------------------------------------------------------
    // Consulta
    // ------------------------------------------------------------------

    public static async Task<IResult> ObtenerAsync(Guid id, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!PuedeVer(ctx))
            return Respuestas.SinPermiso("Sin permiso para ver turnos");
        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var turno = await ObtenerAsync(s, id, ct);
        if (turno is null)
            return Respuestas.NoEncontrado("Turno");
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Leer, Recurso, id, null, ct);
        await s.ConfirmarAsync(ct);
        return Respuestas.ConVersion(Results.Ok(turno), turno.Version);
    }

    /// <summary>Agenda del día de un profesional, o turnos de un paciente desde una fecha.</summary>
    public static async Task<IResult> ListarAsync(
        Guid? profesionalId, DateOnly? fecha, Guid? pacienteId, DateOnly? desde,
        ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!PuedeVer(ctx))
            return Respuestas.SinPermiso("Sin permiso para ver turnos");
        if ((profesionalId is null || fecha is null) && pacienteId is null)
            return Respuestas.Invalido("criterio", "Indicá profesionalId y fecha, o pacienteId.");

        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var zona = await ZonaAsync(s, ct);
        List<TurnoDto> turnos;
        if (pacienteId is not null)
        {
            DateTime? d = desde is { } x ? InicioDelDia(x, zona) : null;
            turnos = await ConsultarAsync(s, "t.paciente_id = @pacienteId AND (@d::timestamptz IS NULL OR lower(t.horario) >= @d)",
                new { pacienteId, d }, ct);
        }
        else
        {
            var dia = fecha!.Value;
            turnos = await ConsultarAsync(s, "t.profesional_id = @profesionalId AND t.horario && tstzrange(@d0, @d1, '[)')",
                new { profesionalId, d0 = InicioDelDia(dia, zona), d1 = InicioDelDia(dia.AddDays(1), zona) }, ct);
        }
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Buscar, Recurso, null,
            new { profesionalId, fecha, pacienteId, desde, resultados = turnos.Select(t => t.Id) }, ct);
        await s.ConfirmarAsync(ct);
        return Results.Ok(turnos);
    }

    // ------------------------------------------------------------------
    // Cambios de estado
    // ------------------------------------------------------------------

    public static async Task<IResult> CancelarAsync(
        Guid id, CancelarTurnoSolicitud x, HttpRequest req, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!ctx.TieneAlgunRol(Roles.Admision, Roles.Administrador))
            return Respuestas.SinPermiso("Se requiere el rol admision o administrador");
        if (!Respuestas.LeerVersion(req, out var version))
            return Respuestas.FaltaVersion(Recurso);
        if (string.IsNullOrWhiteSpace(x.Motivo))
            return Respuestas.Invalido("motivo", "El motivo de cancelación es obligatorio.");

        return await CambiarAsync(id, version, ctx, bd, EventosTurnos.Cancelado, ct, (s, anterior) =>
        {
            if (anterior.Estado is not (EstadosTurno.Reservado or EstadosTurno.Confirmado))
                return Task.FromResult<IResult?>(Respuestas.Conflicto($"No se puede cancelar un turno {anterior.Estado.ToLowerInvariant()}"));
            return Actualizar(s, id, version, """
                estado = 'CANCELADO', motivo_cancelacion = @motivo, cancelado_en = now(), cancelado_por = @usuario
                """, new { motivo = x.Motivo.Trim() }, ct);
        });
    }

    public static async Task<IResult> CambiarEstadoAsync(
        Guid id, CambiarEstadoSolicitud x, HttpRequest req, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!ctx.TieneAlgunRol(Roles.Admision, Roles.Profesional, Roles.Administrador))
            return Respuestas.SinPermiso("Sin permiso para cambiar el estado del turno");
        if (!Respuestas.LeerVersion(req, out var version))
            return Respuestas.FaltaVersion(Recurso);
        if (x.Estado == EstadosTurno.Cancelado)
            return Respuestas.Invalido("estado", "Para cancelar usá /cancelacion, que exige el motivo.");
        if (!EstadosTurno.Transiciones.ContainsKey(x.Estado ?? ""))
            return Respuestas.Invalido("estado", "Estado inválido.");
        // Admisión registra la llegada y la ausencia; el profesional marca la atención.
        if (x.Estado == EstadosTurno.Atendido && !ctx.TieneAlgunRol(Roles.Profesional, Roles.Administrador))
            return Respuestas.SinPermiso("Sólo el profesional marca un turno como atendido");

        return await CambiarAsync(id, version, ctx, bd, EventosTurnos.EstadoCambiado, ct, (s, anterior) =>
        {
            if (!EstadosTurno.Transiciones[anterior.Estado].Contains(x.Estado))
                return Task.FromResult<IResult?>(Respuestas.Conflicto($"Un turno {anterior.Estado.ToLowerInvariant()} no puede pasar a {x.Estado!.ToLowerInvariant()}"));
            return Actualizar(s, id, version, "estado = @estado", new { estado = x.Estado }, ct);
        });
    }

    /// <summary>Esqueleto común: lee, aplica el cambio con control de versión, audita y publica el evento.</summary>
    private static async Task<IResult> CambiarAsync(
        Guid id, int version, ContextoSolicitud ctx, OrigenesBd bd, string evento, CancellationToken ct,
        Func<SesionBd, TurnoDto, Task<IResult?>> aplicar)
    {
        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var anterior = await ObtenerAsync(s, id, ct);
        if (anterior is null)
            return Respuestas.NoEncontrado("Turno");
        if (anterior.Version != version)
            return Respuestas.VersionVieja(Recurso);
        var error = await aplicar(s, anterior);
        if (error is not null)
            return error;

        var actual = (await ObtenerAsync(s, id, ct))!;
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Modificar, Recurso, id,
            new { estadoAnterior = anterior.Estado, estadoNuevo = actual.Estado, actual.MotivoCancelacion }, ct);
        await BandejaSalida.AgregarAsync(s, evento, 1, EventosTurnos.Agregado, id, actual, ct);
        await s.ConfirmarAsync(ct);
        return Respuestas.ConVersion(Results.Ok(actual), actual.Version);
    }

    private static async Task<IResult?> Actualizar(SesionBd s, Guid id, int version, string set, object datos, CancellationToken ct)
    {
        var p = new DynamicParameters(datos);
        p.Add("id", id);
        p.Add("version", version);
        p.Add("usuario", s.Contexto.UsuarioId);
        var filas = await s.Conexion.ExecuteAsync(new CommandDefinition($"""
            UPDATE turnos.turnos
            SET {set}, version = version + 1, modificado_en = now(), modificado_por = @usuario
            WHERE id = @id AND version = @version
            """, p, s.Transaccion, cancellationToken: ct));
        return filas == 0 ? Respuestas.VersionVieja(Recurso) : null;
    }

    // ------------------------------------------------------------------
    // Auxiliares
    // ------------------------------------------------------------------

    private static async Task<TurnoDto?> ObtenerAsync(SesionBd s, Guid id, CancellationToken ct) =>
        (await ConsultarAsync(s, "t.id = @id", new { id }, ct)).SingleOrDefault();

    private static async Task<List<TurnoDto>> ConsultarAsync(SesionBd s, string filtro, object parametros, CancellationToken ct)
    {
        var filas = await s.Conexion.QueryAsync<FilaTurno>(new CommandDefinition(
            $"SELECT {Columnas} FROM turnos.turnos t WHERE {filtro} ORDER BY lower(t.horario), t.cupo",
            parametros, s.Transaccion, cancellationToken: ct));
        return filas.Select(f => f.ADto()).ToList();
    }

    private static async Task<TimeZoneInfo> ZonaAsync(SesionBd s, CancellationToken ct)
    {
        var id = await s.Conexion.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT zona_horaria FROM nucleo.empresas WHERE id = @empresa", new { empresa = s.Contexto.EmpresaId },
            s.Transaccion, cancellationToken: ct));
        return TimeZoneInfo.FindSystemTimeZoneById(id!);
    }

    private static DateTime InicioDelDia(DateOnly dia, TimeZoneInfo zona)
    {
        var local = dia.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zona.GetUtcOffset(local)).UtcDateTime;
    }

    private static bool PuedeVer(ContextoSolicitud ctx) =>
        ctx.TieneAlgunRol(Roles.Admision, Roles.Profesional, Roles.Administrador);

    private static string Fecha(DateOnly d) => d.ToString("yyyy-MM-dd");

    private static string? Limpiar(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private sealed record FilaTurno(
        Guid id, Guid agenda_id, Guid profesional_id, Guid servicio_id, Guid consultorio_id, Guid paciente_id,
        DateTime inicio, DateTime fin, bool es_sobreturno, string estado, Guid? cobertura_id, Guid? plan_id,
        string? numero_afiliado, string? observaciones, string origen, string? motivo_cancelacion, int version)
    {
        public TurnoDto ADto() => new(id, agenda_id, profesional_id, servicio_id, consultorio_id, paciente_id,
            new DateTimeOffset(inicio, TimeSpan.Zero), new DateTimeOffset(fin, TimeSpan.Zero), es_sobreturno, estado,
            cobertura_id, plan_id, numero_afiliado, observaciones, origen, motivo_cancelacion, version);
    }
}
