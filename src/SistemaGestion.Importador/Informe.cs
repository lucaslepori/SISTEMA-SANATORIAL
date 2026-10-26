namespace SistemaGestion.Importador;

/// <summary>
/// Resultado de una corrida: cuántas filas se cargaron y qué no se pudo cargar y por qué.
/// Sólo guarda ids de Geclisa, nunca datos personales, para poder compartir el informe.
/// </summary>
public sealed class Informe
{
    const int EjemplosPorMotivo = 20;

    public SortedDictionary<string, int> Cargados { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, List<string>> Problemas { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> ProblemasTotal { get; } = new(StringComparer.Ordinal);

    public void Contar(string que, int cantidad = 1) =>
        Cargados[que] = Cargados.GetValueOrDefault(que) + cantidad;

    public void Problema(string motivo, object idGeclisa)
    {
        ProblemasTotal[motivo] = ProblemasTotal.GetValueOrDefault(motivo) + 1;
        if (!Problemas.TryGetValue(motivo, out var ejemplos))
            Problemas[motivo] = ejemplos = [];
        if (ejemplos.Count < EjemplosPorMotivo)
            ejemplos.Add(idGeclisa.ToString()!);
    }

    public int Total(string motivo) => ProblemasTotal.GetValueOrDefault(motivo);

    public void Escribir(TextWriter salida)
    {
        salida.WriteLine("Cargado:");
        foreach (var (que, n) in Cargados)
            salida.WriteLine($"  {que}: {n}");
        if (ProblemasTotal.Count == 0)
        {
            salida.WriteLine("Sin problemas.");
            return;
        }
        salida.WriteLine("Problemas (id de Geclisa, hasta 20 ejemplos por motivo):");
        foreach (var (motivo, n) in ProblemasTotal)
            salida.WriteLine($"  {motivo}: {n} [{string.Join(", ", Problemas[motivo])}]");
    }
}
