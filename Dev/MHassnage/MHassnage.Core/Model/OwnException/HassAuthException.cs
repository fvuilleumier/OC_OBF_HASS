using System;
using System.Collections.Generic;
using System.Text;

namespace MHassnage.Core.Model.OwnException;

/// <summary>Hass a refusé le jeton (auth_invalid) : absent, expiré ou révoqué.</summary>
public sealed class HassAuthException(string message) : Exception(message);
