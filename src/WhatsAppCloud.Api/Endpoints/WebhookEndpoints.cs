using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WhatsAppCloud.Api.Configuracion;
using WhatsAppCloud.Api.Envios;
using WhatsAppCloud.Api.Meta;

namespace WhatsAppCloud.Api.Endpoints;

public sealed record ResumenWebhook(int EstadosAplicados, int EstadosIgnorados, int MensajesNuevos, bool FilaDetenida);

public static class WebhookEndpoints
{
    public static void MapWebhook(this IEndpointRouteBuilder app)
    {
        // Meta llama con GET una sola vez, al suscribir el webhook, y espera recibir el reto tal cual.
        app.MapGet("/webhook", (HttpRequest req, IOptions<WhatsAppOpciones> opciones) =>
        {
            var modo = req.Query["hub.mode"].ToString();
            var token = req.Query["hub.verify_token"].ToString();
            var reto = req.Query["hub.challenge"].ToString();

            var tokenCoincide = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(opciones.Value.TokenVerificacion));

            // 403 sin explicación: a quien no sabe el token no se le cuenta qué falló.
            return modo == "subscribe" && tokenCoincide && reto.Length > 0
                ? Results.Text(reto, "text/plain")
                : Results.StatusCode(StatusCodes.Status403Forbidden);
        });

        // Avisos de Meta: estados de lo enviado y mensajes que escriben las personas.
        app.MapPost("/webhook", async (HttpRequest req, IOptions<WhatsAppOpciones> opciones, RegistroMensajes registro,
            FrenoEnvios freno, ILoggerFactory logs) =>
        {
            // La firma se calcula sobre los bytes exactos: hay que leerlos antes de deserializar.
            using var memoria = new MemoryStream();
            await req.Body.CopyToAsync(memoria);
            var cuerpo = memoria.ToArray();

            if (!FirmaWebhook.EsValida(cuerpo, req.Headers[FirmaWebhook.Encabezado], opciones.Value.SecretoApp))
                return Results.Unauthorized();

            NotificacionWebhook? notificacion;
            try
            {
                notificacion = JsonSerializer.Deserialize<NotificacionWebhook>(cuerpo);
            }
            catch (JsonException ex)
            {
                // Con la firma buena, el aviso es de Meta aunque no lo entendamos. Responder error
                // hace que Meta lo reintente una y otra vez y, si sigue, que desactive el webhook.
                logs.CreateLogger("Webhook").LogWarning(ex, "Aviso de Meta con un formato que no se pudo leer.");
                return Results.Ok(new ResumenWebhook(0, 0, 0, freno.Pausado));
            }

            int aplicados = 0, ignorados = 0, nuevos = 0;
            var valores = notificacion?.Entradas?.SelectMany(e => e.Cambios ?? []).Select(c => c.Valor) ?? [];
            foreach (var valor in valores)
            {
                foreach (var estado in valor?.Estados ?? [])
                {
                    if (registro.AplicarEstado(estado, out var fallo))
                    {
                        aplicados++;
                        // Meta acepta y reporta el fallo después: aquí es donde se ve un pago vencido.
                        if (fallo is not null) freno.RegistrarFalloDeCuenta(fallo);
                    }
                    else
                    {
                        ignorados++;
                    }
                }

                foreach (var entrante in valor?.Mensajes ?? [])
                {
                    if (registro.RegistrarRecibido(entrante)) nuevos++;
                }
            }

            // Meta reintenta si no recibe 200 pronto: se responde rápido y sin trabajo pesado.
            return Results.Ok(new ResumenWebhook(aplicados, ignorados, nuevos, freno.Pausado));
        });
    }
}
