using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Npgsql;
using Scalar.AspNetCore;
using SistemaGestion.Compartido;
using SistemaGestion.Compartido.Eventos;
using SistemaGestion.Compartido.Identidad;
using SistemaGestion.Catalogos;
using SistemaGestion.Pacientes;
using SistemaGestion.Rrhh;
using SistemaGestion.Turnos;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// --- Base de datos: un origen por rol, para que cada parte tenga sólo los permisos que necesita.
var cadenaApi = config.GetConnectionString("Api")
    ?? throw new InvalidOperationException("Falta ConnectionStrings:Api");
var cadenaWorker = config.GetConnectionString("Worker")
    ?? throw new InvalidOperationException("Falta ConnectionStrings:Worker");
builder.Services.AddSingleton(new OrigenesBd(
    NpgsqlDataSource.Create(cadenaApi),
    NpgsqlDataSource.Create(cadenaWorker)));

builder.Services.AddScoped<ContextoSolicitud>();
builder.Services.AddSingleton(TimeProvider.System);

// --- Autenticación: OpenID Connect (por ejemplo Keycloak) o, sólo en desarrollo, por encabezado.
var modoAuth = config["Autenticacion:Modo"] ?? "Oidc";
if (modoAuth == AutenticacionDesarrollo.Esquema)
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException("La autenticación de desarrollo no se puede usar fuera del entorno Development.");
    // Sólo en desarrollo: usuario y empresa por defecto para probar desde el navegador.
    builder.Services.Configure<OpcionesDesarrollo>(config.GetSection("Autenticacion:Desarrollo"));
    builder.Services.AddAuthentication(AutenticacionDesarrollo.Esquema)
        .AddScheme<AuthenticationSchemeOptions, AutenticacionDesarrollo>(AutenticacionDesarrollo.Esquema, null);
}
else
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.Authority = config["Autenticacion:Authority"]
                ?? throw new InvalidOperationException("Falta Autenticacion:Authority");
            o.Audience = config["Autenticacion:Audience"];
            o.MapInboundClaims = false;
        });
}
builder.Services.AddAuthorization();

// --- Eventos de integración.
builder.Services.Configure<OpcionesPublicador>(config.GetSection("Publicador"));
builder.Services.AddHttpClient<IDestinoEventos, DestinoWebhooks>();
builder.Services.AddSingleton<PublicadorBandeja>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PublicadorBandeja>());

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseMiddleware<ResolverContextoMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    // Pantalla para probar la API desde el navegador: http://localhost:5080/scalar
    app.MapScalarApiReference();
}

app.MapGet("/salud", async (OrigenesBd bd, CancellationToken ct) =>
{
    await using var cmd = bd.Api.CreateCommand("SELECT 1");
    await cmd.ExecuteScalarAsync(ct);
    return Results.Ok(new { estado = "ok" });
});

app.MapModuloPacientes();
app.MapModuloCatalogos();
app.MapModuloTurnos();
app.MapModuloRrhh();

app.Run();

public partial class Program;
