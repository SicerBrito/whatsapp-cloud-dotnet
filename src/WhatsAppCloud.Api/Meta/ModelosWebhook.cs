using System.Text.Json.Serialization;

namespace WhatsAppCloud.Api.Meta;

// Forma del JSON que Meta envía al webhook. Solo se modela lo que la demo usa: estados de los
// mensajes salientes (con su cobro) y mensajes entrantes de texto, botones y listas.

public sealed record NotificacionWebhook(
    [property: JsonPropertyName("object")] string? Objeto,
    [property: JsonPropertyName("entry")] List<EntradaWebhook>? Entradas);

public sealed record EntradaWebhook(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("changes")] List<CambioWebhook>? Cambios);

public sealed record CambioWebhook(
    [property: JsonPropertyName("field")] string? Campo,
    [property: JsonPropertyName("value")] ValorCambio? Valor);

public sealed record ValorCambio(
    [property: JsonPropertyName("statuses")] List<EstadoWebhook>? Estados,
    [property: JsonPropertyName("messages")] List<MensajeEntrante>? Mensajes);

public sealed record EstadoWebhook(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Estado,
    [property: JsonPropertyName("timestamp")] string? MarcaTiempo,
    [property: JsonPropertyName("recipient_id")] string? Destinatario,
    [property: JsonPropertyName("errors")] List<ErrorWebhook>? Errores,
    [property: JsonPropertyName("pricing")] CobroWebhook? Cobro = null);

public sealed record ErrorWebhook(
    [property: JsonPropertyName("code")] int Codigo,
    [property: JsonPropertyName("title")] string? Titulo,
    [property: JsonPropertyName("message")] string? Mensaje = null,
    [property: JsonPropertyName("error_data")] DatosErrorMeta? Datos = null)
{
    /// <summary>El título solo dice la categoría; lo que explica el fallo está en los detalles.</summary>
    public string? MejorDetalle => Datos?.Detalle ?? Mensaje ?? Titulo;
}

/// <summary>
/// Cómo cobra Meta el mensaje. Llega en los avisos de enviado y entregado; en un fallido no viene,
/// porque ese mensaje no se cobra.
/// </summary>
public sealed record CobroWebhook(
    [property: JsonPropertyName("category")] string? Categoria,
    [property: JsonPropertyName("type")] string? Tipo,
    [property: JsonPropertyName("billable")] bool? Cobrable);

public sealed record MensajeEntrante(
    [property: JsonPropertyName("from")] string De,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("timestamp")] string? MarcaTiempo,
    [property: JsonPropertyName("type")] string Tipo,
    [property: JsonPropertyName("text")] TextoEntrante? Texto,
    [property: JsonPropertyName("button")] BotonEntrante? Boton = null,
    [property: JsonPropertyName("interactive")] InteractivoEntrante? Interactivo = null,
    [property: JsonPropertyName("context")] ContextoEntrante? Contexto = null)
{
    /// <summary>Lo legible del mensaje: lo escrito, o el botón o la opción de lista que tocó.</summary>
    public string? TextoLegible => Tipo switch
    {
        "text" => Texto?.Cuerpo,
        "button" => Boton?.Texto,
        "interactive" => Interactivo?.RespuestaBoton?.Titulo ?? Interactivo?.RespuestaLista?.Titulo,
        _ => null,
    };
}

public sealed record TextoEntrante([property: JsonPropertyName("body")] string Cuerpo);

/// <summary>Botón de respuesta rápida de una plantilla ("Confirmo", "Cambiar la cita").</summary>
public sealed record BotonEntrante(
    [property: JsonPropertyName("text")] string? Texto,
    [property: JsonPropertyName("payload")] string? Carga);

public sealed record InteractivoEntrante(
    [property: JsonPropertyName("type")] string? Tipo,
    [property: JsonPropertyName("button_reply")] OpcionEntrante? RespuestaBoton,
    [property: JsonPropertyName("list_reply")] OpcionEntrante? RespuestaLista);

public sealed record OpcionEntrante(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("title")] string? Titulo);

/// <summary>Si la persona respondió a un mensaje nuestro, el wamid de ese mensaje.</summary>
public sealed record ContextoEntrante([property: JsonPropertyName("id")] string? IdMensaje);
