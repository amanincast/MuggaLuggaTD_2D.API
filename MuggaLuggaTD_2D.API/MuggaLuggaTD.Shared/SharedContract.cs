namespace MuggaLuggaTD.Shared
{
    /// <summary>
    /// Identifies the version of the shared gameplay rules.
    ///
    /// The Unity client consumes this assembly as a DLL committed under Assets/Plugins, so it can
    /// fall behind the server's copy. Since both sides resolve PvP with this code, a stale client
    /// would compute power under different rules than the server. The client sends this version with
    /// every server-resolved action and the API rejects a mismatch, so the failure is a clear error
    /// instead of a silent disagreement about who won.
    ///
    /// Bump this whenever a change alters gameplay results — new or changed weights, modifier
    /// behaviour, or resolution thresholds. Pure refactors and comments do not need a bump.
    /// </summary>
    public static class SharedContract
    {
        // 1.1.0 — conquest rules (ConquestResolver + location enums) moved into this assembly when
        //   PvE conquest became server-applied; a 1.0.0 client still writes conquests through the
        //   world blob and must not be allowed to.
        // 1.2.0 — PvE rewards and ability-upgrade legality moved server-side. Older clients grant
        //   their own rewards and persist unvalidated upgrades, so must re-sync before playing.
        public const string Version = "1.2.0";
    }
}
