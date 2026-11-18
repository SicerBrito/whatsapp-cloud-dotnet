namespace WhatsAppCloud.Api.Envios;

public static class Numeros
{
    /// <summary>
    /// El número va con indicativo de país y solo dígitos (573001234567). Se aceptan espacios,
    /// guiones y el "+" inicial porque así lo escribe la gente; se quitan antes de enviar.
    /// </summary>
    public static string Normalizar(string numero) => new(numero.Where(char.IsAsciiDigit).ToArray());

    public static bool EsValido(string normalizado) => normalizado.Length is >= 10 and <= 15;
}
