namespace SistemaGestion.Catalogos;

// Contratos públicos de la API v1 de catálogos.

public sealed record CrearNombradoSolicitud(string Nombre, int? GeclisaId);

public sealed record NombradoDto(Guid Id, string Nombre, bool Activo, int? GeclisaId);

public sealed record CrearSedeSolicitud(string Nombre, string? Direccion);

public sealed record SedeDto(Guid Id, string Nombre, string? Direccion, bool Activo);

public sealed record CrearConsultorioSolicitud(Guid SedeId, string Nombre, int? GeclisaId);

public sealed record ConsultorioDto(Guid Id, Guid SedeId, string Nombre, bool Activo, int? GeclisaId);

public sealed record CrearCoberturaSolicitud(string Nombre, string? Sigla, string Tipo, int? GeclisaId);

public sealed record CoberturaDto(Guid Id, string Nombre, string? Sigla, string Tipo, bool Activo, int? GeclisaId);

public sealed record CrearPlanSolicitud(string Nombre, int? GeclisaId);

public sealed record PlanDto(Guid Id, Guid CoberturaId, string Nombre, bool Activo, int? GeclisaId);

public sealed record CrearProfesionalSolicitud(
    string Apellidos,
    string Nombres,
    string? MatriculaNacional,
    string? MatriculaProvincial,
    Guid? UsuarioId,
    IReadOnlyList<Guid>? EspecialidadIds,
    int? GeclisaId);

public sealed record ProfesionalDto(
    Guid Id,
    string Apellidos,
    string Nombres,
    string? MatriculaNacional,
    string? MatriculaProvincial,
    Guid? UsuarioId,
    bool Activo,
    int? GeclisaId,
    IReadOnlyList<Guid> EspecialidadIds);

public static class TiposCobertura
{
    public static readonly IReadOnlySet<string> Todos = new HashSet<string> { "OBRA_SOCIAL", "PREPAGA", "PARTICULAR", "OTRA" };
}
