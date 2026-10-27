using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using SistemaGestion.Compartido;

namespace SistemaGestion.Catalogos;

/// <summary>
/// Datos de referencia que usan los demás módulos. Sólo el rol administrador los escribe;
/// cualquier usuario con acceso a la empresa los lee. No se borran: se desactivan.
/// </summary>
public static class ModuloCatalogos
{
    public static IEndpointRouteBuilder MapModuloCatalogos(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/catalogos").WithTags("Catálogos");

        g.MapGet("/servicios", (ContextoSolicitud c, OrigenesBd b, CancellationToken ct) => ListarNombradosAsync("servicios", c, b, ct));
        g.MapPost("/servicios", (CrearNombradoSolicitud s, ContextoSolicitud c, OrigenesBd b, CancellationToken ct) =>
            CrearNombradoAsync("servicios", "servicio", s, c, b, ct));
        g.MapGet("/especialidades", (ContextoSolicitud c, OrigenesBd b, CancellationToken ct) => ListarNombradosAsync("especialidades", c, b, ct));
        g.MapPost("/especialidades", (CrearNombradoSolicitud s, ContextoSolicitud c, OrigenesBd b, CancellationToken ct) =>
            CrearNombradoAsync("especialidades", "especialidad", s, c, b, ct));

        g.MapGet("/sedes", ListarSedesAsync);
        g.MapPost("/sedes", CrearSedeAsync);
        g.MapGet("/consultorios", ListarConsultoriosAsync);
        g.MapPost("/consultorios", CrearConsultorioAsync);
        g.MapGet("/coberturas", ListarCoberturasAsync);
        g.MapPost("/coberturas", CrearCoberturaAsync);
        g.MapGet("/coberturas/{coberturaId:guid}/planes", ListarPlanesAsync);
        g.MapPost("/coberturas/{coberturaId:guid}/planes", CrearPlanAsync);
        g.MapGet("/profesionales", ListarProfesionalesAsync);
        g.MapPost("/profesionales", CrearProfesionalAsync);
        return app;
    }

    // --- Servicios y especialidades: misma forma, distinta tabla.

    private static async Task<IResult> ListarNombradosAsync(string tabla, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var filas = await s.Conexion.QueryAsync<NombradoDto>(new CommandDefinition(
            $"SELECT id, nombre, activo, geclisa_id AS GeclisaId FROM catalogos.{tabla} ORDER BY nombre",
            transaction: s.Transaccion, cancellationToken: ct));
        return Results.Ok(filas);
    }

    private static async Task<IResult> CrearNombradoAsync(
        string tabla, string recurso, CrearNombradoSolicitud x, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!ctx.TieneAlgunRol(Roles.Administrador))
            return Respuestas.SinPermiso("Se requiere el rol administrador");
        if (string.IsNullOrWhiteSpace(x.Nombre))
            return Respuestas.Invalido("nombre", "El nombre es obligatorio.");

        var dto = new NombradoDto(Guid.CreateVersion7(), x.Nombre.Trim(), true, x.GeclisaId);
        return await InsertarAsync(ctx, bd, recurso, dto.Id, $"/api/v1/catalogos/{tabla}", dto, ct, s =>
            s.Conexion.ExecuteAsync(new CommandDefinition(
                $"INSERT INTO catalogos.{tabla} (id, empresa_id, nombre, geclisa_id) VALUES (@Id, @empresa, @Nombre, @GeclisaId)",
                new { dto.Id, empresa = ctx.EmpresaId, dto.Nombre, dto.GeclisaId }, s.Transaccion, cancellationToken: ct)));
    }

    // --- Sedes y consultorios.

    private static async Task<IResult> ListarSedesAsync(ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        return Results.Ok(await s.Conexion.QueryAsync<SedeDto>(new CommandDefinition(
            "SELECT id, nombre, direccion, activo FROM catalogos.sedes ORDER BY nombre",
            transaction: s.Transaccion, cancellationToken: ct)));
    }

    private static async Task<IResult> CrearSedeAsync(CrearSedeSolicitud x, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!ctx.TieneAlgunRol(Roles.Administrador))
            return Respuestas.SinPermiso("Se requiere el rol administrador");
        if (string.IsNullOrWhiteSpace(x.Nombre))
            return Respuestas.Invalido("nombre", "El nombre es obligatorio.");

        var dto = new SedeDto(Guid.CreateVersion7(), x.Nombre.Trim(), Limpiar(x.Direccion), true);
        return await InsertarAsync(ctx, bd, "sede", dto.Id, "/api/v1/catalogos/sedes", dto, ct, s =>
            s.Conexion.ExecuteAsync(new CommandDefinition(
                "INSERT INTO catalogos.sedes (id, empresa_id, nombre, direccion) VALUES (@Id, @empresa, @Nombre, @Direccion)",
                new { dto.Id, empresa = ctx.EmpresaId, dto.Nombre, dto.Direccion }, s.Transaccion, cancellationToken: ct)));
    }

    private static async Task<IResult> ListarConsultoriosAsync(Guid? sedeId, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        return Results.Ok(await s.Conexion.QueryAsync<ConsultorioDto>(new CommandDefinition("""
            SELECT id, sede_id AS SedeId, nombre, activo, geclisa_id AS GeclisaId
            FROM catalogos.consultorios
            WHERE @sedeId::uuid IS NULL OR sede_id = @sedeId
            ORDER BY nombre
            """, new { sedeId }, s.Transaccion, cancellationToken: ct)));
    }

    private static async Task<IResult> CrearConsultorioAsync(
        CrearConsultorioSolicitud x, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!ctx.TieneAlgunRol(Roles.Administrador))
            return Respuestas.SinPermiso("Se requiere el rol administrador");
        if (string.IsNullOrWhiteSpace(x.Nombre))
            return Respuestas.Invalido("nombre", "El nombre es obligatorio.");

        var dto = new ConsultorioDto(Guid.CreateVersion7(), x.SedeId, x.Nombre.Trim(), true, x.GeclisaId);
        return await InsertarAsync(ctx, bd, "consultorio", dto.Id, "/api/v1/catalogos/consultorios", dto, ct, s =>
            s.Conexion.ExecuteAsync(new CommandDefinition("""
                INSERT INTO catalogos.consultorios (id, empresa_id, sede_id, nombre, geclisa_id)
                VALUES (@Id, @empresa, @SedeId, @Nombre, @GeclisaId)
                """, new { dto.Id, empresa = ctx.EmpresaId, dto.SedeId, dto.Nombre, dto.GeclisaId }, s.Transaccion, cancellationToken: ct)));
    }

    // --- Coberturas y planes.

    private static async Task<IResult> ListarCoberturasAsync(ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        return Results.Ok(await s.Conexion.QueryAsync<CoberturaDto>(new CommandDefinition(
            "SELECT id, nombre, sigla, tipo, activo, geclisa_id AS GeclisaId FROM catalogos.coberturas ORDER BY nombre",
            transaction: s.Transaccion, cancellationToken: ct)));
    }

    private static async Task<IResult> CrearCoberturaAsync(
        CrearCoberturaSolicitud x, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!ctx.TieneAlgunRol(Roles.Administrador))
            return Respuestas.SinPermiso("Se requiere el rol administrador");
        if (string.IsNullOrWhiteSpace(x.Nombre))
            return Respuestas.Invalido("nombre", "El nombre es obligatorio.");
        if (!TiposCobertura.Todos.Contains(x.Tipo ?? ""))
            return Respuestas.Invalido("tipo", $"Tipo inválido. Valores admitidos: {string.Join(", ", TiposCobertura.Todos)}.");

        var dto = new CoberturaDto(Guid.CreateVersion7(), x.Nombre.Trim(), Limpiar(x.Sigla), x.Tipo!, true, x.GeclisaId);
        return await InsertarAsync(ctx, bd, "cobertura", dto.Id, "/api/v1/catalogos/coberturas", dto, ct, s =>
            s.Conexion.ExecuteAsync(new CommandDefinition("""
                INSERT INTO catalogos.coberturas (id, empresa_id, nombre, sigla, tipo, geclisa_id)
                VALUES (@Id, @empresa, @Nombre, @Sigla, @Tipo, @GeclisaId)
                """, new { dto.Id, empresa = ctx.EmpresaId, dto.Nombre, dto.Sigla, dto.Tipo, dto.GeclisaId },
                s.Transaccion, cancellationToken: ct)));
    }

    private static async Task<IResult> ListarPlanesAsync(Guid coberturaId, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        return Results.Ok(await s.Conexion.QueryAsync<PlanDto>(new CommandDefinition("""
            SELECT id, cobertura_id AS CoberturaId, nombre, activo, geclisa_id AS GeclisaId
            FROM catalogos.planes WHERE cobertura_id = @coberturaId ORDER BY nombre
            """, new { coberturaId }, s.Transaccion, cancellationToken: ct)));
    }

    private static async Task<IResult> CrearPlanAsync(
        Guid coberturaId, CrearPlanSolicitud x, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!ctx.TieneAlgunRol(Roles.Administrador))
            return Respuestas.SinPermiso("Se requiere el rol administrador");
        if (string.IsNullOrWhiteSpace(x.Nombre))
            return Respuestas.Invalido("nombre", "El nombre es obligatorio.");

        var dto = new PlanDto(Guid.CreateVersion7(), coberturaId, x.Nombre.Trim(), true, x.GeclisaId);
        return await InsertarAsync(ctx, bd, "plan", dto.Id, $"/api/v1/catalogos/coberturas/{coberturaId}/planes", dto, ct, s =>
            s.Conexion.ExecuteAsync(new CommandDefinition("""
                INSERT INTO catalogos.planes (id, empresa_id, cobertura_id, nombre, geclisa_id)
                VALUES (@Id, @empresa, @CoberturaId, @Nombre, @GeclisaId)
                """, new { dto.Id, empresa = ctx.EmpresaId, dto.CoberturaId, dto.Nombre, dto.GeclisaId },
                s.Transaccion, cancellationToken: ct)));
    }

    // --- Profesionales.

    private static async Task<IResult> ListarProfesionalesAsync(
        Guid? especialidadId, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        var filas = await s.Conexion.QueryAsync<FilaProfesional>(new CommandDefinition("""
            SELECT p.id, p.apellidos, p.nombres, p.matricula_nacional, p.matricula_provincial, p.usuario_id,
                   p.activo, p.geclisa_id,
                   coalesce(array_agg(pe.especialidad_id) FILTER (WHERE pe.especialidad_id IS NOT NULL), '{}') AS especialidades
            FROM catalogos.profesionales p
            LEFT JOIN catalogos.profesionales_especialidades pe ON pe.profesional_id = p.id
            WHERE @especialidadId::uuid IS NULL OR EXISTS (
                SELECT 1 FROM catalogos.profesionales_especialidades x
                WHERE x.profesional_id = p.id AND x.especialidad_id = @especialidadId)
            GROUP BY p.id
            ORDER BY p.apellidos, p.nombres
            """, new { especialidadId }, s.Transaccion, cancellationToken: ct));
        return Results.Ok(filas.Select(f => f.ADto()));
    }

    private static async Task<IResult> CrearProfesionalAsync(
        CrearProfesionalSolicitud x, ContextoSolicitud ctx, OrigenesBd bd, CancellationToken ct)
    {
        if (!ctx.TieneAlgunRol(Roles.Administrador))
            return Respuestas.SinPermiso("Se requiere el rol administrador");
        var errores = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(x.Apellidos))
            errores["apellidos"] = ["Los apellidos son obligatorios."];
        if (string.IsNullOrWhiteSpace(x.Nombres))
            errores["nombres"] = ["Los nombres son obligatorios."];
        if (string.IsNullOrWhiteSpace(x.MatriculaNacional) && string.IsNullOrWhiteSpace(x.MatriculaProvincial))
            errores["matricula"] = ["Indicá la matrícula nacional o la provincial."];
        if (errores.Count > 0)
            return Results.ValidationProblem(errores);

        var especialidades = (x.EspecialidadIds ?? []).Distinct().ToArray();
        var dto = new ProfesionalDto(Guid.CreateVersion7(), x.Apellidos.Trim(), x.Nombres.Trim(), Limpiar(x.MatriculaNacional),
            Limpiar(x.MatriculaProvincial), x.UsuarioId, true, x.GeclisaId, especialidades);
        return await InsertarAsync(ctx, bd, "profesional", dto.Id, "/api/v1/catalogos/profesionales", dto, ct, async s =>
        {
            await s.Conexion.ExecuteAsync(new CommandDefinition("""
                INSERT INTO catalogos.profesionales
                    (id, empresa_id, apellidos, nombres, matricula_nacional, matricula_provincial, usuario_id, geclisa_id)
                VALUES (@Id, @empresa, @Apellidos, @Nombres, @MatriculaNacional, @MatriculaProvincial, @UsuarioId, @GeclisaId)
                """, new
            {
                dto.Id, empresa = ctx.EmpresaId, dto.Apellidos, dto.Nombres, dto.MatriculaNacional,
                dto.MatriculaProvincial, dto.UsuarioId, dto.GeclisaId,
            }, s.Transaccion, cancellationToken: ct));
            foreach (var esp in especialidades)
            {
                await s.Conexion.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO catalogos.profesionales_especialidades (empresa_id, profesional_id, especialidad_id)
                    VALUES (@empresa, @profesional, @esp)
                    """, new { empresa = ctx.EmpresaId, profesional = dto.Id, esp }, s.Transaccion, cancellationToken: ct));
            }
        });
    }

    // --- Común.

    private static async Task<IResult> InsertarAsync<T>(
        ContextoSolicitud ctx, OrigenesBd bd, string recurso, Guid id, string ruta, T dto, CancellationToken ct,
        Func<SesionBd, Task> insertar)
    {
        await using var s = await SesionBd.AbrirAsync(bd.Api, ctx, ct);
        try
        {
            await insertar(s);
        }
        catch (PostgresException ex) when (Respuestas.EsDuplicado(ex))
        {
            return Respuestas.Conflicto($"Ya existe un {recurso} con ese nombre o código de Geclisa");
        }
        catch (PostgresException ex) when (Respuestas.EsReferenciaInvalida(ex))
        {
            return Respuestas.NoProcesable($"El {recurso} hace referencia a un dato que no existe en esta empresa");
        }
        await Auditoria.RegistrarAsync(s, AccionAuditoria.Crear, recurso, id, dto, ct);
        await s.ConfirmarAsync(ct);
        return Results.Created($"{ruta}/{id}", dto);
    }

    private static string? Limpiar(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    // Clase con propiedades (no record posicional) porque Dapper no mapea uuid[] por constructor.
    private sealed class FilaProfesional
    {
        public Guid id { get; init; }
        public string apellidos { get; init; } = "";
        public string nombres { get; init; } = "";
        public string? matricula_nacional { get; init; }
        public string? matricula_provincial { get; init; }
        public Guid? usuario_id { get; init; }
        public bool activo { get; init; }
        public int? geclisa_id { get; init; }
        public Guid[] especialidades { get; init; } = [];

        public ProfesionalDto ADto() => new(id, apellidos, nombres, matricula_nacional, matricula_provincial, usuario_id,
            activo, geclisa_id, especialidades);
    }
}
