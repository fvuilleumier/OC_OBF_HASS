using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Obfuscator.Core.Model.Engine;

namespace Obfuscator.Core.Model.Mapping.Strategies;

/// <summary>
/// Remplace une coordonnée géographique — latitude, longitude, altitude — par
/// un autre nombre de même forme.
///
/// La forme est gardée pour que le fichier reste valide et lisible : autant de
/// décimales, un nombre qui reste une latitude ou une longitude possible, une
/// altitude au même nombre de chiffres. Le lieu, lui, ne l'est pas : chaque
/// nombre est tiré indépendamment, par la clé secrète du coffre et le rang de
/// l'attribution, sans aucun décalage commun. Un décalage commun garderait les
/// distances entre les lieux, mais une seule coordonnée réelle devinée — celle
/// d'un lieu public nommé dans le fichier — livrerait toutes les autres, le
/// domicile compris.
/// </summary>
/// <param name="store">Coffre du projet, qui porte la clé de tirage</param>
public sealed class CoordinatePseudonymStrategy(MappingStore store) : IPseudonymStrategy
{
    /// <summary>Nombre de tirages essayés avant de renoncer à un nombre différent de l'original.</summary>
    private const int MaximumDraws = 16;

    private readonly byte[] _key = (store ?? throw new ArgumentNullException(nameof(store))).AllocationKey;

    /// <inheritdoc />
    public bool CanHandle(SpanCategory category) => category == SpanCategory.Coordinate;

    /// <inheritdoc />
    public string Create(PseudonymRequest request)
    {
        string original = request.Original.Trim();
        if (!TryReadShape(original, out Shape shape))
        {
            return request.Format.Compose(SpanCategory.Coordinate.FamilyCode(), request.Index);
        }

        // Un tirage qui retomberait sur la valeur réelle la laisserait en
        // clair : on tire de nouveau, ce qui n'arrive pour ainsi dire jamais.
        for (int attempt = 0; attempt < MaximumDraws; attempt++)
        {
            string candidate = Format(shape, Draw(request.Index, attempt));
            if (!string.Equals(candidate, original, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return request.Format.Compose(SpanCategory.Coordinate.FamilyCode(), request.Index);
    }

    /// <summary>
    /// Lit la forme d'une coordonnée : signe, chiffres de la partie entière,
    /// décimales, et l'intervalle dans lequel tirer son remplaçant.
    /// </summary>
    /// <param name="value">Coordonnée d'origine</param>
    /// <param name="shape">Forme lue</param>
    /// <returns><c>true</c> si la valeur est un nombre</returns>
    private static bool TryReadShape(string value, out Shape shape)
    {
        shape = default;
        if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number))
        {
            return false;
        }

        int point = value.IndexOf('.', StringComparison.Ordinal);
        int decimals = point < 0 ? 0 : value.Length - point - 1;
        string integerPart = (point < 0 ? value : value[..point]).TrimStart('+', '-');
        decimal magnitude = Math.Abs(number);

        // Une valeur décimale qui tient dans ±90 ou ±180 peut être une latitude
        // ou une longitude : son remplaçant reste dans le même intervalle, de
        // signe quelconque. Au-delà, c'est une altitude ou une mesure : son
        // remplaçant garde son signe et son nombre de chiffres.
        if (decimals > 0 && magnitude <= 180)
        {
            shape = new Shape(decimals, magnitude <= 90 ? 90 : 180, Signed: true, MinimumInteger: 0, Negative: false);
            return true;
        }

        int digits = Math.Max(1, integerPart.Length);
        decimal upper = Pow10(digits) - 1;
        decimal lower = digits == 1 ? 0 : Pow10(digits - 1);
        shape = new Shape(decimals, upper, Signed: false, MinimumInteger: lower, Negative: number < 0);
        return true;
    }

    /// <summary>
    /// Compose un nombre de la forme voulue à partir d'un tirage.
    /// </summary>
    /// <param name="shape">Forme à respecter</param>
    /// <param name="draw">Tirage pseudo-aléatoire</param>
    /// <returns>Le nombre, écrit avec le nombre de décimales voulu</returns>
    private static string Format(Shape shape, ulong draw)
    {
        decimal scale = Pow10(shape.Decimals);
        decimal value;

        // Le reste est pris en décimal : le nombre de pas peut dépasser la
        // capacité d'un entier de 64 bits quand les décimales sont nombreuses.
        if (shape.Signed)
        {
            // Intervalle [-borne, +borne], au pas de la dernière décimale.
            decimal steps = (shape.Bound * scale * 2) + 1;
            value = ((draw % steps) / scale) - shape.Bound;
        }
        else
        {
            // Partie entière dans [minimum, borne], décimales quelconques.
            decimal steps = (shape.Bound - shape.MinimumInteger + 1) * scale;
            value = shape.MinimumInteger + ((draw % steps) / scale);
            value = shape.Negative ? -value : value;
        }

        return value.ToString("F" + shape.Decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Tire un nombre pseudo-aléatoire, reproductible pour un même coffre.
    /// </summary>
    /// <param name="index">Rang de l'attribution</param>
    /// <param name="attempt">Numéro de l'essai</param>
    /// <returns>Le nombre tiré</returns>
    private ulong Draw(int index, int attempt)
    {
        byte[] digest = HMACSHA256.HashData(
            _key,
            Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"Obfuscator/coordinate/{index}/{attempt}")));

        return BinaryPrimitives.ReadUInt64LittleEndian(digest);
    }

    /// <summary>
    /// Calcule une puissance de dix.
    /// </summary>
    /// <param name="exponent">Exposant, positif</param>
    /// <returns>Dix à la puissance demandée</returns>
    private static decimal Pow10(int exponent)
    {
        return (decimal)BigInteger.Pow(10, exponent);
    }

    /// <summary>
    /// Forme d'une coordonnée.
    /// </summary>
    /// <param name="Decimals">Nombre de décimales</param>
    /// <param name="Bound">Borne de l'intervalle : absolue pour un nombre signé, partie entière maximale sinon</param>
    /// <param name="Signed">Vrai pour tirer dans [-borne, +borne]</param>
    /// <param name="MinimumInteger">Partie entière minimale d'un nombre non signé</param>
    /// <param name="Negative">Vrai pour un nombre non signé à garder négatif</param>
    private readonly record struct Shape(int Decimals, decimal Bound, bool Signed, decimal MinimumInteger, bool Negative);
}
