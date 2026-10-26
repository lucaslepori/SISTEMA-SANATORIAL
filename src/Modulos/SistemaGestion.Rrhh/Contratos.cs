namespace SistemaGestion.Rrhh;

// Contratos públicos de la API v1 de recursos humanos. Las fechas son de calendario (yyyy-MM-dd).

public sealed record CrearSectorPuestoSolicitud(string Nombre);

public sealed record SectorPuestoDto(Guid Id, string Nombre, bool Activo);

public sealed record CrearEmpleadoSolicitud(
    int Legajo,
    string Apellidos,
    string Nombres,
    string Cuil,
    DateOnly FechaNacimiento,
    string SexoRegistral,
    string? Email,
    string? Telefono,
    string? Domicilio,
    string Modalidad,
    string? Convenio,
    string? Categoria,
    int? HorasSemanales,
    Guid SectorId,
    Guid PuestoId,
    DateOnly FechaIngreso,
    Guid? ProfesionalId,
    Guid? UsuarioId);

/// <summary>Reemplaza los datos modificables del legajo. El número de legajo y el CUIL no cambian.</summary>
public sealed record ModificarEmpleadoSolicitud(
    string Apellidos,
    string Nombres,
    DateOnly FechaNacimiento,
    string SexoRegistral,
    string? Email,
    string? Telefono,
    string? Domicilio,
    string Modalidad,
    string? Convenio,
    string? Categoria,
    int? HorasSemanales,
    Guid SectorId,
    Guid PuestoId,
    DateOnly FechaIngreso,
    Guid? ProfesionalId,
    Guid? UsuarioId);

public sealed record EgresoSolicitud(DateOnly Fecha, string Motivo);

public sealed record ReingresoSolicitud(DateOnly Fecha);

public sealed record EmpleadoDto(
    Guid Id,
    int Legajo,
    string Apellidos,
    string Nombres,
    string Cuil,
    DateOnly FechaNacimiento,
    string SexoRegistral,
    string? Email,
    string? Telefono,
    string? Domicilio,
    string Modalidad,
    string? Convenio,
    string? Categoria,
    int? HorasSemanales,
    Guid SectorId,
    Guid PuestoId,
    DateOnly FechaIngreso,
    DateOnly? FechaEgreso,
    string? MotivoEgreso,
    Guid? ProfesionalId,
    Guid? UsuarioId,
    bool Activo,
    int Version);

/// <summary>Fila del listado: sin CUIL, fecha de nacimiento ni datos de contacto (minimización de datos).</summary>
public sealed record EmpleadoResumenDto(
    Guid Id,
    int Legajo,
    string Apellidos,
    string Nombres,
    string Sector,
    string Puesto,
    string Modalidad,
    Guid? ProfesionalId,
    bool Activo);

public sealed record CrearLicenciaSolicitud(string Tipo, DateOnly Desde, DateOnly Hasta, string? Observaciones);

public sealed record ResolverLicenciaSolicitud(bool Aprobar, string? MotivoRechazo);

public sealed record LicenciaDto(
    Guid Id,
    Guid EmpleadoId,
    string Tipo,
    DateOnly Desde,
    DateOnly Hasta,
    int Dias,
    string Estado,
    string? Observaciones,
    string? MotivoRechazo,
    int Version);

public sealed record CrearDocumentoSolicitud(string Tipo, string Descripcion, string? Numero, DateOnly? Emitido, DateOnly? Vence);

public sealed record DocumentoDto(
    Guid Id, Guid EmpleadoId, string Tipo, string Descripcion, string? Numero, DateOnly? Emitido, DateOnly? Vence);

/// <summary>El documento más reciente de cada tipo y persona que ya venció o vence dentro del plazo pedido.</summary>
public sealed record VencimientoDto(
    Guid DocumentoId,
    Guid EmpleadoId,
    int Legajo,
    string Apellidos,
    string Nombres,
    string Tipo,
    string Descripcion,
    DateOnly Vence,
    int DiasRestantes);

public static class Modalidades
{
    public static readonly IReadOnlySet<string> Todas =
        new HashSet<string> { "RELACION_DEPENDENCIA", "MONOTRIBUTO", "RESIDENCIA", "PASANTIA", "OTRA" };
}

public static class TiposLicencia
{
    public static readonly IReadOnlySet<string> Todos = new HashSet<string>
    {
        "VACACIONES", "ENFERMEDAD", "ACCIDENTE_TRABAJO", "MATERNIDAD", "NACIMIENTO",
        "MATRIMONIO", "FALLECIMIENTO_FAMILIAR", "EXAMEN", "SIN_GOCE", "OTRA",
    };
}

public static class EstadosLicencia
{
    public const string Solicitada = "SOLICITADA";
    public const string Aprobada = "APROBADA";
    public const string Rechazada = "RECHAZADA";
    public const string Anulada = "ANULADA";
}

public static class TiposDocumento
{
    public static readonly IReadOnlySet<string> Todos = new HashSet<string>
    {
        "MATRICULA", "SEGURO_MALA_PRAXIS", "APTO_MEDICO", "VACUNACION", "CAPACITACION", "OTRO",
    };
}

public static class EventosRrhh
{
    public const string AgregadoEmpleado = "empleado";
    public const string AgregadoLicencia = "licencia";
    public const string EmpleadoAlta = "rrhh.empleado_alta";
    public const string EmpleadoModificado = "rrhh.empleado_modificado";
    public const string EmpleadoEgreso = "rrhh.empleado_egreso";
    public const string EmpleadoReingreso = "rrhh.empleado_reingreso";
    public const string LicenciaAprobada = "rrhh.licencia_aprobada";
    public const string LicenciaAnulada = "rrhh.licencia_anulada";
}
