using Dapper;
using Microsoft.Data.SqlClient;

namespace SistemaGestion.Importador;

/// <summary>
/// Lee Geclisa con consultas SELECT, sin escribir nada. Conviene usar un login de SQL Server
/// con permiso de sólo lectura (db_datareader) para que eso no dependa de este código.
/// NO PROBADO contra una base real de Geclisa: las consultas salen del script del esquema.
/// </summary>
public sealed class FuenteSqlServer(string cadenaConexion) : IFuenteGeclisa
{
    public async Task<DatosGeclisa> LeerAsync(DateOnly desde, bool todasLasFichas, CancellationToken ct)
    {
        await using var cn = new SqlConnection(cadenaConexion);
        await cn.OpenAsync(ct);
        var p = new { desde = desde.ToDateTime(TimeOnly.MinValue) };

        async Task<List<T>> Leer<T>(string sql) =>
            (await cn.QueryAsync<T>(new CommandDefinition(sql, p, commandTimeout: 600, cancellationToken: ct))).AsList();

        return new DatosGeclisa
        {
            Ubicaciones = await Leer<GUbicacion>(
                "SELECT Ubic_id AS Id, Ubic_nombre AS Nombre, Ubic_direccion AS Direccion FROM dbo.Ubicaciones"),
            Consultorios = await Leer<GConsultorio>(
                "SELECT Cons_id AS Id, Cons_Nombre AS Nombre, Ubic_id AS UbicId FROM dbo.Consultorios"),
            Servicios = await Leer<GServicio>(
                "SELECT Serv_Id AS Id, Serv_Nombre AS Nombre, serv_inactivo AS Inactivo FROM dbo.Servicios"),
            Especialidades = await Leer<GEspecialidad>(
                "SELECT esp_id AS Id, esp_nombre AS Nombre FROM dbo.Especialidades"),
            Prestadores = await Leer<GPrestador>(
                "SELECT pre_id AS Id, pre_nombre AS Nombre, pre_matn AS MatriculaNacional, pre_matp AS MatriculaProvincial FROM dbo.Prestadores"),
            PrestadoresEspecialidades = await Leer<GPrestadorEspecialidad>(
                "SELECT DISTINCT pre_id AS PreId, esp_id AS EspId FROM dbo.PreEsp"),
            ObrasSociales = await Leer<GObraSocial>(
                "SELECT os_id AS Id, os_sigla AS Sigla, os_nombre AS Nombre, eliminado AS Eliminado, esParticular AS EsParticular FROM dbo.ObrasSociales"),
            Planes = await Leer<GPlan>(
                "SELECT plan_id AS Id, plan_nombre AS Nombre, os_id AS OsId, fec_fin AS FechaFin FROM dbo.Planes"),
            Cronogramas = await Leer<GCronograma>("""
                SELECT c.Cro_id AS Id, d.dia_nombre AS DiaNombre, c.Serv_id AS ServId, c.pre_id AS PreId,
                       c.Hs_Ini AS HsIni, c.Hs_Fin AS HsFin, c.Fec_Ini AS FechaIni, c.Fec_Fin AS FechaFin,
                       t.turt_tiempo AS Tiempo, c.Cons_id AS ConsId, c.CantPacientesSimultaneos AS PacientesSimultaneos
                FROM dbo.Cronograma c
                JOIN dbo.Dias d ON d.dia_id = c.dia_id
                JOIN dbo.TurnosTiempo t ON t.turt_id = c.turt_id
                WHERE c.Fec_Fin >= @desde
                """),
            CronogramasCoberturas = await Leer<GCronogramaCobertura>("""
                SELECT DISTINCT x.Cro_id AS CroId, x.Os_id AS OsId
                FROM dbo.CronogramaxOS x
                JOIN dbo.Cronograma c ON c.Cro_id = x.Cro_id
                WHERE c.Fec_Fin >= @desde AND x.Os_id IS NOT NULL
                """),
            Feriados = await Leer<GFeriado>("""
                SELECT f.Feriado_id AS Id, f.Fecha, RTRIM(f.Descripcion) AS Descripcion, f.Hs_Ini AS HsIni, f.Hs_Fin AS HsFin,
                       f.AplicaEn, (SELECT COUNT(*) FROM dbo.Feriados_PrestadoresExcluidos e WHERE e.Feriado_id = f.Feriado_id) AS PrestadoresExcluidos
                FROM dbo.Feriados f
                WHERE f.Fecha >= @desde
                """),
            Fichas = await Leer<GFicha>($"""
                SELECT f.Ficha_id AS Id, f.fic_ape AS Apellido, f.fic_nombre AS Nombre, RTRIM(td.td_nombre) AS TipoDoc,
                       RTRIM(f.fic_nrodoc) AS NroDoc, f.fic_sexo AS Sexo, f.fic_fechanac AS FechaNacimiento,
                       RTRIM(f.fic_tele) AS Telefono, RTRIM(f.fic_cel) AS Celular, f.fic_email AS Email
                FROM dbo.Ficha f
                LEFT JOIN dbo.TipoDoc td ON td.td_id = f.td_id
                {(todasLasFichas ? "" : "WHERE f.Ficha_id IN (SELECT ficha_id FROM dbo.Turnos WHERE tur_fecha >= @desde)")}
                """),
            Turnos = await Leer<GTurno>("""
                SELECT turno_id AS Id, serv_id AS ServId, tur_fecha AS Fecha, Hs_Ini AS HsIni, Hs_Fin AS HsFin, tur_tiempo AS Tiempo,
                       ficha_id AS FichaId, os_id AS OsId, plan_id AS PlanId, pre_id AS PreId, Cons_id AS ConsId, Cro_id AS CroId,
                       Nro_Afiliado AS NroAfiliado, tur_Obs AS Observaciones, confirmado AS Confirmado, esWeb AS EsWeb,
                       tur_FechaEspera AS FechaEspera, tur_FechaAtendido AS FechaAtendido
                FROM dbo.Turnos
                WHERE tur_fecha >= @desde
                """),
        };
    }
}
