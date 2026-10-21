using System;
using System.Collections.Generic;
using System.Text;

namespace MHassnage.Core.Model.OwnException;

/// <summary>Hass a répondu à une commande par une erreur ({ success: false }).</summary>
public sealed class HassCommandException(string command, string code, string message) : Exception($"{command} : {code} — {message}")
{
    public string Code { get; } = code;
}
