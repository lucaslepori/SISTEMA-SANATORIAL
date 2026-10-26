using System.Net;
using System.Net.Http.Json;
using SistemaGestion.Catalogos;
using SistemaGestion.Rrhh;

namespace SistemaGestion.Tests;

[Collection(ColeccionBase.Nombre)]
public sealed class RrhhApiTests(BaseDePrueba bd)
{
    private const string Rrhh = "rrhh.local";
    private const string Admin = "admin.local";
    private const string Admision = "admision.local";
    private const string ProfesionalSemilla = "0192f000-0000-7000-8000-0000000000b6";

    private readonly HttpClient _rrhh = bd.Cliente(Rrhh, BaseDePrueba.Empresa1);

    private static int _legajo = 1000;

    [Fact]
    public async Task Alta_de_legajo_audita_y_publica_un_evento_sin_cuil()
    {
        var (sector, puesto) = await SectorYPuestoAsync();
        var cuil = Cuil();
        var r = await CrearEmpleadoAsync(sector, puesto, cuil);

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("\"1\"", r.Headers.ETag?.Tag);
        var e = (await r.Content.ReadFromJsonAsync<EmpleadoDto>())!;
        Assert.True(e.Activo);
        Assert.Equal(cuil, e.Cuil);

        Assert.Equal(1, await bd.EscalarAsync<long>(
            $"SELECT count(*) FROM auditoria.eventos WHERE recurso_id = '{e.Id}' AND accion = 'CREAR'"));
        var datos = await bd.EscalarAsync<string>(
            $"SELECT datos::text FROM integracion.bandeja_salida WHERE agregado_id = '{e.Id}' AND tipo = 'rrhh.empleado_alta'");
        Assert.NotNull(datos);
        Assert.DoesNotContain(cuil, datos);
        Assert.DoesNotContain("fechaNacimiento", datos);
    }

    [Fact]
    public async Task Rechaza_un_cuil_con_digito_verificador_incorrecto()
    {
        var (sector, puesto) = await SectorYPuestoAsync();
        var cuil = Cuil();
        var malo = cuil[..10] + (char)('0' + (cuil[10] - '0' + 1) % 10);

        var r = await CrearEmpleadoAsync(sector, puesto, malo);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Una_persona_tiene_un_solo_legajo_por_empresa()
    {
        var (sector, puesto) = await SectorYPuestoAsync();
        var cuil = Cuil();
        Assert.Equal(HttpStatusCode.Created, (await CrearEmpleadoAsync(sector, puesto, cuil)).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await CrearEmpleadoAsync(sector, puesto, cuil)).StatusCode);
    }

    [Fact]
    public async Task Un_profesional_de_catalogos_no_se_duplica_en_dos_legajos()
    {
        var (sector, puesto) = await SectorYPuestoAsync();
        // La semilla ya enlaza a la Dra. Local con el legajo 4.
        var r = await CrearEmpleadoAsync(sector, puesto, Cuil(), profesionalId: Guid.Parse(ProfesionalSemilla));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);

        // Un profesional nuevo sí se puede enlazar, una vez.
        var prof = await CrearAsync<ProfesionalDto>(bd.Cliente(Admin, BaseDePrueba.Empresa1), "/api/v1/catalogos/profesionales",
            new { apellidos = "Médica", nombres = "De Prueba", matriculaProvincial = Random.Shared.Next(1000, 99999).ToString() });
        Assert.Equal(HttpStatusCode.Created, (await CrearEmpleadoAsync(sector, puesto, Cuil(), profesionalId: prof.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await CrearEmpleadoAsync(sector, puesto, Cuil(), profesionalId: prof.Id)).StatusCode);
    }

    [Fact]
    public async Task Sin_rol_rrhh_o_administrador_no_se_ven_legajos()
    {
        var admision = bd.Cliente(Admision, BaseDePrueba.Empresa1);
        Assert.Equal(HttpStatusCode.Forbidden, (await admision.GetAsync("/api/v1/rrhh/empleados")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admision.GetAsync("/api/v1/rrhh/empleados/0192f000-0000-7000-8000-0000000000e1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admision.GetAsync("/api/v1/rrhh/vencimientos")).StatusCode);

        // El administrador sí.
        var admin = bd.Cliente(Admin, BaseDePrueba.Empresa1);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/rrhh/empleados")).StatusCode);
    }

    [Fact]
    public async Task Otra_empresa_no_ve_los_legajos()
    {
        var empresa2 = bd.Cliente(Admin, BaseDePrueba.Empresa2);
        var r = await empresa2.GetAsync("/api/v1/rrhh/empleados/0192f000-0000-7000-8000-0000000000e1");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Empty((await empresa2.GetFromJsonAsync<List<EmpleadoResumenDto>>("/api/v1/rrhh/empleados"))!);
    }

    [Fact]
    public async Task Los_datos_de_ejemplo_estan_cargados_y_el_listado_no_expone_el_cuil()
    {
        var lista = (await _rrhh.GetFromJsonAsync<List<EmpleadoResumenDto>>("/api/v1/rrhh/empleados?texto=Persona"))!;
        Assert.Contains(lista, e => e.Legajo == 1 && e.Sector == "Enfermería");

        var crudo = await _rrhh.GetStringAsync("/api/v1/rrhh/empleados?texto=Persona");
        Assert.DoesNotContain("20990000019", crudo);
    }

    [Fact]
    public async Task Leer_un_legajo_queda_en_la_auditoria()
    {
        var antes = await bd.EscalarAsync<long>(
            "SELECT count(*) FROM auditoria.eventos WHERE recurso_id = '0192f000-0000-7000-8000-0000000000e3' AND accion = 'LEER'");
        var r = await _rrhh.GetAsync("/api/v1/rrhh/empleados/0192f000-0000-7000-8000-0000000000e3");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(antes + 1, await bd.EscalarAsync<long>(
            "SELECT count(*) FROM auditoria.eventos WHERE recurso_id = '0192f000-0000-7000-8000-0000000000e3' AND accion = 'LEER'"));
    }

    [Fact]
    public async Task Modificar_exige_la_version_vigente()
    {
        var (sector, puesto) = await SectorYPuestoAsync();
        var e = (await (await CrearEmpleadoAsync(sector, puesto, Cuil())).Content.ReadFromJsonAsync<EmpleadoDto>())!;
        var cambio = new
        {
            apellidos = e.Apellidos, nombres = e.Nombres, fechaNacimiento = "1990-05-05", sexoRegistral = "F",
            modalidad = "RELACION_DEPENDENCIA", categoria = "Jefa de turno", horasSemanales = 40,
            sectorId = sector, puestoId = puesto, fechaIngreso = "2020-01-01",
        };

        var sinVersion = await _rrhh.PutAsJsonAsync($"/api/v1/rrhh/empleados/{e.Id}", cambio);
        Assert.Equal((HttpStatusCode)428, sinVersion.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PutConVersionAsync($"/api/v1/rrhh/empleados/{e.Id}", cambio, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutConVersionAsync($"/api/v1/rrhh/empleados/{e.Id}", cambio, 1)).StatusCode);

        var actual = (await _rrhh.GetFromJsonAsync<EmpleadoDto>($"/api/v1/rrhh/empleados/{e.Id}"))!;
        Assert.Equal("Jefa de turno", actual.Categoria);
        Assert.Equal(2, actual.Version);
    }

    [Fact]
    public async Task No_se_superponen_licencias_pedidas_o_aprobadas()
    {
        var e = await EmpleadoAsync();
        var primera = await LicenciaAsync(e.Id, "2026-12-01", "2026-12-14");
        Assert.Equal(HttpStatusCode.Created, primera.StatusCode);
        var lic = (await primera.Content.ReadFromJsonAsync<LicenciaDto>())!;
        Assert.Equal(14, lic.Dias);

        Assert.Equal(HttpStatusCode.Conflict, (await LicenciaAsync(e.Id, "2026-12-10", "2026-12-20")).StatusCode);

        // Rechazada, deja libres las fechas.
        var rechazo = await PostConVersionAsync($"/api/v1/rrhh/licencias/{lic.Id}/resolucion",
            new { aprobar = false, motivoRechazo = "Período de alta demanda" }, lic.Version);
        Assert.Equal(HttpStatusCode.OK, rechazo.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await LicenciaAsync(e.Id, "2026-12-10", "2026-12-20")).StatusCode);
    }

    [Fact]
    public async Task Rechazar_sin_motivo_no_se_permite()
    {
        var e = await EmpleadoAsync();
        var lic = (await (await LicenciaAsync(e.Id, "2027-01-05", "2027-01-06")).Content.ReadFromJsonAsync<LicenciaDto>())!;
        var r = await PostConVersionAsync($"/api/v1/rrhh/licencias/{lic.Id}/resolucion", new { aprobar = false }, lic.Version);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Aprobar_la_licencia_de_un_profesional_avisa_a_turnos_sin_el_tipo()
    {
        var (sector, puesto) = await SectorYPuestoAsync();
        var prof = await CrearAsync<ProfesionalDto>(bd.Cliente(Admin, BaseDePrueba.Empresa1), "/api/v1/catalogos/profesionales",
            new { apellidos = "Con", nombres = "Licencia", matriculaProvincial = Random.Shared.Next(1000, 99999).ToString() });
        var e = (await (await CrearEmpleadoAsync(sector, puesto, Cuil(), profesionalId: prof.Id)).Content.ReadFromJsonAsync<EmpleadoDto>())!;
        var lic = (await (await LicenciaAsync(e.Id, "2026-12-01", "2026-12-03", "ENFERMEDAD")).Content.ReadFromJsonAsync<LicenciaDto>())!;

        var r = await PostConVersionAsync($"/api/v1/rrhh/licencias/{lic.Id}/resolucion", new { aprobar = true }, lic.Version);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var datos = await bd.EscalarAsync<string>(
            $"SELECT datos::text FROM integracion.bandeja_salida WHERE agregado_id = '{lic.Id}' AND tipo = 'rrhh.licencia_aprobada'");
        Assert.NotNull(datos);
        Assert.Contains(prof.Id.ToString(), datos);
        Assert.DoesNotContain("ENFERMEDAD", datos);
    }

    [Fact]
    public async Task El_egreso_anula_licencias_posteriores_y_saca_del_listado_y_el_reingreso_reactiva()
    {
        var e = await EmpleadoAsync();
        var lic = (await (await LicenciaAsync(e.Id, "2027-03-01", "2027-03-10")).Content.ReadFromJsonAsync<LicenciaDto>())!;

        var egreso = await _rrhh.PostAsJsonAsync($"/api/v1/rrhh/empleados/{e.Id}/egreso", new { fecha = "2026-01-31", motivo = "Renuncia" });
        Assert.Equal(HttpStatusCode.OK, egreso.StatusCode);
        Assert.False((await egreso.Content.ReadFromJsonAsync<EmpleadoDto>())!.Activo);
        Assert.Equal("ANULADA", await bd.EscalarAsync<string>($"SELECT estado FROM rrhh.licencias WHERE id = '{lic.Id}'"));

        var activos = (await _rrhh.GetFromJsonAsync<List<EmpleadoResumenDto>>($"/api/v1/rrhh/empleados?texto={e.Legajo}"))!;
        Assert.DoesNotContain(activos, x => x.Id == e.Id);
        var todos = (await _rrhh.GetFromJsonAsync<List<EmpleadoResumenDto>>($"/api/v1/rrhh/empleados?texto={e.Legajo}&incluirEgresados=true"))!;
        Assert.Contains(todos, x => x.Id == e.Id);

        Assert.Equal(HttpStatusCode.Conflict,
            (await _rrhh.PostAsJsonAsync($"/api/v1/rrhh/empleados/{e.Id}/egreso", new { fecha = "2026-02-01", motivo = "Otra" })).StatusCode);

        var reingreso = await _rrhh.PostAsJsonAsync($"/api/v1/rrhh/empleados/{e.Id}/reingreso", new { fecha = "2026-06-01" });
        Assert.Equal(HttpStatusCode.OK, reingreso.StatusCode);
        var actual = (await reingreso.Content.ReadFromJsonAsync<EmpleadoDto>())!;
        Assert.True(actual.Activo);
        Assert.Equal(new DateOnly(2026, 6, 1), actual.FechaIngreso);
    }

    [Fact]
    public async Task Vencimientos_muestra_lo_vencido_y_un_documento_renovado_tapa_al_anterior()
    {
        var e = await EmpleadoAsync();
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3));
        await DocumentoAsync(e.Id, "APTO_MEDICO", hoy.AddDays(-10));
        await DocumentoAsync(e.Id, "SEGURO_MALA_PRAXIS", hoy.AddDays(15));
        await DocumentoAsync(e.Id, "MATRICULA", hoy.AddDays(200));

        var lista = (await _rrhh.GetFromJsonAsync<List<VencimientoDto>>("/api/v1/rrhh/vencimientos?dias=30"))!
            .Where(v => v.EmpleadoId == e.Id).ToList();
        Assert.Equal(["APTO_MEDICO", "SEGURO_MALA_PRAXIS"], lista.Select(v => v.Tipo));
        Assert.True(lista[0].DiasRestantes < 0);

        // Renovado: el apto vencido deja de aparecer.
        await DocumentoAsync(e.Id, "APTO_MEDICO", hoy.AddDays(365));
        lista = (await _rrhh.GetFromJsonAsync<List<VencimientoDto>>("/api/v1/rrhh/vencimientos?dias=30"))!
            .Where(v => v.EmpleadoId == e.Id).ToList();
        Assert.Equal(["SEGURO_MALA_PRAXIS"], lista.Select(v => v.Tipo));

        Assert.Equal(4, (await _rrhh.GetFromJsonAsync<List<DocumentoDto>>($"/api/v1/rrhh/empleados/{e.Id}/documentos"))!.Count);
    }

    [Theory]
    [InlineData("20990000019", true)]
    [InlineData("27990000021", true)]
    [InlineData("20990000018", false)]
    [InlineData("2799000003", false)]
    [InlineData("2099000001A", false)]
    public void Valida_el_digito_verificador_del_cuil(string cuil, bool valido) =>
        Assert.Equal(valido, Validacion.CuilValido(cuil));

    // ------------------------------------------------------------------

    private async Task<(Guid Sector, Guid Puesto)> SectorYPuestoAsync()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var sector = await CrearAsync<SectorPuestoDto>(_rrhh, "/api/v1/rrhh/sectores", new { nombre = $"Sector {sufijo}" });
        var puesto = await CrearAsync<SectorPuestoDto>(_rrhh, "/api/v1/rrhh/puestos", new { nombre = $"Puesto {sufijo}" });
        return (sector.Id, puesto.Id);
    }

    private Task<HttpResponseMessage> CrearEmpleadoAsync(Guid sector, Guid puesto, string cuil, Guid? profesionalId = null) =>
        _rrhh.PostAsJsonAsync("/api/v1/rrhh/empleados", new
        {
            legajo = Interlocked.Increment(ref _legajo), apellidos = "Prueba", nombres = "Persona", cuil,
            fechaNacimiento = "1990-05-05", sexoRegistral = "F", modalidad = "RELACION_DEPENDENCIA", convenio = "CCT 122/75",
            horasSemanales = 36, sectorId = sector, puestoId = puesto, fechaIngreso = "2020-01-01", profesionalId,
        });

    private async Task<EmpleadoDto> EmpleadoAsync()
    {
        var (sector, puesto) = await SectorYPuestoAsync();
        var r = await CrearEmpleadoAsync(sector, puesto, Cuil());
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<EmpleadoDto>())!;
    }

    private Task<HttpResponseMessage> LicenciaAsync(Guid empleadoId, string desde, string hasta, string tipo = "VACACIONES") =>
        _rrhh.PostAsJsonAsync($"/api/v1/rrhh/empleados/{empleadoId}/licencias", new { tipo, desde, hasta });

    private async Task DocumentoAsync(Guid empleadoId, string tipo, DateOnly vence)
    {
        var r = await _rrhh.PostAsJsonAsync($"/api/v1/rrhh/empleados/{empleadoId}/documentos", new
        {
            tipo, descripcion = $"{tipo} de prueba", vence = vence.ToString("yyyy-MM-dd"),
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
    }

    private Task<HttpResponseMessage> PutConVersionAsync(string ruta, object cuerpo, int version) =>
        EnviarConVersionAsync(HttpMethod.Put, ruta, cuerpo, version);

    private Task<HttpResponseMessage> PostConVersionAsync(string ruta, object cuerpo, int version) =>
        EnviarConVersionAsync(HttpMethod.Post, ruta, cuerpo, version);

    private Task<HttpResponseMessage> EnviarConVersionAsync(HttpMethod metodo, string ruta, object cuerpo, int version)
    {
        var m = new HttpRequestMessage(metodo, ruta) { Content = JsonContent.Create(cuerpo) };
        m.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return _rrhh.SendAsync(m);
    }

    private static async Task<T> CrearAsync<T>(HttpClient c, string ruta, object cuerpo)
    {
        var r = await c.PostAsJsonAsync(ruta, cuerpo);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>CUIL ficticio con dígito verificador válido (DNI de la serie 98.000.000, no asignada).</summary>
    private static string Cuil()
    {
        while (true)
        {
            var baseCuil = $"2098{Random.Shared.Next(0, 1_000_000):D6}";
            int[] pesos = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
            var dv = 11 - baseCuil.Select((c, i) => (c - '0') * pesos[i]).Sum() % 11;
            if (dv == 11)
                dv = 0;
            if (dv != 10)
                return baseCuil + dv;
        }
    }
}
