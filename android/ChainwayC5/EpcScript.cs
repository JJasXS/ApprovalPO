using System.Text;

namespace ChainwayC5
{
    public static class EpcScript
    {
        public const string ModeQuery =
            "(function(){var m=document.documentElement.getAttribute('data-chainway-mode');" +
            "return m==='continuous'?'continuous':'single';})()";

        public static bool IsHexEpc(string epc)
        {
            if (string.IsNullOrEmpty(epc) || epc.Length > 128)
                return false;
            foreach (var c in epc)
            {
                var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex)
                    return false;
            }
            return true;
        }

        public static string EventScript(string epc)
        {
            return "window.dispatchEvent(new CustomEvent('chainway:epc',{detail:{epc:'" +
                   Escape(epc) + "'}}));";
        }

        static string Escape(string value)
        {
            var sb = new StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                if (c == '\\' || c == '\'')
                    sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
