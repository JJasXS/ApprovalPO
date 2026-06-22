using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ApprovalPO.Services.Orders
{
    internal static class GrNumbering
    {
        public const string DefaultPrefix = "GR-";
        public const int DefaultPad = 5;

        /// <summary>Scans a collection of document numbers and returns (maxNumericSuffix, pad, prefix).</summary>
        public static (int Max, int Pad, string Prefix) SeedFromStrings(IEnumerable<string?> docnos)
        {
            var max = 0;
            var pad = DefaultPad;
            var prefix = DefaultPrefix;

            if (docnos is null) return (max, pad, prefix);

            foreach (var raw in docnos)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var dn = raw!.Trim();
                if (!dn.StartsWith(DefaultPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var dash = dn.LastIndexOf('-');
                if (dash < 0 || dash == dn.Length - 1) continue;
                var tail = dn[(dash + 1)..];
                if (!int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    continue;

                max = Math.Max(max, n);
                pad = Math.Max(pad, tail.Length);
            }

            return (max, pad, prefix);
        }

        public static string BuildNextDocNo(int currentMax, int pad, string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) prefix = DefaultPrefix;
            if (pad <= 0) pad = DefaultPad;
            var next = currentMax + 1;
            return prefix + next.ToString(CultureInfo.InvariantCulture).PadLeft(pad, '0');
        }
    }
}
