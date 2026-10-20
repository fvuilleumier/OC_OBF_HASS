namespace Obfuscator.Core.Model.Detectors;

/// <summary>
/// Sommes de contrôle des identifiants normalisés.
///
/// Ces vérifications transforment des expressions régulières très permissives
/// — une suite de chiffres ressemble à beaucoup de choses — en détecteurs quasi
/// exempts de faux positifs. Une référence de facture ou un numéro de série ne
/// satisfait pas par hasard la clé de contrôle d'un IBAN ou d'un numéro AVS.
/// </summary>
public static class Checksums
{
    /// <summary>
    /// Vérifie la clé de contrôle d'un IBAN par la méthode modulo 97 (ISO 7064).
    /// </summary>
    /// <param name="iban">IBAN, avec ou sans espaces</param>
    /// <returns><c>true</c> si la clé de contrôle est valide</returns>
    public static bool IsValidIban(string iban)
    {
        ArgumentNullException.ThrowIfNull(iban);

        string compact = iban.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (compact.Length is < 15 or > 34)
        {
            return false;
        }

        // Les quatre premiers caractères (pays et clé) passent en fin de chaîne,
        // puis chaque lettre devient sa position dans l'alphabet majorée de 9.
        string rearranged = string.Concat(compact.AsSpan(4), compact.AsSpan(0, 4));
        int remainder = 0;

        foreach (char character in rearranged)
        {
            int value;
            if (char.IsAsciiDigit(character))
            {
                value = character - '0';
                remainder = ((remainder * 10) + value) % 97;
            }
            else if (char.IsAsciiLetterUpper(character))
            {
                value = character - 'A' + 10;
                remainder = ((remainder * 100) + value) % 97;
            }
            else
            {
                return false;
            }
        }

        return remainder == 1;
    }

    /// <summary>
    /// Vérifie la clé de contrôle d'un numéro AVS/AHV suisse, qui suit la règle
    /// de la clé EAN-13.
    /// </summary>
    /// <param name="digits">Les treize chiffres du numéro, sans séparateurs</param>
    /// <returns><c>true</c> si la clé de contrôle est valide</returns>
    public static bool IsValidEan13(string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);

        if (digits.Length != 13 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        int sum = 0;
        for (int position = 0; position < 12; position++)
        {
            int digit = digits[position] - '0';
            sum += position % 2 == 0 ? digit : digit * 3;
        }

        int expected = (10 - (sum % 10)) % 10;
        return expected == digits[12] - '0';
    }

    /// <summary>
    /// Vérifie la clé de contrôle d'un numéro IDE/UID d'entreprise suisse.
    /// </summary>
    /// <param name="digits">Les neuf chiffres du numéro, sans le préfixe CHE ni les points</param>
    /// <returns><c>true</c> si la clé de contrôle est valide</returns>
    public static bool IsValidSwissBusinessIdentifier(string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);

        if (digits.Length != 9 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        ReadOnlySpan<int> weights = [5, 4, 3, 2, 7, 6, 5, 4];
        int sum = 0;
        for (int position = 0; position < weights.Length; position++)
        {
            sum += (digits[position] - '0') * weights[position];
        }

        int remainder = sum % 11;
        int expected = remainder switch
        {
            0 => 0,
            1 => -1, // Combinaison impossible : le numéro n'existe pas.
            _ => 11 - remainder,
        };

        return expected >= 0 && expected == digits[8] - '0';
    }

    /// <summary>
    /// Vérifie la clé de contrôle d'un numéro de carte de paiement (algorithme
    /// de Luhn).
    /// </summary>
    /// <param name="digits">Les chiffres du numéro, sans séparateurs</param>
    /// <returns><c>true</c> si la clé de contrôle est valide</returns>
    public static bool IsValidLuhn(string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);

        if (digits.Length is < 13 or > 19 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        int sum = 0;
        bool doubled = false;

        for (int position = digits.Length - 1; position >= 0; position--)
        {
            int digit = digits[position] - '0';
            if (doubled)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubled = !doubled;
        }

        return sum % 10 == 0;
    }
}
