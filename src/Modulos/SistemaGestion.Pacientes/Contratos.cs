namespace SistemaGestion.Pacientes;

// Contratos públicos de la API v1 del módulo. Cambiarlos de forma incompatible exige una v2.

public sealed record IdentificadorDto(string Sistema, string Valor);

public sealed record CrearPacienteSolicitud(
    string Apellidos,
    string Nombres,
    string? NombreAutopercibido,
    DateOnly FechaNacimiento,
    string SexoRegistral,
    string? Telefono,
    string? Email,
    IReadOnlyList<IdentificadorDto>? Identificadores);

public sealed record ModificarPacienteSolicitud(
    string Apellidos,
    string Nombres,
    string? NombreAutopercibido,
    DateOnly FechaNacimiento,
    string SexoRegistral,
    string? Telefono,
    string? Email);

public sealed record PacienteDto(
    Guid Id,
    string Apellidos,
    string Nombres,
    string? NombreAutopercibido,
    DateOnly FechaNacimiento,
    string SexoRegistral,
    string? Telefono,
    string? Email,
    bool Activo,
    int Version,
    IReadOnlyList<IdentificadorDto> Identificadores);

public static class EventosPacientes
{
    public const string Agregado = "paciente";
    public const string Creado = "pacientes.paciente_creado";
    public const string Modificado = "pacientes.paciente_modificado";
}
