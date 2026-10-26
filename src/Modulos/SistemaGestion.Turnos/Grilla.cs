namespace SistemaGestion.Turnos;

/// <summary>Regla semanal de una agenda, tal como se guarda.</summary>
internal sealed record ReglaAgenda(
    int DiaSemana, TimeOnly HoraDesde, TimeOnly HoraHasta, int DuracionMinutos, DateOnly VigenteDesde, DateOnly? VigenteHasta);

/// <summary>
/// Cálculo de los horarios que genera una agenda. Es puro (no toca la base) para poder probarlo aislado.
/// Las horas de la agenda son locales de la empresa; los horarios resultantes llevan su desplazamiento.
/// </summary>
internal static class Grilla
{
    public static int DiaIso(DateOnly fecha) => fecha.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)fecha.DayOfWeek;

    public static IEnumerable<(DateTimeOffset Inicio, DateTimeOffset Fin)> Horarios(
        ReglaAgenda a, TimeZoneInfo zona, DateOnly desde, DateOnly hasta)
    {
        var primero = desde > a.VigenteDesde ? desde : a.VigenteDesde;
        var ultimo = a.VigenteHasta is { } vh && vh < hasta ? vh : hasta;
        var inicioMin = a.HoraDesde.Hour * 60 + a.HoraDesde.Minute;
        var finMin = a.HoraHasta.Hour * 60 + a.HoraHasta.Minute;

        for (var dia = primero; dia <= ultimo; dia = dia.AddDays(1))
        {
            if (DiaIso(dia) != a.DiaSemana)
                continue;
            for (var m = inicioMin; m + a.DuracionMinutos <= finMin; m += a.DuracionMinutos)
            {
                var local = dia.ToDateTime(new TimeOnly(m / 60, m % 60));
                var inicio = new DateTimeOffset(local, zona.GetUtcOffset(local));
                yield return (inicio, inicio.AddMinutes(a.DuracionMinutos));
            }
        }
    }

    /// <summary>True si <paramref name="inicio"/> coincide exactamente con un horario de la agenda.</summary>
    public static bool EsHorarioDeLaAgenda(ReglaAgenda a, TimeZoneInfo zona, DateTimeOffset inicio)
    {
        var local = TimeZoneInfo.ConvertTime(inicio, zona);
        var dia = DateOnly.FromDateTime(local.DateTime);
        if (dia < a.VigenteDesde || (a.VigenteHasta is { } vh && dia > vh) || DiaIso(dia) != a.DiaSemana)
            return false;
        if (local.Second != 0 || local.Millisecond != 0)
            return false;
        var m = local.Hour * 60 + local.Minute;
        var inicioMin = a.HoraDesde.Hour * 60 + a.HoraDesde.Minute;
        var finMin = a.HoraHasta.Hour * 60 + a.HoraHasta.Minute;
        return m >= inicioMin && m + a.DuracionMinutos <= finMin && (m - inicioMin) % a.DuracionMinutos == 0;
    }
}
