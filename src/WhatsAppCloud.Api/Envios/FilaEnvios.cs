using System.Threading.Channels;

namespace WhatsAppCloud.Api.Envios;

/// <summary>Fila en memoria de mensajes por enviar, en orden de llegada.</summary>
public sealed class FilaEnvios
{
    private readonly Channel<Guid> canal = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public void Encolar(Guid idMensaje) => canal.Writer.TryWrite(idMensaje);

    /// <summary>
    /// Vuelve a poner el mensaje en la fila pasado un tiempo, sin detener a los que siguen. Lo usan
    /// los reintentos: esperar en línea dejaría a toda la fila esperando por un solo mensaje.
    /// </summary>
    public void EncolarDespues(Guid idMensaje, TimeSpan espera, TimeProvider reloj, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(espera, reloj, ct);
                Encolar(idMensaje);
            }
            catch (OperationCanceledException)
            {
                // El servicio se está apagando: en memoria no hay a dónde guardarlo.
            }
        }, CancellationToken.None);
    }

    public ChannelReader<Guid> Lector => canal.Reader;
}
