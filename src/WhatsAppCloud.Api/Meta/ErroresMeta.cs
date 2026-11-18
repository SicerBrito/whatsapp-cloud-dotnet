namespace WhatsAppCloud.Api.Meta;

/// <summary>
/// De quién es la culpa de un fallo. Es lo que decide qué hacer después, más que el código.
/// </summary>
public enum ClaseError
{
    /// <summary>Es de esa persona (no tiene WhatsApp, no quiere recibir). Las demás siguen; no se reintenta.</summary>
    Persona,

    /// <summary>Es de este mensaje (variables, plantilla, formato). Lo corrige quien lo pidió; no se reintenta.</summary>
    Mensaje,

    /// <summary>Es de la cuenta (pago, token, bloqueo). Le va a pasar a todos: se detiene la fila.</summary>
    Cuenta,

    /// <summary>Es del momento (límite de velocidad, Meta caída). Se reintenta con espera creciente.</summary>
    Temporal,
}

/// <summary>
/// Traduce los códigos numéricos de Meta a códigos estables y a una clase de fallo. Quien llama a
/// esta API decide qué hacer con "fuera_de_ventana_24h" sin tener que conocer el 131047.
/// </summary>
public static class ErroresMeta
{
    private static readonly Dictionary<int, (string Codigo, ClaseError Clase, string Explicacion)> Conocidos = new()
    {
        // De la persona: insistir no cambia nada y gasta cupo.
        [131021] = ("destinatario_es_el_remitente", ClaseError.Persona, "El destinatario es el mismo número que envía."),
        [131026] = ("destinatario_no_disponible", ClaseError.Persona, "El número no tiene WhatsApp o no puede recibir mensajes."),
        [131049] = ("retenido_por_meta", ClaseError.Persona, "Meta decidió no entregar el mensaje para proteger la experiencia de la persona."),
        [131050] = ("persona_no_quiere_recibir", ClaseError.Persona, "La persona pidió no recibir mensajes de marketing de la empresa."),
        [130472] = ("numero_en_experimento", ClaseError.Persona, "El número de la persona está en un experimento de Meta."),

        // Del mensaje: hay que corregirlo antes de volver a mandarlo.
        [100] = ("parametro_invalido", ClaseError.Mensaje, "Meta rechazó un parámetro de la solicitud."),
        [131008] = ("falta_un_parametro", ClaseError.Mensaje, "Falta un parámetro obligatorio."),
        [131009] = ("parametro_no_valido", ClaseError.Mensaje, "Un parámetro tiene un valor que Meta no acepta."),
        [131047] = ("fuera_de_ventana_24h", ClaseError.Mensaje, "Pasaron más de 24 horas desde la última respuesta de la persona: solo se puede escribir con plantilla."),
        [131051] = ("tipo_no_soportado", ClaseError.Mensaje, "Ese tipo de mensaje no se puede enviar."),
        [132000] = ("variables_no_coinciden", ClaseError.Mensaje, "La cantidad de variables no coincide con la plantilla aprobada."),
        [132001] = ("plantilla_no_existe", ClaseError.Mensaje, "La plantilla no existe o no está aprobada en ese idioma."),
        [132005] = ("texto_demasiado_largo", ClaseError.Mensaje, "Con las variables puestas, el texto supera el largo permitido."),
        [132007] = ("formato_no_permitido", ClaseError.Mensaje, "El contenido viola las reglas de formato de la plantilla."),
        [132012] = ("formato_de_variable", ClaseError.Mensaje, "Una variable no tiene el formato que declara la plantilla."),
        [132018] = ("variable_con_caracteres_invalidos", ClaseError.Mensaje, "Una variable trae saltos de línea, tabulaciones o demasiados espacios seguidos."),

        // De la cuenta: le va a pasar igual al siguiente de la fila.
        [190] = ("token_invalido", ClaseError.Cuenta, "El token de acceso venció o no es válido."),
        [368] = ("bloqueada_por_politicas", ClaseError.Cuenta, "La cuenta está bloqueada temporalmente por incumplir políticas de Meta."),
        [131005] = ("acceso_denegado", ClaseError.Cuenta, "El token no tiene permiso para enviar desde este número."),
        [131031] = ("cuenta_bloqueada", ClaseError.Cuenta, "Meta bloqueó la cuenta de WhatsApp Business."),
        [131042] = ("problema_de_pago", ClaseError.Cuenta, "Meta no pudo cobrar: revisa el método de pago de la cuenta."),
        [131048] = ("limite_por_spam", ClaseError.Cuenta, "Demasiados mensajes de la cuenta fueron bloqueados o reportados por las personas."),
        [132015] = ("plantilla_pausada", ClaseError.Cuenta, "Meta pausó la plantilla por baja calidad."),
        [132016] = ("plantilla_desactivada", ClaseError.Cuenta, "Meta desactivó la plantilla."),
        [133010] = ("numero_no_registrado", ClaseError.Cuenta, "El número que envía no está registrado en la Cloud API."),

        // Del momento: se reintenta.
        [1] = ("error_desconocido_de_meta", ClaseError.Temporal, "Meta respondió con un error desconocido."),
        [2] = ("servicio_de_meta_no_disponible", ClaseError.Temporal, "El servicio de Meta no está disponible por ahora."),
        [4] = ("limite_de_llamadas_de_la_app", ClaseError.Temporal, "La app superó su límite de llamadas."),
        [80007] = ("limite_de_llamadas_de_la_cuenta", ClaseError.Temporal, "La cuenta superó su límite de llamadas."),
        [130429] = ("limite_de_velocidad", ClaseError.Temporal, "Se superó el límite de mensajes por segundo del número."),
        [131000] = ("error_interno_de_meta", ClaseError.Temporal, "Algo falló dentro de Meta."),
        [131016] = ("servicio_sobrecargado", ClaseError.Temporal, "El servicio de Meta está sobrecargado."),
        [131056] = ("demasiados_mensajes_al_mismo_numero", ClaseError.Temporal, "Se enviaron demasiados mensajes seguidos al mismo número."),
    };

    /// <summary>
    /// Traduce un código de Meta. Lo desconocido se trata como temporal: el tope de intentos es el
    /// freno, y es peor perder un mensaje por no reconocer un código nuevo que reintentarlo dos veces.
    /// </summary>
    public static ErrorEnvio Traducir(int? codigoMeta, string? detalle)
    {
        if (codigoMeta is int codigo && Conocidos.TryGetValue(codigo, out var conocido))
            return new ErrorEnvio(conocido.Codigo, codigo, detalle ?? conocido.Explicacion, conocido.Clase);

        return new ErrorEnvio("error_meta", codigoMeta, detalle ?? "Meta devolvió un error sin detalle.", ClaseError.Temporal);
    }

    /// <summary>Error que no viene de Meta sino de no poder hablar con ella (red, tiempo de espera).</summary>
    public static ErrorEnvio SinConexion(string detalle) => new("sin_conexion", null, detalle, ClaseError.Temporal);
}
