using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppCloud.Api.Meta;

namespace WhatsAppCloud.Tests;

/// <summary>
/// Levanta la API completa en memoria. Sin <paramref name="cliente"/> usa el cliente simulado; con
/// él, cada envío sigue el guion que le dé la prueba (fallar, aceptar…).
/// </summary>
public sealed class FabricaApi(IClienteWhatsApp? cliente = null, Dictionary<string, string>? ajustes = null)
    : WebApplicationFactory<Program>
{
    public const string Secreto = "secreto-de-prueba";
    public const string TokenVerificacion = "token-de-prueba";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("WhatsApp:SecretoApp", Secreto);
        builder.UseSetting("WhatsApp:TokenVerificacion", TokenVerificacion);
        builder.UseSetting("WhatsApp:TopeDestinatariosPorDia", "2");
        builder.UseSetting("WhatsApp:EsperaPrimerReintento", "00:00:00.050");
        foreach (var (clave, valor) in ajustes ?? []) builder.UseSetting(clave, valor);

        if (cliente is not null)
            builder.ConfigureTestServices(s => s.AddSingleton(cliente));
    }
}

/// <summary>Cliente que responde en el orden del guion y, cuando se le acaba, acepta todo.</summary>
public sealed class ClienteGuionado(params ResultadoEnvio[] guion) : IClienteWhatsApp
{
    private readonly ConcurrentQueue<ResultadoEnvio> pendientes = new(guion);
    private int contador;

    public ConcurrentQueue<SolicitudPlantilla> Recibidas { get; } = new();

    public Task<ResultadoEnvio> EnviarPlantillaAsync(SolicitudPlantilla solicitud, CancellationToken ct)
    {
        Recibidas.Enqueue(solicitud);
        return Task.FromResult(pendientes.TryDequeue(out var siguiente)
            ? siguiente
            : ResultadoEnvio.Ok($"wamid.prueba.{Interlocked.Increment(ref contador)}"));
    }
}

public class ApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Vista(Guid Id, string Estado, string? IdMeta, int Intentos);

    private sealed record VistaEstado(bool Pausado, string? MotivoPausa, int MensajesEnFila);

    private static ResultadoEnvio FalloMeta(int codigo) => ResultadoEnvio.Fallo(ErroresMeta.Traducir(codigo, null));

    [Fact]
    public async Task Verificacion_del_webhook_devuelve_el_reto_con_el_token_correcto()
    {
        await using var fabrica = new FabricaApi();
        var http = fabrica.CreateClient();

        var ok = await http.GetAsync($"/webhook?hub.mode=subscribe&hub.verify_token={FabricaApi.TokenVerificacion}&hub.challenge=12345");
        var mal = await http.GetAsync("/webhook?hub.mode=subscribe&hub.verify_token=otro&hub.challenge=12345");

        Assert.Equal("12345", await ok.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, mal.StatusCode);
    }

    [Fact]
    public async Task Un_webhook_sin_firma_valida_se_rechaza()
    {
        await using var fabrica = new FabricaApi();
        var http = fabrica.CreateClient();
        var contenido = new StringContent("""{"object":"whatsapp_business_account","entry":[]}""", Encoding.UTF8, "application/json");
        contenido.Headers.Add(FirmaWebhook.Encabezado, "sha256=00");

        var respuesta = await http.PostAsync("/webhook", contenido);

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }

    [Fact]
    public async Task Un_aviso_firmado_que_no_se_entiende_igual_responde_200()
    {
        await using var fabrica = new FabricaApi();
        var http = fabrica.CreateClient();

        var respuesta = await EnviarWebhookCrudo(http, "esto no es JSON");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task Un_numero_mal_escrito_devuelve_un_error_claro()
    {
        await using var fabrica = new FabricaApi();
        var http = fabrica.CreateClient();

        var respuesta = await http.PostAsJsonAsync("/mensajes/plantilla", new { destinatario = "300 123", plantilla = "recordatorio_cita" });

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Contains("indicativo", await respuesta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Recorrido_completo_de_enviado_a_leido_por_el_webhook()
    {
        await using var fabrica = new FabricaApi();
        var http = fabrica.CreateClient();

        var creado = await http.PostAsJsonAsync("/mensajes/plantilla",
            new { destinatario = "+57 300 000 0001", plantilla = "recordatorio_cita", idioma = "es_CO", parametros = new[] { "Ana" } });
        Assert.Equal(HttpStatusCode.Accepted, creado.StatusCode);
        var id = (await creado.Content.ReadFromJsonAsync<Vista>(Json))!.Id;

        var aceptado = await Esperar(http, id, v => v.Estado == "AceptadoPorMeta");

        await EnviarWebhook(http, Notificacion(aceptado.IdMeta!, "read"));
        var resumen = await EnviarWebhook(http, Notificacion(aceptado.IdMeta!, "delivered"));

        var final = await http.GetFromJsonAsync<Vista>($"/mensajes/{id}", Json);
        Assert.Equal("Leido", final!.Estado);
        Assert.Contains("\"estadosIgnorados\":1", resumen);
    }

    [Fact]
    public async Task Pasado_el_tope_diario_el_mensaje_espera_en_fila()
    {
        await using var fabrica = new FabricaApi(); // tope de 2 personas en 24 horas
        var http = fabrica.CreateClient();

        var ids = new List<Guid>();
        foreach (var numero in new[] { "570000000001", "570000000002", "570000000003" })
            ids.Add(await Encolar(http, numero));

        await Esperar(http, ids[0], v => v.Estado == "AceptadoPorMeta");
        await Esperar(http, ids[1], v => v.Estado == "AceptadoPorMeta");
        await Task.Delay(300);
        var tercero = await http.GetFromJsonAsync<Vista>($"/mensajes/{ids[2]}", Json);

        Assert.Equal("EnFila", tercero!.Estado);
    }

    [Fact]
    public async Task La_misma_referencia_no_le_manda_dos_veces_el_mensaje_a_la_persona()
    {
        var cliente = new ClienteGuionado();
        await using var fabrica = new FabricaApi(cliente);
        var http = fabrica.CreateClient();
        var peticion = new { destinatario = "570000000001", plantilla = "recordatorio_cita", referencia = "cita-8841" };

        var primera = await http.PostAsJsonAsync("/mensajes/plantilla", peticion);
        var id = (await primera.Content.ReadFromJsonAsync<Vista>(Json))!.Id;
        await Esperar(http, id, v => v.Estado == "AceptadoPorMeta");
        var segunda = await http.PostAsJsonAsync("/mensajes/plantilla", peticion);
        var otroNumero = await http.PostAsJsonAsync("/mensajes/plantilla", peticion with { destinatario = "570000000002" });

        Assert.Equal(HttpStatusCode.Accepted, primera.StatusCode);
        Assert.Equal(HttpStatusCode.OK, segunda.StatusCode);
        Assert.Equal(id, (await segunda.Content.ReadFromJsonAsync<Vista>(Json))!.Id);
        Assert.Equal(HttpStatusCode.Conflict, otroNumero.StatusCode);
        Assert.Single(cliente.Recibidas);
    }

    [Fact]
    public async Task Las_variables_con_saltos_de_linea_se_limpian_antes_de_enviar()
    {
        var cliente = new ClienteGuionado();
        await using var fabrica = new FabricaApi(cliente);
        var http = fabrica.CreateClient();

        var id = await Encolar(http, "570000000001", parametros: ["Ana\nMaría ", "Calle 45\t#12-30"]);
        await Esperar(http, id, v => v.Estado == "AceptadoPorMeta");

        var enviada = Assert.Single(cliente.Recibidas);
        Assert.Equal(new[] { "Ana María", "Calle 45 #12-30" }, enviada.Parametros);
    }

    [Fact]
    public async Task Con_desvio_de_pruebas_todo_sale_hacia_ese_numero()
    {
        var cliente = new ClienteGuionado();
        await using var fabrica = new FabricaApi(cliente, new() { ["WhatsApp:NumeroDesvioPruebas"] = "+57 300 999 9999" });
        var http = fabrica.CreateClient();

        var id = await Encolar(http, "570000000001");
        await Esperar(http, id, v => v.Estado == "AceptadoPorMeta");

        Assert.Equal("573009999999", Assert.Single(cliente.Recibidas).Destinatario);
        Assert.Contains("Desviado a 573009999999", await http.GetStringAsync($"/mensajes/{id}"));
    }

    [Fact]
    public async Task Un_fallo_temporal_se_reintenta_y_el_mensaje_sale()
    {
        var cliente = new ClienteGuionado(FalloMeta(130429));
        await using var fabrica = new FabricaApi(cliente);
        var http = fabrica.CreateClient();

        var id = await Encolar(http, "570000000001");
        var final = await Esperar(http, id, v => v.Estado == "AceptadoPorMeta");

        Assert.Equal(2, final.Intentos);
        Assert.Equal(2, cliente.Recibidas.Count);
    }

    [Fact]
    public async Task Un_fallo_de_la_persona_no_se_reintenta()
    {
        var cliente = new ClienteGuionado(FalloMeta(131026));
        await using var fabrica = new FabricaApi(cliente);
        var http = fabrica.CreateClient();

        var id = await Encolar(http, "570000000001");
        var final = await Esperar(http, id, v => v.Estado == "Fallido");
        await Task.Delay(200);

        Assert.Equal(1, final.Intentos);
        Assert.Single(cliente.Recibidas);
    }

    [Fact]
    public async Task Un_rechazo_por_la_cuenta_detiene_la_fila_y_el_mensaje_sale_al_reanudar()
    {
        var cliente = new ClienteGuionado(FalloMeta(131042));
        await using var fabrica = new FabricaApi(cliente);
        var http = fabrica.CreateClient();

        var id = await Encolar(http, "570000000001");
        var pausa = await EsperarEstado(http, e => e.Pausado);
        var mientras = await http.GetFromJsonAsync<Vista>($"/mensajes/{id}", Json);

        Assert.Contains("problema_de_pago", pausa.MotivoPausa);
        Assert.Equal("EnFila", mientras!.Estado); // no queda fallido: la culpa no es de la persona

        (await http.PostAsync("/envios/reanudar", null)).EnsureSuccessStatusCode();
        await Esperar(http, id, v => v.Estado == "AceptadoPorMeta");
    }

    [Fact]
    public async Task Los_fallos_de_la_cuenta_que_avisa_Meta_detienen_la_fila_y_al_reanudar_se_reintentan()
    {
        await using var fabrica = new FabricaApi(ajustes: new() { ["WhatsApp:FallosDeCuentaParaPausar"] = "2" });
        var http = fabrica.CreateClient();

        var ids = new[] { await Encolar(http, "570000000001"), await Encolar(http, "570000000002") };
        var aceptados = new List<Vista>();
        foreach (var id in ids) aceptados.Add(await Esperar(http, id, v => v.Estado == "AceptadoPorMeta"));

        // Meta aceptó los dos y minutos después avisa que no pudo cobrar.
        await EnviarWebhook(http, Notificacion(aceptados[0].IdMeta!, "failed", 131042));
        var resumen = await EnviarWebhook(http, Notificacion(aceptados[1].IdMeta!, "failed", 131042));
        Assert.Contains("\"filaDetenida\":true", resumen);

        var reanudar = await http.PostAsync("/envios/reanudar", null);
        Assert.Contains("\"mensajesDeVueltaEnFila\":2", await reanudar.Content.ReadAsStringAsync());

        foreach (var (id, antes) in ids.Zip(aceptados))
        {
            var despues = await Esperar(http, id, v => v.Estado == "AceptadoPorMeta");
            Assert.NotEqual(antes.IdMeta, despues.IdMeta);
        }
    }

    private static async Task<Guid> Encolar(HttpClient http, string numero, string[]? parametros = null)
    {
        var respuesta = await http.PostAsJsonAsync("/mensajes/plantilla",
            new { destinatario = numero, plantilla = "aviso", parametros = parametros ?? [] });
        respuesta.EnsureSuccessStatusCode();
        return (await respuesta.Content.ReadFromJsonAsync<Vista>(Json))!.Id;
    }

    private static async Task<Vista> Esperar(HttpClient http, Guid id, Func<Vista, bool> condicion)
    {
        for (var intento = 0; intento < 50; intento++)
        {
            var vista = await http.GetFromJsonAsync<Vista>($"/mensajes/{id}", Json);
            if (vista is not null && condicion(vista)) return vista;
            await Task.Delay(100);
        }
        throw new TimeoutException($"El mensaje {id} no llegó al estado esperado.");
    }

    private static async Task<VistaEstado> EsperarEstado(HttpClient http, Func<VistaEstado, bool> condicion)
    {
        for (var intento = 0; intento < 50; intento++)
        {
            var estado = await http.GetFromJsonAsync<VistaEstado>("/estado-envios", Json);
            if (estado is not null && condicion(estado)) return estado;
            await Task.Delay(100);
        }
        throw new TimeoutException("El estado de los envíos no llegó a lo esperado.");
    }

    private static string Notificacion(string idMeta, string estado, int? codigoError = null)
    {
        var errores = codigoError is null ? "" : $$""","errors":[{"code":{{codigoError}},"title":"Fallo"}]""";
        return $$$"""
            {"object":"whatsapp_business_account","entry":[{"id":"WABA","changes":[{"field":"messages","value":{
              "messaging_product":"whatsapp",
              "statuses":[{"id":"{{{idMeta}}}","status":"{{{estado}}}","timestamp":"1795000000","recipient_id":"570000000001"{{{errores}}}}]
            }}]}]}
            """;
    }

    private static async Task<string> EnviarWebhook(HttpClient http, string json)
    {
        var respuesta = await EnviarWebhookCrudo(http, json);
        respuesta.EnsureSuccessStatusCode();
        return await respuesta.Content.ReadAsStringAsync();
    }

    private static Task<HttpResponseMessage> EnviarWebhookCrudo(HttpClient http, string cuerpo)
    {
        var bytes = Encoding.UTF8.GetBytes(cuerpo);
        var contenido = new ByteArrayContent(bytes);
        contenido.Headers.ContentType = new("application/json");
        contenido.Headers.Add(FirmaWebhook.Encabezado, FirmaWebhook.Calcular(bytes, FabricaApi.Secreto));
        return http.PostAsync("/webhook", contenido);
    }
}
