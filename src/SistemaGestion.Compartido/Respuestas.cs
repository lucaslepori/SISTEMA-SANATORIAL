using Microsoft.AspNetCore.Http;
using Npgsql;

namespace SistemaGestion.Compartido;

/// <summary>Respuestas comunes de la API, para que todos los módulos contesten igual.</summary>
public static class Respuestas
{
    public static IResult SinPermiso(string detalle) => Results.Problem(statusCode: 403, title: detalle);

    public static IResult NoEncontrado(string recurso) => Results.Problem(statusCode: 404, title: $"{recurso} no encontrado");

    public static IResult Conflicto(string detalle) => Results.Problem(statusCode: 409, title: detalle);

    public static IResult NoProcesable(string detalle) => Results.Problem(statusCode: 422, title: detalle);

    public static IResult Invalido(string campo, string mensaje) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [campo] = [mensaje] });

    public static IResult FaltaVersion(string recurso) =>
        Results.Problem(statusCode: 428, title: $"Falta el encabezado If-Match con la versión del {recurso}");

    public static IResult VersionVieja(string recurso) =>
        Results.Problem(statusCode: 412, title: $"El {recurso} fue modificado por otra persona. Volvé a cargarlo.");

    /// <summary>Lee la versión esperada del encabezado If-Match ("3" o 3).</summary>
    public static bool LeerVersion(HttpRequest req, out int version) =>
        int.TryParse(req.Headers.IfMatch.ToString().Trim('"', ' '), out version);

    /// <summary>Agrega el encabezado ETag con la versión del recurso.</summary>
    public static IResult ConVersion(IResult resultado, int version) => new ConEncabezado(resultado, "ETag", $"\"{version}\"");

    public static bool EsDuplicado(PostgresException ex) => ex.SqlState == PostgresErrorCodes.UniqueViolation;

    public static bool EsReferenciaInvalida(PostgresException ex) => ex.SqlState == PostgresErrorCodes.ForeignKeyViolation;

    private sealed class ConEncabezado(IResult interno, string nombre, string valor) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.Headers[nombre] = valor;
            return interno.ExecuteAsync(http);
        }
    }
}
