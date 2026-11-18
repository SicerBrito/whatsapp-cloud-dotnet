using Microsoft.Extensions.Options;
using WhatsAppCloud.Api.Configuracion;
using WhatsAppCloud.Api.Meta;

namespace WhatsAppCloud.Api.Envios;

/// <summary>
/// Detiene la fila cuando el problema es de la cuenta y no de las personas.
///
/// Con el pago vencido, el token caducado o una plantilla pausada, cada mensaje que sigue en la
/// fila va a fallar igual. Seguir enviando no arregla nada: gasta los intentos de gente que no
/// tiene la culpa y llena el registro de fallos. Se pausa, alguien revisa la cuenta y reanuda.
/// </summary>
public sealed class FrenoEnvios(IOptions<WhatsAppOpciones> opciones, TimeProvider reloj)
{
    private static readonly TimeSpan Ventana = TimeSpan.FromHours(1);

    private readonly object candado = new();
    private readonly Queue<DateTimeOffset> fallosDeCuenta = new();
    private TaskCompletionSource? reanudar;

    public string? Motivo { get; private set; }
    public DateTimeOffset? PausadoDesde { get; private set; }
    public bool Pausado
    {
        get { lock (candado) return reanudar is not null; }
    }

    /// <summary>Detiene la fila de inmediato. Si ya estaba detenida, conserva el primer motivo.</summary>
    public void Pausar(string motivo)
    {
        lock (candado)
        {
            if (reanudar is not null) return;
            reanudar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Motivo = motivo;
            PausadoDesde = reloj.GetUtcNow();
        }
    }

    /// <summary>
    /// Anota un fallo de la cuenta que Meta reportó después de aceptar el mensaje. Devuelve true si
    /// con este se alcanzó el umbral y la fila quedó detenida.
    /// </summary>
    public bool RegistrarFalloDeCuenta(ErrorEnvio error)
    {
        if (error.Clase != ClaseError.Cuenta) return false;

        lock (candado)
        {
            var ahora = reloj.GetUtcNow();
            fallosDeCuenta.Enqueue(ahora);
            while (fallosDeCuenta.Count > 0 && ahora - fallosDeCuenta.Peek() > Ventana) fallosDeCuenta.Dequeue();

            if (reanudar is not null || fallosDeCuenta.Count < opciones.Value.FallosDeCuentaParaPausar) return false;
        }

        Pausar($"Meta reportó {opciones.Value.FallosDeCuentaParaPausar} fallos de la cuenta en menos de una hora. " +
               $"El último: {error.Codigo} ({error.CodigoMeta}). Revisa la cuenta antes de reanudar.");
        return true;
    }

    public void Reanudar()
    {
        TaskCompletionSource? pendiente;
        lock (candado)
        {
            pendiente = reanudar;
            reanudar = null;
            Motivo = null;
            PausadoDesde = null;
            fallosDeCuenta.Clear();
        }
        pendiente?.TrySetResult();
    }

    /// <summary>Espera a que alguien reanude, si la fila está detenida.</summary>
    public Task EsperarSiPausadoAsync(CancellationToken ct)
    {
        Task espera;
        lock (candado) espera = reanudar?.Task ?? Task.CompletedTask;
        return espera.WaitAsync(ct);
    }
}
