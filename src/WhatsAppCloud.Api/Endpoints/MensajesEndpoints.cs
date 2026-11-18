using Microsoft.Extensions.Options;
using WhatsAppCloud.Api.Configuracion;
using WhatsAppCloud.Api.Envios;
using WhatsAppCloud.Api.Meta;

namespace WhatsAppCloud.Api.Endpoints;

public sealed record PeticionPlantilla(
    string? Destinatario,
    string? Plantilla,
    string? Idioma,
    List<string>? Parametros,
    string? BotonUrl = null,
    string? Referencia = null);

public sealed record VistaMensaje(
    Guid Id,
    string? Referencia,
    string Destinatario,
    string Plantilla,
    string Estado,
    string? IdMeta,
    int Intentos,
    ErrorEnvio? Error,
    CobroMensaje? Cobro,
    IEnumerable<CambioEstado> Historial)
{
    public static VistaMensaje Desde(MensajeSaliente m) =>
        new(m.Id, m.Referencia, m.Solicitud.Destinatario, m.Solicitud.Plantilla, m.Estado.ToString(), m.IdMeta,
            m.Intentos, m.Error, m.Cobro, m.Historial);
}

public sealed record VistaEstadoEnvios(
    bool ModoSimulado,
    string? DesvioPruebas,
    bool Pausado,
    string? MotivoPausa,
    DateTimeOffset? PausadoDesde,
    int TopePorDia,
    int PersonasEnVentana,
    int MensajesEnFila,
    int AceptadosSinAviso,
    string? Alerta);

public sealed record ResultadoReanudar(int MensajesDeVueltaEnFila);

public static class MensajesEndpoints
{
    /// <summary>Tiempo sin noticias de Meta a partir del cual un mensaje aceptado se vuelve sospechoso.</summary>
    public static readonly TimeSpan UmbralSinAviso = TimeSpan.FromMinutes(15);

    public static void MapMensajes(this IEndpointRouteBuilder app)
    {
        app.MapPost("/mensajes/plantilla", (PeticionPlantilla peticion, RegistroMensajes registro, FilaEnvios fila) =>
        {
            var parametros = (peticion.Parametros ?? []).Select(VariablesPlantilla.Limpiar).ToList();
            var errores = Validar(peticion, parametros, out var destinatario);
            if (errores.Count > 0) return Results.ValidationProblem(errores);

            var solicitud = new SolicitudPlantilla(
                destinatario,
                peticion.Plantilla!.Trim(),
                string.IsNullOrWhiteSpace(peticion.Idioma) ? "es" : peticion.Idioma.Trim(),
                parametros,
                string.IsNullOrWhiteSpace(peticion.BotonUrl) ? null : peticion.BotonUrl.Trim());

            var referencia = string.IsNullOrWhiteSpace(peticion.Referencia) ? null : peticion.Referencia.Trim();
            var (mensaje, esNuevo) = registro.Crear(solicitud, referencia);

            if (!esNuevo)
            {
                // La misma referencia con otro destinatario no es un reintento: es un error de quien llama.
                if (mensaje.Solicitud.Destinatario != destinatario)
                    return Results.Problem($"La referencia '{referencia}' ya se usó para otro número.", statusCode: StatusCodes.Status409Conflict);

                // Reintento de quien llama: se le devuelve el mismo mensaje y no se envía otra vez.
                return Results.Ok(VistaMensaje.Desde(mensaje));
            }

            fila.Encolar(mensaje.Id);

            // 202: el mensaje queda en fila; su avance se consulta en /mensajes/{id}.
            return Results.Accepted($"/mensajes/{mensaje.Id}", VistaMensaje.Desde(mensaje));
        });

        app.MapGet("/mensajes/{id:guid}", (Guid id, RegistroMensajes registro) =>
            registro.Obtener(id) is { } mensaje ? Results.Ok(VistaMensaje.Desde(mensaje)) : Results.NotFound());

        app.MapGet("/mensajes/recibidos", (RegistroMensajes registro) => Results.Ok(registro.Recibidos()));

        app.MapGet("/estado-envios", (TopeDiario tope, RegistroMensajes registro, FrenoEnvios freno, IOptions<WhatsAppOpciones> opciones) =>
        {
            var o = opciones.Value;
            var sinAviso = registro.AceptadosSinAviso(UmbralSinAviso);
            var alerta = sinAviso == 0 ? null :
                $"Meta aceptó {sinAviso} mensaje(s) hace más de {UmbralSinAviso.TotalMinutes:0} minutos y no ha avisado nada. " +
                "Si pasa con todos, los avisos no están llegando: revisa que el webhook esté configurado y que la " +
                "cuenta de WhatsApp Business esté suscrita a la app (POST /{id-cuenta}/subscribed_apps).";

            return Results.Ok(new VistaEstadoEnvios(
                o.ModoSimulado,
                o.HayDesvio ? Numeros.Normalizar(o.NumeroDesvioPruebas) : null,
                freno.Pausado, freno.Motivo, freno.PausadoDesde,
                tope.Tope, tope.EnUso(), registro.EnFila(), sinAviso, alerta));
        });

        // Botón de parar: para cuando alguien ve algo raro y prefiere detener todo antes de averiguar.
        app.MapPost("/envios/pausar", (FrenoEnvios freno) =>
        {
            freno.Pausar("Pausado a mano.");
            return Results.NoContent();
        });

        // Al reanudar, lo que falló por la cuenta o por el momento vuelve a la fila: esas personas
        // no tenían la culpa y no deben quedarse sin su mensaje.
        app.MapPost("/envios/reanudar", (FrenoEnvios freno, RegistroMensajes registro, FilaEnvios fila) =>
        {
            freno.Reanudar();
            var reabiertos = registro.ReabrirFallidosNoAtribuibles();
            foreach (var id in reabiertos) fila.Encolar(id);
            return Results.Ok(new ResultadoReanudar(reabiertos.Count));
        });
    }

    private static Dictionary<string, string[]> Validar(PeticionPlantilla p, List<string> parametros, out string destinatario)
    {
        var errores = new Dictionary<string, string[]>();
        destinatario = Numeros.Normalizar(p.Destinatario ?? "");

        if (!Numeros.EsValido(destinatario))
            errores["destinatario"] = ["Escribe el número con indicativo de país, por ejemplo 573001234567."];
        if (string.IsNullOrWhiteSpace(p.Plantilla))
            errores["plantilla"] = ["Falta el nombre de la plantilla aprobada en Meta."];
        if (parametros.Any(string.IsNullOrEmpty))
            errores["parametros"] = ["Las variables de la plantilla no pueden ir vacías: Meta rechaza el envío."];
        if (p.Referencia?.Trim().Length > 100)
            errores["referencia"] = ["La referencia admite hasta 100 caracteres."];

        return errores;
    }
}
