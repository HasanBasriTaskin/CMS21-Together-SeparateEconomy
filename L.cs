using System.Collections.Generic;

namespace TogetherSeparateEconomy
{
    /// <summary>Tiny localization helper for on-screen popups. Language comes from Config.Language ("en" or "tr").</summary>
    public static class L
    {
        static readonly Dictionary<string, string> En_ = new Dictionary<string, string>
        {
            { "host_no_record", "The host did not send your record (timeout); inventory stays shared." },
            { "host_refused",   "Could not connect to the host ({0}); inventory stays shared." },
            { "cfg_differs",    "Your settings differ from the host; the host settings apply this session ({0})." },
            { "host_error",     "The host reported an error ({0}); inventory stays shared." },
            { "loaded",         "Your inventory was loaded: {0} parts, {1} money." },
            { "load_failed",    "Your inventory could not be loaded ({0}); inventory stays shared." },
        };

        static readonly Dictionary<string, string> Tr_ = new Dictionary<string, string>
        {
            { "host_no_record", "Host kaydını göndermedi (zaman aşımı); envanter ortak kalıyor." },
            { "host_refused",   "Host ile bağlantı kurulamadı ({0}); envanter ortak kalıyor." },
            { "cfg_differs",    "Ayarların host ile farklı; bu oturumda host ayarları geçerli ({0})." },
            { "host_error",     "Host hata bildirdi ({0}); envanter ortak kalıyor." },
            { "loaded",         "Envanterin yüklendi: {0} parça, {1} para." },
            { "load_failed",    "Envanter yüklenemedi ({0}); envanter ortak kalıyor." },
        };

        /// <summary>Localized text for the configured language (falls back to English, then to the key).</summary>
        public static string T(string key, params object[] args)
        {
            var d = Config.Language == "tr" ? Tr_ : En_;
            string s;
            if (!d.TryGetValue(key, out s) && !En_.TryGetValue(key, out s)) s = key;
            return Fmt(s, args);
        }

        /// <summary>English text, used for log lines.</summary>
        public static string En(string key, params object[] args)
        {
            string s;
            if (!En_.TryGetValue(key, out s)) s = key;
            return Fmt(s, args);
        }

        static string Fmt(string s, object[] args)
        {
            if (args == null || args.Length == 0) return s;
            try { return string.Format(s, args); } catch (System.FormatException) { return s; }
        }
    }
}
