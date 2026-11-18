using Microsoft.Extensions.Options;
using WhatsAppCloud.Api.Configuracion;
using WhatsAppCloud.Api.Meta;

namespace WhatsAppCloud.Api.Envios;

/// <summary>
/// Saca mensajes de la fila y los envía. Qué hace con cada fallo depende de quién tiene la culpa:
/// <list type="bullet">
///   <item>De la persona o del mensaje: queda fallido y sigue con el siguiente.</item>
///   <item>Del momento: lo reintenta más tarde, con espera creciente, sin frenar a los demás.</item>
///   <item>De la cuenta: detiene la fila entera hasta que alguien revise y reanude.</item>
/// </list>
/// Si el tope de 24 horas está lleno, el mensaje espera un cupo en vez de mandarse igual: pasado
/// el tope, Meta acepta el envío sin error y no lo entrega, y nadie se entera.
/// </summary>
public sealed class ProcesadorEnvios(
    FilaEnvios fila,
    RegistroMensajes registro,
    TopeDiario tope,
    FrenoEnvios freno,
    IServiceProvider servicios,
    IOptions<WhatsAppOpciones> opciones,
    TimeProvider reloj,
    ILogger<ProcesadorEnvios> log) : BackgroundService
{
    private static readonly TimeSpan EsperaMaximaPorCupo = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var id in fila.Lector.ReadAllAsync(ct))
        {
            var mensaje = registro.Obtener(id);
            if (mensaje is null || mensaje.Estado != EstadoMensaje.EnFila) continue;

            try
            {
                await ProcesarAsync(mensaje, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Un mensaje raro no puede tumbar la fila de todos los demás.
                log.LogError(ex, "Error inesperado al procesar el mensaje {Id}.", id);
                registro.MarcarFallido(id, new ErrorEnvio("error_interno", null, ex.Message, ClaseError.Mensaje));
            }
        }
    }

    private async Task ProcesarAsync(MensajeSaliente mensaje, CancellationToken ct)
    {
        var o = opciones.Value;

        // Con desvío configurado, todo sale hacia el número de pruebas, sin importar a quién iba.
        var solicitud = o.HayDesvio
            ? mensaje.Solicitud with { Destinatario = Numeros.Normalizar(o.NumeroDesvioPruebas) }
            : mensaje.Solicitud;
        var nota = o.HayDesvio ? $"Desviado a {solicitud.Destinatario} (NumeroDesvioPruebas)." : null;

        while (true)
        {
            await freno.EsperarSiPausadoAsync(ct);

            var esNuevo = await ReservarCupoAsync(mensaje.Id, solicitud.Destinatario, ct);
            var intento = registro.SumarIntento(mensaje.Id);

            var cliente = servicios.GetRequiredService<IClienteWhatsApp>();
            var resultado = await cliente.EnviarPlantillaAsync(solicitud, ct);

            if (resultado.Exitoso)
            {
                registro.MarcarAceptado(mensaje.Id, resultado.IdMeta!, nota);
                return;
            }

            // Un envío rechazado no cuenta para Meta: se devuelve el cupo si era nuevo.
            if (esNuevo) tope.Liberar(solicitud.Destinatario);

            var error = resultado.Error!;
            log.LogWarning("Meta rechazó el mensaje {Id} (intento {Intento}): {Codigo} ({CodigoMeta}), {Clase}.",
                mensaje.Id, intento, error.Codigo, error.CodigoMeta, error.Clase);

            switch (error.Clase)
            {
                case ClaseError.Cuenta:
                    // El mensaje no tiene la culpa: no queda fallido, espera la reanudación y sale.
                    freno.Pausar($"Meta rechazó un envío por un problema de la cuenta: {error.Codigo} ({error.CodigoMeta}). {error.Detalle}");
                    registro.Anotar(mensaje.Id, $"La fila se detuvo por {error.Codigo}; este mensaje sale al reanudar.");
                    continue;

                case ClaseError.Temporal when intento < o.MaximoIntentos:
                    var espera = o.EsperaPrimerReintento * Math.Pow(2, intento - 1);
                    registro.Anotar(mensaje.Id, $"Falló por {error.Codigo}; se reintenta en {espera:g}.");
                    fila.EncolarDespues(mensaje.Id, espera, reloj, ct);
                    return;

                default:
                    registro.MarcarFallido(mensaje.Id, error);
                    return;
            }
        }
    }

    /// <summary>Espera hasta que haya cupo en la ventana de 24 horas. Devuelve si la persona entró nueva.</summary>
    private async Task<bool> ReservarCupoAsync(Guid id, string destinatario, CancellationToken ct)
    {
        bool esNuevo;
        while (!tope.IntentarReservar(destinatario, out esNuevo))
        {
            var espera = tope.EsperaHastaCupo();
            log.LogInformation("Tope de {Tope} personas en 24 h lleno; el mensaje {Id} espera {Espera}.", tope.Tope, id, espera);
            await Task.Delay(espera < EsperaMaximaPorCupo ? espera : EsperaMaximaPorCupo, reloj, ct);
        }
        return esNuevo;
    }
}
