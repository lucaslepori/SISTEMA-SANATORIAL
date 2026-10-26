namespace SistemaGestion.Turnos;

// Contratos públicos de la API v1 de turnos. Las horas de agenda son locales de la empresa
// ("HH:mm"); los horarios de turnos viajan con su desplazamiento (ISO 8601, por ejemplo 2026-11-03T09:00:00-03:00).

public sealed record CrearAgendaSolicitud(
    Guid ProfesionalId,
    Guid ServicioId,
    Guid ConsultorioId,
    int DiaSemana,
    TimeOnly HoraDesde,
    TimeOnly HoraHasta,
    int DuracionMinutos,
    DateOnly VigenteDesde,
    DateOnly? VigenteHasta,
    int? Capacidad,
    int? Sobreturnos,
    IReadOnlyList<Guid>? CoberturaIds,
    string? GestionadaPor,
    int? GeclisaId);

public sealed record AgendaDto(
    Guid Id,
    Guid ProfesionalId,
    Guid ServicioId,
    Guid ConsultorioId,
    int DiaSemana,
    TimeOnly HoraDesde,
    TimeOnly HoraHasta,
    int DuracionMinutos,
    DateOnly VigenteDesde,
    DateOnly? VigenteHasta,
    int Capacidad,
    int Sobreturnos,
    string GestionadaPor,
    bool Activa,
    int Version,
    IReadOnlyList<Guid> CoberturaIds);

public sealed record CambiarGestionSolicitud(string GestionadaPor);

public sealed record CrearBloqueoSolicitud(Guid? ProfesionalId, DateTimeOffset Desde, DateTimeOffset Hasta, string Motivo);

public sealed record BloqueoDto(Guid Id, Guid? ProfesionalId, DateTimeOffset Desde, DateTimeOffset Hasta, string Motivo,
    IReadOnlyList<Guid> TurnosAfectados);

public sealed record HorarioDisponibleDto(
    Guid AgendaId,
    Guid ProfesionalId,
    Guid ServicioId,
    Guid ConsultorioId,
    DateTimeOffset Inicio,
    DateTimeOffset Fin,
    int Libres,
    int SobreturnosLibres);

public sealed record AsignarTurnoSolicitud(
    Guid AgendaId,
    Guid PacienteId,
    DateTimeOffset Inicio,
    bool? Sobreturno,
    Guid? CoberturaId,
    Guid? PlanId,
    string? NumeroAfiliado,
    string? Observaciones,
    string? Origen);

public sealed record CancelarTurnoSolicitud(string Motivo);

public sealed record CambiarEstadoSolicitud(string Estado);

public sealed record TurnoDto(
    Guid Id,
    Guid AgendaId,
    Guid ProfesionalId,
    Guid ServicioId,
    Guid ConsultorioId,
    Guid PacienteId,
    DateTimeOffset Inicio,
    DateTimeOffset Fin,
    bool EsSobreturno,
    string Estado,
    Guid? CoberturaId,
    Guid? PlanId,
    string? NumeroAfiliado,
    string? Observaciones,
    string Origen,
    string? MotivoCancelacion,
    int Version);

public static class EstadosTurno
{
    public const string Reservado = "RESERVADO";
    public const string Confirmado = "CONFIRMADO";
    public const string Presente = "PRESENTE";
    public const string Atendido = "ATENDIDO";
    public const string Ausente = "AUSENTE";
    public const string Cancelado = "CANCELADO";

    /// <summary>Cambios de estado permitidos. La cancelación tiene su propio endpoint porque exige motivo.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Transiciones = new Dictionary<string, string[]>
    {
        [Reservado] = [Confirmado, Presente, Ausente],
        [Confirmado] = [Presente, Ausente],
        [Presente] = [Atendido],
        [Atendido] = [],
        [Ausente] = [],
        [Cancelado] = [],
    };
}

public static class GestionAgenda
{
    public const string SistemaNuevo = "SISTEMA_NUEVO";
    public const string Geclisa = "GECLISA";
    public static readonly IReadOnlySet<string> Todos = new HashSet<string> { SistemaNuevo, Geclisa };
}

public static class EventosTurnos
{
    public const string Agregado = "turno";
    public const string Asignado = "turnos.turno_asignado";
    public const string Cancelado = "turnos.turno_cancelado";
    public const string EstadoCambiado = "turnos.turno_estado_cambiado";
    public const string AgendaGestionCambiada = "turnos.agenda_gestion_cambiada";
}
