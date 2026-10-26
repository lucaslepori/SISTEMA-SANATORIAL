using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SistemaGestion.Turnos;

public static class ModuloTurnos
{
    public static IEndpointRouteBuilder MapModuloTurnos(this IEndpointRouteBuilder app)
    {
        var agendas = app.MapGroup("/api/v1/agendas").WithTags("Agendas");
        agendas.MapPost("/", Agendas.CrearAsync);
        agendas.MapGet("/", Agendas.ListarAsync);
        agendas.MapPut("/{id:guid}/gestion", Agendas.CambiarGestionAsync);

        app.MapGroup("/api/v1/bloqueos").WithTags("Agendas").MapPost("/", Agendas.CrearBloqueoAsync);

        var turnos = app.MapGroup("/api/v1/turnos").WithTags("Turnos");
        turnos.MapGet("/disponibilidad", Turnos.DisponibilidadAsync);
        turnos.MapPost("/", Turnos.AsignarAsync);
        turnos.MapGet("/{id:guid}", Turnos.ObtenerAsync);
        turnos.MapGet("/", Turnos.ListarAsync);
        turnos.MapPost("/{id:guid}/cancelacion", Turnos.CancelarAsync);
        turnos.MapPost("/{id:guid}/estado", Turnos.CambiarEstadoAsync);
        return app;
    }
}
