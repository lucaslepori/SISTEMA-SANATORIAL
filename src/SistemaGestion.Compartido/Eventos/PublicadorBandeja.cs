using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SistemaGestion.Compartido.Eventos;

/// <summary>Evento tal como lo reciben los sistemas suscriptos (otros proyectos, el puente con Geclisa).</summary>
public sealed record EventoIntegracion(
    Guid Id, Guid EmpresaId, string Tipo, int Version, string AgregadoTipo, Guid AgregadoId,
    JsonElement Datos, DateTimeOffset OcurridoEn);

public interface IDestinoEventos
{
    Task EntregarAsync(EventoIntegracion evento, CancellationToken ct);
}

public sealed class OpcionesPublicador
{
    public bool Habilitado { get; set; } = true;
    public int IntervaloSegundos { get; set; } = 5;
    public int TamanoLote { get; set; } = 100;
    /// <summary>URLs que reciben cada evento por POST. Vacío: los eventos sólo se registran en el log.</summary>
    public List<string> Webhooks { get; set; } = [];
}

/// <summary>Envía cada evento por POST JSON a los webhooks configurados.</summary>
public sealed class DestinoWebhooks(HttpClient http, IOptions<OpcionesPublicador> opciones, ILogger<DestinoWebhooks> log)
    : IDestinoEventos
{
    public async Task EntregarAsync(EventoIntegracion evento, CancellationToken ct)
    {
        if (opciones.Value.Webhooks.Count == 0)
        {
            log.LogInformation("Evento {Tipo} {Id} sin suscriptores", evento.Tipo, evento.Id);
            return;
        }
        foreach (var url in opciones.Value.Webhooks)
        {
            using var respuesta = await http.PostAsJsonAsync(url, evento, Json.Opciones, ct);
            respuesta.EnsureSuccessStatusCode();
        }
    }
}

/// <summary>
/// Lee la bandeja de salida y entrega los eventos en orden. Varias instancias pueden correr
/// a la vez: FOR UPDATE SKIP LOCKED evita que dos tomen el mismo evento.
/// La entrega es "al menos una vez": los suscriptores deben ignorar un Id repetido.
/// </summary>
public sealed class PublicadorBandeja(
    OrigenesBd origenes, IDestinoEventos destino, IOptions<OpcionesPublicador> opciones, ILogger<PublicadorBandeja> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!opciones.Value.Habilitado)
            return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var entregados = await PublicarLoteAsync(ct);
                if (entregados > 0)
                    continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Error al publicar la bandeja de salida");
            }
            await Task.Delay(TimeSpan.FromSeconds(opciones.Value.IntervaloSegundos), ct);
        }
    }

    public async Task<int> PublicarLoteAsync(CancellationToken ct)
    {
        await using var conexion = await origenes.Worker.OpenConnectionAsync(ct);
        await using var tx = await conexion.BeginTransactionAsync(ct);
        var filas = (await conexion.QueryAsync<FilaBandeja>(new CommandDefinition("""
            SELECT id, empresa_id, tipo, version, agregado_tipo, agregado_id, datos::text AS datos, ocurrido_en
            FROM integracion.bandeja_salida
            WHERE publicado_en IS NULL
            ORDER BY ocurrido_en, id
            LIMIT @Lote
            FOR UPDATE SKIP LOCKED
            """, new { Lote = opciones.Value.TamanoLote }, tx, cancellationToken: ct))).ToList();

        var entregados = 0;
        foreach (var f in filas)
        {
            var evento = new EventoIntegracion(f.id, f.empresa_id, f.tipo, f.version, f.agregado_tipo, f.agregado_id,
                JsonDocument.Parse(f.datos).RootElement.Clone(), f.ocurrido_en);
            try
            {
                await destino.EntregarAsync(evento, ct);
                await conexion.ExecuteAsync(new CommandDefinition(
                    "UPDATE integracion.bandeja_salida SET publicado_en = now(), intentos = intentos + 1, ultimo_error = NULL WHERE id = @id",
                    new { f.id }, tx, cancellationToken: ct));
                entregados++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "No se pudo entregar el evento {Id}; se reintenta en el próximo ciclo", f.id);
                await conexion.ExecuteAsync(new CommandDefinition(
                    "UPDATE integracion.bandeja_salida SET intentos = intentos + 1, ultimo_error = @error WHERE id = @id",
                    new { f.id, error = ex.Message }, tx, cancellationToken: ct));
                // Se corta el lote para respetar el orden de los eventos.
                break;
            }
        }
        await tx.CommitAsync(ct);
        return entregados;
    }

    private sealed record FilaBandeja(
        Guid id, Guid empresa_id, string tipo, int version, string agregado_tipo, Guid agregado_id,
        string datos, DateTime ocurrido_en);
}
