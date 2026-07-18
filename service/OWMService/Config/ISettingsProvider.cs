using System;

namespace OWMService.Config
{
    public interface ISettingsProvider
    {
        /// <summary>
        /// Try to read settings. Returns true on success and fills <paramref name="settings"/>.
        /// On failure returns false and a short explanation in <paramref name="errorMessage"/>.
        /// </summary>
        bool TryReadSettings(out Settings settings, out string errorMessage);
    }
}