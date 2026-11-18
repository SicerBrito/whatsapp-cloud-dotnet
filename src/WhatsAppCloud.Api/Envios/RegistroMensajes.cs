using WhatsAppCloud.Api.Meta;

namespace WhatsAppCloud.Api.Envios;

/// <summary>
/// El orden importa: Meta puede avisar "leído" antes que "entregado", y repetir un aviso.
/// Un estado solo reemplaza a uno anterior en esta escala; "fallido" es final.
/// </summary>
public enum EstadoMensaje
{
    EnFila = 0,
    AceptadoPorMeta = 1,
    Enviado = 2,
    Entregado = 3,
    Leido = 4,
    Fallido = 5,
}

public sealed record CambioEstado(EstadoMensaje Estado, DateTimeOffset Fecha, string? Detalle);

/// <summary>Cómo cobra Meta el mensaje: categoría (utility, marketing…) y tipo (regular, gratis…).</summary>
public sealed record CobroMensaje(string? Categoria, string? Tipo, bool? Cobrable);

public sealed class MensajeSaliente(Guid id, SolicitudPlantilla solicitud, string? referencia, DateTimeOffset creado)
{
    private readonly List<CambioEstado> historial = [new(EstadoMensaje.EnFila, creado, null)];

    public Guid Id { get; } = id;
    public SolicitudPlantilla Solicitud { get; } = solicitud;

    /// <summary>Id del sistema que pidió el envío. Si llega dos veces, el mensaje sale una sola.</summary>
    public string? Referencia { get; } = referencia;

    /// <summary>El wamid del último intento. Los avisos de intentos anteriores ya no lo mueven.</summary>
    public string? IdMeta { get; internal set; }
    public EstadoMensaje Estado { get; private set; } = EstadoMensaje.EnFila;
    public ErrorEnvio? Error { get; internal set; }
    public int Intentos { get; internal set; }
    public CobroMensaje? Cobro { get; internal set; }
    public DateTimeOffset? AceptadoEn { get; private set; }

    public IReadOnlyList<CambioEstado> Historial
    {
        get { lock (historial) return historial.ToArray(); }
    }

    internal bool Avanzar(EstadoMensaje nuevo, DateTimeOffset fecha, string? detalle)
    {
        if (Estado == EstadoMensaje.Fallido) return false;
        if (nuevo != EstadoMensaje.Fallido && nuevo <= Estado) return false;

        Estado = nuevo;
        if (nuevo == EstadoMensaje.AceptadoPorMeta) AceptadoEn = fecha;
        Anotar(nuevo, fecha, detalle);
        return true;
    }

    /// <summary>Deja constancia de algo que no cambia el estado: un reintento programado, una pausa.</summary>
    internal void Anotar(DateTimeOffset fecha, string detalle) => Anotar(Estado, fecha, detalle);

    /// <summary>
    /// Vuelve a poner en fila un mensaje que falló por algo que no era de la persona ni del
    /// mensaje. Es la única forma de salir de "fallido", y solo la usa quien reanuda la fila.
    /// </summary>
    internal bool Reabrir(DateTimeOffset fecha)
    {
        if (Estado != EstadoMensaje.Fallido || Error?.Clase is not (ClaseError.Cuenta or ClaseError.Temporal)) return false;

        var motivo = $"Vuelve a la fila: había fallado por {Error.Codigo}, que no era de la persona.";
        Estado = EstadoMensaje.EnFila;
        Error = null;
        AceptadoEn = null;
        IdMeta = null; // un aviso tardío del intento fallido ya no lo mueve
        Anotar(EstadoMensaje.EnFila, fecha, motivo);
        return true;
    }

    private void Anotar(EstadoMensaje estado, DateTimeOffset fecha, string? detalle)
    {
        lock (historial) historial.Add(new CambioEstado(estado, fecha, detalle));
    }
}

/// <summary>Mensaje que escribió una persona. Si respondió a uno nuestro, queda atado a él.</summary>
public sealed record MensajeRecibido(string IdMeta, string De, string Tipo, string? Texto, Guid? EnRespuestaA, DateTimeOffset Fecha);

/// <summary>
/// Registro en memoria de lo enviado y lo recibido. En producción esto vive en la base de
/// datos; la lógica de estados es la misma.
/// </summary>
public sealed class RegistroMensajes(TimeProvider reloj)
{
    private readonly object candado = new();
    private readonly Dictionary<Guid, MensajeSaliente> porId = [];
    private readonly Dictionary<string, MensajeSaliente> porIdMeta = [];
    private readonly Dictionary<string, MensajeSaliente> porReferencia = [];
    private readonly Dictionary<string, MensajeRecibido> recibidos = [];

    /// <summary>
    /// Crea el mensaje, o devuelve el que ya existe con esa referencia. Los sistemas que llaman
    /// reintentan cuando no reciben respuesta a tiempo; sin esto, la persona recibe el mismo
    /// WhatsApp dos o tres veces.
    /// </summary>
    public (MensajeSaliente Mensaje, bool EsNuevo) Crear(SolicitudPlantilla solicitud, string? referencia = null)
    {
        lock (candado)
        {
            if (referencia is not null && porReferencia.TryGetValue(referencia, out var existente))
                return (existente, false);

            var mensaje = new MensajeSaliente(Guid.NewGuid(), solicitud, referencia, reloj.GetUtcNow());
            porId[mensaje.Id] = mensaje;
            if (referencia is not null) porReferencia[referencia] = mensaje;
            return (mensaje, true);
        }
    }

    public MensajeSaliente? Obtener(Guid id)
    {
        lock (candado) return porId.GetValueOrDefault(id);
    }

    public int EnFila()
    {
        lock (candado) return porId.Values.Count(m => m.Estado == EstadoMensaje.EnFila);
    }

    public int SumarIntento(Guid id)
    {
        lock (candado) return ++porId[id].Intentos;
    }

    public void MarcarAceptado(Guid id, string idMeta, string? nota = null)
    {
        lock (candado)
        {
            var mensaje = porId[id];
            mensaje.IdMeta = idMeta;
            porIdMeta[idMeta] = mensaje;
            mensaje.Avanzar(EstadoMensaje.AceptadoPorMeta, reloj.GetUtcNow(), nota);
        }
    }

    public void MarcarFallido(Guid id, ErrorEnvio error)
    {
        lock (candado)
        {
            var mensaje = porId[id];
            mensaje.Error = error;
            mensaje.Avanzar(EstadoMensaje.Fallido, reloj.GetUtcNow(), error.Codigo);
        }
    }

    public void Anotar(Guid id, string detalle)
    {
        lock (candado) porId[id].Anotar(reloj.GetUtcNow(), detalle);
    }

    /// <summary>
    /// Aplica un estado llegado por el webhook. Devuelve false si se ignoró: repetido, viejo, de un
    /// intento anterior o de un mensaje que no es nuestro. Si el estado es un fallo, lo devuelve
    /// traducido en <paramref name="fallo"/> para que el freno lo cuente.
    /// </summary>
    public bool AplicarEstado(EstadoWebhook estado, out ErrorEnvio? fallo)
    {
        fallo = null;
        var nuevo = estado.Estado switch
        {
            "sent" => EstadoMensaje.Enviado,
            "delivered" => EstadoMensaje.Entregado,
            "read" => EstadoMensaje.Leido,
            "failed" => EstadoMensaje.Fallido,
            _ => (EstadoMensaje?)null,
        };

        var fecha = LeerFecha(estado.MarcaTiempo) ?? reloj.GetUtcNow();
        lock (candado)
        {
            if (!porIdMeta.TryGetValue(estado.Id, out var mensaje)) return false;

            // Un aviso atrasado de un intento anterior no puede deshacer el intento que sí salió.
            if (mensaje.IdMeta != estado.Id) return false;

            // El cobro se guarda aunque el estado llegue tarde y no avance: sigue siendo cierto.
            if (estado.Cobro is { } cobro)
                mensaje.Cobro = new CobroMensaje(cobro.Categoria, cobro.Tipo, cobro.Cobrable);

            if (nuevo is null) return false;

            ErrorEnvio? error = null;
            if (nuevo == EstadoMensaje.Fallido)
            {
                // Un "failed" sin errores sigue siendo un fallo: no puede quedar como enviado.
                var primero = estado.Errores?.FirstOrDefault();
                error = ErroresMeta.Traducir(primero?.Codigo, primero?.MejorDetalle);
            }

            var aplicado = mensaje.Avanzar(nuevo.Value, fecha, error?.Codigo);
            if (aplicado && error is not null)
            {
                mensaje.Error = error;
                fallo = error;
            }
            return aplicado;
        }
    }

    /// <summary>Pone otra vez en fila lo que falló por la cuenta o por el momento. Devuelve sus ids.</summary>
    public IReadOnlyList<Guid> ReabrirFallidosNoAtribuibles()
    {
        var ahora = reloj.GetUtcNow();
        lock (candado) return porId.Values.Where(m => m.Reabrir(ahora)).Select(m => m.Id).ToList();
    }

    /// <summary>
    /// Mensajes que Meta aceptó hace más de <paramref name="umbral"/> y de los que no ha vuelto a
    /// decir nada. Casi siempre es que los avisos no están llegando, no que los mensajes se perdieron.
    /// </summary>
    public int AceptadosSinAviso(TimeSpan umbral)
    {
        var limite = reloj.GetUtcNow() - umbral;
        lock (candado) return porId.Values.Count(m => m.Estado == EstadoMensaje.AceptadoPorMeta && m.AceptadoEn <= limite);
    }

    /// <summary>Guarda un mensaje entrante. Meta puede reenviar el mismo aviso: se guarda una sola vez.</summary>
    public bool RegistrarRecibido(MensajeEntrante entrante)
    {
        lock (candado)
        {
            if (recibidos.ContainsKey(entrante.Id)) return false;

            Guid? enRespuestaA = entrante.Contexto?.IdMensaje is { } idContexto && porIdMeta.TryGetValue(idContexto, out var original)
                ? original.Id
                : null;

            recibidos[entrante.Id] = new MensajeRecibido(entrante.Id, entrante.De, entrante.Tipo, entrante.TextoLegible,
                enRespuestaA, LeerFecha(entrante.MarcaTiempo) ?? reloj.GetUtcNow());
            return true;
        }
    }

    public IReadOnlyList<MensajeRecibido> Recibidos()
    {
        lock (candado) return recibidos.Values.OrderByDescending(r => r.Fecha).ToList();
    }

    private static DateTimeOffset? LeerFecha(string? marcaUnix) =>
        long.TryParse(marcaUnix, out var segundos) ? DateTimeOffset.FromUnixTimeSeconds(segundos) : null;
}
