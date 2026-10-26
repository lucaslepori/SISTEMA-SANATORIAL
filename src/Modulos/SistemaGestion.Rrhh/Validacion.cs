using System.Text.RegularExpressions;

namespace SistemaGestion.Rrhh;

internal static partial class Validacion
{
    private static readonly HashSet<string> Sexos = ["F", "M", "X"];

    public static Dictionary<string, string[]> Validar(CrearEmpleadoSolicitud s, DateOnly hoy)
    {
        var e = ValidarDatos(s.Apellidos, s.Nombres, s.FechaNacimiento, s.SexoRegistral, s.Email, s.Modalidad,
            s.HorasSemanales, s.FechaIngreso, hoy);
        if (s.Legajo <= 0)
            Agregar(e, "legajo", "El número de legajo debe ser mayor que cero.");
        if (!CuilValido(s.Cuil))
            Agregar(e, "cuil", "El CUIL debe tener 11 dígitos, sin guiones, y un dígito verificador correcto.");
        return e;
    }

    public static Dictionary<string, string[]> Validar(ModificarEmpleadoSolicitud s, DateOnly hoy) =>
        ValidarDatos(s.Apellidos, s.Nombres, s.FechaNacimiento, s.SexoRegistral, s.Email, s.Modalidad,
            s.HorasSemanales, s.FechaIngreso, hoy);

    public static Dictionary<string, string[]> Validar(CrearLicenciaSolicitud s)
    {
        var e = new Dictionary<string, string[]>();
        if (!TiposLicencia.Todos.Contains(s.Tipo ?? ""))
            Agregar(e, "tipo", $"Tipo inválido. Valores admitidos: {string.Join(", ", TiposLicencia.Todos)}.");
        if (s.Hasta < s.Desde)
            Agregar(e, "hasta", "La licencia termina antes de empezar.");
        else if (s.Hasta.DayNumber - s.Desde.DayNumber >= 366)
            Agregar(e, "hasta", "Una licencia no puede durar más de un año; cargá un período nuevo para continuarla.");
        return e;
    }

    public static Dictionary<string, string[]> Validar(CrearDocumentoSolicitud s)
    {
        var e = new Dictionary<string, string[]>();
        if (!TiposDocumento.Todos.Contains(s.Tipo ?? ""))
            Agregar(e, "tipo", $"Tipo inválido. Valores admitidos: {string.Join(", ", TiposDocumento.Todos)}.");
        if (string.IsNullOrWhiteSpace(s.Descripcion))
            Agregar(e, "descripcion", "La descripción es obligatoria.");
        if (s.Vence is { } v && s.Emitido is { } em && v < em)
            Agregar(e, "vence", "El documento vence antes de ser emitido.");
        return e;
    }

    /// <summary>
    /// Dígito verificador del CUIL/CUIT (módulo 11 con pesos 5,4,3,2,7,6,5,4,3,2). Un resto que daría 10
    /// no es válido: AFIP asigna en ese caso otro prefijo.
    /// </summary>
    public static bool CuilValido(string? cuil)
    {
        if (cuil is null || !Cuil().IsMatch(cuil))
            return false;
        int[] pesos = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var suma = 0;
        for (var i = 0; i < 10; i++)
            suma += (cuil[i] - '0') * pesos[i];
        var dv = 11 - suma % 11;
        if (dv == 11)
            dv = 0;
        return dv != 10 && dv == cuil[10] - '0';
    }

    private static Dictionary<string, string[]> ValidarDatos(
        string apellidos, string nombres, DateOnly nacimiento, string sexo, string? email, string modalidad,
        int? horas, DateOnly ingreso, DateOnly hoy)
    {
        var e = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(apellidos))
            Agregar(e, "apellidos", "Los apellidos son obligatorios.");
        if (string.IsNullOrWhiteSpace(nombres))
            Agregar(e, "nombres", "Los nombres son obligatorios.");
        if (nacimiento <= new DateOnly(1900, 1, 1) || nacimiento > hoy.AddYears(-14))
            Agregar(e, "fechaNacimiento", "La fecha de nacimiento no es válida.");
        if (!Sexos.Contains(sexo ?? ""))
            Agregar(e, "sexoRegistral", "El sexo registral debe ser F, M o X, como figura en el DNI.");
        if (!string.IsNullOrWhiteSpace(email) && !email.Contains('@'))
            Agregar(e, "email", "El email no es válido.");
        if (!Modalidades.Todas.Contains(modalidad ?? ""))
            Agregar(e, "modalidad", $"Modalidad inválida. Valores admitidos: {string.Join(", ", Modalidades.Todas)}.");
        if (horas is < 1 or > 72)
            Agregar(e, "horasSemanales", "Las horas semanales van de 1 a 72.");
        if (ingreso <= nacimiento)
            Agregar(e, "fechaIngreso", "La fecha de ingreso es anterior al nacimiento.");
        return e;
    }

    private static void Agregar(Dictionary<string, string[]> errores, string clave, string mensaje) =>
        errores[clave] = errores.TryGetValue(clave, out var previos) ? [.. previos, mensaje] : [mensaje];

    [GeneratedRegex(@"^\d{11}$")]
    private static partial Regex Cuil();
}
