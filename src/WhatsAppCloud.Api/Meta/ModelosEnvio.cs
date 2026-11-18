using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WhatsAppCloud.Api.Meta;

/// <summary>
/// Lo que el sistema pide enviar: una plantilla aprobada, con sus variables en orden y, si la
/// plantilla tiene un botón de enlace dinámico, el final de ese enlace.
/// </summary>
public sealed record SolicitudPlantilla(
    string Destinatario,
    string Plantilla,
    string Idioma,
    IReadOnlyList<string> Parametros,
    string? SufijoBoton = null);

/// <summary>Cuerpo que espera POST /{id-numero}/messages para una plantilla.</summary>
public sealed record CuerpoPlantilla(
    [property: JsonPropertyName("messaging_product")] string Producto,
    [property: JsonPropertyName("to")] string Para,
    [property: JsonPropertyName("type")] string Tipo,
    [property: JsonPropertyName("template")] PlantillaMeta Plantilla)
{
    public static CuerpoPlantilla Desde(SolicitudPlantilla s)
    {
        var componentes = new List<ComponenteMeta>();

        // Las variables van por posición: el orden es el contrato con la plantilla aprobada.
        if (s.Parametros.Count > 0)
            componentes.Add(new ComponenteMeta("body", s.Parametros.Select(p => new ParametroMeta("text", p)).ToList()));

        // El enlace va en un botón y no en el texto: la base queda aprobada con la plantilla y
        // solo cambia el final (un código, un id). Meta revisa con más rigor los enlaces sueltos.
        if (!string.IsNullOrWhiteSpace(s.SufijoBoton))
            componentes.Add(new ComponenteMeta("button", [new ParametroMeta("text", s.SufijoBoton)], "url", "0"));

        // Sin componentes no se manda "components": Meta rechaza la lista vacía.
        return new CuerpoPlantilla("whatsapp", s.Destinatario, "template",
            new PlantillaMeta(s.Plantilla, new IdiomaMeta(s.Idioma), componentes.Count == 0 ? null : componentes));
    }
}

public sealed record PlantillaMeta(
    [property: JsonPropertyName("name")] string Nombre,
    [property: JsonPropertyName("language")] IdiomaMeta Idioma,
    [property: JsonPropertyName("components"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    List<ComponenteMeta>? Componentes);

public sealed record IdiomaMeta([property: JsonPropertyName("code")] string Codigo);

public sealed record ComponenteMeta(
    [property: JsonPropertyName("type")] string Tipo,
    [property: JsonPropertyName("parameters")] List<ParametroMeta> Parametros,
    [property: JsonPropertyName("sub_type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Subtipo = null,
    [property: JsonPropertyName("index"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Posicion = null);

public sealed record ParametroMeta(
    [property: JsonPropertyName("type")] string Tipo,
    [property: JsonPropertyName("text")] string Texto);

/// <summary>
/// Meta rechaza el envío completo si una variable trae saltos de línea, tabulaciones o varios
/// espacios seguidos. Pasa más de lo que parece con nombres y direcciones digitados a mano, así
/// que se limpian antes de mandar en vez de devolverle el error a quien llama.
/// </summary>
public static partial class VariablesPlantilla
{
    public static string Limpiar(string? valor) =>
        string.IsNullOrEmpty(valor) ? "" : EspaciosYSaltos().Replace(valor, " ").Trim();

    [GeneratedRegex(@"\s{2,}|[\r\n\t]")]
    private static partial Regex EspaciosYSaltos();
}

/// <summary>Respuesta exitosa: Meta acepta el mensaje y le da un id (wamid). Aceptar no es entregar.</summary>
public sealed record RespuestaEnvioMeta(
    [property: JsonPropertyName("messages")] List<IdMensajeMeta>? Mensajes);

public sealed record IdMensajeMeta([property: JsonPropertyName("id")] string Id);

/// <summary>Respuesta de error de la Graph API.</summary>
public sealed record RespuestaErrorMeta([property: JsonPropertyName("error")] DetalleErrorMeta? Error);

public sealed record DetalleErrorMeta(
    [property: JsonPropertyName("code")] int Codigo,
    [property: JsonPropertyName("message")] string? Mensaje,
    [property: JsonPropertyName("error_data")] DatosErrorMeta? Datos);

public sealed record DatosErrorMeta([property: JsonPropertyName("details")] string? Detalle);

/// <summary>Resultado de un intento de envío, ya traducido a códigos propios.</summary>
public sealed record ResultadoEnvio(bool Exitoso, string? IdMeta, ErrorEnvio? Error)
{
    public static ResultadoEnvio Ok(string idMeta) => new(true, idMeta, null);
    public static ResultadoEnvio Fallo(ErrorEnvio error) => new(false, null, error);
}

/// <summary>
/// Error con un código estable para el sistema que llama, el código original de Meta y la clase,
/// que dice si es de la persona, del mensaje, de la cuenta o del momento.
/// </summary>
public sealed record ErrorEnvio(string Codigo, int? CodigoMeta, string Detalle, ClaseError Clase);
