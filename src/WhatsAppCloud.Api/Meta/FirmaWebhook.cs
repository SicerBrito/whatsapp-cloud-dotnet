using System.Security.Cryptography;
using System.Text;

namespace WhatsAppCloud.Api.Meta;

/// <summary>
/// Meta firma el cuerpo de cada llamada al webhook con HMAC-SHA256 y el secreto de la app, y
/// manda el resultado en el encabezado X-Hub-Signature-256 como "sha256=&lt;hex&gt;". Sin esta
/// comprobación, cualquiera que conozca la URL podría marcar mensajes como entregados.
/// </summary>
public static class FirmaWebhook
{
    public const string Encabezado = "X-Hub-Signature-256";
    private const string Prefijo = "sha256=";

    public static bool EsValida(ReadOnlySpan<byte> cuerpo, string? encabezado, string secretoApp)
    {
        if (string.IsNullOrEmpty(encabezado) || !encabezado.StartsWith(Prefijo, StringComparison.Ordinal))
            return false;

        byte[] recibida;
        try
        {
            recibida = Convert.FromHexString(encabezado.AsSpan(Prefijo.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        var esperada = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretoApp), cuerpo);
        // Comparación en tiempo constante: no revela cuántos bytes coinciden.
        return CryptographicOperations.FixedTimeEquals(esperada, recibida);
    }

    /// <summary>Calcula la firma como lo hace Meta. Sirve para pruebas y para simular llamadas.</summary>
    public static string Calcular(ReadOnlySpan<byte> cuerpo, string secretoApp) =>
        Prefijo + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretoApp), cuerpo)).ToLowerInvariant();
}
