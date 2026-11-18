using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using WhatsAppCloud.Api.Configuracion;
using WhatsAppCloud.Api.Envios;
using WhatsAppCloud.Api.Meta;

namespace WhatsAppCloud.Tests;

public class FrenoEnviosTests
{
    private readonly FakeTimeProvider reloj = new(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero));

    private FrenoEnvios Crear(int umbral) =>
        new(Options.Create(new WhatsAppOpciones { FallosDeCuentaParaPausar = umbral }), reloj);

    private static readonly ErrorEnvio Pago = ErroresMeta.Traducir(131042, null);
    private static readonly ErrorEnvio SinWhatsApp = ErroresMeta.Traducir(131026, null);

    [Fact]
    public void Los_fallos_de_las_personas_no_detienen_la_fila()
    {
        var freno = Crear(2);

        for (var i = 0; i < 10; i++) freno.RegistrarFalloDeCuenta(SinWhatsApp);

        Assert.False(freno.Pausado);
    }

    [Fact]
    public void Al_llegar_al_umbral_de_fallos_de_la_cuenta_se_detiene()
    {
        var freno = Crear(3);

        Assert.False(freno.RegistrarFalloDeCuenta(Pago));
        Assert.False(freno.RegistrarFalloDeCuenta(Pago));
        Assert.True(freno.RegistrarFalloDeCuenta(Pago));

        Assert.True(freno.Pausado);
        Assert.Contains("problema_de_pago", freno.Motivo);
    }

    [Fact]
    public void Los_fallos_de_hace_mas_de_una_hora_ya_no_cuentan()
    {
        var freno = Crear(2);

        freno.RegistrarFalloDeCuenta(Pago);
        reloj.Advance(TimeSpan.FromMinutes(61));
        freno.RegistrarFalloDeCuenta(Pago);

        Assert.False(freno.Pausado);
    }

    [Fact]
    public async Task Quien_espera_sigue_solo_cuando_reanudan()
    {
        var freno = Crear(1);
        freno.Pausar("Prueba");

        var espera = freno.EsperarSiPausadoAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(espera.IsCompleted);

        freno.Reanudar();
        await espera.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(freno.Pausado);
        Assert.Null(freno.Motivo);
    }

    [Fact]
    public void Pausar_dos_veces_conserva_el_primer_motivo()
    {
        var freno = Crear(1);

        freno.Pausar("Primero");
        freno.Pausar("Segundo");

        Assert.Equal("Primero", freno.Motivo);
    }
}
