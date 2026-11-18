using Microsoft.Extensions.Time.Testing;
using WhatsAppCloud.Api.Envios;
using WhatsAppCloud.Api.Meta;

namespace WhatsAppCloud.Tests;

public class RegistroMensajesTests
{
    private readonly FakeTimeProvider reloj = new(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero));
    private readonly RegistroMensajes registro;

    public RegistroMensajesTests() => registro = new RegistroMensajes(reloj);

    private static readonly SolicitudPlantilla Solicitud = new("570000000001", "recordatorio_cita", "es", ["Ana"]);

    private MensajeSaliente CrearAceptado(string idMeta)
    {
        var (mensaje, _) = registro.Crear(Solicitud);
        registro.MarcarAceptado(mensaje.Id, idMeta);
        return mensaje;
    }

    private static EstadoWebhook Estado(string idMeta, string estado, params ErrorWebhook[] errores) =>
        new(idMeta, estado, "1795000000", "570000000001", errores.Length == 0 ? null : [.. errores]);

    private bool Aplicar(EstadoWebhook estado) => registro.AplicarEstado(estado, out _);

    [Fact]
    public void Un_mensaje_nuevo_empieza_en_fila()
    {
        var (mensaje, esNuevo) = registro.Crear(Solicitud);

        Assert.True(esNuevo);
        Assert.Equal(EstadoMensaje.EnFila, mensaje.Estado);
        Assert.Equal(1, registro.EnFila());
    }

    [Fact]
    public void La_misma_referencia_devuelve_el_mismo_mensaje_sin_crear_otro()
    {
        var (primero, _) = registro.Crear(Solicitud, "cita-8841");
        var (segundo, esNuevo) = registro.Crear(Solicitud, "cita-8841");

        Assert.False(esNuevo);
        Assert.Equal(primero.Id, segundo.Id);
        Assert.Equal(1, registro.EnFila());
    }

    [Fact]
    public void Leido_antes_que_entregado_deja_el_mensaje_en_leido()
    {
        var mensaje = CrearAceptado("wamid.1");

        Assert.True(Aplicar(Estado("wamid.1", "read")));
        Assert.False(Aplicar(Estado("wamid.1", "delivered")));

        Assert.Equal(EstadoMensaje.Leido, mensaje.Estado);
    }

    [Fact]
    public void Un_aviso_repetido_se_ignora()
    {
        var mensaje = CrearAceptado("wamid.2");

        Assert.True(Aplicar(Estado("wamid.2", "delivered")));
        Assert.False(Aplicar(Estado("wamid.2", "delivered")));

        Assert.Equal(3, mensaje.Historial.Count); // en fila, aceptado, entregado
    }

    [Fact]
    public void Fallido_guarda_el_error_traducido_con_su_detalle_y_es_final()
    {
        var mensaje = CrearAceptado("wamid.3");

        var aplicado = registro.AplicarEstado(Estado("wamid.3", "failed",
            new ErrorWebhook(131049, "Not delivered", Datos: new DatosErrorMeta("Para mantener el compromiso del ecosistema"))), out var fallo);
        Assert.False(Aplicar(Estado("wamid.3", "delivered")));

        Assert.True(aplicado);
        Assert.Equal(EstadoMensaje.Fallido, mensaje.Estado);
        Assert.Equal("retenido_por_meta", fallo!.Codigo);
        Assert.Contains("ecosistema", mensaje.Error!.Detalle);
    }

    [Fact]
    public void Un_fallido_sin_errores_igual_queda_como_fallo()
    {
        var mensaje = CrearAceptado("wamid.4");

        Assert.True(Aplicar(Estado("wamid.4", "failed")));

        Assert.Equal(EstadoMensaje.Fallido, mensaje.Estado);
        Assert.NotNull(mensaje.Error);
    }

    [Fact]
    public void Un_estado_de_un_mensaje_que_no_es_nuestro_se_ignora()
    {
        Assert.False(Aplicar(Estado("wamid.ajeno", "delivered")));
    }

    [Fact]
    public void El_cobro_se_guarda_aunque_el_aviso_llegue_tarde()
    {
        var mensaje = CrearAceptado("wamid.5");
        Aplicar(Estado("wamid.5", "read"));

        var tardio = Estado("wamid.5", "delivered") with { Cobro = new CobroWebhook("utility", "regular", true) };
        Assert.False(Aplicar(tardio));

        Assert.Equal("utility", mensaje.Cobro!.Categoria);
        Assert.Equal(EstadoMensaje.Leido, mensaje.Estado);
    }

    [Fact]
    public void Al_reabrir_solo_vuelve_lo_que_no_era_culpa_de_la_persona()
    {
        var pago = CrearAceptado("wamid.pago");
        var sinWhatsApp = CrearAceptado("wamid.sin");
        Aplicar(Estado("wamid.pago", "failed", new ErrorWebhook(131042, "Business eligibility payment issue")));
        Aplicar(Estado("wamid.sin", "failed", new ErrorWebhook(131026, "Message undeliverable")));

        var reabiertos = registro.ReabrirFallidosNoAtribuibles();

        Assert.Equal(new[] { pago.Id }, reabiertos);
        Assert.Equal(EstadoMensaje.EnFila, pago.Estado);
        Assert.Equal(EstadoMensaje.Fallido, sinWhatsApp.Estado);
    }

    [Fact]
    public void Un_aviso_atrasado_del_intento_anterior_no_mueve_el_reintento()
    {
        var mensaje = CrearAceptado("wamid.viejo");
        Aplicar(Estado("wamid.viejo", "failed", new ErrorWebhook(131042, "Payment issue")));
        registro.ReabrirFallidosNoAtribuibles();

        // Llega tarde un "delivered" del intento que falló, antes de que salga el nuevo.
        Assert.False(Aplicar(Estado("wamid.viejo", "delivered")));
        Assert.Equal(EstadoMensaje.EnFila, mensaje.Estado);

        registro.MarcarAceptado(mensaje.Id, "wamid.nuevo");
        Assert.False(Aplicar(Estado("wamid.viejo", "read")));
        Assert.True(Aplicar(Estado("wamid.nuevo", "delivered")));
        Assert.Equal(EstadoMensaje.Entregado, mensaje.Estado);
    }

    [Fact]
    public void Cuenta_los_aceptados_de_los_que_Meta_no_vuelve_a_decir_nada()
    {
        CrearAceptado("wamid.mudo");
        var conAviso = CrearAceptado("wamid.habla");
        Aplicar(Estado("wamid.habla", "sent"));

        reloj.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(0, registro.AceptadosSinAviso(TimeSpan.FromMinutes(15)));

        reloj.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, registro.AceptadosSinAviso(TimeSpan.FromMinutes(15)));
        Assert.Equal(EstadoMensaje.Enviado, conAviso.Estado);
    }

    [Fact]
    public void Un_mensaje_entrante_se_guarda_una_sola_vez()
    {
        var entrante = new MensajeEntrante("570000000001", "wamid.in.1", "1795000000", "text", new TextoEntrante("Confirmo"));

        Assert.True(registro.RegistrarRecibido(entrante));
        Assert.False(registro.RegistrarRecibido(entrante));
        Assert.Single(registro.Recibidos());
    }

    [Fact]
    public void El_boton_de_respuesta_rapida_queda_atado_al_mensaje_que_lo_trajo()
    {
        var recordatorio = CrearAceptado("wamid.recordatorio");
        var respuesta = new MensajeEntrante("570000000001", "wamid.in.2", "1795000100", "button", null,
            Boton: new BotonEntrante("Confirmo mi cita", "CONFIRMAR"), Contexto: new ContextoEntrante("wamid.recordatorio"));

        registro.RegistrarRecibido(respuesta);

        var recibido = Assert.Single(registro.Recibidos());
        Assert.Equal("Confirmo mi cita", recibido.Texto);
        Assert.Equal(recordatorio.Id, recibido.EnRespuestaA);
    }
}
