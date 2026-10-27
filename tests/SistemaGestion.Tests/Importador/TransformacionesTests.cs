using SistemaGestion.Importador;

namespace SistemaGestion.Tests;

public sealed class TransformacionesTests
{
    [Theory]
    [InlineData(800, "08:00")]
    [InlineData(1830, "18:30")]
    [InlineData(0, "00:00")]
    [InlineData(2359, "23:59")]
    public void Convierte_la_hora_HHMM(short hhmm, string esperado) =>
        Assert.Equal(TimeOnly.Parse(esperado), Transformaciones.Hora(hhmm));

    [Theory]
    [InlineData(2400)]
    [InlineData(860)]
    [InlineData(-1)]
    public void Rechaza_horas_invalidas(short hhmm) => Assert.Null(Transformaciones.Hora(hhmm));

    [Theory]
    [InlineData("Lunes", 1)]
    [InlineData("MIÉRCOLES           ", 3)]
    [InlineData("miercoles", 3)]
    [InlineData("Sábado", 6)]
    [InlineData("Domingo", 7)]
    public void Reconoce_el_dia_por_nombre(string nombre, int esperado) =>
        Assert.Equal(esperado, Transformaciones.DiaSemana(nombre));

    [Fact]
    public void Un_dia_desconocido_no_se_adivina() => Assert.Null(Transformaciones.DiaSemana("Feriado"));

    [Fact]
    public void Separa_el_nombre_con_coma_sin_suponer()
    {
        var (apellidos, nombres, supuesto) = Transformaciones.SepararNombre("DE LA FUENTE,  MARÍA JOSÉ");
        Assert.Equal(("DE LA FUENTE", "MARÍA JOSÉ", false), (apellidos, nombres, supuesto));
    }

    [Fact]
    public void Sin_coma_toma_la_primera_palabra_y_avisa_que_es_supuesto()
    {
        Assert.Equal(("PEREZ", "JUAN CARLOS", true), Transformaciones.SepararNombre("PEREZ JUAN CARLOS"));
        Assert.Equal(("PEREZ", "-", true), Transformaciones.SepararNombre("PEREZ"));
    }

    [Fact]
    public void Las_matriculas_en_cero_no_cuentan()
    {
        Assert.Equal((null, "1234"), Transformaciones.Matriculas(0, 1234));
        Assert.Equal(("99", null), Transformaciones.Matriculas(99, 0));
        Assert.Equal((null, null), Transformaciones.Matriculas(null, 0));
    }

    [Fact]
    public void Los_nombres_repetidos_se_distinguen_con_el_id_de_geclisa()
    {
        var filas = new[] { (Id: 3, Nombre: "Cardiología"), (Id: 1, Nombre: "CARDIOLOGIA "), (Id: 2, Nombre: ""), (Id: 4, Nombre: "Pediatría") };
        var nombres = Transformaciones.NombresUnicos(filas, f => f.Id, f => f.Nombre, porDefecto: "Servicio");

        Assert.Equal("CARDIOLOGIA", nombres[1]);
        Assert.Equal("Cardiología (Geclisa 3)", nombres[3]);
        Assert.Equal("Servicio 2", nombres[2]);
        Assert.Equal("Pediatría", nombres[4]);
    }

    [Fact]
    public void El_mismo_nombre_en_grupos_distintos_no_se_toca()
    {
        var filas = new[] { (Id: 1, Os: 1, Nombre: "Plan 210"), (Id: 2, Os: 2, Nombre: "Plan 210") };
        var nombres = Transformaciones.NombresUnicos(filas, f => f.Id, f => f.Nombre, f => f.Os);
        Assert.Equal("Plan 210", nombres[1]);
        Assert.Equal("Plan 210", nombres[2]);
    }

    [Fact]
    public void El_horario_del_turno_usa_la_duracion_si_falta_la_hora_de_fin()
    {
        var fecha = new DateTime(2030, 1, 7);
        Assert.Equal((fecha.AddHours(9), fecha.AddHours(9).AddMinutes(20)), Transformaciones.HorarioTurno(fecha, 900, 920, 15));
        Assert.Equal((fecha.AddHours(9), fecha.AddHours(9).AddMinutes(15)), Transformaciones.HorarioTurno(fecha, 900, 0, 15));
        Assert.Null(Transformaciones.HorarioTurno(fecha, 900, 0, 0));
    }

    [Fact]
    public void El_estado_se_deduce_de_las_fechas()
    {
        Assert.Equal("RESERVADO", Transformaciones.EstadoTurno(new GTurno()));
        Assert.Equal("CONFIRMADO", Transformaciones.EstadoTurno(new GTurno { Confirmado = true }));
        Assert.Equal("PRESENTE", Transformaciones.EstadoTurno(new GTurno { Confirmado = true, FechaEspera = DateTime.Now }));
        Assert.Equal("ATENDIDO", Transformaciones.EstadoTurno(new GTurno { FechaEspera = DateTime.Now, FechaAtendido = DateTime.Now }));
    }

    static readonly GFicha Ficha = new()
    {
        Id = 77, Apellido = "GÓMEZ ", Nombre = "ANA", TipoDoc = "D.N.I.", NroDoc = "30111222 ", Sexo = "f",
        FechaNacimiento = new DateTime(1985, 6, 20), Telefono = "4111111", Celular = " ", Email = "ana@ejemplo.com",
    };

    [Fact]
    public void Por_defecto_el_paciente_sale_seudonimizado()
    {
        var p = Transformaciones.Paciente(Ficha, datosReales: false, new DateOnly(2030, 1, 1))!;
        Assert.Equal(new PacienteImportado("Paciente", "Geclisa 77", new DateOnly(1985, 1, 1), 'F', null, null, null), p);
    }

    [Fact]
    public void Con_datos_reales_copia_nombre_contacto_y_dni()
    {
        var p = Transformaciones.Paciente(Ficha, datosReales: true, new DateOnly(2030, 1, 1))!;
        Assert.Equal(new PacienteImportado("GÓMEZ", "ANA", new DateOnly(1985, 6, 20), 'F', "4111111", "ana@ejemplo.com", "30111222"), p);
    }

    [Theory]
    [InlineData("I", "1985-06-20")]
    [InlineData("F", "1900-01-01")]
    [InlineData("F", "2031-01-01")]
    public void No_inventa_sexo_ni_fecha_de_nacimiento(string sexo, string nacimiento)
    {
        var f = new GFicha { Id = 1, Apellido = "A", Nombre = "B", Sexo = sexo, FechaNacimiento = DateTime.Parse(nacimiento) };
        Assert.Null(Transformaciones.Paciente(f, datosReales: false, new DateOnly(2030, 1, 1)));
    }

    [Theory]
    [InlineData("DNI", "30111222", true)]
    [InlineData("D.N.I.", "1234567", true)]
    [InlineData("PASAPORTE", "30111222", false)]
    [InlineData("DNI", "AB123", false)]
    [InlineData("DNI", "0", false)]
    public void Solo_toma_como_dni_un_numero_de_dni(string tipo, string numero, bool esperado) =>
        Assert.Equal(esperado, Transformaciones.EsDni(tipo, numero));
}
