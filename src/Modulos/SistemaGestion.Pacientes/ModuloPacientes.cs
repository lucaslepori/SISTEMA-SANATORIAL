using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using SistemaGestion.Compartido;
using SistemaGestion.Compartido.Eventos;

namespace SistemaGestion.Pacientes;

public static class ModuloPacientes
{
    private const string Recurso = "paciente";

    public static IEndpointRouteBuilder MapModuloPacientes(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/pacientes").WithTags("Pacientes");
        g.MapPost("/", CrearAsync).WithName("CrearPaciente");
        g.MapGet("/{id:guid}", ObtenerAsync).WithName("ObtenerPaciente");
        g.MapGet("/", BuscarAsync).WithName("BuscarPacientes");
        g.MapPut("/{id:guid}", ModificarAsync).WithName("ModificarPaciente");
        return app;
    }

    private static async Task<IResult> CrearAsync(
        CrearPacienteSolicitud solicitud, ContextoSolicitud ctx, OrigenesBd bd, TimeProvider reloj, CancellationToken ct)
    {
        if (!ctx.TieneAlgunRol(Roles.Admision, Roles.Administrador))
            return Results.Problem(statusCode: 403, title: "Se requiere el rol admision o administrador");
        var errores = Validacion.Validar(solicitud, Hoy(reloj));
        if (errores.Count > 0)
            return Results.ValidationProblem(errores);

        var id = Guid.CreateVersion7();
        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        try
        {
            await RepositorioPacientes.CrearAsync(s, id, solicitud, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Problem(statusCode: 409, title: "Ya existe un paciente con ese identificador en la empresa");
        }
        var paciente = (await RepositorioPacientes.ObtenerAsync(s, id, ct))!;
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Crear, Recurso, id, null, ct);
        await BandejaSalida.AgregarAsync(s, EventosPacientes.Creado, 1, EventosPacientes.Agregado, id, paciente, ct);
        await s.ConfirmarAsync(ct);
        return ConVersion(Results.Created($"/api/v1/pacientes/{id}", paciente), paciente.Version);
    }

    private static async Task<IResult> ObtenerAsync(Guid id, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!ctx.TieneAlgunRol(Roles.Admision, Roles.Profesional, Roles.Administrador))
            return Results.Problem(statusCode: 403, title: "Sin permiso para ver pacientes");
        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var paciente = await RepositorioPacientes.ObtenerAsync(s, id, ct);
        if (paciente is null)
            return Results.Problem(statusCode: 404, title: "Paciente no encontrado");
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Leer, Recurso, id, null, ct);
        await s.ConfirmarAsync(ct);
        return ConVersion(Results.Ok(paciente), paciente.Version);
    }

    private static async Task<IResult> BuscarAsync(
        string? documento, string? apellidos, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!ctx.TieneAlgunRol(Roles.Admision, Roles.Profesional, Roles.Administrador))
            return Results.Problem(statusCode: 403, title: "Sin permiso para buscar pacientes");
        documento = string.IsNullOrWhiteSpace(documento) ? null : documento.Trim();
        apellidos = string.IsNullOrWhiteSpace(apellidos) ? null : apellidos.Trim();
        // Sin criterio no se lista el padrón entero: evita exponer datos de más (Ley 25.326).
        if (documento is null && (apellidos is null || apellidos.Length < 2))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["criterio"] = ["Indicá un documento o al menos 2 letras del apellido."],
            });

        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var resultado = await RepositorioPacientes.BuscarAsync(s, documento, apellidos, 50, ct);
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Buscar, Recurso, null,
            new { documento, apellidos, resultados = resultado.Select(p => p.Id) }, ct);
        await s.ConfirmarAsync(ct);
        return Results.Ok(resultado);
    }

    private static async Task<IResult> ModificarAsync(
        Guid id, ModificarPacienteSolicitud solicitud, HttpRequest req, ContextoSolicitud ctx, OrigenesBd bd,
        TimeProvider reloj, CancellationToken ct)
    {
        if (!ctx.TieneAlgunRol(Roles.Admision, Roles.Administrador))
            return Results.Problem(statusCode: 403, title: "Se requiere el rol admision o administrador");
        if (!int.TryParse(req.Headers.IfMatch.ToString().Trim('"', ' '), out var versionEsperada))
            return Results.Problem(statusCode: 428, title: "Falta el encabezado If-Match con la versión del paciente");
        var errores = Validacion.Validar(solicitud, Hoy(reloj));
        if (errores.Count > 0)
            return Results.ValidationProblem(errores);

        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var anterior = await RepositorioPacientes.ObtenerAsync(s, id, ct);
        if (anterior is null)
            return Results.Problem(statusCode: 404, title: "Paciente no encontrado");
        var nueva = await RepositorioPacientes.ModificarAsync(s, id, versionEsperada, solicitud, ct);
        if (nueva is null)
            return Results.Problem(statusCode: 412, title: "El paciente fue modificado por otra persona. Volvé a cargarlo.");

        var actual = (await RepositorioPacientes.ObtenerAsync(s, id, ct))!;
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Modificar, Recurso, id, new { anterior, actual }, ct);
        await BandejaSalida.AgregarAsync(s, EventosPacientes.Modificado, 1, EventosPacientes.Agregado, id, actual, ct);
        await s.ConfirmarAsync(ct);
        return ConVersion(Results.Ok(actual), actual.Version);
    }

    private static DateOnly Hoy(TimeProvider reloj) => DateOnly.FromDateTime(reloj.GetLocalNow().DateTime);

    private static IResult ConVersion(IResult resultado, int version) => Respuestas.ConVersion(resultado, version);
}
