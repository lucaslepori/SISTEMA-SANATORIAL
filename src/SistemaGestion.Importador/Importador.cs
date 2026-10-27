using Dapper;
using Npgsql;

namespace SistemaGestion.Importador;

public enum AlcancePacientes { ConTurnosFuturos, Todos }

public sealed record OpcionesImportacion(
    Guid EmpresaId,
    AlcancePacientes Pacientes = AlcancePacientes.ConTurnosFuturos,
    bool DatosReales = false,
    bool Simular = false);

/// <summary>
/// Carga en la base nueva lo que se lee de Geclisa. Se puede correr las veces que haga falta:
/// cada fila se busca por su id de Geclisa (geclisa_id) y se actualiza en lugar de duplicarse.
///
/// Todo corre en una transacción: si algo inesperado falla no queda nada a medias, y con
/// Simular se descarta al final. Las escrituras se hacen con el rol de la API (sg_api) y la
/// empresa fijada, así que valen los mismos permisos y la misma seguridad por fila.
/// </summary>
public sealed class ImportadorGeclisa(string cadenaDestino, OpcionesImportacion opciones)
{
    public const string SujetoImportador = "importador.geclisa";
    const string SedePorDefecto = "Sin sede (Geclisa)";
    const string ConsultorioPorDefecto = "Sin consultorio (Geclisa)";

    NpgsqlConnection cn = null!;
    NpgsqlTransaction tx = null!;
    Guid empresa;
    Guid usuario;
    string zona = "";
    readonly Informe informe = new();

    readonly Dictionary<short, Guid> sedes = [];
    readonly Dictionary<short, Guid> consultorios = [];
    readonly Dictionary<short, Guid> servicios = [];
    readonly Dictionary<short, Guid> especialidades = [];
    readonly Dictionary<int, Guid> profesionales = [];
    readonly Dictionary<short, Guid> coberturas = [];
    readonly Dictionary<short, (Guid Id, short OsId)> planes = [];
    readonly Dictionary<int, AgendaImportada> agendas = [];
    readonly Dictionary<int, Guid> pacientes = [];
    Guid? sedeDefecto;
    Guid? consultorioDefecto;

    sealed record AgendaImportada(Guid Id, Guid ConsultorioId, int Capacidad, int Sobreturnos, bool DeGeclisa);

    public async Task<Informe> EjecutarAsync(IFuenteGeclisa fuente, DateOnly hoy, CancellationToken ct = default)
    {
        var datos = await fuente.LeerAsync(hoy, opciones.Pacientes == AlcancePacientes.Todos, ct);

        await using var conexion = new NpgsqlConnection(cadenaDestino);
        await conexion.OpenAsync(ct);
        cn = conexion;
        await using var transaccion = await cn.BeginTransactionAsync(ct);
        tx = transaccion;
        empresa = opciones.EmpresaId;

        zona = await cn.QuerySingleOrDefaultAsync<string>(
            "SELECT zona_horaria FROM nucleo.empresas WHERE id = @empresa", new { empresa }, tx)
            ?? throw new InvalidOperationException($"No existe la empresa {empresa}.");

        // Usuario técnico que figura como autor de lo importado. Inactivo: no puede entrar a la API.
        usuario = await cn.ExecuteScalarAsync<Guid>("""
            INSERT INTO nucleo.usuarios (id, sujeto_oidc, nombre_mostrar, activo)
            VALUES (@id, @sujeto, 'Importador de Geclisa', false)
            ON CONFLICT (sujeto_oidc) DO UPDATE SET nombre_mostrar = EXCLUDED.nombre_mostrar
            RETURNING id
            """, new { id = Guid.CreateVersion7(), sujeto = SujetoImportador }, tx);

        // De acá en adelante, los mismos permisos y la misma seguridad por fila que la API.
        await cn.ExecuteAsync("SET LOCAL ROLE sg_api", transaction: tx);
        await cn.ExecuteAsync(
            "SELECT set_config('app.empresa_id', @e, true), set_config('app.usuario_id', @u, true)",
            new { e = empresa.ToString(), u = usuario.ToString() }, tx);

        await SedesAsync(datos);
        await ConsultoriosAsync(datos);
        await ServiciosAsync(datos);
        await EspecialidadesAsync(datos);
        await ProfesionalesAsync(datos);
        await CoberturasAsync(datos, hoy);
        await AgendasAsync(datos, hoy);
        await FeriadosAsync(datos);
        await PacientesAsync(datos, hoy);
        await TurnosAsync(datos, hoy);

        await cn.ExecuteAsync("""
            INSERT INTO auditoria.eventos (empresa_id, usuario_id, accion, recurso_tipo, detalle)
            VALUES (@empresa, @usuario, 'CREAR', 'importacion_geclisa', @detalle::jsonb)
            """, new
        {
            empresa,
            usuario,
            detalle = System.Text.Json.JsonSerializer.Serialize(new
            {
                cargados = informe.Cargados,
                problemas = informe.ProblemasTotal,
                datosReales = opciones.DatosReales,
                pacientes = opciones.Pacientes.ToString(),
            }),
        }, tx);

        if (opciones.Simular)
            await tx.RollbackAsync(ct);
        else
            await tx.CommitAsync(ct);
        return informe;
    }

    // ---------------------------------------------------------------- catálogos

    async Task SedesAsync(DatosGeclisa d)
    {
        var nombres = Transformaciones.NombresUnicos(d.Ubicaciones, u => u.Id, u => u.Nombre, porDefecto: "Ubicación");
        foreach (var u in d.Ubicaciones)
        {
            // Las sedes no tienen geclisa_id: se reconocen por el nombre.
            sedes[u.Id] = await SedeAsync(nombres[u.Id], u.Direccion);
            informe.Contar("sedes");
        }
    }

    async Task<Guid> SedeAsync(string nombre, string? direccion) =>
        await cn.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM catalogos.sedes WHERE empresa_id = @empresa AND nombre = @nombre", new { empresa, nombre }, tx)
        ?? await cn.ExecuteScalarAsync<Guid>("""
            INSERT INTO catalogos.sedes (id, empresa_id, nombre, direccion) VALUES (@id, @empresa, @nombre, @direccion)
            RETURNING id
            """, new { id = Guid.CreateVersion7(), empresa, nombre, direccion = Vacio(direccion) }, tx);

    async Task ConsultoriosAsync(DatosGeclisa d)
    {
        var nombres = Transformaciones.NombresUnicos(
            d.Consultorios, c => c.Id, c => c.Nombre, c => SedeDe(c.UbicId), "Consultorio");
        foreach (var c in d.Consultorios)
        {
            var sede = sedes.TryGetValue(c.UbicId ?? -1, out var s) ? s : await SedeDefectoAsync();
            var id = await GuardarAsync("consultorio", c.Id, """
                INSERT INTO catalogos.consultorios (id, empresa_id, sede_id, nombre, geclisa_id)
                VALUES (@id, @empresa, @sede, @nombre, @geclisa)
                ON CONFLICT (empresa_id, geclisa_id) DO UPDATE SET sede_id = EXCLUDED.sede_id, nombre = EXCLUDED.nombre
                RETURNING id
                """, new { id = Guid.CreateVersion7(), empresa, sede, nombre = nombres[c.Id], geclisa = (int)c.Id });
            if (id is { } g)
            {
                consultorios[c.Id] = g;
                informe.Contar("consultorios");
            }
        }
    }

    object SedeDe(short? ubicId) => ubicId is { } u && sedes.ContainsKey(u) ? u : "defecto";

    async Task<Guid> SedeDefectoAsync() => sedeDefecto ??= await SedeAsync(SedePorDefecto, null);

    async Task<Guid> ConsultorioDefectoAsync()
    {
        if (consultorioDefecto is { } c) return c;
        var sede = await SedeDefectoAsync();
        consultorioDefecto = await cn.ExecuteScalarAsync<Guid?>("""
            SELECT id FROM catalogos.consultorios WHERE empresa_id = @empresa AND sede_id = @sede AND nombre = @nombre
            """, new { empresa, sede, nombre = ConsultorioPorDefecto }, tx)
            ?? await cn.ExecuteScalarAsync<Guid>("""
                INSERT INTO catalogos.consultorios (id, empresa_id, sede_id, nombre) VALUES (@id, @empresa, @sede, @nombre)
                RETURNING id
                """, new { id = Guid.CreateVersion7(), empresa, sede, nombre = ConsultorioPorDefecto }, tx);
        return consultorioDefecto.Value;
    }

    async Task ServiciosAsync(DatosGeclisa d)
    {
        var nombres = Transformaciones.NombresUnicos(d.Servicios, s => s.Id, s => s.Nombre, porDefecto: "Servicio");
        foreach (var s in d.Servicios)
        {
            var nombre = nombres[s.Id];
            await VincularPorNombreAsync("servicios", s.Id, nombre);
            var id = await GuardarAsync("servicio", s.Id, """
                INSERT INTO catalogos.servicios (id, empresa_id, nombre, activo, geclisa_id)
                VALUES (@id, @empresa, @nombre, @activo, @geclisa)
                ON CONFLICT (empresa_id, geclisa_id) DO UPDATE SET nombre = EXCLUDED.nombre, activo = EXCLUDED.activo
                RETURNING id
                """, new { id = Guid.CreateVersion7(), empresa, nombre, activo = s.Inactivo != true, geclisa = (int)s.Id });
            if (id is { } g)
            {
                servicios[s.Id] = g;
                informe.Contar("servicios");
            }
        }
    }

    async Task EspecialidadesAsync(DatosGeclisa d)
    {
        var nombres = Transformaciones.NombresUnicos(d.Especialidades, e => e.Id, e => e.Nombre, porDefecto: "Especialidad");
        foreach (var e in d.Especialidades)
        {
            var nombre = nombres[e.Id];
            await VincularPorNombreAsync("especialidades", e.Id, nombre);
            var id = await GuardarAsync("especialidad", e.Id, """
                INSERT INTO catalogos.especialidades (id, empresa_id, nombre, geclisa_id)
                VALUES (@id, @empresa, @nombre, @geclisa)
                ON CONFLICT (empresa_id, geclisa_id) DO UPDATE SET nombre = EXCLUDED.nombre
                RETURNING id
                """, new { id = Guid.CreateVersion7(), empresa, nombre, geclisa = (int)e.Id });
            if (id is { } g)
            {
                especialidades[e.Id] = g;
                informe.Contar("especialidades");
            }
        }
    }

    async Task ProfesionalesAsync(DatosGeclisa d)
    {
        foreach (var p in d.Prestadores)
        {
            var (nacional, provincial) = Transformaciones.Matriculas(p.MatriculaNacional, p.MatriculaProvincial);
            if (nacional is null && provincial is null)
            {
                informe.Problema("profesional sin matrícula (no se cargó)", p.Id);
                continue;
            }
            var (apellidos, nombres, supuesto) = Transformaciones.SepararNombre(p.Nombre);
            if (apellidos.Length == 0)
            {
                informe.Problema("profesional sin nombre (no se cargó)", p.Id);
                continue;
            }
            if (supuesto)
                informe.Problema("profesional: apellido supuesto (primera palabra de pre_nombre), revisar", p.Id);
            var id = await GuardarAsync("profesional", p.Id, """
                INSERT INTO catalogos.profesionales (id, empresa_id, apellidos, nombres, matricula_nacional, matricula_provincial, geclisa_id)
                VALUES (@id, @empresa, @apellidos, @nombres, @nacional, @provincial, @geclisa)
                ON CONFLICT (empresa_id, geclisa_id) DO UPDATE SET
                    apellidos = EXCLUDED.apellidos, nombres = EXCLUDED.nombres,
                    matricula_nacional = EXCLUDED.matricula_nacional, matricula_provincial = EXCLUDED.matricula_provincial
                RETURNING id
                """, new { id = Guid.CreateVersion7(), empresa, apellidos, nombres, nacional, provincial, geclisa = p.Id });
            if (id is { } g)
            {
                profesionales[p.Id] = g;
                informe.Contar("profesionales");
            }
        }

        foreach (var pe in d.PrestadoresEspecialidades)
        {
            if (!profesionales.TryGetValue(pe.PreId, out var prof) || !especialidades.TryGetValue(pe.EspId, out var esp))
            {
                informe.Problema("especialidad de profesional: falta el profesional o la especialidad", $"{pe.PreId}/{pe.EspId}");
                continue;
            }
            informe.Contar("profesionales_especialidades", await cn.ExecuteAsync("""
                INSERT INTO catalogos.profesionales_especialidades (empresa_id, profesional_id, especialidad_id)
                VALUES (@empresa, @prof, @esp) ON CONFLICT DO NOTHING
                """, new { empresa, prof, esp }, tx));
        }
    }

    async Task CoberturasAsync(DatosGeclisa d, DateOnly hoy)
    {
        var nombres = Transformaciones.NombresUnicos(d.ObrasSociales, o => o.Id, o => o.Nombre, porDefecto: "Obra social");
        foreach (var o in d.ObrasSociales)
        {
            var nombre = nombres[o.Id];
            await VincularPorNombreAsync("coberturas", o.Id, nombre);
            // Geclisa sólo distingue "particular"; obra social y prepaga quedan juntas como OBRA_SOCIAL.
            var tipo = o.EsParticular == true ? "PARTICULAR" : "OBRA_SOCIAL";
            var id = await GuardarAsync("cobertura", o.Id, """
                INSERT INTO catalogos.coberturas (id, empresa_id, nombre, sigla, tipo, activo, geclisa_id)
                VALUES (@id, @empresa, @nombre, @sigla, @tipo, @activo, @geclisa)
                ON CONFLICT (empresa_id, geclisa_id) DO UPDATE SET
                    nombre = EXCLUDED.nombre, sigla = EXCLUDED.sigla, tipo = EXCLUDED.tipo, activo = EXCLUDED.activo
                RETURNING id
                """, new { id = Guid.CreateVersion7(), empresa, nombre, sigla = Vacio(o.Sigla), tipo, activo = !o.Eliminado, geclisa = (int)o.Id });
            if (id is { } g)
            {
                coberturas[o.Id] = g;
                informe.Contar("coberturas");
            }
        }

        var nombresPlanes = Transformaciones.NombresUnicos(d.Planes, p => p.Id, p => p.Nombre, p => p.OsId, "Plan");
        foreach (var p in d.Planes)
        {
            if (!coberturas.TryGetValue(p.OsId, out var cobertura))
            {
                informe.Problema("plan de una obra social que no se cargó", p.Id);
                continue;
            }
            var id = await GuardarAsync("plan", p.Id, """
                INSERT INTO catalogos.planes (id, empresa_id, cobertura_id, nombre, activo, geclisa_id)
                VALUES (@id, @empresa, @cobertura, @nombre, @activo, @geclisa)
                ON CONFLICT (empresa_id, geclisa_id) DO UPDATE SET nombre = EXCLUDED.nombre, activo = EXCLUDED.activo
                RETURNING id
                """, new { id = Guid.CreateVersion7(), empresa, cobertura, nombre = nombresPlanes[p.Id], activo = p.FechaFin is null || DateOnly.FromDateTime(p.FechaFin.Value) >= hoy, geclisa = (int)p.Id });
            if (id is { } g)
            {
                planes[p.Id] = (g, p.OsId);
                informe.Contar("planes");
            }
        }
    }

    /// <summary>
    /// Si ya existe una fila cargada a mano con el mismo nombre y sin id de Geclisa, se la vincula
    /// en lugar de crear otra (por ejemplo, "Particular" de los datos de ejemplo).
    /// </summary>
    Task VincularPorNombreAsync(string tabla, int geclisa, string nombre) => cn.ExecuteAsync($"""
        UPDATE catalogos.{tabla} SET geclisa_id = @geclisa
        WHERE empresa_id = @empresa AND nombre = @nombre AND geclisa_id IS NULL
          AND NOT EXISTS (SELECT 1 FROM catalogos.{tabla} WHERE empresa_id = @empresa AND geclisa_id = @geclisa)
        """, new { empresa, nombre, geclisa }, tx);

    // ---------------------------------------------------------------- agendas y feriados

    async Task AgendasAsync(DatosGeclisa d, DateOnly hoy)
    {
        foreach (var c in d.Cronogramas)
        {
            if (Transformaciones.DiaSemana(c.DiaNombre) is not { } dia)
            {
                informe.Problema($"cronograma con día desconocido '{c.DiaNombre.Trim()}'", c.Id);
                continue;
            }
            if (Transformaciones.Hora(c.HsIni) is not { } desde || Transformaciones.Hora(c.HsFin) is not { } hasta || hasta <= desde)
            {
                informe.Problema("cronograma con horario inválido", c.Id);
                continue;
            }
            if (c.Tiempo is < 5 or > 240)
            {
                informe.Problema("cronograma con duración fuera de 5 a 240 minutos", c.Id);
                continue;
            }
            if (!profesionales.TryGetValue(c.PreId, out var profesional) || !servicios.TryGetValue(c.ServId, out var servicio))
            {
                informe.Problema("cronograma de un profesional o servicio que no se cargó", c.Id);
                continue;
            }
            var consultorio = c.ConsId is { } ci && consultorios.TryGetValue(ci, out var co) ? co : await ConsultorioDefectoAsync();
            var capacidad = Math.Clamp((int)(c.PacientesSimultaneos ?? 1), 1, 20);

            var existente = await cn.QuerySingleOrDefaultAsync<(Guid Id, string Gestion, Guid Consultorio, int Capacidad, int Sobreturnos)?>("""
                SELECT id, gestionada_por, consultorio_id, capacidad, sobreturnos FROM turnos.agendas
                WHERE empresa_id = @empresa AND geclisa_id = @geclisa
                """, new { empresa, geclisa = c.Id }, tx);
            if (existente is { Gestion: "SISTEMA_NUEVO" } e)
            {
                // Ya la maneja el sistema nuevo: Geclisa deja de ser la fuente de su configuración.
                agendas[c.Id] = new(e.Id, e.Consultorio, e.Capacidad, e.Sobreturnos, false);
                informe.Contar("agendas ya pasadas al sistema nuevo (sin cambios)");
                continue;
            }

            var p = new
            {
                id = existente?.Id ?? Guid.CreateVersion7(),
                empresa, profesional, servicio, consultorio, dia,
                desde = desde.ToString("HH:mm"), hasta = hasta.ToString("HH:mm"),
                duracion = (int)c.Tiempo,
                vigenteDesde = DateOnly.FromDateTime(c.FechaIni).ToString("yyyy-MM-dd"),
                vigenteHasta = DateOnly.FromDateTime(c.FechaFin).ToString("yyyy-MM-dd"),
                capacidad, usuario, geclisa = c.Id,
                activa = true,
            };
            const string sql = """
                INSERT INTO turnos.agendas (id, empresa_id, profesional_id, servicio_id, consultorio_id, dia_semana,
                    hora_desde, hora_hasta, duracion_minutos, vigente_desde, vigente_hasta, capacidad, sobreturnos,
                    gestionada_por, activa, geclisa_id, creado_por)
                VALUES (@id, @empresa, @profesional, @servicio, @consultorio, @dia, @desde::time, @hasta::time, @duracion,
                    @vigenteDesde::date, @vigenteHasta::date, @capacidad, 0, 'GECLISA', @activa, @geclisa, @usuario)
                ON CONFLICT (empresa_id, geclisa_id) DO UPDATE SET
                    profesional_id = EXCLUDED.profesional_id, servicio_id = EXCLUDED.servicio_id,
                    consultorio_id = EXCLUDED.consultorio_id, dia_semana = EXCLUDED.dia_semana,
                    hora_desde = EXCLUDED.hora_desde, hora_hasta = EXCLUDED.hora_hasta,
                    duracion_minutos = EXCLUDED.duracion_minutos, vigente_desde = EXCLUDED.vigente_desde,
                    vigente_hasta = EXCLUDED.vigente_hasta, capacidad = EXCLUDED.capacidad, activa = EXCLUDED.activa,
                    version = turnos.agendas.version + 1, modificado_en = now(), modificado_por = EXCLUDED.creado_por
                RETURNING id
                """;
            var (id, error) = await IntentarAsync(() => cn.ExecuteScalarAsync<Guid>(sql, p, tx));
            if (error is { SqlState: PostgresErrorCodes.ExclusionViolation })
            {
                // Geclisa permite cronogramas superpuestos; acá quedan cargados pero inactivos para revisarlos.
                informe.Problema("cronograma superpuesto con otro del mismo profesional (cargado inactivo)", c.Id);
                (id, error) = await IntentarAsync(() => cn.ExecuteScalarAsync<Guid>(sql, p with { activa = false }, tx));
            }
            if (error is not null)
            {
                informe.Problema($"cronograma rechazado por la base ({error.SqlState})", c.Id);
                continue;
            }
            agendas[c.Id] = new(id, consultorio, capacidad, 0, true);
            informe.Contar("agendas");

            // Coberturas aceptadas: se reemplazan completas con lo que dice Geclisa.
            await cn.ExecuteAsync("DELETE FROM turnos.agendas_coberturas WHERE agenda_id = @id", new { id }, tx);
            foreach (var x in d.CronogramasCoberturas.Where(x => x.CroId == c.Id))
            {
                if (!coberturas.TryGetValue(x.OsId, out var cobertura))
                {
                    informe.Problema("cobertura de cronograma que no se cargó", $"{c.Id}/{x.OsId}");
                    continue;
                }
                informe.Contar("agendas_coberturas", await cn.ExecuteAsync("""
                    INSERT INTO turnos.agendas_coberturas (empresa_id, agenda_id, cobertura_id)
                    VALUES (@empresa, @id, @cobertura) ON CONFLICT DO NOTHING
                    """, new { empresa, id, cobertura }, tx));
            }
        }
    }

    async Task FeriadosAsync(DatosGeclisa d)
    {
        foreach (var f in d.Feriados)
        {
            if (!string.IsNullOrWhiteSpace(f.AplicaEn))
                informe.Problema($"feriado con AplicaEn='{f.AplicaEn.Trim()}' (significado desconocido, se cargó para todos)", f.Id);
            if (f.PrestadoresExcluidos > 0)
                informe.Problema("feriado con prestadores exceptuados (no soportado: bloquea a todos)", f.Id);

            var fecha = DateOnly.FromDateTime(f.Fecha);
            var parcial = f.HsIni is { } hi && f.HsFin is { } hf
                && Transformaciones.Hora(hi) is { } ini && Transformaciones.Hora(hf) is { } fin && fin > ini;
            var desde = parcial ? fecha.ToDateTime(Transformaciones.Hora(f.HsIni!.Value)!.Value) : fecha.ToDateTime(TimeOnly.MinValue);
            var hasta = parcial ? fecha.ToDateTime(Transformaciones.Hora(f.HsFin!.Value)!.Value) : fecha.AddDays(1).ToDateTime(TimeOnly.MinValue);
            var motivo = Transformaciones.Limpiar(f.Descripcion);
            if (motivo.Length == 0) motivo = "Feriado";

            var n = await cn.ExecuteAsync("""
                INSERT INTO turnos.bloqueos (id, empresa_id, profesional_id, periodo, motivo, creado_por)
                SELECT @id, @empresa, NULL, r, @motivo, @usuario
                FROM (SELECT tstzrange(@desde::timestamp AT TIME ZONE @zona, @hasta::timestamp AT TIME ZONE @zona, '[)') AS r) x
                WHERE NOT EXISTS (
                    SELECT 1 FROM turnos.bloqueos b
                    WHERE b.empresa_id = @empresa AND b.profesional_id IS NULL AND b.periodo = x.r AND b.motivo = @motivo)
                """, new { id = Guid.CreateVersion7(), empresa, motivo, usuario, zona, desde = Iso(desde), hasta = Iso(hasta) }, tx);
            informe.Contar(n > 0 ? "feriados" : "feriados ya cargados");
        }
    }

    // ---------------------------------------------------------------- pacientes y turnos

    async Task PacientesAsync(DatosGeclisa d, DateOnly hoy)
    {
        foreach (var f in d.Fichas)
        {
            if (Transformaciones.Paciente(f, opciones.DatosReales, hoy) is not { } p)
            {
                informe.Problema("ficha con sexo, fecha de nacimiento o nombre inválidos (no se cargó)", f.Id);
                continue;
            }
            var ficha = f.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var id = await cn.ExecuteScalarAsync<Guid?>("""
                SELECT paciente_id FROM pacientes.identificadores
                WHERE empresa_id = @empresa AND sistema = 'GECLISA_FICHA' AND valor = @ficha
                """, new { empresa, ficha }, tx);

            if (id is null && p.Dni is not null)
            {
                // Geclisa no repite (tipo, número) de documento: si el DNI ya está en la base nueva es la misma persona.
                id = await cn.ExecuteScalarAsync<Guid?>("""
                    SELECT paciente_id FROM pacientes.identificadores
                    WHERE empresa_id = @empresa AND sistema = 'DNI' AND valor = @dni
                    """, new { empresa, p.Dni }, tx);
                if (id is not null)
                {
                    await AgregarIdentificadorAsync(id.Value, "GECLISA_FICHA", ficha);
                    informe.Contar("pacientes vinculados por DNI");
                }
            }

            var datos = new
            {
                empresa, usuario, p.Apellidos, p.Nombres, p.Telefono, p.Email,
                nacimiento = p.FechaNacimiento.ToString("yyyy-MM-dd"), sexo = p.Sexo.ToString(),
            };
            if (id is { } existente)
            {
                await cn.ExecuteAsync("""
                    UPDATE pacientes.pacientes SET apellidos = @Apellidos, nombres = @Nombres,
                        fecha_nacimiento = @nacimiento::date, sexo_registral = @sexo, telefono = @Telefono, email = @Email,
                        version = version + 1, modificado_en = now(), modificado_por = @usuario
                    WHERE id = @id AND (apellidos, nombres, fecha_nacimiento, sexo_registral, telefono, email)
                        IS DISTINCT FROM (@Apellidos, @Nombres, @nacimiento::date, @sexo::char(1), @Telefono, @Email)
                    """, new { id = existente, datos.empresa, datos.usuario, datos.Apellidos, datos.Nombres, datos.Telefono, datos.Email, datos.nacimiento, datos.sexo }, tx);
                pacientes[f.Id] = existente;
                informe.Contar("pacientes actualizados");
                continue;
            }

            var nuevo = Guid.CreateVersion7();
            await cn.ExecuteAsync("""
                INSERT INTO pacientes.pacientes (id, empresa_id, apellidos, nombres, fecha_nacimiento, sexo_registral, telefono, email, creado_por)
                VALUES (@id, @empresa, @Apellidos, @Nombres, @nacimiento::date, @sexo, @Telefono, @Email, @usuario)
                """, new { id = nuevo, datos.empresa, datos.usuario, datos.Apellidos, datos.Nombres, datos.Telefono, datos.Email, datos.nacimiento, datos.sexo }, tx);
            await AgregarIdentificadorAsync(nuevo, "GECLISA_FICHA", ficha);
            if (p.Dni is not null)
                await AgregarIdentificadorAsync(nuevo, "DNI", p.Dni);
            pacientes[f.Id] = nuevo;
            informe.Contar("pacientes nuevos");
        }
    }

    Task AgregarIdentificadorAsync(Guid paciente, string sistema, string valor) => cn.ExecuteAsync("""
        INSERT INTO pacientes.identificadores (id, empresa_id, paciente_id, sistema, valor)
        VALUES (@id, @empresa, @paciente, @sistema, @valor) ON CONFLICT DO NOTHING
        """, new { id = Guid.CreateVersion7(), empresa, paciente, sistema, valor }, tx);

    async Task TurnosAsync(DatosGeclisa d, DateOnly hoy)
    {
        var vistos = new List<int>();
        foreach (var t in d.Turnos.OrderBy(t => t.Fecha).ThenBy(t => t.HsIni).ThenBy(t => t.Id))
        {
            if (DateOnly.FromDateTime(t.Fecha) < hoy) continue;
            vistos.Add(t.Id);

            if (t.CroId is not { } cro || !agendas.TryGetValue(cro, out var agenda))
            {
                informe.Problema("turno sin cronograma cargado (no se cargó)", t.Id);
                continue;
            }
            if (!pacientes.TryGetValue(t.FichaId, out var paciente))
            {
                informe.Problema("turno de una ficha que no se cargó", t.Id);
                continue;
            }
            if (!profesionales.TryGetValue(t.PreId, out var profesional) || !servicios.TryGetValue(t.ServId, out var servicio))
            {
                informe.Problema("turno de un profesional o servicio que no se cargó", t.Id);
                continue;
            }
            if (Transformaciones.HorarioTurno(t.Fecha, t.HsIni, t.HsFin, t.Tiempo) is not var (desde, hasta))
            {
                informe.Problema("turno con horario inválido", t.Id);
                continue;
            }

            var ya = await cn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM turnos.turnos WHERE empresa_id = @empresa AND geclisa_id = @geclisa)",
                new { empresa, geclisa = t.Id }, tx);
            if (ya)
            {
                informe.Contar("turnos ya importados");
                continue;
            }

            Guid? cobertura = coberturas.TryGetValue(t.OsId, out var cob) ? cob : null;
            Guid? plan = null;
            if (t.PlanId is { } pl)
            {
                if (planes.TryGetValue(pl, out var pp) && pp.OsId == t.OsId)
                    plan = pp.Id;
                else
                    informe.Problema("turno con un plan que no es de su obra social (se cargó sin plan)", t.Id);
            }
            if (cobertura is null)
                informe.Problema("turno con obra social que no se cargó (se cargó sin cobertura)", t.Id);

            // Mismo candado que la API, para no cruzarse con turnos que se estén dando en ese momento.
            await cn.ExecuteAsync(
                "SELECT pg_advisory_xact_lock(hashtextextended('turnos.profesional:' || @profesional::text, 0))",
                new { profesional }, tx);

            var rango = new { empresa, profesional, zona, desde = Iso(desde), hasta = Iso(hasta) };
            var ocupados = (await cn.QueryAsync<int>("""
                SELECT cupo FROM turnos.turnos
                WHERE empresa_id = @empresa AND profesional_id = @profesional AND estado <> 'CANCELADO'
                  AND horario && tstzrange(@desde::timestamp AT TIME ZONE @zona, @hasta::timestamp AT TIME ZONE @zona, '[)')
                """, rango, tx)).ToHashSet();
            var cupo = 1;
            while (ocupados.Contains(cupo)) cupo++;

            var (_, error) = await IntentarAsync(() => cn.ExecuteAsync("""
                INSERT INTO turnos.turnos (id, empresa_id, agenda_id, profesional_id, servicio_id, consultorio_id, paciente_id,
                    horario, cupo, es_sobreturno, estado, cobertura_id, plan_id, numero_afiliado, observaciones, origen,
                    geclisa_id, creado_por)
                VALUES (@id, @empresa, @agenda, @profesional, @servicio, @consultorio, @paciente,
                    tstzrange(@desde::timestamp AT TIME ZONE @zona, @hasta::timestamp AT TIME ZONE @zona, '[)'),
                    @cupo, @sobreturno, @estado, @cobertura, @plan, @afiliado, @observaciones, @origen, @geclisa, @usuario)
                """, new
            {
                id = Guid.CreateVersion7(), empresa, agenda = agenda.Id, profesional, servicio,
                consultorio = t.ConsId is { } ci && consultorios.TryGetValue(ci, out var co) ? co : agenda.ConsultorioId,
                paciente, zona, desde = Iso(desde), hasta = Iso(hasta), cupo, sobreturno = cupo > agenda.Capacidad,
                estado = Transformaciones.EstadoTurno(t), cobertura, plan,
                afiliado = opciones.DatosReales ? Vacio(t.NroAfiliado) : null,
                // Texto libre: puede tener datos de salud o de contacto, así que sólo con datos reales.
                observaciones = opciones.DatosReales ? Vacio(t.Observaciones) : null,
                origen = t.EsWeb == true ? "WEB" : "PRESENCIAL",
                geclisa = t.Id, usuario,
            }, tx));
            if (error is { SqlState: PostgresErrorCodes.ExclusionViolation })
                informe.Problema("turno superpuesto con otro del mismo paciente (no se cargó)", t.Id);
            else if (error is not null)
                informe.Problema($"turno rechazado por la base ({error.SqlState})", t.Id);
            else
            {
                informe.Contar("turnos");
                if (cupo > agenda.Capacidad + agenda.Sobreturnos)
                    informe.Problema("turno por encima de la capacidad del horario (doble turno en Geclisa)", t.Id);
            }
        }

        // Geclisa borra los turnos cancelados. Un turno importado antes que ya no está en Geclisa
        // se canceló allá; acá se cancela también, sólo en agendas que todavía maneja Geclisa.
        var cancelados = await cn.ExecuteAsync("""
            UPDATE turnos.turnos t SET estado = 'CANCELADO', cancelado_en = now(), cancelado_por = @usuario,
                motivo_cancelacion = 'Borrado en Geclisa', version = t.version + 1, modificado_en = now(), modificado_por = @usuario
            FROM turnos.agendas a
            WHERE a.empresa_id = t.empresa_id AND a.id = t.agenda_id AND a.gestionada_por = 'GECLISA'
              AND t.empresa_id = @empresa AND t.geclisa_id IS NOT NULL AND t.estado IN ('RESERVADO', 'CONFIRMADO')
              AND lower(t.horario) >= (@hoy::date)::timestamp AT TIME ZONE @zona
              AND NOT (t.geclisa_id = ANY(@vistos))
            """, new { usuario, empresa, zona, hoy = hoy.ToString("yyyy-MM-dd"), vistos = vistos.ToArray() }, tx);
        if (cancelados > 0)
            informe.Contar("turnos cancelados (ya no están en Geclisa)", cancelados);
    }

    // ---------------------------------------------------------------- ayudas

    /// <summary>Inserta o actualiza dentro de un punto de guardado; si la base lo rechaza, lo informa y sigue.</summary>
    async Task<Guid?> GuardarAsync(string que, int geclisa, string sql, object p)
    {
        var (id, error) = await IntentarAsync(() => cn.ExecuteScalarAsync<Guid>(sql, p, tx));
        if (error is null) return id;
        informe.Problema(error.SqlState == PostgresErrorCodes.UniqueViolation
            ? $"{que}: el nombre ya existe en la base nueva con otro id de Geclisa"
            : $"{que} rechazado por la base ({error.SqlState})", geclisa);
        return null;
    }

    async Task<(T Valor, PostgresException? Error)> IntentarAsync<T>(Func<Task<T>> accion)
    {
        await tx.SaveAsync("fila");
        try
        {
            var valor = await accion();
            await tx.ReleaseAsync("fila");
            return (valor, null);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.ExclusionViolation or PostgresErrorCodes.ForeignKeyViolation
            or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.NotNullViolation)
        {
            await tx.RollbackAsync("fila");
            return (default!, ex);
        }
    }

    static string Iso(DateTime local) => local.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    static string? Vacio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
