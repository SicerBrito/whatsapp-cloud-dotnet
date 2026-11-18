using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using WhatsAppCloud.Api.Configuracion;
using WhatsAppCloud.Api.Endpoints;
using WhatsAppCloud.Api.Envios;
using WhatsAppCloud.Api.Meta;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<WhatsAppOpciones>()
    .Bind(builder.Configuration.GetSection(WhatsAppOpciones.Seccion))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Los estados y las clases de error salen como texto ("Leido", "Cuenta"), no como números.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RegistroMensajes>();
builder.Services.AddSingleton<TopeDiario>();
builder.Services.AddSingleton<FilaEnvios>();
builder.Services.AddSingleton<FrenoEnvios>();
builder.Services.AddHostedService<ProcesadorEnvios>();

builder.Services.AddHttpClient<ClienteWhatsApp>((sp, http) =>
{
    var o = sp.GetRequiredService<IOptions<WhatsAppOpciones>>().Value;
    http.BaseAddress = new Uri($"https://graph.facebook.com/{o.VersionApi}/");
    http.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<ClienteSimulado>();

// Sin token de acceso la demo usa el cliente simulado: corre completa sin cuenta de Meta.
builder.Services.AddTransient<IClienteWhatsApp>(sp =>
    sp.GetRequiredService<IOptions<WhatsAppOpciones>>().Value.ModoSimulado
        ? sp.GetRequiredService<ClienteSimulado>()
        : sp.GetRequiredService<ClienteWhatsApp>());

var app = builder.Build();

var opciones = app.Services.GetRequiredService<IOptions<WhatsAppOpciones>>().Value;
if (opciones.HayDesvio)
    app.Logger.LogWarning("NumeroDesvioPruebas está activo: todos los mensajes salen hacia {Numero}.", opciones.NumeroDesvioPruebas);

app.MapGet("/", () => Results.Redirect("/estado-envios"));
app.MapWebhook();
app.MapMensajes();

app.Run();

/// <summary>Visible para las pruebas de integración con WebApplicationFactory.</summary>
public partial class Program;
