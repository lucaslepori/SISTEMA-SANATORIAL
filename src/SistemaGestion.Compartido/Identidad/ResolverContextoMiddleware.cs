using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Http;

namespace SistemaGestion.Compartido.Identidad;

/// <summary>
/// Convierte el usuario autenticado (claim "sub" del proveedor OpenID Connect) y la empresa
/// pedida en el encabezado X-Empresa-Id en un <see cref="ContextoSolicitud"/>.
/// Rechaza la solicitud si el usuario no existe, está inactivo o no pertenece a esa empresa.
/// </summary>
public sealed class ResolverContextoMiddleware(RequestDelegate siguiente)
{
    public const string EncabezadoEmpresa = "X-Empresa-Id";

    public async Task InvokeAsync(HttpContext http, ContextoSolicitud contexto, OrigenesBd origenes)
    {
        if (!http.Request.Path.StartsWithSegments("/api"))
        {
            await siguiente(http);
            return;
        }

        var sujeto = http.User.FindFirstValue("sub") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (http.User.Identity?.IsAuthenticated != true || string.IsNullOrEmpty(sujeto))
        {
            await Results.Problem(statusCode: 401, title: "No autenticado").ExecuteAsync(http);
            return;
        }

        if (!Guid.TryParse(http.Request.Headers[EncabezadoEmpresa], out var empresaId))
        {
            await Results.Problem(statusCode: 400, title: $"Falta el encabezado {EncabezadoEmpresa}").ExecuteAsync(http);
            return;
        }

        await using var conexion = await origenes.Api.OpenConnectionAsync(http.RequestAborted);
        var fila = await conexion.QuerySingleOrDefaultAsync<(Guid UsuarioId, string[] Roles)>(new CommandDefinition("""
            SELECT u.id, ue.roles
            FROM nucleo.usuarios u
            JOIN nucleo.usuarios_empresas ue ON ue.usuario_id = u.id
            JOIN nucleo.empresas e ON e.id = ue.empresa_id
            WHERE u.sujeto_oidc = @sujeto AND ue.empresa_id = @empresaId
              AND u.activo AND ue.activo AND e.activa
            """, new { sujeto, empresaId }, cancellationToken: http.RequestAborted));

        if (fila.UsuarioId == Guid.Empty)
        {
            await Results.Problem(statusCode: 403, title: "El usuario no tiene acceso a esta empresa").ExecuteAsync(http);
            return;
        }

        contexto.Establecer(fila.UsuarioId, empresaId, fila.Roles, http.Connection.RemoteIpAddress);
        await siguiente(http);
    }
}
