namespace RbfLauncher.Core
{
    /// <summary>The name of a player as other people get to see it: the login
    /// (username), and never anything that looks like an e-mail address.
    ///
    /// The Frame Perfect API keeps e-mail private - it only comes back from the
    /// player's own /api/auth/me/ - but a username or nickname can still have
    /// been typed as an address at sign-up. So whatever a name came from, the
    /// part after '@' never reaches the screen.</summary>
    public static class PublicName
    {
        public static string Of(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string s = name.Trim();
            int at = s.IndexOf('@');
            return at > 0 ? s.Substring(0, at) : at == 0 ? "?" : s;
        }
    }
}
