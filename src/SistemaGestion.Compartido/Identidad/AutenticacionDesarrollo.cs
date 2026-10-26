using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SistemaGestion.Compartido.Identidad;

/// <summary>Valores por defecto del modo de desarrollo, para probar la API desde el navegador sin encabezados.</summary>
public sealed class OpcionesDesarrollo
{
    public string? UsuarioPorDefecto { get; set; }
    public string? EmpresaPorDefecto { get; set; }
}

/// <summary>
/// SÓLO PARA DESARROLLO Y PRUEBAS. Toma el sujeto del encabezado X-Usuario-Sujeto (o el usuario por defecto
/// configurado) en lugar de validar un token. La API se niega a arrancar con este modo fuera del entorno Development.
/// </summary>
public sealed class AutenticacionDesarrollo(
    IOptionsMonitor<AuthenticationSchemeOptions> opciones, ILoggerFactory logger, UrlEncoder encoder,
    IOptions<OpcionesDesarrollo> desarrollo)
    : AuthenticationHandler<AuthenticationSchemeOptions>(opciones, logger, encoder)
{
    public const string Esquema = "Desarrollo";
    public const string Encabezado = "X-Usuario-Sujeto";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var sujeto = Request.Headers[Encabezado].ToString();
        if (string.IsNullOrWhiteSpace(sujeto))
            sujeto = desarrollo.Value.UsuarioPorDefecto ?? "";
        if (string.IsNullOrWhiteSpace(sujeto))
            return Task.FromResult(AuthenticateResult.NoResult());
        var identidad = new ClaimsIdentity([new Claim("sub", sujeto)], Esquema);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identidad), Esquema)));
    }
}
