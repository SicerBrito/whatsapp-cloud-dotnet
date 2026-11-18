using System.Text;
using WhatsAppCloud.Api.Meta;

namespace WhatsAppCloud.Tests;

public class FirmaWebhookTests
{
    private const string Secreto = "secreto-de-prueba";
    private static readonly byte[] Cuerpo = Encoding.UTF8.GetBytes("""{"object":"whatsapp_business_account","entry":[]}""");

    [Fact]
    public void Acepta_la_firma_calculada_con_el_secreto_de_la_app()
    {
        var firma = FirmaWebhook.Calcular(Cuerpo, Secreto);

        Assert.True(FirmaWebhook.EsValida(Cuerpo, firma, Secreto));
    }

    [Fact]
    public void Acepta_la_firma_en_mayusculas()
    {
        var firma = "sha256=" + FirmaWebhook.Calcular(Cuerpo, Secreto)["sha256=".Length..].ToUpperInvariant();

        Assert.True(FirmaWebhook.EsValida(Cuerpo, firma, Secreto));
    }

    [Fact]
    public void Rechaza_un_cuerpo_alterado()
    {
        var firma = FirmaWebhook.Calcular(Cuerpo, Secreto);
        var alterado = Encoding.UTF8.GetBytes("""{"object":"whatsapp_business_account","entry":[{}]}""");

        Assert.False(FirmaWebhook.EsValida(alterado, firma, Secreto));
    }

    [Fact]
    public void Rechaza_una_firma_hecha_con_otro_secreto()
    {
        var firma = FirmaWebhook.Calcular(Cuerpo, "otro-secreto");

        Assert.False(FirmaWebhook.EsValida(Cuerpo, firma, Secreto));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1=abcdef")]
    [InlineData("sha256=no-es-hexadecimal")]
    [InlineData("sha256=abcd")]
    public void Rechaza_encabezados_ausentes_o_mal_formados(string? encabezado)
    {
        Assert.False(FirmaWebhook.EsValida(Cuerpo, encabezado, Secreto));
    }
}
