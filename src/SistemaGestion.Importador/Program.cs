using SistemaGestion.Importador;

// Uso:
//   dotnet run --project src/SistemaGestion.Importador -- --empresa <id> [--pacientes con-turnos-futuros|todos] [--datos-reales] [--simular]
// Cadenas de conexión: variables SG_GECLISA (SQL Server de Geclisa, sólo lectura) y SG_DB_ADMIN (base nueva),
// o los argumentos --geclisa "..." y --destino "...".
string? Valor(string nombre)
{
    var i = Array.IndexOf(args, nombre);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var geclisa = Valor("--geclisa") ?? Environment.GetEnvironmentVariable("SG_GECLISA");
var destino = Valor("--destino") ?? Environment.GetEnvironmentVariable("SG_DB_ADMIN");
if (string.IsNullOrWhiteSpace(geclisa) || string.IsNullOrWhiteSpace(destino) || !Guid.TryParse(Valor("--empresa"), out var empresa))
{
    Console.Error.WriteLine("Faltan datos: --empresa <id>, y las conexiones SG_GECLISA y SG_DB_ADMIN (o --geclisa y --destino).");
    return 2;
}
var alcance = Valor("--pacientes") switch
{
    null or "con-turnos-futuros" => AlcancePacientes.ConTurnosFuturos,
    "todos" => AlcancePacientes.Todos,
    var otro => throw new ArgumentException($"--pacientes no acepta '{otro}'."),
};
var opciones = new OpcionesImportacion(empresa, alcance, args.Contains("--datos-reales"), args.Contains("--simular"));

if (opciones.DatosReales)
    Console.WriteLine("ATENCIÓN: se van a copiar nombres, DNI, teléfonos y correos reales (Ley 25.326). Usar sólo en un servidor protegido.");
else
    Console.WriteLine("Pacientes seudonimizados: no se copia ningún dato que identifique a la persona (usar --datos-reales para copiarlos).");

try
{
    var informe = await new ImportadorGeclisa(destino, opciones)
        .EjecutarAsync(new FuenteSqlServer(geclisa), DateOnly.FromDateTime(DateTime.Now));
    informe.Escribir(Console.Out);
    Console.WriteLine(opciones.Simular ? "Simulación: no se guardó nada." : "Importación confirmada.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
