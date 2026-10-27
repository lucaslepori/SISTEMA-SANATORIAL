using System.Net;
using System.Net.Http.Json;
using SistemaGestion.Catalogos;
using SistemaGestion.Pacientes;
using SistemaGestion.Turnos;

namespace SistemaGestion.Tests;

/// <summary>Arma por API un escenario propio de cada prueba: catálogos, un profesional y su agenda.</summary>
internal sealed class Escenario(BaseDePrueba bd)
{
    public const string Admision = "admision.local";
    public const string Medica = "medica.local";
    public const string Admin = "admin.local";

    public HttpClient ComoAdmin { get; } = bd.Cliente(Admin, BaseDePrueba.Empresa1);
    public HttpClient ComoAdmision { get; } = bd.Cliente(Admision, BaseDePrueba.Empresa1);
    public HttpClient ComoMedica { get; } = bd.Cliente(Medica, BaseDePrueba.Empresa1);

    public Guid ServicioId { get; private set; }
    public Guid ConsultorioId { get; private set; }
    public Guid CoberturaId { get; private set; }

    /// <summary>Un lunes a más de una semana de hoy, para que todos sus horarios estén en el futuro.</summary>
    public static DateOnly Lunes { get; } = ProximoLunes();

    public static string Horario(DateOnly dia, string hora) => $"{dia:yyyy-MM-dd}T{hora}:00-03:00";

    public async Task<Escenario> PrepararAsync()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        ServicioId = (await Crear<NombradoDto>("/api/v1/catalogos/servicios", new { nombre = $"Clínica médica {sufijo}" })).Id;
        var sede = await Crear<SedeDto>("/api/v1/catalogos/sedes", new { nombre = $"Sede {sufijo}", direccion = "Av. Siempre Viva 123" });
        ConsultorioId = (await Crear<ConsultorioDto>("/api/v1/catalogos/consultorios", new { sedeId = sede.Id, nombre = "Consultorio 1" })).Id;
        CoberturaId = (await Crear<CoberturaDto>("/api/v1/catalogos/coberturas", new { nombre = $"OSDE {sufijo}", tipo = "PREPAGA" })).Id;
        return this;
    }

    public async Task<Guid> ProfesionalAsync() =>
        (await Crear<ProfesionalDto>("/api/v1/catalogos/profesionales", new
        {
            apellidos = "Gómez", nombres = "Laura", matriculaProvincial = Random.Shared.Next(1000, 99999).ToString(),
        })).Id;

    public async Task<AgendaDto> AgendaAsync(
        Guid profesionalId, int capacidad = 1, int sobreturnos = 0, string gestion = "SISTEMA_NUEVO", Guid[]? coberturas = null)
    {
        var r = await ComoAdmin.PostAsJsonAsync("/api/v1/agendas", new
        {
            profesionalId, servicioId = ServicioId, consultorioId = ConsultorioId,
            diaSemana = 1, horaDesde = "09:00", horaHasta = "12:00", duracionMinutos = 30,
            vigenteDesde = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            capacidad, sobreturnos, gestionadaPor = gestion, coberturaIds = coberturas,
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<AgendaDto>())!;
    }

    public async Task<Guid> PacienteAsync()
    {
        var r = await ComoAdmision.PostAsJsonAsync("/api/v1/pacientes", new
        {
            apellidos = "Paciente", nombres = "De Prueba", fechaNacimiento = "1990-01-01", sexoRegistral = "X",
            identificadores = new[] { new { sistema = "DNI", valor = Random.Shared.Next(10_000_000, 99_999_999).ToString() } },
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<PacienteDto>())!.Id;
    }

    public Task<HttpResponseMessage> AsignarAsync(
        Guid agendaId, Guid pacienteId, string hora = "09:00", bool sobreturno = false, Guid? coberturaId = null, HttpClient? como = null) =>
        (como ?? ComoAdmision).PostAsJsonAsync("/api/v1/turnos", new
        {
            agendaId, pacienteId, inicio = Horario(Lunes, hora), sobreturno, coberturaId,
        });

    public async Task<TurnoDto> AsignarOkAsync(Guid agendaId, Guid pacienteId, string hora = "09:00")
    {
        var r = await AsignarAsync(agendaId, pacienteId, hora);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<TurnoDto>())!;
    }

    public async Task<List<HorarioDisponibleDto>> DisponibilidadAsync(Guid profesionalId) =>
        (await ComoAdmision.GetFromJsonAsync<List<HorarioDisponibleDto>>(
            $"/api/v1/turnos/disponibilidad?profesionalId={profesionalId}&desde={Lunes:yyyy-MM-dd}&hasta={Lunes:yyyy-MM-dd}"))!;

    private async Task<T> Crear<T>(string ruta, object cuerpo)
    {
        var r = await ComoAdmin.PostAsJsonAsync(ruta, cuerpo);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static DateOnly ProximoLunes()
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var hastaLunes = ((int)DayOfWeek.Monday - (int)hoy.DayOfWeek + 7) % 7;
        return hoy.AddDays(hastaLunes + 7);
    }
}
