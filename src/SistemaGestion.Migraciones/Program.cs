using SistemaGestion.Migraciones;

// Uso: dotnet run --project src/SistemaGestion.Migraciones -- "<cadena de conexión de administrador>"
// o con la variable de entorno SG_DB_ADMIN.
var cadena = args.FirstOrDefault() ?? Environment.GetEnvironmentVariable("SG_DB_ADMIN");
if (string.IsNullOrWhiteSpace(cadena))
{
    Console.Error.WriteLine("Falta la cadena de conexión de administrador (argumento o variable SG_DB_ADMIN).");
    return 2;
}

try
{
    await Migrador.AplicarAsync(cadena, Console.Out);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
