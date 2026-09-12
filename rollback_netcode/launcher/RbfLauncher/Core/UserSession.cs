using System;

namespace RbfLauncher.Core
{
    /// <summary>The player who is logged in, and the tokens that prove it.
    ///
    /// In memory only. Nothing here is written to `rbf-launcher.json` - a
    /// refresh token on disk next to the exe is a password on disk next to the
    /// exe, and this launcher runs on machines people share. Closing the
    /// launcher logs you out; that is the intended trade.
    ///
    /// Everything above the network layer reads this, so it is deliberately
    /// dumb: fields, no behaviour.</summary>
    public sealed class UserSession
    {
        public int    Id       { get; set; }
        public string Username { get; set; } = "";
        public string Email    { get; set; } = "";

        /// <summary>Null or empty when the player never set one.</summary>
        public string Nickname { get; set; }

        public int    Ranking  { get; set; }

        public string AccessToken  { get; set; } = "";
        public string RefreshToken { get; set; } = "";

        /// <summary>When the access token in hand was issued. The lobby verifies
        /// the token at connect time, so a launcher left open for two hours
        /// would be refused on the next Conectar with nothing to show for it -
        /// this is what lets it renew first instead.</summary>
        public DateTime AccessIssuedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Past two thirds of the hour the API gives it. Early enough
        /// that a slow renewal still lands inside the window.</summary>
        public bool AccessNearlyExpired =>
            DateTime.UtcNow - AccessIssuedUtc > TimeSpan.FromMinutes(40);

        /// <summary>What to show on screen: the nickname when there is one, the
        /// username otherwise. The API sends `nickname: null` for a player who
        /// has not picked one, so this is the normal case, not the edge.</summary>
        public string DisplayName =>
            string.IsNullOrWhiteSpace(Nickname) ? Username : Nickname.Trim();

        // ---- the one that is logged in right now ----------------------------
        /// <summary>Null when nobody is logged in. Set by the login flow and
        /// cleared on logout; read by the main window and by matchmaking.</summary>
        public static UserSession Current { get; private set; }

        public static bool IsLoggedIn => Current != null;

        public static void SetCurrent(UserSession s) => Current = s;

        public static void Clear() => Current = null;

        public override string ToString() => $"{DisplayName} (rank {Ranking})";
    }
}
