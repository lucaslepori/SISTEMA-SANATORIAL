using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SistemaGestion.Compartido.Eventos;

namespace SistemaGestion.Pacientes.Tests;

[Collection(ColeccionBase.Nombre)]
public sealed class PacientesApiTests(BaseDePrueba bd)
{
    private const string Admision = "admision.local";
    private const string Medica = "medica.local";
    private const string Admin = "admin.local";

    private static object NuevoPaciente(string dni, string apellidos = "Pérez") => new
    {
        apellidos,
        nombres = "Ana María",
        fechaNacimiento = "1985-04-12",
        sexoRegistral = "F",
        identificadores = new[] { new { sistema = "DNI", valor = dni } },
    };

    private static string DniUnico() => Random.Shared.Next(10_000_000, 99_999_999).ToString();

    private async Task<PacienteDto> CrearAsync(string dni, string apellidos = "Pérez")
    {
        var r = await bd.Cliente(Admision, BaseDePrueba.Empresa1).PostAsJsonAsync("/api/v1/pacientes", NuevoPaciente(dni, apellidos));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<PacienteDto>())!;
    }

    [Fact]
    public async Task Crear_y_leer_registra_auditoria_y_evento()
    {
        var dni = DniUnico();
        var creado = await CrearAsync(dni);
        Assert.Equal(1, creado.Version);
        Assert.Equal(new DateOnly(1985, 4, 12), creado.FechaNacimiento);
        Assert.Equal(dni, Assert.Single(creado.Identificadores).Valor);

        var leido = await bd.Cliente(Medica, BaseDePrueba.Empresa1).GetFromJsonAsync<PacienteDto>($"/api/v1/pacientes/{creado.Id}");
        Assert.Equal(creado, leido! with { Identificadores = creado.Identificadores });

        var acciones = await bd.EscalarAsync<string>(
            $"SELECT string_agg(accion, ',' ORDER BY id) FROM auditoria.eventos WHERE recurso_id = '{creado.Id}'");
        Assert.Equal("CREAR,LEER", acciones);
        var eventos = await bd.EscalarAsync<long>(
            $"SELECT count(*) FROM integracion.bandeja_salida WHERE agregado_id = '{creado.Id}' AND tipo = 'pacientes.paciente_creado'");
        Assert.Equal(1, eventos);
    }

    [Fact]
    public async Task Otra_empresa_no_ve_el_paciente()
    {
        var dni = DniUnico();
        var creado = await CrearAsync(dni);
        var otra = bd.Cliente(Admin, BaseDePrueba.Empresa2);

        Assert.Equal(HttpStatusCode.NotFound, (await otra.GetAsync($"/api/v1/pacientes/{creado.Id}")).StatusCode);
        var busqueda = await otra.GetFromJsonAsync<List<PacienteDto>>($"/api/v1/pacientes?documento={dni}");
        Assert.Empty(busqueda!);
    }

    [Fact]
    public async Task Mismo_dni_en_otra_empresa_es_un_paciente_distinto()
    {
        var dni = DniUnico();
        await CrearAsync(dni);
        var r = await bd.Cliente(Admin, BaseDePrueba.Empresa2).PostAsJsonAsync("/api/v1/pacientes", NuevoPaciente(dni));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
    }

    [Fact]
    public async Task Usuario_sin_acceso_a_la_empresa_es_rechazado()
    {
        var r = await bd.Cliente(Admision, BaseDePrueba.Empresa2).GetAsync("/api/v1/pacientes?apellidos=Pe");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var anonimo = bd.Fabrica.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/v1/pacientes?apellidos=Pe")).StatusCode);
    }

    [Fact]
    public async Task Profesional_no_puede_dar_de_alta()
    {
        var r = await bd.Cliente(Medica, BaseDePrueba.Empresa1).PostAsJsonAsync("/api/v1/pacientes", NuevoPaciente(DniUnico()));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Dni_repetido_en_la_misma_empresa_devuelve_409()
    {
        var dni = DniUnico();
        await CrearAsync(dni);
        var r = await bd.Cliente(Admision, BaseDePrueba.Empresa1).PostAsJsonAsync("/api/v1/pacientes", NuevoPaciente(dni));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Datos_invalidos_devuelven_400_con_detalle()
    {
        var r = await bd.Cliente(Admision, BaseDePrueba.Empresa1).PostAsJsonAsync("/api/v1/pacientes", new
        {
            apellidos = "",
            nombres = "Ana",
            fechaNacimiento = "2999-01-01",
            sexoRegistral = "Z",
            identificadores = new[] { new { sistema = "DNI", valor = "12.345.678" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var cuerpo = await r.Content.ReadAsStringAsync();
        Assert.Contains("apellidos", cuerpo);
        Assert.Contains("fechaNacimiento", cuerpo);
        Assert.Contains("sexoRegistral", cuerpo);
        Assert.Contains("identificadores[0]", cuerpo);
    }

    [Fact]
    public async Task Modificar_exige_la_version_vigente()
    {
        var creado = await CrearAsync(DniUnico());
        var cliente = bd.Cliente(Admision, BaseDePrueba.Empresa1);
        var cambio = new
        {
            apellidos = "Pérez Gómez",
            nombres = "Ana María",
            nombreAutopercibido = "Ana",
            fechaNacimiento = "1985-04-12",
            sexoRegistral = "F",
        };

        var sinVersion = await cliente.PutAsJsonAsync($"/api/v1/pacientes/{creado.Id}", cambio);
        Assert.Equal((HttpStatusCode)428, sinVersion.StatusCode);

        var ok = await Put(cliente, creado.Id, 1, cambio);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var modificado = (await ok.Content.ReadFromJsonAsync<PacienteDto>())!;
        Assert.Equal(2, modificado.Version);
        Assert.Equal("Pérez Gómez", modificado.Apellidos);

        var vieja = await Put(cliente, creado.Id, 1, cambio);
        Assert.Equal(HttpStatusCode.PreconditionFailed, vieja.StatusCode);

        var detalle = await bd.EscalarAsync<string>(
            $"SELECT detalle->'anterior'->>'apellidos' FROM auditoria.eventos WHERE recurso_id = '{creado.Id}' AND accion = 'MODIFICAR'");
        Assert.Equal("Pérez", detalle);
    }

    [Fact]
    public async Task Buscar_sin_criterio_suficiente_devuelve_400()
    {
        var r = await bd.Cliente(Admision, BaseDePrueba.Empresa1).GetAsync("/api/v1/pacientes?apellidos=P");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Buscar_por_apellido_encuentra_y_audita()
    {
        var apellido = "Zz" + Guid.NewGuid().ToString("N")[..8];
        var creado = await CrearAsync(DniUnico(), apellido);
        var r = await bd.Cliente(Medica, BaseDePrueba.Empresa1)
            .GetFromJsonAsync<List<PacienteDto>>($"/api/v1/pacientes?apellidos={apellido[..5].ToLowerInvariant()}");
        Assert.Contains(r!, p => p.Id == creado.Id);
        var auditado = await bd.EscalarAsync<long>(
            $"SELECT count(*) FROM auditoria.eventos WHERE accion = 'BUSCAR' AND detalle->'resultados' ? '{creado.Id}'");
        Assert.Equal(1, auditado);
    }

    [Fact]
    public async Task La_base_impide_borrar_pacientes_y_alterar_la_auditoria()
    {
        var creado = await CrearAsync(DniUnico());

        // La aplicación no tiene permiso de DELETE.
        var exApi = await Assert.ThrowsAsync<PostgresException>(() => BaseDePrueba.EjecutarAsync(bd.CadenaApi, $"""
            BEGIN;
            SELECT set_config('app.empresa_id', '{BaseDePrueba.Empresa1}', true);
            DELETE FROM pacientes.pacientes WHERE id = '{creado.Id}';
            COMMIT;
            """));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exApi.SqlState);

        // Ni siquiera el dueño de la base puede modificar o borrar la auditoría.
        var exUpdate = await Assert.ThrowsAsync<PostgresException>(() =>
            BaseDePrueba.EjecutarAsync(bd.CadenaAdmin, "UPDATE auditoria.eventos SET accion = 'LEER'"));
        Assert.Contains("sólo agregar", exUpdate.MessageText);
        await Assert.ThrowsAsync<PostgresException>(() =>
            BaseDePrueba.EjecutarAsync(bd.CadenaAdmin, "DELETE FROM auditoria.eventos"));
    }

    [Fact]
    public async Task Sin_empresa_fijada_la_base_no_devuelve_filas()
    {
        await CrearAsync(DniUnico());
        await using var c = new NpgsqlConnection(bd.CadenaApi);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pacientes.pacientes", c);
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task El_publicador_entrega_los_eventos_y_los_marca()
    {
        var creado = await CrearAsync(DniUnico());
        var destino = new DestinoEnMemoria();
        var publicador = ActivatorUtilities.CreateInstance<PublicadorBandeja>(bd.Fabrica.Services, destino);

        while (await publicador.PublicarLoteAsync(CancellationToken.None) > 0) { }

        var evento = Assert.Single(destino.Recibidos, e => e.AgregadoId == creado.Id);
        Assert.Equal("pacientes.paciente_creado", evento.Tipo);
        Assert.Equal(Guid.Parse(BaseDePrueba.Empresa1), evento.EmpresaId);
        Assert.Equal("Pérez", evento.Datos.GetProperty("apellidos").GetString());
        var pendientes = await bd.EscalarAsync<long>(
            $"SELECT count(*) FROM integracion.bandeja_salida WHERE agregado_id = '{creado.Id}' AND publicado_en IS NULL");
        Assert.Equal(0, pendientes);
    }

    private static Task<HttpResponseMessage> Put(HttpClient c, Guid id, int version, object cuerpo)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/pacientes/{id}") { Content = JsonContent.Create(cuerpo) };
        req.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return c.SendAsync(req);
    }

    private sealed class DestinoEnMemoria : IDestinoEventos
    {
        public List<EventoIntegracion> Recibidos { get; } = [];

        public Task EntregarAsync(EventoIntegracion evento, CancellationToken ct)
        {
            Recibidos.Add(evento);
            return Task.CompletedTask;
        }
    }
}
