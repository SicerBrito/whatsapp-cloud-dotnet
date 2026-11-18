using System.ComponentModel.DataAnnotations;

namespace WhatsAppCloud.Api.Configuracion;

/// <summary>Datos de la app de Meta y límites de envío. Se leen de la sección "WhatsApp".</summary>
public sealed class WhatsAppOpciones
{
    public const string Seccion = "WhatsApp";

    /// <summary>Versión de la Graph API. Meta publica una nueva varias veces al año.</summary>
    public string VersionApi { get; set; } = "v21.0";

    /// <summary>Id del número de teléfono de la cuenta de WhatsApp Business (no es el número).</summary>
    public string IdNumeroTelefono { get; set; } = "";

    /// <summary>Token de acceso. Vacío = modo simulado: la demo corre sin llamar a Meta.</summary>
    public string TokenAcceso { get; set; } = "";

    /// <summary>Secreto de la app, con el que Meta firma cada llamada al webhook.</summary>
    [Required(ErrorMessage = "Falta WhatsApp:SecretoApp: sin él no se puede comprobar que el webhook viene de Meta.")]
    public string SecretoApp { get; set; } = "";

    /// <summary>Texto que se registra en Meta al suscribir el webhook y que Meta devuelve al verificarlo.</summary>
    [Required(ErrorMessage = "Falta WhatsApp:TokenVerificacion.")]
    public string TokenVerificacion { get; set; } = "";

    /// <summary>
    /// Personas distintas a las que se les puede escribir en 24 horas. Meta impone un tope por
    /// cuenta (250, 1.000, 10.000…) y, si se pasa, acepta mensajes que luego no entrega.
    /// </summary>
    [Range(1, 1_000_000)]
    public int TopeDestinatariosPorDia { get; set; } = 250;

    /// <summary>
    /// Red de seguridad para probar con datos reales: si tiene un número, <b>todos</b> los mensajes
    /// salen hacia él, sin importar a quién iban. Un WhatsApp enviado no se puede recoger.
    /// </summary>
    public string NumeroDesvioPruebas { get; set; } = "";

    /// <summary>Intentos por mensaje ante fallos temporales (límite de velocidad, Meta caída).</summary>
    [Range(1, 10)]
    public int MaximoIntentos { get; set; } = 3;

    /// <summary>Espera antes del primer reintento; se duplica en cada intento siguiente.</summary>
    public TimeSpan EsperaPrimerReintento { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Fallos de la cuenta que Meta reporta por el webhook, en una hora, para detener la fila.
    /// Meta acepta los mensajes y avisa del fallo minutos después: sin este freno, un pago
    /// vencido se descubre cuando ya se gastó la lista entera.
    /// </summary>
    [Range(1, 1000)]
    public int FallosDeCuentaParaPausar { get; set; } = 5;

    public bool ModoSimulado => string.IsNullOrWhiteSpace(TokenAcceso);

    public bool HayDesvio => !string.IsNullOrWhiteSpace(NumeroDesvioPruebas);
}
