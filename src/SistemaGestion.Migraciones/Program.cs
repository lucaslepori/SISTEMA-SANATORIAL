using SistemaGestion.Migraciones;

// Uso:
//   dotnet run --project src/SistemaGestion.Migraciones -- "<cadena de conexión de administrador>" [--datos-desarrollo]
// La cadena también puede venir en la variable de entorno SG_DB_ADMIN.
// --datos-desarrollo crea los usuarios locales de la aplicación y carga datos de ejemplo. Nunca en producción.
var datosDesarrollo = args.Contains("--datos-desarrollo");
var cadena = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
    ?? Environment.GetEnvironmentVariable("SG_DB_ADMIN");
if (string.IsNullOrWhiteSpace(cadena))
{
    Console.Error.WriteLine("Falta la cadena de conexión de administrador (argumento o variable SG_DB_ADMIN).");
    return 2;
}

try
{
    await Migrador.AplicarAsync(cadena, Console.Out);
    if (datosDesarrollo)
        await Migrador.CargarDatosDesarrolloAsync(cadena, Console.Out);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
