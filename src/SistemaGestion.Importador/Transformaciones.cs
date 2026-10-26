using System.Globalization;
using System.Text;

namespace SistemaGestion.Importador;

/// <summary>
/// Conversiones de Geclisa al modelo nuevo. Funciones puras: no tocan la base, se prueban solas.
/// Cuando un dato no se puede convertir devuelven null y el importador lo informa; nunca lo inventan.
/// </summary>
public static class Transformaciones
{
    /// <summary>Geclisa guarda la hora como número HHMM (800 = 08:00, 1830 = 18:30).</summary>
    public static TimeOnly? Hora(short hhmm)
    {
        if (hhmm < 0) return null;
        int h = hhmm / 100, m = hhmm % 100;
        return h < 24 && m < 60 ? new TimeOnly(h, m) : null;
    }

    /// <summary>
    /// Día ISO (1 = lunes ... 7 = domingo) a partir del nombre de la tabla Dias.
    /// Se usa el nombre y no dia_id porque no se sabe con qué numeración se cargó esa tabla.
    /// </summary>
    public static int? DiaSemana(string nombre) => SinAcentos(nombre).Trim().ToUpperInvariant() switch
    {
        "LUNES" => 1,
        "MARTES" => 2,
        "MIERCOLES" => 3,
        "JUEVES" => 4,
        "VIERNES" => 5,
        "SABADO" => 6,
        "DOMINGO" => 7,
        _ => null,
    };

    /// <summary>
    /// Separa pre_nombre (un solo campo) en apellidos y nombres. Con coma: "APELLIDO, NOMBRE".
    /// Sin coma se toma la primera palabra como apellido: es una SUPOSICIÓN, que falla con apellidos compuestos.
    /// </summary>
    public static (string Apellidos, string Nombres, bool Supuesto) SepararNombre(string completo)
    {
        var limpio = string.Join(' ', completo.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var coma = limpio.IndexOf(',');
        if (coma > 0 && coma < limpio.Length - 1)
            return (limpio[..coma].Trim(), limpio[(coma + 1)..].Trim(), false);

        limpio = limpio.Trim(',', ' ');
        var espacio = limpio.IndexOf(' ');
        return espacio < 0
            ? (limpio, "-", true)
            : (limpio[..espacio], limpio[(espacio + 1)..], true);
    }

    /// <summary>Matrículas mayores que cero. Si no hay ninguna, el profesional no se puede cargar.</summary>
    public static (string? Nacional, string? Provincial) Matriculas(int? nacional, int provincial) =>
        (nacional > 0 ? nacional.Value.ToString(CultureInfo.InvariantCulture) : null,
         provincial > 0 ? provincial.ToString(CultureInfo.InvariantCulture) : null);

    /// <summary>Sexo registral. Sólo F, M o X: cualquier otro valor se informa.</summary>
    public static char? Sexo(string? valor) => valor?.Trim().ToUpperInvariant() switch
    {
        "F" => 'F',
        "M" => 'M',
        "X" => 'X',
        _ => null,
    };

    public static bool EsDni(string? tipoDoc, string? numero) =>
        tipoDoc is not null && numero is not null
        && SinAcentos(tipoDoc).Replace(".", "").Trim().ToUpperInvariant() == "DNI"
        && numero.Trim().Length is >= 6 and <= 9 && numero.Trim().All(char.IsAsciiDigit);

    /// <summary>
    /// Nombres únicos dentro de cada grupo. Geclisa no impide nombres repetidos y la base nueva sí:
    /// a partir del segundo repetido se agrega " (Geclisa {id})". Es determinístico, así que
    /// volver a importar da los mismos nombres.
    /// </summary>
    public static Dictionary<TId, string> NombresUnicos<T, TId>(
        IEnumerable<T> filas, Func<T, TId> id, Func<T, string?> nombre, Func<T, object?>? grupo = null, string? porDefecto = null)
        where TId : notnull
    {
        var resultado = new Dictionary<TId, string>();
        foreach (var g in filas.GroupBy(f => (grupo?.Invoke(f), Normalizar(nombre(f) ?? ""))))
        {
            var primero = true;
            foreach (var f in g.OrderBy(id))
            {
                var n = Limpiar(nombre(f));
                if (n.Length == 0)
                    n = $"{porDefecto ?? "Sin nombre"} {id(f)}";
                else if (!primero)
                    n = $"{n} (Geclisa {id(f)})";
                resultado[id(f)] = n;
                primero = false;
            }
        }
        return resultado;
    }

    /// <summary>
    /// Horario local del turno. Si Hs_Fin no es posterior a Hs_Ini se usa la duración (tur_tiempo).
    /// </summary>
    public static (DateTime Desde, DateTime Hasta)? HorarioTurno(DateTime fecha, short hsIni, short hsFin, short minutos)
    {
        if (Hora(hsIni) is not { } ini) return null;
        var desde = DateOnly.FromDateTime(fecha).ToDateTime(ini);
        DateTime hasta;
        if (Hora(hsFin) is { } fin && fin > ini)
            hasta = DateOnly.FromDateTime(fecha).ToDateTime(fin);
        else if (minutos > 0)
            hasta = desde.AddMinutes(minutos);
        else
            return null;
        return (desde, hasta);
    }

    /// <summary>
    /// Estado del turno. Geclisa no tiene columna de estado: se deduce de las fechas de espera y atención.
    /// Los cancelados no existen porque Geclisa los borra (van a TurnosBorrados).
    /// </summary>
    public static string EstadoTurno(GTurno t) =>
        t.FechaAtendido is not null ? "ATENDIDO"
        : t.FechaEspera is not null ? "PRESENTE"
        : t.Confirmado == true ? "CONFIRMADO"
        : "RESERVADO";

    /// <summary>Datos personales que se guardan. Con seudonimización no sale ningún dato que identifique a la persona.</summary>
    public static PacienteImportado? Paciente(GFicha f, bool datosReales, DateOnly hoy)
    {
        if (Sexo(f.Sexo) is not { } sexo) return null;
        var nacimiento = DateOnly.FromDateTime(f.FechaNacimiento);
        if (nacimiento <= new DateOnly(1900, 1, 1) || nacimiento > hoy) return null;

        if (!datosReales)
            return new PacienteImportado("Paciente", $"Geclisa {f.Id}", new DateOnly(nacimiento.Year, 1, 1), sexo, null, null, null);

        var apellidos = Limpiar(f.Apellido);
        var nombres = Limpiar(f.Nombre);
        if (apellidos.Length == 0 || nombres.Length == 0) return null;
        var telefono = Vacio(f.Celular) ?? Vacio(f.Telefono);
        var dni = EsDni(f.TipoDoc, f.NroDoc) ? f.NroDoc!.Trim() : null;
        return new PacienteImportado(apellidos, nombres, nacimiento, sexo, telefono, Vacio(f.Email), dni);
    }

    public static string Limpiar(string? s) => string.Join(' ', (s ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    static string? Vacio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static string Normalizar(string s) => SinAcentos(Limpiar(s)).ToUpperInvariant();

    static string SinAcentos(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}

public sealed record PacienteImportado(
    string Apellidos, string Nombres, DateOnly FechaNacimiento, char Sexo, string? Telefono, string? Email, string? Dni);
