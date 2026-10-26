using System.Net;
using System.Net.Http.Json;
using SistemaGestion.Turnos;

namespace SistemaGestion.Tests;

[Collection(ColeccionBase.Nombre)]
public sealed class TurnosApiTests(BaseDePrueba bd)
{
    private Task<Escenario> EscenarioAsync() => new Escenario(bd).PrepararAsync();

    [Fact]
    public async Task La_disponibilidad_muestra_los_horarios_de_la_agenda()
    {
        var e = await EscenarioAsync();
        var prof = await e.ProfesionalAsync();
        await e.AgendaAsync(prof);

        var horarios = await e.DisponibilidadAsync(prof);

        // 09:00 a 12:00 cada 30 minutos.
        Assert.Equal(6, horarios.Count);
        Assert.Equal(DateTimeOffset.Parse(Escenario.Horario(Escenario.Lunes, "09:00")), horarios[0].Inicio);
        Assert.Equal(DateTimeOffset.Parse(Escenario.Horario(Escenario.Lunes, "11:30")), horarios[^1].Inicio);
        Assert.All(horarios, h => Assert.Equal(1, h.Libres));
    }

    [Fact]
    public async Task Asignar_ocupa_el_horario_y_genera_evento_y_auditoria()
    {
        var e = await EscenarioAsync();
        var prof = await e.ProfesionalAsync();
        var agenda = await e.AgendaAsync(prof);
        var turno = await e.AsignarOkAsync(agenda.Id, await e.PacienteAsync());

        Assert.Equal("RESERVADO", turno.Estado);
        Assert.Equal(DateTimeOffset.Parse(Escenario.Horario(Escenario.Lunes, "09:30")), turno.Fin);
        Assert.DoesNotContain(await e.DisponibilidadAsync(prof), h => h.Inicio == turno.Inicio);

        Assert.Equal(1, await bd.EscalarAsync<long>(
            $"SELECT count(*) FROM integracion.bandeja_salida WHERE agregado_id = '{turno.Id}' AND tipo = 'turnos.turno_asignado'"));
        Assert.Equal(1, await bd.EscalarAsync<long>(
            $"SELECT count(*) FROM auditoria.eventos WHERE recurso_id = '{turno.Id}' AND accion = 'CREAR'"));
    }

    [Fact]
    public async Task No_se_puede_dar_dos_veces_el_mismo_horario()
    {
        var e = await EscenarioAsync();
        var agenda = await e.AgendaAsync(await e.ProfesionalAsync());
        await e.AsignarOkAsync(agenda.Id, await e.PacienteAsync());

        var r = await e.AsignarAsync(agenda.Id, await e.PacienteAsync());
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Con_pedidos_simultaneos_solo_uno_obtiene_el_horario()
    {
        var e = await EscenarioAsync();
        var agenda = await e.AgendaAsync(await e.ProfesionalAsync());
        var pacientes = new List<Guid>();
        for (var i = 0; i < 10; i++)
            pacientes.Add(await e.PacienteAsync());

        // Diez puestos de admisión piden el mismo horario a la vez.
        var respuestas = await Task.WhenAll(pacientes.Select(p => e.AsignarAsync(agenda.Id, p, como: bd.Cliente(Escenario.Admision, BaseDePrueba.Empresa1))));

        Assert.Equal(1, respuestas.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(9, respuestas.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await bd.EscalarAsync<long>(
            $"SELECT count(*) FROM turnos.turnos WHERE agenda_id = '{agenda.Id}' AND estado <> 'CANCELADO'"));
    }

    [Fact]
    public async Task Con_pedidos_simultaneos_se_respeta_la_capacidad()
    {
        var e = await EscenarioAsync();
        var agenda = await e.AgendaAsync(await e.ProfesionalAsync(), capacidad: 3);
        var pacientes = new List<Guid>();
        for (var i = 0; i < 8; i++)
            pacientes.Add(await e.PacienteAsync());

        var respuestas = await Task.WhenAll(pacientes.Select(p => e.AsignarAsync(agenda.Id, p, como: bd.Cliente(Escenario.Admision, BaseDePrueba.Empresa1))));

        Assert.Equal(3, respuestas.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, respuestas.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Los_sobreturnos_tienen_su_propio_limite()
    {
        var e = await EscenarioAsync();
        var prof = await e.ProfesionalAsync();
        var agenda = await e.AgendaAsync(prof, sobreturnos: 1);
        await e.AsignarOkAsync(agenda.Id, await e.PacienteAsync());

        Assert.Equal(HttpStatusCode.Created, (await e.AsignarAsync(agenda.Id, await e.PacienteAsync(), sobreturno: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await e.AsignarAsync(agenda.Id, await e.PacienteAsync(), sobreturno: true)).StatusCode);
        Assert.DoesNotContain(await e.DisponibilidadAsync(prof), h => h.Inicio == DateTimeOffset.Parse(Escenario.Horario(Escenario.Lunes, "09:00")));
    }

    [Fact]
    public async Task Sin_sobreturnos_configurados_se_rechaza_el_pedido()
    {
        var e = await EscenarioAsync();
        var agenda = await e.AgendaAsync(await e.ProfesionalAsync());
        var r = await e.AsignarAsync(agenda.Id, await e.PacienteAsync(), sobreturno: true);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Un_paciente_no_puede_tener_dos_turnos_al_mismo_tiempo()
    {
        var e = await EscenarioAsync();
        var a1 = await e.AgendaAsync(await e.ProfesionalAsync());
        var a2 = await e.AgendaAsync(await e.ProfesionalAsync());
        var paciente = await e.PacienteAsync();
        await e.AsignarOkAsync(a1.Id, paciente);

        var r = await e.AsignarAsync(a2.Id, paciente);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("otro turno", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Un_horario_fuera_de_la_grilla_se_rechaza()
    {
        var e = await EscenarioAsync();
        var agenda = await e.AgendaAsync(await e.ProfesionalAsync());
        var p = await e.PacienteAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await e.AsignarAsync(agenda.Id, p, "09:10")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await e.AsignarAsync(agenda.Id, p, "12:00")).StatusCode);
    }

    [Fact]
    public async Task Una_agenda_de_Geclisa_no_da_turnos_hasta_que_se_pasa_al_sistema_nuevo()
    {
        var e = await EscenarioAsync();
        var prof = await e.ProfesionalAsync();
        var agenda = await e.AgendaAsync(prof, gestion: "GECLISA");
        var paciente = await e.PacienteAsync();

        Assert.Empty(await e.DisponibilidadAsync(prof));
        Assert.Equal(HttpStatusCode.Conflict, (await e.AsignarAsync(agenda.Id, paciente)).StatusCode);

        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/agendas/{agenda.Id}/gestion")
        {
            Content = JsonContent.Create(new { gestionadaPor = "SISTEMA_NUEVO" }),
        };
        req.Headers.TryAddWithoutValidation("If-Match", $"\"{agenda.Version}\"");
        Assert.Equal(HttpStatusCode.OK, (await e.ComoAdmin.SendAsync(req)).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await e.AsignarAsync(agenda.Id, paciente)).StatusCode);
    }

    [Fact]
    public async Task Las_agendas_superpuestas_del_mismo_profesional_se_rechazan()
    {
        var e = await EscenarioAsync();
        var prof = await e.ProfesionalAsync();
        await e.AgendaAsync(prof);
        var r = await e.ComoAdmin.PostAsJsonAsync("/api/v1/agendas", new
        {
            profesionalId = prof, servicioId = e.ServicioId, consultorioId = e.ConsultorioId,
            diaSemana = 1, horaDesde = "11:00", horaHasta = "13:00", duracionMinutos = 20,
            vigenteDesde = Escenario.Lunes.ToString("yyyy-MM-dd"),
        });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Un_bloqueo_saca_los_horarios_e_informa_los_turnos_afectados()
    {
        var e = await EscenarioAsync();
        var prof = await e.ProfesionalAsync();
        var agenda = await e.AgendaAsync(prof);
        var turno = await e.AsignarOkAsync(agenda.Id, await e.PacienteAsync(), "10:00");

        var r = await e.ComoAdmin.PostAsJsonAsync("/api/v1/bloqueos", new
        {
            profesionalId = prof, desde = Escenario.Horario(Escenario.Lunes, "10:00"),
            hasta = Escenario.Horario(Escenario.Lunes, "11:00"), motivo = "Licencia",
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var bloqueo = (await r.Content.ReadFromJsonAsync<BloqueoDto>())!;
        Assert.Equal([turno.Id], bloqueo.TurnosAfectados);

        var horarios = await e.DisponibilidadAsync(prof);
        Assert.Equal(4, horarios.Count); // 09:00, 09:30, 11:00, 11:30
        Assert.Equal(HttpStatusCode.Conflict, (await e.AsignarAsync(agenda.Id, await e.PacienteAsync(), "10:30")).StatusCode);
    }

    [Fact]
    public async Task Cancelar_exige_motivo_libera_el_horario_y_no_borra_el_turno()
    {
        var e = await EscenarioAsync();
        var prof = await e.ProfesionalAsync();
        var agenda = await e.AgendaAsync(prof);
        var turno = await e.AsignarOkAsync(agenda.Id, await e.PacienteAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(e.ComoAdmision, $"/api/v1/turnos/{turno.Id}/cancelacion", 1, new { motivo = "" })).StatusCode);
        var cancelado = await Post(e.ComoAdmision, $"/api/v1/turnos/{turno.Id}/cancelacion", 1, new { motivo = "Pedido del paciente" });
        Assert.Equal(HttpStatusCode.OK, cancelado.StatusCode);
        Assert.Equal("CANCELADO", (await cancelado.Content.ReadFromJsonAsync<TurnoDto>())!.Estado);

        Assert.Contains(await e.DisponibilidadAsync(prof), h => h.Inicio == turno.Inicio);
        Assert.Equal(HttpStatusCode.Created, (await e.AsignarAsync(agenda.Id, await e.PacienteAsync())).StatusCode);
        Assert.Equal("Pedido del paciente", await bd.EscalarAsync<string>(
            $"SELECT motivo_cancelacion FROM turnos.turnos WHERE id = '{turno.Id}'"));
    }

    [Fact]
    public async Task Los_estados_siguen_el_circuito_de_atencion()
    {
        var e = await EscenarioAsync();
        var agenda = await e.AgendaAsync(await e.ProfesionalAsync());
        var turno = await e.AsignarOkAsync(agenda.Id, await e.PacienteAsync());
        var ruta = $"/api/v1/turnos/{turno.Id}/estado";

        Assert.Equal(HttpStatusCode.OK, (await Post(e.ComoAdmision, ruta, 1, new { estado = "PRESENTE" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(e.ComoAdmision, ruta, 2, new { estado = "ATENDIDO" })).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Post(e.ComoMedica, ruta, 1, new { estado = "ATENDIDO" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(e.ComoMedica, ruta, 2, new { estado = "ATENDIDO" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(e.ComoAdmision, ruta, 3, new { estado = "AUSENTE" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(e.ComoAdmision, $"/api/v1/turnos/{turno.Id}/cancelacion", 3, new { motivo = "x" })).StatusCode);
    }

    [Fact]
    public async Task Una_agenda_con_coberturas_rechaza_las_demas()
    {
        var e = await EscenarioAsync();
        var agenda = await e.AgendaAsync(await e.ProfesionalAsync(), coberturas: [e.CoberturaId]);
        var p = await e.PacienteAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await e.AsignarAsync(agenda.Id, p)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await e.AsignarAsync(agenda.Id, p, coberturaId: e.CoberturaId)).StatusCode);
    }

    [Fact]
    public async Task La_agenda_del_dia_y_los_turnos_del_paciente_se_pueden_consultar()
    {
        var e = await EscenarioAsync();
        var prof = await e.ProfesionalAsync();
        var agenda = await e.AgendaAsync(prof);
        var paciente = await e.PacienteAsync();
        var t1 = await e.AsignarOkAsync(agenda.Id, paciente, "09:00");
        await e.AsignarOkAsync(agenda.Id, await e.PacienteAsync(), "09:30");

        var delDia = await e.ComoMedica.GetFromJsonAsync<List<TurnoDto>>($"/api/v1/turnos?profesionalId={prof}&fecha={Escenario.Lunes:yyyy-MM-dd}");
        Assert.Equal(2, delDia!.Count);
        var delPaciente = await e.ComoAdmision.GetFromJsonAsync<List<TurnoDto>>($"/api/v1/turnos?pacienteId={paciente}");
        Assert.Equal(t1.Id, Assert.Single(delPaciente!).Id);
        var desdeDespues = await e.ComoAdmision.GetFromJsonAsync<List<TurnoDto>>(
            $"/api/v1/turnos?pacienteId={paciente}&desde={Escenario.Lunes.AddDays(1):yyyy-MM-dd}");
        Assert.Empty(desdeDespues!);
    }

    [Fact]
    public async Task Otra_empresa_no_ve_los_turnos()
    {
        var e = await EscenarioAsync();
        var agenda = await e.AgendaAsync(await e.ProfesionalAsync());
        var turno = await e.AsignarOkAsync(agenda.Id, await e.PacienteAsync());

        var otra = bd.Cliente(Escenario.Admin, BaseDePrueba.Empresa2);
        Assert.Equal(HttpStatusCode.NotFound, (await otra.GetAsync($"/api/v1/turnos/{turno.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await otra.PostAsJsonAsync("/api/v1/turnos", new
        {
            agendaId = agenda.Id, pacienteId = Guid.NewGuid(), inicio = Escenario.Horario(Escenario.Lunes, "10:00"),
        })).StatusCode);
    }

    [Fact]
    public async Task Solo_el_administrador_define_agendas()
    {
        var e = await EscenarioAsync();
        var r = await e.ComoAdmision.PostAsJsonAsync("/api/v1/agendas", new
        {
            profesionalId = await e.ProfesionalAsync(), servicioId = e.ServicioId, consultorioId = e.ConsultorioId,
            diaSemana = 2, horaDesde = "09:00", horaHasta = "10:00", duracionMinutos = 15, vigenteDesde = "2026-01-01",
        });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    private static Task<HttpResponseMessage> Post(HttpClient c, string ruta, int version, object cuerpo)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, ruta) { Content = JsonContent.Create(cuerpo) };
        req.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return c.SendAsync(req);
    }
}
