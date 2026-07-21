using System;

namespace MuggaLuggaTD.Shared
{
    /// <summary>
    /// Error sink for the shared gameplay code, which cannot reference UnityEngine.Debug or
    /// ILogger directly. Each host installs its own sink at startup: the Unity client points it at
    /// Debug.LogError, the API at its ILogger. Defaults to a no-op so an uninitialised host stays
    /// silent rather than throwing.
    /// </summary>
    public static class SharedDiagnostics
    {
        private static Action<string> _errorSink = _ => { };

        /// <summary>Installs the host's error sink. Passing null restores the no-op sink.</summary>
        public static void SetErrorSink(Action<string> sink)
        {
            _errorSink = sink ?? (_ => { });
        }

        public static void LogError(string message)
        {
            _errorSink(message);
        }
    }
}
