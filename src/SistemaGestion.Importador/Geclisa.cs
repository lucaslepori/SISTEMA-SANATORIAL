namespace SistemaGestion.Importador;

// Filas tal como se leen de Geclisa. Los nombres siguen a las columnas de origen
// para que el mapeo se pueda revisar contra el esquema de Macena (ver docs/turnos-geclisa.md).

public sealed class GUbicacion
{
    public short Id { get; init; }
    public string? Nombre { get; init; }
    public string? Direccion { get; init; }
}

public sealed class GConsultorio
{
    public short Id { get; init; }
    public string? Nombre { get; init; }
    public short? UbicId { get; init; }
}

public sealed class GServicio
{
    public short Id { get; init; }
    public string? Nombre { get; init; }
    public bool? Inactivo { get; init; }
}

public sealed class GEspecialidad
{
    public short Id { get; init; }
    public string Nombre { get; init; } = "";
}

public sealed class GPrestador
{
    public int Id { get; init; }
    public string Nombre { get; init; } = "";
    public int? MatriculaNacional { get; init; }
    public int MatriculaProvincial { get; init; }
}

public sealed class GPrestadorEspecialidad
{
    public int PreId { get; init; }
    public short EspId { get; init; }
}

public sealed class GObraSocial
{
    public short Id { get; init; }
    public string Sigla { get; init; } = "";
    public string Nombre { get; init; } = "";
    public bool Eliminado { get; init; }
    public bool? EsParticular { get; init; }
}

public sealed class GPlan
{
    public short Id { get; init; }
    public string Nombre { get; init; } = "";
    public short OsId { get; init; }
    public DateTime? FechaFin { get; init; }
}

/// <summary>Cronograma con el nombre del día (Dias) y la duración del turno (TurnosTiempo) ya resueltos.</summary>
public sealed class GCronograma
{
    public int Id { get; init; }
    public string DiaNombre { get; init; } = "";
    public short ServId { get; init; }
    public int PreId { get; init; }
    public short HsIni { get; init; }
    public short HsFin { get; init; }
    public DateTime FechaIni { get; init; }
    public DateTime FechaFin { get; init; }
    public short Tiempo { get; init; }
    public short? ConsId { get; init; }
    public short? PacientesSimultaneos { get; init; }
}

public sealed class GCronogramaCobertura
{
    public int CroId { get; init; }
    public short OsId { get; init; }
}

public sealed class GFeriado
{
    public int Id { get; init; }
    public DateTime Fecha { get; init; }
    public string Descripcion { get; init; } = "";
    public short? HsIni { get; init; }
    public short? HsFin { get; init; }
    public string? AplicaEn { get; init; }
    /// <summary>Prestadores exceptuados (Feriados_PrestadoresExcluidos). Todavía no se puede representar.</summary>
    public int PrestadoresExcluidos { get; init; }
}

/// <summary>Ficha de paciente con el nombre del tipo de documento (TipoDoc) ya resuelto.</summary>
public sealed class GFicha
{
    public int Id { get; init; }
    public string Apellido { get; init; } = "";
    public string Nombre { get; init; } = "";
    public string? TipoDoc { get; init; }
    public string? NroDoc { get; init; }
    public string? Sexo { get; init; }
    public DateTime FechaNacimiento { get; init; }
    public string? Telefono { get; init; }
    public string? Celular { get; init; }
    public string? Email { get; init; }
}

public sealed class GTurno
{
    public int Id { get; init; }
    public short ServId { get; init; }
    public DateTime Fecha { get; init; }
    public short HsIni { get; init; }
    public short HsFin { get; init; }
    public short Tiempo { get; init; }
    public int FichaId { get; init; }
    public short OsId { get; init; }
    public short? PlanId { get; init; }
    public int PreId { get; init; }
    public short? ConsId { get; init; }
    public int? CroId { get; init; }
    public string? NroAfiliado { get; init; }
    public string? Observaciones { get; init; }
    public bool? Confirmado { get; init; }
    public bool? EsWeb { get; init; }
    public DateTime? FechaEspera { get; init; }
    public DateTime? FechaAtendido { get; init; }
}

public sealed class DatosGeclisa
{
    public List<GUbicacion> Ubicaciones { get; init; } = [];
    public List<GConsultorio> Consultorios { get; init; } = [];
    public List<GServicio> Servicios { get; init; } = [];
    public List<GEspecialidad> Especialidades { get; init; } = [];
    public List<GPrestador> Prestadores { get; init; } = [];
    public List<GPrestadorEspecialidad> PrestadoresEspecialidades { get; init; } = [];
    public List<GObraSocial> ObrasSociales { get; init; } = [];
    public List<GPlan> Planes { get; init; } = [];
    public List<GCronograma> Cronogramas { get; init; } = [];
    public List<GCronogramaCobertura> CronogramasCoberturas { get; init; } = [];
    public List<GFeriado> Feriados { get; init; } = [];
    public List<GFicha> Fichas { get; init; } = [];
    public List<GTurno> Turnos { get; init; } = [];
}

/// <summary>Origen de los datos. La implementación real lee SQL Server; las pruebas usan una en memoria.</summary>
public interface IFuenteGeclisa
{
    /// <param name="desde">Fecha local desde la que se leen cronogramas vigentes, feriados y turnos.</param>
    /// <param name="todasLasFichas">false: sólo los pacientes con turnos desde esa fecha.</param>
    Task<DatosGeclisa> LeerAsync(DateOnly desde, bool todasLasFichas, CancellationToken ct);
}
