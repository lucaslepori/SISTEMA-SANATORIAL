using System.Net;

namespace SistemaGestion.Compartido;

/// <summary>
/// Quién hace la solicitud y en qué empresa. Lo completa <see cref="Identidad.ResolverContextoMiddleware"/>
/// y lo usan las sesiones de base para aplicar la seguridad por fila.
/// </summary>
public sealed class ContextoSolicitud
{
    public Guid UsuarioId { get; private set; }
    public Guid EmpresaId { get; private set; }
    public IReadOnlySet<string> Roles { get; private set; } = new HashSet<string>();
    public IPAddress? OrigenIp { get; private set; }
    public bool Resuelto { get; private set; }

    public void Establecer(Guid usuarioId, Guid empresaId, IEnumerable<string> roles, IPAddress? origenIp)
    {
        if (Resuelto)
            throw new InvalidOperationException("El contexto de la solicitud ya fue establecido.");
        UsuarioId = usuarioId;
        EmpresaId = empresaId;
        Roles = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase);
        OrigenIp = origenIp;
        Resuelto = true;
    }

    public bool TieneAlgunRol(params string[] roles) => roles.Any(Roles.Contains);
}

public static class Roles
{
    public const string Administrador = "administrador";
    public const string Admision = "admision";
    public const string Profesional = "profesional";
}
