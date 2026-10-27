using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using SistemaGestion.Compartido;

namespace SistemaGestion.Rrhh;

/// <summary>
/// Recursos humanos: legajos, licencias y documentación con vencimiento.
/// Todo el módulo exige el rol rrhh o administrador: son datos personales del personal.
/// </summary>
public static class ModuloRrhh
{
    public static IEndpointRouteBuilder MapModuloRrhh(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/rrhh").WithTags("RRHH");

        g.MapGet("/sectores", (ContextoSolicitud c, OrigenesBd b, CancellationToken ct) => ListarNombradosAsync("sectores", c, b, ct));
        g.MapPost("/sectores", (CrearSectorPuestoSolicitud s, ContextoSolicitud c, OrigenesBd b, CancellationToken ct) =>
            CrearNombradoAsync("sectores", "sector", s, c, b, ct));
        g.MapGet("/puestos", (ContextoSolicitud c, OrigenesBd b, CancellationToken ct) => ListarNombradosAsync("puestos", c, b, ct));
        g.MapPost("/puestos", (CrearSectorPuestoSolicitud s, ContextoSolicitud c, OrigenesBd b, CancellationToken ct) =>
            CrearNombradoAsync("puestos", "puesto", s, c, b, ct));

        g.MapPost("/empleados", Empleados.CrearAsync);
        g.MapGet("/empleados", Empleados.ListarAsync);
        g.MapGet("/empleados/{id:guid}", Empleados.LeerAsync);
        g.MapPut("/empleados/{id:guid}", Empleados.ModificarAsync);
        g.MapPost("/empleados/{id:guid}/egreso", Empleados.EgresoAsync);
        g.MapPost("/empleados/{id:guid}/reingreso", Empleados.ReingresoAsync);

        g.MapPost("/empleados/{empleadoId:guid}/licencias", Licencias.CrearAsync);
        g.MapGet("/licencias", Licencias.ListarAsync);
        g.MapPost("/licencias/{id:guid}/resolucion", Licencias.ResolverAsync);
        g.MapPost("/licencias/{id:guid}/anulacion", Licencias.AnularAsync);

        g.MapPost("/empleados/{empleadoId:guid}/documentos", Documentos.CrearAsync);
        g.MapGet("/empleados/{empleadoId:guid}/documentos", Documentos.ListarAsync);
        g.MapGet("/vencimientos", Documentos.VencimientosAsync);
        return app;
    }

    // --- Sectores y puestos: misma forma, distinta tabla.

    private static async Task<IResult> ListarNombradosAsync(string tabla, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!Empleados.PuedeOperar(ctx))
            return Empleados.SinPermiso();
        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        return Results.Ok(await s.Conexion.QueryAsync<SectorPuestoDto>(new CommandDefinition(
            $"SELECT id, nombre, activo FROM rrhh.{tabla} ORDER BY nombre", transaction: s.Transaccion, cancellationToken: ct)));
    }

    private static async Task<IResult> CrearNombradoAsync(
        string tabla, string recurso, CrearSectorPuestoSolicitud x, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!Empleados.PuedeOperar(ctx))
            return Empleados.SinPermiso();
        if (string.IsNullOrWhiteSpace(x.Nombre))
            return Respuestas.Invalido("nombre", "El nombre es obligatorio.");

        var dto = new SectorPuestoDto(Guid.CreateVersion7(), x.Nombre.Trim(), true);
        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        try
        {
            await s.Conexion.ExecuteAsync(new CommandDefinition(
                $"INSERT INTO rrhh.{tabla} (id, empresa_id, nombre) VALUES (@Id, @empresa, @Nombre)",
                new { dto.Id, empresa = ctx.EmpresaId, dto.Nombre }, s.Transaccion, cancellationToken: ct));
        }
        catch (PostgresException ex) when (Respuestas.EsDuplicado(ex))
        {
            return Respuestas.Conflicto($"Ya existe un {recurso} con ese nombre");
        }
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Crear, recurso, dto.Id, dto, ct);
        await s.ConfirmarAsync(ct);
        return Results.Created($"/api/v1/rrhh/{tabla}/{dto.Id}", dto);
    }
}
