using System.Text.RegularExpressions;

namespace SistemaGestion.Pacientes;

internal static partial class Validacion
{
    private static readonly HashSet<string> Sexos = ["F", "M", "X"];
    private static readonly HashSet<string> Sistemas = ["DNI", "PASAPORTE", "CUIL", "GECLISA_FICHA", "OTRO"];

    public static Dictionary<string, string[]> Validar(CrearPacienteSolicitud s, DateOnly hoy)
    {
        var errores = ValidarDatos(s.Apellidos, s.Nombres, s.FechaNacimiento, s.SexoRegistral, s.Email, hoy);
        var ids = s.Identificadores ?? [];
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            var clave = $"identificadores[{i}]";
            if (!Sistemas.Contains(id.Sistema ?? ""))
                Agregar(errores, clave, $"Sistema inválido. Valores admitidos: {string.Join(", ", Sistemas)}.");
            else if (id.Sistema == "DNI" && !Dni().IsMatch(id.Valor ?? ""))
                Agregar(errores, clave, "El DNI debe tener entre 7 y 8 dígitos, sin puntos.");
            else if (id.Sistema == "CUIL" && !Cuil().IsMatch(id.Valor ?? ""))
                Agregar(errores, clave, "El CUIL debe tener 11 dígitos, sin guiones.");
            else if (string.IsNullOrWhiteSpace(id.Valor))
                Agregar(errores, clave, "El valor es obligatorio.");
        }
        if (ids.GroupBy(x => x.Sistema).Any(g => g.Count() > 1))
            Agregar(errores, "identificadores", "No puede haber dos identificadores del mismo sistema.");
        return errores;
    }

    public static Dictionary<string, string[]> Validar(ModificarPacienteSolicitud s, DateOnly hoy) =>
        ValidarDatos(s.Apellidos, s.Nombres, s.FechaNacimiento, s.SexoRegistral, s.Email, hoy);

    private static Dictionary<string, string[]> ValidarDatos(
        string apellidos, string nombres, DateOnly nacimiento, string sexo, string? email, DateOnly hoy)
    {
        var errores = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(apellidos))
            Agregar(errores, "apellidos", "Los apellidos son obligatorios.");
        if (string.IsNullOrWhiteSpace(nombres))
            Agregar(errores, "nombres", "Los nombres son obligatorios.");
        if (nacimiento <= new DateOnly(1900, 1, 1) || nacimiento > hoy)
            Agregar(errores, "fechaNacimiento", "La fecha de nacimiento no es válida.");
        if (!Sexos.Contains(sexo ?? ""))
            Agregar(errores, "sexoRegistral", "El sexo registral debe ser F, M o X, como figura en el DNI.");
        if (!string.IsNullOrWhiteSpace(email) && !email.Contains('@'))
            Agregar(errores, "email", "El email no es válido.");
        return errores;
    }

    private static void Agregar(Dictionary<string, string[]> errores, string clave, string mensaje) =>
        errores[clave] = errores.TryGetValue(clave, out var previos) ? [.. previos, mensaje] : [mensaje];

    [GeneratedRegex(@"^\d{7,8}$")]
    private static partial Regex Dni();

    [GeneratedRegex(@"^\d{11}$")]
    private static partial Regex Cuil();
}
