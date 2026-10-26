using Dapper;
using SistemaGestion.Compartido;

namespace SistemaGestion.Pacientes;

/// <summary>Acceso a pacientes.*. La seguridad por fila ya limita todo a la empresa de la sesión.</summary>
internal static class RepositorioPacientes
{
    private const string Columnas = """
        id, apellidos, nombres, nombre_autopercibido, fecha_nacimiento, sexo_registral,
        telefono, email, activo, version
        """;

    public static async Task<PacienteDto?> ObtenerAsync(SesionBd s, Guid id, CancellationToken ct)
    {
        var fila = await s.Conexion.QuerySingleOrDefaultAsync<FilaPaciente>(new CommandDefinition(
            $"SELECT {Columnas} FROM pacientes.pacientes WHERE id = @id", new { id }, s.Transaccion, cancellationToken: ct));
        if (fila is null)
            return null;
        var ids = await IdentificadoresAsync(s, [id], ct);
        return fila.ADto(ids.GetValueOrDefault(id, []));
    }

    public static async Task<IReadOnlyList<PacienteDto>> BuscarAsync(
        SesionBd s, string? documento, string? apellidos, int limite, CancellationToken ct)
    {
        var filas = (await s.Conexion.QueryAsync<FilaPaciente>(new CommandDefinition($"""
            SELECT {Columnas}
            FROM pacientes.pacientes p
            WHERE p.activo
              AND (@documento::text IS NULL OR EXISTS (
                    SELECT 1 FROM pacientes.identificadores i
                    WHERE i.paciente_id = p.id AND i.valor = @documento))
              AND (@apellidos::text IS NULL OR lower(p.apellidos) LIKE lower(@apellidos) || '%')
            ORDER BY lower(p.apellidos), lower(p.nombres), p.id
            LIMIT @limite
            """, new { documento, apellidos, limite }, s.Transaccion, cancellationToken: ct))).ToList();
        var ids = await IdentificadoresAsync(s, filas.Select(f => f.id).ToArray(), ct);
        return filas.Select(f => f.ADto(ids.GetValueOrDefault(f.id, []))).ToList();
    }

    public static async Task CrearAsync(SesionBd s, Guid id, CrearPacienteSolicitud x, CancellationToken ct)
    {
        await s.Conexion.ExecuteAsync(new CommandDefinition("""
            INSERT INTO pacientes.pacientes
                (id, empresa_id, apellidos, nombres, nombre_autopercibido, fecha_nacimiento, sexo_registral,
                 telefono, email, creado_por)
            VALUES
                (@id, @empresa, @Apellidos, @Nombres, @NombreAutopercibido, @nacimiento::date, @SexoRegistral,
                 @Telefono, @Email, @usuario)
            """, new
        {
            id,
            empresa = s.Contexto.EmpresaId,
            usuario = s.Contexto.UsuarioId,
            Apellidos = x.Apellidos.Trim(),
            Nombres = x.Nombres.Trim(),
            NombreAutopercibido = Limpiar(x.NombreAutopercibido),
            nacimiento = x.FechaNacimiento.ToDateTime(TimeOnly.MinValue),
            x.SexoRegistral,
            Telefono = Limpiar(x.Telefono),
            Email = Limpiar(x.Email),
        }, s.Transaccion, cancellationToken: ct));

        foreach (var ident in x.Identificadores ?? [])
        {
            await s.Conexion.ExecuteAsync(new CommandDefinition("""
                INSERT INTO pacientes.identificadores (id, empresa_id, paciente_id, sistema, valor)
                VALUES (@id, @empresa, @paciente, @Sistema, @Valor)
                """, new
            {
                id = Guid.CreateVersion7(),
                empresa = s.Contexto.EmpresaId,
                paciente = id,
                ident.Sistema,
                Valor = ident.Valor.Trim(),
            }, s.Transaccion, cancellationToken: ct));
        }
    }

    /// <summary>Devuelve la nueva versión, o null si el paciente no existe o la versión esperada no coincide.</summary>
    public static Task<int?> ModificarAsync(
        SesionBd s, Guid id, int versionEsperada, ModificarPacienteSolicitud x, CancellationToken ct) =>
        s.Conexion.QuerySingleOrDefaultAsync<int?>(new CommandDefinition("""
            UPDATE pacientes.pacientes SET
                apellidos = @Apellidos, nombres = @Nombres, nombre_autopercibido = @NombreAutopercibido,
                fecha_nacimiento = @nacimiento::date, sexo_registral = @SexoRegistral,
                telefono = @Telefono, email = @Email,
                version = version + 1, modificado_en = now(), modificado_por = @usuario
            WHERE id = @id AND version = @versionEsperada
            RETURNING version
            """, new
        {
            id,
            versionEsperada,
            usuario = s.Contexto.UsuarioId,
            Apellidos = x.Apellidos.Trim(),
            Nombres = x.Nombres.Trim(),
            NombreAutopercibido = Limpiar(x.NombreAutopercibido),
            nacimiento = x.FechaNacimiento.ToDateTime(TimeOnly.MinValue),
            x.SexoRegistral,
            Telefono = Limpiar(x.Telefono),
            Email = Limpiar(x.Email),
        }, s.Transaccion, cancellationToken: ct));

    private static async Task<Dictionary<Guid, List<IdentificadorDto>>> IdentificadoresAsync(
        SesionBd s, Guid[] pacientes, CancellationToken ct)
    {
        if (pacientes.Length == 0)
            return [];
        var filas = await s.Conexion.QueryAsync<(Guid PacienteId, string Sistema, string Valor)>(new CommandDefinition(
            "SELECT paciente_id, sistema, valor FROM pacientes.identificadores WHERE paciente_id = ANY(@pacientes) ORDER BY sistema",
            new { pacientes }, s.Transaccion, cancellationToken: ct));
        return filas.GroupBy(f => f.PacienteId)
            .ToDictionary(g => g.Key, g => g.Select(f => new IdentificadorDto(f.Sistema, f.Valor)).ToList());
    }

    private static string? Limpiar(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    private sealed record FilaPaciente(
        Guid id, string apellidos, string nombres, string? nombre_autopercibido, DateOnly fecha_nacimiento,
        string sexo_registral, string? telefono, string? email, bool activo, int version)
    {
        public PacienteDto ADto(IReadOnlyList<IdentificadorDto> identificadores) => new(
            id, apellidos, nombres, nombre_autopercibido, fecha_nacimiento, sexo_registral,
            telefono, email, activo, version, identificadores);
    }
}
