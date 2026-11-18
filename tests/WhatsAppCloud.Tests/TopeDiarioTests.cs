using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using WhatsAppCloud.Api.Configuracion;
using WhatsAppCloud.Api.Envios;

namespace WhatsAppCloud.Tests;

public class TopeDiarioTests
{
    private readonly FakeTimeProvider reloj = new(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero));

    private TopeDiario Crear(int tope) =>
        new(Options.Create(new WhatsAppOpciones { TopeDestinatariosPorDia = tope }), reloj);

    [Fact]
    public void Deja_contactar_personas_distintas_hasta_el_tope()
    {
        var tope = Crear(2);

        Assert.True(tope.IntentarReservar("570000000001", out _));
        Assert.True(tope.IntentarReservar("570000000002", out _));
        Assert.False(tope.IntentarReservar("570000000003", out _));
    }

    [Fact]
    public void Escribirle_otra_vez_a_la_misma_persona_no_gasta_cupo()
    {
        var tope = Crear(1);

        Assert.True(tope.IntentarReservar("570000000001", out var primeraVez));
        Assert.True(tope.IntentarReservar("570000000001", out var segundaVez));

        Assert.True(primeraVez);
        Assert.False(segundaVez);
        Assert.Equal(1, tope.EnUso());
    }

    [Fact]
    public void El_cupo_se_libera_24_horas_despues_del_ultimo_mensaje()
    {
        var tope = Crear(1);
        tope.IntentarReservar("570000000001", out _);

        reloj.Advance(TimeSpan.FromHours(23));
        Assert.False(tope.IntentarReservar("570000000002", out _));
        Assert.Equal(TimeSpan.FromHours(1), tope.EsperaHastaCupo());

        reloj.Advance(TimeSpan.FromHours(1));
        Assert.True(tope.IntentarReservar("570000000002", out _));
    }

    [Fact]
    public void Un_mensaje_nuevo_a_quien_ya_esta_en_la_ventana_la_extiende()
    {
        var tope = Crear(1);
        tope.IntentarReservar("570000000001", out _);

        reloj.Advance(TimeSpan.FromHours(20));
        tope.IntentarReservar("570000000001", out _);
        reloj.Advance(TimeSpan.FromHours(5));

        Assert.False(tope.IntentarReservar("570000000002", out _));
    }

    [Fact]
    public void Liberar_devuelve_el_cupo_de_un_envio_rechazado()
    {
        var tope = Crear(1);
        tope.IntentarReservar("570000000001", out _);

        tope.Liberar("570000000001");

        Assert.True(tope.IntentarReservar("570000000002", out _));
    }
}
