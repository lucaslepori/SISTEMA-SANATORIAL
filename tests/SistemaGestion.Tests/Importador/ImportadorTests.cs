using SistemaGestion.Importador;

namespace SistemaGestion.Tests;

/// <summary>
/// Importa datos de Geclisa armados en memoria contra la base de prueba. Usa la empresa 2, que no
/// tiene agendas, para no interferir con las pruebas de turnos. Cada prueba usa sus propios ids.
/// </summary>
[Collection(ColeccionBase.Nombre)]
public sealed class ImportadorTests(BaseDePrueba bd)
{
    // Lunes. Las pruebas fijan "hoy" para no depender de la fecha en que corren.
    static readonly DateOnly Hoy = new(2030, 1, 7);
    static readonly DateTime Lunes = new(2030, 1, 14);

    sealed class FuenteFija(DatosGeclisa datos) : IFuenteGeclisa
    {
        public Task<DatosGeclisa> LeerAsync(DateOnly desde, bool todasLasFichas, CancellationToken ct) => Task.FromResult(datos);
    }

    /// <summary>Un profesional que atiende los lunes de 8 a 12, turnos de 20 minutos, con dos pacientes y un turno.</summary>
    static DatosGeclisa Datos(short b, params GTurno[] turnos) => new()
    {
        Ubicaciones = [new() { Id = b, Nombre = $"Sede {b}", Direccion = "Mitre 100" }],
        Consultorios = [new() { Id = b, Nombre = "Consultorio A", UbicId = b }],
        Servicios = [new() { Id = b, Nombre = $"Clínica {b}" }],
        Especialidades = [new() { Id = b, Nombre = $"Cardiología {b}" }],
        Prestadores = [new() { Id = b, Nombre = $"PEREZ{b}, JUAN", MatriculaProvincial = 1000 + b }],
        PrestadoresEspecialidades = [new() { PreId = b, EspId = b }],
        ObrasSociales = [new() { Id = b, Sigla = "OS", Nombre = $"Obra social {b}" }],
        Planes = [new() { Id = b, Nombre = "210", OsId = b }],
        Cronogramas =
        [
            new()
            {
                Id = b, DiaNombre = "Lunes", ServId = b, PreId = b, HsIni = 800, HsFin = 1200, Tiempo = 20, ConsId = b,
                FechaIni = new DateTime(2029, 1, 1), FechaFin = new DateTime(2030, 12, 31), PacientesSimultaneos = 1,
            },
        ],
        CronogramasCoberturas = [new() { CroId = b, OsId = b }],
        Feriados = [new() { Id = b, Fecha = Lunes.AddDays(7 * b), Descripcion = $"Feriado {b}    " }],
        Fichas =
        [
            new() { Id = b * 10 + 1, Apellido = "GOMEZ", Nombre = "ANA", TipoDoc = "DNI", NroDoc = $"{30000000 + b * 10 + 1}", Sexo = "F", FechaNacimiento = new DateTime(1980, 5, 1) },
            new() { Id = b * 10 + 2, Apellido = "LOPEZ", Nombre = "LUIS", TipoDoc = "DNI", NroDoc = $"{30000000 + b * 10 + 2}", Sexo = "M", FechaNacimiento = new DateTime(1990, 5, 1) },
        ],
        Turnos = turnos.Length > 0 ? [.. turnos] : [Turno(b, b * 100 + 1, b * 10 + 1, 900)],
    };

    static GTurno Turno(short b, int id, int ficha, short hora, int? cro = null) => new()
    {
        Id = id, ServId = b, Fecha = Lunes, HsIni = hora, HsFin = (short)(hora + 20), Tiempo = 20, FichaId = ficha,
        OsId = b, PlanId = b, PreId = b, ConsId = b, CroId = cro ?? b, NroAfiliado = "123/4", Observaciones = "trae estudios",
    };

    Task<Informe> ImportarAsync(DatosGeclisa d, bool simular = false, bool datosReales = false) =>
        new ImportadorGeclisa(bd.CadenaAdmin, new OpcionesImportacion(Guid.Parse(BaseDePrueba.Empresa2), DatosReales: datosReales, Simular: simular))
            .EjecutarAsync(new FuenteFija(d), Hoy);

    Task<long?> ContarAsync(string sql) => bd.EscalarAsync<long?>(sql);

    [Fact]
    public async Task Carga_catalogos_agenda_feriado_pacientes_seudonimizados_y_turnos()
    {
        var informe = await ImportarAsync(Datos(1));

        Assert.Empty(informe.ProblemasTotal);
        Assert.Equal(1, informe.Cargados["agendas"]);
        Assert.Equal(1, informe.Cargados["turnos"]);
        Assert.Equal(2, informe.Cargados["pacientes nuevos"]);

        // Lunes 8 a 12 hora local, manejada por Geclisa, con la cobertura aceptada.
        Assert.Equal("1|08:00:00|12:00:00|20|GECLISA|1", await bd.EscalarAsync<string>("""
            SELECT concat_ws('|', a.dia_semana, a.hora_desde, a.hora_hasta, a.duracion_minutos, a.gestionada_por,
                (SELECT count(*) FROM turnos.agendas_coberturas c WHERE c.agenda_id = a.id))
            FROM turnos.agendas a WHERE a.geclisa_id = 1
            """));
        // El turno queda a las 9 de Buenos Aires, sin datos de texto libre ni afiliado (seudonimizado).
        Assert.Equal("2030-01-14 12:00:00+00|RESERVADO|1|", await bd.EscalarAsync<string>("""
            SELECT concat_ws('|', to_char(lower(horario) AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS+00'), estado, cupo,
                coalesce(observaciones, '') || coalesce(numero_afiliado, ''))
            FROM turnos.turnos WHERE geclisa_id = 101
            """));
        // El paciente no tiene ningún dato que lo identifique (ni teléfono ni DNI; concat_ws omite los NULL).
        Assert.Equal("Paciente|Geclisa 11|1980-01-01|F|", await bd.EscalarAsync<string>("""
            SELECT concat_ws('|', p.apellidos, p.nombres, p.fecha_nacimiento, p.sexo_registral, coalesce(p.telefono, ''),
                (SELECT string_agg(sistema, ',') FROM pacientes.identificadores i WHERE i.paciente_id = p.id AND sistema <> 'GECLISA_FICHA'))
            FROM pacientes.pacientes p
            JOIN pacientes.identificadores i ON i.paciente_id = p.id AND i.sistema = 'GECLISA_FICHA' AND i.valor = '11'
            """));
        Assert.Equal(1L, await ContarAsync("SELECT count(*) FROM turnos.bloqueos WHERE motivo = 'Feriado 1'"));
        // Cada corrida deja un evento de auditoría (otras pruebas también importan, por eso no se cuenta exacto).
        Assert.True(await ContarAsync("SELECT count(*) FROM auditoria.eventos WHERE recurso_tipo = 'importacion_geclisa'") >= 1);
    }

    [Fact]
    public async Task Volver_a_importar_no_duplica_y_cancela_lo_que_Geclisa_borro()
    {
        await ImportarAsync(Datos(2, Turno(2, 201, 21, 900), Turno(2, 202, 22, 920)));
        // Segunda corrida: el turno 202 ya no está (Geclisa borra los cancelados).
        var informe = await ImportarAsync(Datos(2, Turno(2, 201, 21, 900)));

        Assert.Equal(1, informe.Cargados["turnos ya importados"]);
        Assert.Equal(1, informe.Cargados["turnos cancelados (ya no están en Geclisa)"]);
        Assert.Equal(1L, await ContarAsync("SELECT count(*) FROM turnos.agendas WHERE geclisa_id = 2"));
        Assert.Equal(1L, await ContarAsync("SELECT count(*) FROM catalogos.profesionales WHERE geclisa_id = 2"));
        Assert.Equal(1L, await ContarAsync("SELECT count(*) FROM pacientes.identificadores WHERE sistema = 'GECLISA_FICHA' AND valor = '21'"));
        Assert.Equal(1L, await ContarAsync("SELECT count(*) FROM turnos.bloqueos WHERE motivo = 'Feriado 2'"));
        Assert.Equal("CANCELADO|Borrado en Geclisa", await bd.EscalarAsync<string>(
            "SELECT estado || '|' || motivo_cancelacion FROM turnos.turnos WHERE geclisa_id = 202"));
    }

    [Fact]
    public async Task Informa_el_doble_turno_de_Geclisa_en_lugar_de_ocultarlo()
    {
        // Dos pacientes en el mismo horario de capacidad 1, y un paciente con dos turnos superpuestos.
        var informe = await ImportarAsync(Datos(3,
            Turno(3, 301, 31, 900), Turno(3, 302, 32, 900), Turno(3, 303, 31, 900)));

        Assert.Equal(2, informe.Cargados["turnos"]);
        Assert.Equal(1, informe.Total("turno por encima de la capacidad del horario (doble turno en Geclisa)"));
        Assert.Equal(["303"], informe.Problemas["turno superpuesto con otro del mismo paciente (no se cargó)"]);
        Assert.Equal("2|true", await bd.EscalarAsync<string>("SELECT cupo || '|' || es_sobreturno FROM turnos.turnos WHERE geclisa_id = 302"));
    }

    [Fact]
    public async Task Un_cronograma_superpuesto_queda_inactivo_y_se_informa()
    {
        var d = Datos(4);
        d.Cronogramas.Add(new()
        {
            Id = 44, DiaNombre = "LUNES", ServId = 4, PreId = 4, HsIni = 1100, HsFin = 1300, Tiempo = 30,
            FechaIni = new DateTime(2029, 1, 1), FechaFin = new DateTime(2030, 12, 31),
        });
        var informe = await ImportarAsync(d);

        Assert.Equal(["44"], informe.Problemas["cronograma superpuesto con otro del mismo profesional (cargado inactivo)"]);
        Assert.False(await bd.EscalarAsync<bool>("SELECT activa FROM turnos.agendas WHERE geclisa_id = 44"));
        // Sin consultorio en Geclisa: va al consultorio "Sin consultorio (Geclisa)".
        Assert.Equal("Sin consultorio (Geclisa)", await bd.EscalarAsync<string>(
            "SELECT c.nombre FROM turnos.agendas a JOIN catalogos.consultorios c ON c.id = a.consultorio_id WHERE a.geclisa_id = 44"));
    }

    [Fact]
    public async Task No_pisa_una_agenda_que_ya_maneja_el_sistema_nuevo()
    {
        await ImportarAsync(Datos(5));
        await BaseDePrueba.EjecutarAsync(bd.CadenaAdmin,
            "UPDATE turnos.agendas SET gestionada_por = 'SISTEMA_NUEVO', hora_hasta = '11:00' WHERE geclisa_id = 5");

        var informe = await ImportarAsync(Datos(5));

        Assert.Equal(1, informe.Cargados["agendas ya pasadas al sistema nuevo (sin cambios)"]);
        Assert.Equal("11:00:00", await bd.EscalarAsync<string>("SELECT hora_hasta::text FROM turnos.agendas WHERE geclisa_id = 5"));
    }

    [Fact]
    public async Task Con_datos_reales_copia_el_dni_y_vincula_al_paciente_que_ya_existia()
    {
        // Un paciente cargado a mano en el sistema nuevo con el mismo DNI que la ficha 61.
        await BaseDePrueba.EjecutarAsync(bd.CadenaAdmin, $"""
            INSERT INTO pacientes.pacientes (id, empresa_id, apellidos, nombres, fecha_nacimiento, sexo_registral, creado_por)
            VALUES ('0192f000-0000-7000-8000-00000000e601', '{BaseDePrueba.Empresa2}', 'Gómez', 'Ana', '1980-05-01', 'F', '0192f000-0000-7000-8000-0000000000a3');
            INSERT INTO pacientes.identificadores (id, empresa_id, paciente_id, sistema, valor)
            VALUES ('0192f000-0000-7000-8000-00000000e602', '{BaseDePrueba.Empresa2}', '0192f000-0000-7000-8000-00000000e601', 'DNI', '30000061');
            """);

        var informe = await ImportarAsync(Datos(6), datosReales: true);

        Assert.Equal(1, informe.Cargados["pacientes vinculados por DNI"]);
        Assert.Equal("0192f000-0000-7000-8000-00000000e601", (await bd.EscalarAsync<Guid>(
            "SELECT paciente_id FROM pacientes.identificadores WHERE sistema = 'GECLISA_FICHA' AND valor = '61'")).ToString());
        Assert.Equal("LOPEZ|LUIS", await bd.EscalarAsync<string>("""
            SELECT p.apellidos || '|' || p.nombres FROM pacientes.pacientes p
            JOIN pacientes.identificadores i ON i.paciente_id = p.id AND i.sistema = 'DNI' AND i.valor = '30000062'
            """));
        Assert.Equal("trae estudios", await bd.EscalarAsync<string>("SELECT observaciones FROM turnos.turnos WHERE geclisa_id = 601"));
    }

    [Fact]
    public async Task La_simulacion_no_guarda_nada()
    {
        var informe = await ImportarAsync(Datos(7), simular: true);

        Assert.Equal(1, informe.Cargados["turnos"]);
        Assert.Equal(0L, await ContarAsync("SELECT count(*) FROM catalogos.servicios WHERE geclisa_id = 7"));
        Assert.Equal(0L, await ContarAsync("SELECT count(*) FROM turnos.turnos WHERE geclisa_id = 701"));
    }

    [Fact]
    public async Task Lo_que_no_se_puede_convertir_se_informa_y_no_se_inventa()
    {
        var d = Datos(8);
        d.Cronogramas[0] = new()
        {
            Id = 8, DiaNombre = "Lunes", ServId = 8, PreId = 8, HsIni = 800, HsFin = 1200, Tiempo = 20, ConsId = 8,
            FechaIni = new DateTime(2029, 1, 1), FechaFin = new DateTime(2030, 12, 31),
        };
        d.Cronogramas.Add(new() { Id = 88, DiaNombre = "Feriado", ServId = 8, PreId = 8, HsIni = 800, HsFin = 1200, Tiempo = 20, FechaIni = new DateTime(2029, 1, 1), FechaFin = new DateTime(2030, 12, 31) });
        d.Prestadores.Add(new() { Id = 89, Nombre = "SIN MATRICULA", MatriculaProvincial = 0 });
        d.Fichas.Add(new() { Id = 89, Apellido = "X", Nombre = "Y", Sexo = "I", FechaNacimiento = new DateTime(1980, 1, 1) });

        var informe = await ImportarAsync(d);

        Assert.Equal(["88"], informe.Problemas["cronograma con día desconocido 'Feriado'"]);
        Assert.Equal(["89"], informe.Problemas["profesional sin matrícula (no se cargó)"]);
        Assert.Equal(["89"], informe.Problemas["ficha con sexo, fecha de nacimiento o nombre inválidos (no se cargó)"]);
        Assert.Equal(1, informe.Cargados["turnos"]);
    }
}
