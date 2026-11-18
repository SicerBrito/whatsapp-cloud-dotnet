using Microsoft.Extensions.Options;
using WhatsAppCloud.Api.Configuracion;

namespace WhatsAppCloud.Api.Envios;

/// <summary>
/// Cuenta personas distintas contactadas en las últimas 24 horas, como lo cuenta Meta.
/// Escribirle otra vez a alguien que ya está en la ventana no gasta cupo, pero lo mantiene
/// dentro de ella 24 horas más desde ese último mensaje.
/// </summary>
public sealed class TopeDiario(IOptions<WhatsAppOpciones> opciones, TimeProvider reloj)
{
    private static readonly TimeSpan Ventana = TimeSpan.FromHours(24);
    private readonly object candado = new();
    private readonly Dictionary<string, DateTimeOffset> ultimoContacto = [];

    public int Tope => opciones.Value.TopeDestinatariosPorDia;

    /// <summary>Intenta reservar cupo. <paramref name="esNuevo"/> dice si la persona entró a la ventana ahora.</summary>
    public bool IntentarReservar(string destinatario, out bool esNuevo)
    {
        lock (candado)
        {
            var ahora = reloj.GetUtcNow();
            Limpiar(ahora);

            esNuevo = !ultimoContacto.ContainsKey(destinatario);
            if (esNuevo && ultimoContacto.Count >= Tope) return false;

            ultimoContacto[destinatario] = ahora;
            return true;
        }
    }

    /// <summary>Devuelve el cupo de una reserva nueva cuando Meta rechazó el envío.</summary>
    public void Liberar(string destinatario)
    {
        lock (candado) ultimoContacto.Remove(destinatario);
    }

    public int EnUso()
    {
        lock (candado)
        {
            Limpiar(reloj.GetUtcNow());
            return ultimoContacto.Count;
        }
    }

    /// <summary>Cuánto falta para que se libere el primer cupo; cero si ya hay.</summary>
    public TimeSpan EsperaHastaCupo()
    {
        lock (candado)
        {
            var ahora = reloj.GetUtcNow();
            Limpiar(ahora);
            if (ultimoContacto.Count < Tope) return TimeSpan.Zero;
            return ultimoContacto.Values.Min() + Ventana - ahora;
        }
    }

    private void Limpiar(DateTimeOffset ahora)
    {
        foreach (var vencido in ultimoContacto.Where(p => ahora - p.Value >= Ventana).Select(p => p.Key).ToList())
            ultimoContacto.Remove(vencido);
    }
}
