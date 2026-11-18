using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WhatsAppCloud.Api.Configuracion;

namespace WhatsAppCloud.Api.Meta;

public interface IClienteWhatsApp
{
    Task<ResultadoEnvio> EnviarPlantillaAsync(SolicitudPlantilla solicitud, CancellationToken ct);
}

/// <summary>Cliente real de la WhatsApp Cloud API (Graph API de Meta).</summary>
public sealed class ClienteWhatsApp(HttpClient http, IOptions<WhatsAppOpciones> opciones) : IClienteWhatsApp
{
    public async Task<ResultadoEnvio> EnviarPlantillaAsync(SolicitudPlantilla solicitud, CancellationToken ct)
    {
        var o = opciones.Value;
        using var peticion = new HttpRequestMessage(HttpMethod.Post, $"{o.IdNumeroTelefono}/messages")
        {
            Content = JsonContent.Create(CuerpoPlantilla.Desde(solicitud)),
        };
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.TokenAcceso);

        HttpResponseMessage respuesta;
        try
        {
            respuesta = await http.SendAsync(peticion, ct);
        }
        catch (HttpRequestException ex)
        {
            return ResultadoEnvio.Fallo(ErroresMeta.SinConexion(ex.Message));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // Se agotó el tiempo de espera. No se sabe si Meta alcanzó a aceptarlo; se reintenta
            // igual, porque perder un mensaje es peor que, rara vez, mandarlo dos veces.
            return ResultadoEnvio.Fallo(ErroresMeta.SinConexion("Meta no respondió a tiempo."));
        }

        using (respuesta)
        {
            if (respuesta.IsSuccessStatusCode)
            {
                var ok = await respuesta.Content.ReadFromJsonAsync<RespuestaEnvioMeta>(ct);
                var id = ok?.Mensajes?.FirstOrDefault()?.Id;
                // Meta respondió bien pero sin id: pudo haber salido, así que no se reintenta
                // (mejor un mensaje de menos, visible aquí, que uno repetido).
                return id is null
                    ? ResultadoEnvio.Fallo(new ErrorEnvio("respuesta_inesperada", null, "Meta respondió sin id de mensaje.", ClaseError.Mensaje))
                    : ResultadoEnvio.Ok(id);
            }

            return ResultadoEnvio.Fallo(await LeerErrorAsync(respuesta, ct));
        }
    }

    private static async Task<ErrorEnvio> LeerErrorAsync(HttpResponseMessage respuesta, CancellationToken ct)
    {
        try
        {
            var error = await respuesta.Content.ReadFromJsonAsync<RespuestaErrorMeta>(ct);
            return ErroresMeta.Traducir(error?.Error?.Codigo, error?.Error?.Datos?.Detalle ?? error?.Error?.Mensaje);
        }
        catch (JsonException)
        {
            return ErroresMeta.Traducir(null, $"Meta respondió {(int)respuesta.StatusCode} sin un error legible.");
        }
    }
}

/// <summary>
/// Cliente para correr la demo sin cuenta de Meta: acepta todo y devuelve un id inventado.
/// Los estados (entregado, leído) se simulan llamando al webhook con una firma válida.
/// </summary>
public sealed class ClienteSimulado : IClienteWhatsApp
{
    public Task<ResultadoEnvio> EnviarPlantillaAsync(SolicitudPlantilla solicitud, CancellationToken ct) =>
        Task.FromResult(ResultadoEnvio.Ok($"wamid.SIMULADO.{Guid.NewGuid():N}"));
}
