using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WhatsAppCloud.Api.Configuracion;
using WhatsAppCloud.Api.Meta;

namespace WhatsAppCloud.Tests;

public class ClienteWhatsAppTests
{
    private sealed class ManejadorFalso(HttpStatusCode codigo, string respuesta) : HttpMessageHandler
    {
        public HttpRequestMessage? Peticion { get; private set; }
        public string? CuerpoEnviado { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Peticion = request;
            CuerpoEnviado = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(codigo) { Content = new StringContent(respuesta, Encoding.UTF8, "application/json") };
        }
    }

    private static (ClienteWhatsApp Cliente, ManejadorFalso Manejador) Crear(HttpStatusCode codigo, string respuesta)
    {
        var manejador = new ManejadorFalso(codigo, respuesta);
        var http = new HttpClient(manejador) { BaseAddress = new Uri("https://graph.facebook.com/v21.0/") };
        var opciones = Options.Create(new WhatsAppOpciones { IdNumeroTelefono = "1234567890", TokenAcceso = "token-falso" });
        return (new ClienteWhatsApp(http, opciones), manejador);
    }

    private static readonly SolicitudPlantilla Solicitud =
        new("570000000001", "recordatorio_cita", "es_CO", ["Ana", "martes 3 p. m."]);

    [Fact]
    public async Task Envia_la_plantilla_al_numero_de_la_cuenta_con_el_token()
    {
        var (cliente, manejador) = Crear(HttpStatusCode.OK, """{"messaging_product":"whatsapp","messages":[{"id":"wamid.ABC"}]}""");

        var resultado = await cliente.EnviarPlantillaAsync(Solicitud, CancellationToken.None);

        Assert.True(resultado.Exitoso);
        Assert.Equal("wamid.ABC", resultado.IdMeta);
        Assert.Equal("https://graph.facebook.com/v21.0/1234567890/messages", manejador.Peticion!.RequestUri!.ToString());
        Assert.Equal("Bearer", manejador.Peticion.Headers.Authorization!.Scheme);
        Assert.Equal("token-falso", manejador.Peticion.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task El_cuerpo_tiene_la_forma_que_pide_Meta()
    {
        var (cliente, manejador) = Crear(HttpStatusCode.OK, """{"messages":[{"id":"wamid.ABC"}]}""");

        await cliente.EnviarPlantillaAsync(Solicitud, CancellationToken.None);

        using var json = JsonDocument.Parse(manejador.CuerpoEnviado!);
        var raiz = json.RootElement;
        Assert.Equal("whatsapp", raiz.GetProperty("messaging_product").GetString());
        Assert.Equal("570000000001", raiz.GetProperty("to").GetString());
        Assert.Equal("template", raiz.GetProperty("type").GetString());
        var plantilla = raiz.GetProperty("template");
        Assert.Equal("recordatorio_cita", plantilla.GetProperty("name").GetString());
        Assert.Equal("es_CO", plantilla.GetProperty("language").GetProperty("code").GetString());
        var parametros = plantilla.GetProperty("components")[0].GetProperty("parameters");
        Assert.Equal("martes 3 p. m.", parametros[1].GetProperty("text").GetString());
    }

    [Fact]
    public void Sin_variables_no_se_manda_components()
    {
        var cuerpo = CuerpoPlantilla.Desde(Solicitud with { Parametros = [] });

        var json = JsonSerializer.Serialize(cuerpo);

        Assert.DoesNotContain("components", json);
    }

    [Fact]
    public async Task Traduce_el_error_de_Meta_a_un_codigo_estable()
    {
        var (cliente, _) = Crear(HttpStatusCode.BadRequest,
            """{"error":{"message":"(#132001) Template name does not exist in the translation","code":132001,"error_data":{"details":"template name (recordatorio_cita) does not exist in es_CO"}}}""");

        var resultado = await cliente.EnviarPlantillaAsync(Solicitud, CancellationToken.None);

        Assert.False(resultado.Exitoso);
        Assert.Equal("plantilla_no_existe", resultado.Error!.Codigo);
        Assert.Equal(132001, resultado.Error.CodigoMeta);
        Assert.Contains("es_CO", resultado.Error.Detalle);
    }

    [Fact]
    public async Task Un_error_sin_cuerpo_legible_no_rompe_el_envio()
    {
        var (cliente, _) = Crear(HttpStatusCode.BadGateway, "<html>bad gateway</html>");

        var resultado = await cliente.EnviarPlantillaAsync(Solicitud, CancellationToken.None);

        Assert.False(resultado.Exitoso);
        Assert.Equal("error_meta", resultado.Error!.Codigo);
        Assert.Equal(ClaseError.Temporal, resultado.Error.Clase);
    }

    [Fact]
    public void El_enlace_variable_va_en_el_boton_y_no_en_el_texto()
    {
        var cuerpo = CuerpoPlantilla.Desde(Solicitud with { SufijoBoton = "c/8841" });

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(cuerpo));
        var boton = json.RootElement.GetProperty("template").GetProperty("components")[1];
        Assert.Equal("button", boton.GetProperty("type").GetString());
        Assert.Equal("url", boton.GetProperty("sub_type").GetString());
        Assert.Equal("0", boton.GetProperty("index").GetString());
        Assert.Equal("c/8841", boton.GetProperty("parameters")[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("Ana\nMaría", "Ana María")]
    [InlineData("Calle 45\t#12-30", "Calle 45 #12-30")]
    [InlineData("  Torre    B  ", "Torre B")]
    [InlineData("Línea\r\n\r\nsiguiente", "Línea siguiente")]
    public void Limpia_lo_que_haria_rechazar_la_variable(string crudo, string esperado)
    {
        Assert.Equal(esperado, VariablesPlantilla.Limpiar(crudo));
    }

    [Theory]
    [InlineData(131026, "destinatario_no_disponible", ClaseError.Persona)]
    [InlineData(131047, "fuera_de_ventana_24h", ClaseError.Mensaje)]
    [InlineData(132001, "plantilla_no_existe", ClaseError.Mensaje)]
    [InlineData(190, "token_invalido", ClaseError.Cuenta)]
    [InlineData(131042, "problema_de_pago", ClaseError.Cuenta)]
    [InlineData(130429, "limite_de_velocidad", ClaseError.Temporal)]
    [InlineData(999999, "error_meta", ClaseError.Temporal)]
    public void Cada_codigo_dice_de_quien_es_la_culpa(int codigoMeta, string codigo, ClaseError clase)
    {
        var error = ErroresMeta.Traducir(codigoMeta, null);

        Assert.Equal(codigo, error.Codigo);
        Assert.Equal(clase, error.Clase);
    }
}
