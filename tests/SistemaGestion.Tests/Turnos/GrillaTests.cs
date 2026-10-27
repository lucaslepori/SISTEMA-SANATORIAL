using SistemaGestion.Turnos;

namespace SistemaGestion.Tests;

public sealed class GrillaTests
{
    private static readonly TimeZoneInfo BuenosAires = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");

    // Lunes 9 a 12, turnos de 30 minutos, vigente durante noviembre de 2026.
    private static readonly ReglaAgenda Regla = new(1, new TimeOnly(9, 0), new TimeOnly(12, 0), 30,
        new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));

    [Fact]
    public void Genera_los_horarios_de_los_lunes_dentro_de_la_vigencia()
    {
        var horarios = Grilla.Horarios(Regla, BuenosAires, new DateOnly(2026, 10, 26), new DateOnly(2026, 12, 7)).ToList();

        // Lunes 2, 9, 16, 23 y 30 de noviembre: 5 días de 6 horarios.
        Assert.Equal(30, horarios.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T09:00:00-03:00"), horarios[0].Inicio);
        Assert.Equal(DateTimeOffset.Parse("2026-11-30T12:00:00-03:00"), horarios[^1].Fin);
    }

    [Fact]
    public void No_genera_un_turno_que_no_entra_completo_en_la_franja()
    {
        var regla = Regla with { HoraHasta = new TimeOnly(10, 45) };
        var dia = new DateOnly(2026, 11, 2);
        Assert.Equal(3, Grilla.Horarios(regla, BuenosAires, dia, dia).Count());
    }

    [Theory]
    [InlineData("2026-11-02T09:00:00-03:00", true)]
    [InlineData("2026-11-02T12:00:00Z", true)]        // 09:00 en Buenos Aires
    [InlineData("2026-11-02T11:30:00-03:00", true)]
    [InlineData("2026-11-02T09:15:00-03:00", false)]  // fuera de la grilla
    [InlineData("2026-11-02T12:00:00-03:00", false)]  // termina después de la franja
    [InlineData("2026-11-03T09:00:00-03:00", false)]  // martes
    [InlineData("2026-12-07T09:00:00-03:00", false)]  // fuera de la vigencia
    public void Reconoce_si_un_inicio_es_un_horario_de_la_agenda(string inicio, bool esperado) =>
        Assert.Equal(esperado, Grilla.EsHorarioDeLaAgenda(Regla, BuenosAires, DateTimeOffset.Parse(inicio)));
}
