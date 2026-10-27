using SistemaGestion.Migraciones;

// Uso:
//   dotnet run --project src/SistemaGestion.Migraciones -- "<cadena de conexión de administrador>" [--datos-desarrollo]
// La cadena también puede venir en la variable de entorno SG_DB_ADMIN.
// --crear-base crea la base si no existe (se conecta a la base "postgres" del mismo servidor).
// --datos-desarrollo crea los usuarios locales de la aplicación y carga datos de ejemplo. Nunca en producción.
var datosDesarrollo = args.Contains("--datos-desarrollo");
var crearBase = args.Contains("--crear-base");
var cadena = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
    ?? Environment.GetEnvironmentVariable("SG_DB_ADMIN");
if (string.IsNullOrWhiteSpace(cadena))
{
    Console.Error.WriteLine("Falta la cadena de conexión de administrador (argumento o variable SG_DB_ADMIN).");
    return 2;
}

try
{
    if (crearBase)
        await Migrador.CrearBaseSiFaltaAsync(cadena, Console.Out);
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
