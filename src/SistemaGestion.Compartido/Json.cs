using System.Text.Json;

namespace SistemaGestion.Compartido;

public static class Json
{
    public static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web);
}
